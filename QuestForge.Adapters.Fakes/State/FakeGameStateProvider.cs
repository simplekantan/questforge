using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.State;

public sealed class FakeGameStateProvider : IGameStateProvider
{
    // ----- internal mutable state -----
    private ZoneId _zone = new(0);
    private WorldPosition _position = new(0f, 0f, 0f);
    private JobId _job = new(1);
    private int _jobLevel = 1;
    private bool _inCombat;
    private MountState _mountState = MountState.Dismounted;
    private bool _dead;
    private bool _diving;
    private bool _casting;
    private InstanceKind _instanceKind = InstanceKind.None;
    private NewGamePlusState _newGamePlusState = new(false, null, false);
    private readonly List<NpcReference> _npcs = new();
    private readonly List<InteractableReference> _interactables = new();
    private readonly Dictionary<InteractableId, bool> _interactableActive = new();
    private readonly HashSet<AetheryteId> _attunedAetherytes = new();
    private UiState _uiState = new(false, false, false, false, false, false, false, false, null);
    private int _freeInventorySlots;
    private readonly Dictionary<ItemId, int> _itemCounts = new();
    private long _gil;
    private readonly Dictionary<ZoneId, TravelCapability> _travelCapabilities = new();

    // ----- observable recording -----
    public record StateRead(string Method, DateTimeOffset At) : AdapterCall(At);
    public CallLog<StateRead> RecordedReads { get; } = new();

    private void Record(string method) =>
        RecordedReads.Add(new StateRead(method, DateTimeOffset.UtcNow));

    // ----- scriptable setters -----
    public void SetZone(ZoneId zone) => _zone = zone;
    public void SetPosition(WorldPosition position) => _position = position;
    public void SetJob(JobId job, int level) { _job = job; _jobLevel = level; }
    public void SetInCombat(bool inCombat) => _inCombat = inCombat;
    public void SetMountState(MountState mountState) => _mountState = mountState;
    public void SetDead(bool dead) => _dead = dead;
    public void SetDiving(bool diving) => _diving = diving;
    public void SetCasting(bool casting) => _casting = casting;
    public void SetInstanceKind(InstanceKind instanceKind) => _instanceKind = instanceKind;
    public void SetNewGamePlusState(NewGamePlusState state) => _newGamePlusState = state;

    public void AddNpc(NpcReference npc) { lock (_npcs) _npcs.Add(npc); }
    public void RemoveNpc(NpcId id) { lock (_npcs) _npcs.RemoveAll(n => n.Id == id); }
    public void ClearNpcs() { lock (_npcs) _npcs.Clear(); }

    public void AddInteractable(InteractableReference interactable)
    {
        lock (_interactables)
        {
            _interactables.Add(interactable);
            _interactableActive[interactable.Id] = true;
        }
    }

    public void SetInteractableActive(InteractableId id, bool active)
    {
        lock (_interactables) _interactableActive[id] = active;
    }

    public void ClearInteractables()
    {
        lock (_interactables)
        {
            _interactables.Clear();
            _interactableActive.Clear();
        }
    }

    public void SetAetheryteAttuned(AetheryteId aetheryte, bool attuned)
    {
        if (attuned) _attunedAetherytes.Add(aetheryte);
        else _attunedAetherytes.Remove(aetheryte);
    }

    public void SetUiState(UiState uiState) => _uiState = uiState;
    public void SetFreeInventorySlots(int slots) => _freeInventorySlots = slots;
    public void SetItemCount(ItemId item, int count) => _itemCounts[item] = count;
    public void SetGil(long gil) => _gil = gil;
    public void SetTravelCapability(ZoneId zone, TravelCapability capability) =>
        _travelCapabilities[zone] = capability;

    // Called by FakeNavigator to update navigating state (kept for interface compatibility)
    public void SetIsNavigating(bool isNavigating) { /* state tracks externally via FakeNavigator */ }

    // ----- Reset -----
    public void Reset() => RecordedReads.Clear();

    // ----- IGameStateProvider implementation -----

