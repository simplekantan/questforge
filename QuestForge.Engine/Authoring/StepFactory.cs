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

        // WHY effectiveSourceShard: when AethernetTeleportCompleted is set but the author
        // never explicitly targeted a shard (e.g. used the main aetheryte crystal which
        // isn't tracked as LastAethernetShardInteracted), sourceShard is null. Fall back
        // to the From field carried by the teleport event itself.
        var effectiveSourceShard = sourceShard
            ?? (after?.AethernetTeleportCompleted?.From is { } fromHop
                ? new QuestForge.Adapters.Types.AetheryteId(fromHop.Value)
                : (QuestForge.Adapters.Types.AetheryteId?)null);

        // Aethernet travel fires when AethernetTeleportCompleted is set (explicit TelepotTown event)
        // AND we have an effective source shard to navigate to,
        // or via legacy signals when the shard conditions and destination are met.
        var hasAethernetDestination = (after?.AethernetTeleportCompleted != null && effectiveSourceShard.HasValue)
            || (isAethernet
                && (before?.AethernetDestinationSelected.HasValue == true
                    || (after?.LastAethernetShardInteracted is { } ds2 && ds2.Value != sourceShard!.Value.Value)));

        // Build dialogue choices from snapshot signals. SelectIconString list choices and
        // SelectYesno confirmations are tracked in separate snapshot fields so they produce
        // the correct choice type rather than a spurious {type:"list"} for a yesno prompt.
        DialogueChoice[] dialogueChoices;
        if (dialogueChoicesOverride is { Count: > 0 })
        {
            dialogueChoices = dialogueChoicesOverride
                .Select(i => new DialogueChoice(Type: "list", Answer: i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();
        }
        else
        {
            var derived = new List<DialogueChoice>();
            if (after?.DialogueOptionSelected is { } idx)
                derived.Add(new DialogueChoice(Type: "list", Answer: idx.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (after?.SelectYesnoConfirmed == true)
                derived.Add(new DialogueChoice(Type: "yesno", Answer: "yes"));
            dialogueChoices = derived.ToArray();
        }

        // Detect NpcDialogue travel: source NPC captured AND at least one dialogue choice recorded.
        var hasNpcDialogue = (after?.DialogueOptionSelected.HasValue == true || after?.SelectYesnoConfirmed == true)
            && after?.DialogueNpcSource != null;

        // Combat-start position: use CombatStartPosition if captured, else fall back to player position.
        var combatStartPos = after?.CombatStartPosition is { } csp
            ? new Position3(csp.X, csp.Y, csp.Z)
            : playerPos;
        var combatStartZone = after?.CombatStartZone > 0 ? after.CombatStartZone : zone;

        return stepType switch
        {
            "combat" => new CombatStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = zoneStr,
                KillEnemyDataIds = after?.KillCorrelatedTargets is { } kct
                    ? kct.Values.SelectMany(t => t.DataIds).Distinct().OrderBy(id => id).ToArray()
                    : [],
                Spawn = CombatSpawn.OverworldEnemies,
                Location = new NpcLocation(
                    NpcId: 0,
                    Zone: combatStartZone,
                    Position: combatStartPos)
            },
            "travel" when hasNpcDialogue =>
                BuildNpcDialogueTravelStep(stepId, expectValue, zoneStr, zone, before, after!, dialogueChoices),
            "travel" when hasAethernetDestination =>
                BuildAethernetTravelStep(stepId, expectValue, zoneStr, zone, before!, after, effectiveSourceShard!.Value),
            "travel" => new TravelStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = ZoneStr(before?.Zone.Value ?? 0u),
                // isAethernet=false: if we fell through from the aethernet branch, the destination
                // shard was not confirmed (same-shard = zone-gate walk, not an Aethernet hop).
                Destination = new TravelDestination(Zone: zone, Position: ResolveTravelPosition(before, after, playerPos, false)),
                StopDistance = 1.0f  // close enough to trigger zone-gate crossing and NPC interact
            },
            "accept" => new AcceptStep { Id = stepId, Expect = expectValue, Zone = zoneStr, RequiredZone = zoneStr, Target = npcLoc },
            "turn-in" => new TurnInStep { Id = stepId, Expect = expectValue, Zone = zoneStr, RequiredZone = zoneStr, Target = npcLoc },
            "talk" => new TalkStep { Id = stepId, Expect = expectValue, Zone = zoneStr, RequiredZone = zoneStr, Target = npcLoc, DialogueChoices = dialogueChoices },
            "hand-over-item" => new HandOverItemStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = zoneStr,
                Target = npcLoc,
                Items = itemsOverride ?? (after?.KeyItemsRemoved?.ToArray() ?? [])
            },
            "teleport" => new TeleportStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = ZoneStr(before?.Zone.Value ?? 0u),
                // Read AetheryteId from the snapshot's TeleportCompleted signal.
                // Defensive: if TeleportCompleted is null (should not happen — inference engine
                // only returns "teleport" when the field is set), fall back to 0u so the
                // draft validator catches it rather than throwing here.
                AetheryteId = new QuestForge.Schema.AetheryteId(after?.TeleportCompleted?.Value ?? 0u)
            },
            "use-action" => new UseActionStep
            {
                Id = stepId,
                Expect = expectValue,
                ActionType = after?.ActionCompleted?.ActionType ?? QuestForge.Schema.ActionType.Action,
                ActionId = after?.ActionCompleted?.ActionId ?? 0u,
                TargetNpcId = after?.ActionCompleted?.TargetBaseId
            },
            "use-emote" => new UseEmoteStep
            {
                Id = stepId,
                Expect = expectValue,
                EmoteId = after?.EmoteCompleted?.EmoteId ?? 0u,
                TargetNpcId = after?.EmoteCompleted?.TargetBaseId,
                Motion = true
            },
            "use-item" => new QuestForge.Schema.UseItemStep
            {
                Id = stepId,
                Expect = expectValue,
                Kind = after?.ItemUsed?.Kind ?? QuestForge.Schema.ItemKind.KeyItem,
                ItemId = after?.ItemUsed?.ItemId ?? 0u,
                TargetNpcId = after?.ItemUsed?.TargetBaseId,
                TargetPosition = after?.ItemUsed?.TargetPosition,
            },
            "say-chat-message" => new QuestForge.Schema.SayChatMessageStep
            {
                Id = stepId,
                Expect = expectValue,
                Message = after?.SayChatMessageSent?.Message ?? string.Empty,
                TargetNpcId = after?.SayChatMessageSent?.TargetBaseId
            },
            "equip-gear-for-quest" => new QuestForge.Schema.EquipGearForQuestStep
            {
                Id = stepId,
                Expect = expectValue,
                ItemIds = after?.EquipmentChanged?.NewItemIds.ToArray() ?? []
            },
            "change-job" => new QuestForge.Schema.ChangeJobStep
            {
                Id = stepId,
                Expect = expectValue,
                JobId = after?.JobChanged?.NewJobId ?? 0u
            },
            "register-gearset" => new QuestForge.Schema.RegisterGearsetStep
            {
                Id = stepId,
                Expect = expectValue,
            },
            "attune" => new AttunementStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = zoneStr,
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
                RequiredZone = zoneStr,
                Target = new InteractableTarget(InteractableId: npcId, Zone: zone, Position: npcPos)
            },
            "interact-object" => new InteractObjectStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = zoneStr,
                Target = new InteractableTarget(InteractableId: npcId, Zone: zone, Position: npcPos)
            },
            "purchase-item" => BuildPurchaseItemStep(stepId, expectValue, zoneStr, zone, npcPos, after),
            _ => new TalkStep { Id = stepId, Expect = expectValue, Zone = zoneStr, RequiredZone = zoneStr, Target = npcLoc, DialogueChoices = dialogueChoices }
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
        // Use DialogueNpcSource: captured from live TargetManager when SelectIconString opened,
        // independent of before/after LastNpcInteracted. No pre-targeting required from the author.
        var srcNpc = after.DialogueNpcSource!;
        var npcId  = srcNpc.NpcId;
        var npcPos = srcNpc.Position;
        var playerPos = new Position3(after.Position.X, after.Position.Y, after.Position.Z);

        // Source zone from DialogueNpcSource (captured when SelectIconString opened = source zone).
        var sourceZone = srcNpc.Zone;

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
            RequiredZone = ZoneStr(sourceZone),
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
        // Use the source shard's world position so the engine can navigate to it first.
        // Only treat LastNpcPosition as the shard position when LastNpcInteracted WAS the
        // departure shard. If the player last talked to a quest NPC (not the shard), LastNpcPosition
        // is that NPC's location — using it would send the engine back to the wrong spot.
        // before.Position is where the player stood when they opened the Record modal, which
        // is always near the shard (they just used it).
        var lastNpcWasShard = before.LastNpcInteracted?.Value == sourceShard.Value;
        var shardPos = (lastNpcWasShard ? before.LastNpcPosition : null) ?? before.Position;
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
            RequiredZone = ZoneStr(before.Zone.Value),
            Destination = new TravelDestination(Zone: zone, Position: destPos),
            RouteHint = routeHint
        };
    }

    private static PurchaseItemStep BuildPurchaseItemStep(
        string stepId,
        ExpectValue? expectValue,
        string? zoneStr,
        int zone,
        Position3 npcPos,
        GameStateSnapshot? after)
    {
        uint primaryId = 0;
        int primaryQty = 1;
        PurchaseCurrency currency = PurchaseCurrency.Gil;

        if (after?.PurchaseDetected is { } pd && pd.ItemDeltas.Count > 0)
        {
            var primary = pd.ItemDeltas
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .First();
            primaryId = primary.Key;
            primaryQty = primary.Value;

            if (pd.GilDropped > 0 && pd.SealsDropped == 0)
                currency = PurchaseCurrency.Gil;
            else if (pd.SealsDropped > 0 && pd.GilDropped == 0)
                currency = PurchaseCurrency.GcSeals;
            else if (pd.GilDropped > 0 && pd.SealsDropped > 0)
                currency = pd.GilDropped >= pd.SealsDropped ? PurchaseCurrency.Gil : PurchaseCurrency.GcSeals;
            else
                currency = PurchaseCurrency.Gil;
        }

        var npcId = after?.LastNpcInteracted?.Value ?? 0u;

        return new PurchaseItemStep
        {
            Id = stepId,
            Expect = expectValue,
            Zone = zoneStr,
            RequiredZone = zoneStr,
            Target = new NpcLocation(NpcId: npcId, Zone: zone, Position: npcPos),
            ItemId = primaryId,
            Quantity = primaryQty,
            Currency = currency,
            GcCategory = after?.PurchaseDetected?.ActiveGcCategory,
            GcRankTier = after?.PurchaseDetected?.ActiveGcRankTier
        };
    }

    private static string? ZoneStr(uint zone) => zone > 0 ? zone.ToString() : null;
    private static string? ZoneStr(int zone)  => zone > 0 ? zone.ToString() : null;
}
