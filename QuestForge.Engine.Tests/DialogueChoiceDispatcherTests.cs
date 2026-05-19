using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests;

/// <summary>
/// RED PHASE: Tests for DialogueChoiceDispatcher — the pure static dispatch helper
/// that drives SelectIconString / SelectString list choices from a TalkStep's
/// DialogueChoices list.
///
/// ALL tests will fail to compile until Builder creates:
///   C:\Users\publi\RiderProjects\questforge\QuestForge.Engine\DialogueChoiceDispatcher.cs
///
/// Expected class shape:
///   public static class DialogueChoiceDispatcher {
///       public static bool TryDispatch(
///           Step? currentStep,
///           bool selectIconStringOpen,
///           bool selectStringOpen,
///           ref int progress,
///           IInteractor interactor,
///           CancellationToken ct);
///   }
///
/// Semantics:
///   - Returns true  → a choice was dispatched (caller must NOT call AdvanceDialogue this tick)
///   - Returns false → no choice dispatched   (caller falls through to AdvanceDialogue)
///   - Dispatches choices[progress] when progress &lt; choices.Length and an addon is open
///   - Only handles Type == "list"; skips "yesno" and other types
///   - Calls interactor.SelectStringOption(int.Parse(Answer), ct) on match
///   - Increments progress on successful dispatch
///   - Returns false if Answer is not a valid non-negative integer
/// </summary>
public sealed class DialogueChoiceDispatcherTests
{
    // =========================================================================
    // Minimal stub IInteractor that only tracks SelectStringOption calls
    // =========================================================================

    private sealed class DispatchStub : IInteractor
    {
        public List<int> SelectStringCalls { get; } = [];

        public Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct)
        {
            SelectStringCalls.Add(zeroBasedIndex);
            return Task.FromResult<Result<Unit>>(Result.Ok());
        }

