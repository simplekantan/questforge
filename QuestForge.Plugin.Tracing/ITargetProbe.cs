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
    (uint BaseId, float X, float Y, float Z)? GetAetheryteTarget();

    /// <summary>
    /// Current hard target if its ObjectKind == BattleNpc, otherwise null.
    /// GUARDRAIL: the filter is object-KIND only — do NOT add IsHostile/aggression checks.
    /// Docile quest mobs (IsHostile:False) ARE BattleNpcs and must be captured.
    /// </summary>
    (uint BaseId, float X, float Y, float Z, int Zone)? GetBattleNpcTarget();
}
