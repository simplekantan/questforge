# Combat Step — Design Plan (Issue #6)

**Status:** design / scoping — not yet ready for test creation
**Related:** issue #6 (combat step), issue #63 (InstanceKind classifier — death-routing dependency), `docs/ADAPTERS.md §8`, `docs/SCHEMA.md` (combat step, `CombatTarget`), `docs/DESIGN.md` (death recovery), `CLAUDE.md` (three independent failure counters)
**Reference implementation:** Questionable (local clone at `../Questionable`) — a mature FFXIV quest-automation plugin that solves this exact problem. WrathCombo IPC (local clone at `../WrathCombo`).

This plan captures the research behind issue #6 and the design revisions it forces. It is deliberately broader than the one-line issue: combat is **deceptively large** because the targeting/movement/completion logic is ours, not the combat plugin's.

---

## 1. Problem statement

A `combat` step must: get the player to where the target(s) spawn, target the correct enemy/enemies, hand rotation off to a combat plugin (WrathCombo / RSR / BossMod), detect completion, recover from death, and emit replayable traces. The current schema (`CombatStep { CombatTarget Target }`, `CombatTarget(Kind, Radius?, NpcId?)`) and `ICombat` interface (`EngageTarget`/`EngageNearestHostile`/…) under-model the problem.

Out of scope for issue #6 (separate, later): authoring-mode *inference* of combat steps (`StepInferenceEngine`), and how quest *data authors* express "enemies to defeat" ergonomically.

---

## 2. Reference model — how Questionable does it

### 2.1 The combat plugin is ONLY a rotation executor (behind a lease)

`Questionable/Controller/CombatModules/ICombatModule.cs` — the plugin abstraction is tiny:
`CanHandleFight(combatData)`, `Start(combatData)`, `Stop()`, `Update(IGameObject nextTarget)`, `CanAttack(IBattleNpc)`. WrathCombo, RSR, and BossMod are interchangeable modules behind it.

`WrathComboModule.cs` IPC flow:
- `RegisterForLeaseWithCallback("Questionable", "Questionable", prefix)` → lease `Guid`.
- `SetAutoRotationState(lease)` — turn auto-rotation on.
- `SetCurrentJobAutoRotationReady(lease)` — make the current job rotation-ready.
- `SetAutoRotationConfigState(lease, option, value)` — configure; notably **`DPSRotationMode.Manual`** (WrathCombo attacks the *manually-set* target — it does NOT pick targets), plus IncludeNPCs, OnlyAttackInCombat=false, AutoRez, etc.
- `ReleaseControl(lease)` on stop; a callback IPC (`Questionable$Wrath.WrathComboCallback`) signals lease revocation.

**Takeaway:** WrathCombo runs the rotation on whatever target *we* hard-set. It owns no targeting. (RSR/BossMod modules can auto-target, but Questionable still drives targeting itself for determinism.)

### 2.2 Targeting / movement / kill-priority is the controller's job (ours)

`Questionable/Controller/CombatController.cs` owns everything that matters:
- scans `IObjectTable` for battle NPCs;
- **kill priority** (`GetKillPriority`): enemies targeting us `+150`, on our enmity/Hater list `+125`, quest-marker enemies (`NamePlateIconId != 0`), `KillEnemyDataIds` match `90`, complex-combat-data match `100`; filters dead/untargetable/FATE/wrong-kind;
- sets `_targetManager.Target` itself and **moves into range** (`MoveToTarget`: melee ~2.9, ranged/healer ~20, navmesh when far, line-of-sight raycast);
- `EStatus` = `NotStarted | InCombat | Moving | Complete`.

### 2.3 Completion = quest-work variables (+ "was in combat → not in combat")

`Combat.cs` `HandleCombat.Update`: run `combatController.Update()` until `EStatus.Complete` (combat ended), **then**:
- if the step has `CompletionQuestVariablesFlags`, complete only when `QuestWorkUtils.MatchesQuestWork(flags, questProgressInfo)` — i.e. the live quest **work variables (V0–V5)** match the expected pattern; otherwise keep fighting (more enemies/waves);
- if it's the last step in the sequence, wait indefinitely for the game to advance the sequence.

