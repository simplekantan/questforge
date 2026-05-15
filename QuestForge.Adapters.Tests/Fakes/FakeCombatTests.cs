using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeCombatTests
{
    [Fact]
    public async Task Default_EngageTarget_ReturnsTargetDefeated_IsRecorded()
    {
        var combat = new FakeCombat();
        var result = await combat.EngageTarget(new NpcId(1), CancellationToken.None);
        Assert.Equal(CombatOutcome.TargetDefeated, result.ValueOrThrow);
        Assert.Equal(1, combat.RecordedEngagements.Count);
    }

    [Fact]
    public async Task ScriptNextCombatResult_Fled_ReturnsFlied_Consumed_SecondCallReturnsDefault()
    {
        var combat = new FakeCombat();
        // Note: CombatOutcome does not have Fled — using Disengaged as a non-default value
        combat.ScriptNextCombatResult(CombatOutcome.Disengaged);

        var first = await combat.EngageTarget(new NpcId(1), CancellationToken.None);
        Assert.Equal(CombatOutcome.Disengaged, first.ValueOrThrow);

        var second = await combat.EngageTarget(new NpcId(1), CancellationToken.None);
        Assert.Equal(CombatOutcome.TargetDefeated, second.ValueOrThrow);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceledException_NotRecorded()
    {
        var combat = new FakeCombat();
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => combat.EngageTarget(new NpcId(1), ct));
        Assert.Equal(0, combat.RecordedEngagements.Count);
    }
}