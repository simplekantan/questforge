using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.State;

public sealed class DalamudQuestState : IQuestState
{
    private readonly PluginServices _svc;

    public DalamudQuestState(PluginServices svc) => _svc = svc;

    // IQuestState — Status queries

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
        => throw new NotImplementedException();

    // IQuestState — Lifecycle queries

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    // IQuestState — Collections

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
        => throw new NotImplementedException();

    // IQuestState — Rewards

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
        => throw new NotImplementedException();
}