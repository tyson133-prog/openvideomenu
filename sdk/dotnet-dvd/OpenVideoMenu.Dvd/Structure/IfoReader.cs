using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenVideoMenu.Dvd.Structure;

public sealed class DvdFormatException : Exception
{
    public DvdFormatException(string message) : base(message) { }
}

/// <summary>Bounds-checked big-endian reader over an in-memory buffer.</summary>
internal readonly struct BeReader
{
    private readonly byte[] _b;
    public BeReader(byte[] bytes) => _b = bytes;
    public int Length => _b.Length;

    private void Check(int off, int len)
    {
        if (off < 0 || off + len > _b.Length)
            throw new DvdFormatException($"Read of {len} bytes at 0x{off:X} is outside the buffer (size 0x{_b.Length:X}).");
    }

    public byte U8(int off) { Check(off, 1); return _b[off]; }
    public ushort U16(int off) { Check(off, 2); return (ushort)(_b[off] << 8 | _b[off + 1]); }
    public uint U32(int off) { Check(off, 4); return (uint)(_b[off] << 24 | _b[off + 1] << 16 | _b[off + 2] << 8 | _b[off + 3]); }
    public byte[] Bytes(int off, int len) { Check(off, len); var r = new byte[len]; Array.Copy(_b, off, r, 0, len); return r; }
    public bool InRange(int off, int len) => off >= 0 && off + len <= _b.Length;
}

/// <summary>
/// Parses VIDEO_TS.IFO (VMGI) and VTS_nn_0.IFO (VTSI) per the DVD-Video format
/// as documented at dvd.sourceforge.net ("DVD-Video Information") and mpucoder.com.
/// </summary>
public static class IfoReader
{
    public static VmgInfo ReadVmg(IDvdFileSystem fs, DvdWarnings? warnings = null)
    {
        var bytes = ReadIfoBytes(fs, "VIDEO_TS", "DVDVIDEO-VMG");
        var r = new BeReader(bytes);

        var vmg = new VmgInfo
        {
            TitleSetCount = r.U16(0x3E),
            FirstPlay = TryParse(() => { var off = (int)r.U32(0x84); return off > 0 ? ParsePgc(r, off) : null; }, "first-play PGC", warnings),
            MenuVideo = VideoAttributes.Parse(r.U8(0x100), r.U8(0x101)),
        };

        var ttSrpt = (int)r.U32(0xC4) * 2048;
        if (ttSrpt > 0)
        {
            TryParse(() =>
            {
                var n = r.U16(ttSrpt);
                for (var i = 0; i < n; i++)
                {
                    var e = ttSrpt + 8 + i * 12;
                    if (!r.InRange(e, 12)) break;
                    vmg.Titles.Add(new TitleSearchEntry
                    {
                        TitleNumber = i + 1,
                        PlaybackType = r.U8(e),
                        AngleCount = r.U8(e + 1),
                        ChapterCount = r.U16(e + 2),
                        Vtsn = r.U8(e + 6),
                        VtsTtn = r.U8(e + 7),
                    });
                }
                return true;
            }, "title search table", warnings);
        }

        var pgciUt = (int)r.U32(0xC8) * 2048;
        if (pgciUt > 0)
            TryParse(() => { ParsePgciUt(r, pgciUt, vmg.Menus); return true; }, "VMGM menu table", warnings);

        return vmg;
    }

