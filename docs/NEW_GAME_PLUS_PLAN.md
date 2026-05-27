# New Game+ (NG+) Support Implementation Plan

**Status:** design — S1 BUILT (detection surface live; Dalamud `IsActive` validated in-game). S2 re-based on `IsActive` and ready for test creation. S3 sketched. Awaiting user gate (in-game facts).
**Input docs:** CLAUDE.md, `docs/SCHEMA.md`, `docs/ADAPTERS.md`, clean-room behavioral study of Questionable (NEVER its source — AGPL vs our MIT).
**Output (CI behavior changes):** S1 (done) — a NG+ adapter surface settable on `FakeGameStateProvider`, flowing through record/replay. S2 — engine-level tests prove the engine **runs a single replayed quest off live signals while a replay `IsActive`**, ignoring the lying completed-quest bitmap; the existing non-NG+ `IsQuestComplete → Done` path is preserved (regression test). S3 — completion-by-`+46`-advance and chaining across a chapter (gated on the S3 offset/chaining fix).
**Branch:** `feat/new-game-plus` (off `main`).

---

## Goal (scoped)

Run / accept / complete and **chain** through an entire NG+ chapter (a long quest chain) automatically during a replay. The NG+ *start* is NOT automated (out of scope; its entry mechanism is unknown). Once a replay is running, the engine should follow the game and complete one quest after another until the chapter ends. **S2 delivers the single-quest core; chaining and a clean per-quest completion edge are S3.**

---

## What we know (clean-room + in-game probing, behavior/API only)

1. **NG+ detection is GLOBAL and VALIDATED** — `IsActive` is true iff `IsQuestComplete(3759)` (quest that unlocks NG+) AND the **QuestRedoHud** agent is active (`AgentModule → GetAgentByInternalId(AgentId.QuestRedoHud) → IsAgentActive()`) AND addon `AtkValues[0].UInt ∈ {0,2,3,4}` (replay running). In-game probing confirms `IsActive` reads `true` reliably throughout a replay.
2. **The active replay quest id is NOT reliably readable, and the QuestRedoHud `+46` slot does NOT mean what the original design assumed.** `+46` holds the NEXT replay quest to *accept* (it zeroes the instant that quest is accepted), NOT the in-progress quest. Our `+46` read is also offset-shifted. The in-progress quest is tracked by the **normal `QuestManager`** (live `questSequence` / quest variables). Therefore **S2 must NOT depend on `ActiveReplayQuestId`.** The S1 Dalamud impl returns `CurrentChapter = null` and `ActiveReplayQuestId = null` (validated reads only). Fixing the `+46` offset + deriving a reliable per-quest completion/advance edge is **S3**.
3. **Trustworthy during a replay:** `questSequence(id)` and quest variables (`questVariableLow/High`, `questVariable*`) are LIVE. In-game probing confirms `questSequence` increments live.
4. **LYING:** `isQuestComplete(id)` — the completed-quest bitmap never clears, so it reads `true` for every replay quest the character already finished pre-NG+. In-game probing confirms `isQuestComplete(65847)` reads `true` *during* the replay. This is exactly the bitmap the engine's `IsQuestComplete → Done` gate (`QuestEngine.cs:315-317`) reads — so `/qf run 65847` currently fires `Done` immediately and does nothing.
5. **UNKNOWN:** `isQuestAccepted(id)` under replay. Questionable never relies on it for NG+. Whether it lies must be verified in-game (see §Open Questions); S2 is designed to run the accept step normally and only add defensive machinery if an in-game tell shows it lies.

---

## Current model (verified against the code)

