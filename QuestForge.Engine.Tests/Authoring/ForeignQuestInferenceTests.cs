using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Tests for StepInferenceEngine Rule 2.1: ForeignQuestAccepted.
///
/// When an NPC offers a quest OTHER than the active quest being authored, the
/// inference engine must detect this as an "accept" step for the foreign quest.
///
/// Test IDs: FQ1 through FQ3
/// </summary>
public sealed class ForeignQuestInferenceTests
{
    private static readonly QuestId ActiveQuest = new(0x12054); // active quest being authored
    private static readonly QuestId ForeignQuest = new(0x1BEEF); // sub-quest offered by NPC

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        QuestId? activeQuest = null,
        bool questAccepted = false,
        bool questCompleted = false,
        int questSequence = 0,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        QuestId? foreignQuestAccepted = null,
        DateTimeOffset? capturedAt = null) =>
        new(
            CapturedAt: capturedAt ?? T0,
            Zone: zone ?? new ZoneId(131),
            Position: position ?? new WorldPosition(0, 0, 0),
            ActiveQuest: activeQuest ?? ActiveQuest,
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
            ForeignQuestAccepted = foreignQuestAccepted
        };

    // FQ1: before has no ForeignQuestAccepted, after has one set → infers accept step with High confidence
    [Fact]
    public void FQ1_ForeignQuestAccepted_InfersAcceptStep()
    {
        /*
         * CONTRACT: Given before.ForeignQuestAccepted is null and after.ForeignQuestAccepted is set,
         *           When Infer is called,
         *           Then StepType="accept", Confidence=High, InferredFrom=QuestAccepted,
         *                SuggestedStepId contains the foreign quest ID,
         *                SuggestedExpect = "isQuestAccepted({foreignQuestId})",
         *                Notes contains the foreign quest ID.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            foreignQuestAccepted: null);
        var after = MakeSnapshot(
            capturedAt: T1,
            lastNpcInteracted: new NpcId(9999),
            foreignQuestAccepted: ForeignQuest);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
        Assert.Contains(ForeignQuest.Value.ToString(), result.SuggestedStepId, StringComparison.Ordinal);
        Assert.Equal($"isQuestAccepted({ForeignQuest.Value})", result.SuggestedExpect);
        Assert.NotNull(result.Notes);
        Assert.Contains(ForeignQuest.Value.ToString(), result.Notes, StringComparison.Ordinal);
    }

    // FQ1b: NPC is known → SuggestedStepId uses "accept-quest-{id}" form
    [Fact]
    public void FQ1b_ForeignQuestAccepted_WithNpc_StepIdIncludesNpcForm()
    {
        /*
         * CONTRACT: When after.LastNpcInteracted is set, SuggestedStepId == "accept-quest-{foreignQuestId}".
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(foreignQuestAccepted: null);
        var after = MakeSnapshot(
            capturedAt: T1,
            lastNpcInteracted: new NpcId(9999),
            foreignQuestAccepted: ForeignQuest);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal($"accept-quest-{ForeignQuest.Value}", result.SuggestedStepId);
    }

    // FQ1c: NPC is unknown → SuggestedStepId falls back to "accept-quest"
    [Fact]
    public void FQ1c_ForeignQuestAccepted_NoNpc_StepIdFallsBack()
    {
        /*
         * CONTRACT: When neither before nor after has a LastNpcInteracted,
         *           SuggestedStepId == "accept-quest".
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            lastNpcInteracted: null,
            foreignQuestAccepted: null);
        var after = MakeSnapshot(
            capturedAt: T1,
            lastNpcInteracted: null,
            foreignQuestAccepted: ForeignQuest);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept-quest", result.SuggestedStepId);
    }

    // FQ2: before AND after both have the SAME ForeignQuestAccepted → no inference (already done)
    [Fact]
    public void FQ2_ForeignQuestAccepted_AlreadyInBefore_NoInference()
    {
        /*
         * CONTRACT: If before.ForeignQuestAccepted == after.ForeignQuestAccepted,
         *           Rule 2.1 must NOT fire (the accept already happened in a prior window).
         *           The result must not be an accept step triggered by the foreign quest rule.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(foreignQuestAccepted: ForeignQuest);
        var after = MakeSnapshot(
            capturedAt: T1,
            foreignQuestAccepted: ForeignQuest); // same value as before

        // Act
        var result = engine.Infer(before, after);

        // Assert — Rule 2.1 must not have fired
        // The result may be Empty or a lower-priority rule, but not a High-confidence
        // QuestAccepted-inferred accept step for the foreign quest.
        var isForeignAcceptInference =
            result.StepType == "accept"
            && result.InferredFrom == InferredFrom.QuestAccepted
            && result.SuggestedExpect == $"isQuestAccepted({ForeignQuest.Value})";
        Assert.False(isForeignAcceptInference,
            $"Rule 2.1 must not fire when ForeignQuestAccepted is unchanged. Got: {result.StepType}/{result.SuggestedStepId}");
    }

    // FQ3: after.ForeignQuestAccepted is null → no inference
    [Fact]
    public void FQ3_ForeignQuestAccepted_Null_NoInference()
    {
        /*
         * CONTRACT: When after.ForeignQuestAccepted is null, Rule 2.1 does not fire.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(foreignQuestAccepted: null);
        var after = MakeSnapshot(
            capturedAt: T1,
            foreignQuestAccepted: null);

        // Act
        var result = engine.Infer(before, after);

        // Assert — must not be an accept from ForeignQuestAccepted
        var isForeignAcceptInference =
            result.StepType == "accept"
            && result.InferredFrom == InferredFrom.QuestAccepted
            && result.Notes != null
            && result.Notes.Contains("sub-quest", StringComparison.OrdinalIgnoreCase);
        Assert.False(isForeignAcceptInference,
            $"Rule 2.1 must not fire when ForeignQuestAccepted is null. Got: {result.StepType}/{result.Notes}");
    }

    // FQ4: Rule 2.1 fires before lower-priority rules (e.g. NPC changed)
    [Fact]
    public void FQ4_ForeignQuestAccepted_BeatsLowerPriorityRules()
    {
        /*
         * CONTRACT: When ForeignQuestAccepted fires and LastNpcInteracted also changed,
         *           Rule 2.1 (ForeignQuestAccepted) wins over Rule 7 (NPC changed → talk Low).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            lastNpcInteracted: new NpcId(1111),
            foreignQuestAccepted: null);
        var after = MakeSnapshot(
            capturedAt: T1,
            lastNpcInteracted: new NpcId(9999), // NPC changed — Rule 7 could fire
            foreignQuestAccepted: ForeignQuest);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
        Assert.Equal(Confidence.High, result.Confidence);
    }
}
