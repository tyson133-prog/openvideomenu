using System;
using System.Collections.Generic;
using System.Linq;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>A menu cell with buttons: one manifest menu. <see cref="Unit"/> is the menu language unit (0-based).</summary>
public readonly record struct MenuCellKey(DvdDomain Domain, int Vtsn, int Pgcn, int Cell, int Unit = 0)
{
    public override string ToString() => (Domain == DvdDomain.VmgMenu ? "vmgm" : $"vts{Vtsn}") + $"-u{Unit}-pgc{Pgcn}-c{Cell}";
}

/// <summary>What the compiler made of one menu cell.</summary>
public sealed class CompiledMenu
{
    public MenuCellKey Key { get; init; }
    public string Id { get; set; } = "";
    public HighlightInfo Highlight { get; init; } = new();
    public VideoAttributes Video { get; init; } = new();
    public MenuType MenuType { get; init; }

    /// <summary>Button number → its action.</summary>
    public Dictionary<int, MenuAction> Buttons { get; } = new();

    /// <summary>What happens when the menu is left alone; null when it waits (or only loops).</summary>
    public MenuAction? Timeout { get; set; }

    public double TimeoutAfter { get; set; }

    /// <summary>The PGC's Go Up (the remote's Return), compiled; null without one.</summary>
    public MenuAction? GoUp { get; set; }

    /// <summary>The menu runs on into another cell with the same buttons by itself (a motion intro, then its loop).</summary>
    public MenuCellKey? LoopsInto { get; set; }

    /// <summary>
    /// Times on the cell's clock when other buttons show than <see cref="Highlight"/> (the disc's highlight
    /// changes while the menu video plays), each with its buttons' actions. Outside them, the main buttons show.
    /// </summary>
    public List<MenuButtonWindow> Windows { get; } = new();

    /// <summary>A menu without buttons the disc never leaves by itself (it loops on nothing): no timeout.</summary>
    public bool Holds { get; set; }
}

public sealed record MenuButtonWindow(double From, double? To, HighlightInfo Highlight, Dictionary<int, MenuAction> Buttons);

public sealed class DvdCompileOptions
{
    /// <summary>Files keep the disc's audio/subtitle tracks in order, so stream choices set @audio/@subtitle.</summary>
    public bool TrackChoices { get; init; }

    /// <summary>A transition clip for button-less menu cells played before a menu (Full level); null = none.</summary>
    public Func<IReadOnlyList<MenuCellRef>, string?> Transition { get; init; } = _ => null;

    /// <summary>Menu clips on the way to a menu are exported (as its transition): only then are they followed.</summary>
    public bool Transitions { get; init; }

    /// <summary>
    /// How many of a title cell's VOBUs to read for buttons over the film (null = all, as an export does; a
    /// check samples, since the disc's simulation doesn't play those buttons anyway).
    /// </summary>
    public int? TitleButtonVobus { get; init; }

    /// <summary>Runs of titles that weren't ripped and are shorter than this are skipped (warnings, logos).</summary>
    public double SkipUnrippedBelow { get; init; } = 300;

    /// <summary>Instruction budget per compiled action (a button, a timeout, first play).</summary>
    public int StepBudget { get; init; } = 300_000;

    /// <summary>Fork budget per compiled action: conditions on values only known at playback.</summary>
    public int ForkBudget { get; init; } = 6_000;

    /// <summary>Called with a line for each step of the translation (debugging a disc); null for none.</summary>
    public Action<string>? Trace { get; init; }
}

/// <summary>
/// Translates a disc's navigation programs (first play, PGC pre/post/cell commands, button commands)
/// into Open Video Menu actions: registers become variables, conditions become <c>if</c>, register
/// commands <c>set</c>, and the disc's links the menus and playback they lead to (spec §6.1, DVD
/// profile). It runs the programs symbolically: what's known from the program itself is worked out here,
/// what depends on the viewer's choices (register values at playback) is left to the player as logic.
/// Title playback is followed through the cells the disc plays, which map onto the ripped files by sector
/// ranges (<see cref="IPlaybackMapper"/>); commands that run during a title become cues, and where it goes
/// when it ends the play's <c>after</c>.
/// </summary>
public sealed class DvdMenuCompiler
{
    private readonly DvdDisc _disc;
    private readonly IPlaybackMapper _media;
    private readonly DvdCompileOptions _options;
    private readonly Dictionary<MenuCellKey, CompiledMenu> _menus = new();
    private readonly Queue<CompiledMenu> _pending = new();
    private readonly HashSet<int> _sprmsRead = new();
    private readonly HashSet<string> _vars = new();
    private Shared _shared = new();

    public IReadOnlyDictionary<MenuCellKey, CompiledMenu> Menus => _menus;

    /// <summary>What first play does (with the player-state set-up at its start); null = straight to the root menu.</summary>
    public MenuAction? FirstPlay { get; private set; }

    /// <summary>The menu first play reaches, if it reaches one unconditionally.</summary>
    public MenuCellKey? RootMenu { get; private set; }

    /// <summary>
    /// Named actions (spec §6.4): each PGC entry outside title playback is compiled once and run by name
    /// from wherever the disc links to it, as the disc's own programs are shared.
    /// </summary>
    public Dictionary<string, MenuAction> Actions { get; } = new();

    /// <summary>Registers the disc puts in counter mode (they count seconds by themselves).</summary>
    public SortedSet<string> Counters { get; } = new(StringComparer.Ordinal);

    /// <summary>Variables the actions use, with their starting values.</summary>
    public Dictionary<string, Scalar> Vars { get; } = new();

    public DvdWarnings Warnings { get; } = new();

    /// <summary>Plays that found no ripped file and were left out (the button does nothing).</summary>
    public int UnavailablePlays { get; private set; }

    /// <summary>Titles some navigation plays that no ripped file holds.</summary>
    public SortedSet<int> UnrippedTitles { get; } = new();

    /// <summary>Title-domain PGCs no title lists (clips only navigation plays) that no file holds.</summary>
    public SortedSet<(int Vtsn, int Pgcn)> UnrippedOrphans { get; } = new();

    /// <summary>Unripped titles that left a button or a path with nothing to play (not just a skipped warning).</summary>
    public SortedSet<int> MissingTitles { get; } = new();

    /// <summary>Conditions moved to the start of playback although a register they read changes during it.</summary>
    public int ApproximatedConditions { get; private set; }

    public int BudgetCuts { get; private set; }

    public DvdMenuCompiler(DvdDisc disc, IPlaybackMapper media, DvdCompileOptions? options = null)
    {
        _disc = disc;
        _media = media;
        _options = options ?? new DvdCompileOptions();
        FindSprmReads();
    }

    // ================================================================== entry points

    /// <summary>Compiles first play and every menu it can lead to.</summary>
    public void Compile()
    {
        var fp = Root();
        fp.Domain = DvdDomain.FirstPlay;
        fp.Pgcn = 1;
        fp.Phase = Phase.EnterPgc;
        // At the start everything is as a player resets it: registers 0, system registers at their
        // defaults, except what comes from the viewer's setup (languages, region, parental level, TV shape).
        var reset = new VmState();
        for (var reg = 0; reg < 16; reg++) fp.G[reg] = One(0);
        for (var s = 0; s < 24; s++)
            if (!PlayerSprms.Contains(s) && s != 8) fp.S[s] = One(reset.GetSprm(s));
        var body = Finish(Follow(fp));
        RootMenu = FirstGoto(body) is { } g ? _menus.Values.FirstOrDefault(m => m.Id == g.Menu)?.Key : null;

        // The remote's Title and Menu keys reach the title menu and each title set's root menu; when first
        // play shows no menu (it plays the film), those are where the viewer lands.
        if (RootMenu is null)
            foreach (var (domain, vtsn, type) in MenuKeyEntries())
            {
                var m = Root();
                m.Domain = domain;
                m.Vtsn = vtsn;
                m.Unit = _disc.DefaultMenuUnit(domain, vtsn);
                m.Pgcn = _disc.FindMenuPgc(domain, vtsn, type, m.Unit);
                m.Phase = Phase.EnterPgc;
                if (FirstGoto(Finish(Follow(m))) is { } g2) { RootMenu = _menus.Values.First(x => x.Id == g2.Menu).Key; break; }
            }

        RegisterKeys();
        while (_pending.Count > 0 || _pendingNamed.Count > 0 || _pendingOverlays.Count > 0 || _pendingResumes.Count > 0 || _pendingKeys.Count > 0)
        {
            if (_pendingKeys.Count > 0 && _pending.Count == 0 && _pendingNamed.Count == 0 && _pendingOverlays.Count == 0 && _pendingResumes.Count == 0)
            {
                CompileKey(_pendingKeys.Dequeue());
                continue;
            }
            if (_pending.Count > 0) CompileMenu(_pending.Dequeue());
            else if (_pendingNamed.Count > 0) CompileNamed(_pendingNamed.Dequeue());
            else if (_pendingOverlays.Count > 0) CompileOverlayButton(_pendingOverlays.Dequeue());
            else CompileResumeAfter(_pendingResumes.Dequeue());
        }

        var init = PlayerSetup();
        var steps = new List<MenuAction>(init);
        if (body is not null) steps.Add(body);
        FirstPlay = init.Count == 0 && body is GotoAction { Transition: null } only && RootMenu is { } r && _menus[r].Id == only.Menu
            ? null
            : Normalize(new DoAction { Steps = steps }, after: false);
        foreach (var v in _vars.OrderBy(v => v, StringComparer.Ordinal)) Vars[v] = StartValue(v);
    }

    /// <summary>A shared entry point: a PGC entered with a given highlight state (the only path state it keeps).</summary>
    private sealed class NamedEntry
    {
        public string Name = "";
        public DvdDomain Domain;
        public int Vtsn;
        public int Unit;
        public int Pgcn;
        public int Pgn;
        public HlKind Hl;
        public List<MenuCellRef> LeadIn = new();

        /// <summary>Where the action starts: entering the PGC (pre commands), at a program or cell of it, or a line of its commands.</summary>
        public Phase Start = Phase.EnterPgc;
        public int Cell;
        public int Line = 1;
    }

    private readonly Dictionary<string, NamedEntry> _named = new();
    private readonly Queue<NamedEntry> _pendingNamed = new();

    /// <summary>
    /// A link to a program or cell of the same title PGC, outside playback, that many paths make (a gallery or a
    /// play-all stepping through a PGC's programs with a counter): from there the path runs a shared action,
    /// which links to itself as the loop goes on, instead of unrolling every pass.
    /// </summary>
    private bool SharedLinkTarget(M m)
    {
        if (!m.LinkedInPgc) return false;
        m.LinkedInPgc = false;
        if (m.Domain != DvdDomain.Title || m.Run is not null || m.Inline) return false;
        var key = (m.Vtsn, m.Pgcn, m.Phase == Phase.PlayCell ? -m.Cell : m.Pgn);
        var n = _linkTargets.GetValueOrDefault(key);
        _linkTargets[key] = n + 1;
        if (n < MaxInlineTitleEntries) return false;
        if (m.Joins.Count > 0 && m.Joins.FirstOrDefault(j => j.Matches(m)) is { } meets)
        {
            meets.Arrived.Add(m);
            return true;
        }
        if (m.Dry is { } dry)
        {
            dry.Add(PointOf(m));
            return true;
        }
        m.Out.Add(new RunAction { Action = NamedFor(m) });
        return true;
    }

    private readonly Dictionary<(int, int, int), int> _linkTargets = new();

    private readonly Dictionary<(int, int), bool> _titlePlaysFirst = new();

    /// <summary>The title PGC has cells, and nothing in its pre commands can leave it before they play.</summary>
    private bool TitlePlaysFirst(int vtsn, int pgcn)
    {
        if (_titlePlaysFirst.TryGetValue((vtsn, pgcn), out var known)) return known;
        var pgc = _disc.GetPgc(DvdDomain.Title, vtsn, pgcn);
        var plays = pgc is { CellCount: > 0, ProgramCount: > 0 } &&
                    pgc.PreCommands.Select(DvdCommands.Decode).All(i => !i.Before.Concat(i.Guarded).Concat(i.After).Any(op => op is LinkOp)) &&
                    pgc.PreCommands.All(c => !DvdCommands.Disassemble(c).Contains("Exit", StringComparison.Ordinal));
        _titlePlaysFirst[(vtsn, pgcn)] = plays;
        return plays;
    }

    /// <summary>A title PGC entered by this many plays still going inline is entered through its shared action after that.</summary>
    private const int MaxInlineTitleEntries = 0;

    private readonly Dictionary<(int Vtsn, int Pgcn), int> _inlineTitleEntries = new();

    /// <summary>How many plays (and probes for where branches meet) have gone on into this title PGC.</summary>
    private int InlineTitleEntries(M m)
    {
        var key = (m.Vtsn, m.Pgcn);
        var n = _inlineTitleEntries.GetValueOrDefault(key);
        _inlineTitleEntries[key] = n + 1;
        return n;
    }

    /// <summary>Whether a path entering a PGC runs the shared action instead of carrying on itself.</summary>
    private static bool Shares(M m) => !m.NamedStart && m.Run is null && !m.Inline;

    /// <summary>The shared action for where a path enters, queued for compiling the first time.</summary>
    private string NamedFor(M m)
    {
        // A highlight the path set goes to the action in the s8 register, so there's one action per entry
        // whatever the button (and one for entries that keep the viewer's highlight).
        if (m.Hl == HlKind.Known && Single(m.S[8]) is { } known)
        {
            m.Out.Add(Set(Use("s8"), known));
            m.Hl = HlKind.Variable;
        }
        var hl = m.Hl == HlKind.Focus ? HlKind.Focus : HlKind.Variable;
        // A title PGC that plays its cells before it can go anywhere drops the menu clips on the way (playback
        // starts): one action for every way in.
        if (m.Domain == DvdDomain.Title && m.Phase is not Phase.Post && TitlePlaysFirst(m.Vtsn, m.Pgcn)) m.LeadIn.Clear();
        var leadIn = string.Join(",", m.LeadIn.Select(c => $"{c.Domain}.{c.Vtsn}.{c.Pgcn}.{c.Cell}.{c.Unit}"));
        var start = m.Phase is Phase.PlayProgram or Phase.PlayCell or Phase.Post ? m.Phase
            : m.Phase == Phase.Pre && m.Line > 1 ? Phase.Pre : Phase.EnterPgc;
        var line = start is Phase.Pre or Phase.Post ? m.Line : 0;
        var key = $"{m.Domain}:{m.Vtsn}:{m.Unit}:{m.Pgcn}:{m.Pgn}:{hl}:{leadIn}:{start}:{(start == Phase.PlayCell ? m.Cell : 0)}:{line}";
        if (_named.TryGetValue(key, out var e)) return e.Name;
        var name = (m.Domain switch
        {
            DvdDomain.FirstPlay => "fp",
            DvdDomain.VmgMenu => "vmgm",
            DvdDomain.Title => $"vts{m.Vtsn}-title",
            _ => $"vts{m.Vtsn}",
        }) + (m.Unit > 0 ? $"-u{m.Unit}" : "") + $"-pgc{m.Pgcn}" + (m.Pgn > 1 ? $"-pg{m.Pgn}" : "") +
            (start == Phase.PlayProgram ? "-play" : start == Phase.PlayCell ? $"-c{m.Cell}" : start == Phase.Post ? "-post" : start == Phase.Pre ? "-pre" : "") +
            (line > 1 ? $"-l{line}" : "") +
            (hl == HlKind.Variable ? "-hl" : "") + (m.LeadIn.Count > 0 ? "-clip" : "");
        var unique = name;
        for (var n = 2; _named.Values.Any(x => x.Name == unique); n++) unique = $"{name}-{n}";
        if (_options.Trace is { } trace1 && m.LeadIn.Count > 0) trace1($"  leadin {m.LeadIn.Count} {leadIn}");
        e = new NamedEntry { Name = unique, Domain = m.Domain, Vtsn = m.Vtsn, Unit = m.Unit, Pgcn = m.Pgcn, Pgn = m.Pgn, Hl = hl, LeadIn = new(m.LeadIn),
                             Start = start, Cell = start == Phase.PlayCell ? m.Cell : 0, Line = Math.Max(1, line) };
        _named[key] = e;
        _pendingNamed.Enqueue(e);
        return unique;
    }

    // ------------------------------------------------------------------ remote keys

    /// <summary>The remote's menu keys as the disc's MenuCall does them, one shared action per target menu.</summary>
    private readonly Dictionary<(DvdDomain Domain, int Vtsn, MenuType Type), string> _keyActions = new();
    private readonly Dictionary<(DvdDomain Domain, int Vtsn, MenuType Type), string> _titleKeyActions = new();
    private readonly Queue<(string Name, DvdDomain Domain, int Vtsn, MenuType Type, bool FromTitle)> _pendingKeys = new();

    /// <summary>
    /// Some title's commands set the highlight register (SetHL_BTNN), so the button a menu shows after the Menu
    /// key during a title is that register's, kept in s8, not the viewer's last focus.
    /// </summary>
    private bool TitleSetsHighlight => _titleSetsHighlight ??= _disc.TitleSets.Values.SelectMany(v => v.TitlePgcs)
        .Concat(MenuUnits().SelectMany(u => u.Item4.Pgcs))
        .SelectMany(p => p.PreCommands.Concat(p.PostCommands).Concat(p.CellCommands))
        .Any(c => DvdCommands.Disassemble(c) is var d && (d.Contains("SetHL_BTNN", StringComparison.Ordinal) || d.Contains("(button ", StringComparison.Ordinal)));
    private bool? _titleSetsHighlight;

    private string? KeyAction(DvdDomain domain, int vtsn, MenuType type, bool fromTitle = false)
    {
        if (_disc.FindMenuPgc(domain, vtsn, type, _disc.DefaultMenuUnit(domain, vtsn)) <= 0) return null;
        var table = fromTitle ? _titleKeyActions : _keyActions;
        if (table.TryGetValue((domain, vtsn, type), out var name)) return name;
        name = (domain == DvdDomain.VmgMenu ? $"key-vmgm-{type.ToString().ToLowerInvariant()}" : $"key-vts{vtsn}-{type.ToString().ToLowerInvariant()}") +
               (fromTitle ? "-title" : "");
        table[(domain, vtsn, type)] = name;
        _pendingKeys.Enqueue((name, domain, vtsn, type, fromTitle));
        return name;
    }

    private void RegisterKeys()
    {
        foreach (var fromTitle in new[] { false, true })
        {
            KeyAction(DvdDomain.VmgMenu, 0, MenuType.Title, fromTitle);
            foreach (var v in _disc.TitleSets.Keys)
                foreach (var type in new[] { MenuType.Root, MenuType.Audio, MenuType.Subpicture, MenuType.Angle, MenuType.Chapter })
                    KeyAction(DvdDomain.VtsMenu, v, type, fromTitle);
        }
    }

