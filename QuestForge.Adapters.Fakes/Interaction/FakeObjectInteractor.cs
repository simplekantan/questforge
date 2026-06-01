using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Interaction;

public sealed class FakeObjectInteractor : IObjectInteractor
{
    public record InteractCall(InteractableId Target, DateTimeOffset At) : AdapterCall(At);

    public CallLog<InteractCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new InteractCall(target, DateTimeOffset.UtcNow));
        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
