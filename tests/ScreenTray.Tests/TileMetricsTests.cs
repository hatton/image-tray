using ScreenTray;
using Xunit;

namespace ScreenTray.Tests;

/// <summary>
/// The sizing rules: uniform cells, with each screenshot fitted inside its own.
/// </summary>
public class TileMetricsTests
{
    private const double Tray = 340;

    private static TileSize ImageBox(int width, int height, double trayHeight = Tray) =>
        TileMetrics.ResolveImageBox(TileMetrics.ResolveCell(trayHeight), (double)width / height);

    [Fact]
    public void Every_cell_is_the_same_size_whatever_the_screenshot()
    {
        var cell = TileMetrics.ResolveCell(Tray);

        Assert.Equal(Tray, cell.Height, precision: 3);
        Assert.Equal(Tray * TileMetrics.CellAspect, cell.Width, precision: 3);
    }

    [Fact]
    public void Dragging_the_tray_taller_grows_the_cell()
    {
        Assert.True(TileMetrics.ResolveCell(400).Height > TileMetrics.ResolveCell(150).Height);
        Assert.True(TileMetrics.ResolveCell(400).Width > TileMetrics.ResolveCell(150).Width);
    }

    [Fact]
    public void A_cell_never_collapses_below_the_minimum()
    {
        var cell = TileMetrics.ResolveCell(1);

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
        var cell = TileMetrics.ResolveCell(Tray);
        var image = ImageBox(width, height);

        Assert.True(image.Width <= cell.Width + 0.001);
        Assert.True(image.Height <= cell.Height + 0.001);
    }

    [Fact]
    public void A_screenshot_taller_than_the_cell_shape_is_limited_by_height()
    {
        var cell = TileMetrics.ResolveCell(Tray);
        var image = ImageBox(700, 1400);

        Assert.Equal(cell.Height, image.Height, precision: 3);
        Assert.True(image.Width < cell.Width);
    }

    [Fact]
    public void A_screenshot_wider_than_the_cell_shape_is_limited_by_width()
    {
        var cell = TileMetrics.ResolveCell(Tray);
        var image = ImageBox(642, 64);

        Assert.Equal(cell.Width, image.Width, precision: 3);
        Assert.True(image.Height < cell.Height);
    }

    [Fact]
    public void A_16_by_9_screenshot_fills_its_cell_almost_exactly()
    {
        var cell = TileMetrics.ResolveCell(Tray);
        var image = ImageBox(1920, 1080);

        Assert.Equal(cell.Width, image.Width, precision: 3);
        Assert.Equal(cell.Height, image.Height, precision: 3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_nonsense_aspect_ratio_does_not_produce_a_nonsense_box(double aspect)
    {
        var image = TileMetrics.ResolveImageBox(TileMetrics.ResolveCell(Tray), aspect);

        Assert.False(double.IsNaN(image.Width));
        Assert.False(double.IsNaN(image.Height));
        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);
    }
}