        // All other members return default success — not exercised by dispatcher tests.
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
        public Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(InteractableOrNpc trigger, DutyDifficulty preferredDifficulty, CancellationToken ct)
            => Task.FromResult<Result<SpdEntryOutcome>>(Result.Ok(SpdEntryOutcome.Entered));
        public Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, NpcId target, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<UseItemOutcome>> UseItemOnPosition(ItemId item, WorldPosition position, float tolerance, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<Unit>> UseEmote(uint emoteId, NpcId? target, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<HandOverOutcome>> HandOverItem(ItemId[] items, NpcId target, CancellationToken ct)
            => Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.HandedOver));
    }

    private static TalkStep TalkWith(params DialogueChoice[] choices) => new()
    {
        Id = "step-talk",
        DialogueChoices = choices
    };

    private static TalkStep TalkWithListChoice(string answer) =>
        TalkWith(new DialogueChoice(Type: "list", Answer: answer));

    // EH-1
    [Fact]
    public void EH_1_TalkStepListChoice2_SelectIconStringOpen_Dispatches_ReturnsTrue()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given TalkStep with DialogueChoices=[("list",null,"2")], progress=0,
         *           and selectIconStringOpen=true,
         *           When TryDispatch is called,
         *           Then SelectStringOption(2) is called,
         *                progress becomes 1,
         *                and TryDispatch returns true.
         *
         * BUILDER GUIDANCE: "list" type + addon open → parse Answer to int →
         * call interactor.SelectStringOption(parsedIndex, ct).Wait() or .GetAwaiter().GetResult().
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWithListChoice("2");
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.True(dispatched, "TryDispatch should return true when a choice is dispatched");
        Assert.Equal(1, progress);
        Assert.Single(stub.SelectStringCalls);
        Assert.Equal(2, stub.SelectStringCalls[0]);
    }

    // EH-2
    [Fact]
    public void EH_2_TalkStepListChoice2_SelectStringOpen_Dispatches_ReturnsTrue()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given selectStringOpen=true (not selectIconString),
         *           When TryDispatch is called with a "list" choice of Answer="2",
         *           Then SelectStringOption(2) is called and returns true.
         *
         * BUILDER GUIDANCE: Both addons (SelectIconString and SelectString) use the same
         * dispatch path — SelectStringOption handles both.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWithListChoice("2");
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: false,
            selectStringOpen: true,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.True(dispatched);
        Assert.Equal(2, stub.SelectStringCalls[0]);
        Assert.Equal(1, progress);
    }

    // EH-3
    [Fact]
    public void EH_3_TalkStepEmptyChoices_AddonOpen_NoCall_ReturnsFalse()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given TalkStep with empty DialogueChoices and addon open,
         *           When TryDispatch is called,
         *           Then no SelectStringOption call is made and returns false.
         *
         * BUILDER GUIDANCE: No choices → nothing to dispatch; fall through to AdvanceDialogue.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWith(); // empty choices
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
        Assert.Equal(0, progress);
    }

    // EH-4
    [Fact]
    public void EH_4_ChoicesExhausted_NoCall_ReturnsFalse()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given TalkStep with 1 choice and progress=1 (already consumed),
         *           When TryDispatch is called with addon open,
         *           Then no call is made and returns false.
         *
         * BUILDER GUIDANCE: Guard with progress < choices.Length before dispatching.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWithListChoice("2");
        int progress = 1; // already past the single choice

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
        Assert.Equal(1, progress); // unchanged
    }

    // EH-5
    [Fact]
    public void EH_5_BothAddonsClosed_NoCall_ReturnsFalse()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given selectIconStringOpen=false and selectStringOpen=false,
         *           When TryDispatch is called with a valid choice at progress=0,
         *           Then no call is made and returns false.
         *
         * BUILDER GUIDANCE: Must check that at least one addon is open before dispatching.
         * The list addon may not yet be visible on this tick.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWithListChoice("2");
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: false,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
    }

    // EH-6
    [Fact]
    public void EH_6_TwoChoices_DispatchedSequentially_ProgressEndsAt2()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given TalkStep with DialogueChoices=[("list","2"),("list","0")],
         *           When TryDispatch is called twice (addon open both times),
         *           Then first call dispatches "2", second dispatches "0",
         *                and progress ends at 2.
         *
         * BUILDER GUIDANCE: progress is a ref parameter; increment after each successful dispatch.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWith(
            new DialogueChoice(Type: "list", Answer: "2"),
            new DialogueChoice(Type: "list", Answer: "0"));
        int progress = 0;

        // Act — first dispatch
        // RED: DialogueChoiceDispatcher does not exist yet
        bool first = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Act — second dispatch (progress now 1)
        bool second = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, progress);
        Assert.Equal(2, stub.SelectStringCalls.Count);
        Assert.Equal(2, stub.SelectStringCalls[0]);
        Assert.Equal(0, stub.SelectStringCalls[1]);
    }

    // EH-7
    [Fact]
    public void EH_7_NonTalkStepOrigin_NoCall_ReturnsFalse()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given currentStep is a TravelStep (not a TalkStep),
         *           When TryDispatch is called with addon open,
         *           Then no call is made and returns false.
         *
         * BUILDER GUIDANCE: Type-check currentStep; only TalkStep has DialogueChoices.
         */

        // Arrange
        var stub = new DispatchStub();
        var travelStep = new TravelStep
        {
            Id = "step-travel",
            Destination = new TravelDestination(Zone: 131)
        };
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: travelStep,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
    }

    // EH-8
    [Fact]
    public void EH_8_NullCurrentStep_NoCall_ReturnsFalse_NoNullReferenceException()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given currentStep == null,
         *           When TryDispatch is called with addon open,
         *           Then no exception is thrown, no call is made, returns false.
         *
         * BUILDER GUIDANCE: Guard with `if (currentStep is not TalkStep talkStep) return false;`
         */

        // Arrange
        var stub = new DispatchStub();
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: null,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
    }

    // EH-9
    [Fact]
    public void EH_9_YesnoTypeChoice_AddonOpen_NoCall_ReturnsFalse()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given TalkStep with a "yesno" type choice and addon open,
         *           When TryDispatch is called,
         *           Then no SelectStringOption call is made and returns false.
         *
         * BUILDER GUIDANCE: "yesno" choices are handled by the auto-confirm path (TextAdvance),
         * not by SelectStringOption. Skip any choice whose Type != "list".
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWith(new DialogueChoice(Type: "yesno", Answer: "yes"));
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
    }

    // EH-10
    [Fact]
    public void EH_10_NonNumericAnswer_NoCall_ReturnsFalse()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given TalkStep with a "list" choice whose Answer is "not-a-number",
         *           When TryDispatch is called with addon open,
         *           Then no SelectStringOption call is made and returns false.
         *
         * BUILDER GUIDANCE: Use int.TryParse(answer, out int idx); if parse fails, return false.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWith(new DialogueChoice(Type: "list", Answer: "not-a-number"));
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
        Assert.Equal(0, progress); // not incremented
    }

    // EH-11
    [Fact]
    public void EH_11_NegativeAnswer_NoCall_ReturnsFalse()
    {
        /*
         * RED: Will fail to compile until Builder creates DialogueChoiceDispatcher.
         *
         * CONTRACT: Given TalkStep with a "list" choice whose Answer is "-1",
         *           When TryDispatch is called with addon open,
         *           Then no SelectStringOption call is made and returns false.
         *
         * BUILDER GUIDANCE: After parsing, validate idx >= 0. Negative indices are invalid
         * for a zero-based list and should be treated as a malformed choice.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TalkWith(new DialogueChoice(Type: "list", Answer: "-1"));
        int progress = 0;

        // Act
        // RED: DialogueChoiceDispatcher does not exist yet
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.False(dispatched);
        Assert.Empty(stub.SelectStringCalls);
        Assert.Equal(0, progress); // not incremented
    }
}
