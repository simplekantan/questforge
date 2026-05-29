using QuestForge.Adapters.Actions;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Fakes.Actions;

public sealed class FakeActionExecutor : IActionExecutor
{
    // ---- recording ----

    public record UseActionCall(
        ActionType Type,
        uint ActionId,
        NpcId? TargetNpcId,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<UseActionCall> RecordedCalls { get; } = new();

    // ---- scripting ----

    private ActionStatus _nextStatus = new ActionStatus.Ready();
    private (string Reason, string? Detail)? _nextFailure;
    private (string Reason, string? Detail)? _nextStatusFailure;

    /// <summary>Sets the status to return on subsequent GetActionStatus calls (sticky until changed).</summary>
    public void ScriptNextStatus(ActionStatus status) => _nextStatus = status;

    /// <summary>Forces UseAction to return Result.Failure on the next call only (then resets).</summary>
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    /// <summary>Forces GetActionStatus to return Result.Failure on the next call only (then resets).</summary>
    public void ScriptNextStatusFailure(string reason, string? detail = null)
        => _nextStatusFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextStatus = new ActionStatus.Ready();
        _nextFailure = null;
        _nextStatusFailure = null;
    }

    // ---- IActionExecutor ----

    public Task<Result<Unit>> UseAction(
        ActionType type, uint actionId, NpcId? targetNpcId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new UseActionCall(type, actionId, targetNpcId, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<ActionStatus>> GetActionStatus(
        ActionType type, uint actionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_nextStatusFailure is { } f)
        {
            _nextStatusFailure = null;
            return Task.FromResult<Result<ActionStatus>>(Result.Fail<ActionStatus>(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<ActionStatus>>(Result.Ok(_nextStatus));
    }
}
