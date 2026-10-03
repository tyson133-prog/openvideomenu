using System;
using System.Collections.Generic;
using System.Linq;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>
/// Last passes over a compiled manifest: drops register commands whose values nothing ever reads (a
/// disc's bookkeeping that changes no menu, track or playback), and keeps every action shallow enough
/// for JSON readers (the .NET library reads 64 levels).
/// </summary>
public static class ActionTidy
{
    /// <summary>The manifest as compact JSON, at any nesting depth.</summary>
    public static string ToJsonDeep(this Manifest manifest) => System.Text.Json.JsonSerializer.Serialize(manifest, DvdMenuCompiler.DeepJson);

    /// <summary>Deepest JSON nesting a manifest may have.</summary>
    public const int MaxJsonDepth = 62;

    /// <summary>
    /// Overlays that open on the button SPRM 8 names read its id from <see cref="DvdMenuCompiler.HighlightIdVar"/>:
    /// every set of s8 also sets that (a literal directly, anything else through one shared action).
    /// </summary>
    public static void TrackHighlightId(Manifest manifest)
    {
        const string id = DvdMenuCompiler.HighlightIdVar, convert = "s8-to-hl";
        if (!manifest.Media.Values.Any(m => m.Cues?.OfType<Overlay>().Any(o => o.DefaultButton?.Var == id) == true)) return;
        foreach (var s in AllActions(manifest).OfType<SetAction>().Where(s => s.Var == "s8").ToList())
            s.Then = s.Op is null or "set" && s.Value.Literal?.Number is { } n
                ? new SetAction { Var = id, Value = $"b{n >> 10}", Then = s.Then }
                : s.Then is null ? new RunAction { Action = convert } : new DoAction { Steps = { new RunAction { Action = convert }, s.Then } };
        manifest.Actions ??= new Dictionary<string, MenuAction>();
        var steps = new List<MenuAction>();
        foreach (var n in Enumerable.Range(1, 36))
            steps.Add(new IfAction { Test = new Test.Compare("s8", "eq", n << 10), Then = new SetAction { Var = id, Value = $"b{n}" } });
        manifest.Actions[convert] = new DoAction { Steps = steps };
        manifest.Vars ??= new Dictionary<string, Scalar>();
        manifest.Vars[id] = "b1";
    }

    /// <summary>
    /// Writes once on the media entry what every play of it repeats (spec §6.6): its keys, when every play has
    /// the same (a play item's keys win over its media's), and the prohibitions every play shares (the media's
    /// and the item's both apply). A title's plays all carry its title set's keys and its PGC's operations.
    /// </summary>
    public static void HoistToMedia(Manifest manifest)
    {
        var plays = new List<(string Media, Func<MenuKeys?> GetKeys, Action<MenuKeys?> SetKeys, Func<List<string>?> GetOps, Action<List<string>?> SetOps)>();
        void Item(PlayItem? i)
        {
            if (i is not null) plays.Add((i.Media, () => i.Keys, k => i.Keys = k, () => i.Prohibits, p => i.Prohibits = p));
        }
        foreach (var a in AllActions(manifest))
            switch (a)
            {
                case PlayAction p:
                    plays.Add((p.Media, () => p.Keys, k => p.Keys = k, () => p.Prohibits, o => p.Prohibits = o));
                    break;
                case PlayAllAction pa:
                    foreach (var i in pa.Items) Item(i);
                    break;
                case SaveResumeAction sr:
                    Item(sr.Item);
                    foreach (var i in sr.Queue ?? new()) Item(i);
                    break;
            }
        string Json(object? o) => System.Text.Json.JsonSerializer.Serialize(o, DvdMenuCompiler.DeepJson);
        foreach (var group in plays.GroupBy(p => p.Media))
        {
            if (!manifest.Media.TryGetValue(group.Key, out var media)) continue;
            var list = group.ToList();
            if (media.Keys is null && list[0].GetKeys() is { } keys && list.All(p => Json(p.GetKeys()) == Json(keys)))
            {
                media.Keys = keys;
                foreach (var p in list) p.SetKeys(null);
            }
            var shared = list.Select(p => p.GetOps() ?? new List<string>()).Aggregate((x, y) => x.Intersect(y).ToList());
            if (shared.Count > 0)
            {
                media.Prohibits = (media.Prohibits ?? new()).Union(shared).ToList();
                foreach (var p in list)
                    if (p.GetOps() is { } ops)
                    {
                        var rest = ops.Except(shared).ToList();
                        p.SetOps(rest.Count > 0 ? rest : null);
                    }
            }
        }
    }

