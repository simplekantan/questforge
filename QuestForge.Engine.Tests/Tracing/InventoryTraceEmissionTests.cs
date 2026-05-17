using System.Text.Json;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Integration tests verifying that InventoryChangedEvent is emitted to the
/// trace stream at the right times by the key-item polling producer.
/// All 3 tests will fail until Builder implements the polling producer / recorder integration.
///
/// These tests target the producer that polls key items, calls KeyItemPollDiff.Diff,
/// and — when Changed == true — writes an InventoryChangedEvent to the TraceWriter.
/// The producer is expected to live in QuestForge.Engine.Authoring or a closely related
/// namespace, accepting an ITraceWriter (or TraceWriter) and a run-id string.
/// </summary>
public sealed class InventoryTraceEmissionTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 5, 17, 0, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // Helper: parse JSONL stream into a list of JsonDocuments
    // -------------------------------------------------------------------------

    private static List<JsonDocument> ParseJsonl(MemoryStream ms)
    {
        ms.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(ms, System.Text.Encoding.UTF8);
        var content = reader.ReadToEnd();
        return content
            .TrimEnd('\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Test 1: WrittenToTrace_WhenHashChanges
    // -------------------------------------------------------------------------

    [Fact]
    public void WrittenToTrace_WhenHashChanges()
    {
        /*
         * RED: Will fail until Builder implements the key-item polling producer.
         *
         * CONTRACT: Given a KeyItemInventoryPoller configured with a TraceWriter and runId="run-001",
         *           When Poll is called with previous map {} and current slots [(2000123, 1)],
         *           Then exactly one InventoryChangedEvent is written to the trace stream
         *                with type="inventory.changed", runId="run-001",
         *                and gained containing item 2000123.
         *
         * BUILDER GUIDANCE: KeyItemInventoryPoller.Poll(previous, currentSlots) should:
         *   1. Call KeyItemPollDiff.Diff(previous, currentSlots)
         *   2. If result.Changed: write InventoryChangedEvent to ITraceWriter
         *   3. Return the new map (NewMap from the diff result)
         *   The poller takes a TraceWriter (or ITraceWriter) and string runId at construction.
         */

        // Arrange
        var ms          = new MemoryStream();
        var traceWriter = new TraceWriter(ms, leaveOpen: true);
        var poller      = new KeyItemInventoryPoller(traceWriter, runId: "run-001");

        var previous    = new Dictionary<uint, int>();
        var currentSlots = new List<(uint id, int qty)> { (2000123u, 1) };

        // Act
        poller.Poll(previous, currentSlots);
        traceWriter.Dispose();

        // Assert — exactly one inventory.changed event in the stream
        var docs = ParseJsonl(ms);
        var invChangedDocs = docs
            .Where(d => d.RootElement.GetProperty("type").GetString() == "inventory.changed")
            .ToList();

        Assert.Single(invChangedDocs);
        var evt = invChangedDocs[0].RootElement;
        Assert.Equal("run-001", evt.GetProperty("runId").GetString());

        var gained = evt.GetProperty("gained").EnumerateArray().ToList();
        Assert.Single(gained);
        Assert.Equal(2000123u, gained[0].GetProperty("itemId").GetUInt32());
    }

    // -------------------------------------------------------------------------
    // Test 2: NotWritten_WhenHashUnchanged
    // -------------------------------------------------------------------------

    [Fact]
    public void NotWritten_WhenHashUnchanged()
    {
        /*
         * RED: Will fail until Builder implements the key-item polling producer.
         *
         * CONTRACT: Given a poller with a non-null previous map equal to the current snapshot,
         *           When Poll is called with the same contents,
         *           Then NO InventoryChangedEvent is written to the trace stream.
         *
         * BUILDER GUIDANCE: KeyItemPollDiff.Diff returns Changed=false when hashes match.
         *   The poller must check result.Changed before writing the event. If false, skip.
         */

        // Arrange
        var ms          = new MemoryStream();
        var traceWriter = new TraceWriter(ms, leaveOpen: true);
        var poller      = new KeyItemInventoryPoller(traceWriter, runId: "run-001");

        var previous     = new Dictionary<uint, int> { [2000100u] = 1 };
        var currentSlots = new List<(uint id, int qty)> { (2000100u, 1) };

        // Act
        poller.Poll(previous, currentSlots);
        traceWriter.Dispose();

        // Assert — no inventory.changed event in the stream
        var docs = ParseJsonl(ms);
        var invChangedDocs = docs
            .Where(d => d.RootElement.GetProperty("type").GetString() == "inventory.changed")
            .ToList();

        Assert.Empty(invChangedDocs);
    }

    // -------------------------------------------------------------------------
    // Test 3: NotWritten_WhenRunIdIsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void NotWritten_WhenRunIdIsNull()
    {
        /*
         * RED: Will fail until Builder implements the key-item polling producer.
         *
         * CONTRACT: Given a poller constructed with runId=null (no active run),
         *           When Poll is called with changed inventory,
         *           Then NO InventoryChangedEvent is written to the trace stream.
         *
         * BUILDER GUIDANCE: The poller must guard against null runId. If runId is null,
         *   the run has not started yet (or has ended). Skip trace emission to avoid
         *   orphaned events without a run.start parent.
         *   Guard: if (runId is null) return after computing NewMap (still track state).
         */

        // Arrange
        var ms          = new MemoryStream();
        var traceWriter = new TraceWriter(ms, leaveOpen: true);
        var poller      = new KeyItemInventoryPoller(traceWriter, runId: null);  // null = no active run

        var previous     = new Dictionary<uint, int>();
        var currentSlots = new List<(uint id, int qty)> { (2000123u, 1) };

        // Act
        poller.Poll(previous, currentSlots);
        traceWriter.Dispose();

        // Assert — no events at all in the stream
        var docs = ParseJsonl(ms);
        var invChangedDocs = docs
            .Where(d => d.RootElement.GetProperty("type").GetString() == "inventory.changed")
            .ToList();

        Assert.Empty(invChangedDocs);
    }
}
