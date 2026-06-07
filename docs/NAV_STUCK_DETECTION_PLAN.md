# Navigation Stuck Detection Plan

## Problem

When vnavmesh encounters an obstacle it can't path around (low fence, rock, terrain seam), the player gets stuck indefinitely. The engine re-emits `Navigate` every tick but the player doesn't move. There is no detection, no recovery, and no upper bound on how long the player stays stuck.

## Design Decisions

### SD1 — Engine-layer watchdog (pure C#)

Stuck detection lives in `QuestForge.Engine` as a new `NavigationWatchdog` class. It tracks player position across ticks and detects stalls. This keeps the logic Dalamud-free and unit-testable against fakes.

The watchdog is consumed by the engine's `ResolveAction` flow. When the engine would emit a `Navigate` action, it first consults the watchdog. The watchdog returns one of: `Continue` (normal navigation), `Jump` (attempt a jump to clear the obstacle), or `CastReturn` (all jumps exhausted, use Return to recover).

### SD2 — Stall detection: 5 seconds, < 2m movement

A stall is detected when:
- The engine has been emitting `Navigate` actions (vnavmesh is pathfinding/running)
- The player's position has not changed by more than 2m over 5 seconds

Both thresholds (stall duration and distance) are configurable in `PluginConfig`.

**Tick math:** The engine ticks every 250ms. 5 seconds = 20 ticks. The watchdog samples position on every tick and compares against the position recorded when the current stall window opened.

### SD3 — Jump as first recovery (INavigator.Jump)

When a stall is detected, the watchdog advises `Jump`. A new `Jump()` method is added to `INavigator`. The jump fires while vnavmesh continues its current path — the player's forward momentum from the pathfinding movement carries through the jump, potentially clearing the obstacle. On ground this hops over low obstacles; while flying this ascends, clearing vertical terrain.

After a successful jump (player position changes > 2m from the stall position), the jump counter resets to 0. The stall window also resets.

### SD4 — Max jump count: default 3, configurable

If 3 jumps (configurable in `PluginConfig`) fail to clear the stall (player still within 2m of stall origin after each jump), the watchdog escalates to `CastReturn`.

### SD5 — Return as escalation

After max jumps exhausted, the engine emits a new `EngineAction.UseReturn`. This fires `ITeleporter.UseReturn()` (already on the adapter interface, not yet implemented in the engine). The player teleports to their home aetheryte.

**Return cooldown:** FFXIV's Return has a fixed 900-second (15-minute) cooldown that cannot be reduced. If Return is on cooldown when the watchdog escalates, `UseReturn` will fail at the adapter layer. The engine treats this as an adapter error and emits `AwaitUser("Return on cooldown — manual repositioning required")`. This is a known v1 limitation; a future enhancement could check `GetReturnCooldown()` before escalating and skip directly to `AwaitUser` with the remaining cooldown time.

### SD6 — Post-Return recovery via Recover.OnObstacle

When the watchdog returns `CastReturn`, the engine checks the current step's `Recover?.OnObstacle` at resolve time (before emitting any action):

- **If `OnObstacle` is null (default):** Emit `EngineAction.UseReturn`. After Return completes and the zone loads, the next tick's `ResolveAction` re-evaluates the step from scratch. The step was never confirmed (its `Expect` predicate is still unmet), so the cursor naturally lands on it again. If the step has `RequiredZone` + `ResumePointFragmentId`, the resume fragment fires (player is now in the wrong zone). If it doesn't, the step re-evaluates from the player's new position (home aetheryte), which will likely emit Teleport/Navigate to get back.

- **If `OnObstacle` is set:** Map the recovery action using the existing `MapRecoverAction` infrastructure (extended to handle `UseReturnRecoverAction` and `UseTeleportRecoverAction`). The mapped action substitutes *instead of* the default `UseReturn`.

"Retry the step" is implicit, not a separate mechanism — the step is never confirmed during a stall, so the cursor re-enters it naturally on the next tick after Return/zone-load completes.

This keeps recovery and resume as distinct mechanisms:
- **Recovery** decides "what to do after failure" → default is UseReturn (which leads to implicit retry)
- **Resume** handles "player is in wrong zone when step starts" → fires naturally on retry if configured

