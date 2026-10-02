using System.Text.Json;

namespace OpenVideoMenu;

/// <summary>
/// Conformance levels (spec §10): which level a manifest's menus use. A port of
/// packages/core/src/levels.ts. Players support levels; a manifest declares the level its menus need
/// (<see cref="Manifest.Level"/>), and the validator checks that against what it uses.
/// </summary>
public static class Levels
{
    /// <summary>
    /// Level 2: fields that make a manifest need it wherever they appear (motion and music, timeouts,
    /// button sets, transitions, angles, segments, track choices, cues, holds, keys, play-all order, and
    /// the information-only prohibits and altRects).
    /// </summary>
    private static readonly HashSet<string> Level2Fields =
    [
        "video", "audio", "buttonsAt", "timeout", "buttonSets", "transition", "angles", "angleTracks", "segments",
        "subtitle", "angle", "end", "cues", "holds", "keys", "prohibits", "altRects", "order", "count", "cuePlayback",
    ];

    private static readonly HashSet<string> Level2Actions = ["seek", "exit"];

    /// <summary>Level 3 (disc logic): actions that keep or change program state.</summary>
    private static readonly HashSet<string> Level3Actions = ["set", "if", "do", "run", "saveResume", "clearResume", "timer", "cancelTimer", "requestParental"];

    private static readonly string[] Level3Top = ["vars", "actions", "resumePoint", "counters"];

    /// <summary>
    /// The level manifest JSON uses: 0 with only a fallback list, 1 with menus, 2 or 3 with those levels'
    /// features. Any {"var": name} is Level 3. The fallback list itself is Level 0 whatever it carries.
    /// </summary>
    public static int Used(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var level = manifest.TryGetProperty("menus", out _) || manifest.TryGetProperty("firstPlay", out _) ? 1 : 0;
        if (Level3Top.Any(key => manifest.TryGetProperty(key, out _)))
        {
            return 3;
        }

        void Walk(JsonElement value)
        {
            if (level == 3)
            {
                return;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in value.EnumerateArray())
                {
                    Walk(v);
                }

                return;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                if (Level3Actions.Contains(type.GetString()!))
                {
                    level = 3;
                }
                else if (Level2Actions.Contains(type.GetString()!))
                {
                    level = Math.Max(level, 2);
                }
            }

            if (value.TryGetProperty("var", out _))
            {
                level = 3;
            }

            foreach (var p in value.EnumerateObject())
            {
                if (p.Name.StartsWith("x-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Level2Fields.Contains(p.Name))
                {
                    level = Math.Max(level, 2);
                }

                Walk(p.Value);
            }
        }

        foreach (var p in manifest.EnumerateObject())
        {
            if (p.Name is not ("fallback" or "title" or "source") && !p.Name.StartsWith("x-", StringComparison.Ordinal))
            {
                // e.g. the manifest's keys, cuePlayback
                if (Level2Fields.Contains(p.Name))
                {
                    level = Math.Max(level, 2);
                }

                Walk(p.Value);
            }
        }

        return level;
    }
}
