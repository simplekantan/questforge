namespace QuestForge.Plugin.Tracing;

public interface IVendorProbe
{
    bool IsShopOpen();
    long GetGil();
    int GetGrandCompanySeals();
    IReadOnlyList<(uint ItemId, int Count)> GetChangedItemCounts();
}
