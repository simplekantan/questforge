namespace QuestForge.Plugin.Tracing;

public interface IVendorProbe
{
    bool IsShopOpen();
    long GetGil();
    int GetGrandCompanySeals();
    IReadOnlyList<(uint ItemId, int Count)> GetChangedItemCounts();

    /// <summary>Currently-selected GC category radio (0..3 positional). Null on closed/fail-quiet.</summary>
    int? GetActiveGcCategory();

    /// <summary>Currently-selected GC rank-tier radio (0..2 bottom-to-top). Null on closed/fail-quiet.</summary>
    int? GetActiveGcRankTier();
}
