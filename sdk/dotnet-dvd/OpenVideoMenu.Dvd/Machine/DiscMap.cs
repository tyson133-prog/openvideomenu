using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OpenVideoMenu.Dvd.Machine;

public enum TargetKind
{
    /// <summary>Starts playback of a title (or chapter of a title).</summary>
    Title,

    /// <summary>Opens another menu (or another page of the same menu).</summary>
    Menu,

    /// <summary>Changes a setting (audio, subtitles) without leaving the menu.</summary>
    Setting,

    /// <summary>The simulation could not determine the destination.</summary>
    Unresolved,
}

public sealed class ButtonTarget
{
    public TargetKind Kind { get; init; }
    public TitleLanding? Landing { get; init; }

    /// <summary>Short titles played on the way to <see cref="Landing"/> (e.g. an intro before an episode).</summary>
    public IReadOnlyList<TitleLanding> PassedThrough { get; init; } = Array.Empty<TitleLanding>();

    public MenuNode? Menu { get; internal set; }
    public string? Detail { get; init; }

    /// <summary>The VM state right after activation (title start or new menu).</summary>
    public VmState? ResultState { get; init; }

    /// <summary>Button-less menu cells played before <see cref="Menu"/> shows (a page transition clip).</summary>
    public IReadOnlyList<MenuCellRef> LeadIn { get; init; } = Array.Empty<MenuCellRef>();

    /// <summary>
    /// The disc resumes the stopped title here once one has been played (a conditional LinkRSM on the
    /// way); this target is where the button goes before that.
    /// </summary>
    public bool Resumes { get; init; }

    public override string ToString() => (Resumes ? "resume, else " : "") + Kind switch
    {
        TargetKind.Title => Landing!.ToString() + (PassedThrough.Count > 0 ? $" via {string.Join(", ", PassedThrough.Select(p => $"title {p.Ttn}"))}" : ""),
        TargetKind.Menu => $"-> {Menu?.Name ?? "menu"}",
        TargetKind.Setting => "setting" + (Detail is null ? "" : $" [{Detail}]"),
        _ => "unresolved: " + Detail,
    };
}

public sealed class MenuButtonNode
{
    public DvdButton Button { get; init; } = new();
    public ButtonTarget Target { get; set; } = new() { Kind = TargetKind.Unresolved };

    /// <summary>False when arrow-key navigation from the menu's initial button can never select it.</summary>
    public bool ReachableByArrows { get; set; } = true;

    /// <summary>A likely easter egg: not reachable by arrows, or with no visible area.</summary>
    public bool IsHidden => !ReachableByArrows || !Button.HasArea;
}

public sealed class MenuNode
{
    public int Id { get; init; }
    public string Key { get; init; } = "";

    /// <summary>Identifies the menu's picture and button layout regardless of register state.</summary>
    public string LayoutKey { get; init; } = "";

    public DvdDomain Domain { get; init; }
    public int Vtsn { get; init; }
    public int Pgcn { get; init; }
    public int Cell { get; init; }

    /// <summary>The menu language unit (0-based); -1 = the one navigation uses by default.</summary>
    public int MenuUnit { get; init; } = -1;
    public MenuType MenuType { get; init; }
    public VmState State { get; init; } = new();
    public HighlightInfo Highlight { get; init; } = new();
    public VideoAttributes Video { get; init; } = new();
    public List<MenuButtonNode> Buttons { get; } = new();
    public int Depth { get; init; }
    public string ReachedVia { get; init; } = "";
    public int InitialButton { get; init; } = 1;

    /// <summary>
    /// Where the disc goes by itself when nothing is pressed (a timed still that runs out, possibly through a
    /// short clip). Not in <see cref="Buttons"/>; its button number is 0. Null when the menu waits forever.
    /// </summary>
    public MenuButtonNode? Timeout { get; internal set; }

    /// <summary>How long until <see cref="Timeout"/> fires, counted from when the menu appears.</summary>
    public TimeSpan TimeoutAfter { get; internal set; }

    /// <summary>Summary of where every button leads; equal signatures mean interchangeable variants.</summary>
    internal string Signature { get; init; } = "";

    public string Name =>
        (Domain == DvdDomain.VtsMenu ? $"VTS {Vtsn} menu" : Domain == DvdDomain.VmgMenu ? "Disc menu" : "First-play menu") +
        (MenuType != MenuType.None ? $" ({MenuType})" : "") + $" PGC {Pgcn}" + (Cell > 1 ? $" cell {Cell}" : "");

