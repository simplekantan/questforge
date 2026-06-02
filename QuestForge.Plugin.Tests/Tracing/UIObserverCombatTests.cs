using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice 1 combat tests (GWT-U1..U11).
//
// Kill detection is being reworked from "corpse-removal (tracked-then-gone)" to
// "death-state (alive→dead transition)".  PollCombat still uses _trackedHostiles
// (tracked-then-gone), so every kill-detection test (U1..U10) FAILS behaviourally.
// U7 and U11 test InCombat dedup / no-probe back-compat which already work, so they
// pass today and remain green — that is expected (they confirm unchanged behaviour).
//
// ICombatProbe.GetVisibleHostiles() now returns the 3-tuple (ObjectId, DataId, IsDead).
// FakeCombatProbe has been updated accordingly.  DalamudCombatProbe has a minimal stub
// (IsDead: false always) so QuestForge.Plugin compiles — builder replaces it (Slice 2D).
//
// Builder: rework PollCombat per plan D3/D4/D5, swap _trackedHostiles→_hostileStates,
// update ResetHeartbeatState per D7.  Then all U1..U10 go green.
//
// Run with:
//   dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverCombatTests"
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Tracing;

// ─── FakeCombatProbe ─────────────────────────────────────────────────────────

/// <summary>
/// Controllable in-memory implementation of <see cref="ICombatProbe"/>.
/// Now uses the 3-tuple (ObjectId, DataId, IsDead) per plan D2.
/// </summary>
public sealed class FakeCombatProbe : ICombatProbe
{
    private bool _inCombat;
    private readonly List<(ulong ObjectId, uint DataId, bool IsDead)> _hostiles = new();

    /// <summary>Sets the return value of <see cref="IsInCombat"/>.</summary>
    public void SetInCombat(bool value) => _inCombat = value;

    /// <summary>
    /// Replaces the visible-hostiles list with the full 3-tuple.
    /// A linger = same (id, data, IsDead:true) across polls.
    /// A despawn = remove the entry.
    /// </summary>
    public void SetHostiles(params (ulong ObjectId, uint DataId, bool IsDead)[] hostiles)
    {
        _hostiles.Clear();
        _hostiles.AddRange(hostiles);
    }

    /// <summary>
    /// Convenience: set alive hostiles (IsDead=false) without spelling out the bool.
    /// </summary>
    public void SetAliveHostiles(params (ulong ObjectId, uint DataId)[] hostiles)
    {
        _hostiles.Clear();
        foreach (var (objectId, dataId) in hostiles)
            _hostiles.Add((objectId, dataId, IsDead: false));
    }

    /// <summary>Clears the hostile list — models all mobs despawned (NOT a kill by itself).</summary>
    public void ClearHostiles() => _hostiles.Clear();

    // ICombatProbe
    public bool IsInCombat() => _inCombat;
    public IReadOnlyList<(ulong ObjectId, uint DataId, bool IsDead)> GetVisibleHostiles()
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

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string FormatEvents(FakeTraceWriter writer)
        => string.Join("; ", writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Select(e => $"[{e.Data.Method}] value={e.Data.Value?.GetRawText()}"));

