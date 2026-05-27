# Combat Clear-Aggro (Mop-up) Implementation Plan

**Status:** ready for test creation (awaiting user gate on §Open question)
**Input docs:** docs/SCHEMA.md (CombatStep), docs/ADAPTERS.md (IGameStateProvider, ICombat, INavigator), prior combat plans (COMBAT_STEP_PART_A_PLAN, COMBAT_APPROACH_HANDOFF_PLAN)
**Output (behavior change):** a CombatStep whose `Expect` is satisfied while the player is **still in combat** no longer confirms-and-advances immediately. The engine first **mops up the player's attackers** (engages aggro'd/enmity actors only, never the kill-set) until `IsPlayerInCombat` reads false, then confirms + `ResetAsync` + advances. A wall-clock timeout bounds the mop-up; on expiry the engine emits `AwaitUser("could not leave combat")`. Normal (non-combat) completion is unchanged.
**Branch:** `feat/combat-clear-aggro` (off `main`)

---

## Dependency graph

Single repo (`questforge`), no cross-repo coupling. Build order:

```
QuestForge.Adapters            ← IGameStateProvider.IsPlayerInCombat (ALREADY EXISTS — no change)
QuestForge.Adapters.Fakes      ← FakeGameStateProvider.SetInCombat (ALREADY EXISTS — no change)
   └── QuestForge.Engine
         ├── Combat/CombatController.cs   ← add DecideClearAggro(...)   [new method]
         └── QuestEngine.cs               ← redefine combat completion + mop-up loop + timeout
   └── QuestForge.Engine.Tests
         └── Combat/CombatClearAggroTests.cs   [new GWT suite]
```

No new adapter interface members, no new fake setters, no schema change. Everything needed already exists on `IGameStateProvider` (`IsPlayerInCombat`), `FakeGameStateProvider` (`SetInCombat`, `AddHostileActor`), and the engine (`TimeProvider _clock`). This is what keeps the slice small.

---

## Architectural decisions (read before coding)

### D1. Combat completion is redefined as `Expect ∧ ¬InCombat` (CombatStep only)

The Expect-satisfied confirmation branch (QuestEngine.cs ~lines 419-427) currently does, for any step:

```csharp
if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct))
{
    _confirmedStepIds.Add(step.Id);
    if (step is CombatStep) await _combatController.ResetAsync(ct);
    continue;
}
```

The redefinition splits the `CombatStep` case out of this branch. For a `CombatStep` with satisfied `Expect`, completion additionally requires the player to be **out of combat**:

```csharp
if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct))
{
    if (step is CombatStep cs)
    {
        // Objective met. Only "done" once aggro is cleared.
        var inCombatResult = await _gameState.IsPlayerInCombat(ct);
        var inCombat = inCombatResult is Result<bool>.Success { Value: true }; // fail-open: read failure ⇒ treat as NOT in combat ⇒ confirm

        if (!inCombat)
        {
            _confirmedStepIds.Add(cs.Id);
            await _combatController.ResetAsync(ct);
            _combatController.ClearMopUpTimer();      // no-op if never armed
            continue;
        }

        // Objective met but still in combat → mop-up this tick instead of confirming.
        return await ResolveMopUp(cs, ct);            // see D2/D3
    }

    // Non-combat steps: UNCHANGED.
    _confirmedStepIds.Add(step.Id);
    continue;
}
```

**Why fail-open on the `IsPlayerInCombat` read** (read failure ⇒ treat as NOT in combat ⇒ confirm): this preserves the existing completion semantics whenever combat-state is unknowable. A read failure must never *trap* a satisfied objective in a mop-up loop — the worst case of trusting the objective predicate is the pre-existing behavior we are improving on, not a regression. This mirrors the sibling fail-open reads in `ResolveAction` (NG+, UiState, position, zone).

