# Combat "Move-to-Target" (Approach-to-Target) Plan

**Status:** ready for test creation
**Input docs:** docs/COMBAT_STEP_PART_A_PLAN.md (CombatController origin), docs/ADAPTERS.md (INavigator, IGameStateProvider), CLAUDE.md (engine testability boundary, clean-room)
**Output:** Melee/tank jobs close the gap to the live combat target before WrathCombo's rotation can land; no behavior change for already-in-range targets. CI-testable in `QuestForge.Engine.Tests` with no Dalamud.
**Phase dependencies:** Builds on the shipped CombatController (Phase 11 combat-step work). No schema change, no new adapter interface.

---

## Problem statement

WrathCombo in Manual DPS mode only *uses actions* — it never moves the character. QuestForge owns targeting (`CombatController.Decide` → `SetTarget`) and delegates the rotation. When the selected enemy is outside attack range, a melee job stands still and the rotation no-ops forever. The engine must navigate the player toward the current target until within attack range, hold position, and let the rotation work. Because the target dies and we retarget to aggro'd adds, approach must run every tick *alongside* target selection — inside `Decide`, not as a preceding travel step.

---

## Dependency graph

```
QuestForge.Adapters
  ├── INavigator              (exists — NavigateTo / Stop / IsNavigating)
  ├── IGameStateProvider      (exists — GetCurrentJob → JobId)
  └── Types/Identifiers.cs    (exists — JobId is record struct JobId(uint Value))
        └── consumed by ↓
QuestForge.Engine
  ├── Combat/JobRangeTable.cs (NEW — pure C# JobId → attack range classifier)
  └── Combat/CombatController.cs (MODIFIED — gains INavigator dep + approach logic)
        └── tested by ↓
QuestForge.Engine.Tests/Combat
  ├── CombatControllerApproachTests.cs (NEW)
  └── JobRangeTableTests.cs            (NEW)
        ├── FakeNavigator   (exists — RecordedNavigationRequests / RecordedStops)
        ├── FakeGameStateProvider (exists — SetJob, AddHostileActor with Position)
        └── FakeCombat      (exists)
```

**Build order:** `JobRangeTable` (pure, no deps) first → `CombatController` constructor + `Decide` change → wire `INavigator` at the two construction sites (`QuestEngine.cs:85`, `QfCommand.cs:690`).

---

## Architectural decisions (read before coding)

### D1 — Approach lives inside `CombatController.Decide`, fed by the live target

Settled. The target is selected fresh every tick by `KillPriority.SelectTarget`; it moves and can change when the quest mob dies. A preceding travel step cannot track a moving/changing target. `Decide` already computes `target` each tick — approach reuses that exact decision.

### D2 — `CombatController` gains an `INavigator` constructor dependency

```csharp
public CombatController(IGameStateProvider gameState, ICombat combat, INavigator navigator)
```

`navigator` is null-guarded like the others (`?? throw new ArgumentNullException`). This is a breaking constructor signature change. Two production construction sites must be updated:
- `QuestForge.Engine/QuestEngine.cs:85` — `new CombatController(gameState, combat, navigator)` (QuestEngine already receives an `INavigator`; thread it in).
- `QuestForge.Plugin/Commands/QfCommand.cs:690` — `new CombatController(_host.DebugGameState, _host.DebugCombat, _host.DebugNavigator)` (expose a debug navigator on the host if not already present).

**What breaks if violated:** the engine cannot move; melee combat hangs. **Testability:** controller stays directly constructable as `new CombatController(fakeGameState, fakeCombat, fakeNavigator)`.

### D3 — Job role → attack range is a pure-C# table, keyed by FFXIV ClassJob row id

`JobId.Value` is the FFXIV ClassJob row id (confirmed: `DalamudGameStateProvider` constructs `new JobId((uint)local.ClassJob.RowId)`). `JobId` is an opaque `record struct JobId(uint Value)` — it carries **no** role granularity. We do **not** modify `JobId`. Instead add a pure classifier in the engine assembly:

```csharp
// QuestForge.Engine/Combat/JobRangeTable.cs  (NEW — pure C#, no Dalamud, no adapter calls)
namespace QuestForge.Engine.Combat;

public enum CombatRole { Melee, PhysicalRanged, Caster, Healer, Tank, Unknown }

public static class JobRangeTable
{
    // Attack/stop distance in metres, centre-to-centre. All values < ScanRadius (30 m).
    public const float MeleeRange   = 3.0f;   // melee DPS + tanks
    public const float RangedRange  = 20.0f;  // physical ranged, casters, healers
    public const float FallbackRange = 3.0f;  // Unknown / unmapped → treat as melee (safe: always closes)

    public static CombatRole Classify(JobId job);      // row id → role
    public static float AttackRange(JobId job);         // row id → stop distance (role → range)
}
```

