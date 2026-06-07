# Navigation Stuck Detection -- Testable Specification

**Source plan:** `docs/NAV_STUCK_DETECTION_PLAN.md` (decisions SD1--SD13)
**Status:** SPEC COMPLETE -- ready for Tester
**Branch:** `feat/nav-stuck-detection`
**Outputs:** New types, engine integration, harness updates, config fields, combat diagnostic log

---

## 1. Code-Level Issues Found During Review

### Issue 1 -- MapRecoverAction does not map UseReturnRecoverAction or UseTeleportRecoverAction

The plan (SD6) states that `MapRecoverAction` must be extended to handle `UseReturnRecoverAction` and `UseTeleportRecoverAction`. The current implementation at `QuestEngine.cs:938` only handles `AwaitUserRecoverAction` and `AbandonRecoverAction`; all other subtypes fall through to the wildcard arm which emits `AwaitUser`. This is correct as a fallback, but the plan expects `UseReturnRecoverAction` to map to `EngineAction.UseReturn` and `UseTeleportRecoverAction` to map to `EngineAction.Teleport`. The Builder must add these arms.

### Issue 2 -- FakeLogger does not exist

The plan (SD11) references `FakeLogger` for asserting on captured log entries. No `FakeLogger` exists in the codebase. All tests currently use `NullLogger<QuestEngine>.Instance`. The Builder must either: (a) introduce a minimal `CapturingLogger<T>` that stores `LogEntry` records, or (b) use the existing `Microsoft.Extensions.Logging.Testing.FakeLogger<T>` from the `Microsoft.Extensions.Diagnostics.Testing` NuGet package. Option (b) is preferred since it is a Microsoft-supported test utility. The spec below uses `FakeLogCollector` / `FakeLogger<T>` from that package.

### Issue 3 -- NW8 "null position" needs clarification

The plan lists NW8 as "Position read fails (null)". The watchdog's `Update` method takes `WorldPosition` (a struct, non-nullable). The engine's `playerPos` is `WorldPosition?` (nullable). The null-check must happen at the call site in `ResolveAction`, not inside the watchdog. When `playerPos` is null, the engine should skip the watchdog entirely (fail-open: emit the Navigate unchanged). The watchdog itself never receives null.

### Issue 4 -- Watchdog needs TimeProvider, not raw DateTimeOffset

The engine uses `TimeProvider _clock` for all time-sensitive logic (wait steps, action cooldowns). The watchdog must accept `TimeProvider` and use `_clock.GetUtcNow()` rather than `DateTimeOffset.UtcNow`. This ensures tests can use `FakeTimeProvider` to advance time deterministically without real delays.

### Issue 5 -- CombatStep in plan scenario NE6 needs re-evaluation

The plan says NE6 tests "CombatStep navigate-to-anchor, player stuck". However, combat approach navigation is issued by `CombatController.ApplyDecision` directly (calling `_navigator.NavigateTo`), NOT via `EngineAction.Navigate`. The engine emits `EngineAction.Engage`, not `Navigate`, for combat. The only Navigate the engine emits during a CombatStep is the "fallback to Location" navigate (line 624-629 in QuestEngine.cs). The watchdog monitors that fallback Navigate correctly. NE6 must test this specific path (combat fallback navigate-to-Location, not CombatController approach).

### Issue 6 -- Watchdog consultation point must handle resume fragment navigation

The `ProcessActiveResume` method can also emit `Navigate` actions (when resume fragment steps resolve to Navigate via `ResolveActionForStep`). The watchdog consultation must also intercept these Navigate actions. The cleanest approach: apply watchdog consultation as a post-processing step on ANY `Navigate` returned from `ResolveAction`, before emitting it from `Tick`.

---

## 2. New Types and Modifications

### 2.1 -- WatchdogAdvice (new enum)

**File:** `QuestForge.Engine/Navigation/WatchdogAdvice.cs`

```csharp
namespace QuestForge.Engine.Navigation;

public enum WatchdogAdvice
{
    /// <summary>Not navigating; watchdog inactive.</summary>
    Idle,

    /// <summary>Navigating normally; no stall detected.</summary>
    Continue,

    /// <summary>Stall detected; attempt a jump.</summary>
    Jump,

    /// <summary>All jump attempts exhausted; escalate to Return.</summary>
    CastReturn
}
```

### 2.2 -- NavigationWatchdog (new class)

**File:** `QuestForge.Engine/Navigation/NavigationWatchdog.cs`

