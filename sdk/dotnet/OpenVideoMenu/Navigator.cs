namespace OpenVideoMenu;

// Menu navigation as a pure state machine: Navigator.Step(manifest, state, event) → (state, effects).
// Players feed it input and playback events and carry out the effects. The conformance suite
// describes expected behaviour in exactly these terms, so every implementation is checked against
// the same cases. A port of packages/core/src/navigator.ts.

/// <summary>A remote key.</summary>
public enum Key
{
    Up,
    Down,
    Left,
    Right,
    Enter,
    Back,

    /// <summary>Level 2: the remote's Menu key (a disc's root menu), spec §6.6.</summary>
    Menu,

    /// <summary>Level 2: the remote's Title key (a disc's title menu).</summary>
    Title,

    /// <summary>Level 2: a call to the audio menu.</summary>
    AudioMenu,

    /// <summary>Level 2: a call to the subtitle menu.</summary>
    SubtitleMenu,

    /// <summary>Level 2: a call to the angle menu.</summary>
    AngleMenu,

    /// <summary>Level 2: a call to the chapter menu.</summary>
    ChapterMenu,
}

/// <summary>Something that happened: input, or playback reaching a point.</summary>
public abstract record NavEvent
{
    /// <summary>
    /// Begin: run firstPlay, or open the root menu. <paramref name="Player"/> is what the player knows of
    /// the viewer's setup as "@" player state; <paramref name="Seed"/> seeds random (Level 2).
    /// </summary>
    public sealed record Start(IReadOnlyDictionary<string, Scalar>? Player = null, long? Seed = null) : NavEvent;

    /// <summary>Level 2: player state changed outside the menus (e.g. the viewer picked another soundtrack).</summary>
    public sealed record PlayerChanged(IReadOnlyDictionary<string, Scalar> Values) : NavEvent;

    /// <summary>
    /// Remote or keyboard input. While playing, only Back and the menu keys are handled. With a menu key
    /// during playback, players pass the <paramref name="Position"/> (seconds) it was pressed at, for resume.
    /// </summary>
    public sealed record Press(Key Key, double? Position = null) : NavEvent;

    /// <summary>A pointer or touch moved onto a button.</summary>
    public sealed record Select(string Button) : NavEvent;

    /// <summary>A pointer or touch clicked a button.</summary>
    public sealed record Activate(string Button) : NavEvent;

    /// <summary>The current playback item finished on its own.</summary>
    public sealed record Ended : NavEvent;

    /// <summary>The viewer stopped playback early; the position (seconds) is remembered for resume.</summary>
    public sealed record Stopped(double Position) : NavEvent;

    /// <summary>The menu intro reached buttonsAt; buttons appear and respond.</summary>
    public sealed record ButtonsReady : NavEvent;

    /// <summary>A transition clip finished playing.</summary>
    public sealed record TransitionEnded : NavEvent;

    /// <summary>Level 2: the countdown a <see cref="Effect.StartTimer"/> asked for ran out on <paramref name="Menu"/>.</summary>
    public sealed record Timeout(string Menu) : NavEvent;

    /// <summary>
    /// Level 2: playback passed a cue the play effect listed: a point cue's at, or an overlay's from (its
    /// buttons show). Not for the time playback starts from, nor when seeking past it.
    /// </summary>
    public sealed record Cue(string Id) : NavEvent;

    /// <summary>Level 2: playback passed an overlay's to (or left its window by seeking): its buttons go.</summary>
    public sealed record CueEnd(string Id) : NavEvent;

    /// <summary>
    /// Level 2: the menu's clock entered the window of its button set <paramref name="Index"/>, or left
    /// them all (null): those buttons show now (spec §5.3).
    /// </summary>
    public sealed record ButtonSetChanged(int? Index) : NavEvent;
}

/// <summary>Something the player must do.</summary>
public abstract record Effect
{
    /// <summary>
    /// Start playing a media entry from <paramref name="Start"/> seconds, with tracks when given (Level 2).
    /// <paramref name="Angle"/> is set for media with angles (spec §4.1): 1 = its ref, 2 = Angles[0] or video track 2, ….
    /// <paramref name="End"/>: stop there and report <see cref="NavEvent.Ended"/>; <paramref name="Cues"/>:
    /// report <see cref="NavEvent.Cue"/>/<see cref="NavEvent.CueEnd"/> for these while it plays (spec §6.3).
    /// </summary>
    public sealed record Play(
        string Media,
        double Start,
        int? Audio = null,
        int? Subtitle = null,
        int? Angle = null,
        double? End = null,
        IReadOnlyList<CueWatch>? Cues = null,
        IReadOnlyList<string>? Prohibits = null,
        IReadOnlyList<Hold>? Holds = null) : Effect
    {
        public bool Equals(Play? other) =>
            other is not null && Media == other.Media && Start == other.Start && Audio == other.Audio && Subtitle == other.Subtitle &&
            Angle == other.Angle && End == other.End && (Cues ?? []).SequenceEqual(other.Cues ?? []) &&
            (Prohibits ?? []).SequenceEqual(other.Prohibits ?? []) && (Holds ?? []).SequenceEqual(other.Holds ?? []);

        public override int GetHashCode() => HashCode.Combine(Media, Start, Audio, Subtitle, Angle, End, Cues?.Count ?? 0);

        public override string ToString() =>
            $"Play {{ Media = {Media}, Start = {Start}, Audio = {Audio}, Subtitle = {Subtitle}, Angle = {Angle}, End = {End}, Cues = [{string.Join(", ", Cues ?? [])}], Prohibits = [{string.Join(", ", Prohibits ?? [])}], Holds = [{string.Join(", ", Holds ?? [])}] }}";
    }

    /// <summary>Briefly show the button's activated state.</summary>
    public sealed record Activated(string Menu, string Button) : Effect;

    /// <summary>Level 2: briefly show the activated state of a button of the overlay showing during playback.</summary>
    public sealed record OverlayActivated(string Cue, string Button) : Effect;

    /// <summary>Back was pressed at the top of the menus; the host may leave the title.</summary>
    public sealed record Exit : Effect;

    /// <summary>Play a transition clip, then report <see cref="NavEvent.TransitionEnded"/>.</summary>
    public sealed record Transition(string Src) : Effect;

