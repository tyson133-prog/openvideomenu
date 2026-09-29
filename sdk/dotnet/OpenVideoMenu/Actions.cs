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

    /// <summary>Runs when playback finishes on its own; must not start another playback.</summary>
    public MenuAction? After { get; set; }

    /// <summary>What this plays, as a play item.</summary>
    public PlayItem ToItem() => new() { Media = Media, Chapter = Chapter, Start = Start, Audio = Audio, Subtitle = Subtitle };
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

    public string? Button { get; set; }

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

/// <summary>Level 2: set a variable, then optionally run another action.</summary>
public sealed class SetAction : MenuAction
{
    public override string Type => "set";

    public string Var { get; set; } = "";

    public int Value { get; set; }

    public MenuAction? Then { get; set; }
}
