# Combat Target-Attribution Plan (Piece 3 — replace kill→bump correlation with target-based attribution)

**Status:** ready for test creation
**Input docs:** docs/COMBAT_NIBBLE_DETECTION_PLAN.md (the merged kill→bump correlation this SUPERSEDES — nibble expect derivation KEPT), docs/COMBAT_DEATH_DETECTION_PLAN.md (the merged death-state `EnemyKilled` whose *attribution role* this supersedes), docs/NIBBLE_PREDICATES_PLAN.md (the merged `questVariableLow`/`questVariableHigh` predicates, unchanged), docs/SCHEMA.md (CombatStep/CombatSpawn/NpcLocation — unchanged), docs/TRACE_FORMAT.md, CLAUDE.md (Dalamud-free observer; clean-room).
**Output:** the author tool reconstructs a `CombatStep` whose `KillEnemyDataIds` come from the **hostile the player targeted during the InCombat span**, NOT from a kill event coinciding with the nibble bump. The `expect` derivation (nibble predicate from the bumped nibble) is unchanged. **CI behaviour change:** the engine `CombatCorrelationAggregatorTests` are rewritten from kill→bump timing expectations to span/target expectations and go green against a target-driven `SnapshotAggregator`; `CombatInferenceEngineTests` / `CombatStepFactoryTests` keep their nibble-expect assertions (the dominant-target selection replaces the dominant-nibble-by-kills selection); the tools `SnapshotStateCombatTests` / `TraceToQuestExtractorCombatTests` are rewritten to drive `GetTarget` + `InCombat` + `GetQuestVariables` (no `EnemyKilled` needed for attribution) and go green. No engine replay-fixture re-record (combat observations are authoring-mode emit only; the nibble expect rides the already-recorded `GetQuestVariables(activeQuest)` read).
**Target quest:** 65847 — Way of the Marauder (sequence 2: three "defeat 3× of one NPC type" objectives, nibble-packed). Worked span: `GetTarget 338` → `InCombat true` → V1-high 0→3 ⇒ `questVariableHigh(65847, 1) >= 3`, `KillEnemyDataIds=[338]`.
**Branch:** `feat/combat-target-attribution` (questforge); paired branch in questforge-tools.

---

## Why this exists (the diagnosis, from in-game traces of 65847)

The merged design (Piece 2 + the death-detection rework, both on `main`) attributes a quest-variable **nibble** bump to a mob whose **death** coincides with it, over a ±500 ms symmetric window. **Proven broken in-game on 65847, across three traces, for one root cause:** the "which mob died" signal lags the variable bump too far to fit the window.

1. **Corpse-removal lags 0.5–2 s** and is gated out post-combat (the original death-detection diagnosis).
2. The death-detection rework fixed the *gate* (emit on the alive→dead **state** transition during the linger, not on corpse despawn) but **not the lag**: the variable bumps on the **killing-blow frame**, while `IBattleChara.IsDead` flips only after the **death animation** — observed **1–6 s** later. So even the reworked `EnemyKilled` (alive→dead) arrives 1–6 s after its bump. The 500 ms correlation window cannot span that. The merged `CombatCorrelationAggregatorTests` pass only because their synthetic kills land *inside* the window (the opposite of in-game reality).

**The only two signals coincident with the action** are:
- the **quest-variable bump** — tells us an objective progressed and to what nibble value, but not *which* mob;
- the **player's hard target** — set at *engagement*, zero death/animation lag, and (for a BattleNpc) its `BaseId` IS the `DataId` we want for `KillEnemyDataIds`.

Verified against the latest trace: **clean span** `GetTarget 338` right before `InCombat true`, V1-high bumped 0→3 during the span ⇒ `338 ↔ V1-high` (matches the hand-authored reference; zero lag). **Mixed-pack spans** (two NPC types fought interleaved, two nibbles bumped) are undisambiguable by *any* method — that is a **workflow problem**, surfaced via `Notes` for author split, not a signal problem.

This Piece 3 replaces the kill→bump timing correlation with **target-based attribution within the InCombat span**.

---

## Decisions already SETTLED upstream (design to these; do not re-litigate)

