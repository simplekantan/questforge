using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudJobChanger : IJobChanger
{
    public DalamudJobChanger(PluginServices svc) { }

    public unsafe Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
    {
        var gsm = RaptureGearsetModule.Instance();
        if (gsm == null)
            return Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Failed));

        var ps = PlayerState.Instance();
        if (ps != null && ps->CurrentClassJobId == job.Value)
            return Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Changed));

        for (var i = 0; i < 100; i++)
        {
            if (!gsm->IsValidGearset(i)) continue;
            var entry = gsm->GetGearset(i);
            if (entry == null) continue;
            if (entry->ClassJob != job.Value) continue;
            gsm->EquipGearset(i);
            return Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Changed));
        }

        return Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.GearsetNotFound));
    }

    public unsafe Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
    {
        var gsm = RaptureGearsetModule.Instance();
        if (gsm == null)
            return Task.FromResult<Result<bool>>(Result.Ok(false));

        for (var i = 0; i < 100; i++)
        {
            if (!gsm->IsValidGearset(i)) continue;
            var entry = gsm->GetGearset(i);
            if (entry == null) continue;
            if (entry->ClassJob == job.Value)
                return Task.FromResult<Result<bool>>(Result.Ok(true));
        }

        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }
}
