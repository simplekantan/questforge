using QuestForge.Adapters.Gear;
using Xunit;

namespace QuestForge.Adapters.Tests.Gear;

/// <summary>
/// Tests for EquipSlotResolver.GetTargetSlot -- the pure EquipSlotCategory-to-slot-index
/// mapping extracted from DalamudGearEquipper for Dalamud-free unit testing.
///
/// Spec: docs/EQUIP_GEAR_FOR_QUEST_STEP_PLAN.md Decision EG-7.
/// Mapping (from GEAR_RESEARCH.md Section 2.1):
///   Categories 1-11  -> slot indices 0-10
///   Category 12      -> slot 11 (right ring default)
///   Category 13      -> slot 0  (two-hand weapon -> MainHand)
///   Category 17      -> slot 13 (soul crystal)
///   Unknown category -> null
///
/// ALL tests will fail to compile until Builder creates:
///   - QuestForge.Adapters/Gear/EquipSlotResolver.cs with static GetTargetSlot(uint) -> int?
///
/// Run with: dotnet test QuestForge.Adapters.Tests --filter "FullyQualifiedName~EquipSlotResolverTests"
/// </summary>
public sealed class EquipSlotResolverTests
{
    // =========================================================================
    // Standard categories 1-11 map to slot indices 0-10
    // =========================================================================

    [Theory]
    [InlineData(1u, 0)]    // MainHand
    [InlineData(2u, 1)]    // OffHand
    [InlineData(3u, 2)]    // Head
    [InlineData(4u, 3)]    // Body
    [InlineData(5u, 4)]    // Hands
    [InlineData(6u, 5)]    // Waist (legacy, but slot exists)
    [InlineData(7u, 6)]    // Legs
    [InlineData(8u, 7)]    // Feet
    [InlineData(9u, 8)]    // Ear
    [InlineData(10u, 9)]   // Neck
    [InlineData(11u, 10)]  // Wrist
    public void StandardCategories_MapToExpectedSlotIndex(uint equipSlotCategory, int expectedSlot)
    {
        var result = EquipSlotResolver.GetTargetSlot(equipSlotCategory);

        Assert.NotNull(result);
        Assert.Equal(expectedSlot, result!.Value);
    }

    // =========================================================================
    // Category 12 (Ring) maps to slot 11 (right ring default)
    // =========================================================================

    [Fact]
    public void Category12_Ring_MapsToSlot11()
    {
        var result = EquipSlotResolver.GetTargetSlot(12u);

        Assert.NotNull(result);
        Assert.Equal(11, result!.Value);
    }

    // =========================================================================
    // Category 13 (two-hand weapon) maps to slot 0 (MainHand)
    // =========================================================================

    [Fact]
    public void Category13_TwoHandWeapon_MapsToSlot0()
    {
        var result = EquipSlotResolver.GetTargetSlot(13u);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value);
    }

    // =========================================================================
    // Category 17 (soul crystal) maps to slot 13
    // =========================================================================

    [Fact]
    public void Category17_SoulCrystal_MapsToSlot13()
    {
        var result = EquipSlotResolver.GetTargetSlot(17u);

        Assert.NotNull(result);
        Assert.Equal(13, result!.Value);
    }

    // =========================================================================
    // Unknown categories return null
    // =========================================================================

    [Theory]
    [InlineData(0u)]    // zero is not a valid category
    [InlineData(14u)]   // gap between 13 and 17
    [InlineData(15u)]
    [InlineData(16u)]
    [InlineData(18u)]   // above soul crystal
    [InlineData(99u)]   // arbitrary large value
    public void UnknownCategory_ReturnsNull(uint equipSlotCategory)
    {
        var result = EquipSlotResolver.GetTargetSlot(equipSlotCategory);

        Assert.Null(result);
    }
}
