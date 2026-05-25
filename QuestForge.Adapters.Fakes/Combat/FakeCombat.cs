using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Combat;

public sealed class FakeCombat : ICombat
{
    private bool _rotationModuleAvailable = true;

    // ---- Call logs ----

    public record TargetCall(ActorId? Target, bool IsClear, DateTimeOffset At) : AdapterCall(At);
    public record RotationCall(string Method, DateTimeOffset At) : AdapterCall(At);

    /// <summary>All SetTarget / ClearTarget calls in order.</summary>
    public CallLog<TargetCall>   RecordedTargets  { get; } = new();

    /// <summary>All StartRotation / StopRotation calls in order.</summary>
    public CallLog<RotationCall> RecordedRotation { get; } = new();

    // ---- Scripting ----

    public void SetRotationModuleAvailable(bool available)
        => _rotationModuleAvailable = available;

    // ---- Reset ----

    public void Reset()
    {
        RecordedTargets.Clear();
        RecordedRotation.Clear();
    }

    // ---- ICombat — rotation module ----

    public Task<Result<bool>> IsRotationModuleAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(_rotationModuleAvailable));
    }

    public Task<Result<RotationModuleInfo>> GetRotationModule(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<RotationModuleInfo>>(
            Result.Ok(new RotationModuleInfo("FakeCombat", "0.0.0", LeaseHeld: false)));
    }

    public Task<Result<Unit>> StartRotation(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedRotation.Add(new RotationCall(nameof(StartRotation), DateTimeOffset.UtcNow));
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> StopRotation(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedRotation.Add(new RotationCall(nameof(StopRotation), DateTimeOffset.UtcNow));
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    // ---- ICombat — targeting ----

    public Task<Result<Unit>> SetTarget(ActorId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedTargets.Add(new TargetCall(target, IsClear: false, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> ClearTarget(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedTargets.Add(new TargetCall(null, IsClear: true, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    // ---- ICombat — direct action use (use-action step type, unchanged) ----

    public Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Executed));
    }

    public Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Executed));
    }

    public Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }
}
