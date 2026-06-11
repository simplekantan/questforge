using System.Globalization;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Plugin.Tracing.Authoring;
using Xunit;

namespace QuestForge.Plugin.Tests.Authoring;

/// <summary>
/// RED PHASE — All tests fail to compile until PlayerStateFormatter is created.
///
/// Tests target the pure static class:
///   QuestForge.Plugin.Tracing.Authoring.PlayerStateFormatter
/// in QuestForge.Plugin.Tracing/Authoring/PlayerStateFormatter.cs
///
/// Scenario ids match Task 3 (GWT) of PLAYER_STATE_PANEL_PLAN.md.
///
/// Run with: dotnet test QuestForge.Plugin.Tests/QuestForge.Plugin.Tests.csproj --configuration Debug
/// </summary>
public sealed class PlayerStateFormatterTests
{
    // -------------------------------------------------------------------------
    // Helper — formats all errors in a single assertion failure message
    // -------------------------------------------------------------------------

    private static string FormatResult(string actual, string expected)
        => $"Expected: \"{expected}\", Actual: \"{actual}\"";

    // =========================================================================
    // FormatJob
    // =========================================================================

    // GWT: Happy — resolved name
    // Given JobId(32), name "Dark Knight", level 30
    // When  FormatJob is called
    // Then  returns "Job: Dark Knight (32), Lv 30"
    [Fact]
    public void FormatJob_ResolvedName_ReturnsNameIdAndLevel()
    {
        // Given
        var job = new JobId(32);
        const string name = "Dark Knight";
        const int level = 30;

        // When
        string result = PlayerStateFormatter.FormatJob(job, name, level);

        // Then
        const string expected = "Job: Dark Knight (32), Lv 30";
        Assert.Equal(expected, result);
    }

    // GWT: Edge — name is null
    // Given JobId(32), name null, level 30
    // When  FormatJob is called
    // Then  returns "Job: (32), Lv 30" (id-only fallback)
    [Fact]
    public void FormatJob_NullName_ReturnsIdOnlyFallback()
    {
        // Given
        var job = new JobId(32);
        const string? name = null;
        const int level = 30;

        // When
        string result = PlayerStateFormatter.FormatJob(job, name, level);

        // Then
        const string expected = "Job: (32), Lv 30";
        Assert.Equal(expected, result);
    }

    // GWT: Edge — name is empty string (treated as unresolved)
    // Given JobId(32), name "", level 30
    // When  FormatJob is called
    // Then  returns "Job: (32), Lv 30" (same fallback as null)
    [Fact]
    public void FormatJob_EmptyName_ReturnsIdOnlyFallback()
    {
        // Given
        var job = new JobId(32);
        const string name = "";
        const int level = 30;

        // When
        string result = PlayerStateFormatter.FormatJob(job, name, level);

        // Then
        const string expected = "Job: (32), Lv 30";
        Assert.Equal(expected, result);
    }

    // GWT: Edge — no job loaded (id == 0)
    // Given JobId(0), name null, level 0
    // When  FormatJob is called
    // Then  returns "Job: (none)"
    [Fact]
    public void FormatJob_JobIdZero_ReturnsNone()
    {
        // Given
        var job = new JobId(0);
        const string? name = null;
        const int level = 0;

        // When
        string result = PlayerStateFormatter.FormatJob(job, name, level);

        // Then
        const string expected = "Job: (none)";
        Assert.Equal(expected, result);
    }

    // =========================================================================
    // FormatPosition
    // =========================================================================

    // GWT: Happy — standard coords
    // Given WorldPosition(157.94f, -19.48f, 53.02f)
    // When  FormatPosition is called
    // Then  returns "Position: (157.94, -19.48, 53.02)"
    [Fact]
    public void FormatPosition_StandardCoords_ReturnsTwoDpInvariantCulture()
    {
        // Given
        var pos = new WorldPosition(157.94f, -19.48f, 53.02f);

        // When
        string result = PlayerStateFormatter.FormatPosition(pos);

        // Then
        const string expected = "Position: (157.94, -19.48, 53.02)";
        Assert.Equal(expected, result);
    }

