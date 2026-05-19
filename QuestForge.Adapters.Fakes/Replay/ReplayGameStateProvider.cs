using System.Text.Json;
using QuestForge.Adapters;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// IGameStateProvider implementation backed by recorded ObservationEvents.
/// Each method call consumes the next matching observation from the scanner.
/// Throws ReplayObservationStarvationException when no matching observation is found.
/// </summary>
public sealed class ReplayGameStateProvider : IGameStateProvider
{
    private readonly ObservationScanner _scanner;

    public ReplayGameStateProvider(IReadOnlyList<ObservationEvent> observations)
    {
        _scanner = new ObservationScanner(observations);
    }

    // ----- Player state -----

    public Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetPlayerState), null);
        return Task.FromResult(Materialize<PlayerStateSnapshot>(obs.Value));
    }

    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetPlayerPosition), null);
        return Task.FromResult(Materialize<WorldPosition>(obs.Value));
    }

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetPlayerZone), null);
        return Task.FromResult(Materialize<ZoneId>(obs.Value));
    }

    public Task<Result<bool>> IsPlayerInCombat(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsPlayerInCombat), null);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<MountState>> GetMountState(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetMountState), null);
        return Task.FromResult(Materialize<MountState>(obs.Value));
    }

    public Task<Result<bool>> IsPlayerDiving(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsPlayerDiving), null);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsPlayerCasting(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsPlayerCasting), null);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsPlayerDead(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsPlayerDead), null);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<JobId>> GetCurrentJob(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetCurrentJob), null);
        return Task.FromResult(Materialize<JobId>(obs.Value));
    }

    public Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetJobLevel), job);
        return Task.FromResult(Materialize<int>(obs.Value));
    }

    // ----- Instanced content -----

    public Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetCurrentInstanceKind), null);
        return Task.FromResult(Materialize<InstanceKind>(obs.Value));
    }

    public Task<Result<DutyAvailability>> GetDutyAvailability(DutyId duty, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetDutyAvailability), duty);
        return Task.FromResult(Materialize<DutyAvailability>(obs.Value));
    }

    // ----- New Game+ -----

    public Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetNewGamePlusState), null);
        return Task.FromResult(Materialize<NewGamePlusState>(obs.Value));
    }

    // ----- World / NPC state -----

    public Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetNearbyNpcs), radius);
        return Task.FromResult(Materialize<IReadOnlyList<NpcReference>>(obs.Value));
    }

    public Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(FindNpc), npc);
        return Task.FromResult(Materialize<NpcReference?>(obs.Value));
    }

    public Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetNearbyInteractables), radius);
        return Task.FromResult(Materialize<IReadOnlyList<InteractableReference>>(obs.Value));
    }

    public Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(FindInteractable), obj);
        return Task.FromResult(Materialize<InteractableReference?>(obs.Value));
    }

    public Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsInteractableActive), obj);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(IsAetheryteAttuned), aetheryte);
        return Task.FromResult(Materialize<bool>(obs.Value));
    }

    public Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetAttunedAetherytes), null);
        return Task.FromResult(Materialize<IReadOnlyList<AetheryteId>>(obs.Value));
    }

    // ----- UI state -----

    public Task<Result<UiState>> GetUiState(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetUiState), null);
        return Task.FromResult(Materialize<UiState>(obs.Value));
    }

    // ----- Inventory -----

    public Task<Result<int>> GetFreeInventorySlots(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetFreeInventorySlots), null);
        return Task.FromResult(Materialize<int>(obs.Value));
    }

    public Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetItemCount), item);
        return Task.FromResult(Materialize<int>(obs.Value));
    }

    public Task<Result<long>> GetGil(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetGil), null);
        return Task.FromResult(Materialize<long>(obs.Value));
    }

    // ----- Composite/derived -----

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetTravelCapability), destination);
        return Task.FromResult(Materialize<TravelCapability>(obs.Value));
    }

    // ----- Aethernet -----

    public Task<Result<AethernetId?>> GetLastAethernetDestination(CancellationToken ct)
        // Phase 7 placeholder: not recorded in trace observations, return null
        => Task.FromResult<Result<AethernetId?>>(Result.Ok((AethernetId?)null));

    private static Result<T> Materialize<T>(JsonElement? value)
        => ObservationMaterializer.Materialize<T>(value);
}
