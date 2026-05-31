using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for RegisterGearsetStep authoring inference -- covers:
///   RGI1..RGI4 : StepInferenceEngine Rule 3.5k -- GearsetRegistered
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - GearsetRegisteredSignal record in GameStateSnapshot.cs              (TODO)
///   - GameStateSnapshot.GearsetRegistered property                        (TODO)
///   - InferredFrom.GearsetRegistered enum value                           (TODO)
///   - SnapshotAggregator.OnGearsetRegistered / OnGearsetRegisteredConsumed (TODO)
///   - StepInferenceEngine Rule 3.5k (GearsetRegistered check)             (TODO)
///   - StepFactory.Build "register-gearset" arm                            (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~RegisterGearsetInferenceTests"
/// </summary>
public sealed class RegisterGearsetInferenceTests
{
    private static readonly QuestId        ActiveQuest = new(2055);
    private static readonly DateTimeOffset T0          = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors ChangeJobInferenceTests.MakeSnapshot)
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
    // RGI1 -- GearsetRegistered signal fires inference rule 3.5k
    // =========================================================================

    [Fact]
    public void RegisterGearsetInference_GearsetRegistered_InfersRegisterGearsetStep_RGI1()
    {
        // CONTRACT: Given GearsetRegistered = GearsetRegisteredSignal(5, 6) in after,
        //           When Infer is called,
        //           Then StepType="register-gearset", InferredFrom=GearsetRegistered,
        //                Confidence=High, SuggestedStepId="register-gearset".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            GearsetRegistered = new GearsetRegisteredSignal(5, 6)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("register-gearset",                result.StepType);
        Assert.Equal(InferredFrom.GearsetRegistered,    result.InferredFrom);
        Assert.Equal(Confidence.High,                   result.Confidence);
        Assert.Equal("register-gearset",                result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
    }

    // =========================================================================
    // RGI2 -- JobChanged has higher priority than GearsetRegistered
    // =========================================================================

    [Fact]
    public void RegisterGearsetInference_LosesToJobChanged_Rule35jWins_RGI2()
    {
        // CONTRACT: Given both JobChanged AND GearsetRegistered set in after,
        //           When Infer is called,
        //           Then StepType="change-job" (Rule 3.5j > Rule 3.5k).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            JobChanged = new JobChangedSignal(19, 32),
            GearsetRegistered = new GearsetRegisteredSignal(5, 6)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("change-job",                  result.StepType);
        Assert.Equal(InferredFrom.JobChanged,       result.InferredFrom);
        Assert.NotEqual("register-gearset",         result.StepType);
    }

    // =========================================================================
    // RGI3 -- GearsetRegistered has higher priority than EquipmentChanged
    // =========================================================================

    [Fact]
    public void RegisterGearsetInference_WinsOverEquipmentChanged_Rule35kOverRule35g_RGI3()
    {
        // CONTRACT: Given both GearsetRegistered AND EquipmentChanged set in after,
        //           When Infer is called,
        //           Then StepType="register-gearset" (Rule 3.5k > Rule 3.5g).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            GearsetRegistered = new GearsetRegisteredSignal(5, 6),
            EquipmentChanged = new EquipmentChangedSignal([12345u])
        };

        var result = engine.Infer(before, after);

        Assert.Equal("register-gearset",                    result.StepType);
        Assert.Equal(InferredFrom.GearsetRegistered,        result.InferredFrom);
        Assert.NotEqual("equip-gear-for-quest",             result.StepType);
    }

    // =========================================================================
    // RGI4 -- StepFactory builds RegisterGearsetStep from inference result
    // =========================================================================

    [Fact]
    public void StepFactory_RegisterGearset_BuildsEmptyStep_RGI4()
    {
        // CONTRACT: Given after.GearsetRegistered = GearsetRegisteredSignal(5, 6),
        //           When Build("register-gearset", ..., after) is called,
        //           Then result is RegisterGearsetStep with Id = "register-gearset".

        var after = MakeSnapshot() with
        {
            GearsetRegistered = new GearsetRegisteredSignal(5, 6)
        };

        var step = StepFactory.Build("register-gearset", "register-gearset", null, after);

        var rgs = Assert.IsType<QuestForge.Schema.RegisterGearsetStep>(step);
        Assert.Equal("register-gearset", rgs.Id);
        Assert.Null(rgs.Expect);
    }
}
