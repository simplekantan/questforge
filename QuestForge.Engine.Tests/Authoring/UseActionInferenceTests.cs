using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names (AetheryteId). Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for UseActionStep authoring inference — covers three areas:
///   UAI1..UAI7  : StepInferenceEngine Rule 3.5 — ActionCompleted
///   UAI8..UAI10 : SnapshotAggregator.OnActionCompleted / OnActionConsumed
///   UAI11..UAI11b: StepFactory.Build "use-action" arm
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - ActionCompletedSignal record in GameStateSnapshot.cs              (Task UAI-T2)
///   - GameStateSnapshot.ActionCompleted property                         (Task UAI-T2)
///   - InferredFrom.ActionCompleted enum value                            (Task UAI-T3)
///   - SnapshotAggregator.OnActionCompleted / OnActionConsumed            (Task UAI-T4)
///   - StepInferenceEngine Rule 3.5 (ActionCompleted check)              (Task UAI-T5)
///   - StepFactory.Build "use-action" arm                                 (Task UAI-T6)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~UseActionInferenceTests"
/// </summary>
public sealed class UseActionInferenceTests
{
    private static readonly QuestId   ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0      = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors TeleportInferenceTests.MakeSnapshot)
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ZoneId?    zone             = null,
        WorldPosition? position     = null,
        bool       questAccepted    = false,
        bool       questCompleted   = false,
        int        questSequence    = 0,
        IReadOnlyList<uint>? keyItemsAdded = null,
        AetheryteId? teleportCompleted     = null,
        ActionCompletedSignal? actionCompleted = null) =>
        new(
            CapturedAt:          T0,
            Zone:                zone ?? new ZoneId(131),
            Position:            position ?? new WorldPosition(0, 0, 0),
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
            KeyItemsAdded       = keyItemsAdded,
            TeleportCompleted   = teleportCompleted,
            ActionCompleted     = actionCompleted
        };

    // =========================================================================
    // UAI1 — Happy path, no target: ActionCompleted set → infers "use-action" step
    // =========================================================================

    [Fact]
    public void UseActionInference_HappyPath_NoTarget_InfersUseActionStep_UAI1()
    {
        // CONTRACT: Given ActionCompleted = Action(31, null) in the after snapshot,
        //           no zone change and no quest changes,
        //           When Infer is called,
        //           Then StepType="use-action", SuggestedStepId="use-action-31",
        //                SuggestedExpect=null, Confidence=High, InferredFrom=ActionCompleted,
        //                Notes is non-null and contains "Expect".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-action",                     result.StepType);
        Assert.Equal("use-action-31",                  result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.High,                  result.Confidence);
        Assert.Equal(InferredFrom.ActionCompleted,     result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("Expect", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // UAI2 — Happy path, with NPC target: SuggestedStepId has "-on-<id>" suffix
    // =========================================================================

    [Fact]
    public void UseActionInference_WithTarget_StepIdIncludesOnSuffix_UAI2()
    {
        // CONTRACT: Given ActionCompleted = Action(31, TargetBaseId=2001234),
        //           When Infer is called,
        //           Then SuggestedStepId="use-action-31-on-2001234".

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: 2001234u)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-action",                 result.StepType);
        Assert.Equal("use-action-31-on-2001234",   result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(InferredFrom.ActionCompleted, result.InferredFrom);
    }

    // =========================================================================
    // UAI3 — Self-cast (null target): SuggestedStepId has NO "-on-" suffix
    // =========================================================================

    [Fact]
    public void UseActionInference_SelfCast_StepIdHasNoOnSuffix_UAI3()
    {
        // CONTRACT: Given ActionCompleted = GeneralAction(4, null) (Sprint, self-cast),
        //           When Infer is called,
        //           Then SuggestedStepId="use-action-4" — no "-on-" suffix.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.GeneralAction,
                ActionId:     4u,
                TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-action",                 result.StepType);
        Assert.Equal("use-action-4",               result.SuggestedStepId);
        Assert.Equal(InferredFrom.ActionCompleted, result.InferredFrom);
    }

    // =========================================================================
    // UAI4 — Priority ABOVE Rule 3 (QuestSequence advanced): use-action wins
    // =========================================================================

    [Fact]
    public void UseActionInference_WinsOverQuestSequenceAdvance_UAI4()
    {
        // CONTRACT: Given ActionCompleted set AND QuestSequence advances (1→2)
        //           in the same window,
        //           When Infer is called,
        //           Then StepType="use-action" (NOT "talk") because Rule 3.5 fires
        //           BEFORE Rule 3 (per Decision UAI4 final placement).
        //
        // RATIONALE: If the action IS the quest objective, inferring "talk" would be wrong.
        //            The author's intent is "I used THIS action to advance the sequence."

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 1);
        var after  = before with
        {
            QuestSequence   = 2,
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: 2001234u)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-action",                 result.StepType);
        Assert.Equal(InferredFrom.ActionCompleted, result.InferredFrom);
    }

    // =========================================================================
    // UAI5 — Priority ABOVE Rule 4.0 (TeleportCompleted): use-action wins (defensive)
    // =========================================================================

    [Fact]
    public void UseActionInference_WinsOverTeleportCompleted_WhenBothSet_UAI5()
    {
        // CONTRACT: Given both ActionCompleted=GeneralAction(5,null) AND TeleportCompleted set
        //           AND zone changed (defensive — should not occur in production),
        //           When Infer is called,
        //           Then StepType="use-action" (NOT "teleport"), SuggestedStepId="use-action-5".
        //
        // WHY 3.5 > 4.0: ActionCompleted is the more specific signal (exact action id + target);
        //                 TeleportCompleted is retained as a fallback when no action was recorded.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132));
        var after  = before with
        {
            Zone              = new ZoneId(129),
            ActionCompleted   = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.GeneralAction,
                ActionId:     5u,
                TargetBaseId: null),
            TeleportCompleted = new AetheryteId(8)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("use-action",                 result.StepType);
        Assert.Equal("use-action-5",               result.SuggestedStepId);
        Assert.Equal(InferredFrom.ActionCompleted, result.InferredFrom);
    }

    // =========================================================================
    // UAI6 — Priority BELOW Rule 1 (QuestCompleted): turn-in wins over ActionCompleted
    // =========================================================================

    [Fact]
    public void UseActionInference_LosesToQuestCompleted_Rule1Wins_UAI6()
    {
        // CONTRACT: Given QuestCompleted=true AND ActionCompleted set in the same window,
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1), NOT "use-action".
        //
        // RATIONALE: "Quest completed" is the definitive authoring intent for this window —
        //            the action was incidental to the turn-in.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = before with
        {
            QuestCompleted  = true,
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in",                  result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }

    // =========================================================================
    // UAI7 — Priority BELOW Rule 2.3 (KeyItemsAdded): pickup-item wins over ActionCompleted
    // =========================================================================

    [Fact]
    public void UseActionInference_LosesToKeyItemsAdded_Rule23Wins_UAI7()
    {
        // CONTRACT: Given KeyItemsAdded=[2001u] AND ActionCompleted set,
        //           When Infer is called,
        //           Then StepType="pickup-item" (Rule 2.3), NOT "use-action".
        //
        // RATIONALE: Key-item acquisition is a more specific authoring intent than
        //            "an action was pressed." The action was the delivery mechanism.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            KeyItemsAdded   = [2001u],
            ActionCompleted = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("pickup-item",                       result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction,    result.InferredFrom);
    }

    // =========================================================================
    // UAI8 — Aggregator: OnActionCompleted sets the ActionCompleted field
    // =========================================================================

    [Fact]
    public void Aggregator_OnActionCompleted_SetsField_UAI8()
    {
        // CONTRACT: Given a fresh SnapshotAggregator,
        //           When OnActionCompleted(Action, 31u, 2001234u) is called,
        //           Then agg.Current.ActionCompleted is non-null with the correct values.
        //
        // ALSO PINS: OnActionCompleted does NOT touch LastNpcInteracted, LastAttuned,
        //            or LastAethernetShardInteracted (Decision UAI3).

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        agg.OnActionCompleted(
            QuestForge.Schema.ActionType.Action,
            actionId:     31u,
            targetBaseId: 2001234u);

        var snap = agg.Current;

        Assert.NotNull(snap.ActionCompleted);
        Assert.Equal(QuestForge.Schema.ActionType.Action, snap.ActionCompleted!.ActionType);
        Assert.Equal(31u,      snap.ActionCompleted.ActionId);
        Assert.Equal(2001234u, snap.ActionCompleted.TargetBaseId);

        // Side-effect guard (Decision UAI3)
        Assert.Null(snap.LastNpcInteracted);
        Assert.Null(snap.LastAttuned);
        Assert.Null(snap.LastAethernetShardInteracted);
    }

    // =========================================================================
    // UAI9 — Aggregator: OnActionConsumed clears the field
    // =========================================================================

    [Fact]
    public void Aggregator_OnActionConsumed_ClearsField_UAI9()
    {
        // CONTRACT: Given OnActionCompleted was called,
        //           When OnActionConsumed() is called,
        //           Then agg.Current.ActionCompleted is null.
        //
        // Also pins that the field is null after consumption, not stale.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);
        agg.OnActionCompleted(QuestForge.Schema.ActionType.Action, 31u, 2001234u);
        Assert.NotNull(agg.Current.ActionCompleted); // sanity

        agg.OnActionConsumed();

        Assert.Null(agg.Current.ActionCompleted);
        // Side-effect guard: other state untouched
        Assert.Null(agg.Current.LastNpcInteracted);
        Assert.Null(agg.Current.LastAttuned);
        Assert.Null(agg.Current.LastAethernetShardInteracted);
    }

    // =========================================================================
    // UAI10 — Aggregator: ResetDeltas does NOT clear ActionCompleted
    // =========================================================================

    [Fact]
    public void Aggregator_ResetDeltas_DoesNotClearActionCompleted_UAI10()
    {
        // CONTRACT: Given OnActionCompleted was called,
        //           When ResetDeltas() is called (per-window delta reset),
        //           Then agg.Current.ActionCompleted is still set.
        //
        // WHY: ActionCompleted survives the per-window reset so PreviewInference can see it
        //      when the author opens the Record-Step modal. Mirrors TeleportCompleted lifecycle.

        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);
        agg.OnActionCompleted(QuestForge.Schema.ActionType.Action, 31u, null);

        agg.ResetDeltas();

        Assert.NotNull(agg.Current.ActionCompleted);
        Assert.Equal(31u, agg.Current.ActionCompleted!.ActionId);
    }

    // =========================================================================
    // UAI11 — StepFactory "use-action" arm: builds UseActionStep with snapshot fields
    // =========================================================================

    [Fact]
    public void StepFactory_UseAction_ProducesUseActionStep_UAI11()
    {
        // CONTRACT: Given after.ActionCompleted = Action(31, TargetBaseId=2001234),
        //           When Build("use-action", "use-action-31-on-2001234", null, after) is called,
        //           Then:
        //             - step is UseActionStep
        //             - Id == "use-action-31-on-2001234"
        //             - ActionType == Action
        //             - ActionId == 31u
        //             - TargetNpcId == 2001234u
        //             - Expect is null

        var after = MakeSnapshot(
            actionCompleted: new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action,
                ActionId:     31u,
                TargetBaseId: 2001234u));

        var step = StepFactory.Build("use-action", "use-action-31-on-2001234", null, after);

        var us = Assert.IsType<QuestForge.Schema.UseActionStep>(step);
        Assert.Equal("use-action-31-on-2001234",       us.Id);
        Assert.Equal(QuestForge.Schema.ActionType.Action, us.ActionType);
        Assert.Equal(31u,      us.ActionId);
        Assert.Equal(2001234u, us.TargetNpcId);
        Assert.Null(us.Expect);
    }

    // =========================================================================
    // UAI11b — StepFactory "use-action" arm defensive: null ActionCompleted → no throw
    // =========================================================================

    [Fact]
    public void StepFactory_UseAction_NullActionCompleted_FallsBackToZeroId_UAI11b()
    {
        // CONTRACT: Given after.ActionCompleted is null (defensive caller),
        //           When Build("use-action", "use-action-X", null, after) is called,
        //           Then:
        //             - step is UseActionStep (no exception)
        //             - ActionId == 0u (fallback; validator will reject)
        //             - TargetNpcId is null
        //             - ActionType == ActionType.Action (the default arm)
        //
        // WHY: The inference engine only returns "use-action" when ActionCompleted is set,
        //      but defensive callers (e.g., UI auto-build) must not crash.

        var after = MakeSnapshot(); // ActionCompleted is null

        var ex = Record.Exception(() =>
        {
            var step = StepFactory.Build("use-action", "use-action-X", null, after);
            var us = Assert.IsType<QuestForge.Schema.UseActionStep>(step);
            Assert.Equal(0u,  us.ActionId);
            Assert.Null(us.TargetNpcId);
            Assert.Equal(QuestForge.Schema.ActionType.Action, us.ActionType);
        });

        Assert.Null(ex);
    }
}
