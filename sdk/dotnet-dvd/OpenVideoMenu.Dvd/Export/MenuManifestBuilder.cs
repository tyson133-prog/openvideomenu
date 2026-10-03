using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>
/// Builds an Open Video Menu manifest from a disc, following the spec's DVD import profile
/// (spec/profiles/dvd.md): one menu per menu cell with buttons, rects from the PCI coordinates, the disc's
/// neighbours, and every button, timeout and Go Up translated from the disc's own programs by
/// <see cref="DvdMenuCompiler"/> (registers as variables, conditions as logic, titles as plays of the ripped
/// files). The disc's numbers are kept in "x-dvd".
/// </summary>
public sealed class MenuManifestBuilder
{
    private readonly DvdDisc _disc;
    private readonly DiscMap? _map;
    private readonly DvdManifestOptions _options;
    private readonly DvdMenuCompiler _compiler;
    private readonly CellMediaIndex _index;
    private readonly Dictionary<string, MediaEntry> _media;
    private readonly Dictionary<string, IReadOnlyList<MenuCellRef>> _leadIns = new();
    private readonly Dictionary<MenuNode, CompiledMenu> _nodes = new();
    private readonly Dictionary<CompiledMenu, MenuNode> _nodeOf = new();

    private const string LeadInToken = "\u0001leadin:";

    public DvdWarnings Warnings { get; } = new();

    /// <summary>Buttons and paths that lead to titles that weren't ripped (kept, but inert).</summary>
    public int UnrippedButtons => _compiler.UnavailablePlays;

    /// <summary>Titles the menus play that no file holds (not counting short warnings and logos that are skipped).</summary>
    public IReadOnlyCollection<int> MissingTitles => _compiler.MissingTitles;

    /// <summary>Art per menu (Still and up); filled by the exporter before <see cref="Build"/>.</summary>
    public Dictionary<MenuNode, MenuArtSet> Art { get; } = new();

    /// <summary>Transition clip per lead-in (Full); filled by the exporter.</summary>
    public Func<IReadOnlyList<MenuCellRef>, string?> Transition { get; set; } = _ => null;

    /// <summary>
    /// A manifest of the disc over the given media: <paramref name="index"/> says which file holds each disc cell
    /// (and where in it); <paramref name="options"/> names, labels and what the files can carry.
    /// </summary>
    /// <param name="map">The disc map, when the caller has one: its menu nodes key the labels and the art.</param>
    public MenuManifestBuilder(DvdDisc disc, DiscMap? map, Dictionary<string, MediaEntry> media, CellMediaIndex index, DvdManifestOptions? options = null)
    {
        _disc = disc;
        _map = map;
        _options = options ?? new DvdManifestOptions();
        _media = media;
        _index = index;
        var transitions = _options.Transitions;
        _compiler = new DvdMenuCompiler(disc, index, new DvdCompileOptions
        {
            TrackChoices = _options.TrackChoices,
            TitleButtonVobus = _options.TitleButtonVobus,
            Transitions = transitions,
            Trace = _options.CompileTrace,
            Transition = cells =>
            {
                if (!transitions) return null;
                var key = LeadInToken + _leadIns.Count;
                _leadIns[key] = cells.ToArray();
                return key;
            },
        });
        _compiler.Compile();
        Warnings.AddRange(_compiler.Warnings);
        if (_compiler.ApproximatedConditions > 0)
            Warnings.Add(DvdWarningCode.ApproximatedConditions, $"{_compiler.ApproximatedConditions} conditions inside titles are decided when playback starts.");
        AssignIds();
    }

    public DvdMenuCompiler Compiler => _compiler;

    /// <summary>The menus that get a manifest entry, as map nodes for the art maker.</summary>
    public IEnumerable<MenuNode> Menus => _nodes.Keys;

    public string IdOf(MenuNode m) => _nodes[m].Id;

    /// <summary>
    /// The cell a timed menu runs on into by itself when that's the same menu (same buttons) that then waits
    /// or loops: an intro and its loop, made into one looping clip.
    /// </summary>
    public MenuNode? LoopContinuation(MenuNode m) =>
        _nodes[m].LoopsInto is { } next && _compiler.Menus.TryGetValue(next, out var c) && c.Timeout is null ? _nodeOf[c] : null;

    // ------------------------------------------------------------------ ids