**Where `IsPlayerInCombat` is read:** ONLY inside this CombatStep arm of the Expect-satisfied branch, i.e. only when (a) the active step is a `CombatStep` AND (b) its `Expect` already evaluated true this tick. It is **not** hoisted into the per-tick prelude. This preserves invariant **D6** (combat-only scans): non-combat steps and not-yet-complete combat steps never read it. A `TalkStep`/`TravelStep` tick records zero `IsPlayerInCombat` calls.

**Rejected alternative — read `IsPlayerInCombat` in the prelude every tick.** Rejected: violates D6 (would add a per-tick read on the common path, starving every non-combat fixture), and is wasteful — combat state only matters at the completion boundary of a combat step.

**Rejected alternative — gate on `HostileActor.IsTargetingPlayer` presence instead of `IsPlayerInCombat`.** Rejected: "in combat" is the authoritative game flag the *next* step (mount/interact) is actually blocked on. Enmity flags can lag or list mobs the player isn't flagged against; the engine should leave combat by the same signal the game uses to block the next action.

### D2. Mop-up target selection is attackers-only — a new `CombatController.DecideClearAggro`

Mop-up must engage ONLY the player's attackers, never the step's kill-set. Reusing `Decide` (which scores `DataId ∈ killIds` at +1000) would make the player chase non-attacking kill-set mobs and respawns — the exact "wander off and farm" behavior COMBAT_APPROACH_HANDOFF removed. A new controller method selects attackers only and reuses the existing approach/re-path/range machinery:

```csharp
// CombatController.cs
/// <summary>
/// Mop-up selection: engage ONLY actors attacking the player (IsTargetingPlayer OR
/// OnPlayerEnmityList), independent of the step's kill-set. Reuses the same SetTarget /
/// approach / re-path / attack-range logic as Decide so a fleeing attacker is still chased
/// into attack range. Returns Target=null when no attacker is in scan range (e.g. the
/// attacker is unreachable/untargetable) — the engine uses that to drive the timeout.
/// </summary>
public async Task<CombatDecision> DecideClearAggro(CancellationToken ct)
```

**Implementation:** `DecideClearAggro` is structurally identical to `Decide` except for target selection. It calls `GetHostileActors(ScanRadius)`, then selects via a dedicated attacker filter, then runs the **same** SetTarget-on-change / range-check / NavigateTo-on-approach / Stop-on-in-range block. To avoid duplicating that ~80-line block, factor the post-selection tail into a private `ApplyDecision(KillTarget? target, IReadOnlyList<HostileActor> actors, CancellationToken ct)` that both `Decide` and `DecideClearAggro` call after selecting their respective target. This is a pure refactor of existing code, behavior-preserving for `Decide`.

**Attacker selection — explicit filter, NOT an empty kill-set.** Under `CombatSpawn.AutoOnEnterArea`, `KillPriority.SelectTarget(actors, emptyKillIds, AutoOnEnterArea)` makes ALL living/targetable hostiles eligible (kill-set is a scoring bonus, not a filter there — see KillPriority.cs lines 64-68). So an empty kill-set would still select a non-attacking bystander under that spawn type. Mop-up therefore uses an explicit attacker predicate, independent of spawn type:

```csharp
// New overload/helper on KillPriority — pure, deterministic, unit-testable.
public static KillTarget? SelectAttacker(IReadOnlyList<HostileActor> actors)
{
    // Eligible iff alive && targetable && (IsTargetingPlayer || OnPlayerEnmityList).
    // Score: IsTargetingPlayer +10, OnPlayerEnmityList +5 (NO +1000 kill-set term, NO +100 quest-marker term).
    // Tie-break: higher score → nearest (DistanceToPlayer asc) → lowest ActorId.Value (same as SelectTarget).
}
```

Because the eligibility test is `IsTargetingPlayer || OnPlayerEnmityList` and the score never includes the kill-set term, **both spawn types behave identically under mop-up**: a kill-set respawn that is NOT attacking the player scores 0 and is ineligible, so it is never selected. This is the load-bearing difference from the objective phase.

