using ScreenshotTray;
using Xunit;

namespace ScreenshotTray.Tests;

/// <summary>
/// The sizing rules: uniform cells, with each screenshot fitted inside its own.
/// </summary>
public class TileMetricsTests
{
    private const double Tray = 340;
    private const double Aspect = TileMetrics.DefaultCellAspect;

    private static TileSize Cell(double trayHeight = Tray, double cellAspect = Aspect) =>
        TileMetrics.ResolveCell(trayHeight, cellAspect);

    private static TileSize ImageBox(int width, int height, double trayHeight = Tray, double cellAspect = Aspect) =>
        TileMetrics.ResolveImageBox(Cell(trayHeight, cellAspect), (double)width / height);

    [Fact]
    public void Every_cell_is_the_same_size_whatever_the_screenshot()
    {
        var cell = Cell();

        Assert.Equal(Tray, cell.Height, precision: 3);
        Assert.Equal(Tray * Aspect, cell.Width, precision: 3);
    }

    [Fact]
    public void Dragging_the_tray_taller_grows_the_cell()
    {
        Assert.True(Cell(400).Height > Cell(150).Height);
        Assert.True(Cell(400).Width > Cell(150).Width);
    }

    [Fact]
    public void The_cell_aspect_sets_the_width_and_leaves_the_height_alone()
    {
        // What the splitter and the width slider control.
        var narrow = Cell(cellAspect: 0.75);
        var wide = Cell(cellAspect: 2.5);

        Assert.Equal(Tray, narrow.Height, precision: 3);
        Assert.Equal(Tray, wide.Height, precision: 3);
        Assert.Equal(Tray * 0.75, narrow.Width, precision: 3);
        Assert.Equal(Tray * 2.5, wide.Width, precision: 3);
    }

    [Theory]
    // A real number out of range is clamped: you asked for something, so get the
    // nearest allowed thing.
    [InlineData(0.1, TileMetrics.MinCellAspect)]
    [InlineData(1.5, 1.5)]
    [InlineData(99, TileMetrics.MaxCellAspect)]
    // Nonsense falls back to the default instead, because a corrupt settings file
    // should give you ordinary thumbnails, not the narrowest possible slivers.
    [InlineData(0, TileMetrics.DefaultCellAspect)]
    [InlineData(-3, TileMetrics.DefaultCellAspect)]
    [InlineData(double.NaN, TileMetrics.DefaultCellAspect)]
    [InlineData(double.PositiveInfinity, TileMetrics.DefaultCellAspect)]
    public void ClampCellAspect_keeps_the_cell_shape_sane(double given, double expected) =>
        Assert.Equal(expected, TileMetrics.ClampCellAspect(given), precision: 3);

    [Fact]
    public void A_cell_never_collapses_below_the_minimum()
    {
        var cell = Cell(1);

        Assert.True(cell.Height >= TileMetrics.MinHeight);
        Assert.True(cell.Width >= TileMetrics.MinHeight);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(700, 1400)]
    [InlineData(642, 64)]
    [InlineData(2184, 411)]
    [InlineData(900, 900)]
    [InlineData(5120, 1440)]
    public void The_aspect_ratio_is_always_preserved(int width, int height)
    {
        var image = ImageBox(width, height);

        Assert.Equal((double)width / height, image.Width / image.Height, precision: 3);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(700, 1400)]
    [InlineData(642, 64)]
    [InlineData(5120, 1440)]
    public void The_image_always_fits_inside_its_cell(int width, int height)
    {
        var cell = Cell();
        var image = ImageBox(width, height);

        Assert.True(image.Width <= cell.Width + 0.001);
        Assert.True(image.Height <= cell.Height + 0.001);
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(1.0)]
    [InlineData(1.78)]
    [InlineData(3.0)]
    public void The_image_still_fits_at_any_cell_shape(double cellAspect)
    {
        var cell = Cell(cellAspect: cellAspect);

        foreach (var (w, h) in new[] { (1920, 1080), (700, 1400), (642, 64), (900, 900) })
        {
            var image = ImageBox(w, h, cellAspect: cellAspect);
            Assert.True(image.Width <= cell.Width + 0.001);
            Assert.True(image.Height <= cell.Height + 0.001);
        }
    }

    [Fact]
    public void A_screenshot_taller_than_the_cell_shape_is_limited_by_height()
    {
        var cell = Cell();
        var image = ImageBox(700, 1400);

        Assert.Equal(cell.Height, image.Height, precision: 3);
        Assert.True(image.Width < cell.Width);
    }

    [Fact]
    public void A_screenshot_wider_than_the_cell_shape_is_limited_by_width()
    {
        var cell = Cell();
        var image = ImageBox(642, 64);

        Assert.Equal(cell.Width, image.Width, precision: 3);
        Assert.True(image.Height < cell.Height);
    }

    [Fact]
    public void A_screenshot_matching_the_cell_shape_fills_it_exactly()
    {
        var cell = Cell();
        var image = ImageBox(1920, 1080);

        Assert.Equal(cell.Width, image.Width, precision: 3);
        Assert.Equal(cell.Height, image.Height, precision: 3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_nonsense_image_aspect_ratio_does_not_produce_a_nonsense_box(double aspect)
    {
        var image = TileMetrics.ResolveImageBox(Cell(), aspect);

        Assert.False(double.IsNaN(image.Width));
        Assert.False(double.IsNaN(image.Height));
        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);
    }
}
