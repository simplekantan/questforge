namespace QuestForge.Engine.Authoring;

public static class KeyItemHasher
{
    public const uint OffsetBasis = 0x811C9DC5u;
    private const uint Prime = 0x01000193u;

    public static uint Compute(IReadOnlyDictionary<uint, int> keyItems)
    {
        ArgumentNullException.ThrowIfNull(keyItems);
        var hash = OffsetBasis;
        foreach (var kvp in keyItems.OrderBy(k => k.Key))
        {
            // FNV-1a over itemId bytes then qty bytes
            foreach (var b in BitConverter.GetBytes(kvp.Key))
                hash = (hash ^ b) * Prime;
            foreach (var b in BitConverter.GetBytes(kvp.Value))
                hash = (hash ^ b) * Prime;
        }
        return hash;
    }
}
