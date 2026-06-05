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
/// value — there is no per-read walk.
///
/// Segment advancement is driven by two mechanisms:
///   1. AdvanceSegment() — called by the harness on new unique transitions
///   2. TryAdvanceForDecision() — called by the harness on EVERY decision (including
///      duplicates). Advances only when the decision matches the next expected decision
///      in the trace's recorded sequence. This enables combat replay where the same
///      transition repeats but observations change across segments.
/// </summary>
public sealed class SegmentedObservationScanner
{
    private readonly IReadOnlyList<ObservationEvent> _observations;

    // _segmentEnds[k] = exclusive upper-bound (index into _observations) for segment k.
    // One entry per decision, plus one terminal-tail entry = _observations.Count.
    private readonly IReadOnlyList<int> _segmentEnds;

    // The trace's decision sequence for decision-matching advancement.
    private readonly IReadOnlyList<(string? StepId, string ActionType)> _decisions;
    private int _decisionCursor;

    private int _segment;

    // Per-pair most-recently-returned observation.
    private readonly Dictionary<string, ObservationEvent> _lastSeen = new();

    // Debug log for diagnosing replay issues. Access via DebugLog property.
    private readonly List<string> _debugLog = new();
    public IReadOnlyList<string> DebugLog => _debugLog;

    public SegmentedObservationScanner(IReadOnlyList<TraceEvent> trace)
    {
        var observations = new List<ObservationEvent>();
        var segmentEnds = new List<int>();
        var decisions = new List<(string? StepId, string ActionType)>();

        foreach (var evt in trace)
        {
            if (evt is ObservationEvent obs)
            {
                observations.Add(obs);
            }
            else if (evt is DecisionEvent dec)
            {
                segmentEnds.Add(observations.Count);
                decisions.Add((dec.Data.StepId, dec.Data.ActionType));
            }
        }

        // Terminal tail: observations after the last decision (or all obs if no decisions).
        segmentEnds.Add(observations.Count);

        _observations = observations;
        _segmentEnds = segmentEnds;
        _decisions = decisions;
        _decisionCursor = 0;
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
        // Keep decision cursor in sync — skip to the next unmatched decision
        if (_decisionCursor < _segment)
            _decisionCursor = _segment;
    }

    /// <summary>
    /// Try to advance the segment by matching the replay's decision against the trace's
    /// decision sequence. If the next expected decision in the trace matches, advance.
    /// Called by the harness on EVERY decision, including duplicates.
    /// Returns true if the segment was advanced.
    /// </summary>
    public bool TryAdvanceForDecision(string? stepId, string actionType)
    {
        if (_decisionCursor >= _decisions.Count)
            return false;

        // Try to match against the trace's decision sequence.
        // Skip null-stepId decisions (defense engage, loading zone guard) — these are
        // non-deterministic and replay may not reproduce them. Each skipped null
        // decision advances both cursor and segment (they consumed a segment in the trace).
        while (_decisionCursor < _decisions.Count)
        {
            var expected = _decisions[_decisionCursor];

            if (expected.StepId is null)
            {
                // Non-deterministic decision — skip and advance segment
                _debugLog.Add($"SKIP-NULL dec[{_decisionCursor}] (null)/{expected.ActionType}");
                _decisionCursor++;
                if (_segment < _segmentEnds.Count - 1)
                    _segment++;
                continue;
            }

            if (expected.StepId == stepId
                && string.Equals(expected.ActionType, actionType, StringComparison.OrdinalIgnoreCase))
            {
                _decisionCursor++;
                if (_segment < _segmentEnds.Count - 1)
                {
                    _debugLog.Add($"MATCH seg {_segment}→{_segment + 1} dec[{_decisionCursor - 1}] {stepId}/{actionType}");
                    _segment++;
                }
                return true;
            }

            // Non-matching non-null decision — don't advance
            return false;
        }

        return false;
    }

    private int SegmentEnd => _segmentEnds[_segment];

    /// <summary>
    /// Returns the deciding value for (method, argument) in the current segment.
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
            if (obs.Data.Method == method && ArgumentEquals(obs.Data.Argument, requestedArg))
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
