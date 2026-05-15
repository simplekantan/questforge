using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Combat;

// Phase 6 placeholder — quest 66130 never enters combat.
// WrathCombo IPC wiring deferred to a later phase.
public sealed class WrathComboAdapter : ICombat
{
    public WrathComboAdapter(PluginServices svc) { }

    // IsCombatPluginAvailable — no combat plugin wired in Phase 6
    public Task<Result<bool>> IsCombatPluginAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    // GetActiveCombatPlugin — no active plugin
    public Task<Result<CombatPluginInfo>> GetActiveCombatPlugin(CancellationToken ct)
        => Task.FromResult<Result<CombatPluginInfo>>(
            Result.Fail<CombatPluginInfo>("noCombatPlugin", "Phase 6 placeholder: WrathCombo IPC not wired"));

    // EngageTarget — not supported without combat plugin
    public Task<Result<CombatOutcome>> EngageTarget(NpcId target, CancellationToken ct)
        => Task.FromResult<Result<CombatOutcome>>(Result.Ok(CombatOutcome.NoCombatPlugin));

    // EngageNearestHostile — not supported without combat plugin
    public Task<Result<CombatOutcome>> EngageNearestHostile(float radius, CancellationToken ct)
        => Task.FromResult<Result<CombatOutcome>>(Result.Ok(CombatOutcome.NoCombatPlugin));

    // Disengage — no-op; nothing to disengage from
    public Task<Result<Unit>> Disengage(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    // UseAction — no combat plugin to delegate to
    public Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    // UseActionOnObject — no combat plugin to delegate to
    public Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    // IsActionUsable — cannot query without combat plugin
    public Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
