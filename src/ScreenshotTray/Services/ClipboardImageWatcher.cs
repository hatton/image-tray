using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenshotTray.Models;

namespace ScreenshotTray.Services;

/// <summary>
/// Watches the clipboard and saves any image that lands on it into the watched
/// folder, where the folder watcher picks it up like any other screenshot.
/// </summary>
/// <remarks>
/// <para>
/// Three things count as an image arriving: a bitmap, the path of an image file as
/// text, and an image file copied in Explorer. The last two are copied rather than
/// re-encoded, so they keep their own name, format and bytes.
/// </para>
/// <para>
/// Nothing here talks to the tray directly. The file goes into the folder the app is
/// already watching, so the tile, the pop-up, and rotation all happen through the
/// path they always did.
/// </para>
/// <para>
/// Listening needs a window handle, so this owns an invisible message-only one of its
/// own rather than borrowing the strip's: the strip spends most of its life hidden and
/// can be closed to the tray, and a clipboard listener that stopped working when you
/// put the window away would be worse than none.
/// </para>
/// </remarks>
public sealed partial class ClipboardImageWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WS_EX_TOOLWINDOW = 0x0080;

    /// <summary>
    /// How long to wait before reading the clipboard. The app that just wrote to it
    /// often still has it open, and a moment's pause avoids most of the retries.
    /// </summary>
    private static readonly TimeSpan ReadDelay = TimeSpan.FromMilliseconds(120);

    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long a capture is held before being written. This is the grace period a
    /// capture tool gets to save its own copy of the same snip, so that the duplicate
    /// check has something to find rather than racing it. Nobody is watching the strip
    /// this closely a second after a snip, so the delay costs nothing visible.
    /// </summary>
    private static readonly TimeSpan AutoSaveGrace = TimeSpan.FromMilliseconds(900);

    private readonly AppSettings _settings;

    private HwndSource? _source;
    private bool _capturing;
    private bool _changedAgain;
    private string? _lastFingerprint;
    private bool _warnedAboutFolder;
    private bool _disposed;

    public ClipboardImageWatcher(AppSettings settings) => _settings = settings;

    /// <summary>Begins listening. Must be called on the UI thread.</summary>
    public void Start()
    {
        if (_source is not null || _disposed)
        {
            return;
        }

        try
        {
            // No WS_VISIBLE, and a tool window besides, so it never shows up on
            // screen or in Alt+Tab.
            _source = new HwndSource(
                classStyle: 0,
                style: 0,
                exStyle: WS_EX_TOOLWINDOW,
                x: 0,
                y: 0,
                name: "ScreenshotTray clipboard listener",
                parent: IntPtr.Zero);

            _source.AddHook(OnMessage);

            if (!AddClipboardFormatListener(_source.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            // Everything else about the app still works; only this one feature is
            // lost, so it is a warning rather than a dialog.
            Log.Warn("Could not listen for clipboard changes. Images copied to the clipboard will not be saved.", ex);
            TearDown();
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardChanged();
        }

        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        // Checked before the clipboard is so much as opened: off means we keep our
        // hands off it entirely.
        if (_disposed || !_settings.IncludeClipboardImages)
        {
            return;
        }

        // A single copy commonly raises several updates, and a capture tool may set
        // the clipboard more than once as it finishes up. While a capture is in
        // flight, note that something changed and look again at the end rather than
        // starting a second one alongside it.
        if (_capturing)
        {
            _changedAgain = true;
            return;
        }

        _ = CaptureAsync();
    }

    /// <summary>
    /// Runs on the UI thread, apart from the decoding and the disk work. Reading the
    /// clipboard is what needs this thread, and the awaits here all resume on it.
    /// </summary>
    private async Task CaptureAsync()
    {
        _capturing = true;

        try
        {
            do
            {
                _changedAgain = false;
                await CaptureOnceAsync();
            }
            while (_changedAgain && !_disposed);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save an image from the clipboard.", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task CaptureOnceAsync()
    {
        await Task.Delay(ReadDelay);

        if (_disposed || !_settings.IncludeClipboardImages)
        {
            return;
        }

        var folder = _settings.WatchedFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            // Said once. Every copy you make would otherwise write another line.
            if (!_warnedAboutFolder)
            {
                _warnedAboutFolder = true;
                Log.Warn("There is nowhere to put clipboard images: no watched folder, or it has gone.");
            }

            return;
        }

        _warnedAboutFolder = false;

        // Reading the clipboard has to happen on this thread; everything after it is
        // decoding, hashing and disk work, which does not.
        var payload = await ReadPayloadAsync(folder);
        if (payload is null || _disposed)
        {
            return;
        }

        var capture = await Task.Run(() => Describe(payload));
        if (capture is null || _disposed)
        {
            return;
        }

        // Pressing Ctrl+C twice on the same picture is one image, not two. Recorded
        // before the save, so a capture that turns out to be a duplicate on disk is
        // not reconsidered either.
        if (string.Equals(capture.Fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastFingerprint = capture.Fingerprint;

        await Task.Delay(AutoSaveGrace);

        if (_disposed)
        {
            return;
        }

        // Decoding is done; the write and the duplicate scan can leave the UI thread.
        var saved = await Task.Run(() => ClipboardImageStore.Save(folder, capture, DateTime.Now));

        if (saved is not null)
        {
            Log.Info($"Saved a clipboard image as '{saved}'.");
        }
    }

    /// <summary>
    /// What one clipboard read found: a bitmap, or an image file to copy.
    /// </summary>
    /// <remarks>
    /// The bitmap has already been detached from whatever the clipboard was holding,
    /// so it is safe to hand to another thread.
    /// </remarks>
    private sealed record Payload(BitmapSource? Image, string? File);

    /// <summary>Turns a payload into something the store can write. Off the UI thread.</summary>
    private static ClipboardCapture? Describe(Payload payload) =>
        payload.Image is not null
            ? ClipboardImage.Capture(payload.Image)
            : ClipboardImage.CaptureFile(payload.File!);

    /// <summary>
    /// Reads the clipboard, retrying while another process has it open.
    /// </summary>
    /// <returns>Null when there is no image on it, or it is one of ours.</returns>
    private static async Task<Payload?> ReadPayloadAsync(string folder)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null)
                {
                    return null;
                }

                // Our own copy buttons put this here. Saving it back would turn every
                // click on a tile into a new screenshot.
                if (data.GetDataPresent(ClipboardService.OwnCopyFormatName, autoConvert: false))
                {
                    return null;
                }

                var image = ReadImage(data);
                if (image is not null)
                {
                    return new Payload(image, File: null);
                }

                var file = ReadImageFile(data, folder);
                return file is null ? null : new Payload(Image: null, file);
            }
            catch (Exception ex) when (ex is COMException or ExternalException && attempt < ReadAttempts)
            {
                await Task.Delay(ReadRetryDelay);
            }
        }
    }

    /// <summary>Pulls a bitmap out of a clipboard payload, or null when there is not one.</summary>
    private static BitmapSource? ReadImage(IDataObject data)
    {
        // PNG first. It is what browsers, chat apps, and the Snipping Tool offer, and
        // it is the only one of these formats whose transparency is unambiguous.
        if (data.GetDataPresent("PNG", autoConvert: false)
            && data.GetData("PNG", autoConvert: false) is Stream png)
        {
            var decoded = TryDecodePng(png);
            if (decoded is not null)
            {
                return ClipboardImage.Normalise(decoded);
            }
        }

        // Then the bitmap formats, for anything that offers no PNG. WPF's own
        // conversion handles CF_DIBV5 and CF_DIB; what it does not handle is a DIB
        // with an unfilled alpha channel, which is one of the things Normalise is for.
        if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true)
            && data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource bitmap)
        {
            return ClipboardImage.Normalise(bitmap);
        }

        return null;
    }

    /// <summary>
    /// An image file named by the clipboard, either as text or as a copied file.
    /// </summary>
    /// <remarks>
    /// Copying a file in Explorer counts, but only one at a time. Selecting forty
    /// images and pressing Ctrl+C is a file-management gesture, and answering it by
    /// filling the strip with forty copies and rotating away everything that was
    /// there would be its own kind of data loss.
    /// </remarks>
    private static string? ReadImageFile(IDataObject data, string folder)
    {
        if (data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)
            && data.GetData(DataFormats.UnicodeText, autoConvert: true) is string text
            && TryResolveImageFile(text, folder, out var fromText))
        {
            return fromText;
        }

        if (data.GetDataPresent(DataFormats.FileDrop, autoConvert: true)
            && data.GetData(DataFormats.FileDrop, autoConvert: true) is string[] dropped)
        {
            var images = dropped.Where(WindowsShotFileSystem.IsImageFile).ToList();

            if (images.Count > 1)
            {
                Log.Info($"{images.Count} image files were copied at once; leaving them alone.");
                return null;
            }

            if (images.Count == 1 && TryResolveImageFile(images[0], folder, out var fromDrop))
            {
                return fromDrop;
            }
        }

        return null;
    }

    /// <summary>
    /// Decides whether some clipboard text names an image file we should copy.
    /// </summary>
    /// <remarks>
    /// Deliberately strict. Clipboard text is usually just text, and the cost of
    /// being loose here is files appearing in the screenshots folder for reasons the
    /// user cannot see. A single fully qualified path to an image that exists and is
    /// not already in the folder qualifies; anything else does not.
    /// </remarks>
    internal static bool TryResolveImageFile(string? text, string folder, out string? path)
    {
        path = null;

        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
        {
            return false;
        }

        // Quotes are how a path with spaces gets copied out of Explorer or a shell.
        var candidate = text.Trim().Trim('"').Trim();

        // One path, not a list of them, and not a sentence that happens to mention one.
        if (candidate.Length == 0 || candidate.AsSpan().ContainsAny('\r', '\n'))
        {
            return false;
        }

        if (!WindowsShotFileSystem.IsImageFile(candidate))
        {
            return false;
        }

        // A relative path would be resolved against our working directory, which has
        // nothing to do with wherever the text came from.
        if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate))
        {
            return false;
        }

        // Already in the folder, so already in the strip. This is what stops the
        // "Copy path" button, and a file copied out of the folder itself, from
        // breeding.
        var parent = Path.GetDirectoryName(Path.GetFullPath(candidate));
        if (parent is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static BitmapSource? TryDecodePng(Stream stream)
    {
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            // Fall through to the bitmap formats, which is what the caller does with
            // a null.
            Log.Warn("Could not decode the PNG on the clipboard; trying the other formats.", ex);
            return null;
        }
    }

    private void TearDown()
    {
        if (_source is null)
        {
            return;
        }

        try
        {
            if (_source.Handle != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_source.Handle);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not stop listening for clipboard changes.", ex);
        }

        _source.RemoveHook(OnMessage);
        _source.Dispose();
        _source = null;
    }

    public void Dispose()
    {
        _disposed = true;
        TearDown();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(IntPtr hwnd);
}
