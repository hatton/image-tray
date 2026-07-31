namespace ScreenTray.Models;

/// <summary>
/// One screenshot file on disk. Pixel dimensions come from the image header
/// during the folder scan so tiles get their correct width before any thumbnail
/// finishes decoding, which avoids the row visibly reflowing.
/// </summary>
public sealed record Shot(
    string Path,
    DateTime LastWriteUtc,
    long Length,
    int PixelWidth,
    int PixelHeight)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Width divided by height, with a sane floor for degenerate images.</summary>
    public double AspectRatio =>
        PixelHeight > 0 && PixelWidth > 0 ? (double)PixelWidth / PixelHeight : 16d / 10d;
}
