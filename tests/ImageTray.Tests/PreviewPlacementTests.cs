using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageTray.Controls;
using ImageTray.Models;
using ImageTray.ViewModels;
using Xunit;

namespace ImageTray.Tests;

/// <summary>
/// Opens the real preview over a real window and measures where everything landed.
/// </summary>
/// <remarks>
/// <para>
/// The preview's position is arithmetic across three frames of reference — the
/// display, the strip window, and the popup's own contents — and every time it has
/// gone wrong it has gone wrong silently: the popup opens, it just opens in the wrong
/// place, or its buttons sit on top of each other. Reading the numbers back off the
/// running control is the only check that catches that, and a screenshot is a slow
/// and unreliable way to take those measurements.
/// </para>
/// <para>
/// These need a desktop session: they show a window and open a popup.
/// </para>
/// </remarks>
[Collection("ui")]
public class PreviewPlacementTests
{
    /// <summary>How far the buttons may sit from the thumbnail before it counts as a miss.</summary>
    private const double CentringTolerance = 2;

    public static TheoryData<string, StripPosition, double> Cases =>
        new()
        {
            { "wide", StripPosition.Bottom, 0.1 },
            { "wide", StripPosition.Bottom, 0.5 },
            { "wide", StripPosition.Bottom, 0.95 },
            { "wide", StripPosition.Top, 0.1 },
            { "wide", StripPosition.Top, 0.95 },
            { "wide", StripPosition.Middle, 0.5 },
            { "small", StripPosition.Bottom, 0.1 },
            { "small", StripPosition.Bottom, 0.5 },
            { "small", StripPosition.Bottom, 0.95 },
            { "small", StripPosition.Top, 0.5 },
            { "small", StripPosition.Middle, 0.95 },
            { "tall", StripPosition.Bottom, 0.5 },
        };

    public enum StripPosition
    {
        Top,
        Middle,
        Bottom,
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_preview_stays_clear_of_the_strip(string image, StripPosition position, double anchorFraction) =>
        Probe(image, position, anchorFraction, layout =>
            Assert.False(
                layout.Popup.IntersectsWith(layout.Strip),
                $"The preview overlaps the strip. {layout}"));

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_preview_stays_on_the_display(string image, StripPosition position, double anchorFraction) =>
        Probe(image, position, anchorFraction, layout =>
            Assert.True(
                layout.Work.Contains(layout.Popup),
                $"The preview hangs off the display. {layout}"));

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_buttons_stay_inside_the_preview(string image, StripPosition position, double anchorFraction) =>
        Probe(image, position, anchorFraction, layout =>
            Assert.True(
                layout.Popup.Contains(layout.Actions),
                $"The buttons hang off the preview. {layout}"));

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_buttons_stay_clear_of_close(string image, StripPosition position, double anchorFraction) =>
        Probe(image, position, anchorFraction, layout =>
            Assert.False(
                layout.Actions.IntersectsWith(layout.Close),
                $"The buttons overlap Close. {layout}"));

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_buttons_line_up_with_the_thumbnail(string image, StripPosition position, double anchorFraction) =>
        Probe(image, position, anchorFraction, layout =>
        {
            // Against the end of the strip the buttons cannot be centred on the
            // thumbnail and stay inside the preview, so being hard against the limit
            // they ran into is the right answer there.
            var clampedLeft = layout.Actions.Left <= layout.Popup.Left + layout.BandPadding + 1;
            var clampedRight = layout.Actions.Right >= layout.Close.Left - layout.BandGap - 1;

            if (clampedLeft || clampedRight)
            {
                return;
            }

            var offset = Math.Abs(Centre(layout.Actions) - Centre(layout.Anchor));

            Assert.True(
                offset <= CentringTolerance * layout.Scale,
                $"The buttons are {offset:F0}px from the thumbnail. {layout}");
        });

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_buttons_are_on_the_side_of_the_preview_facing_the_strip(
        string image, StripPosition position, double anchorFraction) =>
        Probe(image, position, anchorFraction, layout =>
        {
            var above = layout.Popup.Bottom <= layout.Strip.Top;
            var actionsAtBottom = Centre(layout.Actions, vertical: true) > Centre(layout.Popup, vertical: true);

            Assert.True(
                above == actionsAtBottom,
                $"The buttons are on the far side of the preview from the strip. {layout}");
        });

    private static double Centre(Rect rect, bool vertical = false) =>
        vertical ? rect.Top + (rect.Height / 2) : rect.Left + (rect.Width / 2);

