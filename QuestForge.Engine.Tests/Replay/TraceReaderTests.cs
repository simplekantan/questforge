using System.Text.Json;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: Tests for TraceReader.ReadFile — the JSONL trace deserializer.
/// All tests will fail until Builder implements TraceReader in QuestForge.Adapters.Fakes.Replay.
///
/// Covers §8.1 of PHASE_7_PLAN.md — 5 scenarios.
/// </summary>
public sealed class TraceReaderTests
{
    private static readonly DateTimeOffset _at = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _atStr = _at.ToString("O");

    // -------------------------------------------------------------------------
    // §8.1 Happy path — 7 events in document order
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadFile_SevenEventTrace_ReturnsAllEventsInDocumentOrder()
    {
        /*
         * RED: Will fail until TraceReader is implemented.
         *
         * CONTRACT: Given a JSONL file with 7 events
         *   (1 run.start, 4 observation, 1 decision, 1 run.end),
         *   each on its own line, terminated by '\n',
         *   When TraceReader.ReadFile(path) is called,
         *   Then it returns a list of 7 TraceEvent instances in document order,
         *   with the correct subtypes matching each "type" discriminator.
         *
         * BUILDER GUIDANCE: deserialize each non-empty line via
         *   JsonSerializer.Deserialize<TraceEvent>(line, TraceEventJsonContext.Default.TraceEvent).
         *   The polymorphic discriminator "type" drives subtype selection.
         */

        // Arrange
        var lines = new[]
        {
            $$$"""{"type":"run.start","runId":"run-001","questId":66130,"questSchemaId":66130,"at":"{{{_atStr}}}"}""",
            $$$"""{"type":"observation","runId":"run-001","method":"GetPlayerZone","argument":null,"value":{"value":182},"at":"{{{_atStr}}}"}""",
            $$$"""{"type":"observation","runId":"run-001","method":"GetPlayerPosition","argument":null,"value":{"x":0.0,"y":0.0,"z":0.0},"at":"{{{_atStr}}}"}""",
            $$$"""{"type":"observation","runId":"run-001","method":"IsQuestComplete","argument":{"value":66130},"value":false,"at":"{{{_atStr}}}"}""",
            $$$"""{"type":"observation","runId":"run-001","method":"GetQuestSequence","argument":{"value":66130},"value":0,"at":"{{{_atStr}}}"}""",
            $$$"""{"type":"decision","runId":"run-001","stepId":"travel-to-wymond","actionType":"Navigate","at":"{{{_atStr}}}"}""",
            $$$"""{"type":"run.end","runId":"run-001","outcome":"done","at":"{{{_atStr}}}"}""",
        };
        var path = WriteTempFile(lines);

        // Act
        var events = TraceReader.ReadFile(path);

        // Assert — count
        Assert.Equal(7, events.Count);

        // Assert — subtypes in document order
        Assert.IsType<RunStartEvent>(events[0]);
        Assert.IsType<ObservationEvent>(events[1]);
        Assert.IsType<ObservationEvent>(events[2]);
        Assert.IsType<ObservationEvent>(events[3]);
        Assert.IsType<ObservationEvent>(events[4]);
        Assert.IsType<DecisionEvent>(events[5]);
        Assert.IsType<RunEndEvent>(events[6]);

        // Assert — field spot-checks
        Assert.Equal("run-001", ((RunStartEvent)events[0]).RunId);
        Assert.Equal(66130u, ((RunStartEvent)events[0]).QuestId);
        Assert.Equal("GetPlayerZone", ((ObservationEvent)events[1]).Method);
        Assert.Equal("Navigate", ((DecisionEvent)events[5]).ActionType);
        Assert.Equal("travel-to-wymond", ((DecisionEvent)events[5]).StepId);
        Assert.Equal("done", ((RunEndEvent)events[6]).Outcome);
    }

