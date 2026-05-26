using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice A (RED): StepInferenceEngine Rule 2.2 — target-based span attribution tests.
//
// Rewrites GWT-I1'..I6' (most-distinct-DataIds dominance, kill→bump correlation) →
// GWT-I1..I6 (highest-FinalValue dominance, ambiguity Notes on multi-target/multi-nibble,
// per COMBAT_TARGET_ATTRIBUTION_PLAN.md §6.2).
//
// DELETED (kill→bump / old dominance, obsolete):
//   GWT-I4' — dominant = most-distinct-DataIds (replaced by highest-FinalValue dominance)
//   GWT-I5' — tie-break: lowest VarIndex then Low (kept in I4 with different primary criterion)
//   GWT-I6' — step-id from lowest DataId (kept in I5/I6)
//   The old GWT-I4' multi-DataId dominance is gone — every nibble shares the span target-set.
//
// NEW assertions:
//   I4 — dominant = highest FinalValue; tie → lowest VarIndex then Low
//   I5 — ambiguity flag (Confidence.Low + "Ambiguous"/"split" Notes) on multi-target span
//   I6 — ambiguity flag on multi-nibble span
//
// Expected RED cause: StepInferenceEngine.Rule 2.2 still selects by most-distinct-DataIds
// (old), not highest-FinalValue; ambiguity logic does not exist yet.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatInferenceEngineTests
{
    private static readonly QuestId Quest65847 = new(65847);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static string FormatResult(InferenceResult r)
        => $"StepType={r.StepType}, InferredFrom={r.InferredFrom}, Expect={r.SuggestedExpect}, " +
           $"Id={r.SuggestedStepId}, Conf={r.Confidence}, Notes={r.Notes}";

    // ─── Snapshot builder ────────────────────────────────────────────────────

    private static GameStateSnapshot MakeSnapshot(
        int questSequence = 0,
        IReadOnlyDictionary<NibbleKey, KillCorrelation>? killCorrelatedTargets = null,
        DateTimeOffset? capturedAt = null,
        NpcId? lastNpcInteracted = null,
        bool inCombat = false,
        int combatStartZone = 0,
        WorldPosition? combatStartPosition = null) =>
        new(
            CapturedAt:         capturedAt ?? T0,
            Zone:               new ZoneId(100),
            Position:           new WorldPosition(10, 0, 10),
            ActiveQuest:        Quest65847,
            QuestSequence:      questSequence,
            QuestFlags:         0,
            QuestAccepted:      false,
            QuestCompleted:     false,
            LastNpcInteracted:  lastNpcInteracted,
            LastNpcPosition:    null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash:      0,
            LastAttuned:        null)
        {
            KillCorrelatedTargets = killCorrelatedTargets,
            InCombat              = inCombat,
            CombatStartZone       = combatStartZone,
            CombatStartPosition   = combatStartPosition,
        };

    private static KillCorrelation Corr(uint[] dataIds, int finalValue)
        => new(DataIds: dataIds, FinalValue: finalValue);

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I1 combat beats sequence advance + Rule 7 NPC → low-nibble expect.
    //
    // before seq=0; after seq=3, [(0,Low)]=([347],3), LastNpcInteracted=9999.
    // Rule 2.2 fires before Rule 3 (seq advance) and Rule 7 (NPC).
    // Expect: StepType="combat", SuggestedExpect="questVariableLow(65847, 0) >= 3",
    //         SuggestedStepId="defeat-347", InferredFrom=Combat.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtI1_LowNibbleCombat_BeatsSequenceAdvanceAndNpcRule()
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 3,
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = Corr([347u], 3)
            },
            lastNpcInteracted: new NpcId(9999u),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.Equal(InferredFrom.Combat, result.InferredFrom);
        Assert.Equal("questVariableLow(65847, 0) >= 3", result.SuggestedExpect);
        Assert.Equal("defeat-347", result.SuggestedStepId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I2 high-nibble produces questVariableHigh expect.
    //
    // after: [(1,High)]=([338],3).
    // Expect: SuggestedExpect="questVariableHigh(65847, 1) >= 3", StepId="defeat-338".
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtI2_HighNibbleCombat_ProducesQuestVariableHighExpect()
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 0,
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(1, NibbleHalf.High)] = Corr([338u], 3)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.Equal("questVariableHigh(65847, 1) >= 3", result.SuggestedExpect);
        Assert.Equal("defeat-338", result.SuggestedStepId);
        Assert.Equal(InferredFrom.Combat, result.InferredFrom);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I3 no correlation falls through to Rule 3 (talk / seq advance).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtI3_NoKillCorrelation_FallsThroughToTalkRule()
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 1,
            killCorrelatedTargets: null,
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("talk", result.StepType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I4 dominant nibble = highest FinalValue; tie → lowest VarIndex then Low.
    //
    // Case A: [(0,Low)]=([347],2) and [(1,High)]=([347],3) — different reached values.
    //         Highest FinalValue = 3 → primary (1,High).
    //         Expect: "questVariableHigh(65847, 1) >= 3".
    //
    // Case B: [(0,Low)]=([347],3) and [(0,High)]=([347],3) — tie on value.
    //         Tie-break: Low before High → primary (0,Low).
    //         Expect: "questVariableLow(65847, 0) >= 3".
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)] // Case A: higher FinalValue wins
    [InlineData(true)]  // Case B: tie → Low before High
    public void GwtI4_DominantNibble_HighestFinalValue_ThenLowestVarIndexThenLow(bool tieCase)
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);

        if (!tieCase)
        {
            // Case A: (1,High) wins because FinalValue 3 > 2
            var after = MakeSnapshot(
                questSequence: 0,
                killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
                {
                    [new NibbleKey(0, NibbleHalf.Low)]  = Corr([347u], 2),
                    [new NibbleKey(1, NibbleHalf.High)] = Corr([347u], 3)
                },
                capturedAt: T1);
            var result = engine.Infer(before, after);
            Assert.Equal("combat", result.StepType);
            Assert.True(result.SuggestedExpect == "questVariableHigh(65847, 1) >= 3",
                $"Highest-FinalValue dominance: expected (1,High). Got: {FormatResult(result)}");
        }
        else
        {
            // Case B: tie at 3 → (0,Low) wins because Low < High at same VarIndex
            var after = MakeSnapshot(
                questSequence: 0,
                killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
                {
                    [new NibbleKey(0, NibbleHalf.Low)]  = Corr([347u], 3),
                    [new NibbleKey(0, NibbleHalf.High)] = Corr([347u], 3)
                },
                capturedAt: T1);
            var result = engine.Infer(before, after);
            Assert.Equal("combat", result.StepType);
            Assert.True(result.SuggestedExpect == "questVariableLow(65847, 0) >= 3",
                $"Tie-break Low before High: expected (0,Low). Got: {FormatResult(result)}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I5 ambiguity flag — multi-target span.
    //
    // after: [(0,Low)]=([49,347],3) — one nibble, TWO targets.
    // Then StepType=="combat", SuggestedExpect=="questVariableLow(65847, 0) >= 3",
    //      Confidence==Low, Notes contains "Ambiguous" and "47" (DataId fragment) and "split",
    //      SuggestedStepId=="defeat-49" (lowest DataId).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtI5_AmbiguityFlag_MultiTargetSpan_ConfidenceLow_NotesContainsAmbiguous()
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 0,
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = Corr([49u, 347u], 3)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.Equal("questVariableLow(65847, 0) >= 3", result.SuggestedExpect);
        Assert.True(result.Confidence == Confidence.Low,
            $"Multi-target must yield Confidence.Low. Got: {FormatResult(result)}");
        Assert.NotNull(result.Notes);
        Assert.True(result.Notes!.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase),
            $"Notes must contain 'Ambiguous'. Got: {result.Notes}");
        Assert.True(result.Notes.Contains("47") || result.Notes.Contains("49") || result.Notes.Contains("347"),
            $"Notes must mention the DataIds. Got: {result.Notes}");
        Assert.True(result.Notes.Contains("split", StringComparison.OrdinalIgnoreCase),
            $"Notes must contain 'split'. Got: {result.Notes}");
        Assert.True(result.SuggestedStepId == "defeat-49",
            $"StepId must use lowest DataId (49). Got: {result.SuggestedStepId}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I6 ambiguity flag — multi-nibble span.
    //
    // after: [(0,Low)]=([347,49],3) and [(1,Low)]=([347,49],3) — TWO nibbles.
    // Then Confidence==Low, Notes contains "Ambiguous" and "2 nibble bump" (or similar),
    //      dominant (tie → (0,Low)): expect "questVariableLow(65847, 0) >= 3".
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtI6_AmbiguityFlag_MultiNibbleSpan_ConfidenceLow_NotesMentionsNibbles()
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 0,
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = Corr([347u, 49u], 3),
                [new NibbleKey(1, NibbleHalf.Low)] = Corr([347u, 49u], 3)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.True(result.Confidence == Confidence.Low,
            $"Multi-nibble must yield Confidence.Low. Got: {FormatResult(result)}");
        Assert.NotNull(result.Notes);
        Assert.True(result.Notes!.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase),
            $"Notes must contain 'Ambiguous'. Got: {result.Notes}");
        // Notes must mention nibble count / multi-nibble context
        Assert.True(
            result.Notes.Contains("nibble") || result.Notes.Contains("bump") || result.Notes.Contains("2"),
            $"Notes must mention nibble/bump count. Got: {result.Notes}");
        // Dominant by tie-break: (0,Low) wins over (1,Low) because VarIndex 0 < 1
        Assert.True(result.SuggestedExpect == "questVariableLow(65847, 0) >= 3",
            $"Dominant must be (0,Low) by tie-break. Got: {FormatResult(result)}");
    }
}
