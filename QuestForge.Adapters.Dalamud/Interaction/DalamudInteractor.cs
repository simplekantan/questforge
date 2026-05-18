using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Interaction;

public sealed class DalamudInteractor : IInteractor
{
    private readonly PluginServices _svc;
    private DateTimeOffset _lastAdvanceAt  = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInteractAt = DateTimeOffset.MinValue;

    // ~15 game ticks at 60 fps ≈ 250 ms; prevents spamming dialogue clicks every frame
    private static readonly TimeSpan AdvanceThrottle  = TimeSpan.FromMilliseconds(250);
    // Re-trigger NPC interaction at most once per second; the game needs time to open the addon
    private static readonly TimeSpan InteractThrottle = TimeSpan.FromMilliseconds(1000);

    public DalamudInteractor(PluginServices svc) => _svc = svc;

    public Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastInteractAt < InteractThrottle)
            return Task.FromResult<Result<InteractOutcome>>(Result.Ok(InteractOutcome.AlreadyInteracted));

        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null || obj.BaseId != npc.Value) continue;
            if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc or ObjectKind.Aetheryte)) continue;

            _lastInteractAt = DateTimeOffset.UtcNow;
            _svc.TargetManager.Target = obj;
            unsafe
            {
                var ts = TargetSystem.Instance();
                if (ts->Target != null)
                    ts->InteractWithObject(ts->Target, checkLineOfSight: false);
            }
            return Task.FromResult<Result<InteractOutcome>>(Result.Ok(InteractOutcome.DialogueOpened));
        }
        return Task.FromResult<Result<InteractOutcome>>(Result.Ok(InteractOutcome.NpcNotFound));
    }

    public Task<Result<InteractOutcome>> InteractWithObject(InteractableId obj, CancellationToken ct)
    {
        foreach (var entry in _svc.ObjectTable)
        {
            if (entry is null || entry.BaseId != obj.Value) continue;
            if (entry.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure or ObjectKind.Aetheryte)) continue;

            _svc.TargetManager.Target = entry;
            unsafe
            {
                var ts = TargetSystem.Instance();
                if (ts->Target != null)
                    ts->InteractWithObject(ts->Target, checkLineOfSight: false);
            }
            return Task.FromResult<Result<InteractOutcome>>(Result.Ok(InteractOutcome.DialogueOpened));
        }
        return Task.FromResult<Result<InteractOutcome>>(Result.Ok(InteractOutcome.ObjectNotFound));
    }

    public Task<Result<DialogueOutcome>> AdvanceDialogue(CancellationToken ct)
    {
        var addonPtr = _svc.GameGui.GetAddonByName("Talk");
        // IsReady is the preferred check (per AtkUnitBase docs); IsVisible can be false
        // even when the addon is rendering and accepting events.
        if (addonPtr.IsNull || !addonPtr.IsReady)
            return Task.FromResult<Result<DialogueOutcome>>(Result.Ok(DialogueOutcome.NoActiveDialogue));

        if (DateTimeOffset.UtcNow - _lastAdvanceAt < AdvanceThrottle)
            return Task.FromResult<Result<DialogueOutcome>>(Result.Ok(DialogueOutcome.Advanced));

        _lastAdvanceAt = DateTimeOffset.UtcNow;
        unsafe
        {
            var addon = (AtkUnitBase*)addonPtr.Address;
            var evt = stackalloc AtkEvent[1];
            evt->Node     = null;
            evt->Target   = (AtkEventTarget*)AtkStage.Instance();
            evt->Listener = (AtkEventListener*)addon;
            evt->State    = new AtkEventState { StateFlags = (AtkEventStateFlags)132 };
            var data = stackalloc AtkEventData[1];
            addon->ReceiveEvent(AtkEventType.MouseDown,  0, evt, data);
            addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
            addon->ReceiveEvent(AtkEventType.MouseUp,    0, evt, data);
        }
        return Task.FromResult<Result<DialogueOutcome>>(Result.Ok(DialogueOutcome.Advanced));
    }

    public Task<Result<DialogueOutcome>> SelectDialogueOption(
        DialogueOptionId option,
        CancellationToken ct)
        => Task.FromResult<Result<DialogueOutcome>>(
            Result.Fail<DialogueOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<DialogueOutcome>> SelectDialogueOptionByIndex(
        int zeroBasedIndex,
        CancellationToken ct)
        => Task.FromResult<Result<DialogueOutcome>>(
            Result.Fail<DialogueOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<DialogueOutcome>> SelectDialogueOptionBySheetReference(
        string sheetReference,
        CancellationToken ct)
        => Task.FromResult<Result<DialogueOutcome>>(
            Result.Fail<DialogueOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<Unit>> ConfirmYesNoPrompt(bool yes, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Fail("notImplemented", "Phase 6 placeholder"));

    public Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Fail("notImplemented", "Phase 6 placeholder"));

    public Task<Result<Unit>> CloseDialogue(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Fail("notImplemented", "Phase 6 placeholder"));

    // quest param unused — the JournalAccept addon already knows which quest is being accepted
    public Task<Result<Unit>> AcceptQuest(QuestId quest, CancellationToken ct)
        => ClickAddonButton("JournalAccept", buttonId: 44,
            missingAddonCode: "noAcceptDialog",
            missingButtonCode: "acceptButtonMissing");

    public Task<Result<Unit>> CompleteQuest(QuestId quest, CancellationToken ct)
        => ClickAddonButton("JournalResult", buttonId: 37,
            missingAddonCode: "noCompleteDialog",
            missingButtonCode: "completeButtonMissing");

    public Task<Result<Unit>> SelectQuestReward(int rewardIndex, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Fail("notImplemented", "Phase 6 placeholder"));

    public Task<Result<Unit>> AbandonQuest(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Fail("notImplemented", "Phase 6 placeholder"));

    public Task<Result<DutyEntryOutcome>> EnterDutyWithSupport(DutyId duty, CancellationToken ct)
        => Task.FromResult<Result<DutyEntryOutcome>>(
            Result.Fail<DutyEntryOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<DutyEntryOutcome>> EnterDutyWithFinder(DutyId duty, CancellationToken ct)
        => Task.FromResult<Result<DutyEntryOutcome>>(
            Result.Fail<DutyEntryOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(
        InteractableOrNpc trigger,
        DutyDifficulty preferredDifficulty,
        CancellationToken ct)
        => Task.FromResult<Result<SpdEntryOutcome>>(
            Result.Fail<SpdEntryOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(
            Result.Fail<UseItemOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, NpcId target, CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(
            Result.Fail<UseItemOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(
            Result.Fail<UseItemOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<UseItemOutcome>> UseItemOnPosition(
        ItemId item,
        WorldPosition position,
        float tolerance,
        CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(
            Result.Fail<UseItemOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Fail("notImplemented", "Phase 6 placeholder"));

    public Task<Result<Unit>> UseEmote(uint emoteId, NpcId? target, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Fail("notImplemented", "Phase 6 placeholder"));

    public unsafe Task<Result<HandOverOutcome>> HandOverItem(ItemId[] items, NpcId target, CancellationToken ct)
    {
        var addonPtr = _svc.GameGui.GetAddonByName("Request");
        if (addonPtr.IsNull || !addonPtr.IsReady)
            return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.NoDialog));

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (!addon->IsVisible || addon->AtkValuesCount < 4)
            return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.NoDialog));

        // AtkValue[0]=Int: items currently placed in hand-over slots (confirmed /xldata)
        // AtkValue[3]=UInt: required item count (up to 5 for multi-item hand-overs)
        var placedCount   = addon->AtkValues[0].Int;
        var requiredCount = (int)addon->AtkValues[3].UInt;

        if (placedCount >= requiredCount)
        {
            // The Request addon's button events live on child collision nodes, not on the
            // button's OwnerNode. The addon's FocusNode (offset 0xF8) points directly to
            // the Hand Over button's inner collision node, which DOES have events.
            var focusNode = addon->FocusNode;
            if (focusNode == null)
                return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.NoDialog));
            var evt = focusNode->AtkEventManager.Event;
            if (evt == null)
                return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.NoDialog));
            addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt);
            return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.HandedOver));
        }

        // Place items not yet in slots — one item per call, engine retries each tick
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.ItemNotFound));

        // InventoryType.HandIn (2005) = the hand-over slot container (confirmed FFXIVClientStructs)
        var container = mgr->GetInventoryContainer(InventoryType.KeyItems);
        if (container == null)
            return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.ItemNotFound));

        foreach (var itemId in items)
        {
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId.Value) continue;
                mgr->MoveItemSlot(InventoryType.KeyItems, (ushort)i, InventoryType.HandIn, (ushort)placedCount);
                return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.ItemPlaced));
            }
        }

        return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.ItemNotFound));
    }

    private Task<Result<Unit>> ClickAddonButton(
        string addonName, uint buttonId,
        string missingAddonCode, string missingButtonCode)
    {
        var addonPtr = _svc.GameGui.GetAddonByName(addonName);
        if (addonPtr.IsNull || !addonPtr.IsReady)
            return Task.FromResult<Result<Unit>>(Result.Fail(missingAddonCode));
        unsafe
        {
            var addon = (AtkUnitBase*)addonPtr.Address;
            var btn = addon->GetComponentButtonById(buttonId);
            if (btn == null)
                return Task.FromResult<Result<Unit>>(Result.Fail(missingButtonCode));
            var resNode = (AtkResNode*)btn->AtkComponentBase.OwnerNode;
            var evt = resNode->AtkEventManager.Event;
            if (evt == null)
                return Task.FromResult<Result<Unit>>(Result.Fail("noButtonEvent"));
            addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt);
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
