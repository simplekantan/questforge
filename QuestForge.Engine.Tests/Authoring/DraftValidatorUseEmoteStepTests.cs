using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Failing tests for UseEmoteStep validator rules (E9, E10, W8).
/// These tests compile cleanly but FAIL at runtime because DraftValidator has no
/// UseEmoteStep branch yet â€” the builder must add E9, E10, and W8 cases.
///
/// W8 / W1 interaction (documented here for the builder):
///   W8 fires AND suppresses W1 for UseEmoteStep-with-null-Expect.
///   Implementation: extend the W1 skip-guard from
///     step.Raw is not UseActionStep
///   to:
///     step.Raw is not (UseActionStep or UseEmoteStep)
///   Then emit W8 in its own loop for UseEmoteStep-with-null-Expect.
///   (C# 11+ pattern-matching syntax, verified compatible per Decision UE7.)
///
/// Prerequisites (builder tasks):
/// </summary>
public sealed class DraftValidatorUseEmoteStepTests
{
    // =========================================================================
    // UE13 â€” E9: EmoteId == 0 raises an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseEmoteStep with EmoteId = 0,
    /// When Validate() is called,
    /// Then AssertSingleError "E9" â€” zero is never a valid emote id.
    /// </summary>
    [Fact]
    public void Validate_UseEmoteStep_EmoteIdZero_RaisesE9_UE13()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-emote-zero-id", 2,
            new UseEmoteStep
            {
                Id = "use-emote-zero-id",
                EmoteId = 0,                               // TODO: new field shape â€” the one broken thing
                TargetNpcId = 1000789u,                    // TODO: new field shape
                Motion = true,                             // TODO: new field shape
                Expect = new PredicateExpect { Predicate = "questFlag(82013, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E9"); // TODO: DraftValidator.E9 rule
        Assert.Contains("use-emote-zero-id", result.Errors[0].Message);
        Assert.Contains("EmoteId", result.Errors[0].Message);
    }

    // =========================================================================
    // UE14 â€” E10: TargetNpcId == 0 raises an error; null does NOT raise E10
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseEmoteStep with TargetNpcId = 0 (explicit zero),
    /// When Validate() is called,
    /// Then AssertSingleError "E10" â€” an explicit zero is never a valid NPC id;
    /// null is the correct sentinel for "no target".
    ///
    /// Defensive sub-case: TargetNpcId = null must NOT trigger E10.
    /// </summary>
    [Fact]
    public void Validate_UseEmoteStep_TargetNpcIdZero_RaisesE10_UE14()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-emote-zero-npc", 2,
            new UseEmoteStep
            {
                Id = "use-emote-zero-npc",
                EmoteId = 17,                              // TODO: new field shape
                TargetNpcId = 0,                           // TODO: new field shape â€” explicit zero is invalid
                Motion = true,                             // TODO: new field shape
                Expect = new PredicateExpect { Predicate = "questFlag(82014, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E10"); // TODO: DraftValidator.E10 rule
        Assert.Contains("use-emote-zero-npc", result.Errors[0].Message);
        Assert.Contains("TargetNpcId", result.Errors[0].Message);

        // Defensive: TargetNpcId = null must NOT trigger E10
        var draftWithNullTarget = DraftValidatorTestData.ValidBaseline();
        draftWithNullTarget.AddStep(DraftValidatorTestData.MakeDraftStep("use-emote-null-npc", 2,
            new UseEmoteStep
            {
                Id = "use-emote-null-npc",
                EmoteId = 17,
                TargetNpcId = null,                        // null is allowed â€” must not raise E10
                Motion = true,
                Expect = new PredicateExpect { Predicate = "questFlag(82014, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var nullTargetResult = new DraftValidator().Validate(draftWithNullTarget);
        Assert.DoesNotContain(nullTargetResult.Errors, e => e.Code == "E10");
    }

    // =========================================================================
    // UE15 â€” W8: missing Expect raises W8; W1 is suppressed for UseEmoteStep
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a UseEmoteStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning W8; W1 must NOT fire for the same step.
    /// Mirrors the W7/W1 suppression pattern for UseActionStep (Decision UE7).
    /// </summary>
    [Fact]
    public void Validate_UseEmoteStep_NoExpect_RaisesW8_SuppressesW1_UE15()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("use-emote-no-expect", 2,
            new UseEmoteStep
            {
                Id = "use-emote-no-expect",
                EmoteId = 17,                              // TODO: new field shape
                TargetNpcId = null,                        // TODO: new field shape
                Motion = true                              // TODO: new field shape
                // Expect is null â€” the one broken thing
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // Exactly one warning, and it must be W8 (not W1)
        DraftValidatorAssertions.AssertSingleWarning(result, "W8"); // TODO: DraftValidator.W8 rule
        // W1 must be suppressed for UseEmoteStep (mirrors UseActionStep's W7/W1 suppression)
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }
}
