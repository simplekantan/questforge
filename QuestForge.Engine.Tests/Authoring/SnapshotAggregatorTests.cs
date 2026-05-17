using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for SnapshotAggregator — scenarios 41-45 from PHASE_9_PLAN.md §5.5.
/// All tests will fail until Builder implements SnapshotAggregator.
/// </summary>
public sealed class SnapshotAggregatorTests
{
    private static readonly QuestId Quest2054 = new(2054);
    private static readonly QuestId OtherQuest = new(9999);

    // =========================================================================
    // Scenario 41 — SnapshotAggregator_OnZoneChanged_UpdatesZoneAndPosition
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnZoneChanged_UpdatesZoneAndPosition()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator initialized at zone 100,
         *           When OnZoneChanged(zone=200, pos=(50,0,50)) is called,
         *           Then Current.Zone == 200 and Current.Position == (50,0,50)
         *
         * BUILDER GUIDANCE: OnZoneChanged updates both Zone and Position fields in Current.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnZoneChanged(new ZoneId(200), new WorldPosition(50, 0, 50));

        // Assert
        Assert.Equal(new ZoneId(200), aggregator.Current.Zone);
        Assert.Equal(new WorldPosition(50, 0, 50), aggregator.Current.Position);
    }

    // =========================================================================
    // Scenario 42 — SnapshotAggregator_OnQuestSequenceChanged_UpdatesSequence
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnQuestSequenceChanged_UpdatesSequence()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator with QuestId=2054, QuestSequence=0,
         *           When OnQuestSequenceChanged(2054, 2) is called,
         *           Then Current.QuestSequence == 2
         *
         * BUILDER GUIDANCE: OnQuestSequenceChanged checks quest matches activeQuest, then updates.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnQuestSequenceChanged(Quest2054, 2);

        // Assert
        Assert.Equal(2, aggregator.Current.QuestSequence);
    }

    // =========================================================================
    // Scenario 43 — SnapshotAggregator_OnQuestSequenceChanged_Ignored_WhenDifferentQuest
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnQuestSequenceChanged_Ignored_WhenDifferentQuest()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator authoring quest 2054 with QuestSequence=0,
         *           When OnQuestSequenceChanged(9999, 5) is called (different quest),
         *           Then Current.QuestSequence remains 0 (unchanged)
         *
         * BUILDER GUIDANCE: Silently ignore sequence/flag changes for non-active quests.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act — send event for a different quest
        aggregator.OnQuestSequenceChanged(OtherQuest, 5);

        // Assert
        Assert.Equal(0, aggregator.Current.QuestSequence);
    }

    // =========================================================================
    // Scenario 44 — SnapshotAggregator_OnDialogueChoice_UpdatesLastPromptAndAnswer
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnDialogueChoice_UpdatesLastPromptAndAnswer()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given empty aggregator,
         *           When OnDialogueChoice("TEXT_Q1", "TEXT_A1") is called,
         *           Then Current.LastDialoguePrompt == "TEXT_Q1"
         *                and Current.LastDialogueAnswer == "TEXT_A1"
         *
         * BUILDER GUIDANCE: OnDialogueChoice updates both prompt and answer fields.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnDialogueChoice("TEXT_Q1", "TEXT_A1");

        // Assert
        Assert.Equal("TEXT_Q1", aggregator.Current.LastDialoguePrompt);
        Assert.Equal("TEXT_A1", aggregator.Current.LastDialogueAnswer);
    }

    // =========================================================================
    // Phase 11B — SnapshotAggregator attunement tests (B1-B3 from §3.6)
    // =========================================================================

    [Fact]
    public void SnapshotAggregator_FreshInstance_LastAttuned_IsNull()
    {
        /*
         * RED: Will fail until Builder implements OnAttunementChanged
         *      (the _lastAttuned field must be initialized to null and included in Current).
         *
         * CONTRACT: Given a fresh SnapshotAggregator,
         *           When Current is read,
         *           Then LastAttuned == null.
         *
         * BUILDER GUIDANCE: _lastAttuned = null is the field initializer (already added as a stub).
         *   Current must include LastAttuned: _lastAttuned.
         *   This test will PASS once the stub compiles correctly — but it's included to document
         *   the contract and guard against regressions.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        var snapshot = aggregator.Current;

        // Assert
        Assert.Null(snapshot.LastAttuned);
    }

    [Fact]
    public void SnapshotAggregator_OnAttunementChanged_SetsLastAttuned()
    {
        /*
         * RED: Will fail until Builder implements OnAttunementChanged.
         *
         * CONTRACT: Given aggregator.OnAttunementChanged(new AetheryteId(500)),
         *           When Current is read,
         *           Then LastAttuned == new AetheryteId(500).
         *
         * BUILDER GUIDANCE: OnAttunementChanged sets _lastAttuned = aetheryte.
         *   The NotImplementedException stub must be replaced with: _lastAttuned = aetheryte;
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnAttunementChanged(new AetheryteId(500));

        // Assert
        Assert.Equal(new AetheryteId(500), aggregator.Current.LastAttuned);
    }

    [Fact]
    public void SnapshotAggregator_OnAttunementChanged_Overwrite_TracksLatest()
    {
        /*
         * RED: Will fail until Builder implements OnAttunementChanged.
         *
         * CONTRACT: Given OnAttunementChanged(AetheryteId(500)) then OnAttunementChanged(AetheryteId(501)),
         *           When Current is read,
         *           Then LastAttuned == new AetheryteId(501).
         *           (Aggregator tracks most-recent attunement event — same semantics as LastNpcInteracted.)
         *
         * BUILDER GUIDANCE: _lastAttuned is overwritten on each call; no history is retained.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnAttunementChanged(new AetheryteId(500));
        aggregator.OnAttunementChanged(new AetheryteId(501));

        // Assert
        Assert.Equal(new AetheryteId(501), aggregator.Current.LastAttuned);
    }

    // =========================================================================
    // Phase 12 — SnapshotAggregator key-item inventory snapshot tests
    // =========================================================================

    [Fact]
    public void OnKeyItemsSnapshot_PopulatesKeyItemsAndHash()
    {
        /*
         * RED: Will fail until Builder implements OnKeyItemsSnapshot.
         *
         * CONTRACT: Given aggregator with no key-item state,
         *           When OnKeyItemsSnapshot({ 2000100 → 1 }, hash=0xABCDu) is called,
         *           Then Current.KeyItems contains { 2000100 → 1 }
         *                and Current.InventoryHash == 0xABCDu.
         *
         * BUILDER GUIDANCE: OnKeyItemsSnapshot stores both the full map and the pre-computed hash.
         *   Current.KeyItems is a new IReadOnlyDictionary<uint,int>? init property.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);
        var map = new Dictionary<uint, int> { [2000100u] = 1 };

        // Act
        aggregator.OnKeyItemsSnapshot(map, 0xABCDu);

        // Assert
        Assert.NotNull(aggregator.Current.KeyItems);
        Assert.True(aggregator.Current.KeyItems!.ContainsKey(2000100u));
        Assert.Equal(1, aggregator.Current.KeyItems![2000100u]);
        Assert.Equal(0xABCDu, aggregator.Current.InventoryHash);
    }

    [Fact]
    public void OnKeyItemsSnapshot_EmptyMap_ClearsKeyItemsButSetsHash()
    {
        /*
         * RED: Will fail until Builder implements OnKeyItemsSnapshot.
         *
         * CONTRACT: Given aggregator after a previous OnKeyItemsSnapshot call,
         *           When OnKeyItemsSnapshot({}, OffsetBasis) is called with an empty map,
         *           Then Current.KeyItems == null (or empty; no entries remain)
         *                and Current.InventoryHash == OffsetBasis (0x811C9DC5).
         *
         * BUILDER GUIDANCE: An empty map means no key items are held. Store null (or empty dict).
         *   The hash for an empty map equals KeyItemHasher.OffsetBasis.
         */

        // Arrange
        const uint OffsetBasis = 0x811C9DC5u;
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);
        aggregator.OnKeyItemsSnapshot(new Dictionary<uint, int> { [2000100u] = 1 }, 0xABCDu);

        // Act — replace with empty
        aggregator.OnKeyItemsSnapshot(new Dictionary<uint, int>(), OffsetBasis);

        // Assert
        // KeyItems null signals "no key items in authoring mode snapshot"
        Assert.Null(aggregator.Current.KeyItems);
        Assert.Equal(OffsetBasis, aggregator.Current.InventoryHash);
    }

    [Fact]
    public void OnKeyItemsSnapshot_DoesNotAffectDeltaLists()
    {
        /*
         * RED: Will fail until Builder implements OnKeyItemsSnapshot.
         *
         * CONTRACT: Given aggregator with KeyItemsAdded=[1001u] from a prior OnKeyItemsChanged call,
         *           When OnKeyItemsSnapshot is called,
         *           Then Current.KeyItemsAdded remains unchanged (still [1001u]).
         *
         * BUILDER GUIDANCE: OnKeyItemsSnapshot updates the snapshot map and hash only.
         *   It must NOT touch _keyItemsAdded or _keyItemsRemoved.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);
        aggregator.OnKeyItemsChanged(new List<uint> { 1001u });

        // Act
        aggregator.OnKeyItemsSnapshot(new Dictionary<uint, int> { [2000100u] = 1 }, 0x1234u);

        // Assert — delta list must be intact
        Assert.NotNull(aggregator.Current.KeyItemsAdded);
        var item = Assert.Single(aggregator.Current.KeyItemsAdded!);
        Assert.Equal(1001u, item);
    }

    [Fact]
    public void ResetDeltas_DoesNotClearKeyItems()
    {
        /*
         * RED: Will fail until Builder implements OnKeyItemsSnapshot
         *      and confirms ResetDeltas does NOT touch the KeyItems map.
         *
         * CONTRACT: Given aggregator after OnKeyItemsSnapshot({ 2000100 → 1 }, hash),
         *           When ResetDeltas() is called,
         *           Then Current.KeyItems is still non-null with the same entry.
         *
         * BUILDER GUIDANCE: ResetDeltas clears _keyItemsAdded and _keyItemsRemoved only.
         *   The _keyItemsMap (full snapshot) persists so the next poll can diff against it.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);
        aggregator.OnKeyItemsSnapshot(new Dictionary<uint, int> { [2000100u] = 1 }, 0xABCDu);

        // Act
        aggregator.ResetDeltas();

        // Assert
        Assert.NotNull(aggregator.Current.KeyItems);
        Assert.Equal(1, aggregator.Current.KeyItems![2000100u]);
    }

    [Fact]
    public void OnInventoryHashChanged_UpdatesHashOnly_LeavesKeyItemsNull()
    {
        /*
         * RED: Will fail until Builder implements OnInventoryHashChanged.
         *
         * CONTRACT: Given a fresh aggregator (no OnKeyItemsSnapshot called),
         *           When OnInventoryHashChanged(0x9999u) is called,
         *           Then Current.InventoryHash == 0x9999u
         *                and Current.KeyItems == null (production mode — hash-only).
         *
         * BUILDER GUIDANCE: OnInventoryHashChanged is the production-mode path; it only updates
         *   _inventoryHash. KeyItems remains null because the full map is never populated
         *   outside authoring mode.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnInventoryHashChanged(0x9999u);

        // Assert
        Assert.Equal(0x9999u, aggregator.Current.InventoryHash);
        Assert.Null(aggregator.Current.KeyItems);
    }

    // =========================================================================
    // Scenario 45 — SnapshotAggregator_OnQuestAccepted_SetsAcceptedFlag
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnQuestAccepted_SetsAcceptedFlag()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator with QuestAccepted=false,
         *           When OnQuestAccepted(2054) is called,
         *           Then Current.QuestAccepted == true
         *
         * BUILDER GUIDANCE: OnQuestAccepted sets QuestAccepted = true when questId matches activeQuest.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnQuestAccepted(Quest2054);

        // Assert
        Assert.True(aggregator.Current.QuestAccepted);
    }
}