**Why a fallback of melee (3 m) for Unknown:** closing to 3 m is always safe for an unrecognised job — a ranged job that over-approaches still fights; a melee job that under-approaches cannot. Erring toward "closer" never strands the player. We log/return `Unknown` so the gap is visible, but never refuse to fight.

**Clean-room:** the row-id→role mapping below is derived from publicly observable in-game job behaviour and the standard FFXIV role wheel, authored fresh for QuestForge. No external plugin's table was copied.

**Rejected alternative — add a `Role` field to `JobId`:** rejected. `JobId` is a transport identifier shared across adapters; baking role into it couples the adapter layer to combat semantics and forces every fake/recording path to populate it. The classifier belongs in the engine where the policy lives.

**Rejected alternative — read role from `IGameStateProvider`:** rejected. Adds an adapter method + Dalamud Lumina lookup for data that is a static function of the job id. Pure table is deterministic and free to unit-test.

### D4 — Re-issue cadence is governed by explicit tracked state, not "call every tick"

Re-calling `NavigateTo` toward a moving target every tick repaths constantly (chatty, and vnavmesh will thrash). The controller tracks two new fields and a derived per-tick decision:

```csharp
private KillTarget? _approachTarget;   // the target we last issued NavigateTo for (null = not navigating)
```

(`_currentTarget` already exists for the SetTarget latch; `_approachTarget` is the *navigation* latch and is distinct — SetTarget fires on identity change, NavigateTo fires on the in-range/out-of-range transition.)

Per-tick rule, evaluated **after** target selection and SetTarget/ClearTarget:

| Condition this tick | Action | New `_approachTarget` |
|---|---|---|
| No eligible target (`target is null`) | If `_approachTarget is not null` → `Stop`. Else nothing. | `null` |
| Target exists, `DistanceToPlayer <= attackRange` (in range) | If `_approachTarget is not null` → `Stop` (transition into range). Else nothing. | `null` |
| Target exists, out of range, AND target differs from `_approachTarget` (new/changed target) | `NavigateTo(target.Position, opts)` | `target` |
| Target exists, out of range, AND target == `_approachTarget` (already heading there) | nothing (do NOT re-issue) | unchanged (`target`) |

`opts = new NavigationOptions(StoppingDistance: attackRange, UseMount: false, UseFlight: false)`. `Timeout` left default (null) — combat ticks supersede any single nav call.

**Determinism:** every branch is a pure function of (target identity, `DistanceToPlayer`, `attackRange`, prior `_approachTarget`). No clocks, no `IsNavigating` poll in the decision path (see D5). This makes `RecordedNavigationRequests` / `RecordedStops` counts exactly assertable.

**Distance source:** use `target`'s `HostileActor.DistanceToPlayer` from the same `GetHostileActors` scan that selected it — do **not** issue a second position read. `KillTarget` (the selection result) only carries `Id` + `DataId`, so the controller must resolve the chosen `HostileActor` from the scan list (match on `Id`) to read `Position` and `DistanceToPlayer`. Add a small private helper rather than changing `KillPriority`.

### D5 — Do NOT consult `INavigator.IsNavigating` in the decision

Rejected as the cadence gate. `IsNavigating` is a round-trip adapter read whose truthiness races the just-issued `NavigateTo`, making tick N's decision depend on adapter timing — non-deterministic and hard to replay. The controller's own `_approachTarget` latch is the single source of truth for "am I already heading at this target". `IsNavigating` remains on the interface for other callers; the controller ignores it.

### D6 — Large-mob hitbox undershoot: accept for v1, document it

A flat 3 m melee range is centre-to-centre and a large enemy's hitbox may still leave us out of true attack range. For v1 we **accept the undershoot** and do not add a hitbox pad. Rationale: `HostileActor` exposes no hitbox radius today; adding one is an adapter change out of scope. The cadence rule self-heals partially — if the rotation still can't land and the engine keeps re-deciding, the target stays out of `attackRange` only if `DistanceToPlayer` (centre-to-centre) genuinely exceeds 3 m, which closing to 3 m fixes for normal mobs. **Documented limitation:** very large quest bosses may need a future per-step or hitbox-aware pad. Tracked, not implemented here.

