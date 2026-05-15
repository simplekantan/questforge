using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Gear;

public sealed class FakeGearManager : IGearManager
{
    private EquipOutcome _equipOutcome = EquipOutcome.NoChange;
    private RepairOutcome _repairOutcome = RepairOutcome.Repaired;
    private int _lowestCondition = 100;

    public record EquipCall(ItemId? Item, EquipSlot? Slot, DateTimeOffset At) : AdapterCall(At);
    public record RepairCall(NpcId? Npc, bool WithSelf, DateTimeOffset At) : AdapterCall(At);

    public CallLog<EquipCall> RecordedEquips { get; } = new();
    public CallLog<RepairCall> RecordedRepairs { get; } = new();

    // ----- Scripting -----
    public void ScriptEquipResult(EquipOutcome outcome) => _equipOutcome = outcome;
    public void ScriptRepairResult(RepairOutcome outcome) => _repairOutcome = outcome;
    public void SetLowestCondition(int condition) => _lowestCondition = condition;

    // ----- Reset -----
    public void Reset()
    {
        RecordedEquips.Clear();
        RecordedRepairs.Clear();
        _equipOutcome = EquipOutcome.NoChange;
        _repairOutcome = RepairOutcome.Repaired;
        _lowestCondition = 100;
    }

    // ----- IGearManager implementation -----

    public Task<Result<IReadOnlyList<EquippedItem>>> GetEquippedGear(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<EquippedItem> items = Array.Empty<EquippedItem>();
        return Task.FromResult<Result<IReadOnlyList<EquippedItem>>>(Result.Ok(items));
    }

    public Task<Result<IReadOnlyList<GearItem>>> GetAvailableGear(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<GearItem> items = Array.Empty<GearItem>();
        return Task.FromResult<Result<IReadOnlyList<GearItem>>>(Result.Ok(items));
    }

    public Task<Result<int>> GetAverageItemLevel(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<int>>(Result.Ok(0));
    }

    public Task<Result<bool>> IsItemEquipped(ItemId item, EquipSlot? slot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    public Task<Result<EquipOutcome>> EquipItem(ItemId item, EquipSlot slot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedEquips.Add(new EquipCall(item, slot, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(_equipOutcome));
    }

    public Task<Result<EquipOutcome>> EquipRecommendedGear(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedEquips.Add(new EquipCall(null, null, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(_equipOutcome));
    }

    public Task<Result<EquipOutcome>> EquipBestGearViaStylist(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedEquips.Add(new EquipCall(null, null, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(_equipOutcome));
    }

    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    public Task<Result<Unit>> ApplyGearset(int gearsetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Changed));
    }

    public Task<Result<IReadOnlyList<EquippedItemCondition>>> GetEquippedGearCondition(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<EquippedItemCondition> conditions = Array.Empty<EquippedItemCondition>();
        return Task.FromResult<Result<IReadOnlyList<EquippedItemCondition>>>(Result.Ok(conditions));
    }

    public Task<Result<int>> GetLowestEquippedCondition(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<int>>(Result.Ok(_lowestCondition));
    }

    public Task<Result<RepairOutcome>> RepairAtNpc(NpcId npc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedRepairs.Add(new RepairCall(npc, false, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<RepairOutcome>>(Result.Ok(_repairOutcome));
    }

    public Task<Result<RepairOutcome>> RepairWithSelf(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedRepairs.Add(new RepairCall(null, true, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<RepairOutcome>>(Result.Ok(_repairOutcome));
    }

    public Task<Result<bool>> CanRepairWithSelf(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }
}