- `QuestEngine.ResolveAction` (`QuestEngine.cs:307-467`) runs exactly ONE fixed `_quest`. Per tick it: reads `GetQuestSequence`; then `IsQuestComplete` → if `true`, returns `Done` immediately (`:315-317`); selects the `QuestSequence` block matching the live sequence (`GetQuestSequence` selection is already live); runs the per-step `Expect`/`SkipIf`/cursor loop. `Done` and `AwaitUser` are terminal (emit `run.end`); `Wait` is non-terminal.
- The engine reads several adapter signals once per tick in a fixed block (`:334-358`): `GetUiState`, `GetPlayerPosition`, `GetPlayerZone`, `GetQuestVariables`. **It does NOT yet read `GetNewGamePlusState`.** S2 adds exactly one such read to that block.
- `EngineHost` (`EngineHost.cs`) owns the run lifecycle: `BeginRun(QuestDefinition, runId, …)` wraps live adapters in recording proxies and calls `_engine.StartQuest` + `_engine.BeginRun`; `TickAsync` calls `_engine.Tick`; `DispatchAction` maps `EngineAction.Done`/`AwaitUser` to `EndRun()`. `EngineAction.Wait` does NOT end the run.
- **NG+ adapter surface exists and S1 is built:** `IGameStateProvider.GetNewGamePlusState` returns `NewGamePlusState(bool IsActive, NewGamePlusChapter? CurrentChapter, bool IsSuspended, QuestId? ActiveReplayQuestId = null)` (`State/IGameStateProvider.cs:24,94-98`). `FakeGameStateProvider.SetNewGamePlusState(NewGamePlusState)` (`FakeGameStateProvider.cs:62,224-229`) is the engine-test seam. `RecordingGameStateProvider`/`ReplayGameStateProvider` handle `GetNewGamePlusState` generically. The Dalamud impl reads/validates `IsActive`; it returns `CurrentChapter=null`, `ActiveReplayQuestId=null` until S3.
- Predicates `isQuestComplete`, `isQuestAccepted`, `questSequence`, `questVariable*` route through `PredicateEvaluator` to `IQuestState`. NG+ does not change these — it changes whether the engine *trusts* `IsQuestComplete` for its terminal gate.

---

## Architectural decisions

### D1 — Detection surface: extend `NewGamePlusState` with `ActiveReplayQuestId` (not a new method)

**Decision:** add one nullable field to the existing record. Keep all QuestRedoHud memory reads isolated in `DalamudGameStateProvider`.

```csharp
public record NewGamePlusState(
    bool IsActive,                         // QuestRedoHud agent active AND IsQuestComplete(3759) AND AtkValues[0] ∈ {0,2,3,4}
    NewGamePlusChapter? CurrentChapter,    // null until S3 (offset fix)
    bool IsSuspended,
    QuestId? ActiveReplayQuestId = null);  // null until S3 (+46 offset-shifted AND wrong semantics — see "What we know" #2)
```

**Why extend the record, not add `GetActiveReplayQuest()`:** IsActive, the chapter, and the active quest id are one atomic snapshot from one source. One record = one consistent observation, and record/replay serializes the whole value with zero proxy changes. (Built in S1.)

**Testability:** the engine learns everything about NG+ through this one record via `FakeGameStateProvider.SetNewGamePlusState(...)` — fully CI-testable. The fragile memory reads live ONLY in `DalamudGameStateProvider` and are validated in-game, never unit-tested. **S2 only consumes `IsActive` and `IsSuspended`; it does NOT consume `ActiveReplayQuestId` or `CurrentChapter` (both null until S3).**

### D2 — Single-quest replay gating: bypass the lying bitmap while a replay is active, drive entirely off live signals

