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
    private IReadOnlyList<QuestReward> _availableRewards = Array.Empty<QuestReward>();
    private readonly Dictionary<QuestId, QuestUnlockReason?> _whyUnavailable = new();
    private readonly Dictionary<QuestId, (string Reason, string? Detail)> _availabilityFailures = new();
    private readonly Dictionary<QuestId, IReadOnlyList<byte>> _variables = new();
    // IsAcceptableNow scripting: if key absent → mirror IsQuestAvailable; if present → override (clamped to availability)
    private readonly Dictionary<QuestId, bool> _acceptableNowOverrides = new();
    private readonly Dictionary<QuestId, (string Reason, string? Detail)> _acceptableNowFailures = new();

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

    /// <summary>
    /// Sets all 6 quest variable bytes (V0–V5). Throws if the array length is not exactly 6.
    /// </summary>
    public void SetQuestVariables(QuestId quest, params byte[] variables)
    {
        if (variables.Length != 6)
            throw new ArgumentException("Quest variables must be exactly 6 bytes (V0–V5).", nameof(variables));
        _variables[quest] = variables;
    }

    /// <summary>
    /// Convenience overload: sets V0 and leaves V1–V5 as zero.
    /// Called when a single numeric literal is passed (e.g. SetQuestVariables(Q, 1)).
    /// </summary>
    public void SetQuestVariables(QuestId quest, int v0)
    {
        _variables[quest] = [(byte)v0, 0, 0, 0, 0, 0];
    }

    public void AddAcceptedQuest(QuestId quest) => _accepted.Add(quest);
    public void RemoveAcceptedQuest(QuestId quest) => _accepted.Remove(quest);

    public void SetAvailableRewards(IReadOnlyList<QuestReward> rewards) =>
        _availableRewards = rewards;

    // Scripts a specific WhyUnavailable result for a quest.
    // Pass null to clear scripting (reverts to the default null-reason response).
    public void SetWhyUnavailable(QuestId quest, QuestUnlockReason? reason) =>
        _whyUnavailable[quest] = reason;

    // Scripts IsQuestAvailable to return a Failure result for a specific quest.
    public void SetIsQuestAvailableFail(QuestId quest, string reason, string? detail = null) =>
        _availabilityFailures[quest] = (reason, detail);

    // Clears all scripted IsQuestAvailable failures.
    public void ClearIsQuestAvailableFail(QuestId quest) =>
        _availabilityFailures.Remove(quest);

    // Scripts IsAcceptableNow to return an explicit bool (clamped to availability invariant).
    public void SetIsAcceptableNow(QuestId quest, bool value) =>
        _acceptableNowOverrides[quest] = value;

    // Scripts IsAcceptableNow to return a Failure.
    public void SetIsAcceptableNowFail(QuestId quest, string reason, string? detail = null) =>
        _acceptableNowFailures[quest] = (reason, detail);

    // Clears an IsAcceptableNow failure override.
    public void ClearIsAcceptableNowFail(QuestId quest) =>
        _acceptableNowFailures.Remove(quest);

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
        if (_availabilityFailures.TryGetValue(quest, out var failure))
            return Task.FromResult<Result<bool>>(Result.Fail<bool>(failure.Reason, failure.Detail));
        bool available = _statuses.TryGetValue(quest, out var s) && s == QuestStatus.Available;
        return Task.FromResult<Result<bool>>(Result.Ok(available));
    }

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(WhyUnavailable));
        QuestUnlockReason? reason = _whyUnavailable.TryGetValue(quest, out var scripted) ? scripted : null;
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
        return Task.FromResult<Result<IReadOnlyList<QuestReward>>>(Result.Ok(_availableRewards));
    }

    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetQuestVariables));
        IReadOnlyList<byte> vars = _variables.TryGetValue(quest, out var v) ? v : new byte[6];
        return Task.FromResult<Result<IReadOnlyList<byte>>>(Result.Ok(vars));
    }

    public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsAcceptableNow));

        // Scripted failure takes precedence.
        if (_acceptableNowFailures.TryGetValue(quest, out var failure))
            return Task.FromResult<Result<bool>>(Result.Fail<bool>(failure.Reason, failure.Detail));

        // Get the availability result — we need to mirror it or apply the clamp.
        // Re-use IsQuestAvailable logic inline (don't call the method — avoids double-recording).
        Result<bool> availResult;
        if (_availabilityFailures.TryGetValue(quest, out var availFail))
            availResult = Result.Fail<bool>(availFail.Reason, availFail.Detail);
        else
        {
            bool available = _statuses.TryGetValue(quest, out var s) && s == QuestStatus.Available;
            availResult = Result.Ok(available);
        }

        // Propagate availability failure unchanged.
        if (availResult is Result<bool>.Failure)
            return Task.FromResult(availResult);

        var isAvailable = ((Result<bool>.Success)availResult).Value;

        // If an explicit override is set, clamp it to availability: true only if available.
        if (_acceptableNowOverrides.TryGetValue(quest, out var explicitValue))
            return Task.FromResult<Result<bool>>(Result.Ok(explicitValue && isAvailable));

        // Default: mirror IsQuestAvailable.
        return Task.FromResult<Result<bool>>(Result.Ok(isAvailable));
    }
}