namespace ScreenTray.Models;

/// <summary>How the app decides between the light and dark palettes.</summary>
public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Everything the app remembers between runs. Serialised as JSON to
/// %APPDATA%\ScreenTray\settings.json.
/// </summary>
public sealed class AppSettings
{
    public const int DefaultKeepCount = 12;
    public const int MinKeepCount = 4;
    public const int MaxKeepCount = 50;

    /// <summary>
    /// The folder being watched. Null or missing on disk sends the app to the
    /// folder chooser at startup.
    /// </summary>
    public string? WatchedFolder { get; set; }

    /// <summary>How many screenshots survive; everything older is recycled.</summary>
    public int KeepCount { get; set; } = DefaultKeepCount;

    /// <summary>
    /// On by default. The app is only useful if it is there when you take a
    /// screenshot, and having to go and start it first defeats the point.
    /// </summary>
    public bool RunAtLogin { get; set; } = true;

    /// <summary>
    /// Whether the strip was closed to the tray when the app last shut down. Signing
    /// in restores whichever state you left it in, rather than always hiding (which
    /// would fight "keep it open") or always showing (which would ignore the fact
    /// that you deliberately closed it).
    /// </summary>
    public bool ClosedToTray { get; set; }

    /// <summary>
    /// Bring the strip to the front when a screenshot arrives. On by default,
    /// because it removes the last remaining reason to go hunting in the tray.
    /// Showing never takes focus, so it cannot swallow what you are typing.
    /// </summary>
    public bool ShowOnNewScreenshot { get; set; } = true;

    /// <summary>
    /// The shape of every thumbnail cell, width over height. Set by dragging the
    /// splitter between two thumbnails, or by the slider in Settings. Stored as a ratio
    /// rather than a pixel width so the cells keep their shape when the tray height
    /// changes.
    /// </summary>
    public double CellAspect { get; set; } = TileMetrics.DefaultCellAspect;

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    /// <summary>
    /// Raw WINDOWPLACEMENT values from the last session. Saving the struct that
    /// Windows itself hands out is what makes multi-monitor and mixed-DPI
    /// restores land where you left them.
    /// </summary>
    public WindowPlacementData? Placement { get; set; }

    /// <summary>
    /// Folders the app has already rotated at least once. A folder missing from
    /// this list gets a one-time confirmation before its first bulk rotation, so
    /// pointing the app at a folder of 300 images is never a silent surprise.
    /// </summary>
    public List<string> RotationApprovedFolders { get; set; } = [];

    public int NormalisedKeepCount() => Math.Clamp(KeepCount, MinKeepCount, MaxKeepCount);

    public bool HasApprovedRotation(string folder) =>
        RotationApprovedFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));

    public void ApproveRotation(string folder)
    {
        if (!HasApprovedRotation(folder))
        {
            RotationApprovedFolders.Add(folder);
        }
    }
}

/// <summary>The parts of WINDOWPLACEMENT worth persisting.</summary>
public sealed class WindowPlacementData
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }

    /// <summary>SW_* value. Minimised is deliberately never persisted.</summary>
    public int ShowCommand { get; set; }
}
