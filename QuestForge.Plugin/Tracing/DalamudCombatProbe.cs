using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace QuestForge.Plugin.Tracing;

/// <summary>
/// Dalamud-backed <see cref="ICombatProbe"/> for authoring-mode combat detection: the
/// in-combat flag plus the currently-visible BattleNpc set, each with a per-hostile death
/// indicator. No correlation logic lives here — UIObserver derives kills from the alive→dead
/// state transition and the consumers correlate.
/// </summary>
public sealed class DalamudCombatProbe : ICombatProbe
{
    private readonly ICondition _condition;
    private readonly IObjectTable _objectTable;

    public DalamudCombatProbe(ICondition condition, IObjectTable objectTable)
    {
        _condition   = condition;
        _objectTable = objectTable;
    }

    public bool IsInCombat() => _condition[ConditionFlag.InCombat];

    public IReadOnlyList<(ulong ObjectId, uint DataId, bool IsDead)> GetVisibleHostiles()
    {
        var result = new List<(ulong ObjectId, uint DataId, bool IsDead)>();
        foreach (var obj in _objectTable)
        {
            if (obj is null) continue;
            if (obj.ObjectKind is not ObjectKind.BattleNpc) continue;

            // Surface ALL BattleNpcs — deliberately NOT filtered on an "is hostile/aggressive"
            // flag. Many quest target mobs are docile (yellow, non-aggressive) yet are exactly
            // the enemies a combat objective wants; an aggression filter would drop them. The
            // downstream variable-correlation bounds the real kill-set, so over-surfacing here
            // (e.g. allied combatants) is harmless — they never coincide with a quest-var bump.
            if (obj is not IBattleChara bc) continue;
            var isDead = bc.IsDead || bc.CurrentHp == 0;
            result.Add((bc.GameObjectId, bc.BaseId, isDead));
        }
        return result;
    }
}
