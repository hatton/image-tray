namespace ScreenshotTray.Services;

/// <summary>A file in the watched folder, as far as rotation is concerned.</summary>
public readonly record struct ShotFile(string Path, DateTime LastWriteUtc, long Length);

/// <summary>
/// The file-system surface rotation needs. Kept behind an interface purely so the
/// "which files get recycled" rules can be unit tested without a real disk and
/// without a real Recycle Bin.
/// </summary>
public interface IShotFileSystem
{
    bool DirectoryExists(string folder);

    IReadOnlyList<ShotFile> EnumerateImageFiles(string folder);

    /// <summary>Sends a single file to the Recycle Bin. Never a hard delete.</summary>
    void RecycleFile(string path);
}
