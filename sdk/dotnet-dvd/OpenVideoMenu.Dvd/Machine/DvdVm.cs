using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenVideoMenu.Dvd.Machine;

public sealed record ResumePoint(int Vtsn, int Pgcn, int Cell, ushort Ttn, ushort VtsTtn, ushort TtPgcn, ushort Ptt);

/// <summary>Complete, clonable DVD VM state: position plus GPRM/SPRM registers.</summary>
public sealed class VmState
{
    public DvdDomain Domain { get; set; } = DvdDomain.FirstPlay;
    public int Vtsn { get; set; }
    public int Pgcn { get; set; } = 1;
    public int Pgn { get; set; } = 1;
    public int Cell { get; set; } = 1;

    public ushort[] Gprm { get; private set; } = new ushort[16];
    public ushort[] Sprm { get; private set; } = new ushort[24];
    public ResumePoint? Resume { get; set; }

    /// <summary>
    /// The programs a random or shuffle PGC plays, in the order drawn when it started (null for sequential
    /// PGCs), and how far it has got.
    /// </summary>
    public int[]? ProgramOrder { get; set; }
    public int OrderIndex { get; set; }

    /// <summary>
    /// After a resume, the program whose chapter SPRM 7 still holds (the one saved with the resume point) until
    /// playback reaches another chapter; null while the chapter is live.
    /// </summary>
    public int? ChapterHeldPgn { get; set; }

    /// <summary>The random generator's state: the same generator Open Video Menu's reference navigator uses, so checks can compare.</summary>
    private long _rng = 1;

    public VmState()
    {
        Sprm[0] = ('e' << 8) | 'n';   // menu language
        Sprm[1] = 0;                  // audio stream
        Sprm[2] = 62;                 // subpicture: none
        Sprm[3] = 1;                  // angle
        Sprm[4] = 1;                  // title number
        Sprm[5] = 1;                  // VTS title number
        Sprm[7] = 1;                  // chapter
        Sprm[8] = 1 << 10;            // highlighted button
        Sprm[12] = ('U' << 8) | 'S';  // parental country
        Sprm[13] = 15;                // parental level
        Sprm[14] = 0x0C00;            // player config: 16:9 display
        Sprm[15] = 0x7CFC;            // audio capabilities
        Sprm[16] = ('e' << 8) | 'n';  // preferred audio language
        Sprm[18] = ('e' << 8) | 'n';  // preferred subpicture language
        Sprm[20] = 1;                 // region
    }

    /// <summary>A fresh state for a player with these settings.</summary>
    public VmState(DvdPlayerSettings settings) : this() => settings.ApplyTo(this);

    public ushort GetSprm(int i) => i >= 0 && i < Sprm.Length ? Sprm[i] : (ushort)0;

    public void SetSprm(int i, ushort v)
    {
        if (i >= 0 && i < Sprm.Length) Sprm[i] = v;
    }

    public int HighlightedButton
    {
        get => Sprm[8] >> 10;
        set => Sprm[8] = (ushort)(value << 10);
    }

    /// <summary>Deterministic pseudo-random value in 1..max (the DVD RND set op).</summary>
    internal int NextRandom(int max)
    {
        _rng = (_rng * 1103515245L + 12345L) % 2147483648L;
        return (int)(_rng % max) + 1;
    }

    public VmState Clone()
    {
        var c = (VmState)MemberwiseClone();
        c.Gprm = (ushort[])Gprm.Clone();
        c.Sprm = (ushort[])Sprm.Clone();
        return c;
    }

    /// <summary>Identifies register contents that can change what a menu's buttons do.</summary>
    public string RegisterKey()
    {
        var sb = new StringBuilder(80);
        foreach (var g in Gprm) sb.Append(g.ToString("X")).Append(',');
        sb.Append(Sprm[1]).Append(',').Append(Sprm[2]).Append(',').Append(Sprm[3]);
        return sb.ToString();
    }

    public override string ToString() => $"{Domain} vts{Vtsn} pgc{Pgcn} pg{Pgn} c{Cell}";
}

/// <summary>Where title playback landed.</summary>
public sealed record TitleLanding(int Ttn, int Vtsn, int VtsTtn, int Pgcn, int Pgn, int Chapter, TimeSpan PgcDuration)
{
    public override string ToString() =>
        $"Title {Ttn} ch {Chapter} (vts{Vtsn} ttn{VtsTtn} pgc{Pgcn} pg{Pgn}, {PgcDuration:hh\\:mm\\:ss})";
}

public enum VmStopKind
{
    /// <summary>Stopped on a menu cell that has buttons.</summary>
    Menu,

    /// <summary>Stopped at the start of title playback.</summary>
    Title,

    /// <summary>The disc stopped (Exit, or end of a PGC with nowhere to go).</summary>
    Exit,

    /// <summary>Playback reached something the simulation can't continue from (still frame without buttons, missing PGC).</summary>
    DeadEnd,

    /// <summary>The command changed registers but did not navigate.</summary>
    NoNavigation,

    /// <summary>Instruction budget exhausted (usually a loop that depends on timers or user input).</summary>
    LoopLimit,
}

/// <summary>One cell of a menu PGC.</summary>
public readonly record struct MenuCellRef(DvdDomain Domain, int Vtsn, int Pgcn, int Cell, int Unit = -1);

