using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Scheduling;

public abstract record SchedulerStatus
{
    // The engine is currently running this quest (set by EngineHost — scheduler emits
    // this when NextQuestToRun returns a non-null QuestId).
    public sealed record Running(QuestId CurrentQuest) : SchedulerStatus;

    // The scheduler is mid-evaluation. Transient; only observable in concurrent reads.
    public sealed record SelectingNext : SchedulerStatus;

    // Tier-0 (manual chain) has hit a blocker. Automation stops entirely.
    public sealed record AwaitingUser(QuestId BlockedQuest, QuestUnlockReason Reason)
        : SchedulerStatus;

    // No quest meets the criteria of any active tier.
    public sealed record Idle : SchedulerStatus;

    // The user has paused automation. Set externally (not by NextQuestToRun).
    public sealed record Paused : SchedulerStatus;
}
