using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Inert no-op IInteractor for trace-replay fixtures. Never mutates any quest or game state.
/// Returns benign success for all calls. Holds no state; parameterless constructor.
/// </summary>
public sealed class InertInteractor : IInteractor
{
    public Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct)
        => Task.FromResult<Result<InteractOutcome>>(Result.Ok(InteractOutcome.DialogueOpened));

    public Task<Result<InteractOutcome>> InteractWithObject(InteractableId obj, CancellationToken ct)
        => Task.FromResult<Result<InteractOutcome>>(Result.Ok(InteractOutcome.DialogueOpened));

    public Task<Result<DialogueOutcome>> AdvanceDialogue(CancellationToken ct)
        => Task.FromResult<Result<DialogueOutcome>>(Result.Ok(DialogueOutcome.Advanced));

    public Task<Result<DialogueOutcome>> SelectDialogueOption(DialogueOptionId option, CancellationToken ct)
        => Task.FromResult<Result<DialogueOutcome>>(Result.Ok(DialogueOutcome.Advanced));

    public Task<Result<DialogueOutcome>> SelectDialogueOptionByIndex(int zeroBasedIndex, CancellationToken ct)
        => Task.FromResult<Result<DialogueOutcome>>(Result.Ok(DialogueOutcome.Advanced));

    public Task<Result<DialogueOutcome>> SelectDialogueOptionBySheetReference(string sheetReference, CancellationToken ct)
        => Task.FromResult<Result<DialogueOutcome>>(Result.Ok(DialogueOutcome.Advanced));

    public Task<Result<Unit>> ConfirmYesNoPrompt(bool yes, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<Unit>> CloseDialogue(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<Unit>> AcceptQuest(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<Unit>> CompleteQuest(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<Unit>> SelectQuestReward(int rewardIndex, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<Unit>> AbandonQuest(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<DutyEntryOutcome>> EnterDutyWithSupport(DutyId duty, CancellationToken ct)
        => Task.FromResult<Result<DutyEntryOutcome>>(Result.Ok(DutyEntryOutcome.Entered));

    public Task<Result<DutyEntryOutcome>> EnterDutyWithFinder(DutyId duty, CancellationToken ct)
        => Task.FromResult<Result<DutyEntryOutcome>>(Result.Ok(DutyEntryOutcome.Entered));

    public Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(
        InteractableOrNpc trigger,
        DutyDifficulty preferredDifficulty,
        CancellationToken ct)
        => Task.FromResult<Result<SpdEntryOutcome>>(Result.Ok(SpdEntryOutcome.Entered));

    public Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));

    public Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, NpcId target, CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));

    public Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));

    public Task<Result<UseItemOutcome>> UseItemOnPosition(
        ItemId item,
        WorldPosition position,
        float tolerance,
        CancellationToken ct)
        => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));

    public Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<Unit>> UseEmote(uint emoteId, NpcId? target, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<HandOverOutcome>> HandOverItem(ItemId[] items, NpcId target, CancellationToken ct)
        => Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.HandedOver));

    public Task<Result<HandOverOutcome>> TryFillRequestAddon(CancellationToken ct)
        => Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.NoDialog));
}
