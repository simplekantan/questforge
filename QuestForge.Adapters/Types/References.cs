namespace QuestForge.Adapters.Types;

public enum InteractableKind
{
    ItemPickup,
    EventTrigger,
    Aetheryte,
    AetherCurrent,
    DutyTrigger,
    Other
}

public record NpcReference(NpcId Id, WorldPosition Position, float DistanceToPlayer);

public record InteractableReference(
    InteractableId Id,
    WorldPosition Position,
    float DistanceToPlayer,
    InteractableKind Kind
);