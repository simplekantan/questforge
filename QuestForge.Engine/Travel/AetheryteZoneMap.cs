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

    /// <summary>
    /// Replaces the map with data read from Lumina at plugin startup.
    /// Called once from the Plugin layer; not thread-safe (call before any engine ticks).
    /// </summary>
    public static void Populate(IReadOnlyDictionary<uint, uint> map) => _map = map;

    public static bool TryGetZone(uint aetheryteId, out uint zoneId)
        => _map.TryGetValue(aetheryteId, out zoneId);

    public static IReadOnlyDictionary<uint, uint> All => _map;
}