public sealed class VmResult
{
    public VmStopKind Kind { get; init; }
    public VmState State { get; init; } = new();
    public HighlightInfo? Highlight { get; init; }
    public TitleLanding? Landing { get; init; }
    public string? Detail { get; init; }

    /// <summary>Menu-domain video played on the way (menu clips without buttons).</summary>
    public TimeSpan MenuVideoPlayed { get; init; }

    /// <summary>The button-less menu cells behind <see cref="MenuVideoPlayed"/>, in play order (a page transition).</summary>
    public IReadOnlyList<MenuCellRef> LeadInCells { get; init; } = Array.Empty<MenuCellRef>();

    /// <summary>Titles played through before stopping (e.g. warnings before the menu).</summary>
    public IReadOnlyList<TitleLanding> TitlesPassed { get; init; } = Array.Empty<TitleLanding>();

    /// <summary>
    /// A resume (LinkRSM) was on the way but not taken: the disc resumes here once a title has been
    /// played ("play" that means "continue the film" after the viewer stopped it).
    /// </summary>
    public bool OffersResume { get; init; }

    public override string ToString() => Kind switch
    {
        VmStopKind.Menu => $"Menu {State}",
        VmStopKind.Title => $"{Landing}",
        _ => $"{Kind}{(Detail is null ? "" : ": " + Detail)}",
    };
}

/// <summary>
/// Simulates DVD-Video playback logic (no decoding): executes PGC pre/post/cell commands and
/// button commands, and stops where a player would wait for the user or start showing a title.
/// </summary>
public sealed class DvdVm
{
    public const int MaxSteps = 20000;

    private readonly DvdDisc _disc;

    /// <summary>The disc this machine plays.</summary>
    public DvdDisc Disc => _disc;

    public DvdVm(DvdDisc disc, DvdPlayerSettings? settings = null)
    {
        _disc = disc;
        Settings = settings ?? DvdPlayerSettings.Default;
    }

    /// <summary>The player the machine stands for: what a disc's commands read about it.</summary>
    public DvdPlayerSettings Settings { get; }

    /// <summary>Called at every step the machine takes (tracing, tools); null for none.</summary>
    public Action<VmStep>? Observer { get; init; }

    /// <summary>A fresh state for this machine's player.</summary>
    public VmState NewState() => new(Settings);

    /// <summary>
    /// Stop where a title cell holds its last frame until the viewer acts and shows buttons (a menu made of title
    /// cells), as a Menu result in the title domain, instead of playing on. Off for the map and the scout.
    /// </summary>
    public bool StopsAtTitleButtons { get; init; }

    private enum Phase
    {
        EnterPgc,
        PlayProgram,
        PlayCell,
        EndCell,
        EndPgc,
    }

    private sealed class RunContext
    {
        public bool StopAtTitles;
        public (int Vtsn, int Pgcn)? SkipTitlePgc;
        public bool FollowForcedActivate = true;
        public int Steps;
        public readonly List<TitleLanding> Passed = new();
        public readonly Dictionary<string, int> Visits = new();

        /// <summary>Menu-domain video played without buttons (menu clips, easter eggs hidden in menu VOBs).</summary>
        public TimeSpan MenuVideoPlayed;
        public readonly HashSet<string> MenuCellsPlayed = new();
        public readonly List<MenuCellRef> LeadIn = new();

        /// <summary>A resume (LinkRSM) was on the way: skipped by its condition, or with no resume point yet.</summary>
        public bool OffersResume;
    }

    // ------------------------------------------------------------------
    // Public entry points
    // ------------------------------------------------------------------

    /// <summary>Plays the disc from first play, through any intro titles, to the first menu.</summary>
    public VmResult StartFromFirstPlay(bool stopAtTitles = false, ushort? menuLanguage = null)
    {
        var s = NewState();
        s.Domain = DvdDomain.FirstPlay;
        s.Pgcn = 1;
        if (menuLanguage is { } language) s.SetSprm(0, language);
        return Run(s, Phase.EnterPgc, new RunContext { StopAtTitles = stopAtTitles });
    }

    /// <summary>Enters a menu PGC directly (used to map menus the first-play path never reaches).</summary>
    public VmResult EnterMenuPgc(DvdDomain domain, int vtsn, int pgcn, VmState? registers = null)
    {
        var s = registers?.Clone() ?? NewState();
        s.Domain = domain;
        s.Vtsn = domain == DvdDomain.VtsMenu ? vtsn : 0;
        if (domain == DvdDomain.VtsMenu)
        {
            // Make SPRM4/5 point at a title of this VTS so menu commands that consult them behave.
            var t = _disc.Vmg.Titles.FirstOrDefault(x => x.Vtsn == vtsn);
            if (t is not null) { s.Sprm[4] = (ushort)t.TitleNumber; s.Sprm[5] = (ushort)t.VtsTtn; }
        }
        s.Pgcn = pgcn;
        s.Pgn = 1;
        return Run(s, Phase.EnterPgc, new RunContext { StopAtTitles = true });
    }

