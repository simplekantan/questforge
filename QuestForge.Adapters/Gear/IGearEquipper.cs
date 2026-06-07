using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IGearEquipper
{
    Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct);
    Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct);
}

public enum EquipOutcome { Equipped, NoChange, Pending, InCombat, InInstance, ItemNotFound, Failed }
