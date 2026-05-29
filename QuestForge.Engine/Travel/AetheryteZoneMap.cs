namespace QuestForge.Engine.Travel;

internal static class AetheryteZoneMap
{
    private static readonly IReadOnlyDictionary<uint, uint> _map =
        new Dictionary<uint, uint>
        {
            { 1000u, 130u }, // test-corpus aetheryte
            { 8u,    129u }, // Limsa Lominsa Lower Decks
        };

    public static bool TryGetZone(uint aetheryteId, out uint zoneId)
        => _map.TryGetValue(aetheryteId, out zoneId);

    public static IReadOnlyDictionary<uint, uint> All => _map;
}
