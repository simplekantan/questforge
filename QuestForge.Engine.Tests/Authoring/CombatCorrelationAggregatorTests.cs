using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice 2: SnapshotAggregator kill-correlation tests (GWT-L1..L7).
// Covers OnEnemyKilled / OnInCombatChanged / OnQuestVariablesUpdated correlation,
// the ±500 ms window, sequence-advance correlation, and ResetDeltas baseline handling.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatCorrelationAggregatorTests
{
    private static readonly QuestId Quest65847 = new(65847);

    // T0 is epoch; each step advances by the given ms.
    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string FormatTargets(IReadOnlyDictionary<int, KillCorrelation>? targets)
    {
        if (targets is null || targets.Count == 0) return "(empty)";
        return string.Join("; ", targets.Select(kv =>
            $"[{kv.Key}] DataIds=[{string.Join(",", kv.Value.DataIds)}] Final={kv.Value.FinalValue}"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L1 — single mob ×3, sequential kills each correlating with a variable bump
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L1_SingleMobThreeKills_CorrelatesAsOneTargetFinalValue3()
    {
        /*
         * Given quest 65847, InCombat true at t=0 ms.
         * When:
         *   t=250ms OnEnemyKilled(347)  + OnQuestVariablesUpdated([1,0,0,0,0,0])
         *   t=500ms OnEnemyKilled(347)  + OnQuestVariablesUpdated([2,0,0,0,0,0])
         *   t=900ms OnEnemyKilled(347)  + OnQuestVariablesUpdated([3,0,0,0,0,0])
         * Then Current.KillCorrelatedTargets[0] == KillCorrelation([347], FinalValue=3).
         *
         * The 500 ms window: kill at t, bump at T where T-t <= 500ms → correlated.
         * V0 walks 0→1→2→3; each bump correlated to the preceding kill.
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        // t=0: InCombat true
        aggregator.OnInCombatChanged(true);

        // t=250ms: kill + bump
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 1, 0, 0, 0, 0, 0 });

        // t=500ms: kill + bump
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 2, 0, 0, 0, 0, 0 });

        // t=900ms: kill + bump
        clock.Advance(TimeSpan.FromMilliseconds(400));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 3, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(0),
            $"Expected index 0 in KillCorrelatedTargets. Got: {FormatTargets(targets)}");
        var entry = targets[0];
        Assert.Equal(3, entry.FinalValue);
        Assert.Contains(347u, entry.DataIds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L2 — AoE triple kill, single bump → data-id deduped, FinalValue from variable
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L2_AoeTripleKillSingleBump_CorrelatesAsOneTargetDeduped()
    {
        /*
         * Given t=0: InCombat true.
         * t=250ms: OnEnemyKilled(347) ×3, then OnQuestVariablesUpdated([3,0,...]).
         * Then KillCorrelatedTargets[0].DataIds == {347} (deduped), FinalValue == 3.
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnEnemyKilled(347u);
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 3, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(0),
            $"Expected index 0. Got: {FormatTargets(targets)}");
        var entry = targets[0];
        // DataIds must be a set (deduped) — exactly one unique data-id (347)
        Assert.Equal(3, entry.FinalValue);
        var uniqueDataIds = entry.DataIds.Distinct().ToList();
        Assert.Single(uniqueDataIds);
        Assert.Equal(347u, uniqueDataIds[0]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L3 — kill outside the 500 ms window is NOT correlated
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L3_KillOutsideWindow_NotCorrelated()
    {
        /*
         * Given t=0: OnEnemyKilled(999).
         * t=600ms: OnQuestVariablesUpdated([1,0,...]).
         * The gap is 600ms > 500ms → the kill is outside the backward-looking window.
         * Then KillCorrelatedTargets must be empty (index 0 absent).
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnEnemyKilled(999u);

        clock.Advance(TimeSpan.FromMilliseconds(600));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 1, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        // Either null/empty, or index 0 absent / DataIds empty
        var hasCorrelatedEntry = targets is { Count: > 0 }
            && targets.TryGetValue(0, out var e0)
            && e0.DataIds.Count > 0;

        Assert.False(hasCorrelatedEntry,
            $"Kill at t=0 with bump at t=600ms (gap 600ms > 500ms window) must NOT be correlated. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L4 — two distinct data-ids in same window for the same bump
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L4_TwoDataIdsInWindow_BothCorrelated()
    {
        /*
         * t=0: InCombat true.
         * t=250ms: OnEnemyKilled(347).
         * t=300ms: OnEnemyKilled(348).
         * t=500ms: OnQuestVariablesUpdated([2,0,...]).
         * All kills fall within [t-500ms, t] relative to the bump at t=500ms.
         * Then KillCorrelatedTargets[0].DataIds == {347, 348}, FinalValue == 2.
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnEnemyKilled(347u);

        clock.Advance(TimeSpan.FromMilliseconds(50)); // t=300ms
        aggregator.OnEnemyKilled(348u);

        clock.Advance(TimeSpan.FromMilliseconds(200)); // t=500ms
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 2, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(0),
            $"Expected index 0. Got: {FormatTargets(targets)}");
        var entry = targets[0];
        Assert.Equal(2, entry.FinalValue);
        Assert.Contains(347u, entry.DataIds);
        Assert.Contains(348u, entry.DataIds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L5 — sequence-advance combat (no variable moved) → index -1
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L5_SequenceAdvanceWithKillsInWindow_CorrelatesAtSyntheticIndex()
    {
        /*
         * t=0: InCombat true.
         * t=250ms: OnEnemyKilled(347).
         * t=500ms: OnEnemyKilled(347).
         * t=550ms: OnQuestSequenceChanged(Quest65847, newSeq=3) — no variable change, but
         *          recent kills in window → correlation under index SequenceVariableIndex (-1).
         * Then KillCorrelatedTargets[-1].FinalValue == 3.
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnEnemyKilled(347u);

        clock.Advance(TimeSpan.FromMilliseconds(250)); // t=500ms
        aggregator.OnEnemyKilled(347u);

        clock.Advance(TimeSpan.FromMilliseconds(50)); // t=550ms
        aggregator.OnQuestSequenceChanged(Quest65847, 3);

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);

        var seqIdx = SnapshotAggregator.SequenceVariableIndex; // must be -1
        Assert.Equal(-1, seqIdx);
        Assert.True(targets!.ContainsKey(seqIdx),
            $"Expected synthetic index {seqIdx} in KillCorrelatedTargets. Got: {FormatTargets(targets)}");
        Assert.Equal(3, targets[seqIdx].FinalValue);
        Assert.Contains(347u, targets[seqIdx].DataIds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L6 — ResetDeltas clears correlation but KEEPS the variable baseline
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L6_ResetDeltas_ClearsCorrelation_KeepsVariableBaseline()
    {
        /*
         * Part A — after a correlated window, ResetDeltas clears KillCorrelatedTargets.
         * Part B (decisive) — re-emitting the SAME value after ResetDeltas must NOT correlate,
         *          because the preserved baseline yields no delta. This distinguishes
         *          "baseline kept at 3" from "baseline zeroed" (a zeroed baseline would treat
         *          the re-emitted 3 as a 0→3 bump and spuriously correlate).
         * Part C — a genuine 3→4 bump still correlates against the preserved baseline.
         *
         * Sequence:
         *   t=250ms: kill(347) + V0 0→3    → KillCorrelatedTargets[0] = ([347], 3)
         *   ResetDeltas()                   → KillCorrelatedTargets cleared; baseline stays at 3
         *   t=750ms: kill(347) + V0 3 (same) → NO correlation (no delta)
         *   t=1250ms: kill(347) + V0 3→4    → correlation; FinalValue == 4
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // First window
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 3, 0, 0, 0, 0, 0 });

        // Confirm correlation before reset
        Assert.NotNull(aggregator.Current.KillCorrelatedTargets);
        Assert.True(aggregator.Current.KillCorrelatedTargets!.ContainsKey(0),
            "Precondition: index 0 should be correlated before ResetDeltas");

        // ResetDeltas
        aggregator.ResetDeltas();

        // Assert A — cleared
        var afterReset = aggregator.Current.KillCorrelatedTargets;
        var isEmpty = afterReset is null || afterReset.Count == 0
            || afterReset.Values.All(e => e.DataIds.Count == 0);
        Assert.True(isEmpty,
            $"KillCorrelatedTargets must be cleared after ResetDeltas. Got: {FormatTargets(afterReset)}");

        // Assert B (decisive) — re-emit the SAME value [3,...] with an in-window kill.
        // Preserved baseline (3) → no delta (3→3) → no correlation.
        // Zeroed baseline → spurious 0→3 correlation. The original FinalValue==4 check could
        // not tell these apart (both paths yield 4); this re-emit step can.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 3, 0, 0, 0, 0, 0 });

        var afterSameValue = aggregator.Current.KillCorrelatedTargets;
        var spuriouslyCorrelated = afterSameValue is { Count: > 0 }
            && afterSameValue.TryGetValue(0, out var eSame)
            && eSame.DataIds.Count > 0;
        Assert.False(spuriouslyCorrelated,
            "Re-emitting the same variable value [3,...] after ResetDeltas must NOT correlate " +
            "(no delta against the preserved baseline). A correlation here proves ResetDeltas " +
            $"wrongly zeroed _prevQuestVariables. Got: {FormatTargets(afterSameValue)}");

        // Assert C — a genuine bump from 3 to 4 still correlates against the preserved baseline.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 4, 0, 0, 0, 0, 0 });

        var targets2 = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets2);
        Assert.True(targets2!.ContainsKey(0),
            $"Expected index 0 after the 3→4 bump. Got: {FormatTargets(targets2)}");
        Assert.Equal(4, targets2[0].FinalValue);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L8 — resumed quest: first observation of an already-non-zero variable with a
    // stale in-window kill must NOT spuriously correlate when the production poll order
    // (variables forwarded before any kill is buffered) is followed.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L8_ResumedQuestFirstObservation_NonZeroVariable_NoSpuriousCorrelation()
    {
        /*
         * Reproduces the all-zeros-baseline concern under the real production flow:
         * PollQuestState forwards OnQuestVariablesUpdated EVERY poll, before PollCombat can
         * buffer a kill. So the FIRST observation of a resumed quest (V0 already 2) establishes
         * the baseline while _recentKills is empty → no correlation. A later unrelated kill that
         * does not move the variable must also not correlate.
         *
         * t=250ms: OnQuestVariablesUpdated([2,0,...])   → first obs, no kills buffered → baseline=2
         * t=400ms: OnEnemyKilled(999)                   → unrelated kill enters buffer
         * t=500ms: OnQuestVariablesUpdated([2,0,...])   → same value, kill in window, but no delta
         * Then KillCorrelatedTargets has no correlated entry for index 0.
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // First observation of a resumed quest — variable already non-zero, no kills yet.
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 2, 0, 0, 0, 0, 0 });

        // Unrelated kill, then the variable is re-observed unchanged within the window.
        clock.Advance(TimeSpan.FromMilliseconds(150)); // t=400ms
        aggregator.OnEnemyKilled(999u);

        clock.Advance(TimeSpan.FromMilliseconds(100)); // t=500ms
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 2, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        var hasCorrelatedEntry = targets is { Count: > 0 }
            && targets.TryGetValue(0, out var e0)
            && e0.DataIds.Count > 0;
        Assert.False(hasCorrelatedEntry,
            "A resumed quest whose first observation is already non-zero must establish the " +
            "baseline without correlating, and an unrelated later kill that does not move the " +
            $"variable must not correlate. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L7 — InCombat false→true records combat start position and zone
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_L7_InCombatFalseToTrue_RecordsCombatStartPositionAndZone()
    {
        /*
         * Given OnZoneChanged(zone=148, pos=(10,0,20)) then OnPlayerMoved((10,0,20)).
         * When OnInCombatChanged(true).
         * Then Current.CombatStartPosition == WorldPosition(10,0,20)
         *      and Current.CombatStartZone == 148.
         */

        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnZoneChanged(new ZoneId(148), new WorldPosition(10, 0, 20));
        aggregator.OnPlayerMoved(new WorldPosition(10, 0, 20));

        aggregator.OnInCombatChanged(true);

        var snapshot = aggregator.Current;
        Assert.True(snapshot.InCombat,
            "Current.InCombat should be true after OnInCombatChanged(true)");
        Assert.NotNull(snapshot.CombatStartPosition);
        Assert.Equal(10f, snapshot.CombatStartPosition!.Value.X);
        Assert.Equal(0f,  snapshot.CombatStartPosition.Value.Y);
        Assert.Equal(20f, snapshot.CombatStartPosition.Value.Z);
        Assert.Equal(148, snapshot.CombatStartZone);
    }
}
