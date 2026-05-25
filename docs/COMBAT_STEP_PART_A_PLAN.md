# Combat Step — Part A Implementation Plan (Issue #6 Part A)

**Status:** ready for test creation (pending user confirmation of the §3 open-decision resolutions — see §0)
**Input docs:** `docs/COMBAT_STEP_PLAN.md` (master design — read §3, §6, §7), `docs/QUEST_VARIABLES_TRACE_PLAN.md` (fixture-starvation cascade), `docs/SCHEMA.md §4.8` (combat step), `docs/ADAPTERS.md §8` (ICombat), `CLAUDE.md` (engine-purity boundary, Result<T>, three failure counters)
**Branch:** `feat/combat-step-part-a` (`questforge` repo)
**Output:** A `combat` step resolves end-to-end against fakes with **no running game** — the engine navigates to the arena, hands off to a stateful combat controller that selects targets by kill-priority and drives an in-combat→complete loop, and the step completes via its `expect` predicate (incl. `questVariable(...)`). CI behavior change: `QuestForge.Engine.Tests` and `QuestForge.Adapters.Tests` gain green combat coverage; existing replay fixtures stay green (no starvation).

**Clean-room note.** Questionable / WrathCombo are behavioral references only (kill-priority *concept*, in-combat→complete *loop concept*, WrathCombo's *published IPC* as an integration surface in part B). No source is copied, ported, or adapted. Every type, signature, and control-flow decision below is independently derived for QuestForge.

---

## 0. Decisions to confirm before test creation (the §7 resolutions)

These three resolve `COMBAT_STEP_PLAN.md §7 "Open"`. They are the load-bearing choices the user must confirm before the Tester starts. Each is fully specified in the section noted.

1. **Boundary (→ §3).** The engine stays **stateless per tick** and emits a new `EngineAction.Engage`. A new stateful `CombatController` lives in **`QuestForge.Engine/Combat/`** (pure C#, engine assembly) and depends **only on adapter interfaces** — it never touches Dalamud. The Dalamud object-table scan + `TargetManager` writes are pushed behind two new adapter-interface reads/acts (`IGameStateProvider.GetHostileActors` and `ICombat.SetTarget`), whose concrete impls land in **part B**. The controller is constructed by the engine (not the host), so it is unit-testable against `FakeGameStateProvider` + `FakeCombat`.

2. **Schema (→ §4).** `CombatStep` is realigned to `{ KillEnemyDataIds: uint[], Spawn: "autoOnEnterArea"|"overworldEnemies", Location: NpcLocation?, expect: <predicate> }`. The coarse `CombatTarget(Kind, Radius, NpcId)` record is **retired** (deleted — early dev, no schema-version bump per the user). Completion is **via `expect`** (`questVariable`/`questFlag`/`questSequence`), never "target dead." A **paired questforge-tools change** adds structural validation of the new fields (mirrored schema + one validator rule).

3. **EngineAction + traces (→ §5, §6).** One new action: `EngineAction.Engage(CombatStep Step, KillTarget? Target)`. The `CombatStep` arm of `ResolveActionForStep` navigates to `Location` first (reusing `ResolveInteractOrNavigate`), then returns `Engage` while `expect` is unmet. **Trace capture of combat is deferred to part B** (no `RecordingCombat` proxy in part A); part A records nothing new on the combat path. The single new engine-side read the controller performs (`GetHostileActors`) is **step-gated** to the active combat step so it never enters the common per-tick path — preventing the fixture-starvation cascade documented in `QUEST_VARIABLES_TRACE_PLAN.md`.

---

## Dependency graph

Three repos; part A touches two. Strict build order:

```
1. questforge (this repo)
   ├── QuestForge.Schema           ← realign CombatStep, retire CombatTarget
   ├── QuestForge.Adapters         ← new reads/acts: GetHostileActors, SetTarget, HostileActor, IRotationModule
   ├── QuestForge.Adapters.Fakes   ← FakeGameStateProvider.GetHostileActors scripting; FakeCombat reshape
   ├── QuestForge.Engine           ← CombatController (Combat/), EngineAction.Engage, ResolveActionForStep arm
   ├── QuestForge.Engine.Tests     ← controller + engine-resolution tests
   └── QuestForge.Adapters.Tests   ← FakeCombat / FakeGameStateProvider contract tests

2. questforge-tools (paired, schema mirror + validator)
   ├── QuestForge.Schema (mirror)  ← same CombatStep realignment
   └── QuestForge.Tools.Validator  ← structural rule for new combat fields
```

**Build order:** Schema realignment first (both repos) → adapter reshape + fakes → engine controller + action → tests. The questforge-tools change is independent of the engine work and can land in parallel once the schema mirror is updated.

**Prerequisite already landed:** the `questVariable(questId, index) -> Int` predicate (registry, parser, checker, engine evaluator). Confirmed present in `QuestForge.Engine/Predicates/PredicateEvaluator.cs:103,118` and `questforge-tools/QuestForge.Predicates/FunctionRegistry.cs`. Completion-via-`questVariable` is therefore unblocked; part A adds no predicate work.

---

## Architectural decisions (read before coding)

### D1 — The engine stays stateless per tick; the controller is the stateful unit

`QuestEngine.ResolveActionForStep(step, ui, playerPos)` (`QuestForge.Engine/QuestEngine.cs:430`) is a pure `Step → EngineAction` function re-evaluated every tick; `EngineHost.DispatchAction` (`QuestForge.Plugin/EngineHost.cs:219`) executes the action. Combat is inherently stateful (current target, "was in combat"). We reconcile this by **not** making the engine stateful. Instead:

- The `CombatStep` arm of `ResolveActionForStep` stays a pure mapping: while `expect` is unmet it returns `EngineAction.Engage(step, target?)`; the navigate-first leg reuses `ResolveInteractOrNavigate`.
- All combat *state* (selected target, in-combat latch) lives in a **`CombatController`** that the engine owns as a field, and which the engine drives during the `CombatStep` resolution. The controller's `Tick` is called only while the active step is a `CombatStep` (step-gated, see D6). It is reset when the engine leaves the combat step (sequence advance / step confirmed).

**Why not push the controller into the host/adapter layer?** Rejected: the host layer (`EngineHost`) is Dalamud-coupled and untestable in CI. Putting kill-priority there would make the core combat logic untestable against fakes, violating the testing-tier strategy. The controller's *reads* are all adapter-interface calls, so it stays in the engine assembly and CI-testable — exactly the model `PredicateEvaluator` already uses (it lives in `QuestForge.Engine/Predicates/` and reads via `IGameStateProvider`/`IQuestState`).

**Why not make the engine itself stateful?** Rejected: every other step type is a pure per-tick mapping, and the resume sub-loop is the only existing stateful construct. Encapsulating combat state in a dedicated controller object keeps `QuestEngine` cohesion intact and gives the Tester a small, directly-constructable unit (`new CombatController(gameState, combat)`).

```csharp
// QuestForge.Engine/Combat/CombatController.cs  (NEW — pure C#, engine assembly)
public sealed class CombatController
{
    private readonly IGameStateProvider _gameState;
    private readonly ICombat _combat;

    // Stateful across ticks within one combat step:
    private KillTarget? _currentTarget;   // the actor we last told ICombat to attack
    private bool _wasInCombat;            // "was in combat" latch for the completion loop

    public CombatController(IGameStateProvider gameState, ICombat combat) { ... }

    // Called once per tick WHILE the active step is the given CombatStep.
    // Returns the target the engine should pass to EngineAction.Engage (or null when
    // there is nothing to attack right now — engine emits Engage(step, null) → host idles).
    public async Task<CombatDecision> Decide(CombatStep step, CancellationToken ct);

    // Engine calls this when it leaves the combat step (confirmed / sequence advance).
    public void Reset();
}

public readonly record struct KillTarget(ActorId Id, uint DataId);

// What the controller decided this tick. RotationShouldRun mirrors "are we engaged".
public sealed record CombatDecision(KillTarget? Target, bool RotationShouldRun, string Reason);
```

### D2 — Split `ICombat`: a thin **rotation module** vs the **controller**

Today `ICombat` conflates targeting + rotation (`EngageTarget(NpcId)`, `EngageNearestHostile(radius)` — `QuestForge.Adapters/Combat/ICombat.cs`). Per `COMBAT_STEP_PLAN.md §3.1` we split along the proven seam:

- **`ICombat` becomes the rotation-module + target-write surface only.** The Dalamud impl (part B) wraps WrathCombo's lease IPC; the rotation runs on whatever target *we* set. Reshaped surface:

```csharp
// QuestForge.Adapters/Combat/ICombat.cs  (RESHAPED)
public interface ICombat
{
    // ---- rotation module (lease lifecycle; part-B impl wraps WrathCombo IPC) ----
    Task<Result<bool>>            IsRotationModuleAvailable(CancellationToken ct);
    Task<Result<RotationModuleInfo>> GetRotationModule(CancellationToken ct);
    Task<Result<Unit>>           StartRotation(CancellationToken ct);   // acquire lease + auto-rotation on
    Task<Result<Unit>>           StopRotation(CancellationToken ct);    // release lease

    // ---- targeting (controller drives this; part-B impl writes TargetManager.Target) ----
    Task<Result<Unit>>           SetTarget(ActorId target, CancellationToken ct);
    Task<Result<Unit>>           ClearTarget(CancellationToken ct);
}

public record RotationModuleInfo(string Name, string Version, bool LeaseHeld);
```

- **Direct action use moves out of `ICombat`.** `UseAction`/`UseActionOnObject`/`IsActionUsable` belong to the `use-action` step type, not combat. **Decision: leave them where they are for part A** (do not break `use-action`) — extract them to a future `IActionExecutor` only if/when `use-action` is reworked. To keep part A scoped: `UseAction`/`UseActionOnObject`/`IsActionUsable` **stay on `ICombat` unchanged**; only the targeting/rotation methods are reshaped. The retired methods are `EngageTarget`, `EngageNearestHostile`, `Disengage`, `IsCombatPluginAvailable`, `GetActiveCombatPlugin`, plus enums `CombatOutcome`/`CombatPluginInfo` (replaced by `RotationModuleInfo`). `UseActionOutcome` is retained.

  *(Confirm with user: keeping `UseAction*` on `ICombat` vs extracting now. Recommendation: keep — extraction is unrelated churn and `use-action` is not in this issue.)*

**Why a lease-shaped rotation module rather than `Engage(target)`?** The game requires the combat plugin to attack a *manually-set* target (WrathCombo `DPSRotationMode.Manual`); the plugin owns no targeting. Modeling rotation as start/stop/availability + a separate `SetTarget` mirrors that reality and keeps the controller (ours) authoritative over *who* we fight. An `Engage(target)`-style method would re-conflate the two concerns we are splitting.

**Why does targeting return `ActorId`, not `NpcId`?** Multiple live actors can share one BNpc **base data-id** (`KillEnemyDataIds` matches by base id), but `TargetManager` writes a specific live object. We introduce a runtime **`ActorId`** (the live object's GameObjectId) distinct from `NpcId` (a base data-id used in schema/dialogue). `SetTarget(ActorId)` targets one concrete actor; `KillEnemyDataIds` (uint base ids) decide *which* actors are eligible.

```csharp
// QuestForge.Adapters/Types/ActorId.cs  (NEW)
public readonly record struct ActorId(ulong Value);   // live GameObject identity
```

### D3 — New adapter read: `GetHostileActors` (richer than `GetNearbyNpcs`)

The controller needs more than `NpcReference(Id, Position, DistanceToPlayer)` (`QuestForge.Adapters/Types/References.cs:13`) to compute kill-priority. Per `COMBAT_STEP_PLAN.md §3.1` it needs: data-id, distance, targetability, aggro-on-us, on-our-enmity, and a quest-marker flag. New read on `IGameStateProvider`:

```csharp
// QuestForge.Adapters/State/IGameStateProvider.cs  (ADD)
Task<Result<IReadOnlyList<HostileActor>>> GetHostileActors(float radius, CancellationToken ct);

// QuestForge.Adapters/Types/HostileActor.cs  (NEW)
public record HostileActor(
    ActorId Id,            // live object identity (for SetTarget)
    uint    DataId,        // BNpc base data-id (matches KillEnemyDataIds)
    WorldPosition Position,
    float   DistanceToPlayer,
    bool    IsTargetable,  // filters untargetable/dead
    bool    IsDead,
    bool    IsTargetingPlayer, // aggro on us (highest priority concept)
    bool    OnPlayerEnmityList, // on our hate list
    bool    HasQuestMarker);    // nameplate quest icon present
```

`GetNearbyNpcs`/`FindNpc` are **unchanged** (they serve dialogue/interaction targeting). `GetHostileActors` is the combat-only read. The Dalamud impl (part B) does the ClientStructs object-table scan; part A only adds the interface + `FakeGameStateProvider` scripting.

### D4 — Kill-priority is a pure, deterministic function (no Dalamud, fully testable)

The controller selects the next target with a pure ranking over the `HostileActor` list filtered to eligible enemies. Determinism is a hard requirement (replay; CI). Concept (independently derived; weights chosen for QuestForge):

1. **Filter** out actors where `IsDead || !IsTargetable`. Then, **only when `Spawn == overworldEnemies`**, also drop actors whose `DataId` is not in `KillEnemyDataIds` (overworld = only kill the named mobs among many free-roaming ones). For `autoOnEnterArea`, all targetable/living hostiles are eligible **regardless of `KillEnemyDataIds`** — the kill-set is a **scoring bonus** (step 2), not a filter, so aggro'd adds in the curated arena are still fought.
2. **Score** each survivor (higher = attack first): `IsTargetingPlayer` (+150), `OnPlayerEnmityList` (+125), `HasQuestMarker` (+100), `DataId ∈ KillEnemyDataIds` (+90).
3. **Tie-break deterministically:** higher score first; then **nearest** (`DistanceToPlayer` asc); then **lowest `ActorId.Value`** (stable final tie-break — no clock, no RNG).
4. Empty survivor set → `null` (nothing to attack this tick).

```csharp
// QuestForge.Engine/Combat/KillPriority.cs  (NEW — pure static, no async, no Dalamud)
public static class KillPriority
{
    public static KillTarget? SelectTarget(
        IReadOnlyList<HostileActor> actors, IReadOnlySet<uint> killEnemyDataIds, CombatSpawn spawn);
    public static int Score(HostileActor a, IReadOnlySet<uint> killEnemyDataIds); // exposed for unit assertions
}
```

**Why expose `Score` and keep selection pure/static?** It lets the Tester pin exact weight ordering and tie-breaks without constructing a controller or any async machinery — the highest-ROI unit surface. Weights are QuestForge's own; the *concept* (aggro > enmity > quest-marker > kill-set, nearest tie-break) is what the game requires.

### D5 — Completion is the step's `expect`, evaluated by the engine (not the controller)

The controller never decides completion. It only selects targets and reports whether rotation should run. **Completion stays exactly where it is today:** `QuestEngine.ResolveAction`'s step loop already confirms-and-skips a step when `step.Expect` evaluates true (`QuestEngine.cs:389`). For a `CombatStep`, that means:

- While `expect` is **unmet** → the step is the active step; the engine drives the controller and returns `EngineAction.Engage(step, controller-selected target)`.
- When `expect` becomes **true** (e.g. `questVariable(66104,0) >= 3`, or `questFlag(...)`, or `questSequence(...)`) → the existing confirm-and-skip path confirms the step; the engine moves on; the engine calls `controller.Reset()`.

The "was in combat → not in combat" loop from the reference model is **not** the completion signal in QuestForge — it is only a controller-internal heuristic for *whether to keep attacking / re-acquire a target* (`CombatDecision.RotationShouldRun`). The authoritative, replayable completion is the permanent `expect` predicate. This aligns with the postcondition-discipline invariant (CLAUDE.md) and issue #33 (no transient predicates).

**Why not let the controller signal "done"?** Rejected: "target dead" / "left combat" are transient and non-deterministic under replay; the engine already has a permanent, replayable completion mechanism (`expect`) that reads recorded observations. Routing completion through the controller would duplicate and weaken it.

### D6 — Step-gated combat reads (the fixture-starvation analysis)

`QUEST_VARIABLES_TRACE_PLAN.md §5` documents the cascade: adding any read to the **common per-tick path** in `ResolveAction` makes the generic replay scanner (`SegmentedObservationScanner`) throw `ReplayObservationStarvationException` for every existing fixture whose trace never recorded that `(method, arg)` pair. The new combat read **`GetHostileActors`** must therefore be **step-gated**: fired **only** while the active step being resolved is a `CombatStep`.

Placement: `GetHostileActors` is called **inside the controller's `Decide`**, which the engine calls **only** from the `CombatStep` arm of step resolution — never from the per-tick prelude (`GetUiState`/`GetPlayerPosition`/`GetPlayerZone`/`GetQuestVariables`). Concretely, the engine's `ResolveActionForStep` is sync today; combat needs an async controller call, so the `CombatStep` case is handled in `ResolveAction` (async) **after** the step loop selects the combat step as active, not in the sync `ResolveActionForStep` switch. See §5 for the exact wiring.

**Starvation conclusion (explicit):**
- **No existing replay fixture starves.** `simple-linear-acceptance` (scripted, `FakeQuestState`/`FakeGameStateProvider`) and `with-attunement` (generic, scanner-backed, quest 65644) contain **no `combat` step**, so the active step is never a `CombatStep`, so `GetHostileActors` is **never called** during their replay. The `(GetHostileActors, *)` pair never enters their read pattern → no starvation. Both stay green; **no fixture re-record is required for part A.** This is the direct payoff of step-gating and is the key difference from the quest-variables change (which added a read to the *common* path and therefore did force a re-record).
- A *future* combat fixture (part B / corpus) will record `GetHostileActors` because its active step *is* a `CombatStep`; its trace will carry the pair from creation. Part A introduces no `RecordingCombat` proxy and records nothing new on the combat path (D-trace below), so no proxy-side cascade either.

**Trace handling (D-trace).** Part A does **not** capture combat actions/reads in traces (no `RecordingCombat` proxy; the engine-side `GetHostileActors` is read through the existing `RecordingGameStateProvider` only when a combat step is active, which no current fixture exercises). Recording the `Engage` action and `SetTarget` acts is **deferred to part B**, alongside the Dalamud impl that actually performs them. Rationale: part A is CI-against-fakes; there is nothing in-game to record yet, and adding a `RecordingCombat` proxy now would be untested speculation. Flagged as a part-B handoff.

### D7 — `FakeCombat` and `FakeGameStateProvider` reshape (CI scriptability)

- **`FakeCombat`** implements the reshaped `ICombat`. It records `SetTarget`/`ClearTarget`/`StartRotation`/`StopRotation` calls into a `CallLog` (mirroring the existing `RecordedEngagements` pattern) and exposes `SetRotationModuleAvailable(bool)`. The old `RecordedEngagements`/`ScriptNextCombatResult` are removed. `UseAction*` keep their current trivial fakes.
- **`FakeGameStateProvider`** gains `AddHostileActor(HostileActor)` / `ClearHostileActors()` and implements `GetHostileActors(radius)` (filter by `DistanceToPlayer <= radius`, record `nameof(GetHostileActors)`), mirroring `GetNearbyNpcs` (`FakeGameStateProvider.cs:217`).

This is what lets the controller and the engine combat arm be exercised with **no running game**.

---

## Task breakdown

### Task 1 — Schema realignment (`QuestForge.Schema`, both repos)

**1.1** Replace `CombatStep` (`QuestForge.Schema/Step.cs:88`):

```csharp
public class CombatStep : Step
{
    // Set of BNpc base data-ids to defeat. Empty + autoOnEnterArea ⇒ "all hostiles in the arena".
    public uint[] KillEnemyDataIds { get; init; } = [];

    // v1 spawn type. autoOnEnterArea: a fixed set already present on entry.
    // overworldEnemies: kill N of possibly-many free-roaming mobs.
    public CombatSpawn Spawn { get; init; } = CombatSpawn.AutoOnEnterArea;

    // Coarse "get to the arena" navigation — mirrors AttunementStep.Location.
    // When present and the player is beyond StopDistance, the engine emits Navigate first.
    public NpcLocation? Location { get; init; }

    // Completion is the inherited Step.Expect (questVariable/questFlag/questSequence). REQUIRED for
    // combat in practice; the validator warns when absent (a combat step with no expect can never complete).
}

public enum CombatSpawn { AutoOnEnterArea, OverworldEnemies }   // serialized camelCase via UseStringEnumConverter
```

**1.2** **Delete** `CombatTarget` (`QuestForge.Schema/SharedValueTypes.cs:121`). Retire the `Kind` enum entirely (no `nearestHostile`/`specificNpc`/`wave`). `wave` (AoE clear-the-room) is expressible as `Spawn=autoOnEnterArea` + empty `KillEnemyDataIds` + an `expect` of `not playerInCombat()` only if a permanent predicate exists; otherwise authors use a `questVariable`/`questSequence` `expect` (the real signal). No back-compat shim — early dev, no version bump.

**1.3** `QuestForgeJsonContext` (`QuestForge.Schema/QuestForgeJsonContext.cs`): `CombatStep` already registered (line 16); add `[JsonSerializable(typeof(CombatSpawn))]` is not needed (enums serialize inline), but verify round-trip. No new top-level type to register beyond the `CombatStep` shape change.

**1.4** Mirror **1.1–1.3** into `questforge-tools/QuestForge.Schema/` (the copied schema). This is mechanical — same edits.

### Task 2 — Adapter reshape (`QuestForge.Adapters`)

**2.1** Add `QuestForge.Adapters/Types/ActorId.cs` (D2) and `QuestForge.Adapters/Types/HostileActor.cs` (D3).
**2.2** Reshape `ICombat` (D2): rotation-module + targeting methods; remove engage/disengage/plugin-info; keep `UseAction*`.
**2.3** Add `GetHostileActors` to `IGameStateProvider` (D3).
**2.4** Update `WrathComboAdapter` (`QuestForge.Adapters.Dalamud/Combat/WrathComboAdapter.cs`) to the new `ICombat` surface as a **stub** returning `IsRotationModuleAvailable → false`, `StartRotation/StopRotation/SetTarget/ClearTarget → Result.Ok`, with an `// IPC wiring in part B` comment. Add a `DalamudGameStateProvider.GetHostileActors` stub returning `Result.Ok(empty)` with the same part-B comment. *(These keep the plugin compiling; their real impls are part B and are explicitly out of CI scope.)*

### Task 3 — Fakes reshape (`QuestForge.Adapters.Fakes`)

**3.1** `FakeCombat` (D7): implement reshaped `ICombat`; `RecordedTargets`/`RecordedRotation` call logs; `SetRotationModuleAvailable(bool)`.
**3.2** `FakeGameStateProvider` (D7): `AddHostileActor`/`ClearHostileActors` + `GetHostileActors(radius)` impl recording `nameof(GetHostileActors)`.

### Task 4 — Combat controller + kill-priority (`QuestForge.Engine/Combat/`)

**4.1** `KillPriority` (D4) — pure static `SelectTarget` + `Score`.
**4.2** `CombatController` (D1) — `Decide(step, ct)`:
1. `var actors = await _gameState.GetHostileActors(radius, ct)` (radius: a controller constant, e.g. 30 — `overworldEnemies` can roam; not author-configurable in v1).
2. On adapter failure → `CombatDecision(null, RotationShouldRun:false, "hostile query failed")` (fail-safe: idle, no throw).
3. `var target = KillPriority.SelectTarget(actors, step.KillEnemyDataIds.ToHashSet(), step.Spawn)`.
4. If `target` differs from `_currentTarget` → `await _combat.SetTarget(target.Id, ct)`; update `_currentTarget`.
5. If `target is null` → `await _combat.ClearTarget(ct)`; `_currentTarget = null`.
6. Return `CombatDecision(target, RotationShouldRun: target is not null, reason)`.
`Reset()` clears `_currentTarget`/`_wasInCombat`. (The rotation start/stop lease lifecycle is driven by the **host** in part B around `Engage`; part A's controller asserts target selection + `SetTarget` only. `StartRotation`/`StopRotation` are exercised by `FakeCombat` contract tests, Task 6, not the controller loop.)

### Task 5 — Engine action + resolution arm (`QuestForge.Engine`)

**5.1** `EngineAction` (`QuestForge.Engine/EngineAction.cs`): add
```csharp
public sealed record Engage(CombatStep Step, KillTarget? Target) : EngineAction;
```
**5.2** `QuestEngine`: construct a `CombatController` in the ctor (it already receives `ICombat _combat` and `IGameStateProvider _gameState`). Drive it from the **async** `ResolveAction` step loop, not the sync `ResolveActionForStep` switch (D6 — keeps the read step-gated and lets the controller be awaited):

- In the step loop (`QuestEngine.cs:379`), when the active (unconfirmed, expect-unmet, skip-unmet) step is a `CombatStep`:
  - **navigate-first:** if `step.Location` is set and the player is beyond `StopDistance`, return the navigate leg via the existing `ResolveInteractOrNavigate` pattern (return `Navigate`, *do not* call the controller this tick).
  - **engage:** otherwise `var decision = await _combatController.Decide(combatStep, ct)` and return `(new EngineAction.Engage(combatStep, decision.Target), step.Id)`.
- When a `CombatStep` is confirmed (expect met) or the sequence advances, call `_combatController.Reset()`. (Hook into the existing confirm path and the existing sequence-change reset at `QuestEngine.cs:358-364`.)
- The `_ => throw NotSupportedException` default in `ResolveActionForStep` (`QuestEngine.cs:505`) no longer needs a `CombatStep` arm because combat is handled in the async loop; leave the throw for genuinely-unsupported types.

**5.3** `EngineTestHarness.RunToCompletion` (`QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs:112`): add an `EngineAction.Engage` arm that records the action and (to advance the fake world) lets the test's wired callbacks flip the completion predicate. The harness arm calls no real adapter (combat is fake) — it records and continues, mirroring the `Wait` arm.

### Task 6 — Tests (`QuestForge.Engine.Tests`, `QuestForge.Adapters.Tests`)

Per §Acceptance Criteria. Controller/kill-priority/engine-resolution tests in `QuestForge.Engine.Tests`; `FakeCombat`/`FakeGameStateProvider` contract tests in `QuestForge.Adapters.Tests`; schema round-trip in the schema test project.

### Task 7 — Paired questforge-tools validator change

**7.1** Mirror Task 1 schema edits into `questforge-tools/QuestForge.Schema/`.
**7.2** Add one structural rule in `QuestForge.Tools.Validator`: `combat` step with empty `KillEnemyDataIds` **and** `Spawn == overworldEnemies` is invalid (`structural/combat-overworld-needs-kill-ids`); and a **warning** when a `combat` step has no `expect` (`structural/combat-missing-expect` — can never complete). *(Flagged as a paired tools PR; part A's CI acceptance tests live in `questforge` against fakes. The validator rule has its own tests in `QuestForge.Tools.Validator.Tests` but is not a blocker for the engine PR.)*

---

## Validation rule table (paired questforge-tools change — Task 7)

| Rule | Code | Severity | Suppressed when |
|---|---|---|---|
| `combat` with `Spawn == overworldEnemies` requires non-empty `KillEnemyDataIds` | `structural/combat-overworld-needs-kill-ids` | Error | — |
| `combat` step has no `expect` | `structural/combat-missing-expect` | Warning | — |
| `KillEnemyDataIds` entries are non-zero | `structural/combat-kill-id-zero` | Error | — |

*(The old `CombatTarget.Kind` had no validator rule today — confirmed by grep, no `nearestHostile`/`specificNpc` strings in `QuestForge.Tools.Validator` — so nothing is being removed validator-side; these are net-new rules for the new fields.)*

---

## Given-When-Then specifications

### G1 — Kill-priority (pure `KillPriority`, no async)

- **KP-happy:** Given three hostiles — A `{IsTargetingPlayer:true, dist 20}`, B `{HasQuestMarker:true, dist 5}`, C `{DataId∈killIds, dist 2}` — with `killIds={C.DataId}`, `Spawn=autoOnEnterArea`. Then `SelectTarget` returns **A** (aggro +150 beats quest-marker +100 and kill-set +90 regardless of distance).
- **KP-enmity-over-marker:** Given B `{OnPlayerEnmityList:true}` (+125) and C `{HasQuestMarker:true}` (+100), no aggro. Then returns **B**.
- **KP-distance-tiebreak:** Given two hostiles with **equal score** (both only `DataId∈killIds`, +90), one at dist 10 and one at dist 3. Then returns the **dist-3** actor.
- **KP-actorid-tiebreak:** Given two hostiles, equal score **and** equal distance, `ActorId` 7 and 4. Then returns `ActorId 4` (lowest id — final deterministic tie-break).
- **KP-filter-dead:** Given the only kill-set match has `IsDead:true`. Then returns **null**.
- **KP-filter-untargetable:** Given the only kill-set match has `IsTargetable:false`. Then returns **null**.
- **KP-overworld-filters-non-killset:** Given `Spawn=overworldEnemies`, `killIds={100}`, actors with DataIds `{100, 200}`. Then only the `100` actor is eligible (returns it); the `200` actor is never selected.
- **KP-auto-empty-killset-all-eligible:** Given `Spawn=autoOnEnterArea`, `killIds={}` (empty), two targetable hostiles. Then returns the higher-priority/nearer one (all hostiles eligible — the "clear-the-room" case).
- **KP-empty:** Given no actors. Then returns **null**.

### G2 — Combat controller against fakes (`CombatController` + `FakeGameStateProvider` + `FakeCombat`)

- **CC-sets-target:** Given a fake with one eligible hostile `ActorId 9 / DataId 100`, `step.KillEnemyDataIds={100}`. When `Decide` is called once, Then it returns `CombatDecision(Target: {Id 9, DataId 100}, RotationShouldRun: true, ...)` AND `FakeCombat.RecordedTargets` contains exactly one `SetTarget(ActorId 9)`.
- **CC-no-redundant-settarget:** Given the same fake, call `Decide` **twice** with the world unchanged. Then `FakeCombat.RecordedTargets` contains **one** `SetTarget` (the controller does not re-issue `SetTarget` for an unchanged current target).
- **CC-retarget-on-death:** Given hostile X selected on tick 1; before tick 2 the fake marks X `IsDead:true` and adds Y (eligible). When `Decide` runs tick 2, Then it `SetTarget(Y)` and returns Y.
- **CC-clears-when-empty:** Given X selected; before tick 2 the fake clears all hostiles. When `Decide` runs, Then it returns `CombatDecision(Target: null, RotationShouldRun: false, ...)` AND `FakeCombat.RecordedTargets`/`RecordedRotation` shows a `ClearTarget`.
- **CC-fail-safe-on-read-failure:** Given `FakeGameStateProvider` scripted to fail `GetHostileActors`. When `Decide` runs, Then no exception; returns `CombatDecision(null, RotationShouldRun:false, ...)` (idle).
- **CC-reset:** After selecting X, call `Reset()`, then `Decide` again with the same world — Then a fresh `SetTarget(X)` is issued (state was cleared).

### G3 — Engine `CombatStep` resolution (full engine, `EngineTestHarness`)

- **EC-navigate-first:** Given a quest with one `combat` step having `Location` at `(100,0,100)`, player at `(0,0,0)`, `StopDistance` default. When `Tick` runs, Then the action is `EngineAction.Navigate` to `(100,0,100)` (controller **not** consulted — `FakeGameStateProvider.RecordedReads` contains **no** `GetHostileActors` this tick). Pins navigate-first.
- **EC-engage-when-in-range:** Given the same step, player already at `(100,0,100)`, one eligible hostile scripted. When `Tick` runs, Then the action is `EngineAction.Engage` with `Target.DataId == killId` AND `RecordedReads` now contains `GetHostileActors`.
- **EC-engage-null-target:** Given player in range but **no** eligible hostile scripted, `expect` unmet. When `Tick` runs, Then the action is `EngineAction.Engage(step, null)` (engine still selects the combat step; controller found nothing). No exception.
- **EC-not-supported-removed:** Given a `combat` step, Then `ResolveActionForStep` no longer throws `NotSupportedException` for `CombatStep` (it is routed through the async arm).

### G4 — Completion via `expect` (full engine)

- **EX-questvariable-gates-completion:** Given a 1-sequence quest with one `combat` step `expect: "questVariable(66104, 0) >= 3"`, player in range, eligible hostile present. Tick 1: `FakeQuestState.SetQuestVariables(66104, [0,0,0,0,0,0])` → action `Engage` (expect false). Then set `[3,0,0,0,0,0]`; Tick 2 → the step is **confirmed-and-skipped** and the engine emits `Wait`/`Done` (no further `Engage`). Pins completion-via-`questVariable`.
- **EX-questflag-completion:** Same shape with `expect: "questFlag(66104, 3)"`; flipping the flag bit confirms the step. (Mirrors EX-1 with a flag predicate.)
- **EX-completed-no-engage:** Given the `combat` step's `expect` already true on tick 1. Then the engine never emits `Engage` and never calls `GetHostileActors` (the confirm-and-skip path short-circuits before the combat arm) — `RecordedReads` has no `GetHostileActors`. Pins step-gating at the confirmed boundary.
- **EX-reset-on-advance:** Given a quest where after the combat step the sequence advances; assert `controller.Reset()` was invoked (observable via a fresh `SetTarget` if the same combat step were re-entered, or via a controller spy in the engine test). *(Tester may use a small seam: expose `CombatController` on the engine for test inspection, or assert via `FakeCombat` call ordering.)*

### G5 — `FakeCombat` / `FakeGameStateProvider` contract (`QuestForge.Adapters.Tests`)

- **FK-settarget-records:** `FakeCombat.SetTarget(ActorId 5)` then assert `RecordedTargets` has one entry with `ActorId 5`.
- **FK-rotation-availability:** `SetRotationModuleAvailable(false)` → `IsRotationModuleAvailable` returns `Result.Ok(false)`; default true.
- **FK-start-stop-records:** `StartRotation`/`StopRotation` each record into `RecordedRotation`.
- **FK-hostiles-radius-filter:** Add two hostiles at dist 5 and 50; `GetHostileActors(10)` returns only the dist-5 actor; `RecordedReads` contains `GetHostileActors`.
- **FK-hostiles-empty-default:** With none added, `GetHostileActors(100)` returns an empty list (not null), success.

### G6 — Schema round-trip (schema test project)

- **SC-roundtrip:** Serialize then deserialize a `CombatStep { KillEnemyDataIds=[100,200], Spawn=OverworldEnemies, Location=<npcloc>, Expect=questVariable(...) }` via `QuestForgeJsonContext.QuestFileOptions`; assert all fields survive and `type` discriminator is `"combat"`.
- **SC-spawn-enum-camelcase:** Assert `Spawn=AutoOnEnterArea` serializes as `"autoOnEnterArea"` (string-enum camelCase) and round-trips.
- **SC-defaults:** Deserialize `{ "type":"combat", "id":"x" }` (minimal) → `KillEnemyDataIds=[]`, `Spawn=AutoOnEnterArea`, `Location=null`. (Defaults hold.)
- **SC-no-combattarget:** Assert the `CombatTarget` type no longer exists / a JSON with the old `{"target":{"kind":"nearestHostile"}}` shape **does not** populate any property (silently ignored or, preferably, the old field is simply gone). *(Builder: confirm old `target` key is dropped, not mapped.)*

### G7 — Fixture non-starvation (run existing replay fixtures)

- **NS-existing-fixtures-green:** Run the parametric engine replay fixtures (`simple-linear-acceptance`, `with-attunement`). Then **both pass unchanged** — neither contains a `combat` step, so `GetHostileActors` is never called during their replay, so no `(GetHostileActors, *)` starvation. **No re-record required.** This is the explicit payoff of step-gating (D6).
- **NS-no-common-path-read:** A unit assertion on a **non-combat** step (e.g. a `talk` step quest): after a `Tick`, `FakeGameStateProvider.RecordedReads` contains **no** `GetHostileActors` entry. Pins that the combat read never leaked into the common per-tick prelude.

---

## Implementation order

**Phase A — Schema (both repos), 0.5 day.** Task 1 + Task 7.1 (mirror). Write SC-* round-trip tests (G6) first; make them green. Delete `CombatTarget`. **Done before B.**

**Phase B — Adapters + fakes, 0.5 day.** Task 2 + Task 3. Write FK-* contract tests (G5); make them green. Update `WrathComboAdapter`/`DalamudGameStateProvider` stubs so the plugin compiles. **Done before C.**

**Phase C — Controller + kill-priority, 1 day.** Task 4. Write KP-* (G1) then CC-* (G2); make them green. Pure unit work, no engine wiring yet. **Done before D.**

**Phase D — Engine action + resolution arm, 1 day.** Task 5. Write EC-* (G3), EX-* (G4), NS-* (G7); make them green. Verify NS-existing-fixtures-green (no re-record). **Done before E.**

**Phase E — Validator (paired, parallel-safe), 0.5 day.** Task 7.2 in questforge-tools. Validator rule tests. Independent of D; can land separately.

---

## Done criteria

1. A `combat` step with a `Location` resolves to `Navigate` while the player is out of range, then to `EngineAction.Engage` once in range — verified in `QuestForge.Engine.Tests` against fakes, **no game** (EC-navigate-first, EC-engage-when-in-range).
2. The `CombatController` selects a target by the kill-priority ordering (aggro > enmity > quest-marker > kill-set; nearest then lowest-ActorId tie-break), issues `ICombat.SetTarget` only on change, re-targets when the current target dies, and clears the target when none remain — all against `FakeGameStateProvider`/`FakeCombat` (G1, G2).
3. A combat step completes **only** via its `expect` predicate, including a `questVariable(...)`-gated case; before `expect` is met the engine emits `Engage`, and the tick `expect` becomes true the step confirms-and-skips and the engine stops engaging (EX-questvariable-gates-completion, EX-questflag-completion).
4. `ICombat` is reshaped to a rotation-module + targeting surface (`IsRotationModuleAvailable`/`StartRotation`/`StopRotation`/`SetTarget`/`ClearTarget`), the old `EngageTarget`/`EngageNearestHostile`/`Disengage`/plugin-info members are gone, and `FakeCombat` implements the new surface with call logs (G5, FK-*).
5. `IGameStateProvider.GetHostileActors` exists with the `HostileActor` shape (data-id, targetability, aggro, enmity, quest-marker), and `FakeGameStateProvider` can script it (FK-hostiles-*).
6. The new combat read is **step-gated**: a non-combat step tick performs **no** `GetHostileActors` read, and the two existing replay fixtures (`simple-linear-acceptance`, `with-attunement`) **stay green with no re-record** (NS-existing-fixtures-green, NS-no-common-path-read, EX-completed-no-engage).
7. `CombatStep` serializes/round-trips with `{KillEnemyDataIds, Spawn, Location, expect}`; `CombatTarget` is deleted; the schema mirror in questforge-tools matches (G6, SC-*).
8. (Paired) The questforge-tools validator flags `overworldEnemies` without `KillEnemyDataIds`, zero kill-ids, and warns on a combat step with no `expect` — with its own tests (Task 7).

---

## Exclusions (part B / later — do NOT design or build here)

- **WrathCombo lease IPC** (`RegisterForLeaseWithCallback`, `SetAutoRotationState`, `DPSRotationMode.Manual`, callback revocation) — the real `ICombat` rotation-module impl. Part A ships a compiling stub only.
- **Real `IObjectTable` scan + `TargetManager` writes** — the Dalamud `DalamudGameStateProvider.GetHostileActors` and `WrathComboAdapter.SetTarget` impls. Part A ships stubs.
- **Death recovery / `InstanceKind` routing (#63)** — open-world return-to-aetheryte, dungeon delegation, SPD wait. The combat controller in part A does not handle death; `IsPlayerDead`/recovery ladder integration is part B and depends on #63.
- **RSR / BossMod rotation modules** — additional `ICombat` impls behind the same interface.
- **Triggered spawn types** — `AfterInteraction` / `AfterItemUse` / `AfterAction` / `AfterEmote` / FATE. v1 ships only `autoOnEnterArea` and `overworldEnemies`.
- **`ComplexCombatData` multi-stage fights** — wave/phase modeling beyond a single kill-set.
- **Rotation lease lifecycle in the host** — `StartRotation`/`StopRotation` around `Engage` in `EngineHost.DispatchAction`. Part A defines the interface methods and `FakeCombat` records them, but wiring them into the live dispatch loop is part B (needs the real lease).
- **Combat trace capture** — no `RecordingCombat` proxy; the `Engage` action and `SetTarget` acts are not recorded in traces in part A (D-trace). Deferred to part B with the Dalamud impl.
- **Authoring inference** of combat steps (`StepInferenceEngine`) and quest-data authoring ergonomics for enemy sets.
- **Extracting `UseAction*` off `ICombat`** — left in place for part A to avoid `use-action` churn (D2).

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Given-When-Then specifications.
- Happy paths: 10 scenarios (KP-happy, KP-overworld-filters-non-killset, KP-auto-empty-killset-all-eligible, CC-sets-target, EC-navigate-first, EC-engage-when-in-range, EX-questvariable-gates-completion, FK-settarget-records, SC-roundtrip, NS-existing-fixtures-green)
- Edge cases: 16 scenarios (KP-enmity-over-marker, KP-distance-tiebreak, KP-actorid-tiebreak, KP-empty, CC-no-redundant-settarget, CC-retarget-on-death, CC-clears-when-empty, CC-reset, EC-engage-null-target, EC-not-supported-removed, EX-questflag-completion, EX-reset-on-advance, FK-rotation-availability, FK-start-stop-records, FK-hostiles-radius-filter, SC-spawn-enum-camelcase, SC-defaults, NS-no-common-path-read) *(count includes the two SC defaults variants)*
- Error/fail-open cases: 4 scenarios (KP-filter-dead, KP-filter-untargetable, CC-fail-safe-on-read-failure, EX-completed-no-engage)
- Expected total: ~34 tests — ~9 in `QuestForge.Engine.Tests/Combat` (KillPriority), ~6 in `QuestForge.Engine.Tests/Combat` (CombatController), ~7 in `QuestForge.Engine.Tests` (engine resolution + completion + non-starvation), ~5 in `QuestForge.Adapters.Tests` (FakeCombat/FakeGameStateProvider contract), ~4 in the schema test project (round-trip), plus the paired questforge-tools validator tests (Task 7, separate suite).
