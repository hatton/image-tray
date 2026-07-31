using ScreenshotTray.Models;
using ScreenshotTray.Services;
using Xunit;

namespace ScreenshotTray.Tests;

public class WindowPlacementTests
{
    [Fact]
    public void A_rectangle_on_the_primary_display_is_usable()
    {
        var placement = new WindowPlacementData { Left = 100, Top = 100, Right = 1020, Bottom = 300, ShowCommand = 1 };

        Assert.True(WindowPlacementService.IsUsable(placement));
    }

    [Fact]
    public void A_rectangle_far_off_in_space_is_rejected()
    {
        // What a saved position looks like after the display it lived on has been
        // unplugged, or after the monitors were rearranged around it.
        var placement = new WindowPlacementData
        {
            Left = -30000,
            Top = -30000,
            Right = -29080,
            Bottom = -29800,
            ShowCommand = 1,
        };

        Assert.False(WindowPlacementService.IsUsable(placement));
    }

    [Fact]
    public void A_rectangle_with_no_area_is_rejected()
    {
        Assert.False(WindowPlacementService.IsUsable(
            new WindowPlacementData { Left = 200, Top = 200, Right = 200, Bottom = 400 }));

        Assert.False(WindowPlacementService.IsUsable(
            new WindowPlacementData { Left = 200, Top = 200, Right = 900, Bottom = 200 }));
    }

    [Fact]
    public void An_inverted_rectangle_is_rejected()
    {
        Assert.False(WindowPlacementService.IsUsable(
            new WindowPlacementData { Left = 900, Top = 400, Right = 200, Bottom = 100 }));
    }
}
