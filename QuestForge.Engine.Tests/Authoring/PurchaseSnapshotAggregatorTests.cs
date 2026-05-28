using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice E.1 purchase aggregator correlation tests (GWT-PL1..PL6).
//
// ALL tests compile-fail because:
//   1. SnapshotAggregator.OnShopOpened(bool) does not exist.
//   2. SnapshotAggregator.OnCurrencyChanged(long, int) does not exist.
//   3. SnapshotAggregator.OnVendorItemCountChanged(uint, int) does not exist.
//   4. GameStateSnapshot.PurchaseDetected (type PurchaseDetection?) does not exist.
//   5. PurchaseDetection record does not exist.
//
// Builder: add the three aggregator methods and PurchaseDetection + the
// GameStateSnapshot.PurchaseDetected projection; implement the §13.1 span model
// (baseline-on-open, accumulate while open, retain after close, clear in ResetDeltas).
//
// Run with:
//   $env:PATH = "C:\Users\publi\.dotnet;" + $env:PATH
//   & "C:\Users\publi\.dotnet\dotnet.exe" test QuestForge.Engine.Tests --filter "FullyQualifiedName~Purchase"
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Engine.Tests.Authoring;

public sealed class PurchaseSnapshotAggregatorTests
{
    // Test constants from §13.5
    private static readonly QuestId BuyQuest = new(70001);
    private const uint Item      = 1601;
    private const uint SealItem  = 6141;

    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string FormatPd(PurchaseDetection? pd)
    {
        if (pd is null) return "(null)";
        var deltas = string.Join(",", pd.ItemDeltas.Select(kv => $"{kv.Key}:{kv.Value}"));
        return $"ShopWasOpen={pd.ShopWasOpen} ItemDeltas=[{deltas}] GilDropped={pd.GilDropped} SealsDropped={pd.SealsDropped}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PL1 — clean gil purchase → PurchaseDetected carries the delta
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPL1_CleanGilPurchase_PurchaseDetectedReflectsIt()
    {
        // CONTRACT: baseline is the balance at shop-open; delta = baseline − current.
        //
        // Sequence (mirrors how UIObserver forwards during a heartbeat):
        //   1. OnCurrencyChanged(10000, 0)  ← seed pre-open balance (becomes baseline on open)
        //   2. OnShopOpened(true)           ← baseline captured (gil=10000, seals=0); span starts
        //   3. OnVendorItemCountChanged(1601, 1) ← item count rose
        //   4. OnCurrencyChanged(9000, 0)   ← gil fell; tracked as current balance

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        // Step 1: seed pre-open balance (the builder must baseline these on shop-open)
        agg.OnCurrencyChanged(10_000L, 0);

        // Step 2: shop opens → baseline captured
        agg.OnShopOpened(true);

        // Step 3: item rose
        agg.OnVendorItemCountChanged(Item, 1);

        // Step 4: gil fell
        agg.OnCurrencyChanged(9_000L, 0);

        var pd = agg.Current.PurchaseDetected;

        Assert.NotNull(pd);
        Assert.True(pd!.ShopWasOpen,
            $"PurchaseDetected.ShopWasOpen must be true. Got: {FormatPd(pd)}");
        Assert.True(pd.ItemDeltas.ContainsKey(Item),
            $"PurchaseDetected.ItemDeltas must contain {Item}. Got: {FormatPd(pd)}");
        Assert.Equal(1, pd.ItemDeltas[Item]);
        Assert.Equal(1_000L, pd.GilDropped);
        Assert.Equal(0, pd.SealsDropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PL2 — GC-seal purchase → SealsDropped reflects the drop
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPL2_GcSealPurchase_SealsDroppedReflectsIt()
    {
        // CONTRACT: seals 2000→1400, gil unchanged → SealsDropped==600, GilDropped==0.

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        // Seed pre-open balance
        agg.OnCurrencyChanged(5_000L, 2_000);

        // Shop opens → baseline gil=5000, seals=2000
        agg.OnShopOpened(true);

        // Item rose (seal item)
        agg.OnVendorItemCountChanged(SealItem, 3);

        // Seals fell, gil unchanged
        agg.OnCurrencyChanged(5_000L, 1_400);

        var pd = agg.Current.PurchaseDetected;

        Assert.NotNull(pd);
        Assert.True(pd!.ItemDeltas.ContainsKey(SealItem),
            $"PurchaseDetected.ItemDeltas must contain {SealItem}. Got: {FormatPd(pd)}");
        Assert.Equal(3, pd.ItemDeltas[SealItem]);
        Assert.Equal(0L, pd.GilDropped);
        Assert.Equal(600, pd.SealsDropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PL3 — span retained after shop close
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPL3_SpanRetainedAfterShopClose_PurchaseDetectedPersists()
    {
        // CONTRACT: PL1 scenario, then OnShopOpened(false) →
        //           Current.PurchaseDetected still reports the same deltas.
        //           (Mirrors combat span retention after InCombatChanged(false).)

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        agg.OnCurrencyChanged(10_000L, 0);
        agg.OnShopOpened(true);
        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(9_000L, 0);

        // Shop closes
        agg.OnShopOpened(false);

        var pd = agg.Current.PurchaseDetected;

        Assert.NotNull(pd);
        Assert.True(pd!.ShopWasOpen,
            $"PurchaseDetected.ShopWasOpen must be true even after shop close. Got: {FormatPd(pd)}");
        Assert.True(pd.ItemDeltas.ContainsKey(Item),
            $"PurchaseDetected.ItemDeltas must still have {Item} after shop close. Got: {FormatPd(pd)}");
        Assert.Equal(1_000L, pd.GilDropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PL4 — multiple items accumulated in span
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPL4_MultipleItems_ItemDeltasHasBothKeys()
    {
        // CONTRACT: shop open, item 1601 +1 and item 1602 +2, gil dropped →
        //           ItemDeltas has BOTH keys.

        const uint Item2 = 1602;

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        agg.OnCurrencyChanged(10_000L, 0);
        agg.OnShopOpened(true);
        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnVendorItemCountChanged(Item2, 2);
        agg.OnCurrencyChanged(7_000L, 0); // gil fell

        var pd = agg.Current.PurchaseDetected;

        Assert.NotNull(pd);
        Assert.True(pd!.ItemDeltas.ContainsKey(Item),
            $"ItemDeltas must contain {Item}. Got: {FormatPd(pd)}");
        Assert.True(pd.ItemDeltas.ContainsKey(Item2),
            $"ItemDeltas must contain {Item2}. Got: {FormatPd(pd)}");
        Assert.Equal(1, pd.ItemDeltas[Item]);
        Assert.Equal(2, pd.ItemDeltas[Item2]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PL5 — both currencies dropped (ambiguous — both reflected)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPL5_BothCurrenciesDropped_BothReflectedInDetection()
    {
        // CONTRACT: shop open, item rose, gil 10000→9500 AND seals 2000→1900 →
        //           GilDropped==500, SealsDropped==100 (both > 0; inference picks larger).

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        agg.OnCurrencyChanged(10_000L, 2_000);
        agg.OnShopOpened(true);
        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(9_500L, 1_900); // both fell

        var pd = agg.Current.PurchaseDetected;

        Assert.NotNull(pd);
        Assert.Equal(500L, pd!.GilDropped);
        Assert.Equal(100,  pd.SealsDropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PL6 — ResetDeltas clears the purchase span; re-open re-baselines
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPL6_ResetDeltas_ClearsPurchaseSpan_SubsequentOpenRebaselines()
    {
        // CONTRACT: after a detected purchase, ResetDeltas() → Current.PurchaseDetected
        //           is null; a subsequent OnShopOpened(true) re-baselines cleanly (new span).

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        // First purchase span
        agg.OnCurrencyChanged(10_000L, 0);
        agg.OnShopOpened(true);
        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(9_000L, 0);

        // Sanity: span is populated before reset
        Assert.NotNull(agg.Current.PurchaseDetected);

        // Reset — clears the purchase span
        agg.ResetDeltas();

        Assert.Null(agg.Current.PurchaseDetected);

        // Second purchase: new shop-open should re-baseline cleanly
        agg.OnCurrencyChanged(9_000L, 0);  // new balance (post-purchase; new baseline on next open)
        agg.OnShopOpened(true);            // re-baselines: gil=9000, seals=0
        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(8_500L, 0);  // gil 9000→8500 → GilDropped=500

        var pd2 = agg.Current.PurchaseDetected;
        Assert.NotNull(pd2);
        Assert.Equal(500L, pd2!.GilDropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // G-S1 (refined) — both API entry points populate ActiveGcCategory/ActiveGcRankTier (§14.9.6)
    //
    // RED: SnapshotAggregator.OnShopOpened no-op stub does not apply GC axes;
    //      OnActiveGcAxesObserved is a no-op.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GS1_Refined_OnShopOpened_WithAxes_PopulatesGcFields()
    {
        // CONTRACT (§14.9.6 G-S1 refined — transition path):
        // OnShopOpened(true, activeGcCategory: 2, activeGcRankTier: 1) + purchase signal
        // → PurchaseDetected.ActiveGcCategory==2 && ActiveGcRankTier==1.

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        agg.OnCurrencyChanged(10_000L, 0);

        // Transition with GC axes — RED: stub does not store these
        agg.OnShopOpened(true, activeGcCategory: 2, activeGcRankTier: 1);

        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(9_000L, 0);

        var pd = agg.Current.PurchaseDetected;
        Assert.NotNull(pd);

        // RED: ActiveGcCategory should be 2 but stub leaves it null
        Assert.Equal(2, pd!.ActiveGcCategory);
        Assert.Equal(1, pd.ActiveGcRankTier);
    }

    [Fact]
    public void GS1_Refined_OnActiveGcAxesObserved_AfterOpen_PopulatesGcFields()
    {
        // CONTRACT (§14.9.6 G-S1 refined — heartbeat path):
        // OnShopOpened(true) without axes, then OnActiveGcAxesObserved(2, 1) + purchase signal
        // → PurchaseDetected.ActiveGcCategory==2 && ActiveGcRankTier==1.

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        agg.OnCurrencyChanged(10_000L, 0);
        agg.OnShopOpened(true);  // no axes on transition

        // Heartbeat path — RED: no-op stub does not store these
        agg.OnActiveGcAxesObserved(2, 1);

        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(9_000L, 0);

        var pd = agg.Current.PurchaseDetected;
        Assert.NotNull(pd);

        // RED: ActiveGcCategory should be 2 but stub leaves it null
        Assert.Equal(2, pd!.ActiveGcCategory);
        Assert.Equal(1, pd.ActiveGcRankTier);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // G-S2 — null probe reads keep nulls (§14.6, unchanged)
    //
    // GREEN (trivially): PurchaseDetection already defaults both fields to null,
    // and the stub does not populate them. Passes even without Builder work.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GS2_NullProbeReads_KeepNullsOnPurchaseDetected()
    {
        // CONTRACT (§14.6 G-S2):
        // OnShopOpened(true, null, null) + purchase signal → projection has both axes null.

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        agg.OnCurrencyChanged(10_000L, 0);
        agg.OnShopOpened(true, activeGcCategory: null, activeGcRankTier: null);
        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(9_000L, 0);

        var pd = agg.Current.PurchaseDetected;
        Assert.NotNull(pd);
        Assert.Null(pd!.ActiveGcCategory);
        Assert.Null(pd.ActiveGcRankTier);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // G-S3 (refined) — "last seen non-null wins"; intermediate nulls preserve prior value (§14.9.6)
    //
    // RED: OnActiveGcAxesObserved is a no-op stub.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GS3_Refined_LastSeenNonNullWins_IntermediateNullsPreservePriorValue()
    {
        // CONTRACT (§14.9.6 G-S3 refined):
        // OnShopOpened(true, 2, 1)
        //   → OnActiveGcAxesObserved(null, null)   ← intermediate null must NOT clear 2/1
        //   → OnActiveGcAxesObserved(3, 1)         ← should update category to 3
        // Then PurchaseDetected.ActiveGcCategory==3 && ActiveGcRankTier==1.
        // The intermediate null did NOT clear the 2.

        var clock = new FakeClock(Epoch);
        var agg   = new SnapshotAggregator(BuyQuest, clock);

        agg.OnCurrencyChanged(10_000L, 0);
        agg.OnShopOpened(true, activeGcCategory: 2, activeGcRankTier: 1);

        // Intermediate null — RED: stub is no-op; but even when wired, null must NOT clear 2
        agg.OnActiveGcAxesObserved(null, null);

        // Non-null update — RED: stub is no-op
        agg.OnActiveGcAxesObserved(3, 1);

        agg.OnVendorItemCountChanged(Item, 1);
        agg.OnCurrencyChanged(9_000L, 0);

        var pd = agg.Current.PurchaseDetected;
        Assert.NotNull(pd);

        // RED: Builder must implement "last seen non-null wins" for both to work
        Assert.Equal(3, pd!.ActiveGcCategory);
        Assert.Equal(1, pd.ActiveGcRankTier);
    }
}