    /// <summary>Starts playback of a title directly, as JumpTT would (used for previews and inventory).</summary>
    public VmResult JumpToTitle(int ttn, int chapter = 1, VmState? registers = null)
    {
        var s = registers?.Clone() ?? NewState();
        var ctx = new RunContext { StopAtTitles = true };
        var next = ApplyLink(s, new DvdLink(chapter > 1 ? LinkCmd.JumpVTS_PTT : LinkCmd.JumpTT, ttn, chapter), ctx, out var stop);
        if (stop is not null) return stop;
        return Run(s, next!.Value, ctx);
    }

    /// <summary>Activates a button: selects it, runs its command, and follows navigation.</summary>
    public VmResult ActivateButton(VmState menuState, DvdButton button)
    {
        var s = menuState.Clone();
        s.HighlightedButton = button.Number;
        var ctx = new RunContext { StopAtTitles = true, FollowForcedActivate = true };
        var r = DvdCommands.Evaluate(button.Command, s);
        if (r.Kind != EvalKind.Link)
            return new VmResult { Kind = VmStopKind.NoNavigation, State = s, Detail = DvdCommands.Disassemble(button.Command) };

        var next = ApplyLink(s, r.Link, ctx, out var stop);
        if (stop is not null) return stop;
        return Run(s, next!.Value, ctx);
    }

    /// <summary>
    /// The player's menu key: the current title set's root menu (with a resume point when leaving a
    /// title), falling back to the disc's title menu.
    /// </summary>
    public VmResult MenuCall(VmState current)
    {
        var s = current.Clone();
        var ctx = new RunContext { StopAtTitles = true };
        DvdLink link;
        // In the disc's own menus (VMGM) the Menu key opens its title menu (the DVD profile of Open Video Menu).
        if (s.Domain != DvdDomain.VmgMenu && s.Vtsn > 0 && _disc.FindMenuPgc(DvdDomain.VtsMenu, s.Vtsn, MenuType.Root) > 0)
            link = s.Domain == DvdDomain.Title
                ? new DvdLink(LinkCmd.CallSS_VTSM, (int)MenuType.Root, s.Cell)
                : new DvdLink(LinkCmd.JumpSS_VTSM, 0, 0, (int)MenuType.Root);
        else
            link = s.Domain == DvdDomain.Title
                ? new DvdLink(LinkCmd.CallSS_VMGM_MENU, (int)MenuType.Title, s.Cell)
                : new DvdLink(LinkCmd.JumpSS_VMGM_MENU, (int)MenuType.Title);

        var next = ApplyLink(s, link, ctx, out var stop);
        if (stop is not null) return stop;
        return Run(s, next!.Value, ctx);
    }

    /// <summary>The remote's Title key: the disc's title menu (a call from a title, so it can be resumed).</summary>
    public VmResult TitleCall(VmState current)
    {
        var s = current.Clone();
        var ctx = new RunContext { StopAtTitles = true };
        if (_disc.FindMenuPgc(DvdDomain.VmgMenu, 0, MenuType.Title) <= 0)
            return new VmResult { Kind = VmStopKind.NoNavigation, State = s, Detail = "no title menu" };
        var link = s.Domain == DvdDomain.Title
            ? new DvdLink(LinkCmd.CallSS_VMGM_MENU, (int)MenuType.Title, s.Cell)
            : new DvdLink(LinkCmd.JumpSS_VMGM_MENU, (int)MenuType.Title);
        var next = ApplyLink(s, link, ctx, out var stop);
        if (stop is not null) return stop;
        return Run(s, next!.Value, ctx);
    }

    /// <summary>
    /// Continues from a menu as if nobody pressed anything and its still time ran out (timed intro screens
    /// that hand over to the real menu by themselves). NoNavigation when the menu waits forever.
    /// </summary>
    public VmResult ContinueAfterStill(VmState menuState)
    {
        var s = menuState.Clone();
        var pgc = _disc.GetPgc(s.Domain, s.Vtsn, s.Pgcn, UnitOf(s));
        if (pgc is null || s.Cell < 1 || s.Cell > pgc.Cells.Count || pgc.Cells[s.Cell - 1].StillTime == 0xFF)
            return new VmResult { Kind = VmStopKind.NoNavigation, State = s, Detail = "waits for a key" };
        return Run(s, Phase.EndCell, new RunContext { StopAtTitles = true });
    }

    /// <summary>Continues from a title-playback stop as if the title played to its end.</summary>
    public VmResult ContinueAfterTitle(VmState titleState)
    {
        var s = titleState.Clone();
        var ctx = new RunContext { StopAtTitles = true, SkipTitlePgc = (s.Vtsn, s.Pgcn) };
        return Run(s, Phase.EndCell, ctx);
    }

    // ------------------------------------------------------------------
    // Core loop
    // ------------------------------------------------------------------

    /// <summary>
    /// The menu language unit a player uses: the one in the menu language (SPRM 0), else the first.
    /// </summary>
    private int UnitFor(VmState s, DvdDomain domain, int vtsn)
    {
        var units = _disc.MenuUnits(domain, vtsn);
        if (units.Count <= 1) return -1;
        var language = s.GetSprm(0);
        for (var i = 0; i < units.Count; i++)
            if (units[i].Language is { Length: 2 } l && (char.ToLowerInvariant(l[0]) << 8 | char.ToLowerInvariant(l[1])) == language)
                return i;
        return 0;
    }

