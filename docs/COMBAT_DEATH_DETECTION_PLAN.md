# Combat Death-Detection Plan: emit EnemyKilled on the alive→dead transition

**Status:** ready for test creation
**Input docs:** docs/COMBAT_AUTHORING_DETECTION_PLAN.md (Slice 1 origin), docs/COMBAT_NIBBLE_DETECTION_PLAN.md (correlation, unchanged), docs/TRACE_FORMAT.md, CLAUDE.md (Dalamud-free observer, clean-room)
**Target quest:** 65847 — Way of the Marauder. Two combat objectives where corpse-removal kill detection misses kills.
**Output:** `EnemyKilled` is DERIVED from the observable death STATE (alive→dead transition) instead of object-table removal (corpse despawn). CI behaviour change: the locally-gated GWT-U1..U5 combat tests are rewritten around death-state transition semantics and go green against the reworked `PollCombat`; the merged nibble correlation (`SnapshotAggregator` / `SnapshotState`) is untouched and its tests are unaffected. In-game: 65847 re-authored shows both objectives' kills land inside the correlation window.

---

## The problem (diagnosed in-game against quest 65847)

Kill detection currently emits `EnemyKilled` on **object-table removal** (a tracked hostile present last poll, gone this poll, while in combat). Corpse despawn lags the actual death by 0.5–2 s and is gated out when the corpse despawns after combat ends.

- **Objective 1:** variable bumped at `t=13.87`, `EnemyKilled` (corpse-gone) at `t=14.39` — **519 ms later**, outside the 500 ms correlation window. The kill is lost.
- **Objective 2:** variable bumped 3× during combat, **zero `EnemyKilled` fired** — corpses despawned after `InCombat→false`, so the was-in-combat gate dropped every one.

The quest variable increments on the **death frame** (instant, reliably polled). Corpse removal is a laggy, lossy proxy for the same event.

## The fix (decided): death-STATE detection

A dead mob lingers in the object table at HP 0 in a "Dead" state for the death animation (~1–2 s) before despawning. The death state is therefore observable for ~1–2 s — multiple 250 ms polls, not a brief window. Detecting the **alive→dead transition** catches the kill within one poll of the death frame (coincident with the variable bump, inside the 500 ms window) and during the linger (so it is not gated out by a corpse that despawns after combat ends).

---

## Dependency graph

One repo. No schema change. No correlation change.

```
questforge (this repo)
  QuestForge.Plugin.Tracing
    ├── ICombatProbe                 ← shape change: per-hostile death indicator
    └── UIObserver.PollCombat        ← rework: emit on alive→dead transition (REMOVE tracked-then-gone)
  QuestForge.Plugin
    └── Tracing/DalamudCombatProbe   ← add the death-state read (in-game only)
  QuestForge.Plugin.Tests
    ├── Tracing/FakeCombatProbe      ← gains a death indicator
    ├── Tracing/UIObserverCombatTests (GWT-U1..U5)        ← rewritten around transition
    └── Tracing/UIObserverCombatForwardingTests (U-FWD-1/2) ← confirm still holds
```

**Untouched (do NOT modify):** `SnapshotAggregator.OnEnemyKilled(uint dataId)` / `OnInCombatChanged(bool)` and their offline mirror `SnapshotState`; the nibble correlation (`KillCorrelatedTargets`, `CombatCorrelationWindow`); `GameStateSnapshot`; the `ObservationEvent` wire contract for `EnemyKilled` (`{"dataId": <uint>}`) and `InCombat` (`{"value": <bool>}`).

**Build order:** `ICombatProbe` shape + `FakeCombatProbe` death indicator → `PollCombat` rework (locally TDD-able, Slice 1) → `DalamudCombatProbe` death-state read + 65847 in-game re-author/replay (Slice 2, in-game).

---

## Architectural decisions (read before coding)

### D1 — Death tell: explicit dead-state, with HP==0 as a backstop. `Mode == CharacterModes.Dead || IsDead() || CurrentHp == 0`

