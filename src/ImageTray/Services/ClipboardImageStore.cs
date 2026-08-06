using System.IO;

namespace ImageTray.Services;

/// <summary>
/// Writes clipboard images into the watched folder, and declines to write one that
/// is already there.
/// </summary>
/// <remarks>
/// The duplicate check is what makes this feature liveable on Windows 11. Win+Shift+S
/// puts the snip on the clipboard *and*, with auto-save on, writes its own file into
/// the Screenshots folder. Saving the clipboard copy as well would give you two
/// identical tiles for every snip, which reads as a bug rather than a feature. So a
/// capture is dropped when an image with the same pixels was written to the folder
/// moments ago, whoever wrote it.
/// </remarks>
public static class ClipboardImageStore
{
    /// <summary>
    /// How far back a file counts as the same capture. Generous, because it only has
    /// to cover the gap between a tool setting the clipboard and that same tool
    /// finishing its own save, and because the pixel comparison is what actually
    /// decides the question.
    /// </summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(8);

    private static readonly WindowsShotFileSystem FileSystem = new();

    /// <summary>Saves a capture into <paramref name="folder"/>.</summary>
    /// <returns>The path written, or null when the same image was already there.</returns>
    public static string? Save(string folder, ClipboardCapture capture, DateTime nowLocal)
    {
        var existing = FindSameImage(folder, capture, nowLocal.ToUniversalTime());
        if (existing is not null)
        {
            Log.Info($"The clipboard image is already in the folder as '{Path.GetFileName(existing)}'; not saving it again.");
            return null;
        }

        var path = ResolveFreePath(folder, capture.SourceFileName ?? BuildFileName(nowLocal));

        if (capture.SourcePath is not null)
        {
            File.Copy(capture.SourcePath, path);

            // File.Copy brings the original's timestamp with it, and the strip is
            // ordered by write time. Left alone, a picture from last year would arrive
            // at the far end of the strip instead of the near one, and rotation might
            // recycle it before you ever saw it. What is new here is the copying of
            // it, so that is what the time records.
            File.SetLastWriteTime(path, nowLocal);
            File.SetCreationTime(path, nowLocal);
        }
        else
        {
            File.WriteAllBytes(path, capture.Png!);
        }

        return path;
    }

    /// <summary>
    /// Named for a human reading the folder later: sortable, and obvious about where
    /// the file came from, so it does not look like something your screenshot tool
    /// wrote.
    /// </summary>
    public static string BuildFileName(DateTime localTime) => $"Clipboard {localTime:yyyy-MM-dd HHmmss}.png";

    /// <summary>
    /// A file in the folder holding this same image, if there is one. Dimensions are
    /// checked first because they rule out almost every candidate without decoding
    /// anything.
    /// </summary>
    internal static string? FindSameImage(string folder, ClipboardCapture capture, DateTime nowUtc)
    {
        var cutoff = nowUtc - DuplicateWindow;

        foreach (var file in FileSystem.EnumerateImageFiles(folder))
        {
            if (file.LastWriteUtc < cutoff)
            {
                continue;
            }

            if (!ImageHeader.TryReadPixelSize(file.Path, out var width, out var height)
                || width != capture.Width
                || height != capture.Height)
            {
                continue;
            }

            if (string.Equals(ClipboardImage.FingerprintFile(file.Path), capture.Fingerprint, StringComparison.Ordinal))
            {
                return file.Path;
            }
        }

        return null;
    }

    internal static string ResolveFreePath(string folder, string fileName) =>
        ResolveFreePath(folder, fileName, File.Exists);

    /// <summary>
    /// Two images copied inside the same second would otherwise land on the same
    /// name, and the second one would overwrite the first.
    /// </summary>
    internal static string ResolveFreePath(string folder, string fileName, Func<string, bool> exists)
    {
        var path = Path.Combine(folder, fileName);
        if (!exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var suffix = 2; suffix <= 99; suffix++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        // A hundred images copied in one second is not a thing that happens, but a
        // name that cannot collide beats returning one that overwrites.
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}
