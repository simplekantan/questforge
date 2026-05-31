using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: DraftValidator tests for RegisterGearsetStep.
/// Spec: docs/REGISTER_GEARSET_STEP_PLAN.md -- Decision RG-7, scenario RG12.
///
/// RG12: W1 fires for RegisterGearsetStep without Expect (NOT suppressed).
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - RegisterGearsetStep : Step { } (empty sealed class)                 (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorRegisterGearsetTests"
/// </summary>
public sealed class DraftValidatorRegisterGearsetTests
{
    // =========================================================================
    // RG12 -- W1 fires for RegisterGearsetStep without Expect
    // =========================================================================

    [Fact]
    public void Validate_RegisterGearsetStep_NoExpect_W1Fires_RG12()
    {
        // CONTRACT: Given RegisterGearsetStep { Expect = null },
        //           When DraftValidator.Validate(draft),
        //           Then warnings contains W1 for the register-gearset step.
        //           RegisterGearsetStep is NOT in the W1 suppression list (per RG-7).

        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("register-gearset-no-expect-rg12", 2,
            new RegisterGearsetStep
            {
                Id = "register-gearset-no-expect-rg12",
                Expect = null  // missing Expect -- W1 fires (NOT suppressed per RG-7)
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // W1 must fire for RegisterGearsetStep (no implicit postcondition, spin-loop risk)
        Assert.Contains(result.Warnings, w => w.Code == "W1");
    }

    // =========================================================================
    // RG12b -- W1 does NOT fire for RegisterGearsetStep with Expect
    // =========================================================================

    [Fact]
    public void Validate_RegisterGearsetStep_WithExpect_NoW1_RG12b()
    {
        // CONTRACT: Given RegisterGearsetStep { Expect = "jobGearsetExists(32)" },
        //           When DraftValidator.Validate(draft),
        //           Then warnings does NOT contain W1.

        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("register-gearset-with-expect", 2,
            new RegisterGearsetStep
            {
                Id = "register-gearset-with-expect",
                Expect = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // W1 must NOT fire when Expect is present
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }
}
