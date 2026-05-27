# Combat Approach Handoff + Step-Exit Cleanup — Implementation Plan

**Status:** READY FOR TEST CREATION
**Branch:** `fix/combat-approach-handoff` (off `main`)
**Input docs:** `docs/COMBAT_STEP_PART_A_PLAN.md`, `docs/COMBAT_STEP_PART_B_PLAN.md`, `docs/COMBAT_MOVE_TO_TARGET_PLAN.md`, `docs/ADAPTERS.md` (§ICombat, §INavigator), `docs/SCHEMA.md` (CombatStep)
**Output (behavior change):** Two in-game bugs observed running quest 65847 are eliminated, pinned by new engine-level and controller-level unit tests against fakes. (1) After killing the mob at `CombatStep.Location`, the player approaches and actually engages the *next* mob instead of oscillating back to the fixed `Location`; the player only returns toward `Location` when there is **no eligible target in scan range** (option B — return-on-empty, not skip-forever). (2) When a combat step completes, the leftover game target is cleared so no extra engage fires as the next (travel) step begins.
**Phase dependency:** Builds on the implemented CombatStep pipeline (Part A: `CombatController.Decide`/`Reset`; Part B: `RotationLeaseLatch`; Move-to-target: approach navigation inside `Decide`, `CombatDecision.Approach`). No new step types. No schema change.

---

## 1. Bug analysis (confirmed against code + in-game trace)

### Bug 1 — chased mob never engaged; player oscillates at `Location`
`QuestEngine.ResolveAction` drives a `CombatStep` in two legs, **navigate-first** (`QuestEngine.cs:433-450`):

1. **Navigate-first leg** — if `combatStep.Location` is set and the player is beyond `StopDistance` of it, return `Navigate(Location)`. The controller is *not* consulted (no `GetHostileActors` read this tick).
2. **Engage leg** — only reached when the player is within range of `Location`; calls `CombatController.Decide`, which selects a target and (`CombatController.cs:120-128`) issues `NavigateTo(targetPosition)` as a side effect, then returns `Engage(step, target)`.

After the mob standing at `Location` dies, `Decide` selects a wandering mob `A2` and navigates toward it. On the **next** tick the player has moved away from the fixed `Location`, so leg 1 fires `Navigate(Location)` again and yanks the player back. The two navigation intents (controller→`A2`, engine→`Location`) fight every tick. The player oscillates near `Location` and only ever kills mobs that wander into the `Location` radius; the actively-chased mob is never reached or attacked.

**Root cause:** the navigate-first leg re-pins to `Location` on every tick where the player is out of range, *without first checking whether there is a live target worth chasing*. The fix is to ask the controller first: if a target is in scan range, the controller owns movement; only when there is **no eligible target in range** does the fixed `Location` get to pull the player back (to roam the spawn area / wait for respawns).

### Bug 2 — one extra engage while moving to the next objective
When the kill-count predicate finally passes, `QuestEngine.ResolveAction` confirms the step (`QuestEngine.cs:395-402`) and calls `_combatController.Reset()`. `Reset()` (`CombatController.cs:169-175`) only nulls internal latches — it does **not** issue `ClearTarget` via `ICombat`, and it leaves the game's selected target in place. The next tick emits a non-Engage action (`Navigate`/`Interact` for the following travel step). The `RotationLeaseLatch` (driven in `EngineHost.DispatchAction`, `EngineHost.cs:233`) *does* call `StopRotation` on that first non-Engage action — but `StopRotation` releasing the WrathCombo lease is not instantaneous in-game, and the **leftover game target** means the still-winding-down rotation fires one more weaponskill/engage at the dead/last target as the player walks off. Cleanup never clears the target.

**Root cause:** step-exit cleanup nulls engine-side latches but never tells the game to drop the target. Rotation-stop is lease-managed (the latch handles it correctly); target-clear is not, and it is the missing half.

---

## 2. Architectural decisions

### D1 — Option B: the controller is consulted FIRST; `Location` is a fall-back, not a gate

**Decision (Fix 1, option B):** Flip the leg order inside the `CombatStep` arm. To decide "is there a target worth chasing?" the engine must consult the controller, so `Decide` runs **first** every combat tick. The fixed `Location` navigate becomes a *fall-back* taken **only when the controller found no eligible target in scan range** and the player has drifted out of the spawn area:

```csharp
if (step is CombatStep combatStep)
{
    // B: controller FIRST. It scans (GetHostileActors), selects, and—if a target is
    // live—owns ALL approach movement. The fixed Location is only a fall-back for the
    // no-target case (roam back to the spawn area / wait for respawns).
    var decision = await _combatController.Decide(combatStep, ct);

    if (decision.Target is not null)
    {
        // A target is in range: the controller owns approach navigation (it already
        // issued NavigateTo inside Decide). Do NOT navigate to Location — that is the
        // oscillation. Engage.
        return (new EngineAction.Engage(combatStep, decision.Target), step.Id);
    }

    // No eligible target in scan range. The step's kill predicate is necessarily UNMET
    // here (a met Expect would have confirmed the step at QuestEngine.cs:395 before this
    // arm). If the player has drifted beyond StopDistance of the spawn anchor, roam back
    // so respawns land in scan range again.
    if (combatStep.Location is not null)
    {
        var nav = ResolveInteractOrNavigate(
            step, combatStep.Location.Position, playerPos,
            new EngineAction.Wait("combat-roam-sentinel"));
        if (nav is EngineAction.Navigate navAction)
            return (navAction, step.Id);
    }

    // No target and already at/within Location (or Location unset): stand and wait for a
    // respawn to enter scan range. Engage(null) is the engine's no-target combat idle —
    // it carries no target, fires no rotation (decision.RotationShouldRun is false), and
    // is a forward decision (never a stall).
    return (new EngineAction.Engage(combatStep, null), step.Id);
}
```

Precise gating for the `Navigate(Location)` fall-back — **all** must hold:
1. `decision.Target is null` (controller found no eligible target in the 30 m scan radius this tick), AND
2. the kill predicate is unmet (guaranteed — a met `Expect` confirms-and-skips at `QuestEngine.cs:395` before control reaches this arm), AND
3. `combatStep.Location is not null` AND the player is beyond `StopDistance` of `Location.Position` (the `ResolveInteractOrNavigate` proximity check; if within `StopDistance` it returns the `Wait` sentinel, which is *not* an `EngineAction.Navigate`, so we fall through to `Engage(null)`).

**Why option B over "skip-forever" (rejected alternative):**
- *Skip-forever (the prior plan: latch `CombatStarted`, never navigate to `Location` again once combat starts).* Rejected per the user's choice. Skip-forever wanders unboundedly: after killing the local mob the player chases `A2`, then `A3`, drifting arbitrarily far from the authored spawn anchor with no way home, and in a sparse-respawn area can strand itself where no mob will ever path into scan range — a silent stall. Option B keeps the player tethered to the authored spawn area: chase what is in range, but when range empties, roam back to where respawns appear.

**Why controller-first leg order (rejected alternative):**
- *Keep navigate-first, add a "has a live target" pre-check via a cheaper read.* Rejected: the only authoritative "is there an eligible kill target in range" answer is `KillPriority.SelectTarget` over `GetHostileActors` — i.e. exactly what `Decide` computes. Any cheaper proxy (e.g. `IsPlayerInCombat`) is wrong: it is true for unrelated aggro and false in the dead window between two kills. Reusing `Decide`'s own result is precise and adds no extra read (D4: `Decide` already runs every combat tick).

### D2 — `CombatStarted` latch is DROPPED (redundant under B)

**Decision:** Do **not** add `_combatStarted` / `CombatStarted`. The monotonic latch existed solely to suppress the navigate-first leg after combat began. Under B the suppression is structural, not stateful:

