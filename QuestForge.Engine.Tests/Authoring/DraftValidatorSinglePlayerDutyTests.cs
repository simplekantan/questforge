using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// DraftValidator tests for the new SinglePlayerDutyStep validator rules.
/// Spec: docs/DUTY_STEP_SPLIT_PLAN.md -- Section 7, scenarios SPDV_T1 through SPDV_T10.
///
/// All tests reference SinglePlayerDutyStep and SpdEntryKind which do NOT yet exist (RED).
/// These tests will FAIL TO COMPILE until the builder implements:
///   - SinglePlayerDutyStep sealed class in Step.cs
///   - SpdEntryKind enum in Step.cs
///   - E30, E31, E32, E33 validator rules in DraftValidator.cs
///   - W15 validator rule in DraftValidator.cs
///   - W1 suppression guard for SinglePlayerDutyStep
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorSinglePlayerDutyTests"
/// </summary>
public sealed class DraftValidatorSinglePlayerDutyTests
{
    // =========================================================================
    // SPDV_T1 -- E30: ContentFinderConditionId == 0
    // =========================================================================

    /// <summary>
    /// SPDV_T1: Given QuestDraft with a SinglePlayerDutyStep where
    /// ContentFinderConditionId = 0; When DraftValidator.Validate(draft);
    /// Then Errors contains exactly one entry with Code == "E30",
    /// Message contains "ContentFinderConditionId == 0".
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_CfcIdZero_E30Fires_SPDV_T1()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-cfc-zero", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-cfc-zero",
                ContentFinderConditionId = 0,
                EntryKind = SpdEntryKind.Proximity,                    // RED: enum does not exist
                EntryTargetId = null,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E30");
        Assert.Contains("ContentFinderConditionId == 0", result.Errors[0].Message);
    }

    // =========================================================================
    // SPDV_T2 -- E31: EntryKind is Talk, EntryTargetId is null
    // =========================================================================

    /// <summary>
    /// SPDV_T2: Given QuestDraft with a SinglePlayerDutyStep where
    /// EntryKind = Talk, EntryTargetId = null; When DraftValidator.Validate(draft);
    /// Then Errors contains exactly one entry with Code == "E31",
    /// Message contains "EntryTargetId is null".
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_TalkNullTarget_E31Fires_SPDV_T2()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-talk-null", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-talk-null",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Talk,                         // RED: enum does not exist
                EntryTargetId = null,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E31");
        Assert.Contains("EntryTargetId is null", result.Errors[0].Message);
    }

    // =========================================================================
    // SPDV_T3 -- E31: EntryKind is Interact, EntryTargetId is null
    // =========================================================================

    /// <summary>
    /// SPDV_T3: Given QuestDraft with a SinglePlayerDutyStep where
    /// EntryKind = Interact, EntryTargetId = null; When DraftValidator.Validate(draft);
    /// Then Errors contains exactly one entry with Code == "E31",
    /// Message contains "EntryTargetId is null".
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_InteractNullTarget_E31Fires_SPDV_T3()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-interact-null", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-interact-null",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Interact,                     // RED: enum does not exist
                EntryTargetId = null,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E31");
        Assert.Contains("EntryTargetId is null", result.Errors[0].Message);
    }

    // =========================================================================
    // SPDV_T4 -- E32: EntryKind is Talk, EntryTargetId == 0
    // =========================================================================

    /// <summary>
    /// SPDV_T4: Given QuestDraft with a SinglePlayerDutyStep where
    /// EntryKind = Talk, EntryTargetId = 0; When DraftValidator.Validate(draft);
    /// Then Errors contains exactly one entry with Code == "E32",
    /// Message contains "EntryTargetId == 0".
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_TalkZeroTarget_E32Fires_SPDV_T4()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-talk-zero", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-talk-zero",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Talk,                         // RED: enum does not exist
                EntryTargetId = 0u,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E32");
        Assert.Contains("EntryTargetId == 0", result.Errors[0].Message);
    }

    // =========================================================================
    // SPDV_T5 -- E32: EntryKind is Interact, EntryTargetId == 0
    // =========================================================================

    /// <summary>
    /// SPDV_T5: Given QuestDraft with a SinglePlayerDutyStep where
    /// EntryKind = Interact, EntryTargetId = 0; When DraftValidator.Validate(draft);
    /// Then Errors contains exactly one entry with Code == "E32",
    /// Message contains "EntryTargetId == 0".
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_InteractZeroTarget_E32Fires_SPDV_T5()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-interact-zero", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-interact-zero",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Interact,                     // RED: enum does not exist
                EntryTargetId = 0u,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E32");
        Assert.Contains("EntryTargetId == 0", result.Errors[0].Message);
    }

    // =========================================================================
    // SPDV_T6 -- E33: EntryKind is Proximity, EntryTargetId is set
    // =========================================================================

    /// <summary>
    /// SPDV_T6: Given QuestDraft with a SinglePlayerDutyStep where
    /// EntryKind = Proximity, EntryTargetId = 5000;
    /// When DraftValidator.Validate(draft);
    /// Then Errors contains exactly one entry with Code == "E33",
    /// Message contains "Proximity" and "EntryTargetId is set".
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_ProximityWithTarget_E33Fires_SPDV_T6()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-prox-target", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-prox-target",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Proximity,                    // RED: enum does not exist
                EntryTargetId = 5000u,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E33");
        Assert.Contains("Proximity", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EntryTargetId is set", result.Errors[0].Message);
    }

    // =========================================================================
    // SPDV_T7 -- W15: no Expect, spin-loop warning
    // =========================================================================

    /// <summary>
    /// SPDV_T7: Given QuestDraft with a SinglePlayerDutyStep where Expect is null;
    /// When DraftValidator.Validate(draft); Then Warnings contains exactly one entry
    /// with Code == "W15", Message contains "spin-loop", W1 is NOT present.
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_NoExpect_W15Fires_SPDV_T7()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-no-expect", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-no-expect",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Talk,                         // RED: enum does not exist
                EntryTargetId = 1045123u,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = null  // missing Expect -- W15 fires
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W15");
        Assert.Contains("spin-loop", result.Warnings[0].Message, StringComparison.OrdinalIgnoreCase);
        // W1 must NOT fire -- SinglePlayerDutyStep is in the W1 suppression guard
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }

    // =========================================================================
    // SPDV_T8 -- Valid SinglePlayerDutyStep (talk): no errors or warnings
    // =========================================================================

    /// <summary>
    /// SPDV_T8: Given QuestDraft with a SinglePlayerDutyStep where
    /// CFC=830, EntryKind=Talk, EntryTargetId=1045123,
    /// Expect="questSequence(91001) >= 3";
    /// When DraftValidator.Validate(draft); Then no errors E30-E33, no W15.
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_ValidTalk_Clean_SPDV_T8()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-valid-talk", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-valid-talk",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Talk,                         // RED: enum does not exist
                EntryTargetId = 1045123u,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(91001) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // SPDV_T9 -- Valid SinglePlayerDutyStep (proximity, null target): no errors
    // =========================================================================

    /// <summary>
    /// SPDV_T9: Given QuestDraft with a SinglePlayerDutyStep where
    /// CFC=832, EntryKind=Proximity, EntryTargetId=null,
    /// Expect="questSequence(91009) >= 3";
    /// When DraftValidator.Validate(draft); Then no errors E30-E33.
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_ValidProximity_Clean_SPDV_T9()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-valid-prox", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-valid-prox",
                ContentFinderConditionId = 832,
                EntryKind = SpdEntryKind.Proximity,                    // RED: enum does not exist
                EntryTargetId = null,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(91009) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // SPDV_T10 -- Multiple errors: E30 and E31 can fire simultaneously
    // =========================================================================

    /// <summary>
    /// SPDV_T10: Given QuestDraft with a SinglePlayerDutyStep where
    /// CFC=0, EntryKind=Talk, EntryTargetId=null;
    /// When DraftValidator.Validate(draft);
    /// Then errors contain both E30 (CFC == 0) and E31 (EntryTargetId null).
    /// </summary>
    [Fact]
    public void Validate_SinglePlayerDutyStep_MultipleErrors_E30AndE31_SPDV_T10()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-multi-error", 2,
            new SinglePlayerDutyStep                                   // RED: type does not exist
            {
                Id = "spd-multi-error",
                ContentFinderConditionId = 0,
                EntryKind = SpdEntryKind.Talk,                         // RED: enum does not exist
                EntryTargetId = null,
                EntryPosition = new Position3(10f, 0f, 10f),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertErrorCodes(result, "E30", "E31");
    }
}
