using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVideoMenu;

// The logic layer (Level 2, spec §6.1): values, player-state variables, conditions and set operations.
// Mirrors packages/core/src/logic.ts.

/// <summary>A variable's value: an integer or text.</summary>
[JsonConverter(typeof(ScalarConverter))]
public readonly record struct Scalar
{
    public Scalar(int number) => Number = number;

    public Scalar(string text) => Text = text;

    public int? Number { get; }

    public string? Text { get; }

    public bool IsText => Text is not null;

    public static implicit operator Scalar(int number) => new(number);

    public static implicit operator Scalar(string text) => new(text);

    public override string ToString() => Text ?? Number?.ToString(CultureInfo.InvariantCulture) ?? "0";
}

/// <summary>A literal value, or the current value of a variable (or "@" player state).</summary>
[JsonConverter(typeof(ValueConverter))]
public sealed record Value
{
    public Value(Scalar literal) => Literal = literal;

    public Value(string? var, bool isVar) => Var = var;

    public Scalar? Literal { get; }

    public string? Var { get; }

    public static implicit operator Value(int number) => new(new Scalar(number));

    public static implicit operator Value(string text) => new(new Scalar(text));

    public static Value Of(string var) => new(var, true);
}

/// <summary>A button: its id, or a variable holding one.</summary>
[JsonConverter(typeof(ButtonRefConverter))]
public sealed record ButtonRef
{
    public ButtonRef(string id) => Id = id;

    private ButtonRef(string var, bool isVar) => Var = var;

    public string? Id { get; }

    public string? Var { get; }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(id))]
    public static implicit operator ButtonRef?(string? id) => id is null ? null : new(id);

    public static ButtonRef FromVar(string var) => new(var, true);
}

/// <summary>A condition: one comparison of a variable, or all/any/not of other conditions.</summary>
[JsonConverter(typeof(TestConverter))]
public abstract record Test
{
    /// <summary>{var, op: value}: eq/ne for any value; lt/le/gt/ge for integers; has = any bit set.</summary>
    public sealed record Compare(string Var, string Op, Value Value) : Test;

    public sealed record All(IReadOnlyList<Test> Tests) : Test;

    public sealed record Any(IReadOnlyList<Test> Tests) : Test;

    public sealed record Not(Test Test) : Test;
}

/// <summary>Player-state variables, their types, whether set may change them, and their defaults.</summary>
public static class PlayerState
{
    public sealed record Variable(bool IsText, bool Writable, Scalar Default);

    public static IReadOnlyDictionary<string, Variable> Variables { get; } = new Dictionary<string, Variable>
    {
        ["@audio"] = new(false, true, 0),
        ["@subtitle"] = new(false, true, 0),
        ["@angle"] = new(false, true, 1),
        ["@focus"] = new(true, true, ""),
        ["@audioLanguage"] = new(true, false, ""),
        ["@subtitleLanguage"] = new(true, false, ""),
        ["@menuLanguage"] = new(true, false, ""),
        ["@lastMedia"] = new(true, false, ""),
        ["@lastChapter"] = new(false, false, 0),
        ["@region"] = new(false, false, 0),
        ["@parentalLevel"] = new(false, false, 8),
        ["@country"] = new(true, false, ""),
        ["@aspect"] = new(true, false, "16:9"),
    };

    public static readonly string[] TestOps = ["eq", "ne", "lt", "le", "gt", "ge", "has"];

    public static readonly string[] SetOps = ["set", "add", "sub", "mul", "div", "mod", "and", "or", "xor", "random"];
}

/// <summary>Evaluation, shared by the navigator.</summary>
internal static class Logic
{
    public static Scalar Read(IReadOnlyDictionary<string, Scalar> vars, IReadOnlyDictionary<string, Scalar> player, string? focus, string name)
    {
        if (name == "@focus")
        {
            return focus ?? "";
        }

        if (name.StartsWith('@'))
        {
            return player.TryGetValue(name, out var p) ? p : PlayerState.Variables.TryGetValue(name, out var v) ? v.Default : 0;
        }

        return vars.TryGetValue(name, out var x) ? x : 0;
    }

    /// <summary>The next random number 1..max: seed = (seed × 1103515245 + 12345) mod 2^31, result 1 + seed mod max.</summary>
    public static int NextRandom(ref long seed, int max)
    {
        seed = (seed * 1103515245L + 12345L) % 2147483648L;
        return 1 + (int)(seed % max);
    }

