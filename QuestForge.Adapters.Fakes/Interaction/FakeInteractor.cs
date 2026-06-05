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
    private readonly Queue<HandOverOutcome> _handOverSequence = new();
    private SpdEntryOutcome _spdResult = SpdEntryOutcome.Entered;

    // Per-quest callbacks fired after the default state transition
    private readonly Dictionary<QuestId, Action> _onAcceptCallbacks = new();
    private readonly Dictionary<QuestId, Action> _onCompleteCallbacks = new();

    // Per-NPC callbacks fired when InteractWith(npcId) is called
    private readonly Dictionary<NpcId, Action> _onInteractWithCallbacks = new();

    // Per-method call logs
    public record InteractionCall(NpcId? NpcId, InteractableId? ObjectId, DateTimeOffset At) : AdapterCall(At);
    public record DialogueAdvanceCall(DateTimeOffset At) : AdapterCall(At);
    public record QuestAcceptCall(QuestId QuestId, DateTimeOffset At) : AdapterCall(At);
    public record QuestCompleteCall(QuestId QuestId, DateTimeOffset At) : AdapterCall(At);
    public record HandOverCall(ItemId[] Items, NpcId Target, DateTimeOffset At) : AdapterCall(At);

    public CallLog<InteractionCall> RecordedInteractions { get; } = new();
    public CallLog<DialogueAdvanceCall> RecordedDialogueAdvances { get; } = new();
    public CallLog<QuestAcceptCall> RecordedQuestAccepts { get; } = new();
    public CallLog<QuestCompleteCall> RecordedQuestCompletes { get; } = new();
    public CallLog<HandOverCall> RecordedHandOvers { get; } = new();

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

    public void ScriptHandOverSequence(params HandOverOutcome[] outcomes)
    {
        _handOverSequence.Clear();
        foreach (var o in outcomes)
            _handOverSequence.Enqueue(o);
    }

    public void SetSpdResult(SpdEntryOutcome outcome) => _spdResult = outcome;

    public void OnAcceptQuest(QuestId quest, Action callback) => _onAcceptCallbacks[quest] = callback;
    public void OnCompleteQuest(QuestId quest, Action callback) => _onCompleteCallbacks[quest] = callback;
    public void OnInteractWith(NpcId npcId, Action callback) => _onInteractWithCallbacks[npcId] = callback;

    // ----- Reset -----
    public void Reset()
    {
        RecordedInteractions.Clear();
        RecordedDialogueAdvances.Clear();
        RecordedQuestAccepts.Clear();
        RecordedQuestCompletes.Clear();
        RecordedHandOvers.Clear();
        _nextInteractResult = null;
        _dialogueSequence.Clear();
        _handOverSequence.Clear();
        _onAcceptCallbacks.Clear();
        _onCompleteCallbacks.Clear();
        _onInteractWithCallbacks.Clear();
    }

    // ----- IInteractor implementation -----

    public Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedInteractions.Add(new InteractionCall(npc, null, DateTimeOffset.UtcNow));
        var outcome = ConsumeInteractResult();
        if (_onInteractWithCallbacks.TryGetValue(npc, out var cb)) cb();
        return Task.FromResult<Result<InteractOutcome>>(Result.Ok(outcome));
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
        if (_onAcceptCallbacks.TryGetValue(quest, out var cb)) cb();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> CompleteQuest(QuestId quest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedQuestCompletes.Add(new QuestCompleteCall(quest, DateTimeOffset.UtcNow));
        _questState.SetQuestStatus(quest, QuestStatus.Complete);
        if (_onCompleteCallbacks.TryGetValue(quest, out var cb)) cb();
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

    public Task<Result<HandOverOutcome>> HandOverItem(ItemId[] items, NpcId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedHandOvers.Add(new HandOverCall(items, target, DateTimeOffset.UtcNow));
        var outcome = _handOverSequence.Count > 0
            ? _handOverSequence.Dequeue()
            : HandOverOutcome.HandedOver;
        return Task.FromResult<Result<HandOverOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<HandOverOutcome>> TryFillRequestAddon(CancellationToken ct)
        => Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.NoDialog));

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