    // -------------------------------------------------------------------------
    // §8.1 Edge — empty file returns empty list, no exception
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadFile_EmptyFile_ReturnsEmptyList()
    {
        /*
         * RED: Will fail until TraceReader is implemented.
         *
         * CONTRACT: Given a file with 0 bytes,
         *   When ReadFile is called,
         *   Then it returns an empty list. No exception is thrown.
         *
         * BUILDER GUIDANCE: iterate File.ReadLines; no lines → no events added.
         */

        // Arrange
        var path = WriteTempFile(Array.Empty<string>());

        // Act
        var events = TraceReader.ReadFile(path);

        // Assert
        Assert.Empty(events);
    }

    // -------------------------------------------------------------------------
    // §8.1 Edge — trailing empty line is silently skipped
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadFile_TrailingEmptyLine_SkippedSilently()
    {
        /*
         * RED: Will fail until TraceReader is implemented.
         *
         * CONTRACT: Given a valid trace ending with '\n\n' (one extra blank line),
         *   When ReadFile is called,
         *   Then the trailing empty line is skipped. List length matches the event count.
         *
         * BUILDER GUIDANCE: use string.IsNullOrWhiteSpace(line) to skip blank lines.
         */

        // Arrange — two events followed by a blank line
        var lines = new[]
        {
            $$$"""{"type":"run.start","runId":"run-002","questId":66130,"questSchemaId":66130,"at":"{{{_atStr}}}"}""",
            $$$"""{"type":"run.end","runId":"run-002","outcome":"done","at":"{{{_atStr}}}"}""",
            "",   // trailing blank line
        };
        var path = WriteTempFile(lines);

        // Act
        var events = TraceReader.ReadFile(path);

        // Assert — blank line does not produce an extra (null) event
        Assert.Equal(2, events.Count);
        Assert.IsType<RunStartEvent>(events[0]);
        Assert.IsType<RunEndEvent>(events[1]);
    }

    // -------------------------------------------------------------------------
    // §8.1 Error — malformed JSON line throws JsonException
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadFile_MalformedJsonLine_ThrowsJsonException()
    {
        /*
         * RED: Will fail until TraceReader is implemented.
         *
         * CONTRACT: Given a file where line 3 is '{not valid json',
         *   When ReadFile is called,
         *   Then JsonException (or a wrapping exception containing JsonException) is thrown.
         *   Replay must not silently swallow parse errors.
         *
         * BUILDER GUIDANCE: do NOT catch JsonException inside ReadFile.
         *   Let it propagate so callers know the fixture is corrupt.
         */

        // Arrange
        var lines = new[]
        {
            $$$"""{"type":"run.start","runId":"run-003","questId":66130,"questSchemaId":66130,"at":"{{{_atStr}}}"}""",
            $$$"""{"type":"observation","runId":"run-003","method":"GetPlayerZone","argument":null,"value":{"value":182},"at":"{{{_atStr}}}"}""",
            "{not valid json",
        };
        var path = WriteTempFile(lines);

        // Act + Assert
        Assert.ThrowsAny<JsonException>(() => TraceReader.ReadFile(path));
    }

    // -------------------------------------------------------------------------
    // §8.1 Error — unknown discriminator value throws (STJ polymorphic rejection)
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadFile_UnknownDiscriminator_ThrowsJsonException()
    {
        /*
         * RED: Will fail until TraceReader is implemented.
         *
         * CONTRACT: Given a line {"type":"unknown.event","at":"2026-05-15T..."},
         *   When ReadFile is called,
         *   Then JsonException from TraceEventJsonContext polymorphic deserialization
         *   is thrown (System.Text.Json rejects unknown discriminators by default).
         *
         * BUILDER GUIDANCE: TraceEventJsonContext uses [JsonPolymorphic] which throws
         *   JsonException on unregistered discriminator values. Do not catch it.
         */

        // Arrange
        var lines = new[]
        {
            $$$"""{"type":"unknown.event","runId":"run-004","at":"{{{_atStr}}}"}""",
        };
        var path = WriteTempFile(lines);

        // Act + Assert
        Assert.ThrowsAny<JsonException>(() => TraceReader.ReadFile(path));
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes lines to a temp file (one per array entry, joined by newline) and returns the path.
    /// Caller does not need to clean up — temp files are cleaned by the OS.
    /// </summary>
    private static string WriteTempFile(IEnumerable<string> lines)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, string.Join("\n", lines));
        return path;
    }
}