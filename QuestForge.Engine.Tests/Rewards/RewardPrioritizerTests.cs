using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Rewards;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Rewards;

/// <summary>
/// Tests for RewardPrioritizer.SelectBestReward and ApplyOverride.
/// Spec: docs/REWARD_SELECTION_PLAN.md -- scenarios R1-R33.
///
/// All tests call the static methods directly. The ilvl lookup delegate is
/// provided as an inline lambda returning Task.FromResult(Result.Ok(value))
/// or Task.FromResult(Result.Fail&lt;int&gt;("unavailable")).
/// </summary>
public sealed class RewardPrioritizerTests
{
    // ---- helpers ----

    private static readonly JobId Gla = new(1);
    private static readonly JobId Blm = new(19);
    private static readonly JobId AnyJob = new(99);

    private static QuestReward Reward(
        int index,
        uint itemId = 0,
        int quantity = 1,
        int itemLevel = 0,
        long vendorPrice = 0,
        IReadOnlyList<JobId>? restrictedJobs = null,
        bool isUntradable = false,
        uint equipSlotCategory = 0,
        uint itemActionId = 0,
        uint itemUiCategoryId = 0)
        => new(
            index,
            new ItemId(itemId),
            quantity,
            itemLevel,
            vendorPrice,
            restrictedJobs ?? Array.Empty<JobId>(),
            isUntradable,
            equipSlotCategory,
            itemActionId,
            itemUiCategoryId);

    /// <summary>
    /// Build a slot-level ilvl lookup from a dictionary of slotIndex -> ilvl.
    /// Slots not in the dictionary return Result.Ok(0).
    /// </summary>
    private static Func<int, Task<Result<int>>> IlvlLookup(Dictionary<int, int> equipped)
        => slot => Task.FromResult<Result<int>>(
            equipped.TryGetValue(slot, out var ilvl)
                ? Result.Ok(ilvl)
                : Result.Ok(0));

    /// <summary>
    /// Build an ilvl lookup that always fails.
    /// </summary>
    private static Func<int, Task<Result<int>>> FailingIlvlLookup()
        => _ => Task.FromResult<Result<int>>(Result.Fail<int>("unavailable"));

    /// <summary>
    /// Build an ilvl lookup that fails for specific slots and succeeds for others.
    /// </summary>
    private static Func<int, Task<Result<int>>> MixedIlvlLookup(
        Dictionary<int, int> successes,
        HashSet<int> failures)
        => slot =>
        {
            if (failures.Contains(slot))
                return Task.FromResult<Result<int>>(Result.Fail<int>("unavailable"));
            return Task.FromResult<Result<int>>(
                successes.TryGetValue(slot, out var ilvl)
                    ? Result.Ok(ilvl)
                    : Result.Ok(0));
        };

    private static Func<int, Task<Result<int>>> NoOpIlvlLookup()
        => _ => Task.FromResult<Result<int>>(Result.Ok(0));

    // =========================================================================
    // R1 -- Empty rewards returns null
    // =========================================================================

