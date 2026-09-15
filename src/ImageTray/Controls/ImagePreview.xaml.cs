using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageTray.Services;
using ImageTray.ViewModels;

namespace ImageTray.Controls;

/// <summary>
/// One image at its own size, shown above or below the strip.
/// </summary>
/// <remarks>
/// <para>
/// There is one of these for the whole window rather than one per tile. A preview is
/// a big decoded bitmap and a second top-level surface, and only one can be looked at
/// anyway.
/// </para>
/// <para>
/// It opens two ways. Pointing at a thumbnail opens it until the pointer leaves;
/// clicking pins it, and then only the two close buttons, Escape, or the window
/// losing focus put it away. The pinned one wins: sweeping the pointer along the rest
/// of the strip does not yank a preview you deliberately opened out from under you.
/// </para>
/// <para>
/// It also carries the buttons that copy the image, copy its path and recycle it, so
/// a preview you only pointed at cannot close the instant the pointer leaves the
/// thumbnail: the way to those buttons leads across the gap between the strip and the
/// preview. Leaving either one starts a short countdown that the other one cancels.
/// </para>
/// </remarks>
public partial class ImagePreview : UserControl
{
    /// <summary>
    /// How close the preview may come to the edge of the display, and how much room
    /// to leave for the strip itself when working out how large the image can be.
    /// </summary>
    private const double ScreenMargin = 24;

    /// <summary>
    /// The shadow's allowance, which the popup's own content carries as a margin. It
    /// doubles as the visible gap between the preview and the strip, so no separate
    /// gap is added anywhere.
    /// </summary>
    private const double ShadowMargin = 14;

    /// <summary>The height of the button band, and the inset of its contents.</summary>
    private const double BandHeight = 42;
    private const double BandPadding = 10;

    /// <summary>
    /// The least room left between the buttons and Close when they share the bottom
    /// band, which is what stops the buttons sliding under Close on their way to a
    /// thumbnail at the right-hand end of the strip.
    /// </summary>
    private const double BandGap = 16;

    /// <summary>How narrow the preview may be, whatever it is showing.</summary>
    private const double MinFrameWidth = 240;

    /// <summary>
    /// How long a preview that was only pointed at survives the pointer leaving. Long
    /// enough to cross the shadow gap on the way to its buttons, short enough that a
    /// preview you have finished with does not linger.
    /// </summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromMilliseconds(250);

    private readonly PreviewImageCache _images = new();
    private readonly DispatcherTimer _closeGrace;

    private Window? _owner;

    /// <summary>
    /// The thumbnail this preview belongs to. Kept because the buttons are lined up
    /// with it after the popup is on screen, not when it is asked for.
    /// </summary>
    private FrameworkElement? _anchor;

    /// <summary>
    /// True when the preview hangs off the top edge of the strip. Decided before the
    /// popup opens, because the buttons go on whichever edge of the preview faces the
    /// strip and the popup itself never says which position it took.
    /// </summary>
    private bool _placeAbove = true;

    /// <summary>
    /// Bumped by every request. A decode that finishes after the pointer has moved on
    /// compares its own number against this and drops what it produced.
    /// </summary>
    private int _generation;

    public ImagePreview()
    {
        InitializeComponent();

        _closeGrace = new DispatcherTimer { Interval = CloseGrace };
        _closeGrace.Tick += OnCloseGraceElapsed;
    }

    /// <summary>True while showing a preview that was clicked open rather than pointed at.</summary>
    public bool IsPinned { get; private set; }

    public bool IsOpen => Host.IsOpen;

    /// <summary>
    /// True while the pointer is inside the preview itself. The window asks before
    /// closing on deactivation: a click heading for one of these buttons must not be
    /// answered by removing the button from under it.
    /// </summary>
    public bool IsMouseOverPopup => Host.IsOpen && Host.Child is FrameworkElement child && child.IsMouseOver;

    /// <summary>What is on show, or was about to be.</summary>
    public ShotViewModel? Shot { get; private set; }

    /// <summary>Opens the preview, or moves it to a different image.</summary>
    /// <param name="pinned">True for a click, which is what makes it stay open.</param>
    public void ShowFor(ShotViewModel shot, Window owner, FrameworkElement anchor, bool pinned)
    {
        _closeGrace.Stop();

        // The buttons in the popup bind to the image's own commands, and the popup is
        // outside the strip's visual tree, so it is handed the view model directly.
        Frame.DataContext = shot;

        Shot = shot;
        IsPinned = pinned;
        _owner = owner;

        _ = LoadAndOpenAsync(shot, owner, anchor, ++_generation);
    }