    /// <summary>The viewer skipped the menu intro; jump the background to buttonsAt.</summary>
    public sealed record SkipIntro(string Menu) : Effect;

    /// <summary>
    /// Level 2: (re)start the countdown for <paramref name="Menu"/>'s timeout: report
    /// <see cref="NavEvent.Timeout"/> after <paramref name="After"/> seconds, unless another StartTimer
    /// replaces it or the viewer leaves that menu first.
    /// </summary>
    public sealed record StartTimer(string Menu, double After) : Effect;

    /// <summary>
    /// Level 2: move the playback in progress to <paramref name="Position"/> seconds on its timeline (a
    /// seek action, spec §6.3). Cues behave as when the viewer seeks.
    /// </summary>
    public sealed record Seek(double Position) : Effect;

    /// <summary>
    /// Level 2: the menus changed a track of the playback in progress (a set on @audio, @subtitle or
    /// @angle during playback, a disc's SetSTN in a cell command): apply it now, from where playback is
    /// (<paramref name="At"/>, the cue's time, when the player doesn't know better).
    /// </summary>
    public sealed record Tracks(double At, int? Audio = null, int? Subtitle = null, int? Angle = null) : Effect;

    /// <summary>
    /// Level 2: one event ran <see cref="Navigator.StepBudget"/> actions (a named action running itself
    /// without end); the rest were dropped. For diagnostics: players carry on with the state as it is.
    /// </summary>
    public sealed record BudgetExceeded : Effect;
}

public enum NavMode
{
    Idle,
    Menu,
    Playing,
    Fallback,
    Transition,
}

/// <summary>A menu and the button focused on it.</summary>
public sealed record MenuPosition(string Menu, string Focus);

/// <summary>
/// Playback in progress: the item playing now and those still queued. <paramref name="ResumeTo"/>:
/// playback a cue interrupted, which continues from its time when this ends or is stopped (Level 2).
/// </summary>
public sealed record Playback(PlayItem Item, IReadOnlyList<PlayItem> Queue, MenuAction? After, MenuPosition? ReturnTo, Interrupted? ResumeTo = null);

/// <summary>Level 2: playback a cue interrupted, and the time it continues from.</summary>
public sealed record Interrupted(Playback Playing, double At);

/// <summary>Level 2: the overlay showing during playback (its cue id) and its focused button.</summary>
public sealed record OverlayFocus(string Cue, string Focus);

/// <summary>
/// The resume point: where the viewer stopped (or a saveResume put it). Hosts may seed it with just
/// media and position. Set by the navigator, it also holds the rest of that playback (the item, what
/// was queued after it, its after-action), so resuming continues a play-all as a disc does.
/// </summary>
public sealed record LastStop(string Media, double Position, PlayItem? Item = null, IReadOnlyList<PlayItem>? Queue = null, MenuAction? After = null);

/// <summary>Navigation state. Treat as a value: <see cref="Navigator.Step"/> returns a new one.</summary>
public sealed record NavState
{
    public NavMode Mode { get; init; } = NavMode.Idle;

    public string? Menu { get; init; }

    public string? Focus { get; init; }

    /// <summary>
    /// True when the menu was entered fresh (start or goto), so its background plays its intro from
    /// 0; false when returning to it (Back, end of playback): the intro isn't replayed.
    /// </summary>
    public bool Intro { get; init; }

    /// <summary>False while a menu intro runs before buttonsAt; buttons are hidden and inactive.</summary>
    public bool ButtonsReady { get; init; } = true;

    /// <summary>The menu (and button) a running transition leads to.</summary>
    public (string Menu, string? Button)? TransitionTo { get; init; }

    /// <summary>Menus to return to with Back.</summary>
    public IReadOnlyList<MenuPosition> History { get; init; } = [];

    public Playback? Playing { get; init; }

    /// <summary>Level 2: the overlay showing during playback, if any.</summary>
    public OverlayFocus? Overlay { get; init; }

    /// <summary>Level 2: the menu's button set showing (its index in ButtonSets); null: the menu's own buttons.</summary>
    public int? ButtonSet { get; init; }

    /// <summary>Level 2: a button asked for on entering the menu that only one of its button sets has.</summary>
    public string? FocusWanted { get; init; }

    /// <summary>Where the viewer stopped last. Hosts may set it from their own records to seed resume.</summary>
    public LastStop? LastStop { get; init; }

    /// <summary>Level 2: variable values for this session.</summary>
    public IReadOnlyDictionary<string, Scalar> Vars { get; init; } = new Dictionary<string, Scalar>();

    /// <summary>Level 2: player state ("@audio", "@audioLanguage", …) as reported by the player or changed by set.</summary>
    public IReadOnlyDictionary<string, Scalar> Player { get; init; } = new Dictionary<string, Scalar>();

    /// <summary>Level 2: the random generator's state.</summary>
    public long Seed { get; init; } = 1;

    /// <summary>Counts menu entries and playback starts, so "do" knows when a step navigated.</summary>
    public int Seq { get; init; }
}

public static class Navigator
{
    /// <summary>Level 2: the most actions one event runs (spec §6.4), so a named action that runs itself still ends.</summary>
    public const int StepBudget = 10_000;

