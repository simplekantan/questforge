using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Combat;

public sealed class WrathComboAdapter : ICombat
{
    private readonly PluginServices _svc;

    public WrathComboAdapter(PluginServices svc) => _svc = svc;

    public Task<Result<CombatOutcome>> EngageTarget(NpcId target, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<CombatOutcome>> EngageNearestHostile(float radius, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<Unit>> Disengage(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsCombatPluginAvailable(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<CombatPluginInfo>> GetActiveCombatPlugin(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct)
        => throw new NotImplementedException();
}