    public static VtsInfo ReadVts(IDvdFileSystem fs, int vtsn, DvdWarnings? warnings = null)
    {
        var bytes = ReadIfoBytes(fs, $"VTS_{vtsn:00}_0", "DVDVIDEO-VTS");
        var r = new BeReader(bytes);

        var vts = new VtsInfo
        {
            Vtsn = vtsn,
            MenuVideo = VideoAttributes.Parse(r.U8(0x100), r.U8(0x101)),
            TitleVideo = VideoAttributes.Parse(r.U8(0x200), r.U8(0x201)),
        };

        TryParse(() =>
        {
            var nAudio = Math.Min((int)r.U16(0x202), 8);
            for (var i = 0; i < nAudio; i++)
            {
                var a = 0x204 + i * 8;
                var coding = (r.U8(a) >> 5) & 7;
                var codec = coding switch { 0 => "AC3", 2 => "MPEG1", 3 => "MPEG2", 4 => "LPCM", 6 => "DTS", _ => "Unknown" };
                vts.Audio.Add(new AudioStreamInfo(i, LangCode(r, a + 2), codec, (r.U8(a + 1) & 7) + 1));
            }
            var nSub = Math.Min((int)r.U16(0x254), 32);
            for (var i = 0; i < nSub; i++)
                vts.Subpictures.Add(new SubpictureStreamInfo(i, LangCode(r, 0x256 + i * 6 + 2)));
            return true;
        }, $"VTS {vtsn} stream attributes", warnings);

        var ptt = (int)r.U32(0xC8) * 2048;
        if (ptt > 0)
        {
            TryParse(() =>
            {
                var n = r.U16(ptt);
                var end = (int)r.U32(ptt + 4);
                var offsets = new int[n];
                for (var i = 0; i < n; i++) offsets[i] = (int)r.U32(ptt + 8 + i * 4);
                for (var i = 0; i < n; i++)
                {
                    var start = ptt + offsets[i];
                    var stop = i + 1 < n ? ptt + offsets[i + 1] : ptt + end + 1;
                    var list = new List<PttEntry>();
                    for (var p = start; p + 4 <= stop && r.InRange(p, 4); p += 4)
                        list.Add(new PttEntry(r.U16(p), r.U16(p + 2)));
                    vts.Ptts.Add(list);
                }
                return true;
            }, $"VTS {vtsn} chapter table", warnings);
        }

        var pgcit = (int)r.U32(0xCC) * 2048;
        if (pgcit > 0)
        {
            TryParse(() =>
            {
                var n = r.U16(pgcit);
                for (var i = 0; i < n; i++)
                {
                    var e = pgcit + 8 + i * 8;
                    var cat = r.U8(e);
                    var pgc = ParsePgc(r, pgcit + (int)r.U32(e + 4));
                    pgc.IsEntry = (cat & 0x80) != 0;
                    pgc.VtsTitleNumber = cat & 0x7F;
                    vts.TitlePgcs.Add(pgc);
                }
                return true;
            }, $"VTS {vtsn} title PGC table", warnings);
        }

        var pgciUt = (int)r.U32(0xD0) * 2048;
        if (pgciUt > 0)
            TryParse(() => { ParsePgciUt(r, pgciUt, vts.Menus); return true; }, $"VTSM {vtsn} menu table", warnings);

        return vts;
    }

    private static string LangCode(BeReader r, int off)
    {
        var a = r.U8(off);
        var b = r.U8(off + 1);
        return a >= 'a' && a <= 'z' && b >= 'a' && b <= 'z'
            ? new string(new[] { (char)a, (char)b })
            : "";
    }

    private static T? TryParse<T>(Func<T?> parse, string what, DvdWarnings? warnings)
    {
        try { return parse(); }
        catch (Exception ex) when (ex is DvdFormatException or ArgumentException or IndexOutOfRangeException)
        {
            warnings?.Add(DvdWarningCode.DiscRead, $"Could not parse {what}: {ex.Message}");
            return default;
        }
    }

