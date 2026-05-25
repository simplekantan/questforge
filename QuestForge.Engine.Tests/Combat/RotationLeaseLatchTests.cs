using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE — G-LL: RotationLeaseLatch transition rules, tested against FakeCombat.
///
/// All tests fail RED because RotationLeaseLatch.OnAction and Release throw NotImplementedException.
///
/// RotationLeaseLatch lives in QuestForge.Engine.Combat (CI-buildable, pure C#, no Dalamud).
/// Tests verify the 8 transition scenarios in §G-LL:
///   LL-first-engage-starts, LL-repeat-engage-noop, LL-engage-null-holds,
///   LL-nonengage-after-engage-stops, LL-nonengage-while-unlatched-noop,
///   LL-release-while-latched-stops, LL-release-while-unlatched-noop, LL-terminal-stops.
///
/// Spec: docs/COMBAT_STEP_PART_B_PLAN.md §G-LL, §A4
/// </summary>
public sealed class RotationLeaseLatchTests
{
    // Helper: build a minimal CombatStep for Engage actions
    private static CombatStep MakeStep() => new CombatStep
    {
        Id = "ll-test-combat",
        KillEnemyDataIds = [100u],
        Spawn = CombatSpawn.AutoOnEnterArea
    };

    private static KillTarget MakeTarget() => new KillTarget(new ActorId(9), 100u);

    private static string FormatRotation(FakeCombat combat)
        => string.Join(", ", combat.RecordedRotation.Snapshot().Select(r => r.Method));

    // -------------------------------------------------------------------------
    // LL-first-engage-starts
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-first-engage-starts: first Engage on a fresh latch calls StartRotation once,
    /// returns LeaseAct.Started, and InLease becomes true.
    /// </summary>
    [Fact]
    public async Task LL_FirstEngage_Starts_ReturnsStarted_InLeaseTrue()
    {
        // RED: RotationLeaseLatch.OnAction throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();
        var step = MakeStep();
        var target = MakeTarget();

        Assert.False(latch.InLease, "Fresh latch must have InLease == false");

        var act = await latch.OnAction(new EngineAction.Engage(step, target), combat, CancellationToken.None);

        Assert.Equal(LeaseAct.Started, act);
        Assert.True(latch.InLease);

        var rot = combat.RecordedRotation.Snapshot();
        Assert.Equal(1, rot.Count);
        Assert.Equal("StartRotation", rot[0].Method);
    }

    // -------------------------------------------------------------------------
    // LL-repeat-engage-noop
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-repeat-engage-noop: a second Engage when already latched returns LeaseAct.None,
    /// InLease stays true, and no new StartRotation is issued (no per-tick IPC churn).
    /// </summary>
    [Fact]
    public async Task LL_RepeatEngage_Noop_NoExtraStart()
    {
        // RED: throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();
        var step = MakeStep();
        var target = MakeTarget();

        // First Engage — latches
        await latch.OnAction(new EngineAction.Engage(step, target), combat, CancellationToken.None);
        Assert.True(latch.InLease);
        Assert.Equal(1, combat.RecordedRotation.Count); // one StartRotation

        // Second Engage — must be a no-op
        var act = await latch.OnAction(new EngineAction.Engage(step, target), combat, CancellationToken.None);

        Assert.Equal(LeaseAct.None, act);
        Assert.True(latch.InLease);
        Assert.Equal(1, combat.RecordedRotation.Count); // still only one StartRotation
    }

    // -------------------------------------------------------------------------
    // LL-engage-null-holds
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-engage-null-holds: Engage(step, null) — controller found no target — keeps the latch
    /// held (we keep the lease; WrathCombo idles with no target). Returns LeaseAct.None.
    /// Lease is not released because the combat step is still active.
    /// </summary>
    [Fact]
    public async Task LL_EngageNull_Holds_NoStartStop()
    {
        // RED: throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();
        var step = MakeStep();

        // First engage latches
        await latch.OnAction(new EngineAction.Engage(step, MakeTarget()), combat, CancellationToken.None);
        Assert.True(latch.InLease);

        // Engage(null) — idle tick, no target found
        var act = await latch.OnAction(new EngineAction.Engage(step, null), combat, CancellationToken.None);

        Assert.Equal(LeaseAct.None, act);
        Assert.True(latch.InLease, "Latch must remain held on Engage(null)");

        // Only one StartRotation; no StopRotation
        var rot = combat.RecordedRotation.Snapshot();
        Assert.Equal(1, rot.Count);
        Assert.Equal("StartRotation", rot[0].Method);
    }

