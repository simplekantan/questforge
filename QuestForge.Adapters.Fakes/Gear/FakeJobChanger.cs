using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Gear;

public sealed class FakeJobChanger : IJobChanger
{
    public record ChangeJobCall(JobId Job, DateTimeOffset At) : AdapterCall(At);

    public CallLog<ChangeJobCall> RecordedCalls { get; } = new();

    private readonly HashSet<uint> _availableGearsets = new();
    private (string Reason, string? Detail)? _nextFailure;
    private JobChangeOutcome? _nextOutcome;

    public void ScriptNextResult(JobChangeOutcome outcome) => _nextOutcome = outcome;
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);
    public void AddGearsetForJob(JobId job) => _availableGearsets.Add(job.Value);
    public void RemoveGearsetForJob(JobId job) => _availableGearsets.Remove(job.Value);

    public void Reset()
    {
        RecordedCalls.Clear();
        _availableGearsets.Clear();
        _nextFailure = null;
        _nextOutcome = null;
    }

    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new ChangeJobCall(job, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<JobChangeOutcome>>(Result.Fail<JobChangeOutcome>(f.Reason, f.Detail));
        }

        var outcome = _nextOutcome ?? JobChangeOutcome.Changed;
        _nextOutcome = null;
        return Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(_availableGearsets.Contains(job.Value)));
    }
}
