using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Scheduling;

public sealed record SchedulerOptions(
    // Tier-0 manual chain. If non-empty, the scheduler tries this list FIRST in order.
    // Completed entries are skipped. Blockers in this list stop ALL automation.
    IReadOnlyList<QuestId> ManualChain,

    // Reserved for future DoH/DoL Tier-1 opt-out. Not currently used by the scheduler.
    bool EnableCraftGatherQuests,

    // Tier-5 gate. Default false. When false, side quests are never selected.
    bool EnableSideQuests,

    // Tier-4 gate. Default false. When false, blue (feature unlock) quests are never selected.
    // "blue-urgent" quests are NOT gated by this — they run at Tier 1 unconditionally.
    bool EnableBlueQuests,

    // Which Grand Company the player wants to join at the MSQ branch point.
    // UserDecides halts automation (AwaitingUser). Random picks one per evaluation.
    GrandCompanyPreference GrandCompanyChoice = GrandCompanyPreference.UserDecides
)
{
    public static SchedulerOptions Default { get; } = new(
        ManualChain: [],
        EnableCraftGatherQuests: false,
        EnableSideQuests: false,
        EnableBlueQuests: false);
}
