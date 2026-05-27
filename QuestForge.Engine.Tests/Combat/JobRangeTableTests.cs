using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: JobRangeTable unit tests — G1–G7.
///
/// All tests will fail to compile until the builder creates
/// QuestForge.Engine/Combat/JobRangeTable.cs with:
///   - enum CombatRole { Melee, PhysicalRanged, Caster, Healer, Tank, Unknown }
///   - static class JobRangeTable with Classify(JobId), AttackRange(JobId),
///     const float MeleeRange, RangedRange, FallbackRange.
///
/// Spec: docs/COMBAT_MOVE_TO_TARGET_PLAN.md §G1–G7
/// </summary>
public sealed class JobRangeTableTests
{
    // =========================================================================
    // Role → range table (as pinned by spec). Builder fills the full table;
    // tests only assert the rows explicitly listed in the GWT scenarios.
    // =========================================================================

    // G1 — melee classify (PGL, row id 2)
    [Fact]
    public void Classify_PGL_ReturnsMelee()
    {
        var role = JobRangeTable.Classify(new JobId(2));
        Assert.Equal(CombatRole.Melee, role);
    }

    [Fact]
    public void AttackRange_PGL_Returns1m()
    {
        // A2: MeleeRange changed 3.0 -> 1.0 (addendum)
        var range = JobRangeTable.AttackRange(new JobId(2));
        Assert.Equal(1.0f, range);
    }

    // G2 — tank classify (PLD, row id 19)
    [Fact]
    public void Classify_PLD_ReturnsTank()
    {
        var role = JobRangeTable.Classify(new JobId(19));
        Assert.Equal(CombatRole.Tank, role);
    }

    [Fact]
    public void AttackRange_PLD_Returns1m()
    {
        // A2: Tank maps to MeleeRange, which changed 3.0 -> 1.0 (addendum — pins Tank->Melee at new value)
        var range = JobRangeTable.AttackRange(new JobId(19));
        Assert.Equal(1.0f, range);
    }

    // G3 — physical ranged (BRD, row id 23)
    [Fact]
    public void Classify_BRD_ReturnsPhysicalRanged()
    {
        var role = JobRangeTable.Classify(new JobId(23));
        Assert.Equal(CombatRole.PhysicalRanged, role);
    }

    [Fact]
    public void AttackRange_BRD_Returns20m()
    {
        var range = JobRangeTable.AttackRange(new JobId(23));
        Assert.Equal(20.0f, range);
    }

    // G4 — caster (BLM, row id 25)
    [Fact]
    public void Classify_BLM_ReturnsCaster()
    {
        var role = JobRangeTable.Classify(new JobId(25));
        Assert.Equal(CombatRole.Caster, role);
    }

    [Fact]
    public void AttackRange_BLM_Returns20m()
    {
        var range = JobRangeTable.AttackRange(new JobId(25));
        Assert.Equal(20.0f, range);
    }

    // G5 — healer (WHM, row id 24)
    [Fact]
    public void Classify_WHM_ReturnsHealer()
    {
        var role = JobRangeTable.Classify(new JobId(24));
        Assert.Equal(CombatRole.Healer, role);
    }

    [Fact]
    public void AttackRange_WHM_Returns20m()
    {
        var range = JobRangeTable.AttackRange(new JobId(24));
        Assert.Equal(20.0f, range);
    }

    // G6 — unknown fallback (id 999 — unmapped)
    [Fact]
    public void Classify_UnknownId_ReturnsUnknown()
    {
        var role = JobRangeTable.Classify(new JobId(999));
        Assert.Equal(CombatRole.Unknown, role);
    }

    [Fact]
    public void AttackRange_UnknownId_ReturnsFallback1m()
    {
        // A2: FallbackRange changed 3.0 -> 1.0 (addendum)
        var range = JobRangeTable.AttackRange(new JobId(999));
        Assert.Equal(JobRangeTable.FallbackRange, range);
        Assert.Equal(1.0f, range);
    }

    // G7 — all mapped ranges < ScanRadius (50 m)
    // Pins every row id listed in the spec's role table.
    [Theory]
    // Tanks
    [InlineData(1u)]   // GLA
    [InlineData(3u)]   // MRD
    [InlineData(19u)]  // PLD
    [InlineData(21u)]  // WAR
    [InlineData(32u)]  // DRK
    [InlineData(37u)]  // GNB
    // Melee
    [InlineData(2u)]   // PGL
    [InlineData(4u)]   // LNC
    [InlineData(20u)]  // MNK
    [InlineData(22u)]  // DRG
    [InlineData(29u)]  // ROG
    [InlineData(30u)]  // NIN
    [InlineData(34u)]  // SAM
    [InlineData(39u)]  // RPR
    [InlineData(41u)]  // VPR
    // PhysicalRanged
    [InlineData(5u)]   // ARC
    [InlineData(23u)]  // BRD
    [InlineData(31u)]  // MCH
    [InlineData(38u)]  // DNC
    // Caster
    [InlineData(7u)]   // THM
    [InlineData(25u)]  // BLM
    [InlineData(26u)]  // ACN
    [InlineData(27u)]  // SMN
    [InlineData(35u)]  // RDM
    [InlineData(36u)]  // BLU
    [InlineData(42u)]  // PCT
    // Healer
    [InlineData(6u)]   // CNJ
    [InlineData(24u)]  // WHM
    [InlineData(28u)]  // SCH
    [InlineData(33u)]  // AST
    [InlineData(40u)]  // SGE
    public void AttackRange_AllMappedIds_IsUnderScanRadius(uint jobIdValue)
    {
        const float ScanRadius = 50f;
        var range = JobRangeTable.AttackRange(new JobId(jobIdValue));
        Assert.True(range < ScanRadius,
            $"JobId({jobIdValue}) has range {range} m which is >= ScanRadius ({ScanRadius} m)");
    }

    // Constants are exposed on the class and match the canonical values.
    // A2 (addendum): MeleeRange 3.0->1.0, FallbackRange 3.0->1.0, RangedRange unchanged.
    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(1.0f,  JobRangeTable.MeleeRange);
        Assert.Equal(20.0f, JobRangeTable.RangedRange);
        Assert.Equal(1.0f,  JobRangeTable.FallbackRange);
    }
}
