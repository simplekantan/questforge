using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for ChangeJobStep authoring inference -- covers:
///   CJI1..CJI4 : StepInferenceEngine Rule 3.5j -- JobChanged
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - JobChangedSignal record in GameStateSnapshot.cs                   (TODO)
///   - GameStateSnapshot.JobChanged property                             (TODO)
///   - InferredFrom.JobChanged enum value                                (TODO)
///   - SnapshotAggregator.OnJobChanged / OnJobChangedConsumed            (TODO)
///   - StepInferenceEngine Rule 3.5j (JobChanged check)                  (TODO)
///   - StepFactory.Build "change-job" arm                                (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~ChangeJobInferenceTests"
/// </summary>
public sealed class ChangeJobInferenceTests
{
    private static readonly QuestId        ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0          = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors EquipGearInferenceTests.MakeSnapshot)
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
    // CJI1 -- JobChanged signal fires inference rule 3.5j
    // =========================================================================

    [Fact]
    public void ChangeJobInference_JobChanged_InfersChangeJobStep_CJI1()
    {
        // CONTRACT: Given JobChanged = JobChangedSignal(19, 32) in after,
        //           When Infer is called,
        //           Then StepType="change-job", InferredFrom=JobChanged,
        //                Confidence=High, SuggestedStepId="change-job-32".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            JobChanged = new JobChangedSignal(19, 32)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("change-job",              result.StepType);
        Assert.Equal(InferredFrom.JobChanged,   result.InferredFrom);
        Assert.Equal(Confidence.High,           result.Confidence);
        Assert.Equal("change-job-32",           result.SuggestedStepId);
    }

    // =========================================================================
    // CJI2 -- JobChanged has higher priority than EquipmentChanged
    // =========================================================================

    [Fact]
    public void ChangeJobInference_WinsOverEquipmentChanged_Rule35jOverRule35g_CJI2()
    {
        // CONTRACT: Given both JobChanged AND EquipmentChanged set in after,
        //           When Infer is called,
        //           Then StepType="change-job" (Rule 3.5j > 3.5g).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            JobChanged = new JobChangedSignal(19, 32),
            EquipmentChanged = new EquipmentChangedSignal([12345u])
        };

        var result = engine.Infer(before, after);

        Assert.Equal("change-job",                  result.StepType);
        Assert.Equal(InferredFrom.JobChanged,       result.InferredFrom);
        Assert.NotEqual("equip-gear-for-quest",     result.StepType);
    }

    // =========================================================================
    // CJI3 -- StepFactory builds ChangeJobStep from inference result
    // =========================================================================

    [Fact]
    public void StepFactory_ChangeJob_BuildsStepWithCorrectJobId_CJI3()
    {
        // CONTRACT: Given after.JobChanged = JobChangedSignal(19, 32),
        //           When Build("change-job", ..., after) is called,
        //           Then result is ChangeJobStep with JobId = 32.

        var after = MakeSnapshot() with
        {
            JobChanged = new JobChangedSignal(19, 32)
        };

        var step = StepFactory.Build("change-job", "change-job-32", null, after);

        var cjs = Assert.IsType<QuestForge.Schema.ChangeJobStep>(step);
        Assert.Equal("change-job-32", cjs.Id);
        Assert.Equal(32u, cjs.JobId);
        Assert.Null(cjs.Expect);
    }

    // =========================================================================
    // CJI4 -- ItemUsed has higher priority than JobChanged
    // =========================================================================

    [Fact]
    public void ChangeJobInference_LosesToItemUsed_Rule35iWins_CJI4()
    {
        // CONTRACT: Given both ItemUsed AND JobChanged set in after,
        //           When Infer is called,
        //           Then StepType="use-item" (Rule 3.5i > 3.5j).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ItemUsed = new ItemUsedSignal(
                QuestForge.Schema.ItemKind.KeyItem,
                ItemId:         2000456u,
                TargetBaseId:   null,
                TargetPosition: null),
            JobChanged = new JobChangedSignal(19, 32)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-item",                result.StepType);
        Assert.Equal(InferredFrom.ItemUsed,     result.InferredFrom);
        Assert.NotEqual("change-job",           result.StepType);
    }
}