    /// <summary>Stable ids from the profile: vmgm-pgc1, vts1-pgc2, plus -c3 when a PGC has several button cells.</summary>
    private void AssignIds()
    {
        foreach (var byPgc in _compiler.Menus.Values.GroupBy(m => (m.Key.Domain, m.Key.Vtsn, m.Key.Pgcn, m.Key.Unit)))
        {
            var multiCell = byPgc.Count() > 1;
            foreach (var m in byPgc)
            {
                // Menus in several languages: the language in the id (vts1-fr-pgc3).
                var units = _disc.MenuUnits(m.Key.Domain, m.Key.Vtsn);
                var language = units.Count > 1 ? "-" + LanguageTag(units, m.Key.Unit) : "";
                var id = (m.Key.Domain == DvdDomain.VmgMenu ? "vmgm" : $"vts{m.Key.Vtsn}") + language + $"-pgc{m.Key.Pgcn}";
                if (multiCell) id += $"-c{m.Key.Cell}";
                Rename(m, id);
            }
        }
        foreach (var m in _compiler.Menus.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var node = MapNode(m) ?? SyntheticNode(m);
            _nodes[node] = m;
            _nodeOf[m] = node;
        }
    }

    /// <summary>Gives a menu its manifest id, updating every goto the compiler wrote with the old one.</summary>
    private void Rename(CompiledMenu m, string id)
    {
        if (m.Id == id) return;
        var old = m.Id;
        m.Id = id;
        foreach (var a in AllActions()) if (a is GotoAction g && g.Menu == old) g.Menu = id;
    }

    private IEnumerable<MenuAction> AllActions()
    {
        var roots = new List<MenuAction?> { _compiler.FirstPlay };
        roots.AddRange(_compiler.Actions.Values);
        foreach (var m in _compiler.Menus.Values)
        {
            roots.AddRange(m.Buttons.Values);
            roots.AddRange(m.Windows.SelectMany(w => w.Buttons.Values));
            roots.Add(m.Timeout);
            roots.Add(m.GoUp);
        }
        foreach (var r in roots)
            if (r is not null)
                foreach (var a in Walk(r))
                    yield return a;
    }

    private static IEnumerable<MenuAction> Walk(MenuAction? a) => ActionTree.Walk(a);

    /// <summary>A language unit's tag: its language code, numbered when two units share one.</summary>
    private static string LanguageTag(IReadOnlyList<MenuLanguageUnit> units, int unit)
    {
        var code = units[unit].Language is { Length: 2 } l && l.All(char.IsLetter) ? l.ToLowerInvariant() : "lu";
        var same = units.Take(unit).Count(u => string.Equals(u.Language, units[unit].Language, StringComparison.OrdinalIgnoreCase));
        return same > 0 || code == "lu" ? $"{code}{unit + 1}" : code;
    }

    /// <summary>The scout's node for a menu (labels come from it), only for the language unit the scout walked.</summary>
    private MenuNode? MapNode(CompiledMenu m) =>
        m.Key.Unit != _disc.DefaultMenuUnit(m.Key.Domain, m.Key.Vtsn) ? null :
        _map?.Menus.FirstOrDefault(n => n.Domain == m.Key.Domain && n.Vtsn == m.Key.Vtsn && n.Pgcn == m.Key.Pgcn && n.Cell == m.Key.Cell);

    /// <summary>A map node for a menu the simulation never reached (the art maker and labels work on nodes).</summary>
    private static MenuNode SyntheticNode(CompiledMenu m)
    {
        var node = new MenuNode
        {
            Domain = m.Key.Domain,
            Vtsn = m.Key.Vtsn,
            Pgcn = m.Key.Pgcn,
            Cell = m.Key.Cell,
            MenuUnit = m.Key.Unit,
            MenuType = m.MenuType,
            Highlight = m.Highlight,
            Video = m.Video,
            LayoutKey = $"{m.Key.Domain}:{m.Key.Vtsn}:{m.Key.Pgcn}:{m.Key.Cell}",
            InitialButton = Math.Max(1, m.Highlight.ForcedSelect),
        };
        foreach (var b in m.Highlight.Buttons) node.Buttons.Add(new MenuButtonNode { Button = b });
        return node;
    }

    // ------------------------------------------------------------------ manifest

