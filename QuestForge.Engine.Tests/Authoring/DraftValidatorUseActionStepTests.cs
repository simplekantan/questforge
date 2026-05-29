using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Failing tests for UseActionStep validator rules (E7, E8, W7).
/// These tests compile cleanly but FAIL at runtime because DraftValidator has no
/// UseActionStep branch yet — the builder must add E7, E8, and W7 cases.
///
/// W7 / W1 interaction decision (documented here for the builder):
///   W7 fires AND suppresses W1 for UseActionStep-with-null-Expect.
///   Rationale: W7 is more actionable (names the spin-loop risk); a double warning
///   for the same missing field adds noise without value.
///   Implementation: in the W1 loop, add `|| step.Raw is UseActionStep` to the skip-guard,
///   then emit W7 in its own loop for UseActionStep-with-null-Expect.
/// </summary>
public sealed class DraftValidatorUseActionStepTests
{
    // =========================================================================
    // T-UA1 — E7: ActionId == 0 raises an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseActionStep with ActionId = 0,
    /// When Validate() is called,
    /// Then AssertSingleError "E7" — zero is never a valid action id.
    /// </summary>
    [Fact]
    public void Validate_UseActionStep_ActionIdZero_RaisesE7()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-action-zero", 2,
            new UseActionStep
            {
                Id = "use-action-zero",
                ActionType = ActionType.Action,
                ActionId = 0,                           // << the one broken thing
                TargetNpcId = null,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E7");
        Assert.Contains("use-action-zero", result.Errors[0].Message);
        Assert.Contains("ActionId", result.Errors[0].Message);
    }

    // =========================================================================
    // T-UA2 — E7: ActionId non-zero does NOT raise E7
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseActionStep with ActionId = 145 (Sprint — a known real action),
    /// When Validate() is called,
    /// Then AssertClean — E7 must not fire for a non-zero ActionId.
    /// </summary>
    [Fact]
    public void Validate_UseActionStep_ActionIdNonZero_NoE7()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-sprint", 2,
            new UseActionStep
            {
                Id = "use-sprint",
                ActionType = ActionType.Action,
                ActionId = 145,                         // Sprint — non-zero
                TargetNpcId = null,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T-UA3 — E8: TargetNpcId explicitly 0 raises an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseActionStep with TargetNpcId = 0 (explicit zero),
    /// When Validate() is called,
    /// Then AssertSingleError "E8" — an explicit zero is never a valid NPC id;
    /// null is the correct way to express "no target".
    /// </summary>
    [Fact]
    public void Validate_UseActionStep_TargetNpcIdZero_RaisesE8()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-action-zero-npc", 2,
            new UseActionStep
            {
                Id = "use-action-zero-npc",
                ActionType = ActionType.Action,
                ActionId = 145,
                TargetNpcId = 0,                        // << explicit zero — invalid
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E8");
        Assert.Contains("use-action-zero-npc", result.Errors[0].Message);
        Assert.Contains("TargetNpcId", result.Errors[0].Message);
    }

    // =========================================================================
    // T-UA4 — E8: TargetNpcId null does NOT raise E8
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseActionStep with TargetNpcId = null,
    /// When Validate() is called,
    /// Then AssertClean — null is the correct sentinel for "untargeted action".
    /// </summary>
    [Fact]
    public void Validate_UseActionStep_TargetNpcIdNull_NoE8()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-action-no-target", 2,
            new UseActionStep
            {
                Id = "use-action-no-target",
                ActionType = ActionType.Action,
                ActionId = 145,
                TargetNpcId = null,                     // << null — valid
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T-UA5 — E8: TargetNpcId non-zero does NOT raise E8
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseActionStep with TargetNpcId = 1000789 (a real NPC),
    /// When Validate() is called,
    /// Then AssertClean — a non-zero TargetNpcId is valid.
    /// </summary>
    [Fact]
    public void Validate_UseActionStep_TargetNpcIdNonZero_NoE8()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-action-with-target", 2,
            new UseActionStep
            {
                Id = "use-action-with-target",
                ActionType = ActionType.Action,
                ActionId = 145,
                TargetNpcId = 1000789,                  // << non-zero — valid
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T-UA6 — W7: UseActionStep with Expect == null raises W7 and suppresses W1
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseActionStep with Expect = null,
    /// When Validate() is called,
    /// Then AssertSingleWarning "W7" — exactly one warning, code W7 (not W1).
    /// W7 fires in place of W1 for UseActionStep, delivering a more actionable message
    /// about the spin-loop risk (see class doc for the suppression decision).
    /// </summary>
    [Fact]
    public void Validate_UseActionStep_NoExpect_RaisesW7_SuppressesW1()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-action-no-expect", 2,
            new UseActionStep
            {
                Id = "use-action-no-expect",
                ActionType = ActionType.Action,
                ActionId = 145,
                TargetNpcId = null
                // Expect is null — the broken thing
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // Exactly one warning, and it must be W7 (not W1).
        DraftValidatorAssertions.AssertSingleWarning(result, "W7");
        // The message must reference the spin-loop consequence.
        Assert.Contains("spin-loop", result.Warnings[0].Message,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // T-UA7 — W7: UseActionStep WITH Expect does NOT raise W7
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseActionStep with Expect set,
    /// When Validate() is called,
    /// Then AssertClean — W7 must not fire when an expect predicate is present.
    /// </summary>
    [Fact]
    public void Validate_UseActionStep_WithExpect_NoW7()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-action-with-expect", 2,
            new UseActionStep
            {
                Id = "use-action-with-expect",
                ActionType = ActionType.Action,
                ActionId = 145,
                TargetNpcId = null,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }
}
