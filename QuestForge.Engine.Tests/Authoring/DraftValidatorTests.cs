using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Strengthened DraftValidator tests — full coverage of E1-E6, W1-W6.
/// All tests use DraftValidatorAssertions (AssertSingleError / AssertSingleWarning / AssertClean).
/// T1–T6: strengthened from original seven tests (T7 tautological E1 test deleted; replaced by T8).
/// T8–T23: new tests for previously-uncovered rules and new W6 rule.
/// </summary>
public sealed class DraftValidatorTests
{
    // =========================================================================
    // T1 — Validation_PassesForValidDraft
    // =========================================================================

    /// <summary>
    /// Given the baseline (accept + turn-in, both with expect, both with notes, QuestName set),
    /// When Validate() is called,
    /// Then AssertClean — zero errors, zero warnings.
    /// This is the negative-for-everything test: a new rule that fires unexpectedly on a
    /// clean baseline will surface here.
    /// </summary>
    [Fact]
    public void Validation_PassesForValidDraft()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        var result = new DraftValidator().Validate(draft);
        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T2 — E2_RawNull_ReportsSingleError
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a third step with Raw = null (unconfirmed step),
    /// When Validate() is called,
    /// Then AssertSingleError "E2" — and no spillover warnings (the Raw-null guard suppresses W1/W2).
    /// </summary>
    [Fact]
    public void E2_RawNull_ReportsSingleError()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        var nullRawStep = new DraftStep(
            StepId: "unconfirmed",
            StepType: "talk",
            SequenceNumber: 2,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: DraftValidatorTestData.MakeSnapshot(),
            ObservedAfter: DraftValidatorTestData.MakeSnapshot(DraftValidatorTestData.T1),
            SuggestedExpect: null,
            Notes: "some notes",
            Raw: null);
        draft.AddStep(nullRawStep, DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E2");
    }

    // =========================================================================
    // T3 — E4_MissingAcceptStep_ReportsSingleError
    // =========================================================================