    public Manifest Build()
    {
        ResolveLeadIns();
        var media = new Dictionary<string, MediaEntry>(_media);
        foreach (var (k, v) in _index.Composites) media[k] = Describe(v);
        var manifest = new Manifest
        {
            Title = new Title
            {
                Name = string.IsNullOrWhiteSpace(_options.Title) ? "Untitled disc" : _options.Title,
                Year = _options.Year,
                Ids = _options.Ids is { Count: > 0 } ids ? new Dictionary<string, string>(ids) : null,
            },
            Source = new Source { Kind = "dvd", Generator = _options.Generator },
            Media = media,
            Fallback = _options.Fallback?.Invoke() ?? Fallback(),
        };

        if (_options.Menus && _compiler.Menus.Count > 0)
        {
            manifest.Menus = new Dictionary<string, Menu>();
            var depth = Depths();
            foreach (var m in _compiler.Menus.Values) manifest.Menus[m.Id] = BuildMenu(m, depth);
            manifest.RootMenu = RootMenu().Id;
        }
        else if (_compiler.FirstPlay is not null && _options.Menus)
            Warnings.Add(DvdWarningCode.NoMenus, "The disc has no menus with buttons.");

        // The disc's logic, also without menus with buttons (it can still have buttons in its titles' cells).
        if (_options.Menus && (_compiler.Menus.Count > 0 || _compiler.FirstPlay is not null))
        {
            manifest.FirstPlay = _compiler.FirstPlay;
            if (_compiler.Actions.Count > 0) manifest.Actions = new Dictionary<string, MenuAction>(_compiler.Actions);
            // A call out of a title keeps its resume point through other playback until a resume uses it, as on a disc.
            manifest.ResumePoint = "untilUsed";
            // The remote's Title key opens the disc's title menu; the Menu key and the others depend on the title set.
            manifest.Keys = _compiler.ManifestKeys;
            // A disc's link out of a title ends it; nothing returns to it except a resume.
            manifest.CuePlayback = "replace";
            if (_compiler.Counters.Count > 0) manifest.Counters = _compiler.Counters.ToList();
            // The disc's own logic: players below Level 3 show the fallback list instead.
            manifest.Level = 3;
            if (_compiler.Vars.Count > 0) manifest.Vars = new Dictionary<string, Scalar>(_compiler.Vars);
        }

        if (_compiler.PositionRegisters.Count > 0)
        {
            manifest.Actions ??= new Dictionary<string, MenuAction>();
            manifest.Actions[DvdMenuCompiler.PlayerPositionAction] = PlayerPosition(manifest.Media);
        }
        // The stills and buttons of the media's own cells, on the media.
        foreach (var (mediaId, set) in _compiler.MediaCues)
            if (manifest.Media.TryGetValue(mediaId, out var entry))
            {
                if (set.Overlays.Count > 0) entry.Cues = set.Overlays.Values.Cast<Cue>().ToList();
                if (set.Holds.Count > 0) entry.Holds = set.Holds.Values.ToList();
                // What the cells' navigation packs forbid, over time: neighbouring windows that forbid the same merged.
                var windows = new List<ProhibitWindow>();
                foreach (var w in set.Windows.Values)
                    if (windows.Count > 0 && windows[^1].To is { } to && Math.Abs(to - w.From) < 0.002 && windows[^1].Ops.SequenceEqual(w.Ops))
                        windows[^1].To = w.To;
                    else
                        windows.Add(new ProhibitWindow { From = w.From, To = w.To, Ops = w.Ops });
                if (windows.Count > 0) entry.ProhibitsAt = windows;
            }
        _compiler.FillResumeItems(() => ActionTidy.AllActions(manifest));
        ActionTidy.TrackHighlightId(manifest);
        var passes = _options.Passes;
        if (passes.HasFlag(ManifestPasses.HoistToMedia)) ActionTidy.HoistToMedia(manifest);
        var read = passes.HasFlag(ManifestPasses.DropUnreadSets) ? ActionTidy.DropUnreadSets(manifest) : null;
        // What a resumed title does that its resume point can't carry, unless it only sets variables nothing reads.
        foreach (var loss in _compiler.ResumeLosses)
            if (loss.Other || read is null || loss.Sets.Overlaps(read))
                Warnings.Add(DvdWarningCode.ResumeLoss, loss.Where);
        // The same logic written once.
        if (passes.HasFlag(ManifestPasses.MergeIdentical)) manifest = ActionTidy.MergeIdenticalActions(manifest);
        // Logic written out in many places, once (and tests whose branches agree, as one).
        if (passes.HasFlag(ManifestPasses.ShareRepeated))
        {
            manifest = ActionShare.Compact(manifest);
            if (passes.HasFlag(ManifestPasses.MergeIdentical)) manifest = ActionTidy.MergeIdenticalActions(manifest);
        }
        // A named action that is just a play can be run as an after-action, which may only play through a do.
        if (manifest.Actions is not null)
            foreach (var name in manifest.Actions.Keys.ToList())
                if (manifest.Actions[name] is PlayAction or PlayAllAction)
                    manifest.Actions[name] = new DoAction { Steps = { manifest.Actions[name] } };
        // The do-nothing step ("set _none") needs its variable declared wherever it ended up.
        if (manifest.ToJsonDeep().Contains("\"_none\""))
        {
            manifest.Vars ??= new Dictionary<string, Scalar>();
            manifest.Vars.TryAdd("_none", 0);
        }
        return manifest;
    }

