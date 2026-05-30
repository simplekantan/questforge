namespace QuestForge.Adapters.Items;

using QuestForge.Schema;

public static class ItemKindMapper
{
    public static ItemKind? FromFFXIVActionType(uint ffxivActionType) => ffxivActionType switch
    {
        2u => ItemKind.InventoryItem,  // FFXIVClientStructs "Item"
        3u => ItemKind.KeyItem,        // FFXIVClientStructs "EventItem"
        _  => null,
    };
}
