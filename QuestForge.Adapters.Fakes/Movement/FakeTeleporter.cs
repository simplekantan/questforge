using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Movement;

public sealed class FakeTeleporter : ITeleporter
{
    private readonly FakeGameStateProvider _state;

    private TeleportOutcome? _nextTeleportResult;
    private AetheryteId? _homeAetheryte;
    private TimeSpan _returnCooldown = TimeSpan.Zero;

    // aetheryteId -> (zoneId, position)
    private readonly Dictionary<AetheryteId, (ZoneId Zone, WorldPosition Position)> _aetherytes = new();

    public record TeleportCall(AetheryteId Destination, DateTimeOffset At) : AdapterCall(At);
    public record TeleportAethernetCall(AethernetId Destination, DateTimeOffset At) : AdapterCall(At);
    public record ReturnCall(DateTimeOffset At) : AdapterCall(At);

    public CallLog<TeleportCall> RecordedTeleports { get; } = new();
    public CallLog<TeleportAethernetCall> RecordedAethernetTeleports { get; } = new();
    public CallLog<ReturnCall> RecordedReturns { get; } = new();

    public FakeTeleporter(FakeGameStateProvider state)
    {
        _state = state;
    }

    // ----- Scripting -----
    public void ScriptNextTeleportResult(TeleportOutcome outcome) => _nextTeleportResult = outcome;
    public void SetHomeAetheryte(AetheryteId? id) => _homeAetheryte = id;
    public void SetReturnCooldown(TimeSpan cooldown) => _returnCooldown = cooldown;

    public void RegisterAetheryte(AetheryteId id, ZoneId zone, WorldPosition position) =>
        _aetherytes[id] = (zone, position);

    // ----- Reset -----
    public void Reset()
    {
        RecordedTeleports.Clear();
        RecordedAethernetTeleports.Clear();
        RecordedReturns.Clear();
        _nextTeleportResult = null;
    }

    // ----- ITeleporter implementation -----

    public Task<Result<TeleportOutcome>> TeleportToAetheryte(AetheryteId destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedTeleports.Add(new TeleportCall(destination, DateTimeOffset.UtcNow));

        var outcome = ConsumeNextResult();

        if (outcome == TeleportOutcome.Arrived && _aetherytes.TryGetValue(destination, out var info))
        {
            _state.SetZone(info.Zone);
            _state.SetPosition(info.Position);
        }

        return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<TeleportOutcome>> TeleportToAethernet(AethernetId destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedAethernetTeleports.Add(new TeleportAethernetCall(destination, DateTimeOffset.UtcNow));
        var outcome = ConsumeNextResult();
        return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<TeleportOutcome>> UseReturn(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedReturns.Add(new ReturnCall(DateTimeOffset.UtcNow));
        var outcome = ConsumeNextResult();
        return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<AetheryteId?>> GetHomeAetheryte(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<AetheryteId?>>(Result.Ok(_homeAetheryte));
    }

    public Task<Result<TimeSpan>> GetReturnCooldown(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<TimeSpan>>(Result.Ok(_returnCooldown));
    }

    public Task<Result<TimeSpan>> GetTeleportCooldown(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<TimeSpan>>(Result.Ok(TimeSpan.Zero));
    }

    public Task<Result<long>> EstimateTeleportCost(AetheryteId destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<long>>(Result.Ok(0L));
    }

    public Task<Result<bool>> IsTeleportAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }

    private TeleportOutcome ConsumeNextResult()
    {
        if (_nextTeleportResult.HasValue)
        {
            var outcome = _nextTeleportResult.Value;
            _nextTeleportResult = null;
            return outcome;
        }
        return TeleportOutcome.Arrived;
    }
}