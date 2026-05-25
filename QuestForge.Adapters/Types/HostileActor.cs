namespace QuestForge.Adapters.Types;

/// <summary>
/// A hostile actor visible from the object table, enriched with combat-priority fields.
/// Consumed by KillPriority.SelectTarget and the CombatController.
/// </summary>
public record HostileActor(
    ActorId         Id,                  // live object identity (for SetTarget)
    uint            DataId,              // BNpc base data-id (matches KillEnemyDataIds)
    WorldPosition   Position,
    float           DistanceToPlayer,
    bool            IsTargetable,        // filters untargetable / dead actors
    bool            IsDead,
    bool            IsTargetingPlayer,   // has aggro on us — highest priority concept
    bool            OnPlayerEnmityList,  // on our hate list
    bool            HasQuestMarker);     // nameplate quest icon present
