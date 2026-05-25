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
    private readonly ObservationScanner? _legacyScanner;
    private readonly SegmentedObservationScanner? _segmentedScanner;

    public ReplayQuestState(IReadOnlyList<ObservationEvent> observations)
    {
        _legacyScanner = new ObservationScanner(observations);
    }

    public ReplayQuestState(SegmentedObservationScanner scanner)
    {
        _segmentedScanner = scanner;
    }

    private ObservationEvent ScanNext(string method, object? argument)
    {
        if (_segmentedScanner is not null)
            return _segmentedScanner.Next(method, argument);
        return _legacyScanner!.Next(method, argument);
    }

    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(GetQuestStatus), quest);
        return Task.FromResult(Materialize<QuestStatus>(obs.Value));
    }

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(GetQuestSequence), quest);
        return Task.FromResult(Materialize<int>(obs.Value));
    }

    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(GetQuestFlags), quest);
        return Task.FromResult(Materialize<uint>(obs.Value));
    }

    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
    {
        var obs = ScanNext(nameof(IsQuestFlagSet), new { quest, flagBit });
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(IsQuestAccepted), quest);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(IsQuestComplete), quest);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(IsQuestAvailable), quest);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(WhyUnavailable), quest);
        return Task.FromResult(Materialize<QuestUnlockReason?>(obs.Value));
    }

    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
    {
        var obs = ScanNext(nameof(GetAcceptedQuests), null);
        return Task.FromResult(Materialize<IReadOnlyList<QuestId>>(obs.Value));
    }

    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
    {
        var obs = ScanNext(nameof(GetAvailableQuestRewards), null);
        return Task.FromResult(Materialize<IReadOnlyList<QuestReward>>(obs.Value));
    }

    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
    {
        var obs = ScanNext(nameof(GetQuestVariables), quest);
        return Task.FromResult(Materialize<IReadOnlyList<byte>>(obs.Value));
    }

    // Authoring-only signal; never recorded, never replayed. Do NOT ScanNext — that would starve every fixture.
    // Return a benign default (§3.4).
    public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    private static Result<T> Materialize<T>(JsonElement? value)
        => ObservationMaterializer.Materialize<T>(value);
}
