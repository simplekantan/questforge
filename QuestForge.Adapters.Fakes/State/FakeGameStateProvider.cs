using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.State;

public sealed class FakeGameStateProvider : IGameStateProvider
{
    // Single lock guards all mutable state — consistent, no partial-update races.
    private readonly object _lock = new();
    private readonly HashSet<uint> _aetherCurrents = [];

    private ZoneId _zone = new(0);
    private WorldPosition _position = new(0f, 0f, 0f);
    private JobId _job = new(1);
    private int _jobLevel = 1;
    private (string Reason, string? Detail)? _currentJobFailure;
    private (string Reason, string? Detail)? _currentPositionFailure;
    private bool _inCombat;
    private (string Reason, string? Detail)? _inCombatFailure;
    private MountState _mountState = MountState.Dismounted;
    private bool _dead;
    private bool _diving;
    private bool _casting;
    private bool _canMount = true;
    private InstanceKind _instanceKind = InstanceKind.None;
    private NewGamePlusState _ngpState = new(false, null, false);
    private (string Reason, string? Detail)? _ngpFailure;
    private readonly List<NpcReference> _npcs = [];
    private readonly List<HostileActor> _hostileActors = [];
    private (string Reason, string? Detail)? _hostileActorsFailure;
    private readonly List<InteractableReference> _interactables = [];
    private readonly Dictionary<InteractableId, bool> _interactableActive = new();
    private readonly HashSet<AetheryteId> _attunedAetherytes = [];
    private UiState _uiState = new(false, false, false, false, false, false, false, false, null);
    private int _freeInventorySlots;
    private readonly Dictionary<ItemId, int> _itemCounts = new();
    private readonly HashSet<ItemId> _equippedItems = [];
    private long _gil;
    private (string Reason, string? Detail)? _gilFailure;
    private int _gcSeals;
    private (string Reason, string? Detail)? _gcSealsFailure;
    private readonly Dictionary<ZoneId, TravelCapability> _travelCapabilities = new();
    private readonly Dictionary<uint, bool> _gearsetExistsForJob = new();

    // ---- observable recording ----
    public record StateRead(string Method, DateTimeOffset At) : AdapterCall(At);
    public CallLog<StateRead> RecordedReads { get; } = new();

    private void Record(string method) =>
        RecordedReads.Add(new StateRead(method, DateTimeOffset.UtcNow));

    // ---- Reset ----
    public void Reset() => RecordedReads.Clear();

    // ---- scriptable setters (all guarded by _lock) ----

    public void SetZone(ZoneId zone)                 { lock (_lock) _zone = zone; }
    public void SetPosition(WorldPosition position)  { lock (_lock) _position = position; }
    public void SetJob(JobId job, int level)          { lock (_lock) { _job = job; _jobLevel = level; _currentJobFailure = null; } }
    public void SetCurrentJobFail(string reason, string? detail = null) { lock (_lock) _currentJobFailure = (reason, detail); }
    public void ClearCurrentJobFail()                { lock (_lock) _currentJobFailure = null; }
    public void SetPositionFailure(string reason, string? detail = null) { lock (_lock) _currentPositionFailure = (reason, detail); }
    public void ClearPositionFailure()               { lock (_lock) _currentPositionFailure = null; }
    public void SetInCombat(bool v)                  { lock (_lock) { _inCombat = v; _inCombatFailure = null; } }
    public void SetInCombatFailure(string reason, string? detail = null) { lock (_lock) _inCombatFailure = (reason, detail); }
    public void ClearInCombatFailure()               { lock (_lock) _inCombatFailure = null; }
    public void SetMountState(MountState v)          { lock (_lock) _mountState = v; }
    public void SetDead(bool v)                      { lock (_lock) _dead = v; }
    public void SetDiving(bool v)                    { lock (_lock) _diving = v; }
    public void SetCasting(bool v)                   { lock (_lock) _casting = v; }
    public void SetCanMount(bool v)                  { lock (_lock) _canMount = v; }
    public void SetInstanceKind(InstanceKind v)      { lock (_lock) _instanceKind = v; }
    public void SetNewGamePlusState(NewGamePlusState v) { lock (_lock) { _ngpState = v; _ngpFailure = null; } }
    public void SetNewGamePlusStateFailure(string reason, string? detail = null) { lock (_lock) _ngpFailure = (reason, detail); }
    public void ClearNewGamePlusStateFailure() { lock (_lock) _ngpFailure = null; }
    public void SetUiState(UiState v)                { lock (_lock) _uiState = v; }
    public void SetFreeInventorySlots(int v)         { lock (_lock) _freeInventorySlots = v; }
    public void SetItemCount(ItemId item, int count) { lock (_lock) _itemCounts[item] = count; }

