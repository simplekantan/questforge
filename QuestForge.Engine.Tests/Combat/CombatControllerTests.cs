using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: CombatController against FakeGameStateProvider + FakeCombat.
/// All tests throw NotImplementedException until Builder implements
/// CombatController.Decide and CombatController.Reset.
///
/// Spec: docs/COMBAT_STEP_PART_A_PLAN.md §G2
/// </summary>
public sealed class CombatControllerTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatTargets(FakeCombat combat)
        => string.Join(", ", combat.RecordedTargets.Snapshot()
            .Select(t => t.IsClear ? "ClearTarget" : $"SetTarget({t.Target!.Value.Value})"));

    private static HostileActor MakeHostile(ulong id, uint dataId, float distance,
        bool isTargetingPlayer = false, bool isDead = false, bool isTargetable = true)
        => new HostileActor(
            new ActorId(id),
            dataId,
            new WorldPosition(0f, 0f, 0f),
            distance,
            isTargetable,
            isDead,
            isTargetingPlayer,
            OnPlayerEnmityList: false,
            HasQuestMarker: false);

    private static CombatStep MakeStep(uint[] killIds, CombatSpawn spawn = CombatSpawn.AutoOnEnterArea)
        => new CombatStep
        {
            Id = "test-combat",
            KillEnemyDataIds = killIds,
            Spawn = spawn
        };

    // =========================================================================
    // CC-sets-target — first Decide issues SetTarget for the selected actor
    // =========================================================================

    [Fact]
    public async Task CC_SetsTarget_OnFirstDecide_RecordsSetTarget()
    {
        /*
         * RED: CombatController.Decide throws NotImplementedException.
         *
         * Given FakeGameStateProvider with one hostile ActorId 9 / DataId 100,
         *       FakeCombat, step.KillEnemyDataIds={100}.
         * When Decide called once,
         * Then returns CombatDecision with Target.Id == ActorId(9), RotationShouldRun:true,
         *      AND FakeCombat.RecordedTargets has exactly one SetTarget(9).
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var controller = new CombatController(gameState, combat);

        gameState.AddHostileActor(MakeHostile(9, 100, distance: 5));
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);

        Assert.NotNull(decision.Target);
        Assert.Equal(new ActorId(9), decision.Target!.Value.Id);
        Assert.Equal(100u, decision.Target!.Value.DataId);
        Assert.True(decision.RotationShouldRun,
            "RotationShouldRun should be true when a target is selected");
        var targets = combat.RecordedTargets.Snapshot();
        Assert.Equal(1, targets.Count);
        Assert.False(targets[0].IsClear, $"Expected SetTarget, got: {FormatTargets(combat)}");
        Assert.Equal(new ActorId(9), targets[0].Target!.Value);
    }

    // =========================================================================
    // CC-no-redundant-settarget — second Decide with same world issues only one SetTarget
    // =========================================================================

    [Fact]
    public async Task CC_NoRedundantSetTarget_SameWorldTwiceTicks_OnlyOneSetTarget()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given the same fake, call Decide TWICE with the world unchanged.
         * Then FakeCombat.RecordedTargets contains exactly ONE SetTarget
         * (no redundant re-issue for an unchanged current target).
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var controller = new CombatController(gameState, combat);

        gameState.AddHostileActor(MakeHostile(9, 100, distance: 5));
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None);
        await controller.Decide(step, CancellationToken.None);

        var targets = combat.RecordedTargets.Snapshot();
        Assert.Equal(1, targets.Count);
    }

    // =========================================================================
    // CC-retarget-on-death — when current target dies, controller picks next
    // =========================================================================

    [Fact]
    public async Task CC_RetargetOnDeath_CurrentTargetDies_SetsNewTarget()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given hostile X selected on tick 1;
         * Before tick 2, mark X IsDead:true and add Y (eligible).
         * When Decide runs tick 2,
         * Then it SetTarget(Y) and returns Y.
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var controller = new CombatController(gameState, combat);

        // Tick 1: X is alive
        var x = MakeHostile(1, 100, distance: 5);
        gameState.AddHostileActor(x);
        var step = MakeStep([100u]);
        var decision1 = await controller.Decide(step, CancellationToken.None);
        Assert.Equal(new ActorId(1), decision1.Target?.Id);

        // Mark X dead, add Y
        gameState.ClearHostileActors();
        var xDead = MakeHostile(1, 100, distance: 5, isDead: true, isTargetable: false);
        var y = MakeHostile(2, 100, distance: 8);
        gameState.AddHostileActor(xDead);
        gameState.AddHostileActor(y);

        // Tick 2: expect retarget to Y
        var decision2 = await controller.Decide(step, CancellationToken.None);

        Assert.NotNull(decision2.Target);
        Assert.Equal(new ActorId(2), decision2.Target!.Value.Id);

        // Two SetTarget calls in total: first X, then Y
        var targets = combat.RecordedTargets.Snapshot()
            .Where(t => !t.IsClear).ToList();
        Assert.Equal(2, targets.Count);
        Assert.Equal(new ActorId(2), targets[1].Target!.Value);
    }

    // =========================================================================
    // CC-clears-when-empty — when no hostiles remain, ClearTarget is called
    // =========================================================================

    [Fact]
    public async Task CC_ClearsWhenEmpty_NoHostilesOnSecondTick_ClearTargetCalled()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given X selected; before tick 2 the fake clears all hostiles.
         * When Decide runs,
         * Then returns CombatDecision(Target:null, RotationShouldRun:false)
         *      AND FakeCombat.RecordedTargets shows a ClearTarget.
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var controller = new CombatController(gameState, combat);

        gameState.AddHostileActor(MakeHostile(1, 100, distance: 5));
        var step = MakeStep([100u]);
        await controller.Decide(step, CancellationToken.None); // tick 1 — sets target

        gameState.ClearHostileActors(); // all gone

        var decision = await controller.Decide(step, CancellationToken.None); // tick 2

        Assert.Null(decision.Target);
        Assert.False(decision.RotationShouldRun,
            "RotationShouldRun should be false when there is nothing to attack");

        var clears = combat.RecordedTargets.Snapshot().Where(t => t.IsClear).ToList();
        Assert.Equal(1, clears.Count);
    }

    // =========================================================================
    // CC-fail-safe-on-read-failure — adapter failure → idle, no exception
    // =========================================================================

    [Fact]
    public async Task CC_FailSafeOnReadFailure_GetHostileActorsFails_NoExceptionIdleDecision()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given FakeGameStateProvider scripted to fail GetHostileActors.
         * When Decide runs,
         * Then no exception; returns CombatDecision(null, RotationShouldRun:false).
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var controller = new CombatController(gameState, combat);

        gameState.SetHostileActorsFailure("simulated-failure", "unit test");
        var step = MakeStep([100u]);

        var decision = await controller.Decide(step, CancellationToken.None);

        Assert.Null(decision.Target);
        Assert.False(decision.RotationShouldRun);
    }

    // =========================================================================
    // CC-reset — after Reset, next Decide re-issues SetTarget even for same target
    // =========================================================================

    [Fact]
    public async Task CC_Reset_AfterReset_FreshSetTargetIssued()
    {
        /*
         * RED: throws NotImplementedException (both Decide and Reset).
         *
         * After selecting X, call Reset(), then Decide again with the same world.
         * Then a fresh SetTarget(X) is issued (state was cleared).
         * Two SetTarget calls total — first on tick 1, second after reset.
         */
        var gameState = new FakeGameStateProvider();
        var combat = new FakeCombat();
        var controller = new CombatController(gameState, combat);

        gameState.AddHostileActor(MakeHostile(9, 100, distance: 5));
        var step = MakeStep([100u]);

        await controller.Decide(step, CancellationToken.None); // tick 1: SetTarget(9)
        Assert.Equal(1, combat.RecordedTargets.Count);

        controller.Reset();

        await controller.Decide(step, CancellationToken.None); // tick 2 post-reset: SetTarget(9) again
        Assert.Equal(2, combat.RecordedTargets.Count);
    }
}
