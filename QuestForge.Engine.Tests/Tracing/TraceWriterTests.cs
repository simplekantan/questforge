using System.Text;
using System.Text.Json;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Tracing;
using Xunit;

namespace QuestForge.Engine.Tests.Tracing;

/// <summary>
/// RED PHASE: Tests for the TraceWriter (JSONL writer over a Stream).
/// All tests fail until Builder implements QuestForge.Engine.Tracing.TraceWriter.
///
/// Covers §6.2 of PHASE_5_PLAN.md — 11 tests total.
/// </summary>
public sealed class TraceWriterTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    private static RunStartEvent MakeRunStart(string runId = "test-run") =>
        new RunStartEvent(runId, 66130u, 66130u, _now);

    private static string ReadStreamAsString(MemoryStream ms)
    {
        ms.Seek(0, SeekOrigin.Begin);
        return new StreamReader(ms, Encoding.UTF8, leaveOpen: true).ReadToEnd();
    }

    // -------------------------------------------------------------------------
    // Single write produces exactly one newline-terminated line
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_SingleEvent_ProducesExactlyOneNewlineTerminatedLine()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given a fresh TraceWriter over a MemoryStream and a RunStartEvent,
         *           When Write(evt) is called once,
         *           Then the stream contains exactly one line terminated by '\n'.
         *           The line is not empty and contains valid JSON.
         *
         * BUILDER GUIDANCE: Write(evt) must append exactly: json + '\n'.
         *   No '\r'. No extra whitespace lines.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);
        var evt = MakeRunStart();

        // Act
        writer.Write(evt);

        // Assert
        var content = ReadStreamAsString(ms);
        var lines = content.Split('\n');
        // Split on '\n' of "json\n" yields ["json", ""] — two elements, last empty
        Assert.Equal(2, lines.Length, "Expected exactly one event line followed by trailing empty after final \\n");
        Assert.True(lines[0].Length > 0, "The single line must not be empty");
        Assert.Equal(string.Empty, lines[1], "No content after the final \\n");
    }

    // -------------------------------------------------------------------------
    // Three writes produce three independently parseable JSON lines
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_ThreeEvents_ProducesThreeNewlineTerminatedLines()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given a fresh TraceWriter,
         *           When Write is called three times with distinct events,
         *           Then reading the stream yields 3 newline-terminated lines,
         *           each parseable as JSON with a "type" field.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var events = new TraceEvent[]
        {
            new RunStartEvent("run-001", 66130u, 66130u, _now),
            new ObservationEvent("run-001", "GetPlayerZone", null, null, _now),
            new DecisionEvent("run-001", "travel-to-wymond", "Navigate", _now)
        };

        // Act
        foreach (var evt in events)
            writer.Write(evt);

        // Assert
        var content = ReadStreamAsString(ms);
        var lines = content.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length, "Expected exactly 3 event lines");
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line); // throws JsonException if invalid
            Assert.True(doc.RootElement.TryGetProperty("type", out _),
                $"Line must contain 'type' field: {line}");
        }
    }

    // -------------------------------------------------------------------------
    // JSONL lines are single-line JSON (WriteIndented = false)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_SingleEvent_LineContainsNoEmbeddedNewlines()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given any single event written,
         *           Then the resulting line content contains no embedded '\n' characters.
         *           This verifies WriteIndented = false on TraceEventJsonContext.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);
        var evt = new RunStartEvent("run-001", 66130u, 66130u, _now);

        // Act
        writer.Write(evt);

        // Assert
        var content = ReadStreamAsString(ms);
        // The only '\n' must be the final line terminator — strip it, then check no embedded newlines
        var lineContent = content.TrimEnd('\n');
        Assert.DoesNotContain('\n', lineContent,
            "The JSON line must not contain embedded newlines (WriteIndented must be false)");
    }

    // -------------------------------------------------------------------------
    // After Write returns, no bytes are buffered (flush per write)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_AfterReturn_StreamPositionReflectsWrittenBytes()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given a TraceWriter over a MemoryStream,
         *           When Write returns,
         *           Then ms.Position == ms.Length (no pending buffered bytes).
         *
         * BUILDER GUIDANCE: Flush both _writer and _stream inside Write before returning.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);
        var evt = MakeRunStart();

        // Act
        writer.Write(evt);

        // Assert
        Assert.Equal(ms.Length, ms.Position,
            "Stream.Position must equal Stream.Length after Write — no unflushed bytes");
        Assert.True(ms.Length > 0, "Stream must be non-empty after Write");
    }

    // -------------------------------------------------------------------------
    // Disposed writer rejects subsequent writes
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_AfterDispose_ThrowsObjectDisposedException()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given a TraceWriter that has been disposed,
         *           When Write is called,
         *           Then ObjectDisposedException is thrown.
         */

        // Arrange
        using var ms = new MemoryStream();
        var writer = new TraceWriter(ms, leaveOpen: true);
        writer.Dispose();

        // Act + Assert
        Assert.Throws<ObjectDisposedException>(() =>
            writer.Write(new RunStartEvent("run-001", 1u, 1u, _now)));
    }

    // -------------------------------------------------------------------------
    // Oversized event throws InvalidOperationException
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_OversizedEvent_ThrowsInvalidOperationExceptionWithCapMessage()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given an ObservationEvent whose serialized form exceeds 4096 bytes
         *           (synthetic value with 5000 chars),
         *           When Write is called,
         *           Then InvalidOperationException is thrown with a message
         *           containing "4096-byte cap".
         *
         * BUILDER GUIDANCE: Serialize first, check json.Length > 4096, throw before writing.
         */

        // Arrange — create an ObservationEvent with a large Value to exceed 4096 bytes
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var largeString = new string('x', 5000);
        var largeValue = JsonSerializer.SerializeToElement(largeString);
        var oversizedEvt = new ObservationEvent("run-001", "SomeMethod", null, largeValue, _now);

        // Act + Assert
        var ex = Assert.Throws<InvalidOperationException>(() => writer.Write(oversizedEvt));
        Assert.Contains("4096-byte cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Each written line has a "type" field matching the event's Type property
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_EachEventType_LineContainsMatchingTypeField()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: For each event type written, the corresponding JSON line
         *           has a "type" field whose value matches the event's Type property.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var events = new TraceEvent[]
        {
            new RunStartEvent("r", 1u, 1u, _now),
            new RunEndEvent("r", "done", _now),
            new ObservationEvent("r", "M", null, null, _now),
            new DecisionEvent("r", null, "Navigate", _now),
            new ActionSubmittedEvent("r", "Navigate", null, _now),
            new ActionCompletedEvent("r", "Navigate", "Arrived", _now)
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
            Assert.Equal(events[i].Type, typeValue,
                $"Line {i}: expected type '{events[i].Type}' but got '{typeValue}'");
        }
    }

    // -------------------------------------------------------------------------
    // RunStartEvent written → JSON line has "type":"run.start"
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_RunStartEvent_LineHasRunStartType()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given a RunStartEvent written to a TraceWriter,
         *           When the stream content is read and the first line parsed,
         *           Then the "type" field equals "run.start".
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        // Act
        writer.Write(new RunStartEvent("run-01", 66130u, 66130u, _now));

        // Assert
        ms.Seek(0, SeekOrigin.Begin);
        var line = new StreamReader(ms, Encoding.UTF8).ReadLine()!;
        var doc = JsonDocument.Parse(line);
        Assert.Equal("run.start", doc.RootElement.GetProperty("type").GetString());
    }

    // -------------------------------------------------------------------------
    // OpenFile static factory creates readable file
    // -------------------------------------------------------------------------

    [Fact]
    public void OpenFile_WritesEvents_FileReadableAfterDispose()
    {
        /*
         * RED: Will fail until TraceWriter.OpenFile is implemented.
         *
         * CONTRACT: Given TraceWriter.OpenFile(path) creates a file,
         *           When events are written and the writer is disposed,
         *           Then the file exists and can be read as JSONL
         *           with each line independently parseable.
         *
         * BUILDER GUIDANCE: OpenFile uses FileMode.Append, FileAccess.Write, FileShare.Read.
         */

        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"qf-test-{Guid.NewGuid():N}.trace.jsonl");
        try
        {
            // Act
            using (var writer = TraceWriter.OpenFile(path))
            {
                writer.Write(new RunStartEvent("r1", 66130u, 66130u, _now));
                writer.Write(new DecisionEvent("r1", "step-1", "Navigate", _now));
            }

            // Assert
            Assert.True(File.Exists(path), "File must exist after OpenFile + dispose");
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length, "Two events must produce two lines");
            foreach (var line in lines)
            {
                var doc = JsonDocument.Parse(line);
                Assert.True(doc.RootElement.TryGetProperty("type", out _));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // -------------------------------------------------------------------------
    // Concurrent writes from two threads produce 2 complete non-interleaved lines
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Write_ConcurrentFromTwoThreads_ProducesTwoCompleteLines()
    {
        /*
         * RED: Will fail until TraceWriter is implemented with locking.
         *
         * CONTRACT: Given two threads both calling Write concurrently,
         *           When both complete,
         *           Then the stream contains exactly 2 valid JSON lines
         *           with no interleaved bytes within any single line.
         *
         * BUILDER GUIDANCE: lock(_writeLock) wraps serialize + write + flush.
         */

        // Arrange
        using var ms = new MemoryStream();
        using var writer = new TraceWriter(ms, leaveOpen: true);

        var evt1 = new RunStartEvent("run-a", 1u, 1u, _now);
        var evt2 = new RunEndEvent("run-a", "done", _now);

        // Act — fire both concurrently
        var t1 = Task.Run(() => writer.Write(evt1));
        var t2 = Task.Run(() => writer.Write(evt2));
        await Task.WhenAll(t1, t2);

        // Assert
        var content = ReadStreamAsString(ms);
        var lines = content.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length, "Exactly 2 complete lines expected");
        foreach (var line in lines)
        {
            Assert.True(line.Length > 0, "No empty lines");
            // Each line must be valid JSON (no interleaving)
            JsonDocument.Parse(line); // throws on malformed JSON
        }
    }

    // -------------------------------------------------------------------------
    // Write to a non-writable stream propagates exception
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_ToNonWritableStream_ThrowsException()
    {
        /*
         * RED: Will fail until TraceWriter is implemented.
         *
         * CONTRACT: Given a TraceWriter constructed over a non-writable stream
         *           (e.g., a MemoryStream opened read-only),
         *           When Write is called,
         *           Then an IOException or NotSupportedException is propagated.
         *
         * BUILDER GUIDANCE: The StreamWriter write will throw naturally; do not swallow it
         *   (the TraceWriter's crash-safety only applies to the engine's TraceSafe wrapper,
         *    not to the TraceWriter itself — TraceWriter.Write propagates write failures).
         */

        // Arrange — create a read-only MemoryStream from a byte array
        var bytes = Encoding.UTF8.GetBytes("existing content\n");
        using var readOnlyStream = new MemoryStream(bytes, writable: false);

        // Act + Assert — constructor may succeed, but Write must throw
        // (StreamWriter will throw on first flush to a non-writable stream)
        var writer = new TraceWriter(readOnlyStream, leaveOpen: true);
        Assert.ThrowsAny<Exception>(() =>
            writer.Write(new RunStartEvent("r", 1u, 1u, _now)));
    }
}