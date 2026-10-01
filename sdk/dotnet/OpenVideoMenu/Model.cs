using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVideoMenu;

// The manifest as C# types. The normative definition is spec/openvideomenu-v0.1.md and
// spec/schema/manifest.schema.json; this mirrors packages/core/src/types.ts.
//
// Unknown properties are allowed everywhere (compatibility charter rule 3) and vendor extensions
// use keys that start with "x-". Every object keeps the ones it doesn't know in Extensions, so
// reading a manifest and writing it back never loses them.

/// <summary>An Open Video Menu manifest.</summary>
public sealed partial class Manifest
{
    /// <summary>The JSON Schema reference some editors add; optional.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Always <see cref="OpenVideoMenu.Format.Id"/>.</summary>
    public string Format { get; set; } = OpenVideoMenu.Format.Id;

    /// <summary>MAJOR.MINOR of the spec the manifest was written for.</summary>
    public string Version { get; set; } = OpenVideoMenu.Format.Version;

    public Title Title { get; set; } = new();

    public Source? Source { get; set; }

    /// <summary>Everything the menus play, by id: the film, extras, episodes.</summary>
    public Dictionary<string, MediaEntry> Media { get; set; } = [];

    /// <summary>The menus, by id. Absent for a list-only manifest.</summary>
    public Dictionary<string, Menu>? Menus { get; set; }

    /// <summary>The menu shown first (after <see cref="FirstPlay"/>). Required when there are menus.</summary>
    public string? RootMenu { get; set; }

    /// <summary>What runs when the title opens, like a DVD's first-play program.</summary>
    public MenuAction? FirstPlay { get; set; }

    /// <summary>The plain list every player can show. At least one visible group.</summary>
    public List<FallbackGroup> Fallback { get; set; } = [];

    /// <summary>Level 2: the manifest's own variables and their starting values (integer or text; a variable keeps its type).</summary>
    public Dictionary<string, Scalar>? Vars { get; set; }

    /// <summary>
    /// Level 2: named actions, run from anywhere with <see cref="RunAction"/>, so logic many buttons
    /// share (a disc's PGC entry, a dispatcher, an episode's end) is written once (spec §6.4).
    /// </summary>
    public Dictionary<string, MenuAction>? Actions { get; set; }

    /// <summary>
    /// Level 2: how long the resume point lasts (spec §6.5): "untilEnd" (default), until that playback
    /// completes; "untilUsed", like a disc's, until a resume uses it or it's replaced or cleared.
    /// </summary>
    public string? ResumePoint { get; set; }

