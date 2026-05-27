using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Schema;
using Xunit;
using NpcId  = QuestForge.Adapters.Types.NpcId;
using ItemId = QuestForge.Adapters.Types.ItemId;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Adapter contract tests for FakeVendor — Group D (D1-D3).
///
/// RED PHASE: All tests fail to compile until Builder implements:
///   - IVendor interface (QuestForge.Adapters.Interaction)
///   - PurchaseOutcome enum (QuestForge.Adapters.Interaction)
///   - PurchaseCurrency enum (QuestForge.Schema — already planned for Slice A)
///   - FakeVendor (QuestForge.Adapters.Fakes.Interaction)
///   - FakeGameStateProvider.SetGrandCompanySeals (QuestForge.Adapters.Fakes.State)
/// </summary>
public sealed class FakeVendorTests
{
    private const uint VendorNpc = 1001234u;
    private const uint ItemNum   = 1601u;

    private static FakeVendor CreateVendor() => new FakeVendor();    // RED: type does not exist
    private static FakeVendor CreateVendor(FakeGameStateProvider gs) =>
        new FakeVendor(gs);                                           // RED: ctor does not exist

    // =========================================================================
    // D1 — records the call and returns scripted Purchased outcome
    // =========================================================================

    [Fact]
    public async Task D1_ScriptedPurchasedOutcome_RecordsCallAndReturnsOutcome()
    {
        // CONTRACT: scripted Purchased → result is Ok(Purchased); call logged with correct fields
        var vendor   = CreateVendor();
        var npcId    = new NpcId(VendorNpc);
        var itemId   = new ItemId(ItemNum);
        const int qty = 1;

        vendor.ScriptNextOutcome(PurchaseOutcome.Purchased);    // RED: method + enum do not exist

        var result = await vendor.Purchase(               // RED: IVendor.Purchase does not exist
            npcId, itemId, qty, PurchaseCurrency.Gil, CancellationToken.None);

        Assert.Equal(PurchaseOutcome.Purchased, result.ValueOrThrow);
        Assert.Equal(1, vendor.RecordedCalls.Count);             // RED: property does not exist
        Assert.Equal(npcId,             vendor.RecordedCalls[0].Vendor);
        Assert.Equal(itemId,            vendor.RecordedCalls[0].Item);
        Assert.Equal(qty,               vendor.RecordedCalls[0].Quantity);
        Assert.Equal(PurchaseCurrency.Gil, vendor.RecordedCalls[0].Currency);
    }

    // =========================================================================
    // D2 — inventory-mutating callback increments item count on Purchased
    // =========================================================================

    [Fact]
    public async Task D2_InventoryMutatingCallback_RaisesGetItemCount_OnPurchased()
    {
        // CONTRACT: Purchased outcome fires callback that increments FakeGameStateProvider item count
        var gs     = new FakeGameStateProvider();
        var vendor = CreateVendor(gs);
        var itemId = new ItemId(ItemNum);

        gs.SetItemCount(itemId, 0);
        // Wire: on Purchased for itemId, increment count by 1
        vendor.OnPurchased(itemId, increment: 1, gameState: gs);  // RED: method does not exist
        vendor.ScriptNextOutcome(PurchaseOutcome.Purchased);

        await vendor.Purchase(
            new NpcId(VendorNpc), itemId, 1, PurchaseCurrency.Gil, CancellationToken.None);

        var countResult = await gs.GetItemCount(itemId, CancellationToken.None);
        Assert.Equal(1, countResult.ValueOrThrow);
    }

    // =========================================================================
    // D3 — unscripted default is benign ShopOpening; call is still recorded
    // =========================================================================

    [Fact]
    public async Task D3_UnscriptedDefault_ReturnsShopOpening_RecordsCall()
    {
        // CONTRACT: no scripting → default outcome is ShopOpening (benign), call logged
        var vendor = CreateVendor();
        var npcId  = new NpcId(VendorNpc);
        var itemId = new ItemId(ItemNum);

        var result = await vendor.Purchase(
            npcId, itemId, 1, PurchaseCurrency.GcSeals, CancellationToken.None);

        Assert.Equal(PurchaseOutcome.ShopOpening, result.ValueOrThrow);
        Assert.Equal(1, vendor.RecordedCalls.Count);
    }
}
