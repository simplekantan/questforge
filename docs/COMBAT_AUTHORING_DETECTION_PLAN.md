# Combat-Step Authoring Detection Plan

**Status:** ready for test creation
**Input docs:** docs/SCHEMA.md (CombatStep, CombatSpawn), docs/AUTHORING.md, docs/TRACE_FORMAT.md, CLAUDE.md (clean-room, Dalamud-free observer, no fixture starvation)
**Output:** the author tool reconstructs a `CombatStep` (with `KillEnemyDataIds` + `expect`) for a quest that requires killing enemies whose deaths bump a quest variable — proven against Quest 65847 (Way of the Marauder). CI behaviour change: new live-path and offline-path inference tests go green; no engine fixture re-record cascade.
**Target quest:** 65847 — Way of the Marauder (closes a GAME-* test item).

---

## Dependency graph

Two repos. The schema is already in place (`CombatStep`/`CombatSpawn` exist in both `QuestForge.Schema`); no schema change is required, which removes the usual "mirror the schema" coupling.

```
questforge (this repo)
  QuestForge.Plugin.Tracing
    ├── ICombatProbe                 ← NEW probe interface (Dalamud-free contract)
    └── UIObserver.PollCombat        ← NEW heartbeat poller; emits EnemyKilled + InCombat observations
  QuestForge.Engine/Authoring
    ├── GameStateSnapshot            ← + combat fields (recent kills, kill-correlated targets, in-combat)
    ├── SnapshotAggregator           ← + OnEnemyKilled / OnInCombatChanged / kill-to-variable correlation
    ├── StepInferenceEngine          ← + Rule 2.2 (combat) BEFORE Rule 3 (sequence) / Rule 5 / Rule 7
    └── StepFactory                  ← + "combat" case → CombatStep
  QuestForge.Adapters.Dalamud
    └── DalamudCombatProbe           ← NEW concrete probe (in-game validation only)

questforge-tools
  QuestForge.Tools.Trace
    ├── Parsing/TraceEventParser     ← unchanged (EnemyKilled flows through the generic "method" branch)
    ├── SnapshotState                ← + EnemyKilled / InCombat handling + correlation (mirror of live)
    └── Quest/TraceToQuestExtractor  ← + emit CombatStep when inference returns StepType "combat"
```

**Build order:** schema (already done) → ObservationEvent contract + probe + UIObserver emission (Slice 1) → live inference (Slice 2) → offline extractor (Slice 3) → Dalamud probe + in-game validation (Slice 4).

---

## Architectural decisions (read before coding)

### D1 — UIObserver stays dumb: emit raw kills, correlate in the consumers

**Decision.** `UIObserver` emits two raw observation methods (`EnemyKilled`, `InCombat`) and nothing else combat-related. The kill→variable correlation lives in BOTH consumers — `SnapshotAggregator` (live) and `SnapshotState` (offline) — mirroring the existing live/offline duplication for every other inference signal.

**Why.** The variable bump (`GetQuestVariables` / `OnQuestVariablesUpdated`) is *already* emitted on the observation channel by `PollQuestState`. If the observer correlated, the recorded trace would only contain the *result* (a synthesized "combat target" event), and the offline path could never re-derive it differently or fix correlation bugs without re-recording. Keeping raw signals in the trace means both paths replay the SAME inputs and the correlation algorithm is a pure, unit-testable function of the event stream — consistent with how `DialogueOptionSelected` + `GetQuestVariables` are already independent raw signals.

**Rejected alternative — correlate in the observer.** Produces a single "killForVariable" event. Rejected: bakes the timing window into recording time (un-tunable offline), couples the Dalamud-free observer to quest-variable semantics, and starves the offline path of the raw data it needs to be the source of regression truth.

**What breaks if violated.** If correlation moves into `UIObserver`, the offline extractor diverges permanently from the live path the moment the window constant changes, because old traces encode the old window.

### D2 — Correlation state: a recent-kills ring buffer + per-variable baseline, held by each consumer

**Decision.** Each consumer (`SnapshotAggregator`, `SnapshotState`) maintains:
- `RecentKills`: a time-ordered buffer of `(uint DataId, DateTimeOffset At)` for kills observed in the current correlation window.
- `_questVariableBaseline`: the `IReadOnlyList<byte>` last seen for the active quest (already tracked as `QuestVariables` — extend to keep the *previous* value so a delta can be computed).
- `KillCorrelatedTargets`: a `Dictionary<int /*varIndex*/, (HashSet<uint> DataIds, int FinalValue)>` accumulated across the recording window. This is the payload Rule 2.2 reads.

