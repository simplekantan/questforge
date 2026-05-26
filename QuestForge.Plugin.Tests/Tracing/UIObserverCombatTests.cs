using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice 1 combat tests (GWT-U1..U5).
//
// These tests WILL fail to compile because the following do not exist yet:
//   1. QuestForge.Plugin.Tracing.ICombatProbe               (new interface)
//   2. UIObserver ctor param: ICombatProbe? combatProbe = null
//   3. UIObserver.PollCombat()                              (new heartbeat poller)
//   4. UIObserver._trackedHostiles / _lastInCombat fields   (internal state)
//   5. UIObserver.ResetHeartbeatState() clearing _trackedHostiles/_lastInCombat
//
// Every OTHER symbol (FakeFramework, FakeClock, FakeTraceWriter, TraceSession,
// ObservationEvent, SnapshotAggregator, UIObserver core ctor) already exists.
//
// Builder: implement ICombatProbe, add combatProbe ctor param, add PollCombat()
// to the heartbeat block, extend ResetHeartbeatState(). Then all 5 tests go green.
//
// Scope: Slice 1 ONLY.  NO tests for Slice 2 (SnapshotAggregator.OnEnemyKilled /
// OnInCombatChanged), Slice 3 (SnapshotState, TraceToQuestExtractor), or Slice 4
// (DalamudCombatProbe).
//
// Run with:
//   dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverCombatTests"
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Tracing;

// ─── FakeCombatProbe ─────────────────────────────────────────────────────────

/// <summary>
/// Controllable in-memory implementation of <see cref="ICombatProbe"/>.
/// Mirrors the existing FakeAddonProbe / FakeGameProbe pattern in UIObserverTests.cs.
/// </summary>
public sealed class FakeCombatProbe : ICombatProbe
{
    private bool _inCombat;
    private readonly List<(ulong ObjectId, uint DataId)> _hostiles = new();

    /// <summary>Sets the return value of <see cref="IsInCombat"/>.</summary>
    public void SetInCombat(bool value) => _inCombat = value;

    /// <summary>Replaces the visible-hostiles list returned by <see cref="GetVisibleHostiles"/>.</summary>
    public void SetHostiles(params (ulong ObjectId, uint DataId)[] hostiles)
    {
        _hostiles.Clear();
        _hostiles.AddRange(hostiles);
    }

    /// <summary>Clears the hostile list (simulates all mobs gone).</summary>
    public void ClearHostiles() => _hostiles.Clear();

    // ICombatProbe
    public bool IsInCombat() => _inCombat;
    public IReadOnlyList<(ulong ObjectId, uint DataId)> GetVisibleHostiles()
        => _hostiles.AsReadOnly();
}

// ─────────────────────────────────────────────────────────────────────────────
// Test class
// ─────────────────────────────────────────────────────────────────────────────