    // GWT: Edge — invariant culture guard (1.5f, 2f, 3f must not produce commas as decimal separator)
    // Given WorldPosition(1.5f, 2f, 3f)
    // When  FormatPosition is called (even on a comma-decimal locale)
    // Then  returns "Position: (1.50, 2.00, 3.00)" with dot separators
    [Fact]
    public void FormatPosition_InvariantCulture_UsesDotSeparator()
    {
        // Given
        var pos = new WorldPosition(1.5f, 2f, 3f);

        // When
        string result = PlayerStateFormatter.FormatPosition(pos);

        // Then — must contain a dot, never a comma, for decimal
        const string expected = "Position: (1.50, 2.00, 3.00)";
        Assert.Equal(expected, result);
        // Belt-and-suspenders: the number part must use '.' not ',' for decimals
        Assert.DoesNotContain("1,50", result);
    }

    // GWT: Edge — rounding (0.005f rounds to 0.01 under F2 / round-half-even)
    // Given WorldPosition(0.005f, 0f, 0f)
    // When  FormatPosition is called
    // Then  returns the exact F2 / invariant-culture string (pinned to guard regressions)
    [Fact]
    public void FormatPosition_Rounding_PinsF2Output()
    {
        // Given
        var pos = new WorldPosition(0.005f, 0f, 0f);

        // When
        string result = PlayerStateFormatter.FormatPosition(pos);

        // Then — pin exactly what F2 + InvariantCulture produces so any change is visible
        string expected = "Position: ("
            + (0.005f).ToString("F2", CultureInfo.InvariantCulture) + ", "
            + (0f).ToString("F2", CultureInfo.InvariantCulture) + ", "
            + (0f).ToString("F2", CultureInfo.InvariantCulture) + ")";
        Assert.Equal(expected, result);
    }

    // =========================================================================
    // FormatPositionJson
    // =========================================================================

    // GWT: Happy — clipboard JSON shape
    // Given WorldPosition(157.94f, -19.48f, 53.02f)
    // When  FormatPositionJson is called
    // Then  returns {"x": 157.94, "y": -19.48, "z": 53.02}
    [Fact]
    public void FormatPositionJson_StandardCoords_ReturnsJsonShape()
    {
        // Given
        var pos = new WorldPosition(157.94f, -19.48f, 53.02f);

        // When
        string result = PlayerStateFormatter.FormatPositionJson(pos);

        // Then
        const string expected = """{"x": 157.94, "y": -19.48, "z": 53.02}""";
        Assert.Equal(expected, result);
    }

    // GWT: Edge — invariant culture in JSON
    // Given WorldPosition(1.5f, 2f, 3f)
    // When  FormatPositionJson is called
    // Then  numbers use dot decimal separator
    [Fact]
    public void FormatPositionJson_InvariantCulture_UsesDotSeparator()
    {
        // Given
        var pos = new WorldPosition(1.5f, 2f, 3f);

        // When
        string result = PlayerStateFormatter.FormatPositionJson(pos);

        // Then
        const string expected = """{"x": 1.50, "y": 2.00, "z": 3.00}""";
        Assert.Equal(expected, result);
        Assert.DoesNotContain("1,50", result);
    }

    // =========================================================================
    // FormatZone
    // =========================================================================

    // GWT: Happy — zone id 132
    // Given uint 132
    // When  FormatZone is called
    // Then  returns "Zone: 132"
    [Fact]
    public void FormatZone_StandardId_ReturnsZoneLine()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatZone(132u);

