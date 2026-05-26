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

        // Options: StoppingDistance = melee (3 m), no mount, no flight
        Assert.Equal(3.0f,  navReqs[0].Options.StoppingDistance);
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
        var (controller, gameState, _, navigator) = MakeController(jobId: 19 /* PLD — 3 m */);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 3f));  // exactly at boundary → in range
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
        var (controller, gameState, _, navigator) = MakeController(jobId: 2 /* PGL, 3 m */);
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 3.0f));
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

        // Tick 2: same target, now in range → Stop issued, InRange reported
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 2f));
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

        // Tick 2: enters range
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 100, distance: 2f));
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
        Assert.Equal(3.0f, navReqs[0].Options.StoppingDistance);
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
}