**Decision (re-based on `IsActive`):** the original design keyed the gate bypass on `ActiveReplayQuestId == _quest.Id`. In-game probing invalidated that read (see "What we know" #2). **S2 now keys the bypass on `IsActive` alone.** The user names the quest to run via `/qf run <id>`, so "is this the quest we should be following" is already answered by which `_quest` is loaded — we do not need the unreliable `+46` to identify it.

**Where/how/when the engine reads it:** `ResolveAction` reads `GetNewGamePlusState` **once per tick**, added to the existing per-tick read block (next to `GetUiState`/`GetPlayerPosition`/`GetPlayerZone`/`GetQuestVariables`). It goes through `IGameStateProvider.GetNewGamePlusState` (the only path; engine stays Dalamud-free) and is therefore fully fake-testable via `FakeGameStateProvider.SetNewGamePlusState`. Cadence is **per-tick, not cached** — consistent with the sibling reads, and cheap. **D6 note:** this is a quest-state read, not a hostile-actor scan; it does NOT widen any combat/hostile scan and so cannot starve fixtures the way `GetHostileActors` would. Combat scanning stays step-gated, untouched.

**On adapter failure** (fail-open, matching the sibling reads): treat as **not active** — i.e. fall through to the normal-play branch. A NG+ read failure must never *suppress* the safety gate; the worst case of failing open is normal-play behavior, which is correct when no replay is running.

```csharp
// inside ResolveAction's per-tick read block:
var ngpResult = await _gameState.GetNewGamePlusState(ct);
var ngp = ngpResult is Result<NewGamePlusState>.Success { Value: var v }
    ? v
    : new NewGamePlusState(false, null, false);   // fail-open to normal play

bool replayActive = ngp.IsActive && !ngp.IsSuspended;
```

**Replacement gate (replaces `QuestEngine.cs:315-317`):**

```csharp
if (ngp.IsActive && ngp.IsSuspended)
{
    // Transient: chapter/replay paused. Do not fire Done from the lying bitmap,
    // do not AwaitUser. Hold and re-evaluate next tick.
    return (new EngineAction.Wait("ng+ replay suspended"), null);
}

if (!replayActive)   // normal play (IsActive == false) — UNCHANGED, regression-pinned
{
    var completeResult = await _questState.IsQuestComplete(questId, ct);
    if (completeResult is Result<bool>.Success { Value: true })
        return (new EngineAction.Done(), null);
}
// else: replay active and not suspended → SKIP the IsQuestComplete gate entirely
//       (the bitmap lies true). Fall through to the live-sequence loop below.
```

- **Normal play (`IsActive == false`):** identical to today — `IsQuestComplete → Done` fires. Regression-pinned.
- **Replay active, not suspended:** the `IsQuestComplete` gate is NOT read for the quest being run. Step progression is driven entirely by the LIVE `questSequence`/`questVariable*` predicates in the existing per-step `Expect`/`SkipIf`/cursor loop — **unchanged**. Sequence-block selection already uses live `GetQuestSequence`.
- **Replay suspended (`IsSuspended == true`):** `Wait("ng+ replay suspended")` — transient, never `Done`, never `AwaitUser`. (Resume restores `IsActive && !IsSuspended` and the loop continues; the confirmed-step cursor is not cleared by a suspend tick.)

**Inside the sequence loop nothing changes.** `questSequence(id)` and `questVariable*(id, …)` are LIVE under replay and drive steps exactly as in normal play. The ONLY behavioral change is suppressing the terminal `IsQuestComplete → Done` gate while a replay is active.

### D2a — Completion / run-end for v1 (single-quest test)

With the bitmap gate bypassed and `ActiveReplayQuestId` unavailable (the `+46`-advance completion the old design used is an S3 capability), **the engine has no clean "this quest just finished" edge under replay in S2.** The original design fired `Done` when `+46` advanced away; that signal does not exist yet.

**Decision for v1:** when every step in the matching (final) sequence block is confirmed, `ResolveAction` already returns `EngineAction.Wait("all steps in current sequence satisfied; awaiting game sequence advance")` (`QuestEngine.cs:466`). Under replay, S2 lets this stand: **a fully-played-through quest settles into `Wait` (idle), NOT `Done`.** This is acceptable for the v1 single-quest test because:
- It is **correct, not a heuristic.** `Wait` is non-terminal; the engine simply stops issuing actions once the quest's authored steps are all satisfied. The run does not end, but the automation has done everything the quest definition describes.
- It **invents no fragile completion guess.** Inferring "done" from the lying bitmap, from sequence-equals-some-max, or from a debounce on a signal we cannot yet read would all be guesses. We explicitly reject them.
- The S2 deliverable is "the engine *runs* a single replayed quest through its authored steps off live signals." Run *termination* is an S3 concern bundled with the `+46` offset/advance fix, which is the right signal for it.
- For the manual single-quest test, the operator observes the quest visibly completing in-game and stops the run (`/qf` stop) — no engine `Done` required.

**Rejected alternative — `Done` from sequence reaching a sentinel max:** quest definitions do not carry a reliable "final sequence" sentinel, and FFXIV sometimes advances `questSequence` to a transient post-turn-in value. Firing `Done` on it would be a heuristic that mis-fires; deferred to S3 where the real `+46` edge exists.

### D2b — Accept step under replay: run it normally; defensive confirmation is a CONDITIONAL follow-up, not S2 core

The authored 65847 accept step's `Expect` is `isQuestAccepted(65847)`, whose reliability under replay is UNKNOWN. **We do NOT pre-build defensive machinery on a guess** (per the "don't build on unknowns" discipline).

**S2 behavior:** run the accept step normally. Its `Expect` (`isQuestAccepted(65847)`) is evaluated exactly as authored, through the existing `PredicateEvaluator` → `IQuestState` path. No engine special-casing of the accept step.

**The exact in-game tell that decides whether a follow-up is needed:** during the single-quest in-game test, watch whether the accept step **confirms** (its cursor advances and the engine proceeds to the next step) once the quest is accepted in the replay:
- **If `isQuestAccepted` is LIVE under replay** → the accept step confirms normally → **no follow-up needed; S2 is complete as authored.**
- **If `isQuestAccepted` lies** (reads `false` after accept, OR reads `true` before the player accepts) → the engine either **loops re-accepting** (predicate never confirms) or skips the accept prematurely. THIS is the tell.

**Deferred defensive fix (only if the tell fires):** confirm the accept step once the LIVE `questSequence(id)` advances past the accept step's sequence — i.e. treat "live sequence moved beyond the accept point" as proof the accept took. Concretely this is an authoring rule (write the accept step's `expect` as `questSequence(id) >= N`, which is live and trustworthy under both normal play and replay), NOT engine code. Documented here as a conditional follow-up so it is not built speculatively.

