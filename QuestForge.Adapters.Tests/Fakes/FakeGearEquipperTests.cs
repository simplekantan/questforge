using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Fake contract tests for FakeGearEquipper (GE-T1 through GE-T7).
///
/// RED PHASE: All tests fail to compile until Builder implements:
///   - IGearEquipper interface (QuestForge.Adapters.Gear)
///   - EquipOutcome enum (moved to IGearEquipper.cs from IGearManager.cs)
///   - FakeGearEquipper (QuestForge.Adapters.Fakes.Gear)
/// </summary>
public sealed class FakeGearEquipperTests
{
    private static FakeGearEquipper Create() => new FakeGearEquipper(); // RED: type does not exist

    // =========================================================================
    // GE-T1 — Default EquipItem returns Equipped and records call
    // =========================================================================

    [Fact]
    public async Task GE_T1_Default_EquipItem_ReturnsEquipped_AndRecordsCall()
    {
        var fake = Create();

        var result = await fake.EquipItem(12345u, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(EquipOutcome.Equipped, result.ValueOrThrow);
        Assert.Equal(1, fake.RecordedCalls.Count);
        Assert.Equal(12345u, fake.RecordedCalls[0].ItemId);
    }

    // =========================================================================
    // GE-T2 — ScriptNextResult overrides outcome, consumed after one call
    // =========================================================================

    [Fact]
    public async Task GE_T2_ScriptNextResult_OverridesOutcome_ThenResetsToDefault()
    {
        var fake = Create();
        fake.ScriptNextResult(EquipOutcome.InCombat);

        // First call uses the scripted outcome
        var first = await fake.EquipItem(1u, CancellationToken.None);
        Assert.Equal(EquipOutcome.InCombat, first.ValueOrThrow);

        // Second call (no new script) reverts to default
        var second = await fake.EquipItem(2u, CancellationToken.None);
        Assert.Equal(EquipOutcome.Equipped, second.ValueOrThrow);
    }

    // =========================================================================
    // GE-T3 — ScriptNextFailure returns failure result, call IS recorded
    // =========================================================================

    [Fact]
    public async Task GE_T3_ScriptNextFailure_ReturnsFailure_CallIsRecorded()
    {
        var fake = Create();
        fake.ScriptNextFailure("inCombat", "cannot equip while fighting");

        var result = await fake.EquipItem(1u, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.IsType<Result<EquipOutcome>.Failure>(result);
        var failure = (Result<EquipOutcome>.Failure)result;
        Assert.Equal("inCombat", failure.Reason);
        Assert.Equal("cannot equip while fighting", failure.Detail);
        Assert.Equal(1, fake.RecordedCalls.Count);
    }

    // =========================================================================
    // GE-T4 — IsItemEquipped defaults to false
    // =========================================================================

    [Fact]
    public async Task GE_T4_IsItemEquipped_DefaultsFalse()
    {
        var fake = Create();

        var result = await fake.IsItemEquipped(999u, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.ValueOrThrow);
    }

    // =========================================================================
    // GE-T5 — SetItemEquipped makes IsItemEquipped return true
    // =========================================================================

    [Fact]
    public async Task GE_T5_SetItemEquipped_MakesIsItemEquippedReturnTrue()
    {
        var fake = Create();
        fake.SetItemEquipped(42u, true);

        var equipped = await fake.IsItemEquipped(42u, CancellationToken.None);
        Assert.True(equipped.ValueOrThrow);

        // Different item still returns false
        var other = await fake.IsItemEquipped(99u, CancellationToken.None);
        Assert.False(other.ValueOrThrow);
    }

    // =========================================================================
    // GE-T6 — Reset clears everything
    // =========================================================================

    [Fact]
    public async Task GE_T6_Reset_ClearsRecordedCalls_ScriptedState_AndEquippedItems()
    {
        var fake = Create();

        // Build up state
        await fake.EquipItem(1u, CancellationToken.None);
        fake.SetItemEquipped(1u, true);
        fake.ScriptNextResult(EquipOutcome.InCombat);

        // Reset
        fake.Reset();

        // Recorded calls cleared
        Assert.Equal(0, fake.RecordedCalls.Count);

        // Equipped items cleared
        var equipped = await fake.IsItemEquipped(1u, CancellationToken.None);
        Assert.False(equipped.ValueOrThrow);

        // Scripted outcome cleared (default restored)
        var result = await fake.EquipItem(2u, CancellationToken.None);
        Assert.Equal(EquipOutcome.Equipped, result.ValueOrThrow);
    }

    // =========================================================================
    // GE-T7 — CancellationToken cancellation throws
    // =========================================================================

    [Fact]
    public async Task GE_T7_CancelledToken_ThrowsOperationCancelledException()
    {
        var fake = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fake.EquipItem(1u, cts.Token));
    }
}