    /// <summary>
    /// The programs a random (bit 7 clear) or shuffle (bit 7 set) PGC plays: the low 7 bits of its playback mode
    /// count them. Drawn from the state's generator like the format's playAll (random: one draw per program
    /// played; shuffle: every program once, in a Fisher-Yates order).
    /// </summary>
    public static int[] ProgramOrder(VmState s, Pgc pgc)
    {
        var programs = pgc.ProgramMap.Count;
        if (programs == 0) return Array.Empty<int>();
        var count = pgc.PlaybackMode & 0x7F;
        if ((pgc.PlaybackMode & 0x80) != 0)
        {
            var order = Enumerable.Range(1, programs).ToArray();
            for (var i = order.Length - 1; i > 0; i--)
            {
                var j = s.NextRandom(i + 1) - 1;
                (order[i], order[j]) = (order[j], order[i]);
            }
            return order;
        }
        return Enumerable.Range(0, count).Select(_ => s.NextRandom(programs)).ToArray();
    }

    /// <summary>The menu language unit a state's menu is in (0 when its domain has only one).</summary>
    public int MenuUnitOf(VmState s) => Math.Max(0, UnitOf(s));

    private int UnitOf(VmState s) => s.Domain is DvdDomain.VmgMenu or DvdDomain.VtsMenu ? UnitFor(s, s.Domain, s.Vtsn) : -1;

