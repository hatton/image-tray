using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageTray.Models;
using ImageTray.Services;

namespace ImageTray.ViewModels;

/// <summary>One tile in the strip.</summary>
public sealed partial class ShotViewModel : ObservableObject
{
    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private double _aspectRatio;

    /// <summary>
    /// True for a screenshot that arrived while the app was already running, so the
    /// tile can announce itself. Cleared once the arrival animation has played.
    /// </summary>
    [ObservableProperty]
    private bool _isNew;

    /// <summary>
    /// The pill's text, or null for no pill. This marks which screenshot is
    /// currently on the clipboard, so it stays put until the app loses focus rather
    /// than fading on a timer, and only one tile carries it at a time.
    /// </summary>
    [ObservableProperty]
    private string? _copyBadge;

    /// <summary>The decode height the current thumbnail was produced at.</summary>
    private int _thumbnailBucket;

    public ShotViewModel(Shot shot)
    {
        Shot = shot;
        _aspectRatio = shot.AspectRatio;
    }

    /// <summary>Raised when a copy finishes, carrying the text for the pill.</summary>
    public event EventHandler<string>? CopyFeedbackRequested;

    /// <summary>
    /// Raised when the trash button is pressed. The coordinator does the work,
    /// because it owns the file system and the tile list.
    /// </summary>
    public event EventHandler? DeleteRequested;

    public Shot Shot { get; private set; }

    public string Path => Shot.Path;

    public string FileName => Shot.FileName;

    public string Tooltip
    {
        get
        {
            var size = Shot.PixelWidth > 0 && Shot.PixelHeight > 0
                ? $"{Shot.PixelWidth} × {Shot.PixelHeight}"
                : "size not read yet";

            return $"{Shot.FileName}\n{size}\n{Shot.LastWriteUtc.ToLocalTime():g}\n\nClick to copy the image";
        }
    }

    /// <summary>
    /// Applies a fresh scan of the same file. The tile keeps its identity, so an
    /// in-flight animation is not interrupted by a rescan.
    /// </summary>
    public void Update(Shot shot)
    {
        var dimensionsChanged = shot.PixelWidth != Shot.PixelWidth || shot.PixelHeight != Shot.PixelHeight;
        var contentChanged = shot.LastWriteUtc != Shot.LastWriteUtc || shot.Length != Shot.Length;

        Shot = shot;

        if (dimensionsChanged)
        {
            AspectRatio = shot.AspectRatio;
        }

        if (contentChanged)
        {
            // The file was rewritten in place, so whatever we decoded is stale.
            _thumbnailBucket = 0;
            Thumbnail = null;
        }

        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>True when the tile still needs a thumbnail at this decode height.</summary>
    public bool NeedsThumbnail(int bucket) => Thumbnail is null || _thumbnailBucket != bucket;

    public void SetThumbnail(BitmapSource image, int bucket)
    {
        _thumbnailBucket = bucket;
        Thumbnail = image;

        // The header read may have failed earlier, in which case the decoded
        // thumbnail is the first reliable aspect ratio we have.
        if (Shot.PixelWidth <= 0 && image.PixelHeight > 0)
        {
            AspectRatio = (double)image.PixelWidth / image.PixelHeight;
        }
    }

    [RelayCommand]
    private void CopyImage()
    {
        try
        {
            ClipboardService.CopyImage(Shot.Path);
            Announce("Copied");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not copy '{Shot.Path}' to the clipboard.", ex);
            Announce("Copy failed");
        }
    }

    [RelayCommand]
    private void CopyPath()
    {
        try
        {
            ClipboardService.CopyPath(Shot.Path);
            Announce("Path copied");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not copy the path of '{Shot.Path}'.", ex);
            Announce("Copy failed");
        }
    }

    [RelayCommand]
    private void Delete() => DeleteRequested?.Invoke(this, EventArgs.Empty);

    private void Announce(string message)
    {
        CopyBadge = message;

        // The event is what drives the one-shot animations in the view and lets the
        // coordinator clear the pill off whichever tile was wearing it before.
        CopyFeedbackRequested?.Invoke(this, message);
    }
}
