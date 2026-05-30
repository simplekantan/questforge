using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Items;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Fakes.Items;

public sealed class FakeItemUser : IItemUser
{
    public record UseItemCall(
        ItemKind Kind,
        uint ItemId,
        NpcId? TargetNpcId,
        Position3? TargetPosition,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<UseItemCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> UseItem(
        ItemKind kind, uint itemId, NpcId? targetNpcId, Position3? targetPosition,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new UseItemCall(kind, itemId, targetNpcId, targetPosition, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
