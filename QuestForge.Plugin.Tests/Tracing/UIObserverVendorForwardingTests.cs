using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice E.1 vendor forwarding test (Group B, one representative test).
//
// Compile-fails because IVendorProbe, UIObserver vendorProbe param, PollVendor,
// OnShopOpened, OnVendorItemCountChanged, OnCurrencyChanged, and
// Current.PurchaseDetected do not yet exist.
//
// Builder: after E.1 is green (GWT-PU1..PU5), also implement the aggregator
// forwarding methods and PurchaseDetected projection so this test goes green.
//
// Run with:
//   $env:PATH = "C:\Users\publi\.dotnet;" + $env:PATH
//   & "C:\Users\publi\.dotnet\dotnet.exe" test QuestForge.Plugin.Tests --filter "FullyQualifiedName~Vendor"
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Tracing;

public sealed class UIObserverVendorForwardingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 20, 9, 0, 0, TimeSpan.Zero);
    private const string ActiveRunId = "vendor-fwd-001";
    private static readonly QuestId BuyQuest = new(70001);

    // Test constants from §13.5
    private const uint Vendor = 1001234;
    private const uint Item   = 1601;

    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeVendorProbe vendorProbe,
                    FakeClock clock,
                    SnapshotAggregator aggregator)
        BuildForwardingFixture()
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock       = new FakeClock(T0);
        var vendorProbe = new FakeVendorProbe();
        var framework   = new FakeFramework();
        var aggregator  = new SnapshotAggregator(BuyQuest, clock);

        // CONTRACT: UIObserver gains a nullable vendorProbe parameter (Slice E.1).
        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: ActiveRunId,
            vendorProbe:  vendorProbe,
            clock:        clock);

        observer.SetAggregator(aggregator, ActiveRunId);

        return (observer, framework, vendorProbe, clock, aggregator);
    }

    private static void DrivePoll(FakeFramework framework, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        framework.Tick();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Group B representative: shop-open + item-change + currency-change poll →
    // aggregator.Current.PurchaseDetected reflects the purchase.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShopOpenItemChangeCurrencyChange_ForwardedToAggregator_PurchaseDetectedReflectsIt()
    {
        // CONTRACT: a poll with shop-open transition, an item-count change, and a
        //           currency change must result in aggregator.Current.PurchaseDetected
        //           carrying ItemDeltas and a GilDropped value — proving all three
        //           forwarding calls (OnShopOpened / OnVendorItemCountChanged /
        //           OnCurrencyChanged) reach the aggregator.

        var (observer, framework, vendorProbe, clock, aggregator) = BuildForwardingFixture();

        // Seed baseline balances: gil=10000, seals=0 (before shop opens)
        vendorProbe.SetGil(10000);
        vendorProbe.SetSeals(0);
        vendorProbe.SetShopOpen(false);
        DrivePoll(framework, clock); // poll 1: establishes baseline, no shop transition

        // Poll 2: shop opens + item appears + gil drops
        vendorProbe.SetShopOpen(true);  // false→true transition → OnShopOpened(true) forwarded
        vendorProbe.SetGil(9000);       // gil drops → OnCurrencyChanged(9000, 0) forwarded
        vendorProbe.SetChangedItems((Item, 1)); // one-shot → OnVendorItemCountChanged(1601, 1) forwarded
        DrivePoll(framework, clock);

        // CONTRACT: aggregator.Current.PurchaseDetected must be non-null and reflect the purchase.
        // PurchaseDetected is the new projection on GameStateSnapshot (Slice E.2).
        var pd = aggregator.Current.PurchaseDetected;
        Assert.NotNull(pd);
        Assert.True(pd!.ShopWasOpen,
            "PurchaseDetected.ShopWasOpen must be true after OnShopOpened(true) forwarded.");
        Assert.True(pd.ItemDeltas.ContainsKey(Item),
            $"PurchaseDetected.ItemDeltas must contain item {Item}. Got: [{string.Join(",", pd.ItemDeltas.Keys)}]");
        Assert.Equal(1, pd.ItemDeltas[Item]);
        Assert.True(pd.GilDropped > 0,
            $"PurchaseDetected.GilDropped must be > 0 after gil 10000→9000. Got: {pd.GilDropped}");

        observer.Dispose();
    }
}
