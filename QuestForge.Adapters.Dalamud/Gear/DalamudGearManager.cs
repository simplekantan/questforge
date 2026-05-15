using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudGearManager : IGearManager
{
    private readonly PluginServices _svc;

    public DalamudGearManager(PluginServices svc) => _svc = svc;

    // IGearManager — Inspection

    public Task<Result<IReadOnlyList<EquippedItem>>> GetEquippedGear(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<IReadOnlyList<GearItem>>> GetAvailableGear(JobId job, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<int>> GetAverageItemLevel(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsItemEquipped(ItemId item, EquipSlot? slot, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
        => throw new NotImplementedException();

    // IGearManager — Equipping

    public Task<Result<EquipOutcome>> EquipItem(ItemId item, EquipSlot slot, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<EquipOutcome>> EquipRecommendedGear(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<EquipOutcome>> EquipBestGearViaStylist(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
        => throw new NotImplementedException();

    // IGearManager — Gearsets and job

    public Task<Result<Unit>> ApplyGearset(int gearsetId, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
        => throw new NotImplementedException();

    // IGearManager — Condition and repair

    public Task<Result<IReadOnlyList<EquippedItemCondition>>> GetEquippedGearCondition(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<int>> GetLowestEquippedCondition(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<RepairOutcome>> RepairAtNpc(NpcId npc, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<RepairOutcome>> RepairWithSelf(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> CanRepairWithSelf(CancellationToken ct)
        => throw new NotImplementedException();
}