using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Interaction;

public sealed class FakeInteractor : IInteractor
{
    private readonly FakeGameStateProvider _gameState;
    private readonly FakeQuestState _questState;

    // One-shot scripted results
    private InteractOutcome? _nextInteractResult;
    private readonly Queue<DialogueOutcome> _dialogueSequence = new();
    private SpdEntryOutcome _spdResult = SpdEntryOutcome.Entered;

    // Per-method call logs
    public record InteractionCall(NpcId? NpcId, InteractableId? ObjectId, DateTimeOffset At) : AdapterCall(At);
    public record DialogueAdvanceCall(DateTimeOffset At) : AdapterCall(At);
    public record QuestAcceptCall(QuestId QuestId, DateTimeOffset At) : AdapterCall(At);
    public record QuestCompleteCall(QuestId QuestId, DateTimeOffset At) : AdapterCall(At);

    public CallLog<InteractionCall> RecordedInteractions { get; } = new();
    public CallLog<DialogueAdvanceCall> RecordedDialogueAdvances { get; } = new();
    public CallLog<QuestAcceptCall> RecordedQuestAccepts { get; } = new();
    public CallLog<QuestCompleteCall> RecordedQuestCompletes { get; } = new();

    public FakeInteractor(FakeGameStateProvider gameState, FakeQuestState questState)
    {
        _gameState = gameState;
        _questState = questState;
    }

    // ----- Scripting -----
    public void ScriptNextInteractResult(InteractOutcome outcome) => _nextInteractResult = outcome;

    public void ScriptDialogueSequence(params DialogueOutcome[] outcomes)
    {
        _dialogueSequence.Clear();
        foreach (var o in outcomes)
            _dialogueSequence.Enqueue(o);
    }

    public void SetSpdResult(SpdEntryOutcome outcome) => _spdResult = outcome;

    // ----- Reset -----
    public void Reset()
    {
        RecordedInteractions.Clear();
        RecordedDialogueAdvances.Clear();
        RecordedQuestAccepts.Clear();
        RecordedQuestCompletes.Clear();
        _nextInteractResult = null;
        _dialogueSequence.Clear();
    }

    // ----- IInteractor implementation -----

    public Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedInteractions.Add(new InteractionCall(npc, null, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<InteractOutcome>>(Result.Ok(ConsumeInteractResult()));
    }

    public Task<Result<InteractOutcome>> InteractWithObject(InteractableId obj, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedInteractions.Add(new InteractionCall(null, obj, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<InteractOutcome>>(Result.Ok(ConsumeInteractResult()));
    }

    public Task<Result<DialogueOutcome>> AdvanceDialogue(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedDialogueAdvances.Add(new DialogueAdvanceCall(DateTimeOffset.UtcNow));
        var outcome = _dialogueSequence.Count > 0
            ? _dialogueSequence.Dequeue()
            : DialogueOutcome.Advanced;
        return Task.FromResult<Result<DialogueOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<DialogueOutcome>> SelectDialogueOption(DialogueOptionId option, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedDialogueAdvances.Add(new DialogueAdvanceCall(DateTimeOffset.UtcNow));
        var outcome = _dialogueSequence.Count > 0
            ? _dialogueSequence.Dequeue()
            : DialogueOutcome.Advanced;
        return Task.FromResult<Result<DialogueOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<DialogueOutcome>> SelectDialogueOptionByIndex(int zeroBasedIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedDialogueAdvances.Add(new DialogueAdvanceCall(DateTimeOffset.UtcNow));
        var outcome = _dialogueSequence.Count > 0
            ? _dialogueSequence.Dequeue()
            : DialogueOutcome.Advanced;
        return Task.FromResult<Result<DialogueOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<DialogueOutcome>> SelectDialogueOptionBySheetReference(
        string sheetReference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedDialogueAdvances.Add(new DialogueAdvanceCall(DateTimeOffset.UtcNow));
        var outcome = _dialogueSequence.Count > 0
            ? _dialogueSequence.Dequeue()
            : DialogueOutcome.Advanced;
        return Task.FromResult<Result<DialogueOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<Unit>> ConfirmYesNoPrompt(bool yes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> CloseDialogue(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> AcceptQuest(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedQuestAccepts.Add(new QuestAcceptCall(quest, DateTimeOffset.UtcNow));
        _questState.AddAcceptedQuest(quest);
        _questState.SetQuestStatus(quest, QuestStatus.Accepted);
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> CompleteQuest(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedQuestCompletes.Add(new QuestCompleteCall(quest, DateTimeOffset.UtcNow));
        _questState.SetQuestStatus(quest, QuestStatus.Complete);
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> SelectQuestReward(int rewardIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> AbandonQuest(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _questState.RemoveAcceptedQuest(quest);
        _questState.SetQuestStatus(quest, QuestStatus.Available);
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<DutyEntryOutcome>> EnterDutyWithSupport(DutyId duty, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<DutyEntryOutcome>>(Result.Ok(DutyEntryOutcome.Entered));
    }

    public Task<Result<DutyEntryOutcome>> EnterDutyWithFinder(DutyId duty, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<DutyEntryOutcome>>(Result.Ok(DutyEntryOutcome.Entered));
    }

    public Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(
        InteractableOrNpc trigger,
        DutyDifficulty preferredDifficulty,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<SpdEntryOutcome>>(Result.Ok(_spdResult));
    }

    public Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
    }

    public Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, NpcId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
    }

    public Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
    }

    public Task<Result<UseItemOutcome>> UseItemOnPosition(
        ItemId item,
        WorldPosition position,
        float tolerance,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
    }

    public Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> UseEmote(uint emoteId, NpcId? target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    private InteractOutcome ConsumeInteractResult()
    {
        if (_nextInteractResult.HasValue)
        {
            var outcome = _nextInteractResult.Value;
            _nextInteractResult = null;
            return outcome;
        }
        return InteractOutcome.DialogueOpened;
    }
}