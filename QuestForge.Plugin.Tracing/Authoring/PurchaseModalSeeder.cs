using QuestForge.Engine.Authoring;

namespace QuestForge.Plugin.Tracing.Authoring;

/// <summary>
/// Pure static helper that converts a <see cref="PurchaseDetection"/> snapshot into
/// the string-pair used to pre-fill the GC-axis InputText controls in
/// <c>RecordStepModal</c> (§14.9.3).
///
/// Extracted from <c>RecordStepModal</c>'s seeding block so it can be unit-tested
/// without a Dalamud dependency — mirroring the <see cref="InteractionPanelFilter"/>
/// and <see cref="PlayerStateFormatter"/> pattern.
///
/// Builder: call <c>var (cat, tier) = PurchaseModalSeeder.SeedGcAxes(pd);</c> from
/// the purchase-item seeding block in <c>RecordStepModal.DrawWaitingState</c>.
/// </summary>
public static class PurchaseModalSeeder
{
    /// <summary>
    /// Returns <c>(gcCategory, gcRankTier)</c> as display strings for InputText controls.
    /// <para>
    /// When <paramref name="pd"/> is non-null and has <see cref="PurchaseDetection.ActiveGcCategory"/> /
    /// <see cref="PurchaseDetection.ActiveGcRankTier"/> set, those values are converted to their
    /// invariant-culture string representations. A null axis maps to <c>""</c> (blank = "no opinion").
    /// </para>
    /// </summary>
    /// <param name="pd">The purchase detection snapshot from <c>GameStateSnapshot.PurchaseDetected</c>. May be null.</param>
    /// <returns>A pair of display strings; both are <c>""</c> when <paramref name="pd"/> is null or axes are null.</returns>
    public static (string GcCategory, string GcRankTier) SeedGcAxes(PurchaseDetection? pd)
    {
        if (pd is null) return ("", "");
        return (
            pd.ActiveGcCategory?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            pd.ActiveGcRankTier?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }
}
