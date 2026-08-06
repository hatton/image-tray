namespace ImageTray.Services;

/// <summary>
/// The pure decision half of rotation: given the files in the folder and how many
/// to keep, which ones go. No disk access, so the rules are cheap to test.
/// </summary>
public static class RotationPlanner
{
    /// <summary>
    /// Newest first. Timestamps tie surprisingly often, because a burst of
    /// captures or a file copy can land several files in the same second, so the
    /// path breaks the tie. Screenshot tools name files with a leading timestamp,
    /// which makes descending path order agree with descending capture order.
    /// </summary>
    public static IReadOnlyList<ShotFile> OrderNewestFirst(IEnumerable<ShotFile> files) =>
        files
            .OrderByDescending(f => f.LastWriteUtc)
            .ThenByDescending(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The files past the keep window, oldest first so that a partially completed
    /// rotation still removes the least useful files.
    /// </summary>
    public static IReadOnlyList<ShotFile> SelectForRemoval(IEnumerable<ShotFile> files, int keepCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepCount);

        var ordered = OrderNewestFirst(files);
        if (ordered.Count <= keepCount)
        {
            return [];
        }

        return ordered.Skip(keepCount).Reverse().ToList();
    }
}
