using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported here because Schema.AetheryteId and
// Adapters.Types.AetheryteId would collide. Schema step types (TeleportStep, etc.)
// are referenced with full qualification in assertions below.
// Adapters.Types.AetheryteId is the canonical type used by GameStateSnapshot/SnapshotAggregator.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for TeleportStep authoring inference â€” covers four areas:
///   T1â€“T6:  StepInferenceEngine Rule 4.0 â€” TeleportCompleted
///   T7â€“T10: SnapshotAggregator.OnTeleportCompleted / OnTeleportConsumed
///   T11â€“T12: StepFactory.Build "teleport" arm
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - GameStateSnapshot.TeleportCompleted property                      (Task IT-1)
///   - InferredFrom.TeleportCompleted enum value                         (Task IT-3)
///   - SnapshotAggregator.OnTeleportCompleted / OnTeleportConsumed       (Task IT-2)
///   - StepInferenceEngine Rule 4.0 (TeleportCompleted check)            (Task IT-4)
///   - StepFactory.Build "teleport" arm                                   (Task IT-5)
/// </summary>
public sealed class TeleportInferenceTests
{
    private static readonly QuestId ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Snapshot builder helper
    // =========================================================================

    /// <summary>
    /// Builds a GameStateSnapshot with all teleport-inference-relevant fields settable.
    /// Mirrors the pattern established in AethernetInferenceTests.MakeSnapshot.
    /// </summary>
    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        bool questAccepted = false,
        bool questCompleted = false,
        int questSequence = 0,
        NpcId? lastNpcInteracted = null,
        AetheryteId? teleportCompleted = null,
        AethernetHop? aethernetTeleportCompleted = null,
        int? dialogueOptionSelected = null,
        QuestForge.Schema.NpcLocation? dialogueNpcSource = null,
        DateTimeOffset? capturedAt = null) =>
        new(
            CapturedAt: capturedAt ?? T0,
            Zone: zone ?? new ZoneId(131),
            Position: position ?? new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: questSequence,
            QuestFlags: 0,
            QuestAccepted: questAccepted,
            QuestCompleted: questCompleted,
            LastNpcInteracted: lastNpcInteracted,
            LastNpcPosition: null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null)
        {
            TeleportCompleted = teleportCompleted,
            AethernetTeleportCompleted = aethernetTeleportCompleted,
            DialogueOptionSelected = dialogueOptionSelected,
            DialogueNpcSource = dialogueNpcSource
        };

    private static string FormatResult(InferenceResult r) =>
        $"StepType={r.StepType} SuggestedStepId={r.SuggestedStepId} InferredFrom={r.InferredFrom} Confidence={r.Confidence}";

    // =========================================================================
    // T1 â€” Happy path: zone change + TeleportCompleted set â†’ infers `teleport` step
    // =========================================================================

    [Fact]
    public void TeleportInference_HappyPath_InfersTeleportStep_T1()
    {
        //   - GameStateSnapshot.TeleportCompleted property (Task IT-1)
        //   - InferredFrom.TeleportCompleted enum value (Task IT-3)
        //   - StepInferenceEngine Rule 4.0 (Task IT-4)
        //
        // CONTRACT: Given before=Old Gridania zone(132), after=Limsa zone(129) with
        //           TeleportCompleted=AetheryteId(8),
        //           When Infer is called,
        //           Then StepType="teleport", SuggestedStepId="teleport-to-8",
        //                SuggestedExpect="playerZone() == 129", Confidence=High,
        //                InferredFrom=TeleportCompleted, Notes=null.
        //
        // BUILDER GUIDANCE: Insert Rule 4.0 immediately before the `if (after.Zone != before.Zone)` block
        //   in StepInferenceEngine.cs (line ~287). Check after.TeleportCompleted is { } teleId && zone changed.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132), teleportCompleted: null);
        var after  = MakeSnapshot(zone: new ZoneId(129), teleportCompleted: new AetheryteId(8));

        var result = engine.Infer(before, after);

        Assert.Equal("teleport", result.StepType);
        Assert.Equal("teleport-to-8", result.SuggestedStepId);
        Assert.Equal("playerZone() == 129", result.SuggestedExpect);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.TeleportCompleted, result.InferredFrom);
        Assert.Null(result.Notes);
    }

    // =========================================================================
    // T2 â€” Priority over Aethernet: both TeleportCompleted and AethernetTeleportCompleted set
    // =========================================================================

    [Fact]
    public void TeleportInference_WinOverAethernet_WhenBothSet_T2()
    {
        //
        // CONTRACT: Given zone changes AND both TeleportCompleted=AetheryteId(8) and
        //           AethernetTeleportCompleted=AethernetHop(null, AethernetId(99)) are set
        //           (defensive â€” should not happen in production, but fields are independent),
        //           When Infer is called,
        //           Then StepType="teleport" (NOT "travel"), InferredFrom=TeleportCompleted.
        //
        // WHY teleport wins: TeleportCompleted is more specific (main aetheryte + known dest zone).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132));
        var after  = MakeSnapshot(
            zone: new ZoneId(129),
            teleportCompleted: new AetheryteId(8),
            aethernetTeleportCompleted: new AethernetHop(null, new AethernetId(99)));

        var result = engine.Infer(before, after);

        Assert.Equal("teleport", result.StepType);
        Assert.Equal("teleport-to-8", result.SuggestedStepId);
        Assert.Equal(InferredFrom.TeleportCompleted, result.InferredFrom);
    }

    // =========================================================================
    // T3 â€” Priority over NPC dialog: teleport wins even when dialogue signals present
    // =========================================================================

    [Fact]
    public void TeleportInference_WinOverNpcDialogue_WhenBothSet_T3()
    {
        //
        // CONTRACT: Given zone changes + TeleportCompleted set + DialogueOptionSelected + DialogueNpcSource,
        //           When Infer is called,
        //           Then StepType="teleport" (NOT "travel" with npc-dialogue-hint).
        //
        // WHY: TeleportCompleted is the definitive cross-region signal; dialogue noise must not override it.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132), lastNpcInteracted: new NpcId(1234567));
        var after  = MakeSnapshot(
            zone: new ZoneId(129),
            teleportCompleted: new AetheryteId(8),
            dialogueOptionSelected: 0,
            dialogueNpcSource: new QuestForge.Schema.NpcLocation(
                NpcId: 1234567,
                Zone: 132,
                Position: new QuestForge.Schema.Position3(0, 0, 0)));

        var result = engine.Infer(before, after);

        Assert.Equal("teleport", result.StepType);
        Assert.Equal(InferredFrom.TeleportCompleted, result.InferredFrom);
    }

    // =========================================================================
    // T4 â€” No-op: zone change without TeleportCompleted â†’ falls through to Rule 4
    // =========================================================================

    [Fact]
    public void TeleportInference_ZoneChangeWithoutTeleportCompleted_FallsToRule4_T4()
    {
        //      This test also pins that Rule 4.0 does NOT fire when TeleportCompleted is null.
        //
        // CONTRACT: Given zone changes but TeleportCompleted=null (regular cross-zone walk),
        //           When Infer is called,
        //           Then StepType="travel" (Rule 4 catch-all), SuggestedStepId="travel-to-zone-129",
        //                InferredFrom=ZoneChange.
        //
        // BUILDER GUIDANCE: Rule 4.0 guard: `if (after.TeleportCompleted is { } â€¦)`
        //   â€” null means fall through to existing Rule 4.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132));
        var after  = MakeSnapshot(zone: new ZoneId(129), teleportCompleted: null);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal("travel-to-zone-129", result.SuggestedStepId);
        Assert.Equal(InferredFrom.ZoneChange, result.InferredFrom);
    }

    // =========================================================================
    // T5 â€” Defensive: same-zone TeleportCompleted set â†’ falls through (zone-change guard)
    // =========================================================================

    [Fact]
    public void TeleportInference_SameZone_TeleportCompletedSet_FallsThrough_T5()
    {
        //
        // CONTRACT: Given zone unchanged (132â†’132) AND TeleportCompleted=AetheryteId(8)
        //           (stale signal: player teleported to where they already were, or phantom close),
        //           When Infer is called,
        //           Then result == InferenceResult.Empty (Rule 4.0's zone-change guard rejects).
        //
        // BUILDER GUIDANCE: Rule 4.0 condition: `after.TeleportCompleted is { } teleId && after.Zone != before.Zone`.
        //   Same zone â†’ condition false â†’ fall through to Rule 9 (Empty).

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132), position: new WorldPosition(0, 0, 0));
        var after  = MakeSnapshot(
            zone: new ZoneId(132),
            position: new WorldPosition(0, 0, 0),
            teleportCompleted: new AetheryteId(8));

        var result = engine.Infer(before, after);

        Assert.Equal("", result.StepType); // InferenceResult.Empty
    }

    // =========================================================================
    // T6 â€” Priority pinning: Rule 1 (QuestCompleted) beats Rule 4.0
    // =========================================================================

    [Fact]
    public void TeleportInference_QuestCompletedBeats_TeleportCompleted_T6()
    {
        //
        // CONTRACT: Given zone changes + QuestCompleted=true + TeleportCompleted=AetheryteId(8)
        //           (player teleported back and turned in the quest â€” both in same window),
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1), InferredFrom=QuestCompleted.
        //
        // WHY: Rules 1â€“3 (QuestCompleted/QuestAccepted/ForeignQuestAccepted) always take
        //      precedence over travel signals. Rule 4.0 sits below Rule 3 in the cascade.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(132), questCompleted: false);
        var after  = MakeSnapshot(
            zone: new ZoneId(129),
            questCompleted: true,
            teleportCompleted: new AetheryteId(8));

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }

    // =========================================================================
    // T7 â€” Aggregator: OnTeleportCompleted sets TeleportCompleted field
    // =========================================================================

    [Fact]
    public void Aggregator_OnTeleportCompleted_SetsField_T7()
    {
        //   - SnapshotAggregator.OnTeleportCompleted method (Task IT-2)
        //   - GameStateSnapshot.TeleportCompleted property (Task IT-1)
        //
        // CONTRACT: Given a fresh SnapshotAggregator,
        //           When OnTeleportCompleted(AetheryteId(8)) is called,
        //           Then agg.Current.TeleportCompleted == AetheryteId(8).

        var agg = new SnapshotAggregator(activeQuest: null);

        agg.OnTeleportCompleted(new AetheryteId(8));

        var snap = agg.Current;

        Assert.NotNull(snap.TeleportCompleted);
        Assert.Equal(new AetheryteId(8), snap.TeleportCompleted!.Value);
    }

    // =========================================================================
    // T8 â€” Aggregator: OnTeleportConsumed clears the field
    // =========================================================================

    [Fact]
    public void Aggregator_OnTeleportConsumed_ClearsField_T8()
    {
        //
        // CONTRACT: Given OnTeleportCompleted(AetheryteId(8)) has been called,
        //           When OnTeleportConsumed() is called,
        //           Then agg.Current.TeleportCompleted is null.

        var agg = new SnapshotAggregator(activeQuest: null);
        agg.OnTeleportCompleted(new AetheryteId(8));

        agg.OnTeleportConsumed();

        Assert.Null(agg.Current.TeleportCompleted);
    }

    // =========================================================================
    // T9 â€” Aggregator: OnTeleportCompleted does NOT update LastAttuned or LastAethernetShardInteracted
    // =========================================================================

    [Fact]
    public void Aggregator_OnTeleportCompleted_DoesNotSetLastAttuned_T9()
    {
        //
        // CONTRACT: Given a fresh SnapshotAggregator (LastAttuned=null, LastAethernetShardInteracted=null),
        //           When OnTeleportCompleted(AetheryteId(8)) is called,
        //           Then:
        //             - TeleportCompleted == AetheryteId(8)
        //             - LastAttuned is null  (pins Decision I11 â€” teleport is not an attunement)
        //             - LastAethernetShardInteracted is null  (pins Decision I2)
        //
        // BUILDER GUIDANCE: OnTeleportCompleted must ONLY set _teleportCompleted.
        //   Do NOT call _lastAttuned = ... or _lastAethernetShardInteracted = ...

        var agg = new SnapshotAggregator(activeQuest: null);

        agg.OnTeleportCompleted(new AetheryteId(8));

        Assert.NotNull(agg.Current.TeleportCompleted);
        Assert.Null(agg.Current.LastAttuned);
        Assert.Null(agg.Current.LastAethernetShardInteracted);
    }

    // =========================================================================
    // T10 â€” Aggregator: ResetDeltas does NOT clear TeleportCompleted
    // =========================================================================

    [Fact]
    public void Aggregator_ResetDeltas_DoesNotClearTeleportCompleted_T10()
    {
        //
        // CONTRACT: Given OnTeleportCompleted(AetheryteId(8)) was called,
        //           When ResetDeltas() is called,
        //           Then agg.Current.TeleportCompleted still == AetheryteId(8).
        //
        // WHY: TeleportCompleted survives per-window resets (cleared only by OnTeleportConsumed)
        //      so PreviewInference can still see it when the author opens the Record-Step modal.
        //      Mirrors AethernetTeleportCompleted lifecycle.
        //
        // BUILDER GUIDANCE: Do NOT add `_teleportCompleted = null;` inside ResetDeltas().

        var agg = new SnapshotAggregator(activeQuest: null);
        agg.OnTeleportCompleted(new AetheryteId(8));

        agg.ResetDeltas();

        Assert.NotNull(agg.Current.TeleportCompleted);
        Assert.Equal(new AetheryteId(8), agg.Current.TeleportCompleted!.Value);
    }

    // =========================================================================
    // T11 â€” StepFactory: "teleport" arm produces TeleportStep with correct fields
    // =========================================================================

    [Fact]
    public void StepFactory_Teleport_ProducesTeleportStep_T11()
    {
        //      Also depends on TeleportCompleted property (Task IT-1).
        //
        // CONTRACT: Given before=zone(132), after=zone(129) with TeleportCompleted=AetheryteId(8),
        //           When Build("teleport", "teleport-to-8", "playerZone() == 129", after, before) is called,
        //           Then:
        //             - step is TeleportStep
        //             - Id == "teleport-to-8"
        //             - AetheryteId.Value == 8u
        //             - Expect.Predicate == "playerZone() == 129"
        //             - Zone == "129" (destination)
        //             - RequiredZone == "132" (source zone from before)
        //
        // BUILDER GUIDANCE: Add `"teleport" => new TeleportStep { â€¦ }` arm to StepFactory.Build's
        //   switch. Read after.TeleportCompleted?.Value for the AetheryteId. RequiredZone = before.Zone.
        //   Zone = after.Zone.

        var before = MakeSnapshot(zone: new ZoneId(132), position: new WorldPosition(5, 0, 10));
        var after  = MakeSnapshot(zone: new ZoneId(129), teleportCompleted: new AetheryteId(8));

        var step = StepFactory.Build("teleport", "teleport-to-8", "playerZone() == 129", after, before);

        var ts = Assert.IsType<QuestForge.Schema.TeleportStep>(step);
        Assert.Equal("teleport-to-8", ts.Id);
        Assert.Equal(8u, ts.AetheryteId.Value);
        var predicateExpect = Assert.IsType<QuestForge.Schema.PredicateExpect>(ts.Expect);
        Assert.Equal("playerZone() == 129", predicateExpect.Predicate);
        Assert.Equal("129", ts.Zone);
        Assert.Equal("132", ts.RequiredZone);
    }

    // =========================================================================
    // T12 â€” StepFactory: "teleport" arm defensive â€” TeleportCompleted null â†’ AetheryteId(0)
    // =========================================================================

    [Fact]
    public void StepFactory_Teleport_NullTeleportCompleted_FallsBackToZeroId_T12()
    {
        //      Also depends on TeleportCompleted property (Task IT-1).
        //
        // CONTRACT: Given after=zone(129) with TeleportCompleted=null,
        //           When Build("teleport", "teleport-to-X", null, after) is called,
        //           Then:
        //             - step is TeleportStep
        //             - AetheryteId.Value == 0u (defensive fallback; validator catches this later)
        //             - No exception thrown.
        //
        // WHY: Inference engine only returns "teleport" when TeleportCompleted is set,
        //      but defensive fallback prevents a crash if StepFactory is called directly with null.
        //
        // BUILDER GUIDANCE: `AetheryteId = new QuestForge.Schema.AetheryteId(after?.TeleportCompleted?.Value ?? 0u)`

        var after = MakeSnapshot(zone: new ZoneId(129), teleportCompleted: null);

        var ex = Record.Exception(() =>
        {
            var step = StepFactory.Build("teleport", "teleport-to-X", null, after);
            var ts = Assert.IsType<QuestForge.Schema.TeleportStep>(step);
            Assert.Equal(0u, ts.AetheryteId.Value);
        });

        Assert.Null(ex);
    }
}
