using System.Text.Json;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// RED PHASE: Tests for SegmentedObservationScanner — the decision-anchored, deciding-value scanner.
/// ALL tests in this file will produce compile errors (CS0246) because SegmentedObservationScanner
/// does not exist yet. That is the expected RED signal.
///
/// Covers §4 Group S of DECISION_ANCHORED_REPLAY_PLAN.md — 8 scenarios.
///
/// Design under test (from §2.2 + §3.1 of the plan — DECIDING-VALUE-PER-SEGMENT model):
/// - The scanner is built from a FULL trace (ObservationEvents + DecisionEvents in record order).
/// - Observations are partitioned into segments at DecisionEvent boundaries.
/// - Segment K = observations after decision K-1 (or after run.start) and before decision K.
/// - A terminal tail segment holds observations after the last decision.
/// - SegmentCount = number of decisions + 1 (the terminal tail).
/// - A read of (method, arg) in segment K returns the most-recent observation for that pair
///   recorded BEFORE segment K's decision boundary — LastAtOrBefore(segmentEnd[K]).
///   This is the "deciding value". Repeated reads within a segment return the SAME value.
///   There is NO per-read cursor walk.
/// - AdvanceSegment() moves to the next segment (updates the boundary); clamps at the last.
/// - Starvation (ReplayObservationStarvationException) is thrown ONLY when the pair
///   was never recorded anywhere in the trace.
/// </summary>
public sealed class SegmentedObservationScannerTests
{
    private static readonly DateTimeOffset At = new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // S1 — segment count includes terminal tail
    // =========================================================================

    /// <summary>
    /// S1: Given trace [run.start, obs×a, decision1, obs×b, decision2, obs×c, run.end],
    /// SegmentCount == 3 (segment 0 = obs×a before decision1,
    ///                     segment 1 = obs×b before decision2,
    ///                     segment 2 = obs×c after decision2 — terminal tail).
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S1_SegmentCount_TwoDecisions_ThreeSegmentsIncludingTerminalTail()
    {
        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("GetPlayerZone", null, """{"value":181}"""),
            Obs("IsQuestComplete", """{"value":65644}""", "false"),
            Decision("run1", "step-a", "Interact"),
            Obs("GetPlayerPosition", null, """{"x":1.0,"y":0.0,"z":2.0}"""),
            Obs("IsQuestAccepted", """{"value":65644}""", "false"),
            Decision("run1", "step-b", "Navigate"),
            Obs("IsQuestComplete", """{"value":65644}""", "true"),
            RunEnd("run1", "done")
        );

        // CS0246: SegmentedObservationScanner does not exist
        var scanner = new SegmentedObservationScanner(trace);

