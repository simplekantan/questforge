using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Recording;

public sealed class RecordingQuestState : IQuestState
{
    private readonly IQuestState _inner;
    private readonly ITraceWriter _trace;
    private readonly Func<string?> _runIdAccessor;
    private readonly TimeProvider _clock;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly bool _skipIfNoRunId;

    public RecordingQuestState(
        IQuestState inner,
        ITraceWriter trace,
        Func<string?> runIdAccessor,
        TimeProvider? clock = null,
        bool skipIfNoRunId = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _runIdAccessor = runIdAccessor ?? throw new ArgumentNullException(nameof(runIdAccessor));
        _clock = clock ?? TimeProvider.System;
        _skipIfNoRunId = skipIfNoRunId;
    }

    private void Record<T>(string method, object? argument, Result<T> result)
    {
        try
        {
            var runId = _runIdAccessor();
            // If configured to skip when no run is active, skip emission.
            if (_skipIfNoRunId && runId is null or "") return;
            var argEl = argument is null ? (JsonElement?)null
                : JsonSerializer.SerializeToElement(argument, _jsonOpts);
            var valEl = Unwrap(result);
            _trace.Write(new ObservationEvent(
                runId,
                method,
                argEl,
                valEl,
                _clock.GetUtcNow()));
        }
        catch
        {
            // Write failure must not propagate to the caller
        }
    }

    private static JsonElement? Unwrap<T>(Result<T> result) => result switch
    {
        Result<T>.Success { Value: var v } => JsonSerializer.SerializeToElement(v, _jsonOpts),
        Result<T>.Failure f => JsonSerializer.SerializeToElement(
            new { failure = f.Reason, detail = f.Detail }, _jsonOpts),
        _ => null
    };

    public async Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.GetQuestStatus(quest, ct);
        Record(nameof(GetQuestStatus), quest, result);
        return result;
    }

    public async Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.GetQuestSequence(quest, ct);
        Record(nameof(GetQuestSequence), quest, result);
        return result;
    }

    public async Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.GetQuestFlags(quest, ct);
        Record(nameof(GetQuestFlags), quest, result);
        return result;
    }

    public async Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
    {
        var result = await _inner.IsQuestFlagSet(quest, flagBit, ct);
        Record(nameof(IsQuestFlagSet), new { quest, flagBit }, result);
        return result;
    }

    public async Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.IsQuestAccepted(quest, ct);
        Record(nameof(IsQuestAccepted), quest, result);
        return result;
    }

    public async Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.IsQuestComplete(quest, ct);
        Record(nameof(IsQuestComplete), quest, result);
        return result;
    }

    public async Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.IsQuestAvailable(quest, ct);
        Record(nameof(IsQuestAvailable), quest, result);
        return result;
    }

    public async Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.WhyUnavailable(quest, ct);
        Record(nameof(WhyUnavailable), quest, result);
        return result;
    }

    public async Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
    {
        var result = await _inner.GetAcceptedQuests(ct);
        Record(nameof(GetAcceptedQuests), null, result);
        return result;
    }

    public async Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
    {
        var result = await _inner.GetAvailableQuestRewards(ct);
        Record(nameof(GetAvailableQuestRewards), null, result);
        return result;
    }
}