    public MenuButtonNode? GetButton(int number) =>
        number >= 1 && number <= Buttons.Count ? Buttons[number - 1] : null;
}

public enum Reachability
{
    /// <summary>A normal, visible menu button plays it.</summary>
    OnMenu,

    /// <summary>Only a hidden button (unreachable by arrows or invisible) plays it: an easter egg.</summary>
    HiddenButton,

    /// <summary>Played automatically (first play, after another title) but no button selects it.</summary>
    OffMenu,

    /// <summary>Nothing in the navigation reaches it.</summary>
    Unreferenced,
}

/// <summary>A title cell: where it is (title set, PGC, cell number), its sectors and the disc's playback time for it.</summary>
public sealed record DvdCellRef(int Vtsn, int Pgcn, int Cell, uint FirstSector, uint LastSector, TimeSpan Duration);

/// <summary>One DVD title (from the title search table) plus how the navigation reaches it.</summary>
public sealed class DvdTitleInfo
{
    public int Ttn { get; init; }
    public int Vtsn { get; init; }
    public int VtsTtn { get; init; }
    public int ChapterCount { get; init; }
    public int AngleCount { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<TimeSpan> ChapterStarts { get; init; } = Array.Empty<TimeSpan>();
    public IReadOnlyList<int> Pgcns { get; init; } = Array.Empty<int>();

    public Reachability Reach { get; internal set; } = Reachability.Unreferenced;
    public List<string> Sources { get; } = new();

    /// <summary>Chapters that menu buttons start at (episode starts inside a "play all" title).</summary>
    public SortedSet<int> TargetedChapters { get; } = new();

    /// <summary>
    /// Set when another, lower-numbered title plays the same video, so its file can play this title too: exactly the
    /// same cells, or (a near duplicate) nearly all of them plus a few of this title's own, listed in
    /// <see cref="OwnCells"/>.
    /// </summary>
    public int? DuplicateOf { get; internal set; }

    /// <summary>
    /// For a near duplicate, the cells this title plays that <see cref="DuplicateOf"/> doesn't: the same film with
    /// short pieces of its own (a "white rabbit" mode's copies of its rabbit scenes). A ripper that rips only the
    /// original has to rip these too, or playback of this title skips them. Empty otherwise.
    /// </summary>
    public IReadOnlyList<DvdCellRef> OwnCells { get; internal set; } = Array.Empty<DvdCellRef>();

    /// <summary>A duplicate with cells of its own (<see cref="OwnCells"/>).</summary>
    public bool IsNearDuplicate => DuplicateOf is not null && OwnCells.Count > 0;

    /// <summary>Non-null when the title is likely filler (a few frames, a black gap).</summary>
    public string? JunkReason { get; internal set; }

    public TimeSpan ChapterStart(int chapter) =>
        chapter >= 1 && chapter <= ChapterStarts.Count ? ChapterStarts[chapter - 1] : TimeSpan.Zero;

    public TimeSpan ChapterEnd(int chapter) =>
        chapter >= 1 && chapter < ChapterStarts.Count ? ChapterStarts[chapter] : Duration;
}

public sealed class DiscMapOptions
{
    public int MaxMenus { get; init; } = 400;
    public int MaxVariantsPerLayout { get; init; } = 4;