    /// <summary>
    /// Builds a strip with one thumbnail in it, opens the preview on that thumbnail,
    /// and hands the measurements to the check. Everything is in device pixels, which
    /// is what the three frames of reference have in common.
    /// </summary>
    private static void Probe(string image, StripPosition position, double anchorFraction, Action<Layout> check) =>
        UiThread.Run(() =>
        {
            var (pixelWidth, pixelHeight) = image switch
            {
                "small" => (117, 96),
                "tall" => (700, 2000),
                _ => (1920, 1080),
            };

            var path = WriteImage(pixelWidth, pixelHeight);

            var work = SystemParameters.WorkArea;
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Width = Math.Min(1200, work.Width),
                Height = 200,
                Left = work.Left + 40,
                ResizeMode = ResizeMode.NoResize,
            };

            window.Top = position switch
            {
                StripPosition.Top => work.Top,
                StripPosition.Middle => work.Top + ((work.Height - window.Height) / 2),
                _ => work.Bottom - window.Height,
            };

            var anchor = new Border
            {
                Width = 150,
                Height = 150,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness((window.Width - 150) * anchorFraction, 10, 0, 0),
            };

            var preview = new ImagePreview();
            window.Content = new Grid { Children = { anchor, preview } };

            try
            {
                window.Show();
                Settle();

                var shot = new ShotViewModel(new Shot(
                    path, File.GetLastWriteTimeUtc(path), new FileInfo(path).Length, pixelWidth, pixelHeight));

                preview.ShowFor(shot, window, anchor, pinned: true);

                Assert.True(WaitFor(() => preview.IsOpen), "The preview never opened.");
                Settle();

                check(Measure(preview, window, anchor));
            }
            finally
            {
                preview.Release();
                window.Close();
                Settle();
                TryDelete(path);
            }
        });

    private static Layout Measure(ImagePreview preview, Window window, FrameworkElement anchor)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var frame = (FrameworkElement)preview.FindName("Frame");

        return new Layout(
            Popup: ScreenRect(frame),
            Actions: ScreenRect((FrameworkElement)preview.FindName("Actions")),
            Close: ScreenRect((FrameworkElement)preview.FindName("CloseText")),
            Strip: ScreenRect(window),
            Anchor: ScreenRect(anchor),
            Work: Scale(SystemParameters.WorkArea, dpi.DpiScaleX, dpi.DpiScaleY),
            Scale: dpi.DpiScaleX);
    }

    private static Rect ScreenRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

        return new Rect(topLeft, bottomRight);
    }

    private static Rect Scale(Rect rect, double x, double y) =>
        new(rect.Left * x, rect.Top * y, rect.Width * x, rect.Height * y);

    private static string WriteImage(int pixelWidth, int pixelHeight)
    {
        var path = Path.Combine(Path.GetTempPath(), $"preview-placement-{Guid.NewGuid():N}.png");

        var bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var file = File.Create(path);
        encoder.Save(file);

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Runs whatever the dispatcher has queued, including layout.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Settle();
            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>
    /// Every measurement one check might want, so that a failure says what the layout
    /// actually was rather than only which assertion tripped.
    /// </summary>
    public sealed record Layout(
        Rect Popup,
        Rect Actions,
        Rect Close,
        Rect Strip,
        Rect Anchor,
        Rect Work,
        double Scale)
    {
        /// <summary>The inset the buttons keep from the end of their band, in device pixels.</summary>
        public double BandPadding => 10 * Scale;

        public double BandGap => 16 * Scale;

        public override string ToString() =>
            $"popup={Show(Popup)} actions={Show(Actions)} close={Show(Close)} " +
            $"strip={Show(Strip)} thumbnail={Show(Anchor)} work={Show(Work)} scale={Scale:F2}";

        private static string Show(Rect r) => $"({r.Left:F0},{r.Top:F0} {r.Width:F0}x{r.Height:F0})";
    }
}

/// <summary>
/// One WPF thread shared by every test in the collection. An Application has to exist
/// for the styles the controls ask for by key, and there can only be one per process.
/// </summary>
internal static class UiThread
{
    private static Dispatcher? _dispatcher;

    public static void Run(Action body)
    {
        Start();

        ExceptionDispatchInfo? failure = null;

        _dispatcher!.Invoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        failure?.Throw();
    }

    private static void Start()
    {
        if (_dispatcher is not null)
        {
            return;
        }

        var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            app.Resources.MergedDictionaries.Add(Dictionary("/Theme/Light.xaml"));
            app.Resources.MergedDictionaries.Add(Dictionary("/Styles.xaml"));

            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();

            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    private static ResourceDictionary Dictionary(string path) =>
        new() { Source = new Uri($"pack://application:,,,/ImageTray;component{path}", UriKind.Absolute) };
}
