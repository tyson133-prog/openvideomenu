using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVideoMenu;

/// <summary>
/// What a button, a menu's Back key, first-play or a fallback item does. The JSON form is an
/// object whose <c>type</c> picks the subclass.
/// </summary>
[JsonConverter(typeof(MenuActionConverter))]
public abstract class MenuAction
{
    private protected MenuAction()
    {
    }

    /// <summary>The action's <c>type</c> in JSON: play, playAll, goto, back, resume, activate, set.</summary>
    [JsonPropertyOrder(-1)]
    public abstract string Type { get; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>Play one media entry; afterwards return to the menu and run <see cref="After"/>.</summary>
public sealed class PlayAction : MenuAction
{
    public override string Type => "play";

    public string Media { get; set; } = "";

    /// <summary>1-based chapter within the media.</summary>
    public int? Chapter { get; set; }

    /// <summary>Start in seconds (ignored when <see cref="Chapter"/> is given).</summary>
    public double? Start { get; set; }

    /// <summary>Level 2: audio track, 1-based.</summary>
    public IntOrVar? Audio { get; set; }

    /// <summary>Level 2: subtitle track, 1-based; 0 = off.</summary>
    public IntOrVar? Subtitle { get; set; }

    /// <summary>Level 2: angle of media with angles, 1-based; default @angle.</summary>
    public IntOrVar? Angle { get; set; }

    /// <summary>Level 2: stop here (seconds), as if the item ended (spec §6.3).</summary>
    public double? End { get; set; }

    /// <summary>Level 2: cues for this way of playing the media, beside the media's own (spec §6.3).</summary>
    public List<Cue>? Cues { get; set; }

    /// <summary>Level 2: what the menu keys do while this plays (spec §6.6).</summary>
    public MenuKeys? Keys { get; set; }

    /// <summary>Level 2: what the source forbids while this plays, as information (spec §6.6).</summary>
    public List<string>? Prohibits { get; set; }

    /// <summary>Level 2: frames held while this plays (spec §6.3).</summary>
    public List<Hold>? Holds { get; set; }

    /// <summary>Runs when playback finishes on its own; must not start another playback.</summary>
    public MenuAction? After { get; set; }

    /// <summary>What this plays, as a play item.</summary>
    public PlayItem ToItem() => new() { Media = Media, Chapter = Chapter, Start = Start, Audio = Audio, Subtitle = Subtitle, Angle = Angle, End = End, Cues = Cues, Keys = Keys, Prohibits = Prohibits, Holds = Holds };
}

/// <summary>Play several items in order (a "play all"), then run <see cref="After"/>.</summary>
public sealed class PlayAllAction : MenuAction
{
    public override string Type => "playAll";

    public List<PlayItem> Items { get; set; } = [];

    public MenuAction? After { get; set; }

    /// <summary>
    /// Level 2: "shuffle" plays every item once in a random order; "random" plays <see cref="Count"/>
    /// items (default: as many as there are) each picked at random. Default "inOrder".
    /// </summary>
    public string? Order { get; set; }

    public int? Count { get; set; }
}

/// <summary>Open a menu, optionally with a button focused and a transition clip first.</summary>
public sealed class GotoAction : MenuAction
{
    public override string Type => "goto";

    public string Menu { get; set; } = "";

    /// <summary>The button to focus: an id, or (Level 2) a variable holding one.</summary>
    public ButtonRef? Button { get; set; }

    /// <summary>Clip played before the menu opens (skippable).</summary>
    public string? Transition { get; set; }

    /// <summary>"replace" = don't add this menu to Back's history (page turns); default "push".</summary>
    public string? History { get; set; }
}

/// <summary>Return to the previous menu.</summary>
public sealed class BackAction : MenuAction
{
    public override string Type => "back";
}

/// <summary>
/// Continue from where the viewer last stopped. When there's nothing to resume, run
/// <see cref="Otherwise"/> instead (a DVD's "if stopped, LinkRSM, else go to the picker").
/// </summary>
public sealed class ResumeAction : MenuAction
{
    public override string Type => "resume";

    /// <summary>Runs as if it were the action itself when there's nothing to resume. Not another resume.</summary>
    public MenuAction? Otherwise { get; set; }
}

/// <summary>Press a button of the current menu. Only allowed in a menu's <c>back</c>.</summary>
public sealed class ActivateAction : MenuAction
{
    public override string Type => "activate";

    public string Button { get; set; } = "";
}

/// <summary>
/// Level 2: change a variable (or writable player state: @audio, @subtitle, @angle, @focus), then
/// optionally run another action.
/// </summary>
public sealed class SetAction : MenuAction
{
    public override string Type => "set";

    public string Var { get; set; } = "";

    public Value Value { get; set; } = 0;

    /// <summary>set (default), add, sub, mul, div, mod, and, or, xor, random.</summary>
    public string? Op { get; set; }

    public MenuAction? Then { get; set; }
}

/// <summary>Level 2: run <see cref="Then"/> or <see cref="Else"/> depending on a condition.</summary>
public sealed class IfAction : MenuAction
{
    public override string Type => "if";

    public Test Test { get; set; } = new Test.Compare("@audio", "eq", 0);

    public MenuAction Then { get; set; } = new BackAction();

    public MenuAction? Else { get; set; }
}

/// <summary>Level 2: run a named action from <see cref="Manifest.Actions"/>, as if it were written here.</summary>
public sealed class RunAction : MenuAction
{
    public override string Type => "run";

    /// <summary>The name of an entry in <see cref="Manifest.Actions"/>.</summary>
    public string Action { get; set; } = "";
}

/// <summary>
/// Level 2: save the resume point (spec §6.5): <see cref="Media"/> from <see cref="Chapter"/> or
/// <see cref="At"/> seconds; without a media, the playback in progress at <see cref="At"/> (default:
/// the cue's time), like a disc's CallSS naming its resume cell.
/// </summary>
public sealed class SaveResumeAction : MenuAction
{
    public override string Type => "saveResume";

    public string? Media { get; set; }

    public double? At { get; set; }

    public int? Chapter { get; set; }
}

/// <summary>
/// Level 2: continue the playback in progress elsewhere on its timeline (spec §6.3): at <see cref="At"/>
/// seconds or the start of <see cref="Chapter"/>. A disc's link to a cell, program or chapter of the
/// title playing. Only during playback.
/// </summary>
public sealed class SeekAction : MenuAction
{
    public override string Type => "seek";

    public double? At { get; set; }

    public int? Chapter { get; set; }
}

/// <summary>
/// Level 2: start the navigation timer (spec §6.7): <see cref="Action"/> runs after <see cref="After"/>
/// seconds, wherever the viewer is (a disc's SetNVTMR). One timer: a new one replaces it.
/// </summary>
public sealed class TimerAction : MenuAction
{
    public override string Type => "timer";

    public double After { get; set; }

    public MenuAction Action { get; set; } = new BackAction();
}

/// <summary>Level 2: stop the navigation timer.</summary>
public sealed class CancelTimerAction : MenuAction
{
    public override string Type => "cancelTimer";
}

/// <summary>
/// Level 2: ask the player to allow parental level <see cref="Level"/> for now (a disc's SetTmpPML):
/// <see cref="Then"/> runs if it does (and @parentalLevel becomes the level), <see cref="Else"/> if not.
/// </summary>
public sealed class RequestParentalAction : MenuAction
{
    public override string Type => "requestParental";

    public int Level { get; set; }

    public MenuAction Then { get; set; } = new BackAction();

    public MenuAction? Else { get; set; }
}

/// <summary>Level 2: stop and leave the title, as the player's own Stop would (a disc's Exit).</summary>
public sealed class ExitAction : MenuAction
{
    public override string Type => "exit";
}

/// <summary>Level 2: forget the resume point.</summary>
public sealed class ClearResumeAction : MenuAction
{
    public override string Type => "clearResume";
}

/// <summary>Level 2: run actions in order, stopping after the first that navigates.</summary>
public sealed class DoAction : MenuAction
{
    public override string Type => "do";

    public List<MenuAction> Steps { get; set; } = [];
}
