using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotTray.Services;
using Xunit;

namespace ScreenshotTray.Tests;

/// <summary>
/// The parts of saving a clipboard image that are worth pinning down: repairing a
/// DIB with no alpha filled in, recognising the same image twice, and never
/// overwriting a file that is already there. The clipboard itself needs a real
/// desktop, so it is not tested here.
/// </summary>
public class ClipboardImageTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "ScreenshotTrayTests", Guid.NewGuid().ToString("N"));

    public ClipboardImageTests() => Directory.CreateDirectory(_folder);

    private static BitmapSource MakeImage(int width, int height, byte blue, byte alpha = 255)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = 40;
            pixels[i + 2] = 90;
            pixels[i + 3] = alpha;
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private static byte[] AlphaChannel(BitmapSource image)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        var alpha = new byte[image.PixelWidth * image.PixelHeight];
        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = pixels[(i * 4) + 3];
        }

        return alpha;
    }

    [Fact]
    public void Normalise_reads_an_unfilled_alpha_channel_as_opaque()
    {
        // A 32-bit DIB whose alpha bytes were never written. Taken at face value it
        // is an invisible image, and saving it would produce a blank screenshot.
        var normalised = ClipboardImage.Normalise(MakeImage(4, 3, blue: 200, alpha: 0));

        Assert.All(AlphaChannel(normalised), a => Assert.Equal(255, a));
    }

    [Fact]
    public void Normalise_leaves_real_transparency_alone()
    {
        // Some pixels opaque means the alpha channel was filled in on purpose, so it
        // has to survive.
        var stride = 2 * 4;
        byte[] pixels =
        [
            0, 0, 255, 255,      // opaque
            0, 255, 0, 0,        // transparent
        ];

        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();

        Assert.Equal<byte[]>([255, 0], AlphaChannel(ClipboardImage.Normalise(source)));
    }

    [Fact]
    public void Normalise_keeps_the_dimensions()
    {
        var normalised = ClipboardImage.Normalise(MakeImage(7, 5, blue: 10));

        Assert.Equal(7, normalised.PixelWidth);
        Assert.Equal(5, normalised.PixelHeight);
    }

    [Fact]
    public void The_same_pixels_fingerprint_the_same_and_different_pixels_do_not()
    {
        Assert.Equal(
            ClipboardImage.Fingerprint(MakeImage(4, 4, blue: 200)),
            ClipboardImage.Fingerprint(MakeImage(4, 4, blue: 200)));

        Assert.NotEqual(
            ClipboardImage.Fingerprint(MakeImage(4, 4, blue: 200)),
            ClipboardImage.Fingerprint(MakeImage(4, 4, blue: 201)));
    }

    [Fact]
    public void A_saved_png_fingerprints_the_same_as_the_image_it_came_from()
    {
        // The whole duplicate check rests on this: the image on the clipboard and a
        // file on disk holding the same capture have to agree, even though they were
        // encoded by different code.
        var capture = ClipboardImage.Capture(MakeImage(6, 4, blue: 120));

        var path = Path.Combine(_folder, "written.png");
        File.WriteAllBytes(path, capture.Png!);

        Assert.Equal(capture.Fingerprint, ClipboardImage.FingerprintFile(path));
    }

    [Fact]
    public void FingerprintFile_returns_null_for_something_that_is_not_an_image()
    {
        var path = Path.Combine(_folder, "notreally.png");
        File.WriteAllText(path, "this is not a png");

        Assert.Null(ClipboardImage.FingerprintFile(path));
    }

    [Fact]
    public void BuildFileName_is_sortable_and_says_where_the_file_came_from()
    {
        var name = ClipboardImageStore.BuildFileName(new DateTime(2026, 8, 6, 14, 15, 23));

        Assert.Equal("Clipboard 2026-08-06 141523.png", name);
    }

    [Fact]
    public void ResolveFreePath_steps_aside_rather_than_overwriting()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_folder, "Clipboard 2026-08-06 141523.png"),
            Path.Combine(_folder, "Clipboard 2026-08-06 141523 (2).png"),
        };

        var path = ClipboardImageStore.ResolveFreePath(
            _folder, "Clipboard 2026-08-06 141523.png", taken.Contains);

        Assert.Equal(Path.Combine(_folder, "Clipboard 2026-08-06 141523 (3).png"), path);
    }

    [Fact]
    public void ResolveFreePath_uses_the_plain_name_when_nothing_is_in_the_way()
    {
        var path = ClipboardImageStore.ResolveFreePath(_folder, "Clipboard 2026-08-06 141523.png", _ => false);

        Assert.Equal(Path.Combine(_folder, "Clipboard 2026-08-06 141523.png"), path);
    }

    [Fact]
    public void Save_writes_the_image_into_the_folder()
    {
        var capture = ClipboardImage.Capture(MakeImage(5, 5, blue: 30));

        var saved = ClipboardImageStore.Save(_folder, capture, new DateTime(2026, 8, 6, 9, 0, 0));

        Assert.NotNull(saved);
        Assert.True(File.Exists(saved));
        Assert.Equal("Clipboard 2026-08-06 090000.png", Path.GetFileName(saved));
        Assert.Equal(capture.Fingerprint, ClipboardImage.FingerprintFile(saved!));
    }

    [Fact]
    public void Save_declines_when_a_capture_tool_just_saved_the_same_image_itself()
    {
        // What Win+Shift+S does with auto-save on: the snip goes on the clipboard and
        // the tool writes its own file. Saving ours as well would give two identical
        // tiles for one snip.
        var capture = ClipboardImage.Capture(MakeImage(8, 6, blue: 77));
        File.WriteAllBytes(Path.Combine(_folder, "Screenshot 2026-08-06 141522.png"), capture.Png!);

        Assert.Null(ClipboardImageStore.Save(_folder, capture, DateTime.Now));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Save_writes_a_copy_of_an_image_that_has_been_sitting_in_the_folder_a_while()
    {
        // Same pixels, but old. Copying a picture you saved last week is a deliberate
        // act, and the point of the strip is that it is then at hand.
        var capture = ClipboardImage.Capture(MakeImage(8, 6, blue: 77));

        var old = Path.Combine(_folder, "Screenshot from last week.png");
        File.WriteAllBytes(old, capture.Png!);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(7));

        Assert.NotNull(ClipboardImageStore.Save(_folder, capture, DateTime.Now));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void Save_is_not_fooled_by_a_recent_screenshot_that_is_merely_the_same_size()
    {
        // Full-screen captures are all exactly the same shape, so dimensions alone
        // would throw away a genuinely different image copied moments later.
        File.WriteAllBytes(
            Path.Combine(_folder, "Screenshot 2026-08-06 141522.png"),
            ClipboardImage.Capture(MakeImage(8, 6, blue: 10)).Png!);

        var different = ClipboardImage.Capture(MakeImage(8, 6, blue: 250));

        var saved = ClipboardImageStore.Save(_folder, different, DateTime.Now);

        Assert.NotNull(saved);
        Assert.Equal(different.Fingerprint, ClipboardImage.FingerprintFile(saved!));
    }

    [Fact]
    public void Save_does_not_overwrite_an_image_copied_in_the_same_second()
    {
        var when = new DateTime(2026, 8, 6, 9, 0, 0);

        var first = ClipboardImageStore.Save(_folder, ClipboardImage.Capture(MakeImage(4, 4, blue: 10)), when);
        var second = ClipboardImageStore.Save(_folder, ClipboardImage.Capture(MakeImage(4, 4, blue: 250)), when);

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void A_copied_path_to_an_image_resolves()
    {
        var elsewhere = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var image = Path.Combine(elsewhere, "diagram.png");
        File.WriteAllBytes(image, ClipboardImage.Capture(MakeImage(4, 4, blue: 9)).Png!);

        Assert.True(ClipboardImageWatcher.TryResolveImageFile(image, _folder, out var resolved));
        Assert.Equal(image, resolved);

        // Quoted and padded, which is how a path arrives from Explorer or a shell.
        Assert.True(ClipboardImageWatcher.TryResolveImageFile($"  \"{image}\" \r\n", _folder, out var trimmed));
        Assert.Equal(image, trimmed);
    }

    [Fact]
    public void A_path_already_in_the_watched_folder_resolves_to_nothing()
    {
        // Otherwise the "Copy path" button, or copying a file out of the folder
        // itself, would breed copies of what is already in the strip.
        var image = Path.Combine(_folder, "already-here.png");
        File.WriteAllBytes(image, ClipboardImage.Capture(MakeImage(4, 4, blue: 9)).Png!);

        Assert.False(ClipboardImageWatcher.TryResolveImageFile(image, _folder, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some copied prose")]
    [InlineData(@"C:\nowhere\at\all\missing.png")]
    [InlineData("relative/path/image.png")]
    [InlineData("https://example.com/cat.png")]
    public void Text_that_is_not_a_usable_image_path_resolves_to_nothing(string text) =>
        Assert.False(ClipboardImageWatcher.TryResolveImageFile(text, _folder, out _));

    [Fact]
    public void A_path_to_a_file_that_is_not_an_image_resolves_to_nothing()
    {
        var notes = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(notes, "hello");

        Assert.False(ClipboardImageWatcher.TryResolveImageFile(notes, _folder, out _));
    }

    [Fact]
    public void Two_paths_at_once_resolve_to_nothing()
    {
        var elsewhere = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var one = Path.Combine(elsewhere, "one.png");
        var two = Path.Combine(elsewhere, "two.png");
        File.WriteAllBytes(one, ClipboardImage.Capture(MakeImage(4, 4, blue: 9)).Png!);
        File.WriteAllBytes(two, ClipboardImage.Capture(MakeImage(4, 4, blue: 99)).Png!);

        Assert.False(ClipboardImageWatcher.TryResolveImageFile($"{one}\r\n{two}", _folder, out _));
    }

    [Fact]
    public void A_copied_file_is_copied_byte_for_byte_under_its_own_name()
    {
        // Copying rather than re-encoding: a JPEG stays a JPEG, and the file you get
        // is the file you copied.
        var elsewhere = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var source = Path.Combine(elsewhere, "diagram.png");
        File.WriteAllBytes(source, ClipboardImage.Capture(MakeImage(9, 7, blue: 60)).Png!);

        var capture = ClipboardImage.CaptureFile(source);
        Assert.NotNull(capture);
        Assert.Equal(9, capture!.Width);
        Assert.Equal(7, capture.Height);

        var saved = ClipboardImageStore.Save(_folder, capture, DateTime.Now);

        Assert.Equal("diagram.png", Path.GetFileName(saved));
        Assert.Equal<byte[]>(File.ReadAllBytes(source), File.ReadAllBytes(saved!));
    }

    [Fact]
    public void A_copied_file_is_dated_when_it_was_copied_so_it_lands_at_the_near_end_of_the_strip()
    {
        // The strip is ordered by write time, and File.Copy would otherwise carry the
        // original's date across: a picture from last year would arrive at the far end
        // of the strip, and rotation could recycle it before it was ever seen.
        var elsewhere = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var source = Path.Combine(elsewhere, "from-last-year.png");
        File.WriteAllBytes(source, ClipboardImage.Capture(MakeImage(5, 5, blue: 33)).Png!);
        File.SetLastWriteTime(source, new DateTime(2025, 3, 1, 8, 0, 0));

        var when = new DateTime(2026, 8, 6, 9, 0, 0);
        var saved = ClipboardImageStore.Save(_folder, ClipboardImage.CaptureFile(source)!, when);

        Assert.Equal(when, File.GetLastWriteTime(saved!));
    }

    [Fact]
    public void Copying_a_file_the_folder_already_holds_saves_nothing()
    {
        var elsewhere = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var source = Path.Combine(elsewhere, "diagram.png");
        var png = ClipboardImage.Capture(MakeImage(9, 7, blue: 60)).Png!;
        File.WriteAllBytes(source, png);

        // The same pixels, saved moments ago by whatever wrote it, under another name.
        File.WriteAllBytes(Path.Combine(_folder, "Screenshot 2026-08-06 141522.png"), png);

        Assert.Null(ClipboardImageStore.Save(_folder, ClipboardImage.CaptureFile(source)!, DateTime.Now));
    }

    [Fact]
    public void CaptureFile_gives_up_on_something_that_is_not_an_image()
    {
        var path = Path.Combine(_folder, "notreally.png");
        File.WriteAllText(path, "this is not a png");

        Assert.Null(ClipboardImage.CaptureFile(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}
