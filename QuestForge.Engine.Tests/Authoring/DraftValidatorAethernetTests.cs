using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Validator tests for AethernetStep rules E25, E26, W12, and W1 suppression.
/// Spec: docs/AETHERNET_STEP_PLAN.md -- Decision AN8, scenarios AV1-AV7.
///
/// RED PHASE: All tests fail to compile because AethernetStep does not exist in
/// QuestForge.Schema yet. Once it compiles, tests will fail at runtime because
/// DraftValidator does not yet contain E25, E26, or W12 rules.
///
/// E25: AethernetStep.From == 0 -> error.
/// E26: AethernetStep.To == 0 -> error.
/// W12: AethernetStep with no Expect -> spin-loop warning.
/// W1 suppression: AethernetStep must NOT trigger the generic W1 missing-Expect warning.
/// </summary>
public sealed class DraftValidatorAethernetTests
{
    // =========================================================================
    // AV1 -- E25: From == 0 is an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an AethernetStep with From=0, To=48,
    /// When Validate() is called,
    /// Then exactly one error with Code "E25".
    /// </summary>
    [Fact]
    public void Validate_AethernetStep_FromZero_RaisesE25()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("aethernet-from-zero", 2,
            new AethernetStep
            {
                Id = "aethernet-from-zero",
                From = 0,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E25");
    }

    // =========================================================================
    // AV2 -- E26: To == 0 is an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an AethernetStep with From=8, To=0,
    /// When Validate() is called,
    /// Then exactly one error with Code "E26".
    /// </summary>
    [Fact]
    public void Validate_AethernetStep_ToZero_RaisesE26()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("aethernet-to-zero", 2,
            new AethernetStep
            {
                Id = "aethernet-to-zero",
                From = 8,
                To = 0,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E26");
    }

    // =========================================================================
    // AV3 -- E25 + E26: both From and To == 0
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an AethernetStep with From=0, To=0,
    /// When Validate() is called,
    /// Then errors contain both "E25" and "E26" (both fire independently).
    /// </summary>
    [Fact]
    public void Validate_AethernetStep_BothZero_RaisesE25AndE26()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("aethernet-both-zero", 2,
            new AethernetStep
            {
                Id = "aethernet-both-zero",
                From = 0,
                To = 0,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertErrorCodes(result, "E25", "E26");
    }

    // =========================================================================
    // AV4 -- W12: missing Expect emits spin-loop warning
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an AethernetStep with From=8, To=48, Expect=null,
    /// When Validate() is called,
    /// Then warnings contain a warning with Code "W12" and message containing "spin-loop".
    /// </summary>
    [Fact]
    public void Validate_AethernetStep_MissingExpect_RaisesW12()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("aethernet-no-expect", 2,
            new AethernetStep
            {
                Id = "aethernet-no-expect",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = null
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W12");
        Assert.Contains("spin-loop", result.Warnings[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // AV5 -- W12 not emitted when Expect is present
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an AethernetStep with From=8, To=48,
    ///   Expect="playerZone() == 128",
    /// When Validate() is called,
    /// Then warnings do NOT contain "W12".
    /// </summary>
    [Fact]
    public void Validate_AethernetStep_WithExpect_NoW12()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("aethernet-with-expect", 2,
            new AethernetStep
            {
                Id = "aethernet-with-expect",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // AV6 -- W1 suppression: AethernetStep without Expect does NOT trigger W1
    // =========================================================================

    /// <summary>
    /// Given the baseline plus an AethernetStep with From=8, To=48, Expect=null,
    /// When Validate() is called,
    /// Then warnings do NOT contain "W1" (W12 fires instead).
    /// </summary>
    [Fact]
    public void Validate_AethernetStep_MissingExpect_DoesNotTriggerW1()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("aethernet-w1-suppression", 2,
            new AethernetStep
            {
                Id = "aethernet-w1-suppression",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = null
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // W12 fires, not W1
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
        Assert.Contains(result.Warnings, w => w.Code == "W12");
    }

    // =========================================================================
    // AV7 -- Valid AethernetStep produces no errors
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a valid AethernetStep with From=8, To=48,
    ///   Expect="playerZone() == 128",
    /// When Validate() is called,
    /// Then no errors and no warnings related to AethernetStep.
    /// </summary>
    [Fact]
    public void Validate_AethernetStep_Valid_NoErrorsNoWarnings()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("aethernet-valid", 2,
            new AethernetStep
            {
                Id = "aethernet-valid",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }
}
