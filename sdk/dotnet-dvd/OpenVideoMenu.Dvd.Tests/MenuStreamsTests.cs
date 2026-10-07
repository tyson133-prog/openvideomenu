using OpenVideoMenu.Dvd.Art;

namespace OpenVideoMenu.Dvd.Tests;

/// <summary>Reading a menu cell's streams: how many pictures its video has decides motion or still.</summary>
public class MenuStreamsTests
{
    /// <summary>One 2048-byte pack holding one MPEG-2 video PES packet with <paramref name="payload"/>.</summary>
    private static byte[] VideoPack(byte[] payload)
    {
        var b = new byte[2048];
        b[2] = 1; b[3] = 0xBA;                 // pack header (14 bytes, no stuffing)
        var o = 14;
        b[o + 2] = 1; b[o + 3] = 0xE0;         // video stream 0
        var len = 3 + payload.Length;          // PES header (flags, flags, header length 0) + payload
        b[o + 4] = (byte)(len >> 8); b[o + 5] = (byte)len;
        b[o + 6] = 0x80; b[o + 7] = 0; b[o + 8] = 0;
        payload.CopyTo(b, o + 9);
        return b;
    }

    private static readonly byte[] Picture = { 0, 0, 1, 0, 0x11, 0x22 };

    [Fact]
    public void Counts_picture_start_codes_in_the_video()
    {
        var cell = VideoPack(Picture.Concat(Picture).Concat(Picture).ToArray());
        Assert.Equal(3, MenuStreams.CountPictures(cell));
        Assert.Equal(2, MenuStreams.CountPictures(cell, upTo: 2));
    }

    [Fact]
    public void A_start_code_split_between_packets_counts_once()
    {
        // "00 00" ends the first packet, "01 00" starts the next.
        var cell = VideoPack(new byte[] { 0x55, 0, 0 }).Concat(VideoPack(new byte[] { 1, 0, 0x33 })).ToArray();
        Assert.Equal(1, MenuStreams.CountPictures(cell));
    }

    [Fact]
    public void Other_start_codes_and_other_streams_dont_count()
    {
        // A sequence header (00 00 01 B3) in the video is not a picture.
        Assert.Equal(0, MenuStreams.CountPictures(VideoPack(new byte[] { 0, 0, 1, 0xB3, 0x10 })));
    }
}
