using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.MenuStream.Tests;

/// <summary>
/// The plugin's asset rules must match the reference implementation (menuAssets in
/// packages/core/src/assets.ts): same conformance cases, same expected lists as core.test.ts.
/// </summary>
public class MenuAssetsTests
{
    private static JsonElement CaseManifest(string file)
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "conformance", "cases")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("conformance/cases not found above " + AppContext.BaseDirectory);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "conformance", "cases", file)));
        return doc.RootElement.GetProperty("manifest").Clone();
    }

    private static List<string> Assets(JsonElement manifest) => MenuCatalog.MenuAssets(manifest).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void ListsMenuArtButNeverMediaOrAbsoluteUrls()
    {
        Assert.Equal(["menus/main.jpg", "menus/main.m4a", "menus/main.mp4"], Assets(CaseManifest("08-motion-buttonsat.json")));
        Assert.Equal(["menus/extras.mp4", "menus/main.mp4", "menus/to-extras.mp4"], Assets(CaseManifest("09-transition-skip.json")));
        var dvd = Assets(CaseManifest("07-dvd-shaped.json"));
        Assert.Contains("menus/vts1-pgc2-b1-selected.png", dvd);
        Assert.DoesNotContain(dvd, a => a.EndsWith(".mkv", StringComparison.Ordinal));
    }

    [Fact]
    public void FollowsSetActionsToTheirThenAction()
    {
        using var doc = JsonDocument.Parse("""
            {
              "format": "menustream", "version": "0.2", "title": { "name": "T" }, "vars": { "audio": 1 },
              "media": { "feature": { "ref": "f.mp4" } },
              "menus": {
                "lang": {
                  "canvas": { "width": 720, "height": 480 },
                  "buttons": [{ "id": "c", "rect": [0, 0, 1, 1],
                    "action": { "type": "set", "var": "audio", "value": 2, "then": { "type": "goto", "menu": "lang", "transition": "menus/back.mp4" } } }]
                }
              },
              "rootMenu": "lang",
              "fallback": [{ "label": "Movie", "items": [{ "label": "Play", "action": { "type": "play", "media": "feature" } }] }]
            }
            """);
        Assert.Equal(["menus/back.mp4"], Assets(doc.RootElement));
    }
}
