using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Adapters.Fakes.Scheduling;

public sealed class FakeQuestDataProvider : IQuestDataProvider
{
    private sealed record QuestEntry(
        int? Tier,
        int SortKey,
        JobId? ClassJob,
        QuestId[] Prerequisites,
        PrerequisiteJoin Join);

    private readonly Dictionary<QuestId, QuestEntry> _quests = new();

    // Builder-style registration. Returns this for chaining.
    public FakeQuestDataProvider WithQuest(
        QuestId id,
        int? tier,
        int sortKey = 0,
        JobId? classJob = null,
        QuestId[]? prereqs = null,
        PrerequisiteJoin join = PrerequisiteJoin.All)
    {
        _quests[id] = new QuestEntry(tier, sortKey, classJob, prereqs ?? [], join);
        return this;
    }

    public uint GetClassJobCategoryId(QuestId quest)
        => 0; // Not needed by scheduler; IsClassQuestForJob is the relevant API.

    public int GetRequiredLevel(QuestId quest)
        => _quests.TryGetValue(quest, out var e) ? 0 : 0; // Always 0; level checks use WhyUnavailable.

    public int GetJournalSortKey(QuestId quest)
        => _quests.TryGetValue(quest, out var e) ? e.SortKey : int.MaxValue;

    public IReadOnlyList<QuestId> GetPrerequisites(QuestId quest)
        => _quests.TryGetValue(quest, out var e) ? e.Prerequisites : Array.Empty<QuestId>();

    public PrerequisiteJoin GetPrerequisiteJoin(QuestId quest)
        => _quests.TryGetValue(quest, out var e) ? e.Join : PrerequisiteJoin.All;

    public int? GetQuestTier(QuestId quest)
        => _quests.TryGetValue(quest, out var e) ? e.Tier : null;

    public bool IsClassQuestForJob(QuestId quest, JobId job)
        => _quests.TryGetValue(quest, out var e) && e.ClassJob == job;

    public IReadOnlyCollection<QuestId> EnumerateKnownQuests()
        => _quests.Keys.ToArray();
}
