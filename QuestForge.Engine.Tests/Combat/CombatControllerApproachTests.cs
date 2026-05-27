using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: CombatController approach-to-target tests — G8–G24.
///
/// All tests fail to compile until the builder:
///   1. Adds INavigator param to CombatController ctor (3-arg).
///   2. Adds ApproachState enum and Approach field to CombatDecision.
///   3. Implements the D4 cadence and D8 ordering inside Decide.
///   4. Clears _approachTarget in Reset (without calling Stop).
///
/// Spec: docs/COMBAT_MOVE_TO_TARGET_PLAN.md §G8–G24
/// </summary>
public sealed class CombatControllerApproachTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatErrors(CombatDecision d)
        => $"Target={d.Target?.Id}, RotationShouldRun={d.RotationShouldRun}, Reason={d.Reason}, Approach={d.Approach}";

    private static string FormatNavRequests(FakeNavigator nav)
        => string.Join("; ", nav.RecordedNavigationRequests.Snapshot()
            .Select(r => $"Dest={r.Destination} Stop={r.Options.StoppingDistance} Mount={r.Options.UseMount} Flight={r.Options.UseFlight}"));

    /// <summary>
    /// Builds a HostileActor. Position defaults to origin; pass a distinct position for
    /// approach/navigation destination assertions (G8, G13, G14, G18, G19, G23).
    /// DistanceToPlayer is always specified independently of Position so the nav destination
    /// and the in-range check can be tested independently.
    /// </summary>
    private static HostileActor MakeHostile(
        ulong id,
        uint dataId,
        float distance,
        WorldPosition? position = null,
        bool isTargetingPlayer = false,
        bool isDead = false,
        bool isTargetable = true)
        => new HostileActor(
            new ActorId(id),
            dataId,
            position ?? new WorldPosition(0f, 0f, 0f),
            distance,
            isTargetable,
            isDead,
            isTargetingPlayer,
            OnPlayerEnmityList: false,
            HasQuestMarker: false);

    private static CombatStep MakeStep(uint[] killIds, CombatSpawn spawn = CombatSpawn.AutoOnEnterArea)
        => new CombatStep
        {
            Id = "approach-test",
            KillEnemyDataIds = killIds,
            Spawn = spawn
        };

    /// <summary>Creates a controller + its fakes. Job defaults to PGL (melee, JobId(2)).</summary>
    private static (CombatController Controller, FakeGameStateProvider GameState, FakeCombat Combat, FakeNavigator Navigator)
        MakeController(uint jobId = 2, int level = 50)
    {
        var gameState  = new FakeGameStateProvider();
        var combat     = new FakeCombat();
        var navigator  = new FakeNavigator(gameState);

        gameState.SetJob(new JobId(jobId), level);

        // CombatController now takes INavigator as the third ctor param.
        // This will FAIL TO COMPILE until the builder adds the param — correct RED state.
        var controller = new CombatController(gameState, combat, navigator);
        return (controller, gameState, combat, navigator);
    }

    // =========================================================================
    // G8 — melee approaches out-of-range target
    // =========================================================================

    [Fact]
    public async Task G8_MeleeOutOfRange_DecideOnce_IssuesNavigateTo()
    {
        var (controller, gameState, combat, navigator) = MakeController(jobId: 2 /* PGL */);
        var targetPos = new WorldPosition(10f, 0f, 0f);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: targetPos));
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);

        // Exactly one NavigateTo
        var navReqs = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Single(navReqs);

        // Destination matches the actor's Position
        Assert.Equal(targetPos, navReqs[0].Destination);

        // Options: StoppingDistance = ApproachStopBuffer (0.5 m), NOT attackRange (1.0 m).
        // A3 (addendum): navigate stop distance is decoupled from attack range (G30).
        Assert.Equal(0.5f,  navReqs[0].Options.StoppingDistance);
        Assert.False(navReqs[0].Options.UseMount,   $"UseMount should be false; nav={FormatNavRequests(navigator)}");
        Assert.False(navReqs[0].Options.UseFlight,  $"UseFlight should be false; nav={FormatNavRequests(navigator)}");

        // Decision reports Approaching, rotation still runs
        Assert.Equal(ApproachState.Approaching, decision.Approach);
        Assert.True(decision.RotationShouldRun, $"RotationShouldRun should be true while approaching; {FormatErrors(decision)}");

        // No spurious Stop issued
        Assert.Empty(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // G9 — ranged job already in range — no nav
    // =========================================================================

    [Fact]
    public async Task G9_RangedInRange_DecideOnce_NoNavIssued()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 23 /* BRD — 20 m */);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));  // 8 m < 20 m
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);

        Assert.Empty(navigator.RecordedNavigationRequests.Snapshot());
        Assert.Empty(navigator.RecordedStops.Snapshot());
        Assert.Equal(ApproachState.InRange, decision.Approach);
        Assert.True(decision.RotationShouldRun, FormatErrors(decision));
    }

    // =========================================================================
    // G10 — melee in range — no nav
    // =========================================================================

    [Fact]
    public async Task G10_MeleeInRange_DecideOnce_NoNavIssued()
    {
        // A2 (addendum): PLD (Tank) attack range is now 1.0 m; distance 1f is exactly at the new boundary.
        var (controller, gameState, _, navigator) = MakeController(jobId: 19 /* PLD — 1 m */);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 1f));  // exactly at boundary → in range
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);

        Assert.Empty(navigator.RecordedNavigationRequests.Snapshot());
        Assert.Empty(navigator.RecordedStops.Snapshot());
        Assert.Equal(ApproachState.InRange, decision.Approach);
    }

    // =========================================================================
    // G11 — exact boundary is in range (DistanceToPlayer == attackRange)
    // =========================================================================

    [Fact]
    public async Task G11_ExactBoundary_TreatedAsInRange()
    {
        // A2 (addendum): PGL attack range is now 1.0 m; distance 1.0f pins the new exact boundary.
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL, 1 m */);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 1.0f));
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);

        Assert.Empty(navigator.RecordedNavigationRequests.Snapshot());
        Assert.Equal(ApproachState.InRange, decision.Approach);
    }

    // =========================================================================
    // G12 — no re-issue for same out-of-range target on second tick
    // =========================================================================

    [Fact]
    public async Task G12_SameOutOfRangeTarget_TwoTicks_ExactlyOneNavigateTo()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        var step = MakeStep([100u]);

        var d1 = await controller.Decide(step, CancellationToken.None);
        var d2 = await controller.Decide(step, CancellationToken.None);

        var navReqs = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Single(navReqs);  // issued tick 1, not re-issued tick 2
        Assert.Empty(navigator.RecordedStops.Snapshot());

        Assert.Equal(ApproachState.Approaching, d1.Approach);
        Assert.Equal(ApproachState.Approaching, d2.Approach);
    }

    // =========================================================================
    // G13 — re-issue on target change (old target gone, new target appears)
    // =========================================================================

    [Fact]
    public async Task G13_TargetChanges_SecondNavigateToIssued()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        var pos9  = new WorldPosition(8f, 0f, 0f);
        var pos10 = new WorldPosition(5f, 0f, 0f);

        // Tick 1: target Id=9 out of range
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos9));
        var step = MakeStep([100u]);
        await controller.Decide(step, CancellationToken.None);

        // Tick 2: Id=9 removed, new Id=10 also out of range
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(10, 100, distance: 6f, position: pos10));
        await controller.Decide(step, CancellationToken.None);

        var navReqs = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Equal(2, navReqs.Count);  // two distinct targets → two NavigateTo calls
        Assert.Equal(pos9,  navReqs[0].Destination);
        Assert.Equal(pos10, navReqs[1].Destination);
    }

    // =========================================================================
    // G14 — stop on transition into range; _approachTarget cleared
    // =========================================================================

    [Fact]
    public async Task G14_TransitionIntoRange_ExactlyOneStop_InRangeReported()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        var step = MakeStep([100u]);

        // Tick 1: out of range → NavigateTo issued
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        var d1 = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(ApproachState.Approaching, d1.Approach);

        // Tick 2: same target, now in range → Stop issued, InRange reported.
        // A2 (addendum): melee range is now 1 m; use 0.5 m (inside the new ring).
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 0.5f));
        var d2 = await controller.Decide(step, CancellationToken.None);

        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());
        Assert.Single(navigator.RecordedStops.Snapshot());
        Assert.Equal(ApproachState.InRange, d2.Approach);

        // Tick 3: _approachTarget was cleared → staying in range issues no further Stop
        var d3 = await controller.Decide(step, CancellationToken.None);
        Assert.Single(navigator.RecordedStops.Snapshot());  // still just 1
        Assert.Equal(ApproachState.InRange, d3.Approach);
    }

    // =========================================================================
    // G15 — no double-stop while remaining in range (continues G14 tick 3 assertion inline)
    // =========================================================================

    [Fact]
    public async Task G15_RemainingInRange_NoAdditionalStop()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        var step = MakeStep([100u]);

        // Tick 1: out of range
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        await controller.Decide(step, CancellationToken.None);

        // Tick 2: enters range. A2 (addendum): melee range is now 1 m; use 0.5 m.
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 0.5f));
        await controller.Decide(step, CancellationToken.None);

        // Tick 3: still in range — no second Stop
        await controller.Decide(step, CancellationToken.None);

        // Tick 4: still in range — no third Stop
        await controller.Decide(step, CancellationToken.None);

        Assert.Single(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // G16 — target dies mid-approach → Stop issued, None reported
    // =========================================================================

    [Fact]
    public async Task G16_TargetDiesMidApproach_StopIssued_NoneReported()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        var step = MakeStep([100u]);

        // Tick 1: out of range → NavigateTo
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        await controller.Decide(step, CancellationToken.None);

        // Tick 2: no eligible targets (all cleared)
        gameState.ClearHostileActors();
        var d2 = await controller.Decide(step, CancellationToken.None);

        Assert.Null(d2.Target);
        Assert.False(d2.RotationShouldRun, FormatErrors(d2));
        Assert.Equal(ApproachState.None, d2.Approach);

        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());
        Assert.Single(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // G17 — no target from first tick → no Stop, no NavigateTo
    // =========================================================================

    [Fact]
    public async Task G17_NoTargetFromFirstTick_NoNavNoStop()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        var step = MakeStep([100u]);
        // No hostiles added

        var d = await controller.Decide(step, CancellationToken.None);

        Assert.Null(d.Target);
        Assert.False(d.RotationShouldRun, FormatErrors(d));
        Assert.Equal(ApproachState.None, d.Approach);
        Assert.Empty(navigator.RecordedNavigationRequests.Snapshot());
        Assert.Empty(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // G18 — NavmeshUnavailable does not throw; latch is still set
    // =========================================================================

    [Fact]
    public async Task G18_NavmeshUnavailable_DoesNotThrow_LatchSet()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        // Script NavmeshUnavailable for tick 1's NavigateTo call
        navigator.ScriptNextResult(NavigationOutcome.NavmeshUnavailable);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        var step = MakeStep([100u]);

        // Tick 1: must not throw even with NavmeshUnavailable
        CombatDecision tick1 = await controller.Decide(step, CancellationToken.None);

        Assert.NotNull(tick1.Target);
        Assert.Equal(new ActorId(9), tick1.Target!.Value.Id);
        Assert.True(tick1.RotationShouldRun, FormatErrors(tick1));
        Assert.Equal(ApproachState.Approaching, tick1.Approach);
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());

        // Tick 2: same target still out of range — latch (_approachTarget == target) set
        // despite the failed nav → no second NavigateTo issued (no spam on dead navmesh)
        await controller.Decide(step, CancellationToken.None);
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());
    }

    // =========================================================================
    // G19 — NavigateTo hard fail (Result.Fail) does not throw
    // =========================================================================

    [Fact]
    public async Task G19_NavHardFail_DoesNotThrow_ApproachingReported()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        navigator.FailNextWith("navmeshUnavailable");
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        var step = MakeStep([100u]);

        CombatDecision decision = default!;
        var ex = await Record.ExceptionAsync(async () =>
        {
            decision = await controller.Decide(step, CancellationToken.None);
        });

        Assert.Null(ex);
        Assert.True(decision.RotationShouldRun, FormatErrors(decision));
        Assert.Equal(ApproachState.Approaching, decision.Approach);
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());
    }

    // =========================================================================
    // G20 — GetCurrentJob fails → FallbackRange (3 m) applied
    // =========================================================================

    [Fact]
    public async Task G20_GetCurrentJobFails_FallbackRangeApplied()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 23 /* BRD, 20 m */);
        // Override job so GetCurrentJob fails → fallback range 3 m kicks in
        gameState.SetCurrentJobFail("noLocalPlayer");
        // Target at 4 m: would be InRange for BRD (20 m) but out of FallbackRange (3 m)
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 4f));
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);

        var navReqs = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Single(navReqs);
        // A3 (addendum): StoppingDistance is ApproachStopBuffer (0.5 m), decoupled from FallbackRange.
        Assert.Equal(0.5f, navReqs[0].Options.StoppingDistance);
        Assert.Equal(ApproachState.Approaching, decision.Approach);
    }

    // =========================================================================
    // G21 — hostile query fails while approaching → Stop issued
    // =========================================================================

    [Fact]
    public async Task G21_HostileQueryFailsWhileApproaching_StopIssued()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        var step = MakeStep([100u]);

        // Tick 1: out of range → NavigateTo issued
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        await controller.Decide(step, CancellationToken.None);

        // Tick 2: GetHostileActors fails
        gameState.SetHostileActorsFailure("boom");
        var d2 = await controller.Decide(step, CancellationToken.None);

        Assert.Null(d2.Target);
        Assert.False(d2.RotationShouldRun, FormatErrors(d2));
        Assert.Equal("hostile query failed", d2.Reason);
        Assert.Equal(ApproachState.None, d2.Approach);

        Assert.Single(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // G22 — Reset clears approach latch; does NOT call Stop
    // =========================================================================

    [Fact]
    public async Task G22_Reset_ClearsApproachLatch_DoesNotStop()
    {
        var (controller, gameState, _, navigator) = MakeController(jobId: 2);
        var step = MakeStep([100u]);

        // Tick 1: establish latch
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: new WorldPosition(8f, 0f, 0f)));
        await controller.Decide(step, CancellationToken.None);
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());

        // Reset — must NOT call Stop
        controller.Reset();
        Assert.Empty(navigator.RecordedStops.Snapshot());

        // Tick after Reset: same target still out of range → new NavigateTo (latch cleared)
        var d = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(2, navigator.RecordedNavigationRequests.Snapshot().Count);  // two total: pre-Reset + post-Reset
        Assert.Equal(ApproachState.Approaching, d.Approach);
    }

    // =========================================================================
    // G23 — SetTarget precedes NavigateTo in the same tick
    // =========================================================================

    [Fact]
    public async Task G23_SetTargetBeforeNavigateTo_SameTick()
    {
        var (controller, gameState, combat, navigator) = MakeController(jobId: 2);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f));
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);

        var targets = combat.RecordedTargets.Snapshot();
        var navReqs = navigator.RecordedNavigationRequests.Snapshot();

        Assert.NotEmpty(targets);
        Assert.NotEmpty(navReqs);

        // SetTarget must have been recorded at or before NavigateTo
        var setTargetAt  = targets.Where(t => !t.IsClear).Select(t => t.At).First();
        var navigateToAt = navReqs.Select(r => r.At).First();
        Assert.True(setTargetAt <= navigateToAt,
            $"SetTarget ({setTargetAt:O}) must precede or coincide with NavigateTo ({navigateToAt:O})");
    }

    // =========================================================================
    // G24 — null navigator throws ArgumentNullException
    // =========================================================================

    [Fact]
    public void G24_NullNavigator_ThrowsArgumentNullException()
    {
        var gameState = new FakeGameStateProvider();
        var combat    = new FakeCombat();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new CombatController(gameState, combat, null!));

        Assert.Equal("navigator", ex.ParamName);
    }

    // =========================================================================
    // G25 — target moves beyond RepathThreshold (0.5 m) during approach → re-path
    //        Addendum A3: drift > attackRange - ApproachStopBuffer = 1.0 - 0.5 = 0.5
    // =========================================================================

    [Fact]
    public async Task G25_TargetMovesBeyondThreshold_RePathIssued()
    {
        // GIVEN PGL (melee, attackRange=1.0, ApproachStopBuffer=0.5, RepathThreshold=0.5).
        //       Tick 1: hostile Id 9 at (10,0,0), distance 8f (out of range).
        //       → one NavigateTo((10,0,0)), StoppingDistance=0.5, _approachPosition=(10,0,0).
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL */);
        var pos1 = new WorldPosition(10f, 0f, 0f);
        var pos2 = new WorldPosition(20f, 0f, 0f);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos1));
        var step = MakeStep([100u]);

        var d1 = await controller.Decide(step, CancellationToken.None);

        var navReqs1 = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Single(navReqs1);
        Assert.Equal(pos1, navReqs1[0].Destination);
        Assert.Equal(ApproachState.Approaching, d1.Approach);

        // WHEN tick 2: same Id 9 moved to (20,0,0) (drift = 10 m >> 0.5 RepathThreshold),
        //              still out of range (distance 12f).
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 12f, position: pos2));

        var d2 = await controller.Decide(step, CancellationToken.None);

        // THEN a second NavigateTo is recorded to the new position, StoppingDistance=0.5.
        // FAILS ASSERTION: current code has no _approachPosition latch — the "same target"
        // else branch does nothing, so no second nav is issued.
        var navReqs2 = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Equal(2, navReqs2.Count);
        Assert.Equal(pos2, navReqs2[1].Destination);
        Assert.Equal(0.5f, navReqs2[1].Options.StoppingDistance);
        Assert.Equal(ApproachState.Approaching, d2.Approach);
        Assert.Empty(navigator.RecordedStops.Snapshot()); // no Stop; target still live
    }

    // =========================================================================
    // G26 — target jitters ≤ RepathThreshold (0.5 m) → no re-path (anti-spam)
    //        Pins that sub-threshold wobble does NOT trigger a second NavigateTo.
    // =========================================================================

    [Fact]
    public async Task G26_TargetJittersBelowThreshold_NoRePath()
    {
        // GIVEN PGL (RepathThreshold=0.5); tick 1 Id 9 at (10,0,0), distance 8f → one NavigateTo.
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL */);
        var pos1 = new WorldPosition(10f, 0f, 0f);
        var pos2 = new WorldPosition(10.4f, 0f, 0f); // drift = 0.4 ≤ 0.5
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos1));
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);

        // WHEN tick 2: same Id 9 at (10.4,0,0) (drift 0.4 ≤ RepathThreshold 0.5),
        //              still out of range (distance still 8f).
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos2));

        var d2 = await controller.Decide(step, CancellationToken.None);

        // THEN exactly ONE NavigateTo total — no re-path for sub-threshold jitter.
        // This test may PASS with current code (no _approachPosition, same-target else does nothing).
        // It's a regression guard: ensure the new drift check doesn't over-trigger.
        var navReqs = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Single(navReqs);
        Assert.Equal(ApproachState.Approaching, d2.Approach);
        Assert.Empty(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // G27 — identity change still re-paths (regression guard under the new drift branch)
    //        The identity-change condition must short-circuit to re-path before drift check.
    // =========================================================================

    [Fact]
    public async Task G27_IdentityChange_RePathFires_EvenWithNewDriftBranch()
    {
        // GIVEN PGL; tick 1: Id 9 at (8,0,0), distance 8f → nav #1.
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL */);
        var pos9  = new WorldPosition(8f, 0f, 0f);
        var pos10 = new WorldPosition(6f, 0f, 0f); // positions are close (drift=2, > threshold)
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos9));
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);

        // WHEN tick 2: Id 9 removed, new Id 10 appears at a close but different position.
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(10, 100, distance: 6f, position: pos10));

        var d2 = await controller.Decide(step, CancellationToken.None);

        // THEN two NavigateTo calls recorded (identity change triggers re-path regardless of drift).
        // FAILS ASSERTION if identity-change is accidentally gated behind _approachPosition drift.
        var navReqs = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Equal(2, navReqs.Count);
        Assert.Equal(pos9,  navReqs[0].Destination);
        Assert.Equal(pos10, navReqs[1].Destination);
        Assert.Equal(ApproachState.Approaching, d2.Approach);
    }

    // =========================================================================
    // G28 — _approachPosition cleared on in-range transition → fresh approach
    //        re-paths when the same target re-leaves range.
    // =========================================================================

    [Fact]
    public async Task G28_ApproachPositionClearedOnInRange_FreshNavOnReLeave()
    {
        // GIVEN PGL; tick 1: Id 9 at (10,0,0), distance 8f (out of range) → nav#1.
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL */);
        var pos1 = new WorldPosition(10f, 0f, 0f);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos1));
        var step = MakeStep([100u]);

        var d1 = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(ApproachState.Approaching, d1.Approach);
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());

        // Tick 2: same Id 9 now distance 0.5f (in range, ≤ 1.0 attackRange) → Stop; _approachPosition cleared.
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 0.5f, position: pos1));

        var d2 = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(ApproachState.InRange, d2.Approach);
        Assert.Single(navigator.RecordedStops.Snapshot());

        // Tick 3: same Id 9 moves back out to (10,0,0), distance 8f.
        // Because _approachPosition was cleared on in-range, it is now null → fresh NavigateTo fires.
        // FAILS ASSERTION: current code clears _approachTarget on in-range but has no _approachPosition,
        // so tick 3 would correctly re-nav (existing G14 proves this). G28 extends to verify the
        // _approachPosition null path specifically forces a re-nav even if identity is unchanged.
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos1));

        var d3 = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(ApproachState.Approaching, d3.Approach);

        // 2 navs total (tick1 + tick3); 1 stop (tick2).
        Assert.Equal(2, navigator.RecordedNavigationRequests.Snapshot().Count);
        Assert.Single(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // G29 — Reset() clears _approachPosition (IO-free); subsequent same-position tick
    //        issues a fresh NavigateTo (proves the latch was cleared, not just _approachTarget).
    //        Extends G22.
    // =========================================================================

    [Fact]
    public async Task G29_Reset_ClearsApproachPosition_FreshNavAfterReset()
    {
        // GIVEN an approach latched at (10,0,0) (nav#1, _approachPosition=(10,0,0)).
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL */);
        var pos = new WorldPosition(10f, 0f, 0f);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos));
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());

        // WHEN synchronous Reset() — must be IO-free (G22/CS-7 contract unchanged).
        controller.Reset();
        Assert.Empty(navigator.RecordedStops.Snapshot()); // still no Stop

        // THEN a subsequent tick with the same target at the SAME position issues a fresh NavigateTo.
        // If _approachPosition were NOT cleared, drift==0 ≤ threshold → else branch → no nav.
        // If _approachPosition IS cleared (null), the first-nav path fires → nav#2.
        // FAILS ASSERTION: current code only clears _approachTarget in Reset(); if _approachPosition
        // is never introduced, the else-branch would suppress the second nav once it IS introduced
        // unless the null guard fires correctly.
        var d2 = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(2, navigator.RecordedNavigationRequests.Snapshot().Count);
        Assert.Equal(pos, navigator.RecordedNavigationRequests.Snapshot()[1].Destination);
        Assert.Equal(ApproachState.Approaching, d2.Approach);
        Assert.Empty(navigator.RecordedStops.Snapshot()); // still no Stop from Reset
    }

    // =========================================================================
    // G30 — approach StoppingDistance is ApproachStopBuffer (0.5 m), decoupled from attackRange
    //        Pins that the navigate stop distance is independent of the attack range.
    //        This supersedes the old G8 "3.0f" value (already updated above).
    // =========================================================================

    [Fact]
    public async Task G30_ApproachStoppingDistance_IsApproachStopBuffer_NotAttackRange()
    {
        // GIVEN PGL (attackRange now 1.0 m, ApproachStopBuffer = 0.5 m — distinct values).
        //       Out-of-range hostile.
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL */);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: new WorldPosition(8f, 0f, 0f)));
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);

        // THEN the recorded NavigationOptions.StoppingDistance is 0.5f (ApproachStopBuffer),
        // NOT 1.0f (attackRange). Decoupling is the key property that makes the no-strand proof hold.
        // FAILS ASSERTION: current code uses StoppingDistance: attackRange (was 3.0f, now 1.0f with
        // Change 1, but still wrong — should be 0.5f ApproachStopBuffer).
        var navReqs = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Single(navReqs);
        Assert.Equal(0.5f, navReqs[0].Options.StoppingDistance);
    }

    // =========================================================================
    // G31 — small step then idle: player closes to attack range (no-strand pin)
    //        The case that STALLED IN-GAME: drift 0.8 > RepathThreshold 0.5 → re-path.
    //        Under old design (threshold=attackRange=1.0): 0.8 ≤ 1.0 → no re-path → stall.
    // =========================================================================

    [Fact]
    public async Task G31_SmallStepThenIdle_PlayerClosesToAttackRange_NoStrand()
    {
        // GIVEN PGL (attackRange=1.0, ApproachStopBuffer=0.5, RepathThreshold=0.5).
        //       Tick 1: Id 9 at (10,0,0), distance 8f (out of range) → nav#1 to (10,0,0), Stop=0.5.
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL */);
        var pos1 = new WorldPosition(10f, 0f, 0f);
        var pos2 = new WorldPosition(10.8f, 0f, 0f); // drift = 0.8 > RepathThreshold 0.5
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8f, position: pos1));
        var step = MakeStep([100u]);

        var d1 = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(ApproachState.Approaching, d1.Approach);
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());
        Assert.Equal(pos1, navigator.RecordedNavigationRequests.Snapshot()[0].Destination);
        Assert.Equal(0.5f, navigator.RecordedNavigationRequests.Snapshot()[0].Options.StoppingDistance);

        // WHEN tick 2: same Id 9 took a small step to (10.8,0,0) (drift=0.8), then IDLES.
        //              Still out of range (distance: 8.8f).
        //              drift 0.8 > RepathThreshold 0.5 → second NavigateTo fires.
        // Under OLD design: drift 0.8 ≤ old threshold 1.0 → no re-path → player parks at (10,0,0)
        // while target is at (10.8,0,0), distance ≈ 1.8 > attackRange 1.0 → PERMANENT STALL.
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 8.8f, position: pos2));

        var d2 = await controller.Decide(step, CancellationToken.None);

        // THEN nav#2 to (10.8,0,0) is issued.
        // FAILS ASSERTION: current code has no _approachPosition → same-target else does nothing.
        var navReqs2 = navigator.RecordedNavigationRequests.Snapshot();
        Assert.Equal(2, navReqs2.Count);
        Assert.Equal(pos2, navReqs2[1].Destination);
        Assert.Equal(0.5f, navReqs2[1].Options.StoppingDistance);
        Assert.Equal(ApproachState.Approaching, d2.Approach);

        // WHEN tick 3: Id 9 still idle at (10.8,0,0); vnavmesh has arrived 0.5m short →
        //              distance: 0.5f (in range, ≤ attackRange 1.0). _approachPosition cleared.
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 0.5f, position: pos2));

        var d3 = await controller.Decide(step, CancellationToken.None);

        // THEN tick 3 is InRange; Stop recorded; no third NavigateTo.
        // Total: 2 navs (tick1 + tick2), 1 Stop (tick3).
        Assert.Equal(ApproachState.InRange, d3.Approach);
        Assert.Single(navigator.RecordedStops.Snapshot());
        Assert.Equal(2, navigator.RecordedNavigationRequests.Snapshot().Count); // no third nav
    }
}
