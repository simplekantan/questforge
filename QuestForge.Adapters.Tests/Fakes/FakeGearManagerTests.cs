using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeGearManagerTests
{
    [Fact]
    public async Task Default_EquipItem_ReturnsNoChange_IsRecorded()
    {
        var gear = new FakeGearManager();
        var result = await gear.EquipItem(new ItemId(1), EquipSlot.Body, CancellationToken.None);
        Assert.Equal(EquipOutcome.NoChange, result.ValueOrThrow);
        Assert.Equal(1, gear.RecordedEquips.Count);
    }

    [Fact]
    public async Task Default_RepairAtNpc_ReturnsRepaired_IsRecorded()
    {
        var gear = new FakeGearManager();
        var result = await gear.RepairAtNpc(new NpcId(1), CancellationToken.None);
        Assert.Equal(RepairOutcome.Repaired, result.ValueOrThrow);
        Assert.Equal(1, gear.RecordedRepairs.Count);
    }

    [Fact]
    public async Task Default_GetLowestEquippedCondition_Returns100()
    {
        var gear = new FakeGearManager();
        var result = await gear.GetLowestEquippedCondition(CancellationToken.None);
        Assert.Equal(100, result.ValueOrThrow);
    }

    [Fact]
    public async Task SetLowestCondition_GetLowestEquippedCondition_ReturnsSetValue()
    {
        var gear = new FakeGearManager();
        gear.SetLowestCondition(50);
        var result = await gear.GetLowestEquippedCondition(CancellationToken.None);
        Assert.Equal(50, result.ValueOrThrow);
    }
}