### D3 — Chaining + per-quest completion edge: deferred to S3 (needs the `+46` offset fix)

**Decision unchanged in shape, but its prerequisite is now explicit:** the engine remains single-quest; chaining is host-driven in `EngineHost`. **However, S3 cannot follow `+46` until the offset is fixed and its semantics (next-to-accept vs in-progress, zeroing-on-accept) are reconciled into a reliable per-quest completion/advance edge.** S3 owns: (1) fixing the `+46` offset in `DalamudGameStateProvider`, (2) deriving a trustworthy completion edge for the running quest, (3) `EngineHost` swapping the loaded quest by id via the existing `TryLoadQuest`, (4) per-quest run boundaries, (5) `AwaitUser` on a missing chapter quest definition. `Chain.Next` stays metadata only; the game's advance signal is authoritative. No engine dependency on `Chain`.

### D4 — Engine stays Dalamud-free; minimal surface

No new adapter interface, no new engine abstraction beyond reading the existing `GetNewGamePlusState` per tick and the `IsActive`/`IsSuspended` gate in D2. Host orchestration (D3, S3) lives in `EngineHost`. No speculative "NG+ controller" type. The S2 change is a single added per-tick read plus a re-shaped terminal gate.

---

## Validation / behavior table (S2)

| Condition (per tick) | `IsActive` | `IsSuspended` | Engine terminal decision | Notes |
|---|---|---|---|---|
| Normal play | false | — | existing `IsQuestComplete` gate (`Done` if bitmap true) | non-NG+ path preserved (regression) |
| Replay active, quest mid-play | true | false | run live sequence; **never read `IsQuestComplete`**; per-step actions via live `questSequence`/`questVariable*` | bitmap suppressed |
| Replay active, all steps confirmed | true | false | `Wait("all steps … satisfied; awaiting game sequence advance")` | v1 idles; clean `Done` is S3 (`+46` edge) |
| Replay suspended | true | true | `Wait("ng+ replay suspended")` | transient; not `Done`, not `AwaitUser` |
| NG+ read adapter failure | (fail-open) | — | normal-play branch | failure must never suppress the gate |

