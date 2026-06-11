using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// DraftValidator tests for the new DungeonTrialStep validator rules.
/// Spec: docs/DUTY_STEP_SPLIT_PLAN.md -- Section 7, scenarios DTV_T1 through DTV_T3.
///
/// All tests reference DungeonTrialStep which does NOT yet exist (RED).
/// These tests will FAIL TO COMPILE until the builder implements:
///   - DungeonTrialStep sealed class in Step.cs
///   - E29 validator rule in DraftValidator.cs
///   - W14 validator rule in DraftValidator.cs
///   - W1 suppression guard for DungeonTrialStep
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorDungeonTrialTests"
/// </summary>
public sealed class DraftValidatorDungeonTrialTests
{
    // =========================================================================
    // DTV_T1 -- E29: ContentFinderConditionId == 0
    // =========================================================================

    /// <summary>
    /// DTV_T1: Given QuestDraft with a single DungeonTrialStep where
    /// ContentFinderConditionId = 0; When DraftValidator.Validate(draft);
    /// Then Errors contains exactly one entry with Code == "E29",
    /// Message contains "ContentFinderConditionId == 0".
    /// </summary>
    [Fact]
    public void Validate_DungeonTrialStep_CfcIdZero_E29Fires_DTV_T1()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("dt-cfc-zero", 2,
            new DungeonTrialStep                                       // RED: type does not exist
            {
                Id = "dt-cfc-zero",
                ContentFinderConditionId = 0,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E29");
        Assert.Contains("ContentFinderConditionId == 0", result.Errors[0].Message);
    }

    // =========================================================================
    // DTV_T2 -- W14: no Expect, spin-loop warning
    // =========================================================================

    /// <summary>
    /// DTV_T2: Given QuestDraft with a single DungeonTrialStep where Expect is null;
    /// When DraftValidator.Validate(draft); Then Warnings contains exactly one entry
    /// with Code == "W14", Message contains "spin-loop", W1 is NOT present.
    /// </summary>
    [Fact]
    public void Validate_DungeonTrialStep_NoExpect_W14Fires_DTV_T2()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("dt-no-expect", 2,
            new DungeonTrialStep                                       // RED: type does not exist
            {
                Id = "dt-no-expect",
                ContentFinderConditionId = 2,
                Expect = null  // missing Expect -- W14 fires
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W14");
        Assert.Contains("spin-loop", result.Warnings[0].Message, StringComparison.OrdinalIgnoreCase);
        // W1 must NOT fire -- DungeonTrialStep is in the W1 suppression guard
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }

    // =========================================================================
    // DTV_T3 -- Valid DungeonTrialStep: no errors or warnings
    // =========================================================================

    /// <summary>
    /// DTV_T3: Given QuestDraft with a DungeonTrialStep where ContentFinderConditionId = 2,
    /// Expect = "questSequence(90001) >= 3"; When DraftValidator.Validate(draft);
    /// Then no errors with code E29, no warnings with code W14, no W1 warning.
    /// </summary>
    [Fact]
    public void Validate_DungeonTrialStep_ValidCfcIdAndExpect_Clean_DTV_T3()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("dt-valid", 2,
            new DungeonTrialStep                                       // RED: type does not exist
            {
                Id = "dt-valid",
                ContentFinderConditionId = 2,
                Expect = new PredicateExpect { Predicate = "questSequence(90001) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }
}