When a variable bump for index `idx` (new value > old value) is observed, every kill in `RecentKills` within the window is attributed to `idx`: its `DataId` is added to `KillCorrelatedTargets[idx].DataIds`, and `FinalValue` is set to the new variable value. A sequence advance (`QuestSequence` increase) with non-empty `RecentKills` is correlated identically under the synthetic index `SEQUENCE_INDEX = -1`.

**Concrete surface (engine side):**
```csharp
// GameStateSnapshot (non-positional appended fields)
public bool InCombat { get; init; }
public IReadOnlyDictionary<int, KillCorrelation>? KillCorrelatedTargets { get; init; }

public sealed record KillCorrelation(IReadOnlyList<uint> DataIds, int FinalValue);
public const int SequenceVariableIndex = -1; // a static on SnapshotAggregator / a shared const

// SnapshotAggregator new methods
public void OnEnemyKilled(uint dataId);          // pushes (dataId, _clock.UtcNow) into RecentKills
public void OnInCombatChanged(bool inCombat);    // sets InCombat; records combatStartedAt/position
// correlation runs inside OnQuestVariablesUpdated and OnQuestSequenceChanged
```

**Why a buffer keyed by time, not "last kill."** The 250 ms heartbeat means a death and its variable bump can land one or two polls apart, and the player may kill several mobs in a burst before the variable catches up (or a single AoE kills three at once → V0 goes 0→3). A buffer captures the burst; "last kill only" would attribute V0:0→3 to one mob and under-count the data-id set.

### D3 — Correlation timing window: ±2 heartbeats (500 ms) on observation timestamps

**Decision.** A variable bump at time `T` correlates to any kill with `At` in `[T - 500ms, T]`. Kills older than `T - 500ms` are evicted from `RecentKills` lazily at each push/correlation. The window is `CombatCorrelationWindow = TimeSpan.FromMilliseconds(500)` (two heartbeats). Make it a single named constant referenced by both repos' correlation code so they stay identical.

**Why 500 ms / backward-only.** Heartbeat cadence is 250 ms. A kill is observed on the poll *after* the mob disappears; the variable bump may be observed the same poll or the next. The kill therefore precedes or coincides with the bump — never follows it — so the window is backward-looking. Two heartbeats absorbs one extra poll of slack without risking attributing the NEXT objective's kills to THIS variable (objectives are seconds apart, not sub-second).

**Determinism.** Both consumers use observation timestamps (`ObservationEvent.At` offline; `IClock.UtcNow` captured at `OnEnemyKilled` live), never wall-clock at correlation time. The offline path is fully deterministic from the trace. The live path is deterministic given the injected `IClock` (tests use `FakeClock`).

**Worked example (single mob ×3, V0):**
```
t=0ms   InCombat true                          → InCombat=true, combatStartedAt=0, combatStartPos=P
t=250ms EnemyKilled dataId=347                  → RecentKills=[(347,250)]
t=250ms GetQuestVariables [1,0,0,0,0,0] (V0 1)  → bump idx0 0→1; correlate kills in [−250,250] → {347}; final=1
t=500ms EnemyKilled dataId=347                  → RecentKills=[(347,500)]   (250-entry evicted: 500−500=0)
t=500ms GetQuestVariables [2,0,0,0,0,0]         → bump idx0 1→2; correlate → {347}; final=2
t=900ms EnemyKilled dataId=347                  → RecentKills=[(347,900)]
t=900ms GetQuestVariables [3,0,0,0,0,0]         → bump idx0 2→3; correlate → {347}; final=3
=> KillCorrelatedTargets[0] = ({347}, 3)
```
Result: `KillEnemyDataIds=[347]`, `expect: questVariable(65847, 0) >= 3`.

**Worked example (AoE triple kill, one bump):**
```
t=250ms EnemyKilled 347; EnemyKilled 347; EnemyKilled 347   → RecentKills=[(347,250)×3]
t=250ms GetQuestVariables V0 0→3                              → correlate all 3 → DataIds {347}, final=3
=> KillEnemyDataIds=[347], expect questVariable(65847,0) >= 3
```
The set dedups data-ids; `FinalValue` comes from the variable, not the kill count, so it is correct even when N kills map to one bump or one kill maps to a +N bump.

### D4 — `expect` and index/quest resolution

**Decision.** The active quest is `before.ActiveQuest ?? after.ActiveQuest` (existing convention). The variable index is the key of the `KillCorrelatedTargets` entry. `expect` is derived per case:
- Single variable index `idx >= 0`: `questVariable(<questId>, <idx>) >= <FinalValue>`.
- Synthetic index `-1` (sequence advance with correlated kills, no variable moved): `questSequence(<questId>) >= <after.QuestSequence>`.
- Multiple correlated indices (rare; e.g. two distinct objectives in one window): pick the index with the largest `FinalValue` for the primary `expect`, emit the others as `and`-joined predicates, and set `Notes` describing the split so the author can split into two steps. `KillEnemyDataIds` is the union across indices (author edits if wrong).

