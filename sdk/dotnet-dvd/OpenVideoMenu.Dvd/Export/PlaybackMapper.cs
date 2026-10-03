using System;
using System.Collections.Generic;
using System.Linq;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>A title cell that playback passes through, in play order.</summary>
public readonly record struct PlayedCell(int Vtsn, int Pgcn, int Cell, uint FirstSector, uint LastSector, double Seconds);

/// <summary>
/// How a run of disc cells plays from the rip: one media (a file, or a composite of file ranges), from
/// <see cref="Start"/> to <see cref="End"/> (null = to its end), with the time each cell of the run ends on that
/// media's timeline (null for cells no file holds).
/// </summary>
public sealed record PlaybackPlan(string Media, double Start, double? End, IReadOnlyList<double?> CellEnds, double Seconds);

/// <summary>Finds the ripped files that hold a run of disc cells.</summary>
public interface IPlaybackMapper
{
    PlaybackPlan? Map(IReadOnlyList<PlayedCell> cells);

    /// <summary>Where a disc cell starts on a media's timeline (a file, or a composite's), null when it holds none.</summary>
    double? StartOf(string media, int vtsn, uint firstSector, uint lastSector);

    /// <summary>
    /// Another media id for the same video (segments of the same files), so playback of it can say which disc
    /// title is playing when two titles play the same file.
    /// </summary>
    string Alias(string media, string suffix);
}

