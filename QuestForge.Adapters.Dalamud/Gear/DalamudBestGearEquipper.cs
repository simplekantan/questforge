using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudBestGearEquipper : IBestGearEquipper
{
    public DalamudBestGearEquipper(PluginServices svc) { }

    public Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