    // -------------------------------------------------------------------------
    // LL-nonengage-after-engage-stops
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-nonengage-after-engage-stops: a Navigate action while latched calls StopRotation,
    /// returns LeaseAct.Stopped, and InLease becomes false.
    /// Pins that the lease is released when the next step starts.
    /// </summary>
    [Fact]
    public async Task LL_NonEngageAfterEngage_Stops_ReturnsStopped_InLeaseFalse()
    {
        // RED: throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();
        var step = MakeStep();

        // First Engage — latches
        await latch.OnAction(new EngineAction.Engage(step, MakeTarget()), combat, CancellationToken.None);
        Assert.True(latch.InLease);

        // Navigate (next step began)
        var act = await latch.OnAction(
            new EngineAction.Navigate(new WorldPosition(0f, 0f, 0f), new NavigationOptions()),
            combat, CancellationToken.None);

        Assert.Equal(LeaseAct.Stopped, act);
        Assert.False(latch.InLease);

        // One StartRotation, then one StopRotation
        var rot = combat.RecordedRotation.Snapshot();
        Assert.Equal(2, rot.Count);
        Assert.Equal("StartRotation", rot[0].Method);
        Assert.Equal("StopRotation",  rot[1].Method);
    }

    // -------------------------------------------------------------------------
    // LL-nonengage-while-unlatched-noop
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-nonengage-while-unlatched-noop: a Wait action on a fresh latch (never engaged)
    /// returns LeaseAct.None and issues no Start/Stop. Pins we never stop a lease we never started.
    /// </summary>
    [Fact]
    public async Task LL_NonEngageWhileUnlatched_Noop_NoStartStop()
    {
        // RED: throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();

        var act = await latch.OnAction(
            new EngineAction.Wait("idle", null),
            combat, CancellationToken.None);

        Assert.Equal(LeaseAct.None, act);
        Assert.False(latch.InLease);
        Assert.Equal(0, combat.RecordedRotation.Count);
    }

    // -------------------------------------------------------------------------
    // LL-release-while-latched-stops
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-release-while-latched-stops: Release() while latched (e.g. run torn down mid-combat)
    /// calls StopRotation, returns LeaseAct.Stopped, InLease becomes false.
    /// Pins interruption safety — EndRun must not leak the WrathCombo lease.
    /// </summary>
    [Fact]
    public async Task LL_ReleaseWhileLatched_Stops_ReturnsStopped()
    {
        // RED: RotationLeaseLatch.Release throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();
        var step = MakeStep();

        // Latch it
        await latch.OnAction(new EngineAction.Engage(step, MakeTarget()), combat, CancellationToken.None);
        Assert.True(latch.InLease);

        // Force-release (EndRun / StopRun path)
        var act = await latch.Release(combat, CancellationToken.None);

        Assert.Equal(LeaseAct.Stopped, act);
        Assert.False(latch.InLease);

        var rot = combat.RecordedRotation.Snapshot();
        Assert.Equal(2, rot.Count);
        Assert.Equal("StartRotation", rot[0].Method);
        Assert.Equal("StopRotation",  rot[1].Method);
    }

    // -------------------------------------------------------------------------
    // LL-release-while-unlatched-noop
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-release-while-unlatched-noop: Release() on a fresh latch is a no-op.
    /// Returns LeaseAct.None and issues no StopRotation.
    /// </summary>
    [Fact]
    public async Task LL_ReleaseWhileUnlatched_Noop_NoStop()
    {
        // RED: RotationLeaseLatch.Release throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();

        var act = await latch.Release(combat, CancellationToken.None);

        Assert.Equal(LeaseAct.None, act);
        Assert.False(latch.InLease);
        Assert.Equal(0, combat.RecordedRotation.Count);
    }

    // -------------------------------------------------------------------------
    // LL-terminal-stops
    // -------------------------------------------------------------------------

    /// <summary>
    /// LL-terminal-stops: Done action while latched calls StopRotation and returns Stopped.
    /// Terminal actions (Done, AwaitUser) are non-Engage → latch releases.
    /// Pins lease release on run completion.
    /// </summary>
    [Fact]
    public async Task LL_TerminalDone_WhileLatched_Stops()
    {
        // RED: throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();
        var step = MakeStep();

        // Latch it
        await latch.OnAction(new EngineAction.Engage(step, MakeTarget()), combat, CancellationToken.None);
        Assert.True(latch.InLease);

        // Terminal Done action
        var act = await latch.OnAction(new EngineAction.Done(), combat, CancellationToken.None);

        Assert.Equal(LeaseAct.Stopped, act);
        Assert.False(latch.InLease);

        var rot = combat.RecordedRotation.Snapshot();
        Assert.Equal(2, rot.Count);
        Assert.Equal("StopRotation", rot[1].Method);
    }

    /// <summary>
    /// LL-terminal-stops (AwaitUser variant): AwaitUser while latched also releases the lease.
    /// </summary>
    [Fact]
    public async Task LL_TerminalAwaitUser_WhileLatched_Stops()
    {
        // RED: throws NotImplementedException.

        var combat = new FakeCombat();
        var latch = new RotationLeaseLatch();
        var step = MakeStep();

        await latch.OnAction(new EngineAction.Engage(step, MakeTarget()), combat, CancellationToken.None);
        Assert.True(latch.InLease);

        var act = await latch.OnAction(
            new EngineAction.AwaitUser("some-reason"),
            combat, CancellationToken.None);

        Assert.Equal(LeaseAct.Stopped, act);
        Assert.False(latch.InLease);
    }
}
