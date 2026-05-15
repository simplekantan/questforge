using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Combat;

public sealed class FakeCombat : ICombat
{
    private CombatOutcome _nextCombatResult = CombatOutcome.TargetDefeated;
    private bool _combatPluginAvailable = true;

    public record EngagementCall(NpcId? Target, float? Radius, DateTimeOffset At) : AdapterCall(At);
    public CallLog<EngagementCall> RecordedEngagements { get; } = new();

    // ----- Scripting -----
    public void ScriptNextCombatResult(CombatOutcome outcome) => _nextCombatResult = outcome;
    public void SetCombatPluginAvailable(bool available) => _combatPluginAvailable = available;

    // ----- Reset -----
    public void Reset()
    {
        RecordedEngagements.Clear();
        _nextCombatResult = CombatOutcome.TargetDefeated;
        _combatPluginAvailable = true;
    }

    // ----- ICombat implementation -----

    public Task<Result<CombatOutcome>> EngageTarget(NpcId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedEngagements.Add(new EngagementCall(target, null, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<CombatOutcome>>(Result.Ok(_nextCombatResult));
    }

    public Task<Result<CombatOutcome>> EngageNearestHostile(float radius, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedEngagements.Add(new EngagementCall(null, radius, DateTimeOffset.UtcNow));
        return Task.FromResult<Result<CombatOutcome>>(Result.Ok(_nextCombatResult));
    }

    public Task<Result<Unit>> Disengage(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<bool>> IsCombatPluginAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(_combatPluginAvailable));
    }

    public Task<Result<CombatPluginInfo>> GetActiveCombatPlugin(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new CombatPluginInfo("FakeCombat", "0.0.0", true, true);
        return Task.FromResult<Result<CombatPluginInfo>>(Result.Ok(info));
    }

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