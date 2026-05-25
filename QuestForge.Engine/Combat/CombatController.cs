using QuestForge.Adapters.Combat;
using QuestForge.Adapters.State;
using QuestForge.Schema;

namespace QuestForge.Engine.Combat;

/// <summary>
/// Stateful combat controller — owns target-selection and rotation state across ticks
/// while a CombatStep is the active step.
///
/// The engine calls Decide once per tick while the active step is a CombatStep.
/// The engine calls Reset when leaving the combat step (sequence advance / step confirmed).
///
/// Lives in QuestForge.Engine (pure C#, no Dalamud) and depends only on adapter interfaces,
/// making it unit-testable against FakeGameStateProvider + FakeCombat.
/// </summary>
public sealed class CombatController
{
    // Scan radius in metres. Not author-configurable in v1; overworldEnemies can roam.
    private const float ScanRadius = 30f;

    private readonly IGameStateProvider _gameState;
    private readonly ICombat _combat;

    // Stateful across ticks within one combat step.
    // _currentTarget: which actor we last told ICombat to attack.
    // _wasInCombat: "was in combat" latch for the completion loop heuristic.
    private KillTarget? _currentTarget;
    private bool _wasInCombat;

    public CombatController(IGameStateProvider gameState, ICombat combat)
    {
        _gameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        _combat    = combat    ?? throw new ArgumentNullException(nameof(combat));
    }

    /// <summary>
    /// Called once per tick while the active step is the given CombatStep.
    /// Returns the target the engine should pass to EngineAction.Engage
    /// (null = nothing to attack this tick → host idles).
    /// </summary>
    public async Task<CombatDecision> Decide(CombatStep step, CancellationToken ct)
    {
        var actorsResult = await _gameState.GetHostileActors(ScanRadius, ct);
        if (!actorsResult.IsSuccess)
            return new CombatDecision(null, RotationShouldRun: false, "hostile query failed");

        var killIds = new HashSet<uint>(step.KillEnemyDataIds);
        var target = KillPriority.SelectTarget(actorsResult.ValueOrThrow, killIds, step.Spawn);

        if (target != _currentTarget)
        {
            if (target is not null)
            {
                await _combat.SetTarget(target.Value.Id, ct);
                _currentTarget = target;
            }
            else
            {
                await _combat.ClearTarget(ct);
                _currentTarget = null;
            }
        }

        var rotationShouldRun = target is not null;
        _wasInCombat = rotationShouldRun;
        return new CombatDecision(target, rotationShouldRun, target is not null ? "target acquired" : "no eligible target");
    }

    /// <summary>
    /// Called by the engine when it leaves the combat step (confirmed / sequence advance).
    /// Clears all per-combat-step state so the next combat step starts fresh.
    /// </summary>
    public void Reset()
    {
        _currentTarget = null;
        _wasInCombat   = false;
    }
}
