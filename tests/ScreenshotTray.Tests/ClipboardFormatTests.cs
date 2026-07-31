using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotTray.Services;
using Xunit;

namespace ScreenshotTray.Tests;

/// <summary>
/// Byte-level checks on the clipboard payloads. The clipboard itself needs an STA
/// thread and a real desktop, so what is worth testing here is the part that is
/// hand-built and easy to get subtly wrong: the BITMAPV5HEADER and the
/// composite-onto-white that stops transparency pasting as black.
/// </summary>
public class ClipboardFormatTests
{
    /// <summary>A 2x2 image: opaque red, half-transparent green, fully transparent, opaque blue.</summary>
    private static BitmapSource MakeTestImage()
    {
        // Bgra32, straight (non-premultiplied) alpha, in B, G, R, A order.
        byte[] pixels =
        [
            0, 0, 255, 255,      // top-left: red, opaque
            0, 255, 0, 128,      // top-right: green, half alpha
            0, 0, 0, 0,          // bottom-left: fully transparent
            255, 0, 0, 255,      // bottom-right: blue, opaque
        ];

        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 2 * 4);
        image.Freeze();
        return image;
    }

    [Fact]
    public void DibV5_header_declares_the_fields_Windows_expects()
    {
        var bytes = ClipboardService.BuildDibV5(MakeTestImage()).ToArray();

        Assert.Equal(124 + (2 * 2 * 4), bytes.Length);

        Assert.Equal(124u, BitConverter.ToUInt32(bytes, 0));            // bV5Size
        Assert.Equal(2, BitConverter.ToInt32(bytes, 4));                // width
        Assert.Equal(2, BitConverter.ToInt32(bytes, 8));                // height, positive means bottom-up
        Assert.Equal((ushort)1, BitConverter.ToUInt16(bytes, 12));      // planes
        Assert.Equal((ushort)32, BitConverter.ToUInt16(bytes, 14));     // bits per pixel
        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 16));             // BI_BITFIELDS
        Assert.Equal(16u, BitConverter.ToUInt32(bytes, 20));            // bV5SizeImage

        // The channel masks are what tell a consumer this is BGRA with real alpha.
        Assert.Equal(0x00FF0000u, BitConverter.ToUInt32(bytes, 40));    // red
        Assert.Equal(0x0000FF00u, BitConverter.ToUInt32(bytes, 44));    // green
        Assert.Equal(0x000000FFu, BitConverter.ToUInt32(bytes, 48));    // blue
        Assert.Equal(0xFF000000u, BitConverter.ToUInt32(bytes, 52));    // alpha

        Assert.Equal(0x73524742u, BitConverter.ToUInt32(bytes, 56));    // LCS_sRGB
        Assert.Equal(4u, BitConverter.ToUInt32(bytes, 108));            // LCS_GM_IMAGES
    }

    [Fact]
    public void DibV5_pixels_are_written_bottom_up()
    {
        // A DIB with a positive height is bottom-up, so the last image row has to
        // come first. Getting this backwards pastes every screenshot upside down.
        var bytes = ClipboardService.BuildDibV5(MakeTestImage()).ToArray();

        var firstRow = bytes.AsSpan(124, 8).ToArray();

        // The source image's bottom row is transparent, then opaque blue.
        Assert.Equal<byte[]>([0, 0, 0, 0, 255, 0, 0, 255], firstRow);
    }

    [Fact]
    public void DibV5_keeps_the_alpha_channel_intact()
    {
        var bytes = ClipboardService.BuildDibV5(MakeTestImage()).ToArray();

        // Top row comes last in a bottom-up DIB: opaque red then half-alpha green.
        var topRow = bytes.AsSpan(124 + 8, 8).ToArray();

        Assert.Equal(255, topRow[3]);
        Assert.Equal(128, topRow[7]);
    }

    [Fact]
    public void FlattenOntoWhite_makes_every_pixel_opaque()
    {
        var flattened = ClipboardService.FlattenOntoWhite(MakeTestImage());

        var pixels = new byte[2 * 2 * 4];
        flattened.CopyPixels(pixels, 2 * 4, 0);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i]);
        }
    }

    [Fact]
    public void FlattenOntoWhite_turns_a_transparent_pixel_white_rather_than_black()
    {
        // This is the whole reason the flattening exists. Handing a transparent
        // screenshot to a legacy CF_DIB consumer unflattened is what produces the
        // classic black rectangles on paste.
        var flattened = ClipboardService.FlattenOntoWhite(MakeTestImage());

        var pixels = new byte[2 * 2 * 4];
        flattened.CopyPixels(pixels, 2 * 4, 0);

        // Bottom-left of the source was fully transparent; it is index 2 in row
        // order, so byte offset 8.
        Assert.Equal(255, pixels[8]);      // blue
        Assert.Equal(255, pixels[9]);      // green
        Assert.Equal(255, pixels[10]);     // red
    }

    [Fact]
    public void FlattenOntoWhite_composites_a_half_transparent_pixel_towards_white()
    {
        var flattened = ClipboardService.FlattenOntoWhite(MakeTestImage());

        var pixels = new byte[2 * 2 * 4];
        flattened.CopyPixels(pixels, 2 * 4, 0);

        // Top-right was green at alpha 128, so it should land near half way between
        // green and white: low-ish blue and red, high green.
        var blue = pixels[4];
        var green = pixels[5];
        var red = pixels[6];

        Assert.InRange(blue, 120, 135);
        Assert.Equal(255, green);
        Assert.InRange(red, 120, 135);
    }

    [Fact]
    public void FlattenOntoWhite_leaves_opaque_pixels_exactly_alone()
    {
        var flattened = ClipboardService.FlattenOntoWhite(MakeTestImage());

        var pixels = new byte[2 * 2 * 4];
        flattened.CopyPixels(pixels, 2 * 4, 0);

        // Top-left was opaque red.
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(255, pixels[2]);
    }
}
