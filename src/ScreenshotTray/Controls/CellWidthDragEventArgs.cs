using System.Windows;

namespace ScreenshotTray.Controls;

/// <summary>
/// Reports the cell width a splitter drag is asking for. Bubbles from the tile whose
/// gap is being dragged up to the window, which owns the setting and applies it to
/// every tile at once.
/// </summary>
public sealed class CellWidthDragEventArgs(RoutedEvent routedEvent, double cellWidth, bool completed)
    : RoutedEventArgs(routedEvent)
{
    /// <summary>The requested width of one cell, before clamping.</summary>
    public double CellWidth { get; } = cellWidth;

    /// <summary>True on the final event of a drag, when the value should be persisted.</summary>
    public bool Completed { get; } = completed;
}

public delegate void CellWidthDragEventHandler(object sender, CellWidthDragEventArgs e);
