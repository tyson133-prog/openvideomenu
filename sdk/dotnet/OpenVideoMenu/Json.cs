using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVideoMenu;

public sealed partial class Manifest
{
    /// <summary>
    /// How manifests are written: camelCase, absent values left out, two-space indent, non-ASCII
    /// text kept readable. Use these options if you serialize model objects yourself.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    /// <summary>
    /// How deep manifest JSON may nest. The spec asks readers for at least 256 levels; System.Text.Json
    /// stops at 64 unless told otherwise.
    /// </summary>
    public const int MaxDepth = 1024;

    /// <summary>Parse and validate manifest JSON. <see cref="ValidationResult.Manifest"/> is set when it's valid.</summary>
    public static ValidationResult Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException e)
        {
            return ValidationResult.Failed(new Issue("", $"Not valid JSON: {e.Message}"));
        }

        using (doc)
        {
            return Validate(doc.RootElement);
        }
    }

    /// <summary>Validate already-parsed JSON; on success the result carries the typed manifest.</summary>
    public static ValidationResult Validate(JsonElement json)
    {
        var (errors, warnings) = Validator.Run(json);
        Manifest? manifest = null;
        if (errors.Count == 0)
        {
            manifest = json.Deserialize<Manifest>(JsonOptions)!;
        }

        return new ValidationResult(errors, warnings, manifest);
    }

    /// <summary>
    /// Read a manifest file: a loose <c>&lt;Title&gt;.openvideomenu.json</c> or a <c>&lt;Title&gt;.ovmz</c>
    /// bundle (checked as a bundle too, see <see cref="Bundle.Validate"/>).
    /// </summary>
    public static ValidationResult Load(string path) =>
        OpenVideoMenu.Format.IsBundleFileName(path) ? Bundle.Validate(path) : Parse(File.ReadAllText(path));

    /// <summary>Check this manifest against the spec, exactly as a reader would see it once written.</summary>
    public ValidationResult Validate() => Validate(JsonSerializer.SerializeToElement(this, JsonOptions));

    /// <summary>The manifest as JSON text (not validated; call <see cref="Validate()"/> first).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Write the manifest to a file as UTF-8 JSON (not validated; call <see cref="Validate()"/> first).</summary>
    public void Save(string path) => File.WriteAllText(path, ToJson() + "\n");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = MaxDepth,
        };
        options.Converters.Add(new WholeNumberConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>The result of reading or validating a manifest.</summary>
public sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<Issue> errors, IReadOnlyList<Issue> warnings, Manifest? manifest)
    {
        Errors = errors;
        Warnings = warnings;
        Manifest = manifest;
    }

    /// <summary>No errors: a conforming reader accepts it.</summary>
    public bool Ok => Errors.Count == 0;

    public IReadOnlyList<Issue> Errors { get; }

    /// <summary>Problems that don't stop a player: unknown fields, a newer minor version, off-canvas rects…</summary>
    public IReadOnlyList<Issue> Warnings { get; }

    /// <summary>The manifest, when <see cref="Ok"/>.</summary>
    public Manifest? Manifest { get; }

    internal static ValidationResult Failed(params Issue[] errors) => new(errors, [], null);
}

/// <summary>A problem found in a manifest.</summary>
/// <param name="Path">JSON-pointer-like location, e.g. <c>/menus/main/buttons/2/nav/up</c> ("" = the whole manifest).</param>
/// <param name="Message">What's wrong, for people.</param>
public sealed record Issue(string Path, string Message)
{
    public override string ToString() => $"{(Path.Length == 0 ? "/" : Path)}: {Message}";
}

// --- converters --------------------------------------------------------------------------------

