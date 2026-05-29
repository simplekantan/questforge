namespace QuestForge.Engine.Travel;

public static class AetheryteZoneMap
{
    // Default map contains only the test-corpus entry. At plugin startup, Populate() replaces
    // this with the full Lumina-sourced table (aetheryteId → territoryTypeId).
    private static IReadOnlyDictionary<uint, uint> _map = new Dictionary<uint, uint>
    {
        { 1000u, 130u }, // test-corpus aetheryte
        { 8u,    129u }, // Limsa Lominsa Lower Decks
    };

    // Reverse map (zoneId → aetheryteId), rebuilt whenever _map is replaced.
    private static IReadOnlyDictionary<uint, uint> _reverseMap = BuildReverse(_map);

    /// <summary>
    /// Replaces the map with data read from Lumina at plugin startup.
    /// Called once from the Plugin layer; not thread-safe (call before any engine ticks).
    /// </summary>
    public static void Populate(IReadOnlyDictionary<uint, uint> map)
    {
        _map = map;
        _reverseMap = BuildReverse(map);
    }

    public static bool TryGetZone(uint aetheryteId, out uint zoneId)
        => _map.TryGetValue(aetheryteId, out zoneId);

    /// <summary>
    /// Reverse lookup: given a territory/zone id, find the master aetheryte id whose Territory.RowId matches.
    /// Each zone has at most one master aetheryte in the IsAetheryte-filtered map.
    /// </summary>
    public static bool TryGetAetheryteByZone(uint zoneId, out uint aetheryteId)
        => _reverseMap.TryGetValue(zoneId, out aetheryteId);

    public static IReadOnlyDictionary<uint, uint> All => _map;

    private static IReadOnlyDictionary<uint, uint> BuildReverse(IReadOnlyDictionary<uint, uint> forward)
    {
        var rev = new Dictionary<uint, uint>(forward.Count);
        foreach (var (aetheryteId, zoneId) in forward)
            rev[zoneId] = aetheryteId; // last-wins on duplicate zones (shouldn't occur with IsAetheryte filter)
        return rev;
    }
}