    /// <summary>The new value after set, or null when the operation leaves the variable unchanged.</summary>
    public static Scalar? Apply(ref long seed, Scalar current, string op, Scalar operand)
    {
        if (op == "set")
        {
            return operand;
        }

        if (current.Number is not { } a || operand.Number is not { } b)
        {
            return null;
        }

        return op switch
        {
            "add" => a + b,
            "sub" => a - b,
            "mul" => a * b,
            "div" => b == 0 ? null : a / b,
            "mod" => b == 0 ? null : a % b,
            "and" => a & b,
            "or" => a | b,
            "xor" => a ^ b,
            "random" => b < 1 ? null : NextRandom(ref seed, b),
            _ => null,
        };
    }

    public static bool Eval(Test test, Func<string, Scalar> read)
    {
        switch (test)
        {
            case Test.All all:
                return all.Tests.All(t => Eval(t, read));
            case Test.Any any:
                return any.Tests.Any(t => Eval(t, read));
            case Test.Not not:
                return !Eval(not.Test, read);
            case Test.Compare c:
                var left = read(c.Var);
                var right = c.Value.Var is { } v ? read(v) : c.Value.Literal!.Value;
                switch (c.Op)
                {
                    case "eq":
                        return left == right;
                    case "ne":
                        return left != right;
                    case "has":
                        return left.Number is { } l && right.Number is { } r && (l & r) != 0;
                    default:
                        if (left.Number is not { } a || right.Number is not { } b)
                        {
                            return false;
                        }

                        return c.Op switch { "lt" => a < b, "le" => a <= b, "gt" => a > b, _ => a >= b };
                }

            default:
                return false;
        }
    }
}

// --- JSON ------------------------------------------------------------------------------------------

internal sealed class ScalarConverter : JsonConverter<Scalar>
{
    public override Scalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => new Scalar(reader.GetString()!),
        JsonTokenType.Number => new Scalar(WholeNumberConverter.ReadWhole(ref reader)),
        _ => throw new JsonException("Expected an integer or text."),
    };

    public override void Write(Utf8JsonWriter writer, Scalar value, JsonSerializerOptions options)
    {
        if (value.Text is not null)
        {
            writer.WriteStringValue(value.Text);
        }
        else
        {
            writer.WriteNumberValue(value.Number ?? 0);
        }
    }
}

internal sealed class ValueConverter : JsonConverter<Value>
{
    public override Value? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return Value.Of(doc.RootElement.GetProperty("var").GetString()!);
        }

        return new Value(JsonSerializer.Deserialize<Scalar>(ref reader, options));
    }

    public override void Write(Utf8JsonWriter writer, Value value, JsonSerializerOptions options)
    {
        if (value.Var is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("var", value.Var);
            writer.WriteEndObject();
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Literal!.Value, options);
        }
    }
}

internal sealed class ButtonRefConverter : JsonConverter<ButtonRef>
{
    public override ButtonRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new ButtonRef(reader.GetString()!);
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        return ButtonRef.FromVar(doc.RootElement.GetProperty("var").GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, ButtonRef value, JsonSerializerOptions options)
    {
        if (value.Id is not null)
        {
            writer.WriteStringValue(value.Id);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("var", value.Var);
        writer.WriteEndObject();
    }
}

internal sealed class TestConverter : JsonConverter<Test>
{
    public override Test? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return From(doc.RootElement, options);
    }

    private static Test From(JsonElement e, JsonSerializerOptions options)
    {
        if (e.TryGetProperty("all", out var all))
        {
            return new Test.All(all.EnumerateArray().Select(x => From(x, options)).ToList());
        }

        if (e.TryGetProperty("any", out var any))
        {
            return new Test.Any(any.EnumerateArray().Select(x => From(x, options)).ToList());
        }

        if (e.TryGetProperty("not", out var not))
        {
            return new Test.Not(From(not, options));
        }

        var op = PlayerState.TestOps.First(o => e.TryGetProperty(o, out _));
        return new Test.Compare(e.GetProperty("var").GetString()!, op, e.GetProperty(op).Deserialize<Value>(options)!);
    }

    public override void Write(Utf8JsonWriter writer, Test value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case Test.Compare c:
                writer.WriteString("var", c.Var);
                writer.WritePropertyName(c.Op);
                JsonSerializer.Serialize(writer, c.Value, options);
                break;
            case Test.All a:
                writer.WritePropertyName("all");
                JsonSerializer.Serialize(writer, a.Tests, options);
                break;
            case Test.Any a:
                writer.WritePropertyName("any");
                JsonSerializer.Serialize(writer, a.Tests, options);
                break;
            case Test.Not n:
                writer.WritePropertyName("not");
                JsonSerializer.Serialize(writer, n.Test, options);
                break;
        }

        writer.WriteEndObject();
    }
}