**Decision.** The probe reports a hostile as dead when an **explicit dead state** is set OR HP has reached 0. Concretely, in `DalamudCombatProbe` the read is:

```
isDead = battleChara.IsDead || battleChara.CurrentHp == 0;
```

where `IBattleChara.IsDead` (the managed surface over the vtable `IsDead()` at `[0x1D0]`) is the primary tell and `CurrentHp == 0` (`[0x1AC] Health`) is the backstop. ClientStructs `Mode == CharacterModes.Dead` (value 2, `[0x2364]`) is equivalent to `IsDead` for our purpose and is NOT read separately — we prefer the managed `IBattleChara` surface and avoid an unsafe ClientStructs dereference where the managed API already answers the question.

**Why this combination.**
- `IsDead()` / `Mode == Dead` is the least ambiguous single tell — it is the game's own death-animation state and stays true through the entire linger, so the transition fires once and stays latched until despawn. It is what we want to key on.
- `CurrentHp == 0` alone is ambiguous: a mob can momentarily read HP 0 for a frame while not yet "dead" (e.g. invulnerability/transition phases, or a boss scripted to survive at 0), and conversely some despawn paths can null the HP read. Using HP alone risks both false positives (phase mobs) and missed latching.
- But HP==0 as a **backstop OR-clause** is cheap insurance: if the managed `IsDead` flips a poll later than HP hits 0 (observed jitter between the two reads), the OR still fires on the earliest of the two. For quest trash mobs (the only thing combat objectives target) there is no "0 HP but alive" phase, so the backstop never produces a false positive in practice; the documented risk (phase-gimmick bosses) is out of scope for authoring combat objectives.

**Rejected alternatives.**
- *HP==0 only.* Rejected: ambiguous (phase mobs at 0 HP, despawn HP nulling) and the very signal we are replacing leans on a fragile read.
- *Mode==Dead only via ClientStructs.* Rejected: forces an unsafe dereference in the probe when `IBattleChara.IsDead` exposes the same bit cleanly; reserve ClientStructs for data the managed API does not surface.
- *Keep corpse-removal (object-table gone).* Rejected — this is the diagnosed bug.

**Testability implication.** The tell is a single `bool IsDead` per hostile flowing through `ICombatProbe`. `PollCombat` transition logic is a pure function of `(ObjectId, IsDead)` sequences and `IsInCombat()` — fully exercisable via `FakeCombatProbe` with no game. The HP-vs-IsDead OR collapses to one bool at the probe boundary, so the boundary stays Dalamud-free and the test surface is tiny.

**What breaks if violated.** If the tell is keyed on object-table presence again, Objective 2's post-combat corpse despawns are gated out and the kills are lost — the original bug returns.

### D2 — `ICombatProbe` shape: add a per-hostile `IsDead` bool

**Decision.** `GetVisibleHostiles()` returns a death indicator per hostile. The tuple becomes `(ulong ObjectId, uint DataId, bool IsDead)`. `IsInCombat()` is unchanged.

```csharp
public interface ICombatProbe
{
    bool IsInCombat();
    IReadOnlyList<(ulong ObjectId, uint DataId, bool IsDead)> GetVisibleHostiles();
}
```

**Why a bool, not raw HP.** The consumer (`PollCombat`) only needs the death *state*, and the death tell (D1) is an OR over multiple reads that only the probe (which has Dalamud access) can evaluate. Surfacing raw HP would (a) push the ambiguous HP==0 reasoning into the Dalamud-free observer and (b) require the observer to also know MaxHealth to avoid divide/threshold logic. Collapsing to a bool at the probe keeps the testability boundary clean (the observer is pure transition logic) and keeps the death-tell decision in one place (the probe).

**`FakeCombatProbe` expresses death.** The fake's `SetHostiles` accepts the 3-tuple so tests can drive `IsDead`:

```csharp
public void SetHostiles(params (ulong ObjectId, uint DataId, bool IsDead)[] hostiles);
// convenience for the common alive case:
public void SetAliveHostiles(params (ulong ObjectId, uint DataId)[] hostiles); // IsDead=false
```

