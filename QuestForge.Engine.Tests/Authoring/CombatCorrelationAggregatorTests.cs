using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice A: SnapshotAggregator nibble-level bidirectional correlation tests.
// Rewrites GWT-L1..L8 (byte, backward-only) → GWT-L1'..L9' (nibble, symmetric).
//
// Baseline-only first observation: the FIRST OnQuestVariablesUpdated establishes the
// baseline and emits no bump / no correlation (production polls quest variables every
// heartbeat from author-mode start, so the first poll is always the pre-fight state).
// Tests that exercise a bump-under-test must establish a non-null baseline first.
//
// DELETED (byte-level, obsolete):
//   GWT-L1  — kill-before-bump order (opposite of in-game reality)
//   GWT-L2  — two DataIds in same byte window (replaced by L2'/L4')
//   GWT-L3  — kill outside backward-only 500 ms window (replaced by L3' symmetric)
//   GWT-L4  — two distinct DataIds in one byte bump (replaced by L4' nibble version)
//   GWT-L5  — sequence-advance synthetic index -1 (SequenceVariableIndex DELETED)
//   GWT-L6  — ResetDeltas byte (replaced by L6' nibble + bump-buffer clear)
//   GWT-L8  — resumed-quest byte (replaced by L8' nibble)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatCorrelationAggregatorTests
{
    private static readonly QuestId Quest65847 = new(65847);

    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] ZeroBaseline = { 0, 0, 0, 0, 0, 0 };

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string FormatTargets(IReadOnlyDictionary<NibbleKey, KillCorrelation>? targets)
    {
        if (targets is null || targets.Count == 0) return "(empty)";
        return string.Join("; ", targets.Select(kv =>
            $"[V{kv.Key.VarIndex}-{kv.Key.Half}] DataIds=[{string.Join(",", kv.Value.DataIds)}] Final={kv.Value.FinalValue}"));
    }

    private static bool HasCorrelatedEntry(IReadOnlyDictionary<NibbleKey, KillCorrelation>? targets, NibbleKey key)
        => targets is { Count: > 0 }
           && targets.TryGetValue(key, out var e)
           && e.DataIds.Count > 0;

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L1' — bump-before-kill (the real in-game order) correlates to low nibble.
    //
    // THE decisive test: the merged design fails this because its correlation only
    // triggers on bump-arrival (backward-only) — a kill arriving AFTER its bump is
    // missed. The nibble bidirectional design correlates on kill-arrival too.
    //
    // In-game order: PollQuestState (variable bump) BEFORE PollCombat (kill).
    // Round 1 @t=250: bump V0 0x00→0x01 (FIRST obs → baseline only, no correlation),
    //                 THEN kill 347 (buffered, no bump yet to match).
    // Round 2 @t=500: bump 0x01→0x02 (correlates round-1 kill@250 via the ±500 ms window),
    //                 THEN kill 347.
    // Round 3 @t=900: bump 0x02→0x03 (Final=3), THEN kill 347.
    // Expected: KillCorrelatedTargets[(0, Low)] == KillCorrelation([347], 3).
    //
    // NOTE: round-1's [0x01] is now the baseline (no direct correlation), but the
    // symmetric window still captures the round-1 kill via the round-2 bump.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL1_BumpBeforeKill_RealInGameOrder_CorrelatesLowNibble()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        // t=0: InCombat true
        aggregator.OnInCombatChanged(true);

        // Round 1 @t=250ms: bump then kill (in-game order). [0x01] is the FIRST obs → baseline.
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x01, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);

        // Round 2 @t=500ms: bump then kill. 0x01→0x02 bump@500 correlates kill347@250 (Δ=250ms).
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x02, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);

        // Round 3 @t=900ms: bump then kill. Final reaches 3.
        clock.Advance(TimeSpan.FromMilliseconds(400));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);

        var targets = aggregator.Current.KillCorrelatedTargets;
        var key = new NibbleKey(0, NibbleHalf.Low);
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(key),
            $"Expected (0,Low) in KillCorrelatedTargets. Got: {FormatTargets(targets)}");
        var entry = targets[key];
        Assert.Equal(3, entry.FinalValue);
        Assert.Contains(347u, entry.DataIds);
        // No high-nibble key from these bumps (high nibble stayed 0)
        Assert.False(targets.ContainsKey(new NibbleKey(0, NibbleHalf.High)),
            $"High nibble should not be correlated when only low nibble incremented. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L2' — AoE triple kill, single low-nibble bump → deduped DataId, FinalValue=3
    //
    // Baseline-only first observation: a [0,0,0,0,0,0] poll establishes the baseline
    // FIRST (the realistic pre-fight state), THEN the AoE write 0x00→0x03 is a normal
    // bump that correlates the three same-tick kills.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL2_AoeTripleKill_SingleLowNibbleBump_DeduplicatesDataId()
    {
        /*
         * t=0: InCombat true; establish baseline [0,0,0,0,0,0] (pre-fight poll).
         * t=250ms: bump 0x00→0x03 then OnEnemyKilled(347) ×3 (same tick).
         * Then (0,Low).DataIds deduped == {347}, FinalValue == 3.
         */
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // FIRST observation establishes the pre-fight baseline (no bump, no correlation).
        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);
        aggregator.OnEnemyKilled(347u);
        aggregator.OnEnemyKilled(347u);

        var targets = aggregator.Current.KillCorrelatedTargets;
        var key = new NibbleKey(0, NibbleHalf.Low);
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(key),
            $"Expected (0,Low). Got: {FormatTargets(targets)}");
        var entry = targets[key];
        Assert.Equal(3, entry.FinalValue);
        var unique = entry.DataIds.Distinct().ToList();
        Assert.Single(unique);
        Assert.Equal(347u, unique[0]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L3' — kill outside the symmetric ±500 ms window not correlated (both directions).
    // Kill→bump gap > 500 ms: bump at t=600ms, kill at t=0ms (|600-0|=600 > 500).
    // Bump→kill gap > 500 ms: bump at t=0ms, kill at t=600ms (|0-600|=600 > 500).
    //
    // A [0,...] baseline is established first so the bump-under-test is not the first obs.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL3_KillBefore_OutsideSymmetricWindow_NotCorrelated()
    {
        /*
         * Baseline [0,...] @t=0; kill at t=0, bump at t=600ms — 600ms > 500ms window → not correlated.
         */
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        // Establish baseline so the t=600ms write is a genuine bump, not the first obs.
        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        aggregator.OnEnemyKilled(999u);

        clock.Advance(TimeSpan.FromMilliseconds(600));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x01, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.False(HasCorrelatedEntry(targets, new NibbleKey(0, NibbleHalf.Low)),
            $"Kill @t=0 with bump @t=600ms (600ms > 500ms window) must NOT be correlated. Got: {FormatTargets(targets)}");
    }

    [Fact]
    public void GwtL3_BumpBefore_OutsideSymmetricWindow_NotCorrelated()
    {
        /*
         * Baseline [0,...] @t=0; bump at t=0, kill at t=600ms — 600ms > 500ms window → not correlated.
         * Both the baseline and the bump are emitted at t=0 (the baseline first, then the bump).
         */
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        // Establish baseline so [0x01] is a genuine bump, not the first obs.
        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x01, 0, 0, 0, 0, 0 });

        clock.Advance(TimeSpan.FromMilliseconds(600));
        aggregator.OnEnemyKilled(999u);

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.False(HasCorrelatedEntry(targets, new NibbleKey(0, NibbleHalf.Low)),
            $"Bump @t=0 with kill @t=600ms (600ms > 500ms symmetric window) must NOT be correlated. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L4' — both nibbles move in one write → two NibbleKeys produced.
    //
    // Baseline 0x02; kill 347 @t=250ms; then byte write 0x13
    // (low 2→3 up, high 0→1 up) → both (0,Low) Final=3 and (0,High) Final=1, each {347}.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL4_BothNibblesInOneWrite_ProducesTwoNibbleKeys()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // Establish baseline at 0x02 (first obs → baseline only)
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x02, 0, 0, 0, 0, 0 });

        // kill 347, then byte write moving both nibbles
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x13, 0, 0, 0, 0, 0 }); // low 2→3, high 0→1

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);

        var lowKey  = new NibbleKey(0, NibbleHalf.Low);
        var highKey = new NibbleKey(0, NibbleHalf.High);

        Assert.True(targets!.ContainsKey(lowKey),
            $"Expected (0,Low) from low nibble 2→3. Got: {FormatTargets(targets)}");
        Assert.Equal(3, targets[lowKey].FinalValue);
        Assert.Contains(347u, targets[lowKey].DataIds);

        Assert.True(targets.ContainsKey(highKey),
            $"Expected (0,High) from high nibble 0→1. Got: {FormatTargets(targets)}");
        Assert.Equal(1, targets[highKey].FinalValue);
        Assert.Contains(347u, targets[highKey].DataIds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L5' — high-nibble objective only: V1 low unchanged, V1 high bumped.
    //
    // Baseline [0, 0x02, ...]; t=0 InCombat; t=250: bump [0, 0x32, ...] then kill 338.
    // V1 low: 2→2 (unchanged) → no (1,Low); V1 high: 0→3 up → (1,High) Final=3.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL5_HighNibbleObjective_CorrectIndexAndHalf()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // Establish baseline at [0, 0x02, ...] (first obs → baseline only)
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0x02, 0, 0, 0, 0 });

        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0x32, 0, 0, 0, 0 }); // V1 high 0→3, low 2→2
        aggregator.OnEnemyKilled(338u);

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);

        var highKey = new NibbleKey(1, NibbleHalf.High);
        Assert.True(targets!.ContainsKey(highKey),
            $"Expected (1,High) from V1 high nibble 0→3. Got: {FormatTargets(targets)}");
        Assert.Equal(3, targets[highKey].FinalValue);
        Assert.Contains(338u, targets[highKey].DataIds);

        // Low nibble of V1 did not change (2→2) → no (1,Low)
        Assert.False(HasCorrelatedEntry(targets, new NibbleKey(1, NibbleHalf.Low)),
            $"(1,Low) must be absent (low nibble did not change). Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L6' — ResetDeltas clears correlation AND the nibble bump buffer, keeps baseline.
    //
    // Part A: after a correlated window, ResetDeltas clears KillCorrelatedTargets.
    // Part B (decisive): re-emitting same value [0x03,...] with in-window kill → NO correlation
    //   (baseline preserved at 0x03; no nibble delta). If baseline were zeroed, 0→3 would
    //   spuriously correlate — this part distinguishes the two behaviors.
    // Part C: genuine 0x03→0x04 (low 3→4) → correlates, Final=4.
    // Part D: bump buffer cleared: a bump emitted before ResetDeltas should not correlate
    //   a kill emitted after ResetDeltas.
    //
    // A [0,...] baseline is established first so the 0x00→0x03 write is a genuine bump.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL6_ResetDeltas_ClearsCorrelationAndBumpBuffer_KeepsBaseline()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // Establish baseline so the first 0x03 write is a genuine bump, not the first obs.
        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        // First window: 0x00→0x03, kill 347
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);

        // Confirm pre-reset correlation
        var lowKey = new NibbleKey(0, NibbleHalf.Low);
        Assert.True(HasCorrelatedEntry(aggregator.Current.KillCorrelatedTargets, lowKey),
            "Precondition: (0,Low) should be correlated before ResetDeltas");

        // ResetDeltas
        aggregator.ResetDeltas();

        // Part A: cleared
        var afterReset = aggregator.Current.KillCorrelatedTargets;
        Assert.False(HasCorrelatedEntry(afterReset, lowKey),
            $"KillCorrelatedTargets must be cleared after ResetDeltas. Got: {FormatTargets(afterReset)}");

        // Part B (decisive): re-emit same value with in-window kill — baseline preserved → no delta
        clock.Advance(TimeSpan.FromMilliseconds(500));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 }); // 3→3, no change

        Assert.False(HasCorrelatedEntry(aggregator.Current.KillCorrelatedTargets, lowKey),
            "Re-emitting the same value [0x03,...] after ResetDeltas must NOT correlate " +
            $"(baseline preserved at 0x03; no nibble delta). Got: {FormatTargets(aggregator.Current.KillCorrelatedTargets)}");

        // Part C: genuine 0x03→0x04 (low 3→4) → correlates
        clock.Advance(TimeSpan.FromMilliseconds(500));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x04, 0, 0, 0, 0, 0 });

        var targets2 = aggregator.Current.KillCorrelatedTargets;
        Assert.True(HasCorrelatedEntry(targets2, lowKey),
            $"Expected (0,Low) after genuine 3→4 bump. Got: {FormatTargets(targets2)}");
        Assert.Equal(4, targets2![lowKey].FinalValue);

        // Part D: bump buffer cleared — a bump immediately before ResetDeltas should not
        // correlate a kill immediately after ResetDeltas (even within the 500 ms window).
        var clock2 = new FakeClock(Epoch);
        var agg2 = new SnapshotAggregator(Quest65847, clock2);
        agg2.OnInCombatChanged(true);
        // establish baseline, then emit bump (t=0)
        agg2.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);
        agg2.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x01, 0, 0, 0, 0, 0 });
        // ResetDeltas immediately — clears bump buffer
        agg2.ResetDeltas();
        // kill arrives in-window relative to the old bump (t+100ms)
        clock2.Advance(TimeSpan.FromMilliseconds(100));
        agg2.OnEnemyKilled(347u);
        // no further bump → correlation pass on kill finds no buffered bumps
        Assert.False(HasCorrelatedEntry(agg2.Current.KillCorrelatedTargets, new NibbleKey(0, NibbleHalf.Low)),
            "A kill arriving after ResetDeltas must not correlate against a bump that was emitted before ResetDeltas " +
            $"(bump buffer must be cleared). Got: {FormatTargets(agg2.Current.KillCorrelatedTargets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L7' — InCombat false→true records combat start position and zone. UNCHANGED.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL7_InCombatFalseToTrue_RecordsCombatStartPositionAndZone()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnZoneChanged(new ZoneId(148), new WorldPosition(10, 0, 20));
        aggregator.OnPlayerMoved(new WorldPosition(10, 0, 20));

        aggregator.OnInCombatChanged(true);

        var snapshot = aggregator.Current;
        Assert.True(snapshot.InCombat);
        Assert.NotNull(snapshot.CombatStartPosition);
        Assert.Equal(10f, snapshot.CombatStartPosition!.Value.X);
        Assert.Equal(0f,  snapshot.CombatStartPosition.Value.Y);
        Assert.Equal(20f, snapshot.CombatStartPosition.Value.Z);
        Assert.Equal(148, snapshot.CombatStartZone);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L8' — resumed-quest first observation (production order): no spurious correlation.
    //
    // Baseline-only first observation: the first OnQuestVariablesUpdated([0x02,...]) only
    // sets the baseline (no bump, no correlation). A later unrelated kill (999) finds no
    // buffered bump, and the unchanged re-emit produces no delta → no correlation at all.
    //
    // Production order: PollQuestState BEFORE PollCombat.
    // At t=250: first OnQuestVariablesUpdated([0x02,...]) → baseline=0x02 (no bump).
    // At t=400: unrelated kill 999 arrives (no buffered bump).
    // At t=500: OnQuestVariablesUpdated([0x02,...]) unchanged → no delta → no correlation.
    // Then no (0,Low) or (0,High) correlated entry.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL8_ResumedQuestFirstObservation_NoSpuriousCorrelation()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // First obs — baseline only (no bump, no correlation)
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x02, 0, 0, 0, 0, 0 });

        // Unrelated kill enters buffer
        clock.Advance(TimeSpan.FromMilliseconds(150));
        aggregator.OnEnemyKilled(999u);

        // Same value re-emitted — no nibble delta
        clock.Advance(TimeSpan.FromMilliseconds(100));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x02, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.False(HasCorrelatedEntry(targets, new NibbleKey(0, NibbleHalf.Low)),
            $"Resumed-quest first observation must not create spurious (0,Low) correlation. Got: {FormatTargets(targets)}");
        Assert.False(HasCorrelatedEntry(targets, new NibbleKey(0, NibbleHalf.High)),
            $"Resumed-quest first observation must not create spurious (0,High) correlation. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-L9' — nibble decrement (completion reset) does not correlate and does not
    //           erase the prior correlated entry.
    //
    // Build up (0,Low)=([347],3) from the happy path (with a baseline poll first).
    // Then emit OnEnemyKilled(347) then OnQuestVariablesUpdated([0x00,...]) (low 3→0, decrement).
    // Expected: (0,Low) still ([347],3); decrement is not a correlation event.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtL9_NibbleDecrement_CompletionReset_DoesNotCorrelateAndDoesNotErasePrior()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);

        // Establish baseline so the 0x01 write is a genuine bump, not the first obs.
        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        // Build up 3 correlated kills (bump-before-kill order as per L1')
        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x01, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x02, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);

        clock.Advance(TimeSpan.FromMilliseconds(400));
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });
        aggregator.OnEnemyKilled(347u);

        var lowKey = new NibbleKey(0, NibbleHalf.Low);
        Assert.True(HasCorrelatedEntry(aggregator.Current.KillCorrelatedTargets, lowKey),
            "Precondition: (0,Low)=([347],3) must be established before decrement test");

        // Completion reset: all bytes → 0x00 (low 3→0 decrement, strict-increase rule: not a bump)
        clock.Advance(TimeSpan.FromMilliseconds(100));
        aggregator.OnEnemyKilled(347u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x00, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        // Prior correlation must still be present unchanged
        Assert.True(targets is { Count: > 0 } && targets.ContainsKey(lowKey),
            $"(0,Low) must still be present after decrement. Got: {FormatTargets(targets)}");
        Assert.Equal(3, targets![lowKey].FinalValue);
        Assert.Contains(347u, targets[lowKey].DataIds);

        // No new high-nibble entry from the decrement
        Assert.False(HasCorrelatedEntry(targets, new NibbleKey(0, NibbleHalf.High)),
            $"Decrement must not produce a (0,High) entry. Got: {FormatTargets(targets)}");
    }
}
