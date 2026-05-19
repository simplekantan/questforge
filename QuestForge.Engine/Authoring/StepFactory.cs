using System.Linq;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public static class StepFactory
{
    public static Step Build(string stepType, string stepId, string? expect, GameStateSnapshot? after,
        GameStateSnapshot? before = null, uint[]? itemsOverride = null,
        IReadOnlyList<int>? dialogueChoicesOverride = null)
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

        // Aethernet travel fires when:
        //   1. The shard conditions are met (isAethernet) AND
        //   2. A destination is known: either AethernetDestinationSelected (explicit menu pick)
        //      OR after.LastAethernetShardInteracted differs from the source shard (post-arrival).
        // Aethernet travel fires when AethernetTeleportCompleted is set (explicit TelepotTown event),
        // or via legacy signals when the shard conditions and destination are met.
        var hasAethernetDestination = after?.AethernetTeleportCompleted != null
            || (isAethernet
                && (before?.AethernetDestinationSelected.HasValue == true
                    || (after?.LastAethernetShardInteracted is { } ds2 && ds2.Value != sourceShard!.Value.Value)));

        // Build dialogue choices: override beats snapshot
        DialogueChoice[] dialogueChoices;
        if (dialogueChoicesOverride is { Count: > 0 })
        {
            dialogueChoices = dialogueChoicesOverride
                .Select(i => new DialogueChoice(Type: "list", Answer: i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();
        }
        else if (after?.DialogueOptionSelected is { } idx)
        {
            dialogueChoices = [new DialogueChoice(Type: "list", Answer: idx.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        }
        else
        {
            dialogueChoices = [];
        }

        // Detect NpcDialogue travel: all three fields must be present in 'after'
        var hasNpcDialogue = after?.DialogueOptionSelected.HasValue == true
            && after?.LastNpcInteracted.HasValue == true
            && after?.LastNpcPosition.HasValue == true;

        return stepType switch
        {
            "travel" when hasNpcDialogue =>
                BuildNpcDialogueTravelStep(stepId, expectValue, zoneStr, zone, before, after!, dialogueChoices),
            "travel" when hasAethernetDestination =>
                BuildAethernetTravelStep(stepId, expectValue, zoneStr, zone, before!, after, sourceShard!.Value),
            "travel" => new TravelStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                // isAethernet=false: if we fell through from the aethernet branch, the destination
                // shard was not confirmed (same-shard = zone-gate walk, not an Aethernet hop).
                Destination = new TravelDestination(Zone: zone, Position: ResolveTravelPosition(before, after, playerPos, false)),
                StopDistance = 0f  // navigate to exact position; gate triggers require precision
            },
            "accept" => new AcceptStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc },
            "turn-in" => new TurnInStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc },
            "talk" => new TalkStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc, DialogueChoices = dialogueChoices },
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
                // Prefer LastAethernetShardInteracted (the shard the author physically targeted,
                // works even when the aetheryte is already attuned). Fall back to LastAttuned
                // for traces recorded before this field existed.
                Target = new QuestForge.Schema.AetheryteId(
                    after?.LastAethernetShardInteracted?.Value
                    ?? after?.LastAttuned?.Value
                    ?? 0u),
                Location = new NpcLocation(NpcId: npcId, Zone: zone, Position: npcPos),
                // Infer stop distance from the recorded interaction distance (player position vs
                // aetheryte position at time of recording). Aetheryte crystals are large objects;
                // using the natural interaction distance prevents the engine from running into them.
                StopDistance = InferStopDistance(after)
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
            _ => new TalkStep { Id = stepId, Expect = expectValue, Zone = zoneStr, Target = npcLoc, DialogueChoices = dialogueChoices }
        };
    }

    private static TravelStep BuildNpcDialogueTravelStep(
        string stepId,
        ExpectValue? expectValue,
        string? zoneStr,
        int zone,
        GameStateSnapshot? before,
        GameStateSnapshot after,
        DialogueChoice[] dialogueChoices)
    {
        var npcId = after.LastNpcInteracted!.Value.Value;
        var npcRaw = after.LastNpcPosition!.Value;
        var npcPos = new Position3(npcRaw.X, npcRaw.Y, npcRaw.Z);
        var playerPos = new Position3(after.Position.X, after.Position.Y, after.Position.Z);

        // Source zone: where the NPC resides (before zone); fall back to after zone if before is null
        var sourceZone = (int)(before?.Zone.Value ?? (uint)zone);

        // Dialogue choices: override wins; fall back to single recorded choice from snapshot
        var choices = dialogueChoices.Length > 0
            ? dialogueChoices
            : [new DialogueChoice(
                Type: "list",
                Answer: after.DialogueOptionSelected!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))];

        return new TravelStep
        {
            Id = stepId,
            Expect = expectValue,
            Zone = zoneStr,
            Destination = new TravelDestination(Zone: zone, Position: playerPos),
            RouteHint = new RouteHint(NpcDialogue: new NpcDialogueHint(
                target: new NpcLocation(
                    NpcId: npcId,
                    Zone: sourceZone,
                    Position: npcPos),
                dialogueChoices: choices))
        };
    }

    private static float? InferStopDistance(GameStateSnapshot? after)
    {
        if (after?.LastNpcPosition is not { } npcPos) return null;
        var p = after.Position;
        var dx = p.X - npcPos.X;
        var dy = p.Y - npcPos.Y;
        var dz = p.Z - npcPos.Z;
        var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        // Round up to nearest 0.5 and clamp to a sensible range
        return dist > 0.5f ? MathF.Round(dist * 2, MidpointRounding.AwayFromZero) / 2f : null;
    }

    private static Position3 ResolveTravelPosition(
        GameStateSnapshot? before, GameStateSnapshot? after, Position3 afterPlayerPos, bool isAethernet)
    {
        // Aethernet without confirmed destination: navigate to source shard first (shard position in source zone)
        if (isAethernet && before?.LastNpcPosition.HasValue == true)
        {
            var sp = before.LastNpcPosition!.Value;
            return new Position3(sp.X, sp.Y, sp.Z);
        }

        // Cross-zone border run: use before.Position (approach coords in source zone so vnavmesh
        // can pathfind to the gate). The game handles the zone transition when the player enters
        // the trigger area.
        var beforeZone = before?.Zone.Value ?? 0;
        var afterZone = after?.Zone.Value ?? 0;
        if (beforeZone > 0 && afterZone > 0 && beforeZone != afterZone && before?.Position is { } bp)
            return new Position3(bp.X, bp.Y, bp.Z);

        // Same-zone travel: destination is where the player ended up
        return afterPlayerPos;
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

        // Prefer AethernetTeleportCompleted (explicit event) → AethernetDestinationSelected
        // → post-arrival LastAethernetShardInteracted (fallback).
        var hop = after?.AethernetTeleportCompleted;
        var toValue = hop?.To.Value
            ?? before.AethernetDestinationSelected?.Value
            ?? (after?.LastAethernetShardInteracted is { } ds && ds.Value != sourceShard.Value
                ? ds.Value
                : (uint?)null);

        // From: prefer the value carried by the teleport event; fall back to sourceShard
        // (derived from LastNpcInteracted — the shard the author physically targeted before the hop).
        var fromValue = hop?.From?.Value ?? sourceShard.Value;

        var routeHint = toValue is { } to
            ? new RouteHint(Aethernet: new AethernetRouteHint(From: fromValue, To: to))
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