A linger is modelled by leaving the same `(ObjectId, DataId, IsDead:true)` entry in the list across polls; a despawn is modelled by removing it.

**What breaks if violated.** Returning HP instead of a bool leaks the death-tell ambiguity (D1) into the observer and forces a re-record cascade if the HP threshold ever changes; a bool keeps the wire-derivation stable.

### D3 — `PollCombat` keys on a per-ObjectId last-known-dead-state; emit once on first alive→dead

**Decision.** Replace the `_trackedHostiles: Dictionary<ulong, uint>` (ObjectId→DataId, "was present last poll") with **per-ObjectId last-known dead-state plus DataId**:

```csharp
// REMOVE: private readonly Dictionary<ulong, uint> _trackedHostiles = new();
private readonly Dictionary<ulong, CombatHostileState> _hostileStates = new();
private readonly record struct CombatHostileState(uint DataId, bool WasDead);
```

Each poll, for every currently-visible hostile:

1. Compute `now`, `runId`, `wasInCombat = _lastInCombat` (read BEFORE the in-combat transition updates `_lastInCombat`, exactly as today).
2. Look up the prior state for `ObjectId`.
3. Emit `EnemyKilled{dataId}` once **iff** the hostile is dead now (`IsDead == true`), was NOT recorded dead before (prior state absent, or `WasDead == false`), AND `wasInCombat == true`.
4. Record/update `_hostileStates[ObjectId] = (DataId, IsDead)`.
5. After the loop, prune `_hostileStates` entries whose `ObjectId` is no longer visible (despawned). Pruning a despawned-and-already-emitted dead mob produces NO event — the transition already fired on the dead poll.

The `InCombat` direct-write + dedup-on-change block is **unchanged** (still emits `InCombat{value}` once per false→true / true→false and forwards `OnInCombatChanged`). The `wasInCombat` capture must still happen before `_lastInCombat` is mutated so a kill on the same poll combat ends is attributed against the prior in-combat state.

**Why emit-once-on-transition.** The mob lingers dead for ~1–2 s = 4–8 polls. Without the `WasDead` latch we would emit `EnemyKilled` every linger poll, over-counting kills and corrupting correlation. The latch makes the kill a one-shot edge event coincident with the death frame.

**What breaks if violated.** No latch → duplicate kills per mob. Keying on presence (old `_trackedHostiles`) → the diagnosed lag/gate bug.

### D4 — Already-dead-on-first-sight: SKIP (do not emit)

**Decision.** A hostile first observed already dead (no prior alive state recorded) does **not** emit `EnemyKilled`. We record its state as `WasDead = true` so it never emits later either.

**Why.** An alive→dead *transition* is what coincides with the variable bump. A mob that was already dead when first seen (e.g. killed by another player, an ambient death, or a corpse already lingering when the player zoned in / when authoring started mid-fight) has no observable transition under our polling and is not attributable to the player's kill on this poll. Emitting it risks a spurious kill correlated to an unrelated bump. The ~1–2 s linger makes "killed-this-instant-but-first-seen-dead" vanishingly rare; treat it as a documented miss (see D6).

**Edge interaction with `ResetHeartbeatState`.** After a reset (D7), every currently-visible hostile is "first seen" again — so a mob that is dead at the moment of reset is treated as already-dead-on-first-sight and skipped. This matches GWT-U4's no-phantom-kill contract.

### D5 — In-combat gate stays; out-of-combat deaths are not emitted

**Decision.** Keep the gate: only emit when `wasInCombat == true` (the player was in combat as of the prior poll). A hostile that transitions alive→dead while `wasInCombat == false` updates `_hostileStates` silently but emits nothing.

**Why.** The whole point of the linger-based detection is that a mob killed at the end of a fight is still observably dead for several polls *after* `InCombat→false`. So which gate? We gate on **was-in-combat at the prior poll**, identical to today. On the poll where the death is first observed:
- If the death is observed while still in combat (the common case, since the death frame is mid-fight) → `wasInCombat == true` → emit. Correct.
- If `InCombat` flips false on the SAME poll the death is first observed, `_lastInCombat` is still true at the start of the poll (we captured `wasInCombat` before the transition block), so we still emit. This is exactly the Objective-2 fix: the kill that ends the fight is attributed.
- If the death is first observed two-or-more polls after combat already ended (`wasInCombat` already false) → not emitted. This only happens for already-dead-on-arrival cases (D4) or a death we somehow missed during combat; emitting here would risk attributing to nothing.