public sealed class UIObserverCombatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 20, 9, 0, 0, TimeSpan.Zero);
    private const string PassiveRunId = "passive-combat-001";

    // ─── Fixture builder ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a UIObserver wired with a FakeCombatProbe.
    /// Drives one heartbeat tick by advancing clock past 250 ms (so the first
    /// <c>framework.Tick()</c> always fires the heartbeat pollers including PollCombat).
    /// </summary>
    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeCombatProbe combatProbe,
                    FakeClock clock,
                    FakeTraceWriter writer)
        BuildCombatFixture()
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock       = new FakeClock(T0);
        var combatProbe = new FakeCombatProbe();
        var framework   = new FakeFramework();

        // RED: 'combatProbe' parameter does not exist on UIObserver yet → CS1739 / CS1503.
        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            combatProbe:  combatProbe,
            clock:        clock);

        return (observer, framework, combatProbe, clock, writer);
    }

    /// <summary>
    /// Drives one heartbeat poll: advance clock ≥250 ms then tick framework.
    /// </summary>
    private static void DrivePoll(FakeFramework framework, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        framework.Tick();
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    private static string FormatEvents(FakeTraceWriter writer)
        => string.Join("; ", writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Select(e => $"[{e.Method}] value={e.Value?.GetRawText()}"));

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U1 — tracked-then-gone while in combat → one EnemyKilled written
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U1_TrackedHostileGoneWhileInCombat_EmitsEnemyKilledObservation()
    {
        /*
         * RED: Compile-error — ICombatProbe does not exist; UIObserver has no combatProbe param.
         *
         * Given probe IsInCombat()==true and hostiles [(obj=1, data=347)] at poll 1,
         * When poll 2 has no hostiles (mob gone),
         * Then an ObservationEvent with Method="EnemyKilled" and Value={"dataId":347}
         *      is written to the TraceSession.
         *
         * Specifically:
         *   - Exactly ONE EnemyKilled event is written across both polls.
         *   - Its Value JSON must contain a "dataId" field equal to 347 (uint).
         *   - No EnemyKilled is written on poll 1 (mob was present, not gone yet).
         */

        // Arrange
        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: in combat, hostile present
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((ObjectId: 1UL, DataId: 347u));
        DrivePoll(framework, clock); // poll 1

        var countAfterPoll1 = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Count(e => e.Method == "EnemyKilled");

        // Poll 2: still in combat (from previous poll perspective), hostile gone
        combatProbe.ClearHostiles();
        DrivePoll(framework, clock); // poll 2

        // Assert — no EnemyKilled on poll 1
        Assert.Equal(0, countAfterPoll1);

        // Assert — exactly one EnemyKilled after poll 2
        var killedEvents = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "EnemyKilled")
            .ToList();

        Assert.True(killedEvents.Count == 1,
            $"Expected exactly 1 EnemyKilled, got {killedEvents.Count}. Events: {FormatEvents(writer)}");

        // Value must contain dataId:347
        var killed = killedEvents[0];
        Assert.NotNull(killed.Value);
        var valueDoc = killed.Value!.Value;
        Assert.Equal(JsonValueKind.Object, valueDoc.ValueKind);
        Assert.True(valueDoc.TryGetProperty("dataId", out var dataIdEl),
            $"EnemyKilled value must have 'dataId' property. Got: {valueDoc.GetRawText()}");
        Assert.Equal(347u, dataIdEl.GetUInt32());

        // Argument must be null per spec (§1.1)
        Assert.Null(killed.Argument);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U2 — disappearance while NOT in combat is not a kill
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U2_HostileGoneWhileOutOfCombat_NoEnemyKilledEmitted()
    {
        /*
         * RED: Compile-error — same missing Slice-1 members as GWT-U1.
         *
         * Given IsInCombat()==false at poll 1 with hostile [(obj=1,data=347)],
         * When poll 2 has no hostiles,
         * Then NO EnemyKilled observation is written.
         * _trackedHostiles IS updated silently so a future in-combat disappearance
         * would start from the new (empty) snapshot — verified by checking zero kills total.
         */

        // Arrange
        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: out of combat, hostile present
        combatProbe.SetInCombat(false);
        combatProbe.SetHostiles((ObjectId: 1UL, DataId: 347u));
        DrivePoll(framework, clock);

        // Poll 2: hostile gone, still out of combat
        combatProbe.ClearHostiles();
        DrivePoll(framework, clock);

        // Assert — zero EnemyKilled events across both polls
        var killedEvents = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "EnemyKilled")
            .ToList();

        Assert.Empty(killedEvents);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U3 — InCombat transition emitted once per change (dedup on change only)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U3_InCombatTransitionEmittedOnlyOnChange_NotEveryPoll()
    {
        /*
         * RED: Compile-error — same missing Slice-1 members as GWT-U1.
         *
         * Given IsInCombat() returns: false → true → true → false across 4 heartbeats,
         * Then exactly TWO InCombat observations are written:
         *   - poll 2: {value:true}   (false→true transition)
         *   - poll 4: {value:false}  (true→false transition)
         * polls 1 and 3 produce NO InCombat write (false→false and true→true are suppressed).
         *
         * "Direct write, dedup on change only" — mirrors InventoryChanged pattern (§1.1).
         */

        // Arrange
        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: false (initial)
        combatProbe.SetInCombat(false);
        DrivePoll(framework, clock);

        // Poll 2: false → true  (should emit InCombat {value:true})
        combatProbe.SetInCombat(true);
        DrivePoll(framework, clock);

        // Poll 3: true → true  (no change — must NOT emit)
        combatProbe.SetInCombat(true);
        DrivePoll(framework, clock);

        // Poll 4: true → false  (should emit InCombat {value:false})
        combatProbe.SetInCombat(false);
        DrivePoll(framework, clock);

        // Assert
        var inCombatEvents = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "InCombat")
            .ToList();

        Assert.True(inCombatEvents.Count == 2,
            $"Expected exactly 2 InCombat observations (true then false), got {inCombatEvents.Count}. Events: {FormatEvents(writer)}");

        // First event must be true
        var first = inCombatEvents[0];
        Assert.NotNull(first.Value);
        Assert.Equal(JsonValueKind.Object, first.Value!.Value.ValueKind);
        Assert.True(first.Value.Value.TryGetProperty("value", out var firstVal),
            $"InCombat value must have 'value' property. Got: {first.Value.Value.GetRawText()}");
        Assert.True(firstVal.GetBoolean());

        // Second event must be false
        var second = inCombatEvents[1];
        Assert.NotNull(second.Value);
        Assert.True(second.Value!.Value.TryGetProperty("value", out var secondVal),
            $"InCombat value must have 'value' property. Got: {second.Value.Value.GetRawText()}");
        Assert.False(secondVal.GetBoolean());

        // Argument must be null per spec (§1.1)
        Assert.All(inCombatEvents, e => Assert.Null(e.Argument));

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U4 — ResetHeartbeatState clears tracking; no phantom kill on next poll
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U4_ResetHeartbeatState_ClearsCombatTracking_NoPhantomKill()
    {
        /*
         * RED: Compile-error — same missing Slice-1 members as GWT-U1.
         *
         * Verifies two things:
         *
         * A. No phantom kill: Given a tracked hostile and _lastInCombat==true,
         *    When ResetHeartbeatState(), Then the next poll with the SAME hostile
         *    present does not emit EnemyKilled (because _trackedHostiles was cleared,
         *    so it's treated as newly appeared, not as "was tracked and is now gone").
         *
         * B. InCombat re-emits on next true transition: after reset _lastInCombat==false,
         *    so the next poll where IsInCombat()==true should emit InCombat {value:true}.
         */

        // Arrange
        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: in combat with one hostile — establishes tracking
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((ObjectId: 1UL, DataId: 347u));
        DrivePoll(framework, clock);

        // Confirm state is established (InCombat true emitted once)
        var inCombatBeforeReset = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Count(e => e.Method == "InCombat");
        Assert.True(inCombatBeforeReset >= 1,
            "Expected at least one InCombat observation before reset");

        // Act: reset heartbeat state — clears _trackedHostiles and _lastInCombat
        observer.ResetHeartbeatState();

        // Poll 2: same hostile still present, still in combat
        // Since _trackedHostiles was cleared: mob is seen as NEW (not gone), so NO kill.
        // Since _lastInCombat was cleared to false: the false→true transition re-fires InCombat.
        var killedBeforePoll2 = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Count(e => e.Method == "EnemyKilled");

        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((ObjectId: 1UL, DataId: 347u));
        DrivePoll(framework, clock);

        // Assert A — no phantom kill: mob was tracked-cleared-then-seen-new, not gone
        var killedAfterPoll2 = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Count(e => e.Method == "EnemyKilled");
        Assert.Equal(killedBeforePoll2, killedAfterPoll2);

        // Assert B — InCombat re-emits because _lastInCombat reset to false
        var inCombatAfterReset = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "InCombat")
            .Skip(inCombatBeforeReset) // events added after reset
            .ToList();
        Assert.True(inCombatAfterReset.Count >= 1,
            $"Expected at least one InCombat re-emit after ResetHeartbeatState. Events: {FormatEvents(writer)}");

        var reEmitted = inCombatAfterReset[0];
        Assert.NotNull(reEmitted.Value);
        Assert.True(reEmitted.Value!.Value.TryGetProperty("value", out var reEmitVal));
        Assert.True(reEmitVal.GetBoolean(),
            "InCombat re-emitted after reset must have value:true");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U5 — UIObserver without ICombatProbe → no combat observations (back-compat)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U5_NoCombatProbe_NoCombatObservationsEver()
    {
        /*
         * RED: Compile-error — UIObserver ctor does not yet have a combatProbe param,
         *      so this test cannot even call it without combatProbe (but the absence
         *      of the param is itself the compile error that blocks the whole file).
         *
         * Given UIObserver constructed WITHOUT an ICombatProbe (null / param omitted),
         * When several heartbeats fire (with or without game probe data),
         * Then no ObservationEvent with Method="EnemyKilled" or Method="InCombat"
         *      is ever written.
         * Existing pollers (quest, attunement) must still be unaffected.
         *
         * This guards the back-compat contract: the new ctor param must default to null.
         */

        // Arrange — build WITHOUT combatProbe (relies on default null)
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();

        // RED: if combatProbe param doesn't exist yet, the existing ctor call (without it)
        // compiles fine but PollCombat won't be added either → no combat events.
        // Once the builder adds the param with default=null this test documents the null path.
        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            // combatProbe is intentionally omitted → uses default null
            clock:        clock);

        // Act — fire multiple heartbeat polls
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            framework.Tick();
        }

        // Assert — absolutely no EnemyKilled or InCombat observations
        var combatObs = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Method == "EnemyKilled" || e.Method == "InCombat")
            .ToList();

        Assert.Empty(combatObs);

        observer.Dispose();
    }
}