    /// <summary>A composite play (a run of cells spread over several files) named after its first part.</summary>
    /// <summary>
    /// The shared action that sets the title-position registers the disc reads (SPRM 4-7: title, title-set
    /// title, PGC, chapter) from the player's live position (spec §6.1): which media is playing (or played last)
    /// and its chapter. Each media's chapters map to the disc title and chapter they play, as the runs that
    /// play the media recorded; a media whose chapters follow the title's one for one reads the chapter directly.
    /// </summary>
    private MenuAction PlayerPosition(Dictionary<string, MediaEntry> media)
    {
        var regs = _compiler.PositionRegisters;
        var steps = new List<MenuAction>();
        foreach (var (id, positions) in _compiler.MediaPositions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (positions.Count == 0) continue;
            var times = media.TryGetValue(id, out var entry) && entry.Chapters is { Count: > 0 } chapters
                ? chapters.Select(c => c.Time).ToList()
                : new List<double> { 0 };
            if (times[0] > 0.001) times.Insert(0, 0);
            var numbers = new List<(int Ttn, int VtsTtn, int Pgcn, int Ptt)?>();
            for (var k = 0; k < times.Count; k++)
            {
                var end = k + 1 < times.Count ? times[k + 1] : double.MaxValue;
                // The disc's chapter at the media chapter's start, from the cells the runs played there.
                var inChapter = positions.Where(p => p.Key >= times[k] - 0.001 && p.Key < end - 0.001).ToList();
                var at = inChapter.Count > 0 ? inChapter[0].Value : positions.LastOrDefault(p => p.Key <= times[k] + 0.001).Value;
                if (at is null) { numbers.Add(null); continue; }
                var pttAt = Ptt(at);
                if (inChapter.Select(p => Ptt(p.Value)).Distinct().Count() > 1)
                    Warnings.Add(DvdWarningCode.ChapterAmbiguous, $"{id} chapter {k + 1} holds more than one of the disc's chapters; it reads as chapter {pttAt}.");
                numbers.Add((at.Ttn, at.VtsTtn, at.Pgcn, pttAt));
            }
            if (numbers.All(n => n is null)) continue;

            MenuAction Sets((int Ttn, int VtsTtn, int Pgcn, int Ptt) n, int? chapterOffset)
            {
                // Each register only where the player's position is the current one (no link set it since).
                MenuAction Only(int r, params MenuAction[] set) => new IfAction
                {
                    Test = new Test.Compare(DvdMenuCompiler.PositionFlagOf(r), "eq", 0),
                    Then = set.Length == 1 ? set[0] : new DoAction { Steps = set.ToList() },
                };
                var sets = new List<MenuAction>();
                if (regs.Contains(4)) sets.Add(Only(4, new SetAction { Var = "s4", Value = n.Ttn }));
                if (regs.Contains(5)) sets.Add(Only(5, new SetAction { Var = "s5", Value = n.VtsTtn }));
                if (regs.Contains(6)) sets.Add(Only(6, new SetAction { Var = "s6", Value = n.Pgcn }));
                if (regs.Contains(7))
                {
                    if (chapterOffset is { } off)
                        sets.Add(off != 0
                            ? Only(7, new SetAction { Var = "s7", Value = Value.Of("@lastChapter") }, new SetAction { Var = "s7", Op = "add", Value = off })
                            : Only(7, new SetAction { Var = "s7", Value = Value.Of("@lastChapter") }));
                    else sets.Add(Only(7, new SetAction { Var = "s7", Value = n.Ptt }));
                }
                return sets.Count == 1 ? sets[0] : new DoAction { Steps = sets };
            }

            var first = numbers.First(n => n is not null)!.Value;
            var linear = numbers.Select((n, k) => (n, k)).All(x => x.n is { } v && v.Ttn == first.Ttn && v.VtsTtn == first.VtsTtn &&
                                                                    v.Pgcn == first.Pgcn && v.Ptt == first.Ptt + x.k - numbers.IndexOf(first));
            MenuAction body;
            if (linear || numbers.Count == 1)
                body = Sets(first, numbers.Count == 1 ? null : first.Ptt - 1 - numbers.IndexOf(first));
            else
                body = new DoAction
                {
                    Steps = numbers.Select((n, k) => (n, k)).Where(x => x.n is not null)
                        .Select(x => (MenuAction)new IfAction { Test = new Test.Compare("@lastChapter", "eq", x.k + 1), Then = Sets(x.n!.Value, null) })
                        .ToList(),
                };
            steps.Add(new IfAction { Test = new Test.Compare("@lastMedia", "eq", id), Then = body });
        }
        return steps.Count == 0 ? new SetAction { Var = "_none", Value = 0 } : steps.Count == 1 ? steps[0] : new DoAction { Steps = steps };
    }