**Why `>=` not `==`.** Matches existing sequence-advance convention (`questSequence(..) >= N`) and tolerates the engine arriving with the variable already partway bumped after a retry.

### D5 — `Spawn` inference: default `OverworldEnemies`, author-review

**Decision.** v1 always emits `Spawn = OverworldEnemies` and adds a `Notes` line: `"Spawn defaulted to overworldEnemies; change to autoOnEnterArea if this is an arena/instanced fight. Author review required."`

**Why.** The grounding requirement explicitly permits a default + review for v1. Distinguishing arena fights would require tracking whether the hostiles were present on zone entry (zone-change → immediate combat with a fixed set), which is fragile and out of scope. Way of the Marauder is overworld training-ground kills, so the default is correct for the target quest.

### D6 — `Location` inference: player position at combat start

**Decision.** `CombatStep.Location = new NpcLocation(NpcId: 0, Zone: <zone at combat start>, Position: <player position when InCombat went false→true>)`. The aggregator records `combatStartPos`/`combatStartZone` on the `false→true` transition in `OnInCombatChanged`. If no in-combat transition was captured (combat already active at window open), fall back to `after.Position` and note it.

**Why player position, not enemy position.** `Location` is "get to the arena" navigation (mirrors `AttunementStep.Location`). The player's standing position when the fight began is where the engine should navigate to before fighting. Enemy positions are not surfaced (probe only carries data-ids; see D7) and roam.

### D7 — `ICombatProbe`: enumerate hostiles each heartbeat; kill = tracked-then-gone

**Decision.** New Dalamud-free probe injected into `UIObserver` exactly like the existing probes:
```csharp
namespace QuestForge.Plugin.Tracing;

public interface ICombatProbe
{
    /// <summary>True when the local player is in combat (used to gate kill attribution).</summary>
    bool IsInCombat();

    /// <summary>
    /// Snapshot of currently-visible hostile BattleNpcs. Each entry is (live object id, BNpc base data-id).
    /// ObjectId is the transient table identity used to detect the alive→gone transition;
    /// DataId is the stable BNpc base id written to the trace and matched against KillEnemyDataIds.
    /// </summary>
    IReadOnlyList<(ulong ObjectId, uint DataId)> GetVisibleHostiles();
}
```

**Kill-detection algorithm in `UIObserver.PollCombat` (heartbeat poller).** Maintain `Dictionary<ulong, uint> _trackedHostiles` (ObjectId → DataId) across polls.
1. Read `IsInCombat()`. If it changed since last poll, emit `InCombat` observation and forward `OnInCombatChanged`.
2. Read `GetVisibleHostiles()` into a current map.
3. For each ObjectId in `_trackedHostiles` NOT in the current map: it was alive last poll and is now gone → emit `EnemyKilled` with its tracked `DataId`, forward `OnEnemyKilled(dataId)`.
4. Replace `_trackedHostiles` with the current map.
5. **Kill gate:** only treat a disappearance as a kill when `IsInCombat()` was true at the previous poll (so a mob despawning out of combat, or the player walking out of object-table range while not fighting, is not counted). Disappearances while out of combat update `_trackedHostiles` silently.

**Why ObjectId-keyed transition, not `IsDead`.** A dead BattleNpc frequently leaves the object table within one poll, so "tracked-then-gone" is more reliable than catching the brief `IsDead==true` window. `HostileActor` (engine adapter) exposes `IsDead`/`DataId` as reference for field semantics, but the authoring probe deliberately surfaces only `(ObjectId, DataId)` to stay minimal and Dalamud-free.

**Accepted false positive.** A hostile that leaves view by despawn/leash *during combat* counts as a kill. Mitigated by the variable-correlation heuristic (D1): an uncorrelated phantom kill never reaches `KillEnemyDataIds`. State this in `Notes` only if needed.

### D8 — Rule ordering: combat (Rule 2.2) runs BEFORE sequence/flag/talk rules

**Decision.** Insert combat detection as **Rule 2.2**, immediately after Rule 2.1 (foreign quest) and before Rule 2.3 (key item). Guard: fires only when `after.KillCorrelatedTargets` is non-empty. Because a kill that bumps a variable would otherwise be caught by Rule 3 (sequence advance) or Rule 5 (flags) or Rule 7 (NPC) and mis-emitted as a talk step, the combat rule must short-circuit first.

