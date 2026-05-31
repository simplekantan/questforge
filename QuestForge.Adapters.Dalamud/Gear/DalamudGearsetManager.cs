using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudGearsetManager : IGearsetManager
{
    public DalamudGearsetManager(PluginServices svc) { }

    public unsafe Task<Result<RegisterOutcome>> RegisterGearset(CancellationToken ct)
    {
        var gsm = RaptureGearsetModule.Instance();
        if (gsm == null)
            return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Failed));

        var ps = PlayerState.Instance();
        if (ps == null)
            return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Failed));

        var currentJobId = ps->CurrentClassJobId;

        for (var i = 0; i < 100; i++)
        {
            if (!gsm->IsValidGearset(i)) continue;
            var entry = gsm->GetGearset(i);
            if (entry == null) continue;
            if (entry->ClassJob == currentJobId)
            {
                gsm->UpdateGearset(i);
                return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Updated));
            }
        }

        var newId = gsm->CreateGearset();
        if (newId == 255)
            return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.MaxGearsetsReached));

        return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Registered));
    }
}