    /// <summary>Removes sets of variables that no test, value, button reference or track choice reads.</summary>
    /// <summary>Drops sets of variables nothing reads; returns the variables something reads.</summary>
    public static HashSet<string> DropUnreadSets(Manifest manifest)
    {
        var roots = Roots(manifest).ToList();
        var all = roots.SelectMany(r => Walk(r.Get())).ToList();

        // Variables read directly, then those whose values flow into a read one (until nothing changes).
        var live = new HashSet<string>(StringComparer.Ordinal) { "_none" };
        void Read(Value? v) { if (v?.Var is { } n) live.Add(n); }
        void ReadRef(ButtonRef? r) { if (r?.Var is { } n) live.Add(n); }
        void ReadInt(IntOrVar? v) { if (v?.Var is { } n) live.Add(n); }
        void ReadTest(Test t)
        {
            switch (t)
            {
                case Test.Compare c: live.Add(c.Var); Read(c.Value); break;
                case Test.All a: foreach (var x in a.Tests) ReadTest(x); break;
                case Test.Any a: foreach (var x in a.Tests) ReadTest(x); break;
                case Test.Not n: ReadTest(n.Test); break;
            }
        }
        foreach (var a in all)
            switch (a)
            {
                case IfAction i: ReadTest(i.Test); break;
                case GotoAction g: ReadRef(g.Button); break;
                case PlayAction p: ReadInt(p.Audio); ReadInt(p.Subtitle); ReadInt(p.Angle); break;
                case PlayAllAction pa: foreach (var it in pa.Items) { ReadInt(it.Audio); ReadInt(it.Subtitle); ReadInt(it.Angle); } break;
            }
        if (manifest.Menus is not null)
            foreach (var m in manifest.Menus.Values)
                ReadRef(m.DefaultButton);
        foreach (var media in manifest.Media.Values)
            foreach (var o in media.Cues?.OfType<Overlay>() ?? Enumerable.Empty<Overlay>())
                ReadRef(o.DefaultButton);
        var sets = all.OfType<SetAction>().ToList();
        // A random draw stays even when nothing reads its value: each draw moves the generator on, so later
        // draws depend on it (Barney Happy Mad Silly Sad draws an audio choice nothing tests, then a song).
        foreach (var s in sets)
            if (s.Op == "random") live.Add(s.Var);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var s in sets)
                if ((s.Var.StartsWith('@') || live.Contains(s.Var)) && s.Value.Var is { } source && live.Add(source))
                    changed = true;
        }

        foreach (var r in roots) r.Set(DvdMenuCompiler.Normalize(Strip(r.Get(), live), after: false));
        if (manifest.Vars is not null)
            foreach (var k in manifest.Vars.Keys.ToList())
                if (!live.Contains(k)) manifest.Vars.Remove(k);
        if (manifest.Vars is { Count: 0 }) manifest.Vars = null;
        return live;
    }

    private static MenuAction? Strip(MenuAction? a, HashSet<string> live)
    {
        switch (a)
        {
            case null:
                return null;
            case SetAction s when !s.Var.StartsWith('@') && !live.Contains(s.Var):
                return Strip(s.Then, live);
            case SetAction s:
                s.Then = Strip(s.Then, live);
                return s;
            case DoAction d:
                d.Steps = d.Steps.Select(x => Strip(x, live)).OfType<MenuAction>().ToList();
                return d;
            case IfAction i:
                i.Then = Strip(i.Then, live)!;
                i.Else = Strip(i.Else, live);
                return i;
            case PlayAction p:
                p.After = Strip(p.After, live);
                if (p.Cues is not null)
                    foreach (var c in p.Cues.OfType<PointCue>()) c.Action = Strip(c.Action, live)!;
                return p;
            case PlayAllAction pa:
                pa.After = Strip(pa.After, live);
                return pa;
            case ResumeAction r:
                r.Otherwise = Strip(r.Otherwise, live);
                return r;
            case SaveResumeAction sr:
                sr.After = Strip(sr.After, live);
                foreach (var item in new[] { sr.Item }.Concat(sr.Queue ?? new()))
                    if (item?.Cues is not null)
                        foreach (var c in item.Cues.OfType<PointCue>()) c.Action = Strip(c.Action, live) ?? new SetAction { Var = "_none", Value = 0 };
                return sr;
            case RequestParentalAction q:
                q.Then = Strip(q.Then, live) ?? new SetAction { Var = "_none", Value = 0 };
                q.Else = Strip(q.Else, live);
                return q;
            case TimerAction t:
                t.Action = Strip(t.Action, live) ?? new SetAction { Var = "_none", Value = 0 };
                return t;
            default:
                return a;
        }
    }

    /// <summary>
    /// Trims actions that would nest deeper than JSON readers allow: the deepest continuations go (a play
    /// then just returns to its menu). Returns how many were trimmed.
    /// </summary>
    public static int LimitDepth(Manifest manifest)
    {
        var trimmed = 0;
        foreach (var r in Roots(manifest))
        {
            var budget = MaxJsonDepth - r.Depth;
            var a = r.Get();
            if (a is null || Depth(a) <= budget) continue;
            r.Set(Trim(a, budget, ref trimmed));
        }
        return trimmed;
    }

    /// <summary>The JSON nesting an action takes (its own object counts 1).</summary>
    public static int Depth(MenuAction? a) => a switch
    {
        null => 0,
        DoAction d => 2 + d.Steps.Select(Depth).DefaultIfEmpty(0).Max(),
        IfAction i => 1 + Math.Max(TestDepth(i.Test), Math.Max(Depth(i.Then), Depth(i.Else))),
        SetAction s => 1 + Math.Max(1, Depth(s.Then)),
        PlayAction p => 1 + Math.Max(Math.Max(1, Depth(p.After)), p.Cues is { Count: > 0 } ? 2 + p.Cues.OfType<PointCue>().Select(c => Depth(c.Action)).DefaultIfEmpty(1).Max() : 0),
        PlayAllAction pa => 1 + Math.Max(3, Depth(pa.After)),
        ResumeAction r => 1 + Depth(r.Otherwise),
        GotoAction => 2,
        _ => 1,
    };

    private static int TestDepth(Test t) => t switch
    {
        Test.All a => 2 + a.Tests.Select(TestDepth).DefaultIfEmpty(0).Max(),
        Test.Any a => 2 + a.Tests.Select(TestDepth).DefaultIfEmpty(0).Max(),
        Test.Not n => 1 + TestDepth(n.Test),
        _ => 2,
    };

    private static MenuAction? Trim(MenuAction? a, int budget, ref int trimmed)
    {
        if (a is null) return null;
        if (Depth(a) <= budget) return a;
        if (budget <= 2) { trimmed++; return null; }
        switch (a)
        {
            case DoAction d:
                {
                    var steps = new List<MenuAction>();
                    foreach (var s in d.Steps) if (Trim(s, budget - 2, ref trimmed) is { } t) steps.Add(t);
                    d.Steps = steps;
                    return steps.Count == 0 ? null : d;
                }
            case IfAction i:
                i.Then = Trim(i.Then, budget - 1, ref trimmed) ?? new SetAction { Var = "_none", Value = 0 };
                i.Else = Trim(i.Else, budget - 1, ref trimmed);
                return i;
            case SetAction s:
                s.Then = Trim(s.Then, budget - 1, ref trimmed);
                return s;
            case PlayAction p:
                p.After = Trim(p.After, budget - 1, ref trimmed);
                if (p.Cues is not null)
                {
                    var limit = budget - 3;
                    var kept = new List<Cue>();
                    foreach (var c in p.Cues)
                        if (c is not PointCue pc) kept.Add(c);
                        else if (Trim(pc.Action, limit, ref trimmed) is { } t) { pc.Action = t; kept.Add(pc); }
                    p.Cues = kept.Count > 0 ? kept : null;
                }
                return p;
            case PlayAllAction pa:
                pa.After = Trim(pa.After, budget - 1, ref trimmed);
                return pa;
            case ResumeAction r:
                r.Otherwise = Trim(r.Otherwise, budget - 1, ref trimmed);
                return r;
            default:
                trimmed++;
                return null;
        }
    }

    private sealed record Root(int Depth, Func<MenuAction?> Get, Action<MenuAction?> Set);

    /// <summary>Every top-level action in the manifest, with the JSON depth it sits at.</summary>
    private static IEnumerable<Root> Roots(Manifest manifest)
    {
        if (manifest.FirstPlay is not null)
            yield return new Root(2, () => manifest.FirstPlay, v => manifest.FirstPlay = v);
        if (manifest.Actions is not null)
            foreach (var name in manifest.Actions.Keys.ToList())
                yield return new Root(3, () => manifest.Actions[name], v => manifest.Actions[name] = v ?? new SetAction { Var = "_none", Value = 0 });
        if (manifest.Menus is not null)
            foreach (var menu in manifest.Menus.Values)
            {
                foreach (var b in menu.Buttons.Concat(menu.ButtonSets?.SelectMany(x => x.Buttons) ?? Enumerable.Empty<Button>()))
                    yield return new Root(5, () => b.Action, v => b.Action = v ?? new SetAction { Var = "_none", Value = 0 });
                if (menu.Timeout is { } t)
                    yield return new Root(4, () => t.Action, v => { if (v is null) menu.Timeout = null; else t.Action = v; });
                if (menu.Back is not null)
                    yield return new Root(3, () => menu.Back, v => menu.Back = v);
            }
        foreach (var media in manifest.Media.Values)
            if (media.Cues is not null)
                foreach (var c in media.Cues.OfType<PointCue>())
                    yield return new Root(5, () => c.Action, v => c.Action = v ?? new SetAction { Var = "_none", Value = 0 });
    }

    private static IEnumerable<MenuAction> Walk(MenuAction? a) => ActionTree.Walk(a);

    /// <summary>
    /// Merges named actions that are the same (the same command on many cells, pages or entries), keeping one
    /// and pointing every <c>run</c> at it, until no two are left the same. Returns the manifest with the
    /// references rewritten (a new object).
    /// </summary>
    public static Manifest MergeIdenticalActions(Manifest manifest)
    {
        if (manifest.Actions is null) return manifest;
        var options = DvdMenuCompiler.DeepJson;
        while (true)
        {
            var rename = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in manifest.Actions.GroupBy(a => System.Text.Json.JsonSerializer.Serialize(a.Value, options)))
            {
                var names = group.Select(g => g.Key).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();
                foreach (var n in names.Skip(1)) rename[n] = names[0];
            }
            if (rename.Count == 0) return manifest;
            foreach (var n in rename.Keys) manifest.Actions.Remove(n);
            var node = System.Text.Json.JsonSerializer.SerializeToNode(manifest, options)!;
            Rewrite(node, rename);
            manifest = System.Text.Json.JsonSerializer.Deserialize<Manifest>(node, options)!;
        }

        static void Rewrite(System.Text.Json.Nodes.JsonNode? node, Dictionary<string, string> rename)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    if (o["type"]?.GetValue<string>() == "run" && o["action"]?.GetValue<string>() is { } target && rename.TryGetValue(target, out var to))
                        o["action"] = to;
                    foreach (var (_, child) in o.ToList()) Rewrite(child, rename);
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var child in a) Rewrite(child, rename);
                    break;
            }
        }
    }

    /// <summary>Every action in the manifest.</summary>
    public static IEnumerable<MenuAction> AllActions(Manifest manifest) => Roots(manifest).SelectMany(r => Walk(r.Get()));
}
