namespace QuestForge.Adapters.Gear;

/// <summary>
/// Maps an item's EquipSlotCategory (Lumina sheet) to a target equipment slot index
/// (0-based index into InventoryType.EquippedItems).
/// Categories 1-11  → slot indices 0-10
/// Category 12      → slot 11 (right ring default)
/// Category 13      → slot 0  (two-hand weapon → MainHand)
/// Category 17      → slot 13 (soul crystal)
/// Anything else    → null (unmappable)
/// </summary>
public static class EquipSlotResolver
{
    public static int? GetTargetSlot(uint equipSlotCategory) => equipSlotCategory switch
    {
        >= 1 and <= 11 => (int)(equipSlotCategory - 1),
        12 => 11,
        13 => 0,
        17 => 13,
        _ => null
    };
}
