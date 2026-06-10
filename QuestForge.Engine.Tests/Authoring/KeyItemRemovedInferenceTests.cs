using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Tests for StepInferenceEngine Rule 2.4 (hand-over-item) and its priority ordering.
///
/// RED PHASE: All tests will fail until Builder implements:
///   - GameStateSnapshot.KeyItemsRemoved non-positional property
///   - SnapshotAggregator.OnKeyItemsRemoved method
///   - StepInferenceEngine Rule 2.4: KeyItemsRemoved → "hand-over-item"
///
/// Rule priority order (highest to lowest):
///   Rule 1   : QuestCompleted  → "turn-in"
///   Rule 2   : QuestAccepted   → "accept"
///   Rule 2.3 : KeyItemsAdded   → "pickup-item"
///   Rule 2.4 : KeyItemsRemoved → "hand-over-item"   ← NEW
///   Rule 2.5 : Attunement      → "attune"
///   Rule 3+  : sequence/zone/flags/dialogue/npc
/// </summary>
public sealed class KeyItemRemovedInferenceTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    /// <summary>
    /// Makes a baseline snapshot. Parameters mirror the existing MakeSnapshot helper
    /// in KeyItemInferenceTests, extended with <paramref name="keyItemsRemoved"/>.
    /// </summary>
    private static GameStateSnapshot MakeSnapshot(
        bool questAccepted = false,
        bool questCompleted = false,
        AetheryteId? lastAttuned = null,
        int questSequence = 0,
        IReadOnlyList<uint>? keyItemsAdded = null,
        IReadOnlyList<uint>? keyItemsRemoved = null,
        DateTimeOffset? capturedAt = null) =>
        new(
            CapturedAt: capturedAt ?? T0,
            Zone: new ZoneId(100),
            Position: new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: questSequence,
            QuestFlags: 0,
            QuestAccepted: questAccepted,
            QuestCompleted: questCompleted,
            LastNpcInteracted: null,
            LastNpcPosition: null,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: lastAttuned)
        {
            KeyItemsAdded   = keyItemsAdded,
            KeyItemsRemoved = keyItemsRemoved   // RED: will fail until Builder adds this property
        };

    // =========================================================================
    // B3 — single removed item → "hand-over-item"
    // =========================================================================

    [Fact]
    public void InferHandOverItem_WhenSingleKeyItemRemoved()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4 in StepInferenceEngine.
         *
         * CONTRACT: Given before=empty-baseline, after.KeyItemsRemoved=[5001],
         *           When StepInferenceEngine.Infer(before, after),
         *           Then:
         *             result.StepType        == "hand-over-item"
         *             result.SuggestedStepId == "hand-over-item-5001"
         *             result.Notes           == null
         *             result.Confidence      == Confidence.Medium
         *
         * BUILDER GUIDANCE: Insert Rule 2.4 after Rule 2.3 (KeyItemsAdded) and before
         *   Rule 2.5 (Attunement). Pattern-match after.KeyItemsRemoved is { Count: > 0 }.
         *   SuggestedStepId = $"hand-over-item-{firstItemId}".
         *   Notes = null for single item (mirrors Rule 2.3 behavior).
         *   Confidence = Confidence.Medium (same as Rule 2.3).
         *   InferredFrom = InferredFrom.DialogueInteraction (same as Rule 2.3).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(keyItemsRemoved: new List<uint> { 5001u }, capturedAt: T1) with { RequestAddonSeen = true };

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("hand-over-item", result.StepType);
        Assert.Equal("hand-over-item-5001", result.SuggestedStepId);
        Assert.Null(result.Notes);
        Assert.Equal(Confidence.Medium, result.Confidence);
    }

    // =========================================================================
    // B3b — multiple removed items → "hand-over-item", Notes contains all IDs
    // =========================================================================

    [Fact]
    public void InferHandOverItem_WhenMultipleKeyItemsRemoved_NotesMentionsAll()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4.
         *
         * CONTRACT: Given after.KeyItemsRemoved=[5001, 5002],
         *           When Infer,
         *           Then:
         *             result.StepType        == "hand-over-item"
         *             result.SuggestedStepId == "hand-over-item-5001"  (uses first)
         *             result.Notes contains "5001" AND "5002" AND "Multiple key items removed"
         *
         * BUILDER GUIDANCE: Mirror Rule 2.3 multiple-item Notes pattern, using the phrase
         *   "Multiple key items removed" (instead of "acquired"). Step ID still uses newItems[0].
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(keyItemsRemoved: new List<uint> { 5001u, 5002u }, capturedAt: T1) with { RequestAddonSeen = true };

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("hand-over-item", result.StepType);
        Assert.Equal("hand-over-item-5001", result.SuggestedStepId);
        Assert.NotNull(result.Notes);
        Assert.Contains("5001", result.Notes, StringComparison.Ordinal);
        Assert.Contains("5002", result.Notes, StringComparison.Ordinal);
        Assert.Contains("Multiple key items removed", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // B4 — KeyItemsRemoved == null → InferenceResult.Empty
    // =========================================================================

    [Fact]
    public void InferHandOverItem_Skipped_WhenKeyItemsRemovedIsNull()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4 (though currently may already
         *      return Empty for null — this test documents and locks the contract).
         *
         * CONTRACT: Given after.KeyItemsRemoved == null and no other delta,
         *           When Infer,
         *           Then Rule 2.4 does not fire; result.StepType == "" (Rule 8: Empty).
         *
         * BUILDER GUIDANCE: Use { Count: > 0 } pattern guard — same as Rule 2.3.
         *   null does not match { Count: > 0 }, so Rule 2.4 is skipped cleanly.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(keyItemsRemoved: null, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.NotEqual("hand-over-item", result.StepType);
        Assert.Equal("", result.StepType); // Rule 8: Empty
    }

    // =========================================================================
    // B4b — KeyItemsRemoved == [] (empty list) → Rule 2.4 does not fire
    // =========================================================================

    [Fact]
    public void InferHandOverItem_Skipped_WhenKeyItemsRemovedIsEmpty()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4 (pattern guard prevents empty-list fire).
         *
         * CONTRACT: Given after.KeyItemsRemoved == [] (empty, Count == 0),
         *           When Infer,
         *           Then Rule 2.4 does not fire; result.StepType != "hand-over-item".
         *
         * BUILDER GUIDANCE: The { Count: > 0 } guard in the pattern match prevents this.
         *   An empty list must NOT trigger hand-over-item inference.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(keyItemsRemoved: new List<uint>(), capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.NotEqual("hand-over-item", result.StepType);
    }

    // =========================================================================
    // B5a — QuestCompleted + KeyItemsRemoved → "turn-in" wins (Rule 1 priority)
    // =========================================================================

    [Fact]
    public void PriorityRule_QuestCompleted_BeatsKeyItemsRemoved()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4 (but Rule 1 must beat it).
         *
         * CONTRACT: Given after.QuestCompleted=true AND after.KeyItemsRemoved=[5001],
         *           When Infer,
         *           Then result.StepType == "turn-in" (Rule 1 fires first).
         *
         * BUILDER GUIDANCE: Rule 1 (QuestCompleted) is evaluated before Rule 2.4.
         *   No change needed to Rule 1. Rule 2.4 only fires when Rules 1-2.3 do not.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after  = MakeSnapshot(questCompleted: true, keyItemsRemoved: new List<uint> { 5001u }, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }

    // =========================================================================
    // B5b — QuestAccepted + KeyItemsRemoved → "accept" wins (Rule 2 priority)
    // =========================================================================

    [Fact]
    public void PriorityRule_QuestAccepted_BeatsKeyItemsRemoved()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4 (but Rule 2 must beat it).
         *
         * CONTRACT: Given after.QuestAccepted=true AND after.KeyItemsRemoved=[5001],
         *           When Infer,
         *           Then result.StepType == "accept" (Rule 2 fires first).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questAccepted: false);
        var after  = MakeSnapshot(questAccepted: true, keyItemsRemoved: new List<uint> { 5001u }, capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("accept", result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
    }

    // =========================================================================
    // B5c — KeyItemsAdded + KeyItemsRemoved → "pickup-item" wins (Rule 2.3 priority)
    // =========================================================================

    [Fact]
    public void PriorityRule_KeyItemsAdded_BeatsKeyItemsRemoved()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4 (but Rule 2.3 must beat it).
         *
         * CONTRACT: Given after.KeyItemsAdded=[1001] AND after.KeyItemsRemoved=[5001],
         *           When Infer,
         *           Then result.StepType == "pickup-item" (Rule 2.3 fires first).
         *
         * BUILDER GUIDANCE: Rule 2.3 is evaluated before Rule 2.4.
         *   Rule 2.4 only fires when Rule 2.3 does not (KeyItemsAdded is null or empty).
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after  = MakeSnapshot(
            keyItemsAdded:   new List<uint> { 1001u },
            keyItemsRemoved: new List<uint> { 5001u },
            capturedAt: T1);

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("pickup-item", result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
    }

    // =========================================================================
    // B5d — KeyItemsRemoved + Attunement delta → "hand-over-item" wins (Rule 2.4 > 2.5)
    // =========================================================================

    [Fact]
    public void PriorityRule_KeyItemsRemoved_BeatsAttunement()
    {
        /*
         * RED: Will fail until Builder implements Rule 2.4.
         *
         * CONTRACT: Given after.KeyItemsRemoved=[9001] AND after.LastAttuned=AetheryteId(1000),
         *           When Infer,
         *           Then result.StepType == "hand-over-item" (Rule 2.4 fires before Rule 2.5).
         *
         * BUILDER GUIDANCE: Rule 2.4 must be inserted between Rule 2.3 and Rule 2.5.
         *   When both KeyItemsRemoved and an attunement delta are present, hand-over-item wins.
         */

        // Arrange
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(lastAttuned: null);
        var after  = MakeSnapshot(
            lastAttuned:     new AetheryteId(1000),
            keyItemsRemoved: new List<uint> { 9001u },
            capturedAt: T1) with { RequestAddonSeen = true };

        // Act
        var result = engine.Infer(before, after);

        // Assert
        Assert.Equal("hand-over-item", result.StepType);
    }
}
