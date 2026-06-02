using System.Text.Json;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — Slice E.1 vendor observer tests (GWT-PU1..PU5).
//
// ALL tests compile-fail because:
//   1. IVendorProbe does not exist in QuestForge.Plugin.Tracing.
//   2. UIObserver does not accept an IVendorProbe? vendorProbe parameter.
//   3. UIObserver.PollVendor does not exist.
//   4. ResetHeartbeatState does not clear vendor caches.
//
// Builder: add IVendorProbe, extend UIObserver ctor + heartbeat, update
// ResetHeartbeatState. Then GWT-PU1..PU5 go green.
//
// Run with:
//   $env:PATH = "C:\Users\publi\.dotnet;" + $env:PATH
//   & "C:\Users\publi\.dotnet\dotnet.exe" test QuestForge.Plugin.Tests --filter "FullyQualifiedName~Vendor"
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Tracing;

// ─── FakeVendorProbe ─────────────────────────────────────────────────────────

/// <summary>
/// Controllable in-memory implementation of <see cref="IVendorProbe"/>.
/// <see cref="SetChangedItems"/> returns the supplied list once, then reverts
/// to empty — mirroring how <see cref="FakeCombatProbe.SetHostiles"/> works
/// for the returning-once-then-empty semantic.
/// </summary>
public sealed class FakeVendorProbe : IVendorProbe
{
    private bool _shopOpen;
    private long _gil;
    private int _seals;
    private IReadOnlyList<(uint ItemId, int Count)> _changedItems = [];
    private bool _changedItemsConsumed = true; // start empty

    // G5 GC-axes backing fields (§14.9 D14.4 — null = closed/fail-quiet)
    private int? _activeGcCategory;
    private int? _activeGcRankTier;

    public void SetShopOpen(bool value) => _shopOpen = value;
    public void SetGil(long value) => _gil = value;
    public void SetSeals(int value) => _seals = value;

    /// <summary>Sets the active GC category returned by <see cref="GetActiveGcCategory"/>. Null = fail-quiet.</summary>
    public void SetActiveGcCategory(int? value) => _activeGcCategory = value;

    /// <summary>Sets the active GC rank-tier returned by <see cref="GetActiveGcRankTier"/>. Null = fail-quiet.</summary>
    public void SetActiveGcRankTier(int? value) => _activeGcRankTier = value;

    /// <summary>
    /// Queues a one-shot changed-items list. The NEXT call to
    /// <see cref="GetChangedItemCounts"/> returns it, subsequent calls return empty.
    /// </summary>
    public void SetChangedItems(params (uint ItemId, int Count)[] items)
    {
        _changedItems = items;
        _changedItemsConsumed = false;
    }

    // IVendorProbe
    public bool IsShopOpen() => _shopOpen;
    public long GetGil() => _gil;
    public int GetGrandCompanySeals() => _seals;
    public int? GetActiveGcCategory() => _activeGcCategory;
    public int? GetActiveGcRankTier() => _activeGcRankTier;

    public IReadOnlyList<(uint ItemId, int Count)> GetChangedItemCounts()
    {
        if (_changedItemsConsumed) return [];
        _changedItemsConsumed = true;
        return _changedItems;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test class
// ─────────────────────────────────────────────────────────────────────────────

public sealed class UIObserverVendorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 20, 9, 0, 0, TimeSpan.Zero);
    private const string PassiveRunId = "passive-vendor-001";

    // Test constants from §13.5
    private const uint Vendor = 1001234;
    private const uint Item   = 1601;

    // ─── Fixture builder ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a UIObserver wired with a FakeVendorProbe (and no other probes).
    /// </summary>
    private static (UIObserver observer,
                    FakeFramework framework,
                    FakeVendorProbe vendorProbe,
                    FakeClock clock,
                    FakeTraceWriter writer)
        BuildVendorFixture()
    {
        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock       = new FakeClock(T0);
        var vendorProbe = new FakeVendorProbe();
        var framework   = new FakeFramework();

        // CONTRACT: UIObserver gains a nullable vendorProbe parameter (Slice E.1).
        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            vendorProbe:  vendorProbe,
            clock:        clock);

