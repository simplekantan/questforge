using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Items;

public interface ICofferOpener
{
    /// <summary>
    /// Scans player inventory (bags 1-4) for coffer items.
    /// A coffer is an item where ItemAction.RowId is 1085 or 388
    /// AND ItemUICategory.RowId == 61 (Miscellany).
    /// Returns their item IDs (may contain duplicates for stacked coffers).
    /// </summary>
    Task<Result<IReadOnlyList<uint>>> GetCofferItemIds(CancellationToken ct);

    /// <summary>
    /// Opens one coffer by using it as an item.
    /// Calls ActionManager.UseAction(ActionType.Item, itemId, 0xE0000000, 65535).
    /// Success means the game accepted the use request (cast bar started).
    /// </summary>
    Task<Result<Unit>> OpenCoffer(uint itemId, CancellationToken ct);
}