    /// <summary>Advance <paramref name="state"/> by one event. Pure: the input state isn't changed.</summary>
    public static (NavState State, IReadOnlyList<Effect> Effects) Step(Manifest manifest, NavState state, NavEvent navEvent)
    {
        var s = new Working(state);
        var effects = new List<Effect>();

        switch (navEvent)
        {
            case NavEvent.Start start:
                s.History.Clear();
                s.Playing = null;
                s.Vars = new Dictionary<string, Scalar>(manifest.Vars ?? []);
                foreach (var (k, v) in start.Player ?? new Dictionary<string, Scalar>())
                {
                    s.Player[k] = v;
                }

                if (start.Seed is { } seed)
                {
                    s.Seed = seed;
                }

                if (manifest.FirstPlay is { } firstPlay)
                {
                    Run(manifest, s, firstPlay, effects, pushHistory: false);
                }
                else
                {
                    OpenRoot(manifest, s, effects);
                }

                break;

            case NavEvent.Press or NavEvent.Select or NavEvent.Activate:
                // Any key or click skips a transition or a menu intro; the viewer is always in control.
                // Pointer hover (Select) is ignored meanwhile, so moving the mouse doesn't skip anything.
                if ((s.Mode == NavMode.Transition || (s.Mode == NavMode.Menu && !s.ButtonsReady)) && navEvent is NavEvent.Select)
                {
                    break;
                }

                if (s.Mode == NavMode.Transition)
                {
                    EndTransition(manifest, s, effects);
                    break;
                }

                var inMenu = s.Mode == NavMode.Menu ? s.Menu : null;
                if (s.Mode == NavMode.Menu && !s.ButtonsReady)
                {
                    s.ButtonsReady = true;
                    effects.Add(new Effect.SkipIntro(s.Menu!));
                }
                else
                {
                    HandleInput(manifest, s, navEvent, effects);
                }

                // Input restarts the countdown of a menu still showing (unless it says
                // restartOnInput: false), but not when the viewer is leaving (exit).
                if (inMenu is not null && s.Mode == NavMode.Menu && s.Menu == inMenu &&
                    !effects.Any(e => e is Effect.StartTimer or Effect.Exit) &&
                    MenuOf(manifest, inMenu).Timeout is { } restart && restart.RestartOnInput != false)
                {
                    effects.Add(new Effect.StartTimer(inMenu, restart.After));
                }

                break;

            case NavEvent.Timeout timeout:
                // Only for the menu still showing: a countdown from a menu the viewer has left is stale.
                // Nobody chose this, so it runs as a step up, like a menu's back.
                if (s.Mode == NavMode.Menu && s.Menu == timeout.Menu && MenuOf(manifest, s.Menu).Timeout is { } t)
                {
                    Run(manifest, s, t.Action, effects, pushHistory: false, up: true);
                }

                break;

            case NavEvent.ButtonsReady:
                if (s.Mode == NavMode.Menu)
                {
                    s.ButtonsReady = true;
                }

                break;

            case NavEvent.TransitionEnded:
                if (s.Mode == NavMode.Transition)
                {
                    EndTransition(manifest, s, effects);
                }

                break;

            case NavEvent.Ended:
                if (s.Mode != NavMode.Playing || s.Playing is null)
                {
                    break;
                }

                if (s.Playing.Queue.Count > 0)
                {
                    var next = s.Playing.Queue[0];
                    s.Playing = s.Playing with { Item = next, Queue = s.Playing.Queue.Skip(1).ToList() };
                    s.Overlay = null;
                    effects.Add(StartItem(manifest, s, next));
                }
                else
                {
                    FinishPlayback(manifest, s, effects, completed: true, stoppedAt: null);
                }

                break;

            case NavEvent.PlayerChanged changed:
                foreach (var (k, v) in changed.Values)
                {
                    s.Player[k] = v;
                }

                break;

            case NavEvent.Stopped stopped:
                if (s.Mode == NavMode.Playing)
                {
                    FinishPlayback(manifest, s, effects, completed: false, stoppedAt: stopped.Position);
                }

                break;

            case NavEvent.Cue cueEvent:
            {
                if (s.Mode != NavMode.Playing || s.Playing is null)
                {
                    break;
                }

                switch (CueById(manifest, s.Playing.Item, cueEvent.Id))
                {
                    case PointCue point:
                        Run(manifest, s, point.Action, effects, pushHistory: false, cueAt: point.At);
                        break;
                    case Overlay overlay:
                        // The overlay's buttons show; a later one replaces one still showing.
                        s.Overlay = new OverlayFocus(cueEvent.Id, ResolveButton(s, overlay.Buttons, overlay.DefaultButton) ?? overlay.Buttons[0].Id);
                        break;
                }

                break;
            }

            case NavEvent.CueEnd cueEnd:
                if (s.Overlay?.Cue == cueEnd.Id)
                {
                    s.Overlay = null;
                }

                break;

            case NavEvent.ButtonSetChanged changed:
            {
                if (s.Mode != NavMode.Menu || s.Menu is null)
                {
                    break;
                }

                var menu = MenuOf(manifest, s.Menu);
                if ((changed.Index is { } i && (menu.ButtonSets is null || i < 0 || i >= menu.ButtonSets.Count)) || changed.Index == s.ButtonSet)
                {
                    break;
                }

                // The highlighted button stays: by id when the new buttons have it, otherwise by its
                // place (a disc keeps the selected button number when its highlights change mid-cell).
                var place = Shown(manifest, s).Buttons.FindIndex(b => b.Id == s.Focus);
                s.ButtonSet = changed.Index;
                var now = Shown(manifest, s);
                string? wanted = null;
                if (s.FocusWanted is { } w && FindButton(now.Buttons, w) is not null)
                {
                    wanted = w;
                    s.FocusWanted = null;
                }

                s.Focus = wanted
                    ?? (FindButton(now.Buttons, s.Focus!) is not null ? s.Focus : place >= 0 ? now.Buttons[Math.Min(place, now.Buttons.Count - 1)].Id : null)
                    ?? ResolveButton(s, now.Buttons, now.DefaultButton)
                    ?? now.Buttons[0].Id;
                break;
            }
        }

        return (s.ToState(), effects);
    }

    /// <summary>Start offset in seconds for a play item (a chapter wins over start).</summary>
    public static double StartTime(Manifest manifest, PlayItem item)
    {
        if (item.Chapter is { } chapter)
        {
            var chapters = manifest.Media.TryGetValue(item.Media, out var media) ? media.Chapters : null;
            return chapters is not null && chapter >= 1 && chapter <= chapters.Count ? chapters[chapter - 1].Time : 0;
        }

        return item.Start ?? 0;
    }

    /// <summary>
    /// The nearest button in a direction, for menus with autoNav. Candidates must lie that way (by
    /// centre); the score favours a short distance along the axis and penalises sideways offset.
    /// </summary>
    public static Button? NearestButton(Menu menu, Button from, Direction dir) => NearestButton(menu.Buttons, from, dir);

