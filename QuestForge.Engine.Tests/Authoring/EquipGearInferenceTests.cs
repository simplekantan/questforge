using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names (AetheryteId). Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for EquipGearForQuestStep authoring inference -- covers:
///   EGI1..EGI4 : StepInferenceEngine Rule 3.5g -- EquipmentChanged
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - EquipmentChangedSignal record in GameStateSnapshot.cs              (TODO)
///   - GameStateSnapshot.EquipmentChanged property                        (TODO)
///   - InferredFrom.EquipmentChanged enum value                           (TODO)
///   - SnapshotAggregator.OnEquipmentChanged / OnEquipmentChangedConsumed (TODO)
///   - StepInferenceEngine Rule 3.5g (EquipmentChanged check)             (TODO)
///   - StepFactory.Build "equip-gear-for-quest" arm                       (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~EquipGearInferenceTests"
/// </summary>
public sealed class EquipGearInferenceTests
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
        int        questSequence  = 0) =>
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
            LastAttuned:         null);

    // =========================================================================
    // EGI1 -- EquipmentChanged signal fires inference rule 3.5g
    // =========================================================================

    [Fact]
    public void EquipGearInference_SingleItem_InfersEquipGearForQuestStep_EGI1()
    {
        // CONTRACT: Given EquipmentChanged = EquipmentChangedSignal([12345u]) in after,
        //           When Infer is called,
        //           Then StepType="equip-gear-for-quest", InferredFrom=EquipmentChanged,
        //                Confidence=High.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            EquipmentChanged = new EquipmentChangedSignal([12345u])
        };

        var result = engine.Infer(before, after);

        Assert.Equal("equip-gear-for-quest",            result.StepType);
        Assert.Equal(InferredFrom.EquipmentChanged,     result.InferredFrom);
        Assert.Equal(Confidence.High,                   result.Confidence);
    }

    // =========================================================================
    // EGI2 -- EquipmentChanged with multiple items
    // =========================================================================

    [Fact]
    public void EquipGearInference_MultipleItems_StepIdStartsWithEquipGear_EGI2()
    {
        // CONTRACT: Given EquipmentChanged = EquipmentChangedSignal([11111u, 22222u]) in after,
        //           When Infer is called,
        //           Then StepType="equip-gear-for-quest",
        //                SuggestedStepId starts with "equip-gear-".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            EquipmentChanged = new EquipmentChangedSignal([11111u, 22222u])
        };

        var result = engine.Infer(before, after);

        Assert.Equal("equip-gear-for-quest",                    result.StepType);
        Assert.StartsWith("equip-gear-",                        result.SuggestedStepId);
        Assert.Equal(InferredFrom.EquipmentChanged,             result.InferredFrom);
    }

    // =========================================================================
    // EGI3 -- StepFactory builds EquipGearForQuestStep from inference result
    // =========================================================================

    [Fact]
    public void StepFactory_EquipGearForQuest_BuildsStepWithCorrectItemIds_EGI3()
    {
        // CONTRACT: Given after.EquipmentChanged = EquipmentChangedSignal([12345u, 67890u]),
        //           When Build("equip-gear-for-quest", ..., after) is called,
        //           Then result is EquipGearForQuestStep with ItemIds = [12345u, 67890u].

        var after = MakeSnapshot() with
        {
            EquipmentChanged = new EquipmentChangedSignal([12345u, 67890u])
        };

        var step = StepFactory.Build("equip-gear-for-quest", "equip-gear-12345", null, after);

        var egs = Assert.IsType<QuestForge.Schema.EquipGearForQuestStep>(step);
        Assert.Equal("equip-gear-12345", egs.Id);
        Assert.Equal(2, egs.ItemIds.Length);
        Assert.Contains(12345u, egs.ItemIds);
        Assert.Contains(67890u, egs.ItemIds);
        Assert.Null(egs.Expect);
    }

    // =========================================================================
    // EGI4 -- EquipmentChanged has lower priority than ItemUsed
    // =========================================================================

    [Fact]
    public void EquipGearInference_LosesToItemUsed_Rule35iWins_EGI4()
    {
        // CONTRACT: Given both ItemUsed AND EquipmentChanged set in after,
        //           When Infer is called,
        //           Then StepType="use-item" (Rule 3.5i > 3.5g).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:       2000456u,
                TargetBaseId: null,
                TargetPosition: null),
            EquipmentChanged = new EquipmentChangedSignal([12345u])
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                    result.StepType);
        Assert.Equal(InferredFrom.ItemUsed,         result.InferredFrom);
        Assert.NotEqual("equip-gear-for-quest",     result.StepType);
    }
}
