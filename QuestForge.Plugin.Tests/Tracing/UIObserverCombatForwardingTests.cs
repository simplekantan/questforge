using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// Slice A: UIObserver target-attribution forwarding tests (§6.4).
//
// Replaces GWT-U-FWD-1 (kill→aggregator forwarding) with target-based tests:
//   GWT-U-FWD-3 — hostile target → OnBattleNpcTargeted forwarded to aggregator
//   GWT-U-T1    — GetBattleNpcTarget() → GetTarget value shape {baseId, kind:"hostile"}
//   GWT-U-T2    — EventNpc target → bare-uint GetTarget + OnInteraction (NOT OnBattleNpcTargeted)
//   GWT-U-T2B   — BattleNpc hard target does NOT forward OnInteraction (D2 no-corruption, post-split)
//   GWT-U-T3    — PollCombat still writes EnemyKilled but no longer creates KillCorrelatedTargets
//   GWT-U-T4    — non-combat NPC capture (PollTargetNpc) runs EVERY-FRAME, not throttled to heartbeat
//
// GWT-U-FWD-2 (InCombat forwarding) is KEPT unchanged.
//
// Poller split (refactor): PollBattleNpcTarget runs at heartbeat immediately after PollCombat
// (so OnInCombatChanged(true) precedes OnBattleNpcTargeted); PollTargetNpc (aetheryte +
// interactable-NPC) runs every-frame with a BattleNpc-target guard so a hostile is never routed
// to OnInteraction.
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Tracing;

// ─── FakeTargetProbe ─────────────────────────────────────────────────────────

/// <summary>
/// Controllable in-memory implementation of <see cref="ITargetProbe"/>.
/// Each probe method returns whatever the test sets; null by default.
/// The discriminator (BattleNpc vs EventNpc vs Aetheryte) is the method chosen, not a flag.
/// </summary>
public sealed class FakeTargetProbe : ITargetProbe
{
    private (uint BaseId, float X, float Y, float Z, int Zone)? _interactableNpcTarget;
    private (uint BaseId, float X, float Y, float Z, int Zone)? _interactableNpcPreviousTarget;
    private (uint BaseId, float X, float Y, float Z)? _aetheryteTarget;
    private (uint BaseId, float X, float Y, float Z, int Zone)? _battleNpcTarget;

    public void SetInteractableNpcTarget((uint BaseId, float X, float Y, float Z, int Zone)? value)
        => _interactableNpcTarget = value;

    public void SetInteractableNpcPreviousTarget((uint BaseId, float X, float Y, float Z, int Zone)? value)
        => _interactableNpcPreviousTarget = value;

    public void SetAetheryteTarget((uint BaseId, float X, float Y, float Z)? value)
        => _aetheryteTarget = value;

    /// <summary>
    /// Sets the BattleNpc hard target. The builder's DalamudTargetProbe filters on
    /// ObjectKind == BattleNpc; here the test simply sets what it wants to return.
    /// </summary>
    public void SetBattleNpcTarget((uint BaseId, float X, float Y, float Z, int Zone)? value)
        => _battleNpcTarget = value;

    // ITargetProbe
    public (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcTarget()
        => _interactableNpcTarget;

    public (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcPreviousTarget()
        => _interactableNpcPreviousTarget;

    public (uint BaseId, float X, float Y, float Z)? GetAetheryteTarget()
        => _aetheryteTarget;

    public (uint BaseId, float X, float Y, float Z, int Zone)? GetBattleNpcTarget()
        => _battleNpcTarget;
}

// ─────────────────────────────────────────────────────────────────────────────
// Test class
// ─────────────────────────────────────────────────────────────────────────────

public sealed class UIObserverCombatForwardingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 20, 9, 0, 0, TimeSpan.Zero);
    private const string ActiveRunId = "combat-fwd-001";
    private static readonly QuestId Quest65847 = new(65847);

