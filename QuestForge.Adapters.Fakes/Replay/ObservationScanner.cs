using System.Text.Json;
using QuestForge.Adapters.Tracing;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Cursor-based scanner over a flat list of ObservationEvents.
/// Matches observations by (method, serialized-argument) pair, advancing a monotonic cursor.
///
/// Phase 7 uses loose scan-forward matching: the scanner does NOT enforce strict ordering.
/// It scans from _cursor to end and returns the first matching (method, argument) entry.
/// An entry earlier than the cursor is permanently consumed (unreachable).
///
/// Deduplicated traces: when the cursor finds no more observations for a given (method, arg),
/// the scanner falls back to the last successfully returned observation for that pair rather
/// than throwing starvation. This supports recording proxies that only emit on value change.
/// </summary>
public sealed class ObservationScanner
{
    private readonly IReadOnlyList<ObservationEvent> _observations;
    private int _cursor;

    // Last-known-value fallback for deduplicated traces.
    // Key: "method:argJson" — same encoding as the recording proxy's dedup key.
    private readonly Dictionary<string, ObservationEvent> _lastSeen = new();

    public ObservationScanner(IReadOnlyList<ObservationEvent> observations)
    {
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));
    }

    /// <summary>
    /// Finds the next observation matching (method, argument) starting from _cursor.
    /// Advances _cursor past it on success.
    /// Falls back to the last seen value for that (method, argument) pair when the cursor
    /// is exhausted — supports deduplicated traces where unchanged values are omitted.
    /// Throws ReplayObservationStarvationException only when no observation has ever been
    /// seen for this (method, argument) pair.
    /// </summary>
    public ObservationEvent Next(string method, object? argument)
    {
        var requestedArg = argument is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(argument, ReplayJsonOptions.Default);

        var key = $"{method}:{requestedArg?.GetRawText() ?? ""}";

        for (var i = _cursor; i < _observations.Count; i++)
        {
            var obs = _observations[i];
            if (obs.Method != method) continue;
            if (!ArgumentEquals(obs.Argument, requestedArg)) continue;
            _cursor = i + 1;
            _lastSeen[key] = obs;
            return obs;
        }

        // No new observation found — fall back to last known value if available.
        if (_lastSeen.TryGetValue(key, out var lastKnown))
            return lastKnown;

        throw new ReplayObservationStarvationException(
            $"No remaining observation for method '{method}' " +
            $"with argument {requestedArg?.GetRawText() ?? "null"} " +
            $"after cursor {_cursor}/{_observations.Count}.");
    }

    private static bool ArgumentEquals(JsonElement? a, JsonElement? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Value.GetRawText() == b.Value.GetRawText();
    }
}