### D7 — `CombatDecision` shape: add an approach-state field

```csharp
public enum ApproachState { None, Approaching, InRange }

public sealed record CombatDecision(
    KillTarget? Target,
    bool RotationShouldRun,
    string Reason,
    ApproachState Approach = ApproachState.None);
```

**Why expose it:** the host (`EngineHost`) and trace need to distinguish "standing still because in range and fighting" from "moving toward target" from "idle, nothing to do" for status display and recording. Navigation *execution* stays fully internal to the controller (the host never calls `NavigateTo` itself), but the controller reports *which* state it drove. The default `ApproachState.None` keeps the field additive — existing `CombatDecision(target, run, reason)` call sites compile unchanged.

Mapping from the D4 table: out-of-range + issued/continuing nav → `Approaching`; target in range → `InRange`; no target → `None`. `RotationShouldRun` keeps its current meaning (`target is not null`) and is **independent** of approach state: the rotation runs while approaching too (a melee auto-attack/gap-closer may fire as we close). Only movement is gated on range.

### D8 — Ordering inside `Decide`, and what `Reset` must now clear

Order per tick:
1. `GetHostileActors(ScanRadius, ct)` — unchanged. On failure → `CombatDecision(null, false, "hostile query failed", ApproachState.None)` and (D8a) if `_approachTarget is not null`, `Stop` first.
2. `KillPriority.SelectTarget(...)` — unchanged.
3. SetTarget / ClearTarget on target-identity change — unchanged (uses `_currentTarget`).
4. **NEW:** resolve `attackRange` via `GetCurrentJob` → `JobRangeTable.AttackRange`. On job-read failure → fall back to `JobRangeTable.FallbackRange` (3 m); do not abort the tick.
5. **NEW:** apply the D4 cadence table (NavigateTo / Stop / nothing), update `_approachTarget`, compute `ApproachState`.
6. Return `CombatDecision(target, rotationShouldRun, reason, approach)`.

SetTarget happens **before** NavigateTo so the game target is set the same tick we begin moving.

`Reset()` must now clear `_approachTarget` in addition to `_currentTarget` and `_wasInCombat`. **`Reset` does NOT call `Stop`** — Reset is synchronous/void and fires when leaving the step; the engine's next step drives its own navigation. (If a lingering nav into the arena is a problem, the engine's next travel/step issues its own NavigateTo or Stop; the controller does not own cross-step movement.) State-only clear keeps `Reset` pure and matches its current contract.

---

## Role → range classification table (D3 detail)

Keyed on FFXIV ClassJob row id (`JobId.Value`). Base classes share their job's role. Range is the `StoppingDistance` passed to `NavigateTo`.

| Role | Range (m) | ClassJob row ids (Value) | Jobs |
|---|---|---|---|
| Tank | 3.0 | 1, 3, 19, 21, 32, 37 | GLA, MRD, PLD, WAR, DRK, GNB |
| Melee | 3.0 | 2, 4, 20, 22, 29, 30, 34, 39, 41 | PGL, LNC, MNK, DRG, ROG, NIN, SAM, RPR, VPR |
| PhysicalRanged | 20.0 | 5, 23, 31, 38 | ARC, BRD, MCH, DNC |
| Caster | 20.0 | 7, 25, 26, 27, 35, 36, 42 | THM, BLM, ACN, SMN, RDM, BLU, PCT |
| Healer | 20.0 | 6, 24, 28, 33, 40 | CNJ, WHM, SCH, AST, SGE |
| Unknown | 3.0 (fallback) | any other (DoH/DoL 8–18, BLU 36-special handling, id 0, future ids) | crafters/gatherers/unmapped |

Notes for the implementer:
- BLU (Blue Mage) row id 36 collides conceptually with RDM; verify in-game — if 36 is RDM, BLU is a separate id (commonly 36 is RDM, BLU is 36? confirm). **The tester pins only the rows asserted in scenarios below; the Builder fills the full table.** If a future/ambiguous id is encountered, `Classify` returns `Unknown` → 3 m.
- Range constants are centre-to-centre and all strictly `< ScanRadius (30 m)` so an in-range target is always within the scan that found it.
- The exact id set is the Builder's responsibility against current ClassJob data; the table above is the authoritative role grouping and the two range tiers (3 m / 20 m).