    /// <summary>The nearest of <paramref name="buttons"/> in a direction (a menu's, or an overlay's).</summary>
    public static Button? NearestButton(IReadOnlyList<Button> buttons, Button from, Direction dir)
    {
        var (fx, fy) = (from.Rect.CentreX, from.Rect.CentreY);
        Button? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var b in buttons)
        {
            if (b.Id == from.Id)
            {
                continue;
            }

            var dx = b.Rect.CentreX - fx;
            var dy = b.Rect.CentreY - fy;
            var along = dir switch { Direction.Left => -dx, Direction.Right => dx, Direction.Up => -dy, _ => dy };
            var sideways = dir is Direction.Left or Direction.Right ? Math.Abs(dy) : Math.Abs(dx);
            if (along <= 0)
            {
                continue;
            }

            var score = along + 2 * sideways;
            if (score < bestScore)
            {
                bestScore = score;
                best = b;
            }
        }

        return best;
    }

    /// <summary>
    /// Level 2: a menu key (spec §6.6). During playback it runs like a cue's action at the position it
    /// was pressed: a menu it opens leaves playback, remembered for resume; playback it starts returns
    /// there. The item's keys win over the media's, the media's over the manifest's. On a menu, the
    /// menu's keys win over the manifest's, run as a step up. Without an action, Menu and Title open the
    /// root menu.
    /// </summary>
    private static void MenuKey(Manifest manifest, Working s, Key key, double? position, List<Effect> effects)
    {
        MenuAction? fallback = key is Key.Menu or Key.Title && manifest.RootMenu is { } root ? new GotoAction { Menu = root } : null;
        if (s.Mode == NavMode.Playing && s.Playing is { } playing)
        {
            var item = playing.Item;
            var action = item.Keys?.For(key) ?? manifest.Media.GetValueOrDefault(item.Media)?.Keys?.For(key) ?? manifest.Keys?.For(key) ?? fallback;
            if (action is not null)
            {
                Run(manifest, s, action, effects, pushHistory: false, cueAt: position ?? StartTime(manifest, item));
            }
        }
        else if (s.Mode == NavMode.Menu && s.Menu is not null)
        {
            var action = MenuOf(manifest, s.Menu).Keys?.For(key) ?? manifest.Keys?.For(key) ?? fallback;
            if (action is not null)
            {
                Run(manifest, s, action, effects, pushHistory: false, up: true);
            }
        }
    }

    private static void HandleInput(Manifest manifest, Working s, NavEvent navEvent, List<Effect> effects)
    {
        switch (navEvent)
        {
            case NavEvent.Press press:
                if (press.Key >= Key.Menu)
                {
                    MenuKey(manifest, s, press.Key, press.Position, effects);
                    break;
                }

                if (s.Mode == NavMode.Playing)
                {
                    // Players report the position with Stopped; a bare Back stops at an unknown position.
                    if (press.Key == Key.Back)
                    {
                        FinishPlayback(manifest, s, effects, completed: false, stoppedAt: null);
                    }

                    // With an overlay showing, the arrows and Enter are its; otherwise they're the video player's.
                    else if (s.Overlay is not null)
                    {
                        if (press.Key == Key.Enter)
                        {
                            ActivateOverlay(manifest, s, s.Overlay.Focus, effects);
                        }
                        else
                        {
                            MoveOverlay(manifest, s, (Direction)(int)press.Key, effects);
                        }
                    }

                    break;
                }

                if (s.Mode != NavMode.Menu)
                {
                    break;
                }

                switch (press.Key)
                {
                    case Key.Enter:
                        Activate(manifest, s, s.Focus!, effects);
                        break;
                    case Key.Back:
                        RemoteBack(manifest, s, effects);
                        break;
                    default:
                        Move(manifest, s, (Direction)(int)press.Key, effects);
                        break;
                }

                break;

            case NavEvent.Select select:
            {
                if (s.Mode == NavMode.Playing && s.Overlay is not null)
                {
                    if (OverlayOf(manifest, s) is { } overlay && select.Button != s.Overlay.Focus && FindButton(overlay.Buttons, select.Button) is not null)
                    {
                        s.Overlay = s.Overlay with { Focus = select.Button };
                    }

                    break;
                }

                if (s.Mode != NavMode.Menu || select.Button == s.Focus)
                {
                    break;
                }

                if (FindButton(Shown(manifest, s).Buttons, select.Button) is { } button)
                {
                    FocusButton(manifest, s, button, effects);
                }

                break;
            }

            case NavEvent.Activate activate:
                if (s.Mode == NavMode.Playing && s.Overlay is not null)
                {
                    if (OverlayOf(manifest, s) is { } overlay && FindButton(overlay.Buttons, activate.Button) is not null)
                    {
                        ActivateOverlay(manifest, s, activate.Button, effects);
                    }

                    break;
                }

                if (s.Mode != NavMode.Menu)
                {
                    break;
                }

                if (FindButton(Shown(manifest, s).Buttons, activate.Button) is not null)
                {
                    s.Focus = activate.Button;
                    Activate(manifest, s, activate.Button, effects);
                }

                break;
        }
    }

    private static Menu MenuOf(Manifest manifest, string id) =>
        manifest.Menus is not null && manifest.Menus.TryGetValue(id, out var menu) ? menu : throw new InvalidOperationException($"No menu \"{id}\"");

    private static Button? FindButton(Menu menu, string id) => FindButton(menu.Buttons, id);

    /// <summary>The buttons a menu shows now, with their default button and autoNav.</summary>
    public sealed record ButtonView(List<Button> Buttons, ButtonRef? DefaultButton, bool? AutoNav);

    /// <summary>
    /// The buttons the current menu shows now: a button set's while its window runs (Level 2), otherwise
    /// the menu's own. For renderers to draw.
    /// </summary>
    public static ButtonView ButtonsShown(Manifest manifest, NavState state) => Shown(manifest, state.Menu!, state.ButtonSet);

    private static ButtonView Shown(Manifest manifest, Working s) => Shown(manifest, s.Menu!, s.ButtonSet);

    private static ButtonView Shown(Manifest manifest, string menuId, int? set)
    {
        var menu = MenuOf(manifest, menuId);
        return set is { } i && menu.ButtonSets is { } sets && i < sets.Count
            ? new ButtonView(sets[i].Buttons, sets[i].DefaultButton, sets[i].AutoNav)
            : new ButtonView(menu.Buttons, menu.DefaultButton, menu.AutoNav);
    }

    private static Button? FindButton(IEnumerable<Button> buttons, string id) => buttons.FirstOrDefault(b => b.Id == id);

    /// <summary>Level 2: the cues that apply while <paramref name="item"/> plays, with their ids: the media's, then the item's.</summary>
    private static IEnumerable<(string Id, Cue Cue)> CuesOf(Manifest manifest, PlayItem item) =>
        (manifest.Media.TryGetValue(item.Media, out var m) ? m.Cues ?? [] : []).Select((c, i) => ($"media/{i}", c))
            .Concat((item.Cues ?? []).Select((c, i) => ($"item/{i}", c)));

    private static Cue? CueById(Manifest manifest, PlayItem item, string id) =>
        CuesOf(manifest, item).Where(c => c.Id == id).Select(c => c.Cue).FirstOrDefault();

    private static Overlay? OverlayOf(Manifest manifest, Working s) =>
        s.Overlay is not null && s.Playing is not null ? CueById(manifest, s.Playing.Item, s.Overlay.Cue) as Overlay : null;

    private static void MoveOverlay(Manifest manifest, Working s, Direction dir, List<Effect> effects)
    {
        if (OverlayOf(manifest, s) is not { } overlay || s.Overlay is null)
        {
            return;
        }

        var current = FindButton(overlay.Buttons, s.Overlay.Focus)!;
        var targetId = current.Nav?[dir];
        if (string.IsNullOrEmpty(targetId) && overlay.AutoNav == true)
        {
            targetId = NearestButton(overlay.Buttons, current, dir)?.Id;
        }

        if (string.IsNullOrEmpty(targetId) || targetId == current.Id)
        {
            return;
        }

        s.Overlay = s.Overlay with { Focus = targetId };
        if (FindButton(overlay.Buttons, targetId)!.AutoAction == true)
        {
            ActivateOverlay(manifest, s, targetId, effects);
        }
    }

    /// <summary>Press an overlay button: its action runs at the overlay's end time (where interrupted playback resumes).</summary>
    private static void ActivateOverlay(Manifest manifest, Working s, string buttonId, List<Effect> effects)
    {
        if (OverlayOf(manifest, s) is not { } overlay || s.Overlay is null || FindButton(overlay.Buttons, buttonId) is not { } button)
        {
            return;
        }

        s.Overlay = s.Overlay with { Focus = button.Id };
        effects.Add(new Effect.OverlayActivated(s.Overlay.Cue, button.Id));
        Run(manifest, s, button.Action, effects, pushHistory: false, cueAt: overlay.To);
    }

    private static void OpenRoot(Manifest manifest, Working s, List<Effect> effects)
    {
        if (manifest.RootMenu is null || manifest.Menus is null)
        {
            s.Mode = NavMode.Fallback;
            s.Menu = s.Focus = null;
            return;
        }

        EnterMenu(manifest, s, manifest.RootMenu, null, intro: true, effects);
    }

    /// <summary>A button reference (an id, or a variable holding one) resolved to a button of the menu, if it is one.</summary>
    private static string? ResolveButton(Working s, Menu menu, ButtonRef? r) => ResolveButton(s, menu.Buttons, r);

    private static string? ResolveButton(Working s, IEnumerable<Button> buttons, ButtonRef? r)
    {
        if (r is null)
        {
            return null;
        }

        var id = r.Id ?? s.Read(r.Var!).ToString();
        return FindButton(buttons, id) is not null ? id : null;
    }

    private static void EnterMenu(Manifest manifest, Working s, string menuId, ButtonRef? buttonId, bool intro, List<Effect> effects)
    {
        var menu = MenuOf(manifest, menuId);
        s.Mode = NavMode.Menu;
        s.Menu = menuId;
        s.Seq++;
        s.ButtonSet = null;
        s.Focus = ResolveButton(s, menu, buttonId) ?? ResolveButton(s, menu, menu.DefaultButton) ?? menu.Buttons[0].Id;
        // Level 2: a button only a button set has is focused when its set shows (the player reports it).
        var asked = buttonId is null ? null : buttonId.Var is { } v ? s.Read(v).ToString() : buttonId.Id;
        s.FocusWanted = asked is not null && asked != s.Focus && menu.ButtonSets?.Any(set => FindButton(set.Buttons, asked) is not null) == true ? asked : null;
        s.Intro = intro;
        s.ButtonsReady = !(intro && (menu.ButtonsAt ?? 0) > 0);
        s.TransitionTo = null;

        // Every time the menu appears (fresh or returned to), its countdown starts over.
        if (menu.Timeout is { } timeout)
        {
            effects.Add(new Effect.StartTimer(menuId, timeout.After));
        }
    }

    private static void EndTransition(Manifest manifest, Working s, List<Effect> effects)
    {
        var to = s.TransitionTo!.Value;
        EnterMenu(manifest, s, to.Menu, to.Button, intro: true, effects);
    }

    private static void FocusButton(Manifest manifest, Working s, Button button, List<Effect> effects)
    {
        s.Focus = button.Id;
        if (button.AutoAction == true)
        {
            Activate(manifest, s, button.Id, effects);
        }
    }

    private static void Move(Manifest manifest, Working s, Direction dir, List<Effect> effects)
    {
        var shown = Shown(manifest, s);
        var current = FindButton(shown.Buttons, s.Focus!)!;
        var targetId = current.Nav?[dir];
        if (string.IsNullOrEmpty(targetId) && shown.AutoNav == true)
        {
            targetId = NearestButton(shown.Buttons, current, dir)?.Id;
        }

        if (string.IsNullOrEmpty(targetId) || targetId == current.Id)
        {
            return;
        }

        FocusButton(manifest, s, FindButton(shown.Buttons, targetId)!, effects);
    }

    private static void Activate(Manifest manifest, Working s, string buttonId, List<Effect> effects)
    {
        var button = FindButton(Shown(manifest, s).Buttons, buttonId)!;
        effects.Add(new Effect.Activated(s.Menu!, button.Id));
        Run(manifest, s, button.Action, effects, pushHistory: true);
    }

    /// <summary>
    /// The remote's Back on a menu. A menu's back action says where Back goes (like a DVD's "Go
    /// Up"), run as a step up: no history is added, and a target already in history unwinds it.
    /// Without one, Back returns through history, except on the root menu, where it leaves the title.
    /// Back never traps the viewer: if back leaves them on the same menu, they step back (or leave).
    /// </summary>
    private static void RemoteBack(Manifest manifest, Working s, List<Effect> effects)
    {
        var menu = MenuOf(manifest, s.Menu!);
        if (menu.Back is null)
        {
            BackWithoutTarget(manifest, s, effects);
            return;
        }

        var from = s.Menu;
        Run(manifest, s, menu.Back, effects, pushHistory: false, up: true);
        if (s.Mode == NavMode.Menu && s.Menu == from)
        {
            BackWithoutTarget(manifest, s, effects);
        }
    }

    private static void BackWithoutTarget(Manifest manifest, Working s, List<Effect> effects)
    {
        if (s.Menu == manifest.RootMenu)
        {
            effects.Add(new Effect.Exit());
        }
        else
        {
            GoBack(manifest, s, effects);
        }
    }

    private static void GoBack(Manifest manifest, Working s, List<Effect> effects)
    {
        if (s.History.Count == 0)
        {
            effects.Add(new Effect.Exit());
            return;
        }

        var previous = s.History[^1];
        s.History.RemoveAt(s.History.Count - 1);
        EnterMenu(manifest, s, previous.Menu, previous.Focus, intro: false, effects);
    }

    private static MenuPosition? CurrentPosition(Working s) =>
        s.Mode == NavMode.Menu && s.Menu is not null && s.Focus is not null ? new MenuPosition(s.Menu, s.Focus) : null;

    /// <summary>
    /// <paramref name="up"/>: running a menu's back (see RemoteBack): gotos unwind history instead of adding to it.
    /// <paramref name="cueAt"/>: running a cue's action during playback (Level 2); the time interrupted playback resumes from.
    /// </summary>
    private static void Run(Manifest manifest, Working s, MenuAction first, List<Effect> effects, bool pushHistory, bool up = false, double? cueAt = null)
    {
        // An action in tail position (set's then, if's branches, do's last step, resume's otherwise, a
        // pressed button's action, a named action) continues this loop instead of nesting a call, so a
        // disc's loop, written as a named action that runs itself, goes round as often as the budget allows.
        var next = first;
        while (next is not null)
        {
            if (s.StepsLeft <= 0)
            {
                if (s.StepsLeft == 0)
                {
                    effects.Add(new Effect.BudgetExceeded());
                }

                s.StepsLeft = -1;
                return;
            }

            s.StepsLeft--;
            var action = next;
            next = null;
            var duringPlayback = cueAt is not null && s.Mode == NavMode.Playing && s.Playing is not null;
            switch (action)
            {
                case GotoAction g:
                {
                    // A cue opening a menu leaves playback, remembered for resume where the cue is.
                    if (duringPlayback)
                    {
                        LeavePlayback(manifest, s, cueAt!.Value);
                    }

                    var here = CurrentPosition(s);
                    if (up)
                    {
                        // Going up to a menu already in history: drop it and everything after it.
                        var i = s.History.FindLastIndex(h => h.Menu == g.Menu);
                        if (i >= 0)
                        {
                            s.History.RemoveRange(i, s.History.Count - i);
                        }
                    }
                    else if (pushHistory && here is not null && g.History != "replace")
                    {
                        s.History.Add(here);
                    }

                    if (!string.IsNullOrEmpty(g.Transition))
                    {
                        s.Mode = NavMode.Transition;
                        // A variable button is read now, when the viewer chose it.
                        s.TransitionTo = (g.Menu, ResolveButton(s, MenuOf(manifest, g.Menu), g.Button));
                        effects.Add(new Effect.Transition(g.Transition));
                    }
                    else
                    {
                        EnterMenu(manifest, s, g.Menu, g.Button, intro: true, effects);
                    }

                    break;
                }

                case BackAction:
                    // From a cue: like the remote's Back during playback.
                    if (duringPlayback)
                    {
                        FinishPlayback(manifest, s, effects, completed: false, stoppedAt: cueAt);
                    }
                    else
                    {
                        GoBack(manifest, s, effects);
                    }

                    break;
                case PlayAction p:
                    StartPlayback(manifest, s, [p.ToItem()], p.After, effects, cueAt);
                    break;
                case PlayAllAction pa:
                    StartPlayback(manifest, s, pa.Items, pa.After, effects, cueAt);
                    break;
                case ResumeAction resume:
                    if (s.LastStop is { } stop)
                    {
                        // The stopped playback continues: the item from where it stopped (with the tracks as
                        // they are now, which the viewer may have changed while watching), then what was
                        // queued after it, then its after-action. A disc's resume point is used up by resuming.
                        var item = new PlayItem { Media = stop.Media, Start = stop.Position, End = stop.Item?.End, Cues = stop.Item?.Cues, Extensions = stop.Item?.Extensions };
                        if (manifest.ResumePoint == "untilUsed")
                        {
                            s.LastStop = null;
                        }

                        StartPlayback(manifest, s, [item, .. stop.Queue ?? []], stop.After, effects, cueAt);
                    }
                    else
                    {
                        // Nothing to resume: the fallback runs as if it were the action itself.
                        next = resume.Otherwise;
                    }

                    break;
                case SaveResumeAction save:
                    if (save.Media is { } savedMedia)
                    {
                        s.LastStop = new LastStop(savedMedia, save.Chapter is { } c ? StartTime(manifest, new PlayItem { Media = savedMedia, Chapter = c }) : save.At ?? 0);
                    }
                    else if (s.Mode == NavMode.Playing && s.Playing is { } here)
                    {
                        // "Here": the playback in progress (a disc's CallSS names the cell to resume at).
                        var at = save.Chapter is { } c ? StartTime(manifest, new PlayItem { Media = here.Item.Media, Chapter = c }) : save.At ?? cueAt ?? 0;
                        s.LastStop = ResumePointOf(here, at);
                    }

                    s.ResumeSet = true;
                    break;
                case ClearResumeAction:
                    s.LastStop = null;
                    s.ResumeSet = true;
                    break;
                case SeekAction seek:
                    // Only during playback: the title continues elsewhere, as a link ends a disc's program.
                    if (duringPlayback)
                    {
                        var media = s.Playing!.Item.Media;
                        var position = seek.Chapter is { } chapter ? StartTime(manifest, new PlayItem { Media = media, Chapter = chapter }) : seek.At ?? 0;
                        s.Overlay = null;
                        s.Seq++;
                        s.Player["@lastChapter"] = ChapterAt(manifest, media, position);
                        effects.Add(new Effect.Seek(position));
                    }

                    break;
                case SetAction set:
                {
                    var operand = set.Value.Var is { } v ? s.Read(v) : set.Value.Literal!.Value;
                    var seed = s.Seed;
                    var result = Logic.Apply(ref seed, s.Read(set.Var), set.Op ?? "set", operand);
                    s.Seed = seed;
                    if (result is { } value)
                    {
                        if (set.Var == "@focus")
                        {
                            // Highlight a button of the current menu (a DVD's SetHL_BTN).
                            if (s.Menu is not null && FindButton(Shown(manifest, s).Buttons, value.ToString()) is not null)
                            {
                                s.Focus = value.ToString();
                            }
                        }
                        else if (set.Var.StartsWith('@'))
                        {
                            s.Player[set.Var] = value;
                            if (duringPlayback)
                            {
                                LiveTrack(manifest, s, set.Var, value, cueAt!.Value, effects);
                            }
                        }
                        else
                        {
                            s.Vars = new Dictionary<string, Scalar>(s.Vars) { [set.Var] = value };
                        }
                    }

                    next = set.Then;
                    break;
                }

                case IfAction ifAction:
                    next = Logic.Eval(ifAction.Test, s.Read) ? ifAction.Then : ifAction.Else;
                    break;
                case DoAction doAction:
                {
                    // In order, until a step navigates: opens a menu, starts playback or a transition, or leaves.
                    var last = doAction.Steps.Count - 1;
                    for (var i = 0; i < last; i++)
                    {
                        var (seq, mode, count) = (s.Seq, s.Mode, effects.Count);
                        Run(manifest, s, doAction.Steps[i], effects, pushHistory, up, cueAt);
                        if (s.Seq != seq || s.Mode != mode || effects.Skip(count).Any(e => e is Effect.Exit or Effect.Transition) || s.StepsLeft < 0)
                        {
                            return;
                        }
                    }

                    next = last >= 0 ? doAction.Steps[last] : null;
                    break;
                }

                case ActivateAction a:
                {
                    // Press a button of the current menu, as if the viewer had: focus, activated state, action.
                    var button = s.Menu is not null ? FindButton(Shown(manifest, s).Buttons, a.Button) : null;
                    if (button is null)
                    {
                        break;
                    }

                    s.Focus = button.Id;
                    effects.Add(new Effect.Activated(s.Menu!, button.Id));
                    next = button.Action;
                    break;
                }

                case RunAction r:
                    // As if the named action were written here: same history rules, same cue time.
                    next = manifest.Actions?.GetValueOrDefault(r.Action);
                    break;
            }
        }
    }

    /// <summary><paramref name="cueAt"/>: started by a cue during playback, which it interrupts and returns to (Level 2).</summary>
    private static void StartPlayback(Manifest manifest, Working s, IReadOnlyList<PlayItem> items, MenuAction? after, List<Effect> effects, double? cueAt = null)
    {
        var interrupted = cueAt is not null && s.Mode == NavMode.Playing ? s.Playing : null;
        s.Playing = interrupted is not null
            ? new Playback(items[0], items.Skip(1).ToList(), null, interrupted.ReturnTo, new Interrupted(interrupted, cueAt!.Value))
            : new Playback(items[0], items.Skip(1).ToList(), after, CurrentPosition(s));
        s.Mode = NavMode.Playing;
        s.Overlay = null;
        s.Seq++;
        effects.Add(StartItem(manifest, s, items[0]));
    }

    /// <summary>
    /// A cue opened a menu: playback ends, remembered for resume at the cue's time, unless the cue saved
    /// or cleared the resume point itself (a disc's CallSS names its resume cell; a JumpSS saves none).
    /// </summary>
    private static void LeavePlayback(Manifest manifest, Working s, double at)
    {
        var media = s.Playing!.Item.Media;
        if (!s.ResumeSet)
        {
            s.LastStop = ResumePointOf(s.Playing, at);
        }

        s.Player["@lastChapter"] = ChapterAt(manifest, media, at);
        s.Playing = null;
        s.Overlay = null;
    }

    /// <summary>
    /// Level 2: a set on @audio, @subtitle or @angle during playback applies now (a disc's SetSTN in a
    /// cell command or a button over the film), not only to the next playback.
    /// </summary>
    private static void LiveTrack(Manifest manifest, Working s, string name, Scalar value, double at, List<Effect> effects)
    {
        if (value.Number is not { } n)
        {
            return;
        }

        if (name == "@audio" && n > 0)
        {
            effects.Add(new Effect.Tracks(at, Audio: n));
        }
        else if (name == "@subtitle")
        {
            effects.Add(new Effect.Tracks(at, Subtitle: n));
        }
        else if (name == "@angle" && AngleCount(manifest, s.Playing!.Item.Media) is var angles and > 1)
        {
            effects.Add(new Effect.Tracks(at, Angle: n >= 1 && n <= angles ? n : 1));
        }
    }

    /// <summary>The resume point for <paramref name="playing"/> stopped (or left) at <paramref name="at"/>.</summary>
    private static LastStop ResumePointOf(Playback playing, double at) =>
        new(playing.Item.Media, at, playing.Item, playing.Queue.Count > 0 ? playing.Queue : null, playing.After);

    /// <summary>Playback of the resume point's media completed: forget it, unless it lasts until used (a disc's).</summary>
    private static bool ForgetsOnEnd(Manifest manifest, Working s, Playback playing) =>
        manifest.ResumePoint != "untilUsed" && s.LastStop?.Media == playing.Item.Media;

    /// <summary>
    /// The play effect for an item, noting it as the last playback (@lastMedia, @lastChapter).
    /// <paramref name="from"/>: start elsewhere than the item's own start.
    /// </summary>
    private static Effect.Play StartItem(Manifest manifest, Working s, PlayItem item, double? from = null)
    {
        var effect = PlayEffect(manifest, item, s, from);
        s.Player["@lastMedia"] = item.Media;
        s.Player["@lastChapter"] = ChapterAt(manifest, item.Media, effect.Start);
        return effect;
    }

    /// <summary>
    /// How many angles a media has (1 when none). Made of segments: the most of its segments' media, as a
    /// play's angle applies to the segments whose media have angles (spec §4.2).
    /// </summary>
    public static int AngleCount(Manifest manifest, string media)
    {
        if (!manifest.Media.TryGetValue(media, out var m))
        {
            return 1;
        }

        if (m.Segments is { } segments)
        {
            return segments.Select(s => manifest.Media.TryGetValue(s.Media, out var src) && src.Segments is null ? src.AngleCount : 1).Append(1).Max();
        }

        return m.AngleCount;
    }

    /// <summary>The 1-based chapter a time falls in (0 when the media has no chapters).</summary>
    public static int ChapterAt(Manifest manifest, string media, double seconds)
    {
        var chapters = manifest.Media.TryGetValue(media, out var m) ? m.Chapters ?? [] : [];
        var n = 0;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Time <= seconds)
            {
                n = i + 1;
            }
        }

        return n;
    }

    private static void FinishPlayback(Manifest manifest, Working s, List<Effect> effects, bool completed, double? stoppedAt)
    {
        var playing = s.Playing!;
        s.Playing = null;
        s.Overlay = null;

        // Playback a cue started: the interrupted one continues where the cue was (Level 2).
        if (playing.ResumeTo is { } resumeTo)
        {
            if (completed && ForgetsOnEnd(manifest, s, playing))
            {
                s.LastStop = null;
            }

            s.Playing = resumeTo.Playing;
            s.Mode = NavMode.Playing;
            s.Seq++;
            effects.Add(StartItem(manifest, s, resumeTo.Playing.Item, resumeTo.At));
            return;
        }

        if (completed)
        {
            if (ForgetsOnEnd(manifest, s, playing))
            {
                s.LastStop = null;
            }
        }
        else if (stoppedAt is { } position)
        {
            s.LastStop = ResumePointOf(playing, position);
            s.Player["@lastChapter"] = ChapterAt(manifest, playing.Item.Media, position);
        }

        // Go back to where playback was started from, then run the after-action from there.
        if (playing.ReturnTo is { } returnTo)
        {
            EnterMenu(manifest, s, returnTo.Menu, returnTo.Focus, intro: false, effects);
        }
        else
        {
            OpenRoot(manifest, s, effects);
        }

        if (completed && playing.After is not null)
        {
            Run(manifest, s, playing.After, effects, pushHistory: true);
        }
    }

    /// <summary>
    /// Without tracks of its own, playback uses the current ones (after a set on @audio/@subtitle, like a
    /// DVD's SetSTN, or as the player reported them).
    /// </summary>
    private static Effect.Play PlayEffect(Manifest manifest, PlayItem item, Working s, double? from = null)
    {
        var audio = item.Audio?.Resolve(s.Read) ?? (s.Player.TryGetValue("@audio", out var a) && a.Number > 0 ? a.Number : null);
        var subtitle = item.Subtitle?.Resolve(s.Read) ?? (s.Player.TryGetValue("@subtitle", out var t) ? t.Number : null);
        // Media with angles plays the item's angle, otherwise @angle; one it doesn't have plays angle 1.
        int? angle = null;
        var angles = AngleCount(manifest, item.Media);
        if (angles > 1)
        {
            var wanted = item.Angle?.Resolve(s.Read) ?? (s.Player.TryGetValue("@angle", out var g) ? g.Number : null) ?? 1;
            angle = wanted >= 1 && wanted <= angles ? wanted : 1;
        }

        var cues = CuesOf(manifest, item)
            .Select(c => c.Cue is PointCue point ? new CueWatch(c.Id, At: point.At) : new CueWatch(c.Id, From: ((Overlay)c.Cue).From, To: ((Overlay)c.Cue).To))
            .ToList();
        var prohibits = (manifest.Media.GetValueOrDefault(item.Media)?.Prohibits ?? []).Concat(item.Prohibits ?? []).Distinct().ToList();
        var holds = (manifest.Media.GetValueOrDefault(item.Media)?.Holds ?? []).Concat(item.Holds ?? []).OrderBy(h => h.At).ToList();
        return new(item.Media, from ?? StartTime(manifest, item), audio, subtitle, angle, item.End, cues.Count > 0 ? cues : null,
            prohibits.Count > 0 ? prohibits : null, holds.Count > 0 ? holds : null);
    }

    /// <summary>A mutable copy of the state while a step runs.</summary>
    private sealed class Working(NavState from)
    {
        public NavMode Mode { get; set; } = from.Mode;

        public string? Menu { get; set; } = from.Menu;

        public string? Focus { get; set; } = from.Focus;

        public bool Intro { get; set; } = from.Intro;

        public bool ButtonsReady { get; set; } = from.ButtonsReady;

        public (string Menu, string? Button)? TransitionTo { get; set; } = from.TransitionTo;

        public List<MenuPosition> History { get; } = [.. from.History];

        public Playback? Playing { get; set; } = from.Playing;

        public OverlayFocus? Overlay { get; set; } = from.Overlay;

        public int? ButtonSet { get; set; } = from.ButtonSet;

        public string? FocusWanted { get; set; } = from.FocusWanted;

        public LastStop? LastStop { get; set; } = from.LastStop;

        public IReadOnlyDictionary<string, Scalar> Vars { get; set; } = from.Vars;

        public Dictionary<string, Scalar> Player { get; } = new(from.Player);

        public long Seed { get; set; } = from.Seed;

        public int Seq { get; set; } = from.Seq;

        /// <summary>Actions the current event may still run (not part of the state).</summary>
        public int StepsLeft { get; set; } = StepBudget;

        /// <summary>The current event saved or cleared the resume point itself, so leaving playback keeps that (Level 2).</summary>
        public bool ResumeSet { get; set; }

        public Scalar Read(string name) => name == "@resumeMedia" ? LastStop?.Media ?? "" : Logic.Read(Vars, Player, Focus, name);

        public NavState ToState() => new()
        {
            Mode = Mode,
            Menu = Menu,
            Focus = Focus,
            Intro = Intro,
            ButtonsReady = ButtonsReady,
            TransitionTo = TransitionTo,
            History = History,
            Playing = Playing,
            Overlay = Overlay,
            ButtonSet = ButtonSet,
            FocusWanted = FocusWanted,
            LastStop = LastStop,
            Vars = Vars,
            Player = Player,
            Seed = Seed,
            Seq = Seq,
        };
    }
}