### SD7 — Do not re-path after jump

After a jump, vnavmesh continues its current path. We do not call `Stop()` or re-issue `NavigateTo`. The momentum from the stuck movement is needed for the jump to carry the player over the obstacle.

### SD8 — Watchdog state lifecycle

The watchdog tracks:
- `_stallOrigin`: position when the current stall window opened (null when not stalled)
- `_stallStartedAt`: timestamp when the stall window opened
- `_jumpCount`: number of jumps attempted in the current stall sequence

**Grace period after Jump:** When the watchdog returns `Jump`, it resets `_stallStartedAt` to `now` (starting a fresh stall window) while incrementing `_jumpCount`. This gives each jump a full stall-timeout window to take effect before the next one fires. Without this, the already-elapsed 5s timer would cause immediate re-detection on the very next tick.

**Reset conditions (full reset = clear origin, zero jump count, close stall window):**
- Player moves > 2m from `_stallOrigin` → full reset. This covers both successful jumps and normal navigation resuming.
- Engine stops emitting `Navigate` (step changes, step confirms, non-Navigate action) → full reset.
- `CastReturn` is returned → full reset (new navigation context after teleport).

### SD9 — New EngineAction types

Two new action types:

```csharp
public sealed record Jump(Step? Origin = null) : EngineAction;
public sealed record UseReturn(Step? Origin = null) : EngineAction;
```

`Jump` is dispatched by EngineHost to `INavigator.Jump()`.
`UseReturn` is dispatched by EngineHost to `ITeleporter.UseReturn()`.

### SD10 — Watchdog is scoped to Navigate actions only

The watchdog only monitors when the engine emits `Navigate`. Combat approach navigation (managed by `CombatController` internally) uses `INavigator.NavigateTo` directly — the watchdog does not monitor combat approach movement. Combat has its own target-switching logic that naturally handles repositioning.

### SD11 — Combat diagnostic logging (separate concern)

Add a debug-level log line (via `ILogger`) inside the CombatStep arm of `ResolveAction` when `Decide()` returns no target. Log the count of actors returned by `GetHostileActors` and the DataIds present vs. the step's `KillEnemyDataIds`, so we can diagnose whether mobs are absent from the ObjectTable vs. filtered out by KillPriority.

This is independent of stuck detection but is delivered in the same branch since both are diagnostic/recovery improvements. Tested by asserting on `FakeLogger` captured log entries.

### SD12 — Lazy dismount exemption for Jump

`EngineHost.DispatchAction` and `HarnessEngine.Tick` have a dismount guard that fires when the previous action was Navigate and the current action is not in an exemption list. `EngineAction.Jump` must be added to this exemption list (alongside Navigate, Teleport, EquipGear, etc.) because Jump is part of navigation recovery — the player should stay mounted during a jump attempt.

### SD13 — EngineTestHarness updates

`EngineTestHarness.RunToCompletion` and `HarnessEngine.Tick` must handle the new action types:

- `EngineAction.Jump`: record the action, call `Navigator.Jump()`.
- `EngineAction.UseReturn`: record the action, call `Navigator.Stop()` then `Teleporter.UseReturn()`. The test configures `FakeTeleporter` to update the player's position/zone as a side effect.

Without these, any engine integration test that triggers stuck recovery hits the `default: throw` in `RunToCompletion`.

## Configuration

New fields on `PluginConfig`:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `NavStallTimeoutSeconds` | `double` | `5.0` | Seconds of no progress before a stall is detected |
| `NavStallDistanceThreshold` | `float` | `2.0` | Minimum movement (metres) to count as progress |
| `NavMaxJumpAttempts` | `int` | `3` | Max jumps before escalating to Return |

New fields on `ConfigWindow` (Engine section):

- `InputFloat("Stuck detection timeout (sec)")` for `NavStallTimeoutSeconds`
- `InputFloat("Stuck movement threshold (m)")` for `NavStallDistanceThreshold`
- `InputInt("Max jump attempts")` for `NavMaxJumpAttempts`

## Architecture

### NavigationWatchdog (QuestForge.Engine)