    private int Ptt(DvdMenuCompiler.TitlePosition p)
    {
        var pgc = _disc.GetPgc(DvdDomain.Title, p.Vtsn, p.Pgcn);
        var pgn = pgc?.ProgramOfCell(p.Cell) ?? 1;
        var vtsTtn = p.VtsTtn > 0 ? p.VtsTtn : _disc.FindVtsTtnForPgc(p.Vtsn, p.Pgcn);
        return _disc.FindPtt(p.Vtsn, vtsTtn, p.Pgcn, pgn);
    }

    private MediaEntry Describe(MediaEntry composite)
    {
        var first = composite.Segments?.FirstOrDefault()?.Media;
        if (first is not null && _media.TryGetValue(first, out var f))
        {
            composite.Label ??= f.Label;
            composite.Kind = f.Kind;
        }
        return composite;
    }

    private CompiledMenu RootMenu() =>
        _compiler.RootMenu is { } r ? _compiler.Menus[r]
        : _compiler.Menus.Values.FirstOrDefault(m => m.MenuType is MenuType.Root or MenuType.Title) ?? _compiler.Menus.Values.First();

    private void ResolveLeadIns()
    {
        var made = new Dictionary<string, string?>();
        foreach (var a in AllActions().OfType<GotoAction>())
        {
            if (a.Transition is not { } t || !t.StartsWith(LeadInToken, StringComparison.Ordinal)) continue;
            if (!made.TryGetValue(t, out var clip)) made[t] = clip = _leadIns.TryGetValue(t, out var cells) ? Transition(cells) : null;
            a.Transition = clip;
        }
    }

    // ------------------------------------------------------------------ menus

    private Menu BuildMenu(CompiledMenu m, Dictionary<CompiledMenu, int> depth)
    {
        var node = _nodeOf[m];
        var art = Art.TryGetValue(node, out var a) ? a : null;
        var menu = new Menu
        {
            Canvas = new Canvas { Width = m.Video.Width, Height = m.Video.Height, Aspect = m.Video.IsWide ? "16:9" : "4:3" },
            Background = new Background { Image = art?.Image, Color = "#000000" },
            Label = MenuLabel(m),
            Buttons = new List<Button>(),
            Prohibits = DvdMenuCompiler.Prohibits(_disc.CellUops(m.Key.Domain, m.Key.Vtsn, m.Key.Pgcn, m.Key.Cell, m.Key.Unit)),
            Keys = _compiler.KeysFor(m.Key.Domain, m.Key.Vtsn),
        };
        if (art?.Video is { } video) menu.Background.Video = new LoopingMedia { Src = video, LoopStart = art.LoopStart };
        if (art?.Audio is { } audio) menu.Background.Audio = new LoopingMedia { Src = audio };
        if (art?.Video is not null && art.ButtonsAt is { } at) menu.ButtonsAt = at;

        var numbers = m.Highlight.Buttons.Select(b => b.Number).ToHashSet();
        var reachable = ArrowReachable(m);
        menu.Buttons = Buttons(m, m.Highlight, m.Buttons, node, art, reachable);

        // Other buttons while the menu video plays (the disc's highlight changes): button sets on its clock.
        if (m.Windows.Count > 0)
            menu.ButtonSets = m.Windows.Select(w => new ButtonSet
            {
                From = w.From,
                To = w.To,
                Buttons = Buttons(m, w.Highlight, w.Buttons, node, art: null, reachable: null),
                DefaultButton = w.Highlight.ForcedSelect > 0 && w.Highlight.Get(w.Highlight.ForcedSelect) is not null ? ButtonId(w.Highlight.ForcedSelect) : null,
            }).ToList();

        // The disc highlights the button its highlight register names, carried over from the last menu
        // (a forced selection wins).
        menu.DefaultButton = numbers.Count == 0 ? null
            : m.Highlight.ForcedSelect > 0 && numbers.Contains(m.Highlight.ForcedSelect)
                ? ButtonId(m.Highlight.ForcedSelect)
                : ButtonRef.FromVar("@focus");
        // A cell without buttons is a clip: it plays once and holds its last frame (its still time).
        if (numbers.Count == 0)
        {
            if (menu.Background.Video is { } clip) { clip.Loop = false; clip.LoopStart = null; }
            if (menu.Background.Audio is { } sound) sound.Loop = false;
        }

        // Left alone: a motion intro running into its own loop is one menu (the art made one clip of both).
        var merged = art?.LoopStart is not null && m.LoopsInto is not null;
        if (m.Timeout is not null && !merged)
            menu.Timeout = new MenuTimeout { After = Math.Max(0.1, m.TimeoutAfter), Action = m.Timeout, RestartOnInput = false };

        menu.Back = BackFor(m, depth);

        menu.Extensions = XDvd(new Dictionary<string, object?>
        {
            ["domain"] = m.Key.Domain == DvdDomain.VmgMenu ? "VMGM" : "VTSM",
            ["vtsn"] = m.Key.Domain == DvdDomain.VmgMenu ? null : m.Key.Vtsn,
            ["pgcn"] = m.Key.Pgcn,
            ["cell"] = m.Key.Cell,
            ["menuType"] = m.MenuType == MenuType.None ? null : m.MenuType.ToString(),
        });
        return menu;
    }

