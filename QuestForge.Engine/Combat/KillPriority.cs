using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine.Combat;

/// <summary>
/// A specific live actor we have decided to kill this tick.
/// </summary>
public readonly record struct KillTarget(ActorId Id, uint DataId);

/// <summary>
/// What the CombatController decided for this tick.
/// RotationShouldRun mirrors "are we engaged with something".
/// </summary>
public sealed record CombatDecision(KillTarget? Target, bool RotationShouldRun, string Reason);

/// <summary>
/// Pure, deterministic kill-priority ranking.
/// All state is parameter-driven — no Dalamud, no async, fully unit-testable.
///
/// Priority weights (higher = attack first):
///   IsTargetingPlayer  +150  (aggro on us)
///   OnPlayerEnmityList +125  (on our hate list)
///   HasQuestMarker     +100  (quest objective)
///   DataId ∈ killIds   +90   (explicit kill target)
///
/// Tie-breaks: higher score first → nearest (DistanceToPlayer asc) → lowest ActorId.Value.
/// </summary>
public static class KillPriority
{
    /// <summary>
    /// Selects the highest-priority eligible target.
    /// Returns null when no eligible (living, targetable, in-kill-set) actor is present.
    /// </summary>
    public static KillTarget? SelectTarget(
        IReadOnlyList<HostileActor> actors,
        IReadOnlySet<uint> killEnemyDataIds,
        CombatSpawn spawn)
    {
        HostileActor? best = null;
        int bestScore = int.MinValue;

        foreach (var actor in actors)
        {
            if (actor.IsDead || !actor.IsTargetable)
                continue;

            // For OverworldEnemies, only actors in the kill set are eligible.
            // For AutoOnEnterArea, all targetable/living hostiles are eligible (kill set is
            // a scoring bonus only, not a filter). Empty killIds = clear-the-room case.
            if (spawn == CombatSpawn.OverworldEnemies && !killEnemyDataIds.Contains(actor.DataId))
                continue;

            var score = Score(actor, killEnemyDataIds);

            if (best is null
                || score > bestScore
                || (score == bestScore && actor.DistanceToPlayer < best.DistanceToPlayer)
                || (score == bestScore && actor.DistanceToPlayer == best.DistanceToPlayer
                    && actor.Id.Value < best.Id.Value))
            {
                best = actor;
                bestScore = score;
            }
        }

        return best is null ? null : new KillTarget(best.Id, best.DataId);
    }

    /// <summary>
    /// Computes the priority score for a single actor.
    /// Exposed for unit assertions so tests can pin exact weight ordering.
    /// </summary>
    public static int Score(HostileActor a, IReadOnlySet<uint> killEnemyDataIds)
    {
        var score = 0;
        if (a.IsTargetingPlayer)   score += 150;
        if (a.OnPlayerEnmityList)  score += 125;
        if (a.HasQuestMarker)      score += 100;
        if (killEnemyDataIds.Contains(a.DataId)) score += 90;
        return score;
    }
}
