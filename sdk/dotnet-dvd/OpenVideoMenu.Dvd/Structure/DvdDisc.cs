using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenVideoMenu.Dvd.Structure;

/// <summary>
/// A parsed DVD-Video disc: VMG, every title set, and on-demand access to menu button data.
/// Thread-compatible (not thread-safe): use one instance per worker.
/// </summary>
public sealed class DvdDisc : IDisposable
{
    private readonly IDvdFileSystem _fs;
    private readonly bool _ownsFs;
    private readonly Dictionary<string, Stream?> _vobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(DvdDomain, int, int, int, int), HighlightInfo?> _highlightCache = new();

    public string SourcePath { get; }
    public string? VolumeLabel => _fs.VolumeLabel;
    public VmgInfo Vmg { get; }
    public IReadOnlyDictionary<int, VtsInfo> TitleSets { get; }
    public DvdWarnings Warnings { get; } = new();

    private DvdDisc(string sourcePath, IDvdFileSystem fs, bool ownsFs)
    {
        SourcePath = sourcePath;
        _fs = fs;
        _ownsFs = ownsFs;

        Vmg = IfoReader.ReadVmg(fs, Warnings);

        var sets = new Dictionary<int, VtsInfo>();
        var count = Math.Clamp(Vmg.TitleSetCount, 0, 99);
        foreach (var t in Vmg.Titles) count = Math.Max(count, t.Vtsn);
        for (var v = 1; v <= Math.Min(count, 99); v++)
        {
            try { sets[v] = IfoReader.ReadVts(fs, v, Warnings); }
            catch (Exception ex) when (ex is DvdFormatException or IOException or FileNotFoundException)
            {
                Warnings.Add(DvdWarningCode.DiscRead, $"Title set {v}: {ex.Message}");
            }
        }
        TitleSets = sets;
    }

    /// <summary>Opens a disc from an ISO image or a VIDEO_TS folder (or the folder above it).</summary>
    public static DvdDisc Open(string path) => new(path, DvdFileSystemFactory.Open(path), ownsFs: true);

    /// <summary>
    /// Opens a disc through any file system: a physical drive, a network stream, an archive, an in-memory image.
    /// <paramref name="path"/> only names the disc; the caller keeps <paramref name="fs"/> and disposes it.
    /// </summary>

    public static DvdDisc Open(string path, IDvdFileSystem fs) => new(path, fs, ownsFs: false);

    // ------------------------------------------------------------------
    // PGC lookup
    // ------------------------------------------------------------------

    /// <summary>
    /// A menu language unit: <paramref name="unit"/> (0-based) when given, else the one navigation uses by
    /// default (English if present, else the first).
    /// </summary>
    public MenuLanguageUnit? MenuUnit(DvdDomain domain, int vtsn, int unit = -1)
    {
        var units = MenuUnits(domain, vtsn);
        if (units.Count == 0) return null;
        if (unit >= 0) return unit < units.Count ? units[unit] : null;
        return units.FirstOrDefault(u => u.Language == "en") ?? units[0];
    }

    /// <summary>A menu domain's language units (several when the disc has menus in several languages).</summary>
    public IReadOnlyList<MenuLanguageUnit> MenuUnits(DvdDomain domain, int vtsn) => domain switch
    {
        DvdDomain.VmgMenu => Vmg.Menus,
        DvdDomain.VtsMenu => TitleSets.TryGetValue(vtsn, out var v) ? v.Menus : Array.Empty<MenuLanguageUnit>(),
        _ => Array.Empty<MenuLanguageUnit>(),
    };

    /// <summary>The index of the language unit navigation uses by default.</summary>
    public int DefaultMenuUnit(DvdDomain domain, int vtsn)
    {
        var units = MenuUnits(domain, vtsn);
        var en = units.ToList().FindIndex(u => u.Language == "en");
        return en >= 0 ? en : 0;
    }

