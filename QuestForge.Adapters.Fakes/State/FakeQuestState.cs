using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.State;

public sealed class FakeQuestState : IQuestState
{
    private readonly Dictionary<QuestId, QuestStatus> _statuses = new();
    private readonly Dictionary<QuestId, int> _sequences = new();
    private readonly Dictionary<QuestId, uint> _flags = new();
    private readonly HashSet<QuestId> _accepted = new();
    private readonly Dictionary<QuestId, IReadOnlyList<QuestReward>> _rewards = new();

    // ----- observable recording -----
    public record StateRead(string Method, DateTimeOffset At) : AdapterCall(At);
    public CallLog<StateRead> RecordedReads { get; } = new();

    private void Record(string method) =>
        RecordedReads.Add(new StateRead(method, DateTimeOffset.UtcNow));

    // ----- scriptable setters -----
    public void SetQuestStatus(QuestId quest, QuestStatus status) => _statuses[quest] = status;
    public void SetQuestSequence(QuestId quest, int sequence) => _sequences[quest] = sequence;
    public void SetQuestFlags(QuestId quest, uint flags) => _flags[quest] = flags;

    public void SetQuestFlagBit(QuestId quest, int bit, bool value)
    {
        _flags.TryGetValue(quest, out var current);
        if (value)
            _flags[quest] = current | (1u << bit);
        else
            _flags[quest] = current & ~(1u << bit);
    }

    public void AddAcceptedQuest(QuestId quest) => _accepted.Add(quest);
    public void RemoveAcceptedQuest(QuestId quest) => _accepted.Remove(quest);

    public void SetQuestRewards(QuestId quest, IReadOnlyList<QuestReward> rewards) =>
        _rewards[quest] = rewards;

    // ----- Reset -----
    public void Reset() => RecordedReads.Clear();

    // ----- IQuestState implementation -----

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetQuestStatus));
        var status = _statuses.TryGetValue(quest, out var s) ? s : QuestStatus.Unknown;
        return Task.FromResult<Result<QuestStatus>>(Result.Ok(status));
    }

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetQuestSequence));
        var seq = _sequences.TryGetValue(quest, out var s) ? s : 0;
        return Task.FromResult<Result<int>>(Result.Ok(seq));
    }

    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetQuestFlags));
        var flags = _flags.TryGetValue(quest, out var f) ? f : 0u;
        return Task.FromResult<Result<uint>>(Result.Ok(flags));
    }

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsQuestFlagSet));
        _flags.TryGetValue(quest, out var flags);
        bool set = (flags & (1u << flagBit)) != 0;
        return Task.FromResult<Result<bool>>(Result.Ok(set));
    }

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsQuestAccepted));
        return Task.FromResult<Result<bool>>(Result.Ok(_accepted.Contains(quest)));
    }

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsQuestComplete));
        bool complete = _statuses.TryGetValue(quest, out var s) && s == QuestStatus.Complete;
        return Task.FromResult<Result<bool>>(Result.Ok(complete));
    }

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsQuestAvailable));
        bool available = _statuses.TryGetValue(quest, out var s) && s == QuestStatus.Available;
        return Task.FromResult<Result<bool>>(Result.Ok(available));
    }

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(WhyUnavailable));
        QuestUnlockReason? reason = null;
        return Task.FromResult<Result<QuestUnlockReason?>>(Result.Ok(reason));
    }

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetAcceptedQuests));
        IReadOnlyList<QuestId> list = _accepted.ToArray();
        return Task.FromResult<Result<IReadOnlyList<QuestId>>>(Result.Ok(list));
    }

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetAvailableQuestRewards));
        IReadOnlyList<QuestReward> rewards = Array.Empty<QuestReward>();
        return Task.FromResult<Result<IReadOnlyList<QuestReward>>>(Result.Ok(rewards));
    }
}