    /// <summary>
    /// Given a draft with only a TalkStep and a TurnInStep (no AcceptStep), both fully valid,
    /// When Validate() is called,
    /// Then AssertSingleError "E4".
    /// </summary>
    [Fact]
    public void E4_MissingAcceptStep_ReportsSingleError()
    {
        var draft = new QuestDraft(DraftValidatorTestData.Quest2054, DraftValidatorTestData.T0)
        {
            QuestName = "Test Quest"
        };
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-to-npc", 0,
            new TalkStep
            {
                Id = "talk-to-npc",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("turn-in-quest", 1,
            new TurnInStep
            {
                Id = "turn-in-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E4");
    }

    // =========================================================================
    // T4 — E5_UnknownFunction_ReportsSingleError
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep with Expect = "questSequnece(2054) >= 3" (typo),
    /// When Validate() is called,
    /// Then AssertSingleError "E5" and the message contains the did-you-mean target "questSequence".
    /// </summary>
    [Fact]
    public void E5_UnknownFunction_ReportsSingleError()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-typo", 2,
            new TalkStep
            {
                Id = "talk-typo",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequnece(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E5");
        Assert.Contains("questSequence", result.Errors[0].Message);
    }

    // =========================================================================
    // T5 — W1_MissingExpect_ReportsSingleWarning
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep with Expect = null (Notes set to suppress W2),
    /// When Validate() is called,
    /// Then AssertSingleWarning "W1".
    /// </summary>
    [Fact]
    public void W1_MissingExpect_ReportsSingleWarning()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-no-expect", 2,
            new TalkStep
            {
                Id = "talk-no-expect",
                Target = DraftValidatorTestData.ValidNpcLoc
                // Expect is null
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }

    // =========================================================================
    // T6 — W5_NotEmittedForNumericGap
    // =========================================================================

    /// <summary>
    /// Given a draft with steps at sequenceNumber 0 and 2 (gap at 1),
    /// When Validate() is called,
    /// Then AssertClean — W5 is for empty groups, not numeric gaps.
    /// </summary>
    [Fact]
    public void W5_NotEmittedForNumericGap()
    {
        var draft = new QuestDraft(DraftValidatorTestData.Quest2054, DraftValidatorTestData.T0)
        {
            QuestName = "Test Quest"
        };
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("accept-quest", 0,
            new AcceptStep
            {
                Id = "accept-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        // Sequence 1 intentionally skipped
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("turn-in-quest", 2,
            new TurnInStep
            {
                Id = "turn-in-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T8 — E1_DuplicateStepId_ReportsSingleError
    // =========================================================================

    /// <summary>
    /// Given a draft constructed via QuestDraft.CreateForTest with two DraftSteps sharing
    /// StepId = "step-x" (both fully populated so no other rules fire),
    /// When Validate() is called,
    /// Then AssertSingleError "E1" and the message contains "step-x".
    /// Uses CreateForTest (§2.1) to bypass AddStep's uniqueness guard.
    /// </summary>
    [Fact]
    public void E1_DuplicateStepId_ReportsSingleError()
    {
        var acceptStep = DraftValidatorTestData.MakeDraftStep("step-x", 0,
            new AcceptStep
            {
                Id = "step-x",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }
            },
            notes: "accept");
        var duplicateStep = DraftValidatorTestData.MakeDraftStep("step-x", 1,
            new TurnInStep
            {
                Id = "step-x",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }
            },
            notes: "turn-in");

        var draft = QuestDraft.CreateForTest(
            questId: DraftValidatorTestData.Quest2054,
            createdAt: DraftValidatorTestData.T0,
            steps: [acceptStep, duplicateStep],
            questName: "Test Quest");

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E1");
        Assert.Contains("step-x", result.Errors[0].Message);
    }

    // =========================================================================
    // T9 — E6_TalkStep_NpcIdZero_ReportsSingleError
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep with Target.NpcId = 0, Expect set, Notes set,
    /// When Validate() is called,
    /// Then AssertSingleError "E6".
    /// </summary>
    [Fact]
    public void E6_TalkStep_NpcIdZero_ReportsSingleError()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-zero-npc", 2,
            new TalkStep
            {
                Id = "talk-zero-npc",
                Target = new NpcLocation(NpcId: 0, Zone: 128,
                    Position: new Position3(9.5f, 40f, 14.2f)),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E6");
    }

    // =========================================================================
    // T10 — E6_TalkStep_NpcIdNonZero_DoesNotFire
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep with Target.NpcId = 1 (smallest nonzero), Expect, Notes,
    /// When Validate() is called,
    /// Then AssertClean — E6 must not fire for nonzero NpcId.
    /// </summary>
    [Fact]
    public void E6_TalkStep_NpcIdNonZero_DoesNotFire()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-npc-1", 2,
            new TalkStep
            {
                Id = "talk-npc-1",
                Target = new NpcLocation(NpcId: 1, Zone: 128,
                    Position: new Position3(9.5f, 40f, 14.2f)),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T11 — W2_NoNotesAndInferredFromNone_ReportsSingleWarning
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep with Notes = null, InferredFrom = None, Expect set,
    /// When Validate() is called,
    /// Then AssertSingleWarning "W2".
    /// </summary>
    [Fact]
    public void W2_NoNotesAndInferredFromNone_ReportsSingleWarning()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-no-notes", 2,
            new TalkStep
            {
                Id = "talk-no-notes",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            inferredFrom: InferredFrom.None,
            notes: null), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W2");
    }

    // =========================================================================
    // T12 — W2_NotFiredWhenInferredFromIsSet
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep with InferredFrom = QuestSequenceChange and Notes = null,
    /// When Validate() is called,
    /// Then AssertClean — W2 only fires when InferredFrom == None.
    /// </summary>
    [Fact]
    public void W2_NotFiredWhenInferredFromIsSet()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-no-notes", 2,
            new TalkStep
            {
                Id = "talk-no-notes",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            inferredFrom: InferredFrom.QuestSequenceChange,
            notes: null), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T13 — W3_LastStepIsTravel_ReportsSingleWarning
    // =========================================================================

    /// <summary>
    /// Given a draft with an AcceptStep (notes, expect) and a final TravelStep (notes, expect),
    /// When Validate() is called,
    /// Then AssertSingleWarning "W3".
    /// E4 does NOT fire because AcceptStep is present.
    /// </summary>
    [Fact]
    public void W3_LastStepIsTravel_ReportsSingleWarning()
    {
        var draft = new QuestDraft(DraftValidatorTestData.Quest2054, DraftValidatorTestData.T0)
        {
            QuestName = "Test Quest"
        };
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("accept-quest", 0,
            new AcceptStep
            {
                Id = "accept-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }
            },
            notes: "accept"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("travel-somewhere", 1,
            new TravelStep
            {
                Id = "travel-somewhere",
                Destination = new TravelDestination(Zone: 130),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            notes: "travel"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W3");
    }

    // =========================================================================
    // T14 — W3_NotFiredWhenLastStepIsTurnIn
    // =========================================================================

    /// <summary>
    /// Given the baseline (accept + turn-in), identical to T1,
    /// When Validate() is called,
    /// Then AssertClean — W3 must not fire when the last step is a TurnInStep.
    /// </summary>
    [Fact]
    public void W3_NotFiredWhenLastStepIsTurnIn()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        var result = new DraftValidator().Validate(draft);
        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T15 — W4_ConsecutiveTalkStepsSameNpc_ReportsSingleWarning
    // =========================================================================

    /// <summary>
    /// Given a draft with accept (NpcId=1000789), TalkStep#1 (NpcId=1014875),
    /// TalkStep#2 (NpcId=1014875), turn-in (NpcId=1000789),
    /// When Validate() is called,
    /// Then AssertSingleWarning "W4".
    /// </summary>
    [Fact]
    public void W4_ConsecutiveTalkStepsSameNpc_ReportsSingleWarning()
    {
        var sharedNpc = new NpcLocation(NpcId: 1014875, Zone: 128,
            Position: new Position3(9.5f, 40f, 14.2f));

        var draft = new QuestDraft(DraftValidatorTestData.Quest2054, DraftValidatorTestData.T0)
        {
            QuestName = "Test Quest"
        };
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("accept-quest", 0,
            new AcceptStep
            {
                Id = "accept-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }
            },
            notes: "accept"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-1", 1,
            new TalkStep
            {
                Id = "talk-1",
                Target = sharedNpc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-2", 2,
            new TalkStep
            {
                Id = "talk-2",
                Target = sharedNpc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 2" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("turn-in-quest", 3,
            new TurnInStep
            {
                Id = "turn-in-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleWarning(result, "W4");
    }

    // =========================================================================
    // T16 — W4_NonConsecutiveSameNpc_DoesNotFire
    // =========================================================================

    /// <summary>
    /// Given a draft with accept, TalkStep#1 (NpcId=1014875), TalkStep#2 (NpcId=2000000),
    /// TalkStep#3 (NpcId=1014875), turn-in — same NpcId at positions 1 and 3 (non-consecutive),
    /// When Validate() is called,
    /// Then AssertClean — W4 is about consecutive pairs only.
    /// </summary>
    [Fact]
    public void W4_NonConsecutiveSameNpc_DoesNotFire()
    {
        var npcA = new NpcLocation(NpcId: 1014875, Zone: 128,
            Position: new Position3(9.5f, 40f, 14.2f));
        var npcB = new NpcLocation(NpcId: 2000000, Zone: 128,
            Position: new Position3(10f, 40f, 10f));

        var draft = new QuestDraft(DraftValidatorTestData.Quest2054, DraftValidatorTestData.T0)
        {
            QuestName = "Test Quest"
        };
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("accept-quest", 0,
            new AcceptStep
            {
                Id = "accept-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }
            },
            notes: "accept"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-1", 1,
            new TalkStep
            {
                Id = "talk-1",
                Target = npcA,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-2", 2,
            new TalkStep
            {
                Id = "talk-2",
                Target = npcB,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 2" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-3", 3,
            new TalkStep
            {
                Id = "talk-3",
                Target = npcA,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 3" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("turn-in-quest", 4,
            new TurnInStep
            {
                Id = "turn-in-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T17 — W4_AcceptThenTalkSameNpc_DoesNotFire
    // =========================================================================

    /// <summary>
    /// Given a draft with AcceptStep (Target.NpcId=1014875) followed immediately by
    /// TalkStep (Target.NpcId=1014875),
    /// When Validate() is called,
    /// Then AssertClean — W4 guards TalkStep-to-TalkStep pairs only; Accept-to-Talk is exempt.
    /// </summary>
    [Fact]
    public void W4_AcceptThenTalkSameNpc_DoesNotFire()
    {
        var sharedNpc = new NpcLocation(NpcId: 1014875, Zone: 128,
            Position: new Position3(9.5f, 40f, 14.2f));

        var draft = new QuestDraft(DraftValidatorTestData.Quest2054, DraftValidatorTestData.T0)
        {
            QuestName = "Test Quest"
        };
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("accept-quest", 0,
            new AcceptStep
            {
                Id = "accept-quest",
                Target = sharedNpc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }
            },
            notes: "accept"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-same-npc", 1,
            new TalkStep
            {
                Id = "talk-same-npc",
                Target = sharedNpc,
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("turn-in-quest", 2,
            new TurnInStep
            {
                Id = "turn-in-quest",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T18 — E3_PredicateParseError_ReportsSingleError (Theory)
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep whose Expect contains a structurally broken predicate,
    /// When Validate() is called,
    /// Then AssertSingleError "E3" and the error message contains the original predicate string.
    /// Each row is crafted to produce exactly one ParseError from the parser/checker.
    /// </summary>
    [Theory]
    [InlineData("questSequence(")]               // unbalanced paren → parse-error
    [InlineData("questSequence(2054")]           // missing ) → parse-error
    [InlineData("questSequence(2054) >=")]       // missing RHS literal → parse-error
    [InlineData("questSequence()")]              // arity mismatch (expects 1, got 0) → arity-mismatch
    [InlineData("questSequence(2054, 99)")]      // arity mismatch (expects 1, got 2) → arity-mismatch
    [InlineData("questSequence(\"x\")")]         // type mismatch (expects Int, got String) → type-mismatch
    [InlineData("playerZone() == \"x\"")]        // type mismatch (Int == String) → type-mismatch
    [InlineData("default and isQuestComplete(2054)")] // default not composable → default-not-composable
    [InlineData("${param}")]                    // parameter ref outside fragment scope → parse-error
    public void E3_PredicateParseError_ReportsSingleError(string predicate)
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-bad-pred", 2,
            new TalkStep
            {
                Id = "talk-bad-pred",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = predicate }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E3");
        Assert.Contains(predicate, result.Errors[0].Message);
    }

    // =========================================================================
    // T19 — E5_UnknownFunctionOnSkipIf_AlsoReported
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep whose SkipIf has an unknown-function typo
    /// and whose Expect is valid,
    /// When Validate() is called,
    /// Then AssertSingleError "E5" — confirms the SkipIf predicate path is evaluated.
    /// The current implementation only checks Expect; this test enforces the fix.
    /// </summary>
    [Fact]
    public void E5_UnknownFunctionOnSkipIf_AlsoReported()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-skipif-typo", 2,
            new TalkStep
            {
                Id = "talk-skipif-typo",
                Target = DraftValidatorTestData.ValidNpcLoc,
                SkipIf = new PredicateExpect { Predicate = "questSequnece(2054)" },       // typo
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }   // valid
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E5");
    }

    // =========================================================================
    // T20 — E5_DidYouMeanHintPresent
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a TalkStep with Expect = "questSequnece(2054)" (typo),
    /// When Validate() is called,
    /// Then AssertSingleError "E5" and the message contains "questSequence" (the suggestion).
    /// </summary>
    [Fact]
    public void E5_DidYouMeanHintPresent()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-typo2", 2,
            new TalkStep
            {
                Id = "talk-typo2",
                Target = DraftValidatorTestData.ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "questSequnece(2054)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E5");
        Assert.Contains("questSequence", result.Errors[0].Message);
    }

    // =========================================================================
    // T21 — W6_EmptyOrPlaceholderName_ReportsSingleWarning (Theory)
    // =========================================================================

    /// <summary>
    /// Given the baseline with QuestName set to an empty/null/placeholder value,
    /// When Validate() is called,
    /// Then AssertSingleWarning "W6".
    /// The null row uses a separate helper because QuestName is a property, not a ctor arg.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("TODO")]
    [InlineData("todo")]
    [InlineData("Todo")]
    [InlineData("  TODO ")]
    public void W6_EmptyOrPlaceholderName_ReportsSingleWarning(string questName)
    {
        var draft = DraftValidatorTestData.ValidBaseline(questName: questName);
        var result = new DraftValidator().Validate(draft);
        DraftValidatorAssertions.AssertSingleWarning(result, "W6");
    }

    /// <summary>
    /// Null QuestName variant of T21 — requires separate test because [InlineData(null)]
    /// would need a nullable parameter type but the Theory rows use string.
    /// </summary>
    [Fact]
    public void W6_NullName_ReportsSingleWarning()
    {
        // ValidBaseline sets QuestName; clear it to simulate never-set
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.QuestName = null;
        var result = new DraftValidator().Validate(draft);
        DraftValidatorAssertions.AssertSingleWarning(result, "W6");
    }

    // =========================================================================
    // T22 — W6_RealName_DoesNotFire (Theory)
    // =========================================================================

    /// <summary>
    /// Given the baseline with a real (non-placeholder) QuestName,
    /// When Validate() is called,
    /// Then AssertClean — W6 must not fire for legitimate names.
    /// "Todo: figure out name" passes because only whole-string "TODO" is a placeholder.
    /// </summary>
    [Theory]
    [InlineData("Close to Home")]
    [InlineData("Todo: figure out name")]
    [InlineData("A")]
    [InlineData("TODOs aplenty")]
    public void W6_RealName_DoesNotFire(string questName)
    {
        var draft = DraftValidatorTestData.ValidBaseline(questName: questName);
        var result = new DraftValidator().Validate(draft);
        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // T23 — MultipleRules_CoexistCorrectly
    // =========================================================================

    /// <summary>
    /// Given a draft with no AcceptStep (E4), one TalkStep with NpcId=0 (E6),
    /// and QuestName = null (W6),
    /// When Validate() is called,
    /// Then errors contain exactly E4 and E6 (via AssertErrorCodes),
    /// and warnings contain exactly one W6 entry.
    /// Confirms rule-suppression is not silently happening between rules.
    /// </summary>
    [Fact]
    public void MultipleRules_CoexistCorrectly()
    {
        var draft = new QuestDraft(DraftValidatorTestData.Quest2054, DraftValidatorTestData.T0)
        {
            QuestName = null
        };
        // TalkStep with NpcId = 0 — triggers E6
        // No AcceptStep in draft — triggers E4
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("talk-zero-npc", 0,
            new TalkStep
            {
                Id = "talk-zero-npc",
                Target = new NpcLocation(NpcId: 0, Zone: 128,
                    Position: new Position3(9.5f, 40f, 14.2f)),
                Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        // Errors: exactly E4 and E6 — use direct assertions because the fixture also has
        // a warning (W6), so AssertErrorCodes (which asserts zero warnings) would fail.
        var actualErrorCodes = result.Errors.Select(e => e.Code).OrderBy(c => c).ToArray();
        Assert.Equal(new[] { "E4", "E6" }, actualErrorCodes);

        // Warnings: exactly one W6
        Assert.Single(result.Warnings);
        Assert.Equal("W6", result.Warnings[0].Code);
    }
}
