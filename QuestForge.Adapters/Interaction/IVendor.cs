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