**Guard conditions (all required):**
- `after.KillCorrelatedTargets is { Count: > 0 }`.
- At least one correlated entry has a non-empty `DataIds` set.

**Why before Rule 3, not after.** A kill that bumps `questSequence` (kill-then-auto-advance objectives) would be swallowed by Rule 3 as a `talk` step. Placing combat first, and reusing the sequence value only as the `expect` fallback (index `-1`), keeps the typed step correct.

**Interaction with Rules 2.3/2.4 (key items).** If a window contains BOTH a kill-correlated variable bump AND a key-item delta (e.g. kill drops a key item), combat wins (it is earlier in the rule chain). Documented limitation: the key-item pickup is folded into the same step; author splits if needed. Acceptable for v1 and not relevant to Way of the Marauder.

---

## Task 1 — Observation channel: new methods + probe (Slice 1)

### 1.1 ObservationEvent methods

No new top-level event type (honors the settled requirement). Two new `ObservationEvent.Method` values written via the existing `WriteObservation` path:

| Method | Argument | Value | Emitted when |
|---|---|---|---|
| `EnemyKilled` | `null` | `{ "dataId": <uint> }` | a tracked hostile present last poll is gone this poll AND player was in combat last poll |
| `InCombat` | `null` | `{ "value": <bool> }` | `IsInCombat()` differs from last poll |

`InCombat` is written directly (event, not polled state — mirror the `InventoryChanged` direct-write pattern so dedup does not suppress a true→false→true sequence). `EnemyKilled` is also a direct write (each kill is a distinct event even for the same data-id).

### 1.2 `UIObserver` changes

- Add `private readonly ICombatProbe? _combatProbe;` constructor param (optional, last positional, matching `targetProbe` pattern).
- Add `private readonly Dictionary<ulong, uint> _trackedHostiles = new();` and `private bool _lastInCombat;`.
- Add `PollCombat()` to the **heartbeat** block in `OnFrameworkUpdate` (after `PollQuestState` so the variable bump for the same poll is forwarded to the aggregator before/around the kill — ordering within a single poll does not matter for correlation because the window is ±500ms, but keep kills emitted in `PollCombat`).
- `ResetHeartbeatState()` clears `_trackedHostiles` and resets `_lastInCombat = false` so a new authoring session re-detects combat cleanly.

### 1.3 `DalamudCombatProbe` (Slice 4, in-game only)

Implements `ICombatProbe` over the object table / condition flags. `IsInCombat()` from `ICondition[ConditionFlag.InCombat]`. `GetVisibleHostiles()` enumerates `IObjectTable` for `ObjectKind.BattleNpc` with a hostile status, projecting `(GameObjectId, DataId)`. No correlation logic here.

---

## Task 2 — Live path inference (Slice 2)

### 2.1 `GameStateSnapshot` additions
Appended non-positional fields (no churn to existing constructor call sites — same discipline as the existing comments in `GameStateSnapshot.cs`):
```csharp
public bool InCombat { get; init; }
public IReadOnlyDictionary<int, KillCorrelation>? KillCorrelatedTargets { get; init; }
public WorldPosition? CombatStartPosition { get; init; }
public int CombatStartZone { get; init; }
```
`KillCorrelation` (new record in `QuestForge.Engine.Authoring`):
```csharp
public sealed record KillCorrelation(IReadOnlyList<uint> DataIds, int FinalValue);
```

### 2.2 `SnapshotAggregator` additions
- `RecentKills` buffer `List<(uint DataId, DateTimeOffset At)>`; eviction older than `CombatCorrelationWindow` relative to the newest correlation point.
- `_killCorrelatedTargets` `Dictionary<int, (HashSet<uint> DataIds, int FinalValue)>`.
- `_prevQuestVariables` to compute per-index deltas (the existing `_questVariables` keeps current; add previous).
- `OnEnemyKilled(uint dataId)`: push `(dataId, _clock.UtcNow)`, evict stale.
- `OnInCombatChanged(bool inCombat)`: on `false→true`, set `_combatStartPos = _position`, `_combatStartZone = _zone.Value`; set `_inCombat`.
- Extend `OnQuestVariablesUpdated`: for each index where new > old, run correlation against `RecentKills` within window → merge into `_killCorrelatedTargets[idx]`.
- Extend `OnQuestSequenceChanged`: if `RecentKills` non-empty within window and the new sequence > old, correlate under index `SequenceVariableIndex (-1)` with `FinalValue = newSequence`.
- `Current` projects the dictionary into `KillCorrelatedTargets` (as `KillCorrelation` records), `InCombat`, `CombatStartPosition`, `CombatStartZone`.
- `ResetDeltas()` clears `RecentKills` and `_killCorrelatedTargets` (per-window signal; must not bleed into the next Record window). Does NOT clear `_prevQuestVariables` baseline (cross-window continuity needed to detect the next bump).

