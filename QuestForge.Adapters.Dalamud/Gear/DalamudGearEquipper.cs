using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudGearEquipper : IGearEquipper
{
    public DalamudGearEquipper(PluginServices svc) { }

    public Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    public Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