**What breaks if violated.** Dropping the gate emits kills for ambient/other-player deaths observed while idle, polluting correlation. Gating on *current* (not prior) in-combat re-introduces Objective 2's miss.

### D6 — Sub-poll kills: documented miss, NO laggy fallback

**Decision.** A mob that dies AND despawns within a single 250 ms gap (never observed in a dead state) is a **documented miss**. We do NOT add a gone-transition fallback.

**Why.** The ~1–2 s linger means the dead state spans 4–8 polls; for a kill to be missed the corpse would have to fully despawn in <250 ms, which the in-game struct dump shows does not happen for quest trash. Re-adding the "tracked-then-gone" gone-transition as a fallback would re-introduce exactly the lag/gate noise we are removing (a despawn lags death 0.5–2 s and fires after `InCombat` may have flipped), so the fallback would emit *late, mis-correlated* kills — worse than a clean miss. Keep timing tight; accept the rare miss.

### D7 — `ResetHeartbeatState` clears `_hostileStates`

**Decision.** `ResetHeartbeatState()` clears `_hostileStates` (replacing the `_trackedHostiles.Clear()` line) and continues to reset `_lastInCombat = false`. After reset, all hostiles are "first seen" — alive ones start a fresh alive baseline; dead ones are skipped per D4. No phantom kill can fire on the next poll.

### D8 — `CombatTaggerId` / player-credit filtering: DEFERRED

**Decision.** Out of scope. We do not read `[0x1D5] CombatTagType` / `[0x1D8] CombatTaggerId` to confirm the player tagged the mob.

**Why.** Authoring is solo; ambient/other-player deaths are filtered downstream by the variable-correlation window (a kill only becomes a target if it coincides with a quest-variable bump the player caused). Adding tagger filtering now is premature and would require another ClientStructs read. Note it as a future tightening if multi-actor authoring noise ever appears. The docile-mob lesson still applies: NO hostility/aggression filter in `GetVisibleHostiles` (project_zone memory + the in-game `IsHostile: False`, `NamePlateIconId: 0` Wharf Rat read).

---

## What is REMOVED

- `UIObserver._trackedHostiles : Dictionary<ulong, uint>` and all "tracked last poll, gone this poll, while in combat → emit" logic in `PollCombat` (the `foreach (var (objectId, dataId) in _trackedHostiles)` block and the trailing rebuild of `_trackedHostiles`).
- `FakeCombatProbe`'s 2-tuple `SetHostiles((ObjectId, DataId)[])` signature and `ClearHostiles` semantics that meant "kill" — `ClearHostiles` now means only "all mobs despawned" (which is NOT a kill on its own).
- The 2-tuple `ICombatProbe.GetVisibleHostiles()` return and the matching `DalamudCombatProbe` enumeration that surfaced `(GameObjectId, BaseId)` with no death read.

---

## ICombatProbe / PollCombat / DalamudCombatProbe — concrete changes

### ICombatProbe (`QuestForge.Plugin.Tracing/ICombatProbe.cs`)
Add the `bool IsDead` to the hostile tuple (D2). `IsInCombat()` unchanged.

### PollCombat (`QuestForge.Plugin.Tracing/UIObserver.cs`, ~366–410)
Replace the tracked-then-gone block with the per-ObjectId transition logic of D3/D4/D5. Pseudocode:

