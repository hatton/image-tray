using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageTray.Services;
using Xunit;

namespace ImageTray.Tests;

/// <summary>
/// Puts a transparent image on the real clipboard and reads it straight back.
/// </summary>
/// <remarks>
/// The byte-level tests elsewhere check that the payloads are built correctly.
/// This one checks something they cannot: that the payloads actually survive the
/// trip through WPF's DataObject and the OLE clipboard without being replaced by
/// an auto-converted substitute. That substitution is the failure mode that would
/// silently strip alpha from a Figma export, and it is invisible unless you go and
/// look at what landed.
/// </remarks>
public class ClipboardRoundTripTests
{
    [Fact]
    public void Alpha_survives_the_trip_to_the_clipboard_in_the_modern_formats()
    {
        RunOnStaThread(() =>
        {
            var path = WriteTransparentPng();

            try
            {
                ClipboardService.CopyImage(path);

                var data = ReadClipboard();

                // PNG: what Figma, browsers, Slack, and VS Code read.
                Assert.True(data.GetDataPresent("PNG"), "The PNG format is missing from the clipboard.");
                var png = ReadStream(data, "PNG");
                var decoded = ToBgra32(new PngBitmapDecoder(
                    png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);

                var pixels = new byte[2 * 2 * 4];
                decoded.CopyPixels(pixels, 2 * 4, 0);

                Assert.Equal(255, pixels[3]);    // top-left was opaque
                Assert.Equal(128, pixels[7]);    // top-right was half transparent
                Assert.Equal(0, pixels[11]);     // bottom-left was fully transparent

                // CF_DIBV5: what Office and most editors read.
                var dibV5Name = DataFormats.GetDataFormat(17).Name;
                Assert.True(data.GetDataPresent(dibV5Name), "CF_DIBV5 is missing from the clipboard.");
                var v5 = ReadStream(data, dibV5Name).ToArray();

                Assert.Equal(124u, BitConverter.ToUInt32(v5, 0));
                Assert.Equal(0xFF000000u, BitConverter.ToUInt32(v5, 52));

                // Bottom-up, so the first row of data is the source's bottom row:
                // the fully transparent pixel, then the opaque blue one.
                Assert.Equal(0, v5[124 + 3]);
                Assert.Equal(255, v5[124 + 7]);
            }
            finally
            {
                TryDelete(path);
            }
        });
    }

    [Fact]
    public void The_legacy_fallback_is_flattened_onto_white_and_does_not_displace_the_others()
    {
        RunOnStaThread(() =>
        {
            var path = WriteTransparentPng();

            try
            {
                ClipboardService.CopyImage(path);

                var data = ReadClipboard();
                Assert.True(data.GetDataPresent(DataFormats.Dib), "CF_DIB is missing from the clipboard.");

                var dib = ReadStream(data, DataFormats.Dib).ToArray();

                Assert.Equal(40u, BitConverter.ToUInt32(dib, 0));      // BITMAPINFOHEADER
                Assert.Equal((ushort)32, BitConverter.ToUInt16(dib, 14));
                Assert.Equal(0u, BitConverter.ToUInt32(dib, 16));      // BI_RGB

                // The source's bottom-left pixel was fully transparent; in this
                // format it has to read as opaque white rather than as black.
                Assert.Equal(255, dib[40 + 0]);
                Assert.Equal(255, dib[40 + 1]);
                Assert.Equal(255, dib[40 + 2]);
                Assert.Equal(255, dib[40 + 3]);

                // And the alpha-preserving formats are still there alongside it.
                Assert.True(data.GetDataPresent("PNG"));
                Assert.True(data.GetDataPresent(DataFormats.GetDataFormat(17).Name));
            }
            finally
            {
                TryDelete(path);
            }
        });
    }

    [Fact]
    public void CopyPath_puts_the_path_on_the_clipboard_as_text()
    {
        RunOnStaThread(() =>
        {
            const string path = @"C:\Users\someone\Pictures\Screenshots\2026-07-31 14_00_00-example.png";

            ClipboardService.CopyPath(path);

            Assert.Equal(path, Clipboard.GetText());
        });
    }

    /// <summary>
    /// A 2x2 PNG: opaque red, half-transparent green, fully transparent, opaque
    /// blue. Small enough to assert on every byte.
    /// </summary>
    private static string WriteTransparentPng()
    {
        byte[] pixels =
        [
            0, 0, 255, 255,      // top-left: red, opaque
            0, 255, 0, 128,      // top-right: green, half alpha
            0, 0, 0, 0,          // bottom-left: fully transparent
            255, 0, 0, 255,      // bottom-right: blue, opaque
        ];

        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 2 * 4);
        source.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        var path = Path.Combine(Path.GetTempPath(), $"imagetray-alpha-{Guid.NewGuid():N}.png");
        using var file = File.Create(path);
        encoder.Save(file);

        return path;
    }

    /// <summary>
    /// Reads the clipboard, retrying briefly. The clipboard is a single machine-wide
    /// resource, so any other process on the box can be holding it open at the moment a
    /// test looks. ClipboardService already retries when writing; without the matching
    /// retry here the test fails for reasons that have nothing to do with the code.
    /// </summary>
    private static IDataObject ReadClipboard()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is not null)
                {
                    return data;
                }
            }
            catch (ExternalException) when (attempt < 8)
            {
                // Held by someone else; fall through to the delay below.
            }

            Assert.True(attempt < 8, "Could not read the clipboard after several attempts.");
            Thread.Sleep(120);
        }
    }

    private static MemoryStream ReadStream(IDataObject data, string format)
    {
        var value = data.GetData(format);
        var stream = Assert.IsAssignableFrom<Stream>(value);

        var copy = new MemoryStream();
        stream.Position = 0;
        stream.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    private static BitmapSource ToBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The clipboard is only reachable from a single-threaded apartment, which the
    /// test runner's threads are not.
    /// </summary>
    private static void RunOnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}