```csharp
namespace QuestForge.Engine.Navigation;

using QuestForge.Adapters.Types;

public sealed class NavigationWatchdog
{
    private readonly TimeSpan _stallTimeout;
    private readonly float _stallDistance;
    private readonly int _maxJumps;
    private readonly TimeProvider _clock;

    private WorldPosition? _stallOrigin;
    private DateTimeOffset? _stallStartedAt;
    private int _jumpCount;

    public NavigationWatchdog(
        TimeSpan stallTimeout,
        float stallDistance,
        int maxJumps,
        TimeProvider clock)
    {
        _stallTimeout = stallTimeout;
        _stallDistance = stallDistance;
        _maxJumps = maxJumps;
        _clock = clock;
    }

    /// <summary>
    /// Called once per tick. Returns advice on how to proceed.
    /// </summary>
    /// <param name="playerPos">Current player position.</param>
    /// <param name="isNavigating">True when the engine resolved a Navigate action this tick.</param>
    public WatchdogAdvice Update(WorldPosition playerPos, bool isNavigating)
    {
        // ... implementation by Builder
    }

    /// <summary>Full reset: clears stall origin, jump count, and stall window.</summary>
    public void Reset()
    {
        // ... implementation by Builder
    }
}
```

**Behavior contract (for tests):**

1. When `isNavigating == false`: full reset, return `Idle`.
2. When `isNavigating == true` and no stall window is open (`_stallOrigin == null`): open a new stall window (`_stallOrigin = playerPos`, `_stallStartedAt = now`), return `Continue`.
3. When `isNavigating == true` and stall window is open:
   a. If `playerPos.DistanceTo(_stallOrigin) > _stallDistance`: full reset (player moved enough), re-open window with current position, return `Continue`.
   b. If elapsed since `_stallStartedAt` < `_stallTimeout`: return `Continue`.
   c. If elapsed >= `_stallTimeout` and `_jumpCount < _maxJumps`: increment `_jumpCount`, reset `_stallStartedAt` to now (grace period), return `Jump`.
   d. If elapsed >= `_stallTimeout` and `_jumpCount >= _maxJumps`: full reset, return `CastReturn`.

### 2.3 -- EngineAction.Jump (new)

**File:** `QuestForge.Engine/EngineAction.cs`

```csharp
public sealed record Jump(Step? Origin = null) : EngineAction;
```

### 2.4 -- EngineAction.UseReturn (new)

**File:** `QuestForge.Engine/EngineAction.cs`

```csharp
public sealed record UseReturn(Step? Origin = null) : EngineAction;
```

### 2.5 -- INavigator.Jump (new method)

**File:** `QuestForge.Adapters/Movement/INavigator.cs`

```csharp
Task<Result<Unit>> Jump(CancellationToken ct);
```

### 2.6 -- FakeNavigator.Jump (new implementation)

**File:** `QuestForge.Adapters.Fakes/Movement/FakeNavigator.cs`

Add:
```csharp
public record JumpCall(DateTimeOffset At) : AdapterCall(At);
public CallLog<JumpCall> RecordedJumps { get; } = new();

public Task<Result<Unit>> Jump(CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    RecordedJumps.Add(new JumpCall(DateTimeOffset.UtcNow));
    return Task.FromResult<Result<Unit>>(Result.Ok());
}
```

Also add `RecordedJumps.Clear()` to `Reset()`.

### 2.7 -- QuestEngine modifications

**File:** `QuestForge.Engine/QuestEngine.cs`

New field:
```csharp
private readonly NavigationWatchdog _watchdog;
```

Constructor: create watchdog with configurable thresholds. New optional parameters:
```csharp
double navStallTimeoutSeconds = 5.0,
float navStallDistanceThreshold = 2.0f,
int navMaxJumpAttempts = 3
```

Initialization:
```csharp
_watchdog = new NavigationWatchdog(
    TimeSpan.FromSeconds(navStallTimeoutSeconds),
    navStallDistanceThreshold,
    navMaxJumpAttempts,
    _clock);
```

**Integration point:** In `Tick()`, after `ResolveAction` returns `(action, stepId)` but before emitting trace events, apply watchdog consultation:

```csharp
// Watchdog consultation (applies to ALL Navigate actions from any source)
if (action is EngineAction.Navigate && playerPos is { } wp)
{
    var advice = _watchdog.Update(wp, isNavigating: true);
    action = advice switch
    {
        WatchdogAdvice.Jump => new EngineAction.Jump(Origin: FindOriginStep(action)),
        WatchdogAdvice.CastReturn => ResolveObstacleRecovery(FindOriginStep(action)),
        _ => action // Continue or Idle
    };
}
else
{
    if (playerPos is { } wp2)
        _watchdog.Update(wp2, isNavigating: false);
    else
        _watchdog.Reset();
}
```

