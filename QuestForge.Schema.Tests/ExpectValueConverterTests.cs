using System.Text.Encodings.Web;
using System.Text.Json;
using QuestForge.Schema;

namespace QuestForge.Schema.Tests;

public class ExpectValueConverterTests
{
    // UnsafeRelaxedJsonEscaping so quest predicates like ">=" are not escaped
    // to >= in the output files.
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new ExpectValueConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // -------------------------------------------------------------------------
    // Happy paths — read
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_StringToken_ReturnsPredicateExpect()
    {
        var result = JsonSerializer.Deserialize<ExpectValue>("\"questSequence(65) >= 3\"", Options);

        var predicate = Assert.IsType<PredicateExpect>(result);
        Assert.Equal("questSequence(65) >= 3", predicate.Predicate);
    }

    [Fact]
    public void Read_AllObject_ReturnsAllExpect()
    {
        var result = JsonSerializer.Deserialize<ExpectValue>(
            """{"all":["questFlag(65, 1)","questFlag(65, 2)"]}""", Options);

        var all = Assert.IsType<AllExpect>(result);
        Assert.Equal(["questFlag(65, 1)", "questFlag(65, 2)"], all.All);
    }

    [Fact]
    public void Read_AnyObject_ReturnsAnyExpect()
    {
        var result = JsonSerializer.Deserialize<ExpectValue>(
            """{"any":["questSequence(65) >= 3","questFlag(65, 5)"]}""", Options);

        var any = Assert.IsType<AnyExpect>(result);
        Assert.Equal(["questSequence(65) >= 3", "questFlag(65, 5)"], any.Any);
    }

    [Fact]
    public void Read_NullToken_ReturnsNull()
    {
        var result = JsonSerializer.Deserialize<ExpectValue>("null", Options);

        Assert.Null(result);
    }

    [Fact]
    public void Read_EmptyAllArray_ReturnsAllExpectWithEmptyArray()
    {
        var result = JsonSerializer.Deserialize<ExpectValue>("""{"all":[]}""", Options);

        var all = Assert.IsType<AllExpect>(result);
        Assert.Empty(all.All);
    }

    // -------------------------------------------------------------------------
    // Error cases — read
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_UnknownObjectKey_ThrowsJsonException()
    {
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ExpectValue>("""{"foo":["x"]}""", Options));

        Assert.Contains("all", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ObjectWithBothAllAndAny_ThrowsJsonException()
    {
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ExpectValue>("""{"all":["a"],"any":["b"]}""", Options));

        Assert.Contains("exactly one key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ArrayToken_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ExpectValue>("""["questFlag(65, 1)"]""", Options));
    }

    [Fact]
    public void Read_EmptyObject_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ExpectValue>("""{}""", Options));
    }

    [Fact]
    public void Read_AllArrayWithNonStringElement_ThrowsJsonException()
    {
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ExpectValue>("""{"all":[42,"questFlag(65, 1)"]}""", Options));

        Assert.Contains("string", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_NumberToken_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ExpectValue>("42", Options));
    }

    [Fact]
    public void Read_BoolToken_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ExpectValue>("true", Options));
    }

    // -------------------------------------------------------------------------
    // Happy paths — write (round-trip)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_PredicateExpect_SerializesAsString()
    {
        var value = new PredicateExpect { Predicate = "questSequence(65) >= 3" };

        var json = JsonSerializer.Serialize<ExpectValue>(value, Options);
        using var doc = JsonDocument.Parse(json);

        // Compare semantic value, not raw JSON (avoids escaping differences)
        Assert.Equal(JsonValueKind.String, doc.RootElement.ValueKind);
        Assert.Equal("questSequence(65) >= 3", doc.RootElement.GetString());
    }

    [Fact]
    public void Write_AllExpect_SerializesAsAllObject()
    {
        var value = new AllExpect { All = ["questFlag(65, 1)", "questFlag(65, 2)"] };

        var json = JsonSerializer.Serialize<ExpectValue>(value, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("all", out var arr));
        Assert.Equal(2, arr.GetArrayLength());
    }

    [Fact]
    public void Write_AnyExpect_SerializesAsAnyObject()
    {
        var value = new AnyExpect { Any = ["questSequence(65) >= 3"] };

        var json = JsonSerializer.Serialize<ExpectValue>(value, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("any", out _));
    }

    // -------------------------------------------------------------------------
    // Round-trip: deserialize then re-serialize produces equivalent JSON
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("\"questSequence(65) >= 3\"")]
    [InlineData("""{"all":["questFlag(65, 1)","questFlag(65, 2)"]}""")]
    [InlineData("""{"any":["questSequence(65) >= 3","questFlag(65, 5)"]}""")]
    public void RoundTrip_ProducesEquivalentJson(string originalJson)
    {
        var value = JsonSerializer.Deserialize<ExpectValue>(originalJson, Options);
        var roundTripped = JsonSerializer.Serialize(value, Options);

        using var orig = JsonDocument.Parse(originalJson);
        using var rt   = JsonDocument.Parse(roundTripped);

        Assert.Equal(orig.RootElement.ToString(), rt.RootElement.ToString());
    }
}