- While a target is in range, `decision.Target is not null` ⇒ we `Engage` and never reach the `Location` fall-back. No latch needed to suppress it.
- The dead window between mobs (target momentarily null) is handled by the *same per-tick* `decision.Target` test, not by remembering that combat once started.
- The very first approach (player far from `Location`, no mob yet in the 30 m scan radius) is handled by the **same** `Navigate(Location)` fall-back: `Decide` returns null ⇒ player is beyond `StopDistance` ⇒ navigate to `Location`. The fall-back *is* the initial walk-to-area leg. No separate initial-approach leg, and no latch to distinguish "before first acquisition" from "after".

**Verdict: the latch earns nothing under B and is removed.** This is strictly simpler than the prior design: one fewer field, one fewer property, no reset wiring for it. The only state the controller keeps is what it already had (`_currentTarget`, `_approachTarget`, `_wasInCombat`, `_cachedAttackRange`).

**What breaks if someone re-introduces a latch anyway:** a monotonic "combat started" gate would re-suppress the `Location` fall-back, recreating skip-forever and its sparse-area stall — the exact behavior B was chosen to avoid. The fall-back must be driven only by the *current* tick's `decision.Target`, never by history.

### D3 — Step-exit cleanup: `ResetAsync` clears the target and stops approach nav; rotation-stop stays with the lease latch

**Decision (Fix 2, unchanged from the prior plan):** Add an async cleanup method to `CombatController` and have `QuestEngine` call it at the two existing reset sites. Keep the synchronous `Reset()` for callers that only need the latch clear, but route the engine's step-exit path through `ResetAsync`:

```csharp
/// <summary>
/// Step-exit cleanup. Idempotent. Issues ClearTarget so the game drops the leftover
/// target (preventing one extra engage as the next step begins) and Stop so any in-flight
/// approach navigation halts. Then clears all per-step latches.
///
/// Does NOT call StopRotation — the rotation lease is owned by RotationLeaseLatch
/// (EngineHost.DispatchAction issues StopRotation on the first non-Engage action of the
/// next step). Issuing StopRotation here would race/double-stop against the lease.
/// </summary>
public async Task ResetAsync(CancellationToken ct)
{
    if (_currentTarget is not null)
        await _combat.ClearTarget(ct);   // drop the game target — the missing half of cleanup
    if (_approachTarget is not null)
        await _navigator.Stop(ct);       // halt any in-flight approach
    Reset();                             // null all latches
}

// Reset() unchanged — latch-only, no IO (no _combatStarted, since D2 drops it):
public void Reset()
{
    _currentTarget     = null;
    _approachTarget    = null;
    _wasInCombat       = false;
    _cachedAttackRange = null;
}
```

`QuestEngine` (already holds `_combat` and `_combatController`) calls `ResetAsync` at the two sites that currently call `Reset()`:
- sequence change (`QuestEngine.cs:368`)
- confirmed `CombatStep` (`QuestEngine.cs:400-401`)

```csharp
// sequence change
if (_lastKnownSequence != -1 && _lastKnownSequence != currentSeq)
{
    _confirmedStepIds.Clear();
    _resumePointExecutedIds.Clear();
    _activeResumeFragment = null;
    await _combatController.ResetAsync(ct);   // was: _combatController.Reset();
}

// confirmed combat step
if (step is CombatStep)
    await _combatController.ResetAsync(ct);    // was: _combatController.Reset();
```

**Why controller-direct `ClearTarget` (rejected alternatives):**
- *Emit a new `EngineAction.StopCombat`/`ClearTarget` action so the engine's one-action-per-tick model carries the cleanup.* Rejected: cleanup must happen on the *same* tick the step is confirmed/advances — but that tick the engine returns the **next step's** action (`Navigate`/`Wait`). A one-action-per-tick model has no slot for a second "clear" action without delaying the next step by a tick (re-introducing the very window where the extra engage fires). Calling `ICombat.ClearTarget` directly during cleanup is immediate and tick-aligned.
- *Have `RotationLeaseLatch` also clear the target.* Rejected: the latch's contract is strictly rotation lease lifecycle (`Start`/`Stop`); target identity is the controller's responsibility (`SetTarget`/`ClearTarget` already live there). Splitting target-clear across two objects breaks the single-owner invariant for `_currentTarget`.
- *Make `Reset()` itself async and call `ClearTarget`.* Rejected: `Reset()` is also exercised synchronously by controller-level tests (`CC_Reset_AfterReset...`, `G22_Reset...`) which assert `Reset()` does **not** call `Stop`/IO. Changing `Reset()`'s IO behavior would break those pins. A separate `ResetAsync` keeps the synchronous latch-only `Reset()` contract intact for those tests and adds the IO-bearing variant for step exit.

**Cooperation with `RotationLeaseLatch`:** `ResetAsync` deliberately does **not** call `StopRotation`. The lease is released exactly once, by the latch, when `EngineHost.DispatchAction` sees the next step's non-Engage action (`EngineHost.cs:233`). `ResetAsync` only owns `ClearTarget` (target identity) and approach `Stop` (navigation) — orthogonal to the lease. There is therefore no double-stop and no race: rotation-stop = latch; target-clear = controller.

**Idempotency:** `ResetAsync` guards `ClearTarget` on `_currentTarget is not null` and `Stop` on `_approachTarget is not null`, so a step that exits without ever engaging (e.g. expect already true on entry) issues neither — no spurious IO. Safe to call when nothing was engaged.

### D4 — D6 fixture-starvation invariant unchanged (combat-only scanning)

`Decide` (the only caller of `GetHostileActors`) is still reached **only** inside the `step is CombatStep` arm. Under B `Decide` is now the *first* thing that arm does, and it runs **every combat tick** — that is fine: D6 only forbids `GetHostileActors` on **non-combat** steps. Non-combat steps never enter the `CombatStep` arm, so they never call `Decide`/`GetHostileActors`. `ResetAsync` reads no `GetHostileActors`. The `NS-*` non-combat tests remain green unchanged. The leg-order flip cannot leak `Decide` onto a non-combat path — the arm is still guarded by `step is CombatStep`.

### D5 — Between-mobs null tick does NOT cause a spurious pull-back

**The concern:** when `A1` dies before `A2` is acquired, `Decide` may briefly return `Target == null` for one tick. Does that wrongly fire `Navigate(Location)` and yank the player back?

**Analysis:** the fall-back fires only if **all three** D1 conditions hold, and condition (3) — "player beyond `StopDistance` of `Location`" — bounds it:

- If the player is **near** `Location` (within `StopDistance`) during the dead tick, `ResolveInteractOrNavigate` returns the `Wait` sentinel, not a `Navigate`, so we fall through to `Engage(null)`. No pull-back. (This is the common case: `A1` was killed at/near the spawn anchor.)
- If the player is **far** from `Location` during the dead tick (chased `A1` out, killed it, `A2` not yet selected), a single `Navigate(Location)` *can* be emitted that tick. But: (a) it is at most a 1-tick nudge — the very next tick `A2` is in scan range, `Decide` returns it, and we `Engage` + approach `A2`, abandoning the `Location` nudge; (b) `INavigator.NavigateTo` to `Location` and then immediately re-targeting `A2`'s position the next tick is the normal navmesh retarget the controller already does on every target change — no oscillation, because the trigger (`Target == null`) is gone by the next tick. There is no *sustained* fight: the `Location` nudge requires `Target == null`, and `Target == null` does not persist while `A2` is in range.

