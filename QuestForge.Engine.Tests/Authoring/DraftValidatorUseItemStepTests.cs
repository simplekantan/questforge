using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Failing tests for UseItemStep validator rules (E13, E14, E15, W10).
/// Spec: docs/USE_ITEM_STEP_PLAN.md â€” Decision UI12, scenarios UI14â€“UI18.
///
/// W10 / W1 interaction (documented here for the builder):
///   W10 fires AND suppresses W1 for UseItemStep-with-null-Expect.
///   Implementation: extend the W1 skip-guard to also exclude UseItemStep:
///     step.Raw is not UseActionStep and not UseEmoteStep and not SayChatMessageStep and not UseItemStep
///   Then emit W10 in its own loop for UseItemStep-with-null-Expect.
///
/// Prerequisites (builder tasks):
///   - Add ItemKind enum to QuestForge.Schema/SharedValueTypes.cs
///   - Rewrite UseItemStep schema shape (Kind, ItemId, TargetNpcId, TargetPosition)
///   - Add E13 (ItemId == 0), E14 (TargetNpcId == 0), E15 (both targets set), W10 (no Expect)
///   - Extend W1 exclusion list to include UseItemStep
/// </summary>
public sealed class DraftValidatorUseItemStepTests
{
    // =========================================================================
    // UI14 â€” E13: ItemId == 0 raises an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseItemStep with ItemId = 0,
    /// When Validate() is called,
    /// Then AssertSingleError "E13" â€” zero is never a valid item id.
    /// </summary>
    [Fact]
    public void Validate_UseItemStep_ItemIdZero_RaisesE13_UI14()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-zero-id", 2,
            new UseItemStep
            {
                Id = "use-item-zero-id",
                Kind = ItemKind.KeyItem,
                ItemId = 0,                     // the one broken thing â€” zero is never valid
                TargetNpcId = null,
                TargetPosition = null,
                Expect = new PredicateExpect { Predicate = "questFlag(82114, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E13");
        Assert.Contains("use-item-zero-id", result.Errors[0].Message);
        Assert.Contains("ItemId", result.Errors[0].Message);
    }

