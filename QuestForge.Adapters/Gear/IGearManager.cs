using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IGearManager
{
    // Inspection
    Task<Result<IReadOnlyList<EquippedItem>>> GetEquippedGear(CancellationToken ct);
    Task<Result<IReadOnlyList<GearItem>>> GetAvailableGear(JobId job, CancellationToken ct);
    Task<Result<int>> GetAverageItemLevel(CancellationToken ct);
    Task<Result<bool>> IsItemEquipped(ItemId item, EquipSlot? slot, CancellationToken ct);
    Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct);

    // Equipping
    Task<Result<EquipOutcome>> EquipItem(ItemId item, EquipSlot slot, CancellationToken ct);
    Task<Result<EquipOutcome>> EquipRecommendedGear(CancellationToken ct);
    Task<Result<EquipOutcome>> EquipBestGearViaStylist(CancellationToken ct);
    Task<Result<bool>> IsStylistAvailable(CancellationToken ct);

    // Gearsets and job
    Task<Result<Unit>> ApplyGearset(int gearsetId, CancellationToken ct);
    Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct);

    // Condition and repair
    Task<Result<IReadOnlyList<EquippedItemCondition>>> GetEquippedGearCondition(CancellationToken ct);
    Task<Result<int>> GetLowestEquippedCondition(CancellationToken ct);
    Task<Result<RepairOutcome>> RepairAtNpc(NpcId npc, CancellationToken ct);
    Task<Result<RepairOutcome>> RepairWithSelf(CancellationToken ct);
    Task<Result<bool>> CanRepairWithSelf(CancellationToken ct);
}

public record EquippedItem(EquipSlot Slot, ItemId Item, int ItemLevel);
public record EquippedItemCondition(EquipSlot Slot, ItemId Item, int ConditionPercent);
public record GearItem(ItemId Item, int ItemLevel, IReadOnlyList<JobId> UsableBy, bool InInventory, bool InArmoury);

public enum EquipSlot
{
    MainHand, OffHand, Head, Body, Hands, Legs, Feet,
    Earrings, Necklace, Bracelets, RingRight, RingLeft,
    Soul
}

public enum EquipOutcome { Equipped, NoChange, InCombat, InInstance, ItemNotFound, Failed }
public enum JobChangeOutcome { Changed, GearsetNotFound, JobNotUnlocked, InCombat, InInstance, Failed }
public enum RepairOutcome { Repaired, NoNpcInRange, InsufficientGil, NothingToRepair, InCombat, InInstance, MissingDarkMatter, Failed }

public enum GearSelectionMethod
{
    GameRecommended,
    StylistIfAvailable
}

public enum RepairPreference
{
    SelfThenNpc,
    NpcOnly,
    SelfOnly,
    AskUser
}