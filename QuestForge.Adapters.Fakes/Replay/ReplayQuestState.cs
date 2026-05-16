using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// IQuestState implementation backed by recorded ObservationEvents.
/// Each method call consumes the next matching observation from the scanner.
/// Throws ReplayObservationStarvationException when no matching observation is found.
/// </summary>
public sealed class ReplayQuestState : IQuestState
{
    private readonly ObservationScanner _scanner;

    public ReplayQuestState(IReadOnlyList<ObservationEvent> observations)
    {
        _scanner = new ObservationScanner(observations);
    }

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetQuestStatus), quest);
        return Task.FromResult(Materialize<QuestStatus>(obs.Value));
    }

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetQuestSequence), quest);
        return Task.FromResult(Materialize<int>(obs.Value));
    }

    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetQuestFlags), quest);
        return Task.FromResult(Materialize<uint>(obs.Value));
    }

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsQuestFlagSet), new { quest, flagBit });
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsQuestAccepted), quest);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsQuestComplete), quest);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsQuestAvailable), quest);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(WhyUnavailable), quest);
        return Task.FromResult(Materialize<QuestUnlockReason?>(obs.Value));
    }

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetAcceptedQuests), null);
        return Task.FromResult(Materialize<IReadOnlyList<QuestId>>(obs.Value));
    }

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetAvailableQuestRewards), null);
        return Task.FromResult(Materialize<IReadOnlyList<QuestReward>>(obs.Value));
    }

    private static Result<T> Materialize<T>(JsonElement? value)
        => ObservationMaterializer.Materialize<T>(value);
}