### 2.3 `StepInferenceEngine` — Rule 2.2
Insert after Rule 2.1, before Rule 2.3:
```csharp
// Rule 2.2: kill-correlated combat. A killed enemy enters KillEnemyDataIds only if its death
// coincided (within the correlation window) with a quest-variable bump or sequence advance.
if (after.KillCorrelatedTargets is { Count: > 0 } targets
    && targets.Values.Any(t => t.DataIds.Count > 0))
{
    // primary = entry with largest FinalValue
    var primary = targets.OrderByDescending(kv => kv.Value.FinalValue).First();
    var allDataIds = targets.Values.SelectMany(t => t.DataIds).Distinct().OrderBy(x => x).ToArray();

    string expect = primary.Key == SnapshotAggregator.SequenceVariableIndex
        ? $"questSequence({questIdValue}) >= {after.QuestSequence}"
        : $"questVariable({questIdValue}, {primary.Key}) >= {primary.Value.FinalValue}";

    // multi-index → and-join the rest, note the split
    string? notes = "Spawn defaulted to overworldEnemies; review. Location = player position at combat start.";
    if (targets.Count > 1) { /* append and-joined predicates + split note */ }

    return new InferenceResult(
        StepType: "combat",
        SuggestedStepId: $"defeat-{allDataIds[0]}",
        SuggestedExpect: expect,
        Confidence: Confidence.Medium,
        InferredFrom: InferredFrom.Combat,   // NEW enum member
        Notes: notes);
}
```
Add `Combat` to `InferredFrom`.

### 2.4 `StepFactory` — "combat" case
```csharp
"combat" => new CombatStep
{
    Id = stepId,
    Expect = expectValue,
    Zone = zoneStr,
    RequiredZone = zoneStr,
    KillEnemyDataIds = CombatDataIds(after),         // union of after.KillCorrelatedTargets[*].DataIds, sorted, distinct
    Spawn = CombatSpawn.OverworldEnemies,            // D5 default
    Location = new NpcLocation(
        NpcId: 0,
        Zone: after?.CombatStartZone ?? zone,
        Position: after?.CombatStartPosition is { } cp
            ? new Position3(cp.X, cp.Y, cp.Z)
            : playerPos)                              // D6 fallback
},
```

---

## Task 3 — Offline path (Slice 3)

`TraceEventParser` needs **no change**: `EnemyKilled` and `InCombat` have a `method` property and flow through the existing observation branch.

### 3.1 `SnapshotState` additions (mirror of D2/D3)
Add fields mirroring the aggregator: `_recentKills` (`List<(uint,DateTimeOffset)>`), `_killCorrelatedTargets`, `_prevQuestVariables`, `_inCombat`, `_combatStartPosition`, `_combatStartZone`. Add cases to `Apply`:
```csharp
case "InCombat":
    // value {"value": bool} OR bare bool
    // on false→true set _combatStartPosition=Position, _combatStartZone=Zone.Value
    return true;

case "EnemyKilled":
    // value {"dataId": uint}; push (dataId, ev.At) into _recentKills, evict stale (> window before ev.At)
    return true;
```
Correlation runs in the existing `GetQuestVariables` case (currently only sequence/flags are quest-scoped here; `SnapshotState` does not yet track V0–V5 — **add** a `GetQuestVariables` case that parses the array, computes per-index deltas vs `_prevQuestVariables`, and correlates each bump against `_recentKills` using `ev.At` as the correlation point) and in `GetQuestSequence` (sequence-advance correlation under index `-1`).

`ToSnapshot` projects `InCombat`, `KillCorrelatedTargets`, `CombatStartPosition`, `CombatStartZone`. `ResetPendingKeyItemDeltas` (window reset) also clears `_recentKills` and `_killCorrelatedTargets`, preserving `_prevQuestVariables`.

> **Note for the Builder:** offline `GetQuestVariables` parsing must accept the array shape `WriteObservation("GetQuestVariables", publicId, variables.ToList(), ...)` produces — a JSON array of numbers (`[1,0,0,0,0,0]`), possibly object-wrapped `{"value":[...]}`. Quest-ID filter via the existing `QuestArgMatches`.

