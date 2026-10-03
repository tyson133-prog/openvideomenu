using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace OpenVideoMenu.Dvd.Structure;

/// <summary>One DVD menu button as defined in a NAV pack's PCI highlight information.</summary>
public sealed class DvdButton
{
    /// <summary>1-based button number (the value SPRM8 holds, shifted left by 10).</summary>
    public int Number { get; init; }

    public int X0 { get; init; }
    public int Y0 { get; init; }
    public int X1 { get; init; }
    public int Y1 { get; init; }

    public int Up { get; init; }
    public int Down { get; init; }
    public int Left { get; init; }
    public int Right { get; init; }

    /// <summary>Selecting this button activates it immediately.</summary>
    public bool AutoAction { get; init; }

    public int ColorTable { get; init; }
    public byte[] Command { get; init; } = new byte[8];

    /// <summary>The button's rectangle in the menu's other display modes (4:3 letterbox, 4:3 pan-scan), when the disc has them.</summary>
    public (int X0, int Y0, int X1, int Y1)? Letterbox { get; init; }

    public (int X0, int Y0, int X1, int Y1)? PanScan { get; init; }

    public int Width => Math.Max(0, X1 - X0 + 1);
    public int Height => Math.Max(0, Y1 - Y0 + 1);
    public bool HasArea => X1 > X0 && Y1 > Y0;

    public int Neighbor(NavDirection dir) => dir switch
    {
        NavDirection.Up => Up,
        NavDirection.Down => Down,
        NavDirection.Left => Left,
        NavDirection.Right => Right,
        _ => Number,
    };

    public override string ToString() => $"#{Number} ({X0},{Y0})-({X1},{Y1}) U{Up} D{Down} L{Left} R{Right}{(AutoAction ? " AUTO" : "")}";
}

public enum NavDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>The button set active in a menu cell.</summary>
public sealed class HighlightInfo
{
    public IReadOnlyList<DvdButton> Buttons { get; init; } = Array.Empty<DvdButton>();

    /// <summary>Forced-select button (0 = none).</summary>
    public int ForcedSelect { get; init; }

    /// <summary>Forced-activate button (0 = none): the menu activates it on its own.</summary>
    public int ForcedActivate { get; init; }

    public int GroupCount { get; init; }
    public int SelectedGroup { get; init; }

    /// <summary>Sector (relative to the VOB) of the NAV pack the buttons were read from.</summary>
    public uint NavSector { get; init; }

    /// <summary>
    /// The three highlight colour tables (button ColorTable 1-3 = index 0-2): for selected and
    /// activated buttons, four 4-bit palette indexes (high word) and contrasts (low word), one per
    /// subpicture pixel type (background, pattern, emphasis 1, emphasis 2).
    /// </summary>
    public IReadOnlyList<(uint Select, uint Action)> ColorTables { get; init; } = Array.Empty<(uint, uint)>();

    /// <summary>How long after the cell starts the buttons appear (motion menus often show them late).</summary>
    public TimeSpan ButtonsAt { get; init; }

    /// <summary>When the buttons stop (HLI_E_PTM, from the cell's start); null when they last to the end of the cell.</summary>
    public TimeSpan? ButtonsUntil { get; init; }

    public DvdButton? Get(int number) =>
        number >= 1 && number <= Buttons.Count ? Buttons[number - 1] : null;
}

/// <summary>
/// Reads PCI packets from NAV packs in menu VOBs (VIDEO_TS.VOB, VTS_nn_0.VOB).
/// NAV packs are never CSS-scrambled.
/// </summary>
public static class NavPackReader
{
    private const int SectorSize = 2048;