    /// <summary>A menu key's action: entering that menu (its language unit by the menu language), as MenuCall does.</summary>
    private void CompileKey((string Name, DvdDomain Domain, int Vtsn, MenuType Type, bool FromTitle) k)
    {
        var m = Root();
        m.Domain = k.Domain;
        m.Vtsn = k.Vtsn;
        // During a title the highlight register is what the title (or the last menu's button) left in s8, or
        // the button of the overlay showing (a menu made of title cells).
        if (k.FromTitle) m.Hl = HlKind.Variable;
        var top = m.Out;
        if (k.FromTitle) top.Add(OverlayFocusToS8());
        // From a title, the key is the disc's MenuCall: a call that leaves the title resumable from where it was.
        if (k.FromTitle) top.Add(new SaveResumeAction());
        if (EnterMenuDomain(m, k.Domain, k.Vtsn, unit => _disc.FindMenuPgc(k.Domain, k.Vtsn, k.Type, unit))) Go(m);
        Actions[k.Name] = Normalize(Finish(top), after: false) ?? Set(Use("_none"), 0);
    }

    private static RunAction? Run(string? name) => name is null ? null : new RunAction { Action = name };

    /// <summary>A key the disc ignores (no menu to open): it must do nothing, not the format's default (rootMenu).</summary>
    private static MenuAction Nothing() => new SetAction { Var = "_none", Value = 0 };

    /// <summary>The Title key, everywhere: the disc's title menu, or nothing without one.</summary>
    public MenuKeys ManifestKeys => new()
    {
        Title = (MenuAction?)Run(_keyActions.GetValueOrDefault((DvdDomain.VmgMenu, 0, MenuType.Title))) ?? Nothing(),
        Menu = (MenuAction?)Run(_keyActions.GetValueOrDefault((DvdDomain.VmgMenu, 0, MenuType.Title))) ?? Nothing(),
    };

    /// <summary>
    /// The menu keys in a title set (during its titles, and on its menus): Menu opens its root menu (or the
    /// disc's title menu when it has none; nothing without either), and the audio, subtitle, angle and chapter
    /// menus where it has them. On the disc's own menus, Menu opens the title menu.
    /// </summary>
    public MenuKeys? KeysFor(DvdDomain domain, int vtsn)
    {
        var keys = domain == DvdDomain.Title ? _titleKeyActions : _keyActions;
        var title = keys.GetValueOrDefault((DvdDomain.VmgMenu, 0, MenuType.Title));
        // The manifest's keys already say this for the disc's own menus.
        if (domain == DvdDomain.VmgMenu || vtsn == 0) return null;
        string? Of(MenuType t) => keys.GetValueOrDefault((DvdDomain.VtsMenu, vtsn, t));
        if (Of(MenuType.Root) is null && Of(MenuType.Audio) is null && Of(MenuType.Subpicture) is null && Of(MenuType.Angle) is null && Of(MenuType.Chapter) is null &&
            (domain != DvdDomain.Title || title is null))
            return null;
        return new MenuKeys
        {
            Menu = (MenuAction?)Run(Of(MenuType.Root) ?? title) ?? Nothing(),
            // During a title, the Title key too keeps the highlight the title left (the manifest's key otherwise).
            Title = domain == DvdDomain.Title ? (MenuAction?)Run(title) ?? Nothing() : null,
            AudioMenu = Run(Of(MenuType.Audio)),
            SubtitleMenu = Run(Of(MenuType.Subpicture)),
            AngleMenu = Run(Of(MenuType.Angle)),
            ChapterMenu = Run(Of(MenuType.Chapter)),
        };
    }

    /// <summary>What a resumed title does that its saved resume point can't carry.</summary>
    public sealed record ResumeLoss(string Where, HashSet<string> Sets, bool Other);

    public List<ResumeLoss> ResumeLosses { get; } = new();

    /// <summary>Each resume continuation's resumed title as a play item (what <c>saveResume.item</c> holds).</summary>
    private readonly Dictionary<string, PlayItem> _resumeItems = new();

    /// <summary>
    /// Gives each saveResume that names its media the resumed title's whole play item (spec §6.5 <c>item</c>):
    /// its end, cues and holds go with it. Items hold actions too, so this repeats until none is left. The saved
    /// time was on the media played; when the item plays another (a composite of pieces of it), the time moves
    /// onto that one's clock, or the point would be past its end.
    /// </summary>
    public void FillResumeItems(Func<IEnumerable<MenuAction>> all, IReadOnlyDictionary<string, MediaEntry>? media = null)
    {
        var rounds = 0;
        for (var changed = true; changed;)
        {
            changed = false;
            if (++rounds > 64)
            {
                // A resumed title whose own commands save a point with itself: a cycle no inline item can hold.
                ResumeLosses.Add(new ResumeLoss("Resume items nest without end (a resumed title saves itself as its own resume point).", new(), true));
                return;
            }
            foreach (var save in all().OfType<SaveResumeAction>().ToList())
                if (save.Media is not null && save.After is RunAction { Action: { } name } && _resumeItems.TryGetValue(name, out var item))
                {
                    save.Item = CloneItem(item);
                    if (save.At is { } at && media is not null && OnClockOf(media, item.Media, save.Media, at) is { } moved) save.At = moved;
                    save.Media = null;
                    changed = true;
                }
        }
    }

    /// <summary>
    /// A time on <paramref name="from"/>'s clock as a time on <paramref name="to"/>'s, when <paramref name="to"/> is
    /// a composite whose pieces include it there; null when it's the same clock or no piece holds that time.
    /// </summary>
    private static double? OnClockOf(IReadOnlyDictionary<string, MediaEntry> media, string to, string from, double at)
    {
        if (to == from || !media.TryGetValue(to, out var composite) || composite.Segments is not { } pieces) return null;
        var clock = 0.0;
        foreach (var piece in pieces)
        {
            var start = piece.Start ?? 0;
            var end = piece.End ?? (media.TryGetValue(piece.Media, out var file) ? file.Duration : null);
            if (end is null) return null;
            if (piece.Media == from && at >= start - 0.001 && at < end.Value - 0.001) return Math.Round(clock + Math.Max(0, at - start), 3);
            clock += end.Value - start;
        }
        return null;
    }

    private static PlayItem CloneItem(PlayItem item) =>
        System.Text.Json.JsonSerializer.Deserialize<PlayItem>(System.Text.Json.JsonSerializer.Serialize(item, DeepJson), DeepJson)!;

    /// <summary>What follows a resumed title, per resume cell: shared actions, compiled once.</summary>
    private readonly Dictionary<PlayedCellRef, string> _resumeAfters = new();
    private readonly Queue<(string Name, PlayedCellRef Cell)> _pendingResumes = new();

    /// <summary>
    /// A resume point saved with its media (where the save isn't inside the playback, so it can't say "here")
    /// also says what follows that media when it's resumed (spec §6.5): on a disc, the rest of the title from
    /// the resume cell, then its post commands, which may go on to the next title of a play-all.
    /// </summary>
    private void SaveWithMedia(SaveResumeAction save, string media, double at, PlayedCellRef cell)
    {
        save.Media = media;
        save.At = at;
        if (!_resumeAfters.TryGetValue(cell, out var name))
        {
            name = $"vts{cell.Vtsn}-title-pgc{cell.Pgcn}-resume-c{cell.Cell}";
            _resumeAfters[cell] = name;
            _pendingResumes.Enqueue((name, cell));
        }
        save.After = new RunAction { Action = name };
    }

    private void CompileResumeAfter((string Name, PlayedCellRef Cell) e)
    {
        var (vtsn, pgcn, cellNumber) = e.Cell;
        var m = Root();
        m.Domain = DvdDomain.Title;
        m.Vtsn = vtsn;
        m.Pgcn = pgcn;
        m.Cell = cellNumber;
        m.Pgn = _disc.GetPgc(DvdDomain.Title, vtsn, pgcn)?.ProgramOfCell(cellNumber) ?? 1;
        TitleRegisters(m);
        // SPRM 8 is what it was when the resume was asked for (the resume sets it from there).
        m.Hl = HlKind.Variable;
        m.NamedStart = true;
        m.EndRunAtPost = true;
        m.Phase = Phase.PlayCell;
        // Resumed, the title plays from the resume cell on: that play is the saved item (its end, cues and holds
        // go with it), and what follows it is the save's after. Conditions the title's post commands test can sit
        // around the play (each branch plays it, then goes its own way): they move into the after, where the disc
        // tests them. The title's own sets as it starts don't run on a resume; harmless when nothing reads them
        // (the builder checks that once the manifest is tidied).
        var steps = Follow(m);
        var where = $"Resuming VTS {vtsn} title PGC {pgcn} at cell {cellNumber}";
        var leading = steps.TakeWhile(x => x is SetAction { Then: null }).Cast<SetAction>().ToList();
        PlayAction? played = null;
        var after = SplitPlay(Block(steps.Skip(leading.Count).ToList()), ref played, out var ok);
        if (ok && played is not null)
        {
            // The title-position registers come from the player on a resume (the position flag is cleared then).
            var lost = leading.Select(x => x.Var).Where(v => v is not ("s4" or "s5" or "s6" or "s7") && !v.StartsWith(PositionFlag, StringComparison.Ordinal)).Distinct().ToList();
            if (lost.Count > 0)
                ResumeLosses.Add(new ResumeLoss($"{where}: the title's own sets as it starts don't run on a resume ({string.Join(", ", lost)}).",
                    new HashSet<string>(lost, StringComparer.Ordinal), false));
            _resumeItems[e.Name] = new PlayItem
            {
                Media = played.Media, Start = played.Start, End = played.End, Cues = played.Cues, Holds = played.Holds,
                Keys = played.Keys, Prohibits = played.Prohibits, ProhibitsAt = played.ProhibitsAt,
            };
        }
        else
        {
            after = null;
            if (steps.Count > 0)
                ResumeLosses.Add(new ResumeLoss($"{where}: doesn't start with one play: {System.Text.Json.JsonSerializer.Serialize(Block(steps), DeepJson)}.", new(), true));
        }
        Actions[e.Name] = Normalize(after, after: true) ?? Set(Use("_none"), 0);
    }

    /// <summary>
    /// An action that starts with the same play on every branch, as that play (<paramref name="play"/>) and
    /// the action with each play replaced by its after. <paramref name="ok"/> is false when a branch plays
    /// something else, or doesn't start with a play.
    /// </summary>
    private static MenuAction? SplitPlay(MenuAction? a, ref PlayAction? play, out bool ok)
    {
        ok = true;
        switch (a)
        {
            case PlayAction p:
            {
                var json = System.Text.Json.JsonSerializer.Serialize<MenuAction>(new PlayAction
                    { Media = p.Media, Start = p.Start, End = p.End, Cues = p.Cues, Holds = p.Holds, Keys = p.Keys, Prohibits = p.Prohibits }, DeepJson);
                if (play is not null && System.Text.Json.JsonSerializer.Serialize<MenuAction>(new PlayAction
                        { Media = play.Media, Start = play.Start, End = play.End, Cues = play.Cues, Holds = play.Holds, Keys = play.Keys, Prohibits = play.Prohibits }, DeepJson) != json)
                    ok = false;
                play ??= p;
                return p.After;
            }
            case IfAction i:
            {
                var then = SplitPlay(i.Then, ref play, out var ok1);
                var els = SplitPlay(i.Else, ref play, out var ok2);
                ok = ok1 && ok2;
                return new IfAction { Test = i.Test, Then = then ?? new SetAction { Var = "_none", Value = 0 }, Else = els };
            }
            case DoAction { Steps: [var only] }:
                return SplitPlay(only, ref play, out ok);
            default:
                ok = false;
                return null;
        }
    }

    private void CompileNamed(NamedEntry e)
    {
        if (_options.Trace is { } trace2) trace2($"  named {e.Name}");
        var m = Root();
        m.Domain = e.Domain;
        m.Vtsn = e.Vtsn;
        m.Unit = e.Unit;
        m.Pgcn = e.Pgcn;
        m.Pgn = e.Pgn;
        m.Hl = e.Hl;
        m.LeadIn = new List<MenuCellRef>(e.LeadIn);
        m.NamedStart = true;
        m.Phase = e.Start;
        if (e.Start is Phase.Post or Phase.Pre)
        {
            // Starting at a command line, not at a PGC's entry: a PGC the commands link to (even this one) is
            // entered through its own shared action, not written out here (The Matrix Revolutions D2's post
            // commands linking to eleven title PGCs).
            m.NamedStart = false;
            m.Line = e.Line;
            if (e.Start == Phase.Pre && m.Domain == DvdDomain.Title) TitleRegisters(m);
        }
        else if (e.Start != Phase.EnterPgc)
        {
            m.Cell = e.Cell;
            if (m.Domain == DvdDomain.Title) TitleRegisters(m);
        }
        var body = Finish(Follow(m)) ?? Set(Use("_none"), 0);
        // A program that only ever comes back to itself (a trap: an empty cell that links to itself) holds there:
        // a menu without buttons and without a timeout, which only the remote's keys leave.
        if (body is RunAction self && self.Action == e.Name)
        {
            var pgc = _disc.GetPgc(e.Domain, e.Vtsn, e.Pgcn, e.Domain == DvdDomain.Title ? -1 : e.Unit);
            if (e.Domain != DvdDomain.Title && pgc is { CellCount: > 0 })
            {
                var hold = MenuFor(new MenuCellKey(e.Domain, e.Vtsn, e.Pgcn, 1, e.Unit), new HighlightInfo());
                hold.Holds = true;
                body = new GotoAction { Menu = hold.Id };
            }
        }
        Actions[e.Name] = body;
    }

    private IEnumerable<(DvdDomain, int, MenuType)> MenuKeyEntries()
    {
        if (_disc.FindMenuPgc(DvdDomain.VmgMenu, 0, MenuType.Title) > 0) yield return (DvdDomain.VmgMenu, 0, MenuType.Title);
        foreach (var v in _disc.TitleSets.Keys.OrderBy(k => k))
            if (_disc.FindMenuPgc(DvdDomain.VtsMenu, v, MenuType.Root) > 0) yield return (DvdDomain.VtsMenu, v, MenuType.Root);
    }

    private static GotoAction? FirstGoto(MenuAction? a) => a switch
    {
        GotoAction g => g,
        DoAction d => d.Steps.Select(FirstGoto).FirstOrDefault(x => x is not null),
        SetAction s => FirstGoto(s.Then),
        _ => null,
    };

    private void CompileMenu(CompiledMenu menu)
    {
        var k = menu.Key;
        var pgc = _disc.GetPgc(k.Domain, k.Vtsn, k.Pgcn, k.Unit)!;
        var cell = pgc.Cells[k.Cell - 1];

        MenuAction CompileButton(DvdButton b)
        {
            var m = AtMenu(menu);
            SetHighlightKnown(m, b.Number);
            m.Phase = Phase.Button;
            m.Instruction = DvdCommands.Decode(b.Command);
            // A button that does nothing still needs an action: a set nothing reads.
            return Normalize(Finish(Follow(m)), after: false) ?? Set(Use("_none"), 0);
        }
        foreach (var b in menu.Highlight.Buttons) menu.Buttons[b.Number] = CompileButton(b);

        // Buttons that change while the menu's video plays: each other set, over the time it shows. A button
        // with the same command as the main set's runs the same action.
        var sets = _disc.MenuCellHighlights(k.Domain, k.Vtsn, k.Pgcn, k.Cell, k.Unit);
        var main = ButtonSignature(menu.Highlight);
        var compiled = new Dictionary<string, MenuAction>();
        for (var i = 0; i < sets.Count; i++)
        {
            var hl = sets[i];
            var signature = ButtonSignature(hl);
            var to = hl.ButtonsUntil?.TotalSeconds;
            while (i + 1 < sets.Count && ButtonSignature(sets[i + 1]) == signature)
                to = sets[++i].ButtonsUntil?.TotalSeconds;
            if (i + 1 < sets.Count) to = Math.Min(to ?? double.MaxValue, sets[i + 1].ButtonsAt.TotalSeconds);
            if (signature == main) continue;
            var actions = new Dictionary<int, MenuAction>();
            foreach (var b in hl.Buttons)
            {
                var same = menu.Highlight.Get(b.Number) is { } mb && mb.Command.AsSpan().SequenceEqual(b.Command);
                var key = Convert.ToHexString(b.Command) + ":" + b.Number;
                if (!compiled.TryGetValue(key, out var action))
                    compiled[key] = action = same ? menu.Buttons[b.Number] : CompileButton(b);
                actions[b.Number] = Clone(action);
            }
            menu.Windows.Add(new MenuButtonWindow(Math.Round(hl.ButtonsAt.TotalSeconds, 3), to is { } t && t < double.MaxValue ? Math.Round(t, 3) : null, hl, actions));
        }

        // Left alone: the still time runs out (or the cell's video ends), its cell command runs, then the
        // next cell or the post commands.
        if (cell.StillTime != 0xFF && !menu.Holds)
        {
            var m = AtMenu(menu);
            if (menu.Highlight.Buttons.Count == 0) m.Hl = HlKind.Variable;
            m.Phase = Phase.CellCommand;
            var action = Normalize(Finish(Follow(m)), after: false);
            // A menu looping on itself (motion menus loop their own video); a clip without buttons plays again.
            if (action is GotoAction g && g.Menu == menu.Id && menu.Highlight.Buttons.Count > 0) action = null;
            if (action is GotoAction loop && _menus.Values.FirstOrDefault(x => x.Id == loop.Menu) is { } next && SameButtons(menu, next))
                menu.LoopsInto = next.Key;
            if (action is not null)
            {
                menu.Timeout = action;
                menu.TimeoutAfter = Math.Round(cell.Duration.TotalSeconds + cell.StillTime, 1);
            }
        }

        if (pgc.GoUpPgc > 0)
        {
            var m = AtMenu(menu);
            var top = m.Out;
            if (ApplyLink(m, new DvdLink(LinkCmd.LinkGoUpPGC))) Go(m);
            menu.GoUp = Normalize(Finish(top), after: false);
        }
    }

    /// <summary>What makes two highlights the same set of buttons: places, moves between them, commands, forced choices.</summary>
    private static string ButtonSignature(HighlightInfo h) =>
        $"{h.ForcedSelect}/{h.ForcedActivate}:" + string.Join(";", h.Buttons.Select(b =>
            $"{b.Number},{b.X0},{b.Y0},{b.X1},{b.Y1},{b.Up},{b.Down},{b.Left},{b.Right},{b.AutoAction},{Convert.ToHexString(b.Command)}"));

    private static bool SameButtons(CompiledMenu a, CompiledMenu b) =>
        a.Highlight.Buttons.Count == b.Highlight.Buttons.Count &&
        a.Highlight.Buttons.Zip(b.Highlight.Buttons).All(p => p.First.X0 == p.Second.X0 && p.First.Y0 == p.Second.Y0 &&
                                                            p.First.X1 == p.Second.X1 && p.First.Y1 == p.Second.Y1);

    private M Root()
    {
        _shared = new Shared();
        return new M { Shared = _shared, G = (ushort[]?[])_globalG.Clone(), Global = _globalG };
    }

