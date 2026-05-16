using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.State;

public sealed class DalamudQuestState : IQuestState
{
    private readonly PluginServices _svc;
    private readonly Lazy<ExcelSheet<Quest>> _questSheet;
    private readonly Lazy<ExcelSheet<ClassJobCategory>> _classJobCategorySheet;

    public DalamudQuestState(PluginServices svc)
    {
        _svc = svc;
        _questSheet = new Lazy<ExcelSheet<Quest>>(() =>
            svc.DataManager.GetExcelSheet<Quest>()
            ?? throw new InvalidOperationException("Lumina Quest sheet unavailable"));
        _classJobCategorySheet = new Lazy<ExcelSheet<ClassJobCategory>>(() =>
            svc.DataManager.GetExcelSheet<ClassJobCategory>()
            ?? throw new InvalidOperationException("Lumina ClassJobCategory sheet unavailable"));
    }

    // Lumina rowId → QuestManager ushort (lower 16 bits); reverse restores 0x10000 prefix
    private static ushort ToInternal(QuestId q) => (ushort)(q.Value & 0xFFFF);
    private static QuestId ToPublic(ushort id) => new((uint)id | 0x10000u);

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var internalId = ToInternal(quest);

            if (QuestManager.IsQuestComplete(internalId))
                return Task.FromResult<Result<QuestStatus>>(Result.Ok(QuestStatus.Complete));

            var qm = QuestManager.Instance();
            if (qm == null)
                return Task.FromResult<Result<QuestStatus>>(
                    Result.Fail<QuestStatus>("questManagerNull", "QuestManager instance unavailable"));

            if (qm->IsQuestAccepted(internalId))
                return Task.FromResult<Result<QuestStatus>>(Result.Ok(QuestStatus.Accepted));