    /// <summary>
    /// Scans a cell's VOBUs from its first sector until a NAV pack with active highlight
    /// information is found (motion menus often enable buttons after a few VOBUs).
    /// Returns null when the cell has no buttons.
    /// </summary>
    public static HighlightInfo? ReadCellHighlight(Stream vob, CellPlayback cell, bool wideMenu, int maxVobus = 400)
    {
        var sector = cell.FirstSector;
        var buf = new byte[SectorSize];
        uint? cellStartPtm = null;

        for (var i = 0; i < maxVobus; i++)
        {
            if (!ReadSector(vob, sector, buf)) return null;

            if (TryLocatePci(buf, out var pci))
            {
                cellStartPtm ??= ReadU32(buf, pci + 0x0C); // VOBU_S_PTM of the cell's first VOBU
                var hl = ParseHighlight(buf, pci, wideMenu, sector, cellStartPtm.Value);
                if (hl is not null) return hl;
            }

            if (sector >= cell.LastVobuStartSector) return null;

            if (!TryReadVobuEnd(buf, out var vobuEnd) || vobuEnd == 0) return null;
            var next = sector + vobuEnd + 1;
            if (next <= sector || next > cell.LastSector) return null;
            sector = next;
        }
        return null;
    }

    /// <summary>
    /// The highlight status of every VOBU in a cell, in order (0 none, 1 new, 2 same as before, 3 same
    /// buttons with new commands): more than one "new" means the buttons change while the cell plays.
    /// </summary>
    public static List<int> HighlightStatuses(Stream vob, CellPlayback cell, int maxVobus = 2000)
    {
        var list = new List<int>();
        var sector = cell.FirstSector;
        var buf = new byte[SectorSize];
        for (var i = 0; i < maxVobus; i++)
        {
            if (!ReadSector(vob, sector, buf)) break;
            if (TryLocatePci(buf, out var pci)) list.Add((buf[pci + 0x60] << 8 | buf[pci + 0x61]) & 3);
            if (sector >= cell.LastVobuStartSector || !TryReadVobuEnd(buf, out var vobuEnd) || vobuEnd == 0) break;
            var next = sector + vobuEnd + 1;
            if (next <= sector || next > cell.LastSector) break;
            sector = next;
        }
        return list;
    }

    /// <summary>
    /// Every set of buttons a cell shows, in order: each new highlight (or new commands for the same
    /// buttons) with when it starts and stops on the cell's clock. Reads every VOBU's navigation pack.
    /// </summary>
    public static List<HighlightInfo> ReadCellHighlights(Stream vob, CellPlayback cell, bool wide, int maxVobus = 100_000)
    {
        var list = new List<HighlightInfo>();
        var sector = cell.FirstSector;
        var buf = new byte[SectorSize];
        uint? cellStartPtm = null;
        for (var i = 0; i < maxVobus; i++)
        {
            if (!ReadSector(vob, sector, buf)) break;
            if (TryLocatePci(buf, out var pci))
            {
                cellStartPtm ??= ReadU32(buf, pci + 0x0C);
                var status = (buf[pci + 0x60] << 8 | buf[pci + 0x61]) & 3;
                if (status is 1 or 3 && ParseHighlight(buf, pci, wide, sector, cellStartPtm.Value) is { Buttons.Count: > 0 } hl)
                    list.Add(hl);
            }
            if (sector >= cell.LastVobuStartSector || !TryReadVobuEnd(buf, out var vobuEnd) || vobuEnd == 0) break;
            var next = sector + vobuEnd + 1;
            if (next <= sector || next > cell.LastSector) break;
            sector = next;
        }
        return list;
    }

    /// <summary>
    /// The user operations each VOBU of a cell forbids (PCI VOBU_UOP_CTL), as changes: seconds from the cell's
    /// start, and the bits from there on.
    /// </summary>
    public static List<(double At, uint Uops)> ReadCellUops(Stream vob, CellPlayback cell, int maxVobus = 100_000)
    {
        var list = new List<(double, uint)>();
        var sector = cell.FirstSector;
        var buf = new byte[SectorSize];
        uint? cellStartPtm = null;
        for (var i = 0; i < maxVobus; i++)
        {
            if (!ReadSector(vob, sector, buf)) break;
            if (TryLocatePci(buf, out var pci))
            {
                var ptm = ReadU32(buf, pci + 0x0C);
                cellStartPtm ??= ptm;
                var uops = ReadU32(buf, pci + 0x08);
                if (list.Count == 0 || list[^1].Item2 != uops) list.Add((Math.Round((ptm - cellStartPtm.Value) / 90000.0, 3), uops));
            }
            if (sector >= cell.LastVobuStartSector || !TryReadVobuEnd(buf, out var vobuEnd) || vobuEnd == 0) break;
            var next = sector + vobuEnd + 1;
            if (next <= sector || next > cell.LastSector) break;
            sector = next;
        }
        return list;
    }

