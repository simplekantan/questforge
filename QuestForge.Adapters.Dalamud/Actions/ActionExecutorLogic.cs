using ClientStructsActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using SchemaActionType = QuestForge.Schema.ActionType;

namespace QuestForge.Adapters.Dalamud.Actions;

internal static class ActionExecutorLogic
{
    /// <summary>
    /// Maps the schema-side ActionType (string-discriminated enum the engine and JSON use)
    /// to the FFXIVClientStructs ActionType (the uint the game's ActionManager expects).
    ///
    /// CANONICAL MAPPING (verified against FFXIVClientStructs ActionManager.cs:395-417):
    ///
    ///   Schema.Action        → ClientStructs.Action         (value 1; combat abilities, weaponskills, spells)
    ///   Schema.GeneralAction → ClientStructs.GeneralAction  (value 5; mount, sprint, teleport, return)
    ///   Schema.KeyItem       → ClientStructs.EventItem      (value 3; quest key items used as actions —
    ///                                                       NOTE the rename: FFXIVClientStructs does not
    ///                                                       have a "KeyItem" member; "EventItem" is the
    ///                                                       canonical name for the quest-item-as-action bucket)
    /// </summary>
    public static ClientStructsActionType ToFFXIVActionType(SchemaActionType type) => type switch
    {
        SchemaActionType.Action        => ClientStructsActionType.Action,
        SchemaActionType.GeneralAction => ClientStructsActionType.GeneralAction,
        SchemaActionType.KeyItem       => ClientStructsActionType.EventItem,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(type), type,
                 $"Schema.ActionType.{type} has no FFXIVClientStructs mapping — extend ActionExecutorLogic.ToFFXIVActionType")
    };
}
