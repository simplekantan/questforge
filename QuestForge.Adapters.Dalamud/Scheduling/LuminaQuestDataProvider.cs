using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Adapters.Dalamud.Scheduling;

public sealed class LuminaQuestDataProvider : IQuestDataProvider
{
    private sealed record Entry(
        int? Tier,
        uint ClassJobCategoryId,
        int RequiredLevel,
        IReadOnlyList<QuestId> Prerequisites,
        PrerequisiteJoin PrereqJoin,
        int SortKey,
        string Category,
        string? SkipIf);

    private readonly ExcelSheet<Quest> _questSheet;
    private readonly ExcelSheet<ClassJobCategory> _classJobCatSheet;
    private readonly Dictionary<QuestId, Entry> _entries = new();

    /// <param name="questMetadata">QuestId → (Category, SkipIf) from quest files. Built by QuestFileIndex.</param>
    public LuminaQuestDataProvider(IDataManager dataManager, IReadOnlyDictionary<QuestId, (string Category, string? SkipIf)> questMetadata)
    {
        _questSheet = dataManager.GetExcelSheet<Quest>()
            ?? throw new InvalidOperationException("Lumina Quest sheet unavailable");
        _classJobCatSheet = dataManager.GetExcelSheet<ClassJobCategory>()
            ?? throw new InvalidOperationException("Lumina ClassJobCategory sheet unavailable");

        LoadEntries(questMetadata);
    }

    public void ReloadMetadata(IReadOnlyDictionary<QuestId, (string Category, string? SkipIf)> questMetadata)
    {
        _entries.Clear();
        LoadEntries(questMetadata);
    }

    private void LoadEntries(IReadOnlyDictionary<QuestId, (string Category, string? SkipIf)> questMetadata)
    {
        foreach (var (questId, meta) in questMetadata)
        {
            try
            {
                var row = _questSheet.GetRow(questId.Value);
                _entries[questId] = BuildEntry(questId, meta.Category, meta.SkipIf, row);
            }
            catch { /* invalid/missing Lumina row — skip */ }
        }
    }

    public string? GetSkipIf(QuestId quest)
        => _entries.TryGetValue(quest, out var e) ? e.SkipIf : null;

    // Maps QuestDefinition.Category to a scheduler tier.
    // "msq"                    → 3  (auto chain, always runs)
    // "class" | "job" | "role" → 1  (active-job class quests)
    // "blue-urgent"            → 1  (critical feature unlocks, no job filter)
    // "blue"                   → 4  (opt-in feature unlocks, EnableBlueQuests gate)
    // "side"                   → 5  (opt-in, off by default)
    // unknown                  → null (scheduler ignores; fail-safe against category typos)
    private static int? CategoryToTier(string category) => category switch
    {
        "msq"                      => 3,
        "class" or "job" or "role" => 1,
        "blue-urgent"              => 1,
        "blue"                     => 4,
        "side"                     => 5,
        _                          => null,
    };

    private static Entry BuildEntry(QuestId questId, string category, string? skipIf, Quest row)
    {
        var classJobCatId = row.ClassJobCategory0.RowId;

        var prereqs = new List<QuestId>();
        foreach (var p in row.PreviousQuest)
            if (p.RowId != 0) prereqs.Add(new QuestId(p.RowId));

        return new Entry(
            Tier: CategoryToTier(category),
            ClassJobCategoryId: classJobCatId,
            RequiredLevel: row.ClassJobLevel[0],
            Prerequisites: prereqs,
            PrereqJoin: row.PreviousQuestJoin == 0 ? PrerequisiteJoin.All : PrerequisiteJoin.AtLeastOne,
            SortKey: (int)questId.Value,
            Category: category,
            SkipIf: skipIf);
    }

    public uint GetClassJobCategoryId(QuestId quest)
        => _entries.TryGetValue(quest, out var e) ? e.ClassJobCategoryId : 0;

    public int GetRequiredLevel(QuestId quest)
        => _entries.TryGetValue(quest, out var e) ? e.RequiredLevel : 0;

    public int GetJournalSortKey(QuestId quest)
        => _entries.TryGetValue(quest, out var e) ? e.SortKey : int.MaxValue;

    public IReadOnlyList<QuestId> GetPrerequisites(QuestId quest)
        => _entries.TryGetValue(quest, out var e) ? e.Prerequisites : Array.Empty<QuestId>();

    public PrerequisiteJoin GetPrerequisiteJoin(QuestId quest)
        => _entries.TryGetValue(quest, out var e) ? e.PrereqJoin : PrerequisiteJoin.All;

    public int? GetQuestTier(QuestId quest)
        => _entries.TryGetValue(quest, out var e) ? e.Tier : null;

    public bool IsClassQuestForJob(QuestId quest, JobId job)
    {
        if (!_entries.TryGetValue(quest, out var e) || e.ClassJobCategoryId == 0) return false;
        var cat = _classJobCatSheet.GetRow(e.ClassJobCategoryId);
        return JobCategoryHelper.IsJobInCategory(cat, job.Value);
    }

    public IReadOnlyCollection<QuestId> EnumerateKnownQuests()
        => _entries.Keys.ToArray();

    // Prints Lumina fields + derived tier for a quest in the corpus.
    // Use /qf debug quest <id> to call this in-game.
    public string GetDebugInfo(QuestId quest, IDataManager dataManager)
    {
        if (!_entries.TryGetValue(quest, out var entry))
            return $"quest {quest.Value}: not in corpus";

        try
        {
            var questSheet = dataManager.GetExcelSheet<Quest>();
            var genreSheet = dataManager.GetExcelSheet<JournalGenre>();
            if (questSheet is null || genreSheet is null)
                return $"quest {quest.Value}: sheets unavailable";

            var row = questSheet.GetRow(quest.Value);
            var genreRowId = row.JournalGenre.RowId;
            var journalCategoryId = genreRowId != 0
                ? genreSheet.GetRow(genreRowId).JournalCategory.RowId : 0u;

            return $"quest {quest.Value}: " +
                   $"category={entry.Category} " +
                   $"tier={entry.Tier} " +
                   $"classJobCat={entry.ClassJobCategoryId} " +
                   $"level={entry.RequiredLevel} " +
                   $"genre={genreRowId} journalCat={journalCategoryId} " +
                   $"prereqs=[{string.Join(",", entry.Prerequisites.Select(p => p.Value))}]";
        }
        catch (Exception ex)
        {
            return $"quest {quest.Value}: error — {ex.Message}";
        }
    }
}
