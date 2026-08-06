using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageTray.Services;

/// <summary>
/// Puts screenshots and their paths on the clipboard.
/// </summary>
/// <remarks>
/// <para>
/// A single copy publishes the image three ways, because receiving apps disagree
/// about what they want:
/// </para>
/// <list type="bullet">
/// <item><description><c>PNG</c>, which browsers, Slack, Teams, and Discord prefer, and which keeps real transparency.</description></item>
/// <item><description><c>CF_DIBV5</c> (format 17), which Office and the better image editors prefer, and which also carries an alpha channel.</description></item>
/// <item><description><c>CF_BITMAP</c>/<c>CF_DIB</c> composited onto white, for everything older.</description></item>
/// </list>
/// <para>
/// That last one is the whole reason this class exists rather than a call to
/// <c>Clipboard.SetImage</c>. Handing a transparent screenshot straight to
/// <c>SetImage</c> is the classic way to get a paste full of black rectangles,
/// because legacy consumers read the DIB and ignore its alpha. Flattening onto
/// white first makes the fallback look like what you captured.
/// </para>
/// </remarks>
public static class ClipboardService
{
    /// <summary>CF_DIBV5. WPF has no named constant, so it goes on by numeric format name.</summary>
    private static readonly string DibV5FormatName = DataFormats.GetDataFormat(17).Name;

    /// <summary>
    /// A private format stamped onto everything this app copies, so
    /// <see cref="ClipboardImageWatcher"/> can tell our own copies apart from
    /// everybody else's. Without it, clicking a tile would put its image on the
    /// clipboard, which would be saved back into the folder as a new screenshot,
    /// which would then be copyable, and so on.
    /// </summary>
    public const string OwnCopyFormatName = "ImageTray.OwnCopy";

    private const int RetryAttempts = 5;
    private const int RetryDelayMs = 100;

    /// <summary>Copies the image at <paramref name="path"/> in every format worth offering.</summary>
    /// <remarks>
    /// Every format is registered with <c>autoConvert: false</c>. That is
    /// load-bearing rather than tidiness: WPF's <see cref="DataObject"/> will
    /// happily synthesise the bitmap formats from one another, and an
    /// auto-conversion derived from the flattened fallback could overwrite the
    /// hand-built CF_DIBV5, quietly handing Office the white version of an image
    /// whose transparency we went to the trouble of preserving.
    /// </remarks>
    public static void CopyImage(string path)
    {
        var source = LoadFullImage(path);
        var flattened = FlattenOntoWhite(source);

        var data = new DataObject();

        // Alpha intact. Anything written this decade reads one of these two.
        data.SetData("PNG", BuildPng(source), autoConvert: false);
        data.SetData(DibV5FormatName, BuildDibV5(source), autoConvert: false);

        // The fallbacks, for consumers with no notion of alpha at all. Their only
        // options are white or black, and black is how you get a screenshot that
        // pastes with a dark box round its rounded corners.
        data.SetData(DataFormats.Dib, BuildDib(flattened), autoConvert: false);
        data.SetData(DataFormats.Bitmap, flattened, autoConvert: false);

        // Consumers ignore formats they do not know, so this costs nothing but is
        // what stops us treating our own copy as a newly copied image.
        data.SetData(OwnCopyFormatName, path, autoConvert: false);

        SetWithRetry(data);
    }

