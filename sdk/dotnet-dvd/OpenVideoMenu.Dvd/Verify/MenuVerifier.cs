using System;
using System.Collections.Generic;
using System.Linq;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Verify;

/// <summary>What checking a disc's exported menus against the disc found.</summary>
public sealed class MenuVerifyReport
{
    public int Menus { get; set; }
    public int Buttons { get; set; }
    public int Steps { get; set; }
    public int Matches { get; set; }
    public int Mismatches => Problems.Count;
    public int ActionNodes { get; set; }
    public long ManifestBytes { get; set; }
    public bool UsesRandom { get; set; }
    public List<string> Problems { get; } = new();
    public List<string> ValidationErrors { get; } = new();
    public DvdWarnings Warnings { get; } = new();
    public HashSet<string> MenusVisited { get; } = new();
    public HashSet<string> MenusReachedByDisc { get; } = new();
}

/// <summary>
/// Checks an export by playing it against the disc: the disc's own simulation (<see cref="DvdVm"/>) and the
/// format's reference navigator (<see cref="Navigator"/> over the manifest) get the same button presses,
/// timeouts and playback ends, and after each step they must be on the same menu (same highlighted button,
/// same register values) or playing the same video. Every title counts as ripped whole, so this checks
/// the menus' logic, not what was queued.
/// </summary>
public static class MenuVerifier
{
    private static readonly IReadOnlyDictionary<string, Scalar> Player = new Dictionary<string, Scalar>
    {
        ["@menuLanguage"] = "en",
        ["@audioLanguage"] = "en",
        ["@subtitleLanguage"] = "en",
        ["@region"] = 1,
        ["@country"] = "US",
        ["@parentalLevel"] = 8,
        ["@aspect"] = "16:9",
    };

    /// <summary>Every title of the disc as a whole file, the way a full rip would have them.</summary>
    public static (Dictionary<string, MediaEntry> Media, CellMediaIndex Index, List<MediaCells> Files) WholeDisc(DvdDisc disc)
    {
        // Title-domain PGCs no title lists count too: an exporter cuts those from the disc itself.
        var set = DvdMedia.OneFilePerTitle(disc, t => $"t{t}.mkv", (v, p) => $"v{v}p{p}.mkv");
        return (set.Media, set.Index, set.Files.ToList());
    }

    /// <summary>The walk trace of the run in progress on this thread (Run is synchronous).</summary>
    [ThreadStatic] private static Action<string>? _walkTrace;

    /// <summary>The navigator under test in the run in progress on this thread (default: the SDK's).</summary>
    [ThreadStatic] private static NavigatorStep? _step;

    private static (NavState State, IReadOnlyList<Effect> Effects) Step(Manifest manifest, NavState state, NavEvent e) =>
        _step is { } step ? step(manifest, state, e) : Navigator.Step(manifest, state, e);

    /// <param name="menuLanguage">The viewer's menu language (ISO 639-1); both sides start with it.</param>
    public static MenuVerifyReport Run(DvdDisc disc, DiscMap? map = null, int walks = 300, int depth = 25, int seed = 7, string menuLanguage = "en") =>
        Run(disc, map, new MenuVerifyOptions { Walks = walks, Depth = depth, Seed = seed, MenuLanguage = menuLanguage });

    /// <summary>
    /// Exports the whole disc (every title as its own virtual file) and plays random walks on the disc's own
    /// machine and on the export's navigator side by side, reporting where they first differ.
    /// </summary>
    public static MenuVerifyReport Run(DvdDisc disc, DiscMap? map, MenuVerifyOptions options)
    {
        _walkTrace = options.WalkTrace;
        _step = options.Navigator;
        try { return RunWalks(disc, map, options); }
        finally { _walkTrace = null; _step = null; }
    }

