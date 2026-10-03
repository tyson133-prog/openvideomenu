using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Tests;

public class SubpictureTests
{
    /// <summary>A 4x2 subpicture at (10,20): the top row pattern pixels (1), the bottom row emphasis (2).</summary>
    private static byte[] Spu()
    {
        var rle = new byte[] { 0x11, 0x12 }; // run 4 of colour 1; run 4 of colour 2
        var ctrl = new byte[]
        {
            0x00, 0x00, 0x00, 0x06,             // date, offset of the next sequence (itself: last)
            0x05, 0x00, 0xA0, 0x0D, 0x01, 0x40, 0x15, // area x 10-13, y 20-21
            0x06, 0x00, 0x04, 0x00, 0x05,       // field offsets: top at 4, bottom at 5
            0xFF,
        };
        var size = 4 + rle.Length + ctrl.Length;
        var ctrlAt = 4 + rle.Length;
        return new byte[] { (byte)(size >> 8), (byte)size, (byte)(ctrlAt >> 8), (byte)ctrlAt }.Concat(rle).Concat(ctrl).ToArray();
    }

    [Fact]
    public void Decodes_area_and_both_fields()
    {
        var s = Subpicture.Decode(Spu())!;
        Assert.Equal((10, 20, 4, 2), (s.X, s.Y, s.Width, s.Height));
        Assert.All(Enumerable.Range(0, 4), x => Assert.Equal(1, s[x, 0]));
        Assert.All(Enumerable.Range(0, 4), x => Assert.Equal(2, s[x, 1]));
    }

    [Fact]
    public void Finds_the_subpicture_in_a_program_stream_pack()
    {
        var spu = Spu();
        var sector = new byte[2048];
        new byte[] { 0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 0, 0, 3, 0xF8 }.CopyTo(sector, 0); // pack header, no stuffing
        var pesLen = 3 + 1 + spu.Length;                                                         // header ext + substream + data
        new byte[] { 0, 0, 1, 0xBD, (byte)(pesLen >> 8), (byte)pesLen, 0x81, 0x00, 0x00, 0x20 }.CopyTo(sector, 14);
        spu.CopyTo(sector, 24);

        var s = MenuStreams.ReadSubpicture(sector);
        Assert.NotNull(s);
        Assert.Equal(4, s!.Width);
        Assert.False(MenuStreams.HasAudio(sector));
    }

    [Fact]
    public void Colour_words_split_into_palette_indexes_and_contrast()
    {
        var (colors, contrast) = HighlightRenderer.SplitColorWord(0x3210_FEDCu);
        Assert.Equal(new[] { 0, 1, 2, 3 }, colors);
        Assert.Equal(new[] { 0xC, 0xD, 0xE, 0xF }, contrast);
    }

    [Fact]
    public void Renders_the_button_part_in_highlight_colours_with_transparent_background()
    {
        var spu = Subpicture.Decode(Spu())!;
        var palette = Enumerable.Range(0, 16).Select(i => 0x00_EB_80_80u).ToList(); // white
        palette[1] = 0x00_51_F0_5A;                                                   // red
        var button = new DvdButton { Number = 1, X0 = 9, Y0 = 20, X1 = 13, Y1 = 21 };
        // Pixel type 1 -> palette 1, opaque; type 2 -> palette 0, contrast 0 (invisible).
        using var img = HighlightRenderer.Render(spu, button, 0x0000_00F0u | 0x0010_0000u, palette)!;
        Assert.Equal((5, 2), (img.Width, img.Height));
        Assert.Equal(0, img[0, 0].A);    // left of the subpicture
        Assert.Equal(255, img[1, 0].A);  // pattern pixel
        Assert.True(img[1, 0].R > 200 && img[1, 0].G < 60);
        Assert.Equal(0, img[1, 1].A);    // emphasis pixel with contrast 0
    }

    [Fact]
    public void Highlight_png_decodes_to_the_same_pixels()
    {
        using var img = new RgbaImage(3, 2);
        img[1, 0] = new Rgba(200, 10, 20, 255);
        img[2, 1] = new Rgba(1, 2, 3, 127);
        using var decoded = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(img.ToPng());
        Assert.Equal((3, 2), (decoded.Width, decoded.Height));
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 3; x++)
            {
                var (want, got) = (img[x, y], decoded[x, y]);
                Assert.Equal((want.R, want.G, want.B, want.A), (got.R, got.G, got.B, got.A));
            }
    }

    [Fact]
    public void Nothing_visible_means_no_image()
    {
        var spu = Subpicture.Decode(Spu())!;
        var palette = Enumerable.Repeat(0x00_EB_80_80u, 16).ToList();
        Assert.Null(HighlightRenderer.Render(spu, new DvdButton { Number = 1, X0 = 10, Y0 = 20, X1 = 13, Y1 = 21 }, 0u, palette));
    }
}
