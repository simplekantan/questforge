using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId   = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ItemId  = QuestForge.Adapters.Types.ItemId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Engine dispatch tests for PurchaseItemStep — Groups A, B, C.
///
/// RED PHASE: All tests fail to compile until Builder implements:
///   - PurchaseItemStep schema type (QuestForge.Schema)
///   - PurchaseCurrency enum (QuestForge.Schema)
///   - EngineAction.Purchase record (QuestForge.Engine)
///   - IVendor / PurchaseOutcome (QuestForge.Adapters)
///   - FakeVendor (QuestForge.Adapters.Fakes)
///   - GetGrandCompanySeals / SetGrandCompanySeals on IGameStateProvider / FakeGameStateProvider
///   - SetGilFailure on FakeGameStateProvider
///   - QuestEngine ctor IVendor parameter + purchase dispatch arm
///   - EngineTestHarness.Vendor property + Purchase case in RunToCompletion
/// </summary>
public sealed class PurchaseItemStepTests
{
    private const uint TestQuestId    = 12345u;
    private const uint TestVendorNpc  = 1001234u;
    private const uint TestItem       = 1601u;
    private const int  TestSeq        = 0;
    // Distinct from TestQuestId — marking it complete never returns Done prematurely.
    private const uint ConditionQuestId = 99999u;

    private static readonly Position3   TargetPos = new(10.5f, 0f, -20.0f);
    private static readonly NpcLocation TargetLoc =
        new(NpcId: TestVendorNpc, Zone: 128, Position: TargetPos);

    private static string FormatAction(EngineAction a) => a.GetType().Name;

    // =========================================================================
    // A1 — in range, count below target → emits Purchase with all four fields
    // =========================================================================

    [Fact]
    public async Task A1_InRange_CountBelow_EmitsPurchase()
    {
        // CONTRACT: in-range player + count=0 → Purchase(Vendor, Item, Qty, Currency)
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep      // RED: type does not exist
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil              // RED: type does not exist
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var purchase = Assert.IsType<EngineAction.Purchase>(action); // RED: type does not exist
        Assert.Equal(new NpcId(TestVendorNpc), purchase.Vendor);
        Assert.Equal(new ItemId(TestItem),     purchase.Item);
        Assert.Equal(1,                        purchase.Quantity);
        Assert.Equal(PurchaseCurrency.Gil,     purchase.Currency);
    }

    // =========================================================================
    // A2 — out of range → emits Navigate whose Destination matches TargetPos
    // =========================================================================

    [Fact]
    public async Task A2_OutOfRange_EmitsNavigate()
    {
        // CONTRACT: player at origin (far from vendor) → Navigate to TargetPos
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(TargetPos.X, navigate.Destination.X, precision: 2);
        Assert.Equal(TargetPos.Z, navigate.Destination.Z, precision: 2);
    }

    // =========================================================================
    // A3 — custom StopDistance=8, player ~7 away → still in range → Purchase
    // =========================================================================

