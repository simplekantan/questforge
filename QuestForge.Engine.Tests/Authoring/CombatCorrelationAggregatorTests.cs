using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice A: SnapshotAggregator target-based span correlation tests.
//
// GWT-T1, T4, T5, T6, T8, T9 unchanged from previous revision.
//
// GWT-T2 REPLACED by T2' (seeding model — THE bug fix):
//   Pre-combat OnBattleNpcTargeted is tracked as _lastBattleNpcTarget (NO _inCombat gate).
//   OnInCombatChanged(true) seeds _spanBattleNpcTargets with _lastBattleNpcTarget (if any).
//   The real observer dedup means the target is forwarded ONCE on target-change — the
//   re-confirm call in old T2 never happens in production. T2' matches that reality.
//
// GWT-T3 REPLACED by T3' (no target before combat → no attribution):
//   _lastBattleNpcTarget is null → nothing seeded → nibble bump with no span target → null.
//
// GWT-T2b NEW (in-combat target-swap adds to span):
//   Pre-combat target 347 seeds at combat-start; in-combat 348 adds to span; both captured.
//
// GWT-T7 REPLACED by T7' (ResetDeltas clears span but _lastBattleNpcTarget persists):
//   ResetDeltas clears _spanBattleNpcTargets; _lastBattleNpcTarget survives.
//   New OnInCombatChanged(true) re-seeds from the still-current _lastBattleNpcTarget.
//
// GWT-T10 REPLACED by T10' (new span clears prior span; target changed before span B):
//   Span A target 347; target changes to 338 before span B; span B seeded with 338 only.
//
// RED causes (post-fix tests that fail today):
//   T2' — pre-combat target dropped; seeding not implemented → span empty → FAIL
//   T2b  — seeded 347 missing from span → FAIL
//   T7'  — ResetDeltas contract for _lastBattleNpcTarget not implemented → behaviour undefined → FAIL
//   T10' — depends on _lastBattleNpcTarget tracking; may FAIL or pass accidentally
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatCorrelationAggregatorTests
{
    private static readonly QuestId Quest65847 = new(65847);

    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] ZeroBaseline = [0, 0, 0, 0, 0, 0];

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
    // GWT-T1 happy path — target during span + nibble bump attributes to the target.
    //
    // Given quest 65847, baseline [0,0,0,0,0,0] established.
    // When OnInCombatChanged(true);
    //      OnBattleNpcTargeted(338);       ← in-combat target, added to span directly
    //      OnQuestVariablesUpdated([0,0x30,0,0,0,0]) (V1 high 0→3);
    //      OnInCombatChanged(false).
    // Then KillCorrelatedTargets[(1,High)] == KillCorrelation([338], 3).
    //
    // T1's target comes AFTER InCombatChanged(true) — it is an in-combat target added
    // directly to the span (not seeded). Seeding doesn't affect this path.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT1_TargetDuringSpan_NibbleBump_AttributesToTarget()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        // Establish baseline (first observation)
        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        aggregator.OnInCombatChanged(true);
        aggregator.OnBattleNpcTargeted(338u);
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0x30, 0, 0, 0, 0 }); // V1 high 0→3
        aggregator.OnInCombatChanged(false);

        var targets = aggregator.Current.KillCorrelatedTargets;
        var key = new NibbleKey(1, NibbleHalf.High);
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(key),
            $"Expected (1,High) in KillCorrelatedTargets. Got: {FormatTargets(targets)}");
        var entry = targets[key];
        Assert.Equal(3, entry.FinalValue);
        Assert.Single(entry.DataIds);
        Assert.Contains(338u, entry.DataIds);
        Assert.False(targets.ContainsKey(new NibbleKey(1, NibbleHalf.Low)),
            $"(1,Low) must be absent — only high nibble bumped. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T2' (THE BUG FIX) — pre-combat target is captured via seeding.
    //
    // Real observer forwards OnBattleNpcTargeted ONCE on target-change (dedup).
    // The old code gated on _inCombat, so the single pre-combat forward was dropped.
    //
    // Given baseline [0,...] established.
    // When OnBattleNpcTargeted(347)    ← NOT in combat; tracked as _lastBattleNpcTarget
    //      OnInCombatChanged(true)     ← seeds _spanBattleNpcTargets with 347
    //      OnQuestVariablesUpdated([0x03,0,...]) (V0 low 0→3).
    // Then KillCorrelatedTargets[(0,Low)] == KillCorrelation([347], 3).
    //
    // FAILS TODAY: pre-combat target dropped; span empty; KillCorrelatedTargets null.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT2Prime_PreCombatTarget_SeededAtCombatStart_IsAttributed()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        // Single pre-combat target — the ONLY call, like the real observer
        aggregator.OnBattleNpcTargeted(347u);

        // InCombatChanged(true) must seed _spanBattleNpcTargets with _lastBattleNpcTarget (347)
        aggregator.OnInCombatChanged(true);

        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 }); // V0 low 0→3

        var targets = aggregator.Current.KillCorrelatedTargets;
        var lowKey = new NibbleKey(0, NibbleHalf.Low);

        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(lowKey),
            $"Pre-combat target must be seeded into span at combat-start. Got: {FormatTargets(targets)}");
        Assert.Equal(3, targets[lowKey].FinalValue);
        Assert.Contains(347u, targets[lowKey].DataIds);
        Assert.False(targets.ContainsKey(new NibbleKey(0, NibbleHalf.High)),
            $"(0,High) must be absent. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T3' — no target before combat → _lastBattleNpcTarget null → nothing seeded.
    //
    // Given baseline; OnInCombatChanged(true) with NO prior OnBattleNpcTargeted
    //      (_lastBattleNpcTarget is null → nothing seeded);
    //      OnQuestVariablesUpdated([0x01,0,...]) (V0 low 0→1).
    // Then KillCorrelatedTargets is null/empty (nibble bumped but span has no target).
    //
    // Negative path: confirms seeding does NOT invent a target from thin air.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT3Prime_NoPriorTarget_NothingSeeded_NoCorrelation()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        // No OnBattleNpcTargeted before InCombatChanged(true) — _lastBattleNpcTarget is null
        aggregator.OnInCombatChanged(true);

        // Nibble bumps but no target in span (nothing was seeded)
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x01, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.False(HasCorrelatedEntry(targets, new NibbleKey(0, NibbleHalf.Low)),
            $"No prior target must yield no correlation. Got: {FormatTargets(targets)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T2b — in-combat target-swap adds to span alongside seeded pre-combat target.
    //
    // Given baseline; OnBattleNpcTargeted(347) (pre-combat, seeds 347 at combat-start);
    //      OnInCombatChanged(true)              ← 347 seeded;
    //      OnBattleNpcTargeted(348)             ← different mob mid-fight, added in-combat;
    //      OnQuestVariablesUpdated([0x03,0,...]) (V0 low 0→3).
    // Then [(0,Low)].DataIds == [347, 348] (sorted).
    //
    // Note: observer dedup means we DON'T call 347 again — 347 is only present via seeding.
    // FAILS TODAY: 347 not seeded → span only contains 348 → DataIds == [348].
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT2b_InCombatTargetSwap_BothSeededAndInCombatTargetCaptured()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        // Pre-combat target — seeds 347 at combat-start
        aggregator.OnBattleNpcTargeted(347u);
        aggregator.OnInCombatChanged(true); // seeds 347

        // Mid-fight target swap to a different mob — added in-combat
        aggregator.OnBattleNpcTargeted(348u);

        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 }); // V0 low 0→3

        var targets = aggregator.Current.KillCorrelatedTargets;
        var lowKey = new NibbleKey(0, NibbleHalf.Low);

        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(lowKey),
            $"Expected (0,Low). Got: {FormatTargets(targets)}");

        var ids = targets[lowKey].DataIds.OrderBy(x => x).ToList();
        Assert.True(ids.SequenceEqual(new[] { 347u, 348u }),
            $"(0,Low) DataIds must be [347,348] (seeded + in-combat). Got: [{string.Join(",", ids)}]");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T4 successive bumps in one span → highest reached value (overwrite semantics).
    //
    // Given baseline; OnInCombatChanged(true); OnBattleNpcTargeted(347).
    // When bumps: 0x01, 0x02, 0x03 (V0 low 1→2→3).
    // Then [(0,Low)] == ([347], 3) — FinalValue = 3, the latest.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT4_SuccessiveBumps_HighestReachedValue()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);
        aggregator.OnInCombatChanged(true);
        aggregator.OnBattleNpcTargeted(347u);

        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x01, 0, 0, 0, 0, 0 });
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x02, 0, 0, 0, 0, 0 });
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        var lowKey = new NibbleKey(0, NibbleHalf.Low);
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(lowKey),
            $"Expected (0,Low). Got: {FormatTargets(targets)}");
        Assert.Equal(3, targets[lowKey].FinalValue);
        Assert.Contains(347u, targets[lowKey].DataIds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T5 both nibbles move in one write, single target.
    //
    // Given baseline [0x02,0,...]; OnInCombatChanged(true); OnBattleNpcTargeted(347).
    // When OnQuestVariablesUpdated([0x13,0,...]) (low 2→3 up, high 0→1 up).
    // Then BOTH (0,Low) (Final=3) and (0,High) (Final=1) present, each DataIds==[347].
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT5_BothNibblesInOneWrite_SingleTarget_TwoKeys()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        // Baseline at 0x02 so 0x13 write is a genuine bump on both nibbles
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x02, 0, 0, 0, 0, 0 });
        aggregator.OnInCombatChanged(true);
        aggregator.OnBattleNpcTargeted(347u);

        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x13, 0, 0, 0, 0, 0 }); // low 2→3, high 0→1

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);

        var lowKey  = new NibbleKey(0, NibbleHalf.Low);
        var highKey = new NibbleKey(0, NibbleHalf.High);

        Assert.True(targets!.ContainsKey(lowKey),
            $"Expected (0,Low). Got: {FormatTargets(targets)}");
        Assert.Equal(3, targets[lowKey].FinalValue);
        Assert.Contains(347u, targets[lowKey].DataIds);

        Assert.True(targets.ContainsKey(highKey),
            $"Expected (0,High). Got: {FormatTargets(targets)}");
        Assert.Equal(1, targets[highKey].FinalValue);
        Assert.Contains(347u, targets[highKey].DataIds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T6 mixed-pack span → both targets on every bumped nibble (ambiguity raw data).
    //
    // Given baseline; OnInCombatChanged(true);
    //      OnBattleNpcTargeted(347); OnBattleNpcTargeted(49) (target swap mid-fight);
    //      OnQuestVariablesUpdated([0x03,0,...])  (V0 low 0→3);
    //      OnQuestVariablesUpdated([0x03,0x03,...]) (V1 low 0→3).
    // Then (0,Low) and (1,Low) BOTH present, each DataIds==[49,347] (sorted).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT6_MixedPackSpan_BothTargetsOnEveryNibble()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);
        aggregator.OnInCombatChanged(true);
        aggregator.OnBattleNpcTargeted(347u);
        aggregator.OnBattleNpcTargeted(49u); // target swap

        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });        // V0 low 0→3
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0x03, 0, 0, 0, 0 });    // V1 low 0→3

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);

        var key0Low = new NibbleKey(0, NibbleHalf.Low);
        var key1Low = new NibbleKey(1, NibbleHalf.Low);

        Assert.True(targets!.ContainsKey(key0Low),
            $"Expected (0,Low). Got: {FormatTargets(targets)}");
        Assert.True(targets.ContainsKey(key1Low),
            $"Expected (1,Low). Got: {FormatTargets(targets)}");

        // Both nibbles carry the SAME span target-set
        var ids0 = targets[key0Low].DataIds.OrderBy(x => x).ToList();
        var ids1 = targets[key1Low].DataIds.OrderBy(x => x).ToList();
        Assert.True(ids0.SequenceEqual(new[] { 49u, 347u }),
            $"(0,Low) DataIds must be [49,347]. Got: [{string.Join(",", ids0)}]");
        Assert.True(ids1.SequenceEqual(new[] { 49u, 347u }),
            $"(1,Low) DataIds must be [49,347]. Got: [{string.Join(",", ids1)}]");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T7' ResetDeltas clears span sets; _lastBattleNpcTarget persists.
    //
    // Contract decision: _lastBattleNpcTarget persists across ResetDeltas (like _prevQuestVariables).
    // Rationale: it represents "the last mob the player was targeting" — a persistent observation
    // about the player's current state, not a per-window delta signal. Clearing it would cause
    // a pre-combat target to be lost if ResetDeltas runs between targeting and combat-start
    // (e.g. authoring's before-snapshot capture). _prevQuestVariables uses the same rationale.
    //
    // Part A: correlated (0,Low)=([347],3) exists; ResetDeltas() → span cleared → empty.
    // Part B: OnInCombatChanged(true) → re-seeds with persisted _lastBattleNpcTarget (347);
    //         OnQuestVariablesUpdated([0x03,...]) — baseline preserved at 0x03 → no delta → empty.
    // Part C: genuine 0x03→0x04 (low 3→4) → [(0,Low)]=([347],4) (347 re-seeded from persistent last-target).
    //
    // FAILS TODAY: _lastBattleNpcTarget not tracked; seeding not implemented.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT7Prime_ResetDeltas_ClearsSpan_LastTargetPersists_ReseededOnNewCombat()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline);

        // Pre-combat target — sets _lastBattleNpcTarget = 347
        aggregator.OnBattleNpcTargeted(347u);
        aggregator.OnInCombatChanged(true); // seeds 347
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });

        var lowKey = new NibbleKey(0, NibbleHalf.Low);
        Assert.True(HasCorrelatedEntry(aggregator.Current.KillCorrelatedTargets, lowKey),
            "Precondition: (0,Low) must be correlated before ResetDeltas");

        // Authoring's before-snapshot capture
        aggregator.ResetDeltas();

        // Part A: span cleared
        var afterReset = aggregator.Current.KillCorrelatedTargets;
        Assert.False(HasCorrelatedEntry(afterReset, lowKey),
            $"KillCorrelatedTargets must be empty after ResetDeltas. Got: {FormatTargets(afterReset)}");

        // Part B: new combat span; _lastBattleNpcTarget (347) still persists → re-seeded
        // baseline preserved at 0x03 → re-emitting same value yields no bump
        aggregator.OnInCombatChanged(false);
        aggregator.OnInCombatChanged(true); // re-seeds with persisted 347
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 }); // 3→3, no change

        Assert.False(HasCorrelatedEntry(aggregator.Current.KillCorrelatedTargets, lowKey),
            "Re-emitting same value after ResetDeltas must not correlate (baseline preserved at 0x03). " +
            $"Got: {FormatTargets(aggregator.Current.KillCorrelatedTargets)}");

        // Part C: genuine 0x03→0x04 → correlates; 347 re-seeded from persistent _lastBattleNpcTarget
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x04, 0, 0, 0, 0, 0 });
        var targets2 = aggregator.Current.KillCorrelatedTargets;
        Assert.True(HasCorrelatedEntry(targets2, lowKey),
            $"Expected (0,Low) after genuine 3→4 bump with re-seeded 347. Got: {FormatTargets(targets2)}");
        Assert.Equal(4, targets2![lowKey].FinalValue);
        Assert.Contains(347u, targets2[lowKey].DataIds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T8 InCombat false→true records combat start position/zone. UNCHANGED.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT8_InCombatFalseToTrue_RecordsCombatStartPositionAndZone()
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
    // GWT-T9 resumed-quest first observation: no spurious attribution.
    //
    // Given OnInCombatChanged(true); OnBattleNpcTargeted(338);
    //      OnQuestVariablesUpdated([0,0x30,0,...]) as the FIRST observation (no prior baseline).
    // Then KillCorrelatedTargets is null (first obs = baseline only; no bump).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT9_ResumedQuest_FirstObservation_NoSpuriousAttribution()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnInCombatChanged(true);
        aggregator.OnBattleNpcTargeted(338u);

        // FIRST observation — must establish baseline only, no bump
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0x30, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.Null(targets);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-T10' new combat span clears prior span; _lastBattleNpcTarget updated between spans.
    //
    // Span A: pre-combat target 347 → seeded; bump V0 low → (0,Low)=([347],3).
    //         OnInCombatChanged(false).
    //         Player now targets 338 (target changes between fights).
    // Span B: OnInCombatChanged(true) → seeds with current _lastBattleNpcTarget (338, NOT 347);
    //         OnBattleNpcTargeted(338) is NOT called again (dedup);
    //         Bump V1 low → (1,Low)=([338],3).
    // Then: (1,Low)=([338],3); 347 absent; (0,Low) absent.
    //
    // FAILS TODAY: _lastBattleNpcTarget not tracked; seeding not implemented.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtT10Prime_NewSpan_ClearsPriorSpan_LastTargetUpdatedBetweenSpans()
    {
        var clock = new FakeClock(Epoch);
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        aggregator.OnQuestVariablesUpdated(Quest65847, ZeroBaseline); // baseline

        // Span A: pre-combat target 347
        aggregator.OnBattleNpcTargeted(347u);
        aggregator.OnInCombatChanged(true); // seeds 347
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0, 0, 0, 0, 0 });
        aggregator.OnInCombatChanged(false);

        // Between spans: player retargets to a different mob (338)
        // _lastBattleNpcTarget must be updated to 338 (no _inCombat gate on tracking)
        aggregator.OnBattleNpcTargeted(338u);

        // Span B: seeds with current _lastBattleNpcTarget (338, NOT the stale 347)
        aggregator.OnInCombatChanged(true); // clears span A sets; seeds 338
        // V1 low 0→3; baseline is [0x03,0,...] so only V1 index 1 is new
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0x03, 0x03, 0, 0, 0, 0 });

        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);

        var key1Low = new NibbleKey(1, NibbleHalf.Low);
        Assert.True(targets!.ContainsKey(key1Low),
            $"Expected only (1,Low) from span B with 338. Got: {FormatTargets(targets)}");
        Assert.Contains(338u, targets[key1Low].DataIds);
        Assert.Equal(3, targets[key1Low].FinalValue);

        // Span A's nibble and target must be gone
        Assert.False(targets.ContainsKey(new NibbleKey(0, NibbleHalf.Low)),
            $"(0,Low) from span A must be cleared by the new span. Got: {FormatTargets(targets)}");
        Assert.False(targets[key1Low].DataIds.Contains(347u),
            $"347 from span A must not appear in span B's entry. Got: [{string.Join(",", targets[key1Low].DataIds)}]");
    }
}