**Is the 1-tick nudge worth bounding further?** No. It is self-correcting within one tick and only occurs when the player is *already* beyond the spawn area with no live target — exactly when nudging back toward the spawn anchor is the *correct* roam behavior anyway. We do **not** add a debounce/grace-tick: it would add state and a tuning knob to suppress a nudge that is already both bounded and directionally correct. The GWT EH-3 scenario pins the near-`Location` case (no navigate); EH-3b pins that the far case self-corrects to `Engage(A2)` on the next tick rather than oscillating.

### D6 — Non-starvation: no-target-at-`Location` waits without deadlocking the "make progress" invariant

**The concern:** B's no-target-at-`Location` branch returns `Engage(null)` (stand and wait for respawn). Does waiting for a respawn deadlock against the `NonStarvation*` invariant that the engine must always "make a decision, never stall"?

**Analysis:** the `NonStarvation*` invariant is "every tick produces a forward `EngineAction`, never a hang/throw/no-op-spin" — it is **not** "every tick changes game state". `Engage(null)` *is* a forward decision: it is a concrete `EngineAction` returned in bounded time, carrying no target and firing no rotation (`decision.RotationShouldRun == false`). The engine is not blocked, not throwing, not spinning — it is correctly idling on a combat step whose targets are temporarily exhausted, exactly as a `Wait` action idles a non-combat step awaiting a sequence advance (`QuestEngine.cs:455`). Progress resumes deterministically the moment a respawn enters the 30 m scan radius: next `Decide` returns it ⇒ `Engage(target)`. This cannot deadlock the invariant because the invariant never required state mutation — only a per-tick decision, which `Engage(null)` satisfies. (`NS-*` tests assert no `GetHostileActors` leak on non-combat ticks and that fixtures don't starve; they do not assert combat ticks mutate state, so they are unaffected.)

---

## 3. Public surface delta (summary)

| Type | Member | Change |
|------|--------|--------|
| `CombatController` | `Task ResetAsync(CancellationToken ct)` | **new** async cleanup (ClearTarget + Stop + latch clear) |
| `CombatController` | `Reset()` | **unchanged** — latch-only, no IO (no new `_combatStarted` field) |
| `CombatController` | `Decide` | **unchanged** — already returns `CombatDecision.Target` / `RotationShouldRun` |
| `QuestEngine` | `ResolveAction` CombatStep arm | **leg order flipped** — call `Decide` first; `Location` navigate becomes the no-target fall-back |
| `QuestEngine` | two `Reset()` call sites | become `await ResetAsync(ct)` |

No `_combatStarted` latch (dropped per D2). No schema change, no new `EngineAction` (reuses `Engage` with a null target for the idle case), no new adapter interface, engine stays Dalamud-free.

---

## 4. Given-When-Then specs

### Controller-level (extend `CombatControllerTests` / `CombatControllerApproachTests`)

Fakes: `FakeGameStateProvider`, `FakeCombat`, `FakeNavigator`. Observe `combat.RecordedTargets`, `navigator.RecordedNavigationRequests`, `navigator.RecordedStops`, and the returned `CombatDecision`.

**CS-1 — Decide returns null target when no eligible hostile in range**
- Given a fresh `CombatController`, no hostiles added.
- When `Decide` runs once.
- Then `decision.Target == null` AND `decision.RotationShouldRun == false` AND no `SetTarget` recorded. (This is the signal the engine uses to take the `Location` fall-back.)

**CS-2 — Decide returns a target when one eligible hostile is in range**
- Given one eligible hostile (DataId 100, in attack range).
- When `Decide` runs once.
- Then `decision.Target` is that actor AND exactly one `SetTarget` recorded AND `decision.RotationShouldRun == true`.

**CS-3 — ResetAsync clears the game target when engaged**
- Given hostile X acquired on tick 1 (one `SetTarget(X)` recorded).
- When `await ResetAsync(ct)`.
- Then `combat.RecordedTargets` ends with exactly one `ClearTarget` (total: `SetTarget(X)` then `ClearTarget`).

**CS-4 — ResetAsync is idempotent when nothing was engaged**
- Given a fresh controller, `Decide` never called (no target, no approach).
- When `await ResetAsync(ct)`.
- Then NO `ClearTarget` recorded AND NO `Stop` recorded (guards on null latches). No exception.

**CS-5 — ResetAsync stops in-flight approach navigation**
- Given a melee job, an out-of-range hostile acquired on tick 1 (one `NavigateTo` issued, `_approachTarget` set).
- When `await ResetAsync(ct)`.
- Then `navigator.RecordedStops` has exactly one `Stop`.

**CS-6 — ResetAsync never calls StopRotation**
- Given a target acquired.
- When `await ResetAsync(ct)`.
- Then `combat.RecordedRotation` contains NO `StopRotation` entry (rotation-stop is the lease latch's job; `ResetAsync` must not touch it). Pins D3 cooperation with `RotationLeaseLatch`.

**CS-7 — Reset() still performs no IO (regression pin)**
- Given a target acquired and an out-of-range approach latched.
- When the synchronous `Reset()` is called (NOT `ResetAsync`).
- Then NO `ClearTarget`, NO `Stop`, NO `StopRotation` recorded — only latches cleared. Protects existing `CC_Reset_AfterReset...` and `G22_Reset...` contracts.

### Engine-level (extend `CombatEngineTests`)

Harness: `EngineTestHarness`. Observe the `EngineAction` per tick, `harness.GameState.RecordedReads`, `harness.Combat.RecordedTargets`, `harness.Navigator.RecordedNavigationRequests`.

**EH-1 — initial approach navigates to Location when no mob is in scan range (no regression, now via the fall-back)**
- Given a one-combat-step quest, `Location` at (100,0,100), player at (0,0,0), default StopDistance, expect unmet, AND **no hostiles within the 30 m scan radius** of the player at (0,0,0).
- When tick 1: `Decide` runs first, returns `Target == null` (nothing in scan range); the engine takes the no-target fall-back; player is beyond StopDistance of Location.
- Then action is `Navigate` to (100,0,100). Note: under B, tick 1 **does** read `GetHostileActors` (Decide runs first). This replaces the old EH-1's "no GetHostileActors on the navigate-first tick" assertion — D6 only forbids the read on *non-combat* steps, and this is a combat step. (The old `EC_NavigateFirst...` no-read-on-this-tick assertion is RETIRED; see §5.)

**EH-2 — once a target is in range, the engine engages and does NOT re-pin to Location (Bug 1 pin)**
- Given the same quest; player starts AT (100,0,100) (in range of Location); one eligible hostile present at a position FAR from Location (e.g. (140,0,140), distance 50) so it is out of attack range but **within the 30 m scan radius**.
- When tick 1: `Decide` runs first, acquires the target, issues `NavigateTo(targetPos)`; `decision.Target` is non-null; action is `Engage`.
- Then simulate the player having moved toward the mob and away from Location: `harness.GameState.SetPosition((130,0,130))` (now >StopDistance from Location at (100,0,100)), mob still in scan range.
- When tick 2: `Decide` returns the (still live) mob.
- Then action is `Engage` again (NOT `Navigate` to (100,0,100)) AND the only `NavigateTo` requests recorded target the mob position, never (100,0,100) after tick 1. **This is the Bug 1 pin** — a live target in range means the controller owns movement; the engine must not yank the player back to Location.

**EH-3 — no eligible target while near Location → waits, no spurious navigate**
- Given combat in progress; player WITHIN StopDistance of Location at (100,0,100); all hostiles momentarily cleared (none in scan range).
- When a tick runs: `Decide` returns `Target == null`; player is within StopDistance, so the `Location` fall-back returns the `Wait` sentinel (not a Navigate).
- Then action is `Engage(step, null)` (the no-target idle) — NOT `Navigate(Location)` and NOT a stall. `decision.RotationShouldRun` is false. No new `NavigateTo` to (100,0,100) is recorded. Pins D5 (near case) and D6 (waiting is a forward decision).

**EH-3b — between-mobs far null tick self-corrects (no oscillation)**
- Given combat in progress; player at (130,0,130) (BEYOND StopDistance of Location at (100,0,100)); the chased mob just died so scan range is momentarily empty on this tick.
- When tick N: `Decide` returns `Target == null`, player beyond StopDistance ⇒ a single `Navigate(100,0,100)` fall-back is emitted.
- Then on tick N+1 a new eligible hostile `A2` is in scan range at (135,0,135): `Decide` returns `A2`, action is `Engage`, and the recorded navigation retargets `A2`'s position — the `Location` nudge is abandoned, not repeated. Pins D5 (far case): the null tick produces at most one bounded nudge and self-corrects; it does NOT oscillate.

**EH-4 — no eligible target AND no Location set → waits (idle), never navigates**
- Given a one-combat-step quest with `Location == null`; no hostiles in scan range; expect unmet.
- When a tick runs: `Decide` returns null; no Location to fall back to.
- Then action is `Engage(step, null)` (idle) AND no `NavigateTo` recorded this tick. Pins the `Location`-unset path of D1.

**EH-5 — confirmed combat step clears the game target (Bug 2 pin)**
- Given a two-sequence quest: seq 0 a `CombatStep` (expect `questVariable(Q,0) >= 1`), seq 1 a `TalkStep`; player in range, one eligible hostile.
- When tick 1: variables[0]=0 → `Decide` acquires target → `Engage` (controller `SetTarget(X)` recorded).
- Then flip variables[0]=1.
- When tick 2: expect true → step confirmed; engine runs `ResetAsync`.
- Then `harness.Combat.RecordedTargets` contains a `ClearTarget` issued during that confirmation AND tick 2's action is NOT `Engage` (it is the seq-0 fall-through `Wait` / the next step's action). **This is the Bug 2 pin** — leftover target cleared so the next step cannot fire an extra engage.

**EH-6 — sequence-change cleanup clears the target**
- Given combat started in seq 0 with a target acquired.
- When the game advances `SetQuestSequence` to seq 1 (talk step) and a tick runs (sequence-change branch fires `ResetAsync`).
- Then a `ClearTarget` is recorded AND tick action is not `Engage`.

**EH-7 — reset on advance still allows a fresh SetTarget on re-entry (assertion tweak)**
- The existing `EX_ResetOnAdvance...` scenario must stay green: after `ResetAsync` clears state, re-entering seq 0 produces a fresh `SetTarget`. There will now additionally be a `ClearTarget` between the two `SetTarget`s — **update the existing assertion to count `SetTarget` calls specifically (filter `!IsClear`), not total `RecordedTargets`.** This is the one existing test needing an assertion tweak.

**EH-8 — non-combat step tick never reads GetHostileActors (D4/D6 regression)**
- The existing `NS_*` tests must stay green unchanged. A non-combat tick never enters the `CombatStep` arm, so `Decide`/`GetHostileActors` is never called. Re-run as guard.

---

## 5. Existing tests at risk & compatibility

| Test | Risk | Resolution |
|------|------|------------|
| `CombatEngineTests.EC_NavigateFirst...` | Under B, `Decide` runs FIRST on a combat tick, so this tick now DOES read `GetHostileActors`; the old "no GetHostileActors on the navigate-first tick" assertion is no longer true. | **Replace** with EH-1: assert action is `Navigate(Location)` when no mob is in scan range, and DROP the no-read assertion (the read is now expected and legal on a combat step). The navigate-to-Location *behavior* is preserved (initial approach still works), only the leg ordering changed. |
| `CombatEngineTests.EX_ResetOnAdvance...ProducesNewSetTarget` | Asserts `RecordedTargets.Count == 2`. `ResetAsync` now inserts a `ClearTarget`, making total 3. | **Update assertion** to count non-clear `SetTarget` calls == 2 (filter `!IsClear`). Documented as EH-7. |
| `CombatControllerTests.CC_Reset_AfterReset...` | Calls synchronous `Reset()`, asserts 2 `SetTarget`. | Safe: `Reset()` keeps its latch-only, no-IO contract (D3). Pinned by CS-7. |
| `CombatControllerApproachTests.G22_Reset_ClearsApproachLatch_DoesNotStop` | Asserts `Reset()` does NOT call `Stop`. | Safe: `Stop` moved to `ResetAsync`, not `Reset`. Pinned by CS-7. |
| `RotationLeaseLatchTests.*` | `ResetAsync` could double-stop the rotation. | Safe: `ResetAsync` never calls `StopRotation` (D3, CS-6). Lease lifecycle untouched. |
| `CombatNonStarvationTests.*`, `CombatNonStarvationB1Tests.*` | Leg-order flip could leak `Decide` onto a non-combat path; or the `Engage(null)` idle could be read as a stall. | Safe: the arm is still guarded by `step is CombatStep` (D4); `Engage(null)` is a forward decision, not a stall (D6). Re-run as EH-8. |
| `CombatControllerTests.CC_*` (set/clear/retarget) | Removing the (never-added) `_combatStarted` latch — N/A; no latch was ever shipped. | No change: D2 means there is no latch to remove from shipped code; this plan simply does not add one. |

---

## 6. Implementation order

- **A — Controller cleanup (no engine change yet).** Add `ResetAsync` (ClearTarget + Stop + `Reset()`). Leave `Reset()` latch-only. Land CS-1..CS-7 green. *(done before B)*
- **B — Engine leg-order flip + reset routing.** In the `CombatStep` arm: call `Decide` first; on non-null target `Engage`; on null target take the `Location` fall-back (Navigate when beyond StopDistance) else `Engage(null)`. Switch the two `Reset()` sites to `await ResetAsync(ct)`. Land EH-1..EH-6 green; apply the EH-7 assertion tweak; replace `EC_NavigateFirst...`'s no-read assertion. *(done before C)*
- **C — Full suite.** Run `QuestForge.Engine.Tests`; confirm all combat suites + non-starvation (EH-8) green.

Build/test: `$env:PATH = "C:\Users\publi\.dotnet;" + $env:PATH; & "C:\Users\publi\.dotnet\dotnet.exe" test QuestForge.Engine.Tests/QuestForge.Engine.Tests.csproj`

---

## 7. Done criteria

1. When a mob is in scan range, the engine emits `Engage` (with the controller navigating to that mob) and **never** emits `Navigate(Location)` that tick (EH-2). The fixed `Location` is never a navigate destination while a live target is in range.
2. When there is **no eligible target in scan range** and the player has drifted beyond `Location`'s `StopDistance`, the engine emits a single `Navigate(Location)` to roam back; once a mob re-enters scan range the next tick engages it (EH-1, EH-3b).
3. When there is no eligible target and the player is at/within `Location` (or `Location` is unset), the engine emits `Engage(step, null)` (idle, no rotation) — never `Navigate` and never a stall; progress resumes when a respawn enters scan range (EH-3, EH-4).
4. A 1-tick between-mobs null does not cause sustained oscillation: at most one bounded `Navigate(Location)` nudge, self-correcting to `Engage(target)` on the next tick (EH-3b).
5. On combat-step confirmation and on sequence change, a `ClearTarget` is issued via `ICombat` and no `Engage` action is produced that tick (EH-5, EH-6).
6. `ResetAsync` issues `ClearTarget` only when a target was held and `Stop` only when an approach was in flight; calling it on an unengaged step is a no-op (CS-3, CS-4, CS-5).
7. `ResetAsync` never calls `StopRotation`; the rotation lease remains exclusively the `RotationLeaseLatch`'s responsibility (CS-6).
8. `Decide`/`GetHostileActors` is called **only** on `CombatStep` ticks (every combat tick is fine); no non-combat tick reads `GetHostileActors` (EH-8). No `_combatStarted` latch exists (D2).
9. Synchronous `Reset()` performs no adapter IO (CS-7); existing controller reset tests pass unchanged.

---

## 8. Exclusions

- No change to target *selection* / kill-priority (`KillPriority`).
- No change to attack-range resolution or the approach cadence inside `Decide` (move-to-target logic stays as-is).
- No `_combatStarted` latch (explicitly dropped per D2 — option B makes it redundant).
- No debounce/grace-tick for the between-mobs null (D5: the 1-tick nudge is bounded and self-correcting; adding a grace timer is out of scope).
- No new `EngineAction` (the no-target idle reuses `Engage` with a null target), no new adapter interface, no schema change.
- No live-switch of `TraceMode`, no UI change.
- No handling of "target permanently unreachable → give up" beyond the existing per-tick decision guarantee (a dedicated unreachable-target recovery ladder is out of scope).
- No re-recording of existing non-combat fixtures (D4 preserved — no new reads on non-combat paths). Combat-step fixtures, if any exist, already record `GetHostileActors`; the leg-order flip changes *when* within a combat tick it is read, not *whether*, so a combat fixture may need re-record — but none ship today.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §4.
- Happy paths: 4 scenarios (CS-2, CS-3, CS-5, EH-2)
- Edge cases: 7 scenarios (CS-1, CS-4, EH-1, EH-3, EH-3b, EH-4, EH-6)
- Error/regression-pin cases: 5 scenarios (CS-6, CS-7, EH-5, EH-7, EH-8)
- Expected total: ~16 tests in QuestForge.Engine.Tests (7 controller-level extending CombatControllerTests/CombatControllerApproachTests; 9 engine-level extending CombatEngineTests, one of which (EH-7) is an assertion tweak to EX_ResetOnAdvance and one (EH-1) replaces the no-read assertion in EC_NavigateFirst).

---

# Addendum — Follow-up after in-game test (moving-target re-path + attack range 1)

**Status:** READY FOR TEST CREATION (addendum)
**Branch:** `fix/combat-approach-handoff` (same branch; the handoff fix above is implemented + reviewer-approved, NOT yet committed)
**Trigger:** In-game testing of quest 65847 confirmed the approach handoff (Bug 1 / Bug 2 above) works. A residual stall surfaced: when the chased target **moves while the player is still approaching it** (before reaching attack range), the player walks to the target's *stale* position and stalls — the same symptom as Bug 1, now per-target. This addendum adds two changes on top of the approved diff. Both are pure-engine, Dalamud-free; no schema change, no new adapter interface, no new `EngineAction`.

## A1. Residual-bug analysis (confirmed against `CombatController.cs:96-134`)

The approach branch latches a single `NavigateTo(targetPosition)` by **target identity** only:

```csharp
else if (target != _approachTarget)            // identity changed -> re-navigate
{
    _approachTarget = target;
    var opts = new NavigationOptions(StoppingDistance: attackRange, ...);
    await _navigator.NavigateTo(position, opts, ct);
    approach = ApproachState.Approaching;
}
else                                            // SAME target -> "do nothing"
{
    approach = ApproachState.Approaching;
}
```

The `else` ("do nothing") branch is correct for a *stationary* target — it deliberately avoids spamming vnavmesh every tick on positional jitter. But if the **same** target moves a long way during the approach, identity is unchanged, so we never re-issue `NavigateTo`. vnavmesh keeps driving the player to the now-stale destination; the player arrives there, the target is no longer within `attackRange`, and — because the player never reached in-range — `_approachTarget` is never nulled, so no re-navigate ever fires. Stall.

(The in-range -> out-of-range *re-acquisition* case is already handled: reaching in-range nulls `_approachTarget` at `CombatController.cs:113-117`, so the next out-of-range tick takes the `target != _approachTarget` branch and re-navigates. The *un*handled case is movement strictly **during** approach.)

The fix is to make the "do nothing" branch conditional: re-issue `NavigateTo` when the live target has drifted from the position we last navigated to. This must interact carefully with **Change 1** (attack range shrinks to 1 m), or a badly chosen threshold strands the player out of range.

## A2. Change 1 — melee/fallback attack range 3 -> 1 (user-approved)

### Edits to `QuestForge.Engine/Combat/JobRangeTable.cs`

```csharp
public const float MeleeRange    = 1.0f;   // was 3.0f
public const float RangedRange   = 20.0f;  // unchanged
public const float FallbackRange = 1.0f;   // was 3.0f (user wants the job-read-failure fallback at 1 too)
```

`Classify` is unchanged. `AttackRange` is unchanged in *shape*: it still maps **both** `CombatRole.Tank` and `CombatRole.Melee` to `MeleeRange`, so tanks (e.g. MRD for quest 65847) also get 1 m — **intended**. `PhysicalRanged`/`Caster`/`Healer` keep `RangedRange` (20 m). `Unknown` keeps `FallbackRange` (now 1 m). All ranges remain strictly `< ScanRadius` (30 m) — the class-doc invariant holds (1 < 30, 20 < 30); the `AttackRange_AllMappedIds_IsUnderScanRadius` theory keeps passing unchanged.

### `JobRangeTableTests` assertions that must change

| Test | Line | Current | New |
|------|------|---------|-----|
| `AttackRange_PGL_Returns3m` | 34-38 | `Assert.Equal(3.0f, range)` (PGL = Melee) | `1.0f`. **Rename** to `AttackRange_PGL_Returns1m`. |
| `AttackRange_PLD_Returns3m` | 48-53 | `Assert.Equal(3.0f, range)` (PLD = Tank -> MeleeRange) | `1.0f`. **Rename** to `AttackRange_PLD_Returns1m`. Pins Tank->Melee mapping at the new value. |
| `AttackRange_UnknownId_ReturnsFallback3m` | 108-114 | `Assert.Equal(FallbackRange, range)` then `Assert.Equal(3.0f, range)` | keep the first line; second becomes `Assert.Equal(1.0f, range)`. **Rename** to `...ReturnsFallback1m`. |
| `Constants_HaveExpectedValues` | 164-170 | `3.0f` MeleeRange; `20.0f` RangedRange; `3.0f` FallbackRange | MeleeRange -> `1.0f`; RangedRange stays `20.0f`; FallbackRange -> `1.0f`. |

Unchanged in `JobRangeTableTests`: all `Classify_*` role assertions (G1-G6 roles), all `AttackRange_BRD/BLM/WHM` (20 m, unaffected), and the `AttackRange_AllMappedIds_IsUnderScanRadius` theory (1 m and 20 m both < 30 m).

### Other tests touching the old 3 m melee range (must update or confirm)

| File / test | Effect of 3->1 | Action |
|-------------|----------------|--------|
| `CombatControllerApproachTests.G8` (line 107) | asserts `StoppingDistance == 3.0f` for a PGL approach | change to `0.5f` (`NavigationOptions.StoppingDistance` is now `ApproachStopBuffer`, decoupled from `attackRange` — see A3). |
| `CombatControllerApproachTests.G10` (line 146) | PLD, `distance: 3f`, asserts `InRange`, empty nav | **3 m is now OUT of range (3 > 1)** -> would issue NavigateTo and report `Approaching`, breaking the assertion. Change `distance: 3f` -> `distance: 1f` (or `0.5f`) to keep it in-range. |
| `CombatControllerApproachTests.G11` (line 164) | PGL, `distance: 3.0f`, asserts exact-boundary `InRange` | same break. Change `distance: 3.0f` -> `distance: 1.0f` so it still pins "exact boundary == in range" at the new range. |
| `CombatControllerApproachTests.G20` (line 396) | job-read-fail fallback, asserts `StoppingDistance == 3.0f` | change to `0.5f` (`StoppingDistance` is `ApproachStopBuffer`, range-independent — see A3). The target at `distance: 4f` stays out of fallback range (4 > 1), so `Approaching` is still correct. |
| `CombatControllerResetAsyncTests.MakeEngagedController(distance: 2f)` (CS-2/CS-3/CS-6) | comments call 2 m "in range"; 2 > 1 now means *out* of range | **No assertion breaks** — none of CS-2/CS-3/CS-6 assert in-range-specific no-nav/`Approach`; they assert SetTarget/ClearTarget/no-StopRotation, all range-independent. Update the **comments** only (cosmetic). CS-5 already uses `distance: 8f` — unchanged. |
| `CombatControllerTests.CC_*` (distance 5/8) | already > 3 and > 1; assert only SetTarget/ClearTarget/`decision.Target` | **No change** — never assert nav requests or `Approach`. |
| `QuestEngine.cs:469` `DefaultStopDistance = 3.0f` | **NOT the melee range** — it is the navigate-to-`Location` proximity for the no-target roam fall-back (D1) | **Do NOT change.** Confirmed independent of `JobRangeTable`. |
| `CombatFixtureTests`, `CombatNonStarvation*` | no distance/range literals; assert reads/actions only | **No change.** |

**Confirmed:** nothing in the engine hard-codes 3.0 expecting the old melee range. The only two `3.0f` literals in the engine are `JobRangeTable.MeleeRange`/`FallbackRange` (being changed) and `QuestEngine.DefaultStopDistance` (unrelated Location stop distance, stays).

## A3. Change 2 — re-navigate to track a moving target

### New latch + the re-path rule

**Decouple the navigate stop distance from the attack range (Option A).** The previous draft set the approach `StoppingDistance` equal to `attackRange` *and* the re-path threshold equal to `attackRange`. This strands a *settled* (arrived, then idle) player — vnavmesh stops the player exactly at the attack-ring edge relative to the **stale** point, so any residual drift pushes the live target outside the ring with no re-path. (See "Why the old design stranded" below.) The fix uses two **distinct** constants:

- `ApproachStopBuffer` — how far vnavmesh stops *short of the target's live position*. A small fixed melee buffer **strictly below** `attackRange`, not `attackRange` itself. This is the *navigate* stop distance.
- `attackRange` — the in-range/attack threshold, **unchanged**. The in-range check (`distance <= attackRange`, `CombatController.cs:111`) still uses the **live** target distance (`actor.DistanceToPlayer`); it is untouched by this addendum.

```csharp
// QuestForge.Engine/Combat/CombatController.cs (constant beside the controller)
//
// How far short of the target's LIVE position vnavmesh is told to stop. Strictly < MeleeRange (1 m)
// so that, after arrival, player-to-live-target ~= ApproachStopBuffer < attackRange => already in range.
// Non-zero so vnavmesh is not driven into the mob's collision capsule (StoppingDistance 0 makes vnavmesh
// fight the hitbox and jitter in place). 0.5 m sits comfortably inside a 1 m melee ring with slack.
private const float ApproachStopBuffer = 0.5f;
```

Add a `WorldPosition? _approachPosition` field beside `_approachTarget`: the destination of the last `NavigateTo` we issued for the current approach. Replace the `else if (target != _approachTarget) { ... } else { ... }` block (`CombatController.cs:120-133`) with:

```csharp
else // out of attack range
{
    var identityChanged = target != _approachTarget;

    // Re-path if the live target drifted from where we last navigated by more than the
    // re-path threshold. _approachPosition is the destination we last issued NavigateTo to.
    var drifted =
        _approachPosition is { } last &&
        last.DistanceTo(position) > RepathThreshold(attackRange);

    if (identityChanged || _approachPosition is null || drifted)
    {
        _approachTarget   = target;
        _approachPosition = position;                       // remember where we sent the player
        // Navigate stop distance is ApproachStopBuffer, NOT attackRange (decoupled).
        var opts = new NavigationOptions(StoppingDistance: ApproachStopBuffer, UseMount: false, UseFlight: false);
        await _navigator.NavigateTo(position, opts, ct);
    }
    // else: same target, hasn't drifted past threshold -> keep driving (no vnavmesh spam).
    approach = ApproachState.Approaching;
}
```

with the threshold pinned as:

```csharp
// Re-path as soon as the live target has drifted from the stale destination by more than the
// SLACK between where we stop (ApproachStopBuffer) and the attack ring (attackRange). This is the
// largest threshold for which a settled (arrived) player is still guaranteed in range:
//   after arrival   player-to-stale <= ApproachStopBuffer
//                    player-to-live  <= ApproachStopBuffer + drift           (triangle inequality)
//   not re-pathing => drift <= attackRange - ApproachStopBuffer
//   therefore       player-to-live  <= attackRange  => IN RANGE.
// With attackRange == 1 m and ApproachStopBuffer == 0.5 m this is 0.5 m.
private static float RepathThreshold(float attackRange) => attackRange - ApproachStopBuffer;
```

### Why the old design stranded, and why this one cannot (corrected no-strand proof)

**Old design (rejected).** `StoppingDistance == attackRange` and `RepathThreshold == attackRange`. vnavmesh arrives and **stops** `attackRange` short of the stale point, so the player *settles* (stops moving) at the edge of the attack ring relative to the **stale** point. Any residual drift `0 < drift <= attackRange` then leaves the live target up to `attackRange + drift <= 2*attackRange` away — *outside* the `attackRange` ring — while `drift <= attackRange` takes the no-re-path branch. Concretely with `attackRange == 1`: target steps `0.8 m` then idles → player parked `1 m` short of the stale point, live target `<= 1.8 m` away (out of the `1 m` ring), `drift = 0.8 <= 1` → no re-path → permanent stall. The old proof's "still moving" premise was false: an *arrived* player at a *settled* target is stationary.

**New design (Option A).** vnavmesh stops `ApproachStopBuffer` (`< attackRange`) short of the **live** destination. When navigation completes the player is at worst `ApproachStopBuffer` from `_approachPosition`. The live target is at `position`, drifted by `drift = dist(_approachPosition, position)`. By the triangle inequality:

```
dist(player, liveTarget) <= dist(player, _approachPosition) + dist(_approachPosition, liveTarget)
                         <= ApproachStopBuffer               + drift
```

Re-path fires the moment `drift > RepathThreshold = attackRange - ApproachStopBuffer`. So **whenever we do NOT re-path** (`drift <= attackRange - ApproachStopBuffer`):

```
dist(player, liveTarget) <= ApproachStopBuffer + (attackRange - ApproachStopBuffer) = attackRange.
```

A player that has *arrived* while the target is *settled* is therefore **always `<= attackRange` — in range — and attacks.** No appeal to "still moving" is required. The three cases partition the live player→target distance with **zero gap**:

- **In range:** `dist(player, liveTarget) <= attackRange` → in-range branch → `Engage`, no nav. (Covers every settled-arrival case, by the bound above.)
- **Out of range, not re-pathing:** only reachable while the player is *still en route* (has not yet arrived within `ApproachStopBuffer` of `_approachPosition`). The next tick re-evaluates: it either arrives in range, or — if the target moved enough — re-paths.
- **Out of range, drifted past threshold:** `drift > attackRange - ApproachStopBuffer` → a fresh `NavigateTo(liveTarget)` is issued this tick.

There is **no stationary out-of-range state**: a settled player is provably in range. The user's counterexample now resolves cleanly: target steps `0.8 m` then idles → `drift = 0.8 > RepathThreshold = 0.5` → **re-path fires** to the live position → player arrives `0.5 m < 1 m` from the now-current target → in range. No strand. The exact strand zone of the old design — live player→target in `(attackRange, 2*attackRange]` while accumulated drift `<= attackRange` — is eliminated because not-re-pathing now bounds the live distance at `attackRange`, not `2*attackRange`.

**Why `RepathThreshold = attackRange - ApproachStopBuffer` (loosest safe value).** Any *larger* threshold re-opens a gap: if `drift` could reach `attackRange - ApproachStopBuffer + e` without re-pathing, a settled player would sit at `ApproachStopBuffer + (attackRange - ApproachStopBuffer + e) = attackRange + e > attackRange` — out of range, no re-path → strand. So this is the maximum threshold preserving no-strand, and choosing it minimises vnavmesh churn. (For ranged jobs `attackRange = 20`, threshold `= 19.5`; `ApproachStopBuffer` is only meaningful for melee, but the formula is uniform and safe: a ranged target drifting `< 19.5 m` from the stale point cannot strand because the player was already inside the 20 m ring.)

**Jitter suppression preserved:** sub-threshold wobble (`drift <= attackRange - ApproachStopBuffer`, i.e. `<= 0.5 m` at melee) takes the `else` and issues no `NavigateTo` — the original anti-spam intent. The player re-paths only on target movement exceeding `0.5 m` past the stale destination. The epsilon-style "don't spam on jitter" guarantee is intact, now with a provable in-range floor.

### `_approachPosition` lifecycle (set / clear points)

| Location in `Decide` | `_approachTarget` | `_approachPosition` |
|----------------------|-------------------|---------------------|
| Out-of-range, re-path taken (identity change / first nav / drift) | `= target` | `= position` (the nav destination) **(new set)** |
| In-range branch (`distance <= attackRange`, `CombatController.cs:113-117`) | `= null` + `Stop` | **must also `= null`** (new) — clean re-acquisition if the same mob later re-leaves range |
| Target became null, was approaching (`CombatController.cs:98-103`) | `= null` + `Stop` | **must also `= null`** (new) |
| `GetHostileActors` failed, was approaching (`CombatController.cs:59-63`) | `= null` + `Stop` | **must also `= null`** (new) |
| `Reset()` (latch-only, IO-free) | `= null` | **must also `= null`** (new — keeps `Reset()` IO-free; just one more field assignment) |
| `ResetAsync` | (via `Reset()`) | cleared transitively by `Reset()`; the `Stop` guard still keys off `_approachTarget`, so no extra IO |

`ResetAsync`'s `Stop` guard stays on `_approachTarget is not null` (not `_approachPosition`) — they are set/cleared together, so the guard is unchanged and `ResetAsync` issues at most one `Stop`. `Reset()` remains IO-free (CS-7 still holds). The InRange branch nulling `_approachPosition` is what makes re-acquisition clean: a mob that enters then re-leaves range starts a fresh approach with `_approachPosition is null`, taking the first-nav path.

### Invariants preserved

- **D6 (combat-only scan):** no change — all of this is inside the existing out-of-range arm of `Decide`, only reached on `CombatStep` ticks.
- **Non-starvation:** a moving target still yields `Engage(target)` / `Approaching` every tick (the re-path is a side effect; the decision is still forward). Never a stall.
- **Rotation lease:** `RotationShouldRun` (= `target is not null`) is untouched; re-pathing does not change rotation state.
- **G12 anti-spam:** a *stationary* same-target second tick has `drift == 0 <= attackRange` -> `else` -> no second `NavigateTo`. G12 stays green (its actors are stationary).

## A4. New + changed GWT scenarios

### Controller-level (extend `CombatControllerApproachTests`)

**G25 — target moves beyond threshold during approach -> re-path issued**
- Given a melee job (PGL, range 1, `ApproachStopBuffer == 0.5`, `RepathThreshold == 0.5`); tick 1 a hostile Id 9 at `(10,0,0)`, `distance: 8f` (out of range) -> one `NavigateTo((10,0,0))` with `StoppingDistance == 0.5f`, `_approachPosition == (10,0,0)`.
- When tick 2: same Id 9, still out of range, but moved to `(20,0,0)` (`drift == 10 > 0.5`), `distance: 12f`.
- Then a **second** `NavigateTo` is recorded, destination `(20,0,0)`, `StoppingDistance == 0.5f`; total 2 nav requests; `Approach == Approaching`; no `Stop`.

**G26 — target jitters below threshold during approach -> no re-path (anti-spam)**
- Given PGL (`RepathThreshold == 0.5`); tick 1 hostile Id 9 at `(10,0,0)`, `distance: 8f` -> one `NavigateTo`.
- When tick 2: same Id 9 at `(10.4,0,0)` (`drift == 0.4 <= 0.5`), still `distance: 8f` (out of range).
- Then **exactly one** `NavigateTo` total (no re-path); `Approach == Approaching`. Pins jitter suppression at the new `0.5 m` re-path threshold. (Note: the jitter ceiling is now `RepathThreshold == 0.5`, not `attackRange == 1` — a `0.5 < drift <= 1` step now *does* re-path; see G31.)

**G31 — small target step then idle -> player still closes to attack range (the case that stranded today)**
- Given PGL (range 1, `ApproachStopBuffer == 0.5`, `RepathThreshold == 0.5`); tick 1 a hostile Id 9 at `(10,0,0)`, `distance: 8f` (out of range) -> one `NavigateTo((10,0,0))`, `StoppingDistance == 0.5f`, `_approachPosition == (10,0,0)`.
- When tick 2: same Id 9 took a small step to `(10.8,0,0)` (`drift == 0.8`), then **idles** there; still out of range (`distance: 8.8f`). Because `drift == 0.8 > RepathThreshold == 0.5`, a **second** `NavigateTo((10.8,0,0))` is recorded (`_approachPosition == (10.8,0,0)`), `StoppingDistance == 0.5f`.
- When tick 3: same Id 9 still idle at `(10.8,0,0)`; simulate vnavmesh having arrived `0.5 m` short -> set `distance: 0.5f` (in range, `<= attackRange == 1`).
- Then tick 3's `Approach == InRange`, a `Stop` is recorded, `_approachTarget`/`_approachPosition` cleared, and **no third `NavigateTo`** to a stale point. Total 2 navs, 1 Stop. **This is the exact strand case under the OLD design** (drift `0.8 <= old-threshold 1` would NOT have re-pathed, parking the player `~1.8 m` from the idle target, out of range forever). The new `0.5 m` threshold re-paths and the player closes to range. Pins the corrected no-strand property at the controller level.

**G27 — identity change still re-paths (regression guard for the existing G13 path under the new branch)**
- Same shape as existing G13 but assert the second `NavigateTo` fires on identity change even though the new branch added the `drift` condition (identity-change must still short-circuit to re-path). Two navs to the two distinct positions.

**G28 — `_approachPosition` cleared on in-range transition -> fresh approach re-paths on re-leave**
- Given PGL; tick 1 out of range at `(10,0,0)` -> nav#1, `_approachPosition` set.
- Tick 2: same Id 9 now `distance: 0.5f` (in range) -> `Stop`; `_approachPosition` cleared.
- Tick 3: same Id 9 moves back out to `(10,0,0)`, `distance: 8f` -> because `_approachPosition is null`, a fresh `NavigateTo` fires (nav#2). Asserts 2 navs total and 1 Stop. Pins the InRange clear of `_approachPosition`.

**G29 — `Reset()` clears `_approachPosition` (IO-free)**
- Given an approach latched at `(10,0,0)` (nav#1, `_approachPosition` set).
- When synchronous `Reset()`.
- Then no `Stop` / no IO (existing G22 contract), AND a subsequent tick with the same target at the **same** position issues a fresh `NavigateTo` (proving `_approachPosition` was cleared — otherwise `drift == 0` would suppress it). 2 navs total. (Extends G22.)

**G30 — approach stopping distance is `ApproachStopBuffer` (0.5 m), decoupled from attack range**
- Given PGL (attack range now 1 m); out-of-range hostile.
- Then the recorded `NavigationOptions.StoppingDistance == 0.5f` (`ApproachStopBuffer`), **not** `attackRange`. Supersedes the old G8 `3.0f` and the interim `1.0f` draft value. Pins that the *navigate* stop distance is independent of the *attack* range — the decoupling that makes the no-strand proof hold. (A separate JobRangeTable test still pins `AttackRange == 1.0f`; the two values are deliberately different.)

### Engine-level (extend `CombatApproachHandoffTests`)

**EH-9 — moving target during approach does not strand the player (no-strand property)**
- Given the one-combat-step quest (Location `(100,0,100)`); player at `(100,0,100)`; one eligible hostile out of range and **moving** across ticks (e.g. `(140,0,140)` then `(160,0,160)`), always within the 30 m scan radius, `distance` always `> 1`.
- When several ticks run.
- Then every tick's action is `Engage` (target non-null), and the recorded `NavigateTo` destinations **track the moving mob** (a new nav each time the mob drifts > `RepathThreshold == 0.5 m`), never `(100,0,100)`, each with `StoppingDistance == 0.5f`. Pins that a moving target re-paths at the engine level and never falls back to Location while a live target exists (composes the A3 fix with Bug 1).

**EH-10 — small-step-then-idle target is closed to attack range, never stranded (engine-level no-strand)**
- Given the one-combat-step quest; player at `(100,0,100)`; one eligible hostile that on tick 1 is out of range, on tick 2 takes a small step (drift `~0.8 m > 0.5`) then **idles**, and whose `distance` on tick 3 is set to `0.5f` (vnavmesh has arrived `ApproachStopBuffer` short, now in attack range).
- When ticks 1-3 run.
- Then tick 1 and tick 2 emit `Engage` with a re-pathing `NavigateTo` tracking the live mob (2 distinct nav destinations), tick 3 emits `Engage` and the in-range path fires (`ClearTarget`/`Stop` semantics per the controller), and **no tick parks the player at a stale point out of range**. Engine-level pin of the corrected no-strand property — the scenario that stalled in-game today.

### Changed existing scenarios

- **G8** (`StoppingDistance == 3.0f`) -> `1.0f` (or fold into G30).
- **G10 / G11** in-range distances `3f` / `3.0f` -> `1f` / `1.0f` (range shrank).
- **G20** fallback `StoppingDistance == 3.0f` -> `1.0f`.
- **JobRangeTableTests**: four assertion/rename changes per the A2 table.
- **CombatControllerResetAsyncTests**: comment-only ("2 m in range" -> note 2 m is now out of range; assertions unchanged).

## A5. Open questions resolved

**Q1: Fixed re-path threshold, or derive from `attackRange`?** Derive. A fixed threshold cannot adapt to ranged jobs and risks stranding at melee range.

**Q2 (corrected): Should the approach `StoppingDistance` equal `attackRange`?** **No — decouple them.** Setting both `StoppingDistance` and `RepathThreshold` to `attackRange` (the interim draft) leaves *zero slack*: vnavmesh settles the player exactly at the attack-ring edge relative to the **stale** point, so any residual drift `<= attackRange` pushes the live target up to `2*attackRange` away with no re-path — a settled, idle target strands the player permanently out of range (the `(attackRange, 2*attackRange]` strand zone). Resolved by **Option A**: stop `ApproachStopBuffer = 0.5 m` short of the *live* target (`< attackRange`), and set `RepathThreshold = attackRange - ApproachStopBuffer`. Then a settled, arrived player is provably `<= attackRange` from the live target (in range) whenever it is not re-pathing — no stationary out-of-range state (full proof in A3). The *navigate* stop distance (`ApproachStopBuffer`) is now distinct from the *attack* threshold (`attackRange`); the in-range check still uses the live target distance and `<= attackRange`, unchanged.

**Rejected — Option B (live-distance re-path trigger):** re-path while out of range whenever `dist(_approachPosition, livePosition) > epsilon` for a small epsilon. Equivalent in safety to Option A when `epsilon = attackRange - ApproachStopBuffer`, but it conflates "did the target move" with "am I about to strand" and makes the jitter ceiling an opaque tuning knob rather than a derived consequence of the stop buffer. Option A is preferred because the no-strand bound falls directly out of the two named constants with no separate epsilon to justify.

## A6. Done criteria (addendum)

1. `JobRangeTable.MeleeRange == 1.0f`, `FallbackRange == 1.0f`, `RangedRange == 20.0f`; Tank and Melee both resolve `AttackRange == 1.0f`; Unknown resolves `1.0f` (A2; G30, updated JobRangeTableTests).
2. The approach `NavigateTo` uses `StoppingDistance == ApproachStopBuffer (0.5f)`, decoupled from `attackRange`; the in-range check still uses the live target distance and `<= attackRange (1.0f)`, unchanged (A3; G30, JobRangeTableTests).
3. A target that moves more than `RepathThreshold == attackRange - ApproachStopBuffer (0.5 m)` beyond the last navigation destination triggers a fresh `NavigateTo` to the live position (G25, G31, EH-9, EH-10).
4. A target that jitters at most `RepathThreshold (0.5 m)` does **not** trigger a re-path — vnavmesh is not spammed (G26, G12 stays green).
5. Identity change still re-paths (G27); a **settled** (arrived, idle) player is ALWAYS either within `attackRange` (in-range → attacks) or has triggered a re-path — there is no stationary out-of-range state, including the small-step-then-idle case that stranded in-game (G31, EH-10 no-strand property; provable zero-gap bound in A3).
6. `_approachPosition` is set with every issued approach `NavigateTo` and cleared on in-range transition, target-lost, hostile-query-fail, and `Reset()`/`ResetAsync` (A3 lifecycle table; G28, G29). `Reset()` stays IO-free (CS-7 unchanged).

## A7. Exclusions (addendum)

- No change to `KillPriority` / target selection.
- No per-job re-path tuning knob; `RepathThreshold` is derived as `attackRange - ApproachStopBuffer` for all jobs, and `ApproachStopBuffer` is a single melee-tuned constant (0.5 m), not per-job.
- No change to vnavmesh cadence beyond the drift-gated re-issue (still one `NavigateTo` per real movement event, not per tick).
- `QuestEngine.DefaultStopDistance` (the Location roam proximity) is unchanged at 3.0 m — unrelated to attack range.

---

✅ READY FOR TEST CREATION (addendum)

Tester: Apply Change 1 + Change 2 from §A2-A3 (note: Change 2 now decouples the navigate `StoppingDistance` (`ApproachStopBuffer == 0.5`) from `attackRange`, and the re-path threshold is `attackRange - ApproachStopBuffer == 0.5`) and write/adjust tests per §A4.
- New scenarios: 9 (G25, G26, G27, G28, G29, G30, G31, EH-9, EH-10) — 1 happy path (G25), 5 edge (G26, G27, G28, EH-9, G31), 1 strand-regression pin at engine level (EH-10), 2 latch/value pins (G29, G30).
- Changed existing assertions: G8 + G20 `StoppingDistance` `3.0f` -> `0.5f` (`ApproachStopBuffer`, NOT `1.0f`); G10/G11 in-range distances `3f`/`3.0f` -> `1f`/`1.0f` (attack range 3->1); 4 JobRangeTableTests (rename + value); CombatControllerResetAsyncTests comments.
- Expected delta: ~9 new tests + ~8 edited assertions in QuestForge.Engine.Tests (new tests extend CombatControllerApproachTests and CombatApproachHandoffTests).
