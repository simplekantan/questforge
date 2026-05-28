using FFXIVClientStructs.FFXIV.Component.GUI;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Dalamud.Interaction;

public sealed class DalamudVendor : IVendor
{
    private readonly PluginServices _svc;
    private DateTimeOffset _lastBuyAt = DateTimeOffset.MinValue;

    // Mirror DalamudInteractor's InteractThrottle: one buy action fires per ~second.
    private static readonly TimeSpan BuyThrottle = TimeSpan.FromMilliseconds(1000);

    public DalamudVendor(PluginServices svc) => _svc = svc;

    public Task<Result<PurchaseOutcome>> Purchase(
        NpcId vendor, ItemId item, int quantity, PurchaseCurrency currency, CancellationToken ct)
        => currency switch
        {
            PurchaseCurrency.Gil     => PurchaseGil(item, quantity, ct),
            PurchaseCurrency.GcSeals => PurchaseGcSeals(item, quantity, ct),
            _                        => Task.FromResult<Result<PurchaseOutcome>>(
                Result.Ok(PurchaseOutcome.UnsupportedCurrency))
        };

    // -------------------------------------------------------------------------
    // Gil shop path
    // -------------------------------------------------------------------------

    private Task<Result<PurchaseOutcome>> PurchaseGil(ItemId item, int quantity, CancellationToken ct)
    {
        // VERIFY IN-GAME: the gil-vendor shop addon name. The main storefront that opens
        // when you talk to a standard merchant uses "Shop". Some sources also name it
        // "InventoryBuy" but that appears to be an older alias; "Shop" is the current name.
        var addonPtr = _svc.GameGui.GetAddonByName("Shop");
        if (addonPtr.IsNull || !addonPtr.IsReady)
            return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

        if (DateTimeOffset.UtcNow - _lastBuyAt < BuyThrottle)
            return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

        unsafe
        {
            var addon = (AtkUnitBase*)addonPtr.Address;

            // AtkValues layout for the Shop addon (verified from a live struct dump):
            //   [2]       UInt  = number of items for sale
            //   [14 + i]  String = item i's display name
            //   [441 + i] UInt  = item i's item id
            // VERIFY IN-GAME: the 441 base was observed on a single shop; re-check if
            // buys select the wrong item on a shop with a different layout.
            if (addon->AtkValuesCount < 442)
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

            var count = (int)addon->AtkValues[2].UInt;
            if (count <= 0)
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

            var row = ResolveBuyRow(addon, item.Value, count);
            if (row < 0)
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ItemNotSold));

            _lastBuyAt = DateTimeOffset.UtcNow;

            // Confirmed via live FireCallback capture: gil-shop buy = FireCallback([Int 0 = buy, Int row, Int qty]);
            // the follow-up SelectYesno is handled by SelectYesnoResponder.
            var values = stackalloc AtkValue[3];
            values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
            values[1] = new AtkValue { Type = AtkValueType.Int, Int = row };
            values[2] = new AtkValue { Type = AtkValueType.Int, Int = quantity };
            addon->FireCallback(3, values);
        }

        return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.Purchased));
    }

    private static unsafe int ResolveBuyRow(AtkUnitBase* addon, uint itemId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var idx = 441 + i;
            if (idx >= addon->AtkValuesCount) break;
            if (addon->AtkValues[idx].UInt == itemId) return i;
        }
        return -1;
    }

    // -------------------------------------------------------------------------
    // Grand Company seal exchange path
    // -------------------------------------------------------------------------

    private Task<Result<PurchaseOutcome>> PurchaseGcSeals(ItemId item, int quantity, CancellationToken ct)
    {
        // The GC quartermaster exchange addon is named "GrandCompanyExchange".
        var addonPtr = _svc.GameGui.GetAddonByName("GrandCompanyExchange");
        if (addonPtr.IsNull || !addonPtr.IsReady)
            return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

        if (DateTimeOffset.UtcNow - _lastBuyAt < BuyThrottle)
            return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

        unsafe
        {
            var addon = (AtkUnitBase*)addonPtr.Address;

            // AtkValues layout for the GrandCompanyExchange addon (verified from a live struct dump):
            //   [1]        UInt   = number of items for exchange
            //   [17 + i]   String = item i's display name
            //   [67 + i]   UInt   = item i's seal price
            //   [167 + i]  UInt   = item i's icon id
            //   [317 + i]  UInt   = item i's item id
            if (addon->AtkValuesCount < 318)
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

            var count = (int)addon->AtkValues[1].UInt;
            if (count <= 0)
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

            var row = ResolveExchangeRow(addon, item.Value, count);
            if (row < 0)
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ItemNotSold));

            _lastBuyAt = DateTimeOffset.UtcNow;

            // Confirmed via live FireCallback capture: GrandCompanyExchange buy =
            // FireCallback([Int 0 = buy, Int row, Int qty, Int 0, Bool true, Bool false]);
            // the follow-up SelectYesno is dismissed by SelectYesnoResponder during an active run.
            var values = stackalloc AtkValue[6];
            values[0] = new AtkValue { Type = AtkValueType.Int,  Int  = 0 };
            values[1] = new AtkValue { Type = AtkValueType.Int,  Int  = row };
            values[2] = new AtkValue { Type = AtkValueType.Int,  Int  = quantity };
            values[3] = new AtkValue { Type = AtkValueType.Int,  Int  = 0 };
            values[4] = new AtkValue { Type = AtkValueType.Bool, Byte = 1 };
            values[5] = new AtkValue { Type = AtkValueType.Bool, Byte = 0 };
            addon->FireCallback(6, values);
        }

        return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.Purchased));
    }

    private static unsafe int ResolveExchangeRow(AtkUnitBase* addon, uint itemId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var idx = 317 + i;
            if (idx >= addon->AtkValuesCount) break;
            if (addon->AtkValues[idx].UInt == itemId) return i;
        }
        return -1;
    }
}
