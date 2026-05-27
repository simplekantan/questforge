using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: CombatController.ResetAsync — CS-1..CS-7 from COMBAT_APPROACH_HANDOFF_PLAN.md §4.
///
/// CS-1 and CS-2 are basic Decide regression guards (no new API, verify already-existing behaviour
/// now that Reset changes are coming).
/// CS-3..CS-6 require the new Task ResetAsync(CancellationToken) method — these tests will FAIL
/// TO COMPILE until the Builder adds it to CombatController. That is the intended RED state.
/// CS-7 is a no-IO regression pin on the synchronous Reset().
///
/// Spec: docs/COMBAT_APPROACH_HANDOFF_PLAN.md §4 (Controller-level GWT)
/// </summary>
public sealed class CombatControllerResetAsyncTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatTargets(FakeCombat combat)
        => string.Join(", ", combat.RecordedTargets.Snapshot()
            .Select(t => t.IsClear ? "ClearTarget" : $"SetTarget({t.Target!.Value.Value})"));

    private static string FormatRotation(FakeCombat combat)
        => string.Join(", ", combat.RecordedRotation.Snapshot().Select(r => r.Method));

    private static string FormatStops(FakeNavigator nav)
        => string.Join(", ", nav.RecordedStops.Snapshot().Select(_ => "Stop"));

    /// <summary>
    /// Creates a controller with a PGL job (melee, range now 1 m per addendum A2) and a hostile at
    /// the given distance. Use <paramref name="distance"/> to control in-range (≤ 1 m) vs out-of-range
    /// (> 1 m). NOTE: distance 2 m is now OUT of range (2 > 1); callers that pass 2 m are testing
    /// SetTarget/ClearTarget behaviour which is range-independent — assertions are unaffected.
    /// </summary>
    private static (CombatController Controller, FakeGameStateProvider GameState,
                    FakeCombat Combat, FakeNavigator Navigator)
        MakeEngagedController(float distance = 8f)
    {
        var gameState  = new FakeGameStateProvider();
        var combat     = new FakeCombat();
        var navigator  = new FakeNavigator(gameState);

        gameState.SetJob(new JobId(2 /* PGL */), 50);
        gameState.AddHostileActor(new HostileActor(
            new ActorId(9), 100u,
            new WorldPosition(8f, 0f, 0f),
            DistanceToPlayer: distance,
            IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false));

        // FAILS TO COMPILE until builder adds ResetAsync — but the ctor already exists.
        var controller = new CombatController(gameState, combat, navigator);
        return (controller, gameState, combat, navigator);
    }

    private static CombatStep MakeStep(uint[] killIds)
        => new CombatStep
        {
            Id = "reset-async-test",
            KillEnemyDataIds = killIds,
            Spawn = CombatSpawn.AutoOnEnterArea
        };

    // =========================================================================
    // CS-1 — Decide returns null target when no eligible hostile in range
    // =========================================================================

    [Fact]
    public async Task CS1_Decide_NoHostiles_ReturnsNullTargetAndNoSetTarget()
    {
        // GIVEN a fresh controller, no hostiles added.
        var gameState  = new FakeGameStateProvider();
        var combat     = new FakeCombat();
        var navigator  = new FakeNavigator(gameState);
        gameState.SetJob(new JobId(2), 50);
        var controller = new CombatController(gameState, combat, navigator);
        var step = MakeStep([100u]);

        // WHEN Decide runs once.
        var decision = await controller.Decide(step, CancellationToken.None);

        // THEN Target == null, RotationShouldRun == false, no SetTarget issued.
        Assert.Null(decision.Target);
        Assert.False(decision.RotationShouldRun,
            $"RotationShouldRun must be false when no target; targets={FormatTargets(combat)}");
        var targets = combat.RecordedTargets.Snapshot();
        Assert.Empty(targets.Where(t => !t.IsClear).ToList());
    }

    // =========================================================================
    // CS-2 — Decide returns a target when one eligible hostile is in range
    // =========================================================================

    [Fact]
    public async Task CS2_Decide_OneEligibleHostile_ReturnTargetAndSetTarget()
    {
        // GIVEN one eligible hostile (DataId 100, within attack range for PGL at distance 2 m).
        var gameState  = new FakeGameStateProvider();
        var combat     = new FakeCombat();
        var navigator  = new FakeNavigator(gameState);
        // A2 (addendum): PGL attack range is now 1 m. Distance 2 m is OUT of melee range.
        // This test asserts SetTarget count only — not Approach state — so the assertion is unaffected.
        gameState.SetJob(new JobId(2 /* PGL, 1 m */), 50);
        gameState.AddHostileActor(new HostileActor(
            new ActorId(9), 100u,
            new WorldPosition(0f, 0f, 0f),
            DistanceToPlayer: 2f,   // out of melee range (2 > 1); SetTarget/ClearTarget assertions unaffected
            IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false));
        var controller = new CombatController(gameState, combat, navigator);
        var step = MakeStep([100u]);

        // WHEN Decide runs once.
        var decision = await controller.Decide(step, CancellationToken.None);

        // THEN decision.Target is that actor AND exactly one SetTarget recorded AND
        //      RotationShouldRun == true.
        Assert.NotNull(decision.Target);
        Assert.Equal(new ActorId(9), decision.Target!.Value.Id);
        Assert.True(decision.RotationShouldRun,
            $"RotationShouldRun must be true when a target is selected; {FormatTargets(combat)}");
        var setTargets = combat.RecordedTargets.Snapshot().Where(t => !t.IsClear).ToList();
        Assert.Single(setTargets);
        Assert.Equal(new ActorId(9), setTargets[0].Target!.Value);
    }

    // =========================================================================
    // CS-3 — ResetAsync clears the game target when a target was held
    // =========================================================================
    // NOTE: This test FAILS TO COMPILE until the Builder adds ResetAsync to CombatController.

    [Fact]
    public async Task CS3_ResetAsync_WithHeldTarget_RecordsClearTarget()
    {
        // GIVEN hostile X acquired on tick 1 (SetTarget(X) recorded).
        var (controller, _, combat, _) = MakeEngagedController(distance: 2f); // in range
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);
        Assert.NotNull(decision.Target); // confirm engaged

        var setCountBefore = combat.RecordedTargets.Snapshot().Count(t => !t.IsClear);
        Assert.Equal(1, setCountBefore);

        // WHEN await ResetAsync(ct).
        // FAILS TO COMPILE until Builder adds: public Task ResetAsync(CancellationToken ct)
        await controller.ResetAsync(CancellationToken.None);

        // THEN RecordedTargets ends with exactly one ClearTarget entry.
        var allTargets = combat.RecordedTargets.Snapshot();
        var clears = allTargets.Where(t => t.IsClear).ToList();
        Assert.Single(clears);

        // AND the ClearTarget comes AFTER the SetTarget
        var lastSet   = allTargets.Last(t => !t.IsClear).At;
        var clearAt   = clears[0].At;
        Assert.True(clearAt >= lastSet,
            $"ClearTarget must occur after SetTarget; sequence: {FormatTargets(combat)}");
    }

    // =========================================================================
    // CS-4 — ResetAsync is idempotent when nothing was engaged
    // =========================================================================
    // NOTE: FAILS TO COMPILE until the Builder adds ResetAsync.

    [Fact]
    public async Task CS4_ResetAsync_NothingEngaged_NoIOEmitted()
    {
        // GIVEN a fresh controller, Decide never called (no target, no approach).
        var gameState  = new FakeGameStateProvider();
        var combat     = new FakeCombat();
        var navigator  = new FakeNavigator(gameState);
        gameState.SetJob(new JobId(2), 50);
        var controller = new CombatController(gameState, combat, navigator);

        // WHEN await ResetAsync(ct) with nothing engaged.
        // FAILS TO COMPILE until Builder adds ResetAsync.
        await controller.ResetAsync(CancellationToken.None);

        // THEN NO ClearTarget recorded AND NO Stop recorded. No exception.
        var clears = combat.RecordedTargets.Snapshot().Where(t => t.IsClear).ToList();
        Assert.Empty(clears);
        Assert.Empty(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // CS-5 — ResetAsync stops in-flight approach navigation
    // =========================================================================
    // NOTE: FAILS TO COMPILE until the Builder adds ResetAsync.

    [Fact]
    public async Task CS5_ResetAsync_WithInFlightApproach_IssuesStop()
    {
        // GIVEN a melee job, an out-of-range hostile acquired on tick 1 (NavigateTo issued,
        //       _approachTarget set).
        var (controller, _, _, navigator) = MakeEngagedController(distance: 8f); // out of range
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);
        Assert.NotNull(decision.Target); // engaged
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot()); // approach latch set

        // WHEN await ResetAsync(ct).
        // FAILS TO COMPILE until Builder adds ResetAsync.
        await controller.ResetAsync(CancellationToken.None);

        // THEN navigator.RecordedStops has exactly one Stop.
        Assert.Single(navigator.RecordedStops.Snapshot());
    }

    // =========================================================================
    // CS-6 — ResetAsync never calls StopRotation
    // =========================================================================
    // NOTE: FAILS TO COMPILE until the Builder adds ResetAsync.

    [Fact]
    public async Task CS6_ResetAsync_Never_CallsStopRotation()
    {
        // GIVEN a target acquired (in range so RotationShouldRun is true).
        var (controller, _, combat, _) = MakeEngagedController(distance: 2f);
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);
        combat.RecordedRotation.Clear(); // clear any incidental rotation from Decide

        // WHEN await ResetAsync(ct).
        // FAILS TO COMPILE until Builder adds ResetAsync.
        await controller.ResetAsync(CancellationToken.None);

        // THEN RecordedRotation contains NO StopRotation entry.
        // (Rotation-stop is RotationLeaseLatch's sole responsibility — D3.)
        var stopRotations = combat.RecordedRotation.Snapshot()
            .Where(r => r.Method == nameof(FakeCombat.StopRotation))
            .ToList();
        Assert.Empty(stopRotations);
    }

    // =========================================================================
    // CS-7 — synchronous Reset() still performs no ICombat / navigator IO
    // =========================================================================

    [Fact]
    public async Task CS7_SyncReset_NoIO_NoClearTargetNoStop()
    {
        // GIVEN a target acquired and an out-of-range approach latched.
        var (controller, _, combat, navigator) = MakeEngagedController(distance: 8f);
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);
        // Baseline: one SetTarget, one NavigateTo, zero ClearTarget, zero Stop.
        Assert.Single(combat.RecordedTargets.Snapshot().Where(t => !t.IsClear).ToList());
        Assert.Single(navigator.RecordedNavigationRequests.Snapshot());

        // WHEN the synchronous Reset() is called (NOT ResetAsync).
        controller.Reset();

        // THEN NO ClearTarget recorded, NO Stop recorded, NO StopRotation recorded.
        var clears = combat.RecordedTargets.Snapshot().Where(t => t.IsClear).ToList();
        Assert.Empty(clears);
        Assert.Empty(navigator.RecordedStops.Snapshot());
        var stopRotations = combat.RecordedRotation.Snapshot()
            .Where(r => r.Method == nameof(FakeCombat.StopRotation))
            .ToList();
        Assert.Empty(stopRotations);
    }
}