    /// <summary>
    /// A set of the disc's buttons as the format's: places, moves, actions, labels and (with highlight art) their
    /// selected and activated pictures. <paramref name="reachable"/> null: every button counts as reachable.
    /// </summary>
    private List<Button> Buttons(CompiledMenu m, HighlightInfo highlight, Dictionary<int, MenuAction> actions, MenuNode node,
        MenuArtSet? art, HashSet<int>? reachable)
    {
        var numbers = highlight.Buttons.Select(b => b.Number).ToHashSet();
        var list = new List<Button>();
        foreach (var b in highlight.Buttons)
        {
            var action = actions.TryGetValue(b.Number, out var act) ? act : new DoAction();
            MarkPageTurns(m, b, action);
            var button = new Button
            {
                Id = ButtonId(b.Number),
                Rect = new Rect(b.X0, b.Y0, b.Width, b.Height),
                AutoAction = b.AutoAction ? true : null,
                AltRects = b.Letterbox is null && b.PanScan is null ? null : new AltRects
                {
                    Letterbox = b.Letterbox is { } lb ? new Rect(lb.X0, lb.Y0, lb.X1 - lb.X0 + 1, lb.Y1 - lb.Y0 + 1) : null,
                    Panscan = b.PanScan is { } ps ? new Rect(ps.X0, ps.Y0, ps.X1 - ps.X0 + 1, ps.Y1 - ps.Y0 + 1) : null,
                },
                Nav = Nav(b, numbers),
                Action = action,
                A11yLabel = ButtonText(node, b.Number) ?? LabelOfAction(action),
            };
            if (art is not null && art.Buttons.TryGetValue(b.Number, out var states))
                button.States = new ButtonStates
                {
                    Selected = states.Selected is { } s ? new ButtonState { Image = s } : null,
                    Activated = states.Activated is { } ac ? new ButtonState { Image = ac } : null,
                };
            button.Extensions = XDvd(new Dictionary<string, object?>
            {
                ["button"] = b.Number,
                ["colorTable"] = b.ColorTable,
                ["commands"] = new[] { DvdCommands.Disassemble(b.Command) },
                ["hidden"] = reachable is not null && !reachable.Contains(b.Number) || !b.HasArea ? true : null,
            });
            list.Add(button);
        }
        return list;
    }

    private static string ButtonId(int n) => $"b{n}";

    private static Nav? Nav(DvdButton b, HashSet<int> numbers)
    {
        string? To(int n) => n != b.Number && numbers.Contains(n) ? ButtonId(n) : null;
        var nav = new Nav { Up = To(b.Up), Down = To(b.Down), Left = To(b.Left), Right = To(b.Right) };
        return nav.Up is null && nav.Down is null && nav.Left is null && nav.Right is null ? null : nav;
    }

