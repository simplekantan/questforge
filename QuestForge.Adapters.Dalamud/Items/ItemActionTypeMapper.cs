using NativeActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using QuestForge.Schema;

namespace QuestForge.Adapters.Dalamud.Items;

// CANONICAL MAPPING (verified against FFXIVClientStructs ActionType enum):
//   ItemKind.InventoryItem → NativeActionType.Item       (value 2; regular inventory consumables)
//   ItemKind.KeyItem       → NativeActionType.EventItem  (value 3; quest key items used as actions —
//                                                        NOTE: ActionType.KeyItem does NOT exist in
//                                                        FFXIVClientStructs; EventItem is the canonical
//                                                        name for the quest-item-as-action bucket,
//                                                        mirroring ActionExecutorLogic Schema.KeyItem→EventItem)
internal static class ItemActionTypeMapper
{
    public static NativeActionType ToNative(ItemKind kind) => kind switch
    {
        ItemKind.InventoryItem => NativeActionType.Item,
        ItemKind.KeyItem       => NativeActionType.EventItem,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(kind), kind,
                 $"ItemKind.{kind} has no FFXIVClientStructs ActionType mapping — extend ItemActionTypeMapper.ToNative")
    };
}
