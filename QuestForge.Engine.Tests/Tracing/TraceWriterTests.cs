using System.Text;
using System.Text.Json;
using QuestForge.Adapters.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Tests for TraceWriter with the new event shapes (envelope fields, nested Data).
/// Covers TS21 from TRACE_SPEC_COMPLIANCE_PLAN.md, plus basic write correctness tests
/// that exercise the new type shapes.
///
/// All tests WILL FAIL because the new TraceEvent types do not exist yet.
/// </summary>
public sealed class TraceWriterTests
{
    private static string ReadStreamAsString(MemoryStream ms)
    {
        ms.Seek(0, SeekOrigin.Begin);
        return new StreamReader(ms, Encoding.UTF8, leaveOpen: true).ReadToEnd();
    }

    // Helper: create a RunStartEvent with new shape
    private static RunStartEvent MakeRunStart(string runId = "test-run") =>
        new RunStartEvent
        {
            RunId = runId,
            Data = new RunStartEvent.RunStartData
            {
                QuestId = 66130u,
                SchemaVer = "1.0"
            }
        };

    // Helper: create a DecisionEvent with new shape
    private static DecisionEvent MakeDecision(string runId = "test-run") =>
        new DecisionEvent
        {
            RunId = runId,
            Data = new DecisionEvent.DecisionData
            {
                StepId = "step-a",
                ActionType = "Navigate"
            }
        };

    // Helper: create an ObservationEvent with new shape
    private static ObservationEvent MakeObservation(string runId = "test-run") =>
        new ObservationEvent
        {
            RunId = runId,
            Data = new ObservationEvent.ObservationData
            {
                Method = "GetPlayerZone",
                Value = JsonSerializer.SerializeToElement(182)
            }
        };

    // =========================================================================
    // Basic write: single event produces one JSONL line
    // =========================================================================

    [Fact]
    public void Write_SingleEvent_ProducesExactlyOneNewlineTerminatedLine()
    {
        /*
         * CONTRACT: Given a fresh TraceWriter and a RunStartEvent (new shape),
         *           When Write(evt) is called once,
         *           Then the stream contains exactly one line terminated by '\n'.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        // Act
        writer.Write(MakeRunStart());

        // Assert
        var content = ReadStreamAsString(ms);
        var lines = content.Split('\n');
        Assert.Equal(2, lines.Length); // "json\n" splits into ["json", ""]
        Assert.True(lines[0].Length > 0, "Line must not be empty");
        Assert.Equal(string.Empty, lines[1]);
    }

    // =========================================================================
    // Three writes produce three parseable JSON lines with "data" sub-objects
    // =========================================================================

    [Fact]
    public void Write_ThreeEvents_ProducesThreeNewlineTerminatedLinesWithDataField()
    {
        /*
         * CONTRACT: Given three events (RunStart, Observation, Decision) with new shape,
         *           When written,
         *           Then each line is valid JSON with "type" and "data" fields.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var events = new TraceEvent[]
        {
            MakeRunStart(),
            MakeObservation(),
            MakeDecision()
        };

        // Act
        foreach (var evt in events)
            writer.Write(evt);

        // Assert
        var content = ReadStreamAsString(ms);
        var lines = content.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("type", out _),
                $"Line must contain 'type': {line}");
            Assert.True(doc.RootElement.TryGetProperty("data", out _),
                $"Line must contain 'data': {line}");
        }
    }

    // =========================================================================
    // Written JSON contains envelope fields (v, seq, ts)
    // =========================================================================

    [Fact]
    public void Write_Event_ContainsEnvelopeFields()
    {
        /*
         * CONTRACT: Given a RunStartEvent with V=1, Seq=0, Ts=0,
         *           When written,
         *           Then the JSON line contains "v", "seq", "ts" at the root.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        // Act
        writer.Write(MakeRunStart());

        // Assert
        var content = ReadStreamAsString(ms).TrimEnd('\n');
        var doc = JsonDocument.Parse(content);
        Assert.True(doc.RootElement.TryGetProperty("v", out _), "Must contain 'v'");
        Assert.True(doc.RootElement.TryGetProperty("seq", out _), "Must contain 'seq'");
        Assert.True(doc.RootElement.TryGetProperty("ts", out _), "Must contain 'ts'");
    }

    // =========================================================================
    // Written JSON does NOT contain "at" field (removed per TSC1)
    // =========================================================================

    [Fact]
    public void Write_Event_DoesNotContainAtField()
    {
        /*
         * CONTRACT: Given any event with new shape,
         *           When written,
         *           Then the JSON line does NOT contain "at".
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        // Act
        writer.Write(MakeRunStart());

        // Assert
        var content = ReadStreamAsString(ms);
        Assert.DoesNotContain("\"at\":", content);
    }

    // =========================================================================
    // TS21: Byte cap applies to new shape (envelope + data wrapper)
    // =========================================================================

    [Fact]
    public void TS21_Write_OversizedEventWithNewShape_ThrowsInvalidOperationExceptionWithCapMessage()
    {
        /*
         * CONTRACT: Given an ObservationEvent whose serialized form (with envelope + data wrapper)
         *           exceeds 4096 bytes
         *           When TraceWriter.Write is called
         *           Then InvalidOperationException is thrown containing "4096-byte cap"
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var largeString = new string('x', 5000);
        var largeValue = JsonSerializer.SerializeToElement(largeString);
        var oversizedEvt = new ObservationEvent
        {
            RunId = "cap-test",
            Data = new ObservationEvent.ObservationData
            {
                Method = "SomeMethod",
                Value = largeValue
            }
        };

        // Act + Assert
        var ex = Assert.Throws<InvalidOperationException>(() => writer.Write(oversizedEvt));
        Assert.Contains("4096-byte cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Disposed writer rejects writes (unchanged behavior, new shape)
    // =========================================================================

    [Fact]
    public void Write_AfterDispose_ThrowsObjectDisposedException()
    {
        /*
         * CONTRACT: Given a disposed TraceWriter,
         *           When Write is called with a new-shape event,
         *           Then ObjectDisposedException is thrown.
         */

        // Arrange
        using var ms = new MemoryStream();
        var writer = new TraceWriter(ms, leaveOpen: true);
        writer.Dispose();

        // Act + Assert
        Assert.Throws<ObjectDisposedException>(() => writer.Write(MakeRunStart()));
    }

