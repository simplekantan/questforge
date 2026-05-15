using System.Text.Json;
using QuestForge.Adapters.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Tests for TraceEvent record value semantics and polymorphic JSON round-trip.
/// All tests will fail until Phase A scaffolding is in place (TraceEvent types, TraceEventJsonContext).
///
/// Covers §6.1 of PHASE_5_PLAN.md — 8 tests total.
/// </summary>
public sealed class TraceEventTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // Record equality — identical fields
    // -------------------------------------------------------------------------

    [Fact]
    public void RunStartEvent_IdenticalFields_AreEqual()
    {
        /*
         * RED: Will fail until TraceEvent hierarchy exists.
         *
         * CONTRACT: Given two RunStartEvent instances with identical RunId, QuestId,
         *           QuestSchemaId, and At fields,
         *           When compared with == and .Equals,
         *           Then they compare equal (C# record equality semantics).
         *
         * BUILDER GUIDANCE: sealed record RunStartEvent(string RunId, uint QuestId,
         *   uint QuestSchemaId, DateTimeOffset At) : TraceEvent("run.start", At)
         */

        // Arrange
        var a = new RunStartEvent("run-001", 66130u, 66130u, _now);
        var b = new RunStartEvent("run-001", 66130u, 66130u, _now);

        // Act + Assert
        Assert.Equal(a, b);
        Assert.True(a == b, "Record == should be true for identical fields");
        Assert.True(a.Equals(b), "Record .Equals should be true for identical fields");
    }

    // -------------------------------------------------------------------------
    // Record equality — differing field
    // -------------------------------------------------------------------------

    [Fact]
    public void RunStartEvent_DifferentRunId_AreNotEqual()
    {
        /*
         * RED: Will fail until TraceEvent hierarchy exists.
         *
         * CONTRACT: Given two RunStartEvent instances differing only in RunId,
         *           When compared,
         *           Then they are not equal.
         */

        // Arrange
        var a = new RunStartEvent("run-001", 66130u, 66130u, _now);
        var b = new RunStartEvent("run-002", 66130u, 66130u, _now);

        // Act + Assert
        Assert.NotEqual(a, b);
        Assert.False(a == b, "Record == should be false when RunId differs");
    }

    // -------------------------------------------------------------------------
    // Polymorphic serialization — ObservationEvent → JSON contains discriminator
    // -------------------------------------------------------------------------

    [Fact]
    public void ObservationEvent_Serialized_ContainsTypeDiscriminator()
    {
        /*
         * RED: Will fail until TraceEventJsonContext exists.
         *
         * CONTRACT: Given an ObservationEvent with Method = "GetPlayerZone",
         *           When serialized via TraceEventJsonContext.Default.TraceEvent,
         *           Then the JSON string contains "type":"observation" and
         *           "method":"GetPlayerZone".
         *
         * BUILDER GUIDANCE: Use [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
         *   and [JsonDerivedType(typeof(ObservationEvent), "observation")] on TraceEvent.
         *   Use camelCase naming policy so Method → "method".
         */

        // Arrange
        var value = JsonSerializer.SerializeToElement(182, new JsonSerializerOptions());
        var evt = new ObservationEvent(
            RunId: "abcd1234",
            Method: "GetPlayerZone",
            Argument: null,
            Value: value,
            At: _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"type\":\"observation\"", json,
            StringComparison.Ordinal);
        Assert.Contains("\"method\":\"GetPlayerZone\"", json,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Polymorphic deserialization — JSON → ObservationEvent
    // -------------------------------------------------------------------------

    [Fact]
    public void ObservationEvent_DeserializedFromJson_ProducesCorrectSubtype()
    {
        /*
         * RED: Will fail until TraceEventJsonContext and polymorphic JSON exist.
         *
         * CONTRACT: Given JSON with "type":"observation" and "method":"GetPlayerZone",
         *           When deserialized as TraceEvent (polymorphic),
         *           Then the result is an ObservationEvent with Method == "GetPlayerZone".
         *
         * BUILDER GUIDANCE: JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent)
         *   must dispatch on the "type" discriminator to the correct subtype.
         */

        // Arrange
        var at = _now.ToString("O");
        var json = $$"""
            {"type":"observation","runId":"abcd1234","method":"GetPlayerZone","argument":null,"value":182,"at":"{{at}}"}
            """;

        // Act
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        var obs = Assert.IsType<ObservationEvent>(result);
        Assert.Equal("GetPlayerZone", obs.Method);
    }

    // -------------------------------------------------------------------------
    // RunEndEvent outcome field in JSON
    // -------------------------------------------------------------------------

    [Fact]
    public void RunEndEvent_Serialized_ContainsOutcomeField()
    {
        /*
         * RED: Will fail until TraceEventJsonContext exists.
         *
         * CONTRACT: Given a RunEndEvent("rid", "done", now),
         *           When serialized via TraceEventJsonContext,
         *           Then the JSON contains "outcome":"done".
         */

        // Arrange
        var evt = new RunEndEvent("rid", "done", _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"outcome\":\"done\"", json, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Each of the 6 event types deserializes correctly
    // -------------------------------------------------------------------------

    [Fact]
    public void RunStartEvent_RoundTripJson_ProducesCorrectSubtype()
    {
        /*
         * RED: Will fail until serialization context is complete.
         *
         * CONTRACT: Serialize then deserialize RunStartEvent via TraceEvent context.
         *           Result is RunStartEvent with same RunId.
         */

        // Arrange
        var original = new RunStartEvent("run-abc", 66130u, 66130u, _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        var roundTripped = Assert.IsType<RunStartEvent>(result);
        Assert.Equal("run-abc", roundTripped.RunId);
        Assert.Equal(66130u, roundTripped.QuestId);
    }

    [Fact]
    public void DecisionEvent_RoundTripJson_ProducesCorrectSubtype()
    {
        /*
         * RED: Will fail until serialization context is complete.
         *
         * CONTRACT: Serialize then deserialize DecisionEvent via TraceEvent context.
         *           Result is DecisionEvent with same ActionType.
         */

        // Arrange
        var original = new DecisionEvent("run-001", "travel-to-wymond", "Navigate", _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        var roundTripped = Assert.IsType<DecisionEvent>(result);
        Assert.Equal("Navigate", roundTripped.ActionType);
        Assert.Equal("travel-to-wymond", roundTripped.StepId);
    }

    [Fact]
    public void RunEndEvent_RoundTripJson_ProducesCorrectSubtype()
    {
        /*
         * RED: Will fail until serialization context is complete.
         */

        var original = new RunEndEvent("run-001", "done", _now);
        var json = JsonSerializer.Serialize<TraceEvent>(original, TraceEventJsonContext.Default.TraceEvent);
        var result = JsonSerializer.Deserialize<TraceEvent>(json, TraceEventJsonContext.Default.TraceEvent);

        var roundTripped = Assert.IsType<RunEndEvent>(result);
        Assert.Equal("done", roundTripped.Outcome);
    }

    // -------------------------------------------------------------------------
    // DecisionEvent with null StepId serializes correctly
    // -------------------------------------------------------------------------

    [Fact]
    public void DecisionEvent_NullStepId_SerializesWithNullStepId()
    {
        /*
         * RED: Will fail until TraceEventJsonContext exists.
         *
         * CONTRACT: Given a DecisionEvent with StepId = null,
         *           When serialized,
         *           Then the JSON contains "stepId":null.
         *
         * BUILDER GUIDANCE: sealed record DecisionEvent(string RunId, string? StepId,
         *   string ActionType, DateTimeOffset At). The camelCase naming + nullable
         *   string must serialize as "stepId":null.
         */

        // Arrange
        var evt = new DecisionEvent("run-001", StepId: null, "AwaitUser", _now);

        // Act
        var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);

        // Assert
        Assert.Contains("\"stepId\":null", json, StringComparison.Ordinal);
    }
}