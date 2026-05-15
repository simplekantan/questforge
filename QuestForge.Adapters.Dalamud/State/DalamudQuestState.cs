using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.State;

public sealed class DalamudQuestState : IQuestState
{
    private readonly PluginServices _svc;

    // Lazy-initialised: DataManager may not be fully ready at construction time.
    // Throws InvalidOperationException at first use if the sheet is unavailable.
    private readonly Lazy<Lumina.Excel.ExcelSheet<Quest>> _questSheet;

    public DalamudQuestState(PluginServices svc)
    {
        _svc = svc;
        _questSheet = new Lazy<Lumina.Excel.ExcelSheet<Quest>>(() =>
            svc.DataManager.GetExcelSheet<Quest>()
            ?? throw new InvalidOperationException("Lumina Quest sheet unavailable"));
    }

    // --- ID translation ---------------------------------------------------

    // Lumina rowId → in-game QuestManager ushort (lower 16 bits)
    private static ushort ToInternal(QuestId q) => (ushort)(q.Value & 0xFFFF);

    // In-game ushort → public QuestId (or into 0x10000 range)
    private static QuestId ToPublic(ushort id) => new((uint)id | 0x10000u);

    // --- IQuestState — Status queries -------------------------------------

    // Satisfies: GetQuestStatus
    // Logic: complete → Accepted (in journal) → Available (Phase 6 default)
    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            if (qm == null)
                return Task.FromResult<Result<QuestStatus>>(
                    Result.Fail<QuestStatus>("questManagerNull", "QuestManager instance unavailable"));

            var internalId = ToInternal(quest);

            // Most specific first: already finished
            if (QuestManager.IsQuestComplete(internalId))
                return Task.FromResult<Result<QuestStatus>>(Result.Ok(QuestStatus.Complete));

            // In the active journal → Accepted
            if (qm->IsQuestAccepted(internalId))
                return Task.FromResult<Result<QuestStatus>>(Result.Ok(QuestStatus.Accepted));

            // Phase 6 simplification: full prerequisite validation deferred.
            // Quest 66130 done-criterion never exercises this branch with Locked.
            return Task.FromResult<Result<QuestStatus>>(Result.Ok(QuestStatus.Available));
        }
    }

    // Satisfies: GetQuestSequence
    // Uses the static QuestManager.GetQuestSequence helper which returns 0 when
    // the quest is not active (not in journal or already complete).
    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            // Static method — no instance needed; returns 0 when quest not in journal
            return Task.FromResult<Result<int>>(
                Result.Ok((int)QuestManager.GetQuestSequence(ToInternal(quest))));
        }
    }

    // Satisfies: GetQuestFlags
    // QuestWork.Flags is a byte bitfield (IsPriority, RewardItemArrayIndex, IsHidden).
    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            if (qm == null)
                return Task.FromResult<Result<uint>>(
                    Result.Fail<uint>("questManagerNull", "QuestManager instance unavailable"));

            var q = qm->GetQuestById(ToInternal(quest));
            // null → quest not currently in journal; flags are effectively 0
            if (q == null)
                return Task.FromResult<Result<uint>>(Result.Ok(0u));

            return Task.FromResult<Result<uint>>(Result.Ok((uint)q->Flags));
        }
    }

    // Satisfies: IsQuestFlagSet
    // Phase 6 placeholder: individual flag-bit semantics not yet mapped to engine predicates.
    // The Flags byte holds IsPriority (bit 0), RewardItemArrayIndex (bits 1-2), IsHidden (bit 3)
    // but none of these are exercised by the quest 66130 done-criterion.
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

            // Phase 6 placeholder: flag bit mapping to engine predicates deferred.
            // Raw bit test on the Flags byte for callers that know the offset.
            var set = (q->Flags & (1 << flagBit)) != 0;
            return Task.FromResult<Result<bool>>(Result.Ok(set));
        }
    }

    // --- IQuestState — Lifecycle queries ----------------------------------

    // Satisfies: IsQuestAccepted
    // Uses the QuestManager instance method which scans NormalQuests internally.
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

    // Satisfies: IsQuestComplete
    // Static helper — does not require a non-null QuestManager pointer at call site.
    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            return Task.FromResult<Result<bool>>(
                Result.Ok(QuestManager.IsQuestComplete(ToInternal(quest))));
        }
    }

    // Satisfies: IsQuestAvailable
    // Phase 6 placeholder: full prerequisite check (level, class, prior quests) deferred.
    // Quest 66130 done-criterion does not exercise this path.
    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(true));

    // Satisfies: WhyUnavailable
    // Phase 6 placeholder: returns null (no known reason) since IsQuestAvailable always returns true.
    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<QuestUnlockReason?>>(Result.Ok<QuestUnlockReason?>(null));

    // --- IQuestState — Collections ----------------------------------------

    // Satisfies: GetAcceptedQuests
    // Scans the 30-slot NormalQuests journal; skips empty slots (QuestId == 0).
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

    // --- IQuestState — Rewards --------------------------------------------

    // Satisfies: GetAvailableQuestRewards
    // Phase 6 placeholder: reward selection UI deferred; quest 66130 pilot does not use this.
    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<QuestReward>>>(
            Result.Ok<IReadOnlyList<QuestReward>>(Array.Empty<QuestReward>()));
}
