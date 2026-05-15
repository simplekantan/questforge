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
/// </summary>
public sealed class ObservationScanner
{
    private readonly IReadOnlyList<ObservationEvent> _observations;
    private int _cursor;

    public ObservationScanner(IReadOnlyList<ObservationEvent> observations)
    {
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));
    }

    /// <summary>
    /// Finds the next observation matching (method, argument) starting from _cursor.
    /// Advances _cursor past it on success.
    /// Throws ReplayObservationStarvationException when no remaining observation matches.
    /// </summary>
    public ObservationEvent Next(string method, object? argument)
    {
        var requestedArg = argument is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(argument, ReplayJsonOptions.Default);

        for (var i = _cursor; i < _observations.Count; i++)
        {
            var obs = _observations[i];
            if (obs.Method != method) continue;
            if (!ArgumentEquals(obs.Argument, requestedArg)) continue;
            _cursor = i + 1;
            return obs;
        }

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
