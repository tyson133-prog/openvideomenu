using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVideoMenu;

// Level 2 (spec §6.3): what happens during playback. Mirrors Cue/PointCue/Overlay in packages/core/src/types.ts.

/// <summary>Something that happens during playback: a <see cref="PointCue"/> or an <see cref="Overlay"/>.</summary>
[JsonConverter(typeof(CueConverter))]
public abstract class Cue
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>Runs <see cref="Action"/> when playback passes <see cref="At"/>.</summary>
public sealed class PointCue : Cue
{
    /// <summary>Seconds into the media.</summary>
    public double At { get; set; }

    public MenuAction Action { get; set; } = new BackAction();
}

/// <summary>Buttons over the video from <see cref="From"/> to <see cref="To"/>, built like a menu's.</summary>
public sealed class Overlay : Cue
{
    /// <summary>Seconds into the media.</summary>
    public double From { get; set; }

    public double To { get; set; }

    public Canvas Canvas { get; set; } = new();

    public List<Button> Buttons { get; set; } = [];

    public ButtonRef? DefaultButton { get; set; }

    public bool? AutoNav { get; set; }
}

/// <summary>A cue the host watches for during playback, from <see cref="Effect.Play"/>: a point cue's time, or an overlay's window.</summary>
public sealed record CueWatch(string Id, double? At = null, double? From = null, double? To = null)
{
    public override string ToString() => At is { } at ? $"{Id}@{at}" : $"{Id}[{From}..{To})";
}

internal sealed class CueConverter : JsonConverter<Cue>
{
    public override Cue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A cue is {at, action} or an overlay {from, to, canvas, buttons}.");
        }

        return el.TryGetProperty("at", out _) ? el.Deserialize<PointCue>(options) : el.Deserialize<Overlay>(options);
    }

    public override void Write(Utf8JsonWriter writer, Cue value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