    /// <summary>Reads the .IFO, falling back to the .BUP backup copy if the IFO is missing or damaged.</summary>
    private static byte[] ReadIfoBytes(IDvdFileSystem fs, string baseName, string magic)
    {
        Exception? last = null;
        foreach (var ext in new[] { "IFO", "BUP" })
        {
            var path = $@"\VIDEO_TS\{baseName}.{ext}";
            try
            {
                if (!fs.FileExists(path)) continue;
                using var s = fs.OpenRead(path);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length >= 0x400 && Encoding.ASCII.GetString(bytes, 0, 12) == magic)
                    return bytes;
                last = new DvdFormatException($"{path} does not start with {magic}.");
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last as DvdFormatException
              ?? new DvdFormatException($"{baseName}.IFO could not be read: {last?.Message ?? "file not found"}");
    }

    private static void ParsePgciUt(BeReader r, int ut, List<MenuLanguageUnit> into)
    {
        var nLu = r.U16(ut);
        for (var i = 0; i < nLu; i++)
        {
            var e = ut + 8 + i * 8;
            var lang = LangCode(r, e);
            var lu = ut + (int)r.U32(e + 4);
            var unit = new MenuLanguageUnit { Language = lang };
            var nPgc = r.U16(lu);
            for (var j = 0; j < nPgc; j++)
            {
                var pe = lu + 8 + j * 8;
                var cat = r.U8(pe);
                var pgc = ParsePgc(r, lu + (int)r.U32(pe + 4));
                pgc.IsEntry = (cat & 0x80) != 0;
                pgc.MenuType = pgc.IsEntry ? (MenuType)(cat & 0x0F) : MenuType.None;
                unit.Pgcs.Add(pgc);
            }
            into.Add(unit);
        }
    }

    internal static Pgc ParsePgc(BeReader r, int off)
    {
        var programCount = r.U8(off + 2);
        var cellCount = r.U8(off + 3);

        var cmdOff = r.U16(off + 0xE4);
        var pgmOff = r.U16(off + 0xE6);
        var cellOff = r.U16(off + 0xE8);

        var pre = new List<byte[]>();
        var post = new List<byte[]>();
        var cellCmds = new List<byte[]>();
        if (cmdOff != 0)
        {
            var c = off + cmdOff;
            int nPre = r.U16(c), nPost = r.U16(c + 2), nCell = r.U16(c + 4);
            // The format caps the total at 128; clamp to protect against junk tables.
            nPre = Math.Min(nPre, 128);
            nPost = Math.Min(nPost, 128 - nPre);
            nCell = Math.Min(nCell, 128 - nPre - nPost);
            var p = c + 8;
            for (var i = 0; i < nPre && r.InRange(p, 8); i++, p += 8) pre.Add(r.Bytes(p, 8));
            for (var i = 0; i < nPost && r.InRange(p, 8); i++, p += 8) post.Add(r.Bytes(p, 8));
            for (var i = 0; i < nCell && r.InRange(p, 8); i++, p += 8) cellCmds.Add(r.Bytes(p, 8));
        }

        var programMap = new List<int>();
        if (pgmOff != 0)
            for (var i = 0; i < programCount; i++)
                programMap.Add(r.U8(off + pgmOff + i));

        var cells = new List<CellPlayback>();
        if (cellOff != 0)
        {
            for (var i = 0; i < cellCount; i++)
            {
                var c = off + cellOff + i * 24;
                if (!r.InRange(c, 24)) break;
                var flags = r.U8(c);
                cells.Add(new CellPlayback
                {
                    BlockMode = (flags >> 6) & 3,
                    BlockType = (flags >> 4) & 3,
                    StillTime = r.U8(c + 2),
                    CellCommandNumber = r.U8(c + 3),
                    Duration = BcdTime(r, c + 4),
                    FirstSector = r.U32(c + 8),
                    LastVobuStartSector = r.U32(c + 16),
                    LastSector = r.U32(c + 20),
                });
            }
        }

        var palette = new List<uint>(16);
        for (var i = 0; i < 16; i++) palette.Add(r.U32(off + 0xA4 + i * 4));

        return new Pgc
        {
            IfoOffset = off,
            ProgramCount = programCount,
            CellCount = cells.Count,
            Duration = BcdTime(r, off + 4),
            ProhibitedUserOps = r.U32(off + 8),
            NextPgc = r.U16(off + 0x9C),
            PrevPgc = r.U16(off + 0x9E),
            GoUpPgc = r.U16(off + 0xA0),
            StillTime = r.U8(off + 0xA2),
            PlaybackMode = r.U8(off + 0xA3),
            PreCommands = pre,
            PostCommands = post,
            CellCommands = cellCmds,
            ProgramMap = programMap,
            Cells = cells,
            Palette = palette,
        };
    }

    private static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);

    /// <summary>Decodes a 4-byte BCD playback time (hh mm ss ff, frame-rate flag in ff's top 2 bits).</summary>
    internal static TimeSpan BcdTime(BeReader r, int off)
    {
        var h = Bcd(r.U8(off));
        var m = Bcd(r.U8(off + 1));
        var s = Bcd(r.U8(off + 2));
        var ffByte = r.U8(off + 3);
        var fps = ((ffByte >> 6) & 3) == 1 ? 25.0 : 29.97;
        var frames = Bcd((byte)(ffByte & 0x3F));
        if (m > 59 || s > 59) return TimeSpan.Zero;
        return new TimeSpan(h, m, s) + TimeSpan.FromSeconds(frames / fps);
    }
}
