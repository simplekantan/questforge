using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Duty;

public interface IQuestBattleRunner
{
    /// <summary>
    /// Enable BossMod AI for a Single Player Duty.
    /// Creates a "QuestForge" preset, sets it active, enables quest battles.
    /// </summary>
    Task<Result<bool>> StartDuty(CancellationToken ct);

    /// <summary>
    /// Disable BossMod AI after duty completion or engine stop.
    /// Clears preset, disables quest battles, disables AI.
    /// Idempotent — safe to call when not started.
    /// </summary>
    Task<Result<bool>> StopDuty(CancellationToken ct);

    /// <summary>
    /// Check whether BossMod is installed and its IPC is responsive.
    /// </summary>
    Task<Result<bool>> IsBossModAvailable(CancellationToken ct);
}
