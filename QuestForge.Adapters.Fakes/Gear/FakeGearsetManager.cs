using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Gear;

public sealed class FakeGearsetManager : IGearsetManager
{
    public record RegisterGearsetCall(DateTimeOffset At) : AdapterCall(At);

    public CallLog<RegisterGearsetCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;
    private RegisterOutcome? _nextOutcome;

    public void ScriptNextResult(RegisterOutcome outcome) => _nextOutcome = outcome;
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
        _nextOutcome = null;
    }

    public Task<Result<RegisterOutcome>> RegisterGearset(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new RegisterGearsetCall(DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<RegisterOutcome>>(Result.Fail<RegisterOutcome>(f.Reason, f.Detail));
        }

        var outcome = _nextOutcome ?? RegisterOutcome.Registered;
        _nextOutcome = null;
        return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(outcome));
    }
}