        // Then
        Assert.Equal("Zone: 132", result);
    }

    // GWT: Edge — zone id 0 (not loaded); no special-casing, raw value shown
    // Given uint 0
    // When  FormatZone is called
    // Then  returns "Zone: 0"
    [Fact]
    public void FormatZone_ZeroId_ReturnsZeroNoSpecialCase()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatZone(0u);

        // Then
        Assert.Equal("Zone: 0", result);
    }

    // =========================================================================
    // FormatCfc
    // =========================================================================

    [Fact]
    public void FormatCfc_StandardId_ReturnsCfcLine()
    {
        string result = PlayerStateFormatter.FormatCfc(830);
        Assert.Equal("CFC: 830", result);
    }

    [Fact]
    public void FormatCfc_ZeroId_ReturnsZeroNoSpecialCase()
    {
        string result = PlayerStateFormatter.FormatCfc(0);
        Assert.Equal("CFC: 0", result);
    }

    // =========================================================================
    // FormatMount
    // =========================================================================

    // GWT: Theory — each MountState enum value produces "Mount: <EnumName>"
    [Theory]
    [InlineData(MountState.Dismounted, "Mount: Dismounted")]
    [InlineData(MountState.Mounted,    "Mount: Mounted")]
    [InlineData(MountState.Flying,     "Mount: Flying")]
    public void FormatMount_EnumValue_ProducesLabelledEnumName(MountState mount, string expected)
    {
        // Given a MountState value
        // When
        string result = PlayerStateFormatter.FormatMount(mount);

        // Then
        Assert.Equal(expected, result);
    }

    // =========================================================================
    // FormatCombat
    // =========================================================================

    // GWT: in combat → "Combat: Yes"
    [Fact]
    public void FormatCombat_True_ReturnsYes()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatCombat(true);

        // Then
        Assert.Equal("Combat: Yes", result);
    }

    // GWT: not in combat → "Combat: No"
    [Fact]
    public void FormatCombat_False_ReturnsNo()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatCombat(false);

        // Then
        Assert.Equal("Combat: No", result);
    }

    // =========================================================================
    // FormatHp
    // =========================================================================

    // GWT: Happy — full HP (current == max, both 11420)
    [Fact]
    public void FormatHp_FullHp_ReturnsCurrentSlashMax()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatHp(11420, 11420);

        // Then
        Assert.Equal("HP: 11420 / 11420", result);
    }

    // GWT: Happy — partial HP (dead but loaded)
    [Fact]
    public void FormatHp_PartialHp_ReturnsCurrentSlashMax()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatHp(8000, 11420);

        // Then
        Assert.Equal("HP: 8000 / 11420", result);
    }

    // GWT: Edge — zero current, alive max (character dead; max > 0 means HP is known)
    [Fact]
    public void FormatHp_ZeroCurrentAliveMax_ReturnsCurrentSlashMax()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatHp(0, 11420);

        // Then — max > 0 means data is available; show 0 / 11420, not "unknown"
        Assert.Equal("HP: 0 / 11420", result);
    }

    // GWT: Edge — 0/0 (HP source unavailable; deferred-HP / pre-login path)
    [Fact]
    public void FormatHp_BothZero_ReturnsUnknown()
    {
        // Given / When
        string result = PlayerStateFormatter.FormatHp(0, 0);

        // Then
        Assert.Equal("HP: (unknown)", result);
    }

    // =========================================================================
    // FormatInstance
    // =========================================================================

    // GWT: Theory — each InstanceKind enum value produces "Instance: <EnumName>"
    [Theory]
    [InlineData(InstanceKind.None,             "Instance: None")]
    [InlineData(InstanceKind.Dungeon,          "Instance: Dungeon")]
    [InlineData(InstanceKind.Trial,            "Instance: Trial")]
    [InlineData(InstanceKind.Raid,             "Instance: Raid")]
    [InlineData(InstanceKind.AllianceRaid,     "Instance: AllianceRaid")]
    [InlineData(InstanceKind.SinglePlayerDuty, "Instance: SinglePlayerDuty")]
    [InlineData(InstanceKind.PvP,              "Instance: PvP")]
    [InlineData(InstanceKind.DeepDungeon,      "Instance: DeepDungeon")]
    [InlineData(InstanceKind.VariantDungeon,   "Instance: VariantDungeon")]
    [InlineData(InstanceKind.Other,            "Instance: Other")]
    public void FormatInstance_EnumValue_ProducesLabelledEnumName(InstanceKind kind, string expected)
    {
        // Given an InstanceKind value
        // When
        string result = PlayerStateFormatter.FormatInstance(kind);

        // Then
        Assert.Equal(expected, result);
    }
}