```csharp
private void PollCombat()
{
    if (_combatProbe is null) return;

    var wasInCombat = _lastInCombat;          // capture BEFORE the transition block
    var inCombat    = _combatProbe.IsInCombat();
    var now         = _clock.UtcNow;
    var runId       = CurrentRunId;

    if (inCombat != _lastInCombat)            // UNCHANGED InCombat dedup-on-change
    {
        _lastInCombat = inCombat;
        _traceSession.Write(new ObservationEvent(runId, "InCombat", null,
            JsonSerializer.SerializeToElement(new { value = inCombat }, JsonOpts), now));
        _aggregator?.OnInCombatChanged(inCombat);
    }

    var seen = new HashSet<ulong>();
    foreach (var (objectId, dataId, isDead) in _combatProbe.GetVisibleHostiles())
    {
        seen.Add(objectId);
        var hadPrior = _hostileStates.TryGetValue(objectId, out var prior);
        var wasDead  = hadPrior && prior.WasDead;

        // alive→dead transition, gated on prior in-combat; already-dead-on-first-sight is skipped (D4)
        if (isDead && !wasDead && hadPrior && wasInCombat)
        {
            _traceSession.Write(new ObservationEvent(runId, "EnemyKilled", null,
                JsonSerializer.SerializeToElement(new { dataId }, JsonOpts), now));
            _aggregator?.OnEnemyKilled(dataId);
        }

        _hostileStates[objectId] = new CombatHostileState(dataId, isDead);
    }

    // prune despawned hostiles (no event — the transition already fired on the dead poll)
    foreach (var goneId in _hostileStates.Keys.Where(id => !seen.Contains(id)).ToList())
        _hostileStates.Remove(goneId);
}
```

Note the `hadPrior` clause in the emit condition: it enforces D4 (skip already-dead-on-first-sight). A hostile first seen alive (`hadPrior == true, prior.WasDead == false`) that goes dead next poll emits; a hostile first seen dead (`hadPrior == false`) does not.

### DalamudCombatProbe (`QuestForge.Plugin/Tracing/DalamudCombatProbe.cs`)
Enumerate `ObjectKind.BattleNpc`, no hostility filter (D8). For each, cast to `IBattleChara` and read the death tell (D1):

```csharp
foreach (var obj in _objectTable)
{
    if (obj is not IBattleChara bc) continue;
    if (bc.ObjectKind is not ObjectKind.BattleNpc) continue;
    var isDead = bc.IsDead || bc.CurrentHp == 0;
    result.Add((bc.GameObjectId, bc.BaseId, isDead));
}
```

(Exact `IBattleChara` member names verified at build time; `IsDead` and `CurrentHp` are the managed surface confirmed by the struct dump.) In-game / Dalamud-only — not in CI.

---

## Given-When-Then specifications — PollCombat (rewrite GWT-U1..U5)

All scenarios drive the `BuildCombatFixture` UIObserver with `FakeCombatProbe`; one poll = advance clock ≥250 ms then `framework.Tick()`. `EnemyKilled` value must be `{"dataId": <uint>}`, `Argument` null. `InCombat` value `{"value": <bool>}`, `Argument` null.

**GWT-U1 — alive→dead transition while in combat emits exactly one EnemyKilled.**
Given `IsInCombat()==true` and hostile `(obj=1, data=347, IsDead=false)` at poll 1;
When poll 2 has `(obj=1, data=347, IsDead=true)` (same mob now dead, still in combat);
Then exactly ONE `EnemyKilled` with `dataId==347u` is written; ZERO on poll 1 (alive, no transition).

**GWT-U2 — linger does not re-emit.**
Given the U1 setup through poll 2 (one kill emitted);
When polls 3 and 4 still show `(obj=1, data=347, IsDead=true)` (corpse lingering, still in combat);
Then still exactly ONE `EnemyKilled` total (no re-emit per linger poll).

**GWT-U3 — despawn after dead does not re-emit.**
Given the U1 setup through poll 2 (one kill emitted), then a linger poll 3;
When poll 4 has no hostiles (corpse despawned), still in combat;
Then still exactly ONE `EnemyKilled` total (the despawn is silent; the transition already fired).

