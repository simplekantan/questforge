using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Tests for the reshaped FakeCombat (part A).
/// Old EngageTarget / RecordedEngagements tests removed — those types are gone.
/// New tests cover the rotation-module and targeting surfaces.
/// FK-* tests from the GWT are in CombatFakeContractTests.cs.
/// </summary>
public class FakeCombatTests
{
    [Fact]
    public async Task Default_IsRotationModuleAvailable_ReturnsTrue()
    {
        var combat = new FakeCombat();
        var result = await combat.IsRotationModuleAvailable(CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task SetRotationModuleAvailable_False_IsRotationModuleAvailable_ReturnsFalse()
    {
        var combat = new FakeCombat();
        combat.SetRotationModuleAvailable(false);
        var result = await combat.IsRotationModuleAvailable(CancellationToken.None);
        Assert.False(result.ValueOrThrow);
    }

    [Fact]
    public async Task UseAction_ReturnsExecuted()
    {
        var combat = new FakeCombat();
        var result = await combat.UseAction(1u, null, CancellationToken.None);
        Assert.Equal(UseActionOutcome.Executed, result.ValueOrThrow);
    }

    [Fact]
    public async Task UseActionOnObject_ReturnsExecuted()
    {
        var combat = new FakeCombat();
        var result = await combat.UseActionOnObject(1u, new InteractableId(1u), CancellationToken.None);
        Assert.Equal(UseActionOutcome.Executed, result.ValueOrThrow);
    }

    [Fact]
    public async Task IsActionUsable_ReturnsTrue()
    {
        var combat = new FakeCombat();
        var result = await combat.IsActionUsable(1u, CancellationToken.None);
        Assert.True(result.ValueOrThrow);
    }

    [Fact]
    public async Task PreCancelledToken_SetTarget_ThrowsOperationCanceledException()
    {
        var combat = new FakeCombat();
        var ct = new CancellationToken(canceled: true);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => combat.SetTarget(new ActorId(1), ct));
    }
}
