using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests;

/// <summary>
/// RED PHASE: Tests for DialogueChoiceDispatcher extended to handle TravelStep+NpcDialogue.
///
/// ALL tests will fail to compile or runtime-fail until Builder:
///   - Extends DialogueChoiceDispatcher.TryDispatch to also accept TravelStep
///     when RouteHint.NpcDialogue is set and has DialogueChoices
///   - The dispatch logic is identical to TalkStep: parse Answer → SelectStringOption(idx)
///   - Keeps EH_7 regression: TravelStep with Aethernet only still returns false
///   - Keeps EH_1 regression: TalkStep path unchanged
///
/// Test IDs: DD-1 through DD-12
/// </summary>
public sealed class DialogueChoiceDispatcherTravelTests
{
    // =========================================================================
    // Minimal stub IInteractor — identical to the one in DialogueChoiceDispatcherTests
    // =========================================================================

    private sealed class DispatchStub : IInteractor
    {
        public List<int> SelectStringCalls { get; } = [];

        public Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct)
        {
            SelectStringCalls.Add(zeroBasedIndex);
            return Task.FromResult<Result<Unit>>(Result.Ok());
        }

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

    // Convenience builders
    private static readonly NpcLocation SampleNpcLoc =
        new(NpcId: 7777u, Zone: 131, Position: new Position3(10f, 0f, 20f));

    private static TravelStep TravelWithNpcDialogue(params DialogueChoice[] choices) => new()
    {
        Id = "lift-travel",
        Destination = new TravelDestination(Zone: 200, Position: new Position3(0f, 0f, 0f)),
        RouteHint = new RouteHint(NpcDialogue: new NpcDialogueHint(SampleNpcLoc, choices))
    };

    private static TravelStep TravelWithListChoice(string answer) =>
        TravelWithNpcDialogue(new DialogueChoice(Type: "list", Answer: answer));

