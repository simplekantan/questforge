using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for StepInferenceEngine.Infer — scenarios 1-13 from PHASE_9_PLAN.md §5.1.
/// All tests will fail until Builder implements StepInferenceEngine.Infer.
/// </summary>
public sealed class StepInferenceEngineTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static GameStateSnapshot BaseSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        QuestId? activeQuest = null,
        int questSequence = 0,
        uint questFlags = 0,
        bool questAccepted = false,
        bool questCompleted = false,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        string? lastDialoguePrompt = null,
        string? lastDialogueAnswer = null,
        uint inventoryHash = 0,
        DateTimeOffset? capturedAt = null,
        AetheryteId? lastAttuned = null) =>
        new(
            CapturedAt: capturedAt ?? T0,
            Zone: zone ?? new ZoneId(100),
            Position: position ?? new WorldPosition(10, 0, 10),
            ActiveQuest: activeQuest ?? ActiveQuest,
            QuestSequence: questSequence,
            QuestFlags: questFlags,
            QuestAccepted: questAccepted,
            QuestCompleted: questCompleted,
            LastNpcInteracted: lastNpcInteracted,
            LastNpcPosition: lastNpcPosition,
            LastDialoguePrompt: lastDialoguePrompt,
            LastDialogueAnswer: lastDialogueAnswer,
            InventoryHash: inventoryHash,
            LastAttuned: lastAttuned);

    // =========================================================================
    // Scenario 1 — InferTravel_WhenZoneChanges
    // =========================================================================
    [Fact]
    public void InferTravel_WhenZoneChanges()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.Zone=100 and after.Zone=200 (no quest delta),
         *           When Infer is called,
         *           Then StepType=travel, Confidence=High, InferredFrom=ZoneChange,
         *                SuggestedExpect="playerZone() == 200"
         *
         * BUILDER GUIDANCE: Rule 4 in the priority table — zone change with no quest change.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(zone: new ZoneId(100), position: new WorldPosition(10, 0, 10));
        var after = BaseSnapshot(zone: new ZoneId(200), position: new WorldPosition(50, 0, 50), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("travel", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.ZoneChange, result.InferredFrom);
        Assert.Equal("playerZone() == 200", result.SuggestedExpect);
    }

    // =========================================================================
    // Scenario 2 — InferAccept_WhenQuestAcceptedFires
    // =========================================================================
    [Fact]
    public void InferAccept_WhenQuestAcceptedFires()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.QuestAccepted=false and after.QuestAccepted=true,
         *           When Infer is called,
         *           Then StepType=accept, Confidence=High, InferredFrom=QuestAccepted,
         *                SuggestedExpect="isQuestAccepted(2054)"
         *
         * BUILDER GUIDANCE: Rule 2 in the priority table.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questAccepted: false);
        var after = BaseSnapshot(questAccepted: true, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
        Assert.Equal("isQuestAccepted(2054)", result.SuggestedExpect);
    }

    // =========================================================================
    // Scenario 3 — InferTurnIn_WhenQuestCompletedFires
    // =========================================================================
    [Fact]
    public void InferTurnIn_WhenQuestCompletedFires()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.QuestCompleted=false and after.QuestCompleted=true,
         *           When Infer is called,
         *           Then StepType=turn-in, Confidence=High, InferredFrom=QuestCompleted,
         *                SuggestedExpect="isQuestComplete(2054)"
         *
         * BUILDER GUIDANCE: Rule 1 in the priority table.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questCompleted: false);
        var after = BaseSnapshot(questCompleted: true, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
        Assert.Equal("isQuestComplete(2054)", result.SuggestedExpect);
    }

    // =========================================================================
    // Scenario 4 — InferTalk_WhenSequenceAdvances
    // =========================================================================
    [Fact]
    public void InferTalk_WhenSequenceAdvances()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.QuestSequence=0 and after.QuestSequence=1,
         *                 same zone, after.LastNpcInteracted=1014875,
         *           When Infer is called,
         *           Then StepType=talk, Confidence=High, InferredFrom=QuestSequenceChange,
         *                SuggestedExpect="questSequence(2054) >= 1",
         *                SuggestedStepId="talk-to-npc-1014875"
         *
         * BUILDER GUIDANCE: Rule 3 in the priority table.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var npcId = new NpcId(1014875);
        var before = BaseSnapshot(questSequence: 0);
        var after = BaseSnapshot(questSequence: 1, lastNpcInteracted: npcId, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.QuestSequenceChange, result.InferredFrom);
        Assert.Equal("questSequence(2054) >= 1", result.SuggestedExpect);
        Assert.Equal("talk-to-npc-1014875", result.SuggestedStepId);
    }

    // =========================================================================
    // Scenario 5 — InferFlag_WhenFlagBitFlipsWithoutSequence
    // =========================================================================
    [Fact]
    public void InferFlag_WhenFlagBitFlipsWithoutSequence()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.QuestFlags=0x00 and after.QuestFlags=0x04 (bit 2 set),
         *                 quest sequence unchanged,
         *           When Infer is called,
         *           Then StepType=talk, Confidence=Medium, InferredFrom=QuestFlagChange,
         *                SuggestedExpect="questFlag(2054, 2)"
         *
         * BUILDER GUIDANCE: Rule 5. XOR before ^ after = 0x04; lowest set bit = bit 2.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questFlags: 0x00, questSequence: 1);
        var after = BaseSnapshot(questFlags: 0x04, questSequence: 1, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.Equal(InferredFrom.QuestFlagChange, result.InferredFrom);
        Assert.Equal("questFlag(2054, 2)", result.SuggestedExpect);
    }

    // =========================================================================
    // Scenario 6 — InferFlag_LowestBitWins_WhenMultipleBitsFlip
    // =========================================================================
    [Fact]
    public void InferFlag_LowestBitWins_WhenMultipleBitsFlip()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.QuestFlags=0x00 and after.QuestFlags=0x14 (bits 2 and 4),
         *           When Infer is called,
         *           Then SuggestedExpect="questFlag(2054, 2)" (lowest bit wins),
         *                Notes contains "Multiple flag bits changed: bits 2, 4"
         *
         * BUILDER GUIDANCE: Rule 5. XOR = 0x14 = bits 2 and 4. Lowest = bit 2.
         *                   Notes must mention all flipped bits.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questFlags: 0x00, questSequence: 1);
        var after = BaseSnapshot(questFlags: 0x14, questSequence: 1, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("questFlag(2054, 2)", result.SuggestedExpect);
        Assert.NotNull(result.Notes);
        Assert.Contains("2", result.Notes, StringComparison.Ordinal);
        Assert.Contains("4", result.Notes, StringComparison.Ordinal);
        Assert.Contains("Multiple flag bits changed", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Scenario 7 — InferTalk_FromDialogueOnly_WhenNoOtherDelta
    // =========================================================================
    [Fact]
    public void InferTalk_FromDialogueOnly_WhenNoOtherDelta()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given only LastDialogueAnswer differs (rules 1-5 don't match),
         *           When Infer is called,
         *           Then StepType=talk, Confidence=Medium, InferredFrom=DialogueInteraction,
         *                SuggestedExpect="questSequence(2054) >= 0"
         *
         * BUILDER GUIDANCE: Rule 6. Dialogue answer changed; no quest/zone/flag delta.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(lastDialogueAnswer: "old-answer", questSequence: 0);
        var after = BaseSnapshot(lastDialogueAnswer: "new-answer", questSequence: 0, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
        Assert.Equal("questSequence(2054) >= 0", result.SuggestedExpect);
    }

    // =========================================================================
    // Scenario 8 — InferTalk_LowConfidence_WhenOnlyNpcChanged
    // =========================================================================
    [Fact]
    public void InferTalk_LowConfidence_WhenOnlyNpcChanged()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.LastNpcInteracted=null and after.LastNpcInteracted=1014875,
         *                 no quest/zone/dialogue delta,
         *           When Infer is called,
         *           Then StepType=talk, Confidence=Low, SuggestedExpect=null,
         *                InferredFrom=DialogueInteraction
         *
         * BUILDER GUIDANCE: Rule 7. NPC changed with no other signal.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(lastNpcInteracted: null);
        var after = BaseSnapshot(lastNpcInteracted: new NpcId(1014875), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal(Confidence.Low, result.Confidence);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
    }

    // =========================================================================
    // Scenario 9 — NoInference_WhenNothingChanged
    // =========================================================================
    [Fact]
    public void NoInference_WhenNothingChanged()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before == after (same snapshot values),
         *           When Infer is called,
         *           Then returns InferenceResult.Empty
         *                (Confidence=Low, StepType="", InferredFrom=None)
         *
         * BUILDER GUIDANCE: Rule 8. No delta matches any rule.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot();
        var after = BaseSnapshot(capturedAt: T1); // same values, different timestamp

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("", result.StepType);
        Assert.Equal(Confidence.Low, result.Confidence);
        Assert.Equal(InferredFrom.None, result.InferredFrom);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal("", result.SuggestedStepId);
    }

    // =========================================================================
    // Scenario 10 — PriorityRule_SequenceChangeBeatsZoneChange
    // =========================================================================
    [Fact]
    public void PriorityRule_SequenceChangeBeatsZoneChange()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.Zone=100, before.QuestSequence=0
         *                 after.Zone=200,  after.QuestSequence=1
         *           When Infer is called,
         *           Then StepType=talk (rule 3 wins over rule 4),
         *                SuggestedExpect is the questSequence predicate (not playerZone),
         *                Notes mentions the zone change
         *
         * BUILDER GUIDANCE: §2.1 — rule 3 wins over rule 4 when both trigger.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(zone: new ZoneId(100), questSequence: 0);
        var after = BaseSnapshot(zone: new ZoneId(200), questSequence: 1, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal(InferredFrom.QuestSequenceChange, result.InferredFrom);
        Assert.NotNull(result.SuggestedExpect);
        Assert.Contains("questSequence", result.SuggestedExpect, StringComparison.Ordinal);
        Assert.DoesNotContain("playerZone", result.SuggestedExpect, StringComparison.Ordinal);
        Assert.NotNull(result.Notes);
        // Notes must mention zone change from 100 to 200
        Assert.Contains("100", result.Notes, StringComparison.Ordinal);
        Assert.Contains("200", result.Notes, StringComparison.Ordinal);
    }

    // =========================================================================
    // Scenario 11 — PriorityRule_AcceptBeatsSequenceChange
    // =========================================================================
    [Fact]
    public void PriorityRule_AcceptBeatsSequenceChange()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.QuestAccepted=false,  before.QuestSequence=0
         *                 after.QuestAccepted=true,    after.QuestSequence=1
         *           When Infer is called,
         *           Then StepType=accept (rule 2 wins over rule 3),
         *                InferredFrom=QuestAccepted
         *
         * BUILDER GUIDANCE: Rule 2 (accept) is evaluated before rule 3 (sequence).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questAccepted: false, questSequence: 0);
        var after = BaseSnapshot(questAccepted: true, questSequence: 1, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
    }

    // =========================================================================
    // Scenario 12 — PriorityRule_CompletedBeatsAccept
    // =========================================================================
    [Fact]
    public void PriorityRule_CompletedBeatsAccept()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.QuestAccepted=true, before.QuestCompleted=false
         *                 after.QuestAccepted=true,  after.QuestCompleted=true
         *           When Infer is called,
         *           Then StepType=turn-in (rule 1 wins)
         *
         * BUILDER GUIDANCE: Rule 1 (completed) is highest priority; evaluated first.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questAccepted: true, questCompleted: false);
        var after = BaseSnapshot(questAccepted: true, questCompleted: true, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("turn-in", result.StepType);
    }

    // =========================================================================
    // Phase 11B — Rule 2.5: AttunementChange inference (B1-B8 from §3.7)
    // =========================================================================

    [Fact]
    public void InferAttunement_HappyPath_NullToAttuned()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5 in StepInferenceEngine.Infer.
         *
         * CONTRACT: Given before.LastAttuned == null, after.LastAttuned == new AetheryteId(1000)
         *           (no quest delta),
         *           When Infer(before, after),
         *           Then:
         *             result.StepType == "attune"
         *             result.SuggestedStepId == "attune-aetheryte-1000"
         *             result.SuggestedExpect == "isAttuned(1000)"
         *             result.Confidence == Confidence.High
         *             result.InferredFrom == InferredFrom.AttunementChange
         *             result.Notes == null
         *
         * BUILDER GUIDANCE: Insert Rule 2.5 between Rule 2 (QuestAccepted) and Rule 3
         *   (QuestSequence advanced):
         *     if (after.LastAttuned != before.LastAttuned && after.LastAttuned.HasValue)
         *     {
         *         var id = after.LastAttuned.Value.Value;
         *         return new InferenceResult(
         *             StepType: "attune",
         *             SuggestedStepId: $"attune-aetheryte-{id}",
         *             SuggestedExpect: $"isAttuned({id})",
         *             Confidence: Confidence.High,
         *             InferredFrom: InferredFrom.AttunementChange,
         *             Notes: null);
         *     }
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(lastAttuned: null);
        var after = BaseSnapshot(lastAttuned: new AetheryteId(1000), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("attune", result.StepType);
        Assert.Equal("attune-aetheryte-1000", result.SuggestedStepId);
        Assert.Equal("isAttuned(1000)", result.SuggestedExpect);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.AttunementChange, result.InferredFrom);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void InferAttunement_NoChange_FallsThrough()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5.
         *
         * CONTRACT: Given before.LastAttuned == new AetheryteId(1000) and
         *           after.LastAttuned == new AetheryteId(1000) (no delta, no other changes),
         *           When Infer,
         *           Then result is InferenceResult.Empty (Rule 2.5 does not fire).
         *
         * BUILDER GUIDANCE: Rule 2.5 fires only when after.LastAttuned != before.LastAttuned.
         *   When both are equal (same AetheryteId), the rule is skipped. Falls through to Rule 8.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(lastAttuned: new AetheryteId(1000));
        var after = BaseSnapshot(lastAttuned: new AetheryteId(1000), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("", result.StepType);
        Assert.Equal(Confidence.Low, result.Confidence);
        Assert.Equal(InferredFrom.None, result.InferredFrom);
    }

    [Fact]
    public void InferAttunement_ValueChanges_EmitsNewId()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5.
         *
         * CONTRACT: Given before.LastAttuned == new AetheryteId(500),
         *           after.LastAttuned == new AetheryteId(1000),
         *           When Infer,
         *           Then result.SuggestedExpect == "isAttuned(1000)".
         *           (Rule emits the new attunement, not the old one.)
         *
         * BUILDER GUIDANCE: `after.LastAttuned.Value.Value` — .Value unwraps nullable,
         *   second .Value accesses AetheryteId.Value (uint). The emit uses the after value.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(lastAttuned: new AetheryteId(500));
        var after = BaseSnapshot(lastAttuned: new AetheryteId(1000), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("attune", result.StepType);
        Assert.Equal("isAttuned(1000)", result.SuggestedExpect);
        Assert.Equal("attune-aetheryte-1000", result.SuggestedStepId);
    }

    [Fact]
    public void InferAttunement_AttunementDisappears_DoesNotFire()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5.
         *
         * CONTRACT: Given before.LastAttuned == new AetheryteId(500), after.LastAttuned == null,
         *           When Infer,
         *           Then Rule 2.5 does NOT fire (after.LastAttuned.HasValue == false).
         *           Falls through; returns InferenceResult.Empty if no other delta.
         *
         * BUILDER GUIDANCE: The guard is `after.LastAttuned.HasValue` — null means the aggregator
         *   was reset or no attunement occurred. "Disappearing" attunement is not a step event.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(lastAttuned: new AetheryteId(500));
        var after = BaseSnapshot(lastAttuned: null, capturedAt: T1);  // only change: LastAttuned → null

        // Act
        var result = engine.Infer(before, after);

        // Assert
        // Rule 2.5 must NOT fire. No other delta exists, so Rule 8 (Empty) should apply.
        Assert.NotEqual("attune", result.StepType);
        Assert.Equal("", result.StepType);
    }

    [Fact]
    public void InferAttunement_PriorityOverQuestSequence()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5.
         *
         * CONTRACT: Given attunement change AND after.QuestSequence > before.QuestSequence,
         *           When Infer,
         *           Then result.StepType == "attune" (Rule 2.5 fires before Rule 3).
         *
         * BUILDER GUIDANCE: Rule 2.5 is inserted between Rule 2 and Rule 3 in the cascade.
         *   The attunement signal is more specific than a generic sequence advance.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questSequence: 0, lastAttuned: null);
        var after = BaseSnapshot(questSequence: 1, lastAttuned: new AetheryteId(1000), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("attune", result.StepType);
        Assert.Equal(InferredFrom.AttunementChange, result.InferredFrom);
    }

    [Fact]
    public void InferAttunement_PriorityOverZoneChange()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5.
         *
         * CONTRACT: Given attunement change AND zone change (no quest delta),
         *           When Infer,
         *           Then result.StepType == "attune" (Rule 2.5 fires before Rule 4).
         *
         * BUILDER GUIDANCE: Rule 2.5 (between Rule 2 and Rule 3) fires before Rule 4 (zone change).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(zone: new ZoneId(100), lastAttuned: null);
        var after = BaseSnapshot(zone: new ZoneId(200), lastAttuned: new AetheryteId(1000), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("attune", result.StepType);
        Assert.Equal(InferredFrom.AttunementChange, result.InferredFrom);
    }

    [Fact]
    public void InferAttunement_Rule1StillWins_QuestCompleted()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5 (but in correct priority position).
         *
         * CONTRACT: Given after.QuestCompleted == true && !before.QuestCompleted AND attunement change,
         *           When Infer,
         *           Then result.StepType == "turn-in" (Rule 1 fires first).
         *
         * BUILDER GUIDANCE: Rule 1 (QuestCompleted) is evaluated before Rule 2.5.
         *   Even if the attunement also changes, the quest-completion signal dominates.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questCompleted: false, lastAttuned: null);
        var after = BaseSnapshot(questCompleted: true, lastAttuned: new AetheryteId(1000), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }

    [Fact]
    public void InferAttunement_Rule2StillWins_QuestAccepted()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.5 (but in correct priority position).
         *
         * CONTRACT: Given after.QuestAccepted == true && !before.QuestAccepted AND attunement change,
         *           When Infer,
         *           Then result.StepType == "accept" (Rule 2 fires before Rule 2.5).
         *
         * BUILDER GUIDANCE: Rule 2 (QuestAccepted) is evaluated before Rule 2.5.
         *   Accepting a quest is a stronger signal than a coincident attunement.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(questAccepted: false, lastAttuned: null);
        var after = BaseSnapshot(questAccepted: true, lastAttuned: new AetheryteId(1000), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
    }

    // =========================================================================
    // Scenario 13 — MultiSignalExpect_WhenSeqAdvancesAndZoneChanges
    // =========================================================================
    [Fact]
    public void MultiSignalExpect_WhenSeqAdvancesAndZoneChanges()
    {
        /*
         * RED: Will fail until Builder implements StepInferenceEngine.Infer
         *
         * CONTRACT: Given before.Zone=100, before.QuestSequence=0
         *                 after.Zone=200,  after.QuestSequence=1
         *           When Infer is called,
         *           Then StepType=talk, SuggestedExpect="questSequence(2054) >= 1" (single predicate),
         *                Notes contains zone-change hint mentioning "Zone also changed from 100 to 200"
         *                No AllExpect — only a single predicate string
         *
         * BUILDER GUIDANCE: §2.3 single-predicate policy. Rule 3 wins; zone goes into Notes.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = BaseSnapshot(zone: new ZoneId(100), questSequence: 0);
        var after = BaseSnapshot(zone: new ZoneId(200), questSequence: 1, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("talk", result.StepType);
        Assert.Equal("questSequence(2054) >= 1", result.SuggestedExpect);
        Assert.NotNull(result.Notes);
        Assert.Contains("Zone also changed from 100 to 200", result.Notes, StringComparison.OrdinalIgnoreCase);
    }
}
