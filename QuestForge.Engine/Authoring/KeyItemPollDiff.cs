using QuestForge.Adapters.Tracing;

namespace QuestForge.Engine.Authoring;

public static class KeyItemPollDiff
{
    public static (
        Dictionary<uint, int> NewMap,
        List<uint> AddedIds,
        List<uint> RemovedIds,
        List<KeyItemDelta> Gained,
        List<KeyItemDelta> Lost,
        uint NewHash,
        bool Changed) Diff(
        Dictionary<uint, int> previous,
        IEnumerable<(uint id, int qty)> currentSlots)
    {
        // Build currentMap from slots, filtering id >= 2_000_000, summing duplicate slots
        var currentMap = new Dictionary<uint, int>();
        foreach (var (id, qty) in currentSlots)
        {
            if (id < 2_000_000u) continue;
            if (currentMap.TryGetValue(id, out var existing))
                currentMap[id] = existing + qty;
            else
                currentMap[id] = qty;
        }

        var newHash = KeyItemHasher.Compute(currentMap);
        var prevHash = KeyItemHasher.Compute(previous);

        if (newHash == prevHash)
        {
            return (currentMap, new List<uint>(), new List<uint>(), new List<KeyItemDelta>(), new List<KeyItemDelta>(), newHash, false);
        }

        var gained = new List<KeyItemDelta>();
        var lost = new List<KeyItemDelta>();
        var addedIds = new List<uint>();
        var removedIds = new List<uint>();

        // Items gained or qty increased
        foreach (var (id, qty) in currentMap)
        {
            if (previous.TryGetValue(id, out var prevQty))
            {
                if (qty > prevQty)
                    gained.Add(new KeyItemDelta(id, qty - prevQty));
            }
            else
            {
                gained.Add(new KeyItemDelta(id, qty));
                addedIds.Add(id);
            }
        }

        // Items lost or qty decreased
        foreach (var (id, prevQty) in previous)
        {
            if (currentMap.TryGetValue(id, out var qty))
            {
                if (qty < prevQty)
                    lost.Add(new KeyItemDelta(id, prevQty - qty));
            }
            else
            {
                lost.Add(new KeyItemDelta(id, prevQty));
                removedIds.Add(id);
            }
        }

        return (currentMap, addedIds, removedIds, gained, lost, newHash, true);
    }
}
