namespace ImageTray.Services;

/// <summary>What a rotation pass did, so the caller can report or gate on it.</summary>
public sealed record RotationResult(int Recycled, int Failed)
{
    public static readonly RotationResult Nothing = new(0, 0);

    public bool DidAnything => Recycled > 0 || Failed > 0;
}

/// <summary>
/// Sends screenshots past the keep window to the Recycle Bin.
/// </summary>
/// <remarks>
/// Two safety properties are deliberate. Only files that passed the image
/// extension filter are ever candidates, and subdirectories are never
/// enumerated, so nothing outside "images sitting directly in the watched
/// folder" can be touched. And every removal goes to the Recycle Bin, so any
/// mistake is one Ctrl+Z in Explorer away from being undone.
/// </remarks>
public sealed class RotationService(IShotFileSystem fileSystem)
{
    private readonly IShotFileSystem _fileSystem = fileSystem;

    /// <summary>
    /// How many files a rotation pass would remove, without removing anything.
    /// Used to decide whether the first pass over an unfamiliar folder deserves a
    /// confirmation prompt.
    /// </summary>
    public int CountPendingRemoval(IEnumerable<ShotFile> files, int keepCount) =>
        RotationPlanner.SelectForRemoval(files, keepCount).Count;

    public RotationResult Rotate(IEnumerable<ShotFile> files, int keepCount)
    {
        var doomed = RotationPlanner.SelectForRemoval(files, keepCount);
        if (doomed.Count == 0)
        {
            return RotationResult.Nothing;
        }

        var recycled = 0;
        var failed = 0;

        foreach (var file in doomed)
        {
            try
            {
                _fileSystem.RecycleFile(file.Path);
                recycled++;
            }
            catch (Exception ex)
            {
                // Locked, read-only, or open in another app. Log it once and move
                // on; the next pass will try again, and a permanently stuck file
                // just means the folder holds one more screenshot than asked.
                failed++;
                Log.Warn($"Could not recycle '{file.Path}'.", ex);
            }
        }

        if (recycled > 0)
        {
            Log.Info($"Rotated {recycled} image(s) to the Recycle Bin, keeping the newest {keepCount}.");
        }

        return new RotationResult(recycled, failed);
    }
}
