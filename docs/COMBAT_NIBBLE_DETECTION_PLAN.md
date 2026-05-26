# Combat Nibble-Level Detection Plan (Piece 2 — rework of byte correlation)

**Status:** ready for test creation
**Input docs:** docs/COMBAT_AUTHORING_DETECTION_PLAN.md (the merged byte-correlation this REWORKS), docs/NIBBLE_PREDICATES_PLAN.md (the now-merged `questVariableLow`/`questVariableHigh` predicates this emits), docs/SCHEMA.md (CombatStep/CombatSpawn/NpcLocation — unchanged), docs/TRACE_FORMAT.md, CLAUDE.md (clean-room: study Questionable behaviour, never copy its source)
**Output:** the author tool reconstructs a `CombatStep` whose `expect` is a **nibble** predicate (`questVariableLow(activeQuest, idx) >= N` / `questVariableHigh(...)`), correlated to kills **bidirectionally** so the real in-game poll order (variable bump observed ~70 µs BEFORE its kill) no longer structurally misses the kill. **CI behaviour change:** the existing `Combat*Tests` (engine) and `SnapshotStateCombatTests` / `TraceToQuestExtractorCombatTests` (tools) are rewritten to nibble + bidirectional expectations and go green; the previously-passing byte tests (which fired kill-before-bump, the opposite of reality) are deleted. No engine replay-fixture re-record.
**Target quest:** 65847 — Way of the Marauder (sequence 2: three "defeat 3× of one NPC type" objectives, nibble-packed).
**Branch:** `feat/combat-nibble-detection` (questforge); paired branch in questforge-tools.

---

## Why this exists (the diagnosis, from in-game traces of 65847)

The merged byte-level design (COMBAT_AUTHORING_DETECTION_PLAN.md) is **broken for real quests**, proven in-game against 65847. Two independent root causes:

1. **The byte is the wrong granularity.** 65847's three objectives are nibble-packed (Questionable maps them, studied for behaviour only: NPC **347** → V0 **low** == 3; NPC **49** → V1 **low** == 3; NPC **338** → V1 **high** == 3). The raw byte V0 walked `0x00→0x01→0x02→0x13` — the "jump to 19" is really low-nibble `2→3` plus high-nibble `0→1` in the same write. And on **full** objective completion the sequence jumps `2 → 255` and **all variables reset to 0**. So a byte `expect` (`questVariable(65847,0) >= 3` against `0x13`) is satisfied by the wrong thing (high nibble), and a `questSequence` expect is unsatisfiable/indistinct (255 + reset). Only a **nibble** condition checked *while the objective is in progress* works.

2. **The correlation is structurally backward-only and bump-triggered.** In `UIObserver.OnFrameworkUpdate` the heartbeat runs `PollQuestState()` (emits `GetQuestVariables`) **before** `PollCombat()` (emits `EnemyKilled`); plus the variable bumps on the death frame while the kill is detected via corpse-despawn lag (same poll or a later one). So the kill lands **at or after** its bump. The merged `SnapshotAggregator` only *buffers* in `OnEnemyKilled` and only *correlates* inside `OnQuestVariablesUpdated` / `OnQuestSequenceChanged`, looking **backward** at `_recentKills`. A kill that arrives after its bump is never seen by that bump's correlation pass → the kill is structurally missed. The merged unit tests passed only because they fired the kill *before* the bump (e.g. CombatCorrelationAggregatorTests GWT-L1 calls `OnEnemyKilled(347)` then `OnQuestVariablesUpdated([1,…])`) — the opposite of in-game reality.

This Piece 2 fixes both: **nibble-level** correlation keyed by `(VarIndex, NibbleHalf)`, and **bidirectional/symmetric** correlation that triggers on whichever of (kill, nibble-bump) arrives, buffering both, over a symmetric ±window.

---

## Decisions already SETTLED upstream (design to these; do not re-litigate)

1. **Nibble-level correlation.** Correlate each kill to the nibble (low/high of a specific variable index) that *incremented*. Low changed iff `(prev & 0x0F) != (new & 0x0F)`; high iff `(prev>>4) != (new>>4)`; a single byte write can move both — attribute to the nibble that **increased** (see §D2 for the both-moved and decrement rules).
2. **One combat step per record.** Each authoring record captures ONE objective; the author fights one NPC type to that sub-objective's completion, then records → exactly ONE `CombatStep`. **Drop the multi-index auto-splitting** (merged Rule 2.2 `and`-join). Per-record scoping makes the dominant nibble↔DataId signal clean.
3. **Bidirectional / symmetric correlation.** Correlate on **kills** too (look back at recent nibble bumps), not only on bumps. Symmetric window: `|kill.At - bump.At| <= window`, `window = 500 ms` (the existing `CombatCorrelationWindow`). Buffer both recent kills and recent nibble bumps; correlate on whichever arrives.
4. **`expect = questVariableLow(activeQuest, idx) >= N`** or `questVariableHigh(activeQuest, idx) >= N`, where N = the nibble's value at record time. Never a byte `questVariable`, never `questSequence`.
5. **Kill-set = the distinct DataId(s) correlated to the dominant nibble.** Adds that don't correlate are excluded; author prunes residue + reviews. `Spawn` defaults `OverworldEnemies`; `Location` = player position at combat start (unchanged).
6. **Honor the merged nibble-predicate carry-forwards:** emit only **literal** nibble indices (no `${slot}`/ParameterRef — those throw at runtime today) and only against the **active** quest (a non-active quest id introduces a new `GetQuestVariables(otherId)` read and forces fixture re-records — D8 of NIBBLE_PREDICATES_PLAN).

---

## Dependency graph

Two repos. **No schema change** (`CombatStep`/`CombatSpawn`/`NpcLocation` are unchanged — confirmed: Step.cs:88-124, NpcLocation in SharedValueTypes). The nibble predicates already exist on both sides: questforge engine evaluator has the `questVariableLow`/`questVariableHigh` arms (PredicateEvaluator.cs:106-107, `Nibble` enum line 9), and questforge-tools has the registry+checker entries. This Piece 2 only changes **how the author tool produces** the nibble expect string; it does not touch the predicate language.

