using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice 2: StepInferenceEngine Rule 2.2 combat tests (GWT-I1..I5).
//
// These tests WILL FAIL because the following do not exist yet:
//   1. InferredFrom.Combat enum member
//   2. StepInferenceEngine Rule 2.2 (fires when KillCorrelatedTargets non-empty)
//   3. KillCorrelation record
//   4. GameStateSnapshot.KillCorrelatedTargets / QuestSequence must be readable
//   5. SnapshotAggregator.SequenceVariableIndex const = -1
//
// Builder: add InferredFrom.Combat and implement Rule 2.2. Then I1..I5 go green.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CombatInferenceEngineTests
{
    private static readonly QuestId Quest65847 = new(65847);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    // ─── Snapshot builder ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a baseline snapshot with sensible defaults. KillCorrelatedTargets
    /// is provided separately as a non-positional init field.
    /// </summary>
    private static GameStateSnapshot MakeSnapshot(
        int questSequence = 0,
        IReadOnlyDictionary<int, KillCorrelation>? killCorrelatedTargets = null,
        DateTimeOffset? capturedAt = null,
        NpcId? lastNpcInteracted = null,
        bool inCombat = false,
        int combatStartZone = 0,
        WorldPosition? combatStartPosition = null) =>
        new(
            CapturedAt:          capturedAt ?? T0,
            Zone:                new ZoneId(100),
            Position:            new WorldPosition(10, 0, 10),
            ActiveQuest:         Quest65847,
            QuestSequence:       questSequence,
            QuestFlags:          0,
            QuestAccepted:       false,
            QuestCompleted:      false,
            LastNpcInteracted:   lastNpcInteracted,
            LastNpcPosition:     null,
            LastDialoguePrompt:  null,
            LastDialogueAnswer:  null,
            InventoryHash:       0,
            LastAttuned:         null)
        {
            // Slice-2 non-positional fields — will be RED until GameStateSnapshot is extended
            KillCorrelatedTargets = killCorrelatedTargets,
            InCombat              = inCombat,
            CombatStartZone       = combatStartZone,
            CombatStartPosition   = combatStartPosition,
        };

    private static KillCorrelation Correlation(uint[] dataIds, int finalValue)
        => new(DataIds: dataIds, FinalValue: finalValue);

    private static string FormatResult(InferenceResult r)
        => $"StepType={r.StepType}, InferredFrom={r.InferredFrom}, Expect={r.SuggestedExpect}, Id={r.SuggestedStepId}";

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I1 — combat beats sequence advance → questVariable expect, not talk
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_I1_CombatBeatsSequenceAdvance_ReturnsQuestVariableExpect()
    {
        /*
         * RED: InferredFrom.Combat and Rule 2.2 do not exist.
         *
         * before: seq=0, no correlated targets.
         * after:  seq=3, KillCorrelatedTargets[0]=([347],3).
         *
         * Rule 2.2 must fire BEFORE Rule 3 (sequence advance).
         * Expect: StepType="combat", SuggestedExpect="questVariable(65847, 0) >= 3",
         *         InferredFrom=InferredFrom.Combat (NOT QuestSequenceChange or DialogueInteraction).
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 3,
            killCorrelatedTargets: new Dictionary<int, KillCorrelation>
            {
                [0] = Correlation([347u], 3)
            },
            lastNpcInteracted: new NpcId(9999u), // would fire Rule 7 if combat rule absent
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.Equal(InferredFrom.Combat, result.InferredFrom);
        Assert.Equal("questVariable(65847, 0) >= 3", result.SuggestedExpect);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I2 — sequence-only combat (index -1) → questSequence expect
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_I2_SequenceOnlyCombat_ReturnsQuestSequenceExpect()
    {
        /*
         * RED: same missing members as GWT-I1.
         *
         * after: KillCorrelatedTargets[-1]=([347],3), QuestSequence=3.
         * No variable index changed — only sequence advanced.
         * Expect: SuggestedExpect="questSequence(65847) >= 3".
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 3,
            killCorrelatedTargets: new Dictionary<int, KillCorrelation>
            {
                [SnapshotAggregator.SequenceVariableIndex] = Correlation([347u], 3)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.Equal("questSequence(65847) >= 3", result.SuggestedExpect);
        Assert.Equal(InferredFrom.Combat, result.InferredFrom);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I3 — no correlation → falls through to existing Rule 3 (talk)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_I3_NoKillCorrelation_FallsThroughToExistingRules()
    {
        /*
         * RED: this test should PASS once Rule 2.2 is correctly guarded (only fires when
         * KillCorrelatedTargets is non-empty). It is included to verify the guard does not
         * break existing inference. If it fails, Rule 2.2 has an incorrect guard.
         *
         * after: empty KillCorrelatedTargets, seq advanced 0→1 (Rule 3 fires → talk).
         * Then StepType="talk" (Rule 3 unchanged).
         *
         * NOTE: this test uses existing (already-working) engine paths. It will compile and
         * execute today — but the `KillCorrelatedTargets` init property does not exist on
         * GameStateSnapshot yet, so it causes a compile error. Fixing the compile error is
         * the only blocker; the assertion should hold once Rule 2.2 is correctly guarded.
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 1,
            killCorrelatedTargets: null,  // no combat correlation
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("talk", result.StepType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I4 — multi-index split: primary = largest FinalValue, Notes mentions split
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_I4_MultiIndexCorrelation_PrimaryUsesLargestFinalValue_NotesHasSplit()
    {
        /*
         * RED: same missing members as GWT-I1.
         *
         * after: KillCorrelatedTargets[0]=([347],3), [1]=([400],1).
         * Primary = index 0 (FinalValue 3 > 1).
         * SuggestedExpect contains questVariable(65847, 0) >= 3.
         * Notes must mention index 1 / split, and AND-join questVariable(65847, 1) >= 1.
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 0,
            killCorrelatedTargets: new Dictionary<int, KillCorrelation>
            {
                [0] = Correlation([347u], 3),
                [1] = Correlation([400u], 1)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.NotNull(result.SuggestedExpect);
        Assert.Contains("questVariable(65847, 0) >= 3", result.SuggestedExpect!);
        // Notes must indicate multi-index split
        Assert.NotNull(result.Notes);
        Assert.True(
            result.Notes!.Contains("1") || result.Notes.Contains("split") || result.Notes.Contains("index"),
            $"Notes must mention the secondary index split. Got Notes: {result.Notes}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-I5 — SuggestedStepId uses lowest data-id in the union
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GWT_I5_StepIdUsesLowestDataId()
    {
        /*
         * RED: same missing members as GWT-I1.
         *
         * DataIds = {348, 347} (unordered). Lowest = 347.
         * Then SuggestedStepId == "defeat-347".
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 0);
        var after = MakeSnapshot(
            questSequence: 0,
            killCorrelatedTargets: new Dictionary<int, KillCorrelation>
            {
                [0] = Correlation([348u, 347u], 2)
            },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("combat", result.StepType);
        Assert.Equal("defeat-347", result.SuggestedStepId);
    }
}
