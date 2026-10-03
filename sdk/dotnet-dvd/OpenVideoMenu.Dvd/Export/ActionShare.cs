using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>
/// Size passes over a finished manifest that change nothing a player does: tests whose branches do the same
/// thing become one test, and logic written out in many places becomes one named action each place runs
/// (spec §6.4: <c>run</c> is the action as if written in place).
/// </summary>
public static class ActionShare
{
    private static readonly HashSet<string> ActionTypes = new(StringComparer.Ordinal)
    {
        "do", "if", "set", "run", "play", "playAll", "goto", "saveResume", "clearResume", "resume", "seek",
        "timer", "cancelTimer", "requestParental", "exit", "back",
    };

    /// <summary>Repeated logic smaller than this stays where it is (a run and a name cost about this much).</summary>
    private const int MinBytes = 160;

    public static Manifest Compact(Manifest manifest)
    {
        var options = DvdMenuCompiler.DeepJson;
        var root = System.Text.Json.JsonSerializer.SerializeToNode(manifest, options)!.AsObject();
        CombineIfChains(root);
        var actions = root["actions"] as JsonObject ?? new JsonObject();
        root["actions"] = actions;
        var next = 0;
        for (var round = 0; round < 12 && Factor(root, actions, ref next) > 0; round++) { }
        return System.Text.Json.JsonSerializer.Deserialize<Manifest>(root, options)!;
    }

    // ------------------------------------------------------------------ if chains

    /// <summary>
    /// "if A then X else if B then X else Y" → "if any(A, B) then X else Y": a disc testing a register against
    /// many values that all go to the same line (Men in Black II D1's 27-way language checks).
    /// </summary>
    private static void CombineIfChains(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o.ToList()) CombineIfChains(child);
                if (Type(o) != "if") return;
                while (o["else"] is JsonObject inner && Type(inner) == "if" && o["then"] is { } then && inner["then"] is { } then2 &&
                       then.ToJsonString() == then2.ToJsonString())
                {
                    var tests = new JsonArray();
                    foreach (var owner in new[] { o, inner })
                    {
                        var t = owner["test"]!;
                        owner.Remove("test");
                        if (t is JsonObject to && to["any"] is JsonArray any && to.Count == 1)
                            foreach (var x in any.ToList()) { any.Remove(x); tests.Add(x); }
                        else
                            tests.Add(t);
                    }
                    o["test"] = new JsonObject { ["any"] = tests };
                    var rest = inner["else"];
                    inner.Remove("else");
                    o.Remove("else");
                    if (rest is not null) o["else"] = rest;
                }
                break;
            case JsonArray a:
                foreach (var child in a.ToList()) CombineIfChains(child);
                break;
        }
    }

    private static string? Type(JsonObject o) => o["type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ------------------------------------------------------------------ shared subtrees

    private sealed class Info
    {
        public int Sig;
        public int Size;
        public bool Activates;
    }

    /// <summary>One round: the repeated subtrees worth most become named actions. Returns how many.</summary>
    private static int Factor(JsonObject root, JsonObject actions, ref int next)
    {
        var sigs = new Dictionary<string, int>(StringComparer.Ordinal);
        var info = new Dictionary<JsonNode, Info>(ReferenceEqualityComparer.Instance);
        var occurrences = new Dictionary<int, List<JsonObject>>();

        Info Visit(JsonNode? node)
        {
            var sb = new StringBuilder();
            var i = new Info();
            switch (node)
            {
                case JsonObject o:
                    sb.Append('{');
                    i.Size = 2;
                    foreach (var (key, child) in o)
                    {
                        var c = Visit(child);
                        sb.Append(key).Append(':').Append(c.Sig).Append(',');
                        i.Size += key.Length + 4 + c.Size;
                        i.Activates |= c.Activates;
                    }
                    if (Type(o) == "activate") i.Activates = true;
                    break;
                case JsonArray a:
                    sb.Append('[');
                    i.Size = 2;
                    foreach (var child in a)
                    {
                        var c = Visit(child);
                        sb.Append(c.Sig).Append(',');
                        i.Size += c.Size + 1;
                        i.Activates |= c.Activates;
                    }
                    break;
                case null:
                    sb.Append("null");
                    i.Size = 4;
                    break;
                default:
                    var text = node.ToJsonString();
                    sb.Append('=').Append(text);
                    i.Size = text.Length;
                    break;
            }
            var sig = sb.ToString();
            if (!sigs.TryGetValue(sig, out i.Sig)) sigs[sig] = i.Sig = sigs.Count;
            if (node is not null) info[node] = i;
            if (node is JsonObject obj && Type(obj) is { } t && ActionTypes.Contains(t) && !i.Activates && i.Size >= MinBytes)
            {
                if (!occurrences.TryGetValue(i.Sig, out var list)) occurrences[i.Sig] = list = new();
                list.Add(obj);
            }
            return i;
        }
        Visit(root);

        // A named action whose whole body is the repeated logic already is its shared form.
        var bodies = new Dictionary<int, string>();
        foreach (var (name, body) in actions)
            if (body is not null && info.TryGetValue(body, out var bi)) bodies.TryAdd(bi.Sig, name);

        var chosen = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        bool Inside(JsonNode n)
        {
            for (var p = n.Parent; p is not null; p = p.Parent)
                if (chosen.Contains(p)) return true;
            return false;
        }
        var made = 0;
        foreach (var (sig, list) in occurrences.Where(kv => kv.Value.Count > 1)
                     .OrderByDescending(kv => (long)info[kv.Value[0]].Size * (kv.Value.Count - 1)))
        {
            var size = info[list[0]].Size;
            var name = bodies.GetValueOrDefault(sig);
            var places = list.Where(n => !Inside(n) && !(name is not null && ReferenceEquals(actions[name], n))).ToList();
            var copies = places.Count + (name is null ? 0 : 1);
            // Worth it when the copies saved outweigh a run per place (and the new action's name).
            if (copies < 2 || (long)size * (copies - 1) <= (places.Count + 1) * 48L) continue;
            if (name is null)
            {
                name = $"shared-{++next}";
                actions[name] = places[0].DeepClone();
                bodies[sig] = name;
            }
            foreach (var n in places)
            {
                var run = new JsonObject { ["type"] = "run", ["action"] = name };
                n.ReplaceWith(run);
                chosen.Add(run);
                chosen.Add(n); // what was inside it is gone from the manifest
            }
            made++;
        }
        return made;
    }
}
