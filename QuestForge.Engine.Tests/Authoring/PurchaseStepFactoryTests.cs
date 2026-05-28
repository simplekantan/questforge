using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

// ─────────────────────────────────────────────────────────────────────────────
// Slice E.2: StepFactory "purchase-item" case (GWT-PF1, GWT-PF2).
//
// RED phase: tests will fail until Builder adds the "purchase-item" switch arm
// in StepFactory.Build. InferredFrom.Purchase must also exist (added in same
// slice) but is not directly referenced here — compile failures appear only in
// StepInferenceEngineTests.
//
// Factory contract: Build("purchase-item", stepId, expect, after) reads
//   Target from after.LastNpcInteracted + LastNpcPosition + Zone,
//   ItemId/Quantity from the primary PurchaseDetected item delta,
//   Currency from which balance dropped (GilDropped>0 && SealsDropped==0 → Gil;
//                                        SealsDropped>0 && GilDropped==0 → GcSeals).
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PurchaseStepFactoryTests
{
    private static readonly QuestId BuyQuest  = new(70001);
    private static readonly NpcId   Vendor    = new(1001234u);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GameStateSnapshot MakeAfterSnapshot(
        PurchaseDetection? purchaseDetected,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        ZoneId? zone = null,
        WorldPosition? position = null) =>
        new(
            CapturedAt:         T0,
            Zone:               zone ?? new ZoneId(128u),
            Position:           position ?? new WorldPosition(5, 0, 5),
            ActiveQuest:        BuyQuest,
            QuestSequence:      1,
            QuestFlags:         0,
            QuestAccepted:      false,
            QuestCompleted:     false,
            LastNpcInteracted:  lastNpcInteracted ?? Vendor,
            LastNpcPosition:    lastNpcPosition ?? new WorldPosition(10, 0, 20),
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash:      0,
            LastAttuned:        null)
        {
            PurchaseDetected = purchaseDetected,
        };

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PF1: gil purchase → PurchaseItemStep with Target, ItemId, Qty, Currency=Gil
    //
    // after.PurchaseDetected = (true, {1601:1}, 1000, 0).
    // LastNpcInteracted = Vendor(1001234), LastNpcPosition = (10,0,20), Zone = 128.
    // Build("purchase-item","buy-item-1601","playerHasItem(1601,1)", after).
    // Then:
    //   - result is PurchaseItemStep
    //   - Target.NpcId == 1001234, Target.Zone == 128, Target.Position == (10,0,20)
    //   - ItemId == 1601, Quantity == 1, Currency == Gil
    //   - Expect is PredicateExpect with Predicate == "playerHasItem(1601,1)"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPF1_BuildsPurchaseItemStep_GilCurrency_VendorTarget()
    {
        var after = MakeAfterSnapshot(
            purchaseDetected: new PurchaseDetection(
                ShopWasOpen:  true,
                ItemDeltas:   new Dictionary<uint, int> { [1601u] = 1 },
                GilDropped:   1000L,
                SealsDropped: 0),
            lastNpcInteracted: Vendor,
            lastNpcPosition:   new WorldPosition(10, 0, 20),
            zone:              new ZoneId(128u));

        var step = StepFactory.Build(
            stepType: "purchase-item",
            stepId:   "buy-item-1601",
            expect:   "playerHasItem(1601,1)",
            after:    after);

        Assert.IsType<PurchaseItemStep>(step);
        var ps = (PurchaseItemStep)step;

        Assert.Equal("buy-item-1601", ps.Id);
        Assert.Equal(1001234u, ps.Target.NpcId);
        Assert.Equal(128,      ps.Target.Zone);
        Assert.Equal(10f,      ps.Target.Position.X);
        Assert.Equal(0f,       ps.Target.Position.Y);
        Assert.Equal(20f,      ps.Target.Position.Z);
        Assert.Equal(1601u,    ps.ItemId);
        Assert.Equal(1,        ps.Quantity);
        Assert.Equal(PurchaseCurrency.Gil, ps.Currency);

        var pe = Assert.IsType<PredicateExpect>(ps.Expect);
        Assert.Equal("playerHasItem(1601,1)", pe.Predicate);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GWT-PF2 (= GWT-PI2): seals dropped, gil not → Currency == GcSeals.
    //
    // after.PurchaseDetected = (true, {6141:3}, 0, 600).
    // Then built step has Currency == GcSeals, ItemId == 6141, Quantity == 3.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GwtPF2_BuildsPurchaseItemStep_GcSealsCurrency_WhenSealsDropped()
    {
        var after = MakeAfterSnapshot(
            purchaseDetected: new PurchaseDetection(
                ShopWasOpen:  true,
                ItemDeltas:   new Dictionary<uint, int> { [6141u] = 3 },
                GilDropped:   0L,
                SealsDropped: 600),
            lastNpcInteracted: Vendor,
            lastNpcPosition:   new WorldPosition(10, 0, 20),
            zone:              new ZoneId(128u));

        var step = StepFactory.Build(
            stepType: "purchase-item",
            stepId:   "buy-item-6141",
            expect:   "playerHasItem(6141,3)",
            after:    after);

        Assert.IsType<PurchaseItemStep>(step);
        var ps = (PurchaseItemStep)step;

        Assert.Equal(6141u,  ps.ItemId);
        Assert.Equal(3,      ps.Quantity);
        Assert.Equal(PurchaseCurrency.GcSeals, ps.Currency);
    }
}