`ActiveReplayQuestId` / `CurrentChapter` are NOT consulted by S2 (both null until S3).

---

## Slicing (ROI order)

| Slice | Scope | Unit-testable? | Gated on |
|---|---|---|---|
| **S1** (BUILT) | Detection surface: `NewGamePlusState` carries `ActiveReplayQuestId`/`CurrentChapter`; fake setter; record/replay round-trip; Dalamud impl validates `IsActive` (returns chapter/quest-id null). | Engine/fake/record/replay: YES (green). Dalamud impl: in-game validated. | nothing |
| **S2** | Engine runs a SINGLE replayed quest off live signals (D2): **bypass `IsQuestComplete` gate when `IsActive && !IsSuspended`**; `Wait` on suspended; preserve normal-play gate; fully-confirmed final sequence idles to `Wait` (D2a); accept step runs as authored (D2b). | YES — fully via `FakeGameStateProvider` + `FakeQuestState`. | S1 |
| **S3** | Fix `+46` offset + reliable completion/advance edge; `EngineHost` chains by id (reuse `TryLoadQuest`); per-quest run boundaries; `AwaitUser` on missing definition; exit on `IsActive=false`; clean per-quest `Done`. | Host orchestration: partly (a pure swap-decision helper); end-to-end needs in-game + authored chapter JSON. | S2 + `+46` offset fix + authored chapter JSON |

**Why this order:** S1 is built. **S2 is the immediately useful, fully-unit-testable piece**: it lets the engine play *any one* replayed quest off live signals with only that quest's existing definition — no chapter authoring and no reliable `+46` read required — and it contains the bitmap-lie hazard, so it gets the most coverage. S3 is last: it is gated on the `+46` offset fix, in-game advance-timing validation, and chapter JSON authoring.

GWT specs for **S2** are detailed below; **S3 is sketched**. (S1 specs remain in git history / the built tests `NewGamePlusStateS1Tests.cs`.)

---

## S2 — Given/When/Then (engine against fakes)

All tests in `QuestForge.Engine.Tests`, driving `QuestEngine` with `FakeGameStateProvider` + `FakeQuestState`. Quest under test: a minimal single-block quest (e.g. a 2-step talk/turn-in like the existing fixtures), id `Q`. Each test sets NG+ state via `FakeGameStateProvider.SetNewGamePlusState`. **S2 keys only on `IsActive`/`IsSuspended`; `ActiveReplayQuestId` is irrelevant and tests leave it null.**

**Normal-play regression (gate preserved):**
- **S2.1 (normal play, complete bitmap → Done)** — Given `IsActive==false` and `FakeQuestState.IsQuestComplete(Q)==true`; When `StartQuest(Q); BeginRun; Tick`, Then `EngineAction.Done` (existing behavior unchanged).
- **S2.2 (normal play, not complete → runs sequence)** — Given `IsActive==false`, `IsQuestComplete(Q)==false`, sequence 0 active with an unconfirmed talk step; When `Tick`, Then a step action (e.g. `Navigate`/`Interact`), NOT `Done`.