---

## Behavioral contract (summary)

1. Every `Decide` tick with an out-of-range eligible target that is new-or-changed issues exactly one `NavigateTo(target.Position, {StoppingDistance=attackRange, UseMount=false, UseFlight=false})`.
2. A subsequent tick with the *same* out-of-range target issues **no** further `NavigateTo` and **no** `Stop`.
3. The tick on which the target enters range (`DistanceToPlayer <= attackRange`) issues exactly one `Stop`, sets `_approachTarget = null`, and reports `ApproachState.InRange`.
4. The tick on which the target disappears (dies / leaves scan / no eligible) while we were approaching issues exactly one `Stop` and reports `ApproachState.None`.
5. A melee job (3 m) approaches a target at 8 m; a ranged job (20 m) with the same 8 m target is already in range → no `NavigateTo`, reports `InRange`.
6. NavmeshUnavailable / nav `Result.Fail` does not throw and does not abort targeting — the decision still returns the selected target with `RotationShouldRun:true`; approach is reported as `Approaching` (we tried).
7. `Reset` clears `_approachTarget` (next combat step starts with no nav latch); does not call `Stop`.

---

## Edge cases (must be covered)

| Edge | Expected behavior |
|---|---|
| Navmesh unavailable | `NavigateTo` returns `Result.Ok(NavmeshUnavailable)` or `Result.Fail`. Controller does not throw, does not retry within the tick, still returns the target with `RotationShouldRun:true`, `ApproachState.Approaching`. Latch `_approachTarget` is still set to the target (so we don't spam re-issue every tick on a dead navmesh). |
| Target outside scan radius | Never selected (GetHostileActors filters by 30 m). So distance to chosen target is always ≤ 30 m. No special case. |
| Target dies mid-approach | Next tick `SelectTarget` returns a different target or null. If null → `Stop`, `ApproachState.None`. If a new add → SetTarget(new), and NavigateTo(new) only if the new target is out of range. |
| Target changes to a closer in-range add | SetTarget(new add). New add in range → `Stop` (we were approaching the old one), `ApproachState.InRange`, no NavigateTo. |
| No eligible target from the first tick | No `NavigateTo`, no `Stop` (nothing to stop — `_approachTarget` was already null), `ApproachState.None`. |
| `GetCurrentJob` fails | Use `FallbackRange` (3 m); proceed normally. |
| `GetHostileActors` fails | Existing behavior preserved (`CombatDecision(null,false,"hostile query failed")`), plus `Stop` if we were approaching, `ApproachState.None`. |
| Target exactly at `attackRange` (`DistanceToPlayer == attackRange`) | Treated as **in range** (`<=`). No NavigateTo; `Stop` if previously approaching; `InRange`. |

---

## Testable scenarios (Given-When-Then) — for the Tester

All against `new CombatController(fakeGameState, fakeCombat, fakeNavigator)`. **Important fixture note:** the existing `MakeHostile` helper places every actor at `WorldPosition(0,0,0)` and passes `distance` separately. For approach tests the actor's `Position` must be distinct and the player's position set so the navigation destination is meaningful — extend the helper to accept a `WorldPosition position` (default it, then set a non-origin position for approach scenarios) and keep `DistanceToPlayer` independently scriptable. Set the job with `fakeGameState.SetJob(new JobId(rowId), level)`. Assert on `fakeNavigator.RecordedNavigationRequests` and `fakeNavigator.RecordedStops`.

### JobRangeTable (pure)

- **G1 (melee classify):** Given `JobId(2)` (PGL). When `Classify`. Then `CombatRole.Melee`; `AttackRange == 3.0f`.
- **G2 (tank classify):** Given `JobId(19)` (PLD). When `Classify`. Then `Tank`; `AttackRange == 3.0f`.
- **G3 (physical ranged):** Given `JobId(23)` (BRD). When `Classify`. Then `PhysicalRanged`; `AttackRange == 20.0f`.
- **G4 (caster):** Given `JobId(25)`? use a pinned caster `JobId(27)` (BLM). When `Classify`. Then `Caster`; `AttackRange == 20.0f`.
- **G5 (healer):** Given `JobId(24)` (WHM). When `Classify`. Then `Healer`; `AttackRange == 20.0f`.
- **G6 (unknown fallback):** Given `JobId(999)`. When `Classify`. Then `Unknown`; `AttackRange == 3.0f`.
- **G7 (all ranges < scan):** For every mapped id, `AttackRange(id) < 30f`.

### Approach happy paths

- **G8 (melee approaches out-of-range target):** Given job PGL (`JobId(2)`), one eligible target `Id=9 DataId=100` at `Position=(10,0,0)`, `DistanceToPlayer=8`, player at origin. When `Decide` once. Then `RecordedNavigationRequests` has exactly 1 entry; its `Destination == (10,0,0)`; `Options.StoppingDistance == 3.0f`, `UseMount == false`, `UseFlight == false`; decision `ApproachState == Approaching`, `RotationShouldRun == true`. `RecordedStops` empty.
- **G9 (ranged in range, no nav):** Given job BRD (`JobId(23)`), target at `DistanceToPlayer=8`. When `Decide` once. Then `RecordedNavigationRequests` empty; `RecordedStops` empty; `ApproachState == InRange`; `RotationShouldRun == true`.
- **G10 (melee in range, no nav):** Given job PLD (`JobId(19)`), target at `DistanceToPlayer=3`. When `Decide` once. Then no NavigateTo, no Stop, `InRange`.
- **G11 (exact boundary is in range):** Given melee, target `DistanceToPlayer == 3.0` exactly. When `Decide`. Then no NavigateTo, `InRange`.

### Approach cadence (the core deterministic rule)

- **G12 (no re-issue for same out-of-range target):** Given melee, same target `Id=9` at `DistanceToPlayer=8` on tick 1 and tick 2 (still 8). When `Decide` twice. Then `RecordedNavigationRequests` has exactly **1** entry total (issued tick 1, not re-issued tick 2). `RecordedStops` empty. Both ticks report `Approaching`.
- **G13 (re-issue on target change):** Given melee. Tick 1: target `Id=9` at 8 m. Tick 2: `Id=9` dead/removed, new target `Id=10 DataId=100` at `Position=(5,0,0)`, `DistanceToPlayer=6`. When `Decide` twice. Then 2 `NavigateTo` entries: first dest from `Id=9`'s position, second dest `(5,0,0)`. No `Stop` between (target swapped while both out of range — see G14 for the stop-on-swap-into-range case).
- **G14 (stop on transition into range):** Given melee, target `Id=9`. Tick 1: `DistanceToPlayer=8` (issues NavigateTo). Tick 2: same `Id=9` now `DistanceToPlayer=2` (in range). When `Decide` twice. Then `RecordedNavigationRequests` has 1 entry (tick 1); `RecordedStops` has exactly **1** entry (tick 2); tick 2 reports `InRange`; `_approachTarget` cleared (assert via tick 3: same in-range target issues no further Stop).
- **G15 (no double-stop):** Continue G14 with tick 3: same `Id=9` still at 2 m. Then `RecordedStops` still has exactly 1 entry (no second Stop while remaining in range).

### Edge / error cases

- **G16 (target dies mid-approach → stop):** Given melee, tick 1 target `Id=9` at 8 m (NavigateTo issued). Tick 2: no eligible target (hostiles cleared). When `Decide` twice. Then `RecordedStops` has exactly 1 entry; `RecordedNavigationRequests` 1 entry; tick 2 decision `Target == null`, `RotationShouldRun == false`, `ApproachState == None`.
- **G17 (no target from first tick → no stop, no nav):** Given melee, no hostiles. When `Decide` once. Then `RecordedNavigationRequests` empty, `RecordedStops` empty, `ApproachState == None`, `Target == null`.
- **G18 (navmesh unavailable does not throw or abort):** Given melee, out-of-range target at 8 m; `fakeNavigator.ScriptNextResult(NavigationOutcome.NavmeshUnavailable)`. When `Decide`. Then no throw; decision `Target.Id == 9`, `RotationShouldRun == true`, `ApproachState == Approaching`; `RecordedNavigationRequests` has 1 entry; `_approachTarget` set (assert tick 2 same target issues no second NavigateTo — we don't spam a dead navmesh).
- **G19 (navmesh hard fail does not throw):** Given melee, out-of-range target; `fakeNavigator.FailNextWith("navmeshUnavailable")`. When `Decide`. Then no throw; `RotationShouldRun == true`, `ApproachState == Approaching`; 1 NavigateTo recorded.
- **G20 (GetCurrentJob fails → 3 m fallback):** Given `fakeGameState.SetCurrentJobFail("noLocalPlayer")`, target at `DistanceToPlayer=4` (out of 3 m fallback range, in 20 m). When `Decide`. Then NavigateTo issued with `StoppingDistance == 3.0f` (fallback range applied); `ApproachState == Approaching`.
- **G21 (hostile query fails → stop if approaching):** Given melee, tick 1 out-of-range target at 8 m (NavigateTo issued); tick 2 `fakeGameState.SetHostileActorsFailure("boom")`. When `Decide` twice. Then tick 2 decision `Target == null`, `RotationShouldRun == false`, reason `"hostile query failed"`, `ApproachState == None`; `RecordedStops` has 1 entry.
- **G22 (Reset clears approach latch, no Stop):** Given melee, tick 1 out-of-range target at 8 m (NavigateTo issued). When `Reset()`. Then `RecordedStops` empty (Reset does not Stop). And: after Reset, `Decide` with the *same* target at 8 m issues a **new** NavigateTo (latch was cleared → re-issue), proving `_approachTarget` was reset.
- **G23 (SetTarget precedes NavigateTo same tick):** Given melee, out-of-range new target. When `Decide`. Then by call-log timestamps `FakeCombat.RecordedTargets` SetTarget `At` <= `FakeNavigator.RecordedNavigationRequests` `At` (ordering: target set before/at nav issue). (If timestamp granularity is too coarse, assert both occurred and rely on documented order.)

### Construction guard

- **G24 (null navigator throws):** `new CombatController(gameState, combat, null!)` throws `ArgumentNullException` (paramName `"navigator"`).

---

## Implementation order

**Phase A — JobRangeTable (pure, no deps).** Define `CombatRole`, `JobRangeTable.Classify`, `AttackRange`, the constants, and the full row-id table. Pass G1–G7. Done before B.

**Phase B — CombatController approach.** Add `INavigator` ctor param (+ null guard) and `_approachTarget` field. Add the D7 `ApproachState` enum + `CombatDecision` field. Implement the D4 cadence inside `Decide` per the D8 ordering. Update `Reset` to clear `_approachTarget`. Pass G8–G24. Done before C.

**Phase C — Wire production sites.** Update `QuestEngine.cs:85` and `QfCommand.cs:690` to pass an `INavigator`. Build the solution (TreatWarningsAsErrors) green. No new tests; existing engine/host tests stay green.

---

## Done criteria

1. `JobRangeTable.AttackRange` returns 3 m for tanks/melee, 20 m for ranged/caster/healer, 3 m for unknown — verified by G1–G7.
2. A melee job with an out-of-range target issues exactly one `NavigateTo` with `StoppingDistance` = its attack range, `UseMount=false`, `UseFlight=false` (G8).
3. Re-deciding against the same out-of-range target issues **no** further `NavigateTo` (G12) — proves the cadence latch, not per-tick spam.
4. Entering range issues exactly one `Stop` and reports `InRange`; staying in range issues no further `Stop` (G14, G15).
5. Losing the target while approaching issues exactly one `Stop` and reports `None` (G16).
6. Navmesh-unavailable and job-read failures never throw and never abort targeting (G18–G20).
7. `Reset` clears the approach latch and does not `Stop` (G22).
8. Solution builds with `TreatWarningsAsErrors`; both production construction sites compile and existing tests stay green (Phase C).

---

## What this plan does NOT include

- No schema change. `CombatStep.Location` stays the "travel to the arena" anchor; this is in-fight gap-closing only.
- No hitbox-radius pad for large mobs (D6 — accepted undershoot, documented, future work).
- No `IsNavigating` polling in the decision path (D5).
- No new adapter method on `IGameStateProvider` (role is a pure function of the existing `JobId`).
- No `Stop` from `Reset`; cross-step movement is the engine's responsibility, not the controller's.
- No change to `KillPriority.SelectTarget` (the controller resolves the chosen `HostileActor` from the scan list to read `Position`/`DistanceToPlayer`).

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Testable scenarios.
- Happy paths: 4 scenarios (G8–G11)
- Edge cases: 11 scenarios (G7, G12–G16, G18–G19, G22–G24 boundary/cadence/reset/ctor)
- Error cases: 3 scenarios (G19 navmesh fail, G20 job fail, G21 hostile fail)
- Plus JobRangeTable units: 7 scenarios (G1–G7)
- Expected total: ~24 tests across `JobRangeTableTests.cs` and `CombatControllerApproachTests.cs` in QuestForge.Engine.Tests.
