using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Combat;

public interface ICombat
{
    // Delegated combat — combat plugin chooses rotation
    Task<Result<CombatOutcome>> EngageTarget(NpcId target, CancellationToken ct);
    Task<Result<CombatOutcome>> EngageNearestHostile(float radius, CancellationToken ct);
    Task<Result<Unit>> Disengage(CancellationToken ct);
    Task<Result<bool>> IsCombatPluginAvailable(CancellationToken ct);
    Task<Result<CombatPluginInfo>> GetActiveCombatPlugin(CancellationToken ct);

    // Direct action use — engine specifies action and target
    Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct);
    Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct);
    Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct);
}

public record CombatPluginInfo(
    string Name,
    string Version,
    bool SupportsTargetEngagement,
    bool SupportsAutoTargeting
);

public enum CombatOutcome
{
    TargetDefeated,
    PlayerDefeated,
    Disengaged,
    TargetNotFound,
    NoCombatPlugin,
    Interrupted
}

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