    // ─── Fixture builders ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a UIObserver wired with both a FakeTargetProbe and a real SnapshotAggregator
    /// so that forwarded calls have observable effect on aggregator.Current.
    /// </summary>
    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeCombatProbe combatProbe,
                    FakeTargetProbe targetProbe,
                    FakeClock clock,
                    SnapshotAggregator aggregator)
        BuildForwardingFixture()
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock       = new FakeClock(T0);
        var combatProbe = new FakeCombatProbe();
        var targetProbe = new FakeTargetProbe();
        var framework   = new FakeFramework();

        var aggregator = new SnapshotAggregator(Quest65847, clock);

        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: ActiveRunId,
            combatProbe:  combatProbe,
            targetProbe:  targetProbe,
            clock:        clock);

        observer.SetAggregator(aggregator, ActiveRunId);

        return (observer, framework, combatProbe, targetProbe, clock, aggregator);
    }

    /// <summary>
    /// Builds a UIObserver wired with a FakeTargetProbe and FakeTraceWriter for
    /// trace-shape assertion tests (U-T1, U-T2).
    /// </summary>
    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeCombatProbe combatProbe,
                    FakeTargetProbe targetProbe,
                    FakeClock clock,
                    FakeTraceWriter writer)
        BuildTraceFixture()
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock       = new FakeClock(T0);
        var combatProbe = new FakeCombatProbe();
        var targetProbe = new FakeTargetProbe();
        var framework   = new FakeFramework();

        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: ActiveRunId,
            combatProbe:  combatProbe,
            targetProbe:  targetProbe,
            clock:        clock);

        return (observer, framework, combatProbe, targetProbe, clock, writer);
    }

    private static void DrivePoll(FakeFramework framework, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        framework.Tick();
    }

    private static List<ObservationEvent> GetTargetEvents(FakeTraceWriter writer)
        => writer.RecordedEvents.OfType<ObservationEvent>()
                 .Where(e => e.Method == "GetTarget")
                 .ToList();

    private static List<ObservationEvent> EnemyKilledEvents(FakeTraceWriter writer)
        => writer.RecordedEvents.OfType<ObservationEvent>()
                 .Where(e => e.Method == "EnemyKilled")
                 .ToList();

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-FWD-3 hostile target forwards OnBattleNpcTargeted to aggregator (new).
    //
    // REPLACES GWT-U-FWD-1 (kill→aggregator forwarding via OnEnemyKilled).
    //
    // Setup: FakeTargetProbe.GetBattleNpcTarget() returns (338,...);
    //        combatProbe.SetInCombat(true) is applied before the target poll so
    //        the aggregator receives OnInCombatChanged(true) before OnBattleNpcTargeted.
    // After: baseline [0,...] poll; then [0,0x30,...] bump poll (V1 high 0→3).
    // Then: aggregator.Current.KillCorrelatedTargets[(1,High)].DataIds contains 338u.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UFWD3_BattleNpcTarget_ForwardsOnBattleNpcTargetedToAggregator()
    {
        var (observer, framework, combatProbe, targetProbe, clock, aggregator) = BuildForwardingFixture();

        // Establish the pre-fight baseline (first observation → baseline only)
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0, 0, 0, 0, 0 });

        // Set in-combat and target, then poll — observer should forward both OnInCombatChanged and OnBattleNpcTargeted
        combatProbe.SetInCombat(true);
        targetProbe.SetBattleNpcTarget((BaseId: 338u, X: 0f, Y: 0f, Z: 0f, Zone: 148));

        DrivePoll(framework, clock); // heartbeat: PollCombat (InCombatChanged) then PollBattleNpcTarget (BattleNpcTargeted)

        // Aggregator must now be in combat (OnInCombatChanged forwarded)
        Assert.True(aggregator.Current.InCombat,
            "After PollCombat with IsInCombat()==true, aggregator.Current.InCombat must be true. " +
            "This fails if PollCombat doesn't forward OnInCombatChanged.");

        // Simulate quest variable bump (V1 high 0→3)
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0x30, 0, 0, 0, 0 });

        // KillCorrelatedTargets[(1,High)] must contain 338 — proving OnBattleNpcTargeted(338) was forwarded
        var targets = aggregator.Current.KillCorrelatedTargets;
        var highKey = new NibbleKey(1, NibbleHalf.High);
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(highKey),
            "KillCorrelatedTargets[(1,High)] must be set after OnBattleNpcTargeted(338) + nibble bump. " +
            "This fails if PollBattleNpcTarget does not forward OnBattleNpcTargeted, or if the heartbeat " +
            "order does not place PollBattleNpcTarget after PollCombat.");
        Assert.Contains(338u, targets[highKey].DataIds);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-FWD-2 InCombat transition forwards OnInCombatChanged (UNCHANGED).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UFWD2_InCombatTransition_ForwardsOnInCombatChangedToAggregator()
    {
        var (observer, framework, combatProbe, _, clock, aggregator) = BuildForwardingFixture();

        aggregator.OnZoneChanged(new ZoneId(148), new WorldPosition(10, 0, 20));
        aggregator.OnPlayerMoved(new WorldPosition(10, 0, 20));

        combatProbe.SetInCombat(false);
        DrivePoll(framework, clock);

        Assert.False(aggregator.Current.InCombat,
            "After poll 1 with IsInCombat()==false, aggregator.Current.InCombat must be false.");

        combatProbe.SetInCombat(true);
        DrivePoll(framework, clock);

        var snap = aggregator.Current;
        Assert.True(snap.InCombat,
            "After PollCombat forwarded OnInCombatChanged(true), aggregator.Current.InCombat must be true.");
        Assert.Equal(148, snap.CombatStartZone);
        Assert.NotNull(snap.CombatStartPosition);
        Assert.Equal(10f, snap.CombatStartPosition!.Value.X);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-T1 hostile GetTarget value shape = {baseId, kind:"hostile"}.
    //
    // Given GetBattleNpcTarget() returns (347,...).
    // When a poll fires.
    // Then exactly one GetTarget observation whose value JSON has "baseId"==347
    //      and "kind"=="hostile". Argument is null/zero.
    //
    // Pins D2: the hostile branch emits the enriched object shape, not the bare uint.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UT1_BattleNpcTarget_GetTargetValue_HasBaseIdAndKindHostile()
    {
        var (observer, framework, _, targetProbe, clock, writer) = BuildTraceFixture();

        targetProbe.SetBattleNpcTarget((BaseId: 347u, X: 1f, Y: 0f, Z: 2f, Zone: 100));

        DrivePoll(framework, clock);

        var getTargetEvents = GetTargetEvents(writer);
        Assert.True(getTargetEvents.Count == 1,
            $"Expected exactly one GetTarget event. Got: {string.Join("; ", getTargetEvents.Select(e => e.Value?.GetRawText()))}");

        var evt = getTargetEvents[0];
        Assert.NotNull(evt.Value);
        var val = evt.Value!.Value;

        Assert.True(val.TryGetProperty("baseId", out var baseIdEl),
            $"GetTarget value must have 'baseId'. Got: {val.GetRawText()}");
        Assert.Equal(347u, baseIdEl.GetUInt32());

        Assert.True(val.TryGetProperty("kind", out var kindEl),
            $"GetTarget value must have 'kind'. Got: {val.GetRawText()}");
        Assert.Equal("hostile", kindEl.GetString());

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-T2 EventNpc/interactable-NPC target keeps bare-uint GetTarget + OnInteraction.
    //
    // Given GetBattleNpcTarget() null; GetInteractableNpcTarget() returns (1000927,...).
    // When a poll fires.
    // Then GetTarget value is the bare uint 1000927 (no 'kind' property),
    //      AND OnInteraction is forwarded (NOT OnBattleNpcTargeted).
    //
    // Pins D2 ordering: interactable-NPC branch is NOT affected by the hostile-first branch.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UT2_InteractableNpcTarget_BareUintGetTarget_OnInteractionForwarded_NotBattleNpcTargeted()
    {
        var (observer, framework, _, targetProbe, clock, aggregator) = BuildForwardingFixture();

        // BattleNpc slot is null; only interactable-npc target is set
        targetProbe.SetBattleNpcTarget(null);
        targetProbe.SetInteractableNpcTarget((BaseId: 1000927u, X: 5f, Y: 0f, Z: 5f, Zone: 100));

        // We need the trace writer to verify the GetTarget shape — re-build with trace fixture
        var (obs2, fw2, _, tp2, clk2, writer2) = BuildTraceFixture();
        var aggregator2 = new SnapshotAggregator(Quest65847, clk2);
        obs2.SetAggregator(aggregator2, ActiveRunId);

        tp2.SetBattleNpcTarget(null);
        tp2.SetInteractableNpcTarget((BaseId: 1000927u, X: 5f, Y: 0f, Z: 5f, Zone: 100));

        DrivePoll(fw2, clk2);

        var getTargetEvents = GetTargetEvents(writer2);
        Assert.True(getTargetEvents.Count == 1,
            $"Expected exactly one GetTarget. Got: {string.Join("; ", getTargetEvents.Select(e => e.Value?.GetRawText()))}");

        var evt = getTargetEvents[0];
        Assert.NotNull(evt.Value);
        var val = evt.Value!.Value;

        // Bare uint shape — must be a plain number, NOT a JSON object
        var rawText = val.GetRawText();
        Assert.True(val.ValueKind == System.Text.Json.JsonValueKind.Number,
            $"EventNpc GetTarget value must be a bare number (not a JSON object). Got: {rawText}");
        Assert.True(rawText == "1000927",
            $"EventNpc GetTarget value must be bare uint 1000927. Got: {rawText}");

        // OnInteraction must have been forwarded (LastNpcInteracted set)
        Assert.NotNull(aggregator2.Current.LastNpcInteracted);
        Assert.Equal(1000927u, aggregator2.Current.LastNpcInteracted!.Value.Value);

        // Span must be EMPTY (no OnBattleNpcTargeted should have been called)
        Assert.Null(aggregator2.Current.KillCorrelatedTargets);

        obs2.Dispose();
        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-T2B BattleNpc hard target does NOT forward OnInteraction (D2 no-corruption).
    //
    // After the poller split, the every-frame PollTargetNpc must SKIP a BattleNpc hard
    // target so it is never mis-routed to OnInteraction (which would corrupt
    // LastNpcInteracted with a mob id → spurious talk step).
    //
    // Given GetBattleNpcTarget() returns (338,...) AND the aggregator is NOT in combat
    //       (so PollBattleNpcTarget will not add it to the span either — isolating the
    //       OnInteraction question from span accumulation).
    // When a poll fires.
    // Then LastNpcInteracted is NULL (OnInteraction was NOT called for the BattleNpc),
    //      and KillCorrelatedTargets is null (out-of-combat target ignored by the span).
    //
    // Pins the CRITICAL split guarantee: a BattleNpc never reaches the interactable-NPC branch.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UT2B_BattleNpcHardTarget_DoesNotForwardOnInteraction()
    {
        var (observer, framework, combatProbe, targetProbe, clock, aggregator) = BuildForwardingFixture();

        // Out of combat: isolates the OnInteraction question. The interactable-NPC branch
        // (which DOES call OnInteraction) accepts BattleNpc per the ITargetProbe contract, so
        // without the guard a hostile would corrupt LastNpcInteracted here.
        combatProbe.SetInCombat(false);
        targetProbe.SetBattleNpcTarget((BaseId: 338u, X: 0f, Y: 0f, Z: 0f, Zone: 148));
        // Also expose the SAME id via the interactable-npc slot to model the real probe, where a
        // BattleNpc hard target is returned by BOTH GetBattleNpcTarget and GetInteractableNpcTarget.
        targetProbe.SetInteractableNpcTarget((BaseId: 338u, X: 0f, Y: 0f, Z: 0f, Zone: 148));

        DrivePoll(framework, clock);

        Assert.Null(aggregator.Current.LastNpcInteracted);
        Assert.Null(aggregator.Current.KillCorrelatedTargets);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-T3 PollCombat still writes EnemyKilled but does NOT create
    //          KillCorrelatedTargets from the kill alone (D9).
    //
    // Setup: alive→dead transition (poll 1 alive → poll 2 dead) while in combat.
    // Then:  EnemyKilled {dataId:347} is written to the trace (emission kept),
    //        AND aggregator.Current.KillCorrelatedTargets is null/empty
    //           (no KillCorrelatedTargets entry created from the kill alone).
    //
    // Pins D9: emission kept, attribution to aggregator severed.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UT3_PollCombat_StillWritesEnemyKilled_ButDoesNotCreateKillCorrelation()
    {
        var (observer, framework, combatProbe, targetProbe, clock, aggregator) = BuildForwardingFixture();

        // Establish baseline so we can verify KillCorrelatedTargets cleanly
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0, 0, 0, 0, 0 });

        // Ensure no BattleNpc target is set (kill alone must not drive attribution)
        targetProbe.SetBattleNpcTarget(null);

        // We also need the trace writer for emission check — rebuild with trace fixture
        var (obs2, fw2, cp2, tp2, clk2, writer2) = BuildTraceFixture();
        var agg2 = new SnapshotAggregator(Quest65847, clk2);
        obs2.SetAggregator(agg2, ActiveRunId);

        agg2.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0, 0, 0, 0, 0 });
        tp2.SetBattleNpcTarget(null);

        // Poll 1: in combat, hostile alive
        cp2.SetInCombat(true);
        cp2.SetHostiles((ObjectId: 1UL, DataId: 347u, IsDead: false));
        DrivePoll(fw2, clk2);

        // Poll 2: same hostile now dead → alive→dead transition → EnemyKilled emitted
        cp2.SetHostiles((ObjectId: 1UL, DataId: 347u, IsDead: true));
        DrivePoll(fw2, clk2);

        // EnemyKilled must still be in the trace (D9: emission kept)
        var killedEvents = EnemyKilledEvents(writer2);
        Assert.True(killedEvents.Count == 1,
            $"EnemyKilled must still be emitted by PollCombat even though attribution is severed. " +
            $"Got: {killedEvents.Count} events");
        var dataId = killedEvents[0].Value!.Value.GetProperty("dataId").GetUInt32();
        Assert.Equal(347u, dataId);

        // KillCorrelatedTargets must be null — the kill must NOT create an entry
        var targets = agg2.Current.KillCorrelatedTargets;
        Assert.Null(targets);

        obs2.Dispose();
        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-T4 non-combat NPC capture runs EVERY-FRAME, not throttled to heartbeat.
    //
    // The poller split restored PollTargetNpc (interactable-NPC / aethernet) to the
    // every-frame block. This pins that: a tick that does NOT cross the 250 ms heartbeat
    // boundary still captures an interactable-NPC interaction.
    //
    // Given a first heartbeat has already fired (consuming the MinValue throttle reset).
    // When a SECOND tick fires with NO clock advance (sub-heartbeat) and an interactable NPC
    //      target is present.
    // Then OnInteraction is forwarded (LastNpcInteracted set) on that every-frame tick.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UT4_InteractableNpcCapture_RunsEveryFrame_NotThrottledToHeartbeat()
    {
        var (observer, framework, combatProbe, targetProbe, clock, aggregator) = BuildForwardingFixture();

        combatProbe.SetInCombat(false);
        targetProbe.SetBattleNpcTarget(null);

        // First tick consumes the MinValue heartbeat reset (heartbeat fires once here).
        // No target set yet, so nothing is captured.
        framework.Tick();
        Assert.Null(aggregator.Current.LastNpcInteracted);

        // Now set an interactable-NPC target and fire a SUB-heartbeat tick (no clock advance).
        // The heartbeat block is skipped; only the every-frame block runs.
        targetProbe.SetInteractableNpcTarget((BaseId: 1000927u, X: 5f, Y: 0f, Z: 5f, Zone: 100));
        framework.Tick();

        Assert.NotNull(aggregator.Current.LastNpcInteracted);
        Assert.Equal(1000927u, aggregator.Current.LastNpcInteracted!.Value.Value);

        observer.Dispose();
    }
}
