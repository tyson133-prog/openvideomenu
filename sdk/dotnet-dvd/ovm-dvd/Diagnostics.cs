namespace OvmDvd;

/// <summary>stats, scan, uops, modes, sets: one-line diagnostics of what a disc uses.</summary>
internal static class Diagnostics
{
    public static int Stats(string source)
    {
        using var disc = DvdDisc.Open(source);
        var vmgmUnits = disc.Vmg.Menus.Count;
        var vtsUnits = disc.TitleSets.Values.Select(v => v.Menus.Count).DefaultIfEmpty(0).Max();
        var langs = string.Join("/", disc.Vmg.Menus.Select(u => u.Language).Concat(disc.TitleSets.Values.SelectMany(v => v.Menus.Select(u => u.Language))).Distinct());
        var angleTitles = disc.Vmg.Titles.Count(t => t.AngleCount > 1);
        var buttonCells = 0;
        var cells = 0;
        foreach (var (vtsn, vts) in disc.TitleSets)
            for (var p = 1; p <= vts.TitlePgcs.Count; p++)
                for (var c = 1; c <= vts.TitlePgcs[p - 1].Cells.Count; c++)
                {
                    if (++cells > 3000) break;
                    if (disc.GetTitleCellHighlight(vtsn, p, c, maxVobus: 2) is { Buttons.Count: > 0 }) buttonCells++;
                }
        Console.WriteLine($"STATS vmgmUnits={vmgmUnits} vtsUnits={vtsUnits} langs={langs} angleTitles={angleTitles} titleButtonCells={buttonCells}/{cells} :: {source}");
        return 0;
    }
    

