using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Scheduling;

public interface IQuestScheduler
{
    // Returns the QuestId the engine should run next, or null if no quest should run.
    // null means: "either everything in scope is complete, or Tier-0 is blocked."
    // Callers MUST consult CurrentStatus afterwards to distinguish Idle from AwaitingUser.
    Task<Result<QuestId?>> NextQuestToRun(CancellationToken ct);

    // The most recent status emitted by the scheduler.
    // Updated synchronously inside NextQuestToRun. Never null after the first call.
    SchedulerStatus CurrentStatus { get; }

    // Replace the in-effect scheduler options. Affects the NEXT call to NextQuestToRun.
    // Does not retroactively change CurrentStatus.
    void UpdateOptions(SchedulerOptions options);
}
