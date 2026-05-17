using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public static class StepFactory
{
    public static Step Build(string stepType, string stepId, string? expect, GameStateSnapshot? after,
        GameStateSnapshot? before = null, uint[]? itemsOverride = null)
    {
        ExpectValue? expectValue = expect is { Length: > 0 }
            ? new PredicateExpect { Predicate = expect }
            : null;

        var zone = (int)(after?.Zone.Value ?? 0);
        var npcId = after?.LastNpcInteracted?.Value ?? 0u;
        var npcPos = after?.LastNpcPosition is { } p
            ? new Position3(p.X, p.Y, p.Z)
            : new Position3(0, 0, 0);
        var playerPos = new Position3(after?.Position.X ?? 0, after?.Position.Y ?? 0, after?.Position.Z ?? 0);
        var npcLoc = new NpcLocation(NpcId: npcId, Zone: zone, Position: npcPos);

        var zoneStr = zone > 0 ? zone.ToString() : null;

        // Detect aethernet travel: source shard must match the last NPC interacted, and
        // LastNpcPosition must be present (gives us the shard's world coordinates to navigate to).
        var sourceShard = before?.LastAethernetShardInteracted;
        var isAethernet = sourceShard.HasValue
            && before?.LastNpcInteracted?.Value == sourceShard.Value.Value
            && before?.LastNpcPosition.HasValue == true;

        return stepType switch
        {
            "travel" when isAethernet => BuildAethernetTravelStep(stepId, expectValue, zoneStr, zone, before!, after, sourceShard!.Value),
            "travel" => new TravelStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                Destination = new TravelDestination(Zone: zone, Position: playerPos)
            },
            "accept" => new AcceptStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc },
            "turn-in" => new TurnInStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc },
            "talk" => new TalkStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc },
            "hand-over-item" => new HandOverItemStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                Target = npcLoc,
                Items = itemsOverride ?? (after?.KeyItemsRemoved?.ToArray() ?? [])
            },
            "attune" => new AttunementStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                Target = new QuestForge.Schema.AetheryteId(after?.LastAttuned?.Value ?? 0u),
                // npcId/npcPos now come from the aetheryte object itself (ObjectKind.Aetheryte,
                // BaseId = AetheryteId) because PollTargetNpc now includes Aetheryte targets.
                Location = new NpcLocation(NpcId: npcId, Zone: zone, Position: npcPos)
            },
            "pickup-item" => new PickupItemStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                Target = new InteractableTarget(InteractableId: npcId, Zone: zone, Position: npcPos)
            },
            "interact-object" => new InteractObjectStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                Target = new InteractableTarget(InteractableId: npcId, Zone: zone, Position: npcPos)
            },
            _ => new TalkStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc }
        };
    }

    private static TravelStep BuildAethernetTravelStep(
        string stepId,
        ExpectValue? expectValue,
        string? zoneStr,
        int zone,
        GameStateSnapshot before,
        GameStateSnapshot? after,
        QuestForge.Adapters.Types.AetheryteId sourceShard)
    {
        // Use the source shard's world position so the engine can navigate to it first
        var shardPos = before.LastNpcPosition!.Value;
        var destPos = new Position3(shardPos.X, shardPos.Y, shardPos.Z);

        // Populate RouteHint.Aethernet only when we have a distinct destination shard
        var destShard = after?.LastAethernetShardInteracted;
        var destDiffers = destShard.HasValue && destShard.Value.Value != sourceShard.Value;
        var routeHint = destDiffers
            ? new RouteHint(Aethernet: new[] { destShard!.Value.Value })
            : null;

        return new TravelStep
        {
            Id = stepId,
            Expect = expectValue,
            Zone = zoneStr,
            Destination = new TravelDestination(Zone: zone, Position: destPos),
            RouteHint = routeHint
        };
    }
}