    /// <summary>
    /// Starts the countdown to closing a preview that was only being pointed at. A
    /// pinned one stays, which is the whole difference between the two.
    /// </summary>
    /// <remarks>
    /// The delay is what makes the buttons reachable: the pointer has to leave the
    /// thumbnail to get to them, and arriving on the preview cancels the countdown.
    /// </remarks>
    public void HideIfNotPinnedSoon()
    {
        if (IsPinned)
        {
            return;
        }

        _closeGrace.Stop();
        _closeGrace.Start();
    }

    public void Close()
    {
        _closeGrace.Stop();

        // Anything still decoding is now for an image nobody asked to see.
        _generation++;

        IsPinned = false;
        Shot = null;
        Host.IsOpen = false;
    }

    /// <summary>
    /// Closes the preview and lets go of the decoded image, which is the largest
    /// thing the app holds. Called when the strip goes back to the tray, where the
    /// app may then sit for hours.
    /// </summary>
    public void Release()
    {
        Close();
        Full.Source = null;
        _images.Clear();
    }

    private async Task LoadAndOpenAsync(ShotViewModel shot, Window owner, FrameworkElement anchor, int generation)
    {
        var image = await _images.GetAsync(shot.Path, shot.Shot.LastWriteUtc);

        // Either the file would not decode, or the pointer has moved on and this is
        // an answer to a question nobody is asking any more.
        if (image is null || generation != _generation)
        {
            return;
        }

        Open(image, owner, anchor);
    }

    private void Open(BitmapSource image, Window owner, FrameworkElement anchor)
    {
        var dpi = VisualTreeHelper.GetDpi(owner);
        var work = WindowPlacementService.WorkAreaFor(owner);

        // "Original size" means one image pixel per screen pixel, so the pixel count
        // is divided by the display's scaling rather than used as a WPF length. On a
        // 150% display those differ by half again, and a 4K screenshot would ask for
        // a preview half as wide again as the screen.
        var naturalWidth = image.PixelWidth / dpi.DpiScaleX;
        var naturalHeight = image.PixelHeight / dpi.DpiScaleY;

        // What is left of the display once the strip and the margins have had their
        // share. A screenshot of this very screen is always larger than this, so most
        // previews are scaled down, and they say so.
        var (placeAbove, room) = RoomBesideStrip(owner, work);
        _placeAbove = placeAbove;

        // The buttons go on the edge of the preview that faces the strip, so that the
        // pointer reaches them without crossing the image it just travelled away from.
        PlaceActions(atTop: !placeAbove);

        // The bottom band is always there, because Close stays in it. The top one only
        // takes height when the buttons have moved into it.
        var bands = placeAbove ? BandHeight : 2 * BandHeight;

        var maxWidth = Math.Max(160, work.Width - (2 * ScreenMargin) - (2 * ShadowMargin));
        var maxHeight = Math.Max(120, room - ScreenMargin - (2 * ShadowMargin) - bands);

        var scale = Math.Min(1, Math.Min(maxWidth / naturalWidth, maxHeight / naturalHeight));

        Full.Source = image;
        ImageFrame.Width = Full.Width = Math.Max(1, naturalWidth * scale);
        ImageFrame.Height = Full.Height = Math.Max(1, naturalHeight * scale);

        if (scale < 1)
        {
            ScaleText.Text = $"{image.PixelWidth} × {image.PixelHeight}, shown at {scale:P0}";
            ScaleNote.Visibility = Visibility.Visible;
        }
        else
        {
            ScaleNote.Visibility = Visibility.Collapsed;
        }

        _anchor = anchor;

        // The buttons sit on a canvas, which contributes nothing to the measurement,
        // so the width they need is asked for here. Where they sit along the band is
        // settled during placement, where the popup's real width is known.
        Frame.MinWidth = MinimumWidth();

        Host.PlacementTarget = owner;
        Host.CustomPopupPlacementCallback = PlaceAboveOrBelow;

        if (Host.IsOpen)
        {
            // A popup does not re-place itself when its content changes size, and
            // moving between tiles changes both the size and the anchor. Nudging an
            // offset is what makes it ask the callback again.
            Host.HorizontalOffset += 1;
            Host.HorizontalOffset -= 1;
        }
        else
        {
            Host.IsOpen = true;
        }

        // Once it is on screen and laid out, and not before: see the method.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AlignActionsWithThumbnail));
    }

