using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenVideoMenu.Dvd.Art;

/// <summary>
/// Reads the parts of a menu cell (MPEG-2 program stream, as cut from a menu VOB) that ffmpeg doesn't
/// give us: the button-highlight subpicture and whether the cell carries audio. Written from the public
/// DVD-Video format descriptions, like the rest of the disc reader.
/// </summary>
public static class MenuStreams
{
    private const int Sector = 2048;

    /// <summary>One PES packet: stream id, substream (private streams), payload, and the sector it's in.</summary>
    private readonly record struct Packet(int Id, int Sub, int Offset, int Length, int Sector = 0);

    private static IEnumerable<Packet> Packets(byte[] b)
    {
        for (var p = 0; p + Sector <= b.Length; p += Sector)
        {
            if (b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1 || b[p + 3] != 0xBA) continue;
            var o = p + 14 + (b[p + 13] & 7); // MPEG-2 pack header + stuffing
            while (o + 6 <= p + Sector && b[o] == 0 && b[o + 1] == 0 && b[o + 2] == 1)
            {
                var id = b[o + 3];
                var len = b[o + 4] << 8 | b[o + 5];
                var body = o + 6;
                if (body + len > p + Sector) break;
                if (id is 0xBF or 0xBB)
                    yield return new Packet(id, b[body], body + 1, len - 1, p / Sector);
                else if (id == 0xBD)
                {
                    var hdr = body + 3 + b[body + 2];
                    if (hdr < body + len) yield return new Packet(id, b[hdr], hdr + 1, body + len - hdr - 1, p / Sector);
                }
                else if (id is >= 0xC0 and <= 0xDF)
                    yield return new Packet(id, 0, body, len, p / Sector);
                else if (id is >= 0xE0 and <= 0xEF)
                {
                    var hdr = body + 3 + b[body + 2]; // MPEG-2 PES header
                    if (hdr < body + len) yield return new Packet(id, 0, hdr, body + len - hdr, p / Sector);
                }
                o = body + len;
            }
        }
    }

    /// <summary>
    /// How many pictures the cell's video has (MPEG-2 picture start codes), counting up to <paramref name="upTo"/>.
    /// One picture is a still; a few more can be the same still repeated; more is motion.
    /// </summary>
    public static int CountPictures(byte[] cell, int upTo = int.MaxValue)
    {
        var count = 0;
        var zeros = 0; // zero bytes just before, carried across packets (a start code can straddle two)
        var afterPrefix = false;
        foreach (var pk in Packets(cell))
        {
            if (pk.Id is < 0xE0 or > 0xEF) continue;
            for (var i = pk.Offset; i < pk.Offset + pk.Length; i++)
            {
                var v = cell[i];
                if (afterPrefix)
                {
                    afterPrefix = false;
                    if (v == 0x00 && ++count >= upTo) return count;
                }
                if (v == 0x01 && zeros >= 2) afterPrefix = true;
                zeros = v == 0 ? zeros + 1 : 0;
            }
        }
        return count;
    }

    /// <summary>The cell has an audio stream (AC-3/DTS/LPCM in private stream 1, or MPEG audio).</summary>
    public static bool HasAudio(byte[] cell)
    {
        foreach (var pk in Packets(cell))
            if (pk.Id is >= 0xC0 and <= 0xDF || pk.Id == 0xBD && pk.Sub is >= 0x80 and <= 0xAF) return true;
        return false;
    }

    /// <summary>The first complete subpicture unit of a stream (0x20 = subpicture stream 1).</summary>
    internal static byte[]? FirstSpu(byte[] cell, int stream)
    {
        byte[]? data = null;
        var have = 0;
        var need = 0;
        foreach (var pk in Packets(cell))
        {
            if (pk.Id != 0xBD || pk.Sub != stream || pk.Length <= 0) continue;
            if (data is null)
            {
                if (pk.Length < 2) continue;
                need = cell[pk.Offset] << 8 | cell[pk.Offset + 1];
                if (need < 4) return null;
                data = new byte[need];
            }
            var n = Math.Min(pk.Length, need - have);
            Array.Copy(cell, pk.Offset, data, have, n);
            have += n;
            if (have >= need) return data;
        }
        return null;
    }