    /// <summary>Buttons the arrows can reach from the first highlight; the rest are hidden (easter eggs).</summary>
    private static HashSet<int> ArrowReachable(CompiledMenu m)
    {
        var buttons = m.Highlight.Buttons;
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(m.Highlight.ForcedSelect > 0 ? m.Highlight.ForcedSelect : 1);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (m.Highlight.Get(n) is not { } b || !seen.Add(n)) continue;
            foreach (var next in new[] { b.Up, b.Down, b.Left, b.Right }) stack.Push(next);
        }
        // Every highlight a menu's entry can start on counts too.
        return buttons.Count <= 1 ? buttons.Select(b => b.Number).ToHashSet() : seen;
    }

    private string MenuLabel(CompiledMenu m)
    {
        if (_options.MenuHeading?.Invoke(_nodeOf[m]) is { } heading) return heading;
        if (m == RootMenu()) return "Main Menu";
        switch (m.MenuType)
        {
            case MenuType.Chapter: return "Scene Selection";
            case MenuType.Audio: return "Audio";
            case MenuType.Subpicture: return "Subtitles";
            case MenuType.Angle: return "Angles";
        }
        // Named by what its buttons play: episodes, one title's chapters, or extras.
        var plays = m.Buttons.Values.Select(FirstPlay).OfType<PlayAction>().ToList();
        if (plays.Count >= 2 && _options.RoleOf is { } roleOf)
        {
            var roles = plays.Select(p => roleOf(p.Media)).OfType<MediaRole>().ToList();
            if (plays.Select(p => p.Media).Distinct().Count() == 1 && plays.Count(p => p.Chapter is not null || p.Start is not null) >= 2) return "Scene Selection";
            if (roles.Count > 0 && roles.Count(r => r == MediaRole.Episode) * 2 > roles.Count) return "Episodes";
            if (roles.Count > 0 && roles.Count(r => r == MediaRole.Extra) * 2 > roles.Count) return "Special Features";
        }
        return m.MenuType == MenuType.Title ? "Title Menu" : "Menu";
    }

    private PlayAction? FirstPlay(MenuAction a) => ActionTree.WalkRuns(a, _compiler.Actions).OfType<PlayAction>().FirstOrDefault();

    private string? ButtonText(MenuNode node, int number) =>
        _options.ButtonLabel?.Invoke(node, number) is { Length: > 0 } l && l.Count(char.IsLetter) >= 2 ? l : null;

    /// <summary>A button without readable text is named after what it does.</summary>
    private string? LabelOfAction(MenuAction a)
    {
        var first = ActionTree.WalkRuns(a, _compiler.Actions).FirstOrDefault(x => x is PlayAction or GotoAction or ResumeAction);
        switch (first)
        {
            case ResumeAction:
                return "Resume";
            case PlayAction p:
                {
                    var entry = _media.TryGetValue(p.Media, out var e) ? e : _index.Composites.TryGetValue(p.Media, out var c) ? c : null;
                    if (p.Chapter is { } ch) return entry?.Chapters?.ElementAtOrDefault(ch - 1)?.Label ?? $"Chapter {ch}";
                    if (p.Start is { } s && entry?.Chapters?.FindIndex(x => Math.Abs(x.Time - s) < 0.01) is { } i and >= 0)
                        return entry.Chapters[i].Label ?? $"Chapter {i + 1}";
                    return entry?.Label;
                }
            case GotoAction g:
                return _compiler.Menus.Values.FirstOrDefault(x => x.Id == g.Menu) is { } menu ? MenuLabel(menu) : null;
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ page turns and back

    private static readonly Regex PageLink = new(@"\bLink(Next|Prev)PG\b|\bLink(Next|Prev)PGC\b", RegexOptions.Compiled);

    /// <summary>A button's goto to another page of the same list adds no Back step (spec §6.2).</summary>
    private void MarkPageTurns(CompiledMenu from, DvdButton b, MenuAction action)
    {
        foreach (var g in Walk(action).OfType<GotoAction>())
            if (_compiler.Menus.Values.FirstOrDefault(x => x.Id == g.Menu) is { } to && IsPageTurn(from, b, to))
                g.History = "replace";
    }

    /// <summary>
    /// A page turn (next/previous page of the same list): an explicit next/previous link, or a jump between
    /// cells of one PGC whose pages share most button positions (scene selection pages).
    /// </summary>
    private static bool IsPageTurn(CompiledMenu from, DvdButton b, CompiledMenu to)
    {
        if (to == from) return false;
        if (PageLink.IsMatch(DvdCommands.Disassemble(b.Command))) return true;
        if (to.Key.Domain != from.Key.Domain || to.Key.Vtsn != from.Key.Vtsn || to.Key.Pgcn != from.Key.Pgcn) return false;
        var fb = from.Highlight.Buttons;
        var tb = to.Highlight.Buttons;
        var (few, many) = (Math.Min(fb.Count, tb.Count), Math.Max(fb.Count, tb.Count));
        if (many - few > Math.Max(1, many / 4)) return false;
        var rects = tb.Select(x => (x.X0, x.Y0, x.X1, x.Y1)).ToHashSet();
        var shared = fb.Count(x => rects.Contains((x.X0, x.Y0, x.X1, x.Y1)));
        return shared >= 2 && shared * 2 >= few;
    }

    /// <summary>
    /// The remote's Back, chosen like the disc (profile: "Remote Return"): the PGC's Go Up, as the disc
    /// runs it; else the menu's own on-screen back button (a plain link to a menu closer to the root that
    /// isn't a page turn; the nearest wins); else nothing (history).
    /// </summary>
    private MenuAction? BackFor(CompiledMenu m, Dictionary<CompiledMenu, int> depth)
    {
        if (m == RootMenu()) return null;
        if (m.GoUp is { } up && !(up is GotoAction g && g.Menu == m.Id)) return up;
        var own = depth.TryGetValue(m, out var d) ? d : int.MaxValue;
        var best = m.Buttons
            .Select(b => (Number: b.Key, Target: OnlyMenu(b.Value)))
            .Where(x => x.Target is not null && x.Target != m && depth.TryGetValue(x.Target, out var td) && td < own)
            .OrderByDescending(x => depth[x.Target!])
            .ThenBy(x => x.Number)
            .FirstOrDefault();
        return best.Target is not null ? new ActivateAction { Button = ButtonId(best.Number) } : null;
    }

    /// <summary>The one menu a button's logic leads to, however it gets there (null when it plays something, or may go to several).</summary>
    private CompiledMenu? OnlyMenu(MenuAction action)
    {
        var all = ActionTree.WalkRuns(action, _compiler.Actions).ToList();
        if (all.Any(a => a is PlayAction or PlayAllAction or ResumeAction)) return null;
        var gotos = all.OfType<GotoAction>().ToList();
        if (gotos.Count == 0 || gotos.Any(g => g.History == "replace") || gotos.Select(g => g.Menu).Distinct().Count() != 1) return null;
        return _compiler.Menus.Values.FirstOrDefault(x => x.Id == gotos[0].Menu);
    }

    /// <summary>Menu-link distance from the root; page turns and timeouts add no depth.</summary>
    private Dictionary<CompiledMenu, int> Depths()
    {
        var byId = _compiler.Menus.Values.ToDictionary(x => x.Id);
        var root = RootMenu();
        var depth = new Dictionary<CompiledMenu, int> { [root] = 0 };
        var deque = new LinkedList<CompiledMenu>();
        deque.AddFirst(root);
        void Reach(CompiledMenu from, string to, int cost)
        {
            if (!byId.TryGetValue(to, out var t)) return;
            var d = depth[from] + cost;
            if (depth.TryGetValue(t, out var had) && had <= d) return;
            depth[t] = d;
            if (cost == 0) deque.AddFirst(t); else deque.AddLast(t);
        }
        while (deque.Count > 0)
        {
            var m = deque.First!.Value;
            deque.RemoveFirst();
            if (m.Timeout is not null)
                foreach (var g in ActionTree.WalkRuns(m.Timeout, _compiler.Actions).OfType<GotoAction>()) Reach(m, g.Menu, 0);
            foreach (var (n, action) in m.Buttons)
            {
                var b = m.Highlight.Get(n);
                foreach (var g in ActionTree.WalkRuns(action, _compiler.Actions).OfType<GotoAction>())
                {
                    var page = b is not null && byId.TryGetValue(g.Menu, out var to) && IsPageTurn(m, b, to);
                    Reach(m, g.Menu, page ? 0 : 1);
                }
            }
        }
        return depth;
    }

    // ------------------------------------------------------------------ fallback list (Level 0)

    /// <summary>The default fallback list: every media, as one list of titles.</summary>
    private List<FallbackGroup> Fallback() => new()
    {
        new() { Label = "Titles", Items = _media.Where(m => m.Value.Ref is not null).Select(m => new FallbackItem { Label = m.Value.Label ?? m.Key, Action = new PlayAction { Media = m.Key } }).ToList() },
    };

    private static Dictionary<string, JsonElement> XDvd(Dictionary<string, object?> values) =>
        new() { ["x-dvd"] = JsonSerializer.SerializeToElement(values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value)) };
}