    /// <summary>
    /// Puts the preview where <see cref="_placeAbove"/> says: off the top edge of the
    /// strip, or off the bottom one. A strip parked at the bottom of the screen opens
    /// upwards and one parked at the top opens down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One position rather than a list of candidates, even though WPF would happily
    /// take the first of a list that fits. A list leaves the app not knowing which one
    /// was used, and the buttons have to be on the edge facing the strip.
    /// </para>
    /// <para>
    /// Everything in here is in device pixels: that is what a placement callback is
    /// handed and what it has to answer in, while the rest of the control is in WPF's
    /// units. The two are the same number at 100%, which is what makes getting it wrong
    /// so easy to miss — on a 150% display the preview lands half as far again from
    /// where it was asked to go.
    /// </para>
    /// </remarks>
    private CustomPopupPlacement[] PlaceAboveOrBelow(Size popupSize, Size targetSize, Point offset)
    {
        var dpi = _owner is null ? new DpiScale(1, 1) : VisualTreeHelper.GetDpi(_owner);

        // The size given is the popup's content, which does not include the content's
        // own margin, while the position is for the window that margin sits inside.
        var width = popupSize.Width + (2 * ShadowMargin * dpi.DpiScaleX);
        var height = popupSize.Height + (2 * ShadowMargin * dpi.DpiScaleY);

        var x = HorizontalPosition(width, dpi.DpiScaleX);
        var y = _placeAbove ? -height : targetSize.Height;

        return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)];
    }

    /// <summary>
    /// Which side of the strip the preview goes, and how much room it has there: the
    /// deeper of the gap above the strip and the gap below it.
    /// </summary>
    /// <remarks>
    /// The strip can be parked anywhere, not only against an edge of the display, and
    /// a preview is only worth having beside the row of thumbnails you are choosing
    /// from. So the image is scaled to this depth rather than to what is left of the
    /// display once the strip has had its share, which is the same number only when
    /// the strip is at an edge.
    /// </remarks>
    private static (bool Above, double Depth) RoomBesideStrip(Window owner, Rect work)
    {
        if (!owner.IsVisible)
        {
            return (true, Math.Max(0, work.Height - owner.ActualHeight));
        }

        var dpi = VisualTreeHelper.GetDpi(owner);
        var top = owner.PointToScreen(new Point(0, 0)).Y / dpi.DpiScaleY;

        var above = top - work.Top;
        var below = work.Bottom - top - owner.ActualHeight;

        return above >= below ? (true, above) : (false, below);
    }

    /// <summary>
    /// Moves the buttons to the band on the given edge, and gives that band the height
    /// to hold them. Close is not moved: it stays on the bottom edge, because looking
    /// for it somewhere new each time is worse than a slightly longer reach.
    /// </summary>
    private void PlaceActions(bool atTop)
    {
        TopBand.Height = atTop ? BandHeight : 0;

        var host = atTop ? TopBand : BottomBand;

        if (ReferenceEquals(Actions.Parent, host))
        {
            return;
        }

        ((Panel)Actions.Parent).Children.Remove(Actions);
        host.Children.Add(Actions);
    }

    /// <summary>
    /// Puts the buttons directly above or below the thumbnail they act on, rather than
    /// in a corner of a preview that can be most of the width of the display away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here is read off the screen once the popup is up: where it actually
    /// landed, how wide the buttons actually are, where Close actually sits. Working it
    /// out beforehand instead meant answering with numbers that were not yet true —
    /// widths from a measure taken before the icon font had been applied, and a
    /// position WPF is still free to slide sideways to keep the popup on the display —
    /// and the buttons came out tens of pixels from the thumbnail, or under Close.
    /// </para>
    /// <para>
    /// Screen coordinates are in device pixels; the band is laid out in WPF's units, so
    /// the display's scaling is what converts between them.
    /// </para>
    /// </remarks>
    private void AlignActionsWithThumbnail()
    {
        if (!Host.IsOpen || _owner is null || _anchor is null || !_anchor.IsVisible)
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(_owner).DpiScaleX;
        var frameLeft = ScreenLeft(Frame, scale);
        var actions = Actions.ActualWidth;

        var centred = ScreenLeft(_anchor, scale) + (_anchor.ActualWidth / 2) - frameLeft - (actions / 2);

        var limit = Frame.ActualWidth - BandPadding - actions;

        // Close shares the bottom band with the buttons whenever the preview is above
        // the strip, and the buttons stop short of it.
        if (ReferenceEquals(Actions.Parent, BottomBand))
        {
            limit = Math.Min(limit, ScreenLeft(CloseText, scale) - frameLeft - BandGap - actions);
        }

        Canvas.SetLeft(Actions, Math.Clamp(centred, BandPadding, Math.Max(BandPadding, limit)));
    }

    /// <summary>Where an element's left edge is on the display, in WPF's units.</summary>
    private static double ScreenLeft(FrameworkElement element, double scale) =>
        element.PointToScreen(new Point(0, 0)).X / scale;

    /// <summary>
    /// How wide the preview has to be whatever it is showing. The buttons sit on a
    /// canvas, which contributes nothing to the measurement, so the width they need is
    /// asked for here instead.
    /// </summary>
    private double MinimumWidth()
    {
        var reserved = _placeAbove ? WidthOf(CloseText) + BandGap : 0;

        return Math.Max(MinFrameWidth, WidthOf(Actions) + reserved + (2 * BandPadding));
    }

    /// <summary>
    /// How wide an element is, or asks to be before it has ever been laid out. Only the
    /// popup's own width is settled from this; where the buttons sit within it waits
    /// for the real thing.
    /// </summary>
    private static double WidthOf(FrameworkElement element)
    {
        if (element.ActualWidth > 0)
        {
            return element.ActualWidth;
        }

        if (element.DesiredSize.Width <= 0)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        return element.DesiredSize.Width;
    }

    /// <summary>
    /// Where the thumbnail's middle is, measured across the window. Falls back to the
    /// middle of the window for a tile that has left the tree between the click and
    /// the decode finishing.
    /// </summary>
    private double AnchorCentre()
    {
        if (_owner is null || _anchor is null)
        {
            return 0;
        }

        try
        {
            return _anchor.TransformToAncestor(_owner)
                .Transform(new Point(_anchor.ActualWidth / 2, 0))
                .X;
        }
        catch (InvalidOperationException)
        {
            return _owner.ActualWidth / 2;
        }
    }

    /// <summary>
    /// Centred on the thumbnail it belongs to, then pulled back onto the display. The
    /// preview is usually wider than the tile and often wider than the strip, so
    /// without the second part it would hang off the side of the screen.
    /// </summary>
    private double HorizontalPosition(double popupWidth, double scale)
    {
        var centred = (AnchorCentre() * scale) - (popupWidth / 2);

        if (_owner is null || !_owner.IsVisible)
        {
            return centred;
        }

        var work = WindowPlacementService.WorkAreaFor(_owner);

        // The placement is relative to the window, so the screen's limits have to be
        // brought into the window's frame of reference first. PointToScreen already
        // answers in device pixels; the work area does not.
        var windowLeft = _owner.PointToScreen(new Point(0, 0)).X;

        var leftLimit = ((work.Left + ScreenMargin) * scale) - windowLeft;
        var rightLimit = ((work.Right - ScreenMargin) * scale) - popupWidth - windowLeft;

        // A preview wider than the display has no position that satisfies both, and
        // the left edge is the one to keep.
        return rightLimit < leftLimit ? leftLimit : Math.Clamp(centred, leftLimit, rightLimit);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>The pointer has arrived on the preview, so it is not on its way out.</summary>
    private void OnFrameEnter(object sender, MouseEventArgs e) => _closeGrace.Stop();

    /// <summary>
    /// Leaving the preview closes an unpinned one, the same as leaving the thumbnail
    /// does. Without this it would stay open for as long as the pointer avoided both.
    /// </summary>
    private void OnFrameLeave(object sender, MouseEventArgs e) => HideIfNotPinnedSoon();

    private void OnCloseGraceElapsed(object? sender, EventArgs e)
    {
        _closeGrace.Stop();

        // Pinned in the meantime, or the pointer made it across to the buttons.
        if (IsPinned || IsMouseOverPopup)
        {
            return;
        }

        Close();
    }
}
