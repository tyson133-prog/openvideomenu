using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenVideoMenu.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovm-sdk-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A small title the way a converter would build it: a menu, chapters, a play-all, a fallback.</summary>
    private static Manifest Authored() => new()
    {
        Title = new Title { Name = "Test Film", Year = 2001 },
        Source = new Source { Kind = "dvd", Generator = "tests 1.0" },
        Media =
        {
            ["feature"] = new MediaEntry { Ref = "Test Film (2001).mkv", Kind = "feature", Chapters = [new Chapter { Time = 0 }, new Chapter { Time = 312.5, Label = "The chase" }] },
        },
        Menus = new()
        {
            ["main"] = new Menu
            {
                Canvas = new Canvas { Width = 720, Height = 480, Aspect = "16:9" },
                Background = new Background { Image = "menus/main.jpg", Video = new LoopingMedia { Src = "menus/main.mp4", LoopStart = 2.5 } },
                Buttons =
                [
                    new Button
                    {
                        Id = "play", Rect = new Rect(100, 300, 200, 40), Nav = new Nav { Down = "ch2" },
                        States = new ButtonStates { Selected = new ButtonState { Image = "menus/play-on.png" } },
                        Action = new PlayAction { Media = "feature" },
                    },
                    new Button
                    {
                        Id = "ch2", Rect = new Rect(100, 350, 200, 40), Nav = new Nav { Up = "play" },
                        Action = new PlayAction { Media = "feature", Chapter = 2, After = new GotoAction { Menu = "main", Button = "ch2" } },
                    },
                ],
            },
        },
        RootMenu = "main",
        Fallback = [new FallbackGroup { Label = "Movie", Items = [new FallbackItem { Label = "Play", Action = new PlayAction { Media = "feature" } }] }],
    };

    [Fact]
    public void AuthoredManifestIsValidAndWritesTypeFirst()
    {
        var manifest = Authored();
        var result = manifest.Validate();
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);

        var json = manifest.ToJson();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var action = doc.RootElement.GetProperty("menus").GetProperty("main").GetProperty("buttons")[1].GetProperty("action");
        Assert.Equal(["type", "media", "chapter", "after"], action.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("null", json);
        Assert.Equal(["menus/main.jpg", "menus/main.mp4", "menus/play-on.png"], manifest.MenuAssets());
    }

    [Fact]
    public void AuthoredMistakesAreReportedWithPaths()
    {
        var manifest = Authored();
        manifest.Menus!["main"].Buttons[0].Nav = new Nav { Down = "nowhere" };
        manifest.Fallback.Clear();
        manifest.RootMenu = "missing";
        var paths = manifest.Validate().Errors.Select(e => e.Path).ToList();
        Assert.Contains("/menus/main/buttons/0/nav/down", paths);
        Assert.Contains("/fallback", paths);
        Assert.Contains("/rootMenu", paths);
    }

    [Fact]
    public void DeeplyNestedActionsAreRead()
    {
        // The spec asks readers to accept at least 256 levels; System.Text.Json's default is 64.
        var action = """{ "type": "goto", "menu": "main" }""";
        for (var i = 0; i < 150; i++)
        {
            action = $$"""{ "type": "set", "var": "n", "op": "add", "value": 1, "then": {{action}} }""";
        }

        var json = $$"""
            {
              "format": "openvideomenu", "version": "0.1", "title": { "name": "Deep" }, "vars": { "n": 0 },
              "media": { "f": { "ref": "f.mp4" } }, "rootMenu": "main", "firstPlay": {{action}},
              "menus": { "main": { "canvas": { "width": 10, "height": 10 }, "buttons": [{ "id": "a", "rect": [0, 0, 5, 5], "action": { "type": "play", "media": "f" } }] } },
              "fallback": [{ "label": "Movie", "items": [{ "label": "Play", "action": { "type": "play", "media": "f" } }] }]
            }
            """;
        var result = Manifest.Parse(json);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var (state, _) = Navigator.Step(result.Manifest!, new NavState(), new NavEvent.Start());
        Assert.Equal("main", state.Menu);
        Assert.Equal(150, state.Vars["n"]);
        Assert.True(Manifest.Parse(result.Manifest!.ToJson()).Ok);
    }

    [Fact]
    public void ExtensionsAndUnknownFieldsSurviveARoundTrip()
    {
        var json = """
            {
              "format": "openvideomenu", "version": "0.1", "x-tool": { "build": 7 },
              "title": { "name": "T", "x-sort": "t" },
              "media": { "f": { "ref": { "scheme": "jellyfin", "id": "abc" }, "x-hash": "00" } },
              "fallback": [{ "label": "Movie", "items": [{ "label": "Play", "action": { "type": "play", "media": "f", "x-note": 1 } }] }]
            }
            """;
        var result = Manifest.Parse(json);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var m = result.Manifest!;
        Assert.Equal("jellyfin", m.Media["f"].Ref!.Scheme);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(m.ToJson())), m.ToJson());
    }

    [Fact]
    public void WholeNumbersWrittenWithADecimalPointStillRead()
    {
        var result = Manifest.Parse("""
            { "format": "openvideomenu", "version": "0.1", "title": { "name": "T", "year": 1999.0 },
              "media": { "f": { "ref": "f.mp4", "chapters": [{ "time": 0 }, { "time": 10 }] } },
              "fallback": [{ "label": "M", "items": [{ "label": "P", "action": { "type": "play", "media": "f", "chapter": 2.0 } }] }] }
            """);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(1999, result.Manifest!.Title.Year);
    }

    [Fact]
    public void BadJsonIsAnErrorNotAnException()
    {
        var result = Manifest.Parse("{ nope");
        Assert.False(result.Ok);
        Assert.StartsWith("Not valid JSON", result.Errors[0].Message);
    }

    [Fact]
    public void PacksFromMemoryValidatesAndUnpacks()
    {
        var manifest = Authored();
        var art = new Dictionary<string, byte[]>
        {
            ["menus/main.jpg"] = Encoding.ASCII.GetBytes("JPEG"),
            ["menus/main.mp4"] = new byte[200_000],
            ["menus/play-on.png"] = Encoding.ASCII.GetBytes("PNG!"),
        };
        var bundle = Path.Combine(_dir, "Test Film (2001).ovmz");
        var packed = Bundle.Pack(manifest, name => art.TryGetValue(name, out var b) ? new MemoryStream(b) : null, bundle);
        Assert.Equal(4, packed.Written);
        Assert.Empty(packed.Problems);

        var check = Bundle.Validate(bundle);
        Assert.True(check.Ok, string.Join("\n", check.Errors));
        Assert.Empty(check.Warnings);
        Assert.Equal("Test Film", check.Manifest!.Title.Name);

        // Other ZIP readers see the same stored files, byte for byte.
        using (var zip = ZipFile.OpenRead(bundle))
        {
            Assert.Equal(4, zip.Entries.Count);
            using var png = new StreamReader(zip.GetEntry("menus/play-on.png")!.Open());
            Assert.Equal("PNG!", png.ReadToEnd());
        }

        // A member served as a range, the way servers send motion clips.
        var contents = Bundle.Read(bundle);
        using (var clip = Bundle.OpenMember(bundle, contents.Members["menus/main.mp4"]))
        {
            Assert.Equal(200_000, clip.Length);
            clip.Seek(-10, SeekOrigin.End);
            Assert.Equal(10, clip.Read(new byte[64], 0, 64));
        }

        var outDir = Path.Combine(_dir, "out");
        Assert.Equal(4, Bundle.Unpack(bundle, outDir));
        var loose = Manifest.Load(Path.Combine(outDir, "Test Film (2001).openvideomenu.json"));
        Assert.True(loose.Ok);
        Assert.Empty(loose.Manifest!.MissingAssets(outDir));
        Assert.Equal("JPEG", File.ReadAllText(Path.Combine(outDir, "menus", "main.jpg")));
    }

    [Fact]
    public void PackReportsMissingAndOutsideAssets()
    {
        var manifest = Authored();
        manifest.Menus!["main"].Background!.Image = "../elsewhere.jpg";
        var packed = Bundle.Pack(manifest, _ => null, Path.Combine(_dir, "x.ovmz"));
        Assert.Contains(packed.Problems, p => p.StartsWith("Outside the manifest's folder", StringComparison.Ordinal));
        Assert.Contains(packed.Problems, p => p.StartsWith("Menu asset not found", StringComparison.Ordinal));
        Assert.Contains(Bundle.Validate(Path.Combine(_dir, "x.ovmz")).Warnings, w => w.Path == "/bundle/menus/main.mp4");
    }

    [Fact]
    public void PacksALooseManifestFromItsFolder()
    {
        var manifest = Authored();
        var file = Path.Combine(_dir, "Test Film (2001).openvideomenu.json");
        manifest.Save(file);
        Directory.CreateDirectory(Path.Combine(_dir, "menus"));
        File.WriteAllText(Path.Combine(_dir, "menus", "main.jpg"), "JPEG");
        var packed = Bundle.Pack(file, Path.Combine(_dir, "Test Film (2001).ovmz"));
        Assert.Equal(2, packed.Written);
        Assert.Equal(2, packed.Problems.Count);
        Assert.True(Manifest.Load(Path.Combine(_dir, "Test Film (2001).ovmz")).Ok);
    }

    [Fact]
    public void RefusesCompressedAndUnsafeEntries()
    {
        var file = Path.Combine(_dir, "bad.ovmz");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open())
            {
                s.Write(Encoding.UTF8.GetBytes(new string('x', 500)));
            }

            zip.CreateEntry("../escape.jpg", CompressionLevel.NoCompression).Open().Dispose();
        }

        var contents = Bundle.Read(file);
        Assert.Contains(contents.Errors, e => e.StartsWith("Compressed entry", StringComparison.Ordinal));
        Assert.Contains(contents.Errors, e => e.StartsWith("Entry name not allowed", StringComparison.Ordinal));
        Assert.False(Bundle.Validate(file).Ok);
    }

    [Fact]
    public void NavigatorRunsAnAuthoredMenu()
    {
        var manifest = Authored();
        var (s, _) = Navigator.Step(manifest, new NavState(), new NavEvent.Start());
        Assert.Equal(("main", "play"), (s.Menu, s.Focus));
        (s, _) = Navigator.Step(manifest, s, new NavEvent.Press(Key.Down));
        var (playing, effects) = Navigator.Step(manifest, s, new NavEvent.Press(Key.Enter));
        Assert.Equal(NavMode.Playing, playing.Mode);
        Assert.Equal(new Effect.Play("feature", 312.5), effects[^1]);
        Assert.Equal(NavMode.Menu, s.Mode); // the earlier state is untouched
    }

    private static Manifest CaseManifest(string file) =>
        Manifest.Validate(System.Text.Json.JsonDocument.Parse(File.ReadAllText(Repo.Path("conformance", "cases", file))).RootElement.GetProperty("manifest")).Manifest!;

    /// <summary>Same expected lists as menuAssets() in packages/core (core.test.ts).</summary>
    [Fact]
    public void MenuAssetsListArtButNeverMediaOrAbsoluteUrls()
    {
        Assert.Equal(["menus/main.jpg", "menus/main.m4a", "menus/main.mp4"], CaseManifest("08-motion-buttonsat.json").MenuAssets());
        Assert.Equal(["menus/extras.mp4", "menus/main.mp4", "menus/to-extras.mp4"], CaseManifest("09-transition-skip.json").MenuAssets());
        var dvd = CaseManifest("07-dvd-shaped.json").MenuAssets();
        Assert.Contains("menus/vts1-pgc2-b1-selected.png", dvd);
        Assert.DoesNotContain(dvd, a => a.EndsWith(".mkv", StringComparison.Ordinal));
    }

    [Fact]
    public void MenuAssetsFollowSetActionsToTheirThenAction()
    {
        var result = Manifest.Parse("""
            {
              "format": "openvideomenu", "version": "0.1", "title": { "name": "T" }, "vars": { "audio": 1 },
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
        Assert.Equal(["menus/back.mp4"], result.Manifest!.MenuAssets());
    }

    [Theory]
    [InlineData("examples/sintel/Sintel (2010).openvideomenu.json")]
    [InlineData("examples/dvd-shaped/Example Show.openvideomenu.json")]
    public void ExampleManifestsReadAndRoundTrip(string relative)
    {
        var file = Repo.Path(relative.Split('/'));
        if (!File.Exists(file))
        {
            return;
        }

        var text = File.ReadAllText(file);
        var result = Manifest.Parse(text);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(result.Manifest!.ToJson())));
    }

    [Fact]
    public void A_session_round_trips_as_json_in_the_same_shape_as_typescript()
    {
        var json = """{"vars":{"rabbit":1},"player":{"@audio":2},"lastStop":{"media":"film","position":120,"after":{"type":"goto","menu":"end"}}}""";
        var session = NavSession.FromJson(json)!;
        Assert.Equal(new Scalar(1), session.Vars["rabbit"]);
        Assert.Equal(120, session.LastStop!.Position);
        Assert.Equal("goto", session.LastStop.After!.Type);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(session.ToJson())));
    }
}
