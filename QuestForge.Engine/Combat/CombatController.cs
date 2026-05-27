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
    // 50m (wider than melee/ranged attack ranges) so the controller acquires and reroutes
    // to a valid kill-set enemy seen "along the way" instead of walking back to the spawn
    // anchor — kill-set members outscore aggro adds, so distant adds don't hijack selection.
    private const float ScanRadius = 50f;

    // Navigate stop distance: decoupled from attack range so the no-strand proof holds.
    // drift ≤ (attackRange − ApproachStopBuffer) ⟹ player within attackRange after stopping.
    private const float ApproachStopBuffer = 0.5f;

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
    // _mopUpStartedAt: wall-clock instant when mop-up was first entered for the current step.
    //   Null while no mop-up is active. Cleared by Reset() and ClearMopUpTimer().
    private KillTarget? _currentTarget;
    private KillTarget? _approachTarget;
    private WorldPosition? _approachPosition;
    private bool _wasInCombat;
    private float? _cachedAttackRange;
    private DateTimeOffset? _mopUpStartedAt;

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
                _approachTarget    = null;
                _approachPosition  = null;
            }
            return new CombatDecision(null, RotationShouldRun: false, "hostile query failed", ApproachState.None);
        }

        var actors = actorsResult.ValueOrThrow;

        // D8 step 2: select target using kill-set priority
        var killIds = new HashSet<uint>(step.KillEnemyDataIds);
        var target = KillPriority.SelectTarget(actors, killIds, step.Spawn);

        return await ApplyDecision(target, actors, ct);
    }

    /// <summary>
    /// Mop-up selection: engage ONLY actors attacking the player (IsTargetingPlayer OR
    /// OnPlayerEnmityList), independent of the step's kill-set. Reuses the same SetTarget /
    /// approach / re-path / attack-range logic as Decide so a fleeing attacker is still chased
    /// into attack range. Returns Target=null when no attacker is in scan range.
    /// </summary>
    public async Task<CombatDecision> DecideClearAggro(CancellationToken ct)
    {
        var actorsResult = await _gameState.GetHostileActors(ScanRadius, ct);
        if (!actorsResult.IsSuccess)
        {
            if (_approachTarget is not null)
            {
                await _navigator.Stop(ct);
                _approachTarget    = null;
                _approachPosition  = null;
            }
            return new CombatDecision(null, RotationShouldRun: false, "hostile query failed", ApproachState.None);
        }

        var actors = actorsResult.ValueOrThrow;
        var target = KillPriority.SelectAttacker(actors);

        return await ApplyDecision(target, actors, ct);
    }

    /// <summary>
    /// Records the mop-up start instant on first call for this step (idempotent thereafter).
    /// Returns elapsed since the start instant.
    /// </summary>
    public TimeSpan StartOrElapsedMopUp(DateTimeOffset now)
    {
        _mopUpStartedAt ??= now;
        return now - _mopUpStartedAt.Value;
    }

    /// <summary>Clears the mop-up timer. Called on confirm and on Reset().</summary>
    public void ClearMopUpTimer() => _mopUpStartedAt = null;

    /// <summary>
    /// Post-selection tail shared by Decide and DecideClearAggro: issues SetTarget/ClearTarget
    /// on identity change, resolves attack range, handles approach/re-path/in-range.
    /// </summary>
    private async Task<CombatDecision> ApplyDecision(
        KillTarget? target,
        IReadOnlyList<HostileActor> actors,
        CancellationToken ct)
    {
        // SetTarget / ClearTarget on identity change
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

        // Resolve attack range. Cached for the step after the first successful job read.
        var attackRange = await ResolveAttackRange(ct);

        var approach = ApproachState.None;

        if (target is null)
        {
            if (_approachTarget is not null)
            {
                await _navigator.Stop(ct);
                _approachTarget   = null;
                _approachPosition = null;
            }
            approach = ApproachState.None;
        }
        else
        {
            var actor    = FindActor(actors, target.Value.Id);
            var distance = actor?.DistanceToPlayer ?? float.MaxValue;
            var position = actor?.Position ?? new WorldPosition(0f, 0f, 0f);

            if (distance <= attackRange)
            {
                if (_approachTarget is not null)
                {
                    await _navigator.Stop(ct);
                    _approachTarget   = null;
                    _approachPosition = null;
                }
                approach = ApproachState.InRange;
            }
            else
            {
                var repath = attackRange - ApproachStopBuffer;
                var needsNav = target != _approachTarget
                    || _approachPosition is null
                    || _approachPosition.Value.DistanceTo(position) > repath;

                if (needsNav)
                {
                    _approachTarget   = target;
                    _approachPosition = position;
                    var opts = new NavigationOptions(StoppingDistance: ApproachStopBuffer, UseMount: false, UseFlight: false);
                    await _navigator.NavigateTo(position, opts, ct);
                }
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
    /// Step-exit cleanup. Idempotent. Issues ClearTarget so the game drops the leftover
    /// target and Stop so any in-flight approach navigation halts. Then clears all per-step
    /// latches via Reset().
    ///
    /// Does NOT call StopRotation — the rotation lease is owned by RotationLeaseLatch.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        await _combat.ClearTarget(ct);
        if (_approachTarget is not null)
            await _navigator.Stop(ct);
        Reset();
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
        _approachPosition  = null;
        _wasInCombat       = false;
        _cachedAttackRange = null;
        _mopUpStartedAt    = null;
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
