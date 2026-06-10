using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names (AetheryteId). Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for UseItemOnObjectStep authoring inference -- covers four areas:
///   S5_T1..S5_T14: StepInferenceEngine Rule 3.5n + Rule 2.4 guard (priority pinning)
///   S5_F1..S5_F3:  StepFactory.Build "use-item-on-object" arm
///   S5_A1..S5_A4:  SnapshotAggregator addon-flag lifecycle
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - GameStateSnapshot.RequestAddonSeen  (bool)                              (Task S5-T1)
///   - GameStateSnapshot.InventoryEventAddonSeen  (bool)                       (Task S5-T1)
///   - InferredFrom.InventoryEventItemUsed (enum value)                        (Task S5-T3)
///   - SnapshotAggregator.OnRequestAddonSeen / OnInventoryEventAddonSeen       (Task S5-T2)
///   - StepInferenceEngine Rule 2.4 guard change (RequestAddonSeen required)   (Task S5-T4)
///   - StepInferenceEngine Rule 3.5n (InventoryEventAddonSeen + ItemUsed)      (Task S5-T4)
///   - StepFactory.Build "use-item-on-object" arm                              (Task S5-T5)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~UseItemOnObjectInferenceTests"
/// </summary>
public sealed class UseItemOnObjectInferenceTests
{
    private static readonly QuestId       ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0          = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors UseItemInferenceTests.MakeSnapshot)
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ZoneId?    zone           = null,
        bool       questAccepted  = false,
        bool       questCompleted = false,
        int        questSequence  = 0,
        IReadOnlyList<uint>? keyItemsRemoved = null) =>
        new(
            CapturedAt:          T0,
            Zone:                zone ?? new ZoneId(131),
            Position:            new WorldPosition(0, 0, 0),
            ActiveQuest:         ActiveQuest,
            QuestSequence:       questSequence,
            QuestFlags:          0,
            QuestAccepted:       questAccepted,
            QuestCompleted:      questCompleted,
            LastNpcInteracted:   null,
            LastNpcPosition:     null,
            LastDialoguePrompt:  null,
            LastDialogueAnswer:  null,
            InventoryHash:       0,
            LastAttuned:         null)
        {
            KeyItemsRemoved = keyItemsRemoved
        };

    // =========================================================================
    // S5_T1 -- Happy path: InventoryEventAddonSeen + ItemUsed + ObjectInteracted
    //          -> infers "use-item-on-object"
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_HappyPath_InfersUseItemOnObjectStep_S5_T1()
    {
        // CONTRACT: Given InventoryEventAddonSeen=true, ItemUsed set, ObjectInteracted set,
        //           When Infer is called,
        //           Then StepType="use-item-on-object", SuggestedStepId="use-item-2000456-on-2001500",
        //                SuggestedExpect=null, Confidence=High, InferredFrom=InventoryEventItemUsed,
        //                Notes contains "Expect" and "ItemId=2000456" and "InteractableId=2001500".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null),
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item-on-object",                         result.StepType);
        Assert.Equal("use-item-2000456-on-2001500",                result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.High,                              result.Confidence);
        Assert.Equal(InferredFrom.InventoryEventItemUsed,          result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("Expect", result.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ItemId=2000456", result.Notes, StringComparison.Ordinal);
        Assert.Contains("InteractableId=2001500", result.Notes, StringComparison.Ordinal);
    }

    // =========================================================================
    // S5_T2 -- KeyItemsRemoved WITHOUT RequestAddonSeen: Rule 2.4 skipped
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_KeyItemsRemovedNoRequestAddon_Rule24Skipped_S5_T2()
    {
        // CONTRACT: Given KeyItemsRemoved=[2000456], RequestAddonSeen=false,
        //           InventoryEventAddonSeen=false, ItemUsed=null,
        //           When Infer is called,
        //           Then StepType != "hand-over-item" (Rule 2.4 skipped because
        //           RequestAddonSeen is false). Falls through to later rules.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            KeyItemsRemoved = [2000456u]
            // RequestAddonSeen defaults to false
            // InventoryEventAddonSeen defaults to false
            // ItemUsed is null
        };

        var result = engine.Infer(before, after);

        Assert.NotEqual("hand-over-item", result.StepType);
    }

    // =========================================================================
    // S5_T3 -- KeyItemsRemoved + RequestAddonSeen -> "hand-over-item" preserved
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_KeyItemsRemovedWithRequestAddon_HandOverItem_S5_T3()
    {
        // CONTRACT: Given KeyItemsRemoved=[2000456], RequestAddonSeen=true,
        //           When Infer is called,
        //           Then StepType="hand-over-item", InferredFrom=DialogueInteraction,
        //                SuggestedExpect contains "not(playerHasItem(2000456))".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            KeyItemsRemoved = [2000456u],
            RequestAddonSeen = true
        };

        var result = engine.Infer(before, after);

        Assert.Equal("hand-over-item",                         result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction,         result.InferredFrom);
        Assert.NotNull(result.SuggestedExpect);
        Assert.Contains("not(playerHasItem(2000456))", result.SuggestedExpect, StringComparison.Ordinal);
    }

    // =========================================================================
    // S5_T4 -- Only ItemUsed, no InventoryEventAddonSeen -> "use-item" preserved
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_ItemUsedNoInventoryEventAddon_UseItem_S5_T4()
    {
        // CONTRACT: Given ItemUsed set, InventoryEventAddonSeen=false (default),
        //           When Infer is called,
        //           Then StepType="use-item", InferredFrom=ItemUsed.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
            // InventoryEventAddonSeen defaults to false
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                result.StepType);
        Assert.Equal(InferredFrom.ItemUsed,     result.InferredFrom);
    }

    // =========================================================================
    // S5_T5 -- InventoryEventAddonSeen + ItemUsed + KeyItemsRemoved (no Request)
    //          -> Rule 2.4 skipped, Rule 3.5n fires "use-item-on-object"
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_InventoryEventAndItemUsedAndKeyItemsRemoved_Rule35nFires_S5_T5()
    {
        // CONTRACT: Given InventoryEventAddonSeen=true, ItemUsed set, KeyItemsRemoved set,
        //           ObjectInteracted set, RequestAddonSeen=false,
        //           When Infer is called,
        //           Then StepType="use-item-on-object" (Rule 2.4 skipped because no RequestAddonSeen;
        //           Rule 3.5n fires). InferredFrom=InventoryEventItemUsed.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null),
            KeyItemsRemoved = [2000456u],
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
            // RequestAddonSeen defaults to false
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item-on-object",                     result.StepType);
        Assert.Equal(InferredFrom.InventoryEventItemUsed,      result.InferredFrom);
    }

    // =========================================================================
    // S5_T6 -- InventoryEventAddonSeen but NO ItemUsed -> Rule 3.5n skipped
    //          falls through to "interact-object" (Rule 3.5o)
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_InventoryEventNoItemUsed_Rule35nSkipped_S5_T6()
    {
        // CONTRACT: Given InventoryEventAddonSeen=true, ObjectInteracted set, ItemUsed=null,
        //           When Infer is called,
        //           Then StepType != "use-item-on-object" (Rule 3.5n requires both flags).
        //           StepType == "interact-object" (falls through to Rule 3.5o).
        //           InferredFrom == ObjectInteracted.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            InventoryEventAddonSeen = true,
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
            // ItemUsed = null (default)
        };

        var result = engine.Infer(before, after);

        Assert.NotEqual("use-item-on-object",              result.StepType);
        Assert.Equal("interact-object",                    result.StepType);
        Assert.Equal(InferredFrom.ObjectInteracted,        result.InferredFrom);
    }

    // =========================================================================
    // S5_T7 -- Rule 3.5n beats quest sequence advance (Rule 3)
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_BeatsQuestSequenceAdvance_S5_T7()
    {
        // CONTRACT: Given QuestSequence advances (1->2) AND InventoryEventAddonSeen + ItemUsed set,
        //           When Infer is called,
        //           Then StepType="use-item-on-object" (NOT "talk").

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 1);
        var after  = before with
        {
            QuestSequence = 2,
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null),
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item-on-object",                     result.StepType);
        Assert.Equal(InferredFrom.InventoryEventItemUsed,      result.InferredFrom);
        Assert.NotEqual("talk",                                result.StepType);
    }

    // =========================================================================
    // S5_T8 -- Rule 3.5n loses to QuestCompleted (Rule 1)
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_LosesToQuestCompleted_Rule1Wins_S5_T8()
    {
        // CONTRACT: Given QuestCompleted=true AND InventoryEventAddonSeen + ItemUsed set,
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1 wins).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = before with
        {
            QuestCompleted = true,
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null),
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in",                             result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted,           result.InferredFrom);
    }

    // =========================================================================
    // S5_T9 -- InventoryItem kind preserved in inference Notes
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_InventoryItemKind_PreservedInNotes_S5_T9()
    {
        // CONTRACT: Given ItemUsed with Kind=InventoryItem, ItemId=4554,
        //           When Infer is called,
        //           Then StepType="use-item-on-object",
        //                Notes contains "Kind=InventoryItem" and "ItemId=4554".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.InventoryItem,
                ItemId:       4554u,
                TargetBaseId: null,
                TargetPosition: null),
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item-on-object",                     result.StepType);
        Assert.NotNull(result.Notes);
        Assert.Contains("Kind=InventoryItem", result.Notes, StringComparison.Ordinal);
        Assert.Contains("ItemId=4554", result.Notes, StringComparison.Ordinal);
    }

    // =========================================================================
    // S5_T10 -- ObjectInteracted present -> InteractableId in step ID
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_ObjectInteractedPresent_InteractableIdInStepId_S5_T10()
    {
        // CONTRACT: Given ObjectInteracted with InteractableId=2001500,
        //           When Infer is called,
        //           Then SuggestedStepId == "use-item-2000456-on-2001500".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null),
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item-2000456-on-2001500", result.SuggestedStepId);
    }

    // =========================================================================
    // S5_T11 -- ObjectInteracted absent -> InteractableId=0 in step ID
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_ObjectInteractedAbsent_InteractableIdZero_S5_T11()
    {
        // CONTRACT: Given InventoryEventAddonSeen=true, ItemUsed set, ObjectInteracted=null,
        //           When Infer is called,
        //           Then SuggestedStepId == "use-item-2000456-on-0".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
            // ObjectInteracted = null (default)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item-on-object",          result.StepType);
        Assert.Equal("use-item-2000456-on-0",       result.SuggestedStepId);
    }

    // =========================================================================
    // S5_T12 -- Rule 3.5n loses to SayChatMessageSent (Rule 3.5s)
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_LosesToSayChatMessageSent_Rule35sWins_S5_T12()
    {
        // CONTRACT: Given SayChatMessageSent AND InventoryEventAddonSeen + ItemUsed set,
        //           When Infer is called,
        //           Then StepType="say-chat-message" (Rule 3.5s wins).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            SayChatMessageSent = new SayChatMessageSentSignal("hello", null),
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",                    result.StepType);
        Assert.Equal(InferredFrom.SayChatMessageSent,       result.InferredFrom);
    }

    // =========================================================================
    // S5_T13 -- Rule 3.5n loses to QuestAccepted (Rule 2)
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_LosesToQuestAccepted_Rule2Wins_S5_T13()
    {
        // CONTRACT: Given QuestAccepted=true AND InventoryEventAddonSeen + ItemUsed set,
        //           When Infer is called,
        //           Then StepType="accept" (Rule 2 wins).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questAccepted: false);
        var after  = before with
        {
            QuestAccepted = true,
            InventoryEventAddonSeen = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("accept",                          result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted,        result.InferredFrom);
    }

    // =========================================================================
    // S5_T14 -- Both RequestAddonSeen AND InventoryEventAddonSeen + KeyItemsRemoved
    //           -> Rule 2.4 wins (hand-over-item)
    // =========================================================================

    [Fact]
    public void UseItemOnObjectInference_BothAddonsAndKeyItemsRemoved_Rule24Wins_S5_T14()
    {
        // CONTRACT: Given RequestAddonSeen=true, InventoryEventAddonSeen=true,
        //           KeyItemsRemoved=[2000456], ItemUsed set,
        //           When Infer is called,
        //           Then StepType="hand-over-item" (Rule 2.4 fires first because
        //           RequestAddonSeen is true). InferredFrom=DialogueInteraction.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            RequestAddonSeen = true,
            InventoryEventAddonSeen = true,
            KeyItemsRemoved = [2000456u],
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("hand-over-item",                         result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction,         result.InferredFrom);
    }

    // =========================================================================
    // StepFactory tests
    // =========================================================================

    // =========================================================================
    // S5_F1 -- Builds UseItemOnObjectStep from ObjectInteracted + ItemUsed
    // =========================================================================

    [Fact]
    public void StepFactory_UseItemOnObject_ProducesStep_WithCorrectFields_S5_F1()
    {
        // CONTRACT: Given after with ObjectInteracted(2001500, 10.5, 5.0, 20.3),
        //           ItemUsed(KeyItem, 2000456, null, null), InventoryEventAddonSeen=true,
        //           When Build("use-item-on-object", "use-item-2000456-on-2001500", null, after),
        //           Then step is UseItemOnObjectStep with:
        //             InteractableId=2001500, Position=(10.5, 5.0, 20.3),
        //             Kind=KeyItem, ItemId=2000456, Expect=null.

        var after = MakeSnapshot() with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10.5f, 5.0f, 20.3f),
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null),
            InventoryEventAddonSeen = true
        };

        var step = StepFactory.Build("use-item-on-object", "use-item-2000456-on-2001500", null, after);

        var uio = Assert.IsType<QuestForge.Schema.UseItemOnObjectStep>(step);
        Assert.Equal("use-item-2000456-on-2001500", uio.Id);
        Assert.Equal(2001500u,                      uio.InteractableId);
        Assert.Equal(10.5f,                         uio.Position.X);
        Assert.Equal(5.0f,                          uio.Position.Y);
        Assert.Equal(20.3f,                         uio.Position.Z);
        Assert.Equal(QuestForge.Schema.ItemKind.KeyItem, uio.Kind);
        Assert.Equal(2000456u,                      uio.ItemId);
        Assert.Null(uio.Expect);
    }

    // =========================================================================
    // S5_F2 -- Null signals -> zero defaults (defensive)
    // =========================================================================

    [Fact]
    public void StepFactory_UseItemOnObject_NullSignals_FallsBackToDefaults_S5_F2()
    {
        // CONTRACT: Given after with ObjectInteracted=null, ItemUsed=null (defensive),
        //           When Build("use-item-on-object", "use-item-X", null, after),
        //           Then step is UseItemOnObjectStep (no exception),
        //                InteractableId=0, Position=(0,0,0),
        //                Kind=KeyItem (defensive default), ItemId=0.

        var after = MakeSnapshot();
        // ObjectInteracted = null, ItemUsed = null (defaults)

        var ex = Record.Exception(() =>
        {
            var step = StepFactory.Build("use-item-on-object", "use-item-X", null, after);
            var uio = Assert.IsType<QuestForge.Schema.UseItemOnObjectStep>(step);
            Assert.Equal(0u,  uio.InteractableId);
            Assert.Equal(0f,  uio.Position.X);
            Assert.Equal(0f,  uio.Position.Y);
            Assert.Equal(0f,  uio.Position.Z);
            Assert.Equal(QuestForge.Schema.ItemKind.KeyItem, uio.Kind);
            Assert.Equal(0u,  uio.ItemId);
        });

        Assert.Null(ex);
    }

    // =========================================================================
    // S5_F3 -- InventoryItem kind flows through
    // =========================================================================

    [Fact]
    public void StepFactory_UseItemOnObject_InventoryItemKind_FlowsThrough_S5_F3()
    {
        // CONTRACT: Given ItemUsed with Kind=InventoryItem, ItemId=4554,
        //           When Build("use-item-on-object", "use-item-4554-on-2001500", null, after),
        //           Then step.Kind=InventoryItem, step.ItemId=4554.

        var after = MakeSnapshot() with
        {
            ObjectInteracted = new ObjectInteractedSignal(2001500u, 10f, 5f, 20f),
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.InventoryItem,
                ItemId:       4554u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var step = StepFactory.Build("use-item-on-object", "use-item-4554-on-2001500", null, after);

        var uio = Assert.IsType<QuestForge.Schema.UseItemOnObjectStep>(step);
        Assert.Equal(QuestForge.Schema.ItemKind.InventoryItem, uio.Kind);
        Assert.Equal(4554u,                                     uio.ItemId);
    }

    // =========================================================================
    // SnapshotAggregator tests
    // =========================================================================

    // =========================================================================
    // S5_A1 -- OnRequestAddonSeen sets flag on Current
    // =========================================================================

    [Fact]
    public void Aggregator_OnRequestAddonSeen_SetsFlag_S5_A1()
    {
        // CONTRACT: Given a fresh SnapshotAggregator,
        //           When OnRequestAddonSeen() is called,
        //           Then Current.RequestAddonSeen == true.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnRequestAddonSeen();

        Assert.True(agg.Current.RequestAddonSeen);
    }

    // =========================================================================
    // S5_A2 -- OnInventoryEventAddonSeen sets flag on Current
    // =========================================================================

    [Fact]
    public void Aggregator_OnInventoryEventAddonSeen_SetsFlag_S5_A2()
    {
        // CONTRACT: Given a fresh SnapshotAggregator,
        //           When OnInventoryEventAddonSeen() is called,
        //           Then Current.InventoryEventAddonSeen == true.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnInventoryEventAddonSeen();

        Assert.True(agg.Current.InventoryEventAddonSeen);
    }

    // =========================================================================
    // S5_A3 -- ResetDeltas clears both flags
    // =========================================================================

    [Fact]
    public void Aggregator_ResetDeltas_ClearsBothAddonFlags_S5_A3()
    {
        // CONTRACT: Given both flags set,
        //           When ResetDeltas() is called,
        //           Then Current.RequestAddonSeen == false
        //                AND Current.InventoryEventAddonSeen == false.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnRequestAddonSeen();
        agg.OnInventoryEventAddonSeen();
        Assert.True(agg.Current.RequestAddonSeen);      // sanity
        Assert.True(agg.Current.InventoryEventAddonSeen); // sanity

        agg.ResetDeltas();

        Assert.False(agg.Current.RequestAddonSeen);
        Assert.False(agg.Current.InventoryEventAddonSeen);
    }

    // =========================================================================
    // S5_A4 -- Flags are idempotent -- calling twice does not change state
    // =========================================================================

    [Fact]
    public void Aggregator_AddonFlags_Idempotent_S5_A4()
    {
        // CONTRACT: Given a fresh SnapshotAggregator,
        //           When OnRequestAddonSeen() called twice,
        //           Then Current.RequestAddonSeen == true (no exception, no double-set).
        //           Same for OnInventoryEventAddonSeen.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        var ex = Record.Exception(() =>
        {
            agg.OnRequestAddonSeen();
            agg.OnRequestAddonSeen();
            agg.OnInventoryEventAddonSeen();
            agg.OnInventoryEventAddonSeen();
        });

        Assert.Null(ex);
        Assert.True(agg.Current.RequestAddonSeen);
        Assert.True(agg.Current.InventoryEventAddonSeen);
    }
}