    public Pgc? GetPgc(DvdDomain domain, int vtsn, int pgcn, int unit = -1)
    {
        switch (domain)
        {
            case DvdDomain.FirstPlay:
                return Vmg.FirstPlay;
            case DvdDomain.VmgMenu:
            case DvdDomain.VtsMenu:
                var lu = MenuUnit(domain, vtsn, unit);
                return lu is not null && pgcn >= 1 && pgcn <= lu.Pgcs.Count ? lu.Pgcs[pgcn - 1] : null;
            case DvdDomain.Title:
                return TitleSets.TryGetValue(vtsn, out var vts) && pgcn >= 1 && pgcn <= vts.TitlePgcs.Count
                    ? vts.TitlePgcs[pgcn - 1]
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Finds the entry PGC for a menu type in a menu domain. Returns 0 if absent.</summary>
    public int FindMenuPgc(DvdDomain domain, int vtsn, MenuType type, int languageUnit = -1)
    {
        var unit = MenuUnit(domain, vtsn, languageUnit);
        if (unit is null) return 0;
        for (var i = 0; i < unit.Pgcs.Count; i++)
            if (unit.Pgcs[i].IsEntry && unit.Pgcs[i].MenuType == type)
                return i + 1;
        return 0;
    }

    public TitleSearchEntry? GetTitle(int ttn) =>
        ttn >= 1 && ttn <= Vmg.Titles.Count ? Vmg.Titles[ttn - 1] : null;

    /// <summary>Global title number for a (VTS, VTS_TTN) pair, or 0 if not listed.</summary>
    public int FindTtn(int vtsn, int vtsTtn) =>
        Vmg.Titles.FirstOrDefault(t => t.Vtsn == vtsn && t.VtsTtn == vtsTtn)?.TitleNumber ?? 0;

    public PttEntry? GetPtt(int vtsn, int vtsTtn, int ptt)
    {
        if (!TitleSets.TryGetValue(vtsn, out var vts)) return null;
        if (vtsTtn < 1 || vtsTtn > vts.Ptts.Count) return null;
        var list = vts.Ptts[vtsTtn - 1];
        return ptt >= 1 && ptt <= list.Count ? list[ptt - 1] : null;
    }

    /// <summary>Chapter (PTT) number for a playback position, or 0 when the position is not a chapter start.</summary>
    public int FindPtt(int vtsn, int vtsTtn, int pgcn, int pgn)
    {
        if (!TitleSets.TryGetValue(vtsn, out var vts) || vtsTtn < 1 || vtsTtn > vts.Ptts.Count) return 0;
        var list = vts.Ptts[vtsTtn - 1];
        var best = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Pgcn != pgcn) continue;
            if (list[i].Pgn == pgn) return i + 1;
            if (list[i].Pgn < pgn) best = i + 1;
        }
        return best;
    }

    /// <summary>Finds which VTS title a title-domain PGC belongs to (via its PTT table).</summary>
    public int FindVtsTtnForPgc(int vtsn, int pgcn)
    {
        if (!TitleSets.TryGetValue(vtsn, out var vts)) return 0;
        for (var t = 0; t < vts.Ptts.Count; t++)
            if (vts.Ptts[t].Any(p => p.Pgcn == pgcn))
                return t + 1;
        return 0;
    }

    public VideoAttributes MenuVideo(DvdDomain domain, int vtsn) =>
        domain == DvdDomain.VtsMenu && TitleSets.TryGetValue(vtsn, out var v) ? v.MenuVideo : Vmg.MenuVideo;

    // ------------------------------------------------------------------
    // Button data
    // ------------------------------------------------------------------

    /// <summary>Buttons active in a menu cell, or null if the cell has none.</summary>
    public HighlightInfo? GetHighlight(DvdDomain domain, int vtsn, int pgcn, int cellNumber, int unit = -1)
    {
        // The live tracker and the background menu scout both simulate on this disc; the VOB
        // streams and the cache aren't thread-safe.
        lock (_highlightCache) return GetHighlightLocked(domain, vtsn, pgcn, cellNumber, unit);
    }

    private HighlightInfo? GetHighlightLocked(DvdDomain domain, int vtsn, int pgcn, int cellNumber, int unit)
    {
        if (unit < 0) unit = DefaultMenuUnit(domain, vtsn);
        var key = (domain, domain == DvdDomain.VtsMenu ? vtsn : 0, pgcn, cellNumber, unit);
        if (_highlightCache.TryGetValue(key, out var cached)) return cached;

        HighlightInfo? result = null;
        var pgc = GetPgc(domain, vtsn, pgcn, unit);
        if (pgc is not null && cellNumber >= 1 && cellNumber <= pgc.Cells.Count)
        {
            var vobName = MenuVobName(domain, vtsn);
            var vob = vobName is null ? null : GetVob(vobName);
            if (vob is not null)
            {
                try
                {
                    result = NavPackReader.ReadCellHighlight(vob, pgc.Cells[cellNumber - 1],
                        MenuVideo(domain, vtsn).IsWide);
                }
                catch (IOException ex)
                {
                    Warnings.Add(DvdWarningCode.DiscRead, $"Reading {vobName}: {ex.Message}");
                }
            }
        }

        _highlightCache[key] = result;
        return result;
    }

