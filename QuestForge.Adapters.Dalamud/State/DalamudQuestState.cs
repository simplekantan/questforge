using FFXIVClientStructs.FFXIV.Client.Game;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.State;

public sealed class DalamudQuestState : IQuestState
{
    private readonly PluginServices _svc;

    public DalamudQuestState(PluginServices svc) => _svc = svc;

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

            // Phase 6: full prerequisite validation deferred — quest 66130 is always Available
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
            // null → quest not currently in journal; flags are effectively 0
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

    // Phase 6 placeholder: prerequisite check (level, class, prior quests) deferred
    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(true));

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<QuestUnlockReason?>>(Result.Ok<QuestUnlockReason?>(null));

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
