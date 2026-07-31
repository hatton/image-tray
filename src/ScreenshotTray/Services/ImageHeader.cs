using System.IO;
using System.Windows.Media.Imaging;

namespace ScreenshotTray.Services;

/// <summary>Reads pixel dimensions out of an image header without decoding it.</summary>
public static class ImageHeader
{
    /// <summary>
    /// True when the dimensions were read. False usually means the screenshot tool
    /// still has the file open or has only written part of it, which the caller
    /// handles by trying again shortly.
    /// </summary>
    public static bool TryReadPixelSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            // FileShare.Read lets other readers in but fails while a writer holds
            // the file exclusively, which is exactly the "still being saved"
            // condition we want to detect.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            // Locked, truncated, or not really an image. All three mean "no
            // dimensions available right now".
            return false;
        }
    }
}
