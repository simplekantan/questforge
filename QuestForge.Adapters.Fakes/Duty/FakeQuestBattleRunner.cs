using QuestForge.Adapters.Duty;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Duty;

public sealed class FakeQuestBattleRunner : IQuestBattleRunner
{
    public bool BossModAvailable { get; set; } = true;
    public int StartDutyCallCount { get; private set; }
    public int StopDutyCallCount { get; private set; }
    public int IsBossModAvailableCallCount { get; private set; }

    /// <summary>
    /// When non-null, StartDuty returns this failure reason instead of success.
    /// </summary>
    public string? ScriptedStartFailure { get; set; }

    public void Reset()
    {
        StartDutyCallCount = 0;
        StopDutyCallCount = 0;
        IsBossModAvailableCallCount = 0;
        ScriptedStartFailure = null;
    }

    public Task<Result<bool>> StartDuty(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        StartDutyCallCount++;
        if (ScriptedStartFailure is not null)
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("start-duty-failed", ScriptedStartFailure));
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        StopDutyCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> IsBossModAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IsBossModAvailableCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(BossModAvailable));
    }
}
