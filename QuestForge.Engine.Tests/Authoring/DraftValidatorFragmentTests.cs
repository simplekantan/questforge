using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for DraftValidator.ValidateFragment -- fragment-specific rules (F18-F30)
/// from FRAGMENT_AUTHORING_PLAN.md.
/// All tests will fail with NotImplementedException until Builder implements ValidateFragment.
/// </summary>
public sealed class DraftValidatorFragmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static readonly NpcLocation ValidNpcLoc =
        new(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));

    private static GameStateSnapshot MakeSnapshot(DateTimeOffset? at = null) =>
        DraftValidatorTestData.MakeSnapshot(at);

    /// <summary>
    /// Creates a fragment draft baseline that is fully clean: valid FragmentId,
    /// one TalkStep with Expect and Notes set, non-zero NpcId.
    /// Every test should start from this and mutate exactly one thing.
    /// </summary>
    private static FragmentDraft ValidFragmentBaseline(string fragmentId = "travel/test-fragment")
    {
        var draft = new FragmentDraft(fragmentId, T0);
        draft.AddStep(new DraftStep(
            StepId: "talk-npc",
            StepType: "talk",
            SequenceNumber: 0,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: "fragment step",
            Raw: new TalkStep
            {
                Id = "talk-npc",
                Target = ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            }), T0);
        return draft;
    }

    private static DraftStep MakeDraftStep(
        string stepId,
        Step raw,
        InferredFrom inferredFrom = InferredFrom.QuestSequenceChange,
        string? notes = null) =>
        DraftValidatorTestData.MakeDraftStep(stepId, 0, raw, inferredFrom, notes);

    // =========================================================================
    // F18 -- EF1: empty FragmentId
    // =========================================================================
    [Fact]
    public void F18_EF1_EmptyFragmentId_ReportsError()
    {
        // Given: a FragmentDraft with FragmentId = ""
        var draft = ValidFragmentBaseline("");

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: errors contains one entry with Code "EF1" and message mentioning "FragmentId"
        DraftValidatorAssertions.AssertSingleError(result, "EF1");
        Assert.Contains("FragmentId", result.Errors[0].Message);
    }

    // =========================================================================
    // F19 -- EF1: whitespace-only FragmentId
    // =========================================================================
    [Fact]
    public void F19_EF1_WhitespaceOnlyFragmentId_ReportsError()
    {
        // Given: a FragmentDraft with FragmentId = "  "
        var draft = ValidFragmentBaseline("  ");

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: errors contains one entry with Code "EF1"
        DraftValidatorAssertions.AssertSingleError(result, "EF1");
    }

    // =========================================================================
    // F20 -- EF2: uppercase in FragmentId
    // =========================================================================
    [Fact]
    public void F20_EF2_UppercaseFragmentId_ReportsError()
    {
        // Given: a FragmentDraft with FragmentId = "Travel/MyFragment"
        var draft = ValidFragmentBaseline("Travel/MyFragment");

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: errors contains one entry with Code "EF2"
        DraftValidatorAssertions.AssertSingleError(result, "EF2");
    }

    // =========================================================================
    // F21 -- EF2: trailing slash
    // =========================================================================
    [Fact]
    public void F21_EF2_TrailingSlash_ReportsError()
    {
        // Given: a FragmentDraft with FragmentId = "travel/"
        var draft = ValidFragmentBaseline("travel/");

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: errors contains one entry with Code "EF2"
        DraftValidatorAssertions.AssertSingleError(result, "EF2");
    }

    // =========================================================================
    // F22 -- EF2: too short (single char)
    // =========================================================================
    [Fact]
    public void F22_EF2_TooShort_ReportsError()
    {
        // Given: a FragmentDraft with FragmentId = "a" (minimum length 3)
        var draft = ValidFragmentBaseline("a");

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: errors contains one entry with Code "EF2"
        DraftValidatorAssertions.AssertSingleError(result, "EF2");
    }

    // =========================================================================
    // F22b -- EF2: consecutive slashes
    // =========================================================================
    [Fact]
    public void F22b_EF2_ConsecutiveSlashes_ReportsError()
    {
        var draft = ValidFragmentBaseline("travel//test");
        var result = new DraftValidator().ValidateFragment(draft);
        DraftValidatorAssertions.AssertSingleError(result, "EF2");
    }

    // =========================================================================
    // F23 -- EF2: valid fragmentId passes (positive test)
    // =========================================================================
    [Fact]
    public void F23_EF2_ValidFragmentId_NoErrors()
    {
        // Given: a FragmentDraft with FragmentId = "travel/teleport-to-limsa-lominsa"
        var draft = ValidFragmentBaseline("travel/teleport-to-limsa-lominsa");

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: no EF1 or EF2 errors
        Assert.DoesNotContain(result.Errors, e => e.Code is "EF1" or "EF2");
    }

    // =========================================================================
    // F24 -- EF2: single-segment valid
    // =========================================================================
    [Fact]
    public void F24_EF2_SingleSegmentValid_NoErrors()
    {
        // Given: a FragmentDraft with FragmentId = "dismount-before-talk"
        var draft = ValidFragmentBaseline("dismount-before-talk");

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: no EF1 or EF2 errors
        Assert.DoesNotContain(result.Errors, e => e.Code is "EF1" or "EF2");
    }

    // =========================================================================
    // F25 -- WF1: empty steps
    // =========================================================================
    [Fact]
    public void F25_WF1_EmptySteps_ReportsWarning()
    {
        // Given: a FragmentDraft with zero steps
        var draft = new FragmentDraft("travel/empty-fragment", T0);

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: warnings contains one entry with Code "WF1" mentioning "no steps"
        Assert.Contains(result.Warnings, w => w.Code == "WF1");
        Assert.Contains("no steps", result.Warnings.First(w => w.Code == "WF1").Message,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // F26 -- E4 does NOT fire for fragments
    // =========================================================================
    [Fact]
    public void F26_E4_DoesNotFireForFragments()
    {
        // Given: a FragmentDraft with one TalkStep (no AcceptStep)
        var draft = ValidFragmentBaseline();

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: no error with Code "E4"
        Assert.DoesNotContain(result.Errors, e => e.Code == "E4");
    }

    // =========================================================================
    // F27 -- W6 does NOT fire for fragments
    // =========================================================================
    [Fact]
    public void F27_W6_DoesNotFireForFragments()
    {
        // Given: a FragmentDraft (no QuestName concept)
        var draft = ValidFragmentBaseline();

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: no warning with Code "W6"
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W6");
    }

    // =========================================================================
    // F28 -- Shared rules still fire: E1 (duplicate stepId)
    // =========================================================================
    [Fact]
    public void F28_E1_DuplicateStepId_StillFiresForFragments()
    {
        // Given: a FragmentDraft (via CreateForTest) with two steps both having StepId "talk-a"
        var step1 = MakeDraftStep("talk-a",
            new TalkStep
            {
                Id = "talk-a",
                Target = ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "first");
        var step2 = MakeDraftStep("talk-a",
            new TalkStep
            {
                Id = "talk-a",
                Target = ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 2" }
            },
            notes: "second");

        var draft = FragmentDraft.CreateForTest(
            "travel/test-fragment", T0,
            [step1, step2]);

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: errors contains one entry with Code "E1"
        Assert.Contains(result.Errors, e => e.Code == "E1");
    }

    // =========================================================================
    // F29 -- Shared rules still fire: W1 (no expect)
    // =========================================================================
    [Fact]
    public void F29_W1_NoExpect_StillFiresForFragments()
    {
        // Given: a FragmentDraft with a TalkStep that has Expect = null
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(new DraftStep(
            StepId: "talk-no-expect",
            StepType: "talk",
            SequenceNumber: 0,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: "has notes to suppress W2",
            Raw: new TalkStep
            {
                Id = "talk-no-expect",
                Target = ValidNpcLoc
                // Expect is null
            }), T0);

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: warnings contains one entry with Code "W1" for that step
        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }

    // =========================================================================
    // F30 -- Shared rules still fire: E6 (TalkStep NpcId == 0)
    // =========================================================================
    [Fact]
    public void F30_E6_TalkStepNpcIdZero_StillFiresForFragments()
    {
        // Given: a FragmentDraft with a TalkStep whose Target.NpcId == 0
        var draft = new FragmentDraft("travel/test-fragment", T0);
        draft.AddStep(new DraftStep(
            StepId: "talk-zero-npc",
            StepType: "talk",
            SequenceNumber: 0,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: "has notes to suppress W2",
            Raw: new TalkStep
            {
                Id = "talk-zero-npc",
                Target = new NpcLocation(NpcId: 0, Zone: 128,
                    Position: new Position3(9.5f, 40f, 14.2f)),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            }), T0);

        // When: ValidateFragment is called
        var result = new DraftValidator().ValidateFragment(draft);

        // Then: errors contains one entry with Code "E6"
        DraftValidatorAssertions.AssertSingleError(result, "E6");
    }
}
