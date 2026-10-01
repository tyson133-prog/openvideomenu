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

    /// <summary>Runs when playback finishes on its own; must not start another playback.</summary>
    public MenuAction? After { get; set; }

    /// <summary>What this plays, as a play item.</summary>
    public PlayItem ToItem() => new() { Media = Media, Chapter = Chapter, Start = Start, Audio = Audio, Subtitle = Subtitle, Angle = Angle, End = End, Cues = Cues };
}

/// <summary>Play several items in order (a "play all"), then run <see cref="After"/>.</summary>
public sealed class PlayAllAction : MenuAction
{
    public override string Type => "playAll";

    public List<PlayItem> Items { get; set; } = [];

    public MenuAction? After { get; set; }
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
