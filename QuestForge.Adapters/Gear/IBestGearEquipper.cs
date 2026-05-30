using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IBestGearEquipper
{
    Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct);
    Task<Result<bool>> IsStylistAvailable(CancellationToken ct);
}
