using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Combat;

/// <summary>
/// Part A stub — reshapes to the new ICombat surface.
/// All rotation-module and targeting methods return stubs until WrathCombo IPC is wired in part B.
/// </summary>
public sealed class WrathComboAdapter : ICombat
{
    public WrathComboAdapter(PluginServices svc) { }

    // ---- rotation module (stubs — IPC wiring in part B) ----

    public Task<Result<bool>> IsRotationModuleAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    public Task<Result<RotationModuleInfo>> GetRotationModule(CancellationToken ct)
        => Task.FromResult<Result<RotationModuleInfo>>(
            Result.Fail<RotationModuleInfo>("notAvailable", "WrathCombo IPC not wired in part A"));

    public Task<Result<Unit>> StartRotation(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok()); // IPC wiring in part B

    public Task<Result<Unit>> StopRotation(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok()); // IPC wiring in part B

    // ---- targeting (stubs — TargetManager writes in part B) ----

    public Task<Result<Unit>> SetTarget(ActorId target, CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok()); // IPC wiring in part B

    public Task<Result<Unit>> ClearTarget(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok()); // IPC wiring in part B

    // ---- direct action use (use-action step type — unchanged) ----

    public Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    public Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    public Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
