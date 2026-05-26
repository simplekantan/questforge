using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// Slice 2: PollCombat aggregator-forwarding tests.
//
// Verifies that UIObserver.PollCombat forwards OnEnemyKilled / OnInCombatChanged to
// the wired SnapshotAggregator. The aggregator's correlation model is the nibble-level
// bidirectional one (KillCorrelatedTargets keyed by NibbleKey); baseline-only first
// observation means a baseline poll must precede the bump-under-test.
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
         * Given probe IsInCombat()==true, hostile [(obj=1, data=347)] at poll 1.
         * When poll 2: hostile gone → OnEnemyKilled(347) forwarded.
         * Then a variable bump (after a baseline poll) at the same tick correlates the kill,
         * proving the kill reached the aggregator via PollCombat forwarding.
         *
         * Baseline-only first observation: a [0,...] baseline poll is established before the
         * [1,...] bump so the bump is a genuine delta (0→1), not the first observation.
         */

        var (observer, framework, combatProbe, clock, aggregator) = BuildForwardingFixture();

        // Establish the pre-fight baseline (first observation → baseline only, no correlation).
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 0, 0, 0, 0, 0, 0 });

        // Poll 1: in combat, hostile present
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((ObjectId: 1UL, DataId: 347u));
        DrivePoll(framework, clock); // t=250ms

        // At this point OnInCombatChanged(true) should have been forwarded.
        Assert.True(aggregator.Current.InCombat,
            "After PollCombat with InCombat=true, aggregator.Current.InCombat must be true. " +
            "This fails if PollCombat doesn't forward OnInCombatChanged.");

        // Poll 2: hostile gone (kill emitted + forwarded)
        combatProbe.ClearHostiles();
        DrivePoll(framework, clock); // t=500ms

        // Now trigger a variable bump on the aggregator directly (simulates PollQuestState forwarding).
        // The kill at t=500ms is within the 500ms window of this bump at t=500ms.
        aggregator.OnQuestVariablesUpdated(Quest65847, new byte[] { 1, 0, 0, 0, 0, 0 });

        // If OnEnemyKilled was forwarded correctly, the kill at t=500ms correlates with the bump at t=500ms.
        var targets = aggregator.Current.KillCorrelatedTargets;
        var lowKey = new NibbleKey(0, NibbleHalf.Low);
        Assert.NotNull(targets);
        Assert.True(targets!.ContainsKey(lowKey),
            "After PollCombat forwarded OnEnemyKilled(347) at t=500ms and OnQuestVariablesUpdated bumped V0 low nibble, " +
            "KillCorrelatedTargets[(0,Low)] must be set. This fails if PollCombat does not forward OnEnemyKilled.");
        Assert.Contains(347u, targets[lowKey].DataIds);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U-FWD-2 — InCombat transition forwards OnInCombatChanged to aggregator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_UFWD2_InCombatTransition_ForwardsOnInCombatChangedToAggregator()
    {
        /*
         * Given aggregator set with zone 148 and player pos (10,0,20).
         * When probe IsInCombat() goes false→true in poll 2.
         * Then aggregator.Current.InCombat == true AND CombatStartZone == 148
         *      (proves OnInCombatChanged(true) was forwarded — aggregator captures start pos/zone
         *       from its own internal state at the time of the call).
         */

        var (observer, framework, combatProbe, clock, aggregator) = BuildForwardingFixture();

        // Prime aggregator with zone 148 and start position
        aggregator.OnZoneChanged(new ZoneId(148), new WorldPosition(10, 0, 20));
        aggregator.OnPlayerMoved(new WorldPosition(10, 0, 20));

        // Poll 1: out of combat
        combatProbe.SetInCombat(false);
        DrivePoll(framework, clock);

        // Confirm InCombat still false
        Assert.False(aggregator.Current.InCombat,
            "After poll 1 with IsInCombat()==false, aggregator.Current.InCombat must be false.");

        // Poll 2: in combat transition (false→true)
        combatProbe.SetInCombat(true);
        DrivePoll(framework, clock);

        // Assert forwarding: InCombat now true + CombatStartZone captured from aggregator state
        var snap = aggregator.Current;
        Assert.True(snap.InCombat,
            "After PollCombat forwarded OnInCombatChanged(true), aggregator.Current.InCombat must be true. " +
            "This fails if PollCombat does not forward OnInCombatChanged.");
        Assert.Equal(148, snap.CombatStartZone);
        Assert.NotNull(snap.CombatStartPosition);
        Assert.Equal(10f, snap.CombatStartPosition!.Value.X);

        observer.Dispose();
    }
}
