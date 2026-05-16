using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Adapters.Dalamud.Scheduling;

public sealed class LuminaQuestDataProvider : IQuestDataProvider
{
    // JournalCategory.RowId values — verify with /qf debug quest <id> in-game.
    // Use GetLuminaDebugInfo() to print the actual IDs for any quest in the corpus.
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
                // Known MSQ category → Tier 3; anything else with a genre → Tier 3 too.
                // Seasonal events, dailies, etc. are excluded by corpus membership only —
                // if no quest file exists for them, the scheduler never sees them.
                // This avoids silently dropping corpus quests whose JournalCategory ID
                // we haven't confirmed yet (e.g. ARR prologue / starting area quests).
                _ = genre.JournalCategory.RowId; // read to surface data errors early
                tier = 3;
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

    // Returns a one-line summary of the raw Lumina fields used for tier determination.
    // Call via /qf debug quest <id> to verify JournalCategory IDs in-game.
    public string GetLuminaDebugInfo(QuestId quest, IDataManager dataManager)
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
            var journalCategoryId = 0u;
            if (genreRowId != 0)
                journalCategoryId = genreSheet.GetRow(genreRowId).JournalCategory.RowId;

            return $"quest {quest.Value}: " +
                   $"classJobCat={entry.ClassJobCategoryId} " +
                   $"level={entry.RequiredLevel} " +
                   $"genre={genreRowId} " +
                   $"journalCat={journalCategoryId} " +
                   $"tier={entry.Tier?.ToString() ?? "null"} " +
                   $"prereqs=[{string.Join(",", entry.Prerequisites.Select(p => p.Value))}]";
        }
        catch (Exception ex)
        {
            return $"quest {quest.Value}: error — {ex.Message}";
        }
    }
}