    // DD-1
    [Fact]
    public void DD1_TravelStep_NpcDialogue_Choice2_SelectIconStringOpen_Dispatches_ReturnsTrue()
    {
        /*
         * RED: Will fail until Builder extends TryDispatch to handle TravelStep+NpcDialogue.
         *
         * CONTRACT: Given TravelStep with RouteHint.NpcDialogue.DialogueChoices=[("list","2")],
         *           progress=0, selectIconStringOpen=true,
         *           When TryDispatch is called,
         *           Then SelectStringOption(2) is called, progress==1, returns true.
         *
         * BUILDER GUIDANCE: Add a second type-check arm:
         *   if (currentStep is TravelStep travel && travel.RouteHint?.NpcDialogue is { } npcHint)
         *   then use npcHint.DialogueChoices the same way TalkStep uses talkStep.DialogueChoices.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithListChoice("2");
        int progress = 0;

        // Act
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.True(dispatched, "TryDispatch should return true for TravelStep+NpcDialogue list choice");
        Assert.Equal(1, progress);
        Assert.Single(stub.SelectStringCalls);
        Assert.Equal(2, stub.SelectStringCalls[0]);
    }

    // DD-2
    [Fact]
    public void DD2_TravelStep_NpcDialogue_SelectStringOpen_Dispatches_ReturnsTrue()
    {
        /*
         * RED: Will fail until Builder extends TryDispatch for TravelStep+NpcDialogue.
         *
         * CONTRACT: Given selectStringOpen=true (not selectIconString),
         *           TravelStep with NpcDialogue choice "2",
         *           When TryDispatch is called,
         *           Then SelectStringOption(2) is called, returns true.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithListChoice("2");
        int progress = 0;

        // Act
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

    // DD-3
    [Fact]
    public void DD3_TravelStep_NpcDialogue_BothAddonsClosed_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder extends TryDispatch for TravelStep+NpcDialogue.
         *
         * CONTRACT: Given TravelStep with NpcDialogue choice, both addons closed,
         *           When TryDispatch is called,
         *           Then returns false (addon not visible yet).
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithListChoice("2");
        int progress = 0;

        // Act
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

    // DD-4
    [Fact]
    public void DD4_TravelStep_NpcDialogue_EmptyChoices_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder extends TryDispatch for TravelStep+NpcDialogue.
         *
         * CONTRACT: Given TravelStep with NpcDialogue but DialogueChoices is empty,
         *           addon open,
         *           When TryDispatch is called,
         *           Then returns false (nothing to dispatch).
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithNpcDialogue(); // empty choices
        int progress = 0;

        // Act
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

    // DD-5
    [Fact]
    public void DD5_TravelStep_AethernetOnly_NoNpcDialogue_ReturnsFalse_EH7Regression()
    {
        /*
         * RED: Regression guard for EH_7 — TravelStep with Aethernet only must still return false.
         *
         * CONTRACT: Given TravelStep with RouteHint.Aethernet only (no NpcDialogue),
         *           addon open,
         *           When TryDispatch is called,
         *           Then returns false (existing EH_7 behaviour extended to cover aethernet case).
         */

        // Arrange
        var stub = new DispatchStub();
        var step = new TravelStep
        {
            Id = "aethernet-travel",
            Destination = new TravelDestination(Zone: 131, Position: new Position3(0f, 0f, 0f)),
            RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: 125u, To: 33u))
        };
        int progress = 0;

        // Act
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

    // DD-6
    [Fact]
    public void DD6_TravelStep_RouteHintNull_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm with null guard.
         *
         * CONTRACT: Given TravelStep with RouteHint==null, addon open,
         *           When TryDispatch is called,
         *           Then returns false (no NpcDialogue to dispatch).
         *
         * BUILDER GUIDANCE: Guard: travel.RouteHint?.NpcDialogue is { } — null RouteHint falls through.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = new TravelStep
        {
            Id = "bare-travel",
            Destination = new TravelDestination(Zone: 131, Position: new Position3(0f, 0f, 0f)),
            RouteHint = null
        };
        int progress = 0;

        // Act
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

    // DD-7
    [Fact]
    public void DD7_TravelStep_NpcDialogue_ProgressExhausted_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm.
         *
         * CONTRACT: Given TravelStep with 1 NpcDialogue choice and progress=1 (exhausted),
         *           addon open,
         *           When TryDispatch is called,
         *           Then returns false (all choices already dispatched).
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithListChoice("2");
        int progress = 1; // already past the single choice

        // Act
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

    // DD-8
    [Fact]
    public void DD8_TravelStep_NpcDialogue_YesnoChoice_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm with type check.
         *
         * CONTRACT: Given TravelStep with NpcDialogue choice of Type=="yesno", addon open,
         *           When TryDispatch is called,
         *           Then returns false (non-list type falls through).
         *
         * BUILDER GUIDANCE: Only Type=="list" is dispatched. "yesno" is handled by the
         * auto-confirm path (SelectYesno). Return false for any other type.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithNpcDialogue(new DialogueChoice(Type: "yesno", Answer: "yes"));
        int progress = 0;

        // Act
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

    // DD-9
    [Fact]
    public void DD9_TravelStep_NpcDialogue_NonNumericAnswer_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm with answer parsing.
         *
         * CONTRACT: Given TravelStep with NpcDialogue list choice Answer="not-a-number",
         *           When TryDispatch is called with addon open,
         *           Then returns false (parse failure).
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithNpcDialogue(new DialogueChoice(Type: "list", Answer: "not-a-number"));
        int progress = 0;

        // Act
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

    // DD-10
    [Fact]
    public void DD10_TravelStep_NpcDialogue_NegativeAnswer_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm with index validation.
         *
         * CONTRACT: Given TravelStep with NpcDialogue list choice Answer="-1",
         *           When TryDispatch is called with addon open,
         *           Then returns false (negative index invalid).
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithNpcDialogue(new DialogueChoice(Type: "list", Answer: "-1"));
        int progress = 0;

        // Act
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

    // DD-11
    [Fact]
    public void DD11_TravelStep_TwoSequentialNpcDialogueChoices_DispatchedInOrder()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue arm.
         *
         * CONTRACT: Given TravelStep with NpcDialogue choices [("list","1"),("list","0")],
         *           When TryDispatch is called twice (addon open both times),
         *           Then first call dispatches "1", second dispatches "0", progress==2.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = TravelWithNpcDialogue(
            new DialogueChoice(Type: "list", Answer: "1"),
            new DialogueChoice(Type: "list", Answer: "0"));
        int progress = 0;

        // Act — first
        bool first = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step, selectIconStringOpen: true, selectStringOpen: false,
            progress: ref progress, interactor: stub, ct: CancellationToken.None);

        // Act — second
        bool second = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step, selectIconStringOpen: true, selectStringOpen: false,
            progress: ref progress, interactor: stub, ct: CancellationToken.None);

        // Assert
        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, progress);
        Assert.Equal(2, stub.SelectStringCalls.Count);
        Assert.Equal(1, stub.SelectStringCalls[0]);
        Assert.Equal(0, stub.SelectStringCalls[1]);
    }

    // DD-12
    [Fact]
    public void DD12_TalkStep_PathUnchanged_EH1Regression()
    {
        /*
         * Regression guard — EH-1 scenario must still pass after NpcDialogue changes.
         *
         * CONTRACT: Given TalkStep with DialogueChoices=[("list","2")], progress=0,
         *           selectIconStringOpen=true,
         *           When TryDispatch is called,
         *           Then SelectStringOption(2) is called, progress==1, returns true.
         *
         * This is the existing EH_1 scenario reproduced here to confirm no regression.
         */

        // Arrange
        var stub = new DispatchStub();
        var step = new TalkStep
        {
            Id = "talk-step",
            DialogueChoices = [new DialogueChoice(Type: "list", Answer: "2")]
        };
        int progress = 0;

        // Act
        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: step,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        // Assert
        Assert.True(dispatched, "TalkStep EH-1 regression: TryDispatch must still return true");
        Assert.Equal(1, progress);
        Assert.Single(stub.SelectStringCalls);
        Assert.Equal(2, stub.SelectStringCalls[0]);
    }
}
