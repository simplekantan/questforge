namespace QuestForge.Adapters.Items;

/// <summary>
/// Pure predicate: determines if a Lumina Item row represents a treasure coffer.
/// Coffer = ItemAction.RowId in {1085, 388} AND ItemUICategory.RowId == 61.
/// Excludes 367 (Triple Triad card packs).
/// </summary>
public static class CofferIdentifier
{
    private static readonly HashSet<uint> CofferActionIds = [1085, 388];

    private const uint MiscellanyCategory = 61;

    public static bool IsCoffer(uint itemActionRowId, uint itemUiCategoryRowId)
        => CofferActionIds.Contains(itemActionRowId) && itemUiCategoryRowId == MiscellanyCategory;
}
