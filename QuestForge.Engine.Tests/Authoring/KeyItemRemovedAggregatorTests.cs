using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Tests for SnapshotAggregator.OnKeyItemsRemoved and GameStateSnapshot.KeyItemsRemoved.
///
/// RED PHASE: All tests will fail until Builder implements:
///   - GameStateSnapshot.KeyItemsRemoved non-positional property (alongside KeyItemsAdded)
///   - SnapshotAggregator._keyItemsRemoved field and OnKeyItemsRemoved method
///   - SnapshotAggregator.Current includes KeyItemsRemoved in the snapshot
///
/// These tests mirror the structure of KeyItemInferenceTests.cs scenarios for OnKeyItemsChanged
/// (GameStateSnapshot B6/B7 group), but target the new removed-items counterpart.
/// </summary>
public sealed class KeyItemRemovedAggregatorTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // B6 — OnKeyItemsRemoved([9001]) → Current.KeyItemsRemoved == [9001]
    // =========================================================================

    [Fact]
    public void SnapshotAggregator_OnKeyItemsRemoved_SingleItem_SetProperty()
    {
        /*
         * RED: Will fail until Builder adds OnKeyItemsRemoved and KeyItemsRemoved to aggregator.
         *
         * CONTRACT: Given fresh SnapshotAggregator,
         *           When OnKeyItemsRemoved([9001]) is called,
         *           Then Current.KeyItemsRemoved is not null,
         *                Current.KeyItemsRemoved.Count == 1,
         *                Current.KeyItemsRemoved[0] == 9001u.
         *
         * BUILDER GUIDANCE: Add _keyItemsRemoved field (IReadOnlyList<uint>?) to SnapshotAggregator.
         *   Add OnKeyItemsRemoved(IReadOnlyList<uint> removed) method identical to OnKeyItemsChanged
         *   but targeting _keyItemsRemoved. Include KeyItemsRemoved = _keyItemsRemoved in Current.
         *   Add KeyItemsRemoved { get; init; } non-positional property to GameStateSnapshot (same
         *   pattern as KeyItemsAdded).
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        aggregator.OnKeyItemsRemoved(new List<uint> { 9001u }); // RED: method does not exist yet

        // Assert
        Assert.NotNull(aggregator.Current.KeyItemsRemoved);    // RED: property does not exist yet
        var single = Assert.Single(aggregator.Current.KeyItemsRemoved);
        Assert.Equal(9001u, single);
    }

    // =========================================================================
    // B6b — OnKeyItemsRemoved([]) after prior call → Current.KeyItemsRemoved == null
    // =========================================================================

    [Fact]
    public void SnapshotAggregator_OnKeyItemsRemoved_EmptyList_ClearsField()
    {
        /*
         * RED: Will fail until Builder adds OnKeyItemsRemoved.
         *
         * CONTRACT: Given OnKeyItemsRemoved([9001]) already called,
         *           When OnKeyItemsRemoved([]) is called,
         *           Then Current.KeyItemsRemoved == null (empty list clears the field).
         *
         * BUILDER GUIDANCE: Identical semantics to OnKeyItemsChanged: empty list → null.
         *   _keyItemsRemoved = removed.Count > 0 ? removed : null;
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        aggregator.OnKeyItemsRemoved(new List<uint> { 9001u });

        // Act
        aggregator.OnKeyItemsRemoved(new List<uint>());

        // Assert
        Assert.Null(aggregator.Current.KeyItemsRemoved);
    }

    // =========================================================================
    // B6c — OnKeyItemsRemoved([9001, 9002, 9003]) → all three present, Count==3
    // =========================================================================

    [Fact]
    public void SnapshotAggregator_OnKeyItemsRemoved_MultipleItems_AllPresent()
    {
        /*
         * RED: Will fail until Builder adds OnKeyItemsRemoved.
         *
         * CONTRACT: Given OnKeyItemsRemoved([9001, 9002, 9003]),
         *           When Current is read,
         *           Then KeyItemsRemoved.Count == 3 and all three IDs are present.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        aggregator.OnKeyItemsRemoved(new List<uint> { 9001u, 9002u, 9003u });

        // Assert
        var removed = aggregator.Current.KeyItemsRemoved;
        Assert.NotNull(removed);
        Assert.Equal(3, removed.Count);
        Assert.Contains(9001u, removed);
        Assert.Contains(9002u, removed);
        Assert.Contains(9003u, removed);
    }

    // =========================================================================
    // B6d — Independence: OnKeyItemsChanged + OnKeyItemsRemoved do not alias
    // =========================================================================

    [Fact]
    public void SnapshotAggregator_OnKeyItemsChanged_And_OnKeyItemsRemoved_AreIndependent()
    {
        /*
         * RED: Will fail until Builder adds OnKeyItemsRemoved with a separate backing field.
         *
         * CONTRACT: Given OnKeyItemsChanged([1001]) then OnKeyItemsRemoved([9001]),
         *           When Current is read,
         *           Then Current.KeyItemsAdded   == [1001]   (not affected by OnKeyItemsRemoved)
         *                AND Current.KeyItemsRemoved == [9001]  (not affected by OnKeyItemsChanged).
         *
         * BUILDER GUIDANCE: The two methods must use separate backing fields (_keyItemsAdded vs
         *   _keyItemsRemoved). They must not share state. This test catches a common mistake of
         *   aliasing both operations to the same field.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        aggregator.OnKeyItemsChanged(new List<uint> { 1001u });
        aggregator.OnKeyItemsRemoved(new List<uint> { 9001u });

        // Assert — Added and Removed are independent
        var snapshot = aggregator.Current;

        Assert.NotNull(snapshot.KeyItemsAdded);
        var addedSingle = Assert.Single(snapshot.KeyItemsAdded);
        Assert.Equal(1001u, addedSingle);

        Assert.NotNull(snapshot.KeyItemsRemoved);
        var removedSingle = Assert.Single(snapshot.KeyItemsRemoved);
        Assert.Equal(9001u, removedSingle);
    }

    // =========================================================================
    // B7 — Snapshot via positional ctor only → KeyItemsRemoved == null
    // =========================================================================

    [Fact]
    public void GameStateSnapshot_KeyItemsRemoved_IsNullByDefault()
    {
        /*
         * RED: Will fail until Builder adds KeyItemsRemoved property to GameStateSnapshot.
         *
         * CONTRACT: A snapshot constructed without the KeyItemsRemoved init property
         *           leaves KeyItemsRemoved == null.
         *
         * BUILDER GUIDANCE: Add KeyItemsRemoved property as a non-positional init property
         *   (same pattern as KeyItemsAdded — appended after the record's positional parameters
         *   to avoid churn at existing constructor call sites).
         */

        // Arrange
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

        // Assert — no KeyItemsRemoved specified → null
        Assert.Null(snapshot.KeyItemsRemoved); // RED: property does not exist yet
    }

    // =========================================================================
    // B7b — Snapshot with explicit KeyItemsRemoved init → value preserved
    // =========================================================================

    [Fact]
    public void GameStateSnapshot_KeyItemsRemoved_CanBeSetViaObjectInitializer()
    {
        /*
         * RED: Will fail until Builder adds KeyItemsRemoved property to GameStateSnapshot.
         *
         * CONTRACT: The init property can be set via object-initializer syntax.
         *           KeyItemsRemoved[0] == 9001u after construction.
         */

        // Arrange
        var items = new List<uint> { 9001u };
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
        { KeyItemsRemoved = items }; // RED: property does not exist yet

        // Assert
        Assert.NotNull(snapshot.KeyItemsRemoved);
        Assert.Equal(9001u, snapshot.KeyItemsRemoved[0]);
    }
}