    public Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetPlayerState));
        var snapshot = new PlayerStateSnapshot(
            _position, _zone, _job, _jobLevel,
            _inCombat, _mountState, _diving, _casting, _dead);
        return Task.FromResult<Result<PlayerStateSnapshot>>(Result.Ok(snapshot));
    }

    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetPlayerPosition));
        return Task.FromResult<Result<WorldPosition>>(Result.Ok(_position));
    }

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetPlayerZone));
        return Task.FromResult<Result<ZoneId>>(Result.Ok(_zone));
    }

    public Task<Result<bool>> IsPlayerInCombat(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerInCombat));
        return Task.FromResult<Result<bool>>(Result.Ok(_inCombat));
    }

    public Task<Result<MountState>> GetMountState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetMountState));
        return Task.FromResult<Result<MountState>>(Result.Ok(_mountState));
    }

    public Task<Result<bool>> IsPlayerDiving(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerDiving));
        return Task.FromResult<Result<bool>>(Result.Ok(_diving));
    }

    public Task<Result<bool>> IsPlayerCasting(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerCasting));
        return Task.FromResult<Result<bool>>(Result.Ok(_casting));
    }

    public Task<Result<bool>> IsPlayerDead(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerDead));
        return Task.FromResult<Result<bool>>(Result.Ok(_dead));
    }

    public Task<Result<JobId>> GetCurrentJob(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetCurrentJob));
        return Task.FromResult<Result<JobId>>(Result.Ok(_job));
    }

    public Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetJobLevel));
        int level = job == _job ? _jobLevel : 1;
        return Task.FromResult<Result<int>>(Result.Ok(level));
    }

    public Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetCurrentInstanceKind));
        return Task.FromResult<Result<InstanceKind>>(Result.Ok(_instanceKind));
    }

    public Task<Result<DutyAvailability>> GetDutyAvailability(DutyId duty, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetDutyAvailability));
        return Task.FromResult<Result<DutyAvailability>>(
            Result.Ok(new DutyAvailability(false, false, false, false, 0, 0, null)));
    }

    public Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetNewGamePlusState));
        return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(_newGamePlusState));
    }

    public Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetNearbyNpcs));
        IReadOnlyList<NpcReference> nearby;
        lock (_npcs) nearby = _npcs.Where(n => n.DistanceToPlayer <= radius).ToArray();
        return Task.FromResult<Result<IReadOnlyList<NpcReference>>>(Result.Ok(nearby));
    }

    public Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(FindNpc));
        NpcReference? found;
        lock (_npcs) found = _npcs.FirstOrDefault(n => n.Id == npc);
        return Task.FromResult<Result<NpcReference?>>(Result.Ok(found));
    }

    public Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetNearbyInteractables));
        IReadOnlyList<InteractableReference> nearby;
        lock (_interactables) nearby = _interactables.Where(i => i.DistanceToPlayer <= radius).ToArray();
        return Task.FromResult<Result<IReadOnlyList<InteractableReference>>>(Result.Ok(nearby));
    }

    public Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(FindInteractable));
        InteractableReference? found;
        lock (_interactables) found = _interactables.FirstOrDefault(i => i.Id == obj);
        return Task.FromResult<Result<InteractableReference?>>(Result.Ok(found));
    }

    public Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsInteractableActive));
        bool active;
        lock (_interactables) active = _interactableActive.TryGetValue(obj, out var val) && val;
        return Task.FromResult<Result<bool>>(Result.Ok(active));
    }

    public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsAetheryteAttuned));
        return Task.FromResult<Result<bool>>(Result.Ok(_attunedAetherytes.Contains(aetheryte)));
    }

    public Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetAttunedAetherytes));
        IReadOnlyList<AetheryteId> list = _attunedAetherytes.ToArray();
        return Task.FromResult<Result<IReadOnlyList<AetheryteId>>>(Result.Ok(list));
    }

    public Task<Result<UiState>> GetUiState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetUiState));
        return Task.FromResult<Result<UiState>>(Result.Ok(_uiState));
    }

    public Task<Result<int>> GetFreeInventorySlots(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetFreeInventorySlots));
        return Task.FromResult<Result<int>>(Result.Ok(_freeInventorySlots));
    }

    public Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetItemCount));
        _itemCounts.TryGetValue(item, out var count);
        return Task.FromResult<Result<int>>(Result.Ok(count));
    }

    public Task<Result<long>> GetGil(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetGil));
        return Task.FromResult<Result<long>>(Result.Ok(_gil));
    }

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetTravelCapability));
        if (_travelCapabilities.TryGetValue(destination, out var cap))
            return Task.FromResult<Result<TravelCapability>>(Result.Ok(cap));
        var defaultCap = new TravelCapability(true, null, false, false, true, false, 0);
        return Task.FromResult<Result<TravelCapability>>(Result.Ok(defaultCap));
    }
}