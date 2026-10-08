using System.Buffers.Binary;
using System.Text;

namespace OpenVideoMenu.Dvd.Tests;

/// <summary>
/// A tiny DVD-Video disc built in memory, so the package can be tested without real ISOs. It has what the
/// reader needs and nothing more:
/// <list type="bullet">
/// <item>First play: JumpSS to the disc's menu (VMGM PGC 1).</item>
/// <item>VMGM PGC 1, the title menu: one still cell with three buttons: play title 1, play title 2, resume.</item>
/// <item>Title set 1: title 1 (PGC 1, 10 min) and title 2 (PGC 2, 20 min); each calls the menu when it ends (CallSS, resume cell 1).</item>
/// </list>
/// </summary>
public static class SyntheticDisc
{
    public const string JumpTitle1 = "3002000000010000";
    public const string JumpTitle2 = "3002000000020000";
    public const string Resume = "2001000000000010";
    private const string ToMenu = "3006000100C00000";            // JumpSS VMGM PGC 1
    private const string CallMenu = "3008000101C00000";          // CallSS VMGM PGC 1, resume cell 1

    /// <summary>The disc, opened. Dispose it when done.</summary>
    /// <param name="pal">The title set's video is PAL (default NTSC).</param>
    /// <param name="repeatCell">Title 2 lists its one cell twice (5 min each), as discs do for cell commands to pick from.</param>
    /// <param name="second">What title 2 plays (default: video of its own).</param>
    /// <param name="chain">Title 1 runs on into title 2 when it ends, with these lengths (default: it calls the menu).</param>
    public static DvdDisc Open(bool pal = false, bool repeatCell = false, SecondTitle second = SecondTitle.Own, TitleChain chain = TitleChain.None) =>
        DvdDisc.Open("synthetic", new MemoryFileSystem(Files(pal, repeatCell, second, chain)));