    public void SetItemEquipped(ItemId item, bool equipped)
    {
        lock (_lock)
        {
            if (equipped) _equippedItems.Add(item);
            else _equippedItems.Remove(item);
        }
    }
    public void SetGil(long v)                       { lock (_lock) { _gil = v; _gilFailure = null; } }
    public void SetGilFailure(string reason, string? detail = null) { lock (_lock) _gilFailure = (reason, detail); }
    public void ClearGilFailure()                    { lock (_lock) _gilFailure = null; }
    public void SetGrandCompanySeals(int v)          { lock (_lock) { _gcSeals = v; _gcSealsFailure = null; } }
    public void SetGrandCompanySealsFailure(string reason, string? detail = null) { lock (_lock) _gcSealsFailure = (reason, detail); }
    public void ClearGrandCompanySealsFailure()      { lock (_lock) _gcSealsFailure = null; }

    public void SetTravelCapability(ZoneId zone, TravelCapability cap)
        { lock (_lock) _travelCapabilities[zone] = cap; }

    public void SetGearsetExistsForJob(uint jobId, bool exists)
        { lock (_lock) _gearsetExistsForJob[jobId] = exists; }

    private bool _hasCoffers;
    public void SetHasCoffers(bool value) { lock (_lock) _hasCoffers = value; }

    public void AddNpc(NpcReference npc)    { lock (_lock) _npcs.Add(npc); }
    public void RemoveNpc(NpcId id)         { lock (_lock) _npcs.RemoveAll(n => n.Id == id); }
    public void ClearNpcs()                 { lock (_lock) _npcs.Clear(); }

    public void AddHostileActor(HostileActor actor)
        { lock (_lock) _hostileActors.Add(actor); }

    public void ClearHostileActors()
        { lock (_lock) _hostileActors.Clear(); }

    public void SetHostileActorsFailure(string reason, string? detail = null)
        { lock (_lock) _hostileActorsFailure = (reason, detail); }

    public void ClearHostileActorsFailure()
        { lock (_lock) _hostileActorsFailure = null; }

    public void AddInteractable(InteractableReference interactable)
    {
        lock (_lock)
        {
            _interactables.Add(interactable);
            _interactableActive[interactable.Id] = true;
        }
    }

    public void SetInteractableActive(InteractableId id, bool active)
        { lock (_lock) _interactableActive[id] = active; }

    public void ClearInteractables()
    {
        lock (_lock)
        {
            _interactables.Clear();
            _interactableActive.Clear();
        }
    }

    public void SetAetheryteAttuned(AetheryteId id, bool attuned)
    {
        lock (_lock)
        {
            if (attuned) _attunedAetherytes.Add(id);
            else _attunedAetherytes.Remove(id);
        }
    }

    public void SetAetherCurrentAttuned(uint dataId, bool attuned = true)
    {
        lock (_lock)
        {
            if (attuned) _aetherCurrents.Add(dataId);
            else _aetherCurrents.Remove(dataId);
        }
    }

    // ---- IGameStateProvider implementation ----

