using System.Text.RegularExpressions;

namespace OpenVideoMenu;

/// <summary>
/// A menu's season and volume as its path names them, for manifests that don't say (spec §3.1, server API
/// index): "Season 05", "Specials" (season 0), "Disc 2" (or "Disk 2"), or both at once as "S05D2". The name
/// nearest the file wins: "Show/Season 05/Disc 2/Disc 2.ovmz" is season 5, volume 2. Mirrors
/// packages/asset-server/src/place.ts.
/// </summary>
public static partial class Place
{
    public static (int? Season, int? Volume) FromPath(string path)
    {
        int? season = null, volume = null;
        foreach (var name in path.Replace('\\', '/').Split('/').AsEnumerable().Reverse())
        {
            if (Both().Match(name) is { Success: true } both)
            {
                season ??= int.Parse(both.Groups[1].Value);
                if (int.Parse(both.Groups[2].Value) >= 1) volume ??= int.Parse(both.Groups[2].Value);
            }

            if (Season().Match(name) is { Success: true } s) season ??= int.Parse(s.Groups[1].Value);
            else if (name.Equals("Specials", StringComparison.OrdinalIgnoreCase)) season ??= 0;

            if (Volume().Match(name) is { Success: true } v && int.Parse(v.Groups[1].Value) >= 1) volume ??= int.Parse(v.Groups[1].Value);
        }

        return (season, volume);
    }

    [GeneratedRegex(@"\bS([0-9]{1,3})[ ._-]?D([0-9]{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Both();

    [GeneratedRegex(@"\bseason[ ._-]*([0-9]{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Season();

    [GeneratedRegex(@"\bdis[ck][ ._-]*([0-9]{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Volume();
}