```
NavigationWatchdog(stallTimeout, stallDistance, maxJumps)
├── Update(playerPos, isNavigateAction, now) → WatchdogAdvice
├── Reset()
└── fields: _stallOrigin, _stallStartedAt, _jumpCount
```

`WatchdogAdvice` is a discriminated union:
- `Continue` — normal, no intervention needed
- `Jump` — stall detected, attempt jump
- `CastReturn` — jumps exhausted, use Return
- `Idle` — not navigating, watchdog inactive

The watchdog has no `OnJumpCompleted()` method — jump success is detected by observing position change > 2m on subsequent `Update()` calls.

### Engine integration point (QuestEngine.cs)

The watchdog is consulted in `ResolveAction` after the normal action is resolved but before it's returned. If the resolved action is `Navigate`:

1. Call `_watchdog.Update(playerPos, isNavigate: true, now)`
2. If advice is `Jump` → return `EngineAction.Jump(Origin: step)` instead of `Navigate`
3. If advice is `CastReturn` → check `step.Recover?.OnObstacle`:
   - If null → return `EngineAction.UseReturn(Origin: step)`
   - If set → return `MapRecoverAction(onObstacle, step)`
4. If advice is `Continue` → return the original `Navigate`

When the resolved action is NOT `Navigate`, call `_watchdog.Update(playerPos, isNavigate: false, now)` to reset.

### EngineHost dispatch

```csharp
case EngineAction.Jump j:
    DebounceLog("jump:stuck", "[Jump] stuck recovery — attempting jump");
    await _navigator.Jump(ct);
    break;

case EngineAction.UseReturn r:
    DebounceLog("return:stuck", "[UseReturn] stuck recovery — casting Return");
    await _navigator.Stop(ct);  // stop vnavmesh before Return
    await _teleporter.UseReturn(ct);
    break;
```

### Post-Return recovery flow

After `UseReturn` dispatches, the next tick's `ResolveAction` runs. The step is still active (not confirmed — its Expect predicate is unmet). The engine re-evaluates the step:

1. If step has `RequiredZone` and player is now in wrong zone → resume fragment fires (if configured)
2. If step has no resume fragment → step re-evaluates from new position (home aetheryte), which will likely emit a new `Teleport` or `Navigate` to get back

This is the "Option B" default: the step retries naturally because it was never confirmed. No explicit "retry" mechanism is needed.

## Adapter Changes

### INavigator — add Jump()

```csharp
Task<Result<Unit>> Jump(CancellationToken ct);
```

### VnavmeshNavigator (Dalamud) — implement Jump()

FFXIV jump is GeneralAction id 2. Fire via `ActionManager.UseAction(ActionType.GeneralAction, 2)`. Works on ground (hop) and while flying (ascend).

### FakeNavigator — implement Jump()

Record the call in a `JumpCallCount` counter. No position change (the test controls position via `SetPosition`).

### ITeleporter.UseReturn() — already defined, needs Dalamud impl

The Lifestream adapter (`LifestreamTeleporter`) needs a `UseReturn` implementation. Return is GeneralAction id 8 (`ActionManager.UseAction(ActionType.GeneralAction, 8)`). After firing, the adapter waits for the zone-loading condition flag to go true then false (same pattern as teleport). Return has a fixed 900-second cooldown that cannot be reduced.

### FakeTeleporter — UseReturn()

Already on the interface. Verify it records the call and updates player position/zone via `FakeGameStateProvider.SetPosition` / `SetZone`.

## Test Scenarios

### Watchdog unit tests (QuestForge.Engine.Tests/Navigation/NavigationWatchdogTests.cs)