    private VmResult Run(VmState s, Phase phase, RunContext ctx)
    {
        while (true)
        {
            if (++ctx.Steps > MaxSteps)
                return Stop(VmStopKind.LoopLimit, s, ctx, "instruction budget exhausted");

            var pgc = _disc.GetPgc(s.Domain, s.Vtsn, s.Pgcn, UnitOf(s));
            Observer?.Invoke(new VmStep((VmStepKind)phase, s));
            if (pgc is null)
                return Stop(VmStopKind.DeadEnd, s, ctx, $"missing PGC ({s})");

            switch (phase)
            {
                case Phase.EnterPgc:
                    {
                        s.ChapterHeldPgn = null;
                        var key = $"{s.Domain}:{s.Vtsn}:{s.Pgcn}";
                        ctx.Visits[key] = ctx.Visits.TryGetValue(key, out var n) ? n + 1 : 1;
                        if (ctx.Visits[key] > 64)
                            return Stop(VmStopKind.LoopLimit, s, ctx, $"PGC {key} entered repeatedly");

                        if (s.Domain == DvdDomain.Title) UpdateTitleRegisters(s);
                        // Entering a PGC again (a jump to its own title) is new playback, not the rest of the one left.
                        ctx.SkipTitlePgc = null;
                        // A random PGC's drawn order belongs to that PGC's playback (a call out of it leaves it).
                        s.ProgramOrder = null;

                        var link = RunCommandList(pgc.PreCommands, s, ctx);
                        if (link is { } l)
                        {
                            var np = ApplyLink(s, l, ctx, out var stop);
                            if (stop is not null) return stop;
                            phase = np!.Value;
                            continue;
                        }

                        if (s.Pgn < 1) s.Pgn = 1;
                        phase = pgc.CellCount == 0 || pgc.ProgramCount == 0 ? Phase.EndPgc : Phase.PlayProgram;
                        continue;
                    }

                case Phase.PlayProgram:
                    if (s.Domain == DvdDomain.Title && pgc.PlaybackMode != 0 && s.ProgramOrder is null)
                    {
                        // Random or shuffle playback: the programs drawn now, as the format's navigator draws a
                        // playAll's order (spec §6: shuffle = Fisher-Yates from the end, random = one draw each).
                        s.ProgramOrder = ProgramOrder(s, pgc);
                        s.OrderIndex = 0;
                        if (s.ProgramOrder.Length == 0) { phase = Phase.EndPgc; continue; }
                        s.Pgn = s.ProgramOrder[0];
                    }
                    if (s.Pgn < 1 || s.Pgn > pgc.ProgramMap.Count) { phase = Phase.EndPgc; continue; }
                    s.Cell = pgc.ProgramMap[s.Pgn - 1];
                    if (s.Domain == DvdDomain.Title)
                    {
                        var ptt = _disc.FindPtt(s.Vtsn, s.Sprm[5], s.Pgcn, s.Pgn);
                        if (ptt > 0) s.Sprm[7] = (ushort)ptt;
                    }
                    phase = Phase.PlayCell;
                    continue;

                case Phase.PlayCell:
                    {
                        if (s.Cell < 1 || s.Cell > pgc.Cells.Count) { phase = Phase.EndPgc; continue; }

                        // The program is the one holding the cell (players recompute it, so links to a
                        // cell leave no stale program number for a later "next program").
                        s.Pgn = pgc.ProgramOfCell(s.Cell);

                        // Select the angle within an angle block.
                        var cell = pgc.Cells[s.Cell - 1];
                        if (cell.IsAngleBlock && cell.BlockMode == 1)
                        {
                            var angle = Math.Max(1, (int)s.Sprm[3]);
                            var target = s.Cell + angle - 1;
                            if (target <= pgc.Cells.Count && pgc.Cells[target - 1].IsAngleBlock) s.Cell = target;
                        }

                        if (s.Domain == DvdDomain.Title)
                        {
                            // SPRM 7 is the chapter playing (live, as the DVD spec defines it), also after a link
                            // within the PGC; after a resume it keeps the saved chapter until the next one starts.
                            if (s.ChapterHeldPgn != s.Pgn)
                            {
                                s.ChapterHeldPgn = null;
                                var live = _disc.FindPtt(s.Vtsn, s.Sprm[5], s.Pgcn, s.Pgn);
                                if (live > 0) s.Sprm[7] = (ushort)live;
                            }
                            var landing = MakeLanding(s, pgc);
                            var skip = ctx.SkipTitlePgc is { } k && k.Vtsn == s.Vtsn && k.Pgcn == s.Pgcn;
                            if (ctx.StopAtTitles && !skip)
                                return new VmResult { Kind = VmStopKind.Title, State = s, Landing = landing, TitlesPassed = ctx.Passed.ToArray(), OffersResume = ctx.OffersResume };

                            if (!skip && (ctx.Passed.Count == 0 || ctx.Passed[^1].Pgcn != s.Pgcn || ctx.Passed[^1].Vtsn != s.Vtsn))
                                ctx.Passed.Add(landing);
                            phase = Phase.EndCell;
                            continue;
                        }

                        // Menu / first-play cell.
                        var hl = _disc.GetHighlight(s.Domain, s.Vtsn, s.Pgcn, s.Cell, UnitOf(s));
                        if (hl is not null && hl.Buttons.Count > 0)
                        {
                            if (s.HighlightedButton < 1 || s.HighlightedButton > hl.Buttons.Count)
                                s.HighlightedButton = hl.ForcedSelect > 0 ? hl.ForcedSelect : 1;
                            if (hl.ForcedSelect > 0) s.HighlightedButton = hl.ForcedSelect;

                            if (ctx.FollowForcedActivate && hl.Get(hl.ForcedActivate) is { } fa)
                            {
                                s.HighlightedButton = fa.Number;
                                var fr = DvdCommands.Evaluate(fa.Command, s);
                                if (fr.Kind == EvalKind.Link)
                                {
                                    var np = ApplyLink(s, fr.Link, ctx, out var stop);
                                    if (stop is not null) return stop;
                                    phase = np!.Value;
                                    continue;
                                }
                            }

                            return new VmResult { Kind = VmStopKind.Menu, State = s, Highlight = hl, TitlesPassed = ctx.Passed.ToArray(), MenuVideoPlayed = ctx.MenuVideoPlayed, LeadInCells = ctx.LeadIn.ToArray(), OffersResume = ctx.OffersResume };
                        }

                        if (ctx.MenuCellsPlayed.Add($"{s.Domain}:{s.Vtsn}:{s.Pgcn}:{s.Cell}"))
                        {
                            ctx.MenuVideoPlayed += cell.Duration;
                            ctx.LeadIn.Add(new MenuCellRef(s.Domain, s.Vtsn, s.Pgcn, s.Cell));
                        }
                        if (cell.StillTime == 0xFF)
                            return Stop(VmStopKind.DeadEnd, s, ctx, "menu still frame without buttons");
                        phase = Phase.EndCell;
                        continue;
                    }

                case Phase.EndCell:
                    {
                        if (s.Cell < 1 || s.Cell > pgc.Cells.Count) { phase = Phase.EndPgc; continue; }
                        var cell = pgc.Cells[s.Cell - 1];
                        // (The buttons showing as the cell holds on its last frame: its last highlight.)
                        if (StopsAtTitleButtons && s.Domain == DvdDomain.Title && cell.StillTime == 0xFF &&
                            _disc.TitleCellHighlights(s.Vtsn, s.Pgcn, s.Cell, 3).LastOrDefault(h => h.Buttons.Count > 0) is { } titleButtons)
                        {
                            if (s.HighlightedButton < 1 || s.HighlightedButton > titleButtons.Buttons.Count)
                                s.HighlightedButton = titleButtons.ForcedSelect > 0 ? titleButtons.ForcedSelect : 1;
                            // A forced select chooses the button as the buttons appear, over a title as on a menu.
                            if (titleButtons.ForcedSelect > 0) s.HighlightedButton = titleButtons.ForcedSelect;
                            return new VmResult { Kind = VmStopKind.Menu, State = s, Highlight = titleButtons, TitlesPassed = ctx.Passed.ToArray() };
                        }
                        if (cell.CellCommandNumber >= 1 && cell.CellCommandNumber <= pgc.CellCommands.Count)
                        {
                            ctx.Steps++;
                            var r = DvdCommands.Evaluate(pgc.CellCommands[cell.CellCommandNumber - 1], s);
                            if (r.Kind == EvalKind.Link)
                            {
                                var np = ApplyLink(s, r.Link, ctx, out var stop);
                                if (stop is not null) return stop;
                                phase = np!.Value;
                                continue;
                            }
                        }

                        var next = s.Cell + 1;
                        if (cell.IsAngleBlock)
                            while (next <= pgc.Cells.Count && pgc.Cells[next - 2].BlockMode != 3 && pgc.Cells[next - 1].IsAngleBlock)
                                next++;

                        // Random or shuffle playback: at the end of a program, the next one drawn.
                        if (s.ProgramOrder is { } order && (next > pgc.Cells.Count || pgc.ProgramOfCell(next) != s.Pgn))
                        {
                            if (++s.OrderIndex >= order.Length) { phase = Phase.EndPgc; continue; }
                            s.Pgn = order[s.OrderIndex];
                            // Each drawn program is a playback of its own (where a host stops at titles).
                            ctx.SkipTitlePgc = null;
                            phase = Phase.PlayProgram;
                            continue;
                        }

                        if (next > pgc.Cells.Count) { phase = Phase.EndPgc; continue; }
                        s.Cell = next;
                        var pg = pgc.ProgramOfCell(next);
                        if (pg != s.Pgn)
                        {
                            s.Pgn = pg;
                            if (s.Domain == DvdDomain.Title)
                            {
                                var ptt = _disc.FindPtt(s.Vtsn, s.Sprm[5], s.Pgcn, s.Pgn);
                                if (ptt > 0) s.Sprm[7] = (ushort)ptt;
                            }
                        }
                        phase = Phase.PlayCell;
                        continue;
                    }

                case Phase.EndPgc:
                    {
                        s.ProgramOrder = null;
                        if (pgc.StillTime == 0xFF && s.Domain is DvdDomain.VmgMenu or DvdDomain.VtsMenu)
                            return Stop(VmStopKind.DeadEnd, s, ctx, "menu holds on a still with no buttons");

                        var link = RunCommandList(pgc.PostCommands, s, ctx);
                        if (link is { } l)
                        {
                            var np = ApplyLink(s, l, ctx, out var stop);
                            if (stop is not null) return stop;
                            phase = np!.Value;
                            // Back into this PGC's own cells after they've played (a loop): new playback.
                            if (phase is Phase.PlayProgram or Phase.PlayCell) ctx.SkipTitlePgc = null;
                            continue;
                        }

                        if (pgc.NextPgc != 0 && s.Domain != DvdDomain.FirstPlay)
                        {
                            s.Pgcn = pgc.NextPgc;
                            s.Pgn = 1;
                            phase = Phase.EnterPgc;
                            continue;
                        }

                        return Stop(VmStopKind.Exit, s, ctx, "end of PGC with no continuation");
                    }
            }
        }
    }