    public Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetPlayerState));
        lock (_lock)
        {
            var snap = new PlayerStateSnapshot(
                _position, _zone, _job, _jobLevel,
                _inCombat, _mountState, _diving, _casting, _dead, _canMount);
            return Task.FromResult<Result<PlayerStateSnapshot>>(Result.Ok(snap));
        }
    }

    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetPlayerPosition));
        lock (_lock)
        {
            if (_currentPositionFailure is { } pf)
                return Task.FromResult<Result<WorldPosition>>(Result.Fail<WorldPosition>(pf.Reason, pf.Detail));
            return Task.FromResult<Result<WorldPosition>>(Result.Ok(_position));
        }
    }

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetPlayerZone));
        lock (_lock) return Task.FromResult<Result<ZoneId>>(Result.Ok(_zone));
    }

    public Task<Result<bool>> IsPlayerInCombat(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerInCombat));
        lock (_lock)
        {
            if (_inCombatFailure is { } f)
                return Task.FromResult<Result<bool>>(Result.Fail<bool>(f.Reason, f.Detail));
            return Task.FromResult<Result<bool>>(Result.Ok(_inCombat));
        }
    }

    public Task<Result<MountState>> GetMountState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetMountState));
        lock (_lock) return Task.FromResult<Result<MountState>>(Result.Ok(_mountState));
    }

    public Task<Result<bool>> IsPlayerDiving(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerDiving));
        lock (_lock) return Task.FromResult<Result<bool>>(Result.Ok(_diving));
    }

    public Task<Result<bool>> IsPlayerCasting(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerCasting));
        lock (_lock) return Task.FromResult<Result<bool>>(Result.Ok(_casting));
    }

    public Task<Result<bool>> IsPlayerDead(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsPlayerDead));
        lock (_lock) return Task.FromResult<Result<bool>>(Result.Ok(_dead));
    }

    public Task<Result<JobId>> GetCurrentJob(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetCurrentJob));
        lock (_lock)
        {
            if (_currentJobFailure is { } f)
                return Task.FromResult<Result<JobId>>(Result.Fail<JobId>(f.Reason, f.Detail));
            return Task.FromResult<Result<JobId>>(Result.Ok(_job));
        }
    }

    public Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetJobLevel));
        lock (_lock)
        {
            var level = job == _job ? _jobLevel : 1;
            return Task.FromResult<Result<int>>(Result.Ok(level));
        }
    }

    public Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetCurrentInstanceKind));
        lock (_lock) return Task.FromResult<Result<InstanceKind>>(Result.Ok(_instanceKind));
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
        lock (_lock)
        {
            if (_ngpFailure is { } f)
                return Task.FromResult<Result<NewGamePlusState>>(Result.Fail<NewGamePlusState>(f.Reason, f.Detail));
            return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(_ngpState));
        }
    }

    public Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetNearbyNpcs));
        lock (_lock)
        {
            IReadOnlyList<NpcReference> nearby = _npcs.Where(n => n.DistanceToPlayer <= radius).ToArray();
            return Task.FromResult<Result<IReadOnlyList<NpcReference>>>(Result.Ok(nearby));
        }
    }

    public Task<Result<IReadOnlyList<HostileActor>>> GetHostileActors(float radius, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetHostileActors));
        lock (_lock)
        {
            if (_hostileActorsFailure is { } f)
                return Task.FromResult<Result<IReadOnlyList<HostileActor>>>(
                    Result.Fail<IReadOnlyList<HostileActor>>(f.Reason, f.Detail));
            IReadOnlyList<HostileActor> nearby = _hostileActors
                .Where(a => a.DistanceToPlayer <= radius)
                .ToArray();
            return Task.FromResult<Result<IReadOnlyList<HostileActor>>>(Result.Ok(nearby));
        }
    }

    public Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(FindNpc));
        lock (_lock)
        {
            NpcReference? found = _npcs.FirstOrDefault(n => n.Id == npc);
            return Task.FromResult<Result<NpcReference?>>(Result.Ok(found));
        }
    }

    public Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetNearbyInteractables));
        lock (_lock)
        {
            IReadOnlyList<InteractableReference> nearby =
                _interactables.Where(i => i.DistanceToPlayer <= radius).ToArray();
            return Task.FromResult<Result<IReadOnlyList<InteractableReference>>>(Result.Ok(nearby));
        }
    }

    public Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(FindInteractable));
        lock (_lock)
        {
            InteractableReference? found = _interactables.FirstOrDefault(i => i.Id == obj);
            return Task.FromResult<Result<InteractableReference?>>(Result.Ok(found));
        }
    }

    public Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsInteractableActive));
        lock (_lock)
        {
            var active = _interactableActive.TryGetValue(obj, out var v) && v;
            return Task.FromResult<Result<bool>>(Result.Ok(active));
        }
    }

    public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsAetheryteAttuned));
        lock (_lock) return Task.FromResult<Result<bool>>(Result.Ok(_attunedAetherytes.Contains(aetheryte)));
    }

    public Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetAttunedAetherytes));
        lock (_lock)
        {
            IReadOnlyList<AetheryteId> list = _attunedAetherytes.ToArray();
            return Task.FromResult<Result<IReadOnlyList<AetheryteId>>>(Result.Ok(list));
        }
    }

    public Task<Result<UiState>> GetUiState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetUiState));
        lock (_lock) return Task.FromResult<Result<UiState>>(Result.Ok(_uiState));
    }

    public Task<Result<int>> GetFreeInventorySlots(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetFreeInventorySlots));
        lock (_lock) return Task.FromResult<Result<int>>(Result.Ok(_freeInventorySlots));
    }

    public Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetItemCount));
        lock (_lock)
        {
            _itemCounts.TryGetValue(item, out var count);
            return Task.FromResult<Result<int>>(Result.Ok(count));
        }
    }

    public Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsItemEquipped));
        lock (_lock) return Task.FromResult<Result<bool>>(Result.Ok(_equippedItems.Contains(item)));
    }

    public Task<Result<long>> GetGil(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetGil));
        lock (_lock)
        {
            if (_gilFailure is { } f)
                return Task.FromResult<Result<long>>(Result.Fail<long>(f.Reason, f.Detail));
            return Task.FromResult<Result<long>>(Result.Ok(_gil));
        }
    }

    public Task<Result<int>> GetGrandCompanySeals(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetGrandCompanySeals));
        lock (_lock)
        {
            if (_gcSealsFailure is { } f)
                return Task.FromResult<Result<int>>(Result.Fail<int>(f.Reason, f.Detail));
            return Task.FromResult<Result<int>>(Result.Ok(_gcSeals));
        }
    }

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(GetTravelCapability));
        lock (_lock)
        {
            var cap = _travelCapabilities.TryGetValue(destination, out var v)
                ? v
                : new TravelCapability(true, null, false, false, true, false, 0);
            return Task.FromResult<Result<TravelCapability>>(Result.Ok(cap));
        }
    }

    public AethernetId? LastAethernetDestination { get; set; }

    public Task<Result<AethernetId?>> GetLastAethernetDestination(CancellationToken ct)
        => Task.FromResult<Result<AethernetId?>>(Result.Ok(LastAethernetDestination));

    public Task<Result<bool>> GearsetExistsForJob(uint jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var exists = _gearsetExistsForJob.TryGetValue(jobId, out var v) && v;
            return Task.FromResult<Result<bool>>(Result.Ok(exists));
        }
    }

    public Task<Result<bool>> HasCoffers(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(HasCoffers));
        lock (_lock)
            return Task.FromResult<Result<bool>>(Result.Ok(_hasCoffers));
    }

    public Task<Result<bool>> IsAetherCurrentAttuned(uint aetherCurrentDataId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(IsAetherCurrentAttuned));
        lock (_lock)
            return Task.FromResult<Result<bool>>(Result.Ok(_aetherCurrents.Contains(aetherCurrentDataId)));
    }

    public Task<Result<bool>> NpcExistsNearby(uint dataId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(NpcExistsNearby));
        lock (_lock)
        {
            var npcFound = _npcs.Any(n => n.Id.Value == dataId);
            if (npcFound) return Task.FromResult<Result<bool>>(Result.Ok(true));
            var objFound = _interactables.Any(i => i.Id.Value == dataId);
            return Task.FromResult<Result<bool>>(Result.Ok(objFound));
        }
    }
}