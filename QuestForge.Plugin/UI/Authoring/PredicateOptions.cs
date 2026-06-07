using QuestForge.Engine.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

internal static class PredicateOptions
{
    internal static List<string> Build(GameStateSnapshot snap)
    {
        var questId = snap.ActiveQuest?.Value ?? 0;
        var seq = snap.QuestSequence;
        var zone = snap.Zone.Value;
        var npcId = snap.LastNpcInteracted?.Value ?? 0;

        var options = new List<string>
        {
            "(none)",
            $"isQuestAccepted({questId})",
            $"isQuestComplete({questId})",
            $"questSequence({questId}) >= {seq}",
            $"questFlag({questId}, 0)",
            $"questFlag({questId}, 1)",
            $"questFlag({questId}, 2)",
            $"questFlag({questId}, 3)",
            $"questVariable({questId}, 0)",
            $"questVariableLow({questId}, 0) >= 3",
            $"questVariableHigh({questId}, 1) >= 3",
            $"playerZone() == {zone}",
            $"playerNear({{\"x\":0,\"y\":0,\"z\":0}}, 3)",
            "not inventoryHasCoffers()",
            $"playerHasItem({0})",
            $"playerHasEquipped({0})",
            $"isPlayerJob({0})",
            "isDiscipleOfWar",
            "isDiscipleOfMagic",
            $"playerJobId",
            "playerInCombat",
            $"jobGearsetExists({0})",
            $"objectExists({npcId})",
            $"objectExistsInRange({npcId}, 30)",
        };

        if (snap.LastAttuned.HasValue)
            options.Add($"isAttuned({snap.LastAttuned.Value.Value})");

        if (snap.LastAethernetShardInteracted.HasValue)
            options.Add($"isAetherCurrentAttuned({snap.LastAethernetShardInteracted.Value.Value})");

        if (snap.ObjectInteracted is { } oi)
            options.Add($"objectExists({oi.InteractableId})");

        if (snap.EquipmentChanged is { NewItemIds: { Count: > 0 } items })
            options.Add($"playerHasEquipped({items[0]})");

        if (snap.JobChanged is { } jc)
            options.Add($"isPlayerJob({jc.NewJobId})");

        if (snap.KeyItemsAdded is { Count: > 0 } added)
            options.Add($"playerHasItem({added[0]})");

        return options;
    }
}
