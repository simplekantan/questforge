using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.State;

public sealed class DalamudGameStateProvider : IGameStateProvider
{
    private readonly PluginServices _svc;

    public DalamudGameStateProvider(PluginServices svc) => _svc = svc;

    // IGameStateProvider — Player state

    public Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsPlayerInCombat(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<MountState>> GetMountState(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsPlayerDiving(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsPlayerCasting(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsPlayerDead(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<JobId>> GetCurrentJob(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
        => throw new NotImplementedException();

    // IGameStateProvider — Instanced content

    public Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<DutyAvailability>> GetDutyAvailability(DutyId duty, CancellationToken ct)
        => throw new NotImplementedException();

    // IGameStateProvider — New Game+

    public Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct)
        => throw new NotImplementedException();

    // IGameStateProvider — World/NPC state

    public Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct)
        => throw new NotImplementedException();

    // IGameStateProvider — UI state

    public Task<Result<UiState>> GetUiState(CancellationToken ct)
        => throw new NotImplementedException();

    // IGameStateProvider — Inventory

    public Task<Result<int>> GetFreeInventorySlots(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<long>> GetGil(CancellationToken ct)
        => throw new NotImplementedException();

    // IGameStateProvider — Composite/derived

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
        => throw new NotImplementedException();
}