    private static bool ReadSector(Stream s, uint sector, byte[] buf)
    {
        var pos = (long)sector * SectorSize;
        if (pos + SectorSize > s.Length) return false;
        s.Position = pos;
        var read = 0;
        while (read < SectorSize)
        {
            var n = s.Read(buf, read, SectorSize - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>Finds the start of PCI data (after the private-stream-2 header and substream id 0x00).</summary>
    public static bool TryLocatePci(byte[] b, out int pciStart)
    {
        pciStart = 0;
        if (b[0] != 0 || b[1] != 0 || b[2] != 1 || b[3] != 0xBA) return false;

        // MPEG-2 pack header is 14 bytes plus stuffing.
        var p = 14 + (b[13] & 7);

        // Optional system header.
        if (p + 6 <= b.Length && b[p] == 0 && b[p + 1] == 0 && b[p + 2] == 1 && b[p + 3] == 0xBB)
            p += 6 + (b[p + 4] << 8 | b[p + 5]);

        if (p + 7 > b.Length) return false;
        if (b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1 || b[p + 3] != 0xBF) return false;
        if (b[p + 6] != 0x00) return false; // PCI substream

        pciStart = p + 7;
        return pciStart + 0x3D0 <= b.Length;
    }

    public static bool TryLocatePciForDebug(byte[] b, out int pciStart) => TryLocatePci(b, out pciStart);

    public static uint NextVobuForDebug(byte[] b) => TryReadVobuEnd(b, out var e) ? e : 0;

    /// <summary>Reads VOBU_EA (end address of the VOBU, relative to the NAV pack) from DSI.</summary>
    private static bool TryReadVobuEnd(byte[] b, out uint vobuEnd)
    {
        vobuEnd = 0;
        // The DSI packet follows the PCI packet; locate it by scanning for the second private stream 2.
        for (var p = 0x3C0; p + 20 < b.Length && p < 0x440; p++)
        {
            if (b[p] == 0 && b[p + 1] == 0 && b[p + 2] == 1 && b[p + 3] == 0xBF && b[p + 6] == 0x01)
            {
                var dsi = p + 7;
                vobuEnd = (uint)(b[dsi + 8] << 24 | b[dsi + 9] << 16 | b[dsi + 10] << 8 | b[dsi + 11]);
                return true;
            }
        }
        return false;
    }

    private static uint ReadU32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    public static HighlightInfo? ParseHighlight(byte[] b, int pci, bool wideMenu, uint sector, uint cellStartPtm = 0)
    {
        var hli = pci + 0x60;
        var hliSs = (b[hli] << 8 | b[hli + 1]) & 3;
        if (hliSs == 0) return null;

        var btnMd = b[hli + 14] << 8 | b[hli + 15];
        var btnNs = b[hli + 17];
        var foslBtn = b[hli + 20];
        var foacBtn = b[hli + 21];

        if (btnNs == 0 || btnNs > 36) return null;

        var groups = (btnMd >> 12) & 3;
        if (groups == 0) groups = 1;
        var types = new[] { (btnMd >> 8) & 7, (btnMd >> 4) & 7, btnMd & 7 };

        // Choose the button group matching how the menu is displayed. VLC shows the full
        // anamorphic frame, so 16:9 menus use the "wide" group (bit 0 of the display type).
        var group = 1;
        if (groups > 1)
        {
            for (var g = 1; g <= groups; g++)
            {
                var t = types[g - 1];
                if (wideMenu ? (t & 1) != 0 : t == 0) { group = g; break; }
            }
        }

        var perGroup = 36 / groups;
        if (btnNs > perGroup) btnNs = (byte)perGroup;

        var btnit = hli + 22 + 24;

        // The other groups' rectangles for the same button (display types: 2 = letterbox, 4 = pan-scan).
        (int, int, int, int)? Rect(int g, int n)
        {
            var o = btnit + ((g - 1) * perGroup + (n - 1)) * 18;
            if (o + 18 > b.Length) return null;
            return ((b[o] & 0x3F) << 4 | b[o + 1] >> 4, (b[o + 3] & 0x3F) << 4 | b[o + 4] >> 4,
                    (b[o + 1] & 0x03) << 8 | b[o + 2], (b[o + 4] & 0x03) << 8 | b[o + 5]);
        }
        int GroupOf(int type) => Enumerable.Range(1, groups).FirstOrDefault(g => g != group && (types[g - 1] & type) != 0);
        var letterboxGroup = groups > 1 ? GroupOf(2) : 0;
        var panScanGroup = groups > 1 ? GroupOf(4) : 0;

        var buttons = new List<DvdButton>(btnNs);
        for (var n = 1; n <= btnNs; n++)
        {
            var e = btnit + ((group - 1) * perGroup + (n - 1)) * 18;
            if (e + 18 > b.Length) break;

            var cmd = new byte[8];
            Array.Copy(b, e + 10, cmd, 0, 8);
            buttons.Add(new DvdButton
            {
                Number = n,
                ColorTable = (b[e] >> 6) & 3,
                X0 = (b[e] & 0x3F) << 4 | b[e + 1] >> 4,
                X1 = (b[e + 1] & 0x03) << 8 | b[e + 2],
                AutoAction = ((b[e + 3] >> 6) & 3) == 1,
                Y0 = (b[e + 3] & 0x3F) << 4 | b[e + 4] >> 4,
                Y1 = (b[e + 4] & 0x03) << 8 | b[e + 5],
                Up = b[e + 6] & 0x3F,
                Down = b[e + 7] & 0x3F,
                Left = b[e + 8] & 0x3F,
                Right = b[e + 9] & 0x3F,
                Command = cmd,
                Letterbox = letterboxGroup > 0 ? Rect(letterboxGroup, n) : null,
                PanScan = panScanGroup > 0 ? Rect(panScanGroup, n) : null,
            });
        }

        // HLI_S_PTM (90 kHz, same clock as the VOBU times): when the buttons become active.
        var hliStart = ReadU32(b, hli + 2);
        var buttonsAt = cellStartPtm > 0 && hliStart > cellStartPtm && hliStart - cellStartPtm < 90000u * 600
            ? TimeSpan.FromSeconds((hliStart - cellStartPtm) / 90000.0)
            : TimeSpan.Zero;
        var hliEnd = ReadU32(b, hli + 6);
        TimeSpan? buttonsUntil = cellStartPtm > 0 && hliEnd > cellStartPtm && hliEnd != 0xFFFFFFFF && hliEnd - cellStartPtm < 90000u * 7200
            ? TimeSpan.FromSeconds((hliEnd - cellStartPtm) / 90000.0)
            : null;
        var colorTables = new (uint, uint)[3];
        for (var i = 0; i < 3; i++) colorTables[i] = (ReadU32(b, hli + 22 + i * 8), ReadU32(b, hli + 22 + i * 8 + 4));

        return new HighlightInfo
        {
            Buttons = buttons,
            ColorTables = colorTables,
            ButtonsAt = buttonsAt,
            ButtonsUntil = buttonsUntil,
            ForcedSelect = foslBtn is > 0 and <= 36 ? foslBtn : 0,
            ForcedActivate = foacBtn is > 0 and <= 36 ? foacBtn : 0,
            GroupCount = groups,
            SelectedGroup = group,
            NavSector = sector,
        };
    }
}
