using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenVideoMenu;

/// <summary>
/// Checks manifest JSON against spec 0.1: structure first, then cross-references. A line-for-line
/// port of packages/core/src/validate.ts, run on the raw JSON so invalid documents get the same
/// errors (same paths) as the reference validator. Both are checked against conformance/cases.
/// </summary>
internal sealed class Validator
{
    private static readonly Dictionary<string, string[]> KnownKeys = new()
    {
        ["manifest"] = ["format", "version", "title", "source", "media", "menus", "rootMenu", "firstPlay", "fallback", "vars", "$schema"],
        ["title"] = ["name", "year", "ids"],
        ["source"] = ["kind", "generator"],
        ["media"] = ["ref", "label", "kind", "duration", "chapters", "angles", "angleTracks", "cues"],
        ["chapter"] = ["time", "label", "thumbnail"],
        ["menu"] = ["canvas", "background", "label", "defaultButton", "autoNav", "buttons", "buttonsAt", "back", "timeout"],
        ["timeout"] = ["after", "action", "restartOnInput"],
        ["cue"] = ["at", "action"],
        ["overlay"] = ["from", "to", "canvas", "buttons", "defaultButton", "autoNav"],
        ["canvas"] = ["width", "height", "aspect"],
        ["background"] = ["image", "color", "video", "audio"],
        ["loopingMedia"] = ["src", "loopStart"],
        ["button"] = ["id", "rect", "label", "a11yLabel", "showLabel", "states", "nav", "autoAction", "action"],
        ["state"] = ["image", "rect", "textColor", "fill"],
        ["fallbackGroup"] = ["label", "hidden", "items"],
        ["fallbackItem"] = ["label", "action"],
    };

    // Insertion order matters: it's the order "Expected …" lists the types in.
    private static readonly (string Type, string[] Keys)[] ActionKeys =
    [
        ("play", ["type", "media", "chapter", "start", "end", "after", "audio", "subtitle", "angle", "cues"]),
        ("playAll", ["type", "items", "after"]),
        ("set", ["type", "var", "value", "op", "then"]),
        ("if", ["type", "test", "then", "else"]),
        ("do", ["type", "steps"]),
        ("goto", ["type", "menu", "button", "transition", "history"]),
        ("back", ["type"]),
        ("resume", ["type", "otherwise"]),
        ("activate", ["type", "button"]),
    ];

    private static readonly string[] Directions = ["up", "down", "left", "right"];
    private static readonly string[] StateNames = ["normal", "selected", "activated"];

    private readonly List<Issue> _errors = [];
    private readonly List<Issue> _warnings = [];
    /// <summary>Declared variables and their types (true = text); a variable keeps the type of its starting value.</summary>
    private readonly Dictionary<string, bool> _varTypes = new(StringComparer.Ordinal);

    /// <summary>Above 0 while checking cues: playback started there returns to the interrupted one.</summary>
    private int _cueDepth;

    /// <summary>Media id → how many angles it has (1 without angles).</summary>
    private readonly Dictionary<string, int> _angleCounts = new(StringComparer.Ordinal);

    public static (List<Issue> Errors, List<Issue> Warnings) Run(JsonElement data)
    {
        var v = new Validator();
        v.Manifest(data);
        return (v._errors, v._warnings);
    }

    private void Error(string path, string message) => _errors.Add(new Issue(path, message));

    private void Warn(string path, string message) => _warnings.Add(new Issue(path, message));

    /// <summary>Unknown non-extension keys are allowed (charter rule 3) but usually a typo, so warn.</summary>
    private void UnknownKeys(Obj obj, IReadOnlyCollection<string> known, string path)
    {
        foreach (var key in obj.Keys)
        {
            if (!known.Contains(key) && !key.StartsWith("x-", StringComparison.Ordinal))
            {
                Warn($"{path}/{key}", $"Unknown property \"{key}\" (ignored). Vendor extensions must start with \"x-\".");
            }
        }
    }

    private void Manifest(JsonElement data)
    {
        if (!Obj.TryFrom(data, out var m))
        {
            Error("", "Manifest must be a JSON object.");
            return;
        }

        UnknownKeys(m, KnownKeys["manifest"], "");
        if (!(m["format"] is { ValueKind: JsonValueKind.String } f && f.GetString() == Format.Id))
        {
            Error("/format", $"Must be \"{Format.Id}\".");
        }

        Version(m["version"]);
        Title(m["title"]);
        if (m["source"] is { } source)
        {
            Source(source);
        }

        Vars(m["vars"]);
        var mediaIds = Media(m["media"]);
        var menus = Menus(m["menus"], mediaIds);

        // Media cues run actions, so they're checked once every menu is known.
        if (Obj.TryFrom(m["media"], out var allMedia))
        {
            foreach (var (id, entry) in allMedia.Entries)
            {
                if (Obj.TryFrom(entry, out var me) && me["cues"] is { } mediaCues)
                {
                    Cues(mediaCues, $"/media/{id}/cues", mediaIds, menus);
                }
            }
        }

        if (m["rootMenu"] is { } root)
        {
            if (!IsNonEmptyString(root))
            {
                Error("/rootMenu", "Must be a menu id.");
            }
            else if (!menus.ContainsKey(root.GetString()!))
            {
                Error("/rootMenu", $"No menu \"{root.GetString()}\".");
            }
        }
        else if (menus.Count > 0)
        {
            Error("/rootMenu", "Required when the manifest has menus.");
        }

        if (m["firstPlay"] is { } firstPlay)
        {
            Action(firstPlay, "/firstPlay", mediaIds, menus);
        }

        Fallback(m["fallback"], mediaIds, menus);
    }

