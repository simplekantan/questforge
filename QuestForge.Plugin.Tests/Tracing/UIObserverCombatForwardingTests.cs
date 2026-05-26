using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice 2: PollCombat aggregator-forwarding tests.
//
// These tests WILL FAIL because:
//   1. UIObserver.PollCombat does NOT yet forward to _aggregator?.OnEnemyKilled(dataId)
//   2. UIObserver.PollCombat does NOT yet forward to _aggregator?.OnInCombatChanged(bool)
//   3. GameStateSnapshot.KillCorrelatedTargets / InCombat do not exist yet (compile error)
//   4. SnapshotAggregator.OnEnemyKilled / OnInCombatChanged do not exist yet (compile error)
//
// The 5 existing Slice-1 tests in UIObserverCombatTests.cs must remain GREEN.
//
// Builder: in UIObserver.PollCombat, after emitting InCombat observation, add:
//     _aggregator?.OnInCombatChanged(inCombat);
// and after emitting EnemyKilled observation, add:
//     _aggregator?.OnEnemyKilled(dataId);
// Then both tests go green.
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Tracing;

public sealed class UIObserverCombatForwardingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 20, 9, 0, 0, TimeSpan.Zero);
    private const string ActiveRunId = "combat-fwd-001";
    private static readonly QuestId Quest65847 = new(65847);

    // ─── Fixture builder ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a UIObserver wired with a real SnapshotAggregator (not a mock)
    /// so that forwarded calls have observable effect on aggregator.Current.
    /// </summary>
    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeCombatProbe combatProbe,
                    FakeClock clock,
                    SnapshotAggregator aggregator)
        BuildForwardingFixture()
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock        = new FakeClock(T0);
        var combatProbe  = new FakeCombatProbe();
        var framework    = new FakeFramework();

        // Aggregator with the same injected clock so kill timestamps correlate correctly.
        // RED: SnapshotAggregator.OnEnemyKilled / OnInCombatChanged do not exist.
        var aggregator = new SnapshotAggregator(Quest65847, clock);

        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: ActiveRunId,
            combatProbe:  combatProbe,
            clock:        clock);

        // Wire the aggregator so PollCombat forwarding reaches it.
        observer.SetAggregator(aggregator, ActiveRunId);

        return (observer, framework, combatProbe, clock, aggregator);
    }

    private static void DrivePoll(FakeFramework framework, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        framework.Tick();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-FWD-1 — EnemyKilled poll forwards OnEnemyKilled to aggregator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UFWD1_EnemyKilledPoll_ForwardsOnEnemyKilledToAggregator()
    {
        /*
         * RED: UIObserver.PollCombat does not call _aggregator?.OnEnemyKilled(dataId).
         *      Also: OnEnemyKilled and KillCorrelatedTargets do not exist on the aggregator/snapshot.
         *
         * Given probe IsInCombat()==true, hostile [(obj=1, data=347)] at poll 1.
         * Also: OnQuestVariablesUpdated already set a baseline (no variables actually change here,
         *       so we only verify the kill was forwarded, not correlation itself).
         * When poll 2: hostile gone.
         * Then aggregator.Current has received the kill — verified by:
         *   If we set V0 0→1 in the same poll after the kill, KillCorrelatedTargets[0] must be set.
         *   Simpler: confirm InCombat true was forwarded and kill was forwarded by checking the
         *   aggregator.Current.InCombat and that OnEnemyKilled has side effects that the builder
         *   will wire (we cannot directly inspect the private buffer, but we can check a correlated result).
         *
         * Concrete verification: drive kill + variable bump at the same clock tick via aggregator
         * directly, then check KillCorrelatedTargets after poll. If forwarding is missing, the
         * aggregator never receives the kill and KillCorrelatedTargets stays empty.
         *
         * Strategy (avoids mocking): use a SnapshotAggregator for quest 65847.
         *   Poll 1: in combat, hostile present → aggregator.OnInCombatChanged(true) forwarded.
         *   Poll 2 (same clock tick as kill detection): hostile gone → aggregator.OnEnemyKilled(347) forwarded.
         *   Immediately after poll 2, manually call aggregator.OnQuestVariablesUpdated([1,...])
         *   (the variable bump) using the same clock time.
         *   Then KillCorrelatedTargets[0] must contain dataId=347.
         */

        var (observer, framework, combatProbe, clock, aggregator) = BuildForwardingFixture();

        // Poll 1: in combat, hostile present
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((ObjectId: 1UL, DataId: 347u));
        DrivePoll(framework, clock); // t=250ms

        // At this point OnInCombatChanged(true) should have been forwarded.
        // RED: aggregator.Current.InCombat does not exist as a snapshot property yet.
        Assert.True(aggregator.Current.InCombat,
            "After PollCombat with InCombat=true, aggregator.Current.InCombat must be true. " +
            "This fails because: (a) PollCombat doesn't forward OnInCombatChanged, or " +
            "(b) GameStateSnapshot.InCombat does not exist yet.");

        // Poll 2: hostile gone (kill emitted + forwarded)
        combatProbe.ClearHostiles();
        DrivePoll(framework, clock); // t=500ms

        // Now trigger a variable bump on the aggregator directly (simulates PollQuestState forwarding).
        // The kill at t=500ms is within the 500ms window of this bump at t=500ms.
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 1, 0, 0, 0, 0, 0 });

        // If OnEnemyKilled was forwarded correctly, the kill at t=500ms correlates with the bump at t=500ms.
        // RED: KillCorrelatedTargets does not exist on the snapshot; OnEnemyKilled not implemented.
        var targets = aggregator.Current.KillCorrelatedTargets;
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(0),
            "After PollCombat forwarded OnEnemyKilled(347) at t=500ms and OnQuestVariablesUpdated bumped V0, " +
            "KillCorrelatedTargets[0] must be set. This fails because PollCombat does not forward OnEnemyKilled.");
        Assert.Contains(347u, targets[0].DataIds);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-FWD-2 — InCombat transition forwards OnInCombatChanged to aggregator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UFWD2_InCombatTransition_ForwardsOnInCombatChangedToAggregator()
    {
        /*
         * RED: UIObserver.PollCombat does not call _aggregator?.OnInCombatChanged(inCombat).
         *      Also: GameStateSnapshot.InCombat / CombatStartZone / CombatStartPosition missing.
         *
         * Given aggregator set with zone 148 and player pos (10,0,20).
         * When probe IsInCombat() goes false→true in poll 2.
         * Then aggregator.Current.InCombat == true AND CombatStartZone == 148
         *      (proves OnInCombatChanged(true) was forwarded — aggregator captures start pos/zone
         *       from its own internal state at the time of the call).
         *
         * Setup: prime the aggregator's zone+position by calling OnZoneChanged on the aggregator
         * directly (this would normally come from PollQuestState/TerritoryChanged forwarding).
         */

        var (observer, framework, combatProbe, clock, aggregator) = BuildForwardingFixture();

        // Prime aggregator with zone 148 and start position
        aggregator.OnZoneChanged(new ZoneId(148), new WorldPosition(10, 0, 20));
        aggregator.OnPlayerMoved(new WorldPosition(10, 0, 20));

        // Poll 1: out of combat
        combatProbe.SetInCombat(false);
        DrivePoll(framework, clock);

        // Confirm InCombat still false
        // RED: GameStateSnapshot.InCombat missing
        Assert.False(aggregator.Current.InCombat,
            "After poll 1 with IsInCombat()==false, aggregator.Current.InCombat must be false.");

        // Poll 2: in combat transition (false→true)
        combatProbe.SetInCombat(true);
        DrivePoll(framework, clock);

        // Assert forwarding: InCombat now true + CombatStartZone captured from aggregator state
        // RED: CombatStartZone / CombatStartPosition do not exist as snapshot fields.
        var snap = aggregator.Current;
        Assert.True(snap.InCombat,
            "After PollCombat forwarded OnInCombatChanged(true), aggregator.Current.InCombat must be true. " +
            "This fails because PollCombat does not forward OnInCombatChanged.");
        Assert.Equal(148, snap.CombatStartZone);
        Assert.NotNull(snap.CombatStartPosition);
        Assert.Equal(10f, snap.CombatStartPosition!.Value.X);

        observer.Dispose();
    }
}
