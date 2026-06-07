using QuestForge.Engine.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

internal static class PredicateOptions
{
    internal record struct Entry(string Option, string? Tooltip = null);

    internal static List<Entry> Build(GameStateSnapshot snap)
    {
        var questId = snap.ActiveQuest?.Value ?? 0;
        var seq = snap.QuestSequence;
        var zone = snap.Zone.Value;
        var npcId = snap.LastNpcInteracted?.Value ?? 0;

        var options = new List<Entry>
        {
            new("(none)"),
            new($"isQuestAccepted({questId})", "True when the quest is in the player's journal"),
            new($"isQuestComplete({questId})", "True when the quest is completed"),
            new($"questSequence({questId}) >= {seq}", "Current quest sequence number (advances as objectives complete)"),
            new($"questFlag({questId}, 0)", "Quest completion flag bit (0-7)"),
            new($"questFlag({questId}, 1)"),
            new($"questFlag({questId}, 2)"),
            new($"questFlag({questId}, 3)"),
            new($"questVariable({questId}, 0)", "Full byte of quest work variable (V0-V5)"),
            new($"questVariableLow({questId}, 0) >= 3", "Low nibble (bits 0-3) of quest work variable"),
            new($"questVariableHigh({questId}, 1) >= 3", "High nibble (bits 4-7) of quest work variable"),
            new($"playerZone() == {zone}", "Current zone/territory ID"),
            new($"playerNear({{\"x\":0,\"y\":0,\"z\":0}}, 3)", "True when player is within radius of position"),
            new("inventoryHasCoffers()", "True when unopened coffers are in inventory"),
            new($"playerHasItem({0})", "Item count in inventory + armory (arg: itemId, optional qty)"),
            new($"playerHasEquipped({0})", "True when specific item ID is in an equipment slot"),
            new($"isSlotEquipped({0})",
                "True when equipment slot has any item.\n" +
                "0=MainHand  1=OffHand  2=Head  3=Body  4=Hands\n" +
                "5=Waist  6=Legs  7=Feet  8=Ears  9=Neck\n" +
                "10=Wrists  11=RingL  12=RingR  13=SoulCrystal"),
            new($"isPlayerJob({0})",
                "True when current ClassJob row ID matches.\n" +
                "1=GLA 2=PGL 3=MRD 4=LNC 5=ARC 6=CNJ 7=THM\n" +
                "19=PLD 20=MNK 21=WAR 22=DRG 23=BRD 24=WHM 25=BLM\n" +
                "26=ACN 27=SMN 28=SCH 29=ROG 30=NIN\n" +
                "31=MCH 32=DRK 33=AST 34=SAM 35=RDM\n" +
                "36=BLU 37=GNB 38=DNC 39=RPR 40=SGE 41=VPR 42=PCT"),
            new("isDiscipleOfWar", "True when current job is Tank, Melee, or Physical Ranged"),
            new("isDiscipleOfMagic", "True when current job is Caster or Healer"),
            new("playerInCombat", "True when player is in combat"),
            new($"jobGearsetExists({0})", "True when a gearset exists for the given ClassJob ID"),
            new($"objectExists({npcId})", "True when an object with this DataId is targetable nearby"),
            new($"objectExistsInRange({npcId}, 30)", "True when object is within specified range (yalms)"),
        };

        if (snap.LastAttuned.HasValue)
            options.Add(new($"isAttuned({snap.LastAttuned.Value.Value})", "True when attuned to this aetheryte"));

        if (snap.LastAethernetShardInteracted.HasValue)
            options.Add(new($"isAetherCurrentAttuned({snap.LastAethernetShardInteracted.Value.Value})", "True when aether current is attuned"));

        if (snap.ObjectInteracted is { } oi)
            options.Add(new($"objectExists({oi.InteractableId})", "True when this interactable object is nearby"));

        if (snap.EquipmentChanged is { NewItemIds: { Count: > 0 } items })
            options.Add(new($"playerHasEquipped({items[0]})", "True when this item is equipped"));

        if (snap.JobChanged is { } jc)
            options.Add(new($"isPlayerJob({jc.NewJobId})", "True when on this job"));

        if (snap.KeyItemsAdded is { Count: > 0 } added)
            options.Add(new($"playerHasItem({added[0]})", "True when player has this key item"));

        return options;
    }
}