```
questforge (this repo)
  QuestForge.Engine/Authoring
    ├── GameStateSnapshot          ← KillCorrelatedTargets re-keyed (int → (int,NibbleHalf)); add NibbleHalf enum
    ├── SnapshotAggregator         ← bidirectional correlation; nibble delta; dominant-nibble selection; reset/baseline rework
    ├── StepInferenceEngine        ← Rule 2.2 rewrite (single nibble expect, no and-join); still BEFORE Rule 3
    └── StepFactory                ← "combat" case reads the new dictionary shape; expect already passed in
  QuestForge.Plugin.Tracing
    └── UIObserver.PollCombat      ← UNCHANGED contract (already emits EnemyKilled/InCombat); only consumer logic changes
  QuestForge.Adapters.Dalamud
    └── DalamudCombatProbe         ← RESTORED from git stash@{0} (Slice 4); already emits EnemyKilled/InCombat correctly

questforge-tools
  QuestForge.Tools.Trace
    ├── SnapshotState              ← mirror the bidirectional nibble correlation EXACTLY (ev.At-driven)
    └── Quest/TraceToQuestExtractor← unchanged control flow; reuses StepFactory via ProjectReference (already does)
```

**Build order:** engine (`KillCorrelatedTargets` re-key + `NibbleHalf` + aggregator + Rule 2.2 + factory) → offline `SnapshotState` mirror. The offline path takes a ProjectReference on `QuestForge.Engine` (it already `using`s `QuestForge.Engine.Authoring` and shares `StepInferenceEngine`/`StepFactory`/`KillCorrelation`), so the engine slice must land first. The Slice-4 stash restore is independent and in-game-only.

---

## Architectural decisions (read before coding)

### D1 — `NibbleHalf` + the re-keyed `KillCorrelatedTargets` data model

**Decision.** Replace the byte-keyed `Dictionary<int /*varIndex*/, KillCorrelation>` with a nibble-keyed map. Introduce a tiny enum and a composite key.

```csharp
namespace QuestForge.Engine.Authoring;

public enum NibbleHalf { Low, High }

/// <summary>The specific nibble (low/high of a variable byte) a set of kills incremented.</summary>
public readonly record struct NibbleKey(int VarIndex, NibbleHalf Half);

// GameStateSnapshot (non-positional, replaces the old IReadOnlyDictionary<int, KillCorrelation>)
public IReadOnlyDictionary<NibbleKey, KillCorrelation>? KillCorrelatedTargets { get; init; }

// KillCorrelation is UNCHANGED in shape; FinalValue now holds the nibble value (0–15), not a byte.
public sealed record KillCorrelation(IReadOnlyList<uint> DataIds, int FinalValue);
```

