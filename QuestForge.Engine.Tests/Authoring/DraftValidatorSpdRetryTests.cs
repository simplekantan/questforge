using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// DraftValidator tests for E22: DutyStep.EntryTargetId == 0.
/// Spec: docs/SPD_RETRY_PLAN.md -- Decision R7, scenarios R-S9 through R-S11.
///
/// New rule referenced:
///   - E22: EntryTargetId == 0 on any DutyStep (null is OK; explicit 0 is invalid)
///
/// RED: DutyStep.EntryTargetId does not exist yet. Tests will FAIL TO COMPILE.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorSpdRetryTests"
/// </summary>
public sealed class DraftValidatorSpdRetryTests
{
    // =========================================================================
    // R-S9 -- E22 fires: EntryTargetId == 0
    // =========================================================================

    /// <summary>
    /// Given a draft with one DutyStep(kind: "spd") with EntryTargetId = 0,
    /// When DraftValidator.Validate() is called,
    /// Then exactly one error with Code == "E22" is present,
    /// and its message contains "EntryTargetId=0".
    /// </summary>
    [Fact]
    public void Validate_SpdStep_EntryTargetIdZero_E22Fires()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-entry-zero", 2,
            new DutyStep
            {
                Id = "spd-entry-zero",
                Kind = "spd",
                EntryTargetId = 0u,                                // RED: property does not exist
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E22");
        Assert.Contains("EntryTargetId=0", result.Errors[0].Message);
    }

    // =========================================================================
    // R-S10 -- E22 does NOT fire: EntryTargetId null
    // =========================================================================

    /// <summary>
    /// Given a draft with one DutyStep(kind: "spd") with EntryTargetId = null,
    /// When DraftValidator.Validate() is called,
    /// Then no E22 error is present.
    /// (W1 may fire for missing Expect on SPD -- that is orthogonal.)
    /// </summary>
    [Fact]
    public void Validate_SpdStep_EntryTargetIdNull_NoE22()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-entry-null", 2,
            new DutyStep
            {
                Id = "spd-entry-null",
                Kind = "spd",
                // EntryTargetId intentionally not set (defaults to null)
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.DoesNotContain(result.Errors, e => e.Code == "E22");
    }

    // =========================================================================
    // R-S11 -- E22 does NOT fire: EntryTargetId valid (non-zero, non-null)
    // =========================================================================

    /// <summary>
    /// Given a draft with one DutyStep(kind: "spd") with EntryTargetId = 5000,
    /// When DraftValidator.Validate() is called,
    /// Then no E22 error is present.
    /// </summary>
    [Fact]
    public void Validate_SpdStep_EntryTargetIdValid_NoE22()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-entry-valid", 2,
            new DutyStep
            {
                Id = "spd-entry-valid",
                Kind = "spd",
                EntryTargetId = 5000u,                             // RED: property does not exist
                EntryPosition = new Position3(10f, 0f, 20f),      // RED: property does not exist
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.DoesNotContain(result.Errors, e => e.Code == "E22");
    }

    // =========================================================================
    // R-S11b -- E22 fires for kind "duty" too, not just "spd"
    // =========================================================================

    /// <summary>
    /// E22 applies to ALL DutyStep kinds, not just "spd".
    /// Given a DutyStep(kind: "duty") with EntryTargetId = 0,
    /// When DraftValidator.Validate() is called,
    /// Then E22 fires (in addition to any other rules for kind "duty").
    /// </summary>
    [Fact]
    public void Validate_DutyStep_KindDuty_EntryTargetIdZero_E22Fires()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("duty-entry-zero", 2,
            new DutyStep
            {
                Id = "duty-entry-zero",
                Kind = "duty",
                ContentFinderConditionId = 2,
                EntryTargetId = 0u,                                // RED: property does not exist
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.Contains(result.Errors, e => e.Code == "E22");
        var e22 = result.Errors.First(e => e.Code == "E22");
        Assert.Contains("EntryTargetId=0", e22.Message);
    }
}