    private void Vars(JsonElement? value)
    {
        if (value is null)
        {
            return;
        }

        if (!Obj.TryFrom(value.Value, out var vars))
        {
            Error("/vars", "Must be an object of name → integer or text.");
            return;
        }

        foreach (var (name, v) in vars.Entries)
        {
            if (name.StartsWith('@'))
            {
                Error($"/vars/{name}", "Names starting with \"@\" are player state, not the manifest's own variables.");
            }
            else if (IsInteger(v))
            {
                _varTypes[name] = false;
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                _varTypes[name] = true;
            }
            else
            {
                Error($"/vars/{name}", "Must be an integer or text.");
            }
        }
    }

    /// <summary>The type (true = text) of a variable the manifest may read, or null when there's none.</summary>
    private bool? TypeOfVar(JsonElement? name)
    {
        if (name is not { ValueKind: JsonValueKind.String } n)
        {
            return null;
        }

        var s = n.GetString()!;
        if (s.StartsWith('@'))
        {
            return PlayerState.Variables.TryGetValue(s, out var p) ? p.IsText : null;
        }

        return _varTypes.TryGetValue(s, out var t) ? t : null;
    }

    private bool? VarRef(Obj value, string path)
    {
        var t = TypeOfVar(value["var"]);
        if (t is null)
        {
            Error($"{path}/var", $"No variable \"{JsString(value["var"])}\" in /vars or player state.");
        }

        return t;
    }

    /// <summary>A literal value (integer or text) or {var}; returns its type (true = text) when known.</summary>
    private bool? ValueType(JsonElement? value, string path)
    {
        if (Obj.TryFrom(value, out var o))
        {
            return VarRef(o, path);
        }

        if (value is { } v && IsInteger(v))
        {
            return false;
        }

        if (value is { ValueKind: JsonValueKind.String })
        {
            return true;
        }

        Error(path, "Must be an integer, text, or {\"var\": name}.");
        return null;
    }

    private static string Kind(bool text) => text ? "text" : "an integer";

    private void VarOrNumber(JsonElement value, string path, int min)
    {
        if (Obj.TryFrom(value, out var o))
        {
            if (VarRef(o, path) == true)
            {
                Error($"{path}/var", $"Variable \"{JsString(o["var"])}\" holds text; a track number is needed.");
            }
        }
        else if (!IsInteger(value) || value.GetDouble() < min)
        {
            Error(path, $"Must be an integer ≥ {min}, or {{\"var\": name}}.");
        }
    }

    /// <summary>A condition: one comparison of a variable, or all/any/not of other conditions.</summary>
    private void Test(JsonElement? value, string path)
    {
        if (!Obj.TryFrom(value, out var t))
        {
            Error(path, "Must be a condition: {\"var\", \"eq\"|\"ne\"|\"lt\"|\"le\"|\"gt\"|\"ge\"|\"has\": value}, or all/any/not.");
            return;
        }

        foreach (var key in new[] { "all", "any" })
        {
            if (t[key] is { } list)
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                {
                    Error($"{path}/{key}", "Must be a list of at least one condition.");
                }
                else
                {
                    var i = 0;
                    foreach (var item in list.EnumerateArray())
                    {
                        Test(item, $"{path}/{key}/{i++}");
                    }
                }

                return;
            }
        }

        if (t["not"] is { } not)
        {
            Test(not, $"{path}/not");
            return;
        }

        var ops = PlayerState.TestOps.Where(op => t[op] is not null).ToList();
        UnknownKeys(t, ["var", .. PlayerState.TestOps], path);
        var left = VarRef(t, path);
        if (ops.Count != 1)
        {
            Error(path, "Needs exactly one comparison: eq, ne, lt, le, gt, ge or has.");
            return;
        }