**GWT-U4 — death observed as combat ends (the Objective-2 fix).**
Given `IsInCombat()==true` and hostile `(obj=1, data=347, IsDead=false)` at poll 1;
When poll 2 has `IsInCombat()==false` AND hostile `(obj=1, data=347, IsDead=true)` (combat flips off the same poll the death is first seen);
Then exactly ONE `EnemyKilled` with `dataId==347u` is written (because `wasInCombat`, captured before the transition block, was still true). Also exactly one `InCombat{value:false}` is written on poll 2.

**GWT-U5 — already-dead-on-first-sight is skipped.**
Given no hostiles tracked yet;
When poll 1 has `IsInCombat()==true` and hostile `(obj=9, data=347, IsDead=true)` never previously seen alive;
Then ZERO `EnemyKilled` (no observable alive→dead transition); `_hostileStates[9].WasDead==true` so a later linger/despawn also emits nothing.

**GWT-U6 — out-of-combat death is not emitted.**
Given `IsInCombat()==false` and hostile `(obj=1, data=347, IsDead=false)` at poll 1;
When poll 2 has `(obj=1, data=347, IsDead=true)` still out of combat;
Then ZERO `EnemyKilled` (was-in-combat gate fails).

**GWT-U7 — InCombat dedup-on-change is unchanged.**
Given `IsInCombat()` returns false→true→true→false across 4 polls (no hostiles);
Then exactly TWO `InCombat` observations: `{value:true}` (poll 2), `{value:false}` (poll 4). Polls 1 and 3 emit none.

**GWT-U8 — multiple deaths same poll emit one EnemyKilled each.**
Given `IsInCombat()==true` and two hostiles `(obj=1,data=347,alive)`, `(obj=2,data=347,alive)` at poll 1;
When poll 2 has both `(obj=1,data=347,IsDead=true)` and `(obj=2,data=347,IsDead=true)`, still in combat;
Then exactly TWO `EnemyKilled` events, both `dataId==347u` (one transition per ObjectId).

**GWT-U9 — revive (dead→alive→dead) emits on each genuine alive→dead.**
Given hostile `(obj=1,data=347)` goes alive(p1)→dead(p2)→alive(p3)→dead(p4), all in combat;
Then exactly TWO `EnemyKilled` (poll 2 and poll 4): the latch is `WasDead`, so an HP-recovery to alive resets it and the second death is a fresh transition. (Rare; documents the latch reset semantics.)

**GWT-U10 — ResetHeartbeatState clears tracking, no phantom kill.**
Given poll 1 in combat with `(obj=1,data=347,IsDead=false)` (state established, `InCombat{true}` emitted);
When `ResetHeartbeatState()` then poll 2 with the SAME hostile present and dead `(obj=1,data=347,IsDead=true)`, in combat;
Then ZERO new `EnemyKilled` (post-reset the mob is first-seen-dead → skipped per D4); AND because `_lastInCombat` reset to false, `InCombat{value:true}` re-emits on poll 2.

**GWT-U11 — no ICombatProbe → no combat observations (back-compat).**
Given UIObserver built WITHOUT a combatProbe (default null);
When several heartbeats fire;
Then ZERO `EnemyKilled` and ZERO `InCombat` observations ever; other pollers unaffected.

### Forwarding (confirm UIObserverCombatForwardingTests still hold)

**GWT-U-FWD-1 — kill forwards OnEnemyKilled to aggregator (updated trigger).**
Same as today, but the kill is triggered by the alive→dead transition (poll 1 `(obj=1,data=347,alive)` → poll 2 `(obj=1,data=347,dead)`), not by clearing hostiles. After a baseline `[0,...]` poll and a `[1,0,...]` bump at the kill tick, `KillCorrelatedTargets[(0,Low)]` contains `347u`. Correlation surface unchanged.

**GWT-U-FWD-2 — InCombat transition forwards OnInCombatChanged.**
Unchanged: false→true poll forwards `OnInCombatChanged(true)`; `aggregator.Current.InCombat==true`, `CombatStartZone==148`, start position captured.

---

## Implementation order

