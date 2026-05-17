using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.State;

public sealed class DalamudGameStateProvider : IGameStateProvider
{
    private readonly PluginServices _svc;

    public DalamudGameStateProvider(PluginServices svc) => _svc = svc;

    public Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;
        if (local is null)
            return Task.FromResult<Result<PlayerStateSnapshot>>(
                Result.Fail<PlayerStateSnapshot>("noLocalPlayer", "ObjectTable[0] is null or not IPlayerCharacter"));

        var p = local.Position;
        var zone = new ZoneId(_svc.ClientState.TerritoryType);
        var job = new JobId((uint)local.ClassJob.RowId);
        var level = local.Level;
        var inCombat = _svc.Condition[ConditionFlag.InCombat];
        var mounted = _svc.Condition[ConditionFlag.Mounted] || _svc.Condition[ConditionFlag.RidingPillion];
        var flying = _svc.Condition[ConditionFlag.InFlight];
        var diving = _svc.Condition[ConditionFlag.Diving];
        var casting = _svc.Condition[ConditionFlag.Casting];
        var dead = _svc.Condition[ConditionFlag.Unconscious];
        var mountState = flying ? MountState.Flying : mounted ? MountState.Mounted : MountState.Dismounted;

        return Task.FromResult<Result<PlayerStateSnapshot>>(
            Result.Ok(new PlayerStateSnapshot(
                new WorldPosition(p.X, p.Y, p.Z),
                zone, job, level, inCombat, mountState, diving, casting, dead)));
    }

    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;
        if (local is null)
            return Task.FromResult<Result<WorldPosition>>(
                Result.Fail<WorldPosition>("noLocalPlayer", "ObjectTable[0] is null"));

        var p = local.Position;
        return Task.FromResult<Result<WorldPosition>>(Result.Ok(new WorldPosition(p.X, p.Y, p.Z)));
    }

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
        => Task.FromResult<Result<ZoneId>>(Result.Ok(new ZoneId(_svc.ClientState.TerritoryType)));

    public Task<Result<bool>> IsPlayerInCombat(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(_svc.Condition[ConditionFlag.InCombat]));

    public Task<Result<MountState>> GetMountState(CancellationToken ct)
    {
        var mounted = _svc.Condition[ConditionFlag.Mounted] || _svc.Condition[ConditionFlag.RidingPillion];
        var flying = _svc.Condition[ConditionFlag.InFlight];
        var state = flying ? MountState.Flying : mounted ? MountState.Mounted : MountState.Dismounted;
        return Task.FromResult<Result<MountState>>(Result.Ok(state));
    }

    public Task<Result<bool>> IsPlayerDiving(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(_svc.Condition[ConditionFlag.Diving]));

    public Task<Result<bool>> IsPlayerCasting(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(_svc.Condition[ConditionFlag.Casting]));

    public Task<Result<bool>> IsPlayerDead(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(_svc.Condition[ConditionFlag.Unconscious]));

    public Task<Result<JobId>> GetCurrentJob(CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;
        if (local is null)
            return Task.FromResult<Result<JobId>>(
                Result.Fail<JobId>("noLocalPlayer", "ObjectTable[0] is null or not IPlayerCharacter"));

        return Task.FromResult<Result<JobId>>(Result.Ok(new JobId((uint)local.ClassJob.RowId)));
    }

    public Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
    {
        // Phase 6: job parameter ignored — returns current job level only
        var local = _svc.ObjectTable.LocalPlayer;
        if (local is null)
            return Task.FromResult<Result<int>>(
                Result.Fail<int>("noLocalPlayer", "ObjectTable[0] is null or not IPlayerCharacter"));

        return Task.FromResult<Result<int>>(Result.Ok((int)local.Level));
    }

    public Task<Result<InstanceKind>> GetCurrentInstanceKind(CancellationToken ct)
    {
        if (_svc.Condition[ConditionFlag.BoundByDuty] || _svc.Condition[ConditionFlag.BoundByDuty56])
        {
            var kind = ClassifyInstanceKind(_svc.ClientState.TerritoryType);
            return Task.FromResult<Result<InstanceKind>>(Result.Ok(kind));
        }

        return Task.FromResult<Result<InstanceKind>>(Result.Ok(InstanceKind.None));
    }

    // Phase 6: full Lumina table lookup deferred; returns Other for any instanced territory
    private static InstanceKind ClassifyInstanceKind(uint territoryType) => InstanceKind.Other;

    public Task<Result<DutyAvailability>> GetDutyAvailability(DutyId duty, CancellationToken ct)
        // Phase 6 placeholder
        => Task.FromResult<Result<DutyAvailability>>(
            Result.Ok(new DutyAvailability(false, false, false, false, 0, 0, null)));

    public Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct)
        // Phase 6 placeholder
        => Task.FromResult<Result<NewGamePlusState>>(
            Result.Ok(new NewGamePlusState(false, null, false)));

    public Task<Result<IReadOnlyList<NpcReference>>> GetNearbyNpcs(float radius, CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;
        var result = new List<NpcReference>();

        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null) continue;
            if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;

            var dist = local is null
                ? float.PositiveInfinity
                : Vector3.Distance(local.Position, obj.Position);

            if (dist > radius) continue;

            result.Add(new NpcReference(
                new NpcId(obj.BaseId),
                new WorldPosition(obj.Position.X, obj.Position.Y, obj.Position.Z),
                dist));
        }

        return Task.FromResult<Result<IReadOnlyList<NpcReference>>>(Result.Ok<IReadOnlyList<NpcReference>>(result));
    }

    public Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;

        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null || obj.BaseId != npc.Value) continue;
            if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;

            var dist = local is null
                ? float.PositiveInfinity
                : Vector3.Distance(local.Position, obj.Position);

            return Task.FromResult<Result<NpcReference?>>(
                Result.Ok<NpcReference?>(new NpcReference(
                    npc,
                    new WorldPosition(obj.Position.X, obj.Position.Y, obj.Position.Z),
                    dist)));
        }

        // Not found — return null (not Failure; absence is an expected outcome)
        return Task.FromResult<Result<NpcReference?>>(Result.Ok<NpcReference?>(null));
    }

    public Task<Result<IReadOnlyList<InteractableReference>>> GetNearbyInteractables(float radius, CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;
        var result = new List<InteractableReference>();

        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null) continue;
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure or ObjectKind.Aetheryte)) continue;

            var dist = local is null
                ? float.PositiveInfinity
                : Vector3.Distance(local.Position, obj.Position);

            if (dist > radius) continue;

            var kind = obj.ObjectKind switch
            {
                ObjectKind.Aetheryte => InteractableKind.Aetheryte,
                ObjectKind.Treasure  => InteractableKind.ItemPickup,
                _                    => InteractableKind.Other
            };

            result.Add(new InteractableReference(
                new InteractableId(obj.BaseId),
                new WorldPosition(obj.Position.X, obj.Position.Y, obj.Position.Z),
                dist,
                kind));
        }

        return Task.FromResult<Result<IReadOnlyList<InteractableReference>>>(
            Result.Ok<IReadOnlyList<InteractableReference>>(result));
    }

    public Task<Result<InteractableReference?>> FindInteractable(InteractableId obj, CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;

        foreach (var entry in _svc.ObjectTable)
        {
            if (entry is null || entry.BaseId != obj.Value) continue;
            if (entry.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure or ObjectKind.Aetheryte)) continue;

            var dist = local is null
                ? float.PositiveInfinity
                : Vector3.Distance(local.Position, entry.Position);

            var kind = entry.ObjectKind switch
            {
                ObjectKind.Aetheryte => InteractableKind.Aetheryte,
                ObjectKind.Treasure  => InteractableKind.ItemPickup,
                _                    => InteractableKind.Other
            };

            return Task.FromResult<Result<InteractableReference?>>(
                Result.Ok<InteractableReference?>(new InteractableReference(
                    obj,
                    new WorldPosition(entry.Position.X, entry.Position.Y, entry.Position.Z),
                    dist,
                    kind)));
        }

        // Not found — return null (absence is an expected outcome)
        return Task.FromResult<Result<InteractableReference?>>(Result.Ok<InteractableReference?>(null));
    }

    public Task<Result<bool>> IsInteractableActive(InteractableId obj, CancellationToken ct)
    {
        foreach (var entry in _svc.ObjectTable)
        {
            if (entry is null || entry.BaseId != obj.Value) continue;
            if (entry.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure or ObjectKind.Aetheryte)) continue;
            return Task.FromResult<Result<bool>>(Result.Ok(true));
        }

        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    public unsafe Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
    {
        // WHY: UIState tracks aetheryte attunement (not PlayerState as the spec initially guessed).
        // Confirmed against Questionable's AetheryteFunctions.cs: UIState.Instance()->IsAetheryteUnlocked(uint).
        var uiState = UIState.Instance();
        if (uiState == null)
            return Task.FromResult<Result<bool>>(Result.Fail<bool>("noUiState", "UIState.Instance() returned null"));

        return Task.FromResult<Result<bool>>(Result.Ok(uiState->IsAetheryteUnlocked(aetheryte.Value)));
    }

    public unsafe Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct)
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return Task.FromResult<Result<IReadOnlyList<AetheryteId>>>(
                Result.Fail<IReadOnlyList<AetheryteId>>("noUiState", "UIState.Instance() returned null"));

        var sheet = _svc.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
        var attuned = new List<AetheryteId>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                if (uiState->IsAetheryteUnlocked(row.RowId))
                    attuned.Add(new AetheryteId(row.RowId));
            }
        }
        return Task.FromResult<Result<IReadOnlyList<AetheryteId>>>(Result.Ok<IReadOnlyList<AetheryteId>>(attuned));
    }

    public Task<Result<UiState>> GetUiState(CancellationToken ct)
    {
        var talkOpen         = !_svc.GameGui.GetAddonByName("Talk").IsNull;
        var selectStringOpen = !_svc.GameGui.GetAddonByName("SelectString").IsNull;
        var selectYesNoOpen  = !_svc.GameGui.GetAddonByName("SelectYesno").IsNull;
        var rewardOpen       = !_svc.GameGui.GetAddonByName("JournalResult").IsNull;
        var shopOpen         = !_svc.GameGui.GetAddonByName("Shop").IsNull;
        var deathOpen        = !_svc.GameGui.GetAddonByName("_NoticeWidget").IsNull; // approximate

        var cutscene = _svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]   // skippable
                    || _svc.Condition[ConditionFlag.WatchingCutscene]
                    || _svc.Condition[ConditionFlag.WatchingCutscene78];
        var loading  = _svc.Condition[ConditionFlag.BetweenAreas]
                    || _svc.Condition[ConditionFlag.BetweenAreas51];

        return Task.FromResult<Result<UiState>>(Result.Ok(new UiState(
            DialogueOpen:       talkOpen,
            CutscenePlaying:    cutscene,
            LoadingZone:        loading,
            YesNoPromptOpen:    selectYesNoOpen,
            SelectStringOpen:   selectStringOpen,
            RewardSelectionOpen: rewardOpen,
            ShopOpen:           shopOpen,
            DeathPromptOpen:    deathOpen,
            CurrentDialogue:    null)));   // Phase 6: dialogue parsing deferred
    }

    public Task<Result<int>> GetFreeInventorySlots(CancellationToken ct)
        // Phase 6 placeholder: quest 66130 doesn't need inventory checks
        => Task.FromResult<Result<int>>(Result.Ok(35));

    public unsafe Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return Task.FromResult<Result<int>>(Result.Fail<int>("noInventoryManager", "InventoryManager.Instance() returned null"));
        // native function routes by id range: Bag0-3 for normal items, KeyItems for id >= 2000000
        var count = mgr->GetInventoryItemCount(item.Value, isHq: false, checkEquipped: false, checkArmory: false);
        return Task.FromResult<Result<int>>(Result.Ok(count));
    }

    public Task<Result<long>> GetGil(CancellationToken ct)
        // Phase 6 placeholder
        => Task.FromResult<Result<long>>(Result.Ok(0L));

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
    {
        // CanFly: PlayerState.CanFly is set by the game on zone load and accounts for Aether Current unlocks.
        // CanMount: approximated as "not in combat" — combat is the primary in-zone mount restriction.
        // All other fields remain Phase 6 placeholders (teleport cost, nearest aetheryte, etc.).
        unsafe
        {
            var ps = PlayerState.Instance();
            var canFly   = ps != null && ps->CanFly;
            var canMount = !_svc.Condition[ConditionFlag.InCombat];
            return Task.FromResult<Result<TravelCapability>>(
                Result.Ok(new TravelCapability(
                    CanTeleport:           false,
                    NearestAttuned:        null,
                    RequiresOnFootSegment: false,
                    CanFly:                canFly,
                    CanMount:              canMount,
                    CanDive:               false,
                    EstimatedGilCost:      0)));
        }
    }
}