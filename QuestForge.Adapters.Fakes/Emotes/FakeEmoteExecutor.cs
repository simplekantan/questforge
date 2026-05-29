using QuestForge.Adapters.Emotes;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Emotes;

public sealed class FakeEmoteExecutor : IEmoteExecutor
{
    public record UseEmoteCall(
        uint EmoteId,
        NpcId? TargetNpcId,
        bool Motion,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<UseEmoteCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> UseEmote(
        uint emoteId, NpcId? targetNpcId, bool motion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new UseEmoteCall(emoteId, targetNpcId, motion, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
