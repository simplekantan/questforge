using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Interaction;

public interface IVendor
{
    Task<Result<PurchaseOutcome>> Purchase(
        NpcId vendor,
        ItemId item,
        int quantity,
        PurchaseCurrency currency,
        CancellationToken ct = default,
        int? gcCategory = null,
        int? gcRankTier = null);

    /// <summary>
    /// Closes any open shop addon (Shop, GrandCompanyExchange). Fire-and-forget;
    /// best-effort; no-op if no shop addon is currently open.
    /// </summary>
    Task Close(CancellationToken ct = default);
}

public enum PurchaseOutcome
{
    Purchased,
    AlreadyOwned,
    ShopOpening,
    InsufficientFunds,
    ItemNotSold,
    ShopNotOpen,
    UnsupportedCurrency,
    Failed
}