    // =========================================================================
    // Each event type written → line has matching "type" field
    // =========================================================================

    [Fact]
    public void Write_AllSevenEventTypes_LinesContainMatchingTypeFields()
    {
        /*
         * CONTRACT: For each of the 7 event types written with the new shape,
         *           the corresponding JSON line has a "type" field matching the
         *           event's Type property.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var stepElement = JsonSerializer.SerializeToElement(new { type = "talk" });
        var events = new TraceEvent[]
        {
            new RunStartEvent { RunId = "r", Data = new RunStartEvent.RunStartData { QuestId = 1 } },
            new RunEndEvent { RunId = "r", Data = new RunEndEvent.RunEndData { Outcome = "done" } },
            new ObservationEvent { RunId = "r", Data = new ObservationEvent.ObservationData { Method = "M" } },
            new DecisionEvent { RunId = "r", Data = new DecisionEvent.DecisionData { ActionType = "Nav" } },
            new ActionSubmittedEvent { RunId = "r", Data = new ActionSubmittedEvent.ActionSubmittedData { ActionType = "Nav" } },
            new ActionCompletedEvent { RunId = "r", Data = new ActionCompletedEvent.ActionCompletedData { ActionType = "Nav", Outcome = "Ok" } },
            new StepRecordedEvent { RunId = "r", Data = new StepRecordedEvent.StepRecordedData { StepId = "s", Step = stepElement } }
        };

        // Act
        foreach (var evt in events)
            writer.Write(evt);

        // Assert
        ms.Seek(0, SeekOrigin.Begin);
        var allLines = new StreamReader(ms, Encoding.UTF8, leaveOpen: true)
            .ReadToEnd()
            .TrimEnd('\n')
            .Split('\n');

        Assert.Equal(events.Length, allLines.Length);
        for (var i = 0; i < events.Length; i++)
        {
            var doc = JsonDocument.Parse(allLines[i]);
            var typeValue = doc.RootElement.GetProperty("type").GetString();
            Assert.Equal(events[i].Type, typeValue);
        }
    }

    // =========================================================================
    // Concurrent writes from two threads produce 2 complete non-interleaved lines
    // =========================================================================

    [Fact]
    public async Task Write_ConcurrentFromTwoThreads_ProducesTwoCompleteLines()
    {
        /*
         * CONTRACT: Given two threads both calling Write concurrently with new-shape events,
         *           When both complete,
         *           Then the stream contains exactly 2 valid JSON lines.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var evt1 = MakeRunStart("run-a");
        var evt2 = new RunEndEvent { RunId = "run-a", Data = new RunEndEvent.RunEndData { Outcome = "done" } };

        // Act
#pragma warning disable xUnit1051
        var t1 = Task.Run(() => writer.Write(evt1));
        var t2 = Task.Run(() => writer.Write(evt2));
#pragma warning restore xUnit1051
        await Task.WhenAll(t1, t2);

        // Assert
        var content = ReadStreamAsString(ms);
        var lines = content.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            Assert.True(line.Length > 0, "No empty lines");
            JsonDocument.Parse(line); // throws on malformed JSON
        }
    }

    // =========================================================================
    // OpenFile writes events → file readable after dispose
    // =========================================================================

    [Fact]
    public void OpenFile_WritesEventsWithNewShape_FileReadableAfterDispose()
    {
        /*
         * CONTRACT: Given TraceWriter.OpenFile(path),
         *           When new-shape events are written and the writer is disposed,
         *           Then the file exists and each line contains "data" and "type" fields.
         */

        var path = Path.Combine(Path.GetTempPath(), $"qf-test-{Guid.NewGuid():N}.trace.jsonl");
        try
        {
            using (var writer = TraceWriter.OpenFile(path))
            {
                writer.Write(MakeRunStart());
                writer.Write(MakeDecision());
            }

            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                var doc = JsonDocument.Parse(line);
                Assert.True(doc.RootElement.TryGetProperty("type", out _));
                Assert.True(doc.RootElement.TryGetProperty("data", out _));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
