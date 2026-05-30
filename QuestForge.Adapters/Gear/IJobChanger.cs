using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IJobChanger
{
    Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct);
    Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct);
}

public enum JobChangeOutcome { Changed, GearsetNotFound, JobNotUnlocked, InCombat, InInstance, Failed }
