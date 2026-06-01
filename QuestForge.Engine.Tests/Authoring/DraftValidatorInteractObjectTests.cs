using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// DraftValidator tests for InteractObjectStep and PickupItemStep.
/// Spec: docs/INTERACT_OBJECT_STEP_PLAN.md -- Decisions IO8, IO10,
/// scenarios IO-V1 through IO-V5.
///
/// RED: Will fail to compile until Builder flattens InteractObjectStep/PickupItemStep
/// (InteractableId property on the step itself, not nested in InteractableTarget).
/// E19/E20 rules and W1 non-suppression are the validation targets.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DraftValidatorInteractObjectTests"
/// </summary>
public sealed class DraftValidatorInteractObjectTests
{
    // =========================================================================
    // IO-V1 -- E19 fires for InteractObjectStep with InteractableId == 0
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an InteractObjectStep with InteractableId = 0,
    /// When Validate() is called,
    /// Then exactly one error with Code == "E19" is present.
    /// </summary>
    [Fact]
    public void Validate_InteractObject_InteractableIdZero_E19Fires_IOV1()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("interact-zero-id", 2,
            new InteractObjectStep
            {
                Id = "interact-zero-id",
                InteractableId = 0u,                                     // RED: property does not exist yet
                Position = new Position3(10f, 0f, 10f),                  // RED: property does not exist yet
                Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E19");
    }

    // =========================================================================
    // IO-V2 -- E20 fires for PickupItemStep with InteractableId == 0
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PickupItemStep with InteractableId = 0,
    /// When Validate() is called,
    /// Then exactly one error with Code == "E20" is present.
    /// </summary>
    [Fact]
    public void Validate_PickupItem_InteractableIdZero_E20Fires_IOV2()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("pickup-zero-id", 2,
            new PickupItemStep
            {
                Id = "pickup-zero-id",
                InteractableId = 0u,                                     // RED: property does not exist yet
                Position = new Position3(5f, 0f, 5f),                    // RED: property does not exist yet
                Expect = new PredicateExpect { Predicate = "questFlag(65657, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E20");
    }

    // =========================================================================
    // IO-V3 -- No E19/E20 for valid InteractableId
    // =========================================================================

    /// <summary>
    /// Given the baseline plus InteractObjectStep(InteractableId=2001234) and
    /// PickupItemStep(InteractableId=2001500), both with valid Expect,
    /// When Validate() is called,
    /// Then no E19 or E20 errors (clean result).
    /// </summary>
    [Fact]
    public void Validate_InteractAndPickup_ValidIds_NoErrors_IOV3()
    {
        var draft = DraftValidatorTestData.ValidBaseline();

        draft.AddStep(DraftValidatorTestData.MakeDraftStep("interact-valid", 2,
            new InteractObjectStep
            {
                Id = "interact-valid",
                InteractableId = 2001234u,                               // RED
                Position = new Position3(10f, 0f, 10f),                  // RED
                Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        draft.AddStep(DraftValidatorTestData.MakeDraftStep("pickup-valid", 3,
            new PickupItemStep
            {
                Id = "pickup-valid",
                InteractableId = 2001500u,                               // RED
                Position = new Position3(5f, 0f, 5f),                    // RED
                Expect = new PredicateExpect { Predicate = "questFlag(65657, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // IO-V4 -- W1 fires for InteractObjectStep with no Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an InteractObjectStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning with Code == "W1" is present.
    /// InteractObjectStep is NOT in the W1 suppression list (per IO8).
    /// </summary>
    [Fact]
    public void Validate_InteractObject_NoExpect_W1Fires_IOV4()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("interact-no-expect", 2,
            new InteractObjectStep
            {
                Id = "interact-no-expect",
                InteractableId = 2001234u,                               // RED
                Position = new Position3(10f, 0f, 10f),                  // RED
                Expect = null  // missing Expect -- W1 fires (NOT suppressed per IO8)
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }

    // =========================================================================
    // IO-V5 -- W1 fires for PickupItemStep with no Expect
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PickupItemStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning with Code == "W1" is present.
    /// PickupItemStep is NOT in the W1 suppression list (per IO8).
    /// </summary>
    [Fact]
    public void Validate_PickupItem_NoExpect_W1Fires_IOV5()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("pickup-no-expect", 2,
            new PickupItemStep
            {
                Id = "pickup-no-expect",
                InteractableId = 2001234u,                               // RED
                Position = new Position3(5f, 0f, 5f),                    // RED
                Expect = null  // missing Expect -- W1 fires (NOT suppressed per IO8)
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }
}
