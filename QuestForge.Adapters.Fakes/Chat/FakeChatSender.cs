using QuestForge.Adapters.Chat;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Chat;

public sealed class FakeChatSender : IChatSender
{
    public record SendCall(
        string Message,
        NpcId? TargetNpcId,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<SendCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> Send(string message, NpcId? targetNpcId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new SendCall(message, targetNpcId, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