    public static int Scan(string source)
    {
        using var disc = DvdDisc.Open(source);
        var c = new Dictionary<string, int>();
        void Count(string k) => c[k] = c.TryGetValue(k, out var n) ? n + 1 : 1;

        void Scan(IEnumerable<byte[]> cmds, string where)
        {
            foreach (var cmd in cmds)
                foreach (var op in DvdCommands.Decode(cmd).AllOps())
                    switch (op)
                    {
                        case SetNavTimerOp: Count("navTimer"); break;
                        case SetGprmModeOp { Counter: true }: Count("counterReg"); break;
                        case SetGprmOp { Op: 8 }: Count("random"); break;
                        case LinkOp { Link.Cmd: LinkCmd.Exit }: Count("exit"); break;
                        case LinkOp l when where is "titleCell" or "titleButton" &&
                                           l.Link.Cmd is LinkCmd.LinkCN or LinkCmd.LinkPGN or LinkCmd.LinkPTTN or LinkCmd.LinkTopC or
                                               LinkCmd.LinkNextC or LinkCmd.LinkPrevC or LinkCmd.LinkTopPG or LinkCmd.LinkNextPG or
                                               LinkCmd.LinkPrevPG or LinkCmd.LinkTopPGC or LinkCmd.LinkPGCN or LinkCmd.LinkNextPGC or
                                               LinkCmd.LinkPrevPGC:
                            Count(where + "Jump"); break;
                        case LinkOp l when where == "titleButton":
                            Count("titleButton" + (l.Link.Cmd.ToString().StartsWith("CallSS") ? "Call" : l.Link.Cmd.ToString().StartsWith("Jump") ? "Jump" : "Other"));
                            break;
                    }
        }

        foreach (var v in disc.TitleSets.Values)
            foreach (var p in v.TitlePgcs)
            {
                Scan(p.PreCommands.Concat(p.PostCommands), "titlePgc");
                Scan(p.CellCommands, "titleCell");
            }
        var menuPgcs = disc.Vmg.Menus.SelectMany(u => u.Pgcs).Concat(disc.TitleSets.Values.SelectMany(v => v.Menus.SelectMany(u => u.Pgcs)));
        if (disc.Vmg.FirstPlay is { } fp) menuPgcs = menuPgcs.Append(fp);
        foreach (var p in menuPgcs) Scan(p.PreCommands.Concat(p.PostCommands).Concat(p.CellCommands), "menuPgc");

        // PGC playback modes (random/shuffle), prohibited remote operations, stills that wait in titles.
        foreach (var v in disc.TitleSets.Values)
            foreach (var p in v.TitlePgcs)
            {
                if (p.PlaybackMode != 0) Count("shufflePgc");
                if (p.ProhibitedUserOps != 0) Count("uopTitlePgc");
                if (p.StillTime != 0) Count(p.StillTime == 0xFF ? "titlePgcStillForever" : "titlePgcStill");
                foreach (var cell in p.Cells)
                    if (cell.StillTime != 0) Count(cell.StillTime == 0xFF ? "titleCellStillForever" : "titleCellStill");
            }
        foreach (var p in menuPgcs)
        {
            if (p.PlaybackMode != 0) Count("shuffleMenuPgc");
            if (p.ProhibitedUserOps != 0) Count("uopMenuPgc");
        }
        foreach (var cmd in menuPgcs.Concat(disc.TitleSets.Values.SelectMany(v => v.TitlePgcs)).SelectMany(p => p.PreCommands.Concat(p.PostCommands).Concat(p.CellCommands)))
        {
            if (cmd.Length >= 8 && cmd[0] >> 5 == 2 && (cmd[0] & 0x0F) == 4) Count("karaokeMix");
            if (DvdCommands.Decode(cmd).AllOps().Any(o => o is SetParentalOp)) Count("tempParental");
        }

        // Menu cells whose buttons change while they play, and button groups per display shape.
        foreach (var (domain, vtsn, units) in new[] { (DvdDomain.VmgMenu, 0, disc.MenuUnit(DvdDomain.VmgMenu, 0)) }
                     .Concat(disc.TitleSets.Keys.Select(v => (DvdDomain.VtsMenu, v, disc.MenuUnit(DvdDomain.VtsMenu, v)))))
        {
            if (units is null) continue;
            for (var p = 1; p <= units.Pgcs.Count; p++)
                for (var cell = 1; cell <= units.Pgcs[p - 1].Cells.Count; cell++)
                {
                    if (disc.GetHighlight(domain, vtsn, p, cell) is not { } hl) continue;
                    if (hl.GroupCount > 1) Count("buttonGroups");
                    var statuses = disc.MenuCellHighlightStatuses(domain, vtsn, p, cell);
                    if (statuses.Count(x => x == 1) > 1 || statuses.Contains(3)) Count("menuButtonsChange");
                }
        }

        var cells = 0;
        foreach (var (vtsn, vts) in disc.TitleSets)
            for (var p = 1; p <= vts.TitlePgcs.Count; p++)
                for (var cell = 1; cell <= vts.TitlePgcs[p - 1].Cells.Count && cells < 3000; cell++, cells++)
                    if (disc.GetTitleCellHighlight(vtsn, p, cell, maxVobus: 2) is { Buttons.Count: > 0 } hl)
                    {
                        Count("titleButtonCells");
                        if (vts.TitlePgcs[p - 1].Cells[cell - 1].StillTime == 0xFF || vts.TitlePgcs[p - 1].Cells[cell - 1].Duration < TimeSpan.FromSeconds(2))
                            Count("titleMenuCells");
                        Scan(hl.Buttons.Select(b => b.Command), "titleButton");
                    }

        Console.WriteLine("SCAN " + string.Join(" ", c.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) + $" :: {source}");
        return 0;
    }
    

    /// <summary>modes &lt;iso&gt;: title PGCs that play their programs at random or shuffled (IFO only, fast).</summary>
    public static int Modes(string source)
    {
        using var disc = DvdDisc.Open(source);
        var odd = disc.TitleSets.SelectMany(v => v.Value.TitlePgcs.Select((p, i) => (v.Key, i + 1, p)))
            .Where(x => x.p.PlaybackMode != 0).ToList();
        Console.WriteLine($"MODES randomPgcs={odd.Count} {string.Join(" ", odd.Take(6).Select(x => $"vts{x.Key}pgc{x.Item2}={x.p.PlaybackMode:x2}/{x.p.ProgramCount}"))} :: {source}");
        return 0;
    }

