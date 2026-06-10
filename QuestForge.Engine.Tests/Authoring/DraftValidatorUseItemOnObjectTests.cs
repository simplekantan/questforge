using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Failing tests for UseItemOnObjectStep validator rules (E27, E28, W13).
/// Spec: docs/USE_ITEM_ON_OBJECT_STEP_PLAN.md -- Decision UIO9, scenarios UIO_T14-UIO_T17.
///
/// W13 / W1 interaction (documented here for the builder):
///   W13 fires AND suppresses W1 for UseItemOnObjectStep-with-null-Expect.
///   Implementation: extend the W1 skip-guard to also exclude UseItemOnObjectStep:
///     step.Raw is not UseActionStep and not UseEmoteStep and not SayChatMessageStep
///         and not UseItemStep and not UseItemOnObjectStep and not EquipGearForQuestStep ...
///   Then emit W13 in its own loop for UseItemOnObjectStep-with-null-Expect.
///
/// Prerequisites (builder tasks):
///   - Add UseItemOnObjectStep to QuestForge.Schema/Step.cs
///   - Add E27 (InteractableId == 0), E28 (ItemId == 0), W13 (no Expect)
///   - Extend W1 exclusion list to include UseItemOnObjectStep
/// </summary>
public sealed class DraftValidatorUseItemOnObjectTests
{
    // =========================================================================
    // UIO_T14 -- E27: InteractableId == 0 raises an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseItemOnObjectStep with InteractableId = 0,
    /// When Validate() is called,
    /// Then AssertSingleError "E27" -- zero is never a valid interactable id.
    /// </summary>
    [Fact]
    public void Validate_UseItemOnObjectStep_InteractableIdZero_RaisesE27_UIO_T14()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("uio-zero-interactable", 2,
            new UseItemOnObjectStep
            {
                Id = "uio-zero-interactable",
                InteractableId = 0,                     // the one broken thing -- zero is never valid
                Position = new Position3(10f, 0f, 10f),
                Kind = ItemKind.KeyItem,
                ItemId = 2002001u,
                Expect = new PredicateExpect { Predicate = "questFlag(95014, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E27");
        Assert.Contains("uio-zero-interactable", result.Errors[0].Message);
        Assert.Contains("InteractableId", result.Errors[0].Message);
    }

    // =========================================================================
    // UIO_T15 -- E28: ItemId == 0 raises an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseItemOnObjectStep with ItemId = 0,
    /// When Validate() is called,
    /// Then AssertSingleError "E28" -- zero is never a valid item id.
    /// </summary>
    [Fact]
    public void Validate_UseItemOnObjectStep_ItemIdZero_RaisesE28_UIO_T15()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("uio-zero-item", 2,
            new UseItemOnObjectStep
            {
                Id = "uio-zero-item",
                InteractableId = 99001u,
                Position = new Position3(10f, 0f, 10f),
                Kind = ItemKind.KeyItem,
                ItemId = 0,                             // the one broken thing -- zero is never valid
                Expect = new PredicateExpect { Predicate = "questFlag(95015, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E28");
        Assert.Contains("uio-zero-item", result.Errors[0].Message);
        Assert.Contains("ItemId", result.Errors[0].Message);
    }

    // =========================================================================
    // UIO_T16 -- W13: missing Expect raises W13; W1 is suppressed for UseItemOnObjectStep
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseItemOnObjectStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning W13; W1 must NOT fire for the same step.
    /// The W13 message must contain "spin-loop" per Decision UIO9.
    /// Mirrors the W10/W1 suppression pattern for UseItemStep (Decision UI12).
    /// </summary>
    [Fact]
    public void Validate_UseItemOnObjectStep_NoExpect_RaisesW13_SuppressesW1_UIO_T16()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("uio-no-expect", 2,
            new UseItemOnObjectStep
            {
                Id = "uio-no-expect",
                InteractableId = 99001u,
                Position = new Position3(10f, 0f, 10f),
                Kind = ItemKind.KeyItem,
                ItemId = 2002001u
                // Expect is null -- the one broken thing
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // Exactly one warning, and it must be W13 (not W1)
        DraftValidatorAssertions.AssertSingleWarning(result, "W13");
        Assert.Contains("spin-loop", result.Warnings[0].Message, StringComparison.OrdinalIgnoreCase);
        // W1 must be suppressed for UseItemOnObjectStep
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }

    // =========================================================================
    // UIO_T17 -- Valid step: no E27, E28, or W13
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a well-formed UseItemOnObjectStep (non-zero InteractableId,
    /// non-zero ItemId, Expect authored),
    /// When Validate() is called,
    /// Then zero errors and zero warnings.
    /// Pins "no rule fires unexpectedly on a clean UseItemOnObjectStep."
    /// </summary>
    [Fact]
    public void Validate_UseItemOnObjectStep_WellFormed_ZeroErrorsZeroWarnings_UIO_T17()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("uio-clean", 2,
            new UseItemOnObjectStep
            {
                Id = "uio-clean",
                InteractableId = 99001u,
                Position = new Position3(10f, 0f, 10f),
                Kind = ItemKind.KeyItem,
                ItemId = 2002001u,
                Expect = new PredicateExpect { Predicate = "questFlag(65, 3)" }
            },
            notes: "uses the key item on the device"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }
}
