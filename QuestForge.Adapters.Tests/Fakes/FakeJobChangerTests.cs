using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Fakes;

/// <summary>
/// Fake contract tests for FakeJobChanger (JC-T1 through JC-T7).
///
/// RED PHASE: All tests fail to compile until Builder implements:
///   - IJobChanger interface (QuestForge.Adapters.Gear)
///   - JobChangeOutcome enum (moved to IJobChanger.cs from IGearManager.cs)
///   - FakeJobChanger (QuestForge.Adapters.Fakes.Gear)
/// </summary>
public sealed class FakeJobChangerTests
{
    private static FakeJobChanger Create() => new FakeJobChanger(); // RED: type does not exist

    // =========================================================================
    // JC-T1 — Default ChangeToJob returns Changed and records call
    // =========================================================================

    [Fact]
    public async Task JC_T1_Default_ChangeToJob_ReturnsChanged_AndRecordsCall()
    {
        var fake = Create();

        var result = await fake.ChangeToJob(new JobId(19), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(JobChangeOutcome.Changed, result.ValueOrThrow);
        Assert.Equal(1, fake.RecordedCalls.Count);
        Assert.Equal(19u, fake.RecordedCalls[0].Job.Value);
    }

    // =========================================================================
    // JC-T2 — ScriptNextResult overrides outcome, consumed after one call
    // =========================================================================

    [Fact]
    public async Task JC_T2_ScriptNextResult_OverridesOutcome_ThenResetsToDefault()
    {
        var fake = Create();
        fake.ScriptNextResult(JobChangeOutcome.GearsetNotFound);

        var first = await fake.ChangeToJob(new JobId(19), CancellationToken.None);
        Assert.Equal(JobChangeOutcome.GearsetNotFound, first.ValueOrThrow);

        var second = await fake.ChangeToJob(new JobId(19), CancellationToken.None);
        Assert.Equal(JobChangeOutcome.Changed, second.ValueOrThrow);
    }

    // =========================================================================
    // JC-T3 — ScriptNextFailure returns failure
    // =========================================================================

    [Fact]
    public async Task JC_T3_ScriptNextFailure_ReturnsFailure()
    {
        var fake = Create();
        fake.ScriptNextFailure("timeout");

        var result = await fake.ChangeToJob(new JobId(19), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.IsType<Result<JobChangeOutcome>.Failure>(result);
        var failure = (Result<JobChangeOutcome>.Failure)result;
        Assert.Equal("timeout", failure.Reason);
    }

    // =========================================================================
    // JC-T4 — GearsetExistsForJob defaults to false
    // =========================================================================

    [Fact]
    public async Task JC_T4_GearsetExistsForJob_DefaultsFalse()
    {
        var fake = Create();

        var result = await fake.GearsetExistsForJob(new JobId(32), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.ValueOrThrow);
    }

    // =========================================================================
    // JC-T5 — AddGearsetForJob makes GearsetExistsForJob return true
    // =========================================================================

    [Fact]
    public async Task JC_T5_AddGearsetForJob_MakesGearsetExistsForJobReturnTrue()
    {
        var fake = Create();
        fake.AddGearsetForJob(new JobId(32));

        var exists = await fake.GearsetExistsForJob(new JobId(32), CancellationToken.None);
        Assert.True(exists.ValueOrThrow);

        // Different job still returns false
        var other = await fake.GearsetExistsForJob(new JobId(19), CancellationToken.None);
        Assert.False(other.ValueOrThrow);
    }

    // =========================================================================
    // JC-T6 — RemoveGearsetForJob reverses AddGearsetForJob
    // =========================================================================

    [Fact]
    public async Task JC_T6_RemoveGearsetForJob_ReversesAdd()
    {
        var fake = Create();
        fake.AddGearsetForJob(new JobId(32));
        fake.RemoveGearsetForJob(new JobId(32));

        var result = await fake.GearsetExistsForJob(new JobId(32), CancellationToken.None);

        Assert.False(result.ValueOrThrow);
    }

    // =========================================================================
    // JC-T7 — Reset clears everything
    // =========================================================================

    [Fact]
    public async Task JC_T7_Reset_ClearsRecordedCalls_AndGearsets()
    {
        var fake = Create();

        // Build up state
        await fake.ChangeToJob(new JobId(19), CancellationToken.None);
        fake.AddGearsetForJob(new JobId(32));

        // Reset
        fake.Reset();

        // Recorded calls cleared
        Assert.Equal(0, fake.RecordedCalls.Count);

        // Gearsets cleared
        var exists = await fake.GearsetExistsForJob(new JobId(32), CancellationToken.None);
        Assert.False(exists.ValueOrThrow);
    }
}
