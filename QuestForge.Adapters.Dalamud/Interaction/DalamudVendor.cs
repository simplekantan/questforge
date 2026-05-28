using FFXIVClientStructs.FFXIV.Component.GUI;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Dalamud.Interaction;

public sealed class DalamudVendor : IVendor
{
    private readonly PluginServices _svc;
    private DateTimeOffset _lastBuyAt    = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSwitchAt = DateTimeOffset.MinValue;
    private int _switchAttempts;
    private (uint ItemId, int Quantity, int? GcCategory, int? GcRankTier) _lastPurchaseSignature;

    // Mirror DalamudInteractor's InteractThrottle: one buy action fires per ~second.
    private static readonly TimeSpan BuyThrottle    = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan SwitchThrottle = TimeSpan.FromMilliseconds(500);

    // Empirically the GrandCompanyExchange addon needs 2–3 ReceiveEvent dispatches before it
    // visibly commits a tab change. Cap retries at ~2.5s of throttled attempts; beyond that
    // assume the (cat, tier) pair genuinely doesn't surface the item.
    private const int MaxSwitchAttempts = 5;

    public DalamudVendor(PluginServices svc) => _svc = svc;

    public Task<Result<PurchaseOutcome>> Purchase(
        NpcId vendor, ItemId item, int quantity, PurchaseCurrency currency,
        CancellationToken ct = default, int? gcCategory = null, int? gcRankTier = null)
        => currency switch
        {
            PurchaseCurrency.Gil     => PurchaseGil(item, quantity, ct),
            PurchaseCurrency.GcSeals => PurchaseGcSeals(item, quantity, gcCategory, gcRankTier, ct),
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

    private Task<Result<PurchaseOutcome>> PurchaseGcSeals(
        ItemId item, int quantity, int? gcCategory, int? gcRankTier, CancellationToken ct)
    {
        // The GC quartermaster exchange addon is named "GrandCompanyExchange".
        var addonPtr = _svc.GameGui.GetAddonByName("GrandCompanyExchange");
        if (addonPtr.IsNull || !addonPtr.IsReady)
        {
            // Addon closed/not-yet-open: reset the switch counter so a fresh open re-enters
            // the switching path rather than skipping it.
            _switchAttempts = 0;
            return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));
        }

        var sig = (item.Value, quantity, gcCategory, gcRankTier);
        if (!sig.Equals(_lastPurchaseSignature))
        {
            _switchAttempts = 0;
            _lastPurchaseSignature = sig;
        }

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

            // §14.2 D14.3 — resolve-first, switch-only-on-miss:
            //   Try ResolveExchangeRow against the current AtkValues first. If the item is already
            //   visible on the active tab (row >= 0), buy immediately. If not AND the caller asked
            //   for a specific (cat, tier), enter the switch path: dispatch a ReceiveEvent to the
            //   relevant radio buttons and return ShopOpening so the next tick re-resolves against
            //   refreshed AtkValues. The addon empirically takes 2–3 dispatches to commit, so the
            //   switch is re-fired (throttled) up to MaxSwitchAttempts before giving up.
            var row = ResolveExchangeRow(addon, item.Value, count);

            if (row >= 0)
            {
                // Item is on the current tab — buy immediately.
                _lastBuyAt = DateTimeOffset.UtcNow;
                // Reset switch counter on success so the next purchase request starts fresh.
                _switchAttempts = 0;

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

                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.Purchased));
            }

            // Item not found in the current view. Now decide whether to switch tabs.
            var needsSwitch = gcCategory is not null || gcRankTier is not null;

            if (!needsSwitch)
            {
                // Both fields null → back-compat: item simply is not on this tab, give up.
                _switchAttempts = 0;
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ItemNotSold));
            }

            if (_switchAttempts >= MaxSwitchAttempts)
            {
                // Exhausted retries — the requested (cat, tier) doesn't surface this item.
                _switchAttempts = 0;
                _svc.Log.Warning(
                    $"[DalamudVendor] giving up after {MaxSwitchAttempts} switch attempts: " +
                    $"item={item.Value} gcCategory={gcCategory} gcRankTier={gcRankTier}");
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ItemNotSold));
            }

            // Switch path: replay the radio button's natural AtkEvent via ReceiveEvent.
            // Synthetic FireCallback on the addon does NOT trigger the radio buttons' state-change
            // handlers; replaying the button's natural AtkEvent via ReceiveEvent does.
            // Empirically the addon takes 2–3 dispatches to visibly commit a tab change, so we
            // re-fire every SwitchThrottle interval until row >= 0 or we exhaust MaxSwitchAttempts.
            if (DateTimeOffset.UtcNow - _lastSwitchAt < SwitchThrottle)
                return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));

            // Tab/tier switching technique adapted from AutoRetainer (BSD-3, Copyright 2023 Puni.sh):
            // https://github.com/PunishXIV/AutoRetainer
            //   ECommons/Automation/UIInput/ClickHelper.cs (ClickRadioButton)
            //   AutoRetainer/Modules/GcHandin/GCContinuation.cs (SelectGCExchangeHorizontalTab / VerticalTab)
            // Indexing is node-id-position-based:
            //   gcCategory 0..3 → node id 44..47 (0=Weapons, 1=Armor, 2=Materiel, 3=Materials)
            //   gcRankTier 0..2 → node id 37..39 (bottom-to-top: 0=lowest visible tier, 2=highest)
            if (gcRankTier is not null)
                DispatchRadioButtonClick(addon, (uint)(37 + gcRankTier.Value), "rank");

            if (gcCategory is not null)
                DispatchRadioButtonClick(addon, (uint)(44 + gcCategory.Value), "cat");

            _switchAttempts++;
            _lastSwitchAt = DateTimeOffset.UtcNow;
        }

        return Task.FromResult<Result<PurchaseOutcome>>(Result.Ok(PurchaseOutcome.ShopOpening));
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

    // Replay a radio button's natural AtkEvent via the addon's ReceiveEvent path.
    // Per AutoRetainer's ClickRadioButton (see attribution above), synthetic FireCallback on
    // the addon does not trigger radio-button state-change handlers; replaying the button's
    // pre-registered event does. Warns on the three failure modes (missing node, wrong
    // component type, missing event) so a node-id regression after a game patch is visible.
    private unsafe void DispatchRadioButtonClick(AtkUnitBase* addon, uint nodeId, string axis)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null)
        {
            _svc.Log.Warning($"[DalamudVendor] {axis}: GetNodeById({nodeId}) returned null");
            return;
        }

        if (node->GetAsAtkComponentRadioButton() == null)
        {
            _svc.Log.Warning($"[DalamudVendor] {axis}: node {nodeId} is not a radio button (type={node->Type})");
            return;
        }

        var evt = node->AtkEventManager.Event;
        if (evt == null)
        {
            _svc.Log.Warning($"[DalamudVendor] {axis}: node {nodeId} has no pre-registered AtkEvent");
            return;
        }

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, node->AtkEventManager.Event);
    }
}
