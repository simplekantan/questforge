using System.Text.Json;
using QuestForge.Adapters.Tracing;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Reads a JSONL trace file into a typed list of TraceEvent instances.
/// Each non-empty line is deserialized using TraceEventJsonContext (polymorphic).
/// Malformed lines propagate JsonException — replay is intentionally strict on inputs.
/// </summary>
public static class TraceReader
{
    /// <summary>
    /// Reads all events from a JSONL trace file in document order.
    /// Empty and whitespace-only lines are skipped.
    /// </summary>
    public static IReadOnlyList<TraceEvent> ReadFile(string path)
    {
        var events = new List<TraceEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = JsonSerializer.Deserialize<TraceEvent>(line, TraceEventJsonContext.Default.TraceEvent)
                      ?? throw new InvalidDataException($"Trace line deserialized to null: {line}");
            events.Add(evt);
        }
        return events;
    }

    /// <summary>
    /// Convenience overload: reads only events of a specific subtype, in document order.
    /// </summary>
    public static IReadOnlyList<T> ReadFile<T>(string path) where T : TraceEvent
        => ReadFile(path).OfType<T>().ToList();
}