**This confirms postcondition-driven completion** and makes the **`questVariable` predicate a hard prerequisite** for kill-count quests (the kill count lives in quest variables, not flags).

### 2.4 Spawn/trigger types are richer than a target "kind"

`EEnemySpawnType`: `AfterInteraction`, `AfterItemUse`, `AfterAction`, `AfterEmote`, `AutoOnEnterArea`, `OverworldEnemies`, `FateEnemies`, `FinishCombatIfAny`. Many fights are *triggered* (interact an object / use an item / use an action / emote) and the enemies spawn afterward. `OverworldEnemies` means "kill N of possibly-many free-roaming mobs"; the auto/normal types mean "a fixed set." Our 3-value `CombatTarget.Kind` (`nearestHostile | specificNpc | wave`) does not model triggers or the kill-set.

---

## 3. Design revisions this forces in QuestForge

### 3.1 Reshape `ICombat` — split rotation-module from combat-controller

Today `ICombat` (`QuestForge.Adapters/Combat/ICombat.cs`) conflates targeting + rotation (`EngageTarget(NpcId)`, `EngageNearestHostile`). Realign to mirror Questionable:
- A thin **rotation-module** abstraction (lease-based `Start/Stop/Update/CanAttack` + availability) — the Dalamud impl wraps WrathCombo IPC (later RSR/BossMod).
- An engine/adapter-side **combat controller** that owns object-table scanning, kill-priority, target selection, and per-target movement. The engine purity boundary means the controller's *reads* go through adapter interfaces (`IGameStateProvider` enemy/target queries) so it stays testable against fakes; the actual ClientStructs object-table scan + `TargetManager` writes live in the Dalamud layer.

This is the bulk of the work and the reason the issue is deceptively large.

### 3.2 Realign `CombatStep` / `CombatTarget` schema

The `(Kind: nearest/specific/wave, Radius, NpcId)` model is too coarse. Move toward the proven shape:
- **`KillEnemyDataIds`** — a set of BNpc base data-IDs to defeat (generalizes `specificNpc`).
- an optional **spawn/trigger type** (v1: `autoOnEnterArea` / `overworldEnemies`; defer triggered types).
- optional **complex/multi-stage** conditions (defer to a later pass).
- **completion via the step's `expect` predicate** (`questVariable(...)` / `questFlag` / `questSequence`) rather than a target "kind".
- **`Location`** (new) on `CombatStep` — coarse "get to the arena" navigation, mirroring `AttunementStep.Location` and the existing implied-navigation path; per-target movement during the fight is the controller's job. *(No schema version bump needed — early development.)*

`wave` ("engages all hostiles until none remain", `SCHEMA.md:575`) is AoE-clear, distinct from defeat-N (which is `KillEnemyDataIds` + an `expect` count). Reconcile/retire the `Kind` enum in this pass.

### 3.3 New `EngineAction` + dispatch wiring

`EngineAction` (`QuestForge.Engine/EngineAction.cs`) has no combat action. Add a combat action (e.g. `Engage`/combat-tick) and a `EngineHost.DispatchAction` case that drives the combat controller + rotation module. `ICombat` is already injected into `QuestEngine` (`_combat`, ctor) — currently unused. `CombatStep` currently hits `_ => throw NotSupportedException` in `ResolveActionForStep`.

### 3.4 Completion, trace determinism, and the fixture-starvation trap

- Completion is a **permanent predicate** (`questVariable`/`questFlag`/`questSequence`) — never "target is dead" (transient). Aligns with §1.0 / issue #33.
- Combat-state reads the controller needs (in-combat? player dead? nearby hostiles?) must be **step-gated** — fired only while the active step is a `CombatStep` — so they never enter the common per-tick path and starve existing non-combat replay fixtures (the exact cascade from the quest-variables work; see `docs/QUEST_VARIABLES_TRACE_PLAN.md`).
- Under **replay**, combat is non-deterministic in real time, so the *completion signal* must come from recorded observations (quest variables / sequence / flags, which already replay) and the engage action is effectively a no-op. The combat controller's live targeting is not replayed.
- The engage action must be captured in the trace (there is no `RecordingCombat` proxy today — decide how combat actions land in the trace during the realignment pass).

