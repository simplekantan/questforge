using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names (AetheryteId). Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for UseEmoteStep authoring inference — covers three areas:
///   UEI1..UEI8  : StepInferenceEngine Rule 3.5e — EmoteCompleted (priority pinning)
///   UEI9        : SnapshotAggregator.OnEmoteCompleted / OnEmoteConsumed
///   UEI10..UEI11: StepFactory.Build "use-emote" arm
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - EmoteCompletedSignal record in GameStateSnapshot.cs              (Task UEI-T1)
///   - GameStateSnapshot.EmoteCompleted property                         (Task UEI-T1)
///   - InferredFrom.EmoteCompleted enum value                            (Task UEI-T2)
///   - SnapshotAggregator.OnEmoteCompleted / OnEmoteConsumed             (Task UEI-T3)
///   - StepInferenceEngine Rule 3.5e (EmoteCompleted check above Rule 3.5)(Task UEI-T4)
///   - StepFactory.Build "use-emote" arm                                  (Task UEI-T5)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~UseEmoteInferenceTests"
/// </summary>
public sealed class UseEmoteInferenceTests
{
    private static readonly QuestId    ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0       = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors UseActionInferenceTests.MakeSnapshot)
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ZoneId?    zone           = null,
        bool       questAccepted  = false,
        bool       questCompleted = false,
        int        questSequence  = 0,
        IReadOnlyList<uint>? keyItemsAdded = null) =>
        new(
            CapturedAt:          T0,
            Zone:                zone ?? new ZoneId(131),
            Position:            new WorldPosition(0, 0, 0),
            ActiveQuest:         ActiveQuest,
            QuestSequence:       questSequence,
            QuestFlags:          0,
            QuestAccepted:       questAccepted,
            QuestCompleted:      questCompleted,
            LastNpcInteracted:   null,
            LastNpcPosition:     null,
            LastDialoguePrompt:  null,
            LastDialogueAnswer:  null,
            InventoryHash:       0,
            LastAttuned:         null)
        {
            KeyItemsAdded = keyItemsAdded
        };

    // =========================================================================
    // UEI1 — Happy path, no target: EmoteCompleted set → infers "use-emote" step
    // =========================================================================

    [Fact]
    public void UseEmoteInference_HappyPath_NoTarget_InfersUseEmoteStep_UEI1()
    {
        // CONTRACT: Given EmoteCompleted = EmoteCompletedSignal(17, null) (self-cast /cheer),
        //           no zone change and no quest changes,
        //           When Infer is called,
        //           Then StepType="use-emote", SuggestedStepId="use-emote-17",
        //                SuggestedExpect=null, Confidence=High, InferredFrom=EmoteCompleted,
        //                Notes is non-null and contains "Expect".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-emote",                    result.StepType);
        Assert.Equal("use-emote-17",                 result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.High,                result.Confidence);
        Assert.Equal(InferredFrom.EmoteCompleted,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("Expect", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // UEI2 — NPC target: EmoteCompleted with TargetBaseId → step id includes "-on-{tid}"
    // =========================================================================

    [Fact]
    public void UseEmoteInference_WithTarget_StepIdIncludesOnSuffix_UEI2()
    {
        // CONTRACT: Given EmoteCompleted = EmoteCompletedSignal(17, 1000789),
        //           When Infer is called,
        //           Then SuggestedStepId="use-emote-17-on-1000789".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: 1000789u)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-emote",                  result.StepType);
        Assert.Equal("use-emote-17-on-1000789",    result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(InferredFrom.EmoteCompleted,  result.InferredFrom);
    }

    // =========================================================================
    // UEI3 — Self-cast: TargetBaseId = null → step id has NO "-on-" suffix
    // =========================================================================

    [Fact]
    public void UseEmoteInference_SelfCast_StepIdHasNoOnSuffix_UEI3()
    {
        // CONTRACT: Given EmoteCompleted = EmoteCompletedSignal(7, null) (salute, self-cast),
        //           When Infer is called,
        //           Then SuggestedStepId="use-emote-7" — NO "-on-" suffix.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 7u, TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-emote",                 result.StepType);
        Assert.Equal("use-emote-7",               result.SuggestedStepId);
        Assert.DoesNotContain("-on-",             result.SuggestedStepId, StringComparison.Ordinal);
        Assert.Equal(InferredFrom.EmoteCompleted, result.InferredFrom);
    }

    // =========================================================================
    // UEI4 — Priority ABOVE Rule 3 (QuestSequence advanced): EmoteCompleted wins
    // =========================================================================

    [Fact]
    public void UseEmoteInference_WinsOverQuestSequenceAdvance_UEI4()
    {
        // CONTRACT: Given EmoteCompleted set AND QuestSequence advances (1→2) in the same window,
        //           When Infer is called,
        //           Then StepType="use-emote" (NOT "talk") because Rule 3.5e fires BEFORE Rule 3.
        //
        // RATIONALE: Use-emote that advances the sequence is the most common authoring case.
        //            Rule 3 first would draft a stale "talk" step the author would have to delete.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 1);
        var after  = before with
        {
            QuestSequence  = 2,
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: 1000789u)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-emote",                 result.StepType);
        Assert.Equal(InferredFrom.EmoteCompleted, result.InferredFrom);
        Assert.NotEqual("talk",                   result.StepType);
    }

    // =========================================================================
    // UEI5 — Priority ABOVE Rule 4 (Zone changed): EmoteCompleted wins (defensive)
    // =========================================================================

    [Fact]
    public void UseEmoteInference_WinsOverZoneChange_UEI5()
    {
        // CONTRACT: Given EmoteCompleted set AND zone changed (defensive — should not occur in
        //           production but fields are independent),
        //           When Infer is called,
        //           Then StepType="use-emote" (NOT "travel").

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132));
        var after  = before with
        {
            Zone           = new ZoneId(129),
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-emote",                 result.StepType);
        Assert.Equal("use-emote-17",              result.SuggestedStepId);
        Assert.Equal(InferredFrom.EmoteCompleted, result.InferredFrom);
        Assert.NotEqual("travel",                 result.StepType);
    }

    // =========================================================================
    // UEI6 — Priority ABOVE Rule 3.5 (ActionCompleted): EmoteCompleted wins when both set
    // =========================================================================

    [Fact]
    public void UseEmoteInference_WinsOverActionCompleted_WhenBothSet_UEI6()
    {
        // CONTRACT: Given both EmoteCompleted AND ActionCompleted set (defensive — mutually
        //           exclusive in production but fields are independent),
        //           When Infer is called,
        //           Then StepType="use-emote" (NOT "use-action").
        //           Pins Decision UEI13: emote is the more deliberate authoring action.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: null),
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-emote",                 result.StepType);
        Assert.Equal("use-emote-17",              result.SuggestedStepId);
        Assert.Equal(InferredFrom.EmoteCompleted, result.InferredFrom);
        Assert.NotEqual("use-action",             result.StepType);
    }

    // =========================================================================
    // UEI7 — Priority BELOW Rule 1 (QuestCompleted): turn-in wins over EmoteCompleted
    // =========================================================================

    [Fact]
    public void UseEmoteInference_LosesToQuestCompleted_Rule1Wins_UEI7()
    {
        // CONTRACT: Given QuestCompleted=true AND EmoteCompleted set in the same window,
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1), NOT "use-emote".
        //
        // RATIONALE: "Quest completed" is the definitive authoring intent — emote was incidental.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = before with
        {
            QuestCompleted = true,
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in",                    result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted,  result.InferredFrom);
        Assert.NotEqual("use-emote",               result.StepType);
    }

    // =========================================================================
    // UEI8 — Priority BELOW Rule 2.3 (KeyItemsAdded): pickup-item wins over EmoteCompleted
    // =========================================================================

    [Fact]
    public void UseEmoteInference_LosesToKeyItemsAdded_Rule23Wins_UEI8()
    {
        // CONTRACT: Given KeyItemsAdded=[2001u] AND EmoteCompleted set,
        //           When Infer is called,
        //           Then StepType="pickup-item" (Rule 2.3), NOT "use-emote".
        //
        // RATIONALE: Key-item acquisition is a more specific authoring intent.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            KeyItemsAdded  = [2001u],
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("pickup-item",                      result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction,   result.InferredFrom);
        Assert.NotEqual("use-emote",                     result.StepType);
    }

    // =========================================================================
    // UEI9 — Aggregator: OnEmoteCompleted sets field; ResetDeltas does NOT clear;
    //         OnEmoteConsumed clears without side effects on unrelated fields
    // =========================================================================

    [Fact]
    public void Aggregator_OnEmoteCompleted_SetsField_ResetDeltasSurvives_OnEmoteConsumedClears_UEI9()
    {
        // CONTRACT (sub-A): Given a fresh SnapshotAggregator,
        //   When OnEmoteCompleted(17u, 1000789u) is called,
        //   Then agg.Current.EmoteCompleted is non-null with correct EmoteId and TargetBaseId.
        //
        // CONTRACT (sub-B): When ResetDeltas() is called,
        //   Then agg.Current.EmoteCompleted is STILL non-null (survives per-window lifecycle).
        //
        // CONTRACT (sub-C): When OnEmoteConsumed() is called,
        //   Then agg.Current.EmoteCompleted is null.
        //   AND LastNpcInteracted, LastAttuned, LastAethernetShardInteracted, ActionCompleted
        //   are all null (no side effects — pins Decision UEI3).

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        // ── sub-A: OnEmoteCompleted sets the field ──────────────────────────
        agg.OnEmoteCompleted(emoteId: 17u, targetBaseId: 1000789u);
        var snapA = agg.Current;

        Assert.NotNull(snapA.EmoteCompleted);
        Assert.Equal(17u,      snapA.EmoteCompleted!.EmoteId);
        Assert.Equal(1000789u, snapA.EmoteCompleted.TargetBaseId);

        // ── sub-B: ResetDeltas does NOT clear EmoteCompleted ────────────────
        agg.ResetDeltas();
        var snapB = agg.Current;

        Assert.NotNull(snapB.EmoteCompleted);
        Assert.Equal(17u, snapB.EmoteCompleted!.EmoteId);

        // ── sub-C: OnEmoteConsumed clears and has no side effects ───────────
        agg.OnEmoteConsumed();
        var snapC = agg.Current;

        Assert.Null(snapC.EmoteCompleted);
        // Side-effect guards (Decision UEI3)
        Assert.Null(snapC.LastNpcInteracted);
        Assert.Null(snapC.LastAttuned);
        Assert.Null(snapC.LastAethernetShardInteracted);
        Assert.Null(snapC.ActionCompleted);
    }

    // =========================================================================
    // UEI10 — StepFactory "use-emote" arm: builds UseEmoteStep with snapshot fields AND Motion=true
    // =========================================================================

    [Fact]
    public void StepFactory_UseEmote_ProducesUseEmoteStepWithMotionTrue_UEI10()
    {
        // CONTRACT: Given after.EmoteCompleted = EmoteCompletedSignal(17, 1000789),
        //           When Build("use-emote", "use-emote-17-on-1000789", null, after) is called,
        //           Then:
        //             - step is UseEmoteStep
        //             - Id == "use-emote-17-on-1000789"
        //             - EmoteId == 17u
        //             - TargetNpcId == 1000789u
        //             - Motion == true (Decision UEI6 — always infer the canonical authoring default)
        //             - Expect is null

        var after = MakeSnapshot() with
        {
            EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: 1000789u)
        };

        var step = StepFactory.Build("use-emote", "use-emote-17-on-1000789", null, after);

        var ue = Assert.IsType<QuestForge.Schema.UseEmoteStep>(step);
        Assert.Equal("use-emote-17-on-1000789", ue.Id);
        Assert.Equal(17u,      ue.EmoteId);
        Assert.Equal(1000789u, ue.TargetNpcId);
        Assert.True(ue.Motion);
        Assert.Null(ue.Expect);
    }

    // =========================================================================
    // UEI11 — StepFactory "use-emote" arm defensive: null EmoteCompleted → EmoteId(0), no throw
    // =========================================================================

    [Fact]
    public void StepFactory_UseEmote_NullEmoteCompleted_FallsBackToZeroId_UEI11()
    {
        // CONTRACT: Given after.EmoteCompleted is null (defensive caller),
        //           When Build("use-emote", "use-emote-X", null, after) is called,
        //           Then:
        //             - step is UseEmoteStep (no exception)
        //             - EmoteId == 0u (fallback; validator catches via E9)
        //             - TargetNpcId is null
        //             - Motion == true (default still applied, Decision UEI6)
        //
        // WHY: The inference engine only returns "use-emote" when EmoteCompleted is set,
        //      but defensive callers (e.g., UI auto-build) must not crash.

        var after = MakeSnapshot(); // EmoteCompleted is null

        var ex = Record.Exception(() =>
        {
            var step = StepFactory.Build("use-emote", "use-emote-X", null, after);
            var ue = Assert.IsType<QuestForge.Schema.UseEmoteStep>(step);
            Assert.Equal(0u,  ue.EmoteId);
            Assert.Null(ue.TargetNpcId);
            Assert.True(ue.Motion);
        });

        Assert.Null(ex);
    }
}