| ID | Scenario | Expected |
|----|----------|----------|
| NW1 | Player navigating, position changes > 2m every tick | `Continue` every tick, no stall |
| NW2 | Player navigating, position unchanged for 5s | `Jump` on first stall detection |
| NW3 | After Jump returned, stall timer resets — no re-detection for another 5s | `Continue` for 4.9s after Jump, then `Jump` again at 5s |
| NW4 | Player navigating, position unchanged, 3 jumps exhausted (each after 5s grace) | `CastReturn` |
| NW5 | Player stuck, jump fired, then moves > 2m | Jump counter resets to 0, advice returns to `Continue` |
| NW6 | Player navigating, stall detected, then action changes to non-Navigate | Full reset (origin cleared, count zeroed) |
| NW7 | Player not navigating (Interact, Wait, etc.) | `Idle`, watchdog inactive |
| NW8 | Position read fails (null) | `Continue` (fail-open, don't trigger stuck on missing data) |
| NW9 | Custom thresholds: 10s stall, 5m distance, 5 max jumps | Respects configured values |
| NW10 | Stall at 4.9s → Continue, stall at 5.1s → Jump | Threshold is strict boundary |
| NW11 | Player moves 1.9m during stall window → still stalled; moves 2.1m → reset | Distance threshold is strict |
| NW12 | CastReturn returned → full reset, subsequent Navigate starts fresh window | Jump count is 0, no stall carried over |

### Engine integration tests (QuestForge.Engine.Tests/Navigation/NavStuckEngineTests.cs)

| ID | Scenario | Expected |
|----|----------|----------|
| NE1 | TravelStep emits Navigate, player stuck 5s | Engine emits `Jump` instead of `Navigate` |
| NE2 | TravelStep, player stuck, 3 jumps exhausted (with grace periods) | Engine emits `UseReturn` |
| NE3 | TravelStep, player stuck, jump succeeds (position changes) | Engine resumes `Navigate`, counter reset |
| NE4 | Step confirms (Expect met) while stuck | Watchdog resets, no jump/return |
| NE5 | Step has `Recover.OnObstacle = awaitUser("stuck")` | Engine emits `AwaitUser` instead of default `UseReturn` |
| NE6 | CombatStep navigate-to-anchor, player stuck | Watchdog triggers (Navigate action from engine, not from CombatController) |
| NE7 | Non-navigate step (talk, interact) | Watchdog stays idle |
| NE8 | Implied navigation (ResolveInteractOrNavigate emits Navigate) | Watchdog monitors it the same as explicit TravelStep Navigate |

### Combat diagnostic logging test

| ID | Scenario | Expected |
|----|----------|----------|
| CD1 | CombatStep with OverworldEnemies, no target found, Location set | Debug log entry captured by FakeLogger with actor count and DataId breakdown |

## Implementation Order

### Slice 1 — NavigationWatchdog + engine wiring + tests

1. `NavigationWatchdog` class in `QuestForge.Engine/Navigation/`
2. `WatchdogAdvice` enum in same namespace
3. `EngineAction.Jump` and `EngineAction.UseReturn` in `EngineAction.cs`
4. `INavigator.Jump()` addition
5. `FakeNavigator.Jump()` implementation
6. Verify/update `FakeTeleporter.UseReturn()` records call and updates state
7. Watchdog integration in `QuestEngine.ResolveAction`
8. Extend `MapRecoverAction` to handle `UseReturnRecoverAction` → `EngineAction.UseReturn`
9. `EngineTestHarness.RunToCompletion` + `HarnessEngine.Tick`: add Jump and UseReturn cases
10. `HarnessEngine.Tick` + `EngineHost`: add `EngineAction.Jump` to lazy dismount exemption list
11. `PluginConfig` new fields + `ConfigWindow` UI
12. Combat diagnostic log line in CombatStep arm of `ResolveAction`
13. Unit tests: NW1–NW12
14. Engine integration tests: NE1–NE8
15. Combat diagnostic test: CD1

### Slice 2 — Dalamud adapters + EngineHost dispatch

1. `VnavmeshNavigator.Jump()` — GeneralAction 2
2. `LifestreamTeleporter.UseReturn()` — GeneralAction 8
3. `EngineHost.DispatchAction` — Jump and UseReturn cases
4. `EngineHost.DispatchAction` — add Jump to lazy dismount exemption

### Slice 3 — In-game smoke test (manual)

1. Navigate to a known obstacle spot, verify stuck detection fires
2. Verify jump fires with forward momentum (ground)
3. Verify jump fires with ascent (flying)
4. Verify Return fires after max jumps
5. Verify step retries and resume fragment gets player back
6. Verify configurable thresholds work from settings UI

## Open Questions

None — all design decisions resolved in conversation.
