using QuestForge.Adapters.Items;

namespace QuestForge.Adapters.Tests.Items;

/// <summary>
/// Tests for CofferIdentifier.IsCoffer -- the pure filter predicate that determines
/// whether a Lumina Item row represents a gear coffer.
/// Spec: docs/OPEN_COFFERS_STEP_PLAN.md -- Decision OC-8, scenarios OC_CI1-OC_CI5.
///
/// RED: Will fail to compile until Builder implements CofferIdentifier in
/// QuestForge.Adapters/Items/CofferIdentifier.cs.
///
/// Run with: dotnet test QuestForge.Adapters.Tests --filter "FullyQualifiedName~CofferIdentifierTests"
/// </summary>
public sealed class CofferIdentifierTests
{
    // =========================================================================
    // OC_CI1 -- ItemAction 1085 + Miscellany 61 is a coffer
    // =========================================================================

    [Fact]
    public void IsCoffer_ItemAction1085_Miscellany61_ReturnsTrue_OC_CI1()
    {
        // Given: itemActionRowId = 1085 (standard gear coffer action), itemUiCategoryRowId = 61 (Miscellany)
        // When:  CofferIdentifier.IsCoffer is called
        // Then:  returns true (this is a standard gear coffer)

        Assert.True(CofferIdentifier.IsCoffer(1085, 61));
    }

    // =========================================================================
    // OC_CI2 -- ItemAction 388 + Miscellany 61 is a coffer
    // =========================================================================

    [Fact]
    public void IsCoffer_ItemAction388_Miscellany61_ReturnsTrue_OC_CI2()
    {
        // Given: itemActionRowId = 388 (variant gear coffer action), itemUiCategoryRowId = 61 (Miscellany)
        // When:  CofferIdentifier.IsCoffer is called
        // Then:  returns true (this is a variant gear coffer)

        Assert.True(CofferIdentifier.IsCoffer(388, 61));
    }

    // =========================================================================
    // OC_CI3 -- ItemAction 367 (Triple Triad) + Miscellany 61 is NOT a coffer
    // =========================================================================

    [Fact]
    public void IsCoffer_ItemAction367_Miscellany61_ReturnsFalse_OC_CI3()
    {
        // Given: itemActionRowId = 367 (Triple Triad card pack), itemUiCategoryRowId = 61 (Miscellany)
        // When:  CofferIdentifier.IsCoffer is called
        // Then:  returns false (367 is explicitly excluded -- not a gear coffer)

        Assert.False(CofferIdentifier.IsCoffer(367, 61));
    }

    // =========================================================================
    // OC_CI4 -- ItemAction 1085 + wrong category is NOT a coffer
    // =========================================================================

    [Fact]
    public void IsCoffer_ItemAction1085_WrongCategory_ReturnsFalse_OC_CI4()
    {
        // Given: itemActionRowId = 1085, itemUiCategoryRowId = 10 (not Miscellany)
        // When:  CofferIdentifier.IsCoffer is called
        // Then:  returns false (correct action but wrong category)

        Assert.False(CofferIdentifier.IsCoffer(1085, 10));
    }

    // =========================================================================
    // OC_CI5 -- ItemAction 0 + any category is NOT a coffer
    // =========================================================================

    [Fact]
    public void IsCoffer_ItemAction0_Miscellany61_ReturnsFalse_OC_CI5()
    {
        // Given: itemActionRowId = 0 (no action), itemUiCategoryRowId = 61
        // When:  CofferIdentifier.IsCoffer is called
        // Then:  returns false (0 is not in the coffer action set)

        Assert.False(CofferIdentifier.IsCoffer(0, 61));
    }
}
