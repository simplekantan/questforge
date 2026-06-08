using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported here to avoid AetheryteId collisions with
// Adapters.Types.AetheryteId (same pattern as AethernetInferenceTests).

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for StepInferenceEngine Rule 4 NpcDialogue sub-case.
///
/// ALL tests in this file will fail (at compile or runtime) until Builder adds:
///   - GameStateSnapshot.DialogueOptionSelected property (already exists per GameStateSnapshot.cs)
///   - NpcDialogue sub-case in Rule 4 of StepInferenceEngine, BEFORE the aethernet sub-case
///
/// Test IDs: IN-1 through IN-10
/// </summary>
public sealed class NpcDialogueInferenceTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    /// <summary>
    /// Snapshot builder. dialogueOptionSelected maps to GameStateSnapshot.DialogueOptionSelected.
    /// </summary>
    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        bool questAccepted = false,
        bool questCompleted = false,
        int questSequence = 0,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        AetheryteId? lastAethernetShardInteracted = null,
        int? dialogueOptionSelected = null,
        DateTimeOffset? capturedAt = null,
        QuestForge.Schema.NpcLocation? dialogueNpcSource = null) =>
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
            LastAttuned: null)
        {
            LastAethernetShardInteracted = lastAethernetShardInteracted,
            DialogueOptionSelected = dialogueOptionSelected,
            DialogueNpcSource = dialogueNpcSource
        };

    // IN-1
    [Fact]
    public void IN1_ZoneChange_DialogueOptionSet_LastNpcInteracted_InfersNpcDialogue()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case to StepInferenceEngine Rule 4.
         *
         * CONTRACT: Given zone 131→200, DialogueOptionSelected=0, LastNpcInteracted=NpcId(7777),
         *           LastNpcPosition set,
         *           When Infer is called,
         *           Then StepType="travel", SuggestedStepId contains "npc-dialogue" and zone 200,
         *                Confidence=High, InferredFrom=ZoneChange.
         *
         * BUILDER GUIDANCE: In Rule 4 (after.Zone != before.Zone), add NpcDialogue sub-case
         * BEFORE the aethernet sub-case:
         *   if (after.DialogueOptionSelected.HasValue && after.LastNpcInteracted.HasValue)
         *     return NpcDialogue result with stepId = "npc-dialogue-to-zone-{after.Zone.Value}"
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            position: new WorldPosition(5f, 0f, 5f),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1,
            dialogueNpcSource: new QuestForge.Schema.NpcLocation(
                NpcId: 7777,
                Zone: 131,
                Position: new QuestForge.Schema.Position3(10f, 0f, 20f)));

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("travel", result.StepType);
        Assert.Contains("npc-dialogue", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200", result.SuggestedStepId, StringComparison.Ordinal);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.ZoneChange, result.InferredFrom);
    }

    // IN-2
    [Fact]
    public void IN2_BothNpcDialogueAndAethernetConditionsMet_NpcDialogueWins()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case before aethernet sub-case.
         *
         * CONTRACT: Given zone change + NpcDialogue conditions met (DialogueOptionSelected set,
         *           LastNpcInteracted set) + aethernet conditions also met (shard matches NPC),
         *           When Infer is called,
         *           Then SuggestedStepId contains "npc-dialogue", NOT "aethernet".
         *
         * BUILDER GUIDANCE: NpcDialogue sub-case must appear BEFORE aethernet sub-case in Rule 4.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            lastAethernetShardInteracted: new AetheryteId(7777)); // same as NPC — aethernet conditions
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            lastAethernetShardInteracted: new AetheryteId(33),
            dialogueOptionSelected: 0,
            capturedAt: T1,
            dialogueNpcSource: new QuestForge.Schema.NpcLocation(
                NpcId: 7777,
                Zone: 131,
                Position: new QuestForge.Schema.Position3(10f, 0f, 20f)));

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("travel", result.StepType);
        Assert.Contains("npc-dialogue", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aethernet", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
    }

    // IN-3
    [Fact]
    public void IN3_ZoneChange_AethernetConditionsMet_NoDialogueOption_AethernetDetected()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case (and aethernet continues to work).
         *
         * CONTRACT: Given zone change + aethernet conditions met + NO dialogue option set,
         *           When Infer is called,
         *           Then aethernet sub-case fires (SuggestedStepId contains "aethernet").
         *
         * BUILDER GUIDANCE: Absence of DialogueOptionSelected means NpcDialogue sub-case does not
         * fire, so the aethernet sub-case should proceed normally.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            lastAethernetShardInteracted: new AetheryteId(125)); // matches NPC — aethernet
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastAethernetShardInteracted: new AetheryteId(33),
            dialogueOptionSelected: null, // no dialogue option
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("aethernet", result.StepType);
        Assert.Contains("aethernet", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
    }

    // IN-4
    [Fact]
    public void IN4_ZoneChange_NoDialogue_NoAethernet_GenericTravel()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case (regression guard).
         *
         * CONTRACT: Given zone change + no dialogue option + no aethernet conditions,
         *           When Infer is called,
         *           Then generic "travel-to-zone-{N}" is returned.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            dialogueOptionSelected: null,
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("travel", result.StepType);
        Assert.Equal("travel-to-zone-200", result.SuggestedStepId);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Null(result.Notes);
    }

    // IN-5
    [Fact]
    public void IN5_DialogueOptionSet_ZoneUnchanged_NpcDialogueSubCaseDoesNotFire()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case (Rule 4 outer guard).
         *
         * CONTRACT: Given DialogueOptionSelected is set but zone UNCHANGED (131→131),
         *           When Infer is called,
         *           Then the NpcDialogue sub-case does NOT fire (Rule 4 outer guard requires
         *           zone change). Result is InferenceResult.Empty or a later rule match.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f));
        var after = MakeSnapshot(
            zone: new ZoneId(131), // SAME zone
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert — travel step for NpcDialogue must not fire; whatever fires is NOT npc-dialogue travel
        Assert.False(
            result.StepType == "travel" && result.SuggestedStepId.Contains("npc-dialogue", StringComparison.OrdinalIgnoreCase),
            $"NpcDialogue travel sub-case must not fire when zone is unchanged. Got: {result.SuggestedStepId}");
    }

    // IN-6
    [Fact]
    public void IN6_ZoneChange_DialogueOptionSet_LastNpcInteractedNull_FallsThrough()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case.
         *
         * CONTRACT: Given zone change + DialogueOptionSelected set + LastNpcInteracted==null,
         *           When Infer is called,
         *           Then NpcDialogue sub-case does NOT fire (no NPC to reference).
         *           Falls through to generic "travel-to-zone-{N}".
         *
         * BUILDER GUIDANCE: NpcDialogue sub-case requires both DialogueOptionSelected.HasValue
         * AND after.LastNpcInteracted.HasValue. Null NPC = cannot build NpcDialogueHint.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            lastNpcInteracted: null, // no NPC
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("travel", result.StepType);
        Assert.DoesNotContain("npc-dialogue", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("travel-to-zone-200", result.SuggestedStepId);
    }

    // IN-7
    [Fact]
    public void IN7_Priority_QuestCompleted_BeatsNpcDialogue()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case (priority guard).
         *
         * CONTRACT: Given zone change + NpcDialogue conditions met + QuestCompleted=true,
         *           When Infer is called,
         *           Then StepType="turn-in" (Rule 1 beats Rule 4).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            questCompleted: false,
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            questCompleted: true,
            lastNpcInteracted: new NpcId(7777),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }

    // IN-8
    [Fact]
    public void IN8_Priority_QuestAccepted_BeatsNpcDialogue()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case (priority guard).
         *
         * CONTRACT: Given zone change + NpcDialogue conditions met + QuestAccepted=true,
         *           When Infer is called,
         *           Then StepType="accept" (Rule 2 beats Rule 4).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            questAccepted: false,
            lastNpcInteracted: new NpcId(7777));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            questAccepted: true,
            lastNpcInteracted: new NpcId(7777),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
    }

    // IN-9
    [Fact]
    public void IN9_Priority_QuestSequenceAdvanced_BeatsNpcDialogue()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case (priority guard).
         *
         * CONTRACT: Given zone change + NpcDialogue conditions met + QuestSequence advanced,
         *           When Infer is called,
         *           Then StepType="talk" (Rule 3 beats Rule 4).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            questSequence: 10,
            lastNpcInteracted: new NpcId(7777));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            questSequence: 20, // sequence advanced
            lastNpcInteracted: new NpcId(7777),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal(InferredFrom.QuestSequenceChange, result.InferredFrom);
    }

    // IN-10
    [Fact]
    public void IN10_Notes_ExactFormat_NpcDialogue()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case with Notes formatting.
         *
         * CONTRACT: Given zone 131→401, NpcId=7777, DialogueOptionSelected=0,
         *           When Infer is called,
         *           Then Notes == "NpcDialogue: npc 7777 → choice 0 → zone 401" exactly.
         *
         * BUILDER GUIDANCE: Format: $"NpcDialogue: npc {npcId} → choice {dialogueOptionSelected} → zone {destZone}"
         * Use the actual arrow character → (U+2192), not ->.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f));
        var after = MakeSnapshot(
            zone: new ZoneId(401),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1,
            dialogueNpcSource: new QuestForge.Schema.NpcLocation(
                NpcId: 7777,
                Zone: 131,
                Position: new QuestForge.Schema.Position3(10f, 0f, 20f)));

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("travel", result.StepType);
        Assert.Contains("npc-dialogue", result.SuggestedStepId, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Notes);
        Assert.Equal("NpcDialogue: npc 7777 → choice 0 → zone 401", result.Notes);
    }
}