    private static List<ObservationEvent> KilledEvents(FakeTraceWriter writer)
        => writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "EnemyKilled")
            .ToList();

    private static List<ObservationEvent> InCombatEvents(FakeTraceWriter writer)
        => writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "InCombat")
            .ToList();

    private static uint GetDataId(ObservationEvent e)
    {
        Assert.NotNull(e.Data.Value);
        Assert.True(e.Data.Value!.Value.TryGetProperty("dataId", out var el),
            $"EnemyKilled value must have 'dataId'. Got: {e.Data.Value.Value.GetRawText()}");
        return el.GetUInt32();
    }

    private static bool GetInCombatValue(ObservationEvent e)
    {
        Assert.NotNull(e.Data.Value);
        Assert.True(e.Data.Value!.Value.TryGetProperty("value", out var el),
            $"InCombat value must have 'value'. Got: {e.Data.Value.Value.GetRawText()}");
        return el.GetBoolean();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U1 — alive→dead transition while in combat emits exactly one EnemyKilled
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U1_AliveToDeadTransitionWhileInCombat_EmitsExactlyOneEnemyKilled()
    {
        /*
         * RED: PollCombat uses tracked-then-gone logic — it will NOT emit on poll 2
         * (the mob is still visible, just dead), so the assertion for exactly one
         * EnemyKilled will fail.
         *
         * Given IsInCombat()==true and hostile (obj=1, data=347, IsDead=false) at poll 1;
         * When poll 2 has (obj=1, data=347, IsDead=true) — same mob now dead, still in combat;
         * Then exactly ONE EnemyKilled with dataId==347u is written;
         *      ZERO EnemyKilled on poll 1 (alive, no transition yet).
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: in combat, hostile alive
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        var countAfterPoll1 = KilledEvents(writer).Count;

        // Poll 2: same hostile now dead, still in combat
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        Assert.Equal(0, countAfterPoll1);

        var killed = KilledEvents(writer);
        Assert.True(killed.Count == 1,
            $"Expected exactly 1 EnemyKilled after alive→dead transition, got {killed.Count}. Events: {FormatEvents(writer)}");

        Assert.Equal(347u, GetDataId(killed[0]));
        Assert.Null(killed[0].Data.Argument);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U2 — linger (dead mob stays visible) does not re-emit
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U2_LingeringDeadHostile_NoReEmit()
    {
        /*
         * RED: Old logic emits on disappearance, not on death — so with the mob still
         * visible and dead for polls 3 and 4 it will not emit at all (zero, not one).
         *
         * Given the U1 setup through poll 2 (one kill emitted);
         * When polls 3 and 4 still show (obj=1, data=347, IsDead=true) — linger;
         * Then still exactly ONE EnemyKilled total (no re-emit per linger poll).
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: alive
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        // Poll 2: dead (transition — should emit)
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        // Poll 3: still dead (linger)
        DrivePoll(framework, clock);

        // Poll 4: still dead (linger)
        DrivePoll(framework, clock);

        var killed = KilledEvents(writer);
        Assert.True(killed.Count == 1,
            $"Expected exactly 1 EnemyKilled despite linger, got {killed.Count}. Events: {FormatEvents(writer)}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U3 — despawn after dead does not re-emit
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U3_DespawnAfterDeadHostile_SilentNoDuplicateKill()
    {
        /*
         * RED: Old logic would emit on poll 4 when the (already dead) mob finally disappears
         * because it was "tracked then gone while in combat". The new logic must be silent.
         *
         * Given U1 setup through poll 2 (one kill emitted), then linger poll 3;
         * When poll 4 has no hostiles (corpse despawned), still in combat;
         * Then still exactly ONE EnemyKilled total (despawn is silent).
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: alive
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        // Poll 2: dead
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        // Poll 3: linger (still dead)
        DrivePoll(framework, clock);

        // Poll 4: despawn
        combatProbe.ClearHostiles();
        DrivePoll(framework, clock);

        var killed = KilledEvents(writer);
        Assert.True(killed.Count == 1,
            $"Expected exactly 1 EnemyKilled (despawn silent), got {killed.Count}. Events: {FormatEvents(writer)}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U4 — death observed as combat ends (the Objective-2 fix)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U4_DeathObservedSamePollCombatEnds_EmitsKillAndInCombatFalse()
    {
        /*
         * RED: Old logic gates on was-in-combat correctly but never fires because
         * the mob is still visible (just dead). New logic must fire because
         * wasInCombat==true when the dead poll is processed.
         *
         * Given IsInCombat()==true and (obj=1, data=347, alive) at poll 1;
         * When poll 2 has IsInCombat()==false AND (obj=1, data=347, dead);
         * Then exactly ONE EnemyKilled (wasInCombat was true before transition block)
         *      AND exactly one InCombat{value:false}.
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: in combat, alive
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        // Poll 2: combat ends AND mob is dead on same poll
        combatProbe.SetInCombat(false);
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        var killed = KilledEvents(writer);
        Assert.True(killed.Count == 1,
            $"Expected exactly 1 EnemyKilled when death seen same poll combat ends, got {killed.Count}. Events: {FormatEvents(writer)}");
        Assert.Equal(347u, GetDataId(killed[0]));

        var inCombatFalseEvents = InCombatEvents(writer)
            .Where(e => !GetInCombatValue(e))
            .ToList();
        Assert.True(inCombatFalseEvents.Count == 1,
            $"Expected exactly 1 InCombat{{value:false}}, got {inCombatFalseEvents.Count}. Events: {FormatEvents(writer)}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U5 — already-dead-on-first-sight is skipped
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U5_AlreadyDeadOnFirstSight_NoEnemyKilledEmitted()
    {
        /*
         * RED: Old tracked-then-gone logic will emit when this dead mob eventually
         * disappears (because it was "tracked and gone while in combat"). New logic
         * must skip it — no alive→dead transition was ever observed.
         *
         * Given no hostiles tracked yet;
         * When poll 1 has IsInCombat()==true and (obj=9, data=347, IsDead=true) — first seen dead;
         * Then ZERO EnemyKilled; a later linger/despawn also emits nothing.
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: first seen already dead
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((9UL, 347u, true));
        DrivePoll(framework, clock);

        // Poll 2: linger (still dead)
        DrivePoll(framework, clock);

        // Poll 3: despawn
        combatProbe.ClearHostiles();
        DrivePoll(framework, clock);

        var killed = KilledEvents(writer);
        Assert.True(killed.Count == 0,
            $"Expected ZERO EnemyKilled for already-dead-on-first-sight mob, got {killed.Count}. Events: {FormatEvents(writer)}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U6 — out-of-combat death is not emitted
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U6_AliveToDeadWhileOutOfCombat_NoEnemyKilledEmitted()
    {
        /*
         * RED: Old logic gates on was-in-combat and won't emit out-of-combat,
         * so this may pass with the old code — but the alive→dead path is now
         * explicitly exercised against the in-combat gate.
         *
         * Given IsInCombat()==false and (obj=1, data=347, alive) at poll 1;
         * When poll 2 has (obj=1, data=347, dead) still out of combat;
         * Then ZERO EnemyKilled (was-in-combat gate fails).
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: out of combat, alive
        combatProbe.SetInCombat(false);
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        // Poll 2: dead, still out of combat
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        var killed = KilledEvents(writer);
        Assert.Empty(killed);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U7 — InCombat dedup-on-change is unchanged
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U7_InCombatTransitionEmittedOnlyOnChange()
    {
        /*
         * This tests unchanged behaviour — expected to pass today and after the rework.
         *
         * Given IsInCombat() returns: false→true→true→false across 4 polls (no hostiles);
         * Then exactly TWO InCombat observations:
         *   - poll 2: {value:true}
         *   - poll 4: {value:false}
         * Polls 1 and 3 produce NO InCombat write.
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        combatProbe.SetInCombat(false);
        DrivePoll(framework, clock); // poll 1

        combatProbe.SetInCombat(true);
        DrivePoll(framework, clock); // poll 2: false→true

        combatProbe.SetInCombat(true);
        DrivePoll(framework, clock); // poll 3: no change

        combatProbe.SetInCombat(false);
        DrivePoll(framework, clock); // poll 4: true→false

        var inCombatEvents = InCombatEvents(writer);

        Assert.True(inCombatEvents.Count == 2,
            $"Expected exactly 2 InCombat observations, got {inCombatEvents.Count}. Events: {FormatEvents(writer)}");

        Assert.True(GetInCombatValue(inCombatEvents[0]),
            "First InCombat event must be {value:true}");
        Assert.False(GetInCombatValue(inCombatEvents[1]),
            "Second InCombat event must be {value:false}");

        Assert.All(inCombatEvents, e => Assert.Null(e.Data.Argument));

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U8 — multiple deaths same poll emit one EnemyKilled each
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U8_MultipleDeathsSamePoll_EmitsOneKillPerObjectId()
    {
        /*
         * RED: Old logic fires on disappearance only — both are still visible (dead),
         * so zero kills emitted. New logic must emit two (one per ObjectId transition).
         *
         * Given IsInCombat()==true and (obj=1,data=347,alive), (obj=2,data=347,alive) at poll 1;
         * When poll 2 has both dead, still in combat;
         * Then exactly TWO EnemyKilled, both dataId==347u.
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: two alive hostiles
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((1UL, 347u, false), (2UL, 347u, false));
        DrivePoll(framework, clock);

        // Poll 2: both dead
        combatProbe.SetHostiles((1UL, 347u, true), (2UL, 347u, true));
        DrivePoll(framework, clock);

        var killed = KilledEvents(writer);
        Assert.True(killed.Count == 2,
            $"Expected exactly 2 EnemyKilled (one per ObjectId), got {killed.Count}. Events: {FormatEvents(writer)}");

        Assert.All(killed, e => Assert.Equal(347u, GetDataId(e)));

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U9 — revive (dead→alive→dead) emits on each genuine alive→dead transition
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U9_ReviveAndReKill_EmitsTwoEnemyKilled()
    {
        /*
         * RED: Old logic cannot model revival (presence-only tracking); this is a new
         * scenario entirely. Will emit 0 or wrong count.
         *
         * Given (obj=1,data=347): alive(p1)→dead(p2)→alive(p3)→dead(p4), all in combat;
         * Then exactly TWO EnemyKilled (poll 2 and poll 4).
         * The WasDead latch resets when the mob returns alive, enabling a second transition.
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        combatProbe.SetInCombat(true);

        // Poll 1: alive
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        // Poll 2: dead (first kill)
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        // Poll 3: alive again (revive)
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        // Poll 4: dead again (second kill)
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        var killed = KilledEvents(writer);
        Assert.True(killed.Count == 2,
            $"Expected exactly 2 EnemyKilled across alive→dead→alive→dead, got {killed.Count}. Events: {FormatEvents(writer)}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U10 — ResetHeartbeatState clears _hostileStates; no phantom kill on next poll
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U10_ResetHeartbeatState_ClearsHostileStates_NoPhantomKill()
    {
        /*
         * RED: Old logic uses _trackedHostiles (cleared by reset). After reset, the
         * same mob with IsDead:true on poll 2 is seen as already-dead-on-first-sight
         * per D4 — no phantom kill. However, old PollCombat uses tracked-then-gone so
         * this specific assertion (zero new kills after reset when mob arrives dead) is
         * the new D4 semantics; will fail if PollCombat is not reworked.
         *
         * Given poll 1 in combat with (obj=1,data=347,alive) — state established + InCombat{true} emitted;
         * When ResetHeartbeatState(), then poll 2 with (obj=1,data=347,dead), in combat;
         * Then ZERO new EnemyKilled (first-seen-dead after reset → skipped);
         *      AND InCombat{value:true} re-emits on poll 2 (because _lastInCombat reset to false).
         */

        var (observer, framework, combatProbe, clock, writer) = BuildCombatFixture();

        // Poll 1: in combat, alive — establishes state
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((1UL, 347u, false));
        DrivePoll(framework, clock);

        var inCombatBeforeReset = InCombatEvents(writer).Count;
        Assert.True(inCombatBeforeReset >= 1, "Expected InCombat{true} emitted on poll 1");

        // Reset
        observer.ResetHeartbeatState();

        var killedBeforeReset = KilledEvents(writer).Count;

        // Poll 2: same mob now dead — first-seen-dead after reset → must be skipped
        combatProbe.SetInCombat(true);
        combatProbe.SetHostiles((1UL, 347u, true));
        DrivePoll(framework, clock);

        // Assert A: no new kill
        var killedAfterPoll2 = KilledEvents(writer).Count;
        Assert.Equal(killedBeforeReset, killedAfterPoll2);

        // Assert B: InCombat{true} re-emits because _lastInCombat reset to false
        var inCombatAfterReset = InCombatEvents(writer).Skip(inCombatBeforeReset).ToList();
        Assert.True(inCombatAfterReset.Count >= 1,
            $"Expected InCombat re-emit after ResetHeartbeatState. Events: {FormatEvents(writer)}");
        Assert.True(GetInCombatValue(inCombatAfterReset[0]),
            "InCombat re-emitted after reset must be {value:true}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-U11 — no ICombatProbe → no combat observations (back-compat)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_U11_NoCombatProbe_NoCombatObservationsEver()
    {
        /*
         * Expected to pass today and after rework (null probe guard unchanged).
         *
         * Given UIObserver built WITHOUT a combatProbe (default null);
         * When several heartbeats fire;
         * Then ZERO EnemyKilled and ZERO InCombat observations ever.
         */

        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();

        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            // combatProbe intentionally omitted → default null
            clock:        clock);

        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            framework.Tick();
        }

        var combatObs = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "EnemyKilled" || e.Data.Method == "InCombat")
            .ToList();

        Assert.Empty(combatObs);

        observer.Dispose();
    }
}