        var op = ops[0];
        var right = ValueType(t[op], $"{path}/{op}");
        if (op is not ("eq" or "ne") && (left == true || right == true))
        {
            Error($"{path}/{op}", $"\"{op}\" compares integers; text can only be compared with eq or ne.");
        }
        else if (left is { } l && right is { } r && l != r)
        {
            Error($"{path}/{op}", $"Compares {Kind(l)} with {Kind(r)}.");
        }
    }

    /// <summary>A button reference: an id of the menu, or a variable holding one.</summary>
    private void ButtonRef(JsonElement value, string path, HashSet<string> buttons, string where)
    {
        if (Obj.TryFrom(value, out var o))
        {
            if (VarRef(o, path) == false)
            {
                Error($"{path}/var", $"Variable \"{JsString(o["var"])}\" holds an integer; a button id is text.");
            }
        }
        else if (!(IsNonEmptyString(value) && buttons.Contains(value.GetString()!)))
        {
            Error(path, $"No button \"{JsString(value)}\" in {where}.");
        }
    }

    private void Version(JsonElement? value)
    {
        if (!IsNonEmptyString(value) || !Regex.IsMatch(value!.Value.GetString()!, @"^\d+\.\d+$"))
        {
            Error("/version", "Must be \"MAJOR.MINOR\", e.g. \"0.1\".");
            return;
        }

        var text = value.Value.GetString()!;
        var (major, minor) = SplitVersion(text);
        var (ourMajor, ourMinor) = SplitVersion(Format.Version);
        if (major != ourMajor)
        {
            Error("/version", $"Major version {Num(major)} is not supported by this reader (supports {Num(ourMajor)}.x).");
        }
        else if (minor > ourMinor)
        {
            Warn("/version", $"Written for {text}; this reader knows {Format.Version}. Newer fields will be ignored.");
        }
    }

    private static (double Major, double Minor) SplitVersion(string v)
    {
        var parts = v.Split('.');
        return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private void Title(JsonElement? value)
    {
        if (!Obj.TryFrom(value, out var t))
        {
            Error("/title", "Required object with at least a name.");
            return;
        }

        UnknownKeys(t, KnownKeys["title"], "/title");
        if (!IsNonEmptyString(t["name"]))
        {
            Error("/title/name", "Required.");
        }

        if (t["year"] is { } year && !IsInteger(year))
        {
            Error("/title/year", "Must be an integer.");
        }

        if (t["ids"] is { } ids)
        {
            if (!Obj.TryFrom(ids, out var idObj))
            {
                Error("/title/ids", "Must be an object of strings.");
            }
            else
            {
                foreach (var (k, id) in idObj.Entries)
                {
                    if (id.ValueKind != JsonValueKind.String)
                    {
                        Error($"/title/ids/{k}", "Must be a string.");
                    }
                }
            }
        }
    }

    private void Source(JsonElement value)
    {
        if (!Obj.TryFrom(value, out var s))
        {
            Error("/source", "Must be an object.");
            return;
        }

        UnknownKeys(s, KnownKeys["source"], "/source");
        if (!IsNonEmptyString(s["kind"]))
        {
            Error("/source/kind", "Required.");
        }

        if (s["generator"] is { } g && g.ValueKind != JsonValueKind.String)
        {
            Error("/source/generator", "Must be a string.");
        }
    }

    /// <summary>Returns media id → chapter count.</summary>
    private Dictionary<string, int> Media(JsonElement? value)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Obj.TryFrom(value, out var media) || media.Count == 0)
        {
            Error("/media", "Required object with at least one media entry.");
            return ids;
        }

        foreach (var (id, entry) in media.Entries)
        {
            var path = $"/media/{id}";
            if (!Obj.TryFrom(entry, out var m))
            {
                Error(path, "Must be an object.");
                continue;
            }

            UnknownKeys(m, KnownKeys["media"], path);
            MediaRef(m["ref"], $"{path}/ref");
            var angleCount = 1;
            if (m["angles"] is { } angles)
            {
                if (angles.ValueKind != JsonValueKind.Array || angles.GetArrayLength() == 0)
                {
                    Error($"{path}/angles", "Must be a list of at least one media ref.");
                }
                else
                {
                    var i = 0;
                    foreach (var a in angles.EnumerateArray())
                    {
                        MediaRef(a, $"{path}/angles/{i++}");
                    }

                    angleCount = 1 + angles.GetArrayLength();
                }
            }

            if (m["angleTracks"] is { } angleTracks)
            {
                if (m["angles"] is not null)
                {
                    Error($"{path}/angleTracks", "Give angles as files (angles) or as video tracks (angleTracks), not both.");
                }
                else if (!IsInteger(angleTracks) || angleTracks.GetDouble() < 2)
                {
                    Error($"{path}/angleTracks", "Must be an integer ≥ 2.");
                }
                else
                {
                    angleCount = (int)angleTracks.GetDouble();
                }
            }

            _angleCounts[id] = angleCount;

            if (m["label"] is { } label && label.ValueKind != JsonValueKind.String)
            {
                Error($"{path}/label", "Must be a string.");
            }

            if (m["kind"] is { } kind && kind.ValueKind != JsonValueKind.String)
            {
                Error($"{path}/kind", "Must be a string.");
            }

            if (m["duration"] is { } duration && !(IsFiniteNumber(duration) && duration.GetDouble() >= 0))
            {
                Error($"{path}/duration", "Must be a non-negative number of seconds.");
            }

            var chapterCount = 0;
            if (m["chapters"] is { } chapters)
            {
                if (chapters.ValueKind != JsonValueKind.Array)
                {
                    Error($"{path}/chapters", "Must be an array.");
                }
                else
                {
                    chapterCount = chapters.GetArrayLength();
                    var previous = -1.0;
                    var i = 0;
                    foreach (var c in chapters.EnumerateArray())
                    {
                        var cp = $"{path}/chapters/{i++}";
                        if (!Obj.TryFrom(c, out var ch))
                        {
                            Error(cp, "Must be an object.");
                            continue;
                        }

                        UnknownKeys(ch, KnownKeys["chapter"], cp);
                        if (ch["time"] is not { } time || !IsFiniteNumber(time) || time.GetDouble() < 0)
                        {
                            Error($"{cp}/time", "Must be a non-negative number.");
                        }
                        else
                        {
                            if (time.GetDouble() <= previous)
                            {
                                Error($"{cp}/time", "Chapter times must increase.");
                            }

                            previous = time.GetDouble();
                        }

                        if (ch["label"] is { } cl && cl.ValueKind != JsonValueKind.String)
                        {
                            Error($"{cp}/label", "Must be a string.");
                        }

                        if (ch["thumbnail"] is { } thumb && !IsNonEmptyString(thumb))
                        {
                            Error($"{cp}/thumbnail", "Must be a path or URL.");
                        }
                    }
                }
            }

            ids[id] = chapterCount;
        }

        return ids;
    }

    /// <summary>Returns menu id → its button ids.</summary>
    private Dictionary<string, HashSet<string>> Menus(JsonElement? value, Dictionary<string, int> mediaIds)
    {
        var menus = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (value is null)
        {
            return menus;
        }

        if (!Obj.TryFrom(value, out var all))
        {
            Error("/menus", "Must be an object of menus.");
            return menus;
        }

        // First pass: collect button ids so goto targets can be checked in any order.
        foreach (var (id, m) in all.Entries)
        {
            var buttons = new HashSet<string>(StringComparer.Ordinal);
            if (Obj.TryFrom(m, out var mo) && mo["buttons"] is { ValueKind: JsonValueKind.Array } bs)
            {
                foreach (var b in bs.EnumerateArray())
                {
                    if (Obj.TryFrom(b, out var bo) && IsNonEmptyString(bo["id"]))
                    {
                        buttons.Add(bo["id"]!.Value.GetString()!);
                    }
                }
            }

            menus[id] = buttons;
        }

        foreach (var (id, m) in all.Entries)
        {
            Menu(m, $"/menus/{id}", mediaIds, menus);
        }

        return menus;
    }

    private void Menu(JsonElement value, string path, Dictionary<string, int> mediaIds, Dictionary<string, HashSet<string>> menus)
    {
        if (!Obj.TryFrom(value, out var menu))
        {
            Error(path, "Must be an object.");
            return;
        }

        UnknownKeys(menu, KnownKeys["menu"], path);
        var canvas = Canvas(menu["canvas"], $"{path}/canvas");

        if (menu["background"] is { } bgValue)
        {
            if (!Obj.TryFrom(bgValue, out var bg))
            {
                Error($"{path}/background", "Must be an object.");
            }
            else
            {
                UnknownKeys(bg, KnownKeys["background"], $"{path}/background");
                if (bg["image"] is { } image && !IsNonEmptyString(image))
                {
                    Error($"{path}/background/image", "Must be a path or URL.");
                }

                if (bg["color"] is { } color && color.ValueKind != JsonValueKind.String)
                {
                    Error($"{path}/background/color", "Must be a colour string.");
                }

                foreach (var key in new[] { "video", "audio" })
                {
                    if (bg[key] is { } looping)
                    {
                        LoopingMedia(looping, $"{path}/background/{key}");
                    }
                }
            }
        }

        if (menu["autoNav"] is { } autoNav && !IsBoolean(autoNav))
        {
            Error($"{path}/autoNav", "Must be true or false.");
        }

        if (menu["buttonsAt"] is { } buttonsAt && !(IsFiniteNumber(buttonsAt) && buttonsAt.GetDouble() >= 0))
        {
            Error($"{path}/buttonsAt", "Must be a non-negative number of seconds.");
        }

        if (Buttons(menu, path, canvas, mediaIds, menus, "this menu") is not { } own)
        {
            return;
        }

        if (menu["defaultButton"] is { } defaultButton)
        {
            ButtonRef(defaultButton, $"{path}/defaultButton", own, "this menu");
        }

        // What the remote's Back does here; may press one of this menu's own buttons.
        if (menu["back"] is { } back)
        {
            Action(back, $"{path}/back", mediaIds, menus, own);
        }

        if (menu["timeout"] is { } timeoutValue)
        {
            var tp = $"{path}/timeout";
            if (!Obj.TryFrom(timeoutValue, out var t))
            {
                Error(tp, "Must be {after, action, restartOnInput?}.");
            }
            else
            {
                UnknownKeys(t, KnownKeys["timeout"], tp);
                if (t["after"] is not { } after || !(IsFiniteNumber(after) && after.GetDouble() > 0))
                {
                    Error($"{tp}/after", "Must be a positive number of seconds.");
                }

                if (t["restartOnInput"] is { } restart && !IsBoolean(restart))
                {
                    Error($"{tp}/restartOnInput", "Must be true or false.");
                }

                // Like back, it may press one of this menu's own buttons.
                Action(t["action"], $"{tp}/action", mediaIds, menus, own);
            }
        }
    }

    private (double Width, double Height)? Canvas(JsonElement? value, string path)
    {
        if (!Obj.TryFrom(value, out var c))
        {
            Error(path, "Required: {width, height, aspect?}.");
            return null;
        }

        UnknownKeys(c, KnownKeys["canvas"], path);
        var okW = c["width"] is { } w && IsInteger(w) && w.GetDouble() > 0;
        var okH = c["height"] is { } h && IsInteger(h) && h.GetDouble() > 0;
        if (!okW)
        {
            Error($"{path}/width", "Must be a positive integer.");
        }

        if (!okH)
        {
            Error($"{path}/height", "Must be a positive integer.");
        }

        if (c["aspect"] is { } aspect && ParseAspect(aspect) is null)
        {
            Error($"{path}/aspect", "Must be \"W:H\" (e.g. \"16:9\") or a positive number.");
        }

        return okW && okH ? (c["width"]!.Value.GetDouble(), c["height"]!.Value.GetDouble()) : null;
    }

    /// <summary>The buttons of a menu or overlay; returns their ids, or null when there are none.</summary>
    private HashSet<string>? Buttons(
        Obj menu,
        string path,
        (double Width, double Height)? canvas,
        Dictionary<string, int> mediaIds,
        Dictionary<string, HashSet<string>> menus,
        string where)
    {
        if (menu["buttons"] is not { ValueKind: JsonValueKind.Array } buttons || buttons.GetArrayLength() == 0)
        {
            Error($"{path}/buttons", "Required: at least one button.");
            return null;
        }

        var own = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var bValue in buttons.EnumerateArray())
        {
            var bp = $"{path}/buttons/{i++}";
            if (!Obj.TryFrom(bValue, out var b))
            {
                Error(bp, "Must be an object.");
                continue;
            }

            UnknownKeys(b, KnownKeys["button"], bp);
            if (!IsNonEmptyString(b["id"]))
            {
                Error($"{bp}/id", "Required.");
            }
            else if (!own.Add(b["id"]!.Value.GetString()!))
            {
                Error($"{bp}/id", $"Duplicate button id \"{b["id"]!.Value.GetString()}\".");
            }

            Rect(b["rect"], $"{bp}/rect", canvas);
            foreach (var key in new[] { "label", "a11yLabel" })
            {
                if (b[key] is { } s && s.ValueKind != JsonValueKind.String)
                {
                    Error($"{bp}/{key}", "Must be a string.");
                }
            }

            foreach (var key in new[] { "showLabel", "autoAction" })
            {
                if (b[key] is { } flag && !IsBoolean(flag))
                {
                    Error($"{bp}/{key}", "Must be true or false.");
                }
            }

            if (b["showLabel"] is { ValueKind: JsonValueKind.True } && !IsNonEmptyString(b["label"]))
            {
                Error($"{bp}/label", "Required when showLabel is true.");
            }

            if (b["states"] is { } statesValue)
            {
                if (!Obj.TryFrom(statesValue, out var states))
                {
                    Error($"{bp}/states", "Must be an object.");
                }
                else
                {
                    foreach (var (name, s) in states.Entries)
                    {
                        var sp = $"{bp}/states/{name}";
                        if (!StateNames.Contains(name))
                        {
                            if (!name.StartsWith("x-", StringComparison.Ordinal))
                            {
                                Warn(sp, $"Unknown state \"{name}\" (ignored).");
                            }

                            continue;
                        }

                        if (!Obj.TryFrom(s, out var state))
                        {
                            Error(sp, "Must be an object.");
                            continue;
                        }

                        UnknownKeys(state, KnownKeys["state"], sp);
                        if (state["image"] is { } image && !IsNonEmptyString(image))
                        {
                            Error($"{sp}/image", "Must be a path or URL.");
                        }

                        if (state["rect"] is { } rect)
                        {
                            Rect(rect, $"{sp}/rect", canvas);
                        }

                        foreach (var key in new[] { "textColor", "fill" })
                        {
                            if (state[key] is { } colour && colour.ValueKind != JsonValueKind.String)
                            {
                                Error($"{sp}/{key}", "Must be a colour string.");
                            }
                        }
                    }
                }
            }

            Action(b["action"], $"{bp}/action", mediaIds, menus);
        }

        // nav and defaultButton can only be checked once every id in this menu is known.
        i = 0;
        foreach (var bValue in buttons.EnumerateArray())
        {
            var np = $"{path}/buttons/{i++}/nav";
            if (!Obj.TryFrom(bValue, out var b) || b["nav"] is not { } navValue)
            {
                continue;
            }

            if (!Obj.TryFrom(navValue, out var nav))
            {
                Error(np, "Must be an object of direction → button id.");
                continue;
            }

            foreach (var (dir, target) in nav.Entries)
            {
                if (!Directions.Contains(dir))
                {
                    if (!dir.StartsWith("x-", StringComparison.Ordinal))
                    {
                        Warn($"{np}/{dir}", $"Unknown direction \"{dir}\" (ignored).");
                    }
                }
                else if (!IsNonEmptyString(target) || !own.Contains(target.GetString()!))
                {
                    Error($"{np}/{dir}", $"No button \"{JsString(target)}\" in {where}.");
                }
            }
        }

        return own;
    }

    /// <summary>
    /// Level 2: cues during playback (spec §6.3). Point cues {at, action}; overlays {from, to, canvas,
    /// buttons}, which must not overlap each other within one list.
    /// </summary>
    private void Cues(JsonElement value, string path, Dictionary<string, int> mediaIds, Dictionary<string, HashSet<string>> menus)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            Error(path, "Must be a list of cues.");
            return;
        }

        var windows = new List<(double From, double To, int I)>();
        _cueDepth++;
        var i = 0;
        foreach (var cueValue in value.EnumerateArray())
        {
            var cp = $"{path}/{i}";
            if (!Obj.TryFrom(cueValue, out var cue))
            {
                Error(cp, "Must be a point cue {at, action} or an overlay {from, to, canvas, buttons}.");
            }
            else if (cue["at"] is { } at)
            {
                UnknownKeys(cue, KnownKeys["cue"], cp);
                if (!(IsFiniteNumber(at) && at.GetDouble() >= 0))
                {
                    Error($"{cp}/at", "Must be a non-negative number of seconds.");
                }

                Action(cue["action"], $"{cp}/action", mediaIds, menus);
            }
            else if (cue["from"] is not null || cue["to"] is not null)
            {
                UnknownKeys(cue, KnownKeys["overlay"], cp);
                var okFrom = cue["from"] is { } f && IsFiniteNumber(f) && f.GetDouble() >= 0;
                if (!okFrom)
                {
                    Error($"{cp}/from", "Must be a non-negative number of seconds.");
                }

                var okTo = okFrom && cue["to"] is { } t && IsFiniteNumber(t) && t.GetDouble() > cue["from"]!.Value.GetDouble();
                if (!okTo)
                {
                    Error($"{cp}/to", "Must be a number of seconds after from.");
                }
                else
                {
                    var from = cue["from"]!.Value.GetDouble();
                    var to = cue["to"]!.Value.GetDouble();
                    var other = windows.FirstOrDefault(w => from < w.To && w.From < to, (0, 0, -1));
                    if (other.I >= 0)
                    {
                        Error($"{cp}/from", $"Overlaps overlay {other.I}; one overlay shows at a time.");
                    }

                    windows.Add((from, to, i));
                }

                var canvas = Canvas(cue["canvas"], $"{cp}/canvas");
                if (cue["autoNav"] is { } autoNav && !IsBoolean(autoNav))
                {
                    Error($"{cp}/autoNav", "Must be true or false.");
                }

                var own = Buttons(cue, cp, canvas, mediaIds, menus, "this overlay");
                if (own is not null && cue["defaultButton"] is { } defaultButton)
                {
                    ButtonRef(defaultButton, $"{cp}/defaultButton", own, "this overlay");
                }
            }
            else
            {
                Error(cp, "Must be a point cue {at, action} or an overlay {from, to, canvas, buttons}.");
            }

            i++;
        }

        _cueDepth--;
    }

    private void LoopingMedia(JsonElement value, string path)
    {
        if (!Obj.TryFrom(value, out var m))
        {
            Error(path, "Must be {src, loopStart?}.");
            return;
        }

        UnknownKeys(m, KnownKeys["loopingMedia"], path);
        if (!IsNonEmptyString(m["src"]))
        {
            Error($"{path}/src", "Required: a path or URL.");
        }

        if (m["loopStart"] is { } loopStart && !(IsFiniteNumber(loopStart) && loopStart.GetDouble() >= 0))
        {
            Error($"{path}/loopStart", "Must be a non-negative number of seconds.");
        }
    }

    private void Rect(JsonElement? value, string path, (double Width, double Height)? canvas)
    {
        if (value is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() != 4 || !arr.EnumerateArray().All(IsFiniteNumber))
        {
            Error(path, "Must be [x, y, width, height].");
            return;
        }

        var n = arr.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var (x, y, w, h) = (n[0], n[1], n[2], n[3]);
        if (w < 0 || h < 0)
        {
            Error(path, "Width and height must not be negative.");
        }
        else if (canvas is { } cv && (x < 0 || y < 0 || x + w > cv.Width || y + h > cv.Height))
        {
            Warn(path, "Extends outside the canvas.");
        }
    }

    private void PlayItem(Obj value, string path, Dictionary<string, int> mediaIds, Dictionary<string, HashSet<string>> menus)
    {
        var media = value["media"];
        if (!IsNonEmptyString(media) || !mediaIds.ContainsKey(media!.Value.GetString()!))
        {
            Error($"{path}/media", $"No media \"{JsString(media)}\".");
            return;
        }

        var mediaId = media.Value.GetString()!;
        if (value["chapter"] is { } chapter)
        {
            var count = mediaIds[mediaId];
            if (!IsInteger(chapter) || chapter.GetDouble() < 1 || chapter.GetDouble() > count)
            {
                Error($"{path}/chapter", $"Media \"{mediaId}\" has {count} chapter(s); chapter numbers start at 1.");
            }
        }

        if (value["start"] is { } start && !(IsFiniteNumber(start) && start.GetDouble() >= 0))
        {
            Error($"{path}/start", "Must be a non-negative number of seconds.");
        }

        if (value["end"] is { } end)
        {
            if (!(IsFiniteNumber(end) && end.GetDouble() > 0))
            {
                Error($"{path}/end", "Must be a positive number of seconds.");
            }
            else if (value["start"] is { } s0 && IsFiniteNumber(s0) && end.GetDouble() <= s0.GetDouble())
            {
                Error($"{path}/end", "Must be after start.");
            }
        }

        if (value["audio"] is { } audio)
        {
            VarOrNumber(audio, $"{path}/audio", 1);
        }

        if (value["subtitle"] is { } subtitle)
        {
            VarOrNumber(subtitle, $"{path}/subtitle", 0);
        }

        if (value["angle"] is { } angle)
        {
            VarOrNumber(angle, $"{path}/angle", 1);
            var count = _angleCounts.GetValueOrDefault(mediaId, 1);
            if (IsInteger(angle) && angle.GetDouble() > count)
            {
                Error($"{path}/angle", $"Media \"{mediaId}\" has {count} angle(s).");
            }
        }

        if (value["cues"] is { } cues)
        {
            Cues(cues, $"{path}/cues", mediaIds, menus);
        }
    }

    private void MediaRef(JsonElement? reference, string path)
    {
        if (Obj.TryFrom(reference, out var r))
        {
            if (!IsNonEmptyString(r["scheme"]) || !IsNonEmptyString(r["id"]))
            {
                Error(path, "Object refs need non-empty scheme and id.");
            }
        }
        else if (!IsNonEmptyString(reference))
        {
            Error(path, "Required: a relative path, URL, or {scheme, id}.");
        }
    }

    /// <summary><paramref name="ownButtons"/>: set when checking a menu's back, the only place activate may appear.</summary>
    private void Action(JsonElement? value, string path, Dictionary<string, int> mediaIds, Dictionary<string, HashSet<string>> menus, HashSet<string>? ownButtons = null)
    {
        if (!Obj.TryFrom(value, out var action))
        {
            Error(path, "Required action object with a type.");
            return;
        }

        var typeEl = action["type"];
        var type = typeEl is { ValueKind: JsonValueKind.String } t ? t.GetString()! : null;
        var keys = type is null ? null : ActionKeys.FirstOrDefault(a => a.Type == type).Keys;
        if (keys is null)
        {
            Error($"{path}/type", $"Unknown action type \"{JsString(typeEl)}\". Expected {string.Join(", ", ActionKeys.Select(a => a.Type))}.");
            return;
        }

        UnknownKeys(action, keys, path);
        switch (type)
        {
            case "play":
                PlayItem(action, path, mediaIds, menus);
                break;
            case "playAll":
                if (action["items"] is not { ValueKind: JsonValueKind.Array } items || items.GetArrayLength() == 0)
                {
                    Error($"{path}/items", "Required: at least one item.");
                }
                else
                {
                    var i = 0;
                    foreach (var item in items.EnumerateArray())
                    {
                        var ip = $"{path}/items/{i++}";
                        if (!Obj.TryFrom(item, out var io))
                        {
                            Error(ip, "Must be {media, chapter?, start?}.");
                        }
                        else
                        {
                            PlayItem(io, ip, mediaIds, menus);
                        }
                    }
                }

                break;
            case "set":
            {
                var name = action["var"];
                var target = TypeOfVar(name);
                if (target is null)
                {
                    Error($"{path}/var", $"No variable \"{JsString(name)}\" in /vars or player state.");
                }
                else if (name!.Value.GetString()!.StartsWith('@') && !PlayerState.Variables[name.Value.GetString()!].Writable)
                {
                    Error($"{path}/var", $"\"{name.Value.GetString()}\" is read-only player state.");
                }

                // "op": null counts as absent, as in the reference (JavaScript's ??).
                var opEl = action["op"] is { ValueKind: not JsonValueKind.Null } present ? present : (JsonElement?)null;
                var op = opEl is null ? "set" : JsString(opEl);
                var opKnown = opEl is null || (opEl.Value.ValueKind == JsonValueKind.String && PlayerState.SetOps.Contains(op));
                if (!opKnown)
                {
                    Error($"{path}/op", $"Must be one of {string.Join(", ", PlayerState.SetOps)}.");
                }

                var given = ValueType(action["value"], $"{path}/value");
                if (opKnown && op == "set")
                {
                    if (target is { } tt && given is { } gg && tt != gg)
                    {
                        Error($"{path}/value", $"\"{JsString(name)}\" holds {Kind(tt)}.");
                    }
                }
                else if (target == true || given == true)
                {
                    Error($"{path}/op", $"\"{op}\" works on integers only.");
                }

                if (action["then"] is { } then)
                {
                    Action(then, $"{path}/then", mediaIds, menus, ownButtons);
                }

                break;
            }

            case "if":
                Test(action["test"], $"{path}/test");
                Action(action["then"], $"{path}/then", mediaIds, menus, ownButtons);
                if (action["else"] is { } elseAction)
                {
                    Action(elseAction, $"{path}/else", mediaIds, menus, ownButtons);
                }

                break;
            case "do":
                if (action["steps"] is not { ValueKind: JsonValueKind.Array } steps || steps.GetArrayLength() == 0)
                {
                    Error($"{path}/steps", "Required: at least one action.");
                }
                else
                {
                    var i = 0;
                    foreach (var stepAction in steps.EnumerateArray())
                    {
                        Action(stepAction, $"{path}/steps/{i++}", mediaIds, menus, ownButtons);
                    }
                }

                break;
            case "resume":
                if (action["otherwise"] is { } otherwise)
                {
                    if (Obj.TryFrom(otherwise, out var oo) && oo["type"] is { ValueKind: JsonValueKind.String } ot && ot.GetString() == "resume")
                    {
                        Error($"{path}/otherwise/type", "Must not be another resume: there would still be nothing to resume.");
                    }
                    else
                    {
                        Action(otherwise, $"{path}/otherwise", mediaIds, menus, ownButtons);
                    }
                }

                break;
            case "activate":
                if (ownButtons is null)
                {
                    Error($"{path}/type", "\"activate\" is only allowed in a menu's back or timeout.");
                }
                else if (!IsNonEmptyString(action["button"]) || !ownButtons.Contains(action["button"]!.Value.GetString()!))
                {
                    Error($"{path}/button", $"No button \"{JsString(action["button"])}\" in this menu.");
                }

                break;
            case "goto":
            {
                var menu = action["menu"];
                if (!IsNonEmptyString(menu) || !menus.ContainsKey(menu!.Value.GetString()!))
                {
                    Error($"{path}/menu", $"No menu \"{JsString(menu)}\".");
                    break;
                }

                var menuId = menu.Value.GetString()!;
                if (action["button"] is { } button)
                {
                    ButtonRef(button, $"{path}/button", menus[menuId], $"menu \"{menuId}\"");
                }

                if (action["transition"] is { } transition && !IsNonEmptyString(transition))
                {
                    Error($"{path}/transition", "Must be a path or URL to a clip.");
                }

                if (action["history"] is { } history && !(history.ValueKind == JsonValueKind.String && history.GetString() is "push" or "replace"))
                {
                    Error($"{path}/history", "Must be \"push\" or \"replace\".");
                }

                break;
            }
        }

        if (type is "play" or "playAll" && action["after"] is not null && _cueDepth > 0)
        {
            Error($"{path}/after", "Playback started during playback returns to it when done; after is not allowed here.");
        }
        else if (type is "play" or "playAll" && action["after"] is { } after)
        {
            var afterOtherwise = Obj.TryFrom(after, out var ra) && ra["type"] is { ValueKind: JsonValueKind.String } rt && rt.GetString() == "resume" ? ra["otherwise"] : null;
            if (Obj.TryFrom(after, out var ao) && ao["type"] is { ValueKind: JsonValueKind.String } at && at.GetString() is "play" or "playAll")
            {
                Error($"{path}/after", "After-playback action must not start another playback; use playAll instead.");
            }
            else if (Obj.TryFrom(afterOtherwise, out var oa) && oa["type"] is { ValueKind: JsonValueKind.String } ot && ot.GetString() is "play" or "playAll")
            {
                Error($"{path}/after/otherwise", "After-playback action must not start another playback; use playAll instead.");
            }
            else
            {
                Action(after, $"{path}/after", mediaIds, menus);
            }
        }
    }

    private void Fallback(JsonElement? value, Dictionary<string, int> mediaIds, Dictionary<string, HashSet<string>> menus)
    {
        if (value is not { ValueKind: JsonValueKind.Array } groups || groups.GetArrayLength() == 0)
        {
            Error("/fallback", "Required: at least one group. Every manifest carries a plain list for basic players.");
            return;
        }

        var visibleItems = 0;
        var i = 0;
        foreach (var gValue in groups.EnumerateArray())
        {
            var gp = $"/fallback/{i++}";
            if (!Obj.TryFrom(gValue, out var g))
            {
                Error(gp, "Must be {label, items}.");
                continue;
            }

            UnknownKeys(g, KnownKeys["fallbackGroup"], gp);
            if (!IsNonEmptyString(g["label"]))
            {
                Error($"{gp}/label", "Required.");
            }

            if (g["hidden"] is { } hidden && !IsBoolean(hidden))
            {
                Error($"{gp}/hidden", "Must be true or false.");
            }

            if (g["items"] is not { ValueKind: JsonValueKind.Array } items || items.GetArrayLength() == 0)
            {
                Error($"{gp}/items", "Required: at least one item.");
                continue;
            }

            var j = 0;
            foreach (var itemValue in items.EnumerateArray())
            {
                var ip = $"{gp}/items/{j++}";
                if (!Obj.TryFrom(itemValue, out var item))
                {
                    Error(ip, "Must be {label, action}.");
                    continue;
                }

                UnknownKeys(item, KnownKeys["fallbackItem"], ip);
                if (!IsNonEmptyString(item["label"]))
                {
                    Error($"{ip}/label", "Required.");
                }

                Action(item["action"], $"{ip}/action", mediaIds, menus);
                var t = Obj.TryFrom(item["action"], out var ao) && ao["type"] is { ValueKind: JsonValueKind.String } te ? te.GetString() : null;
                if (t is "goto" or "back" or "resume" or "set" or "activate" or "if" or "do")
                {
                    Error($"{ip}/action/type", $"Fallback items must play something (\"{t}\" needs a menu-capable player).");
                }
            }

            if (g["hidden"] is not { ValueKind: JsonValueKind.True })
            {
                visibleItems += items.GetArrayLength();
            }
        }

        if (visibleItems == 0)
        {
            Error("/fallback", "At least one fallback group must be visible (not hidden).");
        }
    }

    // --- JSON helpers with JavaScript's meaning ------------------------------------------------

    /// <summary>"16:9", "4:3", "1.85" or a number → width/height; null when it isn't one.</summary>
    internal static double? ParseAspect(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => IsFiniteNumber(value) && value.GetDouble() > 0 ? value.GetDouble() : null,
        JsonValueKind.String => Aspect.ParseRatio(value.GetString()!),
        _ => null,
    };

    private static bool IsNonEmptyString(JsonElement? v) => v is { ValueKind: JsonValueKind.String } s && s.GetString()!.Length > 0;

    private static bool IsBoolean(JsonElement v) => v.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsFiniteNumber(JsonElement v) => v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble());

    private static bool IsInteger(JsonElement v) => IsFiniteNumber(v) && Math.Floor(v.GetDouble()) == v.GetDouble();

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>A value in a message, the way JavaScript's String() shows it.</summary>
    private static string JsString(JsonElement? v) => v switch
    {
        null => "undefined",
        { ValueKind: JsonValueKind.String } s => s.GetString()!,
        { ValueKind: JsonValueKind.Number } n => Num(n.GetDouble()),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.Null } => "null",
        { ValueKind: JsonValueKind.Array } a => string.Join(",", a.EnumerateArray().Select(e => JsString(e))),
        _ => "[object Object]",
    };

    /// <summary>
    /// A JSON object with JavaScript's view of duplicate keys (JSON.parse keeps the last value, in
    /// the first key's place).
    /// </summary>
    private sealed class Obj
    {
        private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
        private readonly List<string> _keys = [];

        public IReadOnlyList<string> Keys => _keys;

        public int Count => _keys.Count;

        public IEnumerable<(string Key, JsonElement Value)> Entries => _keys.Select(k => (k, _values[k]));

        public JsonElement? this[string key] => _values.TryGetValue(key, out var v) ? v : null;

        public static bool TryFrom(JsonElement? value, out Obj obj)
        {
            obj = new Obj();
            if (value is not { ValueKind: JsonValueKind.Object } el)
            {
                return false;
            }

            foreach (var p in el.EnumerateObject())
            {
                if (!obj._values.ContainsKey(p.Name))
                {
                    obj._keys.Add(p.Name);
                }

                obj._values[p.Name] = p.Value;
            }

            return true;
        }
    }
}
