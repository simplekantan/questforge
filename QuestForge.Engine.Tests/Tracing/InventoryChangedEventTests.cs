using System.Text.Json;
using QuestForge.Adapters.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Tests for InventoryChangedEvent record value semantics and polymorphic
/// JSON round-trip. All tests will fail until Builder implements InventoryChangedEvent
/// and registers it in TraceEventJsonContext.
///
/// Covers the inventory.changed trace event specification — 7 tests total.
/// </summary>
public sealed class InventoryChangedEventTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 5, 17, 0, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // Record equality — identical fields
    // -------------------------------------------------------------------------

    [Fact]
    public void IdenticalFields_AreEqual()
    {
        /*
         * RED: Will fail until InventoryChangedEvent exists.
         *
         * CONTRACT: Given two InventoryChangedEvent instances with identical RunId,
         *           Gained, Lost, NewHash, and At fields,
         *           When compared with == and .Equals,
         *           Then they compare equal (C# record equality semantics).
         *
         * BUILDER GUIDANCE: sealed record InventoryChangedEvent(
         *   string RunId,
         *   IReadOnlyList<KeyItemDelta> Gained,
         *   IReadOnlyList<KeyItemDelta> Lost,
         *   uint NewHash,
         *   DateTimeOffset At) : TraceEvent(At)
         *   with [JsonIgnore] public override string Type => "inventory.changed";
         */

        // Arrange
        var gained = new List<KeyItemDelta> { new KeyItemDelta(2000123u, 1) };
        var lost   = new List<KeyItemDelta>();

        var a = new InventoryChangedEvent("run-001", gained, lost, 0xDEADBEEFu, _now);
        var b = new InventoryChangedEvent("run-001", gained, lost, 0xDEADBEEFu, _now);

        // Act + Assert
        Assert.Equal(a, b);
        Assert.True(a == b, "Record == should be true for identical fields");
        Assert.True(a.Equals(b), "Record .Equals should be true for identical fields");
    }

    // -------------------------------------------------------------------------
    // Polymorphic serialization — type discriminator is dotted "inventory.changed"
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialized_ContainsTypeDiscriminator()
    {
        /*
         * RED: Will fail until InventoryChangedEvent is registered in TraceEventJsonContext.
         *
         * CONTRACT: Given an InventoryChangedEvent,
         *           When serialized via TraceEventJsonContext.Default.TraceEvent,
         *           Then the JSON contains "type":"inventory.changed" (dotted, not underscore).
         *
         * BUILDER GUIDANCE: Register on TraceEvent:
         *   [JsonDerivedType(typeof(InventoryChangedEvent), "inventory.changed")]
         *   The discriminator must use a dot separator to match the existing "run.start",
         *   "action.submitted" convention.
         */

        // Arrange
        var evt = new InventoryChangedEvent(
            RunId:   "run-001",
            Gained:  new List<KeyItemDelta> { new KeyItemDelta(2000123u, 1) },
            Lost:    new List<KeyItemDelta>(),
            NewHash: 0xDEADBEEFu,
            At:      _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"inventory.changed\"", json, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Serialized field names — camelCase itemId and qty
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialized_ContainsGainedAndLostArrays()
    {
        /*
         * RED: Will fail until InventoryChangedEvent is registered in TraceEventJsonContext.
         *
         * CONTRACT: Given an InventoryChangedEvent with one gained and one lost delta,
         *           When serialized via TraceEventJsonContext.Default.TraceEvent,
         *           Then the JSON contains "gained" and "lost" arrays with camelCase
         *           fields "itemId" and "qty".
         *
         * BUILDER GUIDANCE: sealed record KeyItemDelta(uint ItemId, int Qty);
         *   The source-generated context uses JsonKnownNamingPolicy.CamelCase, so
         *   ItemId → "itemId" and Qty → "qty".
         */

        // Arrange
        var evt = new InventoryChangedEvent(
            RunId:   "run-001",
            Gained:  new List<KeyItemDelta> { new KeyItemDelta(2000123u, 1) },
            Lost:    new List<KeyItemDelta> { new KeyItemDelta(2000456u, 1) },
            NewHash: 0u,
            At:      _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"gained\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lost\"",   json, StringComparison.Ordinal);
        Assert.Contains("\"itemId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"qty\"",    json, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Polymorphic deserialization — JSON → InventoryChangedEvent
    // -------------------------------------------------------------------------

    [Fact]
    public void DeserializedFromJson_ProducesCorrectSubtype()
    {
        /*
         * RED: Will fail until TraceEventJsonContext registers InventoryChangedEvent.
         *
         * CONTRACT: Given JSON with "type":"inventory.changed",
         *           When deserialized as TraceEvent (polymorphic),
         *           Then the result is an InventoryChangedEvent with correct fields.
         *
         * BUILDER GUIDANCE: JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent)
         *   must dispatch on "inventory.changed" to InventoryChangedEvent.
         */

        // Arrange
        var at  = _now.ToString("O");
        var json = $$"""
            {"type":"inventory.changed","runId":"run-001","gained":[{"itemId":2000123,"qty":1}],"lost":[],"newHash":3735928559,"at":"{{at}}"}
            """;

        // Act
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        var evt = Assert.IsType<InventoryChangedEvent>(result);
        Assert.Equal("run-001", evt.RunId);
        Assert.Single(evt.Gained);
        Assert.Equal(2000123u, evt.Gained[0].ItemId);
        Assert.Equal(1, evt.Gained[0].Qty);
        Assert.Empty(evt.Lost);
    }

    // -------------------------------------------------------------------------
    // Round-trip JSON — all fields preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void RoundTripJson_PreservesAllFields()
    {
        /*
         * RED: Will fail until serialization context is complete.
         *
         * CONTRACT: Serialize then deserialize an InventoryChangedEvent via TraceEvent context.
         *           All fields (RunId, Gained, Lost, NewHash, At) survive the round-trip.
         *
         * BUILDER GUIDANCE: Confirm every field has a matching JSON property name under
         *   camelCase naming. NewHash → "newHash".
         */

        // Arrange
        var original = new InventoryChangedEvent(
            RunId:   "run-abc",
            Gained:  new List<KeyItemDelta> { new KeyItemDelta(2000100u, 2) },
            Lost:    new List<KeyItemDelta> { new KeyItemDelta(2000200u, 1) },
            NewHash: 0x12345678u,
            At:      _now);

        // Act
        var json   = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        var roundTripped = Assert.IsType<InventoryChangedEvent>(result);
        Assert.Equal("run-abc", roundTripped.RunId);
        Assert.Equal(0x12345678u, roundTripped.NewHash);
        Assert.Equal(_now, roundTripped.At);
        Assert.Single(roundTripped.Gained);
        Assert.Equal(2000100u, roundTripped.Gained[0].ItemId);
        Assert.Equal(2, roundTripped.Gained[0].Qty);
        Assert.Single(roundTripped.Lost);
        Assert.Equal(2000200u, roundTripped.Lost[0].ItemId);
        Assert.Equal(1, roundTripped.Lost[0].Qty);
    }

    // -------------------------------------------------------------------------
    // Empty Gained array — serializes as [] not omitted
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyGained_SerializesEmptyArray()
    {
        /*
         * RED: Will fail until InventoryChangedEvent is implemented.
         *
         * CONTRACT: Given an InventoryChangedEvent where Gained is empty,
         *           When serialized,
         *           Then the JSON contains "gained":[] (not omitted).
         *
         * BUILDER GUIDANCE: Do NOT annotate Gained with [JsonIgnore(Condition=WhenWritingDefault)]
         *   or similar — the array must always be present in the JSON output for
         *   consumers that depend on the property existing.
         */

        // Arrange
        var evt = new InventoryChangedEvent(
            RunId:   "run-001",
            Gained:  new List<KeyItemDelta>(),
            Lost:    new List<KeyItemDelta> { new KeyItemDelta(2000456u, 1) },
            NewHash: 0u,
            At:      _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"gained\":[]", json, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // NewHash — serializes as a JSON number, not a string
    // -------------------------------------------------------------------------

    [Fact]
    public void NewHash_SerializesAsNumber()
    {
        /*
         * RED: Will fail until InventoryChangedEvent is implemented.
         *
         * CONTRACT: Given NewHash = 2166136261 (0x811C9DC5 — FNV offset basis),
         *           When serialized,
         *           Then the JSON contains "newHash":2166136261 as a numeric literal
         *           (no surrounding quotes).
         *
         * BUILDER GUIDANCE: uint serializes as a JSON number by default with STJ.
         *   Do NOT annotate with any string-converter attribute.
         *   2166136261 is 0x811C9DC5 represented as unsigned decimal.
         */

        // Arrange
        var evt = new InventoryChangedEvent(
            RunId:   "run-001",
            Gained:  new List<KeyItemDelta>(),
            Lost:    new List<KeyItemDelta>(),
            NewHash: 2166136261u, // 0x811C9DC5
            At:      _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"newHash\":2166136261", json, StringComparison.Ordinal);
    }
}
