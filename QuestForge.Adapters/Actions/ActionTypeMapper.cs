namespace QuestForge.Adapters.Actions;

using QuestForge.Schema;

/// <summary>
/// Pure reverse-mapping helper: converts the FFXIVClientStructs ActionType byte (as a uint,
/// to keep this assembly Dalamud-free) into the schema-side ActionType enum the snapshot,
/// inference engine, and serializer use.
///
/// CANONICAL VALUES (verified against FFXIVClientStructs ActionManager.cs:395-417):
///   1 = Action         → Schema.ActionType.Action          (combat abilities, weaponskills, spells)
///   3 = EventItem      → Schema.ActionType.KeyItem         (quest key items used as actions)
///   5 = GeneralAction  → Schema.ActionType.GeneralAction   (mount, sprint, teleport, return)
///
/// All other values return null — they are NOT authorable as use-action steps in v1.
/// Inverse of ActionExecutorLogic.ToFFXIVActionType (in QuestForge.Adapters.Dalamud).
/// </summary>
public static class ActionTypeMapper
{
    public static ActionType? FromFFXIVActionType(uint ffxivActionType) => ffxivActionType switch
    {
        1u => ActionType.Action,
        3u => ActionType.KeyItem,
        5u => ActionType.GeneralAction,
        _  => null,
    };
}
