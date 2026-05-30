using QuestForge.Adapters.Items;

// WHY: Schema.ItemKind is imported with full qualification to keep it readable alongside
// the mapper under test. No collision with other types in this file.

namespace QuestForge.Adapters.Tests.Items;

/// <summary>
/// RED PHASE: Tests for ItemKindMapper.FromFFXIVActionType (spec UI-INF-2).
/// ALL tests in this file will fail to compile until Builder adds:
///   - QuestForge.Adapters/Items/ItemKindMapper.cs  (Task UI-INF-T1)
///
/// Run with: dotnet test QuestForge.Adapters.Tests --filter "FullyQualifiedName~ItemKindMapperTests"
/// </summary>
public sealed class ItemKindMapperTests
{
    // =========================================================================
    // UI-M1: uint 2 (FFXIVClientStructs Item) -> ItemKind.InventoryItem
    // =========================================================================

    [Fact]
    public void FromFFXIVActionType_ItemValue2_MapsToInventoryItem_UIM1()
    {
        // Given: uint 2 (FFXIVClientStructs "Item" -- inventory items: potions, food, throwables)
        // When:  ItemKindMapper.FromFFXIVActionType(2u)
        // Then:  ItemKind.InventoryItem

        var result = ItemKindMapper.FromFFXIVActionType(2u);

        Assert.Equal(QuestForge.Schema.ItemKind.InventoryItem, result);
    }

    // =========================================================================
    // UI-M2: uint 3 (FFXIVClientStructs EventItem) -> ItemKind.KeyItem
    // =========================================================================

    [Fact]
    public void FromFFXIVActionType_EventItemValue3_MapsToKeyItem_UIM2()
    {
        // Given: uint 3 (FFXIVClientStructs "EventItem" -- quest key items used as actions)
        // When:  ItemKindMapper.FromFFXIVActionType(3u)
        // Then:  ItemKind.KeyItem

        var result = ItemKindMapper.FromFFXIVActionType(3u);

        Assert.Equal(QuestForge.Schema.ItemKind.KeyItem, result);
    }

    // =========================================================================
    // UI-M3: Theory -- non-item ActionTypes -> null
    //        Pins that ItemKindMapper only handles values 2 and 3.
    // =========================================================================

    [Theory]
    [InlineData(0u)]   // None
    [InlineData(1u)]   // Action (routed by ActionTypeMapper, not ItemKindMapper)
    [InlineData(4u)]   // EventAction
    [InlineData(5u)]   // GeneralAction (routed by ActionTypeMapper)
    [InlineData(6u)]   // BuddyAction
    [InlineData(7u)]   // MainCommand
    [InlineData(13u)]  // Mount
    [InlineData(99u)]  // bogus / future
    public void FromFFXIVActionType_NonItemValue_ReturnsNull_UIM3(uint ffxivType)
    {
        // Given: any wire value that is NOT 2 (Item) or 3 (EventItem)
        // When:  the mapper is called
        // Then:  null is returned -- this is not an item type

        Assert.Null(ItemKindMapper.FromFFXIVActionType(ffxivType));
    }

    // =========================================================================
    // UI-M4: ActionTypeMapper.FromFFXIVActionType(3u) now returns null
    //        Pins Decision UI-INF-13: EventItem=3 is REMOVED from ActionTypeMapper.
    //        Item routing is now exclusively handled by ItemKindMapper.
    //
    //        This test replaces the existing UAI-M3 which expected ActionType.KeyItem.
    //        The tester writes the NEW expectation here; the builder updates
    //        ActionTypeMapper to match.
    // =========================================================================

    [Fact]
    public void ActionTypeMapper_EventItemValue3_NowReturnsNull_UIM4()
    {
        // Given: uint 3 (FFXIVClientStructs EventItem)
        // When:  ActionTypeMapper.FromFFXIVActionType(3u) -- previously returned ActionType.KeyItem
        // Then:  null -- EventItem routing is now handled by ItemKindMapper (Decision UI-INF-13)
        //
        // BREAKING CHANGE: This intentionally contradicts the existing UAI-M3 test. The builder
        // must update ActionTypeMapper to remove the 3u arm AND update UAI-M3 to match.

        var result = QuestForge.Adapters.Actions.ActionTypeMapper.FromFFXIVActionType(3u);

        Assert.Null(result);
    }
}