**Replay gating (bitmap suppressed off `IsActive`):**
- **S2.3 (replay active, bitmap lies true → NOT Done, runs the step)** — Given `IsActive==true`, `IsSuspended==false`, AND `FakeQuestState.IsQuestComplete(Q)==true` (the lie); sequence 0 active with an unconfirmed step; When `Tick`, Then the engine produces the **step action**, NOT `Done`. (Core hazard test: the bitmap says complete, the active replay says keep going.)
- **S2.4 (replay drives via live questSequence)** — Given `IsActive==true`, `IsSuspended==false`, `IsQuestComplete(Q)==true` (lie), `GetQuestSequence(Q)==1`, and the quest has a sequence-1 block with one unconfirmed step; When `Tick`, Then the engine dispatches that sequence-1 step's action (proves live `questSequence` selects the block while the bitmap is ignored).
- **S2.5 (replay active, all steps confirmed → Wait, not Done)** — Given `IsActive==true`, `IsSuspended==false`, `IsQuestComplete(Q)==true` (lie), the matching sequence block's only step already confirmed (its `Expect` satisfied by a live predicate, e.g. `questVariable*`/`questSequence`); When `Tick`, Then `EngineAction.Wait` whose reason mentions "awaiting game sequence advance"; **NOT `Done`** (D2a — v1 idles, no fragile completion heuristic).
- **S2.6 (replay suspended → Wait, not Done)** — Given `IsActive==true`, `IsSuspended==true`, `IsQuestComplete(Q)==true`; When `Tick`, Then `EngineAction.Wait` with reason mentioning "suspended"; NOT `Done`, NOT `AwaitUser`.
- **S2.7 (suspended then resumed continues)** — Given `IsActive==true`, sequence 0, step unconfirmed; tick 1 with `IsSuspended==true` → `Wait("ng+ replay suspended")`; tick 2 with `IsSuspended==false` → the step action. (Suspension is transient; the confirmed-step cursor is not lost.)
- **S2.8 (NG+ read failure fails open to normal play)** — Given `FakeGameStateProvider` configured to fail `GetNewGamePlusState` (adapter failure) and `IsQuestComplete(Q)==true`; When `Tick`, Then `EngineAction.Done` (fail-open: a NG+ read failure must never suppress the gate). *(If the fake cannot yet simulate a failed `GetNewGamePlusState`, this is an open item — see §Open Questions; otherwise assert the normal-play branch is taken.)*

**Accept step under replay (`isQuestAccepted` UNKNOWN — run as authored):**
- **S2.9 (accept step runs as authored under replay)** — Given a quest whose first step is an accept step with `expect: "isQuestAccepted(Q)"`; `IsActive==true`, `IsSuspended==false`, `IsQuestComplete(Q)==true` (lie), and `FakeQuestState.IsQuestAccepted(Q)==true`; When `Tick`, Then the accept step is confirmed (skipped) and the engine proceeds to the next step — proving the engine does NOT special-case the accept step and the live `isQuestAccepted` (when truthful) confirms it normally.
- **S2.10 (accept step NOT prematurely completed by the lying bitmap)** — Given the same accept-step quest; `IsActive==true`, `IsSuspended==false`, `IsQuestComplete(Q)==true` (lie), but `FakeQuestState.IsQuestAccepted(Q)==false`; When `Tick`, Then the engine issues the accept step's action (it does NOT confirm-and-skip via the bitmap, and does NOT return `Done`). This pins that the suppressed bitmap cannot shortcut an unaccepted quest.

**Live step progression independent of NG+ field clutter:**
- **S2.11 (ActiveReplayQuestId null does not affect S2)** — Given `IsActive==true`, `IsSuspended==false`, `ActiveReplayQuestId==null` (as the Dalamud impl returns), `IsQuestComplete(Q)==true` (lie), sequence 0 unconfirmed step; When `Tick`, Then the step action (proves S2 does NOT read `ActiveReplayQuestId`).

---

## S3 — Given/When/Then (sketch — `+46` offset fix, completion edge, chaining)

