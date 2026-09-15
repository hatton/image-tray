using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ImageTray.Controls;
using ImageTray.Services;
using ImageTray.ViewModels;

namespace ImageTray;

/// <summary>
/// The strip itself. Closing hides it rather than ending the app, which is what
/// makes the tray icon a reliable way back in.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Vertical space the tiles do not get: the item margins.</summary>
    private const double StripPadding = 12;

    /// <summary>
    /// Space held back for the horizontal scrollbar whether or not it is showing.
    /// Reserving it unconditionally costs a few pixels and avoids a feedback loop
    /// where a taller tile makes the bar appear, which shortens the tile, which
    /// makes the bar go away again.
    /// </summary>
    private const double ScrollBarGutter = 12;

    private const double MinTileHeight = 48;

    /// <summary>
    /// How long the pointer has to rest on a thumbnail before its preview opens.
    /// Reaching for the far end of the strip crosses every tile on the way, and none
    /// of those crossings is a request to see anything.
    /// </summary>
    private static readonly TimeSpan PreviewHoverDelay = TimeSpan.FromMilliseconds(350);

    private readonly TrayViewModel _viewModel;
    private readonly DispatcherTimer _previewHover;

    private ShotViewModel? _pointedShot;
    private FrameworkElement? _pointedAnchor;

    public MainWindow(TrayViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        // The splitter lives on each tile but sets a value shared by all of them, so
        // the drag is handled up here where the setting is owned.
        Tiles.AddHandler(ShotTile.CellWidthDragEvent, new CellWidthDragEventHandler(OnCellWidthDrag));

        // Same reasoning for the preview: every tile can ask for one, and there is a
        // single preview surface for them to share.
        Tiles.AddHandler(ShotTile.PreviewRequestedEvent, new PreviewRequestEventHandler(OnPreviewRequested));

        _previewHover = new DispatcherTimer { Interval = PreviewHoverDelay };
        _previewHover.Tick += OnPointerSettled;

        // A preview of a screenshot that has just been recycled should not outlive
        // its tile.
        viewModel.Shots.CollectionChanged += OnShotsChanged;
    }

    /// <summary>Raised when the window is closed, so the app can save its position.</summary>
    public event EventHandler? HiddenToTray;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // The window needs a handle before SetWindowPlacement will take, and the
        // frame's dark mode has to be applied to a real HWND too.
        if (!WindowPlacementService.TryApply(this, _viewModel.Settings.Placement))
        {
            WindowPlacementService.PlaceAtPrimaryBottom(this);
        }

        ThemeService.ApplyWindowFrame(this);
        UpdateTileHeight();
    }

    /// <summary>
    /// Closing means "get out of my way", not "quit". The tray icon is the way
    /// back, and quitting is a deliberate choice from its menu.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        HideToTray();
    }

    /// <summary>
    /// The "Copied" pill says what is on the clipboard right now. Once you have
    /// switched to the app you are pasting into, you no longer need telling, so it
    /// comes off.
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        _viewModel.ClearCopyBadges();

        // A preview belongs to the strip, so it goes when you move to another app.
        // Unless the pointer is inside it, which means the click that is arriving is
        // for one of its own buttons.
        if (!Preview.IsMouseOverPopup)
        {
            ClosePreview();
        }

        base.OnDeactivated(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Escape puts away whatever is in front of you: the preview when one is
            // open, and otherwise the strip itself.
            if (Preview.IsOpen)
            {
                ClosePreview();
            }
            else
            {
                HideToTray();
            }

            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Answers a tile's pointer or click. Pointing opens a preview that follows the
    /// pointer away again; clicking opens one that stays until it is closed.
    /// </summary>
    private void OnPreviewRequested(object sender, PreviewRequestEventArgs e)
    {
        if (e.Shot is null)
        {
            return;
        }

        switch (e.Request)
        {
            case PreviewRequest.Pointed:
                // A preview you clicked open stays on the image you chose. Sweeping
                // the pointer across the rest of the strip does not drag it along.
                if (Preview.IsPinned)
                {
                    return;
                }

                _pointedShot = e.Shot;
                _pointedAnchor = e.Anchor;
                _previewHover.Stop();
                _previewHover.Start();
                break;

            case PreviewRequest.Away:
                _previewHover.Stop();
                _pointedShot = null;
                _pointedAnchor = null;

                // Not closed outright: the preview carries the copy and recycle
                // buttons, and reaching them means leaving the thumbnail.
                Preview.HideIfNotPinnedSoon();
                break;

            case PreviewRequest.Clicked:
                _previewHover.Stop();

                // Clicking the tile that is already showing puts the preview away,
                // so the same gesture opens and closes it.
                if (Preview.IsPinned && ReferenceEquals(Preview.Shot, e.Shot))
                {
                    ClosePreview();
                    return;
                }

                Preview.ShowFor(e.Shot, this, e.Anchor, pinned: true);
                break;
        }
    }

    private void OnPointerSettled(object? sender, EventArgs e)
    {
        _previewHover.Stop();

        if (_pointedShot is null || _pointedAnchor is null || Preview.IsPinned)
        {
            return;
        }

        // The pointer can leave the strip entirely while the timer runs, and a tile
        // that has scrolled away takes its mouse-over state with it.
        if (!_pointedAnchor.IsMouseOver)
        {
            return;
        }

        Preview.ShowFor(_pointedShot, this, _pointedAnchor, pinned: false);
    }

    private void OnShotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Preview.Shot is not null && !_viewModel.Shots.Contains(Preview.Shot))
        {
            ClosePreview();
        }
    }

    private void ClosePreview()
    {
        _previewHover.Stop();
        _pointedShot = null;
        _pointedAnchor = null;
        Preview.Close();
    }

    public void HideToTray()
    {
        // The preview holds a full-size decoded image, and the app can sit in the
        // tray for hours. Releasing it here is the difference between tens of
        // megabytes resident overnight and none.
        _previewHover.Stop();
        Preview.Release();

        // Remembered so that signing in again restores the state you left, rather
        // than reopening a strip you had deliberately put away.
        _viewModel.Settings.ClosedToTray = true;
        SavePlacement();
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings the window back to the front.
    /// </summary>
    /// <remarks>
    /// Windows will refuse a foreground activation request from a process that
    /// does not currently own the foreground, which is exactly the situation the
    /// tray icon puts us in. Show and Activate usually win; the brief Topmost
    /// toggle is the documented fallback for when they do not, and without it the
    /// window sometimes reappears behind whatever you were working in.
    /// </remarks>
    public void ShowAndActivate()
    {
        if (_viewModel.Settings.ClosedToTray)
        {
            _viewModel.Settings.ClosedToTray = false;
            _viewModel.SaveSettings();
        }

        // ShowForNewScreenshot leaves ShowActivated off. When you click the tray icon
        // you are asking for the window, so put it back before showing.
        ShowActivated = true;

        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!Activate())
        {
            var wasTopmost = Topmost;
            Topmost = true;
            Topmost = wasTopmost;
            Activate();
        }

        Focus();
    }

    /// <summary>
    /// Brings the strip to the front because a screenshot just arrived, without
    /// taking focus. You are usually typing in another app at that moment, so
    /// stealing focus here would be worse than not appearing at all.
    /// </summary>
    public void ShowForNewScreenshot()
    {
        if (_viewModel.Settings.ClosedToTray)
        {
            _viewModel.Settings.ClosedToTray = false;
            _viewModel.SaveSettings();
        }

        QuietWindowShow.ShowWithoutActivating(this);
    }

    public void SavePlacement()
    {
        var placement = WindowPlacementService.Capture(this);
        if (placement is not null)
        {
            _viewModel.Settings.Placement = placement;
            _viewModel.SaveSettings();
        }
    }

    private void OnStripSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged)
        {
            UpdateTileHeight();
        }
    }

    private void UpdateTileHeight() =>
        _viewModel.TileHeight = Math.Max(MinTileHeight, Strip.ActualHeight - StripPadding - ScrollBarGutter);

    /// <summary>
    /// The strip only scrolls sideways, but WPF sends the wheel to vertical
    /// scrolling by default, which in a vertically-disabled viewer means nothing
    /// happens at all.
    /// </summary>
    private void OnStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Strip.ScrollToHorizontalOffset(Strip.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnCellWidthDrag(object sender, CellWidthDragEventArgs e) =>
        _viewModel.ApplyCellWidth(e.CellWidth, persist: e.Completed);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideToTray();
}
