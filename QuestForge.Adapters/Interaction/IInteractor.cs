using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Interaction;

public interface IInteractor
{
    // NPC and object interaction
    Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct);
    Task<Result<InteractOutcome>> InteractWithObject(InteractableId obj, CancellationToken ct);

    // Dialogue
    Task<Result<DialogueOutcome>> AdvanceDialogue(CancellationToken ct);
    Task<Result<DialogueOutcome>> SelectDialogueOption(
        DialogueOptionId option,
        CancellationToken ct);
    Task<Result<DialogueOutcome>> SelectDialogueOptionByIndex(
        int zeroBasedIndex,
        CancellationToken ct);
    Task<Result<DialogueOutcome>> SelectDialogueOptionBySheetReference(
        string sheetReference,
        CancellationToken ct);

    // Prompts
    Task<Result<Unit>> ConfirmYesNoPrompt(bool yes, CancellationToken ct);
    Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct);
    Task<Result<Unit>> CloseDialogue(CancellationToken ct);

    // Quest lifecycle
    Task<Result<Unit>> AcceptQuest(QuestId quest, CancellationToken ct);
    Task<Result<Unit>> CompleteQuest(QuestId quest, CancellationToken ct);
    Task<Result<Unit>> SelectQuestReward(int rewardIndex, CancellationToken ct);
    Task<Result<Unit>> AbandonQuest(QuestId quest, CancellationToken ct);

    // Duty entry — full duties (dungeons, trials, raids)
    Task<Result<DutyEntryOutcome>> EnterDutyWithSupport(DutyId duty, CancellationToken ct);
    Task<Result<DutyEntryOutcome>> EnterDutyWithFinder(DutyId duty, CancellationToken ct);

    // SPD entry — Quest Battles via NPC or object trigger
    Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(
        InteractableOrNpc trigger,
        DutyDifficulty preferredDifficulty,
        CancellationToken ct);

    // Item use
    Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct);
    Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, NpcId target, CancellationToken ct);
    Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct);
    Task<Result<UseItemOutcome>> UseItemOnPosition(
        ItemId item,
        WorldPosition position,
        float tolerance,
        CancellationToken ct);

    // Chat and emotes (non-standard interactions, often NPC-targeted)
    Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct);
    Task<Result<Unit>> UseEmote(uint emoteId, NpcId? target, CancellationToken ct);

    // Item hand-over (Quest Request addon)
    Task<Result<HandOverOutcome>> HandOverItem(ItemId item, NpcId target, CancellationToken ct);
}

public enum InteractOutcome
{
    DialogueOpened,
    AlreadyInteracted,
    OutOfRange,
    NpcNotFound,
    ObjectNotFound,
    Blocked
}

public enum DialogueOutcome
{
    Advanced,
    DialogueClosed,
    OptionUnavailable,
    NoActiveDialogue
}

public enum DutyEntryOutcome
{
    Entered,
    LevelTooLow,
    NotUnlocked,
    SupportUnavailable,
    FinderUnavailable,
    AlreadyQueued,
    InCombat,
    Failed
}

public enum SpdEntryOutcome
{
    Entered,
    AlreadyOnPreferredDifficulty,
    DifficultyDowngraded,
    NoSuitableDifficulty,
    NotAtTrigger,
    Failed
}

public enum DutyDifficulty { Normal, Easy, VeryEasy }

public enum UseItemOutcome
{
    Used,
    ItemNotInInventory,
    ItemNotUsable,
    TargetOutOfRange,
    TargetInvalid,
    OnCooldown,
    Failed
}

public abstract record InteractableOrNpc;
public sealed record AsNpc(NpcId Id) : InteractableOrNpc;
public sealed record AsObject(InteractableId Id) : InteractableOrNpc;

public enum ChatChannel { Say, Yell, Shout }

public enum HandOverOutcome
{
    ItemPlaced,
    HandedOver,
    NoDialog,
    ItemNotFound
}

// DutyFallbackPolicy is an engine-level config concern (EngineDecisionConfig),
// not an adapter concern. It lives in QuestForge.Engine (Phase 4).