Deferred. S3 must first fix the `+46` offset and establish a reliable completion/advance edge in `DalamudGameStateProvider` (in-game validated), then add host-level chaining. Extract a pure `NgpChainDecision` helper so the swap logic is unit-testable without Dalamud.

- **S3.0 (`+46` offset + semantics fixed)** — `DalamudGameStateProvider.GetNewGamePlusState` returns a reliable `ActiveReplayQuestId`/`CurrentChapter` and a derived per-quest completion edge; in-game validated. (Not unit-tested.)
- **S3.1 (clean per-quest `Done`)** — Given the running quest's completion edge fires (the reliable S3 signal), Then the engine returns `Done` (replaces the v1 `Wait`-idle from D2a).
- **S3.2 (swap on advance)** — Given currently running quest `Q`; When the chapter advances to `R` and `TryLoadQuest(R)` returns a definition, Then host calls `EndRun()` (run.end `done` for `Q`) then `BeginRun(R, newRunId)` — one run per chapter quest.
- **S3.3 (missing definition → AwaitUser)** — Given advance to `R` and `TryLoadQuest(R)` returns null, Then `AwaitUser("ng+ chapter advanced to quest R but no quest definition is available")`; no silent skip.
- **S3.4 (chapter ends → stop)** — Given `IsActive` goes false, Then `EndRun()` and leave NG+-replay mode cleanly.
- **S3.5 (run boundaries per quest)** — Across a 3-quest chapter, exactly 3 `run.start`/`run.end` pairs, each with a distinct runId and quest id; each independently replayable.

Detailed GWT + the `NgpChainDecision` helper signature to be authored when S3 is greenlit (after S2 lands and the in-game `+46` offset/advance-timing probe confirms the edge).

---

## Implementation order

**Phase A — S1 detection surface (DONE).** `NewGamePlusState` carries `ActiveReplayQuestId`/`CurrentChapter`; record/replay round-trip green; Dalamud impl validates `IsActive` (returns chapter/quest-id null).

**Phase B — S2 engine gate (1–1.5 days, TDD).** Add one `GetNewGamePlusState` read to `ResolveAction`'s per-tick read block (fail-open). Compute `replayActive = IsActive && !IsSuspended`. Replace the `IsQuestComplete → Done` gate (`:315-317`) with the D2 branch (suspended → `Wait`; normal play → existing gate; replay active → skip the gate, run the live loop). No change to the per-step loop or completion (`Wait` at `:466` stands, per D2a). Write/await S2.1–S2.11. Done before S3 is greenlit.

**Phase C — S3 (separate PR, after S2 + in-game `+46` probe).** `+46` offset fix + completion edge + `EngineHost` NG+-replay mode + `NgpChainDecision` helper + per-quest run boundaries. Gated on authored chapter JSON.

Each slice ships as its own PR. S2 is independently mergeable and unit-testable today; S3 awaits the gate.

---

## Done criteria

1. (S1, done) `NewGamePlusState` carries `ActiveReplayQuestId`/`CurrentChapter`; record/replay round-trips them; Dalamud `IsActive` validated in-game.
2. Under detected replay (`IsActive && !IsSuspended`), the engine **never returns `Done` from the lying `IsQuestComplete` bitmap** for the quest being run; it runs the live sequence instead (S2.3 green). `/qf run 65847` no longer no-ops under replay.
3. Step progression under replay is driven by live `questSequence`/`questVariable*` exactly as in normal play (S2.4 green).
4. A fully played-through quest under replay settles into `Wait` (idle), NOT a fragile heuristic `Done` (S2.5 green; clean `Done` is S3).
5. `Wait` (not `Done`/`AwaitUser`) while suspended, and progress resumes after un-suspend (S2.6, S2.7 green).
6. **Normal (non-NG+) play is unchanged**: `IsActive==false` → existing `IsQuestComplete → Done` gate fires (S2.1 green; regression). A NG+ read failure fails open to normal play (S2.8).
7. The accept step runs as authored under replay; the suppressed bitmap cannot shortcut an unaccepted quest (S2.9, S2.10 green).
8. D6 (no spurious hostile scans) and combat/handoff/resume behavior are unaffected — the NG+ read is a quest-state read, not a scan; no new per-tick hostile scan; existing combat tests stay green. **The 936 green tests stay green.**
9. (S3, deferred) `+46` offset fixed; clean per-quest `Done`; `EngineHost` chains by id; per-quest run boundaries; `AwaitUser` on missing definition.

