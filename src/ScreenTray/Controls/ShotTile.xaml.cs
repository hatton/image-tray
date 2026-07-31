using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenTray.ViewModels;

namespace ScreenTray.Controls;

/// <summary>
/// One screenshot in the strip: the thumbnail, the corner button that copies the
/// path, and all the feedback that makes a copy feel like it happened.
/// </summary>
/// <remarks>
/// The animations live in this control's XAML and are started from here rather
/// than being driven by view-model state. Storyboards are about timing and
/// restarting, which a bound boolean models badly, and a tile that is mid-pill
/// should not be disturbed by an unrelated folder rescan.
/// </remarks>
public partial class ShotTile : UserControl
{
    /// <summary>
    /// The height every tile takes, which the window derives from its own height.
    /// Width follows from the image's aspect ratio, which is what keeps
    /// screenshots undistorted at any tray size.
    /// </summary>
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight),
        typeof(double),
        typeof(ShotTile),
        new PropertyMetadata(152d, OnSizingInputChanged));

    /// <summary>The shape of every cell, width over height.</summary>
    public static readonly DependencyProperty CellAspectProperty = DependencyProperty.Register(
        nameof(CellAspect),
        typeof(double),
        typeof(ShotTile),
        new PropertyMetadata(TileMetrics.DefaultCellAspect, OnSizingInputChanged));

    /// <summary>
    /// Raised while the splitter in the gap is dragged, carrying the cell width the
    /// drag is asking for. Bubbles so the window can apply it to every tile.
    /// </summary>
    public static readonly RoutedEvent CellWidthDragEvent = EventManager.RegisterRoutedEvent(
        nameof(CellWidthDrag),
        RoutingStrategy.Bubble,
        typeof(CellWidthDragEventHandler),
        typeof(ShotTile));

    /// <summary>The width of the gap that carries the splitter.</summary>
    private const double GripWidth = 12;

    private ShotViewModel? _shot;

    private bool _dragging;
    private double _dragOriginX;
    private double _dragStartCellWidth;
    private int _dragDivisor = 1;

    public ShotTile()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    public double CellAspect
    {
        get => (double)GetValue(CellAspectProperty);
        set => SetValue(CellAspectProperty, value);
    }

    public event CellWidthDragEventHandler CellWidthDrag
    {
        add => AddHandler(CellWidthDragEvent, value);
        remove => RemoveHandler(CellWidthDragEvent, value);
    }

    private static void OnSizingInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ShotTile)d).UpdateSize();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shot is not null)
        {
            _shot.CopyFeedbackRequested -= OnCopyFeedback;
            _shot.PropertyChanged -= OnShotPropertyChanged;
        }

        _shot = e.NewValue as ShotViewModel;

        if (_shot is not null)
        {
            _shot.CopyFeedbackRequested += OnCopyFeedback;
            _shot.PropertyChanged += OnShotPropertyChanged;
        }

        UpdateSize();

        // Tiles are reused as the row reorders, so a tile taking on a view model
        // that already holds the clipboard has to show the pill straight away.
        UpdatePill();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSize();

        // Only screenshots that arrived while the app was watching announce
        // themselves. Clearing the flag here means a tile scrolled out of view and
        // back does not replay the animation.
        if (_shot?.IsNew == true)
        {
            _shot.IsNew = false;
            Play("Arrival");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_shot is not null)
        {
            _shot.CopyFeedbackRequested -= OnCopyFeedback;
            _shot.PropertyChanged -= OnShotPropertyChanged;
        }
    }

    private void OnShotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShotViewModel.AspectRatio):
                UpdateSize();
                break;

            case nameof(ShotViewModel.CopyBadge):
                UpdatePill();
                break;
        }
    }

    /// <summary>
    /// The pill follows view-model state rather than a timer, because what it means
    /// is "this screenshot is on the clipboard", and that stays true until something
    /// else is copied or the app loses focus.
    /// </summary>
    private void UpdatePill()
    {
        var badge = _shot?.CopyBadge;

        if (string.IsNullOrEmpty(badge))
        {
            Play("PillOut");
            return;
        }

        PillText.Text = badge;
        Play("PillIn");
    }

    private void UpdateSize()
    {
        var cell = TileMetrics.ResolveCell(TileHeight, CellAspect);
        Height = cell.Height;

        // The control covers its cell plus the gap that carries the splitter, and the
        // star-sized first column leaves the card exactly the cell's width.
        Width = cell.Width + GripWidth;

        // The image box is sized exactly, so the checkerboard covers the screenshot
        // and nothing else, and the padding around it stays plain.
        var image = TileMetrics.ResolveImageBox(cell, _shot?.AspectRatio ?? 0);
        ImageFrame.Width = image.Width;
        ImageFrame.Height = image.Height;
    }

    private void OnCardPressed(object sender, MouseButtonEventArgs e) => Play("PressDown");

    private void OnCardReleased(object sender, RoutedEventArgs e) => Play("PressUp");

    private void OnCornerButtonFocused(object sender, KeyboardFocusChangedEventArgs e) => Play("RevealCornerButtons");

    private void OnGripMouseEnter(object sender, MouseEventArgs e) => Play("GripIn");

    private void OnGripMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            Play("GripOut");
        }
    }

    private void OnGripPressed(object sender, MouseButtonEventArgs e)
    {
        var reference = ReferenceElement();
        if (reference is null)
        {
            return;
        }

        _dragging = true;
        _dragOriginX = e.GetPosition(reference).X;
        _dragStartCellWidth = Math.Max(TileMetrics.MinHeight, ActualWidth - GripWidth);

        // Every cell resizes together, so this gap sits after N cells and moves N
        // times as far as any one of them grows. Dividing by N keeps the gap under
        // the cursor instead of running away from it.
        _dragDivisor = Math.Max(1, IndexInStrip() + 1);

        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMoved(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var reference = ReferenceElement();
        if (reference is null)
        {
            return;
        }

        var delta = e.GetPosition(reference).X - _dragOriginX;
        var width = _dragStartCellWidth + (delta / _dragDivisor);

        RaiseEvent(new CellWidthDragEventArgs(CellWidthDragEvent, width, completed: false));
        e.Handled = true;
    }

    private void OnGripReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Grip.ReleaseMouseCapture();

        if (!Grip.IsMouseOver)
        {
            Play("GripOut");
        }

        RaiseEvent(new CellWidthDragEventArgs(CellWidthDragEvent, ActualWidth - GripWidth, completed: true));
        e.Handled = true;
    }

    /// <summary>
    /// A frame of reference that does not move while cells resize. The window itself
    /// works; measuring against this control would not, because it is one of the things
    /// being resized.
    /// </summary>
    private IInputElement? ReferenceElement() => Window.GetWindow(this);

    /// <summary>How many tiles sit to the left of this one in the strip.</summary>
    private int IndexInStrip()
    {
        if (VisualTreeHelper.GetParent(this) is not FrameworkElement container)
        {
            return 0;
        }

        return VisualTreeHelper.GetParent(container) is Panel panel
            ? Math.Max(0, panel.Children.IndexOf(container))
            : 0;
    }

    /// <summary>
    /// The one-shot part of the feedback. The pill itself is driven by
    /// <see cref="ShotViewModel.CopyBadge"/>, so re-copying the same tile still
    /// gives a visible flash even though the pill was already showing.
    /// </summary>
    private void OnCopyFeedback(object? sender, string message) => Play("Flash");

    /// <summary>
    /// Starts one of the storyboards from this control's resources.
    /// <c>isControllable</c> is what makes a second click restart the pill instead
    /// of stacking a second one on top.
    /// </summary>
    private void Play(string key)
    {
        if (TryFindResource(key) is Storyboard storyboard)
        {
            storyboard.Begin(this, isControllable: true);
        }
    }
}
