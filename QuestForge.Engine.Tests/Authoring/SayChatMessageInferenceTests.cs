using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// TODO: SayChatMessageSentSignal — Builder must add to GameStateSnapshot.cs:
//         public sealed record SayChatMessageSentSignal(string Message, uint? TargetBaseId);
//         public SayChatMessageSentSignal? SayChatMessageSent { get; init; }
// TODO: InferredFrom.SayChatMessageSent — Builder must append to InferredFrom.cs enum.
// TODO: SnapshotAggregator.OnSayChatMessageSent(string, uint?) / OnSayChatMessageConsumed() — Builder must add.
// TODO: StepInferenceEngine Rule 3.5s — Builder must insert immediately above Rule 3.5e (EmoteCompleted).
// TODO: StepFactory "say-chat-message" arm — Builder must add to StepFactory.Build switch.

// WHY: QuestForge.Schema is NOT imported globally to avoid ambiguity between Schema types and
// Adapters.Types on common names. Schema types are referenced with full qualification.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for SayChatMessageStep authoring inference — covers three areas:
///   SCI1..SCI8  : StepInferenceEngine Rule 3.5s — SayChatMessageSent (priority pinning)
///   SCI9        : SnapshotAggregator.OnSayChatMessageSent / OnSayChatMessageConsumed
///   SCI10..SCI11: StepFactory.Build "say-chat-message" arm
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - SayChatMessageSentSignal record in GameStateSnapshot.cs              (Task SCI-T1)
///   - GameStateSnapshot.SayChatMessageSent property                         (Task SCI-T1)
///   - InferredFrom.SayChatMessageSent enum value                            (Task SCI-T2)
///   - SnapshotAggregator.OnSayChatMessageSent / OnSayChatMessageConsumed   (Task SCI-T3)
///   - StepInferenceEngine Rule 3.5s (above Rule 3.5e)                       (Task SCI-T4)
///   - StepFactory.Build "say-chat-message" arm                              (Task SCI-T5)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~SayChatMessageInferenceTests"
/// </summary>
public sealed class SayChatMessageInferenceTests
{
    private static readonly QuestId ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper (mirrors UseEmoteInferenceTests.MakeSnapshot)
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        bool questAccepted = false,
        bool questCompleted = false,
        int questSequence = 0,
        IReadOnlyList<uint>? keyItemsAdded = null) =>
        new(
            CapturedAt:         T0,
            Zone:               zone ?? new ZoneId(131),
            Position:           new WorldPosition(0, 0, 0),
            ActiveQuest:        ActiveQuest,
            QuestSequence:      questSequence,
            QuestFlags:         0,
            QuestAccepted:      questAccepted,
            QuestCompleted:     questCompleted,
            LastNpcInteracted:  null,
            LastNpcPosition:    null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash:      0,
            LastAttuned:        null)
        {
            KeyItemsAdded = keyItemsAdded
        };

    // =========================================================================
    // SCI1 — Happy path, no target: SayChatMessageSent set → infers "say-chat-message" step
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_HappyPath_NoTarget_InfersSayChatMessageStep_SCI1()
    {
        // CONTRACT: Given SayChatMessageSent = SayChatMessageSentSignal("Open Sesame", null),
        //                  no zone change and no quest changes,
        //           When Infer is called,
        //           Then StepType="say-chat-message", SuggestedStepId="say-chat-message-broadcast",
        //                SuggestedExpect=null, Confidence=High,
        //                InferredFrom=SayChatMessageSent,
        //                Notes is non-null and contains "Expect" AND "Open Sesame".

        // TODO: SayChatMessageSentSignal not yet defined — compile error expected.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",                 result.StepType);
        Assert.Equal("say-chat-message-broadcast",       result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.High,                    result.Confidence);
        Assert.Equal(InferredFrom.SayChatMessageSent,    result.InferredFrom);
        Assert.NotNull(result.Notes);
        Assert.Contains("Expect",      result.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Open Sesame", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // SCI2 — NPC target: SayChatMessageSent with TargetBaseId → step id "-on-{tid}"
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_WithTarget_StepIdIncludesOnSuffix_SCI2()
    {
        // CONTRACT: Given SayChatMessageSent = SayChatMessageSentSignal("Hello friend", 1000789u),
        //           When Infer is called,
        //           Then SuggestedStepId="say-chat-message-on-1000789".

        // TODO: SayChatMessageSentSignal not yet defined.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Hello friend", TargetBaseId: 1000789u)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",                 result.StepType);
        Assert.Equal("say-chat-message-on-1000789",      result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(InferredFrom.SayChatMessageSent,    result.InferredFrom);
    }

    // =========================================================================
    // SCI3 — No target: step id is "say-chat-message-broadcast" (NO -on- suffix)
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_NoTarget_StepIdIsBroadcast_SCI3()
    {
        // CONTRACT: Given SayChatMessageSent = SayChatMessageSentSignal("Hello", null),
        //           When Infer is called,
        //           Then SuggestedStepId="say-chat-message-broadcast" — NO "-on-" suffix.
        //
        // Pins Decision SCI4 step-id pattern for no-target case.

        // TODO: SayChatMessageSentSignal not yet defined.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Hello", TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",             result.StepType);
        Assert.Equal("say-chat-message-broadcast",   result.SuggestedStepId);
        Assert.DoesNotContain("-on-",                result.SuggestedStepId, StringComparison.Ordinal);
        Assert.Equal(InferredFrom.SayChatMessageSent, result.InferredFrom);
    }

    // =========================================================================
    // SCI4 — Priority over Rule 3 (QuestSequence advanced): SayChatMessageSent wins
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_WinsOverQuestSequenceAdvance_SCI4()
    {
        // CONTRACT: Given SayChatMessageSent set AND QuestSequence advances (1→2),
        //           When Infer is called,
        //           Then StepType="say-chat-message" (NOT "talk").
        //
        // Rationale: say-step that advances the sequence is the most common authoring case;
        //            firing Rule 3 would draft a wrong "talk" step.

        // TODO: SayChatMessageSentSignal not yet defined.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questSequence: 1);
        var after  = before with
        {
            QuestSequence      = 2,
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: 1000789u)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",               result.StepType);
        Assert.Equal(InferredFrom.SayChatMessageSent,  result.InferredFrom);
        Assert.NotEqual("talk",                        result.StepType);
    }

    // =========================================================================
    // SCI5 — Priority over Rule 4 (Zone changed): SayChatMessageSent wins (defensive)
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_WinsOverZoneChange_SCI5()
    {
        // CONTRACT: Given SayChatMessageSent set AND zone changed (defensive — independent fields),
        //           When Infer is called,
        //           Then StepType="say-chat-message" (NOT "travel"),
        //                SuggestedStepId="say-chat-message-broadcast".

        // TODO: SayChatMessageSentSignal not yet defined.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132));
        var after  = before with
        {
            Zone               = new ZoneId(129),
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",               result.StepType);
        Assert.Equal("say-chat-message-broadcast",     result.SuggestedStepId);
        Assert.Equal(InferredFrom.SayChatMessageSent,  result.InferredFrom);
        Assert.NotEqual("travel",                      result.StepType);
    }

    // =========================================================================
    // SCI6 — Priority over EmoteCompleted AND ActionCompleted: say wins when all three set
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_WinsOverEmoteAndAction_WhenAllThreeSet_SCI6()
    {
        // CONTRACT: Given all three signals set (defensive — mutually exclusive in production),
        //           When Infer is called,
        //           Then StepType="say-chat-message" (NOT "use-emote", NOT "use-action").
        //
        // Pins Decision SCI14: chat > emote > action tie-break.
        // Rule 3.5s fires ABOVE Rule 3.5e (EmoteCompleted) and Rule 3.5 (ActionCompleted).

        // TODO: SayChatMessageSentSignal not yet defined.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            ActionCompleted    = new ActionCompletedSignal(
                QuestForge.Schema.ActionType.Action, ActionId: 31u, TargetBaseId: null),
            EmoteCompleted     = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null),
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("say-chat-message",               result.StepType);
        Assert.Equal("say-chat-message-broadcast",     result.SuggestedStepId);
        Assert.Equal(InferredFrom.SayChatMessageSent,  result.InferredFrom);
        Assert.NotEqual("use-emote",                   result.StepType);
        Assert.NotEqual("use-action",                  result.StepType);
    }

    // =========================================================================
    // SCI7 — Priority BELOW Rule 1 (QuestCompleted): turn-in wins over SayChatMessageSent
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_LosesToQuestCompleted_Rule1Wins_SCI7()
    {
        // CONTRACT: Given QuestCompleted=true AND SayChatMessageSent set in the same window,
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1), NOT "say-chat-message".

        // TODO: SayChatMessageSentSignal not yet defined.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = before with
        {
            QuestCompleted     = true,
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in",                        result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted,       result.InferredFrom);
        Assert.NotEqual("say-chat-message",             result.StepType);
    }

    // =========================================================================
    // SCI8 — Priority BELOW Rule 2.3 (KeyItemsAdded): pickup-item wins over SayChatMessageSent
    // =========================================================================

    [Fact]
    public void SayChatMessageInference_LosesToKeyItemsAdded_Rule23Wins_SCI8()
    {
        // CONTRACT: Given KeyItemsAdded=[2001u] AND SayChatMessageSent set,
        //           When Infer is called,
        //           Then StepType="pickup-item" (Rule 2.3), NOT "say-chat-message".
        //
        // Pins placement: Rule 3.5s is above Rule 3.5e but below the Rule-2.x family.

        // TODO: SayChatMessageSentSignal not yet defined.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = before with
        {
            KeyItemsAdded      = [2001u],
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: null)
        };

        var result = engine.Infer(before, after);

        Assert.Equal("pickup-item",                    result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
        Assert.NotEqual("say-chat-message",            result.StepType);
    }

    // =========================================================================
    // SCI9 — Aggregator: OnSayChatMessageSent, ResetDeltas, OnSayChatMessageConsumed
    // =========================================================================

    [Fact]
    public void Aggregator_OnSayChatMessageSent_SetsField_ResetDeltasSurvives_ConsumedClears_SCI9()
    {
        // CONTRACT (sub-A): When OnSayChatMessageSent("Open Sesame", 1000789u) is called,
        //   Then agg.Current.SayChatMessageSent is non-null with correct Message + TargetBaseId.
        //
        // CONTRACT (sub-B): When ResetDeltas() is called,
        //   Then agg.Current.SayChatMessageSent is STILL non-null (survives per-window lifecycle).
        //
        // CONTRACT (sub-C): When OnSayChatMessageConsumed() is called,
        //   Then agg.Current.SayChatMessageSent is null.
        //   AND no side effects on LastNpcInteracted, LastAttuned, LastAethernetShardInteracted,
        //       EmoteCompleted, ActionCompleted (pins Decision SCI3).
        //
        // CONTRACT (sub-D): last-write-wins on repeated calls.

        // TODO: OnSayChatMessageSent / OnSayChatMessageConsumed not yet implemented.
        var clock = new FakeClock(T0);
        var agg   = new SnapshotAggregator(activeQuest: null, clock: clock);

        // ── sub-A: OnSayChatMessageSent sets the field ──────────────────────
        agg.OnSayChatMessageSent(message: "Open Sesame", targetBaseId: 1000789u);
        var snapA = agg.Current;

        Assert.NotNull(snapA.SayChatMessageSent);
        Assert.Equal("Open Sesame", snapA.SayChatMessageSent!.Message);
        Assert.Equal(1000789u,      snapA.SayChatMessageSent.TargetBaseId);

        // ── sub-B: ResetDeltas does NOT clear SayChatMessageSent ────────────
        agg.ResetDeltas();
        var snapB = agg.Current;

        Assert.NotNull(snapB.SayChatMessageSent);
        Assert.Equal("Open Sesame", snapB.SayChatMessageSent!.Message);

        // ── sub-C: OnSayChatMessageConsumed clears and has no side effects ──
        agg.OnSayChatMessageConsumed();
        var snapC = agg.Current;

        Assert.Null(snapC.SayChatMessageSent);
        // Side-effect guards (Decision SCI3)
        Assert.Null(snapC.LastNpcInteracted);
        Assert.Null(snapC.LastAttuned);
        Assert.Null(snapC.LastAethernetShardInteracted);
        Assert.Null(snapC.EmoteCompleted);
        Assert.Null(snapC.ActionCompleted);

        // ── sub-D: last-write-wins ───────────────────────────────────────────
        agg.OnSayChatMessageSent("first message", null);
        agg.OnSayChatMessageSent("second message", 555u);
        var snapD = agg.Current;

        Assert.Equal("second message", snapD.SayChatMessageSent!.Message);
        Assert.Equal(555u,             snapD.SayChatMessageSent.TargetBaseId);
    }

    // =========================================================================
    // SCI10 — StepFactory "say-chat-message" arm: builds SayChatMessageStep from snapshot
    // =========================================================================

    [Fact]
    public void StepFactory_SayChatMessage_ProducesSayChatMessageStepWithSnapshotFields_SCI10()
    {
        // CONTRACT: Given after.SayChatMessageSent = SayChatMessageSentSignal("Open Sesame", 1000789u),
        //           When Build("say-chat-message", "say-chat-message-on-1000789", null, after) is called,
        //           Then:
        //             - step is SayChatMessageStep
        //             - sc.Id == "say-chat-message-on-1000789"
        //             - sc.Message == "Open Sesame"
        //             - sc.TargetNpcId == 1000789u
        //             - sc.Expect is null

        // TODO: SayChatMessageSentSignal not yet defined; StepFactory "say-chat-message" arm not yet added.
        var after = MakeSnapshot() with
        {
            SayChatMessageSent = new SayChatMessageSentSignal(Message: "Open Sesame", TargetBaseId: 1000789u)
        };

        var step = StepFactory.Build("say-chat-message", "say-chat-message-on-1000789", null, after);

        var sc = Assert.IsType<QuestForge.Schema.SayChatMessageStep>(step);
        Assert.Equal("say-chat-message-on-1000789", sc.Id);
        Assert.Equal("Open Sesame",                 sc.Message);
        Assert.Equal(1000789u,                      sc.TargetNpcId);
        Assert.Null(sc.Expect);
    }

    // =========================================================================
    // SCI11 — StepFactory defensive: null SayChatMessageSent → Message="", no throw
    // =========================================================================

    [Fact]
    public void StepFactory_SayChatMessage_NullSayChatMessageSent_FallsBackToEmpty_SCI11()
    {
        // CONTRACT: Given after.SayChatMessageSent is null (defensive caller),
        //           When Build("say-chat-message", "say-chat-message-X", null, after) is called,
        //           Then:
        //             - step is SayChatMessageStep (no exception)
        //             - sc.Message == "" (fallback; validator E11 catches)
        //             - sc.TargetNpcId is null
        //
        // Pins Decision SCI6: StepFactory must not throw when signal is absent.

        // TODO: StepFactory "say-chat-message" arm not yet added.
        var after = MakeSnapshot(); // SayChatMessageSent is null

        var ex = Record.Exception(() =>
        {
            var step = StepFactory.Build("say-chat-message", "say-chat-message-X", null, after);
            var sc   = Assert.IsType<QuestForge.Schema.SayChatMessageStep>(step);
            Assert.Equal(string.Empty, sc.Message);
            Assert.Null(sc.TargetNpcId);
        });

        Assert.Null(ex);
    }
}