    /// <summary>Titles shorter than this are followed through when they lead to another title.</summary>
    public TimeSpan FollowThroughBelow { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Every menu, every button's destination, and every title on a disc.</summary>
public sealed class DiscMap
{
    public DvdDisc Disc { get; init; } = null!;
    public List<MenuNode> Menus { get; } = new();
    public MenuNode? StartMenu { get; internal set; }
    public VmResult? Startup { get; internal set; }
    public List<DvdTitleInfo> Titles { get; } = new();

    /// <summary>Title-domain PGCs reached by navigation that no title table entry lists.</summary>
    public List<TitleLanding> OrphanPgcs { get; } = new();

    public DvdWarnings Warnings { get; } = new();

    public DvdTitleInfo? GetTitle(int ttn) => Titles.FirstOrDefault(t => t.Ttn == ttn);

    public IEnumerable<MenuNode> MenusWithLayout(string layoutKey) => Menus.Where(m => m.LayoutKey == layoutKey);
}

public static class DiscMapBuilder
{
    public static DiscMap Build(DvdDisc disc, DiscMapOptions? options = null, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new DiscMapOptions();
        var vm = new DvdVm(disc);
        var map = new DiscMap { Disc = disc };
        map.Warnings.AddRange(disc.Warnings);

        var byKey = new Dictionary<string, MenuNode>();
        var queue = new Queue<MenuNode>();

        MenuNode? AddNode(VmResult r, int depth, string via)
        {
            var s = r.State;
            var layout = LayoutKey(s);
            var key = layout + "|" + s.RegisterKey();
            if (byKey.TryGetValue(key, out var existing)) return existing;

            var variants = map.Menus.Where(m => m.LayoutKey == layout).ToList();

            // Register differences that don't change any button's destination are the same menu.
            var signature = Signature(vm, s, r.Highlight!);
            var same = variants.FirstOrDefault(v => v.Signature == signature);
            if (same is not null)
            {
                byKey[key] = same;
                return same;
            }

            if (variants.Count >= options.MaxVariantsPerLayout || map.Menus.Count >= options.MaxMenus)
                return variants.FirstOrDefault();

            var pgc = disc.GetPgc(s.Domain, s.Vtsn, s.Pgcn);
            var node = new MenuNode
            {
                Id = map.Menus.Count + 1,
                Key = key,
                LayoutKey = layout,
                Domain = s.Domain,
                Vtsn = s.Vtsn,
                Pgcn = s.Pgcn,
                Cell = s.Cell,
                MenuType = pgc?.MenuType ?? MenuType.None,
                State = s.Clone(),
                Highlight = r.Highlight!,
                Video = disc.MenuVideo(s.Domain, s.Vtsn),
                Depth = depth,
                ReachedVia = via,
                InitialButton = Math.Clamp(s.HighlightedButton, 1, Math.Max(1, r.Highlight!.Buttons.Count)),
                Signature = signature,
            };
            foreach (var b in r.Highlight!.Buttons)
                node.Buttons.Add(new MenuButtonNode { Button = b });
            MarkArrowReachability(node);

            byKey[key] = node;
            map.Menus.Add(node);
            queue.Enqueue(node);
            return node;
        }

        // 1. The path a player takes from first play.
        progress?.Report("Simulating first play...");
        var start = vm.StartFromFirstPlay();
        map.Startup = start;
        if (start.Kind == VmStopKind.Menu)
            map.StartMenu = AddNode(start, 0, "first play");
        else
            map.Warnings.Add(DvdWarningCode.FirstPlayNoMenu, $"First play did not reach a menu: {start}");

        void Drain()
        {
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var node = queue.Dequeue();
                progress?.Report($"Mapping {node.Name} ({map.Menus.Count} menus found)...");
                foreach (var bn in node.Buttons)
                    bn.Target = Resolve(vm, node, bn.Button, options, r =>
                        AddNode(r, node.Depth + 1, $"{node.Name} button {bn.Button.Number}"));
                ResolveTimeout(vm, disc, node, options, r => AddNode(r, node.Depth + 1, $"{node.Name} timeout"));
            }
        }

        Drain();

        // 2. Menus listed in the menu tables that the first-play path doesn't reach (with default registers).
        var seeds = new List<(DvdDomain Domain, int Vtsn, int Pgcn, string Label)>();
        void AddSeeds(DvdDomain domain, int vtsn)
        {
            var unit = disc.MenuUnit(domain, vtsn);
            if (unit is null) return;
            for (var i = 0; i < unit.Pgcs.Count; i++)
                if (unit.Pgcs[i].IsEntry)
                    seeds.Add((domain, vtsn, i + 1, $"menu table ({unit.Pgcs[i].MenuType})"));
        }

        AddSeeds(DvdDomain.VmgMenu, 0);
        foreach (var v in disc.TitleSets.Keys.OrderBy(k => k)) AddSeeds(DvdDomain.VtsMenu, v);

        foreach (var (domain, vtsn, pgcn, label) in seeds)
        {
            ct.ThrowIfCancellationRequested();
            var r = vm.EnterMenuPgc(domain, vtsn, pgcn);
            if (r.Kind != VmStopKind.Menu) continue;
            if (map.Menus.Any(m => m.LayoutKey == LayoutKey(r.State))) continue;
            AddNode(r, 1, label);
            Drain();
        }

        BuildTitleInventory(map, start);
        progress?.Report($"Mapped {map.Menus.Count} menus and {map.Titles.Count} titles.");
        return map;
    }

    public static string LayoutKey(VmState s) => $"{s.Domain}:{s.Vtsn}:{s.Pgcn}:{s.Cell}";

    private static string Signature(DvdVm vm, VmState state, HighlightInfo hl)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in hl.Buttons)
        {
            var r = vm.ActivateButton(state, b);
            sb.Append(b.Number).Append('=').Append(r.Kind).Append(':');
            if (r.Kind == VmStopKind.Title && r.Landing is { } l) sb.Append(l.Vtsn).Append('/').Append(l.Pgcn).Append('/').Append(l.Pgn);
            else if (r.Kind == VmStopKind.Menu) sb.Append(LayoutKey(r.State));
            sb.Append(';');
        }
        return sb.ToString();
    }

