using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Combat;

// Phase 6 placeholder — quest 66130 never enters combat.
// WrathCombo IPC wiring deferred to a later phase.
public sealed class WrathComboAdapter : ICombat
{
    public WrathComboAdapter(PluginServices svc) { }

    public Task<Result<bool>> IsCombatPluginAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    public Task<Result<CombatPluginInfo>> GetActiveCombatPlugin(CancellationToken ct)
        => Task.FromResult<Result<CombatPluginInfo>>(
            Result.Fail<CombatPluginInfo>("noCombatPlugin", "WrathCombo IPC not wired in Phase 6"));

    public Task<Result<CombatOutcome>> EngageTarget(NpcId target, CancellationToken ct)
        => Task.FromResult<Result<CombatOutcome>>(Result.Ok(CombatOutcome.NoCombatPlugin));

    public Task<Result<CombatOutcome>> EngageNearestHostile(float radius, CancellationToken ct)
        => Task.FromResult<Result<CombatOutcome>>(Result.Ok(CombatOutcome.NoCombatPlugin));

    public Task<Result<Unit>> Disengage(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    public Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    public Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
