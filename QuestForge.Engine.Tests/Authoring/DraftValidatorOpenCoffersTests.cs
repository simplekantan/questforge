using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// W1 validator tests for OpenCoffersStep.
/// Spec: docs/OPEN_COFFERS_STEP_PLAN.md -- Decision OC-13, scenarios OC13-OC14.
///
/// RED: Will fail to compile until Builder implements OpenCoffersStep in
/// QuestForge.Schema/Step.cs. The DraftValidator code itself does NOT need changes --
/// OpenCoffersStep is not in the W1 suppression list, so W1 fires automatically.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorOpenCoffersTests"
/// </summary>
public sealed class DraftValidatorOpenCoffersTests
{
    // =========================================================================
    // OC13 -- W1 fires for OpenCoffersStep without Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an OpenCoffersStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning with Code == "W1" is present.
    /// OpenCoffersStep has NO implicit postcondition (per OC-13), so W1 fires.
    /// </summary>
    [Fact]
    public void Validate_OpenCoffers_NoExpect_W1Fires_OC13()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("open-coffers-no-expect", 2,
            new OpenCoffersStep
            {
                Id = "open-coffers-no-expect",
                Expect = null  // missing Expect -- W1 fires (NOT suppressed per OC-13)
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // W1 must fire for OpenCoffersStep (no implicit postcondition)
        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }

    // =========================================================================
    // OC14 -- W1 does NOT fire for OpenCoffersStep with Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an OpenCoffersStep with Expect present,
    /// When Validate() is called,
    /// Then W1 does NOT fire.
    /// </summary>
    [Fact]
    public void Validate_OpenCoffers_WithExpect_NoW1_OC14()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("open-coffers-with-expect", 2,
            new OpenCoffersStep
            {
                Id = "open-coffers-with-expect",
                Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // W1 must NOT fire when Expect is present
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }
}