### 3.5 Death recovery & failure counters

Per `DESIGN.md` and `CLAUDE.md`, death is **context-routed by `InstanceKind`**:
- open world → accept return to home aetheryte, re-plan from new position, re-navigate to `Location`, re-engage;
- dungeon/trial/raid → delegated plugin handles it; **never increments any counter**;
- SPD → note death, wait for instance exit, check postcondition.

**Dependency:** correct routing needs the real `InstanceKind` classifier — currently the stub filed as **issue #63** (`ClassifyInstanceKind → Other`). Death-routing is unreliable until #63 lands.

---

## 4. Dependency chain & sequencing

1. **PR 1 — `questVariable` predicate (prerequisite, separate, small).** Unblocks combat completion and is independently useful. See §5.
2. **PR 2 — issue #6 part A: `ICombat`/schema realignment + combat controller (CI, vs `FakeCombat`).** The engine-side controller (targeting/kill-priority/movement loop, completion gating) and the reshaped interface/schema, fully testable against fakes — no game required.
3. **PR 3 — issue #6 part B: WrathCombo IPC + death routing (in-game-verified).** Wire `WrathComboAdapter` (currently a Phase 6 stub) to the lease/auto-rotation IPC; death routing via `InstanceKind` (needs #63).
4. **Later:** RSR/BossMod rotation modules; triggered spawn types (`AfterInteraction/ItemUse/Action/Emote`, FATE); `ComplexCombatData` multi-stage fights; authoring inference; quest-data ergonomics for enemy sets.

---

## 5. PR 1 — `questVariable` predicate (sketch; full architect pass at implementation time)

- **Registry** (`questforge-tools/QuestForge.Predicates/FunctionRegistry.cs`): add `questVariable(Int questId, Int index) -> Int` — returns the byte value of work-variable `V[index]` (0–5), used in comparisons like `questVariable(66104, 0) >= 3`. Returns `Int` (mirrors `questSequence`), composes with the existing comparison operators.
- **Parser / checker**: should follow automatically from the registry (validate `index` bounds 0–5 in `PredicateChecker`).
- **Engine evaluation**: the predicate evaluator reads `IQuestState.GetQuestVariables(questId)` (already implemented + trace-emitted) and indexes it. Confirm whether evaluating this in the engine adds a per-tick read on a non-combat path — if so, gate it like other predicate reads to avoid fixture starvation.
- **Tests**: registry/parser/checker (questforge-tools) + engine evaluation against `FakeQuestState` scripted variables.

This is the immediate next TDD cycle (architect → tester → builder → reviewer).

---

## 6. v1 cut for issue #6 (keep it tractable)

**In:** `autoOnEnterArea` + `overworldEnemies` spawn types; `KillEnemyDataIds` targeting; completion via `expect: questVariable(...)`; `Location` coarse nav + controller per-target movement; WrathCombo rotation module; step-gated combat reads (no fixture starvation).

**Out (deferred):** triggered spawn types (`AfterInteraction/ItemUse/Action/Emote`); FATE; `ComplexCombatData` multi-stage; RSR/BossMod modules; death routing beyond open-world (pending #63); authoring inference; quest-data authoring ergonomics.

---

## 7. Open decisions

**Resolved (this scoping pass):**
1. Completion model → **postcondition-driven** (engage; wait on the permanent `expect`; re-engage on death/disengage). Validated by Questionable.
2. `Location` → **added to `CombatStep`** (no version bump; early dev).
3. Targeting → **always ours**; the plugin is a rotation executor behind a lease. (Corrects the earlier assumption that plugin choice changes who targets.)
4. `wave` → AoE clear-the-room, not defeat-N. Realign the schema away from the 3-`Kind` enum toward `KillEnemyDataIds` + `expect`.

**Open (decide during PR 2 design):**
- Exact reshaped `ICombat` / combat-controller boundary (what's adapter-interface vs Dalamud-only).
- Final `CombatStep`/`CombatTarget` schema shape (kill-set + spawn type + how `expect` expresses completion).
- New `EngineAction` shape and how combat actions are recorded in traces.
