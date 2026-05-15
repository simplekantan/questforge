using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

// Phase 6 placeholder — quest 66130 never checks or changes gear.
// Stylist IPC and chat command integration deferred to Phase 9.
public sealed class DalamudGearManager : IGearManager
{
    public DalamudGearManager(PluginServices svc) { }

    // IGearManager — Inspection

    // GetEquippedGear — inventory reads deferred to Phase 9
    public Task<Result<IReadOnlyList<EquippedItem>>> GetEquippedGear(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<EquippedItem>>>(
            Result.Ok<IReadOnlyList<EquippedItem>>(Array.Empty<EquippedItem>()));

    // GetAvailableGear — armoury reads deferred to Phase 9
    public Task<Result<IReadOnlyList<GearItem>>> GetAvailableGear(JobId job, CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<GearItem>>>(
            Result.Ok<IReadOnlyList<GearItem>>(Array.Empty<GearItem>()));

    // GetAverageItemLevel — ilvl calculation deferred to Phase 9
    public Task<Result<int>> GetAverageItemLevel(CancellationToken ct)
        => Task.FromResult<Result<int>>(Result.Ok(0));

    // IsItemEquipped — inventory reads deferred to Phase 9
    public Task<Result<bool>> IsItemEquipped(ItemId item, EquipSlot? slot, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    // GearsetExistsForJob — gearset reads deferred to Phase 9
    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    // IGearManager — Equipping

    // EquipItem — equip logic deferred to Phase 9
    public Task<Result<EquipOutcome>> EquipItem(ItemId item, EquipSlot slot, CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    // EquipRecommendedGear — chat command integration deferred to Phase 9
    public Task<Result<EquipOutcome>> EquipRecommendedGear(CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    // EquipBestGearViaStylist — Stylist IPC deferred to Phase 9
    public Task<Result<EquipOutcome>> EquipBestGearViaStylist(CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    // IsStylistAvailable — Stylist IPC deferred to Phase 9
    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    // IGearManager — Gearsets and job

    // ApplyGearset — gearset command integration deferred to Phase 9
    public Task<Result<Unit>> ApplyGearset(int gearsetId, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    // ChangeToJob — job change logic deferred to Phase 9
    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Failed));

    // IGearManager — Condition and repair

    // GetEquippedGearCondition — condition reads deferred to Phase 9
    public Task<Result<IReadOnlyList<EquippedItemCondition>>> GetEquippedGearCondition(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<EquippedItemCondition>>>(
            Result.Ok<IReadOnlyList<EquippedItemCondition>>(Array.Empty<EquippedItemCondition>()));

    // GetLowestEquippedCondition — returns 100 (safe default: full condition)
    public Task<Result<int>> GetLowestEquippedCondition(CancellationToken ct)
        => Task.FromResult<Result<int>>(Result.Ok(100));

    // RepairAtNpc — NPC repair deferred to Phase 9
    public Task<Result<RepairOutcome>> RepairAtNpc(NpcId npc, CancellationToken ct)
        => Task.FromResult<Result<RepairOutcome>>(Result.Ok(RepairOutcome.Failed));

    // RepairWithSelf — self-repair deferred to Phase 9
    public Task<Result<RepairOutcome>> RepairWithSelf(CancellationToken ct)
        => Task.FromResult<Result<RepairOutcome>>(Result.Ok(RepairOutcome.Failed));

    // CanRepairWithSelf — dark matter check deferred to Phase 9
    public Task<Result<bool>> CanRepairWithSelf(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