        return (observer, framework, vendorProbe, clock, writer);
    }

    /// <summary>
    /// Drives one heartbeat poll: advance clock ≥250 ms then tick framework.
    /// </summary>
    private static void DrivePoll(FakeFramework framework, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        framework.Tick();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string FormatEvents(FakeTraceWriter writer)
        => string.Join("; ", writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Select(e => $"[{e.Data.Method}] value={e.Data.Value?.GetRawText()}"));

    private static List<ObservationEvent> ShopOpenedEvents(FakeTraceWriter writer)
        => writer.RecordedEvents.OfType<ObservationEvent>()
                 .Where(e => e.Data.Method == "ShopOpened")
                 .ToList();

    private static List<ObservationEvent> VendorItemCountEvents(FakeTraceWriter writer)
        => writer.RecordedEvents.OfType<ObservationEvent>()
                 .Where(e => e.Data.Method == "VendorItemCount")
                 .ToList();

    private static List<ObservationEvent> CurrencyBalanceEvents(FakeTraceWriter writer)
        => writer.RecordedEvents.OfType<ObservationEvent>()
                 .Where(e => e.Data.Method == "CurrencyBalance")
                 .ToList();

    private static bool GetShopOpenedValue(ObservationEvent e)
    {
        Assert.NotNull(e.Data.Value);
        Assert.True(e.Data.Value!.Value.TryGetProperty("value", out var el),
            $"ShopOpened value must have 'value'. Got: {e.Data.Value.Value.GetRawText()}");
        return el.GetBoolean();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PU1 — ShopOpened emits only on transitions (false→true, true→false)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPU1_ShopOpenTransition_EmitsShopOpenedOnlyOnChange()
    {
        // CONTRACT: IsShopOpen() false→true→true→false across 4 polls →
        //           exactly TWO ShopOpened observations ({value:true}, {value:false}), not four.

        var (observer, framework, vendorProbe, clock, writer) = BuildVendorFixture();

        vendorProbe.SetShopOpen(false);
        DrivePoll(framework, clock); // poll 1: no prior → false, should not emit (was already false)

        vendorProbe.SetShopOpen(true);
        DrivePoll(framework, clock); // poll 2: false→true → emit ShopOpened{true}

        vendorProbe.SetShopOpen(true);
        DrivePoll(framework, clock); // poll 3: no change → no emit

        vendorProbe.SetShopOpen(false);
        DrivePoll(framework, clock); // poll 4: true→false → emit ShopOpened{false}

        var events = ShopOpenedEvents(writer);
        Assert.True(events.Count == 2,
            $"Expected exactly 2 ShopOpened observations, got {events.Count}. Events: {FormatEvents(writer)}");
        Assert.True(GetShopOpenedValue(events[0]),
            "First ShopOpened must be {value:true}");
        Assert.False(GetShopOpenedValue(events[1]),
            "Second ShopOpened must be {value:false}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PU2 — GetChangedItemCounts() items written as VendorItemCount per entry
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPU2_ChangedItems_EmitsVendorItemCountPerEntry()
    {
        // CONTRACT: GetChangedItemCounts() returns [(1601,1)] one poll, [] the next →
        //           exactly ONE VendorItemCount {itemId:1601, count:1} written total.

        var (observer, framework, vendorProbe, clock, writer) = BuildVendorFixture();

        vendorProbe.SetChangedItems((Item, 1)); // one-shot — returns once, then empty
        DrivePoll(framework, clock); // poll 1: returns [(1601,1)] → emit VendorItemCount

        DrivePoll(framework, clock); // poll 2: returns [] → no emit

        var events = VendorItemCountEvents(writer);
        Assert.True(events.Count == 1,
            $"Expected exactly 1 VendorItemCount, got {events.Count}. Events: {FormatEvents(writer)}");

        Assert.NotNull(events[0].Data.Value);
        var val = events[0].Data.Value!.Value;
        Assert.True(val.TryGetProperty("itemId", out var itemIdEl),
            $"VendorItemCount must have 'itemId'. Got: {val.GetRawText()}");
        Assert.Equal(Item, itemIdEl.GetUInt32());

        Assert.True(val.TryGetProperty("count", out var countEl),
            $"VendorItemCount must have 'count'. Got: {val.GetRawText()}");
        Assert.Equal(1, countEl.GetInt32());

        Assert.Null(events[0].Data.Argument);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PU3 — Currency balance change emits CurrencyBalance
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPU3_GilDropBetweenPolls_EmitsCurrencyBalance()
    {
        // CONTRACT: gil 10000→9000 between polls → exactly one CurrencyBalance
        //           {gil:9000, seals:…} written.

        var (observer, framework, vendorProbe, clock, writer) = BuildVendorFixture();

        vendorProbe.SetGil(10000);
        vendorProbe.SetSeals(0);
        DrivePoll(framework, clock); // poll 1: establishes baseline (no prior → first-seen, no emit needed
                                     // OR emits once; either way poll 2 is the drop-emit)

        vendorProbe.SetGil(9000);    // gil drops
        DrivePoll(framework, clock); // poll 2: change → emit CurrencyBalance

        // At minimum one CurrencyBalance with gil=9000 must be written.
        var events = CurrencyBalanceEvents(writer);
        var dropEvent = events.LastOrDefault(e =>
        {
            if (e.Data.Value is null) return false;
            if (!e.Data.Value.Value.TryGetProperty("gil", out var g)) return false;
            return g.GetInt64() == 9000L;
        });

        Assert.True(dropEvent.Data.Method == "CurrencyBalance",
            $"Expected a CurrencyBalance with gil=9000 after drop. Events: {FormatEvents(writer)}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PU4 — No IVendorProbe → no vendor observations (back-compat)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPU4_NoVendorProbe_NoVendorObservations()
    {
        // CONTRACT: UIObserver built WITHOUT an IVendorProbe → no ShopOpened /
        //           CurrencyBalance / VendorItemCount ever written; existing pollers unaffected.

        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock     = new FakeClock(T0);
        var framework = new FakeFramework();

        // Back-compat: no vendorProbe argument (default null)
        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            clock:        clock);

        for (var i = 0; i < 4; i++)
            DrivePoll(framework, clock);

        var vendorObs = writer.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Data.Method is "ShopOpened" or "CurrencyBalance" or "VendorItemCount")
            .ToList();

        Assert.Empty(vendorObs);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PU5 — ResetHeartbeatState clears vendor caches → re-emits on next poll
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPU5_ResetHeartbeatState_ClearsVendorCache_ReEmitsNextPoll()
    {
        // CONTRACT: after caching shop-open + balances, ResetHeartbeatState() →
        //           next poll re-emits the shop-open transition and re-baselines balances.
        //           No phantom: if IsShopOpen() still returns the same value, a ShopOpened
        //           transition fires again because the cache was cleared.

        var (observer, framework, vendorProbe, clock, writer) = BuildVendorFixture();

        // Poll 1: shop opens + balances established → emits ShopOpened{true}
        vendorProbe.SetShopOpen(true);
        vendorProbe.SetGil(10000);
        vendorProbe.SetSeals(0);
        DrivePoll(framework, clock);

        var shopEventsBeforeReset = ShopOpenedEvents(writer).Count;
        Assert.True(shopEventsBeforeReset >= 1,
            "Expected at least one ShopOpened{true} after first poll");

        // Poll 2: same state, no change → should not re-emit
        DrivePoll(framework, clock);
        var shopEventsAfterStablePoll = ShopOpenedEvents(writer).Count;
        Assert.Equal(shopEventsBeforeReset, shopEventsAfterStablePoll);

        // Reset clears the vendor state cache
        observer.ResetHeartbeatState();

        // Poll 3: same IsShopOpen()==true, but cache was cleared → re-emits ShopOpened{true}
        DrivePoll(framework, clock);

        var shopEventsAfterReset = ShopOpenedEvents(writer).Skip(shopEventsBeforeReset).ToList();
        Assert.True(shopEventsAfterReset.Count >= 1,
            $"Expected ShopOpened re-emit after ResetHeartbeatState. Events: {FormatEvents(writer)}");
        Assert.True(GetShopOpenedValue(shopEventsAfterReset[0]),
            "Re-emitted ShopOpened after reset must be {value:true}");

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PU6 — ShopOpened payload carries activeGcCategory/activeGcRankTier (§14.9.6)
    //
    // RED: UIObserver.PollVendor does not yet read GetActiveGcCategory()/GetActiveGcRankTier()
    //      or include them in the ShopOpened payload.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPU6_ShopOpenedTransition_WithGcAxes_PayloadCarriesAxes()
    {
        // CONTRACT (§14.9.6 GWT-PU6 positive case):
        // Given FakeVendorProbe returning IsShopOpen==true, GetActiveGcCategory==2, GetActiveGcRankTier==1
        // When the false→true transition heartbeat fires
        // Then the emitted ShopOpened ObservationEvent value has:
        //   value==true, activeGcCategory==2, activeGcRankTier==1
        // And aggregator.OnShopOpened(true, 2, 1) is forwarded (verified via PurchaseDetection after
        // the full purchase signal — the aggregator axes would be populated if wired).

        var (observer, framework, vendorProbe, clock, writer) = BuildVendorFixture();

        // Seed probe state: shop opens with GC category=2, tier=1
        vendorProbe.SetShopOpen(true);
        vendorProbe.SetActiveGcCategory(2);
        vendorProbe.SetActiveGcRankTier(1);

        DrivePoll(framework, clock); // false→true transition

        var events = ShopOpenedEvents(writer);
        Assert.True(events.Count >= 1,
            $"Expected at least one ShopOpened event. Events: {FormatEvents(writer)}");

        var ev = events[0];
        Assert.NotNull(ev.Data.Value);
        var val = ev.Data.Value!.Value;

        // value==true
        Assert.True(val.TryGetProperty("value", out var valueEl),
            $"ShopOpened payload must have 'value' key. Got: {val.GetRawText()}");
        Assert.True(valueEl.GetBoolean(),
            "ShopOpened payload 'value' must be true on false→true transition.");

        // activeGcCategory==2 — RED: Builder must add this to the payload
        Assert.True(val.TryGetProperty("activeGcCategory", out var catEl),
            $"ShopOpened payload must have 'activeGcCategory' key (§14.9.1). Got: {val.GetRawText()}");
        Assert.False(catEl.ValueKind == System.Text.Json.JsonValueKind.Null,
            "activeGcCategory must not be null when probe returns 2.");
        Assert.Equal(2, catEl.GetInt32());

        // activeGcRankTier==1 — RED: Builder must add this to the payload
        Assert.True(val.TryGetProperty("activeGcRankTier", out var tierEl),
            $"ShopOpened payload must have 'activeGcRankTier' key (§14.9.1). Got: {val.GetRawText()}");
        Assert.False(tierEl.ValueKind == System.Text.Json.JsonValueKind.Null,
            "activeGcRankTier must not be null when probe returns 1.");
        Assert.Equal(1, tierEl.GetInt32());

        observer.Dispose();
    }

    [Fact]
    public void GwtPU6_ShopOpenedTransition_NullGcAxes_PayloadCarriesNullKeys()
    {
        // CONTRACT (§14.9.6 GWT-PU6 negative case):
        // When the probe returns null for both axes (fail-quiet or non-GC vendor),
        // the ShopOpened payload still contains the activeGcCategory and activeGcRankTier keys,
        // but their values are JSON null (not absent).

        var (observer, framework, vendorProbe, clock, writer) = BuildVendorFixture();

        vendorProbe.SetShopOpen(true);
        vendorProbe.SetActiveGcCategory(null);
        vendorProbe.SetActiveGcRankTier(null);

        DrivePoll(framework, clock); // false→true transition

        var events = ShopOpenedEvents(writer);
        Assert.True(events.Count >= 1,
            $"Expected at least one ShopOpened event. Events: {FormatEvents(writer)}");

        var val = events[0].Data.Value!.Value;

        // activeGcCategory key present, value null — RED: Builder must emit key with null value
        Assert.True(val.TryGetProperty("activeGcCategory", out var catEl),
            $"ShopOpened payload must have 'activeGcCategory' key even when probe returns null (§14.9.1). Got: {val.GetRawText()}");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, catEl.ValueKind);

        // activeGcRankTier key present, value null — RED
        Assert.True(val.TryGetProperty("activeGcRankTier", out var tierEl),
            $"ShopOpened payload must have 'activeGcRankTier' key even when probe returns null (§14.9.1). Got: {val.GetRawText()}");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, tierEl.ValueKind);

        observer.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PU7 — heartbeat forward of OnActiveGcAxesObserved, no new trace event (§14.9.6)
    //
    // RED: UIObserver.PollVendor does not yet call aggregator.OnActiveGcAxesObserved
    //      on non-transition heartbeats.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPU7_HeartbeatWhileOpen_ForwardsAxesObserved_NoNewShopOpenedEvent()
    {
        // CONTRACT (§14.9.6 GWT-PU7):
        // After the shop-open transition has fired (_lastShopOpen==true),
        // a subsequent heartbeat where IsShopOpen() is still true but GetActiveGcCategory() returns 3
        // must NOT write a new ShopOpened ObservationEvent,
        // but MUST forward OnActiveGcAxesObserved(3, ...) to the aggregator.
        //
        // Verifies "mid-span axis changes update the aggregator without inflating the trace."

        var writer  = new FakeTraceWriter();
        var session = new TraceSession(TraceMode.Always, Path.GetTempPath(), _ => writer);
        session.OnPluginStart();

        var clock       = new FakeClock(T0);
        var vendorProbe = new FakeVendorProbe();
        var framework   = new FakeFramework();
        var aggregator  = new SnapshotAggregator(new QuestForge.Adapters.Types.QuestId(70001), clock);

        var observer = new UIObserver(
            framework:    framework,
            traceSession: session,
            passiveRunId: PassiveRunId,
            vendorProbe:  vendorProbe,
            clock:        clock);
        observer.SetAggregator(aggregator, PassiveRunId);

        // Poll 1: shop opens with cat=2 → transition → emits ShopOpened{true}
        vendorProbe.SetShopOpen(true);
        vendorProbe.SetActiveGcCategory(2);
        vendorProbe.SetActiveGcRankTier(1);
        DrivePoll(framework, clock);

        var shopEventsAfterOpen = ShopOpenedEvents(writer).Count;
        Assert.True(shopEventsAfterOpen >= 1,
            "Expected ShopOpened{true} transition event after first poll.");

        // Poll 2: shop still open, cat changed to 3 — must NOT emit a new ShopOpened event
        //         but MUST forward OnActiveGcAxesObserved(3, 1) to the aggregator
        vendorProbe.SetActiveGcCategory(3);
        vendorProbe.SetActiveGcRankTier(1);
        DrivePoll(framework, clock); // no shop state transition

        var shopEventsAfterHeartbeat = ShopOpenedEvents(writer).Count;
        Assert.Equal(shopEventsAfterOpen, shopEventsAfterHeartbeat);   // no new ShopOpened event

        // RED: Builder must call aggregator.OnActiveGcAxesObserved(3, 1) in the non-transition path.
        // With the no-op stub, aggregator._spanActiveGcCategory stays null → PurchaseDetected.ActiveGcCategory==null.
        // After Builder wires it, the projected value should be 3.
        // We drive a purchase signal to get a PurchaseDetected to inspect:
        vendorProbe.SetChangedItems((Item, 1));
        vendorProbe.SetGil(9000);
        DrivePoll(framework, clock);

        var pd = aggregator.Current.PurchaseDetected;
        Assert.NotNull(pd);

        // GWT-PU7 — both axes round-trip through the heartbeat path.
        // Category was 2 on poll 1 (transition), then 3 on poll 2 (mid-span heartbeat) → 3.
        // Tier was 1 on both polls (no change) → 1; asserts the heartbeat doesn't accidentally
        // clear the stable axis when forwarding a changed sibling.
        Assert.Equal(3, pd!.ActiveGcCategory);
        Assert.Equal(1, pd!.ActiveGcRankTier);

        observer.Dispose();
    }
}