    // =========================================================================
    // UI15 â€” E14: TargetNpcId == 0 raises an error; null does NOT raise E14
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseItemStep with TargetNpcId = 0 (explicit zero),
    /// When Validate() is called,
    /// Then AssertSingleError "E14" â€” explicit zero is never a valid NPC id;
    /// null is the correct sentinel for "no NPC target".
    ///
    /// Defensive sub-case: TargetNpcId = null must NOT trigger E14.
    /// </summary>
    [Fact]
    public void Validate_UseItemStep_TargetNpcIdZero_RaisesE14_UI15()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-zero-npc", 2,
            new UseItemStep
            {
                Id = "use-item-zero-npc",
                Kind = ItemKind.InventoryItem,
                ItemId = 4554u,
                TargetNpcId = 0u,               // the one broken thing â€” explicit zero is invalid
                TargetPosition = null,
                Expect = new PredicateExpect { Predicate = "questFlag(82115, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E14");
        Assert.Contains("use-item-zero-npc", result.Errors[0].Message);
        Assert.Contains("TargetNpcId", result.Errors[0].Message);

        // Defensive: TargetNpcId = null must NOT trigger E14
        var draftWithNullTarget = DraftValidatorTestData.ValidBaseline();
        draftWithNullTarget.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-null-npc", 2,
            new UseItemStep
            {
                Id = "use-item-null-npc",
                Kind = ItemKind.InventoryItem,
                ItemId = 4554u,
                TargetNpcId = null,             // null is allowed â€” must not raise E14
                TargetPosition = null,
                Expect = new PredicateExpect { Predicate = "questFlag(82115, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var nullTargetResult = new DraftValidator().Validate(draftWithNullTarget);
        Assert.DoesNotContain(nullTargetResult.Errors, e => e.Code == "E14");
    }

    // =========================================================================
    // UI16 â€” E15: both TargetNpcId and TargetPosition set â†’ ambiguous target
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseItemStep with both TargetNpcId and TargetPosition set,
    /// When Validate() is called,
    /// Then AssertSingleError "E15" â€” ambiguous target: at most one of the two may be set.
    ///
    /// Defensive sub-case: exactly one target set (NPC or position) must NOT trigger E15.
    /// </summary>
    [Fact]
    public void Validate_UseItemStep_BothTargetsSet_RaisesE15_UI16()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-ambiguous-target", 2,
            new UseItemStep
            {
                Id = "use-item-ambiguous-target",
                Kind = ItemKind.KeyItem,
                ItemId = 2000456u,
                TargetNpcId = 1000789u,                         // both set â€” the one broken thing
                TargetPosition = new Position3(1f, 2f, 3f),    // both set â€” the one broken thing
                Expect = new PredicateExpect { Predicate = "questFlag(82116, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E15");
        Assert.Contains("use-item-ambiguous-target", result.Errors[0].Message);
        // Message must mention both fields so the author understands the mutual exclusion
        Assert.Contains("TargetNpcId", result.Errors[0].Message);

        // Defensive sub-case A: only TargetNpcId set â€” must NOT trigger E15
        var draftNpcOnly = DraftValidatorTestData.ValidBaseline();
        draftNpcOnly.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-npc-only", 2,
            new UseItemStep
            {
                Id = "use-item-npc-only",
                Kind = ItemKind.KeyItem,
                ItemId = 2000456u,
                TargetNpcId = 1000789u,
                TargetPosition = null,
                Expect = new PredicateExpect { Predicate = "questFlag(82116, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        Assert.DoesNotContain(new DraftValidator().Validate(draftNpcOnly).Errors, e => e.Code == "E15");

        // Defensive sub-case B: only TargetPosition set â€” must NOT trigger E15
        var draftPosOnly = DraftValidatorTestData.ValidBaseline();
        draftPosOnly.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-pos-only", 2,
            new UseItemStep
            {
                Id = "use-item-pos-only",
                Kind = ItemKind.KeyItem,
                ItemId = 2000123u,
                TargetNpcId = null,
                TargetPosition = new Position3(1f, 2f, 3f),
                Expect = new PredicateExpect { Predicate = "questFlag(82116, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        Assert.DoesNotContain(new DraftValidator().Validate(draftPosOnly).Errors, e => e.Code == "E15");
    }

    // =========================================================================
    // UI17 â€” W10: missing Expect raises W10; W1 is suppressed for UseItemStep
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseItemStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning W10; W1 must NOT fire for the same step.
    /// The W10 message must contain "spin-loop" per Decision UI12.
    /// Mirrors the W8/W1 suppression pattern for UseEmoteStep (Decision UE7).
    /// </summary>
    [Fact]
    public void Validate_UseItemStep_NoExpect_RaisesW10_SuppressesW1_UI17()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-no-expect", 2,
            new UseItemStep
            {
                Id = "use-item-no-expect",
                Kind = ItemKind.InventoryItem,
                ItemId = 4554u,
                TargetNpcId = null,
                TargetPosition = null
                // Expect is null â€” the one broken thing
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // Exactly one warning, and it must be W10 (not W1)
        DraftValidatorAssertions.AssertSingleWarning(result, "W10");
        Assert.Contains("spin-loop", result.Warnings[0].Message, StringComparison.OrdinalIgnoreCase);
        // W1 must be suppressed for UseItemStep (mirrors UseActionStep's W7/W1 suppression)
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }

    // =========================================================================
    // UI18 â€” Clean baseline: well-formed UseItemStep â†’ zero errors, zero warnings
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a well-formed UseItemStep (valid Kind, non-zero ItemId,
    /// valid TargetNpcId, no TargetPosition, Expect authored),
    /// When Validate() is called,
    /// Then zero errors and zero warnings.
    /// Pins "no rule fires unexpectedly on a clean step."
    /// </summary>
    [Fact]
    public void Validate_UseItemStep_WellFormed_ZeroErrorsZeroWarnings_UI18()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-item-clean", 2,
            new UseItemStep
            {
                Id = "use-item-clean",
                Kind = ItemKind.KeyItem,
                ItemId = 2000456u,
                TargetNpcId = 1000789u,
                TargetPosition = null,
                Expect = new PredicateExpect { Predicate = "questFlag(82118, 3)" }
            },
            notes: "uses the bell"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }
}
