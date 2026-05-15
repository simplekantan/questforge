using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

public class FakeInteractorTests
{
    private static FakeInteractor CreateInteractor(out FakeGameStateProvider gameState, out FakeQuestState questState)
    {
        gameState = new FakeGameStateProvider();
        questState = new FakeQuestState();
        return new FakeInteractor(gameState, questState);
    }

    [Fact]
    public async Task AcceptQuest_QuestStateIsQuestAccepted_ReturnsTrue()
    {
        var interactor = CreateInteractor(out _, out var questState);
        var questId = new QuestId(100);

        await interactor.AcceptQuest(questId, CancellationToken.None);

        var accepted = await questState.IsQuestAccepted(questId, CancellationToken.None);
        Assert.True(accepted.ValueOrThrow);
    }

    [Fact]
    public async Task AcceptQuest_QuestStateStatus_ReturnsAccepted()
    {
        var interactor = CreateInteractor(out _, out var questState);
        var questId = new QuestId(100);

        await interactor.AcceptQuest(questId, CancellationToken.None);

        var status = await questState.GetQuestStatus(questId, CancellationToken.None);
        Assert.Equal(QuestStatus.Accepted, status.ValueOrThrow);
    }

    [Fact]
    public async Task AcceptQuest_IsRecorded()
    {
        var interactor = CreateInteractor(out _, out _);
        var questId = new QuestId(100);

        await interactor.AcceptQuest(questId, CancellationToken.None);

        Assert.Equal(1, interactor.RecordedQuestAccepts.Count);
    }

    [Fact]
    public async Task OnAcceptQuest_Callback_FiresOnAccept()
    {
        var interactor = CreateInteractor(out _, out _);
        var questId = new QuestId(200);
        var callbackFired = false;
        interactor.OnAcceptQuest(questId, () => callbackFired = true);

        await interactor.AcceptQuest(questId, CancellationToken.None);

        Assert.True(callbackFired);
    }

    [Fact]
    public async Task OnAcceptQuest_Callback_DefaultStateTransitionAlsoHappens()
    {
        var interactor = CreateInteractor(out _, out var questState);
        var questId = new QuestId(200);
        interactor.OnAcceptQuest(questId, () => { /* no-op */ });

        await interactor.AcceptQuest(questId, CancellationToken.None);

        var status = await questState.GetQuestStatus(questId, CancellationToken.None);
        Assert.Equal(QuestStatus.Accepted, status.ValueOrThrow);
    }

    [Fact]
    public async Task CompleteQuest_QuestStateStatus_ReturnsComplete()
    {
        var interactor = CreateInteractor(out _, out var questState);
        var questId = new QuestId(300);

        await interactor.CompleteQuest(questId, CancellationToken.None);

        var status = await questState.GetQuestStatus(questId, CancellationToken.None);
        Assert.Equal(QuestStatus.Complete, status.ValueOrThrow);
    }

    [Fact]
    public async Task CompleteQuest_IsRecorded()
    {
        var interactor = CreateInteractor(out _, out _);
        var questId = new QuestId(300);

        await interactor.CompleteQuest(questId, CancellationToken.None);

        Assert.Equal(1, interactor.RecordedQuestCompletes.Count);
    }

    [Fact]
    public async Task OnCompleteQuest_Callback_FiresOnComplete()
    {
        var interactor = CreateInteractor(out _, out _);
        var questId = new QuestId(400);
        var callbackFired = false;
        interactor.OnCompleteQuest(questId, () => callbackFired = true);

        await interactor.CompleteQuest(questId, CancellationToken.None);

        Assert.True(callbackFired);
    }

    [Fact]
    public async Task ScriptDialogueSequence_TwoCalls_ReturnsInOrder_ThirdReturnsDefault()
    {
        var interactor = CreateInteractor(out _, out _);
        interactor.ScriptDialogueSequence(DialogueOutcome.Advanced, DialogueOutcome.DialogueClosed);

        var first = await interactor.AdvanceDialogue(CancellationToken.None);
        var second = await interactor.AdvanceDialogue(CancellationToken.None);
        var third = await interactor.AdvanceDialogue(CancellationToken.None);

        Assert.Equal(DialogueOutcome.Advanced, first.ValueOrThrow);
        Assert.Equal(DialogueOutcome.DialogueClosed, second.ValueOrThrow);
        Assert.Equal(DialogueOutcome.Advanced, third.ValueOrThrow); // default
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceledException_NotRecorded()
    {
        var interactor = CreateInteractor(out _, out _);
        var ct = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => interactor.AcceptQuest(new QuestId(1), ct));
        Assert.Equal(0, interactor.RecordedQuestAccepts.Count);
    }

    [Fact]
    public async Task Reset_ClearsCallbacksAndDialogueSequence()
    {
        var interactor = CreateInteractor(out _, out _);
        var questId = new QuestId(500);
        var callbackFired = false;
        interactor.OnAcceptQuest(questId, () => callbackFired = true);
        interactor.ScriptDialogueSequence(DialogueOutcome.DialogueClosed);

        interactor.Reset();

        // Callback should not fire after reset
        await interactor.AcceptQuest(questId, CancellationToken.None);
        Assert.False(callbackFired);

        // Dialogue sequence should be cleared — default returns Advanced
        var dialogueResult = await interactor.AdvanceDialogue(CancellationToken.None);
        Assert.Equal(DialogueOutcome.Advanced, dialogueResult.ValueOrThrow);
    }
}