    /// <summary>A timed menu's own continuation, followed through short clips to the next menu.</summary>
    private static void ResolveTimeout(DvdVm vm, DvdDisc disc, MenuNode node, DiscMapOptions options, Func<VmResult, MenuNode?> menuFor)
    {
        var pgc = disc.GetPgc(node.Domain, node.Vtsn, node.Pgcn);
        if (pgc is null || node.Cell < 1 || node.Cell > pgc.Cells.Count) return;
        var cell = pgc.Cells[node.Cell - 1];
        var wait = cell.Duration + TimeSpan.FromSeconds(cell.StillTime == 0xFF ? 0 : cell.StillTime);

        var r = vm.ContinueAfterStill(node.State);
        for (var hop = 0; hop < 3 && r.Kind == VmStopKind.Title && r.Landing is { } l && l.PgcDuration < options.FollowThroughBelow; hop++)
        {
            wait += l.PgcDuration;
            r = vm.ContinueAfterTitle(r.State);
        }
        if (r.Kind != VmStopKind.Menu) return;
        wait += r.MenuVideoPlayed;
        var target = menuFor(r);
        if (target is null || target.LayoutKey == node.LayoutKey) return;
        node.Timeout = new MenuButtonNode
        {
            Button = new DvdButton { Number = 0 },
            Target = new ButtonTarget { Kind = TargetKind.Menu, Menu = target, ResultState = r.State, Detail = "after the still times out", LeadIn = r.LeadInCells },
            ReachableByArrows = true,
        };
        node.TimeoutAfter = wait;
    }

    /// <summary>Resolves one button of a menu to its destination.</summary>
    public static ButtonTarget Resolve(DvdVm vm, MenuNode node, DvdButton button, DiscMapOptions options,
        Func<VmResult, MenuNode?> menuFor)
    {
        var r = vm.ActivateButton(node.State, button);
        switch (r.Kind)
        {
            case VmStopKind.Title:
                {
                    var landing = r.Landing!;
                    var passed = new List<TitleLanding>();
                    var state = r.State;
                    for (var hop = 0; hop < 3 && landing.PgcDuration < options.FollowThroughBelow; hop++)
                    {
                        var c = vm.ContinueAfterTitle(state);
                        if (c.Kind != VmStopKind.Title || c.Landing is null) break;
                        if (c.Landing.Vtsn == landing.Vtsn && c.Landing.Pgcn == landing.Pgcn) break;
                        passed.Add(landing);
                        landing = c.Landing;
                        state = c.State;
                    }
                    return new ButtonTarget { Kind = TargetKind.Title, Landing = landing, PassedThrough = passed, ResultState = state, Resumes = r.OffersResume };
                }

            case VmStopKind.Menu:
                {
                    var target = menuFor(r);
                    if (target is not null && target == node)
                        return new ButtonTarget { Kind = TargetKind.Setting, Menu = node, Detail = "stays on this menu", ResultState = r.State, Resumes = r.OffersResume };
                    if (target is not null && target.LayoutKey == node.LayoutKey)
                        return new ButtonTarget { Kind = TargetKind.Setting, Menu = target, Detail = "updates this menu", ResultState = r.State, Resumes = r.OffersResume };
                    return new ButtonTarget { Kind = TargetKind.Menu, Menu = target, ResultState = r.State, Detail = target is null ? "menu limit reached" : null, LeadIn = r.LeadInCells, Resumes = r.OffersResume };
                }

            case VmStopKind.NoNavigation:
                return new ButtonTarget { Kind = TargetKind.Setting, Detail = r.Detail, ResultState = r.State, Resumes = r.OffersResume };

            default:
                return new ButtonTarget { Kind = TargetKind.Unresolved, Detail = r.ToString(), ResultState = r.State, Resumes = r.OffersResume };
        }
    }

