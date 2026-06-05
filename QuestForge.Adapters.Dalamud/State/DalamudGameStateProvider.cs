using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.Items;
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
        bool canMount;
        unsafe
        {
            var am = ActionManager.Instance();
            canMount = am != null && am->GetActionStatus(ActionType.GeneralAction, 9) == 0;
        }

        return Task.FromResult<Result<PlayerStateSnapshot>>(
            Result.Ok(new PlayerStateSnapshot(
                new WorldPosition(p.X, p.Y, p.Z),
                zone, job, level, inCombat, mountState, diving, casting, dead, canMount)));
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

    public unsafe Task<Result<NewGamePlusState>> GetNewGamePlusState(CancellationToken ct)
    {
        try
        {
            // NG+ detection gate 1: the unlock quest (id 3759) must be complete.
            // QuestManager uses the lower 16 bits as the internal key.
            // TODO verify in-game: QuestManager.IsQuestComplete(3759) — is 3759 the correct internal id?
            const ushort NgPlusUnlockQuestInternal = 3759;
            if (!QuestManager.IsQuestComplete(NgPlusUnlockQuestInternal))
                return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(new NewGamePlusState(false, null, false)));

            // NG+ detection gate 2: QuestRedoHud agent must be active.
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(new NewGamePlusState(false, null, false)));

            var agent = agentModule->GetAgentByInternalId(AgentId.QuestRedoHud);
            if (agent == null || !agent->IsAgentActive())
                return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(new NewGamePlusState(false, null, false)));

            // NG+ detection gate 3: QuestRedoHud addon must be present with AtkValues[0].UInt ∈ {0,2,3,4}.
            // AtkValues[0] encodes the replay mode/state:
            //   0 = chapter select (active), 2 = replay running, 3 = replay paused/suspended, 4 = chapter complete
            // Value 1 or others = not in an active replay.
            // TODO verify in-game: exact AtkValues[0] mapping for Active vs Suspended states.
            var addon = _svc.GameGui.GetAddonByName("QuestRedoHud");
            if (addon.IsNull)
                return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(new NewGamePlusState(false, null, false)));

            var atkBase = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address;
            if (atkBase == null || atkBase->AtkValuesCount < 1)
                return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(new NewGamePlusState(false, null, false)));

            var stateValue = atkBase->AtkValues[0].UInt;
            // Valid active-replay states: 0,2,3,4 — anything else means NG+ UI is not in replay mode.
            if (stateValue != 0 && stateValue != 2 && stateValue != 3 && stateValue != 4)
                return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(new NewGamePlusState(false, null, false)));

            // IsSuspended: AtkValues[0] == 3 is the paused/suspended state.
            // TODO verify in-game: confirm stateValue==3 maps to suspended (player paused replay).
            var isSuspended = stateValue == 3;

            // VALIDATED reads only: IsActive (gates above) + IsSuspended (AtkValues[0]==3).
            // CurrentChapter and ActiveReplayQuestId are intentionally left null: the agent +44/+46
            // memory reads proved unreliable in-game (no candidate offset held the active-quest
            // RowId), and chaining will be driven by the quest schema's next-quest field + the
            // scheduler rather than by reading the QuestRedoHud. So the engine only needs IsActive
            // (to bypass the lying isQuestComplete gate) + IsSuspended.
            return Task.FromResult<Result<NewGamePlusState>>(
                Result.Ok(new NewGamePlusState(
                    IsActive: true,
                    CurrentChapter: null,
                    IsSuspended: isSuspended,
                    ActiveReplayQuestId: null)));
        }
        catch
        {
            // Never throw — return safe default on any read failure.
            return Task.FromResult<Result<NewGamePlusState>>(Result.Ok(new NewGamePlusState(false, null, false)));
        }
    }

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
        // UIState->IsAetheryteUnlocked is correct for both main aetherytes and city aethernet
        // sub-shards: the bit is set only when the player physically attunes the specific crystal,
        // NOT when the parent aetheryte group becomes accessible. Telepo.TeleportList was explored
        // as an alternative but is only populated when the teleport window is opened, making it
        // unreliable for postcondition checks during engine execution.
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

    public unsafe Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var im = InventoryManager.Instance();
        if (im is null)
            return Task.FromResult<Result<bool>>(
                Result.Fail<bool>("noInventoryManager", "InventoryManager.Instance() returned null"));

        for (var i = 0; i < 14; i++)
        {
            var slot = im->GetInventorySlot(InventoryType.EquippedItems, i);
            if (slot is not null && slot->ItemId == item.Value)
                return Task.FromResult<Result<bool>>(Result.Ok(true));
        }
        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    public unsafe Task<Result<long>> GetGil(CancellationToken ct)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return Task.FromResult<Result<long>>(
                Result.Fail<long>("noInventoryManager", "InventoryManager.Instance() returned null"));
        // VERIFY IN-GAME: InventoryManager.GetGil() is the correct API for the player's gil balance.
        // It is a well-known ClientStructs member and should be stable, but confirm the value matches
        // the in-game wallet display (top-right HUD element).
        var gil = mgr->GetGil();
        return Task.FromResult<Result<long>>(Result.Ok((long)gil));
    }

    public unsafe Task<Result<int>> GetGrandCompanySeals(CancellationToken ct)
    {
        var ps = PlayerState.Instance();
        if (ps == null)
            return Task.FromResult<Result<int>>(
                Result.Fail<int>("noPlayerState", "PlayerState.Instance() returned null"));

        // GrandCompany byte: 0=none, 1=Maelstrom, 2=Twin Adder, 3=Immortal Flames.
        var gc = ps->GrandCompany;
        if (gc == 0)
            return Task.FromResult<Result<int>>(
                Result.Fail<int>("noGrandCompany", "player has no Grand Company affiliation"));

        // VERIFY IN-GAME: confirm the GC seal item ids: Maelstrom=20, Twin Adder=21,
        // Immortal Flames=22. These are the standard Seals currency item ids from game data.
        // Cross-reference with the Lumina Item sheet (RowId 20/21/22 should be the seal items).
        uint sealItemId = gc switch
        {
            1 => 20u, // Maelstrom Seal
            2 => 21u, // Twin Adder Seal
            3 => 22u, // Immortal Flames Seal
            _ => 0u
        };

        if (sealItemId == 0)
            return Task.FromResult<Result<int>>(
                Result.Fail<int>("unknownGrandCompany", $"unrecognised GC id {gc}"));

        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return Task.FromResult<Result<int>>(
                Result.Fail<int>("noInventoryManager", "InventoryManager.Instance() returned null"));

        // VERIFY IN-GAME: confirm GetInventoryItemCount(sealItemId) returns the seal wallet balance
        // and not an inventory slot count. Seals are a currency, not a stack item, so the underlying
        // implementation should route to the currency wallet rather than a bag search.
        var count = mgr->GetInventoryItemCount(sealItemId, isHq: false, checkEquipped: false, checkArmory: false);
        return Task.FromResult<Result<int>>(Result.Ok(count));
    }

    public Task<Result<AethernetId?>> GetLastAethernetDestination(CancellationToken ct)
        // Phase 7 placeholder: real TelepotTown hook implementation deferred
        => Task.FromResult<Result<AethernetId?>>(Result.Ok((AethernetId?)null));

    public unsafe Task<Result<IReadOnlyList<HostileActor>>> GetHostileActors(float radius, CancellationToken ct)
    {
        var local = _svc.ObjectTable.LocalPlayer;
        var result = new List<HostileActor>();

        // Build a fast set of entity ids on the player's enmity (Hater) list.
        var haterSet = new HashSet<uint>();
        var uiState  = UIState.Instance();
        if (uiState != null)
        {
            // Hater is a FixedSizeArray32 — clamp defensively to its capacity.
            var haterCount = uiState->Hater.HaterCount;
            if (haterCount > 32) haterCount = 32;
            for (var i = 0; i < haterCount; i++)
                haterSet.Add(uiState->Hater.Haters[i].EntityId);
        }

        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null) continue;
            if (obj.ObjectKind is not ObjectKind.BattleNpc) continue;

            var dist = local is null
                ? float.PositiveInfinity
                : Vector3.Distance(local.Position, obj.Position);

            if (dist > radius) continue;

            // ClientStructs pointer for targetable + nameplate icon checks.
            var goPtr = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            var isTargetable = goPtr != null && goPtr->GetIsTargetable();
            var hasQuestMarker = goPtr != null && goPtr->NamePlateIconId != 0;

            var isDead             = obj is IBattleChara bc && bc.CurrentHp == 0;
            var isTargetingPlayer  = local is not null
                && obj is IBattleChara bcTarget
                && bcTarget.TargetObjectId == local.GameObjectId;
            var onEnmityList       = haterSet.Contains(obj.EntityId);

            result.Add(new HostileActor(
                Id:                new ActorId(obj.GameObjectId),
                DataId:            obj.BaseId,
                Position:          new WorldPosition(obj.Position.X, obj.Position.Y, obj.Position.Z),
                DistanceToPlayer:  dist,
                IsTargetable:      isTargetable,
                IsDead:            isDead,
                IsTargetingPlayer: isTargetingPlayer,
                OnPlayerEnmityList: onEnmityList,
                HasQuestMarker:    hasQuestMarker));
        }

        return Task.FromResult<Result<IReadOnlyList<HostileActor>>>(
            Result.Ok<IReadOnlyList<HostileActor>>(result));
    }

    public Task<Result<TravelCapability>> GetTravelCapability(ZoneId destination, CancellationToken ct)
    {
        // CanFly: PlayerState.CanFly is set by the game on zone load and accounts for Aether Current unlocks.
        // CanMount: approximated as "not in combat" — combat is the primary in-zone mount restriction.
        // All other fields remain Phase 6 placeholders (teleport cost, nearest aetheryte, etc.).
        unsafe
        {
            var ps = PlayerState.Instance();
            var canFly   = ps != null && ps->CanFly;
            // TODO: this field is currently unused; PlayerStateSnapshot.CanMount is the per-tick gate.
            // Reconsider when GetTravelCapability gets real consumers (§8 R5 in MOUNT_SUPPORT_PLAN.md).
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

    public unsafe Task<Result<bool>> GearsetExistsForJob(uint jobId, CancellationToken ct)
    {
        var gsm = RaptureGearsetModule.Instance();
        if (gsm == null)
            return Task.FromResult<Result<bool>>(Result.Ok(false));

        for (var i = 0; i < 100; i++)
        {
            if (!gsm->IsValidGearset(i)) continue;
            var entry = gsm->GetGearset(i);
            if (entry == null) continue;
            if (entry->ClassJob == (byte)jobId)
                return Task.FromResult<Result<bool>>(Result.Ok(true));
        }

        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public unsafe Task<Result<bool>> HasCoffers(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var im = InventoryManager.Instance();
        if (im is null)
            return Task.FromResult<Result<bool>>(
                Result.Fail<bool>("inventoryManagerUnavailable", "InventoryManager.Instance() returned null"));

        var itemSheet = _svc.DataManager.GetExcelSheet<Item>();
        if (itemSheet is null)
            return Task.FromResult<Result<bool>>(Result.Ok(false));

        foreach (var bagType in PlayerBags)
        {
            var container = im->GetInventoryContainer(bagType);
            if (container is null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0) continue;
                if (!itemSheet.TryGetRow(slot->ItemId, out var row)) continue;
                if (CofferIdentifier.IsCoffer(row.ItemAction.RowId, row.ItemUICategory.RowId))
                    return Task.FromResult<Result<bool>>(Result.Ok(true));
            }
        }

        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    public unsafe Task<Result<bool>> IsAetherCurrentAttuned(uint aetherCurrentDataId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var playerState = PlayerState.Instance();
        if (playerState == null)
            return Task.FromResult<Result<bool>>(
                Result.Fail<bool>("noPlayerState", "PlayerState.Instance() returned null"));
        return Task.FromResult<Result<bool>>(
            Result.Ok(playerState->IsAetherCurrentUnlocked(aetherCurrentDataId)));
    }

    public Task<Result<bool>> NpcExistsNearby(uint dataId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null) continue;
            if (obj.BaseId == dataId)
                return Task.FromResult<Result<bool>>(Result.Ok(true));
        }
        return Task.FromResult<Result<bool>>(Result.Ok(false));
    }

    public unsafe Task<Result<int>> GetEquippedItemLevelForSlot(int slotIndex, CancellationToken ct)
    {
        if (slotIndex < 0 || slotIndex > 13)
            return Task.FromResult<Result<int>>(
                Result.Fail<int>("slotOutOfRange", $"slotIndex {slotIndex} is out of range 0-13"));

        var im = InventoryManager.Instance();
        if (im is null)
            return Task.FromResult<Result<int>>(
                Result.Fail<int>("noInventoryManager", "InventoryManager.Instance() returned null"));

        var slot = im->GetInventorySlot(InventoryType.EquippedItems, slotIndex);
        if (slot is null || slot->ItemId == 0)
            return Task.FromResult<Result<int>>(Result.Ok(0));

        var itemSheet = _svc.DataManager.GetExcelSheet<Item>();
        if (itemSheet is null || !itemSheet.TryGetRow(slot->ItemId, out var row))
            return Task.FromResult<Result<int>>(Result.Ok(0));

        return Task.FromResult<Result<int>>(Result.Ok((int)row.LevelItem.RowId));
    }
}