    private M AtMenu(CompiledMenu menu)
    {
        var m = Root();
        m.Domain = menu.Key.Domain;
        m.Vtsn = menu.Key.Vtsn;
        m.Pgcn = menu.Key.Pgcn;
        m.Cell = menu.Key.Cell;
        m.Unit = menu.Key.Unit;
        m.Pgn = _disc.GetPgc(menu.Key.Domain, menu.Key.Vtsn, menu.Key.Pgcn, menu.Key.Unit)!.ProgramOfCell(menu.Key.Cell);
        return m;
    }

    // ================================================================== machine

    private enum Phase
    {
        EnterPgc,
        Pre,
        PlayProgram,
        PlayCell,
        CellCommand,
        NextCell,
        Post,
        AfterPost,
        Button,
    }

    private enum HlKind
    {
        /// <summary>Not changed on this path: the highlight is whatever the viewer had (@focus).</summary>
        Focus,
        Known,
        /// <summary>Set from a register at playback: kept in the s8 variable.</summary>
        Variable,
    }

    private sealed class Shared
    {
        public int Steps;
        public int Forks;
        public int Entries;
        public bool Cut;
    }

    private sealed class TitleRun
    {
        public List<PlayedCell> Cells = new();

        /// <summary>For each cell, the title it plays in (SPRM 4 and 5 as the disc has them then).</summary>
        public List<(int Ttn, int VtsTtn)> Titles = new();
        public List<(int AfterCells, List<MenuAction> Actions)> Cues = new();
        public List<MenuAction> Pending = new();
        public HashSet<string> Written = new();
        public HashSet<string> Seen = new();

        /// <summary>The title was left by a call (CallSS), so it can be resumed from there.</summary>
        public bool LeftByCall;

        /// <summary>Cells the disc holds on at their end: still time in seconds, or "input".</summary>
        public List<(int Cell, HoldTime For)> Holds = new();

        /// <summary>Playback goes back to this cell of the run for good (a loop): the play seeks there and never ends.</summary>
        public int? LoopsTo;

        public TitleRun Clone() => new()
        {
            Cells = new(Cells),
            Titles = new(Titles),
            Cues = Cues.Select(c => (c.AfterCells, new List<MenuAction>(c.Actions))).ToList(),
            Pending = new(Pending),
            Written = new(Written),
            Seen = new(Seen),
            LeftByCall = LeftByCall,
            Holds = new(Holds),
            LoopsTo = LoopsTo,
        };
    }

    private sealed class NavigationFlag
    {
        public bool Set;
    }

    /// <summary>Where a path is: the command line it's on, and where playback would go from there (program, cell).</summary>
    private readonly record struct ProgramPoint(DvdDomain Domain, int Vtsn, int Pgcn, Phase Phase, int Line, int Pgn, int Cell);

    private static ProgramPoint PointOf(M m) =>
        new(m.Domain, m.Vtsn, m.Pgcn, m.Phase, m.Phase switch { Phase.PlayCell => m.Cell, Phase.EnterPgc => 0, _ => m.Line }, m.Pgn, m.Phase == Phase.EnterPgc ? 0 : m.Cell);

    /// <summary>
    /// Where the branches of a condition meet again: a line of a command list, in the same run of it
    /// (<paramref name="entry"/>), or anywhere a dry run found both branches pass (entry 0).
    /// </summary>
    private sealed class JoinPoint(int entry, ProgramPoint at, int navigations, bool duringPlayback = false)
    {
        public readonly List<M> Arrived = new();

        /// <summary>A path arrives only if it hasn't navigated since the condition (what it emitted all goes in the if).</summary>
        public bool Matches(M m) =>
            (entry == 0 || m.Entry == entry) && PointOf(m) == at && m.Dry is null && m.Navigations == navigations &&
            (duringPlayback ? m.Inline : m.Run is null);
    }

    /// <summary>One path through the programs: where it is, what's known of the registers, what it emitted.</summary>
    private sealed class M
    {
        public Shared Shared = null!;
        public DvdDomain Domain;
        public int Vtsn;
        public int Pgcn = 1;
        public int Pgn = 1;
        public int Cell = 1;

        /// <summary>The menu language unit in use (in a menu domain).</summary>
        public int Unit;
        public Phase Phase;
        public int Line = 1;
        public DvdInstruction? Instruction;

        /// <summary>What the path knows of each register: null = nothing, else the values it can have.</summary>
        public ushort[]?[] G = new ushort[]?[16];

        public ushort[]?[] S = new ushort[]?[24];

        /// <summary>What a register can hold anywhere on the disc (the most a path can not know).</summary>
        public ushort[]?[] Global = new ushort[]?[16];
        public HlKind Hl = HlKind.Focus;

        public List<MenuCellRef> LeadIn = new();
        public TitleRun? Run;
        public List<MenuAction> Out = new();
        public Dictionary<string, int> Visits = new();

        /// <summary>The PGC entries this path made, with the registers it made them with.</summary>
        public HashSet<string> Seen = new();
        public int LineGuard;

        /// <summary>Counts PGC entries on this path: identifies one run of a PGC's commands.</summary>
        public int Entry;

        /// <summary>
        /// The path runs during playback of this media (a button over the film): a jump to a cell the media
        /// holds is a seek within it, not a new play.
        /// </summary>
        public string? SeekMedia;

        /// <summary>Where the play the seek would move in ends on that media (null: its end): past it is another play.</summary>
        public double? SeekEnd;

        /// <summary>The PGC playing when the path started (a seek stays within it; another PGC is new playback).</summary>
        public int SeekPgcn;

        /// <summary>Playback ends with the PGC's cells: its post commands come after the play (a resumed title).</summary>
        public bool EndRunAtPost;

        /// <summary>The path just linked to a program or cell of the same PGC (no pre commands on the way).</summary>
        public bool LinkedInPgc;

        /// <summary>A SetTmpPML just asked for this parental level; the Goto with it depends on the answer.</summary>
        public int? ParentalRequest;

        /// <summary>The path is a shared action's own start: its first PGC entry is its own, not a run.</summary>
        public bool NamedStart;

        /// <summary>Counts what this path showed or played (a menu, a play, a resume).</summary>
        public int Navigations;

        /// <summary>A dry run: only records the program points the path passes, and stops at anything that navigates or forks.</summary>
        public List<ProgramPoint>? Dry;

        /// <summary>
        /// The path is the body of an if step during playback: what it emits goes in its own list (not the
        /// playback's pending actions), and it must not navigate (<see cref="Navigated"/> records if it does).
        /// </summary>
        public bool Inline;

        public NavigationFlag? Navigated;

        /// <summary>Lines where a structured if rejoins: a path reaching one stops there and hands over its state.</summary>
        public System.Collections.Immutable.ImmutableList<JoinPoint> Joins = System.Collections.Immutable.ImmutableList<JoinPoint>.Empty;

        public M Clone() => new()
        {
            Shared = Shared,
            Domain = Domain, Vtsn = Vtsn, Pgcn = Pgcn, Pgn = Pgn, Cell = Cell, Unit = Unit, Phase = Phase, Line = Line, Instruction = Instruction,
            G = (ushort[]?[])G.Clone(), S = (ushort[]?[])S.Clone(), Global = Global, Hl = Hl,
            LeadIn = new(LeadIn), Run = Run?.Clone(), Out = new(), Visits = new(Visits), Seen = new(Seen), LineGuard = LineGuard, NamedStart = NamedStart, SeekMedia = SeekMedia, SeekEnd = SeekEnd, SeekPgcn = SeekPgcn, EndRunAtPost = EndRunAtPost, LinkedInPgc = LinkedInPgc, Entry = Entry, Navigations = Navigations,
            Inline = Inline, Navigated = Navigated, Joins = Joins, Dry = Dry is null ? null : new(Dry),
        };
    }

    /// <summary>Runs a path to its end: a menu, a play, an exit, or a fork into two paths.</summary>
    /// <summary>
    /// Follows a path from where it is and returns everything it emitted from there: the list it started
    /// with (playback moves a path's output into the play's after, which stays inside this list).
    /// </summary>
    private List<MenuAction> Follow(M m)
    {
        var top = m.Out;
        Go(m);
        return top;
    }


    private void Go(M m)
    {
        while (true)
        {
            if (_options.Trace is { } trace3) trace3($"  [{m.GetHashCode():x8}] {m.Domain} vts{m.Vtsn} pgc{m.Pgcn} pg{m.Pgn} c{m.Cell} {m.Phase} L{m.Line} run={m.Run?.Cells.Count}");
            if (m.Dry is null && ++m.Shared.Steps > _options.StepBudget)
            {
                Cut(m, "too many steps");
                return;
            }
            var pgc = PgcOf(m);
            if (pgc is null)
            {
                EndRun(m);
                return;
            }

            switch (m.Phase)
            {
                case Phase.EnterPgc:
                    {
                        // Title playback that has gone on into a menu program ends here: what the program does
                        // next (maybe another title) is its after, through the program's shared action.
                        // Title playback that keeps going into a title PGC already reached by many other plays (chained
                        // PGCs, every path a different play so far) ends here too, so that PGC is written once.
                        var endsHere = m.Run is { Cells.Count: > 0 } && !m.Inline &&
                                       (m.Domain is DvdDomain.VmgMenu or DvdDomain.VtsMenu or DvdDomain.FirstPlay ||
                                        m.Domain == DvdDomain.Title && InlineTitleEntries(m) >= MaxInlineTitleEntries);
                        if (endsHere && m.Dry is { } dryStop)
                        {
                            dryStop.Add(PointOf(m));
                            return;
                        }
                        if (endsHere && m.Run is { } passing)
                            EndRun(m, asCue: passing.LeftByCall);
                        // Branches of a condition can meet where they enter the same shared action.
                        if (m.Joins.Count > 0 && m.Joins.FirstOrDefault(j => j.Matches(m)) is { } meetsHere)
                        {
                            meetsHere.Arrived.Add(m);
                            return;
                        }
                        if (m.Dry is { } dryEntry && Shares(m))
                        {
                            dryEntry.Add(PointOf(m));
                            return;
                        }
                        // Entered from elsewhere: run the PGC's shared action (compiled once) instead.
                        if (Shares(m))
                        {
                            var name = NamedFor(m);
                            m.Out.Add(new RunAction { Action = name });
                            return;
                        }
                        m.NamedStart = false;
                        // A loop is coming back to a PGC with the registers exactly as before (a disc counting its
                        // way through a play-all comes back changed each time, and goes on).
                        static string Regs(ushort[]?[] r) => string.Join(";", r.Select(v => v is null ? "?" : string.Join(",", v)));
                        var key = $"{m.Domain}:{m.Vtsn}:{m.Unit}:{m.Pgcn}:{m.Pgn}:{Regs(m.G)}:{Regs(m.S)}:{m.Hl}";
                        var place = $"{m.Domain}:{m.Vtsn}:{m.Pgcn}";
                        m.Visits[place] = m.Visits.TryGetValue(place, out var n) ? n + 1 : 1;
                        if (!m.Seen.Add(key) || m.Visits[place] > 500)
                        {
                            EndRun(m);
                            return;
                        }
                        if (m.Domain == DvdDomain.Title) TitleRegisters(m);
                        m.Entry = ++m.Shared.Entries;
                        m.Phase = Phase.Pre;
                        m.Line = 1;
                        m.LineGuard = 0;
                        continue;
                    }

                case Phase.Pre:
                case Phase.Post:
                    {
                        if (m.EndRunAtPost && m.Run is { Cells.Count: > 0 } && m.Dry is null && m.Joins.Count == 0)
                        {
                            m.EndRunAtPost = false;
                            EndRun(m);
                        }
                        // A title's post commands, once its cells have played, are one shared action: the play's
                        // after runs it. Compiled from whole-disc register values, it tests the registers as they
                        // are when the title ends, also after a resume (the viewer may have changed them meanwhile).
                        if (m.Phase == Phase.Post && m.Line == 1 && m.Domain == DvdDomain.Title && m.Run is { Cells.Count: > 0 } &&
                            !m.Inline && !m.NamedStart && pgc.PostCommands.Count > 0)
                        {
                            if (m.Dry is { } dryPost)
                            {
                                dryPost.Add(PointOf(m));
                                return;
                            }
                            if (m.Joins.Count == 0)
                            {
                                EndRun(m);
                                m.Out.Add(new RunAction { Action = NamedFor(m) });
                                return;
                            }
                        }
                        if (m.Joins.Count > 0 && m.Joins.FirstOrDefault(j => j.Matches(m)) is { } joined)
                        {
                            joined.Arrived.Add(m);
                            return;
                        }
                        if (m.Dry is { } dry)
                        {
                            if (dry.Count > 400) return;
                            dry.Add(PointOf(m));
                        }
                        var list = m.Phase == Phase.Pre ? pgc.PreCommands : pgc.PostCommands;
                        if (m.Line < 1 || m.Line > list.Count || ++m.LineGuard > 256)
                        {
                            if (m.Phase == Phase.Pre)
                            {
                                if (m.Pgn < 1) m.Pgn = 1;
                                m.Phase = pgc.CellCount == 0 || pgc.ProgramCount == 0 ? Phase.Post : Phase.PlayProgram;
                                if (m.Phase == Phase.Post) { m.Line = 1; m.LineGuard = 0; }
                            }
                            else m.Phase = Phase.AfterPost;
                            continue;
                        }
                        if (!Execute(m, DvdCommands.Decode(list[m.Line - 1]), CommandSource.List)) return;
                        continue;
                    }

                case Phase.PlayProgram:
                    if (SharedLinkTarget(m)) return;
                    m.NamedStart = false;
                    if (m.Domain == DvdDomain.Title && pgc.PlaybackMode != 0 && m.Dry is null && m.Joins.Count == 0)
                    {
                        PlayRandomPrograms(m, pgc);
                        ToPost(m);
                        continue;
                    }
                    if (m.Pgn < 1 || m.Pgn > pgc.ProgramMap.Count) { ToPost(m); continue; }
                    m.Cell = pgc.ProgramMap[m.Pgn - 1];
                    if (m.Domain == DvdDomain.Title) SetChapter(m);
                    m.Phase = Phase.PlayCell;
                    continue;

                case Phase.PlayCell:
                    {
                        if (SharedLinkTarget(m)) return;
                        m.NamedStart = false;
                        if (m.Joins.Count > 0 && m.Joins.FirstOrDefault(j => j.Matches(m)) is { } meets)
                        {
                            meets.Arrived.Add(m);
                            return;
                        }
                        if (m.Dry is { } dryRun)
                        {
                            // Playback or a menu: the dry run ends; branches can still meet where a title starts.
                            if (m.Domain == DvdDomain.Title) dryRun.Add(PointOf(m));
                            return;
                        }
                        if (m.Cell < 1 || m.Cell > pgc.Cells.Count) { ToPost(m); continue; }
                        m.Pgn = pgc.ProgramOfCell(m.Cell);
                        var cell = pgc.Cells[m.Cell - 1];
                        // Within the play the button is over: a seek. Past its end (a cell command ends the play
                        // there, VeggieTales An Easter Carol title 9), the cell is another play.
                        if (m.Domain == DvdDomain.Title && m.Run is null && m.SeekMedia is { } media && m.Pgcn == m.SeekPgcn &&
                            _media.StartOf(media, m.Vtsn, cell.FirstSector, cell.LastSector) is { } seekTo &&
                            seekTo < (m.SeekEnd ?? double.MaxValue) - 0.001)
                        {
                            m.Out.Add(new SeekAction { At = Math.Round(seekTo, 3) });
                            return;
                        }
                        if (m.Domain == DvdDomain.Title)
                        {
                            PlayTitleCell(m, pgc, cell);
                            if (m.Run is null) return; // looped: playback ends
                            m.Phase = Phase.CellCommand;
                            continue;
                        }

                        // Something shows in the menus: title playback has ended (a call leaves it resumable).
                        if (m.Run is { } run) EndRun(m, asCue: run.LeftByCall);

                        var hl = _disc.GetHighlight(m.Domain, m.Vtsn, m.Pgcn, m.Cell, m.Unit);
                        if (hl is not null && hl.Buttons.Count > 0)
                        {
                            if (hl.ForcedActivate > 0 && hl.Get(hl.ForcedActivate) is { } fa)
                            {
                                SetHighlightKnown(m, fa.Number);
                                m.Phase = Phase.Button;
                                m.Instruction = DvdCommands.Decode(fa.Command);
                                continue;
                            }
                            StopAtMenu(m, hl);
                            return;
                        }

                        // A cell without buttons that shows something (video, or a still) is a menu without buttons
                        // (spec §5): its picture plays, and its timeout is what the disc does next. A cell with no
                        // running time (a dispatcher's empty cell) shows nothing on the way.
                        if (cell.Duration.TotalSeconds >= 0.05 || cell.StillTime != 0)
                        {
                            StopAtMenu(m, new HighlightInfo());
                            return;
                        }
                        m.Phase = Phase.CellCommand;
                        continue;
                    }

                case Phase.CellCommand:
                    {
                        var cell = pgc.Cells[Math.Clamp(m.Cell, 1, pgc.Cells.Count) - 1];
                        m.Phase = Phase.NextCell;
                        if (cell.CellCommandNumber >= 1 && cell.CellCommandNumber <= pgc.CellCommands.Count)
                            if (!Execute(m, DvdCommands.Decode(pgc.CellCommands[cell.CellCommandNumber - 1]), CommandSource.Cell)) return;
                        continue;
                    }

                case Phase.NextCell:
                    {
                        if (m.Cell < 1 || m.Cell > pgc.Cells.Count) { ToPost(m); continue; }
                        var cell = pgc.Cells[m.Cell - 1];
                        var next = m.Cell + 1;
                        if (cell.IsAngleBlock)
                            while (next <= pgc.Cells.Count && pgc.Cells[next - 2].BlockMode != 3 && pgc.Cells[next - 1].IsAngleBlock)
                                next++;
                        if (next > pgc.Cells.Count) { ToPost(m); continue; }
                        m.Cell = next;
                        var pg = pgc.ProgramOfCell(next);
                        if (pg != m.Pgn)
                        {
                            m.Pgn = pg;
                            if (m.Domain == DvdDomain.Title) SetChapter(m);
                        }
                        m.Phase = Phase.PlayCell;
                        continue;
                    }

                case Phase.AfterPost:
                    if (m.Domain == DvdDomain.Title && pgc.StillTime != 0 && m.Run is { Cells.Count: > 0 } stillRun)
                        stillRun.Holds.Add((stillRun.Cells.Count - 1, pgc.StillTime == 0xFF ? HoldTime.Input : (double)pgc.StillTime));
                    if (pgc.StillTime == 0xFF && m.Domain is DvdDomain.VmgMenu or DvdDomain.VtsMenu)
                    {
                        EndRun(m);
                        return;
                    }
                    if (pgc.NextPgc != 0 && m.Domain != DvdDomain.FirstPlay)
                    {
                        m.Pgcn = pgc.NextPgc;
                        m.Pgn = 1;
                        m.Phase = Phase.EnterPgc;
                        continue;
                    }
                    // The program ran off its end with nowhere to go: the player stops, as at the disc's Exit.
                    EndRun(m);
                    if (m.Dry is null && !m.Inline) m.Out.Add(new ExitAction());
                    return;

                case Phase.Button:
                    {
                        var ins = m.Instruction!;
                        m.Instruction = null;
                        m.Phase = Phase.PlayCell; // replaced by the link (a button without one ends the path)
                        if (!Execute(m, ins, CommandSource.Button)) return;
                        continue;
                    }
            }
        }
    }

