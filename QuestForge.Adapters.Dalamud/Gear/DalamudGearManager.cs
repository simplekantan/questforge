using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

// Phase 6 placeholder — quest 66130 never checks or changes gear.
// Stylist IPC and chat-command integration deferred to Phase 9.
public sealed class DalamudGearManager : IGearManager
{
    public DalamudGearManager(PluginServices svc) { }

    public Task<Result<IReadOnlyList<EquippedItem>>> GetEquippedGear(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<EquippedItem>>>(
            Result.Ok<IReadOnlyList<EquippedItem>>(Array.Empty<EquippedItem>()));

    public Task<Result<IReadOnlyList<GearItem>>> GetAvailableGear(JobId job, CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<GearItem>>>(
            Result.Ok<IReadOnlyList<GearItem>>(Array.Empty<GearItem>()));

    public Task<Result<int>> GetAverageItemLevel(CancellationToken ct)
        => Task.FromResult<Result<int>>(Result.Ok(0));

    public Task<Result<bool>> IsItemEquipped(ItemId item, EquipSlot? slot, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    public Task<Result<EquipOutcome>> EquipItem(ItemId item, EquipSlot slot, CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    public Task<Result<EquipOutcome>> EquipRecommendedGear(CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    public Task<Result<EquipOutcome>> EquipBestGearViaStylist(CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    public Task<Result<Unit>> ApplyGearset(int gearsetId, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Failed));

    public Task<Result<IReadOnlyList<EquippedItemCondition>>> GetEquippedGearCondition(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<EquippedItemCondition>>>(
            Result.Ok<IReadOnlyList<EquippedItemCondition>>(Array.Empty<EquippedItemCondition>()));

    // 100 = full condition — engine will not attempt repairs unnecessarily
    public Task<Result<int>> GetLowestEquippedCondition(CancellationToken ct)
        => Task.FromResult<Result<int>>(Result.Ok(100));

    public Task<Result<RepairOutcome>> RepairAtNpc(NpcId npc, CancellationToken ct)
        => Task.FromResult<Result<RepairOutcome>>(Result.Ok(RepairOutcome.Failed));

    public Task<Result<RepairOutcome>> RepairWithSelf(CancellationToken ct)
        => Task.FromResult<Result<RepairOutcome>>(Result.Ok(RepairOutcome.Failed));

    public Task<Result<bool>> CanRepairWithSelf(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
