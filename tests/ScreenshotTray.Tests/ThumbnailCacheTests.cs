using ScreenshotTray.Services;
using Xunit;

namespace ScreenshotTray.Tests;

public class ThumbnailCacheTests
{
    [Theory]
    [InlineData(1, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 64)]
    [InlineData(64, 64)]
    [InlineData(120, 128)]
    [InlineData(128, 128)]
    [InlineData(129, 160)]
    [InlineData(0, 32)]
    [InlineData(-40, 32)]
    public void BucketFor_rounds_up_to_the_next_decode_bucket(double height, int expected) =>
        Assert.Equal(expected, ThumbnailCache.BucketFor(height));

    [Fact]
    public void Nearby_heights_share_a_bucket_so_a_drag_does_not_re_decode_every_pixel()
    {
        // The point of bucketing: dragging the window from 130px to 158px tall must
        // not touch the decoder at all.
        Assert.Equal(ThumbnailCache.BucketFor(130), ThumbnailCache.BucketFor(158));
        Assert.NotEqual(ThumbnailCache.BucketFor(130), ThumbnailCache.BucketFor(170));
    }
}