**Sequence advances are dropped as a correlation target.** The merged `SequenceVariableIndex = -1` synthetic key is **removed** (Settled #4: never a `questSequence` expect — on objective completion the sequence jumps to 255 and resets, so a sequence-keyed correlation produces an unsatisfiable/indistinct expect). `OnQuestSequenceChanged` no longer runs kill correlation. (`SequenceVariableIndex` the const is deleted; its only consumers were the merged combat path.)

**Why `NibbleKey` is a `record struct`, not `(int, NibbleHalf)` tuple.** It is a dictionary key surfaced on the public `GameStateSnapshot`; a named struct gives stable property names (`VarIndex`, `Half`) for tests and the factory, value equality for free, and avoids the positional-tuple readability problem the byte design had with `int`-keyed dictionaries.

**What breaks if violated.** If the key stays `int varIndex`, the high/low ambiguity of `0x13` is unrepresentable — a kill of 347 (low) and a kill of 338 (high) on the *same byte index* would collide into one entry, exactly the in-game failure. The composite key is what makes per-nibble attribution possible.

**Testability.** `KillCorrelation` is unchanged, so the GWT-F factory assertions are a one-line key change. `NibbleKey` is a plain value type with no clock/IO — fully unit-testable.

### D2 — Nibble delta detection: which nibble "incremented"

**Decision.** On a quest-variables observation, for each index `i`, compare the new byte `n` to the previous baseline byte `p` and classify per nibble:

```csharp
// low incremented when the low nibble strictly increased
bool lowUp  = (n & 0x0F) > (p & 0x0F);
// high incremented when the high nibble strictly increased
bool highUp = (n >> 4)   > (p >> 4);
```

A single byte write may set BOTH (`0x02 → 0x13`: low `2→3` AND high `0→1`) — attribute to **both** nibbles (each becomes its own `NibbleKey` correlation; per-record scoping (Settled #2) means in practice only the objective the author is fighting accumulates a kill-set; the other gets the kill too but is discarded by dominant-nibble selection — D5). A nibble that **decreased** (objective reset, e.g. on full completion `0x13 → 0x00`) is **not** a correlation event (`>` not `!=`) — D4 edge case.

**Nibble value for the expect.** `FinalValue = lowUp ? (n & 0x0F) : (n >> 4)` for the respective half — the *current* nibble value, which for an in-progress objective the author fought to completion is the target (e.g. low reaches 3 → `>= 3`). It is NOT the kill count and NOT the byte.

**Why strict-increase, not inequality.** A decrement is a reset/completion artifact, never an objective-progress event; correlating a kill to a decrement would attribute the kill to the wrong (vanishing) value. Strict `>` also makes the resumed-quest baseline case (D7) safe.

### D3 — Bidirectional / symmetric correlation: buffer both sides, correlate on arrival

**Decision.** The aggregator keeps TWO time-ordered buffers and correlates on whichever event arrives:

```csharp
// recent kills (unchanged buffer; semantics extended to symmetric window)
private readonly List<(uint DataId, DateTimeOffset At)> _recentKills = new();
// recent nibble bumps not yet (or also) matched against later kills
private readonly List<(NibbleKey Key, int FinalValue, DateTimeOffset At)> _recentNibbleBumps = new();
```

- **On a nibble bump** (in `OnQuestVariablesUpdated`, per incremented nibble): record it in `_recentNibbleBumps`, then correlate it against every kill in `_recentKills` with `|kill.At - bump.At| <= window` → add those DataIds to `KillCorrelatedTargets[key]`, set `FinalValue` to the nibble value.
- **On a kill** (in `OnEnemyKilled`): push to `_recentKills`, then correlate it against every bump in `_recentNibbleBumps` with `|kill.At - bump.At| <= window` → add this DataId to `KillCorrelatedTargets[bump.Key]` (FinalValue from the buffered bump).
- **Eviction** is symmetric and lazy: at each push, evict entries from the *other* buffer older than `window` relative to the newest event time on *this* side is insufficient for symmetry — instead evict each buffer by `At < (now - window)` where `now` is the arriving event's time, AND keep entries up to `window` in the future is impossible (events arrive monotonically in `ev.At` offline / `clock.UtcNow` live). Concretely: kills and bumps are both retained for `window` after their own `At`; a buffer entry is evicted once an arriving event's `At` exceeds `entry.At + window` (it can no longer match any future event because future events have larger `At`). This makes a kill at `T` matchable by a bump in `[T-window, T+window]` and vice-versa — the symmetric window — while bounding both buffers.

**Idempotent accumulation.** Correlating the same (kill, bump) pair from both directions (the bump's pass and the kill's pass) adds the same DataId to the same `HashSet<uint>` — a no-op the second time. `FinalValue` is set to the bump's nibble value from either direction (identical). So double-correlation is safe by construction; no de-dup bookkeeping needed.

**Why buffer bumps, not just kills.** The merged design buffered only kills and triggered only on bumps → a kill arriving after its bump is invisible. Buffering bumps too, and correlating on kill-arrival, closes exactly the in-game gap (bump ~70 µs before kill; kill same-or-later poll).

**Determinism.** Live uses `_clock.UtcNow` (tests inject `FakeClock`); offline uses `ev.At`. Both are the SAME `window` constant (`SnapshotAggregator.CombatCorrelationWindow`, referenced by `SnapshotState`). Identical inputs → identical correlation on both paths.

**What breaks if violated.** Drop the bump buffer and you reproduce the shipped bug: real traces (kill-after-bump) yield empty `KillCorrelatedTargets` → no `CombatStep` → the author tool emits a spurious travel/talk step (the other in-game symptom).

### D4 — Baseline (`_prevQuestVariables`) and per-window reset, re-examined at the nibble level

**Decision.** `_prevQuestVariables` (full byte array baseline) is preserved across `ResetDeltas` exactly as the merged design did, and nibble deltas are computed against it (D2). The first observation of a quest establishes the baseline with **no** correlation if no bump is detected against an absent prior (treat absent prior as all-zero, so a resumed quest whose V0 is already `0x13` registers low `0→3` and high `0→1` on first observation — but per D7 the production poll order means no kills are buffered yet on that first poll, so nothing correlates; the baseline is set and subsequent real bumps correlate correctly).

`ResetDeltas` (live) / `ResetPendingKeyItemDeltas` (offline) clears `_recentKills`, `_recentNibbleBumps`, and `_killCorrelatedTargets`; it **preserves** `_prevQuestVariables`. (Same discipline as the merged GWT-L6, now extended to clear the new bump buffer.)

**Edge — nibble decrement (reset/completion).** `0x13 → 0x00` (objective complete, all vars reset): low `3→0` and high `1→0` are decrements → no correlation event (D2 strict `>`). The already-accumulated `KillCorrelatedTargets[(0,Low)]=([347],3)` from the in-progress window is unaffected (it was set while the objective was in progress). This is the whole point: capture the nibble *while in progress*, ignore the completion reset.

### D5 — One-step-per-record: dominant-nibble selection

**Decision.** Rule 2.2 emits exactly ONE `CombatStep` per record (Settled #2). If a record window accumulated more than one `NibbleKey` (e.g. an add of another mob type bumped a second objective's nibble), pick the single **dominant** nibble:

1. Primary = the `NibbleKey` whose correlation has the **most distinct DataIds** (most kills correlated).
2. Tie-break = lowest `VarIndex`, then `Low` before `High`.

The non-dominant keys are NOT `and`-joined into the expect (the merged design did this — removed). Instead they are listed in `Notes` for author review ("Also saw progress on V1-low ([49]); if this record covered a second objective, split it into its own combat step.").

**Why most-kills, not largest-FinalValue.** The merged design used largest `FinalValue`; at the nibble level FinalValue is 0–15 and an interleaved single add to another objective can have a *higher* current nibble than the objective being fought. The objective the author actually fought to completion is the one with the most correlated kills. Most-distinct-DataIds is the robust dominance signal under per-record scoping.

```csharp
// dominant selection (deterministic)
var primary = targets
    .OrderByDescending(kv => kv.Value.DataIds.Count)
    .ThenBy(kv => kv.Key.VarIndex)
    .ThenBy(kv => kv.Key.Half)          // Low(0) before High(1)
    .First();
```

**KillEnemyDataIds = the primary's DataIds only** (Settled #5: the kill-set is the DataIds correlated to *that* nibble), sorted/distinct. NOT the union across nibbles (the merged design unioned — removed; a residual add of another type must not pollute the kill-set).

### D6 — Rule 2.2 rewrite: single nibble expect, ordering preserved

**Decision.** Rule 2.2 stays where it is (after Rule 2.1 foreign-quest, before Rule 2.3 key-item, and crucially before Rule 3 sequence / Rule 5 flag / Rule 7 NPC / Rule 8 movement — so combat-while-moving is not mislabeled travel, the other in-game symptom). Guard unchanged in spirit: fires when `KillCorrelatedTargets` is non-empty with at least one non-empty DataId set. The body changes:

```csharp
// Rule 2.2: kill-correlated combat (nibble-level, bidirectional).
if (after.KillCorrelatedTargets is { Count: > 0 } targets
    && targets.Any(kv => kv.Value.DataIds.Count > 0))
{
    var primary = targets
        .Where(kv => kv.Value.DataIds.Count > 0)
        .OrderByDescending(kv => kv.Value.DataIds.Count)
        .ThenBy(kv => kv.Key.VarIndex)
        .ThenBy(kv => kv.Key.Half)
        .First();

    var dataIds = primary.Value.DataIds.Distinct().OrderBy(x => x).ToArray();

    string fn = primary.Key.Half == NibbleHalf.Low ? "questVariableLow" : "questVariableHigh";
    string expect = $"{fn}({questIdValue}, {primary.Key.VarIndex}) >= {primary.Value.FinalValue}";

    string? notes = "Spawn defaulted to overworldEnemies; review. Location = player position at combat start.";
    var others = targets.Where(kv => !kv.Key.Equals(primary.Key) && kv.Value.DataIds.Count > 0).ToArray();
    if (others.Length > 0)
        notes = "Multiple objectives progressed this record: " +
                string.Join(", ", others.Select(o => $"V{o.Key.VarIndex}-{o.Key.Half} ([{string.Join(",", o.Value.DataIds)}])")) +
                ". If this record covered more than one objective, split it. " + notes;

    return new InferenceResult(
        StepType: "combat",
        SuggestedStepId: $"defeat-{dataIds[0]}",
        SuggestedExpect: expect,
        Confidence: Confidence.Medium,
        InferredFrom: InferredFrom.Combat,
        Notes: notes);
}
```

`InferredFrom.Combat` already exists (InferredFrom.cs:16). `questIdValue` is the existing `before.ActiveQuest ?? after.ActiveQuest` convention (StepInferenceEngine.cs:14). The expect uses the literal index `primary.Key.VarIndex` and the active quest id only (Settled #6).

### D7 — Resumed-quest / first-observation safety under the production poll order

**Decision (carry-forward, re-verified).** In `UIObserver.OnFrameworkUpdate` the heartbeat runs `PollQuestState()` (forwards `OnQuestVariablesUpdated` every poll) **before** `PollCombat()`. So on the FIRST poll of a resumed quest whose V0 is already non-zero, the baseline is established while `_recentKills` AND `_recentNibbleBumps` are empty → the first-observation bump correlates against zero kills (no-op) and the baseline is set. The bidirectional change does NOT reintroduce spurious correlation here: a later unrelated kill (e.g. 999) that does not move any nibble produces no bump, so the kill's correlate-on-arrival pass finds no matching buffered bump. (This is the in-game-validated invariant the merged GWT-L8 pins; it must survive the rework — see GWT-L8' below.)

### D8 — Offline parity: `SnapshotState` mirrors the bidirectional nibble logic exactly

**Decision.** `SnapshotState` (questforge-tools) duplicates the aggregator's correlation as a pure function of `ev.At` (it already mirrors the merged byte logic — SnapshotState.cs:124-198). It must mirror the rework EXACTLY: same `NibbleKey`/`NibbleHalf` (imported from `QuestForge.Engine.Authoring` via the existing ProjectReference — no duplicate type), same `_recentNibbleBumps` buffer, same symmetric window (`SnapshotAggregator.CombatCorrelationWindow`), same nibble delta (D2), same dominant-nibble projection in `BuildKillCorrelatedTargets`. `GetQuestSequence` **drops** its kill-correlation block (SnapshotState.cs:181-196 removed — Settled #4). `ToSnapshot` projects the `NibbleKey`-keyed dictionary.

**The expect string is produced identically on both sides** because `TraceToQuestExtractor` builds the `CombatStep` through the shared `StepFactory.Build("combat", …, inference.SuggestedExpect, after, before)` (TraceToQuestExtractor.cs:208) and `inference.SuggestedExpect` comes from the shared `StepInferenceEngine.Infer` (TraceToQuestExtractor.cs:190). So the nibble expect (`questVariableLow(65847, 0) >= 3`) is generated by the SAME `StepInferenceEngine` code in both repos — confirmed: questforge-tools references the engine project, not a copy. No string is hand-rolled offline.

### D9 — `StepFactory` "combat" case: read the new dictionary shape

**Decision.** The factory's `KillEnemyDataIds` must read the **dominant** nibble's DataIds, not the union (it currently unions — StepFactory.cs:89-91). To keep the factory a pure builder and avoid re-deriving dominance, the factory continues to union by default BUT the dominant selection already happened in inference. **Chosen approach:** the factory unions the DataIds across whatever keys are present (unchanged code), and because per-record scoping (Settled #2) + dominant-nibble selection mean the offline/live correlation should normally contain only the dominant key by the time `CombatStep` is built. To make this exact rather than incidental, the inference/extractor passes the *pruned* snapshot is overkill; instead the factory reads union and the GWT-F1' test asserts the dominant-only kill-set by constructing a snapshot with a single nibble key (the common case). **If** multi-key snapshots reach the factory, union is acceptable (author prunes; Notes flagged it). Keep the factory change minimal: only the dictionary key type changes (`NibbleKey` instead of `int`), the `SelectMany(t => t.DataIds)` union stays.

```csharp
"combat" => new CombatStep
{
    Id = stepId,
    Expect = expectValue,                       // the nibble predicate, already a PredicateExpect
    Zone = zoneStr,
    RequiredZone = zoneStr,
    KillEnemyDataIds = after?.KillCorrelatedTargets is { } kct
        ? kct.Values.SelectMany(t => t.DataIds).Distinct().OrderBy(id => id).ToArray()
        : [],
    Spawn = CombatSpawn.OverworldEnemies,        // D5 default (Settled #5)
    Location = new NpcLocation(
        NpcId: 0,
        Zone: after?.CombatStartZone > 0 ? after.CombatStartZone : zone,
        Position: after?.CombatStartPosition is { } csp
            ? new Position3(csp.X, csp.Y, csp.Z)
            : playerPos)                          // fallback to player position (unchanged)
},
```

### D10 — Slice-4 restore from `git stash@{0}`

**Decision.** Piece 2's in-game slice **restores `git stash@{0}`** (`On feat/combat-authoring-detection-slice4: slice4: DalamudCombatProbe + AuthoringHost wiring + QuestStatePanel UI fix`). The stash contains:
- `QuestForge.Plugin/Tracing/DalamudCombatProbe.cs` (untracked, 42 lines) — implements `ICombatProbe` over `ICondition[ConditionFlag.InCombat]` + `IObjectTable` hostile enumeration. **The probe is correct as-is**: it already emits `EnemyKilled`/`InCombat` via the unchanged `UIObserver.PollCombat` (UIObserver.cs:366-410). Only the *consumer* (correlation/inference) changes in Piece 2.
- `QuestForge.Plugin/Authoring/AuthoringHost.cs` — wires `combatProbe: new DalamudCombatProbe(services.Condition, services.ObjectTable)` into the `UIObserver` ctor.
- `QuestForge.Plugin/UI/Authoring/QuestStatePanel.cs` — the transposed Var/Dec/Hex/High/Low variable table (in-game-validated readability fix; directly supports authoring nibble objectives).

Restore via `git stash apply stash@{0}` during Slice 4, then commit. (Apply, not pop, so the stash survives a mistake.) This is CI-irrelevant (Dalamud-only code) and validated in-game, not by xUnit.

---

## What is REMOVED vs the merged byte design

| Removed | Why |
|---|---|
| `int`-keyed `KillCorrelatedTargets` (varIndex → KillCorrelation) | replaced by `NibbleKey`-keyed (D1) |
| `SnapshotAggregator.SequenceVariableIndex = -1` const + sequence-advance correlation in `OnQuestSequenceChanged` | Settled #4: never a `questSequence` expect (255 + reset) (D1/D8) |
| Rule 2.2 multi-index `and`-join of secondary `questVariable` predicates | Settled #2: one step per record; secondaries go to Notes (D5/D6) |
| Rule 2.2 union of DataIds across indices for `KillEnemyDataIds` | Settled #5: kill-set is the dominant nibble's DataIds (D5) |
| Largest-`FinalValue` primary selection | replaced by most-distinct-DataIds dominance (D5) |
| `questVariable(...)` (whole byte) expect from inference | Settled #1/#4: nibble predicate only (D6) |
| Backward-only, bump-triggered correlation | replaced by bidirectional symmetric correlation + bump buffer (D3) |
| `GetQuestSequence` correlation block in offline `SnapshotState` | mirrors the engine removal (D8) |

**Unchanged / NOT touched:** `ICombatProbe` contract; `UIObserver.PollCombat` emission logic (kill = tracked-then-gone while in combat); `EnemyKilled`/`InCombat` observation methods + trace format; `CombatStep`/`CombatSpawn`/`NpcLocation` schema; the nibble predicate language (registry/checker/evaluator); `KillCorrelation` record shape; `CombatStartPosition`/`CombatStartZone` capture; `ResetDeltas` preserving the variable baseline.

---

## Task 1 — Engine data model (re-key)

- Add `NibbleHalf { Low, High }` and `readonly record struct NibbleKey(int VarIndex, NibbleHalf Half)` to `QuestForge.Engine.Authoring` (new file `NibbleKey.cs` or appended to `GameStateSnapshot.cs`).
- Change `GameStateSnapshot.KillCorrelatedTargets` to `IReadOnlyDictionary<NibbleKey, KillCorrelation>?`.
- Delete `SnapshotAggregator.SequenceVariableIndex`.

## Task 2 — `SnapshotAggregator` bidirectional nibble correlation

- Add `_recentNibbleBumps` buffer (D3).
- `OnEnemyKilled`: push kill, evict stale on both buffers, correlate-on-arrival against `_recentNibbleBumps` (D3).
- `OnQuestVariablesUpdated`: compute per-nibble deltas vs `_prevQuestVariables` (D2); for each incremented nibble, record a bump and correlate against `_recentKills` (D3); update baseline.
- `OnQuestSequenceChanged`: remove correlation (just track sequence).
- `OnInCombatChanged`: unchanged (records `_combatStartPosition`/`_combatStartZone` on false→true).
- `ResetDeltas`: also clear `_recentNibbleBumps`; preserve `_prevQuestVariables` (D4).
- `BuildKillCorrelatedTargets`: project to `NibbleKey`-keyed dictionary.

## Task 3 — `StepInferenceEngine` Rule 2.2 rewrite

- Rewrite Rule 2.2 body per D6 (single nibble expect, dominant selection, Notes for secondaries). Keep position (before Rule 3). No `and`-join, no union.

## Task 4 — `StepFactory` "combat" case

- Change the dictionary key type to `NibbleKey` (D9). Keep the union read (per-record scoping makes it effectively single-key). Expect/Spawn/Location unchanged.

## Task 5 — Offline `SnapshotState` mirror

- Import `NibbleKey`/`NibbleHalf` from the engine (ProjectReference).
- Add `_recentNibbleBumps`; mirror D2/D3 in the `EnemyKilled` and `GetQuestVariables` `Apply` cases.
- Remove the `GetQuestSequence` correlation block.
- `ResetPendingKeyItemDeltas`: clear the new bump buffer; preserve `_prevQuestVariables`.
- `BuildKillCorrelatedTargets`: project `NibbleKey`-keyed.
- `TraceToQuestExtractor`: NO control-flow change (still routes `inference.StepType == "combat"` through `StepFactory.Build`, still does not skip a combat `wait` window — TraceToQuestExtractor.cs:206-215). The `wait`-skip guard already defers to inference; verify it still fires for nibble combat.

## Task 6 — Slice 4 restore (in-game)

- `git stash apply stash@{0}`; commit `DalamudCombatProbe` + `AuthoringHost` wiring + `QuestStatePanel` fix (D10).

---

## Worked example — 65847 sequence 2, objective 1 (NPC 347 → V0 low == 3)

Production poll order (each heartbeat: `PollQuestState` THEN `PollCombat`); the variable bump is observed ~70 µs before its kill, and corpse-despawn lag means the kill may be the same poll or the next:

```
t=0      InCombat true                                  → InCombat=true, combatStart=(P,zone)
t=250    GetQuestVariables [0x01,0,0,0,0,0]  (poll N)    → low 0→1 bump@250 buffered; no kills yet → no corr
t=250    EnemyKilled 347                     (poll N)    → kill@250; correlate vs bumps in [−250,750] → bump(V0,Low)@250 matches → KCT[(0,Low)]={347}, Final=1
t=500    GetQuestVariables [0x02,0,0,0,0,0]              → low 1→2 bump@500; correlate vs kills [0,500] → kill347@250 matches → KCT[(0,Low)].Final=2 (347 already in set)
t=500    EnemyKilled 347                                → kill@500; correlate vs bumps [0,1000] → bump@500 matches → Final=2 (347 dup)
t=900    GetQuestVariables [0x03,0,0,0,0,0]              → low 2→3 bump@900; correlate vs kills [400,900] → kill347@500 matches → Final=3
t=900    EnemyKilled 347                                → kill@900; correlate vs bumps [400,1400] → bump@900 matches → Final=3 (dup)
=> KillCorrelatedTargets[(0,Low)] = ([347], 3)
=> Rule 2.2 dominant = (0,Low); expect = "questVariableLow(65847, 0) >= 3"; StepId = "defeat-347"
=> CombatStep { KillEnemyDataIds=[347], Spawn=OverworldEnemies, Location=combatStart, Expect=questVariableLow(65847,0)>=3 }
```

The byte-and-both-nibble case (objective 3, NPC 338 → V1 high; suppose a write `0x03 → 0x13` on V1): low `3→3` no change, high `0→1` up → bump `(V1,High)` with Final=1 → kills of 338 correlate to `(V1,High)` → `questVariableHigh(65847, 1) >= 1` (author fights to high==3 → `>= 3`). The low nibble (objective 2, already complete at 3) does NOT re-bump, so 338 never pollutes `(V1,Low)`.

---

## Given-When-Then specifications

The existing RED-phase byte tests are **rewritten in place** (same files: `CombatCorrelationAggregatorTests`, `CombatInferenceEngineTests`, `CombatStepFactoryTests` in engine; `SnapshotStateCombatTests`, `TraceToQuestExtractorCombatTests` in tools). Tests that asserted byte `questVariable(...)` expects, `SequenceVariableIndex`, multi-index `and`-join, or kill-before-bump ordering are deleted and replaced by the specs below. Helpers (`FakeClock`, `Epoch`/`T0`, `FormatTargets`, `QuestIdArg`, `ObsAt`) are reused.

### 5.1 `SnapshotAggregator` (engine, live) — `QuestForge.Engine.Tests/Authoring/CombatCorrelationAggregatorTests.cs`

**GWT-L1' bump-before-kill (the real order) correlates to the low nibble.**
Given quest 65847, `OnInCombatChanged(true)` at t=0. When (the in-game order) at t=250 `OnQuestVariablesUpdated([0x01,0,0,0,0,0])` THEN `OnEnemyKilled(347)`; at t=500 `OnQuestVariablesUpdated([0x02,…])` then `OnEnemyKilled(347)`; at t=900 `OnQuestVariablesUpdated([0x03,…])` then `OnEnemyKilled(347)`. Then `Current.KillCorrelatedTargets[new NibbleKey(0, NibbleHalf.Low)] == KillCorrelation([347], 3)`. (This is the test the merged design fails; it MUST pass here. Boundary: it is the dominant happy path with reversed ordering.)

**GWT-L2' AoE triple kill, single low-nibble bump.**
Given t=0 InCombat true; t=250 `OnQuestVariablesUpdated([0x03,0,…])` then `OnEnemyKilled(347)`×3. Then `[(0,Low)].DataIds` deduped == `{347}`, `FinalValue == 3`.

**GWT-L3' kill outside the symmetric window not correlated.**
Given `OnEnemyKilled(999)` at t=0; `OnQuestVariablesUpdated([0x01,0,…])` at t=600 (|600−0| = 600 > 500). Then no `(0,Low)` entry with non-empty DataIds (999 never appears). Also the reverse: bump at t=0, kill at t=600 → not correlated (symmetric).

**GWT-L4' both nibbles move in one write → two NibbleKeys.**
Given t=0 InCombat true; t=250 `OnEnemyKilled(347)`; t=250 `OnQuestVariablesUpdated([0x13,0,…])` from baseline `0x02` (low 2→3 up, high 0→1 up). Then `KillCorrelatedTargets` contains BOTH `(0,Low)` (Final=3) and `(0,High)` (Final=1), each with `{347}`. (Edge: byte touching both nibbles; D2.)

**GWT-L5' high-nibble objective.**
Given baseline `[0,0x02,0,0,0,0]`; t=0 InCombat; t=250 `OnQuestVariablesUpdated([0,0x32,…])` (V1 low 2→2 unchanged, high 0→3 up) then `OnEnemyKilled(338)`. Then `[(1,High)] == ([338], 3)`; no `(1,Low)` entry. (Pins high-nibble path + correct index.)

**GWT-L6' ResetDeltas clears both buffers + correlation, keeps baseline.**
Given a correlated `(0,Low)` after a `0x00→0x03` window; When `ResetDeltas()`; Then `KillCorrelatedTargets` empty. Re-emit `[0x03,0,…]` with an in-window kill → NO correlation (baseline preserved at 0x03, no delta). A genuine `0x03→0x04` (low 3→4) → correlates, Final=4. (Decisive baseline test; extends merged GWT-L6 to nibble + clears `_recentNibbleBumps`.)

**GWT-L7' InCombat false→true records start position/zone.** (Unchanged from merged GWT-L7.)
Given `OnZoneChanged(148,(10,0,20))`, `OnPlayerMoved((10,0,20))`; When `OnInCombatChanged(true)`; Then `CombatStartPosition==(10,0,20)`, `CombatStartZone==148`.

**GWT-L8' resumed-quest first observation, production order, no spurious correlation.**
Given t=0 InCombat true; t=250 `OnQuestVariablesUpdated([0x02,0,…])` (FIRST obs, no kills buffered) — baseline set; t=400 `OnEnemyKilled(999)`; t=500 `OnQuestVariablesUpdated([0x02,0,…])` (unchanged). Then no `(0,Low)`/`(0,High)` correlated entry. (D7; the in-game-validated invariant must survive the bidirectional rework.)

**GWT-L9' nibble decrement (objective complete + reset) does not correlate and does not erase prior.**
Given a correlated `(0,Low)=([347],3)`; When `OnEnemyKilled(347)` then `OnQuestVariablesUpdated([0x00,0,…])` (low 3→0 decrement, sequence-completion reset). Then `(0,Low)` still `([347],3)` (the decrement is not a correlation event; D4) and no new entry is created from the reset.

### 5.2 `StepInferenceEngine` Rule 2.2 (engine) — `CombatInferenceEngineTests.cs`

**GWT-I1' combat beats sequence advance → low-nibble expect.**
Given before seq 0, no targets; after seq 3, `[(0,Low)]=([347],3)`, `LastNpcInteracted=9999` (would fire Rule 7). Then `StepType=="combat"`, `SuggestedExpect=="questVariableLow(65847, 0) >= 3"`, `InferredFrom==Combat`.

**GWT-I2' high-nibble expect.**
Given after `[(1,High)]=([338],3)`. Then `SuggestedExpect=="questVariableHigh(65847, 1) >= 3"`.

**GWT-I3' no correlation falls through to Rule 3 (talk).**
Given after empty/null `KillCorrelatedTargets`, seq 0→1. Then `StepType=="talk"`.

**GWT-I4' dominant-nibble = most-distinct-DataIds; secondaries in Notes, NOT and-joined.**
Given after `[(0,Low)]=([347,348],3)` (2 DataIds), `[(1,Low)]=([49],1)` (1 DataId). Then primary is `(0,Low)`; `SuggestedExpect=="questVariableLow(65847, 0) >= 3"` (NO " and "); `Notes` mentions V1-Low / 49 / "split". (Decisive: assert `SuggestedExpect` does NOT contain `"and"` and does NOT contain `questVariableLow(65847, 1)`.)

**GWT-I5' dominance tie-break: equal DataId counts → lowest VarIndex then Low.**
Given `[(1,Low)]=([49],3)` and `[(0,High)]=([338],3)` (both 1 DataId). Then primary is `(0,High)` (VarIndex 0 < 1); expect `questVariableHigh(65847, 0) >= 3`. And a second case: `[(0,High)]=([338],2)` vs `[(0,Low)]=([347],2)` → primary `(0,Low)` (Low before High); expect `questVariableLow(65847, 0) >= 2`.

**GWT-I6' StepId uses lowest DataId of the dominant set.**
Given `[(0,Low)]=([348,347],2)`. Then `SuggestedStepId=="defeat-347"`. (KillEnemyDataIds excludes any non-dominant residue — that is asserted in the factory test.)

### 5.3 `StepFactory` (engine) — `CombatStepFactoryTests.cs`

**GWT-F1' builds CombatStep, nibble expect, OverworldEnemies, Location from combat start.**
Given snapshot `[(0,Low)]=([347,348],3)`, `CombatStartZone=148`, `CombatStartPosition=(10,0,20)`; `Build("combat","defeat-347","questVariableLow(65847,0) >= 3", after)`. Then `CombatStep`, `KillEnemyDataIds==[347,348]`, `Spawn==OverworldEnemies`, `Location.Zone==148`, `Location.Position==(10,0,20)`, `Expect is PredicateExpect` whose `Predicate=="questVariableLow(65847,0) >= 3"`.

**GWT-F2' missing combat-start position falls back to player position.**
Given `CombatStartPosition==null`, `Position=(5,0,5)`. Then `Location.Position==(5,0,5)`.

**GWT-F3' single dominant nibble key → kill-set is that key's DataIds only.**
Given a snapshot whose `KillCorrelatedTargets` contains ONLY `(0,Low)=([347],3)` (the post-dominance shape the inference/extractor produces per record). Then `KillEnemyDataIds==[347]` (no residue). (Pins Settled #5 at the factory boundary; the union-read is exact when the snapshot is single-key.)

### 5.4 `UIObserver.PollCombat` (plugin tracing) — `QuestForge.Engine.Tests` (FakeCombatProbe) — UNCHANGED behaviour

The emission contract is unchanged (D10); the existing GWT-U1..U5 (if present) stay green as-is. **No new GWT-U tests** are required by Piece 2. If the merged GWT-U tests live in the engine/plugin test project, re-run them unchanged to prove emission is untouched.

### 5.5 `SnapshotState` (offline) — `QuestForge.Tools.Trace.Tests/SnapshotStateCombatTests.cs`

**GWT-O1' bump-before-kill correlates (ev.At order = production order).**
Given `SnapshotState(65847)`; Apply `InCombat{value:true}`@t0; then per round (`GetQuestVariables [k,0,…]` THEN `EnemyKilled{347}`) at t=250/500/900 with bytes 0x01/0x02/0x03. Then `ToSnapshot(t).KillCorrelatedTargets[new NibbleKey(0,NibbleHalf.Low)] == ([347],3)`. Each `Apply` returns true.

**GWT-O2' wrong-quest GetQuestVariables ignored.**
Given a kill@250 then `GetQuestVariables` arg quest 12345 `[0x01,0,…]`. Then no correlation; `Apply` returns true.

**GWT-O3' kill outside symmetric window not correlated (both directions).** Mirror of GWT-L3'.

**GWT-O4' both nibbles in one write → two keys.** Mirror of GWT-L4' over the trace API (baseline established via a prior `GetQuestVariables [0x02,…]`, then `[0x13,…]`).

**GWT-O5' high-nibble objective.** Mirror of GWT-L5'.

**GWT-O6' ResetPendingKeyItemDeltas clears combat window + bump buffer, keeps baseline.** Mirror of GWT-L6'.

**GWT-O7' InCombat false→true records start zone/position from current state.**
Given `GetPlayerZone{value:148}`, `GetPlayerPosition{x:10,y:0,z:20}`, then `InCombat{value:true}`. Then `CombatStartZone==148`, `CombatStartPosition==(10,0,20)`.

**GWT-O8' sequence advance does NOT correlate (block removed).**
Given kills@250/500 then `GetQuestSequence` arg 65847 value 3 (advance), no variable bump. Then `KillCorrelatedTargets` has no entry (the merged `-1` synthetic key is gone). (Pins the removal — Settled #4 / D8.)

### 5.6 `TraceToQuestExtractor` (offline) — `TraceToQuestExtractorCombatTests.cs`

**GWT-E1' end-to-end nibble combat extraction.**
Given a synthetic trace: `run.start` quest 65847; `InCombat true`; three (`GetQuestVariables [0x0k,0,…]` then `EnemyKilled 347`) pairs across the window; a `wait` decision in the combat window; `run.end`. Then `Extract` yields a `QuestSequence` whose step is a `CombatStep` with `KillEnemyDataIds==[347]`, `Spawn==OverworldEnemies`, `Expect.Predicate=="questVariableLow(65847, 0) >= 3"`, and a TODO mentioning Spawn review. (Proves offline parity with GWT-I1'/F1'.)

**GWT-E2' combat `wait` window is not skipped.**
Given the combat window's only decision is `wait`. Then the `CombatStep` is still emitted (the `wait`-skip guard defers to inference — TraceToQuestExtractor.cs:206-215 unchanged).

**GWT-E3' uncorrelated kills produce no CombatStep.**
Given kills with no in-window nibble bump. Then no `CombatStep`; existing rules drive the window.

**GWT-E4' parity: same trace → same expect string live and offline.**
Given the GWT-E1' trace, assert the extractor's `CombatStep.Expect.Predicate` equals the string `StepInferenceEngine.Infer` produces for the equivalent live snapshots (`questVariableLow(65847, 0) >= 3`). (Pins D8 lock-step; one assertion is enough since both paths share `StepInferenceEngine`.)

---

## Implementation order

**Slice A — Engine data model + live correlation + inference + factory (CI-gated: `QuestForge.Engine.Tests`).**
1. `NibbleHalf` + `NibbleKey`; re-key `GameStateSnapshot.KillCorrelatedTargets`; delete `SequenceVariableIndex`.
2. `SnapshotAggregator`: `_recentNibbleBumps`, bidirectional correlate-on-arrival, nibble deltas, drop sequence correlation, reset/baseline.
3. Rule 2.2 rewrite (single nibble expect, dominant selection, Notes).
4. `StepFactory` "combat" key-type change.
5. Rewrite/replace GWT-L1'..L9', I1'..I6', F1'..F3'. **Done-before-next:** all green.

**Slice B — Offline mirror (CI-gated: `QuestForge.Tools.Trace.Tests`). Depends on Slice A (imports `NibbleKey`).**
1. `SnapshotState`: `_recentNibbleBumps`, nibble deltas in `EnemyKilled`/`GetQuestVariables`, drop `GetQuestSequence` correlation, reset/baseline, `NibbleKey` projection.
2. `TraceToQuestExtractor`: verify (no change) the combat branch + `wait`-skip guard still fire.
3. Rewrite/replace GWT-O1'..O8', E1'..E4'. **Done-before-next:** all green.

**Slice C — Slice-4 probe restore + in-game G7 (NOT CI-gated; requires game).**
1. `git stash apply stash@{0}`; commit `DalamudCombatProbe` + `AuthoringHost` wiring + `QuestStatePanel` fix (D10).
2. Author 65847 sequence 2 in-game; record each of the three objectives; confirm three drafts: `defeat-347`/`questVariableLow(65847,0)>=3`, `defeat-49`/`questVariableLow(65847,1)>=3`, `defeat-338`/`questVariableHigh(65847,1)>=3`.
3. Replay the recorded trace through `qf-trace extract-quest`; confirm identical `CombatStep`s (DataIds + nibble expect identical to the live drafts). Closes the GAME-* item.

**Build/test commands (net10 SDK at `C:\Users\publi\.dotnet`):** `questforge` pins net10 via `global.json` (10.0.202); **`questforge-tools` has no `global.json` — prepend the path**.
```bash
# Slice A (questforge)
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge/QuestForge.Engine.Tests
# Slice B (questforge-tools — no global.json, prepend net10)
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge-tools/QuestForge.Tools.Trace.Tests
```

---

## PR slicing (small, independently verifiable)

| PR | Repo | Scope | Gate | Depends on |
|---|---|---|---|---|
| **PR-A: live-path rework** | questforge | `feat/combat-nibble-detection`: `NibbleKey`/`NibbleHalf`, re-keyed snapshot, bidirectional aggregator, Rule 2.2 rewrite, factory key-type, drop `SequenceVariableIndex`. Rewritten GWT-L*/I*/F*. | CI: `QuestForge.Engine.Tests` | — |
| **PR-B: offline-path rework** | questforge-tools | paired branch: `SnapshotState` mirror (bidirectional nibble, drop sequence correlation), `NibbleKey` import via ProjectReference, rewritten GWT-O*/E*. | CI: `QuestForge.Tools.Trace.Tests` | PR-A merged (engine types) |
| **PR-C: Slice-4 probe restore + in-game G7** | questforge | `git stash apply stash@{0}`: `DalamudCombatProbe`, `AuthoringHost` wiring, `QuestStatePanel` fix. Author 65847 in-game; replay parity. | In-game (no CI) | PR-A + PR-B merged |

PR-A and PR-B are CI-gated and each independently red→green. PR-C is in-game only (Dalamud code; no xUnit). Merge order A → B → C.

---

## Done criteria

1. Live aggregator correlates kills to the **nibble** that incremented, keyed by `NibbleKey`, with **bidirectional** correlation that succeeds when the variable bump is observed BEFORE its kill (the in-game order) — GWT-L1' green (the case the merged design fails). GWT-L2'..L9' green.
2. `StepInferenceEngine` Rule 2.2 returns `StepType "combat"` before the sequence/flag/talk/movement rules, with a **single** `questVariableLow`/`questVariableHigh(activeQuest, idx) >= N` expect (never a byte `questVariable`, never `questSequence`, never an `and`-join), selecting the dominant nibble by most-distinct-DataIds — GWT-I1'..I6' green.
3. `StepFactory` builds a `CombatStep` with the dominant nibble's `KillEnemyDataIds`, `Spawn=OverworldEnemies`, combat-start `Location`, and the nibble `PredicateExpect` — GWT-F1'..F3' green.
4. Offline `SnapshotState` + `TraceToQuestExtractor` produce the IDENTICAL `CombatStep` (DataIds + nibble expect string) for the same event stream, with sequence-advance correlation removed — GWT-O1'..O8', E1'..E4' green.
5. The `SequenceVariableIndex` synthetic key, the multi-index `and`-join, the DataId union for the kill-set, and the byte `questVariable` expect are all GONE (asserted by GWT-I4'/I5', GWT-O8', GWT-F3').
6. In-game (PR-C): authoring 65847 sequence 2 produces three correct `CombatStep` drafts; `qf-trace extract-quest` on the recorded trace reproduces them identically. No engine replay-fixture re-record (combat observations are authoring-mode emit only; the nibble expect rides the already-recorded `GetQuestVariables(activeQuest)` read — NIBBLE_PREDICATES_PLAN D7).

---

## Exclusions

- **Schema changes** — none; `CombatStep`/`CombatSpawn`/`NpcLocation` unchanged (confirmed Step.cs:88-124).
- **The nibble predicate language** — `questVariableLow`/`questVariableHigh` registry/checker/evaluator already merged; this Piece 2 only changes how the author tool *emits* them.
- **Multi-objective auto-splitting into multiple steps** — one step per record (Settled #2); secondaries are Notes-only for author review.
- **`questVariable` (byte) and `questSequence` combat expects** — removed (Settled #1/#4).
- **`${slot}`/ParameterRef nibble indices and non-active-quest ids** — forbidden (Settled #6); would throw at runtime / force fixture re-records.
- **`UIObserver.PollCombat` / `ICombatProbe` / `EnemyKilled`/`InCombat` trace format** — unchanged.
- **Distinguishing `AutoOnEnterArea` from `OverworldEnemies`** — default + author review (carry-forward).
- **Enemy world-position capture** — `Location` uses player combat-start position only (carry-forward).
- **Engine-side combat execution** — authoring detection only; `CombatController` untouched.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §5.
- Happy paths: 8 scenarios (L1', L2', L5', I1', I2', F1', O1', E1')
- Edge cases: 14 scenarios (L4', L6', L7', L8', L9', I4', I5', F2', F3', O4', O5', O6', O7', E2')
- Error/negative cases: 8 scenarios (L3', I3', O2', O3', O8', E3', E4', plus the deleted byte/sequence tests asserted-absent)
- Expected total: ~30 tests — ~15 in `QuestForge.Engine.Tests` (CombatCorrelationAggregatorTests L1'..L9', CombatInferenceEngineTests I1'..I6', CombatStepFactoryTests F1'..F3') and ~12 in `QuestForge.Tools.Trace.Tests` (SnapshotStateCombatTests O1'..O8', TraceToQuestExtractorCombatTests E1'..E4'). GWT-U emission tests re-run unchanged (no count added).

CI-gated: Slices A (`QuestForge.Engine.Tests`) and B (`QuestForge.Tools.Trace.Tests`). In-game-only: Slice C (PR-C) — DalamudCombatProbe restore + 65847 authoring + replay parity.
