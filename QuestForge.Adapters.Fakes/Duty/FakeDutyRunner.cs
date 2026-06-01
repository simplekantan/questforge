using QuestForge.Adapters.Duty;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Duty;

public sealed class FakeDutyRunner : IDutyRunner
{
    public bool AutoDutyAvailable { get; set; } = true;
    public int StartDutyCallCount { get; private set; }
    public int StopDutyCallCount { get; private set; }
    public int IsAvailableCallCount { get; private set; }
    public int ContentHasPathCallCount { get; private set; }

    private readonly HashSet<uint> _pathAvailable = new();

    public uint? LastStartedTerritoryType { get; private set; }

    public string? ScriptedStartFailure { get; set; }

    public void SetContentHasPath(uint territoryType, bool hasPath)
    {
        if (hasPath) _pathAvailable.Add(territoryType);
        else _pathAvailable.Remove(territoryType);
    }

    public void Reset()
    {
        StartDutyCallCount = 0;
        StopDutyCallCount = 0;
        IsAvailableCallCount = 0;
        ContentHasPathCallCount = 0;
        LastStartedTerritoryType = null;
        ScriptedStartFailure = null;
        _pathAvailable.Clear();
    }

    public Task<Result<bool>> StartDuty(uint territoryType, CancellationToken ct)
    {
        StartDutyCallCount++;
        LastStartedTerritoryType = territoryType;
        if (ScriptedStartFailure is not null)
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("start-duty-failed", ScriptedStartFailure));
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        StopDutyCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> IsAvailable(CancellationToken ct)
    {
        IsAvailableCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(AutoDutyAvailable));
    }

    public Task<Result<bool>> ContentHasPath(uint territoryType, CancellationToken ct)
    {
        ContentHasPathCallCount++;
        return Task.FromResult<Result<bool>>(
            new Result<bool>.Success(_pathAvailable.Contains(territoryType)));
    }
}