---

## Exclusions

- **NG+ start automation** — out of scope (entry mechanism unknown).
- **`ActiveReplayQuestId` / `+46` as an S2 signal** — explicitly NOT used by S2 (offset-shifted + wrong semantics: it is the next-to-accept quest, zeroing on accept). Fixing it is S3.
- **A clean per-quest `Done` under replay in S2** — deferred to S3 (needs the `+46` completion edge); S2 idles to `Wait` when steps are exhausted (D2a). No completion heuristic.
- **Chapter chaining + by-id quest swap** — S3, gated on the `+46` fix and authored chapter JSON.
- **Defensive accept-step machinery** — NOT built in S2; the accept step runs as authored. A `questSequence`-based confirmation is a CONDITIONAL follow-up, triggered only by the in-game tell in D2b (accept step fails to confirm).
- **Verifying `isQuestAccepted` semantics under replay** — escalated as an in-game observation tied to the accept-step tell (D2b); not a precondition for S2.
- **Memory-offset robustness beyond isolation** — QuestRedoHud offsets live in one `DalamudGameStateProvider` helper; patch-day breakage is a known, localized risk, not unit-tested.

---

## Open questions (escalated for the user's gate — require in-game facts)

1. **Accept-step tell (D2b):** with a replay running and `/qf run 65847`, does the authored accept step (`expect: isQuestAccepted(65847)`) **confirm and advance** once the quest is accepted? If YES → `isQuestAccepted` is LIVE, no follow-up. If NO (engine loops re-accepting, or skips the accept prematurely) → add the deferred `questSequence(id) >= N` confirmation. **This decides whether the conditional follow-up ships.**
2. **`+46` offset + advance/completion edge (S3 gate).** What is the correct offset, and at what instant does the in-progress quest's completion become observable (given `+46` is next-to-accept and zeroes on accept)? Determines the S3 completion edge and whether it needs debounce. **Probe before S3; does not block S2.**
3. **Suspended semantics** — what user action causes `IsSuspended==true` (opening the QuestRedoHud menu, leaving the replay temporarily)? Confirms `Wait`-not-`Done` is correct and that resume restores `IsActive && !IsSuspended`. **Affects S2.6/S2.7 assumptions.**
4. **AtkValues `{0,2,3,4}` meaning** — confirm which value(s) map to `IsActive` vs `IsSuspended` so `DalamudGameStateProvider` sets the record fields correctly. **In-game, Dalamud impl only.**
5. **Can `FakeGameStateProvider` simulate a failed `GetNewGamePlusState`?** S2.8 (fail-open) needs the fake to return a `Result.Failure` for that read. If the fake has no failure-injection seam for NG+, either add a minimal one or drop S2.8 to a documented-but-untested invariant. **Tester gate for S2.8 only.**

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §S2.
- Happy paths: 3 scenarios (S2.2, S2.4, S2.9)
- Edge cases: 5 scenarios (S2.3, S2.5, S2.6, S2.7, S2.11)
- Error/regression cases: 3 scenarios (S2.1, S2.8, S2.10)
- Expected total: ~11 tests in `QuestForge.Engine.Tests` (S2 engine gate). S1 tests are already built (`NewGamePlusStateS1Tests.cs`). S3 specs (~6) deferred to a follow-up PR after the in-game `+46`/accept gate.
