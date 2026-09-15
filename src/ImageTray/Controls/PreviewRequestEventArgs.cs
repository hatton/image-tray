using System.Windows;
using ImageTray.ViewModels;

namespace ImageTray.Controls;

/// <summary>What the pointer or the mouse button just did to a thumbnail.</summary>
public enum PreviewRequest
{
    /// <summary>The pointer came to rest on the thumbnail. The preview follows the pointer away again.</summary>
    Pointed,

    /// <summary>The pointer left the thumbnail.</summary>
    Away,

    /// <summary>The thumbnail was clicked, which opens a preview that stays put.</summary>
    Clicked,
}

/// <summary>
/// Asks for the preview of one image. Bubbles from the tile up to the window, which
/// owns the single preview surface: there is one of it, and any tile can be showing
/// in it.
/// </summary>
public sealed class PreviewRequestEventArgs(RoutedEvent routedEvent, ShotViewModel? shot, PreviewRequest request, FrameworkElement anchor)
    : RoutedEventArgs(routedEvent)
{
    /// <summary>The image being asked about. Null when a tile has no view model yet.</summary>
    public ShotViewModel? Shot { get; } = shot;

    public PreviewRequest Request { get; } = request;

    /// <summary>The thumbnail itself, which the preview lines itself up with.</summary>
    public FrameworkElement Anchor { get; } = anchor;
}

public delegate void PreviewRequestEventHandler(object sender, PreviewRequestEventArgs e);
