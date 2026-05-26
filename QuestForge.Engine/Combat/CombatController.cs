using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine.Combat;

/// <summary>
/// Stateful combat controller — owns target-selection, rotation state, and approach navigation
/// across ticks while a CombatStep is the active step.
///
/// The engine calls Decide once per tick while the active step is a CombatStep.
/// The engine calls Reset when leaving the combat step (sequence advance / step confirmed).
///
/// Lives in QuestForge.Engine (pure C#, no Dalamud) and depends only on adapter interfaces,
/// making it unit-testable against FakeGameStateProvider + FakeCombat + FakeNavigator.
/// </summary>
public sealed class CombatController
{
    // Scan radius in metres. Not author-configurable in v1; overworldEnemies can roam.
    private const float ScanRadius = 30f;

    private readonly IGameStateProvider _gameState;
    private readonly ICombat _combat;
    private readonly INavigator _navigator;

    // _currentTarget: which actor we last told ICombat to attack (SetTarget latch).
    // _approachTarget: which actor we last issued NavigateTo for (navigation latch).
    // _wasInCombat: "was in combat" latch for the completion loop heuristic.
    // _cachedAttackRange: attack range resolved from the player's job. The job cannot change
    //   mid-combat in FFXIV, so it is read once (the first tick a successful GetCurrentJob
    //   lands) and reused for the rest of the step. This caps GetCurrentJob at one read per
    //   combat step regardless of tick count, which also keeps the per-tick trace-replay
    //   fixture surface flat. A transient job-read failure is NOT cached (falls back to
    //   FallbackRange for that tick only), so a later tick recovers the real range.
    private KillTarget? _currentTarget;
    private KillTarget? _approachTarget;
    private bool _wasInCombat;
    private float? _cachedAttackRange;

    public CombatController(IGameStateProvider gameState, ICombat combat, INavigator navigator)
    {
        _gameState = gameState  ?? throw new ArgumentNullException(nameof(gameState));
        _combat    = combat     ?? throw new ArgumentNullException(nameof(combat));
        _navigator = navigator  ?? throw new ArgumentNullException(nameof(navigator));
    }

    /// <summary>
    /// Called once per tick while the active step is the given CombatStep.
    /// Returns the targeting/approach decision the engine should use this tick.
    /// </summary>
    public async Task<CombatDecision> Decide(CombatStep step, CancellationToken ct)
    {
        // D8 step 1: GetHostileActors
        var actorsResult = await _gameState.GetHostileActors(ScanRadius, ct);
        if (!actorsResult.IsSuccess)
        {
            if (_approachTarget is not null)
            {
                await _navigator.Stop(ct);
                _approachTarget = null;
            }
            return new CombatDecision(null, RotationShouldRun: false, "hostile query failed", ApproachState.None);
        }

        var actors = actorsResult.ValueOrThrow;

        // D8 step 2: select target
        var killIds = new HashSet<uint>(step.KillEnemyDataIds);
        var target = KillPriority.SelectTarget(actors, killIds, step.Spawn);

        // D8 step 3: SetTarget / ClearTarget on identity change
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

        // D8 step 4: resolve attack range. Cached for the step after the first successful
        // job read; only re-queries GetCurrentJob while the cache is empty (i.e. until the
        // first success). A failed read uses FallbackRange for this tick without caching it.
        var attackRange = await ResolveAttackRange(ct);

        // D8 step 5: D4 cadence
        var approach = ApproachState.None;

        if (target is null)
        {
            if (_approachTarget is not null)
            {
                await _navigator.Stop(ct);
                _approachTarget = null;
            }
            approach = ApproachState.None;
        }
        else
        {
            var actor = FindActor(actors, target.Value.Id);
            var distance = actor?.DistanceToPlayer ?? float.MaxValue;
            var position  = actor?.Position ?? new WorldPosition(0f, 0f, 0f);

            if (distance <= attackRange)
            {
                if (_approachTarget is not null)
                {
                    await _navigator.Stop(ct);
                    _approachTarget = null;
                }
                approach = ApproachState.InRange;
            }
            else if (target != _approachTarget)
            {
                // New or changed out-of-range target — issue NavigateTo and set latch
                // regardless of nav result (don't spam a dead navmesh)
                _approachTarget = target;
                var opts = new NavigationOptions(StoppingDistance: attackRange, UseMount: false, UseFlight: false);
                await _navigator.NavigateTo(position, opts, ct);
                approach = ApproachState.Approaching;
            }
            else
            {
                // Already heading toward this target — do nothing
                approach = ApproachState.Approaching;
            }
        }

        var rotationShouldRun = target is not null;
        _wasInCombat = rotationShouldRun;
        return new CombatDecision(
            target,
            rotationShouldRun,
            target is not null ? "target acquired" : "no eligible target",
            approach);
    }

    /// <summary>
    /// Returns the attack range for the player's job, reading GetCurrentJob at most once per
    /// combat step. On a job-read failure the FallbackRange is returned for this tick only
    /// and the cache is left empty, so a later tick can still resolve the real range.
    /// </summary>
    private async Task<float> ResolveAttackRange(CancellationToken ct)
    {
        if (_cachedAttackRange is { } cached)
            return cached;

        var jobResult = await _gameState.GetCurrentJob(ct);
        if (!jobResult.IsSuccess)
            return JobRangeTable.FallbackRange;

        var range = JobRangeTable.AttackRange(jobResult.ValueOrThrow);
        _cachedAttackRange = range;
        return range;
    }

    /// <summary>
    /// Called by the engine when it leaves the combat step (confirmed / sequence advance).
    /// Clears all per-combat-step state so the next combat step starts fresh.
    /// Does NOT call Stop — cross-step movement is the engine's responsibility.
    /// </summary>
    public void Reset()
    {
        _currentTarget     = null;
        _approachTarget    = null;
        _wasInCombat       = false;
        _cachedAttackRange = null;
    }

    private static HostileActor? FindActor(IReadOnlyList<HostileActor> actors, ActorId id)
    {
        foreach (var a in actors)
        {
            if (a.Id == id) return a;
        }
        return null;
    }
}
