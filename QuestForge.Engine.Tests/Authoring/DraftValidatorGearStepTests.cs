using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Failing tests for gear step validator rules (E16, E17, E18).
/// Spec: docs/GEAR_SCHEMA_UPDATE_PLAN.md -- Sections 5 and 7.
///
/// These tests reference the NEW schema shapes:
///   - EquipGearForQuestStep.ItemIds (uint[]) -- replaces GearItem[] Items
///   - ChangeJobStep.JobId (uint)             -- replaces string Job
///   - EquipBestGearStep with no Constraints  -- GearConstraints removed
///
/// All tests will fail to compile until the builder updates Step.cs and
/// adds E16/E17/E18 rules to DraftValidator.cs.
///
/// W1 suppression: gear steps do NOT suppress W1 (they are not spin-loop-prone).
/// GS-V9 and GS-V10 verify W1 fires normally for these step types.
/// </summary>
public sealed class DraftValidatorGearStepTests
{
    // =========================================================================
    // GS-V1: E16 -- EquipGearForQuestStep with empty ItemIds
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an EquipGearForQuestStep with ItemIds = [],
    /// When Validate() is called,
    /// Then exactly one error with Code == "E16" and StepIndices == [1].
    /// The message contains "empty ItemIds".
    /// </summary>
    [Fact]
    public void Validate_EquipGearForQuest_EmptyItemIds_RaisesE16_GS_V1()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("equip-empty", 2,
            new EquipGearForQuestStep
            {
                Id = "equip-empty",
                ItemIds = [],  // RED: property does not exist yet -- the one broken thing
                Expect = new PredicateExpect { Predicate = "playerHasEquipped(12345)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E16");
        Assert.Contains("equip-empty", result.Errors[0].Message);
        Assert.Contains("empty ItemIds", result.Errors[0].Message);
        Assert.Contains(1, result.Errors[0].StepIndices);
    }

    // =========================================================================
    // GS-V2: E16 suppressed when ItemIds has elements
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an EquipGearForQuestStep with ItemIds = [12345u],
    /// When Validate() is called,
    /// Then no error with Code == "E16".
    /// </summary>
    [Fact]
    public void Validate_EquipGearForQuest_NonEmptyItemIds_NoE16_GS_V2()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("equip-valid", 2,
            new EquipGearForQuestStep
            {
                Id = "equip-valid",
                ItemIds = [12345u],  // RED: property does not exist yet
                Expect = new PredicateExpect { Predicate = "playerHasEquipped(12345)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.DoesNotContain(result.Errors, e => e.Code == "E16");
    }

    // =========================================================================
    // GS-V3: E17 -- zero value in ItemIds at specific index
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an EquipGearForQuestStep with ItemIds = [12345u, 0u, 67890u],
    /// When Validate() is called,
    /// Then exactly one error with Code == "E17" and StepIndices == [1].
    /// The message contains "index 1" (the position of the zero).
    /// </summary>
    [Fact]
    public void Validate_EquipGearForQuest_ZeroInItemIds_RaisesE17_GS_V3()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("equip-zero-mid", 2,
            new EquipGearForQuestStep
            {
                Id = "equip-zero-mid",
                ItemIds = [12345u, 0u, 67890u],  // RED: property does not exist yet
                Expect = new PredicateExpect { Predicate = "playerHasEquipped(12345)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E17");
        Assert.Contains("equip-zero-mid", result.Errors[0].Message);
        Assert.Contains("index 1", result.Errors[0].Message);
        Assert.Contains(1, result.Errors[0].StepIndices);
    }

    // =========================================================================
    // GS-V4: E17 fires for each zero in ItemIds
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an EquipGearForQuestStep with ItemIds = [0u, 12345u, 0u],
    /// When Validate() is called,
    /// Then exactly two errors with Code == "E17".
    /// One message contains "index 0", the other contains "index 2".
    /// </summary>
    [Fact]
    public void Validate_EquipGearForQuest_MultipleZeros_RaisesE17ForEach_GS_V4()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("equip-multi-zero", 2,
            new EquipGearForQuestStep
            {
                Id = "equip-multi-zero",
                ItemIds = [0u, 12345u, 0u],  // RED: property does not exist yet
                Expect = new PredicateExpect { Predicate = "playerHasEquipped(12345)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        var e17Errors = result.Errors.Where(e => e.Code == "E17").ToList();
        Assert.Equal(2, e17Errors.Count);
        Assert.Contains(e17Errors, e => e.Message.Contains("index 0"));
        Assert.Contains(e17Errors, e => e.Message.Contains("index 2"));
    }

    // =========================================================================
    // GS-V5: E16 and E17 do not fire simultaneously for empty array
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an EquipGearForQuestStep with ItemIds = [],
    /// When Validate() is called,
    /// Then E16 is present but E17 is NOT present.
    /// (Empty array has no elements to check for zeros.)
    /// </summary>
    [Fact]
    public void Validate_EquipGearForQuest_EmptyArray_E16Only_NotE17_GS_V5()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("equip-empty-no-e17", 2,
            new EquipGearForQuestStep
            {
                Id = "equip-empty-no-e17",
                ItemIds = [],  // RED: property does not exist yet
                Expect = new PredicateExpect { Predicate = "playerHasEquipped(12345)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.Contains(result.Errors, e => e.Code == "E16");
        Assert.DoesNotContain(result.Errors, e => e.Code == "E17");
    }

    // =========================================================================
    // GS-V6: E18 -- ChangeJobStep with JobId == 0
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a ChangeJobStep with JobId = 0,
    /// When Validate() is called,
    /// Then exactly one error with Code == "E18" and StepIndices == [1].
    /// The message contains "JobId == 0".
    /// </summary>
    [Fact]
    public void Validate_ChangeJob_JobIdZero_RaisesE18_GS_V6()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("change-job-zero", 2,
            new ChangeJobStep
            {
                Id = "change-job-zero",
                JobId = 0,  // RED: property does not exist yet -- the one broken thing
                Expect = new PredicateExpect { Predicate = "currentJob() == 19" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E18");
        Assert.Contains("change-job-zero", result.Errors[0].Message);
        Assert.Contains("JobId == 0", result.Errors[0].Message);
        Assert.Contains(1, result.Errors[0].StepIndices);
    }

    // =========================================================================
    // GS-V7: E18 suppressed when JobId is nonzero
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a ChangeJobStep with JobId = 19,
    /// When Validate() is called,
    /// Then no error with Code == "E18".
    /// </summary>
    [Fact]
    public void Validate_ChangeJob_NonZeroJobId_NoE18_GS_V7()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("change-job-valid", 2,
            new ChangeJobStep
            {
                Id = "change-job-valid",
                JobId = 19,  // RED: property does not exist yet
                Expect = new PredicateExpect { Predicate = "currentJob() == 19" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.DoesNotContain(result.Errors, e => e.Code == "E18");
    }

    // =========================================================================
    // GS-V8: EquipBestGearStep has no step-specific errors
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an EquipBestGearStep with Expect set,
    /// When Validate() is called,
    /// Then no E16, E17, or E18 errors.
    /// </summary>
    [Fact]
    public void Validate_EquipBestGear_NoStepSpecificErrors_GS_V8()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("gear-up", 2,
            new EquipBestGearStep
            {
                Id = "gear-up",
                Expect = new PredicateExpect { Predicate = "playerAverageItemLevel() >= 50" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.DoesNotContain(result.Errors, e => e.Code == "E16");
        Assert.DoesNotContain(result.Errors, e => e.Code == "E17");
        Assert.DoesNotContain(result.Errors, e => e.Code == "E18");
    }

    // =========================================================================
    // GS-V9: W1 fires for EquipGearForQuestStep without Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an EquipGearForQuestStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning with Code == "W1" for that step.
    /// (Gear steps do NOT suppress W1 -- they are not spin-loop-prone.)
    /// </summary>
    [Fact]
    public void Validate_EquipGearForQuest_NoExpect_RaisesW1_GS_V9()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("equip-no-expect", 2,
            new EquipGearForQuestStep
            {
                Id = "equip-no-expect",
                ItemIds = [12345u],  // RED: property does not exist yet
                Expect = null        // missing Expect -- W1 fires
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }

    // =========================================================================
    // GS-V10: W1 fires for ChangeJobStep without Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a ChangeJobStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning with Code == "W1" for that step.
    /// (Gear steps do NOT suppress W1 -- they are not spin-loop-prone.)
    /// </summary>
    [Fact]
    public void Validate_ChangeJob_NoExpect_RaisesW1_GS_V10()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("change-job-no-expect", 2,
            new ChangeJobStep
            {
                Id = "change-job-no-expect",
                JobId = 19,  // RED: property does not exist yet
                Expect = null  // missing Expect -- W1 fires
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }
}
