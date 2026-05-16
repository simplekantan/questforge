using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Adapters.Dalamud.Scheduling;

public sealed class LuminaQuestDataProvider : IQuestDataProvider
{
    // JournalCategory.RowId for Main Scenario quests.
    // Verify in-game: /xldata → JournalCategory sheet → row 1 should be "Main Scenario".
    private const uint MainScenarioCategoryId = 1;

    private sealed record Entry(
        int? Tier,
        uint ClassJobCategoryId,
        int RequiredLevel,
        IReadOnlyList<QuestId> Prerequisites,
        PrerequisiteJoin PrereqJoin,
        int SortKey);

    private readonly ExcelSheet<ClassJobCategory> _classJobCatSheet;
    private readonly Dictionary<QuestId, Entry> _entries = new();

    public LuminaQuestDataProvider(IDataManager dataManager, string questsDir)
    {
        var questSheet = dataManager.GetExcelSheet<Quest>()
            ?? throw new InvalidOperationException("Lumina Quest sheet unavailable");
        _classJobCatSheet = dataManager.GetExcelSheet<ClassJobCategory>()
            ?? throw new InvalidOperationException("Lumina ClassJobCategory sheet unavailable");
        var genreSheet = dataManager.GetExcelSheet<JournalGenre>()
            ?? throw new InvalidOperationException("Lumina JournalGenre sheet unavailable");

        if (!Directory.Exists(questsDir)) return;

        foreach (var file in Directory.EnumerateFiles(questsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!uint.TryParse(stem, out var rawId)) continue;
            var questId = new QuestId(rawId);
            try
            {
                var row = questSheet.GetRow(rawId);
                _entries[questId] = BuildEntry(questId, row, genreSheet);
            }
            catch { /* invalid/missing row — skip */ }
        }
    }

    private Entry BuildEntry(QuestId questId, Quest row, ExcelSheet<JournalGenre> genreSheet)
    {
        var classJobCatId = row.ClassJobCategory0.RowId;

        int? tier = null;
        if (classJobCatId != 0)
        {
            var cat = _classJobCatSheet.GetRow(classJobCatId);
            if (!cat.ADV) // skip universal "Adventurer" category
            {
                bool isDohDol = cat.CRP || cat.BSM || cat.ARM || cat.GSM || cat.LTW || cat.WVR || cat.ALC || cat.CUL
                             || cat.MIN || cat.BTN || cat.FSH;
                tier = isDohDol ? 4 : 1;
            }
        }
        else
        {
            var genreRowId = row.JournalGenre.RowId;
            if (genreRowId != 0)
            {
                var genre = genreSheet.GetRow(genreRowId);
                tier = genre.JournalCategory.RowId == MainScenarioCategoryId ? 3 : 5;
            }
        }

        var prereqs = new List<QuestId>();
        foreach (var p in row.PreviousQuest)
            if (p.RowId != 0) prereqs.Add(new QuestId(p.RowId));

        return new Entry(
            Tier: tier,
            ClassJobCategoryId: classJobCatId,
            RequiredLevel: row.ClassJobLevel[0],
            Prerequisites: prereqs,
            PrereqJoin: row.PreviousQuestJoin == 0 ? PrerequisiteJoin.All : PrerequisiteJoin.AtLeastOne,
            SortKey: (int)questId.Value);
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
}