    /// <summary>
    /// The subpicture a highlight applies to: the last unit complete within the video unit the highlight's
    /// navigation pack starts (<paramref name="navSector"/>, counted from the first sector of
    /// <paramref name="bytes"/>; the unit runs to the next navigation pack), else the first one after it. Of the subpicture streams, the first whose picture covers every button's rectangle
    /// (a title has its subtitles there too), else stream 0x20, else the first present. Null when there's none.
    /// </summary>
    public static Subpicture? SubpictureAt(byte[] bytes, int navSector, IReadOnlyList<(int X0, int Y0, int X1, int Y1)> buttons)
    {
        // Every complete unit of each stream, with the sector its last packet is in.
        var units = new SortedDictionary<int, List<(int Done, byte[] Data)>>();
        var partial = new Dictionary<int, (byte[] Data, int Have)>();
        var unitEnd = int.MaxValue;
        foreach (var pk in Packets(bytes))
        {
            // The next navigation pack (its PCI, private stream 2, substream 0) ends the highlight's video unit.
            if (pk.Id == 0xBF && pk.Sub == 0 && pk.Sector > navSector && pk.Sector < unitEnd) unitEnd = pk.Sector;
            if (pk.Id != 0xBD || pk.Sub is < 0x20 or > 0x3F || pk.Length <= 0) continue;
            if (!partial.TryGetValue(pk.Sub, out var u))
            {
                if (pk.Length < 2) continue;
                var need = bytes[pk.Offset] << 8 | bytes[pk.Offset + 1];
                if (need < 4) continue;
                u = (new byte[need], 0);
            }
            var n = Math.Min(pk.Length, u.Data.Length - u.Have);
            Array.Copy(bytes, pk.Offset, u.Data, u.Have, n);
            u.Have += n;
            if (u.Have < u.Data.Length) { partial[pk.Sub] = u; continue; }
            partial.Remove(pk.Sub);
            if (!units.TryGetValue(pk.Sub, out var list)) units[pk.Sub] = list = new();
            list.Add((pk.Sector, u.Data));
        }

        Subpicture? Pick(List<(int Done, byte[] Data)> list) =>
            Subpicture.Decode((list.LastOrDefault(x => x.Done < unitEnd).Data ?? list[0].Data));
        bool Covers(Subpicture s) => buttons.All(b => b.X0 >= s.X && b.Y0 >= s.Y && b.X1 < s.X + s.Width && b.Y1 < s.Y + s.Height);

        var picked = units.Values.Select(Pick).Where(s => s is not null).ToList();
        if (buttons.Count > 0 && picked.FirstOrDefault(s => Covers(s!)) is { } covering) return covering;
        return units.TryGetValue(0x20, out var main) ? Pick(main) : picked.FirstOrDefault();
    }

    /// <summary>
    /// The menu's highlight subpicture: stream 0x20, else the first subpicture stream present
    /// (menus normally carry one).
    /// </summary>
    public static Subpicture? ReadSubpicture(byte[] cell)
    {
        if (FirstSpu(cell, 0x20) is { } spu) return Subpicture.Decode(spu);
        var tried = new HashSet<int> { 0x20 };
        foreach (var pk in Packets(cell))
            if (pk.Id == 0xBD && pk.Sub is >= 0x20 and <= 0x3F && tried.Add(pk.Sub) && FirstSpu(cell, pk.Sub) is { } other)
                return Subpicture.Decode(other);
        return null;
    }
}