    private static string? MenuVobName(DvdDomain domain, int vtsn) => domain switch
    {
        DvdDomain.VmgMenu or DvdDomain.FirstPlay => "VIDEO_TS.VOB",
        DvdDomain.VtsMenu => $"VTS_{vtsn:00}_0.VOB",
        _ => null,
    };

    /// <summary>Largest menu cell <see cref="ReadMenuCell"/> returns (a motion menu of a few minutes).</summary>
    public const long MaxMenuCellBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The raw sectors of a menu cell (an MPEG program stream: video, audio, subpictures and NAV
    /// packs), or null when the cell or its VOB is missing or too large. Menu VOBs are never
    /// CSS-scrambled.
    /// </summary>
    public byte[]? ReadMenuCell(DvdDomain domain, int vtsn, int pgcn, int cellNumber, int unit = -1)
    {
        lock (_highlightCache)
        {
            var pgc = GetPgc(domain, vtsn, pgcn, unit);
            if (pgc is null || cellNumber < 1 || cellNumber > pgc.Cells.Count) return null;
            var cell = pgc.Cells[cellNumber - 1];
            if (MenuVobName(domain, vtsn) is not { } name || GetVob(name) is not { } vob) return null;
            var start = (long)cell.FirstSector * 2048;
            var length = ((long)cell.LastSector - cell.FirstSector + 1) * 2048;
            if (length <= 0 || length > MaxMenuCellBytes || start + length > vob.Length) return null;
            try
            {
                var buf = new byte[length];
                vob.Position = start;
                vob.ReadExactly(buf);
                return buf;
            }
            catch (IOException ex)
            {
                Warnings.Add(DvdWarningCode.DiscRead, $"Reading {name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// The raw sectors <paramref name="first"/> to <paramref name="last"/> of a title set's video (its VOB files
    /// read as one), or null when they're missing or too many. Decrypted discs only: scrambled sectors come back
    /// as they are on the disc.
    /// </summary>
    public byte[]? ReadTitleSectors(int vtsn, uint first, uint last)
    {
        lock (_highlightCache)
        {
            if (last < first || TitleVob(vtsn) is not { } vob) return null;
            var start = (long)first * 2048;
            var length = ((long)last - first + 1) * 2048;
            if (length > MaxMenuCellBytes || start + length > vob.Length) return null;
            try
            {
                var buf = new byte[length];
                vob.Position = start;
                vob.ReadExactly(buf);
                return buf;
            }
            catch (IOException ex)
            {
                Warnings.Add(DvdWarningCode.DiscRead, $"Reading title set {vtsn}'s video: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Buttons a title cell's navigation packs carry (shown over the film), from its first VOBUs; null when
    /// none within <paramref name="maxVobus"/>.
    /// </summary>
    public HighlightInfo? GetTitleCellHighlight(int vtsn, int pgcn, int cellNumber, int maxVobus = 400)
    {
        lock (_highlightCache)
        {
            var pgc = GetPgc(DvdDomain.Title, vtsn, pgcn);
            if (pgc is null || cellNumber < 1 || cellNumber > pgc.Cells.Count || TitleVob(vtsn) is not { } vob) return null;
            try
            {
                return NavPackReader.ReadCellHighlight(vob, pgc.Cells[cellNumber - 1],
                    TitleSets.TryGetValue(vtsn, out var vts) && vts.TitleVideo.IsWide, maxVobus);
            }
            catch (IOException ex)
            {
                Warnings.Add(DvdWarningCode.DiscRead, $"Reading title set {vtsn}'s video: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>The highlight status of each VOBU of a menu cell (see <see cref="NavPackReader.HighlightStatuses"/>).</summary>
    private readonly Dictionary<(DvdDomain, int, int, int, int), List<HighlightInfo>> _menuHighlightSets = new();

    /// <summary>
    /// Every set of buttons a menu cell shows, in order (each new highlight its navigation packs start, with
    /// when it starts and ends on the cell's clock). One entry for most menus; more when the buttons change
    /// while the menu's video plays.
    /// </summary>
    public List<HighlightInfo> MenuCellHighlights(DvdDomain domain, int vtsn, int pgcn, int cellNumber, int unit = -1)
    {
        lock (_highlightCache)
        {
            if (unit < 0) unit = DefaultMenuUnit(domain, vtsn);
            var key = (domain, domain == DvdDomain.VtsMenu ? vtsn : 0, pgcn, cellNumber, unit);
            if (_menuHighlightSets.TryGetValue(key, out var cached)) return cached;
            var list = new List<HighlightInfo>();
            var pgc = GetPgc(domain, vtsn, pgcn, unit);
            if (pgc is not null && cellNumber >= 1 && cellNumber <= pgc.Cells.Count && MenuVobName(domain, vtsn) is { } name && GetVob(name) is { } vob)
                try { list = NavPackReader.ReadCellHighlights(vob, pgc.Cells[cellNumber - 1], MenuVideo(domain, vtsn).IsWide); }
                catch (IOException ex) { Warnings.Add(DvdWarningCode.DiscRead, $"Reading menu buttons: {ex.Message}"); }
            _menuHighlightSets[key] = list;
            return list;
        }
    }

    public List<int> MenuCellHighlightStatuses(DvdDomain domain, int vtsn, int pgcn, int cellNumber, int unit = -1)
    {
        lock (_highlightCache)
        {
            var pgc = GetPgc(domain, vtsn, pgcn, unit);
            if (pgc is null || cellNumber < 1 || cellNumber > pgc.Cells.Count || MenuVobName(domain, vtsn) is not { } name || GetVob(name) is not { } vob)
                return new List<int>();
            try { return NavPackReader.HighlightStatuses(vob, pgc.Cells[cellNumber - 1]); }
            catch (IOException) { return new List<int>(); }
        }
    }

    /// <summary>
    /// Copies a title set's sectors <paramref name="first"/>..<paramref name="last"/> (as cells address them) to
    /// <paramref name="output"/>. False when the title VOBs can't be read.
    /// </summary>
    public bool CopyTitleSectors(int vtsn, uint first, uint last, Stream output)
    {
        lock (_highlightCache)
        {
            if (TitleVob(vtsn) is not { } vob || last < first) return false;
            var buffer = new byte[2048 * 256];
            try
            {
                vob.Position = (long)first * 2048;
                var left = ((long)last - first + 1) * 2048;
                while (left > 0)
                {
                    var n = vob.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) return false;
                    output.Write(buffer, 0, n);
                    left -= n;
                }
                return true;
            }
            catch (IOException ex)
            {
                Warnings.Add(DvdWarningCode.DiscRead, $"Reading title set {vtsn}'s video: {ex.Message}");
                return false;
            }
        }
    }

    private readonly Dictionary<(int, uint), List<HighlightInfo>> _titleHighlights = new();

    /// <summary>
    /// Every set of buttons a title cell carries in its navigation packs (shown over the film), in order,
    /// each with when it starts and stops on the cell's clock. Empty for cells without buttons.
    /// </summary>
    public List<HighlightInfo> TitleCellHighlights(int vtsn, int pgcn, int cellNumber, int? maxVobus = null)
    {
        lock (_highlightCache)
        {
            var pgc = GetPgc(DvdDomain.Title, vtsn, pgcn);
            if (pgc is null || cellNumber < 1 || cellNumber > pgc.Cells.Count) return new();
            var cell = pgc.Cells[cellNumber - 1];
            if (_titleHighlights.TryGetValue((vtsn, cell.FirstSector), out var cached)) return cached;
            var list = new List<HighlightInfo>();
            if (TitleVob(vtsn) is { } vob)
            {
                // A cell that holds until the viewer acts is a menu: all of it, whatever the sample asked for.
                if (cell.StillTime == 0xFF) maxVobus = null;
                try { list = NavPackReader.ReadCellHighlights(vob, cell, TitleSets.TryGetValue(vtsn, out var vts) && vts.TitleVideo.IsWide, maxVobus ?? 100_000); }
                catch (IOException ex) { Warnings.Add(DvdWarningCode.DiscRead, $"Reading title set {vtsn}'s video: {ex.Message}"); }
            }
            _titleHighlights[(vtsn, cell.FirstSector)] = list;
            return list;
        }
    }

    private readonly Dictionary<(DvdDomain, int, int, int, int), uint> _cellUops = new();

    /// <summary>
    /// What a cell's navigation packs forbid (VOBU_UOP_CTL), read from its first VOBU, combined with its PGC's:
    /// the user operations the disc forbids while the cell plays.
    /// </summary>
    public uint CellUops(DvdDomain domain, int vtsn, int pgcn, int cellNumber, int unit = -1)
    {
        lock (_highlightCache)
        {
            if (domain != DvdDomain.Title && unit < 0) unit = DefaultMenuUnit(domain, vtsn);
            var key = (domain, vtsn, pgcn, cellNumber, unit);
            if (_cellUops.TryGetValue(key, out var cached)) return cached;
            var pgc = GetPgc(domain, vtsn, pgcn, unit);
            var uops = pgc?.ProhibitedUserOps ?? 0;
            if (pgc is not null && cellNumber >= 1 && cellNumber <= pgc.Cells.Count)
            {
                var vob = domain == DvdDomain.Title ? TitleVob(vtsn) : MenuVobName(domain, vtsn) is { } name ? GetVob(name) : null;
                if (vob is not null)
                    try
                    {
                        if (NavPackReader.ReadCellUops(vob, pgc.Cells[cellNumber - 1], maxVobus: 1) is [var first, ..]) uops |= first.Uops;
                    }
                    catch (IOException ex) { Warnings.Add(DvdWarningCode.DiscRead, $"Reading navigation packs: {ex.Message}"); }
            }
            _cellUops[key] = uops;
            return uops;
        }
    }

    /// <summary>What a title cell's VOBUs forbid, as changes over the cell (seconds from its start, UOP bits).</summary>
    public List<(double At, uint Uops)> TitleCellUops(int vtsn, int pgcn, int cellNumber, int? maxVobus = null)
    {
        lock (_highlightCache)
        {
            var pgc = GetPgc(DvdDomain.Title, vtsn, pgcn);
            if (pgc is null || cellNumber < 1 || cellNumber > pgc.Cells.Count || TitleVob(vtsn) is not { } vob) return new();
            try { return NavPackReader.ReadCellUops(vob, pgc.Cells[cellNumber - 1], maxVobus ?? 100_000); }
            catch (IOException ex) { Warnings.Add(DvdWarningCode.DiscRead, $"Reading title set {vtsn}'s video: {ex.Message}"); return new(); }
        }
    }

    private readonly Dictionary<int, Stream?> _titleVobs = new();

    /// <summary>A title set's title VOBs (VTS_nn_1.VOB, _2, ...) as one stream, as sector addresses count them.</summary>
    private Stream? TitleVob(int vtsn)
    {
        if (_titleVobs.TryGetValue(vtsn, out var s)) return s;
        var parts = new List<Stream>();
        for (var i = 1; i <= 9; i++)
        {
            var name = $"VTS_{vtsn:00}_{i}.VOB";
            if (GetVob(name) is not { } part) break;
            parts.Add(part);
        }
        s = parts.Count == 0 ? null : new ConcatStream(parts);
        _titleVobs[vtsn] = s;
        return s;
    }

    private Stream? GetVob(string name)
    {
        if (_vobs.TryGetValue(name, out var s)) return s;
        try
        {
            var path = $@"\VIDEO_TS\{name}";
            s = _fs.FileExists(path) ? _fs.OpenRead(path) : null;
        }
        catch (IOException)
        {
            s = null;
        }
        _vobs[name] = s;
        return s;
    }

    public void Dispose()
    {
        foreach (var s in _vobs.Values) s?.Dispose();
        _vobs.Clear();
        if (_ownsFs) _fs.Dispose();
    }
}

/// <summary>Read-only streams one after another, as one (the parts stay open; their owner disposes them).</summary>
internal sealed class ConcatStream : Stream
{
    private readonly IReadOnlyList<Stream> _parts;
    private readonly long[] _starts;
    private long _position;

    public ConcatStream(IReadOnlyList<Stream> parts)
    {
        _parts = parts;
        _starts = new long[parts.Count + 1];
        for (var i = 0; i < parts.Count; i++) _starts[i + 1] = _starts[i] + parts[i].Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _starts[^1];

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (count > 0 && _position < Length)
        {
            var i = Array.FindLastIndex(_starts, _parts.Count - 1, s => s <= _position);
            var part = _parts[i];
            part.Position = _position - _starts[i];
            var n = part.Read(buffer, offset, (int)Math.Min(count, _starts[i + 1] - _position));
            if (n <= 0) break;
            total += n;
            offset += n;
            count -= n;
            _position += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => Length + offset };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
