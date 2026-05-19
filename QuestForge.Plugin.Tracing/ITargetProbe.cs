namespace QuestForge.Plugin.Tracing;

/// <summary>
/// Abstracts TargetManager access for NPC capture in UI dialogue polls.
/// The probe filters by ObjectKind internally so callers see typed results only.
/// </summary>
public interface ITargetProbe
{
    /// <summary>Current hard target if it is an EventNpc or BattleNpc, otherwise null.</summary>
    (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcTarget();

    /// <summary>Previous hard target if it is an EventNpc or BattleNpc, otherwise null.</summary>
    (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcPreviousTarget();

    /// <summary>Current hard target if it is an Aetheryte, otherwise null.</summary>
    uint? GetAetheryteTarget();
}
