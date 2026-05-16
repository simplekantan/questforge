using System.Text.Json;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: End-to-end authoring test — scenario 46 from PHASE_9_PLAN.md §5.6.
/// This test will fail until Builder implements StepInferenceEngine.Infer,
/// QuestDraft.AddStep, and QuestDraft.ToQuestDefinition.
/// </summary>
public sealed class AuthoringEndToEndTests
{
    private static readonly QuestId Quest2054 = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly NpcLocation AcceptNpcLoc =
        new(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));

    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        int questSeq = 0,
        bool accepted = false,
        bool completed = false,
        NpcId? lastNpc = null,
        string? dialogueAnswer = null,
        DateTimeOffset? at = null) =>
        new(
            CapturedAt: at ?? T0,
            Zone: zone ?? new ZoneId(128),
            Position: new WorldPosition(9.5f, 40f, 14.2f),
            ActiveQuest: Quest2054,
            QuestSequence: questSeq,
            QuestFlags: 0,
            QuestAccepted: accepted,
            QuestCompleted: completed,
            LastNpcInteracted: lastNpc,
            LastNpcPosition: lastNpc.HasValue ? new WorldPosition(9.5f, 40f, 14.2f) : null,
            LastDialoguePrompt: dialogueAnswer != null ? "prompt-ref" : null,
            LastDialogueAnswer: dialogueAnswer,
            InventoryHash: 0,
            LastAttuned: null);

    // =========================================================================
    // Scenario 46 — AuthorOneQuest_EndToEnd_ProducesValidDefinition
    // =========================================================================
    [Fact]
    public void AuthorOneQuest_EndToEnd_ProducesValidDefinition()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer,
         *      QuestDraft.AddStep, and QuestDraft.ToQuestDefinition.
         *
         * CONTRACT: Given snapshot transitions for:
         *             zone change → accept → talk (seq 1) → talk (seq 2) → turn-in,
         *           When Infer is called for each transition, DraftStep built from result,
         *                AddStep called for each, then ToQuestDefinition called,
         *           Then the result has steps with the correct step types in order,
         *                and QuestForgeJsonContext round-trip succeeds.
         *
         * BUILDER GUIDANCE: Full happy path — exercise all inference rules and serialization.
         *                   This test validates the end-to-end contract from raw snapshots
         *                   to a valid exportable QuestDefinition.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var draft = new QuestDraft(Quest2054, T0)
        {
            QuestName = "Ishgardian Justice",
            Category = "msq",
            Expansion = "heavensward",
            LastVerifiedPatch = "7.4"
        };

        var npc = new NpcId(1000789);

        // Transition 1: Zone change (travel)
        var snap0 = MakeSnapshot(zone: new ZoneId(100));
        var snap1 = MakeSnapshot(zone: new ZoneId(128), at: T0.AddSeconds(10));
        var result1 = engine.Infer(snap0, snap1);
        // Build a concrete travel step from inference
        var travelStep = new TravelStep { Id = "travel-to-zone-128", Destination = new TravelDestination(Zone: 128) };
        var draftStep1 = new DraftStep(
            StepId: result1.SuggestedStepId.Length > 0 ? result1.SuggestedStepId : "travel-to-zone-128",
            StepType: result1.StepType,
            SequenceNumber: 0,
            InferredFrom: result1.InferredFrom,
            ObservedBefore: snap0,
            ObservedAfter: snap1,
            SuggestedExpect: result1.SuggestedExpect,
            Notes: result1.Notes,
            Raw: travelStep);
        draft.AddStep(draftStep1, T0.AddSeconds(10));

        // Transition 2: Quest accepted
        var snap2 = MakeSnapshot(zone: new ZoneId(128), accepted: true, lastNpc: npc, at: T0.AddSeconds(20));
        var result2 = engine.Infer(snap1, snap2);
        var acceptStep = new AcceptStep { Id = "accept-quest", Target = AcceptNpcLoc };
        var draftStep2 = new DraftStep(
            StepId: "accept-quest",
            StepType: result2.StepType,
            SequenceNumber: 1,
            InferredFrom: result2.InferredFrom,
            ObservedBefore: snap1,
            ObservedAfter: snap2,
            SuggestedExpect: result2.SuggestedExpect,
            Notes: result2.Notes,
            Raw: acceptStep);
        draft.AddStep(draftStep2, T0.AddSeconds(20));

        // Transition 3: Quest sequence advance (talk step, seq 1 → 2)
        var snap3 = MakeSnapshot(zone: new ZoneId(128), accepted: true, questSeq: 1, lastNpc: npc, at: T0.AddSeconds(30));
        var result3 = engine.Infer(snap2, snap3);
        var talkStep1 = new TalkStep
        {
            Id = "talk-to-npc-first",
            Target = AcceptNpcLoc,
            Expect = result3.SuggestedExpect != null
                ? new PredicateExpect { Predicate = result3.SuggestedExpect }
                : null
        };
        var draftStep3 = new DraftStep(
            StepId: "talk-to-npc-first",
            StepType: result3.StepType,
            SequenceNumber: 2,
            InferredFrom: result3.InferredFrom,
            ObservedBefore: snap2,
            ObservedAfter: snap3,
            SuggestedExpect: result3.SuggestedExpect,
            Notes: result3.Notes,
            Raw: talkStep1);
        draft.AddStep(draftStep3, T0.AddSeconds(30));

        // Transition 4: Quest sequence advance again (talk step, seq 2 → 3)
        var snap4 = MakeSnapshot(zone: new ZoneId(128), accepted: true, questSeq: 2, lastNpc: npc, at: T0.AddSeconds(40));
        var result4 = engine.Infer(snap3, snap4);
        var talkStep2 = new TalkStep
        {
            Id = "talk-to-npc-second",
            Target = AcceptNpcLoc,
            Expect = result4.SuggestedExpect != null
                ? new PredicateExpect { Predicate = result4.SuggestedExpect }
                : null
        };
        var draftStep4 = new DraftStep(
            StepId: "talk-to-npc-second",
            StepType: result4.StepType,
            SequenceNumber: 3,
            InferredFrom: result4.InferredFrom,
            ObservedBefore: snap3,
            ObservedAfter: snap4,
            SuggestedExpect: result4.SuggestedExpect,
            Notes: result4.Notes,
            Raw: talkStep2);
        draft.AddStep(draftStep4, T0.AddSeconds(40));

        // Transition 5: Quest completed (turn-in)
        var snap5 = MakeSnapshot(zone: new ZoneId(128), accepted: true, questSeq: 2, completed: true, lastNpc: npc, at: T0.AddSeconds(50));
        var result5 = engine.Infer(snap4, snap5);
        var turnInStep = new TurnInStep { Id = "turn-in-quest", Target = AcceptNpcLoc };
        var draftStep5 = new DraftStep(
            StepId: "turn-in-quest",
            StepType: result5.StepType,
            SequenceNumber: 4,
            InferredFrom: result5.InferredFrom,
            ObservedBefore: snap4,
            ObservedAfter: snap5,
            SuggestedExpect: result5.SuggestedExpect,
            Notes: result5.Notes,
            Raw: turnInStep);
        draft.AddStep(draftStep5, T0.AddSeconds(50));

        // Act
        var definition = draft.ToQuestDefinition();

        // Assert — structural checks
        Assert.NotNull(definition);
        Assert.Equal(Quest2054.Value, definition.Id);
        Assert.Equal("Ishgardian Justice", definition.Name);
        Assert.Equal(5, draft.Steps.Count);

        // The result has 5 distinct sequences (seq 0-4)
        Assert.Equal(5, definition.Sequences.Length);

        // Step type assertions (verifies inference rules produced correct step types)
        Assert.Equal("travel", result1.StepType);
        Assert.Equal("accept", result2.StepType);
        Assert.Equal("talk", result3.StepType);
        Assert.Equal("talk", result4.StepType);
        Assert.Equal("turn-in", result5.StepType);

        // AcceptFrom should be inferred from the accept step
        Assert.NotNull(definition.AcceptFrom);

        // JSON round-trip
        var json = JsonSerializer.Serialize(definition, QuestForgeJsonContext.QuestFileOptions);
        Assert.NotNull(json);
        var deserialized = JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions);
        Assert.NotNull(deserialized);
        Assert.Equal(definition.Id, deserialized.Id);
        Assert.Equal(definition.Sequences.Length, deserialized.Sequences.Length);
    }
}