    [Fact]
    public async Task R01_EmptyRewards_ReturnsNull()
    {
        var result = await RewardPrioritizer.SelectBestReward(
            Array.Empty<QuestReward>(),
            RewardPrioritizer.DefaultPriorityOrder,
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Null(result);
    }

    // =========================================================================
    // R2 -- Single reward always selected
    // =========================================================================

    [Fact]
    public async Task R02_SingleReward_AlwaysSelected()
    {
        var rewards = new[] { Reward(0, vendorPrice: 10) };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            RewardPrioritizer.DefaultPriorityOrder,
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R3 -- BiggestUpgrade selects largest positive ilvl delta
    // =========================================================================

    [Fact]
    public async Task R03_BiggestUpgrade_SelectsLargestPositiveIlvlDelta()
    {
        // Index 0: MainHand (EquipSlotCategory 1 -> slot 0), ilvl 30, equipped 25, delta = 5
        // Index 1: Head (EquipSlotCategory 3 -> slot 2), ilvl 50, equipped 20, delta = 30
        var rewards = new[]
        {
            Reward(0, itemLevel: 30, equipSlotCategory: 1, restrictedJobs: [Gla]),
            Reward(1, itemLevel: 50, equipSlotCategory: 3, restrictedJobs: [Gla])
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade],
            Gla,
            IlvlLookup(new Dictionary<int, int> { [0] = 25, [2] = 20 }),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R4 -- BiggestUpgrade with no positive delta falls through
    // =========================================================================

    [Fact]
    public async Task R04_BiggestUpgrade_NoPositiveDelta_FallsThrough()
    {
        // ilvl 10, equipped 50 -> delta = -40 (no upgrade)
        var rewards = new[]
        {
            Reward(0, itemLevel: 10, equipSlotCategory: 1, restrictedJobs: [Gla], vendorPrice: 100)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade, RewardPriority.HighestGilValue],
            Gla,
            IlvlLookup(new Dictionary<int, int> { [0] = 50 }),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R5 -- BiggestUpgrade skips rewards not equippable by current job
    // =========================================================================

    [Fact]
    public async Task R05_BiggestUpgrade_SkipsWrongJob()
    {
        var rewards = new[]
        {
            Reward(0, itemLevel: 80, equipSlotCategory: 1, restrictedJobs: [Blm]),  // BLM only
            Reward(1, itemLevel: 30, equipSlotCategory: 3, restrictedJobs: [Gla])   // GLA
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade],
            Gla,
            IlvlLookup(new Dictionary<int, int> { [0] = 10, [2] = 10 }),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R6 -- BiggestUpgrade skips non-equipment (EquipSlotCategory 0)
    // =========================================================================

    [Fact]
    public async Task R06_BiggestUpgrade_SkipsNonEquipment()
    {
        var rewards = new[]
        {
            Reward(0, itemLevel: 0, equipSlotCategory: 0, vendorPrice: 100),
            Reward(1, itemLevel: 30, equipSlotCategory: 1, restrictedJobs: [Gla])
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade],
            Gla,
            IlvlLookup(new Dictionary<int, int> { [0] = 10 }),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R7 -- HighestGilValue selects highest VendorPrice * Quantity
    // =========================================================================

    [Fact]
    public async Task R07_HighestGilValue_SelectsHighestTotal()
    {
        var rewards = new[]
        {
            Reward(0, vendorPrice: 50, quantity: 3),   // 150
            Reward(1, vendorPrice: 200, quantity: 1),  // 200
            Reward(2, vendorPrice: 100, quantity: 1)   // 100
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.HighestGilValue],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R8 -- HighestGilValue tie-break by lowest index
    // =========================================================================

    [Fact]
    public async Task R08_HighestGilValue_TieBreakByLowestIndex()
    {
        var rewards = new[]
        {
            Reward(0, vendorPrice: 100, quantity: 1),
            Reward(1, vendorPrice: 100, quantity: 1)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.HighestGilValue],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R9 -- GearCoffer selects reward identified by CofferIdentifier.IsCoffer
    // =========================================================================

    [Fact]
    public async Task R09_GearCoffer_SelectsCofferByItemAction()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 1, vendorPrice: 50),
            Reward(1, equipSlotCategory: 0, vendorPrice: 0, itemActionId: 1085, itemUiCategoryId: 61),
            Reward(2, equipSlotCategory: 0, vendorPrice: 200)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GearCoffer],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R10 -- GearCoffer tier produces no winner when no rewards match
    // =========================================================================

    [Fact]
    public async Task R10_GearCoffer_NoMatch_FallsThrough()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 1, vendorPrice: 50),
            Reward(1, equipSlotCategory: 0, vendorPrice: 200)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GearCoffer, RewardPriority.AnythingElse],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R11 -- GilSack selects non-equipment tradable vendor-sellable reward
    // =========================================================================

    [Fact]
    public async Task R11_GilSack_SelectsNonEquipmentTradableVendorSellable()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 1, vendorPrice: 50),                                          // equipment
            Reward(1, equipSlotCategory: 0, vendorPrice: 0, itemActionId: 1085, itemUiCategoryId: 61), // gear coffer
            Reward(2, equipSlotCategory: 0, vendorPrice: 200)                                          // Allagan Piece
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GilSack],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(2, result);
    }

    // =========================================================================
    // R12 -- GilSack rejects untradable non-equipment
    // =========================================================================

    [Fact]
    public async Task R12_GilSack_RejectsUntradable()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 100, isUntradable: true),
            Reward(1, equipSlotCategory: 3, restrictedJobs: [Gla])
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GilSack, RewardPriority.AnythingElse],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        // GilSack falls through (index 0 is untradable); AnythingElse catches index 0
        Assert.Equal(0, result);
    }

    // =========================================================================
    // R13a -- GearCoffer vs GilSack: user prefers gil over coffers
    // =========================================================================

    [Fact]
    public async Task R13a_GilSackBeforeGearCoffer_SelectsGilSack()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 0, itemActionId: 1085, itemUiCategoryId: 61),
            Reward(1, equipSlotCategory: 0, vendorPrice: 500)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GilSack, RewardPriority.GearCoffer],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R13b -- GearCoffer vs GilSack: user prefers coffers over gil
    // =========================================================================

    [Fact]
    public async Task R13b_GearCofferBeforeGilSack_SelectsGearCoffer()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 0, itemActionId: 1085, itemUiCategoryId: 61),
            Reward(1, equipSlotCategory: 0, vendorPrice: 500)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GearCoffer, RewardPriority.GilSack],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R14 -- EquippableGear selects first reward matching current job
    // =========================================================================

    [Fact]
    public async Task R14_EquippableGear_SelectsFirstMatchingJob()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 1, restrictedJobs: [Blm]),   // BLM
            Reward(1, equipSlotCategory: 3, restrictedJobs: [Gla]),   // GLA
            Reward(2, equipSlotCategory: 5, restrictedJobs: [])       // all jobs
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.EquippableGear],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R15 -- EquippableGear skips non-equipment rewards
    // =========================================================================

    [Fact]
    public async Task R15_EquippableGear_SkipsNonEquipment()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 100),
            Reward(1, equipSlotCategory: 3, restrictedJobs: [Gla])
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.EquippableGear],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R16 -- UnequippableGear selects gear not matching current job
    // =========================================================================

    [Fact]
    public async Task R16_UnequippableGear_SelectsGearNotMatchingJob()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 1, restrictedJobs: [Gla]),   // matches
            Reward(1, equipSlotCategory: 3, restrictedJobs: [Blm])    // doesn't match
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.UnequippableGear],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R17 -- AnythingElse always returns index 0
    // =========================================================================

    [Fact]
    public async Task R17_AnythingElse_ReturnsIndex0()
    {
        var rewards = new[]
        {
            Reward(0), Reward(1), Reward(2)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.AnythingElse],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R18 -- Custom priority ordering: higher tier wins
    // =========================================================================

    [Fact]
    public async Task R18_CustomOrder_EquippableGearFirst_SelectsGear()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 500),                                 // GilSack
            Reward(1, equipSlotCategory: 3, restrictedJobs: [Gla], itemLevel: 50)              // equippable gear
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.EquippableGear, RewardPriority.GilSack, RewardPriority.BiggestUpgrade],
            Gla,
            IlvlLookup(new Dictionary<int, int> { [2] = 10 }),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R19 -- Custom priority ordering: reversed from default
    // =========================================================================

    [Fact]
    public async Task R19_CustomOrder_GilSackFirst_SelectsGilSack()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 500),
            Reward(1, equipSlotCategory: 3, restrictedJobs: [Gla], itemLevel: 50)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GilSack, RewardPriority.EquippableGear],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R20 -- RewardOverride with SpecificItem
    // =========================================================================

    [Fact]
    public async Task R20_ApplyOverride_SpecificItem_FindsMatch()
    {
        var rewards = new[]
        {
            Reward(0, itemId: 100),
            Reward(1, itemId: 200),
            Reward(2, itemId: 300)
        };

        var result = await RewardPrioritizer.ApplyOverride(
            rewards,
            new RewardOverride("specificItem", ItemId: 200),
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R21 -- RewardOverride with SpecificItem not found
    // =========================================================================

    [Fact]
    public async Task R21_ApplyOverride_SpecificItem_NotFound_ReturnsNull()
    {
        var rewards = new[] { Reward(0, itemId: 100) };

        var result = await RewardPrioritizer.ApplyOverride(
            rewards,
            new RewardOverride("specificItem", ItemId: 999),
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Null(result);
    }

    // =========================================================================
    // R22 -- RewardOverride with named strategy (highestGilValue)
    // =========================================================================

    [Fact]
    public async Task R22_ApplyOverride_HighestGilValue_SelectsHighest()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 500),
            Reward(1, equipSlotCategory: 3, restrictedJobs: [Gla], itemLevel: 50)
        };

        var result = await RewardPrioritizer.ApplyOverride(
            rewards,
            new RewardOverride("highestGilValue", ItemId: null),
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R23 -- RewardOverride with "gearCoffer" strategy
    // =========================================================================

    [Fact]
    public async Task R23_ApplyOverride_GearCoffer_SelectsCoffer()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 500),
            Reward(1, equipSlotCategory: 0, vendorPrice: 0, itemActionId: 388, itemUiCategoryId: 61)
        };

        var result = await RewardPrioritizer.ApplyOverride(
            rewards,
            new RewardOverride("gearCoffer", ItemId: null),
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R24 -- RewardOverride with "gilSack" strategy
    // =========================================================================

    [Fact]
    public async Task R24_ApplyOverride_GilSack_SelectsAllaganPiece()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 0, itemActionId: 1085, itemUiCategoryId: 61),
            Reward(1, equipSlotCategory: 0, vendorPrice: 300)
        };

        var result = await RewardPrioritizer.ApplyOverride(
            rewards,
            new RewardOverride("gilSack", ItemId: null),
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R25 -- Equipped ilvl lookup failure: BiggestUpgrade skips that reward
    // =========================================================================

    [Fact]
    public async Task R25_BiggestUpgrade_IlvlLookupFailure_SkipsReward()
    {
        var rewards = new[]
        {
            Reward(0, itemLevel: 50, equipSlotCategory: 1, restrictedJobs: [Gla]),
            Reward(1, itemLevel: 30, equipSlotCategory: 3, restrictedJobs: [Gla])
        };

        // Slot 0 (MainHand) fails; slot 2 (Head) returns ilvl 10
        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade],
            Gla,
            MixedIlvlLookup(
                new Dictionary<int, int> { [2] = 10 },
                new HashSet<int> { 0 }),
            CancellationToken.None);

        Assert.Equal(1, result);
    }

    // =========================================================================
    // R26 -- All ilvl lookups fail: BiggestUpgrade falls through
    // =========================================================================

    [Fact]
    public async Task R26_BiggestUpgrade_AllIlvlFails_FallsThrough()
    {
        var rewards = new[]
        {
            Reward(0, itemLevel: 50, equipSlotCategory: 1, restrictedJobs: [Gla])
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade, RewardPriority.AnythingElse],
            Gla,
            FailingIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R27 -- Empty priority list returns null
    // =========================================================================

    [Fact]
    public async Task R27_EmptyPriorityList_ReturnsNull()
    {
        var rewards = new[] { Reward(0) };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            Array.Empty<RewardPriority>(),
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Null(result);
    }

    // =========================================================================
    // R28 -- BiggestUpgrade with unmappable EquipSlotCategory (15)
    // =========================================================================

    [Fact]
    public async Task R28_BiggestUpgrade_UnmappableSlotCategory_FallsThrough()
    {
        var rewards = new[]
        {
            Reward(0, itemLevel: 50, equipSlotCategory: 15, restrictedJobs: [Gla])
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade, RewardPriority.AnythingElse],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R29 -- Mixed equippable/unequippable/coffer/gilsack with full chain
    // =========================================================================

    [Fact]
    public async Task R29_FullPriorityChain_EquippableGearFirst_SelectsGlaBody()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 1, restrictedJobs: [Blm]),                  // UnequippableGear for GLA
            Reward(1, equipSlotCategory: 0, vendorPrice: 100),                       // GilSack
            Reward(2, equipSlotCategory: 5, restrictedJobs: [Gla])                   // EquippableGear for GLA
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.EquippableGear, RewardPriority.GilSack, RewardPriority.UnequippableGear, RewardPriority.AnythingElse],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(2, result);
    }

    // =========================================================================
    // R30 -- RewardOverride with unknown strategy returns null
    // =========================================================================

    [Fact]
    public async Task R30_ApplyOverride_UnknownStrategy_ReturnsNull()
    {
        var rewards = new[] { Reward(0) };

        var result = await RewardPrioritizer.ApplyOverride(
            rewards,
            new RewardOverride("invalidStrategy", null),
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Null(result);
    }

    // =========================================================================
    // R31 -- BiggestUpgrade tie-break by lowest index
    // =========================================================================

    [Fact]
    public async Task R31_BiggestUpgrade_TieBreakByLowestIndex()
    {
        var rewards = new[]
        {
            Reward(0, itemLevel: 30, equipSlotCategory: 1, restrictedJobs: [Gla]),
            Reward(1, itemLevel: 30, equipSlotCategory: 3, restrictedJobs: [Gla])
        };

        // Both slots have equipped ilvl 10 -> delta 20 each
        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.BiggestUpgrade],
            Gla,
            IlvlLookup(new Dictionary<int, int> { [0] = 10, [2] = 10 }),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R32 -- Empty RestrictedJobs means equippable by all jobs
    // =========================================================================

    [Fact]
    public async Task R32_EquippableGear_EmptyRestrictedJobs_EquippableByAll()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 5, restrictedJobs: [])
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.EquippableGear],
            AnyJob,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // R33 -- GearCoffer with alternate action ID 388
    // =========================================================================

    [Fact]
    public async Task R33_GearCoffer_AlternateActionId388()
    {
        var rewards = new[]
        {
            Reward(0, equipSlotCategory: 0, vendorPrice: 0, itemActionId: 388, itemUiCategoryId: 61),
            Reward(1, equipSlotCategory: 0, vendorPrice: 500)
        };

        var result = await RewardPrioritizer.SelectBestReward(
            rewards,
            [RewardPriority.GearCoffer],
            Gla,
            NoOpIlvlLookup(),
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    // =========================================================================
    // DefaultPriorityOrder -- verify expected count and order
    // =========================================================================

    [Fact]
    public void DefaultPriorityOrder_Contains7Tiers()
    {
        Assert.Equal(7, RewardPrioritizer.DefaultPriorityOrder.Count);
        Assert.Equal(RewardPriority.BiggestUpgrade, RewardPrioritizer.DefaultPriorityOrder[0]);
        Assert.Equal(RewardPriority.HighestGilValue, RewardPrioritizer.DefaultPriorityOrder[1]);
        Assert.Equal(RewardPriority.GearCoffer, RewardPrioritizer.DefaultPriorityOrder[2]);
        Assert.Equal(RewardPriority.GilSack, RewardPrioritizer.DefaultPriorityOrder[3]);
        Assert.Equal(RewardPriority.EquippableGear, RewardPrioritizer.DefaultPriorityOrder[4]);
        Assert.Equal(RewardPriority.UnequippableGear, RewardPrioritizer.DefaultPriorityOrder[5]);
        Assert.Equal(RewardPriority.AnythingElse, RewardPrioritizer.DefaultPriorityOrder[6]);
    }
}