/// <summary>One ripped file as the disc cells it holds, in order, with where each starts in the file.</summary>
public sealed class MediaCells
{
    public string Id { get; init; } = "";
    public int Order { get; init; }
    public int Vtsn { get; init; }
    public List<(uint First, uint Last)> Cells { get; } = new();
    public List<double> Starts { get; } = new();
    public double Duration { get; set; }
    public IReadOnlyList<double> ChapterTimes { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Maps cell runs onto ripped files by the cells' sectors, so the same video is found wherever the disc
/// plays it from (a play-all title and its episodes, duplicate titles, chapter ranges). A run covered by one
/// file plays that file; a run spread over several becomes a composite media of their ranges (spec §4.2).
/// </summary>
public sealed class CellMediaIndex : IPlaybackMapper
{
    private readonly List<MediaCells> _files;
    private readonly Dictionary<(int Vtsn, uint First, uint Last), List<(MediaCells File, int Index)>> _where = new();
    private readonly Dictionary<string, string> _composites = new();

    /// <summary>Composite media made for runs no single file holds; added to the manifest's media.</summary>
    public Dictionary<string, MediaEntry> Composites { get; } = new();

    public CellMediaIndex(IEnumerable<MediaCells> files)
    {
        _files = files.ToList();
        foreach (var f in _files)
            for (var i = 0; i < f.Cells.Count; i++)
            {
                var key = (f.Vtsn, f.Cells[i].First, f.Cells[i].Last);
                if (!_where.TryGetValue(key, out var list)) _where[key] = list = new();
                list.Add((f, i));
            }
    }

    /// <summary>The cells a title plays from <paramref name="firstChapter"/> through <paramref name="lastChapter"/>, in order (first angle only).</summary>
    public static MediaCells CellsOf(DvdDisc disc, string id, int order, int ttn, int firstChapter, int? lastChapter)
    {
        var t = disc.GetTitle(ttn);
        var file = new MediaCells { Id = id, Order = order, Vtsn = t?.Vtsn ?? 0 };
        if (t is null || !disc.TitleSets.TryGetValue(t.Vtsn, out var vts) || t.VtsTtn < 1 || t.VtsTtn > vts.Ptts.Count) return file;
        var ptts = vts.Ptts[t.VtsTtn - 1];
        if (ptts.Count == 0) return file;
        var from = Math.Clamp(firstChapter, 1, ptts.Count);
        var to = Math.Clamp(lastChapter ?? ptts.Count, from, ptts.Count);
        // Chapter boundaries as (pgcn, first cell); the range ends where the chapter after it starts.
        var start = ptts[from - 1];
        var stop = to < ptts.Count ? ptts[to] : (PttEntry?)null;

        var pgcOrder = new List<int>();
        foreach (var p in ptts) if (!pgcOrder.Contains(p.Pgcn)) pgcOrder.Add(p.Pgcn);

        var chapterTimes = new List<double>();
        var time = 0.0;
        var inRange = false;
        foreach (var pgcn in pgcOrder.SkipWhile(p => p != start.Pgcn))
        {
            var pgc = disc.GetPgc(DvdDomain.Title, t.Vtsn, pgcn);
            if (pgc is null) continue;
            for (var c = 1; c <= pgc.Cells.Count; c++)
            {
                var pg = pgc.ProgramOfCell(c);
                if (!inRange && pgcn == start.Pgcn && c == ProgramEntry(pgc, start.Pgn)) inRange = true;
                if (stop is { } s && pgcn == s.Pgcn && c == ProgramEntry(pgc, s.Pgn)) return Done();
                if (!inRange) continue;
                var cell = pgc.Cells[c - 1];
                if (cell.IsAngleBlock && cell.BlockMode != 1) continue;
                // A chapter starts here (any PTT pointing at this program's entry cell).
                if (ProgramEntry(pgc, pg) == c && ptts.Any(p => p.Pgcn == pgcn && p.Pgn == pg) && (chapterTimes.Count == 0 || time > chapterTimes[^1]))
                    chapterTimes.Add(Math.Round(time, 3));
                file.Cells.Add((cell.FirstSector, cell.LastSector));
                file.Starts.Add(Math.Round(time, 3));
                time += cell.Duration.TotalSeconds;
            }
        }
        return Done();

        MediaCells Done()
        {
            file.Duration = Math.Round(time, 3);
            return new MediaCells { Id = file.Id, Order = file.Order, Vtsn = file.Vtsn, Duration = file.Duration, ChapterTimes = chapterTimes }
                .With(file.Cells, file.Starts);
        }
    }

    /// <summary>
    /// The cells of a title-domain PGC no title lists (an "orphan" only links reach: a clip between screens,
    /// an easter egg), in order (first angle only).
    /// </summary>
    public static MediaCells CellsOfPgc(DvdDisc disc, string id, int order, int vtsn, int pgcn)
    {
        var file = new MediaCells { Id = id, Order = order, Vtsn = vtsn };
        var pgc = disc.GetPgc(DvdDomain.Title, vtsn, pgcn);
        if (pgc is null) return file;
        var time = 0.0;
        foreach (var cell in pgc.Cells)
        {
            if (cell.IsAngleBlock && cell.BlockMode != 1) continue;
            file.Cells.Add((cell.FirstSector, cell.LastSector));
            file.Starts.Add(Math.Round(time, 3));
            time += cell.Duration.TotalSeconds;
        }
        file.Duration = Math.Round(time, 3);
        return file;
    }

    private static int ProgramEntry(Pgc pgc, int pgn) => pgn >= 1 && pgn <= pgc.ProgramMap.Count ? pgc.ProgramMap[pgn - 1] : 1;

    public string Alias(string media, string suffix)
    {
        var id = $"{media}-{suffix}";
        if (Composites.ContainsKey(id)) return id;
        if (Composites.TryGetValue(media, out var composite))
        {
            Composites[id] = new MediaEntry
            {
                Segments = composite.Segments!.Select(s => new Segment { Media = s.Media, Start = s.Start, End = s.End }).ToList(),
                Duration = composite.Duration, Kind = composite.Kind,
                Chapters = composite.Chapters?.Select(c => new Chapter { Time = c.Time }).ToList(),
            };
            return id;
        }
        var file = _files.First(f => f.Id == media);
        Composites[id] = new MediaEntry
        {
            Segments = new List<Segment> { new() { Media = media } },
            Duration = file.Duration, Kind = "other",
            Chapters = file.ChapterTimes.Count >= 2 ? file.ChapterTimes.Select(t => new Chapter { Time = t }).ToList() : null,
        };
        return id;
    }

    /// <summary>The disc cell (title set, sectors) playing at a time on a media's timeline, through a composite's segments.</summary>
    public (int Vtsn, uint First, uint Last)? CellAt(string media, double time)
    {
        if (Composites.TryGetValue(media, out var composite) && composite.Segments is { } segments)
        {
            var clock = 0.0;
            foreach (var s in segments)
            {
                var start = s.Start ?? 0;
                var end = s.End ?? _files.FirstOrDefault(f => f.Id == s.Media)?.Duration ?? start;
                if (time < clock + end - start) return CellAt(s.Media, start + time - clock);
                clock += end - start;
            }
            return null;
        }
        var file = _files.FirstOrDefault(f => f.Id == media);
        if (file is null || file.Cells.Count == 0) return null;
        var i = file.Starts.FindLastIndex(t => t <= time + 0.001);
        return i < 0 ? null : (file.Vtsn, file.Cells[i].First, file.Cells[i].Last);
    }

    public double? StartOf(string media, int vtsn, uint firstSector, uint lastSector)
    {
        var file = _files.FirstOrDefault(f => f.Id == media);
        if (file is not null)
        {
            if (file.Vtsn != vtsn) return null;
            var i = file.Cells.IndexOf((firstSector, lastSector));
            return i >= 0 ? file.Starts[i] : null;
        }
        if (!Composites.TryGetValue(media, out var composite) || composite.Segments is null) return null;
        var clock = 0.0;
        foreach (var seg in composite.Segments)
        {
            var f = _files.FirstOrDefault(x => x.Id == seg.Media);
            if (f is null) return null;
            var from = seg.Start ?? 0;
            var to = seg.End ?? f.Duration;
            if (f.Vtsn == vtsn && f.Cells.IndexOf((firstSector, lastSector)) is var i and >= 0 && f.Starts[i] >= from - 0.001 && f.Starts[i] < to - 0.001)
                return Math.Round(clock + f.Starts[i] - from, 3);
            clock += to - from;
        }
        return null;
    }

    public PlaybackPlan? Map(IReadOnlyList<PlayedCell> cells)
    {
        // Greedy: at each cell, the file that holds the longest stretch of the run from there.
        var spans = new List<(MediaCells File, int From, int Count, int RunIndex)>();
        var i = 0;
        while (i < cells.Count)
        {
            var c = cells[i];
            (MediaCells File, int Index, int Length)? best = null;
            if (_where.TryGetValue((c.Vtsn, c.FirstSector, c.LastSector), out var hits))
                foreach (var (file, index) in hits)
                {
                    var n = 1;
                    while (i + n < cells.Count && index + n < file.Cells.Count &&
                           cells[i + n].Vtsn == file.Vtsn && file.Cells[index + n] == (cells[i + n].FirstSector, cells[i + n].LastSector))
                        n++;
                    if (best is null || n > best.Value.Length || n == best.Value.Length && file.Order < best.Value.File.Order)
                        best = (file, index, n);
                }
            if (best is { } b)
            {
                spans.Add((b.File, b.Index, b.Length, i));
                i += b.Length;
            }
            else i++;
        }
        if (spans.Count == 0) return null;

        var ends = new double?[cells.Count];
        if (spans.Count == 1)
        {
            var (file, from, count, runIndex) = spans[0];
            var start = file.Starts[from];
            var endIndex = from + count;
            double? end = endIndex < file.Cells.Count ? file.Starts[endIndex] : null;
            for (var k = 0; k < count; k++)
                ends[runIndex + k] = k + 1 + from < file.Cells.Count ? file.Starts[from + k + 1] : file.Duration;
            return new PlaybackPlan(file.Id, start, end, ends, (end ?? file.Duration) - start);
        }

        // Several files: a composite media of their ranges, with its own timeline from 0.
        var segments = new List<Segment>();
        var clock = 0.0;
        foreach (var (file, from, count, runIndex) in spans)
        {
            var start = file.Starts[from];
            var endIndex = from + count;
            var end = endIndex < file.Cells.Count ? file.Starts[endIndex] : file.Duration;
            segments.Add(new Segment { Media = file.Id, Start = start > 0 ? start : null, End = Math.Round(end, 3) });
            for (var k = 0; k < count; k++)
            {
                var cellEnd = from + k + 1 < file.Cells.Count ? file.Starts[from + k + 1] : file.Duration;
                ends[runIndex + k] = Math.Round(clock + cellEnd - start, 3);
            }
            clock += end - start;
        }
        // The last segment may run to the end of its media.
        if (segments[^1].End is { } last && Math.Abs(last - spans[^1].File.Duration) < 0.001) segments[^1].End = null;

        var key = string.Join(";", segments.Select(s => $"{s.Media}:{s.Start}:{s.End}"));
        if (!_composites.TryGetValue(key, out var id))
        {
            id = $"play{_composites.Count + 1}";
            _composites[key] = id;
            // Its chapters: where each segment starts, and its files' chapters inside the segments.
            var chapters = new SortedSet<double>();
            var at = 0.0;
            foreach (var (file, from, count, _) in spans)
            {
                var start = file.Starts[from];
                var end = from + count < file.Cells.Count ? file.Starts[from + count] : file.Duration;
                chapters.Add(Math.Round(at, 3));
                foreach (var t in file.ChapterTimes.Where(t => t > start + 0.001 && t < end - 0.001)) chapters.Add(Math.Round(at + t - start, 3));
                at += end - start;
            }
            Composites[id] = new MediaEntry
            {
                Segments = segments, Duration = Math.Round(clock, 3), Kind = "other",
                Chapters = chapters.Count >= 2 ? chapters.Select(t => new Chapter { Time = t }).ToList() : null,
            };
        }
        return new PlaybackPlan(id, 0, null, ends, clock);
    }
}

internal static class MediaCellsExtensions
{
    public static MediaCells With(this MediaCells m, List<(uint, uint)> cells, List<double> starts)
    {
        m.Cells.AddRange(cells);
        m.Starts.AddRange(starts);
        return m;
    }
}