    private static VmResult Stop(VmStopKind kind, VmState s, RunContext ctx, string detail)
    {
        if (kind is VmStopKind.DeadEnd or VmStopKind.LoopLimit && ctx.MenuVideoPlayed >= TimeSpan.FromSeconds(3))
            detail = $"plays a {ctx.MenuVideoPlayed:m\\:ss} clip stored in the menus (not a rippable title), then {detail}";
        return new VmResult { Kind = kind, State = s, Detail = detail, TitlesPassed = ctx.Passed.ToArray(), MenuVideoPlayed = ctx.MenuVideoPlayed, OffersResume = ctx.OffersResume };
    }

    /// <summary>Runs a pre/post command list; returns the link if one fires.</summary>
    private static DvdLink? RunCommandList(IReadOnlyList<byte[]> cmds, VmState s, RunContext ctx)
    {
        var line = 1;
        var guard = 0;
        while (line >= 1 && line <= cmds.Count)
        {
            if (++guard > 1024 || ++ctx.Steps > MaxSteps) return null;
            var r = DvdCommands.Evaluate(cmds[line - 1], s);
            if (r.Kind != EvalKind.Link && DvdCommands.IsResumeLink(cmds[line - 1])) ctx.OffersResume = true;
            switch (r.Kind)
            {
                case EvalKind.Link:
                    return r.Link;
                case EvalKind.Break:
                    return null;
                case EvalKind.Goto:
                    line = r.Line;
                    break;
                default:
                    line++;
                    break;
            }
        }
        return null;
    }

    private void UpdateTitleRegisters(VmState s)
    {
        s.Sprm[6] = (ushort)s.Pgcn;
        // Keep SPRM5 consistent with the PGC actually being played.
        var vtsTtn = s.Sprm[5];
        var inTitle = _disc.GetPtt(s.Vtsn, vtsTtn, 1) is not null &&
                      _disc.TitleSets.TryGetValue(s.Vtsn, out var vts) &&
                      vtsTtn <= vts.Ptts.Count &&
                      vts.Ptts[vtsTtn - 1].Any(p => p.Pgcn == s.Pgcn);
        if (!inTitle)
        {
            var found = _disc.FindVtsTtnForPgc(s.Vtsn, s.Pgcn);
            if (found > 0) s.Sprm[5] = (ushort)found;
        }
        var ttn = _disc.FindTtn(s.Vtsn, s.Sprm[5]);
        if (ttn > 0) s.Sprm[4] = (ushort)ttn;
    }

    private TitleLanding MakeLanding(VmState s, Pgc pgc)
    {
        // A PGC that no chapter table references is an "orphan" (reachable only by LinkPGCN);
        // report it with title number 0 rather than attributing it to whatever SPRM5 held.
        if (_disc.FindVtsTtnForPgc(s.Vtsn, s.Pgcn) == 0)
            return new TitleLanding(0, s.Vtsn, 0, s.Pgcn, s.Pgn, 0, pgc.Duration);

        var vtsTtn = s.Sprm[5];
        var ttn = _disc.FindTtn(s.Vtsn, vtsTtn);
        var chapter = _disc.FindPtt(s.Vtsn, vtsTtn, s.Pgcn, s.Pgn);
        return new TitleLanding(ttn, s.Vtsn, vtsTtn, s.Pgcn, s.Pgn, chapter, pgc.Duration);
    }