    /// <summary>Level 2: what the remote's menu keys do, in menus and during playback (spec §6.6).</summary>
    public MenuKeys? Keys { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class Title
{
    public string Name { get; set; } = "";

    public int? Year { get; set; }

    /// <summary>External ids such as imdb, tmdb, tvdb.</summary>
    public Dictionary<string, string>? Ids { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class Source
{
    /// <summary>Where the menu structure came from: "dvd", "bluray", "generated", "authored"…</summary>
    public string Kind { get; set; } = "";

    /// <summary>The tool (and version) that wrote the manifest.</summary>
    public string? Generator { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>A title the menus play.</summary>
public sealed class MediaEntry
{
    /// <summary>The file (or server item). Null when the media is made of <see cref="Segments"/> (Level 2).</summary>
    public MediaRef? Ref { get; set; }

    /// <summary>Level 2: pieces of other media, played as one title with its own timeline (spec §4.2).</summary>
    public List<Segment>? Segments { get; set; }

    public string? Label { get; set; }

    /// <summary>feature, episode, trailer, featurette, behindthescenes, deleted, interview, scene, short, other (or another string).</summary>
    public string? Kind { get; set; }

    /// <summary>Seconds.</summary>
    public double? Duration { get; set; }

    public List<Chapter>? Chapters { get; set; }

    /// <summary>Level 2: the other angles as files; <see cref="Ref"/> is angle 1, Angles[0] angle 2, … (spec §4.1).</summary>
    public List<MediaRef>? Angles { get; set; }

    /// <summary>Level 2: the file's first N video tracks are angles 1..N (spec §4.1).</summary>
    public int? AngleTracks { get; set; }

    /// <summary>
    /// How many angles this media has: 1 when it has none. For media made of segments, see
    /// <see cref="Navigator.AngleCount"/> (its segments' media decide).
    /// </summary>
    [JsonIgnore]
    public int AngleCount => Angles is { Count: > 0 } a ? 1 + a.Count : AngleTracks ?? 1;

    /// <summary>Level 2: cues whenever this media plays (spec §6.3).</summary>
    public List<Cue>? Cues { get; set; }

    /// <summary>Level 2: what the menu keys do while this media plays, over the manifest's (spec §6.6).</summary>
    public MenuKeys? Keys { get; set; }

    /// <summary>Level 2: what the source forbids while this media plays, as information (spec §6.6).</summary>
    public List<string>? Prohibits { get; set; }

    /// <summary>Level 2: frames held whenever this media plays (spec §6.3).</summary>
    public List<Hold>? Holds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>Level 2: a range of a media entry that has a ref; <see cref="End"/> is required on every segment but the last.</summary>
public sealed class Segment
{
    public string Media { get; set; } = "";

    /// <summary>Seconds into the media; default 0.</summary>
    public double? Start { get; set; }

    public double? End { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// Where a media entry is: a path (relative to the manifest, forward slashes) or URL, or an opaque
/// reference the host player resolves (<c>{"scheme": "jellyfin", "id": "…"}</c>).
/// </summary>
[JsonConverter(typeof(MediaRefConverter))]
public sealed record MediaRef
{
    /// <summary>A relative path or URL.</summary>
    public MediaRef(string path) => Path = path;

    /// <summary>A reference the host resolves.</summary>
    public MediaRef(string scheme, string id)
    {
        Scheme = scheme;
        Id = id;
    }

    public string? Path { get; }

    public string? Scheme { get; }

    public string? Id { get; }

    public static implicit operator MediaRef(string path) => new(path);

    public override string ToString() => Path ?? $"{Scheme}:{Id}";
}

public sealed class Chapter
{
    /// <summary>Start, in seconds from the beginning of the media.</summary>
    public double Time { get; set; }

    public string? Label { get; set; }

    public string? Thumbnail { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class Menu
{
    public Canvas Canvas { get; set; } = new();

    public Background? Background { get; set; }

    public string? Label { get; set; }

    /// <summary>The button focused on entry: an id, or (Level 2) a variable holding one.</summary>
    public ButtonRef? DefaultButton { get; set; }

    /// <summary>When true, a missing nav direction moves to the nearest button that way.</summary>
    public bool? AutoNav { get; set; }

    public List<Button> Buttons { get; set; } = [];

    /// <summary>Seconds into the menu's intro before buttons appear and respond.</summary>
    public double? ButtonsAt { get; set; }

    /// <summary>What the remote's Back key does on this menu (like a DVD's "Go Up").</summary>
    public MenuAction? Back { get; set; }

    /// <summary>Level 2: what happens when the menu is left alone (spec §5.2).</summary>
    public MenuTimeout? Timeout { get; set; }

    /// <summary>Level 2: what the menu keys do on this menu, over the manifest's (spec §6.6).</summary>
    public MenuKeys? Keys { get; set; }

    /// <summary>Level 2: what the source forbids on this menu, as information (spec §6.6).</summary>
    public List<string>? Prohibits { get; set; }

    /// <summary>
    /// Level 2: buttons that change while the menu plays (spec §5.3), each set over a window of the
    /// menu's clock; outside every window, <see cref="Buttons"/> apply.
    /// </summary>
    public List<ButtonSet>? ButtonSets { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>Level 2 (spec §5.3): the buttons a menu shows from <see cref="From"/> to <see cref="To"/> seconds on its clock.</summary>
public sealed class ButtonSet
{
    public double From { get; set; }

    /// <summary>Absent: to the end of the menu's loop.</summary>
    public double? To { get; set; }

    public List<Button> Buttons { get; set; } = [];

    public ButtonRef? DefaultButton { get; set; }

    public bool? AutoNav { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// Level 2: what the remote's menu keys do (spec §6.6): Menu (a disc's root menu), Title (its title
/// menu), and the calls a disc can make to its audio, subtitle, angle and chapter menus. A key without
/// an action does the player's default.
/// </summary>
public sealed class MenuKeys
{
    public MenuAction? Menu { get; set; }

    public MenuAction? Title { get; set; }

    public MenuAction? AudioMenu { get; set; }

    public MenuAction? SubtitleMenu { get; set; }

    public MenuAction? AngleMenu { get; set; }

    public MenuAction? ChapterMenu { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>The action for a menu key, if this table has one.</summary>
    public MenuAction? For(Key key) => key switch
    {
        Key.Menu => Menu,
        Key.Title => Title,
        Key.AudioMenu => AudioMenu,
        Key.SubtitleMenu => SubtitleMenu,
        Key.AngleMenu => AngleMenu,
        Key.ChapterMenu => ChapterMenu,
        _ => null,
    };

    /// <summary>Every action in the table.</summary>
    public IEnumerable<MenuAction> All() => new[] { Menu, Title, AudioMenu, SubtitleMenu, AngleMenu, ChapterMenu }.OfType<MenuAction>();
}

/// <summary>A menu that moves on by itself, like a DVD menu whose video or still time runs out.</summary>
public sealed class MenuTimeout
{
    /// <summary>Seconds, from when the menu appears (or from the last key press, with <see cref="RestartOnInput"/>).</summary>
    public double After { get; set; }

    /// <summary>Runs when the time is up, as a step up like a menu's back (no history; a goto to a menu in history unwinds it).</summary>
    public MenuAction Action { get; set; } = new BackAction();

    /// <summary>Whether a key press or click restarts the countdown. Default (null) is true.</summary>
    public bool? RestartOnInput { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class Canvas
{
    /// <summary>Storage width in pixels; button rects use this coordinate space.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Display aspect ("16:9", "4:3" or a number). Default: width/height.</summary>
    public Aspect? Aspect { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>The display aspect as width/height (square pixels when <see cref="Aspect"/> is absent).</summary>
    public double DisplayAspect() => Aspect?.Ratio ?? (double)Width / Height;
}

/// <summary>A display aspect, written as "W:H" or as a number.</summary>
[JsonConverter(typeof(AspectConverter))]
public sealed record Aspect
{
    public Aspect(string text) => Text = text;

    public Aspect(double number) => Number = number;

    public string? Text { get; }

    public double? Number { get; }

    /// <summary>Width/height, or null when the value isn't a valid aspect.</summary>
    public double? Ratio => Number is { } n ? (n > 0 ? n : null) : ParseRatio(Text!);

    public static implicit operator Aspect(string text) => new(text);

    public static implicit operator Aspect(double number) => new(number);

    /// <summary>"16:9", "4:3", "1.85" → width/height; null when it isn't one.</summary>
    public static double? ParseRatio(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(\d+(?:\.\d+)?)\s*[:/]\s*(\d+(?:\.\d+)?)\s*$");
        if (m.Success)
        {
            var r = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) /
                    double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            return r > 0 && double.IsFinite(r) ? r : null;
        }

        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) &&
               text.Trim().Length > 0 && double.IsFinite(n) && n > 0 ? n : null;
    }

    public override string ToString() => Text ?? Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class Background
{
    /// <summary>Still image; with <see cref="Video"/>, the poster shown while loading and by players without motion.</summary>
    public string? Image { get; set; }

    /// <summary>CSS-style colour behind or without the image.</summary>
    public string? Color { get; set; }

    /// <summary>Motion background: plays once from 0, then loops from its loopStart.</summary>
    public LoopingMedia? Video { get; set; }

    /// <summary>Menu audio, looping the same way.</summary>
    public LoopingMedia? Audio { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class LoopingMedia
{
    public string Src { get; set; } = "";

    /// <summary>Seconds; after the end, playback continues from here. Default 0.</summary>
    public double? LoopStart { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>[x, y, width, height] in canvas pixels.</summary>
[JsonConverter(typeof(RectConverter))]
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double CentreX => X + Width / 2;

    public double CentreY => Y + Height / 2;
}

public enum Direction
{
    Up,
    Down,
    Left,
    Right,
}

public sealed class Button
{
    public string Id { get; set; } = "";

    public Rect Rect { get; set; }

    /// <summary>Its name: for accessibility, fallback lists, and drawn when <see cref="ShowLabel"/> is true.</summary>
    public string? Label { get; set; }

    /// <summary>Screen-reader text when it should differ from the label.</summary>
    public string? A11yLabel { get; set; }

    /// <summary>When true the player draws the label; otherwise it's part of the background art.</summary>
    public bool? ShowLabel { get; set; }

    public ButtonStates? States { get; set; }

    public Nav? Nav { get; set; }

    /// <summary>Activate as soon as the button is selected (DVD auto-action buttons).</summary>
    public bool? AutoAction { get; set; }

    public MenuAction Action { get; set; } = new BackAction();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class ButtonStates
{
    public ButtonState? Normal { get; set; }

    public ButtonState? Selected { get; set; }

    public ButtonState? Activated { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class ButtonState
{
    /// <summary>Overlay drawn over the button's rect (or <see cref="Rect"/>). PNG with alpha recommended.</summary>
    public string? Image { get; set; }

    public Rect? Rect { get; set; }

    /// <summary>Label colour when the player draws it.</summary>
    public string? TextColor { get; set; }

    /// <summary>Fill behind a drawn label.</summary>
    public string? Fill { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>Which button each direction moves to.</summary>
public sealed class Nav
{
    public string? Up { get; set; }

    public string? Down { get; set; }

    public string? Left { get; set; }

    public string? Right { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    public string? this[Direction direction] => direction switch
    {
        Direction.Up => Up,
        Direction.Down => Down,
        Direction.Left => Left,
        _ => Right,
    };
}

public sealed class FallbackGroup
{
    public string Label { get; set; } = "";

    /// <summary>Hidden groups (easter eggs) are left out by basic players unless asked for.</summary>
    public bool? Hidden { get; set; }

    public List<FallbackItem> Items { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class FallbackItem
{
    public string Label { get; set; } = "";

    /// <summary>Must play something (play or playAll).</summary>
    public MenuAction Action { get; set; } = new BackAction();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>A number, or the current value of a variable (Level 2).</summary>
/// <summary>
/// Level 2 (spec §6.3): playback stops on the frame at <see cref="At"/> seconds and stays there for
/// <see cref="For"/>: a number of seconds, or until the viewer goes on. A disc's still cell, a photo
/// gallery, a menu made of a title cell.
/// </summary>
public sealed record Hold
{
    public double At { get; set; }

    public HoldTime For { get; set; } = HoldTime.Input;
}

/// <summary>How long a hold lasts: <see cref="Seconds"/>, or (when null) until the viewer goes on ("input").</summary>
[JsonConverter(typeof(HoldTimeConverter))]
public readonly record struct HoldTime(double? Seconds)
{
    /// <summary>Until the viewer goes on.</summary>
    public static HoldTime Input => new(null);

    public bool IsInput => Seconds is null;

    public static implicit operator HoldTime(double seconds) => new(seconds);
}

[JsonConverter(typeof(IntOrVarConverter))]
public readonly record struct IntOrVar
{
    public IntOrVar(int value) => Value = value;

    public IntOrVar(string var) => Var = var;

    public int? Value { get; }

    public string? Var { get; }

    public static implicit operator IntOrVar(int value) => new(value);

    /// <summary>The number, or the variable's value read through <paramref name="read"/> (null when it isn't an integer).</summary>
    public int? Resolve(Func<string, Scalar> read) => Value ?? read(Var!).Number;
}

/// <summary>Something to play: a media entry, from a chapter or a time.</summary>
public sealed class PlayItem
{
    public string Media { get; set; } = "";

    /// <summary>1-based chapter within the media.</summary>
    public int? Chapter { get; set; }

    /// <summary>Start in seconds (ignored when <see cref="Chapter"/> is given).</summary>
    public double? Start { get; set; }

    /// <summary>Level 2: audio track, 1-based among the media's audio tracks.</summary>
    public IntOrVar? Audio { get; set; }

    /// <summary>Level 2: subtitle track, 1-based; 0 = off.</summary>
    public IntOrVar? Subtitle { get; set; }

    /// <summary>Level 2: angle of media with angles, 1-based; default @angle.</summary>
    public IntOrVar? Angle { get; set; }

    /// <summary>Level 2: stop here (seconds), as if the item ended (spec §6.3).</summary>
    public double? End { get; set; }

    /// <summary>Level 2: cues for this way of playing the media, beside the media's own (spec §6.3).</summary>
    public List<Cue>? Cues { get; set; }

    /// <summary>Level 2: what the menu keys do while this item plays, over the media's and the manifest's (spec §6.6).</summary>
    public MenuKeys? Keys { get; set; }

    /// <summary>Level 2: what the source forbids while this item plays, beside the media's (spec §6.6).</summary>
    public List<string>? Prohibits { get; set; }

    /// <summary>Level 2: holds for this way of playing the media, beside the media's own (spec §6.3).</summary>
    public List<Hold>? Holds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
