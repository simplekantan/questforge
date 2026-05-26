using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice A (RED): StepInferenceEngine Rule 2.2 nibble-level tests (GWT-I1'..I6').
//
// DELETED (byte-level, obsolete):
//   GWT-I1  — questVariable(65847, 0) >= 3 (byte expect, replaced by nibble L/H)
//   GWT-I2  — questSequence(-1 key) → questSequence expect (SequenceVariableIndex deleted)
//   GWT-I4  — multi-index and-join (removed; secondaries go to Notes only)
//   GWT-I5  — step-id from union of all DataIds (now dominant key only)
//
// These compile-fail on:
//   NibbleKey, NibbleHalf, IReadOnlyDictionary<NibbleKey, KillCorrelation>
//   (SnapshotAggregator.SequenceVariableIndex reference removed)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatInferenceEngineTests
{
    private static readonly QuestId Quest65847 = new(65847);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static string FormatResult(InferenceResult r)
        => $"StepType={r.StepType}, InferredFrom={r.InferredFrom}, Expect={r.SuggestedExpect}, Id={r.SuggestedStepId}";

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
    // GWT-I1' — combat beats sequence advance and Rule 7 NPC → low-nibble expect.
    //
    // before seq=0; after seq=3, [(0,Low)]=([347],3), LastNpcInteracted=9999.
    // Rule 2.2 must fire before Rule 3 (seq advance) and Rule 7 (NPC).
    // Expect: StepType="combat", SuggestedExpect="questVariableLow(65847, 0) >= 3",
    //         InferredFrom=Combat.
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
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I2' — high-nibble produces questVariableHigh expect.
    //
    // after: [(1,High)]=([338],3).
    // Expect: SuggestedExpect="questVariableHigh(65847, 1) >= 3".
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
        Assert.Equal(InferredFrom.Combat, result.InferredFrom);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I3' — no correlation falls through to existing Rule 3 (talk / seq advance).
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
    // GWT-I4' — dominant = most-distinct-DataIds; secondaries in Notes, NOT and-joined.
    //
    // [(0,Low)]=([347,348],3) → 2 DataIds (dominant)
    // [(1,Low)]=([49],1)      → 1 DataId  (secondary → Notes only)
    //
    // Assert:
    //   primary is (0,Low); SuggestedExpect="questVariableLow(65847, 0) >= 3"
    //   SuggestedExpect does NOT contain " and " (no and-join)
    //   SuggestedExpect does NOT contain "questVariableLow(65847, 1)" (no secondary)
    //   Notes mentions V1-Low / 49 / "split"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtI4_DominantNibble_MostDistinctDataIds_SecondariesInNotes_NotAndJoined()
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 0,
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = Corr([347u, 348u], 3),
                [new NibbleKey(1, NibbleHalf.Low)] = Corr([49u], 1)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.NotNull(result.SuggestedExpect);
        Assert.Equal("questVariableLow(65847, 0) >= 3", result.SuggestedExpect);

        // Critical: no and-join
        Assert.DoesNotContain(" and ", result.SuggestedExpect!,
            StringComparison.Ordinal);

        // Critical: secondary predicate not in expect string
        Assert.DoesNotContain("questVariableLow(65847, 1)", result.SuggestedExpect,
            StringComparison.Ordinal);

        // Notes must acknowledge the secondary
        Assert.NotNull(result.Notes);
        Assert.True(
            result.Notes!.Contains("split") || result.Notes.Contains("V1") || result.Notes.Contains("49"),
            $"Notes must mention the secondary nibble / DataId 49 / 'split'. Got Notes: {result.Notes}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I5' — dominance tie-break: equal DataId counts → lowest VarIndex, then Low.
    //
    // Case A: [(1,Low)]=([49],3) vs [(0,High)]=([338],3) — both 1 DataId.
    //         Tie-break: VarIndex 0 < 1 → primary (0,High).
    //         Expect: "questVariableHigh(65847, 0) >= 3".
    //
    // Case B: [(0,High)]=([338],2) vs [(0,Low)]=([347],2) — both 1 DataId, same VarIndex.
    //         Tie-break: Low(0) before High(1) → primary (0,Low).
    //         Expect: "questVariableLow(65847, 0) >= 2".
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)] // Case A: lower VarIndex wins
    [InlineData(true)]  // Case B: Low before High at same VarIndex
    public void GwtI5_TieBreak_LowestVarIndexThenLow(bool sameVarIndex)
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);

        InferenceResult result;
        if (!sameVarIndex)
        {
            // Case A
            var after = MakeSnapshot(
                questSequence: 0,
                killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
                {
                    [new NibbleKey(1, NibbleHalf.Low)]  = Corr([49u],  3),
                    [new NibbleKey(0, NibbleHalf.High)] = Corr([338u], 3)
                },
                capturedAt: T1);
            result = engine.Infer(before, after);
            Assert.Equal("questVariableHigh(65847, 0) >= 3", result.SuggestedExpect);
        }
        else
        {
            // Case B
            var after = MakeSnapshot(
                questSequence: 0,
                killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
                {
                    [new NibbleKey(0, NibbleHalf.High)] = Corr([338u], 2),
                    [new NibbleKey(0, NibbleHalf.Low)]  = Corr([347u], 2)
                },
                capturedAt: T1);
            result = engine.Infer(before, after);
            Assert.Equal("questVariableLow(65847, 0) >= 2", result.SuggestedExpect);
        }

        Assert.Equal("combat", result.StepType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I6' — SuggestedStepId uses lowest DataId of the dominant key's set.
    //
    // [(0,Low)]=([348,347],2) → lowest DataId of dominant = 347.
    // Then SuggestedStepId == "defeat-347".
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtI6_StepId_UsesLowestDataIdOfDominantKey()
    {
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 0,
            killCorrelatedTargets: new Dictionary<NibbleKey, KillCorrelation>
            {
                [new NibbleKey(0, NibbleHalf.Low)] = Corr([348u, 347u], 2)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.Equal("defeat-347", result.SuggestedStepId);
    }
}
