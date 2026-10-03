using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>The files a manifest plays, and where each disc cell is in them.</summary>
/// <param name="Media">The manifest's media entries, by id.</param>
/// <param name="Index">Which file holds each disc cell, and where (for <see cref="MenuManifestBuilder"/>).</param>
/// <param name="Files">The same, file by file.</param>
public sealed record DvdMediaSet(Dictionary<string, MediaEntry> Media, CellMediaIndex Index, IReadOnlyList<MediaCells> Files);

/// <summary>Media sets for common ways of ripping a disc. Other layouts build a <see cref="CellMediaIndex"/> from <see cref="MediaCells"/>.</summary>
public static class DvdMedia
{
    /// <summary>
    /// One file per DVD title, as MakeMKV and HandBrake rip them. Each title's media id is <c>t{number}</c>.
    /// </summary>
    /// <param name="fileOf">A title number's file (its ref, relative to the bundle), or null when it wasn't ripped.</param>
    /// <param name="clipOf">
    /// A title-domain PGC that no title lists (a clip only the menus play), by title set and PGC number: its file,
    /// or null. Its media id is <c>v{vtsn}p{pgcn}</c>. Default: none.
    /// </param>
    /// <param name="labelOf">A media id's label (default: the id).</param>
    public static DvdMediaSet OneFilePerTitle(DvdDisc disc, Func<int, string?> fileOf, Func<int, int, string?>? clipOf = null,
        Func<string, string?>? labelOf = null)
    {
        var refs = new Dictionary<string, string>();
        var files = new List<MediaCells>();
        foreach (var t in disc.Vmg.Titles)
            if (fileOf(t.TitleNumber) is { } file && CellMediaIndex.CellsOf(disc, $"t{t.TitleNumber}", t.TitleNumber, t.TitleNumber, 1, null) is { Cells.Count: > 0 } cells)
            {
                files.Add(cells);
                refs[cells.Id] = file;
            }
        if (clipOf is not null)
        {
            var order = 1000;
            foreach (var (vtsn, vts) in disc.TitleSets.OrderBy(v => v.Key))
                for (var p = 1; p <= vts.TitlePgcs.Count; p++)
                    if (disc.FindVtsTtnForPgc(vtsn, p) == 0 && clipOf(vtsn, p) is { } file &&
                        CellMediaIndex.CellsOfPgc(disc, $"v{vtsn}p{p}", order++, vtsn, p) is { Cells.Count: > 0 } orphan)
                    {
                        files.Add(orphan);
                        refs[orphan.Id] = file;
                    }
        }
        var media = files.ToDictionary(f => f.Id, f => new MediaEntry
        {
            Ref = refs[f.Id],
            Label = labelOf?.Invoke(f.Id) ?? f.Id,
            Duration = f.Duration,
            Chapters = f.ChapterTimes.Count >= 2 ? f.ChapterTimes.Select(t => new Chapter { Time = t }).ToList() : null,
        });
        return new DvdMediaSet(media, new CellMediaIndex(files), files);
    }
}