1. **Target-based attribution.** Attribute the bumped nibble to the **hostile the player targeted during the InCombat span**, not to a kill event. Replace the kill→bump timing correlation entirely.
2. **One combat step per record, one NPC type.** The author fights a single type to its sub-objective completion, then records → one `CombatStep` (carry-forward from Piece 2 Settled #2).
3. **Ambiguity flagging, never silent guessing.** If a record's InCombat span saw **more than one distinct hostile target** OR **more than one nibble bumped**, the tool surfaces it in `Notes` for author review/split.
4. **Keep the nibble expect derivation.** `expect = questVariableLow(activeQuest, idx) >= N` / `questVariableHigh(...)`, N = the bumped nibble's value at record time. Never a byte `questVariable`, never `questSequence`. (Carry-forward Piece 2 Settled #1/#4/#6: literal nibble index only, active quest only.)
5. **`Spawn` defaults `OverworldEnemies`; `Location` = player position at combat start** (carry-forward, unchanged).
6. **`InCombat` bounds the span; the target gives the DataId; the nibble gives the expect/count.** `EnemyKilled` is therefore vestigial *for attribution* — see D9 for its disposition.

---

## Dependency graph

Two repos. **No schema change** (`CombatStep`/`CombatSpawn`/`NpcLocation` unchanged). The nibble predicates already exist on both sides (merged). This Piece 3 changes the **attribution signal** (target instead of kill) on the live and offline paths, and the **target probe / forwarding** on the live path.

```
questforge (this repo)
  QuestForge.Plugin.Tracing
    ├── ITargetProbe                ← add a hostile-target query (D1): GetBattleNpcTarget()
    ├── UIObserver.PollTargetNpc    ← detect hostile (BattleNpc) target → OnBattleNpcTargeted; still emits GetTarget (D2)
    └── ICombatProbe / PollCombat   ← unchanged span bounds; EnemyKilled disposition per D9
  QuestForge.Engine/Authoring
    ├── SnapshotAggregator          ← REPLACE kill→bump buffers with span tracker + hostile-target set + nibble-bump set (D3/D4)
    ├── GameStateSnapshot           ← KillCorrelatedTargets re-derived from target↔nibble (shape KEPT: NibbleKey → KillCorrelation) (D5)
    ├── StepInferenceEngine         ← Rule 2.2: dominant target/nibble selection + ambiguity Notes (D6)
    └── StepFactory                 ← "combat" case unchanged in shape (reads KillCorrelatedTargets) (D7)
  QuestForge.Plugin
    └── Tracing/DalamudTargetProbe  ← implement GetBattleNpcTarget() (ObjectKind.BattleNpc) (D1, in-game compiles, not CI)

questforge-tools
  QuestForge.Tools.Trace
    ├── SnapshotState               ← REPLACE kill→bump mirror with the offline target↔nibble span correlation; consume GetTarget (D8)
    └── Quest/TraceToQuestExtractor ← unchanged control flow; reuses StepFactory/StepInferenceEngine via ProjectReference
```

**Build order:** engine + plugin-tracing (target probe shape, `OnBattleNpcTargeted`, aggregator span correlation, Rule 2.2, factory) → offline `SnapshotState` mirror (imports `NibbleKey`/`KillCorrelation` via the existing ProjectReference) → Dalamud probe impl + in-game G7. The offline path takes a ProjectReference on `QuestForge.Engine` and shares `StepInferenceEngine`/`StepFactory`, so the engine slice must land first.

---

## Architectural decisions (read before coding)

### D1 — BattleNpc-target signal: a typed `ITargetProbe.GetBattleNpcTarget()`, NOT raw HP and NOT a cross-reference to the combat probe

**Decision.** Add a single typed query to `ITargetProbe` that returns the current hard target **only when its `ObjectKind == BattleNpc`**. NOTE: the discriminator is the **object kind**, NOT hostility/aggression — quest target mobs are frequently docile (`IsHostile: False`) yet are `BattleNpc`s and must be captured (docile-mob lesson; see the guardrail in the impl below):

```csharp
public interface ITargetProbe
{
    (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcTarget();        // unchanged (EventNpc or BattleNpc)
    (uint BaseId, float X, float Y, float Z, int Zone)? GetInteractableNpcPreviousTarget(); // unchanged
    (uint BaseId, float X, float Y, float Z)? GetAetheryteTarget();                          // unchanged
    (uint BaseId, float X, float Y, float Z, int Zone)? GetBattleNpcTarget();                  // NEW (D1): hard target iff ObjectKind == BattleNpc
}
```

`DalamudTargetProbe.GetBattleNpcTarget()`:

```csharp
public (uint BaseId, float X, float Y, float Z, int Zone)? GetBattleNpcTarget()
{
    var t = _targetManager.Target;
    // GUARDRAIL (docile-mob lesson): filter ONLY on ObjectKind == BattleNpc.
    // Do NOT add an IsHostile / aggression / NamePlateIconId check — quest target mobs are
    // frequently docile (the Wharf Rat, BaseId 347, reads IsHostile:False) and MUST be captured.
    // This is the exact filter reverted twice in GetVisibleHostiles / GetHostileActors.
    if (t?.ObjectKind != ObjectKind.BattleNpc) return null;
    var p = t.Position;
    return (t.BaseId, p.X, p.Y, p.Z, (int)_clientState.TerritoryType);
}
```

**Why a typed probe query, not a hostility flag on the existing `GetInteractableNpcTarget` tuple.** `GetInteractableNpcTarget` deliberately accepts **both** `EventNpc` and `BattleNpc` (it serves the quest-NPC dialogue capture path). Threading an `IsBattleNpc` bool through that tuple would force every existing dialogue-capture call site to ignore the flag and risks the observer accidentally treating a quest-giver `EventNpc` as a combat target. A separate `GetBattleNpcTarget()` keeps the two concerns physically distinct: one tuple means "an NPC I can talk to," the other means "a hostile I can fight." The `BaseId` of a `BattleNpc` is exactly the `DataId` for `KillEnemyDataIds` (verified: the trace's `GetTarget` already emits BattleNpc ids `338`/`347`/`49` and NPC ids like `1000927`).

**Why not cross-reference `ICombatProbe.GetVisibleHostiles()` to decide hostility.** The combat probe enumerates *all* visible BattleNpcs (deliberately unfiltered — docile-mob lesson). Asking "is the hard target in that set?" works live but is fragile offline (the visible-hostiles set is not in the trace) and couples the two probes. The hard target's own `ObjectKind` answers the question directly, in one read, on the same `TargetManager` access the probe already does — and offline the `GetTarget` emission is enriched to carry the answer (D2).

**Rejected alternatives.**
- *Surface raw HP / `IsHostile` aggression flag.* Rejected: docile quest mobs read `IsHostile: False` (project_zone memory) yet are exactly the targets we want; `ObjectKind == BattleNpc` is the correct, lag-free discriminator.
- *Reuse the merged `EnemyKilled` for the DataId.* Rejected: that is the 1–6 s-lagged signal this plan removes (the diagnosis).

**Testability.** `GetBattleNpcTarget()` is a nullable tuple at the probe boundary — `FakeTargetProbe` returns whatever the test sets. The observer's branch (hostile vs interactable-npc vs aetheryte) is pure dispatch, exercised via `QuestForge.Plugin.Tests` with no game.

**What breaks if violated.** If hostility is inferred from `IsHostile`/aggression, docile objective mobs are dropped and `KillEnemyDataIds` comes back empty — no `CombatStep`, the author tool emits a spurious travel/talk step (the Piece 2 failure mode).

### D2 — Forwarding the hostile target: enrich `GetTarget` with a `kind` discriminator + add `SnapshotAggregator.OnBattleNpcTargeted(uint dataId)`

**Decision.** `PollTargetNpc` checks `GetBattleNpcTarget()` **first** (before the aetheryte and interactable-npc branches). On a hostile target change it:
1. emits the existing `GetTarget` observation, now carrying a `kind` so the **offline** path can tell hostile from NPC from the trace alone, and
2. forwards a NEW aggregator signal `OnBattleNpcTargeted(dataId)`.

```csharp
// UIObserver.PollTargetNpc — new FIRST branch
var hostileInfo = _targetProbe.GetBattleNpcTarget();
if (hostileInfo.HasValue)
{
    if (hostileInfo.Value.BaseId != _lastTargetBaseId)
    {
        _lastTargetBaseId = hostileInfo.Value.BaseId;
        WriteObservation("GetTarget", 0u,
            new { baseId = hostileInfo.Value.BaseId, kind = "hostile" }, runId, now);   // ENRICHED value shape
        _aggregator?.OnBattleNpcTargeted(hostileInfo.Value.BaseId);
    }
    return;   // a hostile target is NOT an interaction/aethernet shard — do not fall through
}
```

**`GetTarget` value shape changes from a bare `uint` to `{ baseId, kind }`.** Today `GetTarget` writes the bare `BaseId` (value = uint, argument = 0u) for both aetheryte and interactable-npc targets. The aetheryte/npc branches keep writing the **bare uint** form (back-compat for existing replay/inspect consumers and existing dedup), and ONLY the new hostile branch writes the `{ baseId, kind: "hostile" }` object. `SnapshotState` (D8) reads both shapes: bare uint → not a hostile signal (it is an aetheryte/npc target, irrelevant to combat attribution); `{kind:"hostile"}` → a hostile target. (Rationale: minimizing change to the existing two branches avoids a re-record cascade on inspect-mode traces; only the genuinely new signal carries the discriminator.)

**Why a new `OnBattleNpcTargeted` aggregator method, not reuse `OnInteraction`.** `OnInteraction(NpcId, WorldPosition)` sets `LastNpcInteracted`, which drives talk/attune inference (Rules 5/7). Forwarding a hostile through `OnInteraction` would corrupt `LastNpcInteracted` with a mob id and mislabel the post-combat window as a talk step (a Piece-2-class symptom). `OnBattleNpcTargeted` feeds ONLY the combat span correlation.

**Why hostile is checked FIRST in `PollTargetNpc`.** During a fight the hard target is the mob; if the aetheryte/interactable-npc branches ran first a BattleNpc would still fall through to `GetInteractableNpcTarget` (which accepts BattleNpc) and be mis-forwarded as `OnInteraction`. Ordering hostile first routes a BattleNpc to `OnBattleNpcTargeted` and an `EventNpc`/Aetheryte to the existing branches.

**Testability.** `OnBattleNpcTargeted(uint)` is a one-arg method on the aggregator, drivable directly in `QuestForge.Engine.Tests`. The offline equivalent is the `GetTarget {kind:"hostile"}` event, drivable in `QuestForge.Tools.Trace.Tests`.

**What breaks if violated.** Reuse `OnInteraction` → `LastNpcInteracted` set to a mob id → Rule 7 fires a talk step after combat. Keep the bare-uint shape for the hostile branch → the offline path cannot distinguish a hostile target from a quest-NPC target and either over- or under-attributes.

### D3 — `SnapshotAggregator`: replace kill→bump buffers with an InCombat-span tracker

**Decision.** Remove `_recentKills`, `_recentNibbleBumps`, and the ±500 ms symmetric-window machinery. Replace with **per-span** accumulation bounded by the InCombat transitions:

```csharp
// REMOVE:
//   private readonly List<(uint DataId, DateTimeOffset At)> _recentKills;
//   private readonly List<(NibbleKey Key, int FinalValue, DateTimeOffset At)> _recentNibbleBumps;
//   CombatCorrelationWindow, EvictStale, AbsDelta, CorrelateKillsToBump (kill-arrival)

// ADD: span state, reset on each false→true InCombat transition (D4)
private readonly HashSet<uint> _spanBattleNpcTargets = new();          // distinct hostile targets seen this span
private readonly Dictionary<NibbleKey, int> _spanNibbleBumps = new(); // nibble → latest (highest) value bumped this span
```

- **`OnInCombatChanged(bool inCombat)`**: on false→true, capture `_combatStartPosition`/`_combatStartZone` (unchanged) AND clear `_spanBattleNpcTargets` + `_spanNibbleBumps` (start a fresh span — D4). On true→false, **do not** clear (the bumps/targets accumulated during the span must survive until the record is taken; clearing happens on the next span start or `ResetDeltas`).
- **`OnBattleNpcTargeted(uint dataId)`**: `if (_inCombat) _spanBattleNpcTargets.Add(dataId);` — only targets acquired **during** an InCombat span count. A hostile targeted out of combat (mouse-over of a wandering mob before the fight) is ignored.
- **`OnQuestVariablesUpdated(quest, vars)`**: keep the nibble-delta detection (D2 of Piece 2: `lowUp = (n&0x0F) > (p&0x0F)`, `highUp = (n>>4) > (p>>4)`; strict-increase only; first observation = baseline only; preserve `_prevQuestVariables`). For each incremented nibble, `if (_inCombat) _spanNibbleBumps[key] = nibbleValue;` (store the latest value — successive bumps overwrite, so `>= N` uses the final reached value). Bumps outside combat are ignored for attribution.
- **`BuildKillCorrelatedTargets()`**: project the span into the **unchanged** `IReadOnlyDictionary<NibbleKey, KillCorrelation>` shape (D5).

**Why span-scoped sets, not time buffers.** The lag (1–6 s) makes any fixed time window wrong. The InCombat span is the natural, lag-free boundary: the target is set at engagement (span start-ish), the bump lands mid-span, and the span ends when combat ends — all observable, all coincident with the action, none dependent on the death animation. Per-record scoping (Settled #2) means one span = one objective in the intended workflow.

**Why store target in a `HashSet` and nibble→value in a `Dictionary`.** The ambiguity check (D6) needs the *count* of distinct targets and the *count* of bumped nibbles; sets/dict-keys give both directly. The dict value is the nibble's reached value for the expect.

**What breaks if violated.** Keep the time window → the in-game lag still drops the kill/target outside it (the diagnosed bug). Accumulate targets outside the span → a wandering mob mouse-over before the fight pollutes `KillEnemyDataIds`.

### D4 — Span lifecycle: start on false→true, retain across the record, clear on next span or `ResetDeltas`

**Decision.** A span's `_spanBattleNpcTargets` / `_spanNibbleBumps` are **established** on false→true `InCombat`, **accumulated** while `_inCombat`, and **retained** when combat ends (true→false) so the record (taken after the fight) still sees them. They are cleared by either (a) the next false→true transition (a new fight = a new objective) or (b) `ResetDeltas()` (per-record reset).

`ResetDeltas()` clears `_spanBattleNpcTargets` and `_spanNibbleBumps` (replacing the `_recentKills`/`_recentNibbleBumps`/`_killCorrelatedTargets` clears) and **preserves** `_prevQuestVariables` (unchanged discipline — the next bump's delta must be against the last known values).

**Edge — first observation / resumed quest.** `_prevQuestVariables is null` ⇒ baseline only (unchanged from Piece 2). A resumed quest whose V0 is already non-zero sets the baseline silently; no bump, no span attribution. (The production poll order — `PollQuestState` then `PollCombat` — means the first variables poll is always the pre-fight state.)

**Edge — nibble decrement (objective complete + reset, `0x13 → 0x00`).** Strict-increase (`>`) means a decrement is not a bump → not recorded in `_spanNibbleBumps`. Any value already in `_spanNibbleBumps` from the in-progress span is retained (captured while in progress). This is the whole point: capture the nibble while in progress, ignore the completion reset.

### D5 — `GameStateSnapshot.KillCorrelatedTargets` shape is UNCHANGED; only its derivation changes

**Decision.** Keep `IReadOnlyDictionary<NibbleKey, KillCorrelation>? KillCorrelatedTargets`, `KillCorrelation(IReadOnlyList<uint> DataIds, int FinalValue)`, and `NibbleKey(int VarIndex, NibbleHalf Half)` exactly as they are. The derivation changes from "DataIds = kills correlated to this nibble in the window" to "**DataIds = the span's hostile targets**, FinalValue = the span's reached value for this nibble":

```csharp
private IReadOnlyDictionary<NibbleKey, KillCorrelation>? BuildKillCorrelatedTargets()
{
    if (_spanNibbleBumps.Count == 0 || _spanBattleNpcTargets.Count == 0) return null;
    var dataIds = _spanBattleNpcTargets.OrderBy(id => id).ToList();   // SAME DataIds attached to every bumped nibble in the span
    var result = new Dictionary<NibbleKey, KillCorrelation>(_spanNibbleBumps.Count);
    foreach (var (key, value) in _spanNibbleBumps)
        result[key] = new KillCorrelation(dataIds, value);
    return result.Count > 0 ? result : null;
}
```

**Why DataIds repeat across nibble keys.** In a clean single-type span there is exactly one hostile target and one bumped nibble → one entry, `DataIds=[target]`. In a mixed span (>1 target OR >1 nibble) every bumped nibble carries the full span target-set; the ambiguity check (D6) sees `>1` and flags. We do NOT try to pair target↔nibble within a mixed span — that is the undisambiguable case (Settled #3). The repeated DataIds make the mixed case visibly ambiguous rather than silently mis-paired.

**Why keep the type name `KillCorrelation` / `KillCorrelatedTargets`.** Renaming ripples through `StepInferenceEngine`, `StepFactory`, `SnapshotState`, and every combat test in both repos for zero behavioural gain. The shape is correct; only the population source moves from kills to targets. (A doc-comment update on `KillCorrelation` to "the hostile data-ids targeted during the span that reached this nibble value" is the only surface note.)

**Testability.** `BuildKillCorrelatedTargets` is a pure projection of two in-memory collections — directly assertable via `aggregator.Current.KillCorrelatedTargets`.

### D6 — `StepInferenceEngine` Rule 2.2: dominant selection over the span, ambiguity Notes

**Decision.** Rule 2.2 stays in position (after Rule 2.1 foreign-quest, before Rule 3 sequence / Rule 5 flag / Rule 7 NPC / Rule 8 movement). The guard is unchanged: fires when `KillCorrelatedTargets` is non-empty with at least one non-empty DataId set. The body selects the **dominant nibble** (the objective the author fought) and flags ambiguity:

- **Dominant nibble** = the `NibbleKey` with the highest `FinalValue` (the objective fought to completion reaches the highest nibble), tie-broken by lowest `VarIndex` then `Low` before `High`. (Note: under target attribution, "most distinct DataIds" no longer discriminates — every nibble shares the span target-set — so dominance reverts to highest reached value, with deterministic tie-breaks.)
- **`KillEnemyDataIds`** = the dominant nibble's DataIds (= the span hostile target-set), sorted/distinct.
- **`expect`** = `questVariableLow|High(questIdValue, VarIndex) >= FinalValue` (unchanged derivation; literal index; active quest id).
- **Ambiguity Notes (Settled #3):** if `KillEnemyDataIds.Length > 1` (more than one distinct hostile target this span) OR the span has `> 1` bumped nibble (`targets.Count > 1`), prepend a split warning to `Notes` ("Ambiguous record: span saw N hostile targets [...] and M nibble bumps [...]; one combat step per NPC type — re-record split."). Otherwise the standard "Spawn defaulted ..." note.

```csharp
// Rule 2.2: target-correlated combat (span-scoped). Runs BEFORE Rule 3.
if (after.KillCorrelatedTargets is { Count: > 0 } targets
    && targets.Any(kv => kv.Value.DataIds.Count > 0))
{
    var primary = targets
        .Where(kv => kv.Value.DataIds.Count > 0)
        .OrderByDescending(kv => kv.Value.FinalValue)
        .ThenBy(kv => kv.Key.VarIndex)
        .ThenBy(kv => kv.Key.Half)
        .First();

    var dataIds = primary.Value.DataIds.Distinct().OrderBy(x => x).ToArray();
    string fn = primary.Key.Half == NibbleHalf.Low ? "questVariableLow" : "questVariableHigh";
    string expect = $"{fn}({questIdValue}, {primary.Key.VarIndex}) >= {primary.Value.FinalValue}";

    bool multiTarget = dataIds.Length > 1;
    bool multiNibble = targets.Count(kv => kv.Value.DataIds.Count > 0) > 1;
    string notes = "Spawn defaulted to overworldEnemies; review. Location = player position at combat start.";
    if (multiTarget || multiNibble)
        notes = $"Ambiguous record: span saw {dataIds.Length} hostile target(s) [{string.Join(",", dataIds)}] " +
                $"and {targets.Count} nibble bump(s). One combat step per NPC type — re-record split. " + notes;

    return new InferenceResult(
        StepType: "combat",
        SuggestedStepId: $"defeat-{dataIds[0]}",
        SuggestedExpect: expect,
        Confidence: multiTarget || multiNibble ? Confidence.Low : Confidence.Medium,
        InferredFrom: InferredFrom.Combat,
        Notes: notes);
}
```

**Why highest-FinalValue for dominance** (changed from Piece 2's most-distinct-DataIds): with target attribution every nibble in the span carries the same DataIds, so DataId-count is no longer a discriminator. The objective the author fought to completion is the one whose nibble reached the highest value; ties resolve deterministically by index then half.

**What breaks if violated.** Drop the ambiguity flag → a mixed-pack span silently emits `KillEnemyDataIds=[347,49]` with one nibble's expect, which the engine cannot satisfy correctly (Settled #3 forbids the silent guess).

### D7 — `StepFactory` "combat" case: unchanged

**Decision.** No change. The factory already reads `after.KillCorrelatedTargets.Values.SelectMany(t => t.DataIds).Distinct().OrderBy(...)`. Under per-record scoping the dominant nibble is the only meaningful key; the union read is exact when the span is single-target/single-nibble (the workflow case) and harmlessly unions the span target-set when ambiguous (the author has been Notes-warned to split). `Spawn=OverworldEnemies`, `Location` from combat start, nibble `PredicateExpect` — all unchanged.

### D8 — Offline `SnapshotState`: mirror the span correlation; consume `GetTarget {kind:"hostile"}`

**Decision.** `SnapshotState` removes its kill→bump mirror (`_recentKills`, `_recentNibbleBumps`, `EvictStale`, `AbsDelta`, `CorrelateKillsToBump`, the kill-arrival block in the `EnemyKilled` case) and adds the span mirror (D3/D4) plus a `GetTarget` case:

- **`InCombat` case**: keep the false→true combat-start capture; ADD clearing `_spanBattleNpcTargets`/`_spanNibbleBumps` on false→true.
- **NEW `GetTarget` case**: parse the value; if it is the `{kind:"hostile"}` object shape, `if (_inCombat) _spanBattleNpcTargets.Add(baseId);`. If it is the bare-uint shape (aetheryte/npc target), **ignore** for combat (return true — recognised). (Today `GetTarget` is an *unrecognised* method offline, returning `false`; this adds recognition.)
- **`GetQuestVariables` case**: keep nibble-delta detection; replace the bump-buffer/correlate logic with `if (_inCombat) _spanNibbleBumps[key] = nibbleValue;`.
- **`EnemyKilled` case**: per D9 — keep recognition (return true), drop the correlation body.
- **`ResetPendingKeyItemDeltas`**: clear `_spanBattleNpcTargets`/`_spanNibbleBumps`; preserve `_prevQuestVariables`.
- **`BuildKillCorrelatedTargets`**: identical projection to D5 (imports `NibbleKey`/`KillCorrelation` from the engine ProjectReference — no duplicate type).

**The expect string is produced identically on both sides** because `TraceToQuestExtractor` builds the `CombatStep` through the shared `StepFactory.Build("combat", …, inference.SuggestedExpect, after, before)` and `inference.SuggestedExpect` comes from the shared `StepInferenceEngine.Infer`. No string is hand-rolled offline. The ordering for the offline span: the trace records `GetTarget {kind:"hostile"}` then `InCombat true` (or target during the span) then `GetQuestVariables` bumps — `SnapshotState.Apply` is driven in `ev` order, so the same span accumulation occurs.

**`TraceToQuestExtractor`**: NO control-flow change. It still routes `inference.StepType == "combat"` through `StepFactory.Build` and still defers the `wait`-skip to inference (the combat window with only a `wait` decision still emits the step). Verify the combat branch still fires.

### D9 — `EnemyKilled` disposition: keep emitting as a combat-presence corroborator, drop from attribution

**Decision.** `UIObserver.PollCombat` continues to emit `EnemyKilled` on the alive→dead transition (the death-detection rework is sound and useful: it corroborates that a fight happened and is a future hook for kill-count objectives), and `SnapshotAggregator.OnEnemyKilled` / `SnapshotState`'s `EnemyKilled` case **remain recognised methods** but **no longer correlate** — their bodies become no-ops (or `OnEnemyKilled` is deleted from the aggregator and `PollCombat` simply stops forwarding it to the aggregator while still writing the trace event).

**Chosen concrete form:**
- `PollCombat` KEEPS writing the `EnemyKilled` trace observation (presence corroborator; trace-format stable) but STOPS calling `_aggregator?.OnEnemyKilled(dataId)`.
- `SnapshotAggregator.OnEnemyKilled` is **deleted** (no caller). `_recentKills` and the kill-correlation helpers are deleted.
- `SnapshotState`'s `EnemyKilled` case is reduced to "recognised, no-op" (`return true;`) so existing/old traces with `EnemyKilled` events replay without error and without affecting attribution.

**Why keep emitting but stop forwarding.** Removing the trace emission would (a) force a re-record of any fixture containing combat and (b) discard a clean, useful signal for a future kill-count step type. Keeping the wire event while severing the *attribution* call is the minimal, non-cascading change. The death-detection rework (3-tuple `ICombatProbe`, `_hostileStates`, `DalamudCombatProbe` death read, alive→dead transition, `ResetHeartbeatState` clearing `_hostileStates`) is **retained in full** for the emission; only its role as the attribution source is superseded.

**What breaks if violated.** Delete the `EnemyKilled` emission → fixture re-record cascade + lost future signal. Keep `OnEnemyKilled` correlating → the 1–6 s-lagged kill re-pollutes attribution (the bug).

---

## What is REPLACED / SUPERSEDED vs the merged design

| Removed / superseded | Replaced by | Why |
|---|---|---|
| `SnapshotAggregator._recentKills` + `_recentNibbleBumps` + `CombatCorrelationWindow` + `EvictStale`/`AbsDelta`/`CorrelateKillsToBump` (kill-arrival) | `_spanBattleNpcTargets` + `_spanNibbleBumps` span tracker (D3/D4) | the 1–6 s death-animation lag breaks any fixed time window (diagnosis) |
| `SnapshotAggregator.OnEnemyKilled` (attribution) | `OnBattleNpcTargeted` (D2) | the target is coincident with the action; the kill is not (D9) |
| The ±500 ms symmetric kill↔bump correlation (engine + offline) | span-scoped target↔nibble association (D3/D8) | same lag reason |
| `SnapshotState._recentKills`/`_recentNibbleBumps` + kill-arrival block | `_spanBattleNpcTargets`/`_spanNibbleBumps` + `GetTarget {kind:"hostile"}` consumption (D8) | offline mirror of the live change |
| Rule 2.2 dominance by **most-distinct-DataIds** | dominance by **highest FinalValue** (D6) | every nibble shares the span target-set, so DataId-count no longer discriminates |
| `EnemyKilled` as the attribution DataId source | the hostile **target** `BaseId` (D1/D2) | target `BaseId` of a BattleNpc IS the DataId, with zero lag |

**KEPT unchanged:** the nibble predicate language (`questVariableLow`/`questVariableHigh` registry/checker/evaluator); the nibble `expect` derivation (`>= N`, N = reached nibble value); the nibble-delta detection (strict-increase, both-nibbles-in-one-write, first-observation baseline, preserve `_prevQuestVariables`); `CombatStep`/`CombatSpawn`/`NpcLocation` schema; `Spawn=OverworldEnemies` default; `Location` = combat-start position; `NibbleKey`/`NibbleHalf`/`KillCorrelation`/`KillCorrelatedTargets` **types**; `InCombat` detection + span bounds + combat-start `Location` capture; the death-detection rework's **emission** (3-tuple `ICombatProbe`, `_hostileStates` alive→dead transition, `DalamudCombatProbe` death read, `ResetHeartbeatState`); `EnemyKilled` **trace event** + format; `StepFactory` "combat" case.

**Reconciliation of the two merged pieces:**
- **Piece 2 (nibble kill→bump correlation, on `main`):** its *correlation* is replaced (target instead of kill); its *nibble expect derivation* and *types* are kept. The `CombatCorrelationAggregatorTests`/`SnapshotStateCombatTests` that assert kill→bump timing are **rewritten** to span/target.
- **Death-detection rework (on `main`, NOT uncommitted — already merged):** its *emission* is kept in full; its *attribution role* is superseded by D9. The `UIObserverCombatTests` (GWT-U1..U11, alive→dead transition) stay green unchanged **except** `UIObserverCombatForwardingTests` GWT-U-FWD-1 (kill→correlation forwarding), which is **rewritten/removed** since `PollCombat` no longer forwards `OnEnemyKilled` to the aggregator — replaced by a forwarding test for `OnBattleNpcTargeted` (GWT-U-FWD-3 below).

---

## Task 1 — `ITargetProbe.GetBattleNpcTarget()` + `DalamudTargetProbe` impl (D1)

- Add `(uint BaseId, float X, float Y, float Z, int Zone)? GetBattleNpcTarget();` to `ITargetProbe`.
- Implement in `DalamudTargetProbe` (ObjectKind.BattleNpc; in-game, not CI).
- Extend the test `FakeTargetProbe` (in `QuestForge.Plugin.Tests`) with a settable hostile target.

## Task 2 — `UIObserver.PollTargetNpc` hostile branch + `OnBattleNpcTargeted` forwarding (D2)

- Add the hostile-first branch; emit `GetTarget {baseId, kind:"hostile"}`; forward `OnBattleNpcTargeted`.
- Keep the aetheryte and interactable-npc branches writing the bare-uint `GetTarget` form.

## Task 3 — `SnapshotAggregator` span correlation (D3/D4/D5/D9)

- Add `OnBattleNpcTargeted(uint)`; add `_spanBattleNpcTargets`/`_spanNibbleBumps`.
- `OnInCombatChanged`: clear span sets on false→true (keep combat-start capture).
- `OnQuestVariablesUpdated`: keep nibble-delta; record bump into `_spanNibbleBumps` only when `_inCombat`.
- Delete `OnEnemyKilled` + `_recentKills`/`_recentNibbleBumps`/`CombatCorrelationWindow`/`EvictStale`/`AbsDelta`/`CorrelateKillsToBump`.
- `ResetDeltas`: clear span sets; preserve `_prevQuestVariables`.
- `BuildKillCorrelatedTargets`: project per D5.

## Task 4 — `StepInferenceEngine` Rule 2.2 (D6)

- Dominant nibble by highest FinalValue (tie: VarIndex, then Half); ambiguity Notes on multi-target OR multi-nibble; Confidence.Low when ambiguous.

## Task 5 — Offline `SnapshotState` mirror (D8/D9)

- Remove kill→bump mirror; add span sets + `GetTarget {kind:"hostile"}` case; `EnemyKilled` → recognised no-op; `ResetPendingKeyItemDeltas` clears span sets; `BuildKillCorrelatedTargets` per D5.
- `TraceToQuestExtractor`: verify (no change) the combat branch + `wait`-skip guard still fire.

## Task 6 — Dalamud probe impl + in-game G7 (no CI)

- `DalamudTargetProbe.GetBattleNpcTarget()`; `PollCombat` stops forwarding `OnEnemyKilled` (D9); author 65847 sequence 2 in-game; replay parity.

---

## Worked example — 65847 sequence 2, objective 3 (NPC 338 → V1 high == 3)

Production poll order (each heartbeat: every-frame `PollTargetNpc`, then heartbeat `PollQuestState` then `PollCombat`). The hard target is set at engagement (no lag); the variable bumps on each killing blow; the death STATE lags 1–6 s and is now irrelevant to attribution:

```
t=0      GetTarget 338 {kind:"hostile"}                 → (not yet in combat) ignored by span
t=120    InCombat true                                  → span START: clear span sets; combatStart=(P,zone);
                                                           (PollTargetNpc re-sees 338 next frame while _inCombat)
t=150    GetTarget 338 {kind:"hostile"} (re-confirm)    → _inCombat ⇒ _spanBattleNpcTargets={338}
t=250    GetQuestVariables [0,0x30,0,0,0,0]  (V1 high 0→3 in one write, low unchanged)
                                                         → _inCombat ⇒ _spanNibbleBumps[(1,High)]=3
         EnemyKilled 338  (later, t≈252)                → trace-emitted; NOT forwarded to aggregator (D9)
t=900    InCombat false                                 → span retained (record taken next)
=> KillCorrelatedTargets[(1,High)] = ([338], 3)         (DataIds = span targets; FinalValue = reached nibble)
=> Rule 2.2 dominant = (1,High); expect = "questVariableHigh(65847, 1) >= 3"; StepId = "defeat-338"
=> CombatStep { KillEnemyDataIds=[338], Spawn=OverworldEnemies, Location=combatStart, Expect=questVariableHigh(65847,1)>=3 }
```

Span-1 (objective 1, NPC 338 in the task framing maps to the 347/V0-low reference; here objective 3) is identical in shape: `GetTarget 338` before `InCombat true`, V1-high bumped 0→3 during the span ⇒ `questVariableHigh(65847,1) >= 3`, `KillEnemyDataIds=[338]`. **Mixed-pack span** (killed 347 AND 49 interleaved, target swapped mid-fight, V0-low AND V1-low both bumped): `_spanBattleNpcTargets={347,49}`, `_spanNibbleBumps={(0,Low):3,(1,Low):3}` ⇒ both `KillCorrelatedTargets` entries carry `[347,49]`; Rule 2.2 flags ambiguity (multiTarget AND multiNibble), Confidence.Low, Notes prompts a split.

---

## Given-When-Then specifications

The existing kill→bump tests are **rewritten in place** (same files). Tests asserting kill→bump timing, the ±500 ms window, or `OnEnemyKilled` correlation are deleted and replaced by the specs below. Helpers (`FakeClock`, `Epoch`/`T0`, `FormatTargets`, `HasCorrelatedEntry`) are reused. (`FakeClock` is now only used for `CapturedAt` — attribution no longer depends on the clock, a simplification worth noting in the test header.)

### 6.1 `SnapshotAggregator` (engine, live) — `QuestForge.Engine.Tests/Authoring/CombatCorrelationAggregatorTests.cs`

**GWT-T1 happy path — target during span + nibble bump attributes to the target.**
Given quest 65847, baseline `[0,0,0,0,0,0]` (first `OnQuestVariablesUpdated` establishes baseline). When `OnInCombatChanged(true)`; `OnBattleNpcTargeted(338)`; `OnQuestVariablesUpdated([0,0x30,0,0,0,0])` (V1 high 0→3); `OnInCombatChanged(false)`. Then `Current.KillCorrelatedTargets[new NibbleKey(1, NibbleHalf.High)] == KillCorrelation([338], 3)`. (Boundary: the decisive happy path — target gives DataId, nibble gives expect/value.)

**GWT-T2 low-nibble objective, target before InCombat.**
Given baseline established. When `OnBattleNpcTargeted(347)` (NOT in combat yet — ignored); `OnInCombatChanged(true)`; `OnBattleNpcTargeted(347)` (re-confirm, in combat); `OnQuestVariablesUpdated([0x03,0,0,0,0,0])` (V0 low 0→3). Then `[(0,Low)] == ([347], 3)`; no `(0,High)` entry. (Pins the in-combat gate on targets: the pre-combat target is ignored, the in-combat one counts.)

**GWT-T3 target acquired OUT of combat is ignored.**
Given baseline. When `OnBattleNpcTargeted(999)` (never in combat); `OnInCombatChanged(true)`; `OnQuestVariablesUpdated([0x01,0,...])` (V0 low 0→1) with NO in-combat target. Then `KillCorrelatedTargets` is null/empty (a nibble bumped but no in-span target ⇒ no attribution). (Negative: no hostile target → no CombatStep.)

**GWT-T4 successive bumps in one span → highest reached value.**
Given baseline; `OnInCombatChanged(true)`; `OnBattleNpcTargeted(347)`. When `OnQuestVariablesUpdated([0x01,..])`, then `[0x02,..]`, then `[0x03,..]` (V0 low 1→2→3). Then `[(0,Low)] == ([347], 3)` (FinalValue = 3, the latest). (Edge: overwrite semantics.)

**GWT-T5 both nibbles move in one write, single target.**
Given baseline `[0x02,0,...]` (so V0 = 2); `OnInCombatChanged(true)`; `OnBattleNpcTargeted(347)`. When `OnQuestVariablesUpdated([0x13,0,...])` (low 2→3 up, high 0→1 up). Then BOTH `(0,Low)` (Final=3) and `(0,High)` (Final=1) present, each `DataIds==[347]`. (Edge: byte touching both nibbles; D2-of-Piece-2 carried forward.)

**GWT-T6 mixed-pack span → both targets on every bumped nibble (ambiguity raw data).**
Given baseline; `OnInCombatChanged(true)`; `OnBattleNpcTargeted(347)`; `OnBattleNpcTargeted(49)` (target swap); `OnQuestVariablesUpdated([0x03,0,...])` (V0 low 0→3); `OnQuestVariablesUpdated([0x03,0x03,0,...])` (V1 low 0→3). Then `(0,Low)` and `(1,Low)` BOTH present, each `DataIds==[49,347]` (sorted). (Edge: undisambiguable span; the inference test asserts the flag.)

**GWT-T7 ResetDeltas clears the span, keeps baseline.**
Given a correlated `(0,Low)=([347],3)` after a span; When `ResetDeltas()`; Then `KillCorrelatedTargets` empty. Re-establish: `OnInCombatChanged(true)`, `OnBattleNpcTargeted(347)`, `OnQuestVariablesUpdated([0x03,0,...])` — baseline preserved at `0x03` ⇒ NO bump (no delta) ⇒ empty. A genuine `0x03→0x04` (low 3→4) ⇒ `[(0,Low)]=([347],4)`. (Decisive baseline + span-clear test.)

**GWT-T8 InCombat false→true records start position/zone (unchanged).**
Given `OnZoneChanged(148,(10,0,20))`, `OnPlayerMoved((10,0,20))`; When `OnInCombatChanged(true)`; Then `CombatStartPosition==(10,0,20)`, `CombatStartZone==148`.

**GWT-T9 resumed-quest first observation: no spurious attribution.**
Given `OnInCombatChanged(true)`; `OnBattleNpcTargeted(338)`; `OnQuestVariablesUpdated([0,0x30,0,...])` as the FIRST observation (no prior baseline). Then `KillCorrelatedTargets` is null (first obs = baseline only; no bump). (D4; the resumed-quest invariant survives.)

**GWT-T10 new span clears the prior span's targets.**
Given span A: `OnInCombatChanged(true)`, `OnBattleNpcTargeted(347)`, `OnQuestVariablesUpdated([0x03,0,...])`, `OnInCombatChanged(false)`. When span B starts: `OnInCombatChanged(true)` (clears span sets), `OnBattleNpcTargeted(338)`, `OnQuestVariablesUpdated([0x03,0x03,0,...])` (V1 low 0→3 — note baseline is `[0x03,0,...]`). Then `KillCorrelatedTargets` has ONLY `(1,Low)=([338],3)` (347 and `(0,Low)` are gone). (Edge: span boundary reset on a new fight.)

### 6.2 `StepInferenceEngine` Rule 2.2 (engine) — `CombatInferenceEngineTests.cs`

**GWT-I1 combat beats sequence advance → low-nibble expect (target DataId).**
Given before seq 0 no targets; after seq 3, `[(0,Low)]=([347],3)`, `LastNpcInteracted=9999`. Then `StepType=="combat"`, `SuggestedExpect=="questVariableLow(65847, 0) >= 3"`, `SuggestedStepId=="defeat-347"`, `InferredFrom==Combat`.

**GWT-I2 high-nibble expect.**
Given after `[(1,High)]=([338],3)`. Then `SuggestedExpect=="questVariableHigh(65847, 1) >= 3"`, `SuggestedStepId=="defeat-338"`.

**GWT-I3 no correlation falls through to Rule 3 (talk).**
Given after empty/null `KillCorrelatedTargets`, seq 0→1. Then `StepType=="talk"`.

**GWT-I4 dominant nibble = highest FinalValue; tie → lowest VarIndex then Low.**
Given after `[(0,Low)]=([347],2)` and `[(1,High)]=([347],3)` (same single target, different reached values). Then primary `(1,High)`; `SuggestedExpect=="questVariableHigh(65847, 1) >= 3"`. Second case: `[(0,Low)]=([347],3)` and `[(0,High)]=([347],3)` (tie on value) → primary `(0,Low)` (Low before High); `questVariableLow(65847, 0) >= 3`.

**GWT-I5 ambiguity flag — multi-target span.**
Given after `[(0,Low)]=([49,347],3)` (one nibble, TWO targets). Then `StepType=="combat"`, `SuggestedExpect=="questVariableLow(65847, 0) >= 3"`, `Confidence==Low`, `Notes` contains "Ambiguous" and "47" and "split", and `SuggestedStepId=="defeat-49"` (lowest DataId). (Decisive: assert `Notes` flags it; `KillEnemyDataIds` ambiguity is asserted in the factory test.)

**GWT-I6 ambiguity flag — multi-nibble span.**
Given after `[(0,Low)]=([347,49],3)` and `[(1,Low)]=([347,49],3)` (TWO nibbles). Then `Confidence==Low`, `Notes` contains "Ambiguous" and "2 nibble bump". Expect is the dominant (tie → `(0,Low)`): `questVariableLow(65847, 0) >= 3`. (Pins Settled #3.)

### 6.3 `StepFactory` (engine) — `CombatStepFactoryTests.cs`

**GWT-F1 builds CombatStep, nibble expect, OverworldEnemies, Location from combat start.**
Given snapshot `[(1,High)]=([338],3)`, `CombatStartZone=148`, `CombatStartPosition=(10,0,20)`; `Build("combat","defeat-338","questVariableHigh(65847,1) >= 3", after)`. Then `CombatStep`, `KillEnemyDataIds==[338]`, `Spawn==OverworldEnemies`, `Location.Zone==148`, `Location.Position==(10,0,20)`, `Expect is PredicateExpect` with `Predicate=="questVariableHigh(65847,1) >= 3"`.

**GWT-F2 missing combat-start position falls back to player position.**
Given `CombatStartPosition==null`, `Position=(5,0,5)`. Then `Location.Position==(5,0,5)`.

**GWT-F3 single-target single-nibble snapshot → kill-set is that target only.**
Given `KillCorrelatedTargets` contains ONLY `(1,High)=([338],3)`. Then `KillEnemyDataIds==[338]` (no residue). (Pins Settled #4/#5 at the factory boundary.)

### 6.4 `UIObserver.PollTargetNpc` + `PollCombat` (plugin tracing) — `QuestForge.Plugin.Tests`

**GWT-U-FWD-3 hostile target forwards `OnBattleNpcTargeted` (new).**
Given a `UIObserver` with a `FakeTargetProbe` whose `GetBattleNpcTarget()` returns `(338,…)` and a `SnapshotAggregator` set; with `combatProbe.SetInCombat(true)` so the aggregator's `_inCombat` is true after the poll. When polls fire: `OnInCombatChanged(true)` then `OnBattleNpcTargeted(338)` reach the aggregator, and `GetTarget {baseId:338, kind:"hostile"}` is written. After a baseline `[0,...]` poll and a `[0,0x30,...]` bump poll, `aggregator.Current.KillCorrelatedTargets[(1,High)].DataIds` contains `338u`. (Replaces GWT-U-FWD-1.)

**GWT-U-T1 hostile `GetTarget` value shape.**
Given `GetBattleNpcTarget()` returns `(347,…)`. When a poll fires. Then exactly one `GetTarget` observation with value object `{ baseId: 347, kind: "hostile" }`, `Argument` null. (Pins the enriched shape D2.)

**GWT-U-T2 EventNpc/Aetheryte target keeps the bare-uint `GetTarget` (back-compat).**
Given `GetBattleNpcTarget()` null and `GetInteractableNpcTarget()` returns an EventNpc `(1000927,…)`. When a poll fires. Then `GetTarget` value is the bare uint `1000927` (no `kind`), and `OnInteraction` is forwarded (NOT `OnBattleNpcTargeted`). (Pins D2 ordering + non-corruption of `LastNpcInteracted`.)

**GWT-U-T3 `PollCombat` no longer forwards `OnEnemyKilled` but still writes the event (D9).**
Given the alive→dead transition setup (poll 1 `(1,347,alive)` in combat → poll 2 `(1,347,dead)`). Then `EnemyKilled {dataId:347}` is still written to the trace, AND `aggregator.Current.KillCorrelatedTargets` is unaffected by the kill (no `(_,_)` entry created from the kill alone). (Pins D9: emission kept, attribution severed.)

*(Existing GWT-U1..U11 — alive→dead transition emission — stay green unchanged.)*

### 6.5 `SnapshotState` (offline) — `QuestForge.Tools.Trace.Tests/SnapshotStateCombatTests.cs`

**GWT-O1 target during span + nibble bump correlates (ev order = production order).**
Given `SnapshotState(65847)`; Apply `InCombat{value:true}`@t0; `GetTarget {baseId:338, kind:"hostile"}`@t1; `GetQuestVariables [0,0x30,0,...]`@t2 (after a prior baseline `[0,...]`); `InCombat{value:false}`@t3. Then `ToSnapshot(t).KillCorrelatedTargets[new NibbleKey(1,NibbleHalf.High)] == ([338],3)`. Each `Apply` returns true. (Mirror of GWT-T1.)

**GWT-O2 wrong-quest GetQuestVariables ignored.**
Given an in-span hostile target then `GetQuestVariables` arg quest 12345. Then no correlation; `Apply` returns true.

**GWT-O3 out-of-combat hostile target ignored.**
Given `GetTarget {baseId:999, kind:"hostile"}` BEFORE `InCombat true`, then in-combat bump with no in-span target. Then no correlation. (Mirror of GWT-T3.)

**GWT-O4 bare-uint `GetTarget` recognised, ignored for combat.**
Given `InCombat true` then `GetTarget` value bare uint `1000927` (npc target) then a bump. Then no correlation (no hostile target in span); `Apply("GetTarget")` returns true (recognised). (Pins the shape discrimination D8; today `GetTarget` is unrecognised → false.)

**GWT-O5 both nibbles in one write → two keys, single target.** Mirror of GWT-T5 over the trace API (baseline via prior `[0x02,...]`, then `[0x13,...]`).

**GWT-O6 ResetPendingKeyItemDeltas clears span, keeps baseline.** Mirror of GWT-T7.

**GWT-O7 InCombat false→true records start zone/position from current state.**
Given `GetPlayerZone{value:148}`, `GetPlayerPosition{x:10,y:0,z:20}`, then `InCombat{value:true}`. Then `CombatStartZone==148`, `CombatStartPosition==(10,0,20)`.

**GWT-O8 `EnemyKilled` recognised, no-op (D9).**
Given an in-span target+bump producing `(1,High)=([338],3)`, then `EnemyKilled {dataId:338}`. Then `KillCorrelatedTargets` UNCHANGED (still `([338],3)`, NOT doubled or altered); `Apply("EnemyKilled")` returns true. (Pins the kill→no-attribution disposition.)

**GWT-O9 mixed-pack span → both targets on each bumped nibble.** Mirror of GWT-T6.

### 6.6 `TraceToQuestExtractor` (offline) — `TraceToQuestExtractorCombatTests.cs`

**GWT-E1 end-to-end target-attributed combat extraction.**
Given a synthetic trace: `run.start` quest 65847; baseline `GetQuestVariables [0,0,0,0,0,0]`; `InCombat true`; `GetTarget {baseId:338,kind:"hostile"}`; `GetQuestVariables [0,0x30,0,...]`; an `EnemyKilled 338` (presence corroborator); a `wait` decision in the combat window; `InCombat false`; `run.end`. Then `Extract` yields a `QuestSequence` whose step is a `CombatStep` with `KillEnemyDataIds==[338]`, `Spawn==OverworldEnemies`, `Expect.Predicate=="questVariableHigh(65847, 1) >= 3"`, plus a TODO mentioning Spawn review.

**GWT-E2 combat `wait` window is not skipped.**
Given the combat window's only decision is `wait`. Then the `CombatStep` is still emitted (the `wait`-skip guard defers to inference — unchanged).

**GWT-E3 uncorrelated span (no hostile target) produces no CombatStep.**
Given `InCombat true`, a nibble bump, but NO `GetTarget {kind:"hostile"}` in the span. Then no `CombatStep`; existing rules drive the window. (Mirror of GWT-T3/O3.)

**GWT-E4 parity: same trace → same expect string live and offline.**
Given the GWT-E1 trace, assert the extractor's `CombatStep.Expect.Predicate` equals the string `StepInferenceEngine.Infer` produces for the equivalent live snapshots (`questVariableHigh(65847, 1) >= 3`). (Pins D8 lock-step; both paths share `StepInferenceEngine`/`StepFactory`.)

**GWT-E5 ambiguous span carries the split Notes/TODO.**
Given a span with `GetTarget {347}` and `GetTarget {49}` (both in combat) and two nibble bumps. Then the emitted `CombatStep` is produced with the ambiguity flagged (TODO/Notes mention "split" and both DataIds). (Mirror of GWT-I5/I6 end-to-end.)

---

## Implementation order

**Slice A — Engine + plugin-tracing: target probe, forwarding, span correlation, inference, factory (CI-gated: `QuestForge.Engine.Tests`; locally: `QuestForge.Plugin.Tests`).**
1. `ITargetProbe.GetBattleNpcTarget()`; `FakeTargetProbe` extension; `DalamudTargetProbe` impl (compiles, in-game).
2. `UIObserver.PollTargetNpc` hostile-first branch; `GetTarget {kind:"hostile"}`; `OnBattleNpcTargeted` forwarding; `PollCombat` stops forwarding `OnEnemyKilled` (keeps writing the event — D9).
3. `SnapshotAggregator`: `OnBattleNpcTargeted`, `_spanBattleNpcTargets`/`_spanNibbleBumps`, span-scoped bump recording, delete `OnEnemyKilled` + kill buffers + window, `ResetDeltas`/`BuildKillCorrelatedTargets` rework.
4. Rule 2.2 dominance-by-highest-value + ambiguity Notes.
5. `StepFactory` "combat" — no change; confirm GWT-F* green.
6. Rewrite/replace GWT-T1..T10, I1..I6, F1..F3 (engine); GWT-U-FWD-3, U-T1..U-T3 (plugin). **Done-before-next:** all green.

**Slice B — Offline mirror (CI-gated: `QuestForge.Tools.Trace.Tests`). Depends on Slice A (engine types).**
1. `SnapshotState`: remove kill→bump mirror; add span sets + `GetTarget {kind:"hostile"}` case; `EnemyKilled`→recognised no-op; `ResetPendingKeyItemDeltas`/`BuildKillCorrelatedTargets` rework.
2. `TraceToQuestExtractor`: verify (no change) the combat branch + `wait`-skip still fire.
3. Rewrite/replace GWT-O1..O9, E1..E5. **Done-before-next:** all green.

**Slice C — Dalamud probe + in-game G7 (NOT CI-gated; requires game).**
1. Land `DalamudTargetProbe.GetBattleNpcTarget()` (already added in Slice A but exercised here); confirm `AuthoringHost` wiring intact.
2. Author 65847 sequence 2 in-game; record each of the three objectives; confirm three drafts: `defeat-347`/`questVariableLow(65847,0)>=3`, `defeat-49`/`questVariableLow(65847,1)>=3`, `defeat-338`/`questVariableHigh(65847,1)>=3`.
3. Replay the recorded trace through `qf-trace extract-quest`; confirm identical `CombatStep`s (DataIds + nibble expect identical to the live drafts).

**Build/test commands (net10 SDK at `C:\Users\publi\.dotnet`):** `questforge` pins net10 via `global.json` (10.0.202); **`questforge-tools` has no `global.json` — prepend the path.**
```bash
# Slice A (questforge)
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge/QuestForge.Engine.Tests
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge/QuestForge.Plugin.Tests   # locally; CI-gated
# Slice B (questforge-tools — no global.json, prepend net10)
DOTNET_ROOT=C:/Users/publi/.dotnet PATH=/c/Users/publi/.dotnet:$PATH \
  dotnet test C:/Users/publi/RiderProjects/questforge-tools/QuestForge.Tools.Trace.Tests
```

---

## PR slicing (small, independently verifiable)

| PR | Repo | Scope | Gate | Depends on |
|---|---|---|---|---|
| **PR-A: live target attribution** | questforge | `feat/combat-target-attribution`: `ITargetProbe.GetBattleNpcTarget` + `DalamudTargetProbe` impl, `PollTargetNpc` hostile branch + `GetTarget {kind:"hostile"}`, `OnBattleNpcTargeted`, `PollCombat` stop-forwarding-kill, `SnapshotAggregator` span correlation (delete kill buffers/window/`OnEnemyKilled`), Rule 2.2 rework. Rewritten GWT-T*/I*/F* + plugin GWT-U-FWD-3/U-T*. | CI: `QuestForge.Engine.Tests` (+ locally `QuestForge.Plugin.Tests`) | — |
| **PR-B: offline mirror** | questforge-tools | paired branch: `SnapshotState` span mirror + `GetTarget {kind:"hostile"}` consumption + `EnemyKilled` no-op, rewritten GWT-O*/E*. | CI: `QuestForge.Tools.Trace.Tests` | PR-A merged (engine types) |
| **PR-C: in-game G7** | questforge | author 65847 sequence 2 in-game; replay parity through `qf-trace extract-quest`. | In-game (no CI) | PR-A + PR-B merged |

Merge order A → B → C. PR-A and PR-B are each independently red→green. PR-C is in-game only.

---

## Done criteria

1. The live aggregator attributes a bumped nibble to the **hostile target acquired during the InCombat span** (via `OnBattleNpcTargeted`), with NO dependency on `EnemyKilled` timing — GWT-T1/T2 green (the target-during-span happy path the merged design's window cannot reproduce in-game). GWT-T3..T10 green.
2. `StepInferenceEngine` Rule 2.2 returns `StepType "combat"` before the sequence/flag/talk/movement rules, with a single `questVariableLow`/`questVariableHigh(activeQuest, idx) >= N` expect, selecting the dominant nibble by highest reached value, and **flagging ambiguity** (Confidence.Low + split Notes) when the span saw >1 hostile target OR >1 nibble bump — GWT-I1..I6 green.
3. `StepFactory` builds a `CombatStep` with the span target-set's `KillEnemyDataIds`, `Spawn=OverworldEnemies`, combat-start `Location`, and the nibble `PredicateExpect` — GWT-F1..F3 green.
4. `PollTargetNpc` emits `GetTarget {baseId, kind:"hostile"}` for a BattleNpc target and forwards `OnBattleNpcTargeted`; EventNpc/Aetheryte targets keep the bare-uint form and forward `OnInteraction`; `PollCombat` still writes `EnemyKilled` but no longer forwards it to the aggregator — GWT-U-FWD-3, U-T1..U-T3 green (locally).
5. Offline `SnapshotState` + `TraceToQuestExtractor` produce the IDENTICAL `CombatStep` (DataIds + nibble expect string) for the same `GetTarget`/`InCombat`/`GetQuestVariables` stream, with `EnemyKilled` recognised-but-ignored and the kill→bump window removed — GWT-O1..O9, E1..E5 green.
6. The `_recentKills`/`_recentNibbleBumps` buffers, `CombatCorrelationWindow`, `EvictStale`/`AbsDelta`/`CorrelateKillsToBump`, and `SnapshotAggregator.OnEnemyKilled` are GONE (asserted by GWT-T7 baseline behaviour, GWT-U-T3, GWT-O8).
7. In-game (PR-C): authoring 65847 sequence 2 produces three correct `CombatStep` drafts; `qf-trace extract-quest` on the recorded trace reproduces them identically. No engine replay-fixture re-record.

---

## Exclusions

- **Schema changes** — none; `CombatStep`/`CombatSpawn`/`NpcLocation` unchanged.
- **The nibble predicate language** — `questVariableLow`/`questVariableHigh` registry/checker/evaluator already merged; this Piece 3 only changes how the DataId attribution is derived; the expect derivation is unchanged.
- **Disambiguating mixed-pack spans** — flagged for author split (Settled #3), never auto-split; one combat step per record, one NPC type (Settled #2).
- **`questVariable` (byte) and `questSequence` combat expects** — already removed in Piece 2; not reintroduced.
- **Kill-count objectives / a future `EnemyKilled`-driven step type** — out of scope; the `EnemyKilled` emission is retained as a corroborator/future hook only (D9).
- **`CombatTaggerId` / player-credit filtering** — deferred (carry-forward from the death-detection plan D8); authoring is solo, and the target signal is already player-specific.
- **Hostility/aggression filtering** — `GetBattleNpcTarget` uses `ObjectKind == BattleNpc`, NOT an `IsHostile`/aggression flag (docile-mob lesson); `GetVisibleHostiles` stays unfiltered.
- **Enemy world-position capture** — `Location` uses player combat-start position only (carry-forward).
- **Engine-side combat execution** — authoring detection only; `CombatController` untouched.
- **The death-state emission** (3-tuple `ICombatProbe`, alive→dead transition, `DalamudCombatProbe`) — retained as-is; only its attribution role is superseded.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §6.
- Happy paths: 6 scenarios (T1, T2, I1, F1, O1, E1)
- Edge cases: 13 scenarios (T4, T5, T6, T7, T8, T10, I4, F2, O5, O6, O7, O9, E2)
- Error/negative cases: 11 scenarios (T3, T9, I3, I5, I6, F3, U-T3, O2, O3, O4, O8, E3, E5) and the forwarding rewrites (U-FWD-3, U-T1, U-T2, E4)
- Expected total: ~34 tests — ~19 in `QuestForge.Engine.Tests` (CombatCorrelationAggregatorTests T1..T10, CombatInferenceEngineTests I1..I6, CombatStepFactoryTests F1..F3), ~4 in `QuestForge.Plugin.Tests` (UIObserver target/forwarding U-FWD-3, U-T1..U-T3), ~14 in `QuestForge.Tools.Trace.Tests` (SnapshotStateCombatTests O1..O9, TraceToQuestExtractorCombatTests E1..E5). Existing GWT-U1..U11 (alive→dead emission) re-run unchanged.

CI-gated: Slice A (`QuestForge.Engine.Tests`) and Slice B (`QuestForge.Tools.Trace.Tests`); `QuestForge.Plugin.Tests` runs locally. In-game-only: Slice C (PR-C) — `DalamudTargetProbe.GetBattleNpcTarget` exercised + 65847 authoring + replay parity.
