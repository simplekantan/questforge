using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Scheduling;

public interface IQuestDataProvider
{
    // Lumina ClassJobCategory rowId for the quest, or 0 when the quest has no job restriction.
    // Returns 0 for unknown quest IDs (treat unknown as "no restriction").
    uint GetClassJobCategoryId(QuestId quest);

    // Minimum level required for the quest. Returns 0 for unknown quest IDs.
    int GetRequiredLevel(QuestId quest);

    // Lumina JournalGenre.SortKey for the quest's genre, used to order quests within a tier.
    // Returns int.MaxValue for unknown quest IDs (sorts to the end).
    int GetJournalSortKey(QuestId quest);

    // Up to 3 prerequisite QuestIds defined in Lumina Quest.PreviousQuest[0..2].
    // Zero-row entries are filtered out. Returns an empty list for unknown quest IDs.
    IReadOnlyList<QuestId> GetPrerequisites(QuestId quest);

    // Lumina Quest.PreviousQuestJoin: how prerequisites combine.
    // Returns PrerequisiteJoin.All for unknown quest IDs (safer default).
    PrerequisiteJoin GetPrerequisiteJoin(QuestId quest);

    // Maps a quest to a scheduler tier (1, 3, 4, or 5) based on its category and content type.
    // Tier 0 is user-assigned (not data-driven); Tier 2 is computed from blockers.
    // Returns null for quests that are not categorized — the scheduler MUST ignore null-tier quests.
    int? GetQuestTier(QuestId quest);

    // True when the quest's ClassJobCategory matches the given job.
    // Returns false for unknown quest IDs.
    bool IsClassQuestForJob(QuestId quest, JobId job);

    // Quest-level skipIf predicate from the quest definition file.
    // Returns null for unknown quest IDs or quests without a skipIf field.
    string? GetSkipIf(QuestId quest);

    // All known quest IDs the data provider can answer about.
    // Used by the scheduler to enumerate candidates for each tier.
    IReadOnlyCollection<QuestId> EnumerateKnownQuests();
}
