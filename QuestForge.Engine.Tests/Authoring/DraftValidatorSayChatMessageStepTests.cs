using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// DraftValidator rules for SayChatMessageStep (E11, E12, W9). W9 suppresses W1
/// for SayChatMessageStep via the W1 skip-guard, mirroring the use-action and
/// use-emote precedents.
/// </summary>
public sealed class DraftValidatorSayChatMessageStepTests
{
    // =========================================================================
    // SC12 â€” E11: empty Message raises an error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a SayChatMessageStep with Message = "",
    /// When Validate() is called,
    /// Then AssertSingleError "E11" â€” empty string is never valid message content.
    /// </summary>
    [Fact]
    public void Validate_SayChatMessageStep_EmptyMessage_RaisesE11_SC12()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("say-empty-message", 2,
            new SayChatMessageStep
            {
                Id = "say-empty-message",
                Message = "",
                TargetNpcId = 1000789u,
                Expect = new PredicateExpect { Predicate = "questFlag(83012, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E11");
        Assert.Contains("say-empty-message", result.Errors[0].Message);
        Assert.Contains("Message", result.Errors[0].Message);
    }

    /// <summary>
    /// Defensive sub-case: SayChatMessageStep with Message = null must also trigger E11.
    /// (string.IsNullOrEmpty covers both null and empty.)
    /// </summary>
    [Fact]
    public void Validate_SayChatMessageStep_NullMessage_RaisesE11_SC12b()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("say-null-message", 2,
            new SayChatMessageStep
            {
                Id = "say-null-message",
                Message = null!,
                TargetNpcId = null,
                Expect = new PredicateExpect { Predicate = "questFlag(83012, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E11");
        Assert.Contains("say-null-message", result.Errors[0].Message);
    }

    // =========================================================================
    // SC13 â€” E12: TargetNpcId == 0 raises an error; null does NOT raise E12
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a SayChatMessageStep with TargetNpcId = 0 (explicit zero),
    /// When Validate() is called,
    /// Then AssertSingleError "E12" â€” an explicit zero is never a valid NPC id;
    /// null is the correct sentinel for "no target".
    ///
    /// Defensive sub-case: TargetNpcId = null must NOT trigger E12.
    /// </summary>
    [Fact]
    public void Validate_SayChatMessageStep_TargetNpcIdZero_RaisesE12_SC13()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("say-zero-npc", 2,
            new SayChatMessageStep
            {
                Id = "say-zero-npc",
                Message = "Open Sesame",
                TargetNpcId = 0,
                Expect = new PredicateExpect { Predicate = "questFlag(83013, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E12");
        Assert.Contains("say-zero-npc", result.Errors[0].Message);
        Assert.Contains("TargetNpcId", result.Errors[0].Message);

        // Defensive: TargetNpcId = null must NOT trigger E12
        var draftWithNullTarget = DraftValidatorTestData.ValidBaseline();
        draftWithNullTarget.AddStep(DraftValidatorTestData.MakeDraftStep("say-null-npc", 2,
            new SayChatMessageStep
            {
                Id = "say-null-npc",
                Message = "Open Sesame",
                TargetNpcId = null,                        // null is allowed â€” must not raise E12
                Expect = new PredicateExpect { Predicate = "questFlag(83013, 3)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var nullTargetResult = new DraftValidator().Validate(draftWithNullTarget);
        Assert.DoesNotContain(nullTargetResult.Errors, e => e.Code == "E12");
    }

    // =========================================================================
    // SC14 â€” W9: missing Expect raises W9; W1 is suppressed for SayChatMessageStep
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a SayChatMessageStep with Expect = null,
    /// When Validate() is called,
    /// Then exactly one warning W9; W1 must NOT fire for the same step.
    /// W9 message must contain "spin-loop" (CLAUDE.md slice 2 requirement).
    /// Mirrors the W8/W1 suppression pattern for UseEmoteStep (Decision SC7).
    /// </summary>
    [Fact]
    public void Validate_SayChatMessageStep_NoExpect_RaisesW9_SuppressesW1_SC14()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("say-no-expect", 2,
            new SayChatMessageStep
            {
                Id = "say-no-expect",
                Message = "Open Sesame",
                TargetNpcId = null
                // Expect is null â€” the one broken thing
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // Exactly one warning, and it must be W9 (not W1)
        DraftValidatorAssertions.AssertSingleWarning(result, "W9");
        // W9 message must contain "spin-loop" per CLAUDE.md slice 2 requirement
        Assert.Contains("spin-loop", result.Warnings[0].Message, StringComparison.OrdinalIgnoreCase);
        // W1 must be suppressed for SayChatMessageStep (mirrors UseEmoteStep's W8/W1 suppression)
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }
}