/// <summary>A decoded subpicture bitmap: 2-bit pixel types (0 background, 1 pattern, 2/3 emphasis).</summary>
public sealed class Subpicture
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Pixels { get; init; } = Array.Empty<byte>();

    public byte this[int x, int y] => Pixels[y * Width + x];

    /// <summary>Decodes an SPU: display area from its control sequence, then the RLE of both fields.</summary>
    public static Subpicture? Decode(byte[] spu)
    {
        if (spu.Length < 4) return null;
        var ctrl = spu[2] << 8 | spu[3];
        int x1 = 0, x2 = -1, y1 = 0, y2 = -1, top = -1, bottom = -1;
        var o = ctrl + 4; // first control sequence: date (2) + next (2), then commands
        for (var guard = 0; o < spu.Length && guard < 64; guard++)
        {
            var cmd = spu[o++];
            if (cmd == 0xFF) break;
            switch (cmd)
            {
                case 0x03 or 0x04: o += 2; break; // colours / contrast (the highlight tables replace them)
                case 0x05 when o + 6 <= spu.Length:
                    x1 = spu[o] << 4 | spu[o + 1] >> 4;
                    x2 = (spu[o + 1] & 0xF) << 8 | spu[o + 2];
                    y1 = spu[o + 3] << 4 | spu[o + 4] >> 4;
                    y2 = (spu[o + 4] & 0xF) << 8 | spu[o + 5];
                    o += 6;
                    break;
                case 0x06 when o + 4 <= spu.Length:
                    top = spu[o] << 8 | spu[o + 1];
                    bottom = spu[o + 2] << 8 | spu[o + 3];
                    o += 4;
                    break;
                case 0x00 or 0x01 or 0x02: break; // forced start, start, stop
                default: guard = 64; break;       // unknown command: stop reading
            }
        }
        var width = x2 - x1 + 1;
        var height = y2 - y1 + 1;
        if (width <= 0 || height <= 0 || width > 1920 || height > 1152 || top < 4 || bottom < 4) return null;

        var pixels = new byte[width * height];
        DecodeField(spu, top, 0, width, height, pixels);
        DecodeField(spu, bottom, 1, width, height, pixels);
        return new Subpicture { X = x1, Y = y1, Width = width, Height = height, Pixels = pixels };
    }

    private static void DecodeField(byte[] spu, int offset, int firstRow, int width, int height, byte[] pixels)
    {
        var nib = offset * 2;
        int Next()
        {
            if ((nib >> 1) >= spu.Length) return 0;
            var b = spu[nib >> 1];
            var v = (nib & 1) != 0 ? b & 0xF : b >> 4;
            nib++;
            return v;
        }
        for (var y = firstRow; y < height; y += 2)
        {
            var x = 0;
            while (x < width)
            {
                if ((nib >> 1) >= spu.Length) return;
                var code = Next();
                if (code < 0x4)
                {
                    code = code << 4 | Next();
                    if (code < 0x10)
                    {
                        code = code << 4 | Next();
                        if (code < 0x40) code = code << 4 | Next();
                    }
                }
                var color = (byte)(code & 3);
                var run = code >> 2;
                if (run == 0) run = width - x; // to the end of the line
                var end = Math.Min(width, x + run);
                Array.Fill(pixels, color, y * width + x, end - x);
                x = end;
            }
            if ((nib & 1) != 0) nib++; // lines are byte aligned
        }
    }
}

/// <summary>Draws a button's highlight the way a player does: subpicture pixels in the highlight colours.</summary>
public static class HighlightRenderer
{
    /// <summary>Palette index and contrast (0-15) per pixel type, from a 32-bit HLI colour word.</summary>
    public static (int[] Colors, int[] Contrast) SplitColorWord(uint w) =>
    (
        new[] { (int)(w >> 16) & 0xF, (int)(w >> 20) & 0xF, (int)(w >> 24) & 0xF, (int)(w >> 28) & 0xF },
        new[] { (int)w & 0xF, (int)(w >> 4) & 0xF, (int)(w >> 8) & 0xF, (int)(w >> 12) & 0xF }
    );

    /// <summary>A PGC palette entry (0x00YYCrCb) as RGB (BT.601 limited range).</summary>
    public static (byte R, byte G, byte B) YCrCbToRgb(uint entry)
    {
        double y = (entry >> 16) & 0xFF, cr = (entry >> 8) & 0xFF, cb = entry & 0xFF;
        var yy = 1.164 * (y - 16);
        static byte C(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        return (C(yy + 1.596 * (cr - 128)), C(yy - 0.813 * (cr - 128) - 0.391 * (cb - 128)), C(yy + 2.018 * (cb - 128)));
    }

    /// <summary>
    /// The button's rectangle of the subpicture in one state's colours, as RGBA; null when nothing in
    /// the rectangle is visible (the player then draws its own focus).
    /// </summary>
    public static RgbaImage? Render(Subpicture spu, DvdButton button, uint colorWord, IReadOnlyList<uint> palette)
    {
        var w = button.Width;
        var h = button.Height;
        if (w <= 0 || h <= 0 || palette.Count < 16) return null;
        var (colors, contrast) = SplitColorWord(colorWord);
        var rgba = new Rgba[4];
        for (var t = 0; t < 4; t++)
        {
            var (r, g, b) = YCrCbToRgb(palette[colors[t]]);
            // Contrast 0 is transparent; 1-15 step up to opaque (a player mixes (k+1)/16).
            var a = contrast[t] == 0 ? (byte)0 : (byte)Math.Min(255, (contrast[t] + 1) * 16 - 1);
            rgba[t] = new Rgba(r, g, b, a);
        }

        var img = new RgbaImage(w, h);
        var visible = false;
        for (var y = 0; y < h; y++)
        {
            var sy = button.Y0 + y - spu.Y;
            if (sy < 0 || sy >= spu.Height) continue;
            for (var x = 0; x < w; x++)
            {
                var sx = button.X0 + x - spu.X;
                if (sx < 0 || sx >= spu.Width) continue;
                var px = rgba[spu[sx, sy]];
                if (px.A == 0) continue;
                img[x, y] = px;
                visible = true;
            }
        }
        return visible ? img : null;
    }

    public static void SavePng(RgbaImage img, string path) => img.SavePng(path);
}
