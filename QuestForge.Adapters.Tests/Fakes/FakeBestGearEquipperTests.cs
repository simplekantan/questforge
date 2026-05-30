using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Fake contract tests for FakeBestGearEquipper (BE-T1 through BE-T6).
///
/// RED PHASE: All tests fail to compile until Builder implements:
///   - IBestGearEquipper interface (QuestForge.Adapters.Gear)
///   - FakeBestGearEquipper (QuestForge.Adapters.Fakes.Gear)
///   - EquipOutcome enum (shared from IGearEquipper.cs)
/// </summary>
public sealed class FakeBestGearEquipperTests
{
    private static FakeBestGearEquipper Create() => new FakeBestGearEquipper(); // RED: type does not exist

    // =========================================================================
    // BE-T1 — Default EquipBestGear returns Equipped and records call
    // =========================================================================

    [Fact]
    public async Task BE_T1_Default_EquipBestGear_ReturnsEquipped_AndRecordsCall()
    {
        var fake = Create();

        var result = await fake.EquipBestGear(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(EquipOutcome.Equipped, result.ValueOrThrow);
        Assert.Equal(1, fake.RecordedCalls.Count);
    }

    // =========================================================================
    // BE-T2 — ScriptNextResult overrides outcome, consumed after one call
    // =========================================================================

    [Fact]
    public async Task BE_T2_ScriptNextResult_OverridesOutcome_ThenResetsToDefault()
    {
        var fake = Create();
        fake.ScriptNextResult(EquipOutcome.NoChange);

        var first = await fake.EquipBestGear(CancellationToken.None);
        Assert.Equal(EquipOutcome.NoChange, first.ValueOrThrow);

        var second = await fake.EquipBestGear(CancellationToken.None);
        Assert.Equal(EquipOutcome.Equipped, second.ValueOrThrow);
    }

    // =========================================================================
    // BE-T3 — ScriptNextFailure returns failure
    // =========================================================================

    [Fact]
    public async Task BE_T3_ScriptNextFailure_ReturnsFailure()
    {
        var fake = Create();
        fake.ScriptNextFailure("unavailable");

        var result = await fake.EquipBestGear(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.IsType<Result<EquipOutcome>.Failure>(result);
        var failure = (Result<EquipOutcome>.Failure)result;
        Assert.Equal("unavailable", failure.Reason);
    }

    // =========================================================================
    // BE-T4 — IsStylistAvailable defaults to false
    // =========================================================================

    [Fact]
    public async Task BE_T4_IsStylistAvailable_DefaultsFalse()
    {
        var fake = Create();

        var result = await fake.IsStylistAvailable(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.ValueOrThrow);
    }

    // =========================================================================
    // BE-T5 — SetStylistAvailable makes IsStylistAvailable return true
    // =========================================================================

    [Fact]
    public async Task BE_T5_SetStylistAvailable_MakesIsStylistAvailableReturnTrue()
    {
        var fake = Create();
        fake.SetStylistAvailable(true);

        var result = await fake.IsStylistAvailable(CancellationToken.None);

        Assert.True(result.ValueOrThrow);
    }

    // =========================================================================
    // BE-T6 — Reset clears everything
    // =========================================================================

    [Fact]
    public async Task BE_T6_Reset_ClearsRecordedCalls_AndStylistState()
    {
        var fake = Create();

        // Build up state
        await fake.EquipBestGear(CancellationToken.None);
        fake.SetStylistAvailable(true);

        // Reset
        fake.Reset();

        // Recorded calls cleared
        Assert.Equal(0, fake.RecordedCalls.Count);

        // Stylist available reset to false
        var stylist = await fake.IsStylistAvailable(CancellationToken.None);
        Assert.False(stylist.ValueOrThrow);
    }
}
