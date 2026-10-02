using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenVideoMenu.Tests;

/// <summary>
/// Every case in conformance/cases, run through this library: the validator must accept or reject
/// it like the reference (and report the listed error and warning paths), and the navigator must
/// produce the listed states and effects. Same files as conformance/conformance.test.ts.
/// </summary>
public sealed class ConformanceTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Repo.Path("conformance", "cases"), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Repo.Path("conformance", "cases", name))).RootElement;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Level(string name)
    {
        // Each case is for players at its level, so it must not use anything above it.
        var c = Load(name);
        Assert.Equal(c.GetProperty("level").GetInt32(), Levels.Used(c.GetProperty("manifest")));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Validator(string name)
    {
        var c = Load(name);
        var result = Manifest.Validate(c.GetProperty("manifest"));
        if (c.GetProperty("valid").GetBoolean())
        {
            Assert.True(result.Ok, string.Join("\n", result.Errors));
            Assert.NotNull(result.Manifest);
        }
        else
        {
            Assert.False(result.Ok);
        }

        var errorPaths = result.Errors.Select(e => e.Path).ToList();
        foreach (var path in Strings(c, "errors"))
        {
            Assert.True(errorPaths.Contains(path), $"missing error at {path}; got {string.Join(", ", errorPaths)}");
        }

        var warningPaths = result.Warnings.Select(w => w.Path).ToList();
        foreach (var path in Strings(c, "warnings"))
        {
            Assert.True(warningPaths.Contains(path), $"missing warning at {path}; got {string.Join(", ", warningPaths)}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Navigation(string name)
    {
        var c = Load(name);
        if (!c.TryGetProperty("steps", out var steps))
        {
            return;
        }

        var manifest = Manifest.Validate(c.GetProperty("manifest")).Manifest!;
        var state = new NavState();
        var i = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var navEvent = Event(step.GetProperty("event"));
            var (next, effects) = Navigator.Step(manifest, state, navEvent);
            state = next;
            var label = $"step {i++} ({step.GetProperty("event").GetRawText()})";
            if (!step.TryGetProperty("expect", out var want))
            {
                continue;
            }

            if (want.TryGetProperty("mode", out var mode))
            {
                Assert.True(JsonNamingPolicy.CamelCase.ConvertName(state.Mode.ToString()) == mode.GetString(), $"{label}: mode {state.Mode}");
            }

            if (want.TryGetProperty("menu", out var menu))
            {
                Assert.True(state.Menu == menu.GetString(), $"{label}: menu {state.Menu}");
            }

            if (want.TryGetProperty("focus", out var focus))
            {
                Assert.True(state.Focus == focus.GetString(), $"{label}: focus {state.Focus}");
            }

            if (want.TryGetProperty("intro", out var intro))
            {
                Assert.True(state.Intro == intro.GetBoolean(), $"{label}: intro {state.Intro}");
            }

            if (want.TryGetProperty("buttonsReady", out var ready))
            {
                Assert.True(state.ButtonsReady == ready.GetBoolean(), $"{label}: buttonsReady {state.ButtonsReady}");
            }

            if (want.TryGetProperty("overlay", out var overlay))
            {
                var expectedOverlay = overlay.ValueKind == JsonValueKind.Null ? null : new OverlayFocus(overlay.GetProperty("cue").GetString()!, overlay.GetProperty("focus").GetString()!);
                Assert.True(state.Overlay == expectedOverlay, $"{label}: overlay {state.Overlay}, expected {expectedOverlay}");
            }

            if (want.TryGetProperty("effects", out var wantEffects))
            {
                var expected = wantEffects.EnumerateArray().Select(Effect).ToList();
                Assert.True(expected.SequenceEqual(effects), $"{label}: effects\n  want {string.Join(", ", expected)}\n  got  {string.Join(", ", effects)}");
            }
        }
    }

    /// <summary>Valid manifests survive read → write → read unchanged, extensions included.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrip(string name)
    {
        var c = Load(name);
        if (!c.GetProperty("valid").GetBoolean())
        {
            return;
        }

        var original = JsonNode.Parse(c.GetProperty("manifest").GetRawText());
        var manifest = Manifest.Validate(c.GetProperty("manifest")).Manifest!;
        var written = JsonNode.Parse(manifest.ToJson());
        Assert.True(JsonNode.DeepEquals(original, written), $"wrote:\n{manifest.ToJson()}");
        Assert.True(Manifest.Parse(manifest.ToJson()).Ok);
    }

    private static IEnumerable<string> Strings(JsonElement c, string name) =>
        c.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(e => e.GetString()!) : [];

    private static NavEvent Event(JsonElement e)
    {
        var navEvent = EventBody(e);
        return e.TryGetProperty("time", out var time) ? navEvent with { Time = time.GetDouble() } : navEvent;
    }

    private static NavEvent EventBody(JsonElement e) => e.GetProperty("type").GetString() switch
    {
        "start" => new NavEvent.Start(
            e.TryGetProperty("player", out var player) ? player.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? new Scalar(p.Value.GetString()!) : new Scalar(p.Value.GetInt32())) : null,
            e.TryGetProperty("seed", out var seed) ? seed.GetInt64() : null),
        "player" => new NavEvent.PlayerChanged(e.GetProperty("values").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? new Scalar(p.Value.GetString()!) : new Scalar(p.Value.GetInt32()))),
        "press" => new NavEvent.Press(Enum.Parse<Key>(e.GetProperty("key").GetString()!, ignoreCase: true), e.TryGetProperty("position", out var position) ? position.GetDouble() : null),
        "select" => new NavEvent.Select(e.GetProperty("button").GetString()!),
        "activate" => new NavEvent.Activate(e.GetProperty("button").GetString()!),
        "ended" => new NavEvent.Ended(),
        "stopped" => new NavEvent.Stopped(e.GetProperty("position").GetDouble()),
        "buttonsReady" => new NavEvent.ButtonsReady(),
        "transitionEnded" => new NavEvent.TransitionEnded(),
        "timeout" => new NavEvent.Timeout(e.GetProperty("menu").GetString()!),
        "cue" => new NavEvent.Cue(e.GetProperty("id").GetString()!),
        "cueEnd" => new NavEvent.CueEnd(e.GetProperty("id").GetString()!),
        "parental" => new NavEvent.Parental(e.GetProperty("granted").GetBoolean()),
        "navTimer" => new NavEvent.NavTimer(e.TryGetProperty("position", out var timerPosition) ? timerPosition.GetDouble() : null),
        "buttonSet" => new NavEvent.ButtonSetChanged(e.GetProperty("index").ValueKind == JsonValueKind.Null ? null : e.GetProperty("index").GetInt32()),
        var other => throw new InvalidDataException($"Unknown event {other}"),
    };

    private static Effect Effect(JsonElement e)
    {
        int? Int(string name) => e.TryGetProperty(name, out var v) ? v.GetInt32() : null;
        double? Num(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? v.GetDouble() : null;
        List<CueWatch>? Cues() => e.TryGetProperty("cues", out var list)
            ? [.. list.EnumerateArray().Select(c => new CueWatch(c.GetProperty("id").GetString()!, Num(c, "at"), Num(c, "from"), Num(c, "to")))]
            : null;
        return e.GetProperty("type").GetString() switch
        {
            "play" => new Effect.Play(e.GetProperty("media").GetString()!, e.GetProperty("start").GetDouble(), Int("audio"), Int("subtitle"), Int("angle"), Num(e, "end"), Cues(),
                e.TryGetProperty("prohibits", out var prohibits) ? [.. prohibits.EnumerateArray().Select(p => p.GetString()!)] : null,
                e.TryGetProperty("holds", out var holds)
                    ? [.. holds.EnumerateArray().Select(h => new Hold { At = h.GetProperty("at").GetDouble(), For = h.GetProperty("for").ValueKind == JsonValueKind.String ? HoldTime.Input : h.GetProperty("for").GetDouble() })]
                    : null,
                e.TryGetProperty("prohibitsAt", out var windows)
                    ? [.. windows.EnumerateArray().Select(w => new ProhibitWindow
                    {
                        From = w.GetProperty("from").GetDouble(),
                        To = w.TryGetProperty("to", out var to) ? to.GetDouble() : null,
                        Ops = [.. w.GetProperty("ops").EnumerateArray().Select(o => o.GetString()!)],
                    })]
                    : null),
            "activated" when e.TryGetProperty("cue", out var cue) => new Effect.OverlayActivated(cue.GetString()!, e.GetProperty("button").GetString()!),
            "activated" => new Effect.Activated(e.GetProperty("menu").GetString()!, e.GetProperty("button").GetString()!),
            "exit" => new Effect.Exit(),
            "transition" => new Effect.Transition(e.GetProperty("src").GetString()!),
            "skipIntro" => new Effect.SkipIntro(e.GetProperty("menu").GetString()!),
            "startTimer" => new Effect.StartTimer(e.GetProperty("menu").GetString()!, e.GetProperty("after").GetDouble()),
            "budgetExceeded" => new Effect.BudgetExceeded(),
            "seek" => new Effect.Seek(e.GetProperty("position").GetDouble()),
            "startNavTimer" => new Effect.StartNavTimer(e.GetProperty("after").GetDouble()),
            "cancelNavTimer" => new Effect.CancelNavTimer(),
            "askParental" => new Effect.AskParental(e.GetProperty("level").GetInt32()),
            "tracks" => new Effect.Tracks(e.GetProperty("at").GetDouble(), Int("audio"), Int("subtitle"), Int("angle")),
            var other => throw new InvalidDataException($"Unknown effect {other}"),
        };
    }
}

/// <summary>Files in this repository, found from the test's output folder.</summary>
internal static class Repo
{
    private static readonly string Root = Find();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "conformance", "cases")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("conformance/cases not found above the test output folder.");
    }
}