        // Two decisions → three segments (segment 0, segment 1, terminal tail)
        Assert.Equal(3, scanner.SegmentCount);
        Assert.Equal(0, scanner.CurrentSegment);
    }

    // =========================================================================
    // S2 — step-gated value pins across segments; flips only in the terminal tail
    // =========================================================================

    /// <summary>
    /// S2: The direct regression test for §1.2 of the plan.
    ///
    /// Given a trace where IsQuestComplete(65644)=false is recorded in the first segment
    /// and IsQuestComplete(65644)=true is recorded only in the terminal tail:
    ///
    /// - In segment 0: repeated calls to Next("IsQuestComplete", 65644) return false every time
    ///   (it never advances to true, even after many reads within the same segment).
    /// - After AdvanceSegment() through all intermediate segments to the terminal tail:
    ///   Next("IsQuestComplete", 65644) returns true.
    ///
    /// This directly proves the §1.2 bug is fixed: the old scanner consumed the `false`
    /// observation on tick 1 and returned `true` on tick 2, killing the run after 1 transition.
    /// The segmented scanner pins `false` in all non-terminal segments.
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S2_StepGatedValue_PinsAcrossSegments_FlipsInTerminalTail()
    {
        // Trace layout:
        //   run.start
        //   obs IsQuestComplete(65644)=false      ← segment 0
        //   obs GetPlayerZone=181                 ← segment 0
        //   decision step-1 Interact              ← boundary → segment 0 ends, segment 1 begins
        //   obs GetPlayerPosition=(1,0,2)         ← segment 1 (no IsQuestComplete recorded here)
        //   decision step-2 Navigate              ← boundary → segment 1 ends, terminal tail begins
        //   obs IsQuestComplete(65644)=true       ← terminal tail (segment 2)
        //   run.end done

        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("IsQuestComplete", """{"value":65644}""", "false"),
            Obs("GetPlayerZone", null, """{"value":181}"""),
            Decision("run1", "step-1", "Interact"),
            Obs("GetPlayerPosition", null, """{"x":1.0,"y":0.0,"z":2.0}"""),
            Decision("run1", "step-2", "Navigate"),
            Obs("IsQuestComplete", """{"value":65644}""", "true"),
            RunEnd("run1", "done")
        );

        var scanner = new SegmentedObservationScanner(trace);

        // --- Segment 0: IsQuestComplete must pin to false on every read ---
        var questId = new QuestId(65644);

        var r1 = scanner.Next("IsQuestComplete", questId);
        // xUnit v3 does not support 3-arg Assert.Equal with a string message for string values;
        // use Assert.True for message-bearing equality checks.
        Assert.True("false" == r1.Value?.GetRawText(),
            $"S2 first read in segment 0 — expected false, got {r1.Value?.GetRawText()}. Segment={scanner.CurrentSegment}/{scanner.SegmentCount}");

        // Second read in same segment: must still pin to false (NOT advance to true)
        var r2 = scanner.Next("IsQuestComplete", questId);
        Assert.True("false" == r2.Value?.GetRawText(),
            $"S2 second read in segment 0 — must still pin false. Got {r2.Value?.GetRawText()}. Segment={scanner.CurrentSegment}/{scanner.SegmentCount}");

        // Third read: still false
        var r3 = scanner.Next("IsQuestComplete", questId);
        Assert.True("false" == r3.Value?.GetRawText(),
            $"S2 third read in segment 0 — still pinned false. Got {r3.Value?.GetRawText()}. Segment={scanner.CurrentSegment}/{scanner.SegmentCount}");

        // --- Advance through segment 1 (no IsQuestComplete there) ---
        scanner.AdvanceSegment(); // now at segment 1
        Assert.Equal(1, scanner.CurrentSegment);

        // Segment 1 has no IsQuestComplete obs → should pin to last-at-or-before window = false
        var r4 = scanner.Next("IsQuestComplete", questId);
        Assert.True("false" == r4.Value?.GetRawText(),
            $"S2 read in segment 1 — must pin false (no IsQuestComplete in this window). Got {r4.Value?.GetRawText()}. Segment={scanner.CurrentSegment}/{scanner.SegmentCount}");

        // --- Advance to terminal tail ---
        scanner.AdvanceSegment(); // now at segment 2 = terminal tail
        Assert.Equal(2, scanner.CurrentSegment);

        // Terminal tail contains IsQuestComplete=true → must return true now
        var r5 = scanner.Next("IsQuestComplete", questId);
        Assert.True("true" == r5.Value?.GetRawText(),
            $"S2 read in terminal tail — must return true. Got {r5.Value?.GetRawText()}. Segment={scanner.CurrentSegment}/{scanner.SegmentCount}");
    }

    // =========================================================================
    // S3 — deciding value is STABLE within a segment (no per-read walk)
    // =========================================================================

    /// <summary>
    /// S3: Given three GetPlayerPosition observations p1, p2, p3 all recorded before decision 1
    /// (segment 0's boundary), calling Next("GetPlayerPosition", null) three times WITHOUT
    /// advancing the segment returns p3 every time — NOT p1, then p2, then p3.
    ///
    /// The deciding value for a segment is the last observation at-or-before the boundary.
    /// Repeated reads within a segment return that same deciding value; there is no per-read walk.
    ///
    /// RED against the current walk-scanner: the current implementation returns p1, p2, p3
    /// (per-read cursor advancement), so this test will FAIL until the deciding-value model
    /// is implemented by the Builder.
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S3_DecidingValue_IsStableWithinSegment_RepeatedReadsReturnSameLastValue()
    {
        const string P1 = """{"x":10.0,"y":0.0,"z":0.0}""";
        const string P2 = """{"x":5.0,"y":0.0,"z":0.0}""";
        const string P3 = """{"x":1.0,"y":0.0,"z":0.0}""";

        // p1, p2, p3 are ALL before decision 1 — all in segment 0's window.
        // Deciding value = p3 (last-at-or-before the boundary).
        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("GetPlayerPosition", null, P1),
            Obs("GetPlayerPosition", null, P2),
            Obs("GetPlayerPosition", null, P3),
            Decision("run1", "step-1", "Interact"),
            RunEnd("run1", "done")
        );

        var scanner = new SegmentedObservationScanner(trace);

        // All three reads in segment 0 must return p3 (the deciding value).
        // The walk model (p1 → p2 → p3) is WRONG under the new model.
        var r1 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P3 == r1.Value?.GetRawText(),
            $"S3 first read: expected deciding value p3, got {r1.Value?.GetRawText()}. " +
            "If this returns p1, the per-read walk model is still active (RED expected).");

        var r2 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P3 == r2.Value?.GetRawText(),
            $"S3 second read: expected deciding value p3 (stable), got {r2.Value?.GetRawText()}.");

        var r3 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P3 == r3.Value?.GetRawText(),
            $"S3 third read: expected deciding value p3 (stable), got {r3.Value?.GetRawText()}.");
    }

    // =========================================================================
    // S4 — AdvanceSegment updates the deciding value; no leak of a later segment's value
    // =========================================================================

    /// <summary>
    /// S4: Given GetPlayerPosition=p1 recorded before decision1 (segment 0's boundary)
    /// and GetPlayerPosition=p2 recorded before decision2 (segment 1's boundary):
    ///
    /// - While at segment 0: repeated Next("GetPlayerPosition", null) → p1 (never p2).
    ///   The segment window excludes p2, so it cannot leak into segment 0.
    /// - After AdvanceSegment(): Next("GetPlayerPosition", null) → p2.
    ///   The segment window now includes p2, and it becomes the deciding value.
    ///
    /// Repeated reads at each segment both return the same deciding value (no per-read walk).
    ///
    /// RED against the current walk-scanner: the current implementation advances the cursor
    /// per-read, so the pinning behavior of repeated reads within a segment is absent.
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S4_AdvanceSegment_UpdatesDecidingValue_NoLeakOfNextSegmentValue()
    {
        const string P1 = """{"x":100.0,"y":0.0,"z":0.0}""";
        const string P2 = """{"x":10.0,"y":0.0,"z":0.0}""";

        // p1 is before decision1 (segment 0), p2 is before decision2 (segment 1).
        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("GetPlayerPosition", null, P1),        // segment 0 — deciding value = p1
            Decision("run1", "step-1", "Navigate"),    // boundary: segment 0 ends
            Obs("GetPlayerPosition", null, P2),        // segment 1 — deciding value = p2
            Decision("run1", "step-2", "Interact"),    // boundary: segment 1 ends (terminal tail)
            RunEnd("run1", "done")
        );

        var scanner = new SegmentedObservationScanner(trace);
        Assert.Equal(0, scanner.CurrentSegment);

        // --- Segment 0: deciding value = p1; p2 must NOT leak ---
        var r1 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P1 == r1.Value?.GetRawText(),
            $"S4 first read segment 0: expected p1, got {r1.Value?.GetRawText()}.");

        // Second read: still p1 (stable, no walk)
        var r2 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P1 == r2.Value?.GetRawText(),
            $"S4 second read segment 0: expected p1 (stable deciding value, no leak of p2). Got {r2.Value?.GetRawText()}.");

        // Third read: still p1
        var r3 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P1 == r3.Value?.GetRawText(),
            $"S4 third read segment 0: still p1. Got {r3.Value?.GetRawText()}.");

        // --- Advance to segment 1: deciding value updates to p2 ---
        scanner.AdvanceSegment();
        Assert.Equal(1, scanner.CurrentSegment);

        var r4 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P2 == r4.Value?.GetRawText(),
            $"S4 first read segment 1: expected deciding value p2, got {r4.Value?.GetRawText()}.");

        // Second read in segment 1: still p2 (stable)
        var r5 = scanner.Next("GetPlayerPosition", null);
        Assert.True(P2 == r5.Value?.GetRawText(),
            $"S4 second read segment 1: expected p2 (stable), got {r5.Value?.GetRawText()}.");
    }

    // =========================================================================
    // S5 — gated pin uses last-at-or-before window end, not the first ever
    // =========================================================================

    /// <summary>
    /// S5: Given GetQuestSequence(65644) recorded as 0 (segment 0), 0 (segment 1), 1 (segment 2):
    /// While at segment 2, Next("GetQuestSequence", questId) returns 1 — the most recent value
    /// at-or-before the segment-2 window end — not 0 (the first-ever value).
    ///
    /// This proves LastAtOrBefore uses the most-recent matching observation, not the first.
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S5_GatedPin_UsesLastAtOrBeforeWindowEnd_NotFirstEver()
    {
        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("GetQuestSequence", """{"value":65644}""", "0"),  // segment 0
            Decision("run1", "step-1", "Interact"),
            Obs("GetQuestSequence", """{"value":65644}""", "0"),  // segment 1
            Decision("run1", "step-2", "Navigate"),
            Obs("GetQuestSequence", """{"value":65644}""", "1"),  // segment 2
            Decision("run1", "step-3", "Interact"),
            RunEnd("run1", "done")
        );

        var scanner = new SegmentedObservationScanner(trace);
        var questId = new QuestId(65644);

        // Advance to segment 2 (the segment that has value=1 inside its window)
        scanner.AdvanceSegment(); // segment 0 → 1
        scanner.AdvanceSegment(); // segment 1 → 2

        Assert.Equal(2, scanner.CurrentSegment);

        // Must return 1, not 0 (LastAtOrBefore returns the most recent)
        var result = scanner.Next("GetQuestSequence", questId);
        Assert.True("1" == result.Value?.GetRawText(),
            $"S5: in segment 2, GetQuestSequence must return 1 (most recent at-or-before window end). Got {result.Value?.GetRawText()}");
    }

    // =========================================================================
    // S6 — AdvanceSegment clamps at the last segment
    // =========================================================================

    /// <summary>
    /// S6: Given a scanner at the terminal segment (last segment), calling AdvanceSegment()
    /// again does not throw and leaves CurrentSegment unchanged.
    /// Subsequent reads still resolve to terminal-tail values.
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S6_AdvanceSegment_AtLastSegment_ClampsWithoutThrowing()
    {
        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("IsQuestComplete", """{"value":65644}""", "false"),
            Decision("run1", "step-1", "Interact"),
            Obs("IsQuestComplete", """{"value":65644}""", "true"),  // terminal tail
            RunEnd("run1", "done")
        );

        var scanner = new SegmentedObservationScanner(trace);
        var questId = new QuestId(65644);

        // One decision → two segments (segment 0 and terminal tail = segment 1)
        Assert.Equal(2, scanner.SegmentCount);

        // Advance to the terminal tail
        scanner.AdvanceSegment();
        Assert.Equal(1, scanner.CurrentSegment);

        // AdvanceSegment at the last segment must NOT throw and must NOT change CurrentSegment
        // Use Action delegate explicitly to avoid xUnit v3 Func<Task> overload inference
        // when SegmentedObservationScanner doesn't exist yet.
        Exception? ex = null;
        try { scanner.AdvanceSegment(); } catch (Exception e) { ex = e; }
        Assert.Null(ex);
        Assert.Equal(1, scanner.CurrentSegment);

        // Even after over-advancing, terminal tail values remain accessible
        var result = scanner.Next("IsQuestComplete", questId);
        Assert.True("true" == result.Value?.GetRawText(),
            $"S6: after clamped AdvanceSegment, IsQuestComplete must still return true. Got {result.Value?.GetRawText()}");

        // Another over-advance: still no throw, still no change
        Exception? ex2 = null;
        try { scanner.AdvanceSegment(); } catch (Exception e) { ex2 = e; }
        Assert.Null(ex2);
        Assert.Equal(1, scanner.CurrentSegment);
    }

    // =========================================================================
    // S7 — truly-absent pair → starvation (distinct from within-window exhaustion)
    // =========================================================================

    /// <summary>
    /// S7: Given a scanner whose trace never records GetGil anywhere, Next("GetGil", null)
    /// throws ReplayObservationStarvationException.
    ///
    /// Contrast with S4: a continuous pair exhausted within the window PINS (does not throw).
    /// Starvation is reserved for pairs never recorded at all.
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S7_TrulyAbsentPair_ThrowsStarvation_DistinctFromWithinWindowExhaustion()
    {
        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("GetPlayerZone", null, """{"value":181}"""),  // GetGil is never recorded
            Decision("run1", "step-1", "Interact"),
            RunEnd("run1", "done")
        );

        var scanner = new SegmentedObservationScanner(trace);

        // GetGil never in the trace → starvation
        ReplayObservationStarvationException? starvationEx = null;
        try { scanner.Next("GetGil", null); }
        catch (ReplayObservationStarvationException e) { starvationEx = e; }
        Assert.NotNull(starvationEx);
        Assert.Contains("GetGil", starvationEx!.Message, StringComparison.Ordinal);

        // Contrast: GetPlayerZone IS in the trace; exhausting it within the window pins, no throw
        var r1 = scanner.Next("GetPlayerZone", null);
        Assert.Equal("""{"value":181}""", r1.Value?.GetRawText());

        // Second read: exhausted in window but pair was recorded → pins (no starvation)
        Exception? noEx = null;
        try { scanner.Next("GetPlayerZone", null); } catch (Exception e) { noEx = e; }
        Assert.Null(noEx);
    }

    // =========================================================================
    // S8 — argument keying: independent per-pair cursors
    // =========================================================================

    /// <summary>
    /// S8: Given IsAetheryteAttuned(8)=false and IsAetheryteAttuned(42)=true recorded in the
    /// same segment, Next("IsAetheryteAttuned", aetheryte8) returns false and
    /// Next("IsAetheryteAttuned", aetheryte42) returns true — independent per-pair cursors.
    ///
    /// Matches the existing (method, serialized-arg) keying from ObservationScanner.
    ///
    /// RED: SegmentedObservationScanner does not exist → CS0246.
    /// </summary>
    [Fact]
    public void S8_ArgumentKeying_IndependentPerPairCursors()
    {
        var trace = BuildTrace(
            RunStart("run1", 65644),
            Obs("IsAetheryteAttuned", """{"value":8}""",  "false"),
            Obs("IsAetheryteAttuned", """{"value":42}""", "true"),
            Decision("run1", "step-1", "Interact"),
            RunEnd("run1", "done")
        );

        var scanner = new SegmentedObservationScanner(trace);
        var aetheryte8  = new AetheryteId(8);
        var aetheryte42 = new AetheryteId(42);

        // Both are in segment 0; argument keying must distinguish them
        var r8  = scanner.Next("IsAetheryteAttuned", aetheryte8);
        var r42 = scanner.Next("IsAetheryteAttuned", aetheryte42);

        Assert.Equal("false", r8.Value?.GetRawText());
        Assert.Equal("true",  r42.Value?.GetRawText());
    }

    // =========================================================================
    // Builder helpers — build TraceEvent lists matching the trace file format
    // =========================================================================

    private static IReadOnlyList<TraceEvent> BuildTrace(params TraceEvent[] events)
        => events.ToList();

    private static RunStartEvent RunStart(string runId, uint questId)
        => new(RunId: runId, QuestId: questId, QuestSchemaId: questId, At: At);

    private static RunEndEvent RunEnd(string runId, string outcome)
        => new(RunId: runId, Outcome: outcome, At: At.AddSeconds(60));

    private static DecisionEvent Decision(string runId, string stepId, string actionType)
        => new(RunId: runId, StepId: stepId, ActionType: actionType, At: At.AddSeconds(1));

    private static ObservationEvent Obs(string method, string? argJson, string valueJson)
        => new(
            RunId: "run1",
            Method: method,
            Argument: argJson is null ? null : JsonDocument.Parse(argJson).RootElement,
            Value: JsonDocument.Parse(valueJson).RootElement,
            At: At);
}
