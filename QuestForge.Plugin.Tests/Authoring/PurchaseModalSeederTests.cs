using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing.Authoring;
using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// RED PHASE — G5a modal-seeding tests (G-S6).
//
// Tests the pure static helper PurchaseModalSeeder.SeedGcAxes, which converts
// a PurchaseDetection into the (gcCategory, gcRankTier) string pair used to
// pre-fill the InputText controls in RecordStepModal (§14.9.3).
//
// RED: PurchaseModalSeeder.SeedGcAxes is a no-op stub returning ("", "").
// It will fail GS6_WithNonNullAxes but trivially pass GS6_WithNullAxes.
//
// Builder: implement SeedGcAxes to read pd.ActiveGcCategory/ActiveGcRankTier
// and format non-null values with InvariantCulture.ToString(); null → "".
// Then call it from RecordStepModal's purchase-item seeding block (§14.9.3).
//
// Run with:
//   $env:PATH = "C:\Users\publi\.dotnet;" + $env:PATH
//   & "C:\Users\publi\.dotnet\dotnet.exe" test QuestForge.Plugin.Tests --filter "FullyQualifiedName~PurchaseModalSeeder"
// ─────────────────────────────────────────────────────────────────────────────

namespace QuestForge.Plugin.Tests.Authoring;

public sealed class PurchaseModalSeederTests
{
    private static string FormatResult((string GcCategory, string GcRankTier) r)
        => $"(GcCategory=\"{r.GcCategory}\", GcRankTier=\"{r.GcRankTier}\")";

    // ─────────────────────────────────────────────────────────────────────────
    // G-S6 (positive) — non-null axes → InputText fields show their values
    //
    // RED: SeedGcAxes returns ("", "") stub; fails the non-empty assertions.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GS6_RecordStepModal_SeedsInputs_WithNonNullAxes()
    {
        // CONTRACT (§14.9.3 + §14.9.6 G-S6):
        // Given PurchaseDetection with ActiveGcCategory=2, ActiveGcRankTier=1
        // When PurchaseModalSeeder.SeedGcAxes(pd) is called (mirroring the modal seeding block)
        // Then GcCategory=="2" AND GcRankTier=="1"
        // (mirrors _editPurchaseGcCategory and _editPurchaseGcRankTier after seeding).

        var pd = new PurchaseDetection(
            ShopWasOpen:      true,
            ItemDeltas:       new Dictionary<uint, int> { [6141u] = 1 },
            GilDropped:       0L,
            SealsDropped:     600,
            ActiveGcCategory: 2,
            ActiveGcRankTier: 1);

        var result = PurchaseModalSeeder.SeedGcAxes(pd);

        // RED: stub returns ("", "") → both assertions fail
        Assert.Equal("2", result.GcCategory);
        Assert.Equal("1", result.GcRankTier);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // G-S6 (negative) — null axes → InputText fields are blank
    //
    // GREEN trivially: stub returns ("", "") which matches the expected "".
    // This assertion holds even without Builder work — documents the null→"" contract.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GS6_RecordStepModal_SeedsInputs_WithNullAxes_BothBlank()
    {
        // CONTRACT (§14.9.3 + §14.9.6 G-S6 null case):
        // Given PurchaseDetection with both axes null
        // When SeedGcAxes(pd) is called
        // Then GcCategory=="" AND GcRankTier=="" (blank inputs = "no opinion").

        var pd = new PurchaseDetection(
            ShopWasOpen:      true,
            ItemDeltas:       new Dictionary<uint, int> { [1601u] = 1 },
            GilDropped:       1000L,
            SealsDropped:     0,
            ActiveGcCategory: null,
            ActiveGcRankTier: null);

        var result = PurchaseModalSeeder.SeedGcAxes(pd);

        Assert.Equal("", result.GcCategory);
        Assert.Equal("", result.GcRankTier);
    }

    [Fact]
    public void GS6_RecordStepModal_SeedsInputs_NullPurchaseDetection_BothBlank()
    {
        // CONTRACT: When pd is null (no purchase detected), both fields are "".

        var result = PurchaseModalSeeder.SeedGcAxes(null);

        Assert.Equal("", result.GcCategory);
        Assert.Equal("", result.GcRankTier);
    }
}