    private static void ToPost(M m)
    {
        m.Phase = Phase.Post;
        m.Line = 1;
        m.LineGuard = 0;
    }

    private void Cut(M m, string why)
    {
        if (!m.Shared.Cut)
        {
            m.Shared.Cut = true;
            BudgetCuts++;
            if (Warnings.Count < 200) Warnings.Add(DvdWarningCode.PathStopped, $"Stopped following a path at {m.Domain} vts{m.Vtsn} pgc{m.Pgcn}: {why}.");
        }
        EndRun(m);
    }

    // ================================================================== commands

    private enum CommandSource
    {
        List,
        Cell,
        Button,
    }

    /// <summary>
    /// Runs one command on a path. False when the path ended here (it navigated, or forked and both
    /// branches were followed to their ends).
    /// </summary>
    private bool Execute(M m, DvdInstruction ins, CommandSource source)
    {
        DvdOp? flow = null;
        foreach (var op in ins.Before) Apply(m, op, ref flow);

        if (ins.Condition is { } c)
        {
            var known = Evaluate(m, c);
            if (known is null && m.Dry is not null) return false;
            if (known is null)
            {
                if (++m.Shared.Forks > _options.ForkBudget)
                {
                    Cut(m, "too many conditions");
                    return false;
                }
                // Conditions that rejoin are written once, as an if step, so what follows isn't copied into
                // both branches: a conditional register command, or a Goto that skips a few lines.
                if (flow is null && ins.Guarded.All(IsPlainSet))
                    return ConditionalSet(m, c, ins, source);
                if (flow is null && source == CommandSource.List && ins.Guarded.Count > 0 && ins.Guarded[^1] is GotoOp skip &&
                    ins.Guarded.Take(ins.Guarded.Count - 1).All(IsPlainSet) && ins.After.Count == 0 && m.Run is not { Cells.Count: > 0 } &&
                    TrySkip(m, c, ins, skip.Line))
                    return false;

                if (TryJoin(m, c, ins, flow, source))
                    return false;

                // A condition during playback is decided when playback gets there, not when it starts: the viewer
                // can change registers meanwhile (the Menu key, a button, then a resume). Playback ends here, and
                // the condition is the start of what follows.
                if (m.Run is { Cells.Count: > 0 })
                    SplitRun(m);
                var test = TestFor(m, c);
                var yes = m.Clone();
                var no = m.Clone();
                Refine(yes, c, true);
                Refine(no, c, false);
                DvdOp? yesFlow = flow, noFlow = flow;
                foreach (var op in ins.Guarded) Apply(yes, op, ref yesFlow);
                foreach (var op in ins.After) { Apply(yes, op, ref yesFlow); Apply(no, op, ref noFlow); }
                var (yesOut, noOut) = (yes.Out, no.Out);
                if (Continue(yes, yesFlow, source)) Go(yes);
                if (Continue(no, noFlow, source)) Go(no);
                m.Out.Add(new IfAction { Test = test, Then = Block(yesOut), Else = Block(noOut) });
                return false;
            }
            if (known.Value)
                foreach (var op in ins.Guarded) Apply(m, op, ref flow);
        }
        else
            foreach (var op in ins.Guarded) Apply(m, op, ref flow);

        foreach (var op in ins.After) Apply(m, op, ref flow);
        return Continue(m, flow, source);
    }

    private static bool IsPlainSet(DvdOp op) => op is SetGprmOp or SetGprmModeOp or SetStreamsOp or SetNavTimerOp or SetParentalOp;

    /// <summary>"if (test) { register commands }", then on together with what's known on both branches.</summary>
    private bool ConditionalSet(M m, DvdCondition c, DvdInstruction ins, CommandSource source)
    {
        var test = TestFor(m, c);
        var yes = m.Clone();
        yes.Inline = true;
        Refine(yes, c, true);
        DvdOp? none = null;
        foreach (var op in ins.Guarded) Apply(yes, op, ref none);
        Refine(m, c, false);
        Merge(m, new[] { yes, m });
        if (yes.Out.Count > 0) Emit(m, new IfAction { Test = test, Then = Block(yes.Out)! });
        DvdOp? flow = null;
        foreach (var op in ins.After) Apply(m, op, ref flow);
        return Continue(m, flow, source);
    }

    /// <summary>
    /// "if (test) Goto J" over lines that only run on to J: "if (not test) { those lines }", then on from J
    /// once. False (nothing done) when the lines jump elsewhere or the branches can't be merged.
    /// </summary>
    private bool TrySkip(M m, DvdCondition c, DvdInstruction ins, int join)
    {
        var pgc = PgcOf(m);
        var list = m.Phase == Phase.Pre ? pgc?.PreCommands : m.Phase == Phase.Post ? pgc?.PostCommands : null;
        var from = m.Line + 1;
        if (list is null || join <= from || join > list.Count + 1) return false;
        for (var l = from; l < join; l++)
            foreach (var op in DvdCommands.Decode(list[l - 1]).AllOps())
                if (op is BreakOp || op is GotoOp g && (g.Line < from || g.Line > join)) return false;

        var skipped = m.Clone();
        Refine(skipped, c, true);
        DvdOp? none = null;
        foreach (var op in ins.Guarded) Apply(skipped, op, ref none);
        if (skipped.Out.Count > 0) return false;

        var body = m.Clone();
        Refine(body, c, false);
        body.Line = from;
        var stop = new JoinPoint(m.Entry, new ProgramPoint(m.Domain, m.Vtsn, m.Pgcn, m.Phase, join, m.Pgn, m.Cell), m.Navigations);
        body.Joins = m.Joins.Add(stop);
        var bodyOut = Follow(body);

        var arrivals = stop.Arrived.Prepend(skipped).ToList();
        if (arrivals.Any(a => a.Hl != skipped.Hl || a.Hl == HlKind.Known && !Same(a.S[8], skipped.S[8])))
        {
            // The highlight differs by branch: no rejoining; the branches are followed separately after all.
            return false;
        }

        var test = TestFor(m, c);
        if (Block(bodyOut) is { } region) m.Out.Add(new IfAction { Test = Negate(test), Then = region });
        Merge(m, arrivals);
        m.Line = join;
        Go(m);
        return true;
    }

    /// <summary>
    /// A condition whose branches go different ways through the programs but meet again before anything
    /// shows (both run on into the same dispatcher, say): "if (test) { one way } else { the other }", then
    /// on from where they meet, once. Dry runs of both branches find the first point both pass.
    /// </summary>
    private bool TryJoin(M m, DvdCondition c, DvdInstruction ins, DvdOp? flow, CommandSource source)
    {
        M Branch(bool held, out DvdOp? branchFlow)
        {
            var b = m.Clone();
            Refine(b, c, held);
            branchFlow = flow;
            if (held) foreach (var op in ins.Guarded) Apply(b, op, ref branchFlow);
            foreach (var op in ins.After) Apply(b, op, ref branchFlow);
            return b;
        }

        List<ProgramPoint> Points(bool held)
        {
            var b = Branch(held, out var f);
            b.Dry = new List<ProgramPoint>();
            var trace = b.Dry;
            b.Out = new List<MenuAction>();
            if (Continue(b, f, source)) Go(b);
            return trace;
        }

        var yesPoints = Points(true);
        var noPoints = Points(false).ToHashSet();
        if (yesPoints.Count == 0 || noPoints.Count == 0) return false;
        ProgramPoint? meet = null;
        foreach (var p in yesPoints)
            if (noPoints.Contains(p)) { meet = p; break; }
        if (_options.Trace is { } trace4) trace4($"  join? {c} at {PointOf(m)}: yes {yesPoints.Count} pts, no {noPoints.Count} pts, meet {meet}");
        if (meet is not { } at) return false;

        // During playback the branches may only run commands until they meet (they become an if step at that
        // moment of playback); anything else and the condition is handled the usual way.
        var playing = m.Run is { Cells.Count: > 0 };
        var stop = new JoinPoint(0, at, m.Navigations, playing);
        var navigated = new NavigationFlag();
        var yes = Branch(true, out var yesFlow);
        var no = Branch(false, out var noFlow);
        yes.Joins = no.Joins = m.Joins.Add(stop);
        if (playing)
        {
            yes.Inline = no.Inline = true;
            yes.Navigated = no.Navigated = navigated;
        }
        var (yesOut, noOut) = (yes.Out, no.Out);
        var test = TestFor(m, c);
        if (Continue(yes, yesFlow, source)) Go(yes);
        if (Continue(no, noFlow, source)) Go(no);

        var arrivals = stop.Arrived;
        if (playing && (arrivals.Count == 0 || navigated.Set)) return false;
        if (arrivals.Count == 0 ||
            arrivals.Any(a => a.Hl != arrivals[0].Hl || a.Hl == HlKind.Known && !Same(a.S[8], arrivals[0].S[8]) || a.LeadIn.Count != arrivals[0].LeadIn.Count ||
                              a.Run?.LeftByCall != arrivals[0].Run?.LeftByCall))
        {
            if (playing) return false;
            // They can't be one path after all: each goes on from where it stopped, inside its branch.
            foreach (var a in arrivals)
            {
                a.Joins = m.Joins;
                Go(a);
            }
            m.Out.Add(new IfAction { Test = test, Then = Block(yesOut) ?? Set(Use("_none"), 0), Else = Block(noOut) });
            return true;
        }

        var branches = new IfAction { Test = test, Then = Block(yesOut) ?? Set(Use("_none"), 0), Else = Block(noOut) };
        if (playing) Emit(m, branches);
        else m.Out.Add(branches);
        if (playing && m.Run is { } run)
        {
            run.LeftByCall = arrivals[0].Run!.LeftByCall;
            foreach (var a in arrivals) { run.Written.UnionWith(a.Run!.Written); run.Seen.UnionWith(a.Run.Seen); }
        }
        var lead = arrivals[0];
        m.Domain = lead.Domain;
        m.Vtsn = lead.Vtsn;
        m.Pgcn = lead.Pgcn;
        m.Pgn = lead.Pgn;
        m.Cell = lead.Cell;
        m.Phase = lead.Phase;
        m.Line = lead.Line;
        m.Entry = lead.Entry;
        m.LeadIn = new List<MenuCellRef>(lead.LeadIn);
        Merge(m, arrivals);
        Go(m);
        return true;
    }

    /// <summary>Keeps what every arriving path agrees on (a register two paths set differently is unknown).</summary>
    private static void Merge(M m, IReadOnlyList<M> arrivals)
    {
        if (arrivals.Count == 0) return;
        var first = arrivals[0];
        var g = new ushort[]?[16];
        var sp = new ushort[]?[24];
        for (var i = 0; i < 16; i++) g[i] = Union(arrivals.Select(a => a.G[i])) ?? m.Global[i];
        for (var i = 0; i < 24; i++) sp[i] = Union(arrivals.Select(a => a.S[i]));
        m.G = g;
        m.S = sp;
        m.Hl = first.Hl;
        foreach (var a in arrivals)
        {
            foreach (var (k, v) in a.Visits) m.Visits[k] = Math.Max(v, m.Visits.TryGetValue(k, out var had) ? had : 0);
            m.Seen.UnionWith(a.Seen);
            m.LineGuard = Math.Max(m.LineGuard, a.LineGuard);
            if (a.Run is not null && m.Run is not null) m.Run.Written.UnionWith(a.Run.Written);
        }
    }

    /// <summary>Acts on a command's flow (goto, break, link). False when the path ended.</summary>
    private bool Continue(M m, DvdOp? flow, CommandSource source)
    {
        switch (flow)
        {
            case null:
                if (source == CommandSource.List) m.Line++;
                if (source == CommandSource.Button)
                {
                    // No link: the menu stays, with the highlight where the command put it.
                    if (m.Hl == HlKind.Known && Single(m.S[8]) is { } s8) m.Out.Add(Set("@focus", $"b{s8 >> 10}"));
                    else if (m.Hl == HlKind.Variable) FocusFromRegister(m);
                    EndRun(m);
                    return false;
                }
                return true;
            case GotoOp g when m.ParentalRequest is { } level && source == CommandSource.List && m.Dry is null:
                {
                    // SetTmpPML: the player raises the parental level (the program jumps on) or refuses (next line).
                    m.ParentalRequest = null;
                    var yes = m.Clone();
                    var no = m.Clone();
                    yes.S[13] = One(level);
                    if (_sprmsRead.Contains(13)) yes.Out.Add(Set(Use("s13"), level));
                    yes.Line = g.Line;
                    no.Line++;
                    var (yesOut, noOut) = (yes.Out, no.Out);
                    Go(yes);
                    Go(no);
                    m.Out.Add(new RequestParentalAction { Level = level, Then = Block(yesOut) ?? Set(Use("_none"), 0), Else = Block(noOut) });
                    return false;
                }
            case GotoOp g:
                m.ParentalRequest = null;
                if (source == CommandSource.List)
                {
                    var back = g.Line <= m.Line;
                    m.Line = g.Line;
                    // A loop in the commands (a Goto back): from that line on is one shared action, which runs
                    // itself each time round, however many times the disc goes round.
                    if (back && m.Phase is Phase.Pre or Phase.Post && !m.Inline && m.Run is null && m.Joins.Count == 0)
                    {
                        if (m.Dry is { } dryLoop)
                        {
                            dryLoop.Add(PointOf(m));
                            return false;
                        }
                        m.Out.Add(new RunAction { Action = NamedFor(m) });
                        return false;
                    }
                }
                return true;
            case BreakOp:
                if (source == CommandSource.List) m.Line = int.MaxValue;
                return true;
            case LinkOp l:
                if (l.Link.Cmd == LinkCmd.LinkRSM) return Resume(m, source, l.Link.Data1);
                return ApplyLink(m, l.Link);
            default:
                return true;
        }
    }

    /// <summary>
    /// LinkRSM: continue the stopped title. With nothing to resume a player ignores it and goes on with
    /// the program, so the resume is a step and the rest follows it.
    /// </summary>
    private bool Resume(M m, CommandSource source, int button)
    {
        if (m.Dry is null)
        {
            // "LinkRSM (button n)": the resumed title starts with that button chosen, which the remote's Menu key
            // then opens the menu on (Folks!: resume with button 2).
            if (button != 0) Emit(m, Set(Use("s8"), button << 10));
            // Otherwise the title resumes with the highlight it has: the button pressed (a resume button), or the
            // one the menu showed. Its post commands may call the menu again, which opens on that button.
            else if (m.Hl == HlKind.Known && Single(m.S[8]) is { } known) Emit(m, Set(Use("s8"), known));
            else if (m.Hl == HlKind.Focus) FocusToS8(m);
            // Not counted as navigating for joins: a resume either plays (ending the do it's in) or falls
            // through to the same program as the other branch, so the rest can still be written once.
            if (m.Navigated is { } flag) flag.Set = true;
            EndRun(m);
            // The disc's resume point (saved by a call out of a title; the viewer stopping saves one too).
            if (TracksPosition)
                m.Out.Add(new IfAction { Test = new Test.Compare("@resumeMedia", "ne", ""), Then = Block(PlayerPositionFlags(0)) ?? Set(Use("_none"), 0) });
            m.Out.Add(new ResumeAction());
        }
        // With nothing to resume the program goes on (a resume that plays ends the do it's in).
        return Continue(m, null, source);
    }

    private bool ReadsWritten(TitleRun run, DvdCondition c) =>
        Name(c.Left) is { } l && run.Written.Contains(l) || Name(c.Right) is { } r && run.Written.Contains(r);

    private static string? Name(Operand o) => o.Kind switch
    {
        OperandKind.Gprm => $"g{o.Value}",
        OperandKind.Sprm => $"s{o.Value}",
        _ => null,
    };

    // ------------------------------------------------------------------ register values

    /// <summary>Registers known to be one of up to this many values keep the list; more is "unknown".</summary>
    private const int MaxCandidates = 16;

    private static ushort[] One(int v) => new[] { (ushort)v };

    private static ushort? Single(ushort[]? v) => v is { Length: 1 } ? v[0] : null;

