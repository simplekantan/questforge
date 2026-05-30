using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names (AetheryteId). Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for UseItemStep authoring inference -- covers three areas:
///   UII1..UII9   : StepInferenceEngine Rule 3.5i -- ItemUsed (priority pinning)
///   UII10..UII10b: SnapshotAggregator.OnItemUsed / OnItemUsedConsumed
///   UII11..UII12 : StepFactory.Build "use-item" arm
///   UII13        : Ground-target position flows through inference
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - ItemUsedSignal record in GameStateSnapshot.cs                       (Task UI-INF-T2)
///   - GameStateSnapshot.ItemUsed property                                 (Task UI-INF-T2)
///   - InferredFrom.ItemUsed enum value                                    (Task UI-INF-T3)
///   - SnapshotAggregator.OnItemUsed / OnItemUsedConsumed                  (Task UI-INF-T4)
///   - StepInferenceEngine Rule 3.5i (ItemUsed check)                      (Task UI-INF-T5)
///   - StepFactory.Build "use-item" arm                                    (Task UI-INF-T6)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~UseItemInferenceTests"
/// </summary>
public sealed class UseItemInferenceTests
{
    private static readonly QuestId       ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0          = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors UseActionInferenceTests.MakeSnapshot)
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ZoneId?    zone           = null,
        bool       questAccepted  = false,
        bool       questCompleted = false,
        int        questSequence  = 0,
        IReadOnlyList<uint>? keyItemsAdded   = null,
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
            KeyItemsAdded   = keyItemsAdded,
            KeyItemsRemoved = keyItemsRemoved
        };

    // =========================================================================
    // UII1 -- Happy path, key item, no target, no ground position:
    //         ItemUsed set -> infers "use-item" step
    // =========================================================================

    [Fact]
    public void UseItemInference_HappyPath_KeyItem_NoTarget_InfersUseItemStep_UII1()
    {
        // CONTRACT: Given ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null) in after,
        //           no zone change and no quest changes,
        //           When Infer is called,
        //           Then StepType="use-item", SuggestedStepId="use-item-2000456",
        //                SuggestedExpect=null, Confidence=High, InferredFrom=ItemUsed,
        //                Notes contains "Expect" and "ItemId=2000456".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                    result.StepType);
        Assert.Equal("use-item-2000456",            result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.High,               result.Confidence);
        Assert.Equal(InferredFrom.ItemUsed,         result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("Expect", result.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ItemId=2000456", result.Notes, StringComparison.Ordinal);
    }

    // =========================================================================
    // UII2 -- Happy path, inventory item, with NPC target:
    //         SuggestedStepId has "-on-<id>" suffix
    // =========================================================================

    [Fact]
    public void UseItemInference_InventoryItem_WithTarget_StepIdIncludesOnSuffix_UII2()
    {
        // CONTRACT: Given ItemUsed = ItemUsedSignal(InventoryItem, 4554, 1000789, null),
        //           When Infer is called,
        //           Then SuggestedStepId="use-item-4554-on-1000789",
        //                InferredFrom=ItemUsed.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.InventoryItem,
                ItemId:       4554u,
                TargetBaseId: 1000789u,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                    result.StepType);
        Assert.Equal("use-item-4554-on-1000789",    result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(InferredFrom.ItemUsed,         result.InferredFrom);
    }

    // =========================================================================
    // UII3 -- Self-cast: TargetBaseId null -> step id has NO "-on-" suffix
    // =========================================================================

    [Fact]
    public void UseItemInference_SelfCast_StepIdHasNoOnSuffix_UII3()
    {
        // CONTRACT: Given ItemUsed = ItemUsedSignal(InventoryItem, 4554, null, null),
        //           When Infer is called,
        //           Then SuggestedStepId="use-item-4554" -- NO "-on-" suffix.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.InventoryItem,
                ItemId:       4554u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                  result.StepType);
        Assert.Equal("use-item-4554",             result.SuggestedStepId);
        Assert.DoesNotContain("-on-",             result.SuggestedStepId, StringComparison.Ordinal);
        Assert.Equal(InferredFrom.ItemUsed,       result.InferredFrom);
    }

    // =========================================================================
    // UII4 -- Priority ABOVE Rule 3 (QuestSequence advanced): ItemUsed wins
    // =========================================================================

    [Fact]
    public void UseItemInference_WinsOverQuestSequenceAdvance_UII4()
    {
        // CONTRACT: Given ItemUsed set AND QuestSequence advances (1->2) in the same window,
        //           When Infer is called,
        //           Then StepType="use-item" (NOT "talk") because Rule 3.5i fires
        //           BEFORE Rule 3 (per Decision UI-INF-5).
        //
        // RATIONALE: If the item use IS the quest objective, inferring "talk" would be wrong.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 1);
        var after  = before with
        {
            QuestSequence = 2,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: 1000789u,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                result.StepType);
        Assert.Equal(InferredFrom.ItemUsed,     result.InferredFrom);
        Assert.NotEqual("talk",                 result.StepType);
    }

    // =========================================================================
    // UII5 -- Priority ABOVE Rule 3.5 (ActionCompleted): ItemUsed wins when both set
    // =========================================================================

    [Fact]
    public void UseItemInference_WinsOverActionCompleted_WhenBothSet_UII5()
    {
        // CONTRACT: Given both ActionCompleted AND ItemUsed set (defensive -- mutually
        //           exclusive in production but fields are independent),
        //           When Infer is called,
        //           Then StepType="use-item" (NOT "use-action").
        //           Pins Decision UI-INF-5: item is more specific than generic action.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: null),
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                result.StepType);
        Assert.Equal(InferredFrom.ItemUsed,     result.InferredFrom);
        Assert.NotEqual("use-action",           result.StepType);
    }

    // =========================================================================
    // UII6 -- Priority ABOVE Rule 3.5e (EmoteCompleted): ItemUsed wins when both set
    // =========================================================================

    [Fact]
    public void UseItemInference_WinsOverEmoteCompleted_WhenBothSet_UII6()
    {
        // CONTRACT: Given both EmoteCompleted AND ItemUsed set (defensive),
        //           When Infer is called,
        //           Then StepType="use-item" (NOT "use-emote").
        //           Pins Decision UI-INF-5: item use is the more specific authoring intent.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null),
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                result.StepType);
        Assert.Equal(InferredFrom.ItemUsed,     result.InferredFrom);
        Assert.NotEqual("use-emote",            result.StepType);
    }

    // =========================================================================
    // UII7 -- Priority BELOW Rule 1 (QuestCompleted): turn-in wins over ItemUsed
    // =========================================================================

    [Fact]
    public void UseItemInference_LosesToQuestCompleted_Rule1Wins_UII7()
    {
        // CONTRACT: Given QuestCompleted=true AND ItemUsed set in the same window,
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1), NOT "use-item".
        //
        // RATIONALE: "Quest completed" is the definitive authoring intent -- item was incidental.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = before with
        {
            QuestCompleted = true,
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in",                     result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted,   result.InferredFrom);
        Assert.NotEqual("use-item",                 result.StepType);
    }

    // =========================================================================
    // UII8 -- Priority BELOW Rule 2.4 (KeyItemsRemoved): hand-over-item wins over ItemUsed
    //         Pins natural disambiguation: item used AND consumed -> hand-over-item.
    // =========================================================================

    [Fact]
    public void UseItemInference_LosesToKeyItemsRemoved_Rule24Wins_UII8()
    {
        // CONTRACT: Given KeyItemsRemoved=[2000456] AND ItemUsed set (same item),
        //           When Infer is called,
        //           Then StepType="hand-over-item" (Rule 2.4), NOT "use-item".
        //
        // Pins the natural disambiguation: when a key item is used AND consumed (removed),
        // hand-over-item wins because item removal is the observable game state change.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            KeyItemsRemoved = [2000456u],
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("hand-over-item",                  result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction,  result.InferredFrom);
        Assert.NotEqual("use-item",                     result.StepType);
    }

    // =========================================================================
    // UII9 -- Priority BELOW Rule 3.5s (SayChatMessageSent): chat wins over ItemUsed
    // =========================================================================

    [Fact]
    public void UseItemInference_LosesToSayChatMessageSent_Rule35sWins_UII9()
    {
        // CONTRACT: Given SayChatMessageSent AND ItemUsed set in the same window,
        //           When Infer is called,
        //           Then StepType="say-chat-message" (Rule 3.5s), NOT "use-item".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            SayChatMessageSent = new SayChatMessageSentSignal("hello", null),
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",                result.StepType);
        Assert.Equal(InferredFrom.SayChatMessageSent,   result.InferredFrom);
        Assert.NotEqual("use-item",                     result.StepType);
    }

    // =========================================================================
    // UII10 -- Aggregator: OnItemUsed sets field (with TargetPosition);
    //          ResetDeltas does NOT clear; OnItemUsedConsumed clears.
    // =========================================================================

    [Fact]
    public void Aggregator_OnItemUsed_SetsField_ResetDeltasSurvives_OnItemUsedConsumedClears_UII10()
    {
        // CONTRACT (sub-A): Given a fresh SnapshotAggregator,
        //   When OnItemUsed(KeyItem, 2000456, 1000789, Position3(100.5, 20.0, -30.3)) is called,
        //   Then agg.Current.ItemUsed is non-null with correct Kind, ItemId, TargetBaseId, TargetPosition.
        //
        // CONTRACT (sub-B): When ResetDeltas() is called,
        //   Then agg.Current.ItemUsed is STILL non-null (survives per-window lifecycle).
        //
        // CONTRACT (sub-C): When OnItemUsedConsumed() is called,
        //   Then agg.Current.ItemUsed is null.
        //   AND LastNpcInteracted is null (no side effects -- pins Decision UI-INF-4).

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        // -- sub-A: OnItemUsed sets the field --
        agg.OnItemUsed(
            QuestForge.Schema.ItemKind.KeyItem,
            itemId:       2000456u,
            targetBaseId: 1000789u,
            targetPosition: new QuestForge.Schema.Position3(100.5f, 20.0f, -30.3f));

        var snapA = agg.Current;

        Assert.NotNull(snapA.ItemUsed);
        Assert.Equal(QuestForge.Schema.ItemKind.KeyItem, snapA.ItemUsed!.Kind);
        Assert.Equal(2000456u, snapA.ItemUsed.ItemId);
        Assert.Equal(1000789u, snapA.ItemUsed.TargetBaseId);
        Assert.NotNull(snapA.ItemUsed.TargetPosition);
        Assert.Equal(100.5f,   snapA.ItemUsed.TargetPosition!.X);
        Assert.Equal(20.0f,    snapA.ItemUsed.TargetPosition.Y);
        Assert.Equal(-30.3f,   snapA.ItemUsed.TargetPosition.Z);

        // -- sub-B: ResetDeltas does NOT clear ItemUsed --
        agg.ResetDeltas();
        var snapB = agg.Current;

        Assert.NotNull(snapB.ItemUsed);
        Assert.Equal(2000456u, snapB.ItemUsed!.ItemId);

        // -- sub-C: OnItemUsedConsumed clears without side effects --
        agg.OnItemUsedConsumed();
        var snapC = agg.Current;

        Assert.Null(snapC.ItemUsed);
        // Side-effect guard (Decision UI-INF-4)
        Assert.Null(snapC.LastNpcInteracted);
        Assert.Null(snapC.LastAttuned);
        Assert.Null(snapC.LastAethernetShardInteracted);
    }

    // =========================================================================
    // UII10b -- Aggregator: OnItemUsed with null TargetPosition (non-ground use)
    // =========================================================================

    [Fact]
    public void Aggregator_OnItemUsed_NullTargetPosition_UII10b()
    {
        // CONTRACT: Given OnItemUsed(KeyItem, 2000456, 1000789, null),
        //           Then agg.Current.ItemUsed.TargetPosition is null.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnItemUsed(
            QuestForge.Schema.ItemKind.KeyItem,
            itemId:       2000456u,
            targetBaseId: 1000789u,
            targetPosition: null);

        var snap = agg.Current;

        Assert.NotNull(snap.ItemUsed);
        Assert.Null(snap.ItemUsed!.TargetPosition);
    }

    // =========================================================================
    // UII11 -- StepFactory "use-item" arm: builds UseItemStep with snapshot fields
    //          (no ground target)
    // =========================================================================

    [Fact]
    public void StepFactory_UseItem_ProducesUseItemStep_NoGroundTarget_UII11()
    {
        // CONTRACT: Given after.ItemUsed = ItemUsedSignal(KeyItem, 2000456, 1000789, null),
        //           When Build("use-item", "use-item-2000456-on-1000789", null, after),
        //           Then:
        //             - step is UseItemStep
        //             - Id == "use-item-2000456-on-1000789"
        //             - Kind == KeyItem
        //             - ItemId == 2000456u
        //             - TargetNpcId == 1000789u
        //             - TargetPosition is null
        //             - Expect is null

        var after = MakeSnapshot() with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: 1000789u,
                TargetPosition: null)
        };

        var step = StepFactory.Build("use-item", "use-item-2000456-on-1000789", null, after);

        var ui = Assert.IsType<QuestForge.Schema.UseItemStep>(step);
        Assert.Equal("use-item-2000456-on-1000789", ui.Id);
        Assert.Equal(QuestForge.Schema.ItemKind.KeyItem, ui.Kind);
        Assert.Equal(2000456u, ui.ItemId);
        Assert.Equal(1000789u, ui.TargetNpcId);
        Assert.Null(ui.TargetPosition);
        Assert.Null(ui.Expect);
    }

    // =========================================================================
    // UII11b -- StepFactory "use-item" arm with ground-target position:
    //           TargetPosition populated from signal
    // =========================================================================

    [Fact]
    public void StepFactory_UseItem_WithGroundTarget_TargetPositionPopulated_UII11b()
    {
        // CONTRACT: Given after.ItemUsed = ItemUsedSignal(InventoryItem, 4554, null, Position3(100.5, 20.0, -30.3)),
        //           When Build("use-item", "use-item-4554", null, after),
        //           Then:
        //             - step is UseItemStep
        //             - TargetPosition is not null with correct X/Y/Z
        //             - TargetNpcId is null (ground-target, no NPC)

        var after = MakeSnapshot() with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.InventoryItem,
                ItemId:       4554u,
                TargetBaseId: null,
                TargetPosition: new QuestForge.Schema.Position3(100.5f, 20.0f, -30.3f))
        };

        var step = StepFactory.Build("use-item", "use-item-4554", null, after);

        var ui = Assert.IsType<QuestForge.Schema.UseItemStep>(step);
        Assert.NotNull(ui.TargetPosition);
        Assert.Equal(100.5f,  ui.TargetPosition!.X);
        Assert.Equal(20.0f,   ui.TargetPosition.Y);
        Assert.Equal(-30.3f,  ui.TargetPosition.Z);
        Assert.Null(ui.TargetNpcId);
    }

    // =========================================================================
    // UII12 -- StepFactory "use-item" arm defensive: ItemUsed null -> no throw
    // =========================================================================

    [Fact]
    public void StepFactory_UseItem_NullItemUsed_FallsBackToDefaults_UII12()
    {
        // CONTRACT: Given after.ItemUsed is null (defensive caller),
        //           When Build("use-item", "use-item-X", null, after) is called,
        //           Then:
        //             - step is UseItemStep (no exception)
        //             - ItemId == 0u (fallback; validator catches via E13)
        //             - Kind == ItemKind.KeyItem (defensive default)
        //             - TargetNpcId is null
        //             - TargetPosition is null

        var after = MakeSnapshot(); // ItemUsed is null

        var ex = Record.Exception(() =>
        {
            var step = StepFactory.Build("use-item", "use-item-X", null, after);
            var ui = Assert.IsType<QuestForge.Schema.UseItemStep>(step);
            Assert.Equal(0u,  ui.ItemId);
            Assert.Equal(QuestForge.Schema.ItemKind.KeyItem, ui.Kind);
            Assert.Null(ui.TargetNpcId);
            Assert.Null(ui.TargetPosition);
        });

        Assert.Null(ex);
    }

    // =========================================================================
    // UII13 -- Inference with ground-target position: ItemUsed carries TargetPosition
    //          The inference rule itself does not inspect TargetPosition -- it fires
    //          on ItemUsed is not null. Position flows through StepFactory to the step.
    // =========================================================================

    [Fact]
    public void UseItemInference_WithGroundTargetPosition_InfersUseItem_UII13()
    {
        // CONTRACT: Given ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, Position3(150, 10, -50)),
        //           When Infer is called,
        //           Then StepType="use-item", SuggestedStepId="use-item-2000456" (no NPC, no -on-),
        //                Confidence=High, InferredFrom=ItemUsed.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: new QuestForge.Schema.Position3(150.0f, 10.0f, -50.0f))
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                result.StepType);
        Assert.Equal("use-item-2000456",        result.SuggestedStepId);
        Assert.Equal(InferredFrom.ItemUsed,     result.InferredFrom);
        Assert.Equal(Confidence.High,           result.Confidence);
    }
}
