using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Recording;

public sealed class RecordingGameStateProvider : IGameStateProvider
{
    private readonly IGameStateProvider _inner;
    private readonly ITraceWriter _trace;
    private readonly Func<string?> _runIdAccessor;
    private readonly TimeProvider _clock;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly bool _skipIfNoRunId;

    public RecordingGameStateProvider(
        IGameStateProvider inner,
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

    public async Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct)
    {
        var result = await _inner.GetPlayerState(ct);
        Record(nameof(GetPlayerState), null, result);
        return result;
    }

    public async Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
    {
        var result = await _inner.GetPlayerPosition(ct);
        Record(nameof(GetPlayerPosition), null, result);
        return result;
    }

    public async Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        var result = await _inner.GetPlayerZone(ct);
        Record(nameof(GetPlayerZone), null, result);
        return result;
    }

    public async Task<Result<bool>> IsPlayerInCombat(CancellationToken ct)
    {
        var result = await _inner.IsPlayerInCombat(ct);
        Record(nameof(IsPlayerInCombat), null, result);
        return result;
    }

    public async Task<Result<MountState>> GetMountState(CancellationToken ct)
    {
        var result = await _inner.GetMountState(ct);
        Record(nameof(GetMountState), null, result);
        return result;
    }

    public async Task<Result<bool>> IsPlayerDiving(CancellationToken ct)
    {
        var result = await _inner.IsPlayerDiving(ct);
        Record(nameof(IsPlayerDiving), null, result);
        return result;
    }

    public async Task<Result<bool>> IsPlayerCasting(CancellationToken ct)
    {
        var result = await _inner.IsPlayerCasting(ct);
        Record(nameof(IsPlayerCasting), null, result);
        return result;
    }

    public async Task<Result<bool>> IsPlayerDead(CancellationToken ct)
    {
        var result = await _inner.IsPlayerDead(ct);
        Record(nameof(IsPlayerDead), null, result);
        return result;
    }

    public async Task<Result<JobId>> GetCurrentJob(CancellationToken ct)
    {
        var result = await _inner.GetCurrentJob(ct);
        Record(nameof(GetCurrentJob), null, result);
        return result;
    }

    public async Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
    {
        var result = await _inner.GetJobLevel(job, ct);
        Record(nameof(GetJobLevel), job, result);
        return result;
    }

    public async Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct)
    {
        var result = await _inner.GetCurrentInstanceKind(ct);
        Record(nameof(GetCurrentInstanceKind), null, result);
        return result;
    }

    public async Task<Result<DutyAvailability>> GetDutyAvailability(DutyId duty, CancellationToken ct)
    {
        var result = await _inner.GetDutyAvailability(duty, ct);
        Record(nameof(GetDutyAvailability), duty, result);
        return result;
    }

    public async Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct)
    {
        var result = await _inner.GetNewGamePlusState(ct);
        Record(nameof(GetNewGamePlusState), null, result);
        return result;
    }

    public async Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct)
    {
        var result = await _inner.GetNearbyNpcs(radius, ct);
        Record(nameof(GetNearbyNpcs), radius, result);
        return result;
    }

    public async Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct)
    {
        var result = await _inner.FindNpc(npc, ct);
        Record(nameof(FindNpc), npc, result);
        return result;
    }

    public async Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct)
    {
        var result = await _inner.GetNearbyInteractables(radius, ct);
        Record(nameof(GetNearbyInteractables), radius, result);
        return result;
    }

    public async Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct)
    {
        var result = await _inner.FindInteractable(obj, ct);
        Record(nameof(FindInteractable), obj, result);
        return result;
    }

    public async Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct)
    {
        var result = await _inner.IsInteractableActive(obj, ct);
        Record(nameof(IsInteractableActive), obj, result);
        return result;
    }

    public async Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
    {
        var result = await _inner.IsAetheryteAttuned(aetheryte, ct);
        Record(nameof(IsAetheryteAttuned), aetheryte, result);
        return result;
    }

    public async Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct)
    {
        var result = await _inner.GetAttunedAetherytes(ct);
        Record(nameof(GetAttunedAetherytes), null, result);
        return result;
    }

    public async Task<Result<UiState>> GetUiState(CancellationToken ct)
    {
        var result = await _inner.GetUiState(ct);
        Record(nameof(GetUiState), null, result);
        return result;
    }

    public async Task<Result<int>> GetFreeInventorySlots(CancellationToken ct)
    {
        var result = await _inner.GetFreeInventorySlots(ct);
        Record(nameof(GetFreeInventorySlots), null, result);
        return result;
    }

    public async Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct)
    {
        var result = await _inner.GetItemCount(item, ct);
        Record(nameof(GetItemCount), item, result);
        return result;
    }

    public async Task<Result<long>> GetGil(CancellationToken ct)
    {
        var result = await _inner.GetGil(ct);
        Record(nameof(GetGil), null, result);
        return result;
    }

    public async Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
    {
        var result = await _inner.GetTravelCapability(destination, ct);
        Record(nameof(GetTravelCapability), destination, result);
        return result;
    }
}