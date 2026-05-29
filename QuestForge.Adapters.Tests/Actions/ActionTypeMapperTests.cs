using QuestForge.Adapters.Actions;

// WHY: Schema.ActionType is imported with full qualification to keep it readable alongside
// the mapper under test. No collision with other types in this file.

namespace QuestForge.Adapters.Tests.Actions;

/// <summary>
/// RED PHASE: Tests for ActionTypeMapper.FromFFXIVActionType (spec UAI2).
/// ALL tests in this file will fail to compile until Builder adds:
///   - QuestForge.Adapters/Actions/ActionTypeMapper.cs  (Task UAI-T1)
///
/// Run with: dotnet test QuestForge.Adapters.Tests --filter "FullyQualifiedName~ActionTypeMapperTests"
/// </summary>
public sealed class ActionTypeMapperTests
{
    // =========================================================================
    // UAI-MAP-1: uint 1 (FFXIVClientStructs Action) → Schema.ActionType.Action
    // =========================================================================

    [Fact]
    public void FromFFXIVActionType_Action_MapsToAction()
    {
        // Given: the FFXIVClientStructs wire value for "Action" (combat ability / weaponskill / spell)
        // When:  the mapper converts it
        // Then:  Schema.ActionType.Action is returned

        var result = ActionTypeMapper.FromFFXIVActionType(1u);

        Assert.Equal(QuestForge.Schema.ActionType.Action, result);
    }

    // =========================================================================
    // UAI-MAP-2: uint 5 (FFXIVClientStructs GeneralAction) → Schema.ActionType.GeneralAction
    // =========================================================================

    [Fact]
    public void FromFFXIVActionType_GeneralAction_MapsToGeneralAction()
    {
        // Given: the FFXIVClientStructs wire value for "GeneralAction" (mount, sprint, teleport, return)
        // When:  the mapper converts it
        // Then:  Schema.ActionType.GeneralAction is returned

        var result = ActionTypeMapper.FromFFXIVActionType(5u);

        Assert.Equal(QuestForge.Schema.ActionType.GeneralAction, result);
    }

    // =========================================================================
    // UAI-MAP-3: uint 3 (FFXIVClientStructs "EventItem") → Schema.ActionType.KeyItem
    //            Pins the EventItem→KeyItem rename from Decision DAD-4.
    // =========================================================================

    [Fact]
    public void FromFFXIVActionType_EventItemValue3_MapsToKeyItem()
    {
        // Given: uint 3, which is "EventItem" in FFXIVClientStructs naming but
        //        "KeyItem" in the QuestForge schema (Decision DAD-4 rename).
        // When:  the mapper converts it
        // Then:  Schema.ActionType.KeyItem is returned — the rename is visible here.
        //
        // CRITICAL: This test pins the round-trip through the rename. If someone changes
        //   the mapping to 3u → ActionType.Action (or any other value), this test catches it.

        var result = ActionTypeMapper.FromFFXIVActionType(3u);

        Assert.Equal(QuestForge.Schema.ActionType.KeyItem, result);
    }

    // =========================================================================
    // UAI-MAP-4: uint 2 (FFXIVClientStructs Item) → null
    //            Items are handled by UseItemStep, not UseActionStep.
    // =========================================================================

    [Fact]
    public void FromFFXIVActionType_Item_ReturnsNull_NotMappedToUseActionStep()
    {
        // Given: uint 2, which is "Item" (inventory item use) in FFXIVClientStructs.
        //        Per Decision UA8 in USE_ACTION_STEP_PLAN, inventory items are UseItemStep's domain.
        // When:  the mapper is called
        // Then:  null is returned (poller skips the observation)

        var result = ActionTypeMapper.FromFFXIVActionType(2u);

        Assert.Null(result);
    }

    // =========================================================================
    // UAI-MAP-5: Theory — all unsupported wire values → null
    //            Pins the defensive "skip unknown" contract of the mapper.
    // =========================================================================

    [Theory]
    [InlineData(0u)]   // None             — no action
    [InlineData(4u)]   // EventAction      — unreachable in normal play
    [InlineData(6u)]   // BuddyAction      — chocobo companion
    [InlineData(7u)]   // MainCommand      — UI command
    [InlineData(8u)]   // Companion        — minion
    [InlineData(9u)]   // CraftAction      — crafting ability
    [InlineData(10u)]  // Unk10
    [InlineData(11u)]  // PetAction        — pet
    [InlineData(13u)]  // Mount            — mount action (separate path)
    [InlineData(14u)]  // PvPAction        — not authorable v1
    [InlineData(15u)]  // FieldMarker
    [InlineData(16u)]  // ChocoboRaceAbility
    [InlineData(19u)]  // BgcArmyAction
    [InlineData(20u)]  // Ornament
    [InlineData(99u)]  // bogus / future value — defensive
    public void FromFFXIVActionType_UnsupportedValue_ReturnsNull(uint ffxivType)
    {
        // Given: any wire value that is NOT 1 (Action), 3 (EventItem/KeyItem), or 5 (GeneralAction)
        // When:  the mapper is called
        // Then:  null is returned — poller must skip observation without crashing

        Assert.Null(ActionTypeMapper.FromFFXIVActionType(ffxivType));
    }
}