            return Task.FromResult<Result<QuestStatus>>(Result.Ok(QuestStatus.Available));
        }
    }

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            return Task.FromResult<Result<int>>(
                Result.Ok((int)QuestManager.GetQuestSequence(ToInternal(quest))));
        }
    }

    // QuestWork.Flags is a byte bitfield: IsPriority (bit 0), RewardItemArrayIndex (bits 1-2), IsHidden (bit 3)
    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            if (qm == null)
                return Task.FromResult<Result<uint>>(
                    Result.Fail<uint>("questManagerNull", "QuestManager instance unavailable"));

            var q = qm->GetQuestById(ToInternal(quest));
            if (q == null)
                return Task.FromResult<Result<uint>>(Result.Ok(0u));

            return Task.FromResult<Result<uint>>(Result.Ok((uint)q->Flags));
        }
    }

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            if (qm == null)
                return Task.FromResult<Result<bool>>(
                    Result.Fail<bool>("questManagerNull", "QuestManager instance unavailable"));

            var q = qm->GetQuestById(ToInternal(quest));
            if (q == null)
                return Task.FromResult<Result<bool>>(Result.Ok(false));

            var set = (q->Flags & (1 << flagBit)) != 0;
            return Task.FromResult<Result<bool>>(Result.Ok(set));
        }
    }

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            if (qm == null)
                return Task.FromResult<Result<bool>>(
                    Result.Fail<bool>("questManagerNull", "QuestManager instance unavailable"));

            return Task.FromResult<Result<bool>>(Result.Ok(qm->IsQuestAccepted(ToInternal(quest))));
        }
    }

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            return Task.FromResult<Result<bool>>(
                Result.Ok(QuestManager.IsQuestComplete(ToInternal(quest))));
        }
    }

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var internalId = ToInternal(quest);

            // Already done or in-progress → not "available to accept"
            if (QuestManager.IsQuestComplete(internalId)) return Task.FromResult<Result<bool>>(Result.Ok(false));
            var qm = QuestManager.Instance();
            if (qm != null && qm->IsQuestAccepted(internalId)) return Task.FromResult<Result<bool>>(Result.Ok(false));

            // Delegate to WhyUnavailable — if it returns null the quest is available
            var reason = WhyUnavailable(quest, ct).GetAwaiter().GetResult();
            if (reason is Result<QuestUnlockReason?>.Failure f)
                return Task.FromResult<Result<bool>>(Result.Fail<bool>(f.Reason, f.Detail));

            return Task.FromResult<Result<bool>>(Result.Ok(reason.ValueOrThrow is null));
        }
    }

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var internalId = ToInternal(quest);

            if (QuestManager.IsQuestComplete(internalId))
                return Task.FromResult<Result<QuestUnlockReason?>>(Result.Ok<QuestUnlockReason?>(
                    new QuestUnlockReason(false, 0, false, [], false, null, AlreadyCompleted: true, false, null)));

            var row = _questSheet.Value.GetRow(quest.Value);

            var qm = QuestManager.Instance();
            if (qm != null && qm->IsQuestAccepted(internalId))
                // Already accepted — not "unavailable", just not re-acceptable
                return Task.FromResult<Result<QuestUnlockReason?>>(Result.Ok<QuestUnlockReason?>(null));

            var player = _svc.ObjectTable.LocalPlayer as IPlayerCharacter;
            var currentJobRowId = player is not null ? (uint)player.ClassJob.RowId : 0u;
            var currentLevel = player?.Level ?? 0;

            // Job / class restriction via ClassJobCategory
            var categoryRowId = row.ClassJobCategory0.RowId;
            bool wrongJob;
            JobId? requiredJob = null;
            if (categoryRowId == 0)
            {
                wrongJob = false;
            }
            else
            {
                var category = _classJobCategorySheet.Value.GetRow(categoryRowId);
                wrongJob = !IsJobInCategory(category, currentJobRowId);
                // requiredJob intentionally left null — categories cover multiple jobs (tank, healer, etc.)
            }

            // Level requirement (only meaningful when on the right job)
            var requiredLevel = row.ClassJobLevel[0];
            var levelTooLow = !wrongJob && currentLevel < requiredLevel;

            // Prerequisite quests — respect the join type (All vs AtLeastOne)
            var missingPrereqs = new List<QuestId>();
            var prereqRowIds = new[]
            {
                row.PreviousQuest[0].RowId,
                row.PreviousQuest[1].RowId,
                row.PreviousQuest[2].RowId,
            };
            foreach (var prereqRowId in prereqRowIds)
            {
                if (prereqRowId == 0) continue;
                if (!QuestManager.IsQuestComplete((ushort)(prereqRowId & 0xFFFF)))
                    missingPrereqs.Add(new QuestId(prereqRowId));
            }

            // PreviousQuestJoin: 0 = All must be complete, 1 = AtLeastOne must be complete
            var nonZeroPrereqCount = prereqRowIds.Count(id => id != 0);
            var prerequisiteIncomplete = row.PreviousQuestJoin == 0
                ? missingPrereqs.Count > 0                           // All: any missing = incomplete
                : missingPrereqs.Count == nonZeroPrereqCount;       // AtLeastOne: incomplete only if ALL missing

            if (!wrongJob && !levelTooLow && !prerequisiteIncomplete)
                return Task.FromResult<Result<QuestUnlockReason?>>(Result.Ok<QuestUnlockReason?>(null));

            return Task.FromResult<Result<QuestUnlockReason?>>(Result.Ok<QuestUnlockReason?>(
                new QuestUnlockReason(
                    LevelTooLow: levelTooLow,
                    RequiredLevel: wrongJob ? 0 : requiredLevel,
                    PrerequisiteIncomplete: prerequisiteIncomplete,
                    MissingPrereqs: missingPrereqs,
                    WrongJob: wrongJob,
                    RequiredJob: requiredJob,
                    AlreadyCompleted: false,
                    OtherReason: false,
                    Detail: null)));
        }
    }

    // Maps a ClassJob Lumina row ID to the corresponding ClassJobCategory boolean property.
    // ClassJobCategory has a typed bool property per job abbreviation.
    private static bool IsJobInCategory(ClassJobCategory category, uint classJobRowId) => classJobRowId switch
    {
        0  => category.ADV,
        2  => category.GLA,
        3  => category.PGL,
        4  => category.MRD,
        5  => category.LNC,
        6  => category.ARC,
        7  => category.CNJ,
        8  => category.THM,
        9  => category.CRP,
        10 => category.BSM,
        11 => category.ARM,
        12 => category.GSM,
        13 => category.LTW,
        14 => category.WVR,
        15 => category.ALC,
        16 => category.CUL,
        17 => category.MIN,
        18 => category.BTN,
        19 => category.FSH,
        20 => category.PLD,
        21 => category.MNK,
        22 => category.WAR,
        23 => category.DRG,
        24 => category.BRD,
        25 => category.WHM,
        26 => category.BLM,
        27 => category.ACN,
        28 => category.SMN,
        29 => category.SCH,
        30 => category.ROG,
        31 => category.NIN,
        32 => category.MCH,
        33 => category.DRK,
        34 => category.AST,
        35 => category.SAM,
        36 => category.RDM,
        37 => category.BLU,
        38 => category.GNB,
        39 => category.DNC,
        40 => category.RPR,
        41 => category.SGE,
        42 => category.VPR,
        43 => category.PCT,
        _  => false
    };

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            if (qm == null)
                return Task.FromResult<Result<IReadOnlyList<QuestId>>>(
                    Result.Fail<IReadOnlyList<QuestId>>("questManagerNull", "QuestManager instance unavailable"));

            var result = new List<QuestId>();
            var quests = qm->NormalQuests;
            for (var i = 0; i < quests.Length; i++)
            {
                var id = quests[i].QuestId;
                if (id != 0)
                    result.Add(ToPublic(id));
            }

            return Task.FromResult<Result<IReadOnlyList<QuestId>>>(Result.Ok<IReadOnlyList<QuestId>>(result));
        }
    }

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<QuestReward>>>(
            Result.Ok<IReadOnlyList<QuestReward>>(Array.Empty<QuestReward>()));
}