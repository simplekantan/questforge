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

    // ─────────────────────────────────────────────────────────────────────────
    // G-S4 — StepFactory writes GcCategory/GcRankTier from PurchaseDetection (§14.9.6)
    //
    // RED: StepFactory.BuildPurchaseItemStep does not yet propagate
    //      after.PurchaseDetected.ActiveGcCategory/ActiveGcRankTier into the step.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GS4_FactoryWritesGcFieldsFromPurchaseDetection()
    {
        // CONTRACT (§14.9.4 + §14.9.6 G-S4):
        // Given after.PurchaseDetected has ActiveGcCategory=2, ActiveGcRankTier=1
        // When StepFactory.Build("purchase-item", ...) is called
        // Then the resulting PurchaseItemStep has GcCategory==2 && GcRankTier==1.

        var after = MakeAfterSnapshot(
            purchaseDetected: new PurchaseDetection(
                ShopWasOpen:       true,
                ItemDeltas:        new Dictionary<uint, int> { [6141u] = 1 },
                GilDropped:        0L,
                SealsDropped:      600,
                ActiveGcCategory:  2,     // G5 new field
                ActiveGcRankTier:  1),    // G5 new field
            lastNpcInteracted: Vendor,
            lastNpcPosition:   new WorldPosition(10, 0, 20),
            zone:              new ZoneId(128u));

        var step = StepFactory.Build(
            stepType: "purchase-item",
            stepId:   "buy-gc-item",
            expect:   "playerHasItem(6141,1)",
            after:    after);

        Assert.IsType<PurchaseItemStep>(step);
        var ps = (PurchaseItemStep)step;

        // RED: factory does not yet pass ActiveGcCategory/ActiveGcRankTier into the step
        Assert.Equal(2, ps.GcCategory);
        Assert.Equal(1, ps.GcRankTier);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // G-S5 — factory falls back to null when PurchaseDetection has null axes (§14.6)
    //
    // This test verifies NO synthetic 0 default is introduced.
    // GREEN once G-S4 is implemented (null is the default).
    // May be trivially passing right now since GcCategory/GcRankTier default to null on the step,
    // but RED once Builder adds the factory init lines (still null flow-through).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GS5_FactoryNullAxes_StepGcFieldsAreNull()
    {
        // CONTRACT (§14.6 G-S5):
        // Given after.PurchaseDetected with both axes null
        // Then the resulting PurchaseItemStep has GcCategory==null && GcRankTier==null.
        // The factory must NOT default to 0 — null means "author must fill in."

        var after = MakeAfterSnapshot(
            purchaseDetected: new PurchaseDetection(
                ShopWasOpen:       true,
                ItemDeltas:        new Dictionary<uint, int> { [1601u] = 1 },
                GilDropped:        1000L,
                SealsDropped:      0,
                ActiveGcCategory:  null,  // no GC axis observed
                ActiveGcRankTier:  null),
            lastNpcInteracted: Vendor,
            lastNpcPosition:   new WorldPosition(10, 0, 20),
            zone:              new ZoneId(128u));

        var step = StepFactory.Build(
            stepType: "purchase-item",
            stepId:   "buy-gil-item",
            expect:   "playerHasItem(1601,1)",
            after:    after);

        Assert.IsType<PurchaseItemStep>(step);
        var ps = (PurchaseItemStep)step;

        Assert.Null(ps.GcCategory);
        Assert.Null(ps.GcRankTier);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // V14 — Factory writes VendorCategory from PurchaseDetection.ActiveVendorCategory
    //
    // RED: PurchaseDetection.ActiveVendorCategory does not exist yet.
    //      StepFactory.BuildPurchaseItemStep does not yet propagate it.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void V14_FactoryWritesVendorCategoryFromPurchaseDetection()
    {
        // CONTRACT (VC8 V14):
        // Given after.PurchaseDetected with ActiveVendorCategory=2
        // When StepFactory.Build("purchase-item", ...) is called
        // Then the resulting PurchaseItemStep has VendorCategory==2.

        var after = MakeAfterSnapshot(
            purchaseDetected: new PurchaseDetection(
                ShopWasOpen:           true,
                ItemDeltas:            new Dictionary<uint, int> { [1601u] = 1 },
                GilDropped:            1000L,
                SealsDropped:          0,
                ActiveGcCategory:      null,
                ActiveGcRankTier:      null,
                ActiveVendorCategory:  2));   // RED: parameter does not exist yet

        var step = StepFactory.Build(
            stepType: "purchase-item",
            stepId:   "buy-cat-2",
            expect:   "playerHasItem(1601,1)",
            after:    after);

        Assert.IsType<PurchaseItemStep>(step);
        var ps = (PurchaseItemStep)step;

        Assert.Equal(2, ps.VendorCategory);   // RED: property does not exist on PurchaseItemStep
    }

    // ─────────────────────────────────────────────────────────────────────────
    // V15 — Factory writes VendorCategory null when ActiveVendorCategory is null
    //
    // This test verifies NO synthetic 0 default is introduced.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void V15_FactoryNullVendorCategory_StepFieldIsNull()
    {
        // CONTRACT (VC8 V15):
        // Given after.PurchaseDetected with ActiveVendorCategory=null
        // Then the resulting PurchaseItemStep has VendorCategory==null.
        // The factory must NOT default to 0 — null means "author must fill in."

        var after = MakeAfterSnapshot(
            purchaseDetected: new PurchaseDetection(
                ShopWasOpen:           true,
                ItemDeltas:            new Dictionary<uint, int> { [1601u] = 1 },
                GilDropped:            1000L,
                SealsDropped:          0,
                ActiveGcCategory:      null,
                ActiveGcRankTier:      null,
                ActiveVendorCategory:  null));   // RED: parameter does not exist yet

        var step = StepFactory.Build(
            stepType: "purchase-item",
            stepId:   "buy-no-cat",
            expect:   "playerHasItem(1601,1)",
            after:    after);

        Assert.IsType<PurchaseItemStep>(step);
        var ps = (PurchaseItemStep)step;

        Assert.Null(ps.VendorCategory);   // RED: property does not exist on PurchaseItemStep
    }
}
