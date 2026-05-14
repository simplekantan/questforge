using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Schema;

/// <summary>
/// Represents the value of an <c>expect</c> or <c>skipIf</c> field.
/// JSON forms: a bare predicate string, <c>{"all":[...]}</c>, or <c>{"any":[...]}</c>.
/// </summary>
[JsonConverter(typeof(ExpectValueConverter))]
public abstract class ExpectValue { }

/// <summary>A single predicate string, e.g. <c>"questSequence(65) >= 3"</c>.</summary>
public sealed class PredicateExpect : ExpectValue
{
    public string Predicate { get; init; } = default!;
}

/// <summary>All predicates must be true.</summary>
public sealed class AllExpect : ExpectValue
{
    public string[] All { get; init; } = [];
}

/// <summary>Any predicate being true is sufficient.</summary>
public sealed class AnyExpect : ExpectValue
{
    public string[] Any { get; init; } = [];
}

/// <summary>
/// Reads/writes <see cref="ExpectValue"/> polymorphically without <c>[JsonPolymorphic]</c>.
/// String token → <see cref="PredicateExpect"/>.
/// Object with "all" → <see cref="AllExpect"/>.
/// Object with "any" → <see cref="AnyExpect"/>.
/// </summary>
public sealed class ExpectValueConverter : JsonConverter<ExpectValue>
{
    public override ExpectValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null    => null,
            JsonTokenType.String  => new PredicateExpect { Predicate = reader.GetString()! },
            JsonTokenType.StartObject => ReadObject(ref reader),
            _ => throw new JsonException(
                $"'expect'/'skipIf' must be a string, {{\"all\":[...]}}, or {{\"any\":[...]}}, got {reader.TokenType}.")
        };
    }

    private static ExpectValue ReadObject(ref Utf8JsonReader reader)
    {
        string? key = null;
        List<string>? values = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name inside 'expect' object.");

            var currentKey = reader.GetString()!;

            if (key != null)
                throw new JsonException(
                    $"'expect' object must have exactly one key ('all' or 'any'); found both '{key}' and '{currentKey}'.");

            if (currentKey != "all" && currentKey != "any")
                throw new JsonException(
                    $"'expect' object key must be 'all' or 'any'; got '{currentKey}'.");

            key = currentKey;
            reader.Read();

            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected array for '{key}' in 'expect' object.");

            values = [];
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException($"Elements of '{key}' array in 'expect' must be strings.");
                values.Add(reader.GetString()!);
            }
        }

        if (key == null)
            throw new JsonException("'expect' object must have an 'all' or 'any' key; got empty object.");

        return key == "all"
            ? new AllExpect  { All = [.. values!] }
            : new AnyExpect  { Any = [.. values!] };
    }

    public override void Write(Utf8JsonWriter writer, ExpectValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case PredicateExpect p:
                writer.WriteStringValue(p.Predicate);
                break;

            case AllExpect a:
                writer.WriteStartObject();
                writer.WritePropertyName("all");
                writer.WriteStartArray();
                foreach (var s in a.All) writer.WriteStringValue(s);
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            case AnyExpect a:
                writer.WriteStartObject();
                writer.WritePropertyName("any");
                writer.WriteStartArray();
                foreach (var s in a.Any) writer.WriteStringValue(s);
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            default:
                throw new JsonException($"Unknown ExpectValue subtype: {value.GetType().Name}");
        }
    }
}