    public static Dictionary<string, byte[]> Files(bool pal = false, bool repeatCell = false, SecondTitle second = SecondTitle.Own,
        TitleChain chain = TitleChain.None) => new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\VIDEO_TS\VIDEO_TS.IFO"] = Vmg(),
        [@"\VIDEO_TS\VIDEO_TS.VOB"] = MenuVob(),
        [@"\VIDEO_TS\VTS_01_0.IFO"] = Vts(pal, repeatCell, second, chain),
        [@"\VIDEO_TS\VTS_01_1.VOB"] = new byte[2 * 2048],
    };

    // ------------------------------------------------------------------ IFOs

    private static byte[] Vmg()
    {
        var b = new byte[4 * 2048];
        Ascii(b, 0, "DVDVIDEO-VMG");
        U16(b, 0x3E, 1);                     // title sets
        U32(b, 0x84, 0x400);                 // first play PGC, byte offset
        U32(b, 0xC4, 1);                     // title search table, sector
        U32(b, 0xC8, 2);                     // VMGM menu table, sector
        b[0x100] = 0x0C;                     // menu video: NTSC 16:9 720x480
        Pgc(b, 0x400, pre: new[] { ToMenu }, post: Array.Empty<string>(), cells: Array.Empty<Cell>());

        // Title search table: two titles in title set 1.
        var tt = 2048;
        U16(b, tt, 2);
        U32(b, tt + 4, 8 + 2 * 12 - 1);
        for (var i = 0; i < 2; i++)
        {
            var e = tt + 8 + i * 12;
            b[e] = 0x3C;                     // playback type: one sequential PGC
            b[e + 1] = 1;                    // angles
            U16(b, e + 2, 1);                // chapters
            b[e + 6] = 1;                    // title set
            b[e + 7] = (byte)(i + 1);        // title set title
        }

        // VMGM menu table: one language unit ("en") with the title menu.
        var ut = 2 * 2048;
        U16(b, ut, 1);
        Ascii(b, ut + 8, "en");
        U32(b, ut + 12, 16);                 // language unit, from ut
        var lu = ut + 16;
        U16(b, lu, 1);
        b[lu + 8] = 0x80 | (byte)MenuType.Title;
        U32(b, lu + 12, 16);                 // PGC, from lu
        Pgc(b, lu + 16, pre: Array.Empty<string>(), post: Array.Empty<string>(),
            cells: new[] { new Cell(Seconds: 1, Still: 0xFF, First: 0, Last: 0) });
        return b;
    }

    private static byte[] Vts(bool pal, bool repeatCell, SecondTitle second, TitleChain chain)
    {
        var b = new byte[4 * 2048];
        Ascii(b, 0, "DVDVIDEO-VTS");
        U32(b, 0xC8, 1);                     // chapter table, sector
        U32(b, 0xCC, 2);                     // title PGC table, sector
        b[0x100] = 0x0C;
        b[0x200] = (byte)(pal ? 0x1C : 0x0C); // title video: NTSC (or PAL) 16:9

        // Chapter table: each title is one chapter, program 1 of its PGC.
        var ptt = 2048;
        U16(b, ptt, 2);
        U32(b, ptt + 4, 16 + 2 * 4 - 1);
        U32(b, ptt + 8, 16);
        U32(b, ptt + 12, 20);
        U16(b, ptt + 16, 1); U16(b, ptt + 18, 1);
        U16(b, ptt + 20, 2); U16(b, ptt + 22, 1);

        // Title PGCs: 10 s and 20 s, one cell each, calling the menu when they end.
        var pgcit = 2 * 2048;
        U16(b, pgcit, 2);
        var at = 8 + 2 * 8;
        for (var i = 0; i < 2; i++)
        {
            var e = pgcit + 8 + i * 8;
            b[e] = (byte)(0x80 | (i + 1));   // entry PGC of title set title i+1
            U32(b, e + 4, at);
            // Title 1 as two cells (590 s + 10 s) when title 2 shares them: all of them, or all but the last,
            // which it has its own 10 s copy of (a "white rabbit" mode).
            var shared = new Cell(Seconds: 590, Still: 0, First: 0, Last: 99);
            var cells = second != SecondTitle.Own
                ? new[] { shared, i == 1 && second == SecondTitle.NearlySameCells ? new Cell(10, 0, 110, 119) : new Cell(10, 0, 100, 109) }
                : repeatCell && i == 1
                ? new[] { new Cell(Seconds: 300, Still: 0, First: 1, Last: 1), new Cell(Seconds: 300, Still: 0, First: 1, Last: 1) }
                : chain != TitleChain.None
                ? new[] { new Cell(Seconds: ChainSeconds(chain)[i], Still: 0, First: (uint)i, Last: (uint)i) }
                : new[] { new Cell(Seconds: 600 * (i + 1), Still: 0, First: (uint)i, Last: (uint)i) };
            var post = chain != TitleChain.None && i == 0 ? JumpTitle2 : CallMenu;
            at += Pgc(b, pgcit + at, pre: Array.Empty<string>(), post: new[] { post }, cells: cells);
        }
        return b;
    }

    private sealed record Cell(int Seconds, byte Still, uint First, uint Last);

    /// <summary>Titles 1 and 2's lengths for a chain.</summary>
    private static int[] ChainSeconds(TitleChain chain) => chain switch
    {
        TitleChain.ExtraThenStub => new[] { 30, 1 },
        TitleChain.PrerollThenFilm => new[] { 8, 600 },
        _ => new[] { 1, 2 },
    };

    /// <summary>Writes a PGC at <paramref name="off"/> and returns its size.</summary>
    private static int Pgc(byte[] b, int off, string[] pre, string[] post, Cell[] cells)
    {
        b[off + 2] = (byte)cells.Length;                       // programs (one per cell)
        b[off + 3] = (byte)cells.Length;
        Bcd(b, off + 4, cells.Sum(c => c.Seconds));
        var cmd = 0xEC;
        var commands = pre.Concat(post).ToArray();
        U16(b, off + 0xE4, (ushort)cmd);
        U16(b, off + cmd, (ushort)pre.Length);
        U16(b, off + cmd + 2, (ushort)post.Length);
        U16(b, off + cmd + 4, 0);
        U16(b, off + cmd + 6, (ushort)(8 + commands.Length * 8 - 1));
        for (var i = 0; i < commands.Length; i++) Convert.FromHexString(commands[i]).CopyTo(b, off + cmd + 8 + i * 8);
        var pgm = cmd + 8 + commands.Length * 8;
        var cellTable = pgm + Math.Max(1, cells.Length);
        cellTable += cellTable % 2;
        if (cells.Length > 0)
        {
            U16(b, off + 0xE6, (ushort)pgm);
            U16(b, off + 0xE8, (ushort)cellTable);
            for (var i = 0; i < cells.Length; i++)
            {
                b[off + pgm + i] = (byte)(i + 1);
                var c = off + cellTable + i * 24;
                b[c + 2] = cells[i].Still;
                Bcd(b, c + 4, cells[i].Seconds);
                U32(b, c + 8, cells[i].First);
                U32(b, c + 16, cells[i].First);
                U32(b, c + 20, cells[i].Last);
            }
        }
        return cellTable + cells.Length * 24;
    }

    // ------------------------------------------------------------------ the menu's navigation pack

    /// <summary>One sector: a NAV pack whose PCI carries the menu's three buttons.</summary>
    private static byte[] MenuVob()
    {
        var b = new byte[2048];
        b[2] = 1; b[3] = 0xBA;                                 // pack header, no stuffing
        var p = 14;
        b[p + 2] = 1; b[p + 3] = 0xBF;                         // private stream 2: PCI
        U16(b, p + 4, 0x3D4 + 1);
        b[p + 6] = 0x00;
        var pci = p + 7;
        var hli = pci + 0x60;
        U16(b, hli, 1);                                        // new highlight
        U32(b, hli + 6, 0xFFFFFFFF);                           // to the end of the cell
        U16(b, hli + 14, 0x1000);                              // one button group, normal display
        b[hli + 17] = 3;                                       // buttons
        for (var i = 0; i < 3; i++)                            // colour tables: opaque palette 1 for pattern pixels
        {
            U32(b, hli + 22 + i * 8, 0x0010_00F0);
            U32(b, hli + 22 + i * 8 + 4, 0x0020_00F0);
        }
        var commands = new[] { JumpTitle1, JumpTitle2, Resume };
        for (var n = 1; n <= 3; n++)
        {
            var e = hli + 46 + (n - 1) * 18;
            int x0 = 100, x1 = 300, y0 = 100 + (n - 1) * 80, y1 = y0 + 60;
            b[e] = (byte)(1 << 6 | (x0 >> 4) & 0x3F);
            b[e + 1] = (byte)((x0 & 0x0F) << 4 | (x1 >> 8) & 0x03);
            b[e + 2] = (byte)(x1 & 0xFF);
            b[e + 3] = (byte)((y0 >> 4) & 0x3F);
            b[e + 4] = (byte)((y0 & 0x0F) << 4 | (y1 >> 8) & 0x03);
            b[e + 5] = (byte)(y1 & 0xFF);
            b[e + 6] = (byte)(n == 1 ? 3 : n - 1);             // up
            b[e + 7] = (byte)(n == 3 ? 1 : n + 1);             // down
            b[e + 8] = (byte)n;                                // left: none
            b[e + 9] = (byte)n;                                // right: none
            Convert.FromHexString(commands[n - 1]).CopyTo(b, e + 10);
        }
        return b;
    }

    // ------------------------------------------------------------------ bytes

    private static void Ascii(byte[] b, int off, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, off);
    private static void U16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(off), v);
    private static void U16(byte[] b, int off, int v) => U16(b, off, (ushort)v);
    private static void U32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(off), v);
    private static void U32(byte[] b, int off, int v) => U32(b, off, (uint)v);

    /// <summary>A whole number of seconds as BCD hh mm ss ff (29.97 fps flag).</summary>
    private static void Bcd(byte[] b, int off, int seconds)
    {
        static byte D(int v) => (byte)((v / 10) << 4 | v % 10);
        b[off] = D(seconds / 3600);
        b[off + 1] = D(seconds / 60 % 60);
        b[off + 2] = D(seconds % 60);
        b[off + 3] = 0xC0;
    }
}

