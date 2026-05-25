using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using Xunit;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// FK-* tests from G5 — FakeCombat and FakeGameStateProvider contract tests.
/// All tests pass immediately (these are fake implementations, not stubs).
///
/// Spec: docs/COMBAT_STEP_PART_A_PLAN.md §G5
/// </summary>
public sealed class CombatFakeContractTests
{
    // =========================================================================
    // FK-settarget-records — SetTarget records into RecordedTargets
    // =========================================================================

    [Fact]
    public async Task FK_SetTargetRecords_SetTarget_AppearsInRecordedTargets()
    {
        /*
         * Given FakeCombat.
         * When SetTarget(ActorId 5),
         * Then RecordedTargets has one entry with ActorId 5, IsClear:false.
         */
        var combat = new FakeCombat();

        await combat.SetTarget(new ActorId(5), CancellationToken.None);

        var targets = combat.RecordedTargets.Snapshot();
        Assert.Equal(1, targets.Count);
        Assert.False(targets[0].IsClear);
        Assert.Equal(new ActorId(5), targets[0].Target!.Value);
    }

    // =========================================================================
    // FK-rotation-availability — SetRotationModuleAvailable controls IsRotationModuleAvailable
    // =========================================================================

    [Fact]
    public async Task FK_RotationAvailability_SetFalse_IsRotationModuleAvailableReturnsFalse()
    {
        /*
         * Given FakeCombat with SetRotationModuleAvailable(false).
         * When IsRotationModuleAvailable,
         * Then Result.Ok(false).
         */
        var combat = new FakeCombat();
        combat.SetRotationModuleAvailable(false);

        var result = await combat.IsRotationModuleAvailable(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task FK_RotationAvailability_DefaultIsTrue()
    {
        /*
         * Default FakeCombat should have IsRotationModuleAvailable returning true.
         */
        var combat = new FakeCombat();
        var result = await combat.IsRotationModuleAvailable(CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    // =========================================================================
    // FK-start-stop-records — StartRotation and StopRotation each record into RecordedRotation
    // =========================================================================

    [Fact]
    public async Task FK_StartStopRecords_StartRotation_AppearsInRecordedRotation()
    {
        var combat = new FakeCombat();

        await combat.StartRotation(CancellationToken.None);

        var log = combat.RecordedRotation.Snapshot();
        Assert.Equal(1, log.Count);
        Assert.Equal("StartRotation", log[0].Method);
    }

    [Fact]
    public async Task FK_StartStopRecords_StopRotation_AppearsInRecordedRotation()
    {
        var combat = new FakeCombat();

        await combat.StopRotation(CancellationToken.None);

        var log = combat.RecordedRotation.Snapshot();
        Assert.Equal(1, log.Count);
        Assert.Equal("StopRotation", log[0].Method);
    }

    [Fact]
    public async Task FK_StartStopRecords_BothCalls_InOrder()
    {
        var combat = new FakeCombat();

        await combat.StartRotation(CancellationToken.None);
        await combat.StopRotation(CancellationToken.None);

        var log = combat.RecordedRotation.Snapshot();
        Assert.Equal(2, log.Count);
        Assert.Equal("StartRotation", log[0].Method);
        Assert.Equal("StopRotation", log[1].Method);
    }

    // =========================================================================
    // FK-hostiles-radius-filter — GetHostileActors filters by DistanceToPlayer <= radius
    // =========================================================================

    [Fact]
    public async Task FK_HostilesRadiusFilter_TwoActors_OnlyNearbyReturned()
    {
        /*
         * Given two hostiles at dist 5 and 50.
         * When GetHostileActors(radius=10),
         * Then only the dist-5 actor is returned.
         * AND RecordedReads contains 'GetHostileActors'.
         */
        var state = new FakeGameStateProvider();

        var near = new HostileActor(new ActorId(1), 100, new WorldPosition(0,0,0),
            DistanceToPlayer: 5, IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false);
        var far = new HostileActor(new ActorId(2), 200, new WorldPosition(0,0,0),
            DistanceToPlayer: 50, IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false);

        state.AddHostileActor(near);
        state.AddHostileActor(far);

        var result = await state.GetHostileActors(10f, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.ValueOrThrow.Count);
        Assert.Equal(new ActorId(1), result.ValueOrThrow[0].Id);

        // RecordedReads must contain GetHostileActors
        var reads = state.RecordedReads.Snapshot();
        Assert.Contains(reads, r => r.Method == "GetHostileActors");
    }

    // =========================================================================
    // FK-hostiles-empty-default — GetHostileActors with none added returns empty list, success
    // =========================================================================

    [Fact]
    public async Task FK_HostilesEmptyDefault_NoActorsAdded_ReturnsEmptyListSuccess()
    {
        /*
         * With no hostiles added, GetHostileActors(100) returns an empty list (not null),
         * and result.IsSuccess is true.
         */
        var state = new FakeGameStateProvider();

        var result = await state.GetHostileActors(100f, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ValueOrThrow);
        Assert.Empty(result.ValueOrThrow);
    }

    // =========================================================================
    // FK-clear-target-records — ClearTarget records into RecordedTargets with IsClear:true
    // =========================================================================

    [Fact]
    public async Task FK_ClearTargetRecords_ClearTarget_AppearsInRecordedTargetsAsClear()
    {
        var combat = new FakeCombat();

        await combat.ClearTarget(CancellationToken.None);

        var targets = combat.RecordedTargets.Snapshot();
        Assert.Equal(1, targets.Count);
        Assert.True(targets[0].IsClear);
        Assert.Null(targets[0].Target);
    }
}
