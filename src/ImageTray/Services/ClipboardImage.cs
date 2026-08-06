using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageTray.Services;

/// <summary>
/// One image on its way into the watched folder, from either of the two things the
/// clipboard can offer: a bitmap, which we encode ourselves, or a file that already
/// exists, which is copied as it stands.
/// </summary>
/// <param name="Width">Pixel width, used to rule out most duplicate candidates cheaply.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Fingerprint">A hash of the pixels, which is what actually decides "the same image".</param>
/// <param name="Png">Bytes to write, for a bitmap. Null when there is a file to copy instead.</param>
/// <param name="SourcePath">The file to copy. Null when there are bytes to write instead.</param>
public sealed record ClipboardCapture(
    int Width,
    int Height,
    string Fingerprint,
    byte[]? Png = null,
    string? SourcePath = null)
{
    /// <summary>
    /// A copied file keeps its own name: you copied "diagram.png", so "diagram.png"
    /// is what you should find. Null for a bitmap, which has no name to keep.
    /// </summary>
    public string? SourceFileName => SourcePath is null ? null : Path.GetFileName(SourcePath);
}

/// <summary>
/// Turns whatever the clipboard hands over into something saveable, plus a
/// fingerprint for recognising the same image arriving twice.
/// </summary>
public static class ClipboardImage
{
    public static ClipboardCapture Capture(BitmapSource raw)
    {
        var image = Normalise(raw);
        return new ClipboardCapture(image.PixelWidth, image.PixelHeight, HashPixels(image), Png: EncodePng(image));
    }

    /// <summary>
    /// Describes an image file so it can be copied into the folder. The file is not
    /// re-encoded: copying leaves a JPEG a JPEG, at exactly the bytes it already had.
    /// </summary>
    /// <returns>Null when the file cannot be read as an image after all.</returns>
    public static ClipboardCapture? CaptureFile(string path)
    {
        if (!ImageHeader.TryReadPixelSize(path, out var width, out var height))
        {
            Log.Warn($"'{path}' was copied but could not be read as an image.");
            return null;
        }

        var fingerprint = FingerprintFile(path);
        if (fingerprint is null)
        {
            return null;
        }

        return new ClipboardCapture(width, height, fingerprint, SourcePath: path);
    }

    /// <summary>
    /// Copies the clipboard's bitmap into straight-alpha Bgra32 pixels of our own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two jobs. The copy detaches the image from whatever the clipboard was
    /// holding, so nothing downstream is reading a bitmap that belongs to another
    /// process and may be gone by the time we look.
    /// </para>
    /// <para>
    /// The other job is repairing the thing clipboard bitmaps are notorious for: a
    /// 32-bit DIB whose alpha bytes were never filled in. Read literally that says
    /// every pixel is invisible, and saving it produces a screenshot that is
    /// entirely blank. An image with no opaque pixel anywhere is read as opaque
    /// instead, which is what the app that copied it meant.
    /// </para>
    /// </remarks>
    internal static BitmapSource Normalise(BitmapSource source)
    {
        // FormatConvertedBitmap un-premultiplies Pbgra32 properly, and fills in
        // opaque alpha for the formats that carry none.
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, destinationPalette: null, alphaThreshold: 0);

        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        if (IsEntirelyTransparent(pixels))
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        // 96 DPI, for the same reason a copy out of the tray is: a screenshot's
        // pixels are screen pixels, and a DPI claim only invites something to
        // rescale them later.
        var copy = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, palette: null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    private static bool IsEntirelyTransparent(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    internal static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Hashes the pixels rather than the file bytes, because that is the only way
    /// two encoders' PNGs of the same capture come out equal. Compared alongside the
    /// dimensions, never on its own.
    /// </summary>
    internal static string Fingerprint(BitmapSource image) => HashPixels(Normalise(image));

    /// <summary>Hashes the pixels of an image that has already been normalised.</summary>
    private static string HashPixels(BitmapSource normalised)
    {
        var stride = normalised.PixelWidth * 4;
        var pixels = new byte[stride * normalised.PixelHeight];
        normalised.CopyPixels(pixels, stride, 0);

        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    /// <summary>
    /// The same fingerprint for a file on disk, or null when it cannot be read.
    /// Decoded the same way as the clipboard's copy, since the point is comparing
    /// the two.
    /// </summary>
    internal static string? FingerprintFile(string path)
    {
        try
        {
            byte[] bytes;
            using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var buffer = new MemoryStream();
                file.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            using var stream = new MemoryStream(bytes, writable: false);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return Fingerprint(image);
        }
        catch (Exception ex)
        {
            // Half-written, locked, or not really an image. All three mean we cannot
            // say it is a duplicate, and the caller treats that as "not one".
            Log.Warn($"Could not fingerprint '{path}' while looking for a duplicate.", ex);
            return null;
        }
    }
}
