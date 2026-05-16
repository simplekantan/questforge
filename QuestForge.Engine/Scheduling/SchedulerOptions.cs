using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Scheduling;

public sealed record SchedulerOptions(
    // Tier-0 manual chain. If non-empty, the scheduler tries this list FIRST in order.
    // Completed entries are skipped. Blockers in this list stop ALL automation.
    IReadOnlyList<QuestId> ManualChain,

    // Tier-4 gate. Default false. When false, DoH/DoL quests are never selected.
    bool EnableCraftGatherQuests,

    // Tier-5 gate. Default false. When false, side quests are never selected.
    bool EnableSideQuests
)
{
    public static SchedulerOptions Default { get; } = new(
        ManualChain: [],
        EnableCraftGatherQuests: false,
        EnableSideQuests: false);
}