    /// <summary>The sector ranges a title plays, in order (first angle only).</summary>
    private static string ContentSignature(DvdDisc disc, DvdTitleInfo t)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var pgcn in t.Pgcns)
        {
            var pgc = disc.GetPgc(DvdDomain.Title, t.Vtsn, pgcn);
            if (pgc is null) continue;
            foreach (var c in pgc.Cells)
            {
                if (c.IsAngleBlock && c.BlockMode != 1) continue;
                sb.Append(c.FirstSector).Append('-').Append(c.LastSector).Append(',');
            }
        }
        return sb.ToString();
    }

    /// <summary>The cells <paramref name="t"/> plays that <paramref name="original"/> doesn't, in play order (first angle only).</summary>
    private static List<DvdCellRef> OwnCells(DvdDisc disc, DvdTitleInfo t, DvdTitleInfo original)
    {
        var theirs = original.Pgcns
            .Select(p => disc.GetPgc(DvdDomain.Title, original.Vtsn, p))
            .Where(p => p is not null)
            .SelectMany(p => p!.Cells.Where(c => !c.IsAngleBlock || c.BlockMode == 1))
            .Select(c => (c.FirstSector, c.LastSector))
            .ToHashSet();
        var own = new List<DvdCellRef>();
        foreach (var pgcn in t.Pgcns)
        {
            var pgc = disc.GetPgc(DvdDomain.Title, t.Vtsn, pgcn);
            if (pgc is null) continue;
            for (var i = 0; i < pgc.Cells.Count; i++)
            {
                var c = pgc.Cells[i];
                if (c.IsAngleBlock && c.BlockMode != 1) continue;
                if (theirs.Contains((c.FirstSector, c.LastSector)) || own.Any(o => o.FirstSector == c.FirstSector && o.LastSector == c.LastSector)) continue;
                own.Add(new DvdCellRef(t.Vtsn, pgcn, i + 1, c.FirstSector, c.LastSector, c.Duration));
            }
        }
        return own;
    }

    /// <summary>Fraction of sectors the two titles have in common (by identical cells).</summary>
    private static double CellOverlap(DvdDisc disc, DvdTitleInfo a, DvdTitleInfo b)
    {
        HashSet<(uint, uint)> Cells(DvdTitleInfo t) => t.Pgcns
            .Select(p => disc.GetPgc(DvdDomain.Title, t.Vtsn, p))
            .Where(p => p is not null)
            .SelectMany(p => p!.Cells.Where(c => !c.IsAngleBlock || c.BlockMode == 1))
            .Select(c => (c.FirstSector, c.LastSector))
            .ToHashSet();
        var ca = Cells(a);
        var cb = Cells(b);
        long Size(IEnumerable<(uint F, uint L)> s) => s.Sum(c => (long)c.L - c.F + 1);
        var total = Math.Max(Size(ca), Size(cb));
        return total == 0 ? 0 : (double)Size(ca.Intersect(cb)) / total;
    }

    private static void MarkArrowReachability(MenuNode node)
    {
        var count = node.Buttons.Count;
        var seen = new bool[count + 1];
        var stack = new Stack<int>();
        stack.Push(node.InitialButton);
        if (node.Highlight.ForcedSelect > 0) stack.Push(node.Highlight.ForcedSelect);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n < 1 || n > count || seen[n]) continue;
            seen[n] = true;
            var b = node.Buttons[n - 1].Button;
            foreach (var next in new[] { b.Up, b.Down, b.Left, b.Right })
                if (next >= 1 && next <= count && !seen[next]) stack.Push(next);
        }
        for (var i = 1; i <= count; i++)
            node.Buttons[i - 1].ReachableByArrows = seen[i];
    }

    private static void BuildTitleInventory(DiscMap map, VmResult start)
    {
        var disc = map.Disc;
        foreach (var t in disc.Vmg.Titles)
        {
            if (!disc.TitleSets.TryGetValue(t.Vtsn, out var vts) || t.VtsTtn < 1 || t.VtsTtn > vts.Ptts.Count)
            {
                map.Warnings.Add(DvdWarningCode.MissingTitleSet, $"Title {t.TitleNumber} points at a missing title set or chapter table.");
                continue;
            }

            var ptts = vts.Ptts[t.VtsTtn - 1];
            var pgcOrder = new List<int>();
            foreach (var p in ptts)
                if (!pgcOrder.Contains(p.Pgcn)) pgcOrder.Add(p.Pgcn);

            var pgcStart = new Dictionary<int, TimeSpan>();
            var total = TimeSpan.Zero;
            foreach (var pg in pgcOrder)
            {
                pgcStart[pg] = total;
                total += disc.GetPgc(DvdDomain.Title, t.Vtsn, pg)?.Duration ?? TimeSpan.Zero;
            }

            var starts = ptts.Select(p =>
                pgcStart[p.Pgcn] + (disc.GetPgc(DvdDomain.Title, t.Vtsn, p.Pgcn)?.ProgramStart(p.Pgn) ?? TimeSpan.Zero)).ToList();

            map.Titles.Add(new DvdTitleInfo
            {
                Ttn = t.TitleNumber,
                Vtsn = t.Vtsn,
                VtsTtn = t.VtsTtn,
                ChapterCount = ptts.Count,
                AngleCount = t.AngleCount,
                Duration = total,
                ChapterStarts = starts,
                Pgcns = pgcOrder,
            });
        }

        var visible = new HashSet<int>();
        var hidden = new HashSet<int>();
        var auto = new HashSet<int>();

        void AddOrphan(TitleLanding l)
        {
            if (!map.OrphanPgcs.Any(o => o.Vtsn == l.Vtsn && o.Pgcn == l.Pgcn)) map.OrphanPgcs.Add(l);
        }

        foreach (var l in start.TitlesPassed)
        {
            if (l.Ttn == 0) { AddOrphan(l); continue; }
            auto.Add(l.Ttn);
            map.GetTitle(l.Ttn)?.Sources.Add("plays at disc start");
        }

        foreach (var node in map.Menus)
        {
            foreach (var bn in node.Buttons)
            {
                if (bn.Target.Kind != TargetKind.Title || bn.Target.Landing is not { } l) continue;
                if (l.Ttn == 0) { AddOrphan(l); continue; }
                var info = map.GetTitle(l.Ttn);
                if (info is null) continue;
                info.Sources.Add($"{node.Name} button {bn.Button.Number}" + (bn.IsHidden ? " (hidden)" : ""));
                if (l.Chapter > 0) info.TargetedChapters.Add(l.Chapter);
                (bn.IsHidden ? hidden : visible).Add(l.Ttn);

                foreach (var p in bn.Target.PassedThrough)
                {
                    if (p.Ttn == 0) { AddOrphan(p); continue; }
                    auto.Add(p.Ttn);
                    map.GetTitle(p.Ttn)?.Sources.Add($"plays before title {l.Ttn}");
                }
            }
        }

        foreach (var t in map.Titles)
        {
            t.Reach = visible.Contains(t.Ttn) ? Reachability.OnMenu
                : hidden.Contains(t.Ttn) ? Reachability.HiddenButton
                : auto.Contains(t.Ttn) ? Reachability.OffMenu
                : Reachability.Unreferenced;

            // Same video = same cells on disc, however the navigation wraps them (alternate
            // chapter lists, "white rabbit" modes, commentary entry points).
            var sig = ContentSignature(disc, t);
            var first = map.Titles.FirstOrDefault(o => o.Ttn < t.Ttn && o.Vtsn == t.Vtsn && ContentSignature(disc, o) == sig)
                        ?? map.Titles.FirstOrDefault(o => o.Ttn < t.Ttn && o.Vtsn == t.Vtsn && o.DuplicateOf is null &&
                                                          (o.Duration - t.Duration).Duration() <= TimeSpan.FromSeconds(2) &&
                                                          CellOverlap(disc, o, t) >= 0.9);
            if (first is not null && sig.Length > 0)
            {
                t.DuplicateOf = first.DuplicateOf ?? first.Ttn;
                // Not the same cells: the ones only this title plays.
                if (ContentSignature(disc, map.GetTitle(t.DuplicateOf.Value)!) != sig)
                    t.OwnCells = OwnCells(disc, t, map.GetTitle(t.DuplicateOf.Value)!);
            }

            if (t.Duration < TimeSpan.FromSeconds(3))
                t.JunkReason = "shorter than 3 seconds";
        }
    }
}