/// <summary>Files held in memory, under DVD-rooted paths (<c>\VIDEO_TS\VIDEO_TS.IFO</c>).</summary>
public sealed class MemoryFileSystem(Dictionary<string, byte[]> files) : IDvdFileSystem
{
    private static string Key(string path) => "\\" + path.Replace('/', '\\').TrimStart('\\');

    public bool FileExists(string path) => files.ContainsKey(Key(path));

    public Stream OpenRead(string path) => new MemoryStream(files[Key(path)], writable: false);

    public IEnumerable<string> GetFiles(string directory, string searchPattern)
    {
        var dir = Key(directory).TrimEnd('\\') + "\\";
        var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(searchPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return files.Keys.Where(k => k.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && regex.IsMatch(k[dir.Length..]));
    }

    public string? VolumeLabel => "SYNTHETIC";

    public void Dispose() { }
}

/// <summary>What the synthetic disc's title 2 plays.</summary>
public enum SecondTitle
{
    /// <summary>Video of its own (20 min).</summary>
    Own,

    /// <summary>Exactly title 1's cells: a duplicate.</summary>
    SameCells,

    /// <summary>Title 1's cells but the last, which it has its own copy of: a near duplicate.</summary>
    NearlySameCells,
}

/// <summary>Title 1 runs on into title 2 when it ends (its post command jumps there).</summary>
public enum TitleChain
{
    /// <summary>No chain: title 1 calls the menu.</summary>
    None,

    /// <summary>An extra (30 s), then a 1 s title that only returns to the menu.</summary>
    ExtraThenStub,

    /// <summary>A warning (8 s) before the film (10 min).</summary>
    PrerollThenFilm,

    /// <summary>Two empty titles (1 s and 2 s).</summary>
    AllJunk,
}