    private static MenuVerifyReport RunWalks(DvdDisc disc, DiscMap? map, MenuVerifyOptions options)
    {
        var (walks, depth, seed, menuLanguage) = (options.Walks, options.Depth, options.Seed, options.MenuLanguage);
        var player = new Dictionary<string, Scalar>(Player) { ["@menuLanguage"] = menuLanguage };
        var language = (ushort)(char.ToLowerInvariant(menuLanguage[0]) << 8 | char.ToLowerInvariant(menuLanguage[1]));
        var report = new MenuVerifyReport();
        Manifest manifest;
        CellMediaIndex index;
        IReadOnlyList<MediaCells> files;
        Dictionary<string, MenuCellKey> keyOf;
        if (options.Export is { } given)
        {
            // Another exporter's manifest: its menus say which disc cell they are (x-dvd, or the caller's mapping).
            (manifest, index, files) = (given.Manifest, given.Media.Index, given.Media.Files);
            keyOf = new Dictionary<string, MenuCellKey>();
            foreach (var (id, menu) in manifest.Menus ?? new())
                if ((given.MenuCellOf?.Invoke(id) ?? CellFromExtension(disc, menu)) is { } key) keyOf[id] = key;
        }
        else
        {
            var (media, wholeIndex, wholeFiles) = WholeDisc(disc);
            var builder = new MenuManifestBuilder(disc, map, media, wholeIndex, new DvdManifestOptions { TitleButtonVobus = 3, CompileTrace = options.CompileTrace });
            manifest = builder.Build();
            report.Warnings.AddRange(builder.Warnings);
            (index, files) = (wholeIndex, wholeFiles);
            keyOf = builder.Compiler.Menus.Values.ToDictionary(m => m.Id, m => m.Key);
        }
        var check = manifest.Validate();
        report.ValidationErrors.AddRange(check.Errors.Select(e => $"{e.Path}: {e.Message}"));
        report.Menus = manifest.Menus?.Count ?? 0;
        report.Buttons = manifest.Menus?.Values.Sum(m => m.Buttons.Count) ?? 0;
        var compact = System.Text.Json.JsonSerializer.Serialize(manifest, DvdMenuCompiler.DeepJson);
        report.ManifestBytes = compact.Length;
        report.UsesRandom = compact.Contains("\"random\"");
        if (!check.Ok || manifest.Menus is null && manifest.FirstPlay is null) return report;

        var cellsOf = files.ToDictionary(f => f.Id);
        var vm = new DvdVm(disc, options.Player) { StopsAtTitleButtons = true, Observer = options.VmTrace };
        var rng = new Random(seed);

        for (var w = 0; w < walks; w++)
        {
            var trail = new List<string>();
            var dvd = vm.StartFromFirstPlay(stopAtTitles: true, menuLanguage: language);
            var (nav, effects) = Step(manifest, new NavState(), new NavEvent.Start(player, 1));
            Play? playing = Last(effects);
            (nav, playing) = Settle(manifest, nav, playing);

            for (var step = 0; step < depth; step++)
            {
                // Titles the disc plays one after another inside one exported play (several PGCs of one title) are one playback.
                while (dvd.Kind == VmStopKind.Title && nav.Mode == NavMode.Playing && playing is not null &&
                       !Inside(disc, dvd.State, playing, cellsOf, index, after: false) && Inside(disc, dvd.State, playing, cellsOf, index, after: true))
                    dvd = vm.ContinueAfterTitle(dvd.State);
                // Titles with no running time (dispatchers some discs play on the way) show nothing: on through them.
                for (var hop = 0; hop < 20 && dvd.Kind == VmStopKind.Title && dvd.Landing!.PgcDuration < TimeSpan.FromSeconds(0.5) &&
                                  !(nav.Mode == NavMode.Playing && playing is not null && Inside(disc, dvd.State, playing, cellsOf, index, after: false)); hop++)
                    dvd = vm.ContinueAfterTitle(dvd.State);
                // The same on the export's side: a play of a moment's black on the way is played through.
                for (var hop = 0; hop < 20 && nav.Mode == NavMode.Playing && playing is not null && Seconds(playing, cellsOf, index) < 1.0 &&
                                  !(dvd.Kind == VmStopKind.Title && Inside(disc, dvd.State, playing, cellsOf, index, after: false)) &&
                                  !(dvd.Kind == VmStopKind.Menu && dvd.State.Domain == DvdDomain.Title &&
                                    (Inside(disc, dvd.State, playing, cellsOf, index, after: false) || Inside(disc, dvd.State, playing, cellsOf, index, after: true))); hop++)
                    (nav, playing) = PlayToEnd(manifest, nav, playing);

                if (dvd.Kind == VmStopKind.Menu && dvd.State.Domain == DvdDomain.Title && nav.Mode == NavMode.Playing && playing is not null &&
                    HoldTime(disc, dvd.State, playing, index) is { } holdAt)
                    (nav, playing) = Advance(manifest, nav, playing, holdAt);

                report.Steps++;
                if (_walkTrace is { } walkTrace1)
                    walkTrace1($"  [{w}:{step}] ({(trail.Count > 0 ? trail[^1] : "start")}) disc {dvd} g=[{string.Join(",", dvd.State.Gprm)}] s1={dvd.State.GetSprm(1)} s2={dvd.State.GetSprm(2)} | export {Describe(nav, playing)} " +
                                            string.Join(" ", nav.Vars.Where(v => v.Key.StartsWith('g') || v.Key.StartsWith('s')).OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}")));
                var problem = Compare(vm, disc, manifest, keyOf, cellsOf, index, dvd, nav, playing);
                if (problem is not null)
                {
                    if (report.Problems.Count < 40) report.Problems.Add($"{problem}\n      after: {string.Join(" > ", trail.DefaultIfEmpty("start"))}");
                    else report.Problems.Add(problem);
                    break;
                }
                report.Matches++;

                // Now and then the remote's Menu or Title key, on a menu or as a title starts (the disc's MenuCall).
                if (dvd.Kind is VmStopKind.Menu or VmStopKind.Title && rng.Next(10) == 0)
                {
                    var title = rng.Next(3) == 0;
                    trail.Add($"{(title ? "Title" : "Menu")} key{(dvd.Kind == VmStopKind.Title ? $" during {playing?.Media}" : $" on {nav.Menu}")}");
                    if (_walkTrace is { } walkTrace2)
                        walkTrace2($"  key: disc {dvd} g=[{string.Join(",", dvd.State.Gprm)}] s4-7={dvd.State.GetSprm(4)},{dvd.State.GetSprm(5)},{dvd.State.GetSprm(6)},{dvd.State.GetSprm(7)} rsm={dvd.State.Resume} | export {Describe(nav, playing)} " +
                                                string.Join(" ", nav.Vars.Where(v => v.Key.StartsWith('g')).OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}")));
                    var pressed = title ? vm.TitleCall(dvd.State) : vm.MenuCall(dvd.State);
                    if (pressed.Kind != VmStopKind.NoNavigation) dvd = pressed;
                    (nav, effects) = Step(manifest, nav,
                        new NavEvent.Press(title ? Key.Title : Key.Menu, nav.Mode == NavMode.Playing ? playing?.Start : null));
                    playing = Last(effects) ?? (nav.Mode == NavMode.Playing ? playing : null);
                    (nav, playing) = Settle(manifest, nav, playing);
                    continue;
                }

                if (dvd.Kind == VmStopKind.Menu && dvd.State.Domain == DvdDomain.Title)
                {
                    var buttons = dvd.Highlight!.Buttons;
                    var b = buttons[rng.Next(buttons.Count)];
                    trail.Add($"{playing?.Media} overlay b{b.Number}");
                    var held = dvd;
                    dvd = vm.ActivateButton(dvd.State, b);
                    if (dvd.Kind == VmStopKind.NoNavigation) dvd = new VmResult { Kind = VmStopKind.Menu, State = dvd.State, Highlight = held.Highlight };
                    (nav, effects) = Step(manifest, nav, new NavEvent.Activate($"b{b.Number}"));
                    if (_walkTrace is { } walkTrace3) walkTrace3($"    activate b{b.Number}: {string.Join(", ", effects)}");
                    playing = Last(effects) ?? (nav.Mode == NavMode.Playing ? Seeked(playing, effects) : null);
                    (nav, playing) = Settle(manifest, nav, playing);
                    continue;
                }

                if (dvd.Kind == VmStopKind.Menu)
                {
                    var key = new MenuCellKey(dvd.State.Domain, dvd.State.Vtsn, dvd.State.Pgcn, dvd.State.Cell, vm.MenuUnitOf(dvd.State));
                    report.MenusVisited.Add(nav.Menu!);
                    var menu = manifest.Menus![nav.Menu!];
                    var timer = menu.Timeout is not null && rng.Next(8) == 0;
                    if (timer)
                    {
                        trail.Add($"wait on {nav.Menu}");
                        dvd = AfterStill(vm, dvd);
                        (nav, effects) = Step(manifest, nav, new NavEvent.Timeout(nav.Menu!));
                    }
                    else
                    {
                        var buttons = dvd.Highlight!.Buttons;
                        var b = buttons[rng.Next(buttons.Count)];
                        trail.Add($"{nav.Menu} b{b.Number}");
                        dvd = vm.ActivateButton(dvd.State, b);
                        if (dvd.Kind == VmStopKind.NoNavigation) dvd = StayOnMenu(vm, disc, dvd, key);
                        (nav, effects) = Step(manifest, nav, new NavEvent.Activate($"b{b.Number}"));
                    }
                    playing = Last(effects) ?? (nav.Mode == NavMode.Playing ? playing : null);
                    (nav, playing) = Settle(manifest, nav, playing);
                }
                else if (dvd.Kind == VmStopKind.Title)
                {
                    trail.Add($"play {(playing is null ? "?" : playing.Media)} to its end");
                    var finished = dvd.State;
                    // The disc goes through every title the export's play holds (a composite play-all) before it's over.
                    dvd = vm.ContinueAfterTitle(dvd.State);
                    for (var hop = 0; hop < 200 && dvd.Kind == VmStopKind.Title && playing is not null && Inside(disc, dvd.State, playing, cellsOf, index, after: true); hop++)
                        dvd = vm.ContinueAfterTitle(dvd.State);
                    // The disc stops partway, at a cell that waits for one of its buttons: the export plays up to
                    // there (the next step), not to its end.
                    if (dvd.Kind == VmStopKind.Menu && dvd.State.Domain == DvdDomain.Title && playing is not null && InPgc(disc, playing, index, dvd.State) &&
                        (Inside(disc, dvd.State, playing, cellsOf, index, after: false) || Inside(disc, dvd.State, playing, cellsOf, index, after: true)))
                        continue;
                    (nav, playing) = PlayToEnd(manifest, nav, playing!);
                    // The export plays one disc title in pieces (a cell's condition decided where it is): the pieces
                    // still in the title the disc has played through are part of the same playback.
                    for (var hop = 0; hop < 50 && nav.Mode == NavMode.Playing && playing is not null && InPgc(disc, playing, index, finished) &&
                                      !(dvd.Kind == VmStopKind.Title && Inside(disc, dvd.State, playing, cellsOf, index, after: false)); hop++)
                        (nav, playing) = PlayToEnd(manifest, nav, playing);
                }
                else break;
            }
        }
        report.ActionNodes = CountNodes(manifest);
        return report;
    }

    private sealed record Play(string Media, double Start, double? End, IReadOnlyList<CueWatch> Cues)
    {
        /// <summary>How far into the play (seconds from its start) the disc has been followed.</summary>
        public double Reached { get; set; }

        /// <summary>The media time the player has run the play's cues to (-1: none yet).</summary>
        public double CuesTo { get; set; } = -1;
    }

    private static Play? Last(IReadOnlyList<Effect> effects) =>
        effects.OfType<Effect.Play>().Select(p => new Play(p.Media, p.Start, p.End, p.Cues ?? Array.Empty<CueWatch>())).LastOrDefault();

    /// <summary>Lets transitions and menu intros finish, as a player would.</summary>
    private static (NavState, Play?) Settle(Manifest manifest, NavState nav, Play? playing)
    {
        for (var i = 0; i < 64; i++)
        {
            // A parental request is granted, as the disc's player (parental control off) grants it.
            NavEvent? e = nav.Parental is not null ? new NavEvent.Parental(true)
                // A clip without buttons plays to its end (its timeout), as the disc plays such a cell through.
                : nav.Mode == NavMode.Menu && nav.ButtonsReady && manifest.Menus?.GetValueOrDefault(nav.Menu!) is { Buttons.Count: 0, Timeout: not null } ? new NavEvent.Timeout(nav.Menu!)
                : nav.Mode == NavMode.Transition ? new NavEvent.TransitionEnded()
                : nav.Mode == NavMode.Menu && !nav.ButtonsReady ? new NavEvent.ButtonsReady()
                : null;
            if (e is null) break;
            var (next, effects) = Step(manifest, nav, e);
            nav = next;
            playing = Last(effects) ?? playing;
        }
        return (nav, nav.Mode == NavMode.Playing ? playing : null);
    }

    /// <summary>The player reaches each cue of what's playing, then its end.</summary>
    /// <summary>A menu's disc cell from its x-dvd extension (domain VMGM/VTSM, vtsn, pgcn, cell), in the disc's default language unit.</summary>
    private static MenuCellKey? CellFromExtension(DvdDisc disc, Menu menu)
    {
        if (menu.Extensions?.TryGetValue("x-dvd", out var x) != true || x.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        var domain = x.TryGetProperty("domain", out var d) && d.GetString() == "VMGM" ? DvdDomain.VmgMenu : DvdDomain.VtsMenu;
        var vtsn = x.TryGetProperty("vtsn", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;
        if (!x.TryGetProperty("pgcn", out var p) || !x.TryGetProperty("cell", out var c)) return null;
        return new MenuCellKey(domain, vtsn, p.GetInt32(), c.GetInt32(), disc.DefaultMenuUnit(domain, vtsn));
    }

    private static (NavState, Play?) PlayToEnd(Manifest manifest, NavState nav, Play playing)
    {
        foreach (var cue in playing.Cues.Where(c => c.At is not null && c.At > Math.Max(playing.Start, playing.CuesTo)).OrderBy(c => c.At))
        {
            var (next, effects) = Step(manifest, nav, new NavEvent.Cue(cue.Id));
            nav = next;
            if (Last(effects) is { } p) return Settle(manifest, nav, p);
            if (nav.Mode != NavMode.Playing) return Settle(manifest, nav, null);
        }
        var (after, fx) = Step(manifest, nav, new NavEvent.Ended());
        return Settle(manifest, after, Last(fx));
    }

    /// <summary>Whether a play starts with a cell of the title PGC the disc state was playing.</summary>
    private static bool InPgc(DvdDisc disc, Play playing, CellMediaIndex index, VmState s)
    {
        if (s.Domain != DvdDomain.Title || index.CellAt(playing.Media, playing.Start + 0.01) is not { } at || at.Vtsn != s.Vtsn) return false;
        return disc.GetPgc(DvdDomain.Title, s.Vtsn, s.Pgcn)?.Cells.Any(c => c.FirstSector == at.First && c.LastSector == at.Last) == true;
    }

    /// <summary>The playback goes on from where a seek put it (a disc's link within the title playing).</summary>
    private static Play? Seeked(Play? playing, IReadOnlyList<Effect> effects)
    {
        if (playing is not null && effects.OfType<Effect.Seek>().LastOrDefault() is { } seek)
        {
            // Back before where the play started (a "previous chapter" button on a chapter it started at): the
            // play now runs from there.
            if (seek.Position < playing.Start) playing = playing with { Start = seek.Position };
            playing.Reached = Math.Max(0, seek.Position - playing.Start);
            playing.CuesTo = seek.Position;
        }
        return playing;
    }

    /// <summary>Where on the play's media the disc's held cell holds (its last frame).</summary>
    private static double? HoldTime(DvdDisc disc, VmState s, Play playing, CellMediaIndex index)
    {
        var cell = disc.GetPgc(DvdDomain.Title, s.Vtsn, s.Pgcn)?.Cells.ElementAtOrDefault(s.Cell - 1);
        if (cell is null || index.StartOf(playing.Media, s.Vtsn, cell.FirstSector, cell.LastSector) is not { } start) return null;
        return start + Math.Max(0, cell.Duration.TotalSeconds - 0.04);
    }

    /// <summary>
    /// The player plays on to <paramref name="until"/>: the point cues on the way run, and the overlay showing
    /// there appears. Stops early when a cue leaves the playback.
    /// </summary>
    private static (NavState, Play?) Advance(Manifest manifest, NavState nav, Play playing, double until)
    {
        foreach (var cue in playing.Cues.Where(c => c.At is { } at && at > Math.Max(playing.Start, playing.CuesTo) && at < until - 0.01).OrderBy(c => c.At))
        {
            var (next, effects) = Step(manifest, nav, new NavEvent.Cue(cue.Id));
            nav = next;
            if (Last(effects) is { } p) return Settle(manifest, nav, p);
            if (nav.Mode != NavMode.Playing) return Settle(manifest, nav, null);
            if (effects.OfType<Effect.Seek>().Any()) return (nav, Seeked(playing, effects));
        }
        playing.CuesTo = until;
        if (playing.Cues.LastOrDefault(c => c.From is { } from && from <= until + 0.0005 && (c.To is not { } to || to > until)) is { } overlay)
            (nav, _) = Step(manifest, nav, new NavEvent.Cue(overlay.Id));
        if (_walkTrace is { } walkTrace4) walkTrace4($"    advance {playing.Media} to {until}: overlay {nav.Overlay}");
        return (nav, playing);
    }

    private static VmResult AfterStill(DvdVm vm, VmResult menu)
    {
        var r = vm.ContinueAfterStill(menu.State);
        return r;
    }

    /// <summary>A button that changes nothing but registers: the disc stays on its menu.</summary>
    private static VmResult StayOnMenu(DvdVm vm, DvdDisc disc, VmResult r, MenuCellKey key) =>
        new() { Kind = VmStopKind.Menu, State = r.State, Highlight = disc.GetHighlight(key.Domain, key.Vtsn, key.Pgcn, key.Cell, key.Unit) };

    private static string? Compare(DvdVm vm, DvdDisc disc, Manifest manifest, Dictionary<string, MenuCellKey> keyOf, Dictionary<string, MediaCells> cellsOf,
        CellMediaIndex index, VmResult dvd, NavState nav, Play? playing)
    {
        switch (dvd.Kind)
        {
            case VmStopKind.Menu when dvd.State.Domain == DvdDomain.Title:
                {
                    if (nav.Mode != NavMode.Playing || playing is null)
                        return $"disc waits on title buttons (vts{dvd.State.Vtsn} pgc{dvd.State.Pgcn} cell {dvd.State.Cell}), export is {Describe(nav, playing)}";
                    if (!Inside(disc, dvd.State, playing, cellsOf, index, after: false) && !Inside(disc, dvd.State, playing, cellsOf, index, after: true))
                        return $"disc waits on title buttons (vts{dvd.State.Vtsn} pgc{dvd.State.Pgcn} cell {dvd.State.Cell}), export plays {playing.Media} from {playing.Start:0.###}";
                    if (nav.Overlay is null)
                        return $"disc shows buttons over vts{dvd.State.Vtsn} pgc{dvd.State.Pgcn} cell {dvd.State.Cell}, export shows none over {playing.Media}";
                    for (var g = 0; g < 16; g++)
                        if (nav.Vars.TryGetValue($"g{g}", out var v) && v.Number != dvd.State.Gprm[g])
                            return $"title buttons in {playing.Media}: g{g} is {dvd.State.Gprm[g]} on the disc, {v} in the export";
                    return null;
                }
            case VmStopKind.Menu:
                {
                    var key = new MenuCellKey(dvd.State.Domain, dvd.State.Vtsn, dvd.State.Pgcn, dvd.State.Cell, vm.MenuUnitOf(dvd.State));
                    if (nav.Mode != NavMode.Menu)
                        return $"disc shows menu {key} (button {dvd.State.HighlightedButton}), export is {Describe(nav, playing)}";
                    if (!keyOf.TryGetValue(nav.Menu!, out var shown) || shown != key)
                        return $"disc shows menu {key}, export shows {nav.Menu}";
                    // (A forced selection is already in the state: it applies when the menu is entered, not after.)
                    var hl = dvd.State.HighlightedButton;
                    if (nav.Focus != $"b{hl}" && dvd.Highlight?.Get(hl) is not null)
                        return $"menu {nav.Menu}: disc highlights b{hl}, export b{nav.Focus?.TrimStart('b')} (s8={dvd.State.GetSprm(8)} forced={dvd.Highlight?.ForcedSelect} buttons={dvd.Highlight?.Buttons.Count})";
                    for (var g = 0; g < 16; g++)
                        if (nav.Vars.TryGetValue($"g{g}", out var v) && v.Number != dvd.State.Gprm[g])
                            return $"menu {nav.Menu}: g{g} is {dvd.State.Gprm[g]} on the disc, {v} in the export";
                    return null;
                }
            case VmStopKind.Title:
                {
                    if (nav.Mode != NavMode.Playing || playing is null)
                        return $"disc plays {dvd.Landing}, export is {Describe(nav, playing)}";
                    if (!Inside(disc, dvd.State, playing, cellsOf, index, after: false))
                        return $"disc plays {dvd.Landing} (cell {dvd.State.Cell}), export plays {playing.Media} from {playing.Start:0.###}";
                    return null;
                }
            case VmStopKind.Exit:
                // The disc stops; the export has nowhere to go and stays (a player's stop).
                return nav.Mode == NavMode.Playing ? $"disc stops, export plays {playing?.Media}" : null;
            default:
                return null;
        }
    }

    private static string Describe(NavState nav, Play? playing) => nav.Mode switch
    {
        NavMode.Menu => $"on menu {nav.Menu} ({nav.Focus})",
        NavMode.Playing => $"playing {playing?.Media} from {playing?.Start:0.###}",
        _ => nav.Mode.ToString(),
    };

    /// <summary>
    /// The disc's playback position is the export's: the cell is where the play starts (or, with
    /// <paramref name="after"/>, a later cell of the same play).
    /// </summary>
    private static bool Inside(DvdDisc disc, VmState s, Play playing, Dictionary<string, MediaCells> cellsOf, CellMediaIndex index, bool after)
    {
        var pgc = disc.GetPgc(DvdDomain.Title, s.Vtsn, s.Pgcn);
        if (pgc is null || s.Cell < 1 || s.Cell > pgc.Cells.Count) return false;
        var first = s.Cell;
        while (first > 1 && pgc.Cells[first - 1].IsAngleBlock && pgc.Cells[first - 1].BlockMode != 1) first--;
        var cell = pgc.Cells[first - 1];
        var target = (cell.FirstSector, cell.LastSector);

        // Composite plays: their segments' files, at their offsets.
        // Each part with where it starts on the play's own timeline (0 = where the play started).
        IEnumerable<(MediaCells File, double From, double? To, double At)> Parts()
        {
            if (cellsOf.TryGetValue(playing.Media, out var f)) { yield return (f, playing.Start, playing.End, 0); yield break; }
            if (index.Composites.TryGetValue(playing.Media, out var c) && c.Segments is { } segs)
            {
                var clock = 0.0;
                foreach (var seg in segs)
                {
                    var len = (seg.End ?? cellsOf[seg.Media].Duration) - (seg.Start ?? 0);
                    if (clock + len > playing.Start - 0.01)
                    {
                        var skip = Math.Max(0, playing.Start - clock);
                        yield return (cellsOf[seg.Media], (seg.Start ?? 0) + skip, seg.End, clock + skip - playing.Start);
                    }
                    clock += len;
                }
            }
        }

        foreach (var (file, from, to, at) in Parts())
        {
            if (file.Vtsn != s.Vtsn) { if (!after) return false; continue; }
            for (var i = 0; i < file.Cells.Count; i++)
            {
                if (file.Cells[i] != target) continue;
                var start = file.Starts[i];
                if (start < from - 0.01 || to is { } end && start >= end - 0.01) continue;
                var when = at + start - from;
                // Past where the play ends (a play of part of a composite, or of an alias of a whole file).
                if (playing.End is { } playEnd && playing.Start + when >= playEnd - 0.01) continue;
                if (!after && Math.Abs(when - playing.Reached) < 0.01) return true;
                // Later in the play than the disc has been followed so far (cells can repeat: the next one on).
                if (after && when > playing.Reached + 0.01)
                {
                    playing.Reached = when;
                    return true;
                }
            }
            if (!after) return false;
        }
        return false;
    }

    /// <summary>How long a play runs (from its start to its end or its media's end).</summary>
    private static double Seconds(Play p, Dictionary<string, MediaCells> cellsOf, CellMediaIndex index)
    {
        var length = cellsOf.TryGetValue(p.Media, out var f) ? f.Duration
            : index.Composites.TryGetValue(p.Media, out var c) ? c.Duration ?? 0 : 0;
        return (p.End ?? length) - p.Start;
    }

    private static int CountNodes(Manifest manifest)
    {
        var json = manifest.ToJson();
        var n = 0;
        for (var i = json.IndexOf("\"type\"", StringComparison.Ordinal); i >= 0; i = json.IndexOf("\"type\"", i + 6, StringComparison.Ordinal)) n++;
        return n;
    }
}

/// <summary>How a verification runs: how much it explores, and what it reports along the way.</summary>
public sealed class MenuVerifyOptions
{
    /// <summary>An export to check instead of the package's own (another exporter's); null: export the disc here.</summary>
    public VerifyExport? Export { get; init; }

    /// <summary>The navigator to check instead of the SDK's (another implementation of the format); null: the SDK's.</summary>
    public NavigatorStep? Navigator { get; init; }

    /// <summary>Random walks from first play.</summary>
    public int Walks { get; init; } = 300;

    /// <summary>Steps per walk (button presses, keys, timeouts, playbacks to their end).</summary>
    public int Depth { get; init; } = 25;

    /// <summary>The random seed: the same seed walks the same way.</summary>
    public int Seed { get; init; } = 7;

    /// <summary>The viewer's menu language (ISO 639-1); both sides start with it.</summary>
    public string MenuLanguage { get; init; } = "en";

    /// <summary>The DVD player the disc's side stands for (default: <see cref="DvdPlayerSettings.Default"/>).</summary>
    public DvdPlayerSettings? Player { get; init; }

    /// <summary>A line per walk step, comparing both sides; null for none.</summary>
    public Action<string>? WalkTrace { get; init; }

    /// <summary>Every step of the disc's machine; null for none.</summary>
    public Action<VmStep>? VmTrace { get; init; }

    /// <summary>A line per step of the translation; null for none.</summary>
    public Action<string>? CompileTrace { get; init; }
}

/// <summary>A navigator under test: the same contract as <see cref="Navigator.Step"/>.</summary>
public delegate (NavState State, IReadOnlyList<Effect> Effects) NavigatorStep(Manifest manifest, NavState state, NavEvent navEvent);

/// <summary>An export to check that another exporter made: the manifest, and which disc cells its media hold.</summary>
/// <param name="Media">The manifest's media and where each disc cell is in them (the walks follow playback through it).</param>
/// <param name="MenuCellOf">Which disc menu cell a manifest menu is; default: the menu's <c>x-dvd</c> extension (domain, vtsn, pgcn, cell).</param>
public sealed record VerifyExport(Manifest Manifest, DvdMediaSet Media, Func<string, MenuCellKey?>? MenuCellOf = null);