    /// <summary>
    /// Applies a navigation link to the state. Returns the phase to continue with,
    /// or sets <paramref name="stop"/> when the link ends the run.
    /// </summary>
    private Phase? ApplyLink(VmState s, DvdLink link, RunContext ctx, out VmResult? stop)
    {
        stop = null;
        var pgc = _disc.GetPgc(s.Domain, s.Vtsn, s.Pgcn, UnitOf(s));

        void SetButton(int b)
        {
            if (b != 0) s.HighlightedButton = b;
        }

        switch (link.Cmd)
        {
            case LinkCmd.NoLink:
                return Phase.PlayCell;

            case LinkCmd.LinkTopC:
                SetButton(link.Data1);
                return Phase.PlayCell;
            case LinkCmd.LinkNextC:
                SetButton(link.Data1);
                s.Cell++;
                return Phase.PlayCell;
            case LinkCmd.LinkPrevC:
                SetButton(link.Data1);
                s.Cell = Math.Max(1, s.Cell - 1);
                return Phase.PlayCell;

            case LinkCmd.LinkTopPG:
                SetButton(link.Data1);
                return Phase.PlayProgram;
            case LinkCmd.LinkNextPG:
                SetButton(link.Data1);
                s.Pgn++;
                return Phase.PlayProgram;
            case LinkCmd.LinkPrevPG:
                SetButton(link.Data1);
                s.Pgn = Math.Max(1, s.Pgn - 1);
                return Phase.PlayProgram;

            case LinkCmd.LinkTopPGC:
                SetButton(link.Data1);
                s.Pgn = 1;
                return Phase.EnterPgc;
            case LinkCmd.LinkNextPGC:
            case LinkCmd.LinkPrevPGC:
            case LinkCmd.LinkGoUpPGC:
                {
                    SetButton(link.Data1);
                    var target = link.Cmd switch
                    {
                        LinkCmd.LinkNextPGC => pgc?.NextPgc ?? 0,
                        LinkCmd.LinkPrevPGC => pgc?.PrevPgc ?? 0,
                        _ => pgc?.GoUpPgc ?? 0,
                    };
                    if (target == 0)
                    {
                        stop = Stop(VmStopKind.DeadEnd, s, ctx, $"{link.Cmd} with no target PGC");
                        return null;
                    }
                    s.Pgcn = target;
                    s.Pgn = 1;
                    return Phase.EnterPgc;
                }
            case LinkCmd.LinkTailPGC:
                SetButton(link.Data1);
                return Phase.EndPgc;

            case LinkCmd.LinkRSM:
                {
                    SetButton(link.Data1);
                    var r = s.Resume;
                    if (r is null)
                    {
                        ctx.OffersResume = true;
                        stop = Stop(VmStopKind.Exit, s, ctx, "resume requested with no resume point");
                        return null;
                    }
                    s.Domain = DvdDomain.Title;
                    s.Vtsn = r.Vtsn;
                    s.Pgcn = r.Pgcn;
                    s.Cell = Math.Max(1, r.Cell);
                    s.Sprm[4] = r.Ttn;
                    s.Sprm[5] = r.VtsTtn;
                    s.Sprm[6] = r.TtPgcn;
                    s.Sprm[7] = r.Ptt;
                    s.Resume = null;
                    var rp = _disc.GetPgc(s.Domain, s.Vtsn, s.Pgcn, UnitOf(s));
                    s.Pgn = rp?.ProgramOfCell(s.Cell) ?? 1;
                    s.ChapterHeldPgn = s.Pgn;
                    return Phase.PlayCell;
                }

            case LinkCmd.LinkPGCN:
                s.Pgcn = link.Data1;
                s.Pgn = 1;
                return Phase.EnterPgc;

            case LinkCmd.LinkPTTN:
                {
                    SetButton(link.Data2);
                    if (s.Domain != DvdDomain.Title)
                        return Phase.PlayCell;
                    var e = _disc.GetPtt(s.Vtsn, s.Sprm[5], link.Data1);
                    if (e is not { } p)
                    {
                        stop = Stop(VmStopKind.DeadEnd, s, ctx, $"LinkPTTN {link.Data1} not in title");
                        return null;
                    }
                    // Unlike JumpVTS_PTT, LinkPTTN starts the program directly without running the
                    // PGC's pre-commands (titles often dispatch chapters from their own pre-commands).
                    s.Sprm[7] = (ushort)link.Data1;
                    s.Pgcn = p.Pgcn;
                    s.Pgn = p.Pgn;
                    UpdateTitleRegisters(s);
                    return Phase.PlayProgram;
                }
            case LinkCmd.LinkPGN:
                SetButton(link.Data2);
                s.Pgn = link.Data1;
                return Phase.PlayProgram;
            case LinkCmd.LinkCN:
                SetButton(link.Data2);
                s.Cell = link.Data1;
                return Phase.PlayCell;

            case LinkCmd.Exit:
                stop = Stop(VmStopKind.Exit, s, ctx, "Exit command");
                return null;

            case LinkCmd.JumpTT:
                return EnterTitle(s, link.Data1, 1, ctx, out stop);

            case LinkCmd.JumpVTS_TT:
            case LinkCmd.JumpVTS_PTT:
                {
                    var ttn = _disc.FindTtn(s.Vtsn, link.Data1);
                    if (ttn == 0)
                    {
                        stop = Stop(VmStopKind.DeadEnd, s, ctx, $"{link.Cmd}: VTS {s.Vtsn} title {link.Data1} is not in the title table");
                        return null;
                    }
                    return EnterTitle(s, ttn, link.Cmd == LinkCmd.JumpVTS_PTT ? Math.Max(1, link.Data2) : 1, ctx, out stop);
                }

            case LinkCmd.JumpSS_FP:
            case LinkCmd.CallSS_FP:
                if (link.Cmd == LinkCmd.CallSS_FP) SaveResume(s, link.Data1);
                s.Domain = DvdDomain.FirstPlay;
                s.Vtsn = 0;
                s.Pgcn = 1;
                s.Pgn = 1;
                return Phase.EnterPgc;

            case LinkCmd.JumpSS_VMGM_MENU:
            case LinkCmd.CallSS_VMGM_MENU:
                {
                    if (link.Cmd == LinkCmd.CallSS_VMGM_MENU) SaveResume(s, link.Data2);
                    var pgcn = _disc.FindMenuPgc(DvdDomain.VmgMenu, 0, (MenuType)link.Data1, UnitFor(s, DvdDomain.VmgMenu, 0));
                    if (pgcn == 0)
                    {
                        stop = Stop(VmStopKind.DeadEnd, s, ctx, $"VMGM has no {(MenuType)link.Data1} menu");
                        return null;
                    }
                    s.Domain = DvdDomain.VmgMenu;
                    s.Vtsn = 0;
                    s.Pgcn = pgcn;
                    s.Pgn = 1;
                    return Phase.EnterPgc;
                }

            case LinkCmd.JumpSS_VTSM:
            case LinkCmd.CallSS_VTSM:
                {
                    int vtsn, menu;
                    if (link.Cmd == LinkCmd.JumpSS_VTSM)
                    {
                        vtsn = link.Data1 != 0 ? link.Data1 : s.Vtsn;
                        menu = link.Data3;
                        if (link.Data1 != 0 && link.Data2 != 0)
                        {
                            s.Sprm[5] = (ushort)link.Data2;
                            var ttn = _disc.FindTtn(vtsn, link.Data2);
                            if (ttn > 0) s.Sprm[4] = (ushort)ttn;
                        }
                    }
                    else
                    {
                        SaveResume(s, link.Data2);
                        vtsn = s.Vtsn;
                        menu = link.Data1;
                    }

                    var pgcn = _disc.FindMenuPgc(DvdDomain.VtsMenu, vtsn, (MenuType)menu, UnitFor(s, DvdDomain.VtsMenu, vtsn));
                    if (pgcn == 0)
                    {
                        stop = Stop(VmStopKind.DeadEnd, s, ctx, $"VTS {vtsn} has no {(MenuType)menu} menu");
                        return null;
                    }
                    s.Domain = DvdDomain.VtsMenu;
                    s.Vtsn = vtsn;
                    s.Pgcn = pgcn;
                    s.Pgn = 1;
                    return Phase.EnterPgc;
                }

            case LinkCmd.JumpSS_VMGM_PGC:
            case LinkCmd.CallSS_VMGM_PGC:
                if (link.Cmd == LinkCmd.CallSS_VMGM_PGC) SaveResume(s, link.Data2);
                s.Domain = DvdDomain.VmgMenu;
                s.Vtsn = 0;
                s.Pgcn = link.Data1;
                s.Pgn = 1;
                return Phase.EnterPgc;

            default:
                stop = Stop(VmStopKind.DeadEnd, s, ctx, $"unsupported link {link.Cmd}");
                return null;
        }
    }