### 3.2 `TraceToQuestExtractor` — emit CombatStep
The extractor builds steps from `submitted.ActionType`. Combat has no submitted action in observation-only traces. Add a branch driven by inference: when `inference.StepType == "combat"` (regardless of submitted action type, but combat windows typically have a `wait`/`navigate` or no action), build a `CombatStep` from `after.KillCorrelatedTargets`:
```csharp
if (inference.StepType == "combat")
{
    var dataIds = after.KillCorrelatedTargets is { } kc
        ? kc.Values.SelectMany(t => t.DataIds).Distinct().OrderBy(x => x).ToArray()
        : [];
    step = new CombatStep {
        Id = stepId,
        KillEnemyDataIds = dataIds,
        Spawn = CombatSpawn.OverworldEnemies,
        Location = new NpcLocation(0, after.CombatStartZone, ...),
        Expect = inference.SuggestedExpect is { Length: > 0 } e ? new PredicateExpect { Predicate = e } : null
    };
    todos.Add($"combat step {stepId}: review Spawn (defaulted overworldEnemies) and KillEnemyDataIds");
}
```
Because combat windows may carry a `wait` decision (the author standing still while fighting), ensure the `IsTerminalAction`/`wait` skip at the top of the loop does NOT skip a window whose inference is combat. **Builder guidance:** evaluate inference for `wait` windows too, OR have the live recorder emit a `combat` `ActionSubmittedEvent` during authoring. For Slice 3 (observation-only traces) the simplest correct approach: do not `continue` on `wait` if the post-window snapshot has non-empty `KillCorrelatedTargets`.

---

## Task 4 — Given-When-Then specifications

### 4.1 `SnapshotAggregator` correlation (engine, live) — `QuestForge.Engine.Tests`

**GWT-L1 single mob ×3 → one target, count 3.**
Given an aggregator for quest 65847, `InCombat` true at t=0. When `OnEnemyKilled(347)`+`OnQuestVariablesUpdated([1,0,0,0,0,0])` at t=250, again at t=500 (`[2,...]`), again at t=900 (`[3,...]`). Then `Current.KillCorrelatedTargets[0]` == `KillCorrelation([347], 3)`.

**GWT-L2 AoE triple kill, single bump.**
Given t=250: `OnEnemyKilled(347)`×3 then `OnQuestVariablesUpdated([3,0,...])`. Then `KillCorrelatedTargets[0]` == `([347], 3)` (data-id deduped, FinalValue from variable).

**GWT-L3 kill outside window is NOT correlated.**
Given `OnEnemyKilled(999)` at t=0, `OnQuestVariablesUpdated([1,0,...])` at t=600 (gap 600 > 500). Then `KillCorrelatedTargets` is empty (or has no index-0 entry). The 999 data-id never appears.

**GWT-L4 two data-ids both within window for same bump.**
Given t=250 `OnEnemyKilled(347)`, t=300 `OnEnemyKilled(348)`, t=500 `OnQuestVariablesUpdated([2,0,...])`. Then `KillCorrelatedTargets[0]` DataIds == `{347,348}`, FinalValue 2.

**GWT-L5 sequence-advance combat (no variable moved).**
Given kills at t=250/500, no variable change, `OnQuestSequenceChanged(65847, newSeq=3)` at t=600 with non-empty recent kills. Then `KillCorrelatedTargets[-1]` FinalValue 3.

**GWT-L6 ResetDeltas clears correlation, keeps variable baseline.**
Given a correlated target exists; When `ResetDeltas()`; Then `Current.KillCorrelatedTargets` is empty AND a subsequent `OnQuestVariablesUpdated([4,0,...])` computes the delta against the PREVIOUS baseline (3→4), not from zero.

**GWT-L7 InCombat false→true records start position/zone.**
Given `OnZoneChanged(zone=148, pos=(10,0,20))`, `OnPlayerMoved((10,0,20))`; When `OnInCombatChanged(true)`; Then `Current.CombatStartPosition == (10,0,20)` and `CombatStartZone == 148`.

### 4.2 `StepInferenceEngine` Rule 2.2 (engine) — `QuestForge.Engine.Tests`

**GWT-I1 combat beats sequence advance.**
Given before: seq 0, no targets. after: seq 3, `KillCorrelatedTargets[0]=([347],3)`. Then `Infer` returns `StepType=="combat"`, `SuggestedExpect=="questVariable(65847, 0) >= 3"`, `InferredFrom==Combat`. (NOT a talk step from Rule 3.)

**GWT-I2 sequence-only combat → questSequence expect.**
Given after: `KillCorrelatedTargets[-1]=([347],3)`, seq 3. Then expect == `"questSequence(65847) >= 3"`.

**GWT-I3 no correlation → falls through to existing rules.**
Given after: empty `KillCorrelatedTargets`, seq advanced 0→1. Then `StepType=="talk"` (Rule 3 unchanged).

