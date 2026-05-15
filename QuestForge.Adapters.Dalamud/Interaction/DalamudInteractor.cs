using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Interaction;

public sealed class DalamudInteractor : IInteractor
{
    private readonly PluginServices _svc;

    public DalamudInteractor(PluginServices svc) => _svc = svc;

    // IInteractor — NPC and object interaction

    public Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<InteractOutcome>> InteractWithObject(InteractableId obj, CancellationToken ct)
        => throw new NotImplementedException();

    // IInteractor — Dialogue

    public Task<Result<DialogueOutcome>> AdvanceDialogue(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<DialogueOutcome>> SelectDialogueOption(
        DialogueOptionId option,
        CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<DialogueOutcome>> SelectDialogueOptionByIndex(
        int zeroBasedIndex,
        CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<DialogueOutcome>> SelectDialogueOptionBySheetReference(
        string sheetReference,
        CancellationToken ct)
        => throw new NotImplementedException();

    // IInteractor — Prompts

    public Task<Result<Unit>> ConfirmYesNoPrompt(bool yes, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> CloseDialogue(CancellationToken ct)
        => throw new NotImplementedException();

    // IInteractor — Quest lifecycle

    public Task<Result<Unit>> AcceptQuest(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> CompleteQuest(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> SelectQuestReward(int rewardIndex, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> AbandonQuest(QuestId quest, CancellationToken ct)
        => throw new NotImplementedException();

    // IInteractor — Duty entry

    public Task<Result<DutyEntryOutcome>> EnterDutyWithSupport(DutyId duty, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<DutyEntryOutcome>> EnterDutyWithFinder(DutyId duty, CancellationToken ct)
        => throw new NotImplementedException();

    // IInteractor — SPD entry

    public Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(
        InteractableOrNpc trigger,
        DutyDifficulty preferredDifficulty,
        CancellationToken ct)
        => throw new NotImplementedException();

    // IInteractor — Item use

    public Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, NpcId target, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<UseItemOutcome>> UseItemOnPosition(
        ItemId item,
        WorldPosition position,
        float tolerance,
        CancellationToken ct)
        => throw new NotImplementedException();

    // IInteractor — Chat and emotes

    public Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> UseEmote(uint emoteId, NpcId? target, CancellationToken ct)
        => throw new NotImplementedException();
}