    private Phase? EnterTitle(VmState s, int ttn, int chapter, RunContext ctx, out VmResult? stop)
    {
        stop = null;
        var t = _disc.GetTitle(ttn);
        if (t is null)
        {
            stop = Stop(VmStopKind.DeadEnd, s, ctx, $"title {ttn} does not exist");
            return null;
        }
        var ptt = _disc.GetPtt(t.Vtsn, t.VtsTtn, chapter) ?? _disc.GetPtt(t.Vtsn, t.VtsTtn, 1);
        if (ptt is not { } p)
        {
            stop = Stop(VmStopKind.DeadEnd, s, ctx, $"title {ttn} has no chapter table");
            return null;
        }
        s.Domain = DvdDomain.Title;
        s.Vtsn = t.Vtsn;
        s.Sprm[4] = (ushort)ttn;
        s.Sprm[5] = (ushort)t.VtsTtn;
        s.Sprm[7] = (ushort)chapter;
        s.Pgcn = p.Pgcn;
        s.Pgn = p.Pgn;
        return Phase.EnterPgc;
    }

    private static void SaveResume(VmState s, int rsmCell)
    {
        if (s.Domain != DvdDomain.Title) return;
        s.Resume = new ResumePoint(s.Vtsn, s.Pgcn, rsmCell != 0 ? rsmCell : s.Cell,
            s.Sprm[4], s.Sprm[5], s.Sprm[6], s.Sprm[7]);
    }
}