**GWT-I4 multi-index split note.**
Given `KillCorrelatedTargets[0]=([347],3)`, `[1]=([400],1)`. Then `StepType=="combat"`, primary expect uses index 0 (larger FinalValue), `Notes` mentions index 1 / split, `SuggestedExpect` `and`-joins `questVariable(65847,1) >= 1`.

**GWT-I5 step id from lowest data-id.**
Given DataIds `{348,347}`. Then `SuggestedStepId == "defeat-347"`.

### 4.3 `StepFactory` (engine) — `QuestForge.Engine.Tests`

**GWT-F1 builds CombatStep with union data-ids + default spawn.**
Given snapshot with `KillCorrelatedTargets[0]=([347,348],3)`, `CombatStartZone=148`, `CombatStartPosition=(10,0,20)`; `Build("combat","defeat-347","questVariable(65847,0) >= 3", after)`. Then a `CombatStep` with `KillEnemyDataIds==[347,348]`, `Spawn==OverworldEnemies`, `Location.Zone==148`, `Location.Position==(10,0,20)`, `Expect` is `PredicateExpect`.

**GWT-F2 missing combat-start position falls back to player position.**
Given `CombatStartPosition==null`, `Position=(5,0,5)`. Then `Location.Position==(5,0,5)`.

### 4.4 `UIObserver.PollCombat` (plugin tracing) — `QuestForge.Engine.Tests` / plugin test project

Uses a `FakeCombatProbe : ICombatProbe` and `FakeClock`, the existing `FakeFrameworkDispatch`, capturing `TraceSession` writes + aggregator forwards.

**GWT-U1 tracked-then-gone in combat emits EnemyKilled.**
Given probe `IsInCombat()==true` and hostiles `[(obj=1,data=347)]` at poll 1; `[]` at poll 2. Then an `EnemyKilled` observation `{dataId:347}` is written and `aggregator.OnEnemyKilled(347)` is forwarded.

**GWT-U2 disappearance out of combat is NOT a kill.**
Given `IsInCombat()==false` at poll 1, hostiles `[(1,347)]`; poll 2 hostiles `[]`. Then no `EnemyKilled` is emitted; `_trackedHostiles` is updated silently.

**GWT-U3 InCombat transition emitted once per change.**
Given `IsInCombat()` false→true→true→false across four heartbeats. Then exactly two `InCombat` observations are written (`true` then `false`), not four.

**GWT-U4 ResetHeartbeatState clears tracking.**
Given a tracked hostile and `_lastInCombat==true`; When `ResetHeartbeatState()`; Then the next poll with the same hostile present does not emit a phantom kill and `InCombat` re-emits on the next true transition.

**GWT-U5 no combat probe → no combat emission (back-compat).**
Given `UIObserver` constructed without `ICombatProbe`. Then no combat observations are ever written; existing pollers unaffected.

### 4.5 `SnapshotState` (offline) — `QuestForge.Tools.Trace.Tests`

**GWT-O1 EnemyKilled + GetQuestVariables correlate.**
Given `SnapshotState(65847)`; Apply `InCombat {value:true}` at t0; `EnemyKilled {dataId:347}` at t=250; `GetQuestVariables` arg quest 65847 value `[1,0,0,0,0,0]` at t=250; repeat to `[3,...]` at t=900. Then `ToSnapshot(t).KillCorrelatedTargets[0]` == `([347],3)`. Each `Apply` returns true.

**GWT-O2 wrong-quest GetQuestVariables ignored.**
Given a kill at t=250 then `GetQuestVariables` arg quest 12345 value `[1,0,...]`. Then no correlation; `KillCorrelatedTargets` empty; `Apply` returns true (recognised).

**GWT-O3 EnemyKilled outside window not correlated.**
Mirror of GWT-L3 over the trace API.

**GWT-O4 InCombat false→true records start zone/position from current state.**
Given `GetPlayerZone {value:148}`, `GetPlayerPosition {x:10,y:0,z:20}`, then `InCombat {value:true}`. Then `CombatStartZone==148`, `CombatStartPosition==(10,0,20)`.

**GWT-O5 ResetPendingKeyItemDeltas clears combat window, keeps baseline.**
Mirror of GWT-L6.

### 4.6 `TraceToQuestExtractor` (offline) — `QuestForge.Tools.Trace.Tests`

**GWT-E1 end-to-end combat extraction.**
Given a synthetic trace: `run.start` quest 65847; observations `InCombat true`, three (`EnemyKilled 347` + `GetQuestVariables [k,0,...]`) pairs across the window, then a `wait`/`navigate` decision + completed in the combat window; `run.end`. Then `Extract` yields a `QuestSequence` whose step is a `CombatStep` with `KillEnemyDataIds==[347]`, `Spawn==OverworldEnemies`, `Expect` predicate `questVariable(65847, 0) >= 3`, and a TODO mentioning Spawn review.