    private static bool Same(ushort[]? a, ushort[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>Every value any of the sets allows (unknown if one is unknown or there are too many).</summary>
    private static ushort[]? Union(IEnumerable<ushort[]?> sets)
    {
        var all = new SortedSet<ushort>();
        foreach (var v in sets)
        {
            if (v is null) return null;
            all.UnionWith(v);
            if (all.Count > MaxCandidates) return null;
        }
        return all.ToArray();
    }

    /// <summary>The values an operand can have (null when unknown).</summary>
    private static ushort[]? Candidates(M m, Operand o) => o.Kind switch
    {
        OperandKind.Immediate => One(o.Value),
        OperandKind.Gprm => m.G[o.Value],
        _ when o.Value == 8 && m.Hl != HlKind.Known => null,
        _ => o.Value is >= 0 and < 24 ? m.S[o.Value] : One(0),
    };

    /// <summary>The operand's value when only one is possible.</summary>
    private ushort? Known(M m, Operand o) => Single(Candidates(m, o));

    private bool? Evaluate(M m, DvdCondition c)
    {
        if (Candidates(m, c.Left) is not { } left || Candidates(m, c.Right) is not { } right) return null;
        bool? result = null;
        foreach (var a in left)
            foreach (var b in right)
            {
                var h = Holds(c.Op, a, b);
                if (result is null) result = h;
                else if (result != h) return null;
            }
        return result;
    }

    private static bool Holds(int op, int a, int b) => op switch
    {
        1 => (a & b) != 0,
        2 => a == b,
        3 => a != b,
        4 => a >= b,
        5 => a > b,
        6 => a <= b,
        7 => a < b,
        _ => true,
    };

    private static readonly string[] TestOps = { "", "has", "eq", "ne", "ge", "gt", "le", "lt" };
    private static readonly string[] Mirrored = { "", "has", "eq", "ne", "le", "lt", "ge", "gt" };

    /// <summary>The variable that holds an operand at playback (emitting its set-up when it needs one).</summary>
    private Value ValueOf(M m, Operand o)
    {
        if (Known(m, o) is { } k) return k;
        return Value.Of(VarOf(m, o));
    }

    /// <summary>The highlighted button, from the menu's focus: s8 = button number × 1024 (one shared action).</summary>
    private void FocusToS8(M m)
    {
        if (!Actions.ContainsKey(FocusAction))
        {
            var steps = new List<MenuAction> { Set("s8", 1024) };
            foreach (var n in Enumerable.Range(2, 35))
                steps.Add(new IfAction { Test = new Test.Compare("@focus", "eq", $"b{n}"), Then = Set("s8", n * 1024) });
            Actions[FocusAction] = new DoAction { Steps = steps };
        }
        Use("s8");
        Emit(m, new RunAction { Action = FocusAction }, toOut: true);
    }

    private const string FocusAction = "focus-to-s8";

    /// <summary>The id ("bN") of the button s8 names, for overlays that open on it (ActionTidy.TrackHighlightId).</summary>
    public const string HighlightIdVar = "_hl";

    /// <summary>During playback: the showing overlay's highlighted button into s8 (@focus is "" without one).</summary>
    private MenuAction OverlayFocusToS8()
    {
        if (!Actions.ContainsKey(OverlayFocusAction))
        {
            FocusToS8(Root());
            Actions[OverlayFocusAction] = new IfAction { Test = new Test.Compare("@focus", "ne", ""), Then = new RunAction { Action = FocusAction } };
        }
        Use("s8");
        return new RunAction { Action = OverlayFocusAction };
    }

    private const string OverlayFocusAction = "overlay-focus-to-s8";

    /// <summary>s8 into the showing overlay's highlight (a cell command's SetHL_BTNN over title-cell buttons).</summary>
    private MenuAction S8ToFocus()
    {
        if (!Actions.ContainsKey(S8FocusAction))
        {
            var steps = new List<MenuAction>();
            foreach (var n in Enumerable.Range(1, 36))
                steps.Add(new IfAction { Test = new Test.Compare("s8", "eq", n * 1024), Then = Set("@focus", $"b{n}") });
            Actions[S8FocusAction] = new DoAction { Steps = steps };
        }
        return new RunAction { Action = S8FocusAction };
    }

    private const string S8FocusAction = "s8-to-focus";

    private string VarOf(M m, Operand o)
    {
        if (o.Kind == OperandKind.Gprm) return Use($"g{o.Value}");
        if (o.Value == 8 && m.Domain == DvdDomain.Title)
        {
            // During a title's cells: the overlay's button while one shows, else what s8 holds. Pre and post
            // commands run with no title-cell buttons up (and after playback @focus is the menu's again).
            if (m.Phase == Phase.CellCommand) Emit(m, OverlayFocusToS8(), toOut: true);
            return Use("s8");
        }
        if (o.Value == 8 && m.Hl == HlKind.Focus)
        {
            FocusToS8(m);
            return Use("s8");
        }
        _sprmsRead.Add(o.Value);
        if (o.Value is >= 4 and <= 7)
        {
            // Which title and chapter are (or were last) playing: from the player, which knows them live (spec
            // §6.1 @lastMedia / @lastChapter), through one shared action that turns them into the disc's numbers.
            PositionRegisters.Add(o.Value);
            Emit(m, new RunAction { Action = PlayerPositionAction });
        }
        return Use($"s{o.Value}");
    }

    /// <summary>What a media's own cells carry, for every play of it: still holds and overlays of buttons.</summary>
    public sealed class MediaCueSet
    {
        public SortedDictionary<double, Hold> Holds { get; } = new();
        public SortedDictionary<double, Overlay> Overlays { get; } = new();
        public HashSet<(int, int, int, double?)> Cells { get; } = new();
        public SortedDictionary<double, ProhibitWindow> Windows { get; } = new();
        public HashSet<(int, int, int, double)> UopCells { get; } = new();
    }

    public Dictionary<string, MediaCueSet> MediaCues { get; } = new();

    /// <summary>Where on a media's timeline each disc title, PGC and cell plays: the disc's title and chapter numbers there.</summary>
    public sealed record TitlePosition(int Ttn, int VtsTtn, int Vtsn, int Pgcn, int Cell);

    /// <summary>Per media: the title position each played cell has, by the time the cell starts on the media.</summary>
    public Dictionary<string, SortedDictionary<double, TitlePosition>> MediaPositions { get; } = new();

    /// <summary>
    /// Records which title each of the run's cells plays in, on the media the run maps to. When another title
    /// already plays the same media there (duplicate cells), the run plays an alias of the media instead, so the
    /// player's @lastMedia still says which title it is.
    /// </summary>
    private PlaybackPlan RecordPositions(TitleRun run, PlaybackPlan plan)
    {
        List<(double At, TitlePosition Pos)> Positions()
        {
            var list = new List<(double, TitlePosition)>();
            for (var i = 0; i < run.Cells.Count; i++)
            {
                var start = i == 0 ? plan.Start : plan.CellEnds[i - 1];
                if (start is null) continue;
                var c = run.Cells[i];
                var (ttn, vtsTtn) = i < run.Titles.Count ? run.Titles[i] : (0, 0);
                list.Add((Math.Round(start.Value, 3), new TitlePosition(ttn, vtsTtn, c.Vtsn, c.Pgcn, c.Cell)));
            }
            return list;
        }
        var positions = Positions();
        bool Fits(string media) =>
            !MediaPositions.TryGetValue(media, out var known) ||
            positions.All(p => !known.TryGetValue(p.At, out var k) || k.Ttn == p.Pos.Ttn && k.VtsTtn == p.Pos.VtsTtn && k.Vtsn == p.Pos.Vtsn && k.Pgcn == p.Pos.Pgcn);
        var id = plan.Media;
        if (!Fits(id))
        {
            var ttn = positions.Select(p => p.Pos.Ttn).FirstOrDefault(t => t > 0);
            for (var n = 1; !Fits(id); n++)
                id = _media.Alias(plan.Media, n == 1 ? $"tt{ttn}" : $"tt{ttn}-{n}");
            plan = plan with { Media = id };
        }
        if (!MediaPositions.TryGetValue(id, out var map)) MediaPositions[id] = map = new();
        foreach (var (at, pos) in positions) map.TryAdd(at, pos);
        return plan;
    }

    /// <summary>
    /// A PGC that plays its programs at random or shuffled: a playAll of its programs (each its cells' stretch
    /// of the ripped files) with the order and count its playback mode gives, then its post commands.
    /// </summary>
    private void PlayRandomPrograms(M m, Pgc pgc)
    {
        EndRun(m);
        m.Navigations++;
        if (m.Navigated is { } flag) flag.Set = true;
        var items = new List<PlayItem>();
        for (var p = 1; p <= pgc.ProgramMap.Count; p++)
        {
            var first = pgc.ProgramMap[p - 1];
            var last = p < pgc.ProgramMap.Count ? pgc.ProgramMap[p] - 1 : pgc.Cells.Count;
            var run = new TitleRun();
            for (var c = first; c <= last; c++)
            {
                var cell = pgc.Cells[c - 1];
                if (cell.IsAngleBlock && cell.BlockMode != 1) continue;
                run.Cells.Add(new PlayedCell(m.Vtsn, m.Pgcn, c, cell.FirstSector, cell.LastSector, cell.Duration.TotalSeconds));
                run.Titles.Add((Single(m.S[4]) ?? 0, Single(m.S[5]) ?? 0));
                if (cell.CellCommandNumber > 0)
                    Warnings.Add(DvdWarningCode.RandomCellCommand, $"VTS {m.Vtsn} title PGC {m.Pgcn} plays at random and its cell {c} has a command; it isn't exported.");
            }
            if (run.Cells.Count == 0 || _media.Map(run.Cells) is not { } plan)
            {
                foreach (var t in TitlesOf(run.Cells)) MissingTitles.Add(t);
                continue;
            }
            plan = RecordPositions(run, plan);
            var (prohibits, prohibitsAt) = RunProhibits(run, plan);
            var onMedia = MediaCues.TryGetValue(plan.Media, out var known) ? known : MediaCues[plan.Media] = new MediaCueSet();
            NavProhibits(run, plan, onMedia);
            items.Add(new PlayItem
            {
                Media = plan.Media,
                Start = plan.Start > 0.0005 ? Math.Round(plan.Start, 3) : null,
                End = plan.End is { } end ? Math.Round(end, 3) : null,
                Prohibits = prohibits,
                ProhibitsAt = prohibitsAt,
                Keys = KeysFor(DvdDomain.Title, m.Vtsn),
            });
        }
        if (items.Count == 0) return;
        var shuffle = (pgc.PlaybackMode & 0x80) != 0;
        if (shuffle && (pgc.PlaybackMode & 0x7F) != 0 && (pgc.PlaybackMode & 0x7F) != items.Count)
            Warnings.Add(DvdWarningCode.ShufflePartial, $"VTS {m.Vtsn} title PGC {m.Pgcn} shuffles {pgc.PlaybackMode & 0x7F} of its {items.Count} programs; shuffle plays them all.");
        var rest = new List<MenuAction>();
        var playAll = new PlayAllAction
        {
            Items = items,
            Order = shuffle ? "shuffle" : "random",
            Count = shuffle ? null : pgc.PlaybackMode & 0x7F,
            After = new DoAction { Steps = rest },
        };
        PlayerHasPosition(m);
        m.Out.Add(playAll);
        m.Out = rest;
    }

    /// <summary>The profile's names for user operations 0-24 (spec profile DVD: UOPs).</summary>
    private static readonly string[] UopNames =
    {
        "seek", "chapterJump", "titleJump", "stop", "back", "seek", "previous", "next", "fastForward", "rewind",
        "titleKey", "menuKey", "subtitleMenuKey", "audioMenuKey", "angleMenuKey", "chapterMenuKey", "resume",
        "buttons", "endHold", "pause", "audioChange", "subtitleChange", "angleChange", "karaokeChange", "displayModeChange",
    };

    /// <summary>What a set of user-operation bits forbids, by the profile's names (information for players).</summary>
    public static List<string>? Prohibits(uint uops)
    {
        var names = new List<string>();
        for (var n = 0; n < UopNames.Length; n++)
            if ((uops & (1u << n)) != 0 && !names.Contains(UopNames[n])) names.Add(UopNames[n]);
        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// What a run's playback forbids: its PGCs' user operations, and its title's (time and chapter search).
    /// When the run's PGCs forbid different things, playback forbids them at different times.
    /// </summary>
    private (List<string>? Plain, List<ProhibitWindow>? Windows) RunProhibits(TitleRun run, PlaybackPlan plan)
    {
        var sets = new List<uint>();
        for (var i = 0; i < run.Cells.Count; i++)
        {
            var c = run.Cells[i];
            var uops = _disc.GetPgc(DvdDomain.Title, c.Vtsn, c.Pgcn)?.ProhibitedUserOps ?? 0;
            if (i < run.Titles.Count && _disc.GetTitle(run.Titles[i].Ttn) is { } title) uops |= (uint)(title.PlaybackType & 3);
            sets.Add(uops);
        }
        if (sets.Count == 0) return (null, null);
        // What every PGC of the run forbids is the play's list; what only some do, a window over their cells.
        var common = sets.Aggregate((a, b) => a & b);
        var windows = new List<ProhibitWindow>();
        for (var i = 0; i < sets.Count; i++)
        {
            if (sets[i] == common || (i == 0 ? plan.Start : plan.CellEnds[i - 1]) is not { } from || plan.CellEnds[i] is not { } to) continue;
            var ops = Prohibits(sets[i] & ~common)!;
            if (windows.Count > 0 && windows[^1].To is { } prev && Math.Abs(prev - from) < 0.002 && windows[^1].Ops.SequenceEqual(ops))
                windows[^1].To = Math.Round(to, 3);
            else
                windows.Add(new ProhibitWindow { From = Math.Round(from, 3), To = Math.Round(to, 3), Ops = ops });
        }
        return (Prohibits(common), windows.Count > 0 ? windows : null);
    }

    /// <summary>
    /// What each of a run's cells' navigation packs forbid (VOBU_UOP_CTL, changing as often as every VOBU), as
    /// windows on the media's timeline: the cells' own, whatever plays them.
    /// </summary>
    private void NavProhibits(TitleRun run, PlaybackPlan plan, MediaCueSet onMedia)
    {
        for (var i = 0; i < run.Cells.Count; i++)
        {
            var cell = run.Cells[i];
            if ((i == 0 ? plan.Start : plan.CellEnds[i - 1]) is not { } cellStart || plan.CellEnds[i] is not { } cellEnd) continue;
            if (!onMedia.UopCells.Add((cell.Vtsn, cell.Pgcn, cell.Cell, cellStart))) continue;
            var changes = _disc.TitleCellUops(cell.Vtsn, cell.Pgcn, cell.Cell, _options.TitleButtonVobus);
            for (var k = 0; k < changes.Count; k++)
            {
                if (Prohibits(changes[k].Uops) is not { } ops) continue;
                var from = Math.Round(cellStart + changes[k].At, 3);
                var to = Math.Round(Math.Min(cellEnd, k + 1 < changes.Count ? cellStart + changes[k + 1].At : cellEnd), 3);
                if (to > from) onMedia.Windows.TryAdd(from, new ProhibitWindow { From = from, To = to, Ops = ops });
            }
        }
    }

    /// <summary>
    /// Per register (_pos4 ... _pos7): 1 while the program's own copy is current (a link set it), 0 once playback
    /// has started (the player's position is). A link sets only some of them (a jump to a title set's menu: the
    /// title, not the chapter), so each has its own.
    /// </summary>
    public const string PositionFlag = "_pos";

    public static string PositionFlagOf(int n) => $"{PositionFlag}{n}";

    /// <summary>Sets of the flags of the position registers the disc reads.</summary>
    private List<MenuAction> PlayerPositionFlags(int value) =>
        new[] { 4, 5, 6, 7 }.Where(_sprmsRead.Contains).Select(n => (MenuAction)Set(Use(PositionFlagOf(n)), value)).ToList();

    /// <summary>The disc reads a title-position register (SPRM 4-7) somewhere.</summary>
    private bool TracksPosition => _sprmsRead.Overlaps(new[] { 4, 5, 6, 7 });

    private void PlayerHasPosition(M m)
    {
        if (TracksPosition) m.Out.AddRange(PlayerPositionFlags(0));
    }

    /// <summary>The shared action that sets s4-s7 from the player's position (built by the manifest builder).</summary>
    public const string PlayerPositionAction = "player-position";

    /// <summary>Title-position registers (SPRM 4-7) the disc reads where the program can't know them.</summary>
    public SortedSet<int> PositionRegisters { get; } = new();

    private Test TestFor(M m, DvdCondition c)
    {
        var left = Known(m, c.Left);
        if (left is null)
            return new Test.Compare(VarOf(m, c.Left), TestOps[c.Op], ValueOf(m, c.Right));
        // A constant on the left: compare the other way round.
        return new Test.Compare(VarOf(m, c.Right), Mirrored[c.Op], left.Value);
    }

    /// <summary>
    /// A test that held (or didn't) tells the path about values: g3 == 2 means g3 is 2 from here on; g9 &gt; 50
    /// leaves only the candidates above 50.
    /// </summary>
    private static void Refine(M m, DvdCondition c, bool held)
    {
        var left = Candidates(m, c.Left);
        var right = Candidates(m, c.Right);
        if (left is not null && right is not null)
        {
            Learn(m, c.Left, left.Where(a => right.Any(b => Holds(c.Op, a, b) == held)).ToArray());
            Learn(m, c.Right, right.Where(b => left.Any(a => Holds(c.Op, a, b) == held)).ToArray());
        }
        else if (c.Op == 2 && held || c.Op == 3 && !held)
        {
            if (left is null && Single(right) is { } r) Learn(m, c.Left, One(r));
            else if (right is null && Single(left) is { } l) Learn(m, c.Right, One(l));
        }
    }

    private static void Learn(M m, Operand o, ushort[] v)
    {
        if (v.Length == 0) return; // a branch that can't happen; nothing to learn
        if (o.Kind == OperandKind.Gprm) m.G[o.Value] = v;
        else if (o.Kind == OperandKind.Sprm && o.Value != 8 && o.Value < 24) m.S[o.Value] = v;
    }

    // ------------------------------------------------------------------ effects

    private void Apply(M m, DvdOp op, ref DvdOp? flow)
    {
        switch (op)
        {
            case SetGprmOp s:
                SetGprm(m, s);
                break;
            case SetStreamsOp st:
                if (st.Audio is { } a) SetSprm(m, 1, a);
                if (st.Subpicture is { } p) SetSprm(m, 2, p);
                if (st.Angle is { } g) SetSprm(m, 3, g);
                break;
            case SetNavTimerOp t:
                SetSprm(m, 9, t.Seconds);
                SetSprm(m, 10, Operand.Imm(t.Pgcn));
                NavTimer(m, t);
                break;
            case SetGprmModeOp md:
                SetGprm(m, new SetGprmOp(md.Register, 1, md.Value, 0));
                if (md.Counter) Counters.Add(Use($"g{md.Register}"));
                break;
            case SetKaraokeOp k:
                SetSprm(m, 11, k.Value);
                Emit(m, Set("@karaoke", ValueOf(m, k.Value)));
                break;
            case SetHighlightOp h:
                if (Known(m, h.Value) is { } v)
                {
                    SetHighlightKnown(m, v >> 10);
                    // A program choosing the button a menu will show: kept for the remote's Menu key during a title.
                    if (TitleSetsHighlight && m.Phase is Phase.Pre or Phase.Post or Phase.CellCommand) Emit(m, Set(Use("s8"), v));
                    // Over title-cell buttons (an overlay showing as the cell ends), it moves their highlight.
                    if (m.Domain == DvdDomain.Title && m.Phase == Phase.CellCommand) Emit(m, Set("@focus", $"b{v >> 10}"));
                }
                else
                {
                    Emit(m, Set(Use("s8"), ValueOf(m, h.Value)));
                    if (m.Domain == DvdDomain.Title && m.Phase == Phase.CellCommand) Emit(m, S8ToFocus());
                    m.S[8] = Candidates(m, h.Value);
                    m.Hl = HlKind.Variable;
                }
                break;
            case SetParentalOp pml:
                // Asked of the player: whether the jump that follows happens depends on its answer.
                m.ParentalRequest = pml.Level;
                break;
            case GotoOp or BreakOp or LinkOp:
                flow ??= op;
                break;
        }
    }

    /// <summary>
    /// SetNVTMR: after the seconds, the disc goes to that PGC of the title domain, whatever is showing (spec §6.7
    /// timer). 0 seconds stops it. A time known only at playback can't be a timer's "after": it's left to the
    /// countdown register (@navTimer reads it).
    /// </summary>
    private void NavTimer(M m, SetNavTimerOp t)
    {
        if (m.Dry is not null) return;
        if (Known(m, t.Seconds) is not { } seconds) return;
        if (seconds == 0)
        {
            Emit(m, new CancelTimerAction());
            return;
        }
        var target = m.Clone();
        target.Run = null;
        target.Domain = DvdDomain.Title;
        target.Pgcn = t.Pgcn;
        target.Pgn = 1;
        target.Phase = Phase.EnterPgc;
        target.Out = new List<MenuAction>();
        var action = Block(Follow(target)) ?? Set(Use("_none"), 0);
        Emit(m, new TimerAction { After = seconds, Action = action });
    }

    private static void SetHighlightKnown(M m, int button)
    {
        if (button <= 0) return;
        m.S[8] = One(button << 10);
        m.Hl = HlKind.Known;
    }

    private void SetSprm(M m, int n, Operand source)
    {
        var known = Known(m, source);
        var name = $"s{n}";
        if (known is null || Single(m.S[n]) != known)
        {
            if (_sprmsRead.Contains(n)) Emit(m, Set(Use(name), ValueOf(m, source)));
            m.S[n] = Candidates(m, source);
            m.Run?.Written.Add(name);
        }
        // The angle (numbered like the files' angles, 1-based) doesn't depend on how tracks were kept.
        if (n == 3)
        {
            Emit(m, Set("@angle", ValueOf(m, source)));
            return;
        }
        if (!_options.TrackChoices) return;
        // The streams the viewer hears and sees (DVD stream numbers are 0-based; subtitles show when bit 6 is set).
        switch (n)
        {
            case 1:
                if (known is { } a) { if (a < 8) Emit(m, Set("@audio", a + 1)); }
                else Emit(m, Do(Set("@audio", ValueOf(m, source)), new SetAction { Var = "@audio", Op = "add", Value = 1 }));
                break;
            case 2:
                if (known is { } p) Emit(m, Set("@subtitle", (p & 0x40) != 0 && (p & 0x3F) < 32 ? (p & 0x3F) + 1 : 0));
                else
                {
                    var v = VarOf(m, source);
                    Emit(m, new IfAction
                    {
                        Test = new Test.Compare(v, "has", 0x40),
                        Then = Do(Set("@subtitle", Value.Of(v)), new SetAction { Var = "@subtitle", Op = "and", Value = 0x3F },
                                  new SetAction { Var = "@subtitle", Op = "add", Value = 1 }),
                        Else = Set("@subtitle", 0),
                    });
                }
                break;
        }
    }

    /// <summary>A register command, 16-bit like the disc: sums and products stop at 65535, differences at 0.</summary>
    private void SetGprm(M m, SetGprmOp s)
    {
        var reg = s.Register;
        var name = Use($"g{reg}");
        var cur = m.G[reg];
        var srcSet = Candidates(m, s.Source);
        var src = Single(srcSet);
        m.Run?.Written.Add(name);

        if (s.Op == 2)
        {
            // Swap with the source register.
            var other = Use($"g{s.SwapWith}");
            Emit(m, Set(Use("_swap"), Value.Of(name)));
            Emit(m, Set(name, ValueOf(m, s.Source)));
            Emit(m, Set(other, Value.Of("_swap")));
            m.G[s.SwapWith] = cur ?? m.Global[s.SwapWith];
            m.G[reg] = srcSet ?? m.Global[reg];
            return;
        }

        // What the register can hold afterwards, when its value and the operand's are known well enough.
        ushort[]? results = null;
        if (srcSet is not null && (s.Op == 1 || s.Op != 8 && cur is not null))
            results = Union((s.Op == 1 ? srcSet : cur!.SelectMany(a => srcSet.Select(b => Compute(s.Op, a, b)))).Select(v => One(v)));
        else if (s.Op == 8 && src is { } max && max <= MaxCandidates)
            results = max == 0 ? One(1) : Enumerable.Range(1, max).Select(v => (ushort)v).ToArray();
        if (Single(results) is { } result)
        {
            if (Single(m.G[reg]) != result) Emit(m, Set(name, result));
            m.G[reg] = results;
            return;
        }

        var value = ValueOf(m, s.Source);
        switch (s.Op)
        {
            case 1:
                Emit(m, Set(name, value));
                break;
            case 3:
                Emit(m, Do(Op(name, "add", value), Clamp(name, "gt", 65535, 65535)));
                break;
            case 4:
                Emit(m, Do(Op(name, "sub", value), Clamp(name, "lt", 0, 0)));
                break;
            case 5:
                Emit(m, Do(Op(name, "mul", value), Clamp(name, "gt", 65535, 65535)));
                break;
            case 6:
            case 7:
                {
                    var op = s.Op == 6 ? "div" : "mod";
                    if (src is 0) Emit(m, Set(name, 65535));
                    else if (src is not null) Emit(m, Op(name, op, value));
                    else Emit(m, new IfAction { Test = new Test.Compare(value.Var!, "eq", 0), Then = Set(name, 65535), Else = Op(name, op, value) });
                    break;
                }
            case 8:
                if (src is 0) Emit(m, Set(name, 1));
                else if (src is not null) Emit(m, Op(name, "random", value));
                else Emit(m, new IfAction { Test = new Test.Compare(value.Var!, "gt", 0), Then = Op(name, "random", value), Else = Set(name, 1) });
                break;
            case 9:
                Emit(m, Op(name, "and", value));
                break;
            case 10:
                Emit(m, Op(name, "or", value));
                break;
            case 11:
                Emit(m, Op(name, "xor", value));
                break;
        }
        m.G[reg] = results ?? m.Global[reg];
    }

    private static ushort Compute(int op, int cur, int data) => (ushort)(op switch
    {
        1 => data,
        3 => Math.Min(cur + data, 0xFFFF),
        4 => Math.Max(cur - data, 0),
        5 => Math.Min(cur * data, 0xFFFF),
        6 => data != 0 ? cur / data : 0xFFFF,
        7 => data != 0 ? cur % data : 0xFFFF,
        9 => cur & data,
        10 => cur | data,
        11 => cur ^ data,
        _ => cur,
    });

    private static SetAction Set(string var, Value value) => new() { Var = var, Value = value };

    private static SetAction Op(string var, string op, Value value) => new() { Var = var, Op = op, Value = value };

    private static IfAction Clamp(string var, string test, int bound, int to) =>
        new() { Test = new Test.Compare(var, test, bound), Then = Set(var, to) };

    private static DoAction Do(params MenuAction[] steps) => new() { Steps = steps.ToList() };

    private string Use(string var)
    {
        if (!var.StartsWith('@')) _vars.Add(var);
        return var;
    }

    /// <summary>
    /// Adds an action to the path. During title playback it waits for the next cell boundary (a cue) or the
    /// end of playback (the play's after); <paramref name="toOut"/> puts it before playback instead (set-up
    /// for a condition that's decided when playback starts).
    /// </summary>
    private static void Emit(M m, MenuAction a, bool toOut = false)
    {
        if (m.Inline) m.Out.Add(a);
        else if (!toOut && m.Run is { Cells.Count: > 0 } run) run.Pending.Add(a);
        else m.Out.Add(a);
    }

    // ------------------------------------------------------------------ links

    /// <summary>Moves the path where a link goes. False when the path ended (exit, a dead end).</summary>
    private bool ApplyLink(M m, DvdLink link)
    {
        var pgc = PgcOf(m);
        void Button(int b)
        {
            if (b == 0) return;
            SetHighlightKnown(m, b);
            // A program choosing the button a menu will show: kept for the remote's Menu key during a title.
            if (TitleSetsHighlight && m.Phase is Phase.Pre or Phase.Post or Phase.CellCommand) Emit(m, Set(Use("s8"), b << 10));
        }

        switch (link.Cmd)
        {
            case LinkCmd.NoLink:
                m.Phase = Phase.PlayCell;
                return true;
            case LinkCmd.LinkTopC:
                Button(link.Data1);
                m.Phase = Phase.PlayCell;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.LinkNextC:
                Button(link.Data1);
                m.Cell++;
                m.Phase = Phase.PlayCell;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.LinkPrevC:
                Button(link.Data1);
                m.Cell = Math.Max(1, m.Cell - 1);
                m.Phase = Phase.PlayCell;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.LinkTopPG:
                Button(link.Data1);
                m.Phase = Phase.PlayProgram;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.LinkNextPG:
                Button(link.Data1);
                m.Pgn++;
                m.Phase = Phase.PlayProgram;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.LinkPrevPG:
                Button(link.Data1);
                m.Pgn = Math.Max(1, m.Pgn - 1);
                m.Phase = Phase.PlayProgram;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.LinkTopPGC:
                Button(link.Data1);
                m.Pgn = 1;
                m.Phase = Phase.EnterPgc;
                return true;
            case LinkCmd.LinkNextPGC:
            case LinkCmd.LinkPrevPGC:
            case LinkCmd.LinkGoUpPGC:
                {
                    Button(link.Data1);
                    var target = link.Cmd switch
                    {
                        LinkCmd.LinkNextPGC => pgc?.NextPgc ?? 0,
                        LinkCmd.LinkPrevPGC => pgc?.PrevPgc ?? 0,
                        _ => pgc?.GoUpPgc ?? 0,
                    };
                    if (target == 0) { EndRun(m); return false; }
                    m.Pgcn = target;
                    m.Pgn = 1;
                    m.Phase = Phase.EnterPgc;
                    return true;
                }
            case LinkCmd.LinkTailPGC:
                Button(link.Data1);
                ToPost(m);
                return true;
            case LinkCmd.LinkPGCN:
                m.Pgcn = link.Data1;
                m.Pgn = 1;
                m.Phase = Phase.EnterPgc;
                return true;
            case LinkCmd.LinkPTTN:
                {
                    Button(link.Data2);
                    if (m.Domain != DvdDomain.Title) { m.Phase = Phase.PlayCell; return true; }
                    var vtsTtn = Single(m.S[5]) ?? (ushort)_disc.FindVtsTtnForPgc(m.Vtsn, m.Pgcn);
                    if (_disc.GetPtt(m.Vtsn, vtsTtn, link.Data1) is not { } p) { EndRun(m); return false; }
                    m.Pgcn = p.Pgcn;
                    m.Pgn = p.Pgn;
                    StaticSprm(m, 7, link.Data1);
                    m.Phase = Phase.PlayProgram;
                    return true;
                }
            case LinkCmd.LinkPGN:
                Button(link.Data2);
                m.Pgn = link.Data1;
                m.Phase = Phase.PlayProgram;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.LinkCN:
                Button(link.Data2);
                m.Cell = link.Data1;
                m.Phase = Phase.PlayCell;
                m.LinkedInPgc = true;
                return true;
            case LinkCmd.Exit:
                EndRun(m);
                if (m.Dry is null) m.Out.Add(new ExitAction());
                return false;
            case LinkCmd.JumpTT:
                return EnterTitle(m, link.Data1, 1);
            case LinkCmd.JumpVTS_TT:
            case LinkCmd.JumpVTS_PTT:
                {
                    var ttn = _disc.FindTtn(m.Vtsn, link.Data1);
                    if (ttn == 0) { EndRun(m); return false; }
                    return EnterTitle(m, ttn, link.Cmd == LinkCmd.JumpVTS_PTT ? Math.Max(1, link.Data2) : 1);
                }
            case LinkCmd.JumpSS_FP:
            case LinkCmd.CallSS_FP:
                LeaveTitle(m, link.Cmd == LinkCmd.CallSS_FP, link.Data1);
                m.Domain = DvdDomain.FirstPlay;
                m.Vtsn = 0;
                m.Pgcn = 1;
                m.Pgn = 1;
                m.Phase = Phase.EnterPgc;
                return true;
            case LinkCmd.JumpSS_VMGM_MENU:
            case LinkCmd.CallSS_VMGM_MENU:
                {
                    if (_disc.FindMenuPgc(DvdDomain.VmgMenu, 0, (MenuType)link.Data1, 0) == 0 &&
                        _disc.FindMenuPgc(DvdDomain.VmgMenu, 0, (MenuType)link.Data1) == 0) { EndRun(m); return false; }
                    LeaveTitle(m, link.Cmd == LinkCmd.CallSS_VMGM_MENU, link.Data2);
                    return EnterMenuDomain(m, DvdDomain.VmgMenu, 0, unit => _disc.FindMenuPgc(DvdDomain.VmgMenu, 0, (MenuType)link.Data1, unit));
                }
            case LinkCmd.JumpSS_VTSM:
            case LinkCmd.CallSS_VTSM:
                {
                    int vtsn, menu;
                    if (link.Cmd == LinkCmd.JumpSS_VTSM)
                    {
                        vtsn = link.Data1 != 0 ? link.Data1 : m.Vtsn;
                        menu = link.Data3;
                    }
                    else
                    {
                        vtsn = m.Vtsn;
                        menu = link.Data1;
                    }
                    if (_disc.MenuUnits(DvdDomain.VtsMenu, vtsn).Count == 0) { EndRun(m); return false; }
                    LeaveTitle(m, link.Cmd == LinkCmd.CallSS_VTSM, link.Data2);
                    if (link.Cmd == LinkCmd.JumpSS_VTSM && link.Data1 != 0 && link.Data2 != 0)
                    {
                        StaticSprm(m, 5, link.Data2);
                        var t = _disc.FindTtn(vtsn, link.Data2);
                        if (t > 0) StaticSprm(m, 4, t);
                    }
                    return EnterMenuDomain(m, DvdDomain.VtsMenu, vtsn, unit => _disc.FindMenuPgc(DvdDomain.VtsMenu, vtsn, (MenuType)menu, unit));
                }
            case LinkCmd.JumpSS_VMGM_PGC:
            case LinkCmd.CallSS_VMGM_PGC:
                LeaveTitle(m, link.Cmd == LinkCmd.CallSS_VMGM_PGC, link.Data2);
                return EnterMenuDomain(m, DvdDomain.VmgMenu, 0, _ => link.Data1);
            default:
                EndRun(m);
                return false;
        }
    }

    private Pgc? PgcOf(M m) => _disc.GetPgc(m.Domain, m.Vtsn, m.Pgcn, m.Domain is DvdDomain.VmgMenu or DvdDomain.VtsMenu ? m.Unit : -1);

    /// <summary>
    /// Enters a menu domain. A player uses the language unit whose language is the viewer's menu language
    /// (SPRM 0), else the first; with several units that's decided at playback, so each unit is a branch.
    /// False when the path was split (or ended).
    /// </summary>
    private bool EnterMenuDomain(M m, DvdDomain domain, int vtsn, Func<int, int> pgcnOf)
    {
        var units = _disc.MenuUnits(domain, vtsn);
        void Enter(M x, int unit, int pgcn)
        {
            x.Domain = domain;
            x.Vtsn = vtsn;
            x.Unit = unit;
            x.Pgcn = pgcn;
            x.Pgn = 1;
            x.Phase = Phase.EnterPgc;
        }

        // Which unit: by the menu language, matched against each unit's.
        var codes = units.Select((u, i) => (Index: i, Code: u.Language is { Length: 2 } l && char.IsLetter(l[0]) && char.IsLetter(l[1]) ? Packed(l.ToLowerInvariant()) : -1)).ToList();
        int Choose(int s0) => codes.Where(c => c.Code == s0).Select(c => c.Index).DefaultIfEmpty(0).First();
        if (units.Count <= 1 || codes.Select(c => c.Code).Where(c => c >= 0).Distinct().Count() <= 1 && codes.All(c => c.Code == codes[0].Code))
        {
            var pgcn = pgcnOf(0);
            if (pgcn <= 0) { EndRun(m); return false; }
            Enter(m, 0, pgcn);
            return true;
        }
        _sprmsRead.Add(0);
        if (Known(m, new Operand(OperandKind.Sprm, 0)) is { } known)
        {
            var unit = Choose(known);
            var pgcn = pgcnOf(unit);
            if (pgcn <= 0) { EndRun(m); return false; }
            Enter(m, unit, pgcn);
            return true;
        }

        // Unknown at translation: one branch per unit with its own language, the first unit otherwise.
        var language = VarOf(m, new Operand(OperandKind.Sprm, 0));
        MenuAction? chain = null;
        var otherwise = m.Clone();
        var others = codes.Where(c => c.Code >= 0).Select(c => c.Code).ToList();
        if (pgcnOf(0) is var p0 and > 0)
        {
            Enter(otherwise, 0, p0);
            chain = Block(Follow(otherwise));
        }
        foreach (var (index, code) in codes.Where(c => c.Code >= 0 && c.Index > 0).Reverse())
        {
            if (codes.First(c => c.Code == code).Index != index) continue; // a language listed twice: the first wins
            var branch = m.Clone();
            branch.S[0] = One(code);
            var pgcn = pgcnOf(index);
            if (pgcn <= 0) continue;
            Enter(branch, index, pgcn);
            var then = Block(Follow(branch)) ?? Set(Use("_none"), 0);
            chain = new IfAction { Test = new Test.Compare(language, "eq", code), Then = then, Else = chain };
        }
        if (chain is not null) m.Out.Add(chain);
        return false;
    }

    private bool EnterTitle(M m, int ttn, int chapter)
    {
        var t = _disc.GetTitle(ttn);
        var ptt = t is null ? null : _disc.GetPtt(t.Vtsn, t.VtsTtn, chapter) ?? _disc.GetPtt(t.Vtsn, t.VtsTtn, 1);
        if (t is null || ptt is not { } p)
        {
            EndRun(m);
            return false;
        }
        // Another title straight after one that played (a play-all chaining episodes, maybe through a menu
        // program that shows nothing): playback goes on, as one composite play.
        if (m.Run is not null) m.Run.LeftByCall = false;
        m.Domain = DvdDomain.Title;
        m.Vtsn = t.Vtsn;
        StaticSprm(m, 4, ttn);
        StaticSprm(m, 5, t.VtsTtn);
        StaticSprm(m, 7, chapter);
        m.Pgcn = p.Pgcn;
        m.Pgn = p.Pgn;
        m.Phase = Phase.EnterPgc;
        return true;
    }

    /// <summary>A title-position register (SPRM 4-7) the program now knows; kept in its variable if the disc reads it.</summary>
    private void StaticSprm(M m, int n, int value)
    {
        if (Single(m.S[n]) == value) return;
        m.S[n] = One(value);
        // During a title the player keeps them (read through PlayerPositionAction where the value isn't known
        // here). A link outside playback (a title's start, a jump to a title set's menu) sets them in the
        // program's own copy, which is then the current one until playback starts.
        if (m.Run is null && TracksPosition && _sprmsRead.Contains(n))
        {
            Emit(m, Set(Use($"s{n}"), value));
            Emit(m, Set(Use(PositionFlagOf(n)), 1));
        }
    }

    private void TitleRegisters(M m)
    {
        StaticSprm(m, 6, m.Pgcn);
        var vtsTtn = Single(m.S[5]) is { } k && _disc.GetPtt(m.Vtsn, k, 1) is not null && _disc.TitleSets.TryGetValue(m.Vtsn, out var vts) &&
                     k <= vts.Ptts.Count && vts.Ptts[k - 1].Any(p => p.Pgcn == m.Pgcn)
            ? k
            : _disc.FindVtsTtnForPgc(m.Vtsn, m.Pgcn);
        if (vtsTtn > 0)
        {
            StaticSprm(m, 5, vtsTtn);
            var ttn = _disc.FindTtn(m.Vtsn, vtsTtn);
            if (ttn > 0) StaticSprm(m, 4, ttn);
        }
    }

    private void SetChapter(M m)
    {
        if (Single(m.S[5]) is { } vtsTtn && _disc.FindPtt(m.Vtsn, vtsTtn, m.Pgcn, m.Pgn) is var ptt and > 0) StaticSprm(m, 7, ptt);
    }

    /// <summary>
    /// A link out of the title domain ends the title's playback. A call (CallSS) leaves it remembered for
    /// resume, which the format does with a cue that opens the menu at that moment.
    /// </summary>
    private void LeaveTitle(M m, bool call, int resumeCell)
    {
        if (m.Run is not null) m.Run.LeftByCall = call;
        if (m.Domain != DvdDomain.Title || m.Dry is not null) return;
        if (!call)
        {
            // A jump out of a title leaves nothing to resume.
            Emit(m, new ClearResumeAction());
            return;
        }
        // A call saves the resume point: the cell it names (else this one), kept until a resume uses it
        // (resumePoint "untilUsed"), also when the title has played to its end. Where that is in the file is
        // known once the run is mapped (EndRun fills it in); until then it's "here".
        var save = new SaveResumeAction();
        var target = new PlayedCellRef(m.Vtsn, m.Pgcn, resumeCell != 0 ? resumeCell : m.Cell);
        _resumeCells[save] = target;
        // Before anything of the title has played (a call in its pre commands): the file and time of the cell.
        if (m.Run is not { Cells.Count: > 0 } && _disc.GetPgc(DvdDomain.Title, target.Vtsn, target.Pgcn) is { } pgc &&
            target.Cell >= 1 && target.Cell <= pgc.Cells.Count)
        {
            var c = pgc.Cells[target.Cell - 1];
            if (_media.Map(new[] { new PlayedCell(target.Vtsn, target.Pgcn, target.Cell, c.FirstSector, c.LastSector, c.Duration.TotalSeconds) }) is { } plan)
                SaveWithMedia(save, plan.Media, Math.Round(plan.Start, 3), target);
        }
        Emit(m, save);
    }

    private readonly record struct PlayedCellRef(int Vtsn, int Pgcn, int Cell);

    /// <summary>About one video frame: where a cell's last frame is, before the next cell starts.</summary>
    private const double FrameSeconds = 0.04;

    /// <summary>The cell each saveResume names, for placing it on the played media's timeline.</summary>
    private readonly Dictionary<SaveResumeAction, PlayedCellRef> _resumeCells = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Puts each saveResume a run's commands made at its cell's place in the play: the media and the time the
    /// cell starts on it (a call's resume cell, possibly the next one).
    /// </summary>
    private void PlaceResumes(IEnumerable<MenuAction> actions, TitleRun run, PlaybackPlan plan, bool playing)
    {
        foreach (var save in actions.SelectMany(a => Walk(a)).OfType<SaveResumeAction>())
        {
            if (!_resumeCells.TryGetValue(save, out var cell)) continue;
            var i = run.Cells.FindIndex(c => c.Vtsn == cell.Vtsn && c.Pgcn == cell.Pgcn && c.Cell == cell.Cell);
            // Not among the cells played (the call names an earlier one, say cell 1): where the media holds it. Not
            // "here", even during playback: the playing item ends with these cells and carries their cues, while the
            // resumed title plays on from that cell with its own (Baby Genius: each song's cell calls a menu that
            // resumes at cell 1).
            if (i < 0 && _disc.GetPgc(DvdDomain.Title, cell.Vtsn, cell.Pgcn) is { } rp && cell.Cell >= 1 && cell.Cell <= rp.Cells.Count &&
                _media.StartOf(plan.Media, cell.Vtsn, rp.Cells[cell.Cell - 1].FirstSector, rp.Cells[cell.Cell - 1].LastSector) is { } held)
            {
                SaveWithMedia(save, plan.Media, held, cell);
                continue;
            }
            if (i < 0)
            {
                // A resume cell after those played: the cell after the last one played (CallSS rsm = next cell).
                var last = run.Cells.FindLastIndex(c => c.Vtsn == cell.Vtsn && c.Pgcn == cell.Pgcn && c.Cell < cell.Cell);
                if (last < 0 || plan.CellEnds[last] is not { } end) continue;
                if (playing) save.At = Math.Round(end, 3);
                else SaveWithMedia(save, plan.Media, Math.Round(end, 3), cell);
                continue;
            }
            var start = i == 0 ? plan.Start : plan.CellEnds[i - 1];
            if (start is null) continue;
            if (playing) save.At = Math.Round(start.Value, 3);
            else SaveWithMedia(save, plan.Media, Math.Round(start.Value, 3), cell);
        }
    }

    /// <summary>A deep copy (an action tree may only appear once in the manifest's object graph).</summary>
    private static MenuAction Clone(MenuAction a) =>
        System.Text.Json.JsonSerializer.Deserialize<MenuAction>(System.Text.Json.JsonSerializer.Serialize(a, DeepJson), DeepJson)!;

    /// <summary>The format's JSON settings without the 64-level nesting limit, for working copies.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions DeepJson = new(Manifest.JsonOptions) { MaxDepth = 4096, WriteIndented = false };

    // ================================================================== title playback

    private void PlayTitleCell(M m, Pgc pgc, CellPlayback cell)
    {
        m.Navigations++;
        if (m.Navigated is { } flag) flag.Set = true;
        // Within an angle block, the first angle stands for the block (the files hold each angle in place).
        var first = m.Cell;
        if (cell.IsAngleBlock)
            while (first > 1 && pgc.Cells[first - 1].BlockMode != 1 && pgc.Cells[first - 2].IsAngleBlock) first--;
        var c = pgc.Cells[first - 1];
        var played = new PlayedCell(m.Vtsn, m.Pgcn, first, c.FirstSector, c.LastSector, c.Duration.TotalSeconds);

        if (m.Run is null)
        {
            // Leaving a menu for a title (its timeout, say) keeps the button the menu showed in SPRM 8, which the
            // remote's keys read during the title (The Wiggles: a menu that times out into a song).
            if (m.Hl == HlKind.Focus)
            {
                FocusToS8(m);
                m.Hl = HlKind.Variable;
            }
            m.Run = new TitleRun();
            m.LeadIn.Clear();
        }
        var run = m.Run;
        // Playback that comes back to a cell with the registers as they were is a loop: it ends there.
        static string Key(ushort[]?[] r) => string.Join(";", r.Select(v => v is null ? "?" : string.Join(",", v)));
        var at = $"{m.Vtsn}:{m.Pgcn}:{m.Cell}:{Key(m.G)}:{Key(m.S)}";
        if (!run.Seen.Add(at))
        {
            // Back to a cell with the registers as they were: the disc loops here for as long as nothing
            // interrupts it. The play seeks back to that cell at the end of the last one, and never ends.
            run.LoopsTo = run.Cells.FindIndex(x => x.Vtsn == played.Vtsn && x.Pgcn == played.Pgcn && x.Cell == played.Cell);
            if (run.LoopsTo < 0) run.LoopsTo = null;
            EndRun(m);
            return;
        }
        if (run.Cells.Count > 2000)
        {
            EndRun(m);
            return;
        }
        if (run.Pending.Count > 0)
        {
            run.Cues.Add((run.Cells.Count, run.Pending));
            run.Pending = new();
        }
        run.Cells.Add(played);
        run.Titles.Add((Single(m.S[4]) ?? 0, Single(m.S[5]) ?? 0));
        // A still cell holds its last frame for its still time, or until the viewer goes on.
        if (cell.StillTime != 0) run.Holds.Add((run.Cells.Count - 1, cell.StillTime == 0xFF ? HoldTime.Input : (double)cell.StillTime));
    }

    /// <summary>
    /// Ends title playback on this path: emits the play of the cells it went through, and makes what comes
    /// next its after (or, for a call out of the title, a cue at that point).
    /// </summary>
    /// <summary>
    /// Ends playback at this point and continues the path in its after (cell playback picks up again as a
    /// new play from the next cell).
    /// </summary>
    private void SplitRun(M m)
    {
        var call = m.Run?.LeftByCall ?? false;
        EndRun(m, asCue: call);
    }

    private void EndRun(M m, bool asCue = false)
    {
        if (m.Run is { Cells.Count: > 0 })
        {
            m.Navigations++;
            if (m.Navigated is { } flag) flag.Set = true;
        }
        var run = m.Run;
        if (run is null)
        {
            m.LeadIn.Clear();
            return;
        }
        m.Run = null;
        if (run.Cells.Count == 0)
        {
            foreach (var a in run.Pending) m.Out.Add(a);
            return;
        }

        var plan = _media.Map(run.Cells);
        if (_options.Trace is { } trace5) trace5($"  run of {run.Cells.Count} cells ({string.Join(",", run.Cells.Take(4).Select(c => $"v{c.Vtsn}p{c.Pgcn}c{c.Cell}:{c.FirstSector}-{c.LastSector}"))}) -> {plan?.Media ?? "unmapped"}");
        if (plan is null)
        {
            var seconds = run.Cells.Sum(c => c.Seconds);
            foreach (var t in TitlesOf(run.Cells)) UnrippedTitles.Add(t);
            foreach (var c in run.Cells.Where(c => _disc.FindVtsTtnForPgc(c.Vtsn, c.Pgcn) == 0)) UnrippedOrphans.Add((c.Vtsn, c.Pgcn));
            if (seconds < _options.SkipUnrippedBelow)
            {
                // A warning or logo that wasn't ripped: go on as if it played.
                foreach (var a in run.Pending) m.Out.Add(a);
                return;
            }
            UnavailablePlays++;
            foreach (var t in TitlesOf(run.Cells)) MissingTitles.Add(t);
            m.Out = new List<MenuAction>(); // the rest of this path is dropped with the play
            return;
        }

        plan = RecordPositions(run, plan);
        PlaceResumes(run.Cues.SelectMany(c => c.Actions), run, plan, playing: true);
        // What follows the run's cells: run by a cue while the title still plays (a call out of it, a loop), saves
        // say "here"; after the title has ended (its post commands), a save names its media, and what follows it
        // when resumed.
        var lastCellEnd = plan.CellEnds.LastOrDefault(e => e is not null);
        var endsAtFileEnd = plan.End is null && lastCellEnd is { } lce && lce >= plan.Start + plan.Seconds - 0.05;
        var pendingInCue = asCue && !endsAtFileEnd && lastCellEnd is not null || run.LoopsTo is not null;
        PlaceResumes(run.Pending, run, plan, playing: pendingInCue);
        var (prohibits, prohibitsAt) = RunProhibits(run, plan);
        var play = new PlayAction { Media = plan.Media, Prohibits = prohibits, ProhibitsAt = prohibitsAt, Keys = KeysFor(DvdDomain.Title, run.Cells[0].Vtsn) };
        if (plan.Start > 0.0005) play.Start = Math.Round(plan.Start, 3);
        if (plan.End is { } end) play.End = Math.Round(end, 3);
        var cues = new List<Cue>();
        foreach (var (after, actions) in run.Cues.Where(c => c.AfterCells >= 0))
        {
            if (after < 1 || plan.CellEnds[after - 1] is not { } at || at <= plan.Start + 0.0005) continue;
            if (Block(actions) is { } action) cues.Add(new PointCue { At = Math.Round(at, 3), Action = action });
        }

        // Still cells: the frame at the cell's end holds (a frame before the next cell starts).
        var holds = new List<Hold>();
        foreach (var (cellIndex, length) in run.Holds)
            if (plan.CellEnds[cellIndex] is { } cellEnd)
                holds.Add(new Hold { At = Math.Round(Math.Max(plan.Start, cellEnd - FrameSeconds), 3), For = length });
        // Stills and the buttons the title's cells carry (over the film, or a menu made of title cells) are the
        // cells' own, whatever path plays them: they go on the media, once, not on every play of it.
        var onMedia = MediaCues.TryGetValue(plan.Media, out var known) ? known : MediaCues[plan.Media] = new MediaCueSet();
        foreach (var h in holds) onMedia.Holds.TryAdd(h.At, h);
        foreach (var o in Overlays(run, plan, onMedia)) onMedia.Overlays.TryAdd(o.From, o);
        NavProhibits(run, plan, onMedia);

        // A loop: at the end of the last cell (its commands first), back to the cell it loops to. Nothing follows.
        if (run.LoopsTo is { } loopTo && plan.CellEnds.LastOrDefault(e => e is not null) is { } loopEnd)
        {
            var target = loopTo == 0 ? plan.Start : plan.CellEnds[loopTo - 1];
            if (target is { } seekTo && loopEnd > plan.Start + 0.001)
            {
                var steps = new List<MenuAction>(run.Pending) { new SeekAction { At = Math.Round(seekTo, 3) } };
                cues.Add(new PointCue { At = Math.Round(Math.Max(plan.Start + 0.001, loopEnd - FrameSeconds), 3), Action = new DoAction { Steps = steps } });
                play.Cues = cues.OrderBy(c => c is PointCue pc ? pc.At : 0).ToList();
                PlayerHasPosition(m);
                m.Out.Add(play);
                m.Out = new List<MenuAction>();
                return;
            }
        }

        var rest = new List<MenuAction>(run.Pending);
        var lastEnd = plan.CellEnds.LastOrDefault(e => e is not null);
        var atEnd = plan.End is null && lastEnd is { } le && le >= plan.Start + plan.Seconds - 0.05;
        if (asCue && !atEnd && lastEnd is { } cueAt)
        {
            // Left by a call: the cue opens what follows, and the title can be resumed from there.
            var cue = new PointCue { At = Math.Round(cueAt, 3), Action = new DoAction { Steps = rest } };
            cues.Add(cue);
            _exitCues.Add(cue);
            play.Cues = cues.Count > 0 ? cues : null;
            PlayerHasPosition(m);
            m.Out.Add(play);
            m.Out = rest;
            return;
        }
        play.Cues = cues.Count > 0 ? cues : null;
        play.After = new DoAction { Steps = rest };
        PlayerHasPosition(m);
        m.Out.Add(play);
        m.Out = rest;
    }

    /// <summary>
    /// Each set of buttons in the run's cells as an overlay over its time on the play (spec §6.3): the buttons'
    /// commands compiled where the disc runs them (in the title, the button highlighted), so a jump within the
    /// film is a seek and a call to a menu leaves playback.
    /// </summary>
    private IEnumerable<Overlay> Overlays(TitleRun run, PlaybackPlan plan, MediaCueSet done)
    {
        var overlays = new List<Overlay>();
        for (var i = 0; i < run.Cells.Count; i++)
        {
            var cell = run.Cells[i];
            if (!done.Cells.Add((cell.Vtsn, cell.Pgcn, cell.Cell, i == 0 ? plan.Start : plan.CellEnds[i - 1]))) continue;
            var sets = _disc.TitleCellHighlights(cell.Vtsn, cell.Pgcn, cell.Cell, _options.TitleButtonVobus);
            if (sets.Count == 0) continue;
            var start = i == 0 ? plan.Start : plan.CellEnds[i - 1];
            if (start is not { } cellStart || plan.CellEnds[i] is not { } cellEnd) continue;
            var video = _disc.TitleSets.TryGetValue(cell.Vtsn, out var vts) ? vts.TitleVideo : new VideoAttributes();
            for (var k = 0; k < sets.Count; k++)
            {
                var hl = sets[k];
                var from = cellStart + hl.ButtonsAt.TotalSeconds;
                // The same buttons sent again (navigation packs repeat their highlight) are one overlay.
                var signature = ButtonSignature(hl);
                var last = hl;
                while (k + 1 < sets.Count && ButtonSignature(sets[k + 1]) == signature && sets[k + 1].ButtonsAt >= (last.ButtonsUntil ?? sets[k + 1].ButtonsAt))
                    last = sets[++k];
                var to = Math.Min(cellEnd, last.ButtonsUntil is { } until ? cellStart + until.TotalSeconds : cellEnd);
                if (k + 1 < sets.Count) to = Math.Min(to, cellStart + sets[k + 1].ButtonsAt.TotalSeconds);
                // A cell that holds (a still, or a menu made of a cell with no running time) shows its last
                // buttons while it holds: at least a frame, covering the hold.
                var heldCell = _disc.GetPgc(DvdDomain.Title, cell.Vtsn, cell.Pgcn)?.Cells.ElementAtOrDefault(cell.Cell - 1) is { StillTime: not 0 };
                if (heldCell && k + 1 == sets.Count) to = Math.Max(to, Math.Max(from, cellEnd - FrameSeconds) + FrameSeconds);
                if (to <= from + 0.001) continue;
                var numbers = hl.Buttons.Select(b => b.Number).ToHashSet();
                var buttons = hl.Buttons.Select(b => new Button
                {
                    Id = $"b{b.Number}",
                    Rect = new Rect(b.X0, b.Y0, b.Width, b.Height),
                    AutoAction = b.AutoAction ? true : null,
                    Nav = OverlayNav(b, numbers),
                    Action = OverlayButton(cell, hl, b, plan.Media, plan.End),
                }).ToList();
                overlays.Add(new Overlay
                {
                    From = Math.Round(from, 3),
                    To = Math.Round(to, 3),
                    Canvas = new Canvas { Width = video.Width, Height = video.Height, Aspect = video.IsWide ? "16:9" : "4:3" },
                    Buttons = buttons,
                    // Without a forced select the disc keeps the button SPRM 8 names (when it's one of these): _hl, its id.
                    DefaultButton = hl.ForcedSelect > 0 && numbers.Contains(hl.ForcedSelect) ? $"b{hl.ForcedSelect}" : ButtonRef.FromVar(HighlightIdVar),
                });
            }
        }
        return overlays;
    }

    private static Nav? OverlayNav(DvdButton b, HashSet<int> numbers)
    {
        string? To(int n) => n != b.Number && numbers.Contains(n) ? $"b{n}" : null;
        var nav = new Nav { Up = To(b.Up), Down = To(b.Down), Left = To(b.Left), Right = To(b.Right) };
        return nav.Up is null && nav.Down is null && nav.Left is null && nav.Right is null ? null : nav;
    }

    /// <summary>An overlay button's action, compiled once (it may play the title it's over, with the same overlay).</summary>
    private MenuAction OverlayButton(PlayedCell cell, HighlightInfo hl, DvdButton b, string media, double? end)
    {
        var name = $"vts{cell.Vtsn}-title-pgc{cell.Pgcn}-c{cell.Cell}-at{hl.NavSector}-b{b.Number}" + (media.StartsWith('t') || media.StartsWith("play") ? $"-{media}" : "");
        if (!_overlayNames.Contains(name))
        {
            _overlayNames.Add(name);
            _pendingOverlays.Enqueue((name, cell, b, media, end));
        }
        return new RunAction { Action = name };
    }

    private readonly HashSet<string> _overlayNames = new(StringComparer.Ordinal);
    private readonly Queue<(string Name, PlayedCell Cell, DvdButton Button, string Media, double? End)> _pendingOverlays = new();

    private void CompileOverlayButton((string Name, PlayedCell Cell, DvdButton Button, string Media, double? End) o)
    {
        var (name, cell, b, media, end) = o;
        var m = Root();
        m.Domain = DvdDomain.Title;
        m.Vtsn = cell.Vtsn;
        m.Pgcn = cell.Pgcn;
        m.Cell = cell.Cell;
        m.Pgn = _disc.GetPgc(DvdDomain.Title, cell.Vtsn, cell.Pgcn)?.ProgramOfCell(cell.Cell) ?? 1;
        m.SeekMedia = media;
        m.SeekEnd = end;
        m.SeekPgcn = cell.Pgcn;
        TitleRegisters(m);
        SetHighlightKnown(m, b.Number);
        m.Phase = Phase.Button;
        m.Instruction = DvdCommands.Decode(b.Command);
        Actions[name] = Finish(Follow(m)) ?? Set(Use("_none"), 0);
    }

    private IEnumerable<int> TitlesOf(IEnumerable<PlayedCell> cells) =>
        cells.Select(c => (c.Vtsn, c.Pgcn)).Distinct()
            .Select(p => _disc.FindTtn(p.Vtsn, _disc.FindVtsTtnForPgc(p.Vtsn, p.Pgcn)))
            .Where(t => t > 0);

    // ================================================================== menus

    private void StopAtMenu(M m, HighlightInfo hl)
    {
        m.Navigations++;
        if (m.Navigated is { } flag) flag.Set = true;
        var menu = MenuFor(new MenuCellKey(m.Domain, m.Vtsn, m.Pgcn, m.Cell, m.Unit), hl);
        var go = new GotoAction { Menu = menu.Id };
        if (hl.Buttons.Count == 0)
        {
            // A clip has no buttons to highlight: the highlight register goes on through it in s8, for the menu
            // after it.
            if (m.Hl == HlKind.Known && Single(m.S[8]) is { } known) Emit(m, Set(Use("s8"), known), toOut: true);
            else VarOf(m, new Operand(OperandKind.Sprm, 8));
        }
        else if (hl.ForcedSelect <= 0)
        {
            // A highlight the menu doesn't have falls back to its first button, as players do.
            if (m.Hl == HlKind.Known && Single(m.S[8]) is { } s8) go.Button = hl.Get(s8 >> 10) is not null ? $"b{s8 >> 10}" : "b1";
            else if (m.Hl == HlKind.Variable)
            {
                // The highlight came from a register: name its button in a variable.
                HighlightName(m, hl.Buttons.Count);
                go.Button = ButtonRef.FromVar("_hl");
            }
        }
        if (m.LeadIn.Count > 0) go.Transition = _options.Transition(m.LeadIn);
        m.LeadIn.Clear();
        m.Out.Add(go);
    }

    /// <summary>"_hl" = the id of the button s8 names (button number × 1024).</summary>
    private void HighlightName(M m, int buttons)
    {
        var count = Math.Clamp(buttons, 1, 36);
        var name = $"highlight-of-{count}";
        if (!Actions.ContainsKey(name))
        {
            var steps = new List<MenuAction> { Set(Use("_hl"), "b1") };
            foreach (var n in Enumerable.Range(2, Math.Max(0, count - 1)))
                steps.Add(new IfAction { Test = new Test.Compare(Use("s8"), "ge", n * 1024), Then = Set("_hl", $"b{n}") });
            // Past the last button: the first one.
            steps.Add(new IfAction { Test = new Test.Compare("s8", "ge", (count + 1) * 1024), Then = Set("_hl", "b1") });
            Actions[name] = new DoAction { Steps = steps };
        }
        Emit(m, new RunAction { Action = name });
    }

    private void FocusFromRegister(M m)
    {
        var menu = _disc.GetHighlight(m.Domain, m.Vtsn, m.Pgcn, m.Cell, m.Unit);
        HighlightName(m, menu?.Buttons.Count ?? 36);
        m.Out.Add(Set("@focus", Value.Of("_hl")));
    }

    private CompiledMenu MenuFor(MenuCellKey key, HighlightInfo hl)
    {
        if (_menus.TryGetValue(key, out var menu)) return menu;
        var pgc = _disc.GetPgc(key.Domain, key.Vtsn, key.Pgcn, key.Unit);
        menu = new CompiledMenu
        {
            Key = key,
            Id = key.ToString(),
            Highlight = hl,
            Video = _disc.MenuVideo(key.Domain, key.Vtsn),
            MenuType = pgc?.MenuType ?? MenuType.None,
        };
        _menus[key] = menu;
        _pending.Enqueue(menu);
        return menu;
    }

    // ================================================================== player state

    /// <summary>Which system registers any command on the disc reads, so only those get variables.</summary>
    private void FindSprmReads()
    {
        IEnumerable<byte[]> Commands(Pgc p) => p.PreCommands.Concat(p.PostCommands).Concat(p.CellCommands);
        var pgcs = new List<Pgc>();
        if (_disc.Vmg.FirstPlay is { } fp) pgcs.Add(fp);
        pgcs.AddRange(_disc.Vmg.Menus.SelectMany(u => u.Pgcs));
        foreach (var v in _disc.TitleSets.Values)
        {
            pgcs.AddRange(v.Menus.SelectMany(u => u.Pgcs));
            pgcs.AddRange(v.TitlePgcs);
        }
        var commands = pgcs.SelectMany(Commands).ToList();
        // Button commands live in the menu VOBs' navigation packs.
        foreach (var (domain, vtsn, unit, units) in MenuUnits())
            for (var p = 1; p <= units.Pgcs.Count; p++)
                for (var c = 1; c <= units.Pgcs[p - 1].Cells.Count; c++)
                    if (_disc.GetHighlight(domain, vtsn, p, c, unit) is { } hl)
                        commands.AddRange(hl.Buttons.Select(b => b.Command));
        // And in title cells: buttons over a title (The Incredibles D1's bonus pages set g9 for the menu to read).
        foreach (var (vtsn, vts) in _disc.TitleSets)
            for (var p = 1; p <= vts.TitlePgcs.Count; p++)
                for (var c = 1; c <= vts.TitlePgcs[p - 1].Cells.Count; c++)
                    foreach (var hl in _disc.TitleCellHighlights(vtsn, p, c, _options.TitleButtonVobus))
                        commands.AddRange(hl.Buttons.Select(b => b.Command));
        var decoded = commands.Select(DvdCommands.Decode).ToList();
        foreach (var ins in decoded)
            foreach (var o in ins.Reads())
                if (o.Kind == OperandKind.Sprm && o.Value != 8)
                    _sprmsRead.Add(o.Value);
        FindRegisterValues(decoded);
    }

    /// <summary>Most values a whole-disc register set keeps (more, and the register counts as unknown).</summary>
    private const int MaxGlobalValues = 64;

    /// <summary>
    /// Every value each general register can ever hold, anywhere on the disc (null = any): it starts at 0
    /// and only changes by the disc's commands, so a register only ever given constants is one of those
    /// constants wherever a path picks it up. Paths start from these instead of "unknown", which settles
    /// tests against values the disc never uses.
    /// </summary>
    private void FindRegisterValues(List<DvdInstruction> program)
    {
        var sets = Enumerable.Range(0, 16).Select(_ => new HashSet<ushort> { 0 }).ToArray();
        var any = new bool[16];
        HashSet<ushort>? Values(Operand o) => o.Kind switch
        {
            OperandKind.Immediate => new HashSet<ushort> { (ushort)o.Value },
            OperandKind.Gprm => any[o.Value] ? null : sets[o.Value],
            _ => null,
        };
        bool Add(int reg, IEnumerable<ushort>? values)
        {
            if (any[reg]) return false;
            if (values is null) { any[reg] = true; return true; }
            var changed = false;
            foreach (var v in values)
            {
                changed |= sets[reg].Add(v);
                if (sets[reg].Count > MaxGlobalValues) { any[reg] = true; return true; }
            }
            return changed;
        }
        for (var round = 0; round < 32; round++)
        {
            var changed = false;
            foreach (var op in program.SelectMany(i => i.AllOps()))
                switch (op)
                {
                    case SetGprmOp g:
                        {
                            var src = Values(g.Source);
                            switch (g.Op)
                            {
                                case 1:
                                    changed |= Add(g.Register, src?.ToList());
                                    break;
                                case 2:
                                    var mine = any[g.Register] ? null : sets[g.Register].ToList();
                                    changed |= Add(g.Register, src?.ToList());
                                    changed |= Add(g.SwapWith, mine);
                                    break;
                                case 8:
                                    changed |= Add(g.Register, src is { Count: 1 } && src.First() <= MaxGlobalValues
                                        ? Enumerable.Range(1, Math.Max(1, (int)src.First())).Select(v => (ushort)v).ToList()
                                        : null);
                                    break;
                                default:
                                    {
                                        var cur = any[g.Register] ? null : sets[g.Register].ToList();
                                        changed |= Add(g.Register, cur is null || src is null || cur.Count * src.Count > 4096
                                            ? null
                                            : cur.SelectMany(a => src.Select(b => Compute(g.Op, a, b))).Distinct().ToList());
                                        break;
                                    }
                            }
                            break;
                        }
                    case SetGprmModeOp md:
                        changed |= Add(md.Register, md.Counter ? null : Values(md.Value)?.ToList());
                        break;
                }
            if (!changed) break;
            if (round == 31) for (var i = 0; i < 16; i++) any[i] = true; // didn't settle: assume nothing
        }
        for (var i = 0; i < 16; i++)
            _globalG[i] = any[i] ? null : sets[i].OrderBy(v => v).ToArray();
    }

    private readonly ushort[]?[] _globalG = new ushort[]?[16];

    /// <summary>Every menu table (each language unit of each domain).</summary>
    private IEnumerable<(DvdDomain, int, int, MenuLanguageUnit)> MenuUnits()
    {
        foreach (var (u, i) in _disc.MenuUnits(DvdDomain.VmgMenu, 0).Select((u, i) => (u, i))) yield return (DvdDomain.VmgMenu, 0, i, u);
        foreach (var v in _disc.TitleSets.Keys.OrderBy(k => k))
            foreach (var (u, i) in _disc.MenuUnits(DvdDomain.VtsMenu, v).Select((u, i) => (u, i))) yield return (DvdDomain.VtsMenu, v, i, u);
    }

    private static readonly int[] PlayerSprms = { 0, 12, 13, 14, 16, 18, 20 };

    /// <summary>First play starts by copying what the player knows into the registers the disc reads.</summary>
    private List<MenuAction> PlayerSetup()
    {
        var steps = new List<MenuAction>();
        var languages = DiscLanguages();
        foreach (var n in PlayerSprms.Where(_sprmsRead.Contains))
        {
            var name = Use($"s{n}");
            switch (n)
            {
                case 0:
                case 16:
                case 18:
                    {
                        var player = n == 0 ? "@menuLanguage" : n == 16 ? "@audioLanguage" : "@subtitleLanguage";
                        foreach (var code in languages)
                            steps.Add(new IfAction { Test = new Test.Compare(player, "eq", code), Then = Set(name, Packed(code)) });
                        break;
                    }
                case 12:
                    foreach (var code in new[] { "US", "CA", "GB", "AU", "NZ", "IE", "DE", "FR", "ES", "IT", "NL", "JP" })
                        steps.Add(new IfAction { Test = new Test.Compare("@country", "eq", code), Then = Set(name, Packed(code)) });
                    break;
                case 13:
                    // DVD levels are 1-8, 15 when parental control is off; the format's 8 means unrestricted.
                    steps.Add(new IfAction { Test = new Test.Compare("@parentalLevel", "lt", 8), Then = Set(name, Value.Of("@parentalLevel")) });
                    break;
                case 14:
                    steps.Add(new IfAction { Test = new Test.Compare("@aspect", "eq", "4:3"), Then = Set(name, 0) });
                    break;
                case 20:
                    foreach (var r in Enumerable.Range(1, 8))
                        steps.Add(new IfAction { Test = new Test.Compare("@region", "eq", r), Then = Set(name, 1 << (r - 1)) });
                    break;
            }
        }
        return steps;
    }

    /// <summary>Language codes the disc has streams or menus in (what its commands compare the player's languages with).</summary>
    private List<string> DiscLanguages()
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string? c) { if (c is { Length: 2 } && char.IsLetter(c[0]) && char.IsLetter(c[1])) codes.Add(c.ToLowerInvariant()); }
        foreach (var u in _disc.Vmg.Menus) Add(u.Language);
        foreach (var v in _disc.TitleSets.Values)
        {
            foreach (var u in v.Menus) Add(u.Language);
            foreach (var a in v.Audio) Add(a.Language);
            foreach (var s in v.Subpictures) Add(s.Language);
        }
        codes.Add("en");
        return codes.ToList();
    }

    /// <summary>A two-letter code as the disc stores it: the first letter's ASCII in the high byte.</summary>
    private static int Packed(string code) => code[0] << 8 | code[1];

    /// <summary>Starting values, as a player has them before first play.</summary>
    private static Scalar StartValue(string var)
    {
        if (var.StartsWith('_')) return var == "_hl" ? "b1" : var.StartsWith(PositionFlag, StringComparison.Ordinal) ? 1 : 0;
        if (var[0] == 'g') return 0;
        var defaults = new VmState();
        return int.TryParse(var.AsSpan(1), out var n) ? defaults.GetSprm(n) : 0;
    }

    // ================================================================== tidying

    /// <summary>An action for a list of steps: nothing, the one step, or a do.</summary>
    private static MenuAction? Block(List<MenuAction> steps) => steps.Count switch
    {
        0 => null,
        1 => steps[0],
        _ => new DoAction { Steps = steps },
    };

    private MenuAction? Finish(List<MenuAction> steps) => Normalize(FixExitCues(Block(steps)), after: false);

    /// <summary>Cues that leave a title by a call (resumable), set when the title's playback ends there.</summary>
    private readonly HashSet<PointCue> _exitCues = new();

    /// <summary>
    /// A call out of a title that goes on to play something else can't be a cue (playback a cue starts
    /// returns to the title): playback ends there instead, and that is its after.
    /// </summary>
    private MenuAction? FixExitCues(MenuAction? a)
    {
        foreach (var p in Walk(a).OfType<PlayAction>().ToList())
        {
            if (p.Cues is null) continue;
            foreach (var cue in p.Cues.OfType<PointCue>().Where(_exitCues.Contains).ToList())
            {
                if (!MayPlay(cue.Action)) continue;
                p.Cues.Remove(cue);
                p.End = p.End is { } e ? Math.Min(e, cue.At) : cue.At;
                p.After = cue.Action;
            }
            if (p.Cues.Count == 0) p.Cues = null;
        }
        return a;
    }

    private static bool MayPlay(MenuAction? a) => a switch
    {
        PlayAction or PlayAllAction or ResumeAction => true,
        DoAction d => d.Steps.Any(MayPlay),
        IfAction i => MayPlay(i.Then) || MayPlay(i.Else),
        SetAction s => MayPlay(s.Then),
        _ => false,
    };

    private static IEnumerable<MenuAction> Walk(MenuAction? a) => ActionTree.Walk(a);

    /// <summary>
    /// Simplifies an action tree and makes it valid where it sits: empty blocks go, single steps unwrap, an
    /// after never is a bare play (spec §6 rule 6), and a resume's otherwise never is another resume.
    /// </summary>
    public static MenuAction? Normalize(MenuAction? a, bool after)
    {
        switch (a)
        {
            case null:
                return null;
            case DoAction d:
                {
                    var steps = new List<MenuAction>();
                    foreach (var s in d.Steps)
                    {
                        var n = Normalize(s, after: false);
                        if (n is DoAction inner) steps.AddRange(inner.Steps);
                        else if (n is not null) steps.Add(n);
                        if (n is not null && AlwaysNavigates(n)) break;
                    }
                    if (steps.Count == 0) return null;
                    if (steps.Count == 1 && !(after && steps[0] is PlayAction or PlayAllAction)) return steps[0];
                    return new DoAction { Steps = steps };
                }
            case IfAction i:
                {
                    var then = Normalize(i.Then, after: false);
                    var otherwise = Normalize(i.Else, after: false);
                    if (then is null && otherwise is null) return null;
                    if (then is null) return new IfAction { Test = Negate(i.Test), Then = otherwise! };
                    if (otherwise is null) return new IfAction { Test = i.Test, Then = then };
                    // "if A then (goes somewhere) else B" is "if A then (goes somewhere); B": a do stops at the
                    // first step that navigates. Chains of these stay flat instead of nesting ever deeper.
                    if (AlwaysNavigates(then))
                        return Normalize(new DoAction { Steps = { new IfAction { Test = i.Test, Then = then }, otherwise } }, after);
                    if (AlwaysNavigates(otherwise))
                        return Normalize(new DoAction { Steps = { new IfAction { Test = Negate(i.Test), Then = otherwise }, then } }, after);
                    return new IfAction { Test = i.Test, Then = then, Else = otherwise };
                }
            case SetAction s:
                s.Then = Normalize(s.Then, after: false);
                return s;
            case PlayAction p:
                p.After = Normalize(p.After, after: true);
                if (p.Cues is not null)
                {
                    foreach (var c in p.Cues.OfType<PointCue>()) c.Action = Normalize(c.Action, after: false)!;
                    p.Cues.RemoveAll(c => c is PointCue { Action: null });
                    if (p.Cues.Count == 0) p.Cues = null;
                }
                return after ? new DoAction { Steps = { p } } : p;
            case PlayAllAction pa:
                pa.After = Normalize(pa.After, after: true);
                return after ? new DoAction { Steps = { pa } } : pa;
            case RequestParentalAction q:
                q.Then = Normalize(q.Then, after: false) ?? Set("_none", 0);
                q.Else = Normalize(q.Else, after: false);
                return q;
            case TimerAction ta:
                ta.Action = Normalize(ta.Action, after: false) ?? Set("_none", 0);
                return ta;
            case ResumeAction r:
                {
                    var otherwise = Normalize(r.Otherwise, after);
                    while (otherwise is ResumeAction inner) otherwise = inner.Otherwise;
                    return new ResumeAction { Otherwise = otherwise };
                }
            default:
                return a;
        }
    }

    private static Test Negate(Test t) => t switch
    {
        Test.Not n => n.Test,
        Test.Compare { Op: "eq" } c => c with { Op = "ne" },
        Test.Compare { Op: "ne" } c => c with { Op = "eq" },
        Test.Compare { Op: "lt" } c => c with { Op = "ge" },
        Test.Compare { Op: "ge" } c => c with { Op = "lt" },
        Test.Compare { Op: "gt" } c => c with { Op = "le" },
        Test.Compare { Op: "le" } c => c with { Op = "gt" },
        _ => new Test.Not(t),
    };

    private static bool AlwaysNavigates(MenuAction a) => a switch
    {
        ExitAction or RequestParentalAction => true,
        SetAction s => s.Then is not null && AlwaysNavigates(s.Then),
        ResumeAction r => r.Otherwise is not null && AlwaysNavigates(r.Otherwise),
        GotoAction or PlayAction or PlayAllAction or BackAction => true,
        DoAction d => d.Steps.Any(AlwaysNavigates),
        IfAction i => AlwaysNavigates(i.Then) && i.Else is not null && AlwaysNavigates(i.Else),
        _ => false,
    };
}
