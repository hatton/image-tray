namespace ScreenTray;

/// <summary>The size of a tile, or of the image drawn inside one.</summary>
public readonly record struct TileSize(double Width, double Height);

/// <summary>
/// Works out how big a tile is and how big the screenshot inside it is.
/// </summary>
/// <remarks>
/// <para>
/// Every cell is the same shape and size: the tray's height, by that height times
/// <see cref="CellAspect"/>. The screenshot is then fitted inside its cell, centred,
/// with plain padding wherever it does not reach the edges. This is the arrangement
/// Snagit's editor tray uses, and it is right for the same reasons: the row is
/// perfectly regular so it can be scanned at a glance, one oddly-shaped screenshot
/// cannot dominate the strip, and nothing is ever cropped away.
/// </para>
/// <para>
/// Tile height being an input rather than a result is what makes dragging the tray
/// work as a size control. Earlier versions derived height from each image's aspect
/// ratio, which produced a row of wildly different heights and no usable size
/// control.
/// </para>
/// </remarks>
public static class TileMetrics
{
    /// <summary>No cell is ever smaller than this, so a tiny tray stays clickable.</summary>
    public const double MinHeight = 32;

    /// <summary>
    /// Default cell shape, width over height. 16:9 because most screenshots are roughly
    /// that, so the common case fills its cell with little padding.
    /// </summary>
    public const double DefaultCellAspect = 16d / 9d;

    /// <summary>Narrowest cell the splitter and the slider allow.</summary>
    public const double MinCellAspect = 0.5;

    /// <summary>Widest cell the splitter and the slider allow.</summary>
    public const double MaxCellAspect = 3.0;

    private const double FallbackAspect = 16d / 10d;

    public static double ClampCellAspect(double cellAspect) =>
        Math.Clamp(Sanitise(cellAspect), MinCellAspect, MaxCellAspect);

    /// <summary>The uniform cell every tile occupies.</summary>
    public static TileSize ResolveCell(double trayHeight, double cellAspect)
    {
        var height = Math.Max(MinHeight, trayHeight);
        return new TileSize(Math.Max(MinHeight, height * ClampCellAspect(cellAspect)), height);
    }

    /// <summary>
    /// The screenshot's box inside its cell: the largest rectangle of the right aspect
    /// ratio that fits. Also what the transparency checkerboard is sized to, so that
    /// padding stays plain rather than looking like transparency.
    /// </summary>
    public static TileSize ResolveImageBox(TileSize cell, double aspectRatio)
    {
        var aspect = Sanitise(aspectRatio);
        var widthAtFullHeight = cell.Height * aspect;

        return widthAtFullHeight <= cell.Width
            ? new TileSize(widthAtFullHeight, cell.Height)
            : new TileSize(cell.Width, cell.Width / aspect);
    }

    private static double Sanitise(double aspectRatio) =>
        double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio) || aspectRatio <= 0
            ? FallbackAspect
            : aspectRatio;
}