    [Fact]
    public async Task A3_CustomStopDistance_InRange_EmitsPurchase()
    {
        // CONTRACT: StopDistance=8, XZ dist≈7 → within radius → Purchase, not Navigate
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        // XZ dist from (10.5,0,-20): sqrt((17.5-10.5)^2 + 0^2) = 7 < 8
        harness.GameState.SetPosition(new WorldPosition(17.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id           = "buy-item",
            Target       = TargetLoc,
            ItemId       = TestItem,
            Quantity     = 1,
            Currency     = PurchaseCurrency.Gil,
            StopDistance = 8.0f
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Purchase>(action);
    }

    // =========================================================================
    // A4 — GcSeals currency emits Purchase carrying GcSeals
    // =========================================================================

    [Fact]
    public async Task A4_GcSealsCurrency_EmitsPurchaseWithGcSeals()
    {
        // CONTRACT: Currency=GcSeals, seals>0 → Purchase.Currency==GcSeals
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGrandCompanySeals(2000);   // RED: method does not exist

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-gc-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.GcSeals
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var purchase = Assert.IsType<EngineAction.Purchase>(action);
        Assert.Equal(PurchaseCurrency.GcSeals, purchase.Currency);
    }

    // =========================================================================
    // A5 — position read failure fails open → Purchase (no Navigate)
    // =========================================================================

    [Fact]
    public async Task A5_PositionFailure_FailsOpen_EmitsPurchase()
    {
        // CONTRACT: GetPlayerPosition fails → fail-open, skip distance check, emit Purchase
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPositionFailure("test", "test");
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Purchase>(action);
    }

    // =========================================================================
    // B1 — already owns exact Quantity, no authored Expect → Wait (no purchase)
    // =========================================================================

    [Fact]
    public async Task B1_AlreadyOwnsExactQuantity_NoAuthoredExpect_EmitsWait()
    {
        // CONTRACT: count >= Quantity (no Expect) → step satisfied, emit Wait immediately
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 2); // Quantity=2, owned=2
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 2,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B2 — owns more than Quantity → Wait (absolute >= semantics, never re-buys)
    // =========================================================================

    [Fact]
    public async Task B2_OwnsMoreThanQuantity_EmitsWait()
    {
        // CONTRACT: count(5) > Quantity(1) → absolute >= satisfied → Wait
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 5);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B3 — count rises to target across ticks → second tick emits Wait
    // =========================================================================

    [Fact]
    public async Task B3_CountRisesAcrossTicks_SecondTickEmitsWait()
    {
        // CONTRACT: tick1=Purchase (count=0), SetItemCount(1), tick2=Wait (expect met)
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Purchase>(tick1);

        // Simulate the buy completing — count rises
        harness.GameState.SetItemCount(new ItemId(TestItem), 1);

        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);
    }

    // =========================================================================
    // B4 — authored Expect overrides Quantity: count=1 satisfies Expect even though Qty=3
    // =========================================================================

    [Fact]
    public async Task B4_AuthoredExpect_Overrides_Quantity_EmitsWait()
    {
        // CONTRACT: Expect=playerHasItem(1601,1), count=1, Qty=3 → authored Expect wins → Wait
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 1);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 3,
            Currency = PurchaseCurrency.Gil,
            Expect   = new PredicateExpect { Predicate = $"playerHasItem({TestItem},1)" }
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B5 — SkipIf=isQuestComplete(99999) true → Wait (step skipped)
    // =========================================================================

    [Fact]
    public async Task B5_SkipIf_True_EmitsWait()
    {
        // CONTRACT: SkipIf predicate evaluates true → step skipped → Wait
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.QuestState.SetQuestStatus(new QuestId(ConditionQuestId), QuestStatus.Complete);
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil,
            SkipIf   = new PredicateExpect { Predicate = $"isQuestComplete({ConditionQuestId})" }
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // C1 — zero gil → AwaitUser whose Reason contains "afford" and item id
    // =========================================================================

    [Fact]
    public async Task C1_ZeroGil_EmitsAwaitUserWithAffordReason()
    {
        // CONTRACT: gil=0, Currency=Gil → AwaitUser; Reason contains "afford" and "1601"
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(0L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("afford",           awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TestItem.ToString(), awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // C2 — zero GC seals → AwaitUser whose Reason contains "afford"
    // =========================================================================

    [Fact]
    public async Task C2_ZeroGcSeals_EmitsAwaitUserWithAffordReason()
    {
        // CONTRACT: seals=0, Currency=GcSeals → AwaitUser; Reason contains "afford"
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGrandCompanySeals(0);   // RED: method does not exist

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-gc-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.GcSeals
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("afford", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // C3 — balance read fails → fail open → Purchase (adapter is the backstop)
    // =========================================================================

    [Fact]
    public async Task C3_BalanceReadFailure_FailsOpen_EmitsPurchase()
    {
        // CONTRACT: GetGil returns failed Result → fail-open, proceed to Purchase
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGilFailure("test", "simulated read failure");  // RED: method does not exist

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Purchase>(action);
    }

    // =========================================================================
    // C4 — positive balance (gil=1) → proceeds to Purchase
    // =========================================================================

    [Fact]
    public async Task C4_PositiveBalance_EmitsPurchase()
    {
        // CONTRACT: coarse gate passes on any gil > 0 → Purchase
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(1L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id       = "buy-item",
            Target   = TargetLoc,
            ItemId   = TestItem,
            Quantity = 1,
            Currency = PurchaseCurrency.Gil
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Purchase>(action);
    }

    // =========================================================================
    // G-A — engine resolve arm: GC navigation fields on EngineAction.Purchase
    //
    // RED PHASE: All G-A tests fail to compile until Builder adds:
    //   - GcCategory: int? and GcRankTier: int? to EngineAction.Purchase (D14.2)
    //   - GcCategory / GcRankTier fields on PurchaseItemStep schema type (G1, already landed)
    //   - QuestEngine purchase branch to copy step.GcCategory / step.GcRankTier into
    //     the emitted EngineAction.Purchase (Slice G2)
    // =========================================================================

    private const uint SealItem = 6141u;

    // =========================================================================
    // G-A1 — in range, GC fields set → Purchase carries GcCategory and GcRankTier
    // =========================================================================

    [Fact]
    public async Task GA1_InRange_GcFieldsSet_PurchaseCarriesGcCategoryAndRankTier()
    {
        // CONTRACT: step.GcCategory=2, step.GcRankTier=1, Currency=GcSeals →
        //           Purchase.GcCategory==2 && Purchase.GcRankTier==1 && Purchase.Currency==GcSeals
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(SealItem), 0);
        harness.GameState.SetGrandCompanySeals(2000);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id          = "buy-gc-weapon",
            Target      = TargetLoc,
            ItemId      = SealItem,
            Quantity    = 1,
            Currency    = PurchaseCurrency.GcSeals,
            GcCategory  = 2,    // RED: field does not exist until Builder adds it (G1 schema + G2 engine)
            GcRankTier  = 1     // RED: field does not exist until Builder adds it
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var purchase = Assert.IsType<EngineAction.Purchase>(action);
        Assert.Equal(PurchaseCurrency.GcSeals, purchase.Currency);
        Assert.Equal(2, purchase.GcCategory);   // RED: property does not exist on EngineAction.Purchase
        Assert.Equal(1, purchase.GcRankTier);   // RED: property does not exist on EngineAction.Purchase
    }

    // =========================================================================
    // G-A2 — in range, GC fields null → Purchase carries nulls (back-compat)
    // =========================================================================

    [Fact]
    public async Task GA2_InRange_GcFieldsNull_PurchaseCarriesNulls()
    {
        // CONTRACT: GcCategory=null, GcRankTier=null → engine never substitutes a default like 0;
        //           both fields on the emitted Purchase record are null
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(SealItem), 0);
        harness.GameState.SetGrandCompanySeals(2000);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id         = "buy-gc-weapon",
            Target     = TargetLoc,
            ItemId     = SealItem,
            Quantity   = 1,
            Currency   = PurchaseCurrency.GcSeals,
            GcCategory = null,  // RED: field does not exist yet
            GcRankTier = null   // RED: field does not exist yet
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var purchase = Assert.IsType<EngineAction.Purchase>(action);
        Assert.Null(purchase.GcCategory);   // RED: property does not exist on EngineAction.Purchase
        Assert.Null(purchase.GcRankTier);   // RED: property does not exist on EngineAction.Purchase
    }

    // =========================================================================
    // G-A3 — gil step with GC fields set → engine still emits Purchase with both
    // =========================================================================

    [Fact]
    public async Task GA3_GilCurrency_GcFieldsSet_PurchaseCarriesThem()
    {
        // CONTRACT: the engine is a value-passer; it copies GcCategory/GcRankTier regardless
        //           of Currency — the validator's warning is the right gate, not the engine.
        //           Currency==Gil, GcCategory=2, GcRankTier=1 → Purchase carries all three.
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(10.5f, 0f, -20.0f));
        harness.GameState.SetItemCount(new ItemId(TestItem), 0);
        harness.GameState.SetGil(10_000L);

        var quest = BuildQuest(new PurchaseItemStep
        {
            Id         = "buy-item",
            Target     = TargetLoc,
            ItemId     = TestItem,
            Quantity   = 1,
            Currency   = PurchaseCurrency.Gil,
            GcCategory = 2,     // RED: field does not exist yet
            GcRankTier = 1      // RED: field does not exist yet
        });
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var purchase = Assert.IsType<EngineAction.Purchase>(action);
        Assert.Equal(PurchaseCurrency.Gil, purchase.Currency);
        Assert.Equal(2, purchase.GcCategory);   // RED: property does not exist on EngineAction.Purchase
        Assert.Equal(1, purchase.GcRankTier);   // RED: property does not exist on EngineAction.Purchase
    }

    // =========================================================================
    // Factory
    // =========================================================================

    private static QuestDefinition BuildQuest(Step step) =>
        new()
        {
            SchemaVersion     = "1.0.0",
            Id                = TestQuestId,
            Name              = "PurchaseItem Test Quest",
            Expansion         = "arr",
            Category          = "side",
            Enabled           = true,
            SupportStatus     = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements      = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom        = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences         =
            [
                new QuestSequence
                {
                    Sequence = TestSeq,
                    Steps    = [step]
                }
            ]
        };
}
