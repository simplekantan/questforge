using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// W1 validator tests for DutyStep(kind: "spd").
/// Spec: docs/SINGLE_PLAYER_DUTY_PLAN.md -- Validation rules section.
///
/// DutyStep is NOT in the W1 suppression list, so W1 fires automatically
/// when Expect is missing. No production code changes needed for the
/// DraftValidator -- these tests verify existing behavior is correct.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorSpdTests"
/// </summary>
public sealed class DraftValidatorSpdTests
{
    // =========================================================================
    // W1 fires for DutyStep(kind: "spd") without Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a DutyStep(kind: "spd") with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning with Code == "W1" is present.
    /// DutyStep is NOT in the W1 suppression list, so W1 fires.
    /// The warning message should contain "spin-loop" to inform authors.
    /// </summary>
    [Fact]
    public void Validate_SpdStep_NoExpect_W1Fires()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-no-expect", 2,
            new DutyStep
            {
                Id = "spd-no-expect",
                Kind = "spd",
                Expect = null  // missing Expect -- W1 fires
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }

    // =========================================================================
    // W1 does NOT fire for DutyStep(kind: "spd") with Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a DutyStep(kind: "spd") with Expect present,
    /// When Validate() is called,
    /// Then W1 does NOT fire.
    /// </summary>
    [Fact]
    public void Validate_SpdStep_WithExpect_NoW1()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("spd-with-expect", 2,
            new DutyStep
            {
                Id = "spd-with-expect",
                Kind = "spd",
                Expect = new PredicateExpect { Predicate = "questSequence(70099) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }
}