**Slice 1 — locally TDD-able (this is the whole RED/GREEN cycle in CI-gated `QuestForge.Plugin.Tests`).**
A. Change `ICombatProbe.GetVisibleHostiles()` to the 3-tuple; update `FakeCombatProbe` (3-tuple `SetHostiles`, `SetAliveHostiles` convenience). — done before B.
B. Rewrite GWT-U1..U11 + confirm U-FWD-1/2 (Tester writes failing tests from the GWT specs above).
C. Rework `PollCombat` (D3/D4/D5), swap `_trackedHostiles`→`_hostileStates`, update `ResetHeartbeatState` (D7), remove the tracked-then-gone block (Builder). All Slice-1 tests green.
Estimate: ~0.5 day.

**Slice 2 — in-game only (no CI).**
D. Add the death-state read to `DalamudCombatProbe` (D1) — Dalamud-only, compiles but not unit-tested.
E. Re-author quest 65847 in-game; confirm both objectives' `EnemyKilled` land inside the 500 ms correlation window (Objective 1 now coincident with the bump, Objective 2's end-of-fight kills no longer gated out). Replay the recorded trace through the offline path (G7-style) and confirm `CombatStep.KillEnemyDataIds` is reconstructed. Manual.
Estimate: in-game session.

**Folds into / supersedes the in-flight Slice C.** The uncommitted Slice C working tree holds the restored `DalamudCombatProbe` (2-tuple), `AuthoringHost` probe wiring, and a `QuestStatePanel` UI fix. This plan supersedes the 2-tuple probe (Slice 2D replaces it with the death-state read) and keeps the `AuthoringHost` wiring and `QuestStatePanel` fix as-is. Land Slice 1 first (locally testable), then carry the wiring + UI fix + death-read together into the in-game Slice 2.

---

## Done criteria

1. `ICombatProbe.GetVisibleHostiles()` returns `(ulong ObjectId, uint DataId, bool IsDead)`; `_trackedHostiles` no longer exists in `UIObserver`.
2. `PollCombat` emits `EnemyKilled` exactly once on the first alive→dead transition of a tracked hostile, gated on was-in-combat; never on linger, never on despawn, never on already-dead-on-first-sight, never out of combat.
3. GWT-U1..U11 and U-FWD-1/2 pass in `QuestForge.Plugin.Tests` (locally; project is CI-gated).
4. `ResetHeartbeatState` clears `_hostileStates` and `_lastInCombat`; no phantom kill on the next poll.
5. `InCombat` emission and `SnapshotAggregator`/`SnapshotState` correlation are byte-for-byte unchanged (no correlation test edits).
6. In-game (Slice 2): quest 65847 Objective 1 kill correlates (bump and `EnemyKilled` within 500 ms); Objective 2's three kills all correlate despite corpses despawning after combat — both reconstructing `CombatStep.KillEnemyDataIds`.

---

## What this plan does NOT include

- Any change to the nibble correlation (`SnapshotAggregator`, `SnapshotState`, `KillCorrelatedTargets`, the 500 ms window).
- `CombatTaggerId` / player-credit filtering (D8 — deferred).
- A sub-poll gone-transition fallback (D6 — rejected; documented miss).
- Hostility / aggression filtering in `GetVisibleHostiles` (docile-mob lesson — must stay absent).
- `GameStateSnapshot` shape changes or any engine fixture re-record.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Given-When-Then (PollCombat). Rewrite `UIObserverCombatTests.cs` (GWT-U1..U11) around the 3-tuple `FakeCombatProbe` and the alive→dead transition; confirm `UIObserverCombatForwardingTests.cs` (U-FWD-1/2) with the transition trigger.
- Happy paths: 3 scenarios (U1, U7, U-FWD-1/2)
- Edge cases: 6 scenarios (U2 linger, U3 despawn-after-dead, U4 death-as-combat-ends, U8 multi-death, U9 revive, U10 reset)
- Error/negative cases: 3 scenarios (U5 already-dead-skip, U6 out-of-combat, U11 no-probe back-compat)
- Expected total: ~13 tests in QuestForge.Plugin.Tests (CI-gated locally). Slice 2 (DalamudCombatProbe death-read + 65847 in-game re-author/replay) is in-game, not unit-tested.
