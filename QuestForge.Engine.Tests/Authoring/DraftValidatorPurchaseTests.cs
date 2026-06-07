using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// Validator tests for PurchaseItemStep VendorCategory rules E23 and E24.
///
/// RED PHASE: Tests compile but fail at runtime because DraftValidator does not
/// yet contain E23 or E24 rules. The builder must add both rules to DraftValidator.cs.
///
/// E23: VendorCategory + GcCategory/GcRankTier both set -> mutually exclusive error.
/// E24: VendorCategory negative -> error.
/// </summary>
public sealed class DraftValidatorPurchaseTests
{
    private static readonly NpcLocation VendorLoc =
        new(NpcId: 1001234, Zone: 128, Position: new Position3(10.5f, 0f, -20.0f));

    // =========================================================================
    // V7 -- E23: VendorCategory + GcCategory both set -> error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PurchaseItemStep with VendorCategory=1 AND GcCategory=2,
    /// When Validate() is called,
    /// Then AssertSingleError "E23" and Message contains "mutually exclusive".
    /// </summary>
    [Fact]
    public void Validate_PurchaseItemStep_VendorCategoryAndGcCategory_RaisesE23()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("buy-conflict", 2,
            new PurchaseItemStep
            {
                Id             = "buy-conflict",
                Target         = VendorLoc,
                ItemId         = 1601,
                Quantity       = 1,
                Currency       = PurchaseCurrency.GcSeals,
                VendorCategory = 1,    // RED: property does not exist yet
                GcCategory     = 2,
                Expect         = new PredicateExpect { Predicate = "playerHasItem(1601,1)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E23");
        Assert.Contains("mutually exclusive", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // V8 -- E23: VendorCategory + GcRankTier both set -> error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PurchaseItemStep with VendorCategory=1 AND GcRankTier=0,
    /// When Validate() is called,
    /// Then AssertSingleError "E23".
    /// </summary>
    [Fact]
    public void Validate_PurchaseItemStep_VendorCategoryAndGcRankTier_RaisesE23()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("buy-conflict-rank", 2,
            new PurchaseItemStep
            {
                Id             = "buy-conflict-rank",
                Target         = VendorLoc,
                ItemId         = 1601,
                Quantity       = 1,
                Currency       = PurchaseCurrency.GcSeals,
                VendorCategory = 1,    // RED: property does not exist yet
                GcRankTier     = 0,
                Expect         = new PredicateExpect { Predicate = "playerHasItem(1601,1)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E23");
    }

    // =========================================================================
    // V9 -- E23 negative: VendorCategory set, GcCategory/GcRankTier both null -> no E23
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PurchaseItemStep with VendorCategory=5,
    ///   GcCategory=null, GcRankTier=null,
    /// When Validate() is called,
    /// Then AssertClean -- no E23 error.
    /// </summary>
    [Fact]
    public void Validate_PurchaseItemStep_VendorCategoryAlone_NoE23()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("buy-vendor-cat", 2,
            new PurchaseItemStep
            {
                Id             = "buy-vendor-cat",
                Target         = VendorLoc,
                ItemId         = 1601,
                Quantity       = 1,
                Currency       = PurchaseCurrency.Gil,
                VendorCategory = 5,    // RED: property does not exist yet
                GcCategory     = null,
                GcRankTier     = null,
                Expect         = new PredicateExpect { Predicate = "playerHasItem(1601,1)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // V10 -- E23 negative: GcCategory set, VendorCategory null -> no E23
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PurchaseItemStep with GcCategory=2, VendorCategory=null,
    /// When Validate() is called,
    /// Then AssertClean -- no E23 error.
    /// </summary>
    [Fact]
    public void Validate_PurchaseItemStep_GcCategoryAlone_NoE23()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("buy-gc-only", 2,
            new PurchaseItemStep
            {
                Id             = "buy-gc-only",
                Target         = VendorLoc,
                ItemId         = 1601,
                Quantity       = 1,
                Currency       = PurchaseCurrency.GcSeals,
                VendorCategory = null,   // RED: property does not exist yet
                GcCategory     = 2,
                GcRankTier     = 1,
                Expect         = new PredicateExpect { Predicate = "playerHasItem(1601,1)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // V11 -- E24: VendorCategory negative -> error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PurchaseItemStep with VendorCategory=-1,
    /// When Validate() is called,
    /// Then AssertSingleError "E24" and Message contains "-1".
    /// </summary>
    [Fact]
    public void Validate_PurchaseItemStep_VendorCategoryNegative_RaisesE24()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("buy-neg-cat", 2,
            new PurchaseItemStep
            {
                Id             = "buy-neg-cat",
                Target         = VendorLoc,
                ItemId         = 1601,
                Quantity       = 1,
                Currency       = PurchaseCurrency.Gil,
                VendorCategory = -1,   // RED: property does not exist yet
                Expect         = new PredicateExpect { Predicate = "playerHasItem(1601,1)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertSingleError(result, "E24");
        Assert.Contains("-1", result.Errors[0].Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // V12 -- E24 negative: VendorCategory=0 -> no error (zero-based index)
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PurchaseItemStep with VendorCategory=0,
    /// When Validate() is called,
    /// Then AssertClean -- zero is a valid zero-based index.
    /// </summary>
    [Fact]
    public void Validate_PurchaseItemStep_VendorCategoryZero_NoE24()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("buy-cat-zero", 2,
            new PurchaseItemStep
            {
                Id             = "buy-cat-zero",
                Target         = VendorLoc,
                ItemId         = 1601,
                Quantity       = 1,
                Currency       = PurchaseCurrency.Gil,
                VendorCategory = 0,    // RED: property does not exist yet
                Expect         = new PredicateExpect { Predicate = "playerHasItem(1601,1)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }

    // =========================================================================
    // V13 -- E24 negative: VendorCategory=null -> no error
    // =========================================================================

    /// <summary>
    /// Given the baseline plus a PurchaseItemStep with VendorCategory=null,
    /// When Validate() is called,
    /// Then AssertClean -- null means no category menu, no validation needed.
    /// </summary>
    [Fact]
    public void Validate_PurchaseItemStep_VendorCategoryNull_NoE24()
    {
        var draft = DraftValidatorTestData.ValidBaseline();
        draft.AddStep(DraftValidatorTestData.MakeDraftStep("buy-no-cat", 2,
            new PurchaseItemStep
            {
                Id             = "buy-no-cat",
                Target         = VendorLoc,
                ItemId         = 1601,
                Quantity       = 1,
                Currency       = PurchaseCurrency.Gil,
                VendorCategory = null,   // RED: property does not exist yet
                Expect         = new PredicateExpect { Predicate = "playerHasItem(1601,1)" }
            },
            notes: "x"), DraftValidatorTestData.T0);

        var result = new DraftValidator().Validate(draft);

        DraftValidatorAssertions.AssertClean(result);
    }
}