**GWT-E2 combat window with `wait` action is not skipped.**
Given the combat window's only decision is `wait`. Then the extractor still emits the `CombatStep` (the `wait`-skip guard must not drop a window with non-empty `KillCorrelatedTargets`).

**GWT-E3 uncorrelated kills produce no CombatStep.**
Given kills with no in-window variable bump. Then no `CombatStep` is emitted (existing rules drive the window; combat is absent).

---

## Implementation order

**Slice 1 — Observation contract + probe + emission (CI-testable).**
1. Add `ICombatProbe` to `QuestForge.Plugin.Tracing`.
2. Add `EnemyKilled` / `InCombat` emission + `PollCombat` + tracking to `UIObserver`; wire `ResetHeartbeatState`.
3. Tests GWT-U1..U5 with `FakeCombatProbe`. Done-before-next: U1–U5 green.

**Slice 2 — Live inference (CI-testable). Depends on Slice 1 contract.**
1. `KillCorrelation` record + `GameStateSnapshot` fields.
2. `SnapshotAggregator` correlation (`OnEnemyKilled`/`OnInCombatChanged`, extend variables/sequence, `ResetDeltas`).
3. `InferredFrom.Combat`; `StepInferenceEngine` Rule 2.2; `StepFactory` "combat" case.
4. Tests GWT-L1..L7, I1..I5, F1..F2 green.

**Slice 3 — Offline extractor (CI-testable). Depends on Slices 1+2 (reuses `KillCorrelation`/snapshot fields).**
1. `SnapshotState` combat fields + `Apply` cases + `GetQuestVariables` parsing + correlation; `ToSnapshot`; window reset.
2. `TraceToQuestExtractor` combat branch + `wait`-skip guard fix.
3. Tests GWT-O1..O5, E1..E3 green.

**Slice 4 — Dalamud probe + in-game validation (requires game).**
1. `DalamudCombatProbe` over object table / condition flags.
2. Inject into `UIObserver` construction in `AuthoringHost`.
3. Author Quest 65847 in-game; confirm draft `CombatStep` `KillEnemyDataIds`/`expect`; replay the recorded trace through `qf-trace extract-quest`; confirm identical `CombatStep`. Closes the GAME-* item.

CI-testable: Slices 1–3. In-game: Slice 4 only.

---

## Done criteria

1. `UIObserver` with an `ICombatProbe` emits `EnemyKilled {dataId}` only for tracked-then-gone hostiles while in combat, and `InCombat {value}` only on transitions (GWT-U1..U5).
2. Live aggregator correlates kills to variable bumps within the 500 ms window and surfaces `KillCorrelatedTargets` (GWT-L1..L7).
3. `StepInferenceEngine` returns `StepType "combat"` before sequence/flag/talk rules when a correlated target exists, with the correct `questVariable`/`questSequence` expect (GWT-I1..I5).
4. `StepFactory` and `TraceToQuestExtractor` both build a `CombatStep` with union `KillEnemyDataIds`, `Spawn=OverworldEnemies`, player-start `Location`, and the predicate expect (GWT-F1..F2, GWT-E1..E3).
5. Replaying a recorded Way-of-the-Marauder trace through `qf-trace extract-quest` produces a `CombatStep` matching the live-authored draft (data-ids and expect identical).
6. No engine fixture re-record: combat observations are authoring-mode emit only; existing engine replay fixtures are unchanged and still green.

---

## Exclusions

- Kill-then-talk objectives where the kill moves no variable (settled accepted limitation — author hand-edits).
- Distinguishing `AutoOnEnterArea` from `OverworldEnemies` automatically (D5: default + review).
- Enemy world-position capture / per-mob spawn locations (`Location` uses player combat-start position only).
- Engine-side combat *execution* changes (this is authoring detection only; `CombatController` is untouched).
- Validator rules for `CombatStep` completeness (separate validator work if desired).
- Live multi-objective auto-splitting into multiple steps (single step + split `Notes`; author splits).
- New top-level trace event type (explicitly forbidden; observation channel only).

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Task 4.
- Happy paths: 9 scenarios (L1, L2, L4, L5, I1, I2, F1, O1, E1)
- Edge cases: 12 scenarios (L3, L6, L7, I4, I5, F2, U2, U3, U4, U5, O4, O5, E2)
- Error/negative cases: 4 scenarios (I3, U-none/U5 back-compat, O2, O3, E3)
- Expected total: ~26 tests — ~14 in QuestForge.Engine.Tests (live + inference + factory + observer) and ~12 in QuestForge.Tools.Trace.Tests (offline snapshot + extractor).
