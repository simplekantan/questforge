using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// DraftValidator tests for DutyStep(kind: "duty").
/// Spec: docs/DUTY_AUTODUTY_PLAN.md -- Decisions AD14, scenarios D14-D16.
///
/// New rules referenced:
///   - E21: ContentFinderConditionId == 0 on kind "duty"               (TODO)
///   - W11: DutyStep(kind:"duty") missing Expect (spin-loop warning)   (TODO)
///   - W1 suppression update: DutyStep excluded from W1                (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorDutyTests"
/// </summary>
public sealed class DraftValidatorDutyTests
{
    // =========================================================================
    // D14 -- E21: ContentFinderConditionId == 0 on kind "duty"
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a DutyStep(kind: "duty") with ContentFinderConditionId = 0,
    /// When Validate() is called,
    /// Then exactly one error with Code == "E13" is present,
    /// and its message mentions "ContentFinderConditionId=0".
    /// </summary>
    [Fact]
    public void Validate_DutyStep_CfcIdZero_E21Fires()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("duty-cfc-zero", 2,
            new DutyStep
            {
                Id = "duty-cfc-zero",
                Kind = "duty",
                ContentFinderConditionId = 0,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E21");
        Assert.Contains("ContentFinderConditionId=0", result.Errors[0].Message);
    }

    // =========================================================================
    // D14-positive -- DutyStep(kind: "duty") with valid CFC ID and Expect -> clean
    // =========================================================================

    [Fact]
    public void Validate_DutyStep_ValidCfcIdAndExpect_Clean()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("duty-valid", 2,
            new DutyStep
            {
                Id = "duty-valid",
                Kind = "duty",
                ContentFinderConditionId = 2,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // D15 -- W11: DutyStep(kind:"duty") missing Expect -> spin-loop warning
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a DutyStep(kind: "duty") with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning with Code == "W11" is present,
    /// and its message contains "spin-loop".
    /// </summary>
    [Fact]
    public void Validate_DutyStep_NoExpect_W11Fires()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("duty-no-expect", 2,
            new DutyStep
            {
                Id = "duty-no-expect",
                Kind = "duty",
                ContentFinderConditionId = 2,
                Expect = null  // missing Expect -- W11 fires
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W11");
        Assert.Contains("spin-loop", result.Warnings[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D16 -- W1 does NOT fire for DutyStep (W11 covers it)
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a DutyStep(kind: "duty") with Expect = null,
    /// When Validate() is called,
    /// Then W11 is present but W1 is NOT present for the same step.
    /// This verifies the W1 suppression guard excludes DutyStep.
    /// </summary>
    [Fact]
    public void Validate_DutyStep_NoExpect_W1DoesNotFire()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("duty-no-expect-w1", 2,
            new DutyStep
            {
                Id = "duty-no-expect-w1",
                Kind = "duty",
                ContentFinderConditionId = 2,
                Expect = null
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // W11 fires (DutyStep-specific spin-loop warning)
        Assert.Contains(result.Warnings, w => w.Code == "W11");
        // W1 does NOT fire (suppressed for DutyStep)
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }

    // =========================================================================
    // D14-SPD -- E21 does NOT fire for DutyStep(kind: "spd") even if CFC = 0
    // =========================================================================

    /// <summary>
    /// E21 is specific to kind: "duty". SPD steps with ContentFinderConditionId = 0
    /// (or null) should not trigger E13 -- CFC is irrelevant for SPD.
    /// </summary>
    [Fact]
    public void Validate_SpdStep_CfcIdZero_E21DoesNotFire()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-cfc-zero", 2,
            new DutyStep
            {
                Id = "spd-cfc-zero",
                Kind = "spd",
                ContentFinderConditionId = 0,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.DoesNotContain(result.Errors, e => e.Code == "E21");
    }
}