Wait -- on re-reading `ResolveAction`, `playerPos` is read inside it but not returned. The watchdog consultation must happen inside `Tick()`. But `playerPos` is a local in `ResolveAction`. Two options:

**Chosen approach:** Move the watchdog consultation into `Tick()`. Read `playerPos` once at the top of `Tick()` (it's already read inside `ResolveAction` -- but we need it in `Tick` too). Since this adds a second `GetPlayerPosition` call per tick which would break replay fixture counts, the better approach is to hoist the `playerPos` read out of `ResolveAction` and pass it in, or have `ResolveAction` return it alongside the action. The simplest refactor: change `ResolveAction` signature to also return `playerPos`:

```csharp
private async Task<(EngineAction action, string? stepId, WorldPosition? playerPos)> ResolveAction(CancellationToken ct)
```

Then in `Tick()`:
```csharp
var (action, stepId, playerPos) = await ResolveAction(ct);

// Watchdog consultation
if (action is EngineAction.Navigate nav && playerPos.HasValue)
{
    var advice = _watchdog.Update(playerPos.Value, isNavigating: true);
    action = advice switch
    {
        WatchdogAdvice.Jump => new EngineAction.Jump(Origin: nav.Origin()),
        WatchdogAdvice.CastReturn => ResolveObstacleRecovery(stepId),
        _ => action
    };
}
else if (playerPos.HasValue)
{
    _watchdog.Update(playerPos.Value, isNavigating: false);
}
```

Note: `Navigate` does not have an `Origin` property. The step that originated the navigate is identified by `stepId`. For `Jump` and `UseReturn`, we need the `Step` object. The cleanest approach: store `_lastResolvedStep` (already exists) and use it.

**MapRecoverAction extension** (also in QuestEngine.cs):

```csharp
private static EngineAction MapRecoverAction(RecoverAction action, Step mainStep) => action switch
{
    AwaitUserRecoverAction au => new EngineAction.AwaitUser(au.Reason),
    UseReturnRecoverAction => new EngineAction.UseReturn(Origin: mainStep),
    UseTeleportRecoverAction tp => new EngineAction.Teleport(
        new Adapters.Types.AetheryteId(tp.AetheryteId), Origin: mainStep),
    AbandonRecoverAction => new EngineAction.AwaitUser($"resume abandoned for step '{mainStep.Id}'"),
    _ => new EngineAction.AwaitUser($"recovery '{action.GetType().Name}' for step '{mainStep.Id}'")
};
```

**New private method for obstacle recovery:**

```csharp
private EngineAction ResolveObstacleRecovery(string? stepId)
{
    // Find the step by stepId to check Recover.OnObstacle
    var step = FindStepById(stepId);
    if (step?.Recover?.OnObstacle is { } onObstacle)
        return MapRecoverAction(onObstacle, step);
    return new EngineAction.UseReturn(Origin: step);
}
```

### 2.8 -- EngineTestHarness modifications

**File:** `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs`

**RunToCompletion:** Add two new cases to the switch:

```csharp
case EngineAction.Jump j:
    actions.Add(action);
    EmitActionSubmitted("Jump", default);
    await Navigator.Jump(ct);
    EmitActionCompleted("Jump", "Done");
    break;

case EngineAction.UseReturn r:
    actions.Add(action);
    EmitActionSubmitted("UseReturn", default);
    await Navigator.Stop(ct);
    var returnResult = await Teleporter.UseReturn(ct);
    EmitActionCompleted("UseReturn",
        returnResult.IsSuccess ? returnResult.ValueOrThrow.ToString() : "Failed");
    break;
```

**HarnessEngine.Tick:** Add `EngineAction.Jump` to the lazy dismount exemption list:

```csharp
// Current:
if (_lastDispatchedWasNavigate && action is not EngineAction.Navigate
    and not EngineAction.Teleport and not EngineAction.EquipGear
    and not EngineAction.EquipBestGear and not EngineAction.RegisterGearset)

// Updated:
if (_lastDispatchedWasNavigate && action is not EngineAction.Navigate
    and not EngineAction.Teleport and not EngineAction.EquipGear
    and not EngineAction.EquipBestGear and not EngineAction.RegisterGearset
    and not EngineAction.Jump)
```

Also add inline dispatch for Jump and UseReturn in `HarnessEngine.Tick()`:

```csharp
if (action is EngineAction.Jump)
{
    await _navigator.Jump(ct);
}

if (action is EngineAction.UseReturn)
{
    await _navigator.Stop(ct);
    // FakeTeleporter.UseReturn updates position/zone if scripted
    await _teleporter.UseReturn(ct);
}
```

This requires adding `_teleporter` field to `HarnessEngine`.

### 2.9 -- PluginConfig new fields

**File:** `QuestForge.Plugin/PluginConfig.cs`

```csharp
/// <summary>Seconds of no progress before stuck detection fires.</summary>
public double NavStallTimeoutSeconds { get; set; } = 5.0;

/// <summary>Minimum movement in metres to count as progress during navigation.</summary>
public float NavStallDistanceThreshold { get; set; } = 2.0f;

/// <summary>Number of jump attempts before escalating to Return.</summary>
public int NavMaxJumpAttempts { get; set; } = 3;
```

### 2.10 -- Combat diagnostic logging

**File:** `QuestForge.Engine/QuestEngine.cs`

In the CombatStep arm of `ResolveAction` (around line 609), when `decision.Target is null`, add a debug log:

```csharp
if (decision.Target is null)
{
    _logger.LogDebug(
        "[CombatStep] No target found. HostileActors in scan: {ActorCount}, " +
        "KillEnemyDataIds: [{KillIds}], ActorDataIds: [{ActorIds}]",
        actors.Count,
        string.Join(",", combatStep.KillEnemyDataIds),
        string.Join(",", actors.Select(a => a.DataId)));
    // ... existing fallback logic
}
```

Wait -- the `actors` variable is not accessible here. `Decide` returns a `CombatDecision` which does not include the actor list. The diagnostic logging must happen INSIDE `CombatController.Decide`, not in `ResolveAction`. But `CombatController` does not have an `ILogger`. Two options:

**Chosen approach:** Add `ILogger` to `CombatController` constructor. This is a small change and follows the pattern used by `QuestEngine`. The diagnostic log fires inside `Decide` when `target is null` after the kill-set filtering.

```csharp
// In CombatController constructor:
public CombatController(IGameStateProvider gameState, ICombat combat, INavigator navigator, ILogger? logger = null)

// In Decide, after target selection:
if (target is null && _logger is not null)
{
    _logger.LogDebug(
        "[CombatStep] No target found. HostileActors in scan: {ActorCount}, " +
        "KillEnemyDataIds: [{KillIds}], ActorDataIds: [{ActorIds}]",
        actors.Count,
        string.Join(",", step.KillEnemyDataIds),
        string.Join(",", actors.Select(a => a.DataId)));
}
```

Also update `QuestEngine` constructor to pass `_logger` to `CombatController`:
```csharp
_combatController = new CombatController(gameState, combat, navigator, logger);
```

---

## 3. Test Scenarios

All tests use `FakeTimeProvider` to control time. The watchdog is constructed with `TimeProvider` so time can be advanced deterministically.

### 3.1 -- NavigationWatchdog Unit Tests

**File:** `QuestForge.Engine.Tests/Navigation/NavigationWatchdogTests.cs`

---

**NW1 -- Normal navigation, position changes every tick**

- **Setup:** Watchdog with defaults (5s timeout, 2m distance, 3 max jumps). `FakeTimeProvider` starting at T=0.
- **Action:** Call `Update(pos, isNavigating: true)` 20 times (simulating 5 seconds at 250ms ticks). Each tick, advance time by 250ms and move position by 3m along X axis (total 60m).
- **Assert:** Every call returns `WatchdogAdvice.Continue`. No `Jump` or `CastReturn` ever returned.

---

**NW2 -- Stall detected at 5 seconds**

- **Setup:** Watchdog with defaults. `FakeTimeProvider` at T=0.
- **Action:**
  1. Call `Update(pos=(0,0,0), isNavigating: true)` -- opens stall window. Returns `Continue`.
  2. Advance time by 4.9s. Call `Update(pos=(0,0,0), isNavigating: true)`. Returns `Continue`.
  3. Advance time by 0.2s (total 5.1s). Call `Update(pos=(0,0,0), isNavigating: true)`.
- **Assert:** Step 3 returns `WatchdogAdvice.Jump`.

---

**NW3 -- Grace period after Jump: no re-detection for 5s**

- **Setup:** Watchdog with defaults. `FakeTimeProvider` at T=0.
- **Action:**
  1. Drive to first `Jump` (5s of no movement). Assert `Jump` returned.
  2. Advance time by 4.9s. Call `Update(pos=(0,0,0), isNavigating: true)`.
  3. Assert `Continue` (grace period not expired).
  4. Advance time by 0.2s (total 5.1s after the Jump). Call `Update(pos=(0,0,0), isNavigating: true)`.
- **Assert:** Step 4 returns `WatchdogAdvice.Jump` (second jump attempt).

---

**NW4 -- Three jumps exhausted, then CastReturn**

- **Setup:** Watchdog with defaults (3 max jumps). `FakeTimeProvider` at T=0.
- **Action:**
  1. Drive to first `Jump` at T=5s. Position stays at (0,0,0).
  2. Drive to second `Jump` at T=10s. Position stays at (0,0,0).
  3. Drive to third `Jump` at T=15s. Position stays at (0,0,0).
  4. Advance to T=20s. Call `Update(pos=(0,0,0), isNavigating: true)`.
- **Assert:** Step 4 returns `WatchdogAdvice.CastReturn`.

---

**NW5 -- Jump succeeds (position changes > 2m), counter resets**

- **Setup:** Watchdog with defaults. `FakeTimeProvider` at T=0.
- **Action:**
  1. Drive to first `Jump` at T=5s. Assert `Jump`.
  2. Advance time by 1s. Call `Update(pos=(5,0,0), isNavigating: true)` (moved 5m from origin).
  3. Assert `Continue` (player moved, full reset happened).
  4. Keep position at (5,0,0). Drive for another 5s without movement.
- **Assert:** Step 4 returns `WatchdogAdvice.Jump` (first jump of new stall sequence, not second).

---

**NW6 -- Non-Navigate action causes full reset**

- **Setup:** Watchdog with defaults. `FakeTimeProvider` at T=0.
- **Action:**
  1. Navigate for 3s at pos=(0,0,0). Building up toward a stall but not yet triggered.
  2. Call `Update(pos=(0,0,0), isNavigating: false)`.
  3. Assert `Idle` and full reset.
  4. Call `Update(pos=(0,0,0), isNavigating: true)`. New window opens.
  5. Advance 5s. Call `Update(pos=(0,0,0), isNavigating: true)`.
- **Assert:** Step 5 returns `Jump` (fresh sequence, not carried over from step 1).

---

**NW7 -- Not navigating returns Idle**

- **Setup:** Watchdog with defaults.
- **Action:** Call `Update(pos=(10,20,30), isNavigating: false)`.
- **Assert:** Returns `WatchdogAdvice.Idle`.

---

**NW8 -- Null position at engine layer skips watchdog**

This is an engine integration test, not a watchdog unit test. See NE7b below.

---

**NW9 -- Custom thresholds respected**

- **Setup:** Watchdog with `stallTimeout=10s`, `stallDistance=5m`, `maxJumps=5`. `FakeTimeProvider` at T=0.
- **Action:**
  1. Navigate for 9.9s at pos=(0,0,0). Assert `Continue`.
  2. Advance to 10.1s. Assert `Jump` (10s timeout fires).
  3. Move to pos=(4,0,0) (4m -- below 5m threshold). Advance 10s. Assert `Jump` (4m < 5m, still stalled).
  4. Move to pos=(10,0,0) (6m from 4,0,0). Assert `Continue` (moved > 5m, reset).
  5. Exhaust 5 jumps (5 x 10s cycles). Assert `CastReturn` on 6th timeout.

---

**NW10 -- Stall threshold is strict boundary**

- **Setup:** Watchdog with `stallTimeout=5s`, `stallDistance=2m`.
- **Action:**
  1. Open window at T=0, pos=(0,0,0).
  2. At T=4.999s (just under), call Update. Assert `Continue`.
  3. At T=5.0s (exactly at), call Update. Assert `Jump` (>= comparison).

---

**NW11 -- Distance threshold is strict boundary**

- **Setup:** Watchdog with `stallTimeout=5s`, `stallDistance=2m`.
- **Action:**
  1. Open window at T=0, pos=(0,0,0).
  2. Advance to 5.1s. Call `Update(pos=(1.9, 0, 0), isNavigating: true)`. Assert `Jump` (1.9m < 2m, still stalled).
  3. After jump grace, call `Update(pos=(2.1, 0, 0), isNavigating: true)`. Assert `Continue` (2.1m > 2m, reset).

---

**NW12 -- CastReturn causes full reset, fresh window on next navigate**

- **Setup:** Watchdog with defaults (3 max jumps).
- **Action:**
  1. Exhaust 3 jumps and get `CastReturn`.
  2. Call `Update(pos=(100,0,0), isNavigating: true)` (new position after Return).
  3. Assert `Continue` (fresh stall window, jump count is 0).
  4. Advance 5s without movement. Assert `Jump` (first jump of new sequence).

---

### 3.2 -- Engine Integration Tests

**File:** `QuestForge.Engine.Tests/Navigation/NavStuckEngineTests.cs`

These tests construct the `EngineTestHarness`, but since they need time control, they will need to construct the `QuestEngine` directly with a `FakeTimeProvider` (the harness uses `NullLogger` and `TimeProvider.System`). Alternative: extend the harness to accept a `TimeProvider` override. The simpler approach: construct via the harness but override time by passing `FakeTimeProvider` through a new harness constructor overload.

**Decision for Builder:** Add a `TimeProvider? clock` parameter to `EngineTestHarness` constructor (defaulting to null which means `TimeProvider.System`). The harness passes it through to `QuestEngine`.

Also, since the engine ticks are 250ms in production but instant in tests, the test must manually advance `FakeTimeProvider` between ticks to simulate elapsed time.

For stuck detection engine tests, the key challenge is: `FakeNavigator.NavigateTo` immediately moves the player to the destination (line 84-86 of FakeNavigator). For stuck detection tests, we need the player to NOT arrive. Two approaches:
- Script `FakeNavigator` to return `NavigationOutcome.Arrived` but do NOT update position (override with `ScriptNextResult` and prevent position update).
- Actually, the issue is that `FakeNavigator.NavigateTo` sets position to destination on `Arrived`. For stuck tests, we need to either: (a) script a non-Arrived outcome, or (b) not call NavigateTo at all (the engine emits Navigate but the harness calls NavigateTo which arrives).

**Key insight:** The watchdog runs inside `Tick()`, BEFORE the action is dispatched. The engine's `Tick` returns the action; the harness then dispatches it. So the sequence is:
1. Engine reads position (still at stuck location).
2. Engine resolves Navigate.
3. Watchdog consults: position hasn't moved, stall detected, returns Jump.
4. Engine emits Jump instead of Navigate.
5. Harness dispatches Jump (calls navigator.Jump).
6. Next tick: engine reads position again (test must NOT advance position).

So for engine integration tests, we just need to ensure `FakeGameStateProvider.SetPosition` is NOT called between ticks (player stays stuck). The harness's RunToCompletion WILL call `Navigator.NavigateTo` for Navigate actions, which updates position. But if the engine emits Jump instead of Navigate, the harness calls `Navigator.Jump` which does NOT update position. This is correct.

The challenge is the FIRST few ticks before stuck detection fires -- the engine emits Navigate, the harness dispatches it (which moves the player via FakeNavigator). To prevent this, the test must either:
- Use `Engine.Tick()` directly (not RunToCompletion) and manually control position.
- Script FakeNavigator to NOT update position (use a non-Arrived outcome or override behavior).

**Chosen approach:** Use `Engine.Tick()` directly for stuck detection tests. Set position manually via `GameState.SetPosition()` and do NOT call the Navigator adapter. This matches how the engine works in production (the engine emits Navigate, EngineHost dispatches it to vnavmesh, and next tick the engine reads the player's position again -- if the player didn't move, stuck detection fires).

---

**NE1 -- TravelStep emits Navigate, player stuck 5s, engine emits Jump**

- **Setup:** Harness with `FakeTimeProvider`. Quest with one TravelStep to (100, 0, 0) with `Expect = "questFlag(1, 3)"`. Player at (0, 0, 0), zone 1. `navStallTimeoutSeconds = 5.0`.
- **Action:**
  1. Tick 1: Engine emits `Navigate(100,0,0)`. Advance time by 1s.
  2. Ticks 2-4: Do NOT move player. Engine re-emits `Navigate`. Advance time by 1s each.
  3. Tick 5: Advance to T=5.1s total. Engine re-resolves Navigate. Watchdog fires.
- **Assert:** Tick 5 (or whenever cumulative time >= 5s) returns `EngineAction.Jump`.

---

**NE2 -- Player stuck, 3 jumps exhausted, engine emits UseReturn**

- **Setup:** Same as NE1 but run for longer.
- **Action:**
  1. Drive to first `Jump` at ~5s.
  2. Continue ticking without moving player. Each jump gets 5s grace.
  3. After 3 jumps (at ~20s), next timeout triggers.
- **Assert:** Engine emits `EngineAction.UseReturn`.

---

**NE3 -- Jump succeeds (player moves), engine resumes Navigate**

- **Setup:** Same as NE1.
- **Action:**
  1. Drive to first `Jump`.
  2. After Jump, advance time 1s and move player to (10, 0, 0) (10m from stall origin).
  3. Next tick: engine re-resolves.
- **Assert:** Engine emits `EngineAction.Navigate` (back to normal), not `Jump`. Subsequent stall would be a fresh sequence.

---

**NE4 -- Step confirms (Expect met) while stuck, watchdog resets**

- **Setup:** Quest with TravelStep, `Expect = "questFlag(1, 3)"`.
- **Action:**
  1. Drive to stall (3s in, not yet triggered).
  2. Set quest flag so Expect is satisfied.
  3. Next tick: step confirms, engine moves to next step (or Done).
- **Assert:** No `Jump` or `UseReturn` emitted. Watchdog resets because next action is not Navigate.

---

**NE5 -- Step has Recover.OnObstacle = awaitUser("stuck"), engine emits AwaitUser**

- **Setup:** TravelStep with `Recover = new RecoverConfig { OnObstacle = new AwaitUserRecoverAction { Reason = "stuck" } }`. Player stuck.
- **Action:** Exhaust 3 jumps.
- **Assert:** Engine emits `EngineAction.AwaitUser("stuck")` instead of `EngineAction.UseReturn`.

---

**NE5b -- Step has Recover.OnObstacle = useReturn, engine emits UseReturn**

- **Setup:** TravelStep with `Recover = new RecoverConfig { OnObstacle = new UseReturnRecoverAction() }`.
- **Action:** Exhaust 3 jumps.
- **Assert:** Engine emits `EngineAction.UseReturn`.

---

**NE5c -- Step has Recover.OnObstacle = useTeleport(aetheryteId: 8), engine emits Teleport**

- **Setup:** TravelStep with `Recover = new RecoverConfig { OnObstacle = new UseTeleportRecoverAction { AetheryteId = 8 } }`.
- **Action:** Exhaust 3 jumps.
- **Assert:** Engine emits `EngineAction.Teleport(destination: AetheryteId(8))`.

---

**NE6 -- CombatStep fallback-navigate-to-Location, player stuck**

- **Setup:** CombatStep with `Location = { Position = (50,0,0) }`. Player at (0,0,0) (far from Location). No hostile actors in range (FakeCombat returns no targets). FakeNavigator does NOT update position.
- **Action:** Engine emits Navigate to Location (fallback path). Watchdog monitors. After 5s, stuck fires.
- **Assert:** Engine emits `EngineAction.Jump`. (Not `Engage`.)

---

**NE7 -- Non-navigate step (talk), watchdog stays idle**

- **Setup:** Quest with TalkStep targeting NPC at (0,0,0). Player at (0,0,0) (in range). Player position does not change for 10s.
- **Action:** Tick repeatedly. Engine emits `Interact` each tick.
- **Assert:** Never emits `Jump` or `UseReturn`. Watchdog gets `isNavigating: false` and stays `Idle`.

---

**NE7b -- Null player position, watchdog skipped**

- **Setup:** TravelStep. Player position read fails (`SetPositionFailure`).
- **Action:** Tick.
- **Assert:** Engine emits `Navigate` (fail-open). No stuck detection (watchdog not consulted when position is null).

---

**NE8 -- Implied navigation (ResolveInteractOrNavigate emits Navigate)**

- **Setup:** TalkStep with NPC at (100, 0, 0). Player at (0, 0, 0) (far from NPC, so ResolveInteractOrNavigate emits Navigate). Player does not move.
- **Action:** Tick for 5+s.
- **Assert:** Engine emits `EngineAction.Jump` (watchdog fires on the Navigate from implied navigation).

---

### 3.3 -- Harness Tests

**File:** `QuestForge.Engine.Tests/Navigation/NavStuckHarnessTests.cs`

---

**NH1 -- RunToCompletion handles Jump action**

- **Setup:** Quest that drives to stuck detection and emits Jump. (Or: mock/construct a scenario where the engine emits Jump.)
- **Action:** RunToCompletion processes the Jump action.
- **Assert:** `Navigator.RecordedJumps.Count == 1`.

---

**NH2 -- RunToCompletion handles UseReturn action**

- **Setup:** Quest that drives to UseReturn.
- **Action:** RunToCompletion processes the UseReturn action.
- **Assert:** `Navigator.RecordedStops.Count >= 1` AND `Teleporter.RecordedReturns.Count == 1`.

---

**NH3 -- Jump is exempt from lazy dismount**

- **Setup:** Player mounted. Previous action was Navigate (`_lastDispatchedWasNavigate = true`). Current action is Jump.
- **Action:** `HarnessEngine.Tick()` processes the action.
- **Assert:** `Mount.DismountCallCount == 0`. Player stays mounted.

---

### 3.4 -- Combat Diagnostic Logging Tests

**File:** `QuestForge.Engine.Tests/Combat/CombatDiagnosticLogTests.cs`

---

**CD1 -- No target found, diagnostic log emitted**

- **Setup:** CombatStep with `KillEnemyDataIds = [100, 200]`. `FakeGameStateProvider` returns 3 hostile actors with DataIds [300, 400, 500] (none matching kill set). Use `FakeLogger<CombatController>` (from `Microsoft.Extensions.Diagnostics.Testing`) or a custom capturing logger.
- **Action:** Call `_combatController.Decide(combatStep, ct)`.
- **Assert:** Logger captured one Debug-level log entry containing:
  - "No target found" (or similar)
  - "HostileActors in scan: 3"
  - The KillEnemyDataIds "100,200"
  - The ActorDataIds "300,400,500"

---

**CD2 -- Target found, no diagnostic log emitted**

- **Setup:** Same as CD1 but hostile actor DataId 100 is present (matches kill set).
- **Action:** Call `Decide`.
- **Assert:** Logger captured zero Debug-level log entries.

---

### 3.5 -- MapRecoverAction Extension Tests

**File:** `QuestForge.Engine.Tests/Navigation/ObstacleRecoveryTests.cs`

These test the engine's behavior when `Recover.OnObstacle` is set, exercised through stuck detection scenarios. Already covered by NE5, NE5b, NE5c above. No separate file needed.

---

## 4. Implementation Order

### Phase A -- Pure watchdog (no engine coupling)

1. `WatchdogAdvice` enum
2. `NavigationWatchdog` class
3. `INavigator.Jump()` method
4. `FakeNavigator.Jump()` implementation
5. Tests: NW1--NW12

**Done gate:** All 12 watchdog unit tests pass. No engine changes yet.

### Phase B -- Engine wiring

1. `EngineAction.Jump` and `EngineAction.UseReturn` record types
2. `NavigationWatchdog` field + construction in `QuestEngine`
3. Watchdog consultation in `Tick()` (post-ResolveAction, pre-trace)
4. `MapRecoverAction` extension for `UseReturnRecoverAction` and `UseTeleportRecoverAction`
5. `ResolveObstacleRecovery` private method
6. Harness: `EngineTestHarness` constructor accepts `TimeProvider?`, passes through
7. Harness: `RunToCompletion` Jump and UseReturn cases
8. Harness: `HarnessEngine.Tick()` Jump exemption + inline dispatch
9. Harness: `HarnessEngine` gains `_teleporter` field
10. Tests: NE1--NE8, NE7b, NH1--NH3

**Done gate:** All engine integration tests and harness tests pass.

### Phase C -- Combat diagnostic + config

1. `CombatController` gains `ILogger?` parameter
2. Diagnostic log in `Decide` when target is null
3. `QuestEngine` passes logger to `CombatController`
4. `PluginConfig` new fields
5. `ConfigWindow` new UI controls
6. Tests: CD1, CD2

**Done gate:** All tests pass. `dotnet build` succeeds with TreatWarningsAsErrors.

---

## 5. Done Criteria

1. `dotnet test QuestForge.Engine.Tests` passes with 0 failures, including all NW*, NE*, NH*, CD* tests.
2. `dotnet test QuestForge.Adapters.Tests` passes (FakeNavigator.Jump does not break existing tests).
3. `dotnet build` succeeds for all projects including `QuestForge.Plugin`.
4. `EngineAction.Jump` and `EngineAction.UseReturn` are handled in `RunToCompletion` (no `default: throw`).
5. `INavigator` has a `Jump` method; `FakeNavigator` implements it.
6. `MapRecoverAction` handles `UseReturnRecoverAction` and `UseTeleportRecoverAction`.
7. `CombatController.Decide` emits a Debug log when no target is found.

---

## 6. Exclusions

- **Dalamud adapter implementations** (VnavmeshNavigator.Jump, LifestreamTeleporter.UseReturn) -- Slice 2.
- **EngineHost.DispatchAction** wiring -- Slice 2.
- **In-game smoke testing** -- Slice 3.
- **Return cooldown pre-check** (checking GetReturnCooldown before escalating) -- documented as future enhancement in plan SD5.
- **Tooling catch-up** (TraceConstants, CapabilityInferrer) -- paired with Slice 2.
- **ConfigWindow UI** -- Builder implements alongside PluginConfig fields, but no tests (ImGui is untestable without a game context).

---

## Test Count Summary

```
READY FOR TEST CREATION

Tester: Write failing tests from the scenarios in section 3.

Watchdog unit tests (NW1--NW12):    12 tests
Engine integration tests (NE1--NE8, NE5b, NE5c, NE7b): 11 tests
Harness tests (NH1--NH3):           3 tests
Combat diagnostic tests (CD1--CD2): 2 tests

- Happy paths: 8 scenarios (NW1, NW2, NW5, NE1, NE3, NE8, NH1, NH2)
- Edge cases: 12 scenarios (NW3, NW4, NW6, NW9, NW10, NW11, NW12, NE4, NE6, NE7, NE7b, NH3)
- Error/recovery cases: 8 scenarios (NW7, NE2, NE5, NE5b, NE5c, NE7b, CD1, CD2)
- Expected total: ~28 test methods across 4 test files in QuestForge.Engine.Tests
```