    /// <summary>Copies the full path as text.</summary>
    public static void CopyPath(string path)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, path, autoConvert: true);
        data.SetData(OwnCopyFormatName, path, autoConvert: false);
        SetWithRetry(data);
    }

    /// <summary>
    /// Another process can hold the clipboard open, which surfaces as
    /// CLIPBRD_E_CANT_OPEN. Retrying briefly turns that from a visible failure
    /// into nothing at all.
    /// </summary>
    private static void SetWithRetry(DataObject data)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (Exception ex) when (ex is COMException or ExternalException && attempt < RetryAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    private static BitmapSource LoadFullImage(string path)
    {
        // Decoded from memory so the file handle closes straight away, leaving the
        // file free to be recycled.
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
        return image;
    }

    private static MemoryStream BuildPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Builds a BITMAPV5HEADER followed by bottom-up straight-alpha BGRA pixels,
    /// which is what CF_DIBV5 is.
    /// </summary>
    internal static MemoryStream BuildDibV5(BitmapSource source)
    {
        var bgra = ToBgra32(source);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var stream = new MemoryStream(124 + pixels.Length);
        var writer = new BinaryWriter(stream);

        writer.Write(124u);                     // bV5Size
        writer.Write(width);                    // bV5Width
        writer.Write(height);                   // bV5Height, positive means bottom-up
        writer.Write((ushort)1);                // bV5Planes
        writer.Write((ushort)32);               // bV5BitCount
        writer.Write(3u);                       // bV5Compression = BI_BITFIELDS
        writer.Write((uint)pixels.Length);      // bV5SizeImage
        writer.Write(0);                        // bV5XPelsPerMeter
        writer.Write(0);                        // bV5YPelsPerMeter
        writer.Write(0u);                       // bV5ClrUsed
        writer.Write(0u);                       // bV5ClrImportant
        writer.Write(0x00FF0000u);              // bV5RedMask
        writer.Write(0x0000FF00u);              // bV5GreenMask
        writer.Write(0x000000FFu);              // bV5BlueMask
        writer.Write(0xFF000000u);              // bV5AlphaMask
        writer.Write(0x73524742u);              // bV5CSType = LCS_sRGB
        writer.Write(new byte[36]);             // bV5Endpoints, unused for sRGB
        writer.Write(0u);                       // bV5GammaRed
        writer.Write(0u);                       // bV5GammaGreen
        writer.Write(0u);                       // bV5GammaBlue
        writer.Write(4u);                       // bV5Intent = LCS_GM_IMAGES
        writer.Write(0u);                       // bV5ProfileData
        writer.Write(0u);                       // bV5ProfileSize
        writer.Write(0u);                       // bV5Reserved

        for (var y = height - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * stride, stride);
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Builds a plain BITMAPINFOHEADER DIB, which is what CF_DIB is. The source
    /// should already be flattened, because nothing reading this format will look
    /// at the fourth byte of a pixel.
    /// </summary>
    internal static MemoryStream BuildDib(BitmapSource source)
    {
        var bgra = ToBgra32(source);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var stream = new MemoryStream(40 + pixels.Length);
        var writer = new BinaryWriter(stream);

        writer.Write(40u);                      // biSize
        writer.Write(width);                    // biWidth
        writer.Write(height);                   // biHeight, positive means bottom-up
        writer.Write((ushort)1);                // biPlanes
        writer.Write((ushort)32);               // biBitCount
        writer.Write(0u);                       // biCompression = BI_RGB
        writer.Write((uint)pixels.Length);      // biSizeImage
        writer.Write(0);                        // biXPelsPerMeter
        writer.Write(0);                        // biYPelsPerMeter
        writer.Write(0u);                       // biClrUsed
        writer.Write(0u);                       // biClrImportant

        for (var y = height - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * stride, stride);
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    internal static BitmapSource FlattenOntoWhite(BitmapSource source)
    {
        var bgra = ToBgra32(source);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            // Straight-alpha composite over opaque white.
            var inverse = 255 - alpha;
            pixels[i] = (byte)((pixels[i] * alpha / 255) + inverse);
            pixels[i + 1] = (byte)((pixels[i + 1] * alpha / 255) + inverse);
            pixels[i + 2] = (byte)((pixels[i + 2] * alpha / 255) + inverse);
            pixels[i + 3] = 255;
        }

        // 96 DPI regardless of what the file claimed, so a paste lands at one
        // image pixel per screen pixel instead of being silently rescaled.
        var flattened = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, palette: null, pixels, stride);
        flattened.Freeze();
        return flattened;
    }

    private static BitmapSource ToBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }

        // FormatConvertedBitmap un-premultiplies Pbgra32 correctly, which matters
        // because CF_DIBV5 wants straight alpha.
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, destinationPalette: null, alphaThreshold: 0);
        converted.Freeze();
        return converted;
    }
}
