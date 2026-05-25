using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Combat;

/// <summary>
/// Rotation-module lifecycle + target-write surface.
/// The Dalamud implementation (part B) wraps WrathCombo's lease IPC.
/// Direct action use (UseAction*) stays here for the use-action step type.
/// </summary>
public interface ICombat
{
    // ---- rotation module (lease lifecycle; part-B impl wraps WrathCombo IPC) ----
    Task<Result<bool>>               IsRotationModuleAvailable(CancellationToken ct);
    Task<Result<RotationModuleInfo>> GetRotationModule(CancellationToken ct);
    Task<Result<Unit>>               StartRotation(CancellationToken ct);   // acquire lease + auto-rotation on
    Task<Result<Unit>>               StopRotation(CancellationToken ct);    // release lease

    // ---- targeting (CombatController drives this; part-B impl writes TargetManager.Target) ----
    Task<Result<Unit>> SetTarget(ActorId target, CancellationToken ct);
    Task<Result<Unit>> ClearTarget(CancellationToken ct);

    // ---- direct action use (use-action step type — kept here to avoid churn in part A) ----
    Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct);
    Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct);
    Task<Result<bool>>             IsActionUsable(uint actionId, CancellationToken ct);
}

public record RotationModuleInfo(string Name, string Version, bool LeaseHeld);

public enum UseActionOutcome
{
    Executed,
    ActionNotLearned,
    ActionNotUsable,
    OnCooldown,
    TargetOutOfRange,
    TargetInvalid,
    InsufficientMP,
    Failed
}
