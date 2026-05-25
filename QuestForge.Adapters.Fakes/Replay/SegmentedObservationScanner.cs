using System.Text.Json;
using QuestForge.Adapters.Tracing;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Decision-anchored scanner. Built from the FULL trace (observations + decisions in
/// record order). Partitions observations into segments at DecisionEvent boundaries.
///
/// For each segment, a read of (method, arg) returns the deciding value: the most-recent
/// observation for that pair recorded at or before the current segment's decision boundary
/// (LastAtOrBefore(SegmentEnd)). Repeated reads within a segment return the same deciding
/// value — there is no per-read walk. AdvanceSegment moves the boundary so later segments
/// may serve a newer value. Starvation only if the pair never appears at-or-before the
/// boundary AND was never seen (truly-absent).
/// </summary>
public sealed class SegmentedObservationScanner
{
    private readonly IReadOnlyList<ObservationEvent> _observations;

    // _segmentEnds[k] = exclusive upper-bound (index into _observations) for segment k.
    // One entry per decision, plus one terminal-tail entry = _observations.Count.
    private readonly IReadOnlyList<int> _segmentEnds;

    private int _segment;

    // Per-pair most-recently-returned observation.
    private readonly Dictionary<string, ObservationEvent> _lastSeen = new();

    public SegmentedObservationScanner(IReadOnlyList<TraceEvent> trace)
    {
        var observations = new List<ObservationEvent>();
        var segmentEnds = new List<int>();

        foreach (var evt in trace)
        {
            if (evt is ObservationEvent obs)
            {
                observations.Add(obs);
            }
            else if (evt is DecisionEvent)
            {
                // Decision boundary: close the current segment at the current observation count.
                segmentEnds.Add(observations.Count);
            }
        }

        // Terminal tail: observations after the last decision (or all obs if no decisions).
        segmentEnds.Add(observations.Count);

        _observations = observations;
        _segmentEnds = segmentEnds;
        _segment = 0;
    }

    /// <summary>Number of segments (decisions + 1 for the terminal tail).</summary>
    public int SegmentCount => _segmentEnds.Count;

    /// <summary>Current segment index (0-based).</summary>
    public int CurrentSegment => _segment;

    /// <summary>Advance to the next segment. Clamps at the last segment.</summary>
    public void AdvanceSegment()
    {
        if (_segment < _segmentEnds.Count - 1)
            _segment++;
    }

    private int SegmentEnd => _segmentEnds[_segment];

    /// <summary>
    /// Returns the deciding value for (method, argument) in the current segment: the most-recent
    /// observation at or before the segment boundary (LastAtOrBefore(SegmentEnd)). Repeated reads
    /// within the same segment return the same value. Falls back to the last seen value if the
    /// pair has no observation at or before the current boundary but was seen in an earlier segment.
    /// Throws ReplayObservationStarvationException only when the pair was never recorded anywhere.
    /// </summary>
    public ObservationEvent Next(string method, object? argument)
    {
        var requestedArg = argument is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(argument, ReplayJsonOptions.Default);

        var key = $"{method}:{requestedArg?.GetRawText() ?? ""}";

        var pinned = LastAtOrBefore(method, requestedArg, SegmentEnd);
        if (pinned is not null)
        {
            _lastSeen[key] = pinned;
            return pinned;
        }

        if (_lastSeen.TryGetValue(key, out var last))
            return last;

        throw new ReplayObservationStarvationException(
            $"No observation for method '{method}' with argument " +
            $"{requestedArg?.GetRawText() ?? "null"} anywhere in the trace " +
            $"(segment {_segment}/{_segmentEnds.Count - 1}, window end {SegmentEnd}/{_observations.Count}).");
    }

    private ObservationEvent? LastAtOrBefore(string method, JsonElement? requestedArg, int exclusiveEnd)
    {
        ObservationEvent? result = null;
        for (var i = 0; i < exclusiveEnd && i < _observations.Count; i++)
        {
            var obs = _observations[i];
            if (obs.Method == method && ArgumentEquals(obs.Argument, requestedArg))
                result = obs;
        }
        return result;
    }

    private static bool ArgumentEquals(JsonElement? a, JsonElement? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Value.GetRawText() == b.Value.GetRawText();
    }
}
