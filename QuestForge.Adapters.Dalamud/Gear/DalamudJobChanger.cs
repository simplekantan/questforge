using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudJobChanger : IJobChanger
{
    public DalamudJobChanger(PluginServices svc) { }

    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Failed));

    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
