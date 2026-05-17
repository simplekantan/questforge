using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for KeyItemPollDiff.Diff — 8 tests covering change detection,
/// delta direction, key-item filtering, slot aggregation, and hash consistency.
/// All tests will fail until Builder implements KeyItemPollDiff.
/// </summary>
public sealed class KeyItemPollDiffTests
{
    // =========================================================================
    // NoChange_ChangedIsFalse
    // =========================================================================

    [Fact]
    public void NoChange_ChangedIsFalse()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff.
         *
         * CONTRACT: Given a previous map and a current snapshot that are identical,
         *           When Diff is called,
         *           Then Changed == false, Gained is empty, Lost is empty.
         *
         * BUILDER GUIDANCE: Compare hashes (or entries) before building delta lists.
         *   When hash matches, skip delta computation and return Changed=false.
         */

        // Arrange
        var previous = new Dictionary<uint, int> { [2000100u] = 1 };
        var currentSlots = new List<(uint id, int qty)> { (2000100u, 1) };

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert
        Assert.False(result.Changed, "Changed should be false when nothing changed");
        Assert.Empty(result.Gained);
        Assert.Empty(result.Lost);
    }

    // =========================================================================
    // NewItem_GainedContainsItem
    // =========================================================================

    [Fact]
    public void NewItem_GainedContainsItem()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff.
         *
         * CONTRACT: Given an empty previous map and current slots containing item 2000100,
         *           When Diff is called,
         *           Then Changed == true, Gained contains a KeyItemDelta with ItemId=2000100
         *           and Qty=1, Lost is empty.
         *
         * BUILDER GUIDANCE: A key in NewMap not present in previous → Gained entry with
         *   Qty = newQty.
         */

        // Arrange
        var previous = new Dictionary<uint, int>();
        var currentSlots = new List<(uint id, int qty)> { (2000100u, 1) };

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert
        Assert.True(result.Changed);
        Assert.Empty(result.Lost);
        var delta = Assert.Single(result.Gained);
        Assert.Equal(2000100u, delta.ItemId);
        Assert.Equal(1, delta.Qty);
    }

    // =========================================================================
    // RemovedItem_LostContainsItem
    // =========================================================================

    [Fact]
    public void RemovedItem_LostContainsItem()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff.
         *
         * CONTRACT: Given a previous map with item 2000456 and empty current slots,
         *           When Diff is called,
         *           Then Changed == true, Lost contains KeyItemDelta with ItemId=2000456
         *           and Qty=1, Gained is empty.
         *
         * BUILDER GUIDANCE: A key in previous not present in NewMap → Lost entry with
         *   Qty = previousQty.
         */

        // Arrange
        var previous = new Dictionary<uint, int> { [2000456u] = 1 };
        var currentSlots = new List<(uint id, int qty)>();

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert
        Assert.True(result.Changed);
        Assert.Empty(result.Gained);
        var delta = Assert.Single(result.Lost);
        Assert.Equal(2000456u, delta.ItemId);
        Assert.Equal(1, delta.Qty);
    }

    // =========================================================================
    // GainAndLossSimultaneous_BothListsPopulated
    // =========================================================================

    [Fact]
    public void GainAndLossSimultaneous_BothListsPopulated()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff.
         *
         * CONTRACT: Given previous map { 2000456 → 1 } and current slots { (2000123, 1) },
         *           When Diff is called,
         *           Then Changed == true, Gained has item 2000123, Lost has item 2000456.
         *
         * BUILDER GUIDANCE: Both lists may be populated in the same call when an exchange occurs.
         */

        // Arrange
        var previous = new Dictionary<uint, int> { [2000456u] = 1 };
        var currentSlots = new List<(uint id, int qty)> { (2000123u, 1) };

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert
        Assert.True(result.Changed);
        var gained = Assert.Single(result.Gained);
        Assert.Equal(2000123u, gained.ItemId);
        var lost = Assert.Single(result.Lost);
        Assert.Equal(2000456u, lost.ItemId);
    }

    // =========================================================================
    // QuantityChange_ReportedAsDelta
    // =========================================================================

    [Fact]
    public void QuantityChange_ReportedAsDelta()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff.
         *
         * CONTRACT: Given previous map { 2000100 → 1 } and current slots with qty=3,
         *           When Diff is called,
         *           Then Changed == true, Gained contains KeyItemDelta with ItemId=2000100,
         *           Qty=2 (the net increase, not the absolute value).
         *
         * BUILDER GUIDANCE: Qty in the delta is the absolute difference (newQty - oldQty) for gains,
         *   (oldQty - newQty) for losses. Both positive.
         */

        // Arrange
        var previous = new Dictionary<uint, int> { [2000100u] = 1 };
        var currentSlots = new List<(uint id, int qty)> { (2000100u, 3) };

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert
        Assert.True(result.Changed);
        Assert.Empty(result.Lost);
        var gained = Assert.Single(result.Gained);
        Assert.Equal(2000100u, gained.ItemId);
        Assert.Equal(2, gained.Qty);
    }

    // =========================================================================
    // SkipsNonKeyItems
    // =========================================================================

    [Fact]
    public void SkipsNonKeyItems()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff with id-range guard.
         *
         * CONTRACT: Given current slots containing item ids below 2_000_000,
         *           When Diff is called,
         *           Then Changed == false and Gained is empty (non-key items are ignored).
         *
         * BUILDER GUIDANCE: Key items have id >= 2_000_000. Filter out anything below that
         *   threshold when building NewMap from currentSlots.
         */

        // Arrange
        var previous = new Dictionary<uint, int>();
        // Item 1000001 is a regular inventory item, NOT a key item
        var currentSlots = new List<(uint id, int qty)> { (1000001u, 5) };

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert
        Assert.False(result.Changed, "Non-key items (id < 2_000_000) must be ignored");
        Assert.Empty(result.Gained);
        Assert.Empty(result.Lost);
    }

    // =========================================================================
    // DuplicateSlots_SummedIntoMap
    // =========================================================================

    [Fact]
    public void DuplicateSlots_SummedIntoMap()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff with slot aggregation.
         *
         * CONTRACT: Given current slots containing the same item id in two separate slots,
         *           When Diff is called,
         *           Then the quantities are summed and treated as a single entry.
         *
         * BUILDER GUIDANCE: FFXIV can represent the same key item across multiple inventory
         *   slots. Sum quantities by id when building NewMap from currentSlots.
         */

        // Arrange
        var previous = new Dictionary<uint, int>();
        // Same item in two slots: total qty = 1 + 2 = 3
        var currentSlots = new List<(uint id, int qty)>
        {
            (2000100u, 1),
            (2000100u, 2),
        };

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert
        Assert.True(result.Changed);
        var gained = Assert.Single(result.Gained);
        Assert.Equal(2000100u, gained.ItemId);
        Assert.Equal(3, gained.Qty);
    }

    // =========================================================================
    // NewHash_MatchesKeyItemHasher
    // =========================================================================

    [Fact]
    public void NewHash_MatchesKeyItemHasher()
    {
        /*
         * RED: Will fail until Builder implements KeyItemPollDiff.Diff and KeyItemHasher.Compute.
         *
         * CONTRACT: Given any previous map and current slots,
         *           When Diff is called,
         *           Then result.NewHash == KeyItemHasher.Compute(result.NewMap).
         *
         * BUILDER GUIDANCE: Diff internally builds NewMap from currentSlots, then calls
         *   KeyItemHasher.Compute(NewMap) and stores the result as NewHash. This test
         *   verifies the two are always consistent.
         */

        // Arrange
        var previous = new Dictionary<uint, int> { [2000100u] = 1 };
        var currentSlots = new List<(uint id, int qty)>
        {
            (2000100u, 1),
            (2000200u, 3),
        };

        // Act
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        // Assert — NewHash must equal independently computed hash of NewMap
        var expectedHash = KeyItemHasher.Compute(result.NewMap);
        Assert.Equal(expectedHash, result.NewHash);
    }
}