    /// <summary>sets &lt;iso&gt;: menu cells whose buttons change while they show, and how.</summary>
    public static int Sets(string source)
    {
        using var disc = DvdDisc.Open(source);
        var cells = 0;
        foreach (var (domain, vtsn, units) in new[] { (DvdDomain.VmgMenu, 0, disc.Vmg.Menus) }
                     .Concat(disc.TitleSets.Select(v => (DvdDomain.VtsMenu, v.Key, v.Value.Menus))))
        {
            if (units.Count == 0) continue;
            var pgcs = units[0].Pgcs;
            for (var p = 1; p <= pgcs.Count; p++)
                for (var c = 1; c <= pgcs[p - 1].Cells.Count; c++)
                {
                    var sets = disc.MenuCellHighlights(domain, vtsn, p, c, 0);
                    if (sets.Count < 2) continue;
                    cells++;
                    string Shape(HighlightInfo h) => string.Join(",", h.Buttons.Select(b => $"{b.X0}.{b.Y0}.{b.X1}.{b.Y1}"));
                    string Cmds(HighlightInfo h) => string.Join(",", h.Buttons.Select(b => Convert.ToHexString(b.Command)));
                    Console.WriteLine($"  {domain} vts{vtsn} pgc{p} c{c} ({pgcs[p - 1].Cells[c - 1].Duration.TotalSeconds:0.#}s): " + string.Join(" | ", sets.Select((h, i) =>
                        $"{h.ButtonsAt.TotalSeconds:0.##}-{h.ButtonsUntil?.TotalSeconds.ToString("0.##") ?? "end"}s {h.Buttons.Count}b" +
                        (i == 0 ? "" : $"{(Shape(h) == Shape(sets[0]) ? "" : " rects")}{(Cmds(h) == Cmds(sets[0]) ? "" : " cmds")}"))));
                }
        }
        Console.WriteLine($"SETS cells={cells} :: {source}");
        return 0;
    }

    public static int Uops(string source, bool verbose)
    {
        using var disc = DvdDisc.Open(source);
        int cells = 0, forbidding = 0, changing = 0, differFromPgc = 0, pgcsMixed = 0, pgcsForbidding = 0;
        foreach (var (vtsn, vts) in disc.TitleSets.OrderBy(v => v.Key))
            for (var p = 1; p <= vts.TitlePgcs.Count; p++)
            {
                var pgc = vts.TitlePgcs[p - 1];
                if (pgc.ProhibitedUserOps != 0) pgcsForbidding++;
                var perCell = new System.Collections.Generic.List<uint>();
                for (var c = 1; c <= pgc.Cells.Count; c++)
                {
                    cells++;
                    var changes = disc.TitleCellUops(vtsn, p, c);
                    if (changes.Count == 0) continue;
                    if (changes.Any(x => x.Uops != 0)) forbidding++;
                    if (changes.Count > 1) changing++;
                    if (changes.Any(x => x.Uops != pgc.ProhibitedUserOps)) differFromPgc++;
                    perCell.Add(changes[0].Uops);
                    if (verbose && (changes.Count > 1 || changes[0].Uops != 0 || pgc.ProhibitedUserOps != 0))
                        Console.WriteLine($"  vts{vtsn} pgc{p} c{c} ({pgc.Cells[c - 1].Duration:hh\\:mm\\:ss}) pgc={pgc.ProhibitedUserOps:x7} vobus: " +
                                          string.Join(" ", changes.Select(x => $"{x.At:0.#}s={x.Uops:x7}")));
                }
                if (perCell.Distinct().Count() > 1) pgcsMixed++;
            }
        Console.WriteLine($"UOPS cells={cells} forbidding={forbidding} changingInCell={changing} differFromPgc={differFromPgc} " +
                          $"pgcsForbidding={pgcsForbidding} pgcsWithMixedCells={pgcsMixed} :: {source}");
        return 0;
    }
    

}