/// <summary>Reads an action by its <c>type</c>; writes <c>type</c> first.</summary>
internal sealed class MenuActionConverter : JsonConverter<MenuAction>
{
    public override MenuAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("An action must be an object with a \"type\".");
        }

        Type target = typeEl.GetString() switch
        {
            "play" => typeof(PlayAction),
            "playAll" => typeof(PlayAllAction),
            "goto" => typeof(GotoAction),
            "back" => typeof(BackAction),
            "resume" => typeof(ResumeAction),
            "activate" => typeof(ActivateAction),
            "set" => typeof(SetAction),
            "if" => typeof(IfAction),
            "do" => typeof(DoAction),
            "run" => typeof(RunAction),
            "saveResume" => typeof(SaveResumeAction),
            "clearResume" => typeof(ClearResumeAction),
            "seek" => typeof(SeekAction),
            var other => throw new JsonException($"Unknown action type \"{other}\"."),
        };
        var action = (MenuAction)el.Deserialize(target, options)!;
        action.Extensions?.Remove("type");
        if (action.Extensions is { Count: 0 })
        {
            action.Extensions = null;
        }

        return action;
    }

    public override void Write(Utf8JsonWriter writer, MenuAction value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

internal sealed class MediaRefConverter : JsonConverter<MediaRef>
{
    public override MediaRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new MediaRef(reader.GetString()!);
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("scheme", out var scheme) && el.TryGetProperty("id", out var id))
        {
            return new MediaRef(scheme.GetString()!, id.GetString()!);
        }

        throw new JsonException("A media ref is a path or URL string, or {\"scheme\", \"id\"}.");
    }

    public override void Write(Utf8JsonWriter writer, MediaRef value, JsonSerializerOptions options)
    {
        if (value.Path is not null)
        {
            writer.WriteStringValue(value.Path);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("scheme", value.Scheme);
        writer.WriteString("id", value.Id);
        writer.WriteEndObject();
    }
}

internal sealed class AspectConverter : JsonConverter<Aspect>
{
    public override Aspect? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => new Aspect(reader.GetString()!),
        JsonTokenType.Number => new Aspect(reader.GetDouble()),
        _ => throw new JsonException("An aspect is \"W:H\" or a number."),
    };

    public override void Write(Utf8JsonWriter writer, Aspect value, JsonSerializerOptions options)
    {
        if (value.Text is not null)
        {
            writer.WriteStringValue(value.Text);
        }
        else
        {
            writer.WriteNumberValue(value.Number!.Value);
        }
    }
}

internal sealed class RectConverter : JsonConverter<Rect>
{
    public override Rect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = JsonSerializer.Deserialize<double[]>(ref reader, options);
        if (values is not { Length: 4 })
        {
            throw new JsonException("A rect is [x, y, width, height].");
        }

        return new Rect(values[0], values[1], values[2], values[3]);
    }

    public override void Write(Utf8JsonWriter writer, Rect value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Width);
        writer.WriteNumberValue(value.Height);
        writer.WriteEndArray();
    }
}

internal sealed class HoldTimeConverter : JsonConverter<HoldTime>
{
    public override HoldTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => new HoldTime(reader.GetDouble()),
        JsonTokenType.String when reader.GetString() == "input" => HoldTime.Input,
        _ => throw new JsonException("A hold lasts a number of seconds, or \"input\"."),
    };

    public override void Write(Utf8JsonWriter writer, HoldTime value, JsonSerializerOptions options)
    {
        if (value.Seconds is { } seconds)
        {
            writer.WriteNumberValue(seconds);
        }
        else
        {
            writer.WriteStringValue("input");
        }
    }
}

internal sealed class IntOrVarConverter : JsonConverter<IntOrVar>
{
    public override IntOrVar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new IntOrVar(WholeNumberConverter.ReadWhole(ref reader));
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("var", out var v) && v.ValueKind == JsonValueKind.String)
        {
            return new IntOrVar(v.GetString()!);
        }

        throw new JsonException("Expected an integer or {\"var\": name}.");
    }

    public override void Write(Utf8JsonWriter writer, IntOrVar value, JsonSerializerOptions options)
    {
        if (value.Var is null)
        {
            writer.WriteNumberValue(value.Value ?? 0);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("var", value.Var);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Integers as JSON numbers the way the spec (and JavaScript) reads them: 2 and 2.0 are both the
/// integer 2. System.Text.Json alone refuses "2.0" for an int.
/// </summary>
internal sealed class WholeNumberConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadWhole(ref reader);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);

    internal static int ReadWhole(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt32(out var i))
        {
            return i;
        }

        var d = reader.GetDouble();
        if (d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            return (int)d;
        }

        throw new JsonException($"Expected an integer, got {d.ToString(CultureInfo.InvariantCulture)}.");
    }
}