**Rejected alternative — call `Decide` with `step.Spawn` forced to `OverworldEnemies` and an empty kill-set.** Rejected: under `OverworldEnemies`, empty kill-set + the existing eligibility (`in killIds OR IsTargetingPlayer OR OnPlayerEnmityList`) *would* coincidentally yield attackers-only — but it is fragile (couples mop-up correctness to KillPriority's spawn branching) and still applies the +1000/+100 scoring terms to zero effect only by accident. An explicit `SelectAttacker` states the intent and is robust to both spawn types and future KillPriority changes.

**What breaks if violated:** if mop-up reused the +1000 kill-set scoring, the player would target the nearest/highest-scoring kill-set mob (likely a fresh respawn) the instant the objective predicate flipped, re-entering combat indefinitely and never advancing — a regression of the prior fix and a hard stall.

### D3. Termination bound — per-combat-step wall-clock timeout via the injected `TimeProvider`

Mop-up must not loop forever (e.g. an attacker that is unreachable or untargetable keeps `IsPlayerInCombat` true). The controller tracks the mop-up start instant and the engine enforces a timeout:

```csharp
// CombatController.cs
private DateTimeOffset? _mopUpStartedAt;

/// <summary>Records the mop-up start instant on first call for this step; idempotent thereafter.
/// Returns elapsed since the start instant.</summary>
public TimeSpan StartOrElapsedMopUp(DateTimeOffset now)
{
    _mopUpStartedAt ??= now;
    return now - _mopUpStartedAt.Value;
}

/// <summary>Clears the mop-up timer. Called on confirm, on Reset(), and on sequence change.</summary>
public void ClearMopUpTimer() => _mopUpStartedAt = null;
```

`Reset()` (and therefore `ResetAsync`) also clears `_mopUpStartedAt` so a fresh combat step starts with no armed timer.

Engine mop-up resolution:

```csharp
private async Task<(EngineAction, string?)> ResolveMopUp(CombatStep cs, CancellationToken ct)
{
    var elapsed = _combatController.StartOrElapsedMopUp(_clock.GetUtcNow());
    if (elapsed >= MopUpTimeout)
    {
        await _combatController.ResetAsync(ct);   // clears target/nav and the timer
        return (new EngineAction.AwaitUser("could not leave combat after objective complete"), cs.Id);
    }

    var decision = await _combatController.DecideClearAggro(ct);
    return (new EngineAction.Engage(cs, decision.Target), cs.Id);   // Engage(null) when no attacker reachable — forward decision, never a stall (D6)
}
```

**Default timeout:** `MopUpTimeout = TimeSpan.FromSeconds(15)`. Justification: open-world adds are low-HP and the rotation (delegated to WrathCombo/BossMod) clears 1-3 attackers in well under 10 s; 15 s gives generous headroom for a fleeing/repathing attacker while still bailing out fast enough that a genuinely stuck state (untargetable add, mesh gap) surfaces to the user promptly rather than hanging the run. Defined as a `private const`/`static readonly TimeSpan` on `QuestEngine` (sibling to `DefaultStopDistance`), not author-configurable in this slice.

**Non-starvation invariant (NonStarvation\*):** every mop-up tick returns a forward decision — `Engage(attacker)`, `Engage(null)`, or (on timeout) `AwaitUser`. There is no tick that returns the "all steps satisfied" `Wait` sentinel or stalls. `Engage(null)` is already an established forward decision (EH-3/EH-4). The timeout guarantees the loop is bounded in wall-clock time even if `IsPlayerInCombat` never clears.

**Why wall-clock, not a tick counter:** tick cadence varies in-game and across replay; a wall-clock bound via the seeded `TimeProvider` is deterministic under replay (same `runId` → same clock → same decisions) and meaningful to a human ("15 s") regardless of tick rate. The clock is already injected and faked (`ManualTimeProvider`) for WaitStep.

### D4. Reset clears mop-up state on every exit path

Once out of combat the engine confirms + `ResetAsync` (clears target/stops nav as today) **and** the mop-up timer is cleared (D1 calls `ClearMopUpTimer()` on the confirm path; `ResetAsync`/`Reset` clears it too). The sequence-change cleanup branch (QuestEngine.cs lines 385-393) already calls `await _combatController.ResetAsync(ct)`, so mop-up state is cleared on sequence change for free once `Reset()` clears `_mopUpStartedAt`. No new call site needed there.

**What breaks if violated:** a stale `_mopUpStartedAt` carried into the next combat step would make that step's mop-up appear to have started in the past, tripping the timeout prematurely (spurious `AwaitUser`). Clearing on every exit path closes this.

### D5. Engine reads `IsPlayerInCombat` via `IGameStateProvider` (fake-testable, replay-affecting)

The read goes through the already-wired `_gameState` (the `RecordingGameStateProvider` proxy in the harness), so:
- `FakeGameStateProvider.SetInCombat(bool)` scripts it in engine unit tests (setter already exists).
- The recording proxy captures an `IsPlayerInCombat` observation, and `ReplayGameStateProvider` serves it during replay.

This is a **new per-tick read on the combat completion path** and therefore has the same fixture-starvation impact the recent `GetNewGamePlusState` read had: any combat replay fixture that exercises the Expect-satisfied tick of a CombatStep must now contain an `IsPlayerInCombat` observation for that segment, or replay starves. See §Replay-fixture impact.

---

## Task breakdown

### Task 1 — `KillPriority.SelectAttacker` (pure)

Add the attacker-only selector and score. Pure/deterministic; no async; no Dalamud. Mirrors `SelectTarget`'s tie-break ordering exactly (score desc → distance asc → ActorId asc) but with attacker-only eligibility and the reduced score (no +1000, no +100).

### Task 2 — `CombatController.DecideClearAggro` + mop-up timer + `ApplyDecision` refactor

1. Extract the post-selection tail of `Decide` (SetTarget-on-change, ResolveAttackRange, range check, NavigateTo/Stop, `_wasInCombat`, build `CombatDecision`) into `private async Task<CombatDecision> ApplyDecision(KillTarget? target, IReadOnlyList<HostileActor> actors, CancellationToken ct)`. `Decide` selects via `KillPriority.SelectTarget` then calls `ApplyDecision`; behavior unchanged (existing CombatController/approach/handoff/reset suites must stay green).
2. `DecideClearAggro`: `GetHostileActors(ScanRadius)` → on failure mirror `Decide`'s failure path (stop in-flight approach, return null-target decision) → select via `KillPriority.SelectAttacker(actors)` → `ApplyDecision(...)`.
3. Add `_mopUpStartedAt`, `StartOrElapsedMopUp(now)`, `ClearMopUpTimer()`; clear `_mopUpStartedAt` in `Reset()`.

### Task 3 — `QuestEngine` completion redefinition + mop-up loop + timeout

1. Split the CombatStep case out of the Expect-satisfied branch (D1).
2. Add `MopUpTimeout` const and `ResolveMopUp` (D3).
3. Confirm path calls `ResetAsync` + `ClearMopUpTimer` (D1/D4).

### Task 4 — Tests (`Combat/CombatClearAggroTests.cs`) — see §GWT

Engine-level against `EngineTestHarness` + a directly-constructed engine with `ManualTimeProvider` for the timeout case (the harness is sealed and exposes no clock, mirroring WaitStepTests).

---

## GWT specifications (engine-level, against fakes)

Helpers mirror `CombatEngineTests`: `BuildCombatQuest`, `MakeHostile(id, dataId, dist, isTargetingPlayer, onEnmity)`. Use `harness.GameState.SetInCombat(bool)` to script combat state and `harness.GameState.AddHostileActor(...)` for attackers. For the timeout test, construct the engine directly with a `ManualTimeProvider` (per WaitStepTests `BuildEngine`).

### CA-1 (happy path / boundary) — objective met + already out of combat → advance immediately
**Given** a one-combat-step quest, `Expect` satisfied on tick 1 (e.g. `questVariable(q,0) >= 1` already true), `SetInCombat(false)`.
**When** Tick.
**Then** action is NOT `Engage` (step confirms; engine emits `Wait`/`Done`), AND `harness.Combat.RecordedTargets` contains a `ClearTarget` (from `ResetAsync`), AND `RecordedReads` contains exactly one `IsPlayerInCombat` for the tick, AND no second `Engage` on a follow-up tick.

### CA-2 (core behavior) — objective met + in combat → mop up attacker, then advance
**Given** one combat step, `Expect` satisfied, `SetInCombat(true)`, one attacker `MakeHostile(9, dataId: 999, dist: 5, isTargetingPlayer: true)` where `dataId ∉ KillEnemyDataIds`.
**When** tick 1.
**Then** action is `Engage` with `Target` != null and `Target.Id == ActorId(9)` (the attacker), AND `IsPlayerInCombat` was read this tick.
**When** `SetInCombat(false)` then tick 2.
**Then** action is NOT `Engage` (now confirms), AND a `ClearTarget` is recorded.

### CA-3 (CRITICAL — attackers-only) — does NOT chase a non-attacking kill-set respawn
**Given** one combat step with `KillEnemyDataIds = [100]`, `Spawn = AutoOnEnterArea`, `Expect` satisfied, `SetInCombat(true)`. Two hostiles in scan range:
- A: `MakeHostile(1, dataId: 100, dist: 3, isTargetingPlayer: false, onEnmity: false)` — a fresh kill-set respawn, NOT attacking.
- B: `MakeHostile(2, dataId: 777, dist: 20, isTargetingPlayer: true)` — the actual attacker, farther away, not in kill-set.

**When** tick.
**Then** action is `Engage` with `Target.Id == ActorId(2)` (the attacker B), NOT `ActorId(1)`. Pins that mop-up ignores the +1000 kill-set scoring and the closer respawn. (Run the analogous case with `Spawn = OverworldEnemies` as a second `[Theory]` row — same expected result, proving both spawn types behave identically.)

### CA-4 (edge — no reachable attacker) — `Engage(null)`, no spurious advance, no timeout yet
**Given** one combat step, `Expect` satisfied, `SetInCombat(true)`, but NO attacker in scan range (e.g. only a non-attacking kill-set mob present, or no hostiles at all), clock at T0, `MopUpTimeout` not exceeded.
**When** tick.
**Then** action is `Engage` with `Target == null` (forward decision, never `Wait`-sentinel/stall), AND the step is NOT confirmed.

### CA-5 (termination) — timeout → `AwaitUser`
**Given** a directly-constructed engine with `ManualTimeProvider` at T0, one combat step, `Expect` satisfied, `SetInCombat(true)` held true across ticks, an attacker present but never killable (kept `isTargetingPlayer: true`).
**When** tick 1 (arms mop-up timer) → `Engage`. Advance clock by `>= MopUpTimeout` (e.g. 16 s) → tick 2.
**Then** tick 2 action is `EngineAction.AwaitUser` with reason containing "could not leave combat", AND a `ClearTarget` is recorded (timeout path calls `ResetAsync`).
**And boundary:** advancing by exactly `MopUpTimeout - 1ms` before tick 2 keeps it `Engage` (not yet expired); `>= MopUpTimeout` flips to `AwaitUser`.

### CA-6 (reset on advance) — confirm clears mop-up timer; next combat step is fresh
**Given** a two-sequence quest: seq 0 combat (mopped up and confirmed while clock advanced 5 s), seq 1 a second combat step (or talk). After confirm, re-enter a combat step with `SetInCombat(true)` and an attacker.
**When** the new combat step's objective completes and mop-up arms.
**Then** the timer started at the *new* `_clock.GetUtcNow()` (not carried over) — i.e. it does NOT immediately time out from the prior step's 5 s. Observable: with clock advanced only 1 s into the new mop-up, action is `Engage`, not `AwaitUser`.

### CA-7 (reset on sequence change) — sequence change clears mop-up state
**Given** seq 0 combat with mop-up armed (objective met, in combat, `Engage`), then the game advances `SetQuestSequence` to seq 1 (a talk step).
**When** tick on seq 1.
**Then** action is NOT `Engage`/`AwaitUser` from combat (engine is on the talk step), AND a `ClearTarget` is recorded (existing sequence-change `ResetAsync`), AND the mop-up timer is cleared (re-entering seq 0 combat later does not immediately time out — same observation as CA-6).

### CA-8 (D6 regression) — read is combat-completion-gated
**Given** a non-combat (Talk) quest, OR a combat step whose `Expect` is NOT yet satisfied (still engaging).
**When** tick.
**Then** `RecordedReads` contains NO `IsPlayerInCombat` (the read only happens on the Expect-satisfied tick of a CombatStep). Two cases: (a) talk step → no read; (b) combat step mid-fight (expect false) → `Engage` via normal `Decide`, no `IsPlayerInCombat` read.

### CA-9 (fail-open) — `IsPlayerInCombat` read failure ⇒ confirm
**Given** one combat step, `Expect` satisfied, and `IsPlayerInCombat` scripted to fail (add `FakeGameStateProvider.SetInCombatFailure(reason)` — see §Open question; if not added, this case is deferred).
**When** tick.
**Then** the step confirms (treated as out of combat), action is NOT `Engage`. Pins that an unknowable combat state never traps a satisfied objective.

---

## Implementation order

**Phase A — pure selection (≈0.5 day)**
1. `KillPriority.SelectAttacker` + score (Task 1).
2. Unit-test `SelectAttacker` directly in `KillPriorityTests` (attacker eligibility, kill-set respawn ineligible, tie-break order). Green before Phase B.

**Phase B — controller (≈0.5 day) — done before C**
1. `ApplyDecision` refactor; rerun CombatController/approach/handoff/reset suites — must stay green (behavior-preserving).
2. `DecideClearAggro`, `_mopUpStartedAt`, `StartOrElapsedMopUp`, `ClearMopUpTimer`, `Reset()` clears timer.

**Phase C — engine wiring (≈0.5 day)**
1. Split CombatStep out of the Expect-satisfied branch; add `IsPlayerInCombat` read (D1).
2. `MopUpTimeout` const + `ResolveMopUp` (D3); confirm path clears timer (D1/D4).
3. Run CA-1..CA-9.

**Phase D — replay fixtures (≈0.5 day) — see §Replay-fixture impact**
1. Add `IsPlayerInCombat` observation to the relevant segment of any in-repo combat fixture and the synthetic `BuildCombatTrace` helper.
2. Rerun `CombatFixtureTests` + any `questforge-data` combat replay fixtures.

---

## Replay-fixture impact

This adds a **new per-tick read on the combat completion path** (`IsPlayerInCombat`), exactly analogous to the `GetNewGamePlusState` read that recently cascaded into a fixture re-record. Specific impacts:

1. **Synthetic `BuildCombatTrace` (CombatFixtureTests.cs):** the terminal-tail segment (where `IsQuestComplete`/expect flips true and the combat step confirms) must gain an `IsPlayerInCombat` observation returning `false`, so the confirm path's read is served and the step advances to `Done`. Without it, `FX_SyntheticFixtureReplays` and `FX_ReplayTargetDeterministic` starve at the confirm tick. Note: the Engage tick (expect-false) does **not** read `IsPlayerInCombat`, so only the completion segment needs the new observation.
2. **`questforge-data` combat replay fixtures:** any canonical trace that drives a combat step to completion needs an `IsPlayerInCombat=false` observation in the completing segment. Fixtures that end while still in combat (and then mop up) need `IsPlayerInCombat=true` on the mop-up tick(s) plus a `GetHostileActors`/`GetCurrentJob` observation for the `DecideClearAggro` scan, then `false` when combat clears. This is the re-record cascade flagged in MEMORY (`project_trace_emission_refactor`); coordinate with the general strategy from #40 rather than hand-patching each fixture if more than the synthetic ones are affected.
3. **`RecordingGetHostileActorsTests` / recording-proxy tests:** unaffected unless they assert an exact read-sequence on the completion tick — verify and, if so, add the new read to the expected sequence.

The read is fail-open (D1), so a fixture *missing* the observation fails by starvation (ReplayGameStateProvider throwing), not by silent misbehavior — which is the desired loud failure.

---

## Done criteria

1. A combat step whose objective completes **while still in combat** emits `Engage` targeting an attacker (CA-2), not an immediate confirm.
2. Mop-up never targets a non-attacking kill-set mob/respawn, under both `AutoOnEnterArea` and `OverworldEnemies` (CA-3).
3. Once `IsPlayerInCombat` reads false, the step confirms, `ClearTarget` is issued, and the engine advances (CA-1, CA-2 tick 2).
4. A mop-up that cannot clear combat within `MopUpTimeout` emits `AwaitUser("could not leave combat …")` and resets (CA-5).
5. Mop-up timer state is cleared on confirm, timeout, and sequence change; a subsequent combat step starts fresh (CA-6, CA-7).
6. Non-combat ticks and mid-fight (expect-false) combat ticks never read `IsPlayerInCombat` (CA-8, D6 preserved).
7. Existing suites stay green: `NonStarvation*`, `RotationLeaseLatch*`, `CombatFixture*` (after fixture update), CombatController/approach/handoff/reset, per-quest combat completion, NG+ behavior.

---

## Exclusions

- No new adapter interface members, no schema change, no author-configurable timeout (the 15 s default is a const).
- No change to the objective-phase `Decide` target selection or scoring (kill-set still dominates while the objective is unmet).
- No death/recovery interplay changes (dungeon/SPD death routing unchanged).
- No live combat-state polling on the per-tick prelude (D6 preserved).
- No mop-up for non-CombatStep step types.
- Multi-attacker ordering beyond the deterministic tie-break is out of scope (rotation, delegated, handles AoE; the controller just keeps a valid attacker targeted until combat clears).

---

## Open question (user gate)

**Fail-open test coverage (CA-9):** `FakeGameStateProvider` has `SetHostileActorsFailure`/`SetCurrentJobFail`/`SetNewGamePlusStateFailure` but **no** `SetInCombatFailure`. To test the D1 fail-open path (CA-9) I would add a `SetInCombatFailure(reason, detail)` setter to `FakeGameStateProvider` (one-line, matching the existing failure-setter pattern) and wire `IsPlayerInCombat` to honor it. This is a fake-only change in `QuestForge.Adapters.Fakes`. **Approve adding it, or defer CA-9 and ship the fail-open behavior untested at the unit level?**

Everything else is a single slice (one PR, ~9 GWT tests + 3 KillPriority unit tests).

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §GWT (Task 4) and the `SelectAttacker` units in §Task 1.
- Happy paths: 2 scenarios (CA-1, CA-2)
- Edge cases: 4 scenarios (CA-3 [×2 spawn rows], CA-4, CA-6, CA-7)
- Error cases: 2 scenarios (CA-5 timeout + boundary, CA-9 fail-open [pending gate])
- Regression: 1 scenario (CA-8, two cases)
- Pure-unit: 3 `KillPriority.SelectAttacker` assertions
- Expected total: ~13-15 tests in QuestForge.Engine.Tests (Combat/CombatClearAggroTests.cs + KillPriorityTests additions)
