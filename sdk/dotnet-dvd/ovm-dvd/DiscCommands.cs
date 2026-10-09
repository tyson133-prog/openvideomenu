namespace OvmDvd;

/// <summary>map, nav, disasm: the disc as the package reads it.</summary>
internal static class DiscCommands
{
    public static int Map(string source, bool verbose)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var disc = DvdDisc.Open(source);
        var map = DiscMapBuilder.Build(disc);
        sw.Stop();

        Console.WriteLine($"Source:  {source}");
        Console.WriteLine($"Volume:  {disc.VolumeLabel ?? "(none)"}");
        Console.WriteLine($"Titles:  {disc.Vmg.Titles.Count} in {disc.TitleSets.Count} title sets; mapped in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine();

        Console.WriteLine("== Startup ==");
        var st = map.Startup!;
        foreach (var t in st.TitlesPassed) Console.WriteLine($"  plays {t}");
        Console.WriteLine($"  stops at: {st}");
        Console.WriteLine();

        Console.WriteLine($"== Menus ({map.Menus.Count}) ==");
        foreach (var m in map.Menus)
        {
            Console.WriteLine($"[{m.Id}] {m.Name}  {m.Video}  via {m.ReachedVia}");
            if (m.Timeout?.Target.Menu is { } tm)
                Console.WriteLine($"     times out after {m.TimeoutAfter:m\\:ss} -> menu [{tm.Id}]");
            Console.WriteLine($"     initial button {m.InitialButton}, {m.Buttons.Count} buttons" +
                              (m.Highlight.GroupCount > 1 ? $", button group {m.Highlight.SelectedGroup} of {m.Highlight.GroupCount}" : "") +
                              (m.Highlight.ForcedActivate > 0 ? $", forced activate {m.Highlight.ForcedActivate}" : ""));
            foreach (var b in m.Buttons)
            {
                var flags = b.IsHidden ? " HIDDEN" : "";
                var target = b.Target.Kind == TargetKind.Menu && b.Target.Menu is not null
                    ? (b.Target.Resumes ? "resume, else " : "") + $"-> menu [{b.Target.Menu.Id}]"
                      + (b.Target.PassedThrough.Count > 0 ? $" via {string.Join(", ", b.Target.PassedThrough.Select(p => $"title {p.Ttn}"))}" : "")
                    : b.Target.ToString();
                Console.WriteLine($"     {b.Button}{flags}  =>  {target}");
                if (verbose) Console.WriteLine($"        cmd {DvdCommands.Disassemble(b.Button.Command)}");
            }
        }
        Console.WriteLine();

        Console.WriteLine("== Titles ==");
        foreach (var t in map.Titles)
        {
            var extra = t.DuplicateOf is not { } d ? ""
                : t.IsNearDuplicate ? $" NEAR DUPLICATE of {d} (+{t.OwnCells.Count} cells of its own, {t.OwnCells.Sum(c => c.Duration.TotalSeconds):0.#} s)"
                : $" DUPLICATE of {d}";
            if (t.JunkReason is not null) extra += $" JUNK ({t.JunkReason})";
            Console.WriteLine($"  Title {t.Ttn,2}  vts{t.Vtsn} ttn{t.VtsTtn}  {t.Duration:hh\\:mm\\:ss}  {t.ChapterCount,3} ch  {t.Reach}{extra}");
            if (t.TargetedChapters.Count > 0)
                Console.WriteLine($"            buttons start chapters: {string.Join(",", t.TargetedChapters)}");
            if (t.IsNearDuplicate)
                Console.WriteLine($"            own cells: {string.Join(", ", t.OwnCells.Select(c => $"pgc{c.Pgcn} c{c.Cell} {c.FirstSector}-{c.LastSector} {c.Duration.TotalSeconds:0.#}s"))}");
            foreach (var s in t.Sources.Distinct().Take(verbose ? 50 : 4))
                Console.WriteLine($"            <- {s}");
        }

        if (map.OrphanPgcs.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("== PGCs reached outside the title table ==");
            foreach (var o in map.OrphanPgcs) Console.WriteLine($"  {o}");
        }

        if (map.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("== Warnings ==");
            foreach (var w in map.Warnings) Console.WriteLine($"  [{w.Code}] {w.Message}");
        }

        if (verbose)
        {
            Console.WriteLine();
            Console.WriteLine("== First-play and menu PGC commands ==");
            DumpPgc("FP", disc.Vmg.FirstPlay);
            var vmgm = disc.MenuUnit(DvdDomain.VmgMenu, 0);
            if (vmgm is not null)
                for (var i = 0; i < vmgm.Pgcs.Count; i++) DumpPgc($"VMGM PGC {i + 1} ({vmgm.Pgcs[i].MenuType})", vmgm.Pgcs[i]);
            foreach (var (v, vts) in disc.TitleSets.OrderBy(k => k.Key))
            {
                var u = disc.MenuUnit(DvdDomain.VtsMenu, v);
                if (u is not null)
                    for (var i = 0; i < u.Pgcs.Count; i++) DumpPgc($"VTSM {v} PGC {i + 1} ({u.Pgcs[i].MenuType})", u.Pgcs[i]);
                for (var i = 0; i < vts.TitlePgcs.Count; i++) DumpPgc($"VTS {v} title PGC {i + 1}", vts.TitlePgcs[i]);
            }
        }
        return 0;
    }

    /// <summary>Every NAV pack of a cell: where its PCI is, its highlight status and buttons. Args: vmgm|vtsm|title vtsn pgcn cell.</summary>
    public static int Nav(string source, string[] a)
    {
        if (a.Length < 4) throw new ArgumentException("nav needs: vmgm|vtsm|title <vtsn> <pgcn> <cell>");
        using var disc = DvdDisc.Open(source);
        var domain = a[0] == "vmgm" ? DvdDomain.VmgMenu : a[0] == "title" ? DvdDomain.Title : DvdDomain.VtsMenu;
        int vtsn = int.Parse(a[1]), pgcn = int.Parse(a[2]), cellNo = int.Parse(a[3]);
        var pgc = disc.GetPgc(domain, vtsn, pgcn) ?? throw new ArgumentException("No such PGC.");
        DumpPgc($"{domain} {vtsn} PGC {pgcn}", pgc);
        for (var i = 0; i < pgc.Cells.Count; i++)
        {
            var c = pgc.Cells[i];
            Console.WriteLine($"  cell {i + 1}: sectors {c.FirstSector}-{c.LastSector} lastVobu {c.LastVobuStartSector} still {c.StillTime} cmd {c.CellCommandNumber} {c.Duration}");
        }
        var cell = pgc.Cells[cellNo - 1];
        var name = domain == DvdDomain.VmgMenu ? "VIDEO_TS.VOB" : domain == DvdDomain.Title ? $"VTS_{vtsn:00}_1.VOB" : $"VTS_{vtsn:00}_0.VOB";
        using var fs = DvdFileSystemFactory.Open(source);
        using var vob = fs.OpenRead($"/VIDEO_TS/{name}");
        var buf = new byte[2048];
        var sector = cell.FirstSector;
        for (var n = 0; n < 60 && sector <= cell.LastSector; n++)
        {
            vob.Position = (long)sector * 2048;
            vob.ReadExactly(buf);
            var ok = NavPackReader.TryLocatePci(buf, out var pci);
            Console.WriteLine($"  sector {sector}: pci={ok}@{pci} head {Convert.ToHexString(buf, 0, 48)}");
            if (ok)
            {
                var hli = pci + 0x60;
                Console.WriteLine($"     hli_ss={(buf[hli] << 8 | buf[hli + 1]) & 3} btn_md={buf[hli + 14] << 8 | buf[hli + 15]:X4} btn_ns={buf[hli + 17]} fosl={buf[hli + 20]} foac={buf[hli + 21]}");
                if (NavPackReader.ParseHighlight(buf, pci, false, sector) is { } hl)
                    foreach (var b in hl.Buttons) Console.WriteLine($"       {b}  {DvdCommands.Disassemble(b.Command)}");
            }
            var next = NavPackReader.NextVobuForDebug(buf);
            Console.WriteLine($"     vobu_ea={next}");
            if (next == 0) break;
            sector += next + 1;
        }
        return 0;
    }

    public static int Disasm(IEnumerable<string> hex)
    {
        foreach (var h in hex)
        {
            var bytes = Convert.FromHexString(h.Replace(" ", ""));
            Console.WriteLine(DvdCommands.Disassemble(bytes));
        }
        return 0;
    }

    private static void DumpPgc(string label, Pgc? pgc)
    {
        if (pgc is null) return;
        Console.WriteLine($"  {label}: {pgc.ProgramCount} programs, {pgc.CellCount} cells, {pgc.Duration:hh\\:mm\\:ss}, next {pgc.NextPgc}, still {pgc.StillTime}");
        foreach (var c in pgc.PreCommands) Console.WriteLine($"      pre  {DvdCommands.Disassemble(c)}");
        foreach (var c in pgc.PostCommands) Console.WriteLine($"      post {DvdCommands.Disassemble(c)}");
        foreach (var c in pgc.CellCommands) Console.WriteLine($"      cell {DvdCommands.Disassemble(c)}");
    }
}
