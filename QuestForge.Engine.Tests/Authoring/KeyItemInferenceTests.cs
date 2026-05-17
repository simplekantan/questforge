using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Tests for key-item authoring inference gaps:
///   - GameStateSnapshot.KeyItemsAdded non-positional property
///   - SnapshotAggregator.OnKeyItemsChanged
///   - StepInferenceEngine Rule 2.3 (pickup-item)
/// </summary>
public sealed class KeyItemInferenceTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    // =========================================================================
    // GameStateSnapshot — KeyItems property (new in Phase 12)
    // =========================================================================

    [Fact]
    public void GameStateSnapshot_KeyItems_IsNullByDefault()
    {
        /*
         * RED: Will fail until Builder adds the KeyItems init property to GameStateSnapshot.
         *
         * CONTRACT: A snapshot constructed without the KeyItems init property leaves
         *           KeyItems null (production mode — hash-only path).
         *
         * BUILDER GUIDANCE: Add
         *   public IReadOnlyDictionary<uint,int>? KeyItems { get; init; }
         *   as a non-positional init property on GameStateSnapshot, similar to
         *   KeyItemsAdded and KeyItemsRemoved already present.
         */

        // Arrange + Act
        var snapshot = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(100),
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

        // Assert
        Assert.Null(snapshot.KeyItems);
    }

    [Fact]
    public void GameStateSnapshot_KeyItems_CanBeSetViaObjectInitializer()
    {
        /*
         * RED: Will fail until Builder adds the KeyItems init property to GameStateSnapshot.
         *
         * CONTRACT: The KeyItems init property can be set via object-initializer syntax.
         *           The stored value is accessible and contains the expected entries.
         *
         * BUILDER GUIDANCE: The { KeyItems = ... } object initializer syntax works because
         *   the property is declared as `init`. No constructor change needed.
         */

        // Arrange
        var items = new Dictionary<uint, int> { [2000100u] = 1, [2000200u] = 3 };

        // Act
        var snapshot = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(100),
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
        { KeyItems = items };

        // Assert
        Assert.NotNull(snapshot.KeyItems);
        Assert.Equal(2, snapshot.KeyItems!.Count);
        Assert.Equal(1, snapshot.KeyItems![2000100u]);
        Assert.Equal(3, snapshot.KeyItems![2000200u]);
    }

    // =========================================================================
    // GameStateSnapshot — KeyItemsAdded property
    // =========================================================================

    [Fact]
    public void GameStateSnapshot_KeyItemsAdded_IsNullByDefault()
    {
        // CONTRACT: A snapshot constructed without the init property leaves KeyItemsAdded null.
        var snapshot = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(100),
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

        Assert.Null(snapshot.KeyItemsAdded);
    }

    [Fact]
    public void GameStateSnapshot_KeyItemsAdded_CanBeSetViaObjectInitializer()
    {
        // CONTRACT: The init property can be set via object-initializer syntax.
        var items = new List<uint> { 1001u, 1002u };
        var snapshot = new GameStateSnapshot(
            CapturedAt: T0,
            Zone: new ZoneId(100),
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
        { KeyItemsAdded = items };

        Assert.NotNull(snapshot.KeyItemsAdded);
        Assert.Equal(2, snapshot.KeyItemsAdded.Count);
        Assert.Equal(1001u, snapshot.KeyItemsAdded[0]);
        Assert.Equal(1002u, snapshot.KeyItemsAdded[1]);
    }

    // =========================================================================
    // SnapshotAggregator — OnKeyItemsChanged
    // =========================================================================

    [Fact]
    public void SnapshotAggregator_KeyItemsAdded_IsNullByDefault()
    {
        // CONTRACT: Fresh aggregator produces Current.KeyItemsAdded == null.
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        Assert.Null(aggregator.Current.KeyItemsAdded);
    }

    [Fact]
    public void SnapshotAggregator_OnKeyItemsChanged_SetsKeyItemsAdded()
    {
        // CONTRACT: After OnKeyItemsChanged([1001]), Current.KeyItemsAdded == [1001].
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        aggregator.OnKeyItemsChanged(new List<uint> { 1001u });

        Assert.NotNull(aggregator.Current.KeyItemsAdded);
        var single = Assert.Single(aggregator.Current.KeyItemsAdded);
        Assert.Equal(1001u, single);
    }

    [Fact]
    public void SnapshotAggregator_OnKeyItemsChanged_EmptyList_ClearsField()
    {
        // CONTRACT: OnKeyItemsChanged([]) sets KeyItemsAdded back to null.
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        aggregator.OnKeyItemsChanged(new List<uint> { 1001u });

        aggregator.OnKeyItemsChanged(new List<uint>());

        Assert.Null(aggregator.Current.KeyItemsAdded);
    }

    [Fact]
    public void SnapshotAggregator_OnKeyItemsChanged_MultipleItems_AllPresent()
    {
        // CONTRACT: All item IDs passed are surfaced in Current.KeyItemsAdded.
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        aggregator.OnKeyItemsChanged(new List<uint> { 2001u, 2002u, 2003u });

        var added = aggregator.Current.KeyItemsAdded;
        Assert.NotNull(added);
        Assert.Equal(3, added.Count);
        Assert.Contains(2001u, added);
        Assert.Contains(2002u, added);
        Assert.Contains(2003u, added);
    }

    // =========================================================================
    // StepInferenceEngine — Rule 2.3: pickup-item
    // =========================================================================

    private static GameStateSnapshot MakeSnapshot(
        bool questAccepted = false,
        bool questCompleted = false,
        AetheryteId? lastAttuned = null,
        int questSequence = 0,
        IReadOnlyList<uint>? keyItemsAdded = null,
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
        { KeyItemsAdded = keyItemsAdded };

    [Fact]
    public void InferPickupItem_WhenSingleKeyItemAdded()
    {
        // CONTRACT: Rule 2.3 fires when after.KeyItemsAdded has one item.
        // StepType=pickup-item, SuggestedStepId=pickup-item-{id}, SuggestedExpect=null,
        // Confidence=Medium, InferredFrom=DialogueInteraction, Notes=null.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after = MakeSnapshot(keyItemsAdded: new List<uint> { 5001u }, capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("pickup-item", result.StepType);
        Assert.Equal("pickup-item-5001", result.SuggestedStepId);
        Assert.Null(result.SuggestedExpect);
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void InferPickupItem_WhenMultipleKeyItemsAdded_NotesMentionsAll()
    {
        // CONTRACT: When multiple key items are added, Notes mentions all IDs and
        // SuggestedStepId uses the first item ID.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after = MakeSnapshot(keyItemsAdded: new List<uint> { 5001u, 5002u }, capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("pickup-item", result.StepType);
        Assert.Equal("pickup-item-5001", result.SuggestedStepId);
        Assert.NotNull(result.Notes);
        Assert.Contains("5001", result.Notes, StringComparison.Ordinal);
        Assert.Contains("5002", result.Notes, StringComparison.Ordinal);
        Assert.Contains("Multiple key items acquired", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InferPickupItem_Skipped_WhenKeyItemsAddedIsNull()
    {
        // CONTRACT: Rule 2.3 does not fire when after.KeyItemsAdded is null.
        // With no other delta, falls through to InferenceResult.Empty.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after = MakeSnapshot(keyItemsAdded: null, capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.NotEqual("pickup-item", result.StepType);
        Assert.Equal("", result.StepType); // Rule 8: Empty
    }

    [Fact]
    public void InferPickupItem_Skipped_WhenKeyItemsAddedIsEmpty()
    {
        // CONTRACT: Rule 2.3 does not fire when after.KeyItemsAdded is an empty list.
        // The { Count: > 0 } pattern guard prevents this.
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot();
        var after = MakeSnapshot(keyItemsAdded: new List<uint>(), capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.NotEqual("pickup-item", result.StepType);
    }

    [Fact]
    public void PriorityRule_QuestCompleted_BeatsKeyItemsAdded()
    {
        // CONTRACT: Rule 1 (QuestCompleted) fires before Rule 2.3 (KeyItemsAdded).
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questCompleted: false);
        var after = MakeSnapshot(
            questCompleted: true,
            keyItemsAdded: new List<uint> { 5001u },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }

    [Fact]
    public void PriorityRule_QuestAccepted_BeatsKeyItemsAdded()
    {
        // CONTRACT: Rule 2 (QuestAccepted) fires before Rule 2.3 (KeyItemsAdded).
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(questAccepted: false);
        var after = MakeSnapshot(
            questAccepted: true,
            keyItemsAdded: new List<uint> { 5001u },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("accept", result.StepType);
        Assert.Equal(InferredFrom.QuestAccepted, result.InferredFrom);
    }

    [Fact]
    public void PriorityRule_KeyItemsAdded_BeatsAttunement()
    {
        // CONTRACT: Rule 2.3 (KeyItemsAdded) fires before Rule 2.5 (Attunement).
        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(lastAttuned: null);
        var after = MakeSnapshot(
            lastAttuned: new AetheryteId(1000),
            keyItemsAdded: new List<uint> { 5001u },
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("pickup-item", result.StepType);
        Assert.Equal(InferredFrom.DialogueInteraction, result.InferredFrom);
    }
}
