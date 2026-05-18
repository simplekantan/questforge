using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported here because Schema.AetheryteId and
// Adapters.Types.AetheryteId would collide. Schema step types (TravelStep, TalkStep, etc.)
// are referenced with full qualification in assertions below.
// Adapters.Types.AetheryteId is the canonical type used by GameStateSnapshot/SnapshotAggregator.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for aethernet travel auto-detection across four areas:
///   Group 1 - GameStateSnapshot.LastAethernetShardInteracted (new non-positional property)
///   Group 2 - SnapshotAggregator.OnAethernetShardTargeted (new method)
///   Group 3 - StepInferenceEngine Rule 4 aethernet sub-case
///   Group 4 - StepFactory.Build with new 'before' parameter
///
/// ALL tests in this file will fail to compile until Builder adds:
///   - GameStateSnapshot.LastAethernetShardInteracted property
///   - SnapshotAggregator.OnAethernetShardTargeted method
///   - StepFactory.Build overload accepting a 'before' GameStateSnapshot parameter
/// </summary>
public sealed class AethernetInferenceTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    // =========================================================================
    // Snapshot builder helpers
    // =========================================================================

    /// <summary>
    /// Builds a GameStateSnapshot with all aethernet-relevant fields settable.
    /// Extends the pattern established in KeyItemInferenceTests.MakeSnapshot.
    /// </summary>
    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        bool questAccepted = false,
        bool questCompleted = false,
        int questSequence = 0,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        AetheryteId? lastAttuned = null,
        // RED: LastAethernetShardInteracted does not exist yet — compile error expected
        AetheryteId? lastAethernetShardInteracted = null,
        IReadOnlyList<uint>? keyItemsAdded = null,
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
            LastNpcPosition: lastNpcPosition,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: lastAttuned)
        {
            KeyItemsAdded = keyItemsAdded,
            // RED: This property does not exist yet — compile error expected
            LastAethernetShardInteracted = lastAethernetShardInteracted
        };

    // =========================================================================
    // Group 1: GameStateSnapshot.LastAethernetShardInteracted
    // =========================================================================

    [Fact]
    public void GSS1_LastAethernetShardInteracted_IsNullByDefault()
    {
        // RED: Will fail until Builder adds LastAethernetShardInteracted property.
        //
        // CONTRACT: Given a snapshot built with positional constructor only,
        //           When LastAethernetShardInteracted is read,
        //           Then it returns null.
        //
        // BUILDER GUIDANCE: Add a non-positional init property to GameStateSnapshot
        // following the same pattern as KeyItemsAdded — appended after the record body.

        var snapshot = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(131),
            Position: new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: false,
            QuestCompleted: false,
            LastNpcInteracted: null,
            LastNpcPosition: null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null);

        // RED: LastAethernetShardInteracted does not exist yet
        Assert.Null(snapshot.LastAethernetShardInteracted);
    }

    [Fact]
    public void GSS2_LastAethernetShardInteracted_CanBeSetViaObjectInitializer()
    {
        // RED: Will fail until Builder adds LastAethernetShardInteracted property.
        //
        // CONTRACT: Given a snapshot with LastAethernetShardInteracted = AetheryteId(33) via init,
        //           When the property is read,
        //           Then it returns AetheryteId(33).
        //
        // BUILDER GUIDANCE: Declare as: public AetheryteId? LastAethernetShardInteracted { get; init; }

        var snapshot = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(131),
            Position: new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: false,
            QuestCompleted: false,
            LastNpcInteracted: null,
            LastNpcPosition: null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null)
        {
            // RED: Property does not exist yet
            LastAethernetShardInteracted = new AetheryteId(33)
        };

        // RED: Property does not exist yet
        Assert.NotNull(snapshot.LastAethernetShardInteracted);
        Assert.Equal(new AetheryteId(33), snapshot.LastAethernetShardInteracted.Value);
    }

    [Fact]
    public void GSS3_WithExpression_ProducesNewValueOriginalUnchanged()
    {
        // RED: Will fail until Builder adds LastAethernetShardInteracted property.
        //
        // CONTRACT: Given an original snapshot with LastAethernetShardInteracted = null,
        //           When a new snapshot is created via 'with { LastAethernetShardInteracted = AetheryteId(42) }',
        //           Then the new snapshot has AetheryteId(42) and the original remains null.
        //
        // BUILDER GUIDANCE: Record properties support 'with' expressions automatically.

        var original = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(131),
            Position: new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: false,
            QuestCompleted: false,
            LastNpcInteracted: null,
            LastNpcPosition: null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null);

        // RED: Property does not exist yet
        var modified = original with { LastAethernetShardInteracted = new AetheryteId(42) };

        Assert.Null(original.LastAethernetShardInteracted);
        Assert.NotNull(modified.LastAethernetShardInteracted);
        Assert.Equal(new AetheryteId(42), modified.LastAethernetShardInteracted!.Value);
    }

    // =========================================================================
    // Group 2: SnapshotAggregator.OnAethernetShardTargeted
    // =========================================================================

    [Fact]
    public void SA1_FreshAggregator_LastAethernetShardInteracted_IsNull()
    {
        // RED: Will fail until Builder adds LastAethernetShardInteracted to snapshot.
        //
        // CONTRACT: Given a freshly constructed SnapshotAggregator,
        //           When Current is read,
        //           Then Current.LastAethernetShardInteracted is null.
        //
        // BUILDER GUIDANCE: Initialize the backing field to null; no change needed
        // beyond adding the property to GameStateSnapshot and wiring it in Current.

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // RED: Property does not exist yet
        Assert.Null(aggregator.Current.LastAethernetShardInteracted);
    }

    [Fact]
    public void SA2_OnAethernetShardTargeted_SetsLastAethernetShardInteracted()
    {
        // RED: Will fail until Builder adds OnAethernetShardTargeted method.
        //
        // CONTRACT: Given a SnapshotAggregator,
        //           When OnAethernetShardTargeted(AetheryteId(33)) is called,
        //           Then Current.LastAethernetShardInteracted == AetheryteId(33).
        //
        // BUILDER GUIDANCE: Add method: public void OnAethernetShardTargeted(AetheryteId shard)
        // that sets a backing field _lastAethernetShardInteracted = shard.

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // RED: Method does not exist yet
        aggregator.OnAethernetShardTargeted(new AetheryteId(33));

        Assert.NotNull(aggregator.Current.LastAethernetShardInteracted);
        Assert.Equal(new AetheryteId(33), aggregator.Current.LastAethernetShardInteracted!.Value);
    }

    [Fact]
    public void SA3_OnAethernetShardTargeted_LastWins()
    {
        // RED: Will fail until Builder adds OnAethernetShardTargeted method.
        //
        // CONTRACT: Given OnAethernetShardTargeted(125) then OnAethernetShardTargeted(33),
        //           When Current is read,
        //           Then Current.LastAethernetShardInteracted == AetheryteId(33).
        //
        // BUILDER GUIDANCE: Overwrite the backing field on each call (last-wins semantics,
        // matching LastNpcInteracted behavior).

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // RED: Method does not exist yet
        aggregator.OnAethernetShardTargeted(new AetheryteId(125));
        aggregator.OnAethernetShardTargeted(new AetheryteId(33));

        Assert.Equal(new AetheryteId(33), aggregator.Current.LastAethernetShardInteracted!.Value);
    }

    [Fact]
    public void SA4_ResetDeltas_DoesNotClearLastAethernetShardInteracted()
    {
        // RED: Will fail until Builder adds OnAethernetShardTargeted method.
        //
        // CONTRACT: Given LastAethernetShardInteracted set to AetheryteId(125),
        //           When ResetDeltas() is called,
        //           Then LastAethernetShardInteracted remains AetheryteId(125).
        //
        // BUILDER GUIDANCE: ResetDeltas only clears per-action delta signals (KeyItemsAdded,
        // KeyItemsRemoved). Do NOT clear _lastAethernetShardInteracted in ResetDeltas.
        // The shard targeted persists across the before/after window.

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        // RED: Method does not exist yet
        aggregator.OnAethernetShardTargeted(new AetheryteId(125));

        aggregator.ResetDeltas();

        Assert.Equal(new AetheryteId(125), aggregator.Current.LastAethernetShardInteracted!.Value);
    }

    [Fact]
    public void SA5_OnInteraction_DoesNotClearLastAethernetShardInteracted()
    {
        // RED: Will fail until Builder adds OnAethernetShardTargeted method.
        //
        // CONTRACT: Given LastAethernetShardInteracted set to AetheryteId(125),
        //           When OnInteraction(NpcId(7), ...) is called,
        //           Then LastAethernetShardInteracted remains AetheryteId(125).
        //
        // BUILDER GUIDANCE: OnInteraction only updates _lastNpcInteracted and _lastNpcPosition.
        // It must not touch _lastAethernetShardInteracted.

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        // RED: Method does not exist yet
        aggregator.OnAethernetShardTargeted(new AetheryteId(125));

        aggregator.OnInteraction(new NpcId(7), new WorldPosition(1, 0, 2));

        Assert.Equal(new AetheryteId(125), aggregator.Current.LastAethernetShardInteracted!.Value);
    }

    [Fact]
    public void SA6_CurrentIsIdempotent_AfterOnAethernetShardTargeted()
    {
        // RED: Will fail until Builder adds OnAethernetShardTargeted method.
        //
        // CONTRACT: Given OnAethernetShardTargeted has been called,
        //           When Current is read twice,
        //           Then both reads return the same LastAethernetShardInteracted value.
        //
        // BUILDER GUIDANCE: Current is a computed property — each read produces a new
        // snapshot record from the current backing fields. Two reads must be consistent.

        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        // RED: Method does not exist yet
        aggregator.OnAethernetShardTargeted(new AetheryteId(125));

        var first = aggregator.Current.LastAethernetShardInteracted;
        var second = aggregator.Current.LastAethernetShardInteracted;

        Assert.Equal(first, second);
        Assert.Equal(new AetheryteId(125), first!.Value);
    }

    // =========================================================================
    // Group 3: StepInferenceEngine Rule 4 — aethernet sub-case
    // =========================================================================

    [Fact]
    public void IE1_AethernetHappyPath_ZoneChange_SourceDestinationBothCaptured()
    {
        // RED: Will fail until Builder implements the aethernet sub-case in Rule 4.
        //
        // CONTRACT: Given zone 131->130, before.LastAethernetShardInteracted=125,
        //           before.LastNpcInteracted=NpcId(125) (matches shard), after.LastAethernetShardInteracted=33,
        //           When Infer is called,
        //           Then StepType="travel", SuggestedStepId contains "aethernet",
        //                Confidence=High, Notes contains "125" and "33".
        //
        // BUILDER GUIDANCE: In Rule 4, before returning generic travel, check:
        //   before.LastAethernetShardInteracted.HasValue
        //   && before.LastNpcInteracted.HasValue
        //   && before.LastNpcInteracted.Value.Value == before.LastAethernetShardInteracted.Value.Value
        // If true, enter aethernet sub-case.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Contains("aethernet", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.NotNull(result.Notes);
        Assert.Contains("125", result.Notes, StringComparison.Ordinal);
        Assert.Contains("33", result.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void IE2_AethernetDestinationEqualsSource_MediumConfidence()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given zone changes, shard conditions met, but after.LastAethernetShardInteracted
        //           equals before.LastAethernetShardInteracted (destination not distinguishable from source),
        //           When Infer is called,
        //           Then Confidence=Medium, Notes mentions "destination shard".
        //
        // BUILDER GUIDANCE: When after.LastAethernetShardInteracted == before.LastAethernetShardInteracted,
        // the engine cannot distinguish source from destination. Emit Medium confidence.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastAethernetShardInteracted: new AetheryteId(125),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.NotNull(result.Notes);
        Assert.Contains("destination shard", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IE3_AethernetDestinationNull_MediumConfidence()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given zone changes, shard conditions met, but after.LastAethernetShardInteracted=null,
        //           When Infer is called,
        //           Then Confidence=Medium, Notes mentions "destination shard".
        //
        // BUILDER GUIDANCE: When after.LastAethernetShardInteracted is null, we know an aethernet
        // travel occurred but cannot capture the destination shard. Emit Medium confidence.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastAethernetShardInteracted: null,
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.NotNull(result.Notes);
        Assert.Contains("destination shard", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IE4_StaleShard_LastNpcInteractedDiffers_RegularTravel()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given before.LastNpcInteracted=NpcId(7), before.LastAethernetShardInteracted=AetheryteId(125)
        //           (7 != 125 — stale shard, NPC overrode it),
        //           When zone changes and Infer is called,
        //           Then SuggestedStepId = "travel-to-zone-130", Confidence=High, Notes=null.
        //
        // BUILDER GUIDANCE: The aethernet sub-case only fires when LastNpcInteracted.Value.Value
        // exactly equals LastAethernetShardInteracted.Value.Value. If they differ, fall through
        // to the standard generic travel result.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(7));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal("travel-to-zone-130", result.SuggestedStepId);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void IE5_NoShardTargeted_RegularTravel()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given before.LastAethernetShardInteracted=null (no shard ever targeted),
        //           When zone changes and Infer is called,
        //           Then SuggestedStepId = "travel-to-zone-130" (regular travel, no aethernet hint).
        //
        // BUILDER GUIDANCE: The aethernet sub-case guard requires HasValue on LastAethernetShardInteracted.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: null,
            lastNpcInteracted: new NpcId(7));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal("travel-to-zone-130", result.SuggestedStepId);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void IE6_LastNpcInteractedNull_NoAethernetSubCase()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given before.LastAethernetShardInteracted=AetheryteId(125) but
        //           before.LastNpcInteracted=null,
        //           When zone changes and Infer is called,
        //           Then regular travel fires (SuggestedStepId = "travel-to-zone-130").
        //
        // BUILDER GUIDANCE: The aethernet sub-case requires both HasValue checks to pass.
        // Null LastNpcInteracted means we cannot confirm the player actually interacted
        // with that shard — treat as regular travel.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: null);
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal("travel-to-zone-130", result.SuggestedStepId);
    }

    [Fact]
    public void IE7_NoZoneChange_AethernetSubCaseNotTaken()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given zone unchanged (131 -> 131), shard conditions met,
        //           When Infer is called,
        //           Then the aethernet sub-case does not fire (zone change is required for Rule 4).
        //           Result is InferenceResult.Empty (no other rule fires either).
        //
        // BUILDER GUIDANCE: Rule 4 — including the aethernet sub-case — only fires when
        // after.Zone != before.Zone. The zone guard wraps the entire sub-case.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125));
        var after = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.NotEqual("travel", result.StepType);
        Assert.Equal("", result.StepType); // Falls through to InferenceResult.Empty
    }

    [Fact]
    public void IE8_Priority_QuestCompleted_BeatsAethernetSubCase()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given zone change + shard conditions met + after.QuestCompleted=true,
        //           When Infer is called,
        //           Then StepType="turn-in" (Rule 1 wins over Rule 4).
        //
        // BUILDER GUIDANCE: The aethernet sub-case lives within Rule 4. Rules 1-3 always
        // evaluate first. No change to rule ordering required.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            questCompleted: false,
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            questCompleted: true,
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }

    [Fact]
    public void IE9_Priority_QuestAccepted_BeatsAethernetSubCase()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given zone change + shard conditions met + after.QuestAccepted=true,
        //           When Infer is called,
        //           Then StepType="accept" (Rule 2 wins over Rule 4).
        //
        // BUILDER GUIDANCE: Rule 2 evaluates before Rule 4. No change needed.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            questAccepted: false,
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            questAccepted: true,
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("accept", result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
    }

    [Fact]
    public void IE10_Priority_QuestSequenceAdvanced_BeatsAethernetSubCase()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given zone change + shard conditions met + after.QuestSequence > before.QuestSequence,
        //           When Infer is called,
        //           Then StepType="talk" (Rule 3 wins over Rule 4).
        //
        // BUILDER GUIDANCE: Rule 3 evaluates before Rule 4. No change needed.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            questSequence: 10,
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            questSequence: 20,
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("talk", result.StepType);
        Assert.Equal(InferredFrom.QuestSequenceChange, result.InferredFrom);
    }

    [Fact]
    public void IE11_AethernetSubCaseFiresBeforeGenericRule4()
    {
        // RED: Will fail until Builder implements the aethernet sub-case.
        //
        // CONTRACT: Given zone change + shard conditions fully met,
        //           When Infer is called,
        //           Then SuggestedStepId starts with "aethernet-" not "travel-".
        //
        // BUILDER GUIDANCE: The aethernet sub-case must be checked before emitting the
        // generic "travel-to-zone-{N}" result. Place the sub-case guard at the top of
        // Rule 4's branch, before constructing the generic result.

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.StartsWith("aethernet-", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("travel-", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Group 4: StepFactory.Build with 'before' parameter
    // =========================================================================

    [Fact]
    public void SF1_AethernetDestinationCaptured_BuildsTravelStepWithRouteHint()
    {
        // RED: Will fail until Builder adds 'before' parameter to StepFactory.Build.
        //
        // CONTRACT: Given before with shard 125 (LastNpcInteracted=125, LastNpcPosition=(10,0,20)),
        //           after with shard 33, zone 130, player at (100,5,-50),
        //           When Build("travel", id, expect, after, before: before) is called,
        //           Then TravelStep.Destination.Zone=130, Destination.Position=(10,0,20),
        //                RouteHint.Aethernet SequenceEqual [33u].
        //
        // BUILDER GUIDANCE: When before has matching shard+NPC, use LastNpcPosition as the
        // Destination.Position (the source shard's world position) so the engine can navigate
        // to the shard before initiating the aethernet travel.
        // RouteHint.Aethernet = [after.LastAethernetShardInteracted.Value.Value].

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        // RED: 'before' parameter does not exist yet
        var step = StepFactory.Build("travel", "aethernet-125-33", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal(130, travel.Destination.Zone);
        Assert.NotNull(travel.Destination.Position);
        Assert.Equal(10f, travel.Destination.Position!.X, precision: 3);
        Assert.Equal(0f,  travel.Destination.Position!.Y, precision: 3);
        Assert.Equal(20f, travel.Destination.Position!.Z, precision: 3);
        Assert.NotNull(travel.RouteHint);
        Assert.NotNull(travel.RouteHint!.Aethernet);
        // Issue #25: Aethernet is now AethernetRouteHint, not uint[]
        Assert.Equal(33u, travel.RouteHint!.Aethernet!.To);
        Assert.Equal(125u, travel.RouteHint!.Aethernet!.From);
    }

    [Fact]
    public void SF2_AethernetDestinationEqualsSource_UsesBeforePosition()
    {
        // CONTRACT: Given before with shard 125, after with shard also 125 (same = not confirmed),
        //           When Build is called with both snapshots,
        //           Then Destination.Position = before.Position (author's standing position), RouteHint=null.
        //
        // WHY: Same shard before and after is indistinguishable from a zone-gate walk while
        // standing near a shard. Using before.Position is safe for both cases — it puts the
        // player near the shard for genuine aethernet steps, and at the gate for gate walks.

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            position: new WorldPosition(8, 0, 18),   // author standing near shard
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            lastAethernetShardInteracted: new AetheryteId(125),
            capturedAt: T1);

        var step = StepFactory.Build("travel", "aethernet-125", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.Destination.Position);
        Assert.Equal(8f,  travel.Destination.Position!.X, precision: 3);
        Assert.Equal(0f,  travel.Destination.Position!.Y, precision: 3);
        Assert.Equal(18f, travel.Destination.Position!.Z, precision: 3);
        Assert.Null(travel.RouteHint);
    }

    [Fact]
    public void SF3_AethernetDestinationNull_UsesBeforePosition()
    {
        // CONTRACT: Given before with shard 125 (conditions met), after.LastAethernetShardInteracted=null,
        //           When Build is called,
        //           Then Destination.Position = before.Position (author's standing position), RouteHint=null.
        //
        // WHY: Null destination shard is indistinguishable from a zone-gate walk (player crossed
        // before targeting any aetheryte on the other side). Using before.Position is safe —
        // the author was near the shard, so the player will arrive close enough to interact.

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            position: new WorldPosition(8, 0, 18),   // author standing near shard
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            lastAethernetShardInteracted: null,
            capturedAt: T1);

        var step = StepFactory.Build("travel", "travel-130", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.Destination.Position);
        Assert.Equal(8f,  travel.Destination.Position!.X, precision: 3);
        Assert.Equal(0f,  travel.Destination.Position!.Y, precision: 3);
        Assert.Equal(18f, travel.Destination.Position!.Z, precision: 3);
        Assert.Null(travel.RouteHint);
    }

    [Fact]
    public void SF4_NoAethernet_CrossZone_UsesBefore_Position()
    {
        // CONTRACT: Cross-zone travel with no aethernet → Destination.Position = before.Position
        // (the player's approach coords in the source zone, where vnavmesh navigates to
        // before the zone gate transition happens). NOT the landing coords in after.

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            position: new WorldPosition(60, 4, -80),   // approach position in zone 131
            lastAethernetShardInteracted: null);
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),  // landing position in zone 130
            capturedAt: T1);

        var step = StepFactory.Build("travel", "travel-130", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.Destination.Position);
        Assert.Equal(60f,  travel.Destination.Position!.X, precision: 3);  // before.Position
        Assert.Equal(4f,   travel.Destination.Position!.Y, precision: 3);
        Assert.Equal(-80f, travel.Destination.Position!.Z, precision: 3);
        Assert.Null(travel.RouteHint);
    }

    [Fact]
    public void SF5_BeforeOmitted_RegularTravel_NoThrow()
    {
        // RED: Will fail until Builder adds 'before' parameter to StepFactory.Build.
        //
        // CONTRACT: Given Build("travel", id, expect, after) is called without 'before',
        //           When Build executes,
        //           Then it returns a TravelStep using after.Position, does not throw.
        //
        // BUILDER GUIDANCE: The 'before' parameter must be optional (default null).
        // Existing call sites omitting 'before' must continue to work identically.

        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(55, 1, 33),
            capturedAt: T1);

        // RED: signature change required; current Build has no 'before' parameter
        var step = StepFactory.Build("travel", "travel-130", "playerZone() == 130", after);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal(130, travel.Destination.Zone);
    }

    [Fact]
    public void SF6_StaleShard_CrossZone_UsesBefore_Position()
    {
        // CONTRACT: Stale shard (LastNpcInteracted=7 ≠ shard=125) → isAethernet=false.
        // Cross-zone → Destination.Position = before.Position (approach in source zone).
        // RouteHint=null since this is just a border run, not aethernet.

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            position: new WorldPosition(60, 4, -80),   // approach in source zone
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(7),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            capturedAt: T1);

        var step = StepFactory.Build("travel", "travel-130", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal(60f,  travel.Destination.Position!.X, precision: 3);  // before.Position
        Assert.Equal(4f,   travel.Destination.Position!.Y, precision: 3);
        Assert.Equal(-80f, travel.Destination.Position!.Z, precision: 3);
        Assert.Null(travel.RouteHint);
    }

    [Fact]
    public void SF7_ShardSetButLastNpcPositionIsNull_CrossZone_UsesBefore_Position()
    {
        // CONTRACT: Shard 125 + LastNpcInteracted=125 but LastNpcPosition=null → isAethernet=false
        // (no source position to navigate to, so aethernet branch is skipped).
        // Cross-zone → Destination.Position = before.Position (approach in source zone).

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            position: new WorldPosition(60, 4, -80),   // approach in source zone
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: null);
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        var step = StepFactory.Build("travel", "travel-130", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal(60f,  travel.Destination.Position!.X, precision: 3);  // before.Position
        Assert.Equal(4f,   travel.Destination.Position!.Y, precision: 3);
        Assert.Equal(-80f, travel.Destination.Position!.Z, precision: 3);
    }

    [Fact]
    public void SF8_NonTravelType_Talk_AethernetBeforeHasNoEffect()
    {
        // RED: Will fail until Builder adds 'before' parameter to StepFactory.Build.
        //
        // CONTRACT: Given Build("talk", ..., before: before) where before has aethernet conditions met,
        //           When Build executes,
        //           Then returns TalkStep (aethernet logic does not affect non-travel step types).
        //
        // BUILDER GUIDANCE: The aethernet-aware path is gated on stepType == "travel".
        // All other types return their existing step implementations unchanged.

        var before = MakeSnapshot(
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastNpcInteracted: new NpcId(9),
            lastNpcPosition: new WorldPosition(5, 0, 5),
            lastAethernetShardInteracted: new AetheryteId(33),
            capturedAt: T1);

        // RED: 'before' parameter does not exist yet
        var step = StepFactory.Build("talk", "talk-to-npc-9", null, after, before: before);

        Assert.IsType<QuestForge.Schema.TalkStep>(step);
    }

    [Fact]
    public void SF9_NonTravelType_Accept_AethernetBeforeHasNoEffect()
    {
        // RED: Will fail until Builder adds 'before' parameter to StepFactory.Build.
        //
        // CONTRACT: Given Build("accept", ..., before: before) where before has aethernet conditions,
        //           When Build executes,
        //           Then returns AcceptStep (aethernet logic is travel-only).
        //
        // BUILDER GUIDANCE: Same gate as SF8 — only "travel" type inspects 'before' for aethernet.

        var before = MakeSnapshot(
            lastAethernetShardInteracted: new AetheryteId(125),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10, 0, 20));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastNpcInteracted: new NpcId(9),
            lastNpcPosition: new WorldPosition(5, 0, 5),
            capturedAt: T1);

        // RED: 'before' parameter does not exist yet
        var step = StepFactory.Build("accept", "accept-quest", "isQuestAccepted(2054)", after, before: before);

        Assert.IsType<QuestForge.Schema.AcceptStep>(step);
    }

    [Fact]
    public void SF10_BothSnapshotsNull_TravelStep_NoThrow()
    {
        // RED: Will fail until Builder adds 'before' parameter to StepFactory.Build.
        //
        // CONTRACT: Given Build("travel", id, expect, null) with no 'before',
        //           When Build executes,
        //           Then returns TravelStep with Zone=0, does not throw.
        //
        // BUILDER GUIDANCE: Null-safe guards already exist for 'after'. The new 'before'
        // parameter (default null) must likewise be null-safe throughout the aethernet path.

        // RED: signature change required
        var step = StepFactory.Build("travel", "travel-0", null, after: null);

        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal(0, travel.Destination.Zone);
    }
}
