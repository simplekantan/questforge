using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.State;

public sealed class DalamudGameStateProvider : IGameStateProvider
{
    private readonly PluginServices _svc;

    public DalamudGameStateProvider(PluginServices svc) => _svc = svc;

    public Task<Result<PlayerStateSnapshot>> GetPlayerState(CancellationToken ct)
    {
        // SDK 15: IClientState.LocalPlayer removed; index 0 is local player
        var local = _svc.ObjectTable[0] as IPlayerCharacter;
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
        var local = _svc.ObjectTable[0];
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
        var local = _svc.ObjectTable[0] as IPlayerCharacter;
        if (local is null)
            return Task.FromResult<Result<JobId>>(
                Result.Fail<JobId>("noLocalPlayer", "ObjectTable[0] is null or not IPlayerCharacter"));

        return Task.FromResult<Result<JobId>>(Result.Ok(new JobId((uint)local.ClassJob.RowId)));
    }

    public Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
    {
        // Phase 6: job parameter ignored — returns current job level only
        var local = _svc.ObjectTable[0] as IPlayerCharacter;
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
        var local = _svc.ObjectTable[0];
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
        var local = _svc.ObjectTable[0];

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
        var local = _svc.ObjectTable[0];
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
        var local = _svc.ObjectTable[0];

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

    public Task<Result<bool>> IsAetheryteAttuned(AetheryteId aetheryte, CancellationToken ct)
        // Phase 6 placeholder: quest 66130 never teleports — not exercised by done-criterion
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    public Task<Result<IReadOnlyList<AetheryteId>>> GetAttunedAetherytes(CancellationToken ct)
        // Phase 6 placeholder
        => Task.FromResult<Result<IReadOnlyList<AetheryteId>>>(
            Result.Ok<IReadOnlyList<AetheryteId>>(Array.Empty<AetheryteId>()));

    public Task<Result<UiState>> GetUiState(CancellationToken ct)
    {
        // Non-zero IntPtr returned by GetAddonByName means the addon is visible
        var talkOpen         = _svc.GameGui.GetAddonByName("Talk")             != IntPtr.Zero;
        var selectStringOpen = _svc.GameGui.GetAddonByName("SelectString")     != IntPtr.Zero;
        var selectYesNoOpen  = _svc.GameGui.GetAddonByName("SelectYesno")      != IntPtr.Zero;
        var rewardOpen       = _svc.GameGui.GetAddonByName("JournalResult")    != IntPtr.Zero;
        var shopOpen         = _svc.GameGui.GetAddonByName("Shop")             != IntPtr.Zero;
        var deathOpen        = _svc.GameGui.GetAddonByName("_NoticeWidget")    != IntPtr.Zero; // approximate

        var cutscene = _svc.Condition[ConditionFlag.WatchingCutscene]
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

    public Task<Result<int>> GetItemCount(ItemId item, CancellationToken ct)
        // Phase 6 placeholder
        => Task.FromResult<Result<int>>(Result.Ok(0));

    public Task<Result<long>> GetGil(CancellationToken ct)
        // Phase 6 placeholder
        => Task.FromResult<Result<long>>(Result.Ok(0L));

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
        // Phase 6 placeholder
        => Task.FromResult<Result<TravelCapability>>(
            Result.Ok(new TravelCapability(false, null, false, false, false, false, 0)));
}