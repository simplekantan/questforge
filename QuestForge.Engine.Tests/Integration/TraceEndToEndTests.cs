using System.Text;
using System.Text.Json;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Adapters.Tracing;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Integration;

/// <summary>
/// RED PHASE: End-to-end tests for quest 66130 with a real TraceWriter over a MemoryStream.
/// All tests fail until the complete Phase 5 implementation is in place:
///   - TraceWriter (writes JSONL to stream)
///   - RecordingGameStateProvider/RecordingQuestState (emit observations)
///   - QuestEngine.BeginRun + trace emissions (run.start, decision, run.end)
///   - EngineTestHarness updated to emit action.submitted / action.completed
///
/// Covers §6.5 of PHASE_5_PLAN.md — 3 tests total.
///
/// NOTE: These tests require a specialized harness variant that accepts an external
///   TraceWriter (not the default FakeTraceWriter). See TraceEndToEndHarness below.
/// </summary>
public sealed class TraceEndToEndTests
{
    private static QuestDefinition LoadFixture()
    {
        var json = File.ReadAllText("Fixtures/66130.json");
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions)
               ?? throw new InvalidOperationException("Failed to deserialize fixture 66130.json");
    }

    /// <summary>
    /// Sets up and runs the full quest 66130 flow, writing all trace events to the provided MemoryStream.
    /// Returns the stream positioned at the beginning, ready to read.
    /// </summary>
    private static async Task<MemoryStream> RunFullQuest66130WithTraceWriter()
    {
        var ms = new MemoryStream();
        var traceWriter = new TraceWriter(ms, leaveOpen: true);

        // Use an EngineTestHarness variant that accepts an external TraceWriter.
        // The harness wires recording proxies and emits action.submitted/action.completed.
        var harness = new EngineTestHarness(traceWriter);

        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Accepted);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        // Wire quest state advancement callbacks
        harness.Interactor.OnInteractWith(new NpcId(1003987), () =>
            harness.QuestState.SetQuestSequence(new QuestId(66130), 255));

        harness.Interactor.OnInteractWith(new NpcId(1003988), () =>
            harness.QuestState.SetQuestStatus(new QuestId(66130), QuestStatus.Complete));

        harness.Engine.StartQuest(LoadFixture());
        harness.Engine.BeginRun("e2e-run-001");

        await harness.RunToCompletion(maxTicks: 10);

        // Flush and position stream for reading
        traceWriter.Dispose();
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    // -------------------------------------------------------------------------
    // Test 1: Complete trace shape — correct event counts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FullFlow_TraceShape_CorrectEventTypeCounts()
    {
        /*
         * RED: Will fail until full Phase 5 implementation is in place.
         *
         * CONTRACT: Given the full quest 66130 run with a real TraceWriter over MemoryStream,
         *           When the trace is parsed line by line,
         *           Then the trace contains:
         *             - exactly 1 event with type == "run.start"
         *             - exactly 4 events with type == "decision" (navigate, interact, navigate, interact)
         *             - exactly 4 events with type == "action.submitted"
         *             - exactly 4 events with type == "action.completed"
         *             - at least 1 event with type == "observation"
         *             - exactly 1 event with type == "run.end" with outcome == "done"
         *             - run.end is the LAST line
         *
         * BUILDER GUIDANCE:
         *   - Engine emits: run.start (1), decision per tick (4 action ticks), run.end (1)
         *   - Harness emits: action.submitted (4), action.completed (4)
         *   - Recording proxies emit: >=1 observation per engine tick
         *   For quest 66130: 4 action-producing ticks + final Done tick.
         */

        // Act
        using var ms = await RunFullQuest66130WithTraceWriter();

        // Parse all lines
        var content = new StreamReader(ms, Encoding.UTF8).ReadToEnd();
        var lines = content.TrimEnd('\n').Split('\n');
        var docs = lines.Select(l => JsonDocument.Parse(l)).ToList();

        // Count by type
        int CountType(string type) =>
            docs.Count(d => d.RootElement.GetProperty("type").GetString() == type);

        // Assert
        Assert.Equal(1, CountType("run.start")); // Expected exactly 1 run.start event
        Assert.Equal(4, CountType("decision")); // Expected exactly 4 decision events (one per action-producing tick)
        Assert.Equal(4, CountType("action.submitted")); // Expected exactly 4 action.submitted events
        Assert.Equal(4, CountType("action.completed")); // Expected exactly 4 action.completed events
        Assert.True(CountType("observation") >= 1,
            "Expected at least 1 observation event");
        Assert.Equal(1, CountType("run.end")); // Expected exactly 1 run.end event

        // run.end outcome must be "done"
        var runEnd = docs
            .Where(d => d.RootElement.GetProperty("type").GetString() == "run.end")
            .Single();
        Assert.Equal("done", runEnd.RootElement.GetProperty("data").GetProperty("outcome").GetString());

        // run.end must be the last line
        var lastDoc = docs[docs.Count - 1];
        Assert.Equal("run.end", lastDoc.RootElement.GetProperty("type").GetString()); // The last line must be run.end
    }

    // -------------------------------------------------------------------------
    // Test 2: Ordering invariant — run.start first, run.end last, decision before submitted/completed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FullFlow_EventOrdering_RunStartFirstRunEndLastDecisionBeforeAction()
    {
        /*
         * RED: Will fail until full Phase 5 implementation is in place.
         *
         * CONTRACT: Given the full quest 66130 trace,
         *           Then:
         *             1. run.start is line index 0
         *             2. run.end is the last line
         *             3. For each action: decision appears before action.submitted,
         *                which appears before action.completed.
         *
         * BUILDER GUIDANCE: The ordering is enforced by the structure of Tick + RunToCompletion:
         *   engine emits decision, then harness emits action.submitted + action.completed.
         */

        // Act
        using var ms = await RunFullQuest66130WithTraceWriter();
        var content = new StreamReader(ms, Encoding.UTF8).ReadToEnd();
        var lines = content.TrimEnd('\n').Split('\n');
        var docs = lines.Select(l => JsonDocument.Parse(l)).ToArray();

        // Assert — run.start at index 0
        Assert.Equal("run.start",
            docs[0].RootElement.GetProperty("type").GetString()); // run.start must be the first line (index 0)

        // Assert — run.end at last index
        Assert.Equal("run.end",
            docs[docs.Length - 1].RootElement.GetProperty("type").GetString()); // run.end must be the last line

        // Assert — for each action.submitted, a decision precedes it
        // and an action.completed follows it
        for (var i = 0; i < docs.Length; i++)
        {
            var type = docs[i].RootElement.GetProperty("type").GetString();
            if (type != "action.submitted") continue;

            // Find the decision before this action.submitted
            var decisionIndex = -1;
            for (var j = i - 1; j >= 0; j--)
            {
                if (docs[j].RootElement.GetProperty("type").GetString() == "decision")
                {
                    decisionIndex = j;
                    break;
                }
            }
            Assert.True(decisionIndex >= 0 && decisionIndex < i,
                $"action.submitted at line {i} must be preceded by a decision event");

            // Find action.completed after this action.submitted
            var completedIndex = -1;
            for (var j = i + 1; j < docs.Length; j++)
            {
                if (docs[j].RootElement.GetProperty("type").GetString() == "action.completed")
                {
                    completedIndex = j;
                    break;
                }
            }
            Assert.True(completedIndex > i,
                $"action.submitted at line {i} must be followed by action.completed");
        }
    }

    // -------------------------------------------------------------------------
    // Test 3: Valid JSONL — every line valid JSON, no embedded newlines, ends with '\n'
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FullFlow_OutputIsValidJsonl()
    {
        /*
         * RED: Will fail until TraceWriter is implemented with WriteIndented=false.
         *
         * CONTRACT: Given the full quest 66130 trace written to MemoryStream,
         *           Then:
         *             1. Every line is valid JSON (no JsonException when parsing)
         *             2. No line contains an embedded '\n'
         *             3. The last character in the stream is '\n'
         *
         * BUILDER GUIDANCE: TraceWriter.Write uses WriteIndented = false (from TraceEventJsonContext)
         *   and appends exactly '\n' after each event.
         */

        // Act
        using var ms = await RunFullQuest66130WithTraceWriter();
        var rawBytes = ms.ToArray();

        // Assert — last character is '\n'
        Assert.True(rawBytes.Length > 0, "Stream must be non-empty");
        Assert.Equal((byte)'\n', rawBytes[rawBytes.Length - 1]); // The last byte in the stream must be '\n'

        // Assert — every line is valid JSON with no embedded newlines
        ms.Seek(0, SeekOrigin.Begin);
        var content = new StreamReader(ms, Encoding.UTF8).ReadToEnd();
        var lines = content.TrimEnd('\n').Split('\n');

        Assert.True(lines.Length > 0, "Must have at least one line");
        foreach (var line in lines)
        {
            // No embedded newlines (they were already split, so this checks for \r)
            Assert.DoesNotContain('\r', line); // Line must not contain carriage return

            // Every line must be valid JSON — JsonDocument.Parse throws JsonException on invalid input
            var doc = JsonDocument.Parse(line); // throws on invalid JSON
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object,
                $"Each line must be a JSON object, got: {doc.RootElement.ValueKind}");
        }
    }
}