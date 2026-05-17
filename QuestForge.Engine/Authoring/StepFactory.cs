using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public static class StepFactory
{
    public static Step Build(string stepType, string stepId, string? expect, GameStateSnapshot? after, uint[]? itemsOverride = null)
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

        return stepType switch
        {
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
}
