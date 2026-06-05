using System.Text.Json;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;
using AdaptersQuestId = QuestForge.Adapters.Types.QuestId;
using AdaptersNpcId   = QuestForge.Adapters.Types.NpcId;

namespace QuestForge.Engine.Tests.Integration;

/// <summary>
/// RED PHASE: End-to-end round-trip test for the NpcDialogue travel feature.
///
/// E2E-1: Infer → Build → Serialize/Deserialize → TryDispatch → SelectStringOption(0) called.
///
/// This test will fail to compile or runtime-fail until ALL of the following are implemented:
///   - NpcDialogueHint record in QuestForge.Schema (SC-* tests)
///   - RouteHint.NpcDialogue parameter (SC-* tests)
///   - StepInferenceEngine Rule 4 NpcDialogue sub-case (IN-* tests)
///   - StepFactory NpcDialogue arm (SF-ND-* tests)
///   - QuestForgeJsonContext NpcDialogueHint registration (SC-5/SC-7 tests)
///   - DialogueChoiceDispatcher.TryDispatch TravelStep+NpcDialogue arm (DD-* tests)
/// </summary>
public sealed class NpcDialogueRoundTripTest
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);
    private static readonly AdaptersQuestId ActiveQuest = new(2054);

    // Minimal IInteractor stub that records SelectStringOption calls
    private sealed class DispatchStub : IInteractor
    {
        public List<int> SelectStringCalls { get; } = [];

        public Task<Result<Unit>> SelectStringOption(int zeroBasedIndex, CancellationToken ct)
        {
            SelectStringCalls.Add(zeroBasedIndex);
            return Task.FromResult<Result<Unit>>(Result.Ok());
        }

        public Task<Result<InteractOutcome>> InteractWith(AdaptersNpcId npc, CancellationToken ct)
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
        public Task<Result<Unit>> AcceptQuest(AdaptersQuestId quest, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<Unit>> CompleteQuest(AdaptersQuestId quest, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<Unit>> SelectQuestReward(int rewardIndex, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<Unit>> AbandonQuest(AdaptersQuestId quest, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<DutyEntryOutcome>> EnterDutyWithSupport(DutyId duty, CancellationToken ct)
            => Task.FromResult<Result<DutyEntryOutcome>>(Result.Ok(DutyEntryOutcome.Entered));
        public Task<Result<DutyEntryOutcome>> EnterDutyWithFinder(DutyId duty, CancellationToken ct)
            => Task.FromResult<Result<DutyEntryOutcome>>(Result.Ok(DutyEntryOutcome.Entered));
        public Task<Result<SpdEntryOutcome>> EnterSinglePlayerDuty(InteractableOrNpc trigger, DutyDifficulty preferredDifficulty, CancellationToken ct)
            => Task.FromResult<Result<SpdEntryOutcome>>(Result.Ok(SpdEntryOutcome.Entered));
        public Task<Result<UseItemOutcome>> UseItem(ItemId item, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<UseItemOutcome>> UseItemOnTarget(ItemId item, AdaptersNpcId target, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<UseItemOutcome>> UseItemOnObject(ItemId item, InteractableId target, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<UseItemOutcome>> UseItemOnPosition(ItemId item, WorldPosition position, float tolerance, CancellationToken ct)
            => Task.FromResult<Result<UseItemOutcome>>(Result.Ok(UseItemOutcome.Used));
        public Task<Result<Unit>> SendChatMessage(ChatChannel channel, string messageSheetReference, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<Unit>> UseEmote(uint emoteId, AdaptersNpcId? target, CancellationToken ct)
            => Task.FromResult<Result<Unit>>(Result.Ok());
        public Task<Result<HandOverOutcome>> HandOverItem(ItemId[] items, AdaptersNpcId target, CancellationToken ct)
            => Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.HandedOver));
        public Task<Result<HandOverOutcome>> TryFillRequestAddon(CancellationToken ct)
            => Task.FromResult<Result<HandOverOutcome>>(Result.Ok(HandOverOutcome.NoDialog));
    }

    // E2E-1
    [Fact]
    public void E2E1_Infer_Build_SerializeDeserialize_TryDispatch_SelectStringOptionCalled()
    {
        /*
         * RED: Will fail until ALL NpcDialogue feature components are implemented.
         *
         * CONTRACT: Given before/after snapshots capturing Lift Attendant zone travel
         *           (zone 131→200, NpcId=7777, NpcPosition=(10,0,20), DialogueOptionSelected=0),
         *           When:
         *             1. StepInferenceEngine.Infer produces a "travel"/"npc-dialogue" result,
         *             2. StepFactory.Build constructs a TravelStep with RouteHint.NpcDialogue,
         *             3. The step is serialized then deserialized via QuestForgeJsonContext,
         *             4. DialogueChoiceDispatcher.TryDispatch is called on the deserialized step
         *                with selectIconStringOpen=true, progress=0,
         *           Then SelectStringOption(0) is called exactly once and returns true.
         *
         * BUILDER GUIDANCE: All prerequisite unit tests (SC-*, IN-*, SF-ND-*, DD-*) must pass
         * before this integration test can pass. Use them as the implementation checklist.
         */

        // ── Step 1: Infer ───────────────────────────────────────────────────────

        var inferenceEngine = new StepInferenceEngine();

        var before = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(131),
            Position: new WorldPosition(9f, 0f, 19f),
            ActiveQuest: ActiveQuest,
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: false,
            QuestCompleted: false,
            LastNpcInteracted: new AdaptersNpcId(7777),
            LastNpcPosition: new WorldPosition(10f, 0f, 20f),
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null)
        {
            DialogueOptionSelected = null
        };

        var after = new GameStateSnapshot(
            CapturedAt: T1,
            Zone: new ZoneId(200),
            Position: new WorldPosition(5f, 0f, 5f),
            ActiveQuest: ActiveQuest,
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: false,
            QuestCompleted: false,
            LastNpcInteracted: new AdaptersNpcId(7777),
            LastNpcPosition: new WorldPosition(10f, 0f, 20f),
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null)
        {
            DialogueOptionSelected = 0,
            DialogueNpcSource = new NpcLocation(
                NpcId: 7777,
                Zone: 131,
                Position: new Position3(10f, 0f, 20f))
        };

        var inferResult = inferenceEngine.Infer(before, after);

        // Inference must produce travel/npc-dialogue
        Assert.Equal("travel", inferResult.StepType);
        Assert.Contains("npc-dialogue", inferResult.SuggestedStepId, StringComparison.OrdinalIgnoreCase);

        // ── Step 2: Build ───────────────────────────────────────────────────────

        var builtStep = StepFactory.Build(
            inferResult.StepType,
            inferResult.SuggestedStepId,
            inferResult.SuggestedExpect,
            after,
            before: before);

        var travelStep = Assert.IsType<TravelStep>(builtStep);
        Assert.NotNull(travelStep.RouteHint?.NpcDialogue);
        Assert.Single(travelStep.RouteHint!.NpcDialogue!.DialogueChoices);
        Assert.Equal("0", travelStep.RouteHint.NpcDialogue.DialogueChoices[0].Answer);

        // ── Step 3: Serialize / Deserialize ─────────────────────────────────────

        var json = JsonSerializer.Serialize(travelStep, QuestForgeJsonContext.QuestFileOptions);

        Assert.Contains("npcDialogue", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dialogueChoices", json, StringComparison.OrdinalIgnoreCase);

        var deserialized = JsonSerializer.Deserialize<TravelStep>(json, QuestForgeJsonContext.QuestFileOptions);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.RouteHint?.NpcDialogue);
        Assert.Single(deserialized.RouteHint!.NpcDialogue!.DialogueChoices);
        Assert.Equal("0", deserialized.RouteHint.NpcDialogue.DialogueChoices[0].Answer);

        // ── Step 4: Dispatch ────────────────────────────────────────────────────

        var stub = new DispatchStub();
        int progress = 0;

        bool dispatched = DialogueChoiceDispatcher.TryDispatch(
            currentStep: deserialized,
            selectIconStringOpen: true,
            selectStringOpen: false,
            progress: ref progress,
            interactor: stub,
            ct: CancellationToken.None);

        Assert.True(dispatched, "TryDispatch must return true for round-tripped TravelStep+NpcDialogue");
        Assert.Equal(1, progress);
        Assert.Single(stub.SelectStringCalls);
        Assert.Equal(0, stub.SelectStringCalls[0]);
    }
}
