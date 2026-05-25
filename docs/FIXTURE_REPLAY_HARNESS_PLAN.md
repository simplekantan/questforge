# Generic Trace-Replay Fixture Harness Implementation Plan

**Status:** ready for test creation
**Input docs:** docs/FIXTURES.md, docs/TRACE_FORMAT.md, docs/NEXT_STEPS.md (Phase 7/10/11), the existing replay infra in `QuestForge.Adapters.Fakes/Replay/`, `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs`.
**Output:** adding an engine regression fixture to CI requires committing only **two data files** — a fixture JSON + its source `.jsonl` trace — with **no hand-written per-fixture state machine**. The existing parametric `EngineFixtureTests` theory gains a generic fallback path (`TraceReplayFixtureState`) that replays the source trace's recorded observations through the *current* engine and asserts the engine's decisions match the fixture's `expectedTransitions`. The scripted `simple-linear-acceptance` fixture keeps passing unchanged. One proof fixture is added via the generic path to validate the harness end-to-end. The tools-side extractor (`questforge-tools`) is extended to emit/copy the source trace next to the fixture and to recognise all current step shapes.
**Spans three repos:** `questforge` (engine-test harness + inert no-op adapters in `QuestForge.Adapters.Fakes` + FIXTURES.md doc), `questforge-tools` (extractor `FilenameLookup` + trace co-emission), `questforge-data` (fixture + trace file layout).

---

## 1. Summary and scope

### Problem

Today the parametric `EngineFixtureTests` theory (in `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs`) discovers every `questforge-data/fixtures/engine/*.json` and runs the engine against it. But for each fixture it needs a **hand-scripted fake state machine** — currently `SimpleLinearAcceptanceState`, the only entry in the `StateFactories` dispatch dictionary. That state machine manually calls `GameState.SetPosition(...)`, `QuestState.SetQuestSequence(...)`, etc. on each `OnTick` to flip predicates at exactly the right moment so the engine produces the expected transitions. Writing one of these by hand per fixture is the bottleneck that blocks adding fixtures at scale. A fixture without a registered state machine is silently `Assert.Skip`-ped (`Quest66130ReplayTests.cs:106-107`).

### Fix

Add a **generic** `TraceReplayFixtureState` that requires no per-fixture code. It is constructed from a committed source `.jsonl` trace: it feeds the trace's recorded **observations** (engine *inputs*) into `ReplayGameStateProvider` / `ReplayQuestState`, and wires the engine's **action-side** adapters (Navigator/Teleporter/Interactor/Combat/Gear/Minigames/Dialogue) as **inert no-ops** that never mutate state (because post-action state already lives in the recorded observations). The engine then ticks, reading recorded inputs, and its *outputs* (the `DecisionEvent`s captured by `CapturingTraceWriter`) are asserted against the fixture's `expectedTransitions` exactly as the scripted path does today.

This plan delivers exactly seven things:

1. An `IFixtureState` interface in `QuestForge.Engine.Tests/Replay/States/`, implemented by both the refactored `SimpleLinearAcceptanceState` and the new `TraceReplayFixtureState`.
2. A set of genuinely **inert no-op action adapters** in `QuestForge.Adapters.Fakes/Replay/` (the existing `FakeNavigator`/`FakeTeleporter`/`FakeInteractor` mutate a `FakeGameStateProvider`/`FakeQuestState` — they cannot drive a `ReplayGameStateProvider`, so we need new ones).
3. `TraceReplayFixtureState`, built from a source trace via `TraceReader` + `ReplayGameStateProvider` + `ReplayQuestState` + the inert no-ops.
4. A dispatch change in `EngineFixtureTests`: when no `StateFactories` entry exists, fall back to the generic `TraceReplayFixtureState` **iff** a source trace is present; otherwise `Assert.Skip`. Starvation produces an actionable error distinguishing "engine decision regression" from "observation starvation / re-record needed".
5. The `sourceTrace` linkage decision (a filename convention, see §3.1) documented in FIXTURES.md, with a fixture-format minor version bump to `1.1.0`.
6. One **proof fixture** added via the generic path: a real in-game `done` trace of quest **65644** ("Close to Home"), with 65644 itself added to questforge-data (`quests/arr/msq/65644-close-to-home.json`, validated by `qf-validate`). The scripted `simple-linear-acceptance.json` (66130) stays alongside it — §3.9/§8.
7. Tools-side parity in `questforge-tools`: expand `TraceToFixtureExtractor.FilenameLookup` to current step shapes, optionally co-emit the source trace, and sync the FIXTURES.md actionType table.

### 1.1 Why this is a real regression test (not a tautology)

The source `.jsonl` trace supplies the recorded **observations** — the engine's *inputs* (what the game told the engine: player position, quest sequence, UI state, etc.). The fixture's `expectedTransitions` are the recorded **decisions** — the engine's *outputs* at record time. The harness replays the recorded **inputs** through the **current** engine and compares its **outputs** to `expectedTransitions`.

- The trace (inputs) is **immutable ground truth** — a recording of what the game presented.
- The engine is the **code under test**.

If engine logic regresses (a predicate flips at the wrong time, a step dispatches a different action, a transition is dropped), the current engine's outputs diverge from the recorded `expectedTransitions` → the fixture fails. If engine logic is unchanged, the outputs match → it passes. This is a genuine regression test, not a round-trip of the same data: the inputs and the asserted outputs come from different channels of the same recording, and only the inputs are fed back in.

### 1.2 Fixed design decisions (do not relitigate)

- **Generic harness chosen** over per-fixture scripted state or tool-only polish.
- **Reuse** the existing production `ReplayGameStateProvider`, `ReplayQuestState`, `ObservationScanner`, `TraceReader` (all in `QuestForge.Adapters.Fakes/Replay/`, all unit-tested). Compose, do not rebuild.
- The source trace is committed **alongside** the fixture in `questforge-data`.

---

## 2. Confirmed component inventory (verified against the codebase)

| Component | Location | Role in the harness |
|---|---|---|
| `EngineFixtureTests` (class; legacy filename) | `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs` | The parametric theory to extend. `AllEngineFixtures()` discovers fixtures; `StateFactories` dispatch dict; `ActionTypeString` maps `EngineAction`→string. |
| `SimpleLinearAcceptanceState` | `QuestForge.Engine.Tests/Replay/States/SimpleLinearAcceptanceState.cs` | The one scripted state; will implement the new `IFixtureState`. Exposes `GameState`/`QuestState`/`Navigator` + `OnTick`. |
| `ReplayGameStateProvider(IReadOnlyList<ObservationEvent>)` | `QuestForge.Adapters.Fakes/Replay/ReplayGameStateProvider.cs` | `IGameStateProvider`; each method consumes the next matching observation via `ObservationScanner`. |
| `ReplayQuestState(IReadOnlyList<ObservationEvent>)` | `QuestForge.Adapters.Fakes/Replay/ReplayQuestState.cs` | `IQuestState`; same scanner-backed pattern. |
| `ObservationScanner` | `QuestForge.Adapters.Fakes/Replay/ObservationScanner.cs` | Matches by `(method, serialized-arg)`, scan-forward + last-seen fallback; throws `ReplayObservationStarvationException` only when **no** observation was ever seen for a `(method,arg)` pair. |
| `TraceReader.ReadFile(path)` / `ReadFile<T>(path)` | `QuestForge.Adapters.Fakes/Replay/TraceReader.cs` | Reads JSONL → `IReadOnlyList<TraceEvent>` (polymorphic via `TraceEventJsonContext`); generic overload filters by subtype. |
| `ReplayObservationStarvationException` | `QuestForge.Adapters.Fakes/Replay/ReplayExceptions.cs` | Thrown on true starvation. The harness must catch this and translate it to an actionable failure. |
| `CapturingTraceWriter` | `QuestForge.Engine.Tests/Replay/CapturingTraceWriter.cs` | `ITraceWriter` buffering every `TraceEvent`; the harness reads the `DecisionEvent`s out of it. |
| `FixtureLocator.TryGetQuestForgeDataRoot()` | `QuestForge.Engine.Tests/Replay/FixtureLocator.cs` | Walks up from `AppContext.BaseDirectory` to find `questforge-data`; returns null when absent (→ theory yields no cases → skip). |
| `ObservationEvent(RunId, Method, Argument, Value, At)` | `QuestForge.Adapters/Tracing/ObservationEvent.cs` | `Type => "observation"`. The recorded engine inputs. |
| `DecisionEvent(RunId, StepId, ActionType, At)` | `QuestForge.Adapters/Tracing/DecisionEvent.cs` | `Type => "decision"`. The recorded engine outputs. |
| `RunStartEvent(RunId, QuestId, QuestSchemaId, At)` / `RunEndEvent(RunId, Outcome, At)` | `QuestForge.Adapters/Tracing/` | Run boundaries. `Outcome` ∈ `"done"`/`"awaitUser"`. |

**Action-side fakes that are SAFE to reuse in replay (they hold no game/quest state):** `FakeCombat`, `FakeGearManager`, `FakeMinigameSkipper`, `FakeDialogueResolver`, `FakeTimingProfile`. Verified: none of these touch a `FakeGameStateProvider`/`FakeQuestState`.

**Action-side fakes that are UNSAFE in replay (they mutate state):**
- `FakeNavigator` — `NavigateTo` calls `_state.SetPosition(destination)` on arrival (`FakeNavigator.cs:85`). Constructor takes a `FakeGameStateProvider`.
- `FakeTeleporter` — `TeleportToAetheryte` calls `_state.SetZone`/`SetPosition` (`FakeTeleporter.cs:60-61`). Constructor takes a `FakeGameStateProvider`.
- `FakeInteractor` — `AcceptQuest`/`CompleteQuest` mutate `_questState` (`FakeInteractor.cs:166-168, 176-177`). Constructor takes both providers.

Because `ReplayGameStateProvider`/`ReplayQuestState` are **not** `FakeGameStateProvider`/`FakeQuestState`, the mutating fakes cannot be wired against them anyway, and even if they could, mutation would corrupt the replay (post-action state must come from the recorded observations, not from a fake re-deriving it). Hence the **inert no-op adapters** in §4.

---

## 3. Fixed design decisions (the eight questions)

### 3.1 Q1 — Source trace location & linkage: **filename convention, sibling of the fixture**

**Decision:** the source trace lives **next to** the fixture, same basename, with extension `.trace.jsonl`:

```
questforge-data/fixtures/engine/
  simple-linear-acceptance.json     # existing scripted fixture (no trace; stays scripted)
  with-attunement.json             # proof fixture — real 65644, generic replay path
  with-attunement.trace.jsonl      # its source trace (real run, filtered to one runId)
```

The harness derives the trace path from the fixture path:

```csharp
// fixturePath = ".../fixtures/engine/<name>.json"
// tracePath   = ".../fixtures/engine/<name>.trace.jsonl"
var tracePath = Path.ChangeExtension(fixturePath, null) + ".trace.jsonl";
//             == Path.Combine(dir, fixtureName + ".trace.jsonl")
var hasTrace  = File.Exists(tracePath);
```

**Why convention over a `sourceTrace` JSON field:**
- **No format dependency for the common case.** A scripted fixture (`simple-linear-acceptance.json`) has no trace; a generic fixture has one. The presence/absence of `<name>.trace.jsonl` is the single source of truth. We do not need every fixture to carry a `sourceTrace: null`.
- **Self-locating.** The harness already has the fixture's absolute path; deriving the sibling path needs no parsing and no questforge-data root re-resolution.
- **Atomic commit.** A generic fixture is always two files with the same basename; reviewers see them together in a diff.

**But we ALSO add an optional `sourceTrace` field (minor bump to `1.1.0`)** as an *override* + explicitness mechanism — see §3.5/§7. The override exists so a fixture in a `traces/` subfolder, or sharing a trace with another fixture, is expressible later. Resolution order in the harness:
1. If the fixture JSON has a non-null `sourceTrace`, resolve it **relative to the questforge-data root** (forward slashes, exact case).
2. Otherwise, the `<name>.trace.jsonl` sibling convention.

**Path case-sensitivity (CI is Linux):** the `.trace.jsonl` sibling is derived from the fixture's own on-disk path, so its case always matches (no author-typed case to get wrong). When the explicit `sourceTrace` field is used, the same rule as `questFile` applies — it must match the on-disk case exactly; document this in FIXTURES.md alongside the existing `questFile` case note (FIXTURES.md line ~227).

### 3.2 Q2 — Common state abstraction: `IFixtureState`

**Decision:** introduce `IFixtureState` in `QuestForge.Engine.Tests/Replay/States/IFixtureState.cs`, exposing the **full** adapter set the `QuestEngine` constructor needs, plus `OnTick`. Both `SimpleLinearAcceptanceState` (refactored) and `TraceReplayFixtureState` (new) implement it. The `EngineFixtureTests` wiring then uses `IFixtureState` uniformly — it no longer reaches for the three concrete properties (`GameState`/`QuestState`/`Navigator`) and constructs the other adapters inline.

```csharp
// QuestForge.Engine.Tests/Replay/States/IFixtureState.cs
internal interface IFixtureState
{
    IGameStateProvider GameState { get; }
    IQuestState        QuestState { get; }
    INavigator         Navigator  { get; }
    ITeleporter        Teleporter { get; }
    IInteractor        Interactor { get; }
    ICombat            Combat     { get; }
    IGearManager       Gear       { get; }
    IMinigameSkipper   Minigames  { get; }
    IDialogueResolver  Dialogue   { get; }
    ITimingProfile     Timing     { get; }

    /// <summary>
    /// Advance fake state after the engine has produced <paramref name="action"/> on tick
    /// <paramref name="tick"/>. Scripted states mutate their fakes here; the trace-replay state
    /// is a NO-OP (the ObservationScanner advances as the engine reads recorded observations).
    /// </summary>
    void OnTick(EngineAction action, int tick);
}
```

`EngineFixtureTests` then wires the engine from a single `IFixtureState`:

```csharp
var engine = new QuestEngine(
    state.GameState, state.QuestState, state.Navigator, state.Teleporter,
    state.Interactor, state.Combat, state.Gear, state.Minigames,
    state.Dialogue, state.Timing,
    capturingTrace, NullLogger<QuestEngine>.Instance);
```

**Refactor of `SimpleLinearAcceptanceState`:** it keeps its three concrete fields and `OnTick` logic verbatim. It additionally exposes the four other action adapters (`Teleporter`/`Interactor`/`Combat`/`Gear`/`Minigames`/`Dialogue`/`Timing`) by constructing the *same* fakes the test wired inline today — preserving today's exact behavior:

```csharp
internal sealed class SimpleLinearAcceptanceState : IFixtureState
{
    public FakeGameStateProvider GameState { get; } = new();
    public FakeQuestState QuestState { get; } = new();
    public FakeNavigator Navigator { get; }                 // mutating fake — fine here
    public ITeleporter Teleporter { get; }
    public IInteractor Interactor { get; }
    public ICombat Combat { get; } = new FakeCombat();
    public IGearManager Gear { get; } = new FakeGearManager();
    public IMinigameSkipper Minigames { get; } = new FakeMinigameSkipper();
    public IDialogueResolver Dialogue { get; } = new FakeDialogueResolver();
    public ITimingProfile Timing { get; } = new FakeTimingProfile();

    IGameStateProvider IFixtureState.GameState => GameState;  // explicit-interface widening
    IQuestState IFixtureState.QuestState => QuestState;
    INavigator IFixtureState.Navigator => Navigator;

    public SimpleLinearAcceptanceState()
    {
        Navigator  = new FakeNavigator(GameState);
        Teleporter = new FakeTeleporter(GameState);
        Interactor = new FakeInteractor(GameState, QuestState);
        // ... existing initial-state setup unchanged (zone 182, position, sequence 0) ...
    }

    public void OnTick(EngineAction action, int tick) { /* unchanged */ }
}
```

The mutating fakes are correct **for the scripted path** (they wrap that path's own `FakeGameStateProvider`). They are never used by the replay path.

### 3.3 Q2 (cont.) — Inert no-op action adapters

The replay path must wire **inert** action adapters that (a) satisfy the interface, (b) never mutate any state, (c) return benign success so the engine's per-tick dispatch path does not derail. Add these to `QuestForge.Adapters.Fakes/Replay/` (production assembly, so they are reusable and compiled with the rest of the fakes):

| Type | Implements | Behavior |
|---|---|---|
| `InertNavigator` | `INavigator` | `NavigateTo` → `Result.Ok(NavigationOutcome.Arrived)` (no state mutation — there is no `FakeGameStateProvider` to mutate). `IsNavigating` → `Result.Ok(false)`. `Stop` → `Result.Ok(Unit)`. `GetNavmeshInfo` → `Result.Ok(new NavmeshInfo(NavmeshStatus.Ready, null, null))`. |
| `InertTeleporter` | `ITeleporter` | All teleport calls → `Result.Ok(TeleportOutcome.Arrived)`; no state mutation. Cooldowns → `TimeSpan.Zero`; `GetHomeAetheryte` → `Result.Ok((AetheryteId?)null)`; `IsTeleportAvailable` → `Result.Ok(true)`; cost → `0`. |
| `InertInteractor` | `IInteractor` | All interactions → benign success (`InteractOutcome.DialogueOpened`, `DialogueOutcome.Advanced`, `HandOverOutcome.HandedOver`, `Unit`, `UseItemOutcome.Used`, `DutyEntryOutcome.Entered`, `SpdEntryOutcome.Entered`). **No `_questState` mutation** (the recorded observations carry the post-accept quest state). |

`InertNavigator`/`InertTeleporter`/`InertInteractor` have **parameterless constructors** (they hold no state). Reuse the existing stateless fakes for the rest: `FakeCombat`, `FakeGearManager`, `FakeMinigameSkipper`, `FakeDialogueResolver` (these do not mutate game/quest state, so they are inert enough for replay).

**Why genuinely inert (not "reuse FakeNavigator with a throwaway FakeGameStateProvider"):** the engine reads game state from `ReplayGameStateProvider`. If an action adapter mutated a *separate* throwaway `FakeGameStateProvider`, that mutation would be invisible to the engine (different object) — harmless but pointless. Worse, `FakeNavigator`/`FakeTeleporter`/`FakeInteractor` *require* a concrete `FakeGameStateProvider`/`FakeQuestState` in their constructors, so they literally cannot wrap the replay providers. The inert adapters are the clean solution: they implement the interfaces with no constructor dependency and never mutate anything.

### 3.4 Q3 — Determinism: `FakeTimingProfile` (zero-delay), no reseeding

**Decision:** the replay path uses a plain `new FakeTimingProfile()` (zero `ReactionDelay`/`DecisionDelay`, 50 ms `InterActionGap`, no breaks) — identical to today's scripted path. **No reseeding from the recorded `runId`.**

Rationale: the fixture harness asserts the **sequence of `(stepId, actionType)` transitions**, not tick counts or wall-clock timing (FIXTURES.md "What fixtures do not test: Exact tick counts"). `ITimingProfile` influences *when* the engine acts, not *which* `EngineAction` it produces for a given game state. Because the harness collapses consecutive identical transitions and ticks up to 50,000 times until the transition stream is stable, timing jitter cannot change the asserted output. Reseeding would add complexity (parsing `runId` → seed) for zero assertion value. If a future fixture format asserts timing (FIXTURES.md "No timing assertions" limitation), revisit then.

### 3.5 Q4 — `EngineFixtureTests` dispatch change

**Decision:** the dispatch becomes a three-way branch evaluated in this order:

```csharp
IFixtureState state;
if (StateFactories.TryGetValue(fixtureName, out var scripted))
{
    state = scripted();                              // (1) scripted path — unchanged
}
else if (TryResolveSourceTrace(fixturePath, fixture, dataRoot) is { } tracePath)
{
    state = TraceReplayFixtureState.FromTraceFile(tracePath);   // (2) generic replay path
}
else
{
    Assert.Skip(                                     // (3) neither — skip (safe to commit early)
        $"Fixture '{fixtureName}' has no registered scripted state machine and no source " +
        $"trace (looked for '{fixtureName}.trace.jsonl' beside it, and a 'sourceTrace' field). " +
        $"Add a trace to enable the generic replay harness, or register a state machine in " +
        $"EngineFixtureTests.StateFactories.");
    return; // unreachable after Assert.Skip, present for the compiler's definite-assignment
}
```

`TryResolveSourceTrace` implements §3.1's resolution order: explicit `sourceTrace` field (relative to `dataRoot`) first, else the `<name>.trace.jsonl` sibling; returns the absolute path if the file exists, else null.

The remainder of the test loop (drive engine, collect transitions, assert sequence, assert terminal outcome) is **unchanged** and operates on `IFixtureState` uniformly. `state.OnTick(action, tick)` is still called each tick — it is a no-op for the replay state and the scripted state's existing logic for the scripted state.

**Starvation → actionable error (distinguish regression from re-record):** wrap the per-tick `engine.Tick(ct)` so a `ReplayObservationStarvationException` becomes a clear `Assert.Fail` rather than an opaque test crash:

```csharp
EngineAction action;
try
{
    action = await engine.Tick(ct);
}
catch (ReplayObservationStarvationException ex)
{
    Assert.Fail(
        $"Fixture '{fixtureName}' (generic trace-replay): the engine read game state that the " +
        $"recorded trace does not contain — OBSERVATION STARVATION, not a decision regression.\n" +
        $"This means the engine's read pattern changed (e.g. a new adapter read was added) since " +
        $"'{Path.GetFileName(tracePath)}' was recorded.\n" +
        $"FIX: re-record the trace for this fixture (run the quest in-game with tracing on, then " +
        $"`qf-trace extract-fixture <run>.jsonl` and re-commit both files). Do NOT 'fix' the engine.\n" +
        $"Underlying: {ex.Message}");
}
```

The two distinct failure surfaces a contributor will see:
- **Decision regression** — `engine.Tick` runs to completion but the collected transition list ≠ `expectedTransitions` → the existing `Assert.Equal` failures (transition count / `stepId` / `actionType` mismatch). Message: "engine produced a different decision than the fixture expects." This is a *real engine change* — investigate the engine.
- **Observation starvation** — `engine.Tick` throws `ReplayObservationStarvationException` → the explicit `Assert.Fail` above. This is a *read-pattern drift* — re-record the trace.

The scripted `simple-linear-acceptance` fixture never enters the replay branch (it has a `StateFactories` entry), so it cannot throw starvation and its behavior is identical to today.

### 3.6 Q5 — Read-pattern maintenance rule

Document (in FIXTURES.md, new "Trace-backed fixtures" subsection) the maintenance contract:

- The `ObservationScanner` already tolerates **read-count and read-order drift within known `(method, arg)` pairs** via its scan-forward + last-seen fallback (`ObservationScanner.cs:58-60`). So an engine change that reads `GetPlayerPosition` three extra times, or in a different order, against an already-recorded `(method,arg)` pair, does **not** starve — it reuses the last-seen value. This is intentional and keeps traces robust to benign refactors.
- Starvation occurs **only** when the engine calls a `(method, arg)` pair that **never appears at all** in the trace — i.e. the engine added a genuinely new adapter read (new method, or an existing method with a new argument value that was never recorded). When that happens, **old traces must be re-recorded**. This is acceptable and expected; the harness surfaces it with the §3.5 actionable message telling the author to re-record.
- A re-record is a deliberate, reviewed event (same discipline as FIXTURES.md "Updating a fixture when it breaks") — the contributor must understand *why* the read pattern changed before re-recording, exactly as they must understand *why* a decision changed before updating `expectedTransitions`.

### 3.7 Q6 — Tool-side parity (`questforge-tools`)

Three sub-changes in `questforge-tools`:

**(a) Expand `TraceToFixtureExtractor.FilenameLookup`** (`QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs:18-25`). Today it has 5 shapes (talk+travel, duty, branch, fragment, spd) and `SuggestFilename` matches an *exact* sorted `step:` set. Add entries for the current single-shape and common-combination capability sets the engine emits. The `CapabilityInferrer` (`questforge-tools/.../Capabilities/CapabilityInferrer.cs:14-38`) already maps 22 step types to tags, so the lookup must cover at least the implemented-and-fixturable shapes:

| Sorted `step:` capability set | Suggested filename |
|---|---|
| `step:talk`, `step:travel` | `simple-linear-acceptance.json` *(existing)* |
| `step:accept`, `step:talk`, `step:travel` | `with-accept.json` |
| `step:talk`, `step:travel`, `step:turn-in` | `with-turn-in.json` |
| `step:attune`, `step:travel` | `with-attunement.json` |
| `step:hand-over-item`, `step:talk`, `step:travel` | `with-hand-over-item.json` |
| `step:interact-object`, `step:travel` | `with-interact-object.json` |
| `step:pickup-item`, `step:travel` | `with-pickup-item.json` |
| `step:duty` | `with-dungeon.json` *(existing)* |
| `step:spd` | `with-spd.json` *(existing)* |
| `step:branch` | `with-branching.json` *(existing)* |
| `step:fragment` | `with-fragments.json` *(existing)* |

The exact-`SequenceEqual` match is brittle for multi-step quests; keep the exact-match fast path, then **fall back to a "best single distinguishing capability"** rule: if no exact set matches, pick the highest-priority distinguishing tag present (`step:duty` > `step:spd` > `step:branch` > `step:fragment` > `step:attune` > `step:hand-over-item` > `step:turn-in` > `step:accept` > `step:interact-object` > `step:pickup-item`) and use its filename; else `simple-linear-acceptance.json`. This makes the suggestion useful for real multi-shape quests like 65644 (travel+accept+attune+hand-over-item+turn-in) rather than always defaulting.

**(b) Co-emit the source trace** so the tool produces **both** artifacts CI needs. `qf-trace extract-fixture <run>.jsonl` today writes only the fixture JSON (via `OutputFormatters` / `FixtureModelSerializer`). Add: after writing `<suggested-name>.json`, also **emit** `<suggested-basename>.trace.jsonl` in the same output directory, containing the input trace **filtered to the fixture run's runId** — the runId of the `RunStartEvent` the extractor selected. Real traces are session files that also contain `inspect`/`passive`/other-run events; filtering to the one runId yields a clean single-run trace the replay harness consumes without runId awareness. Gate behind a flag `--with-trace` (default ON for `extract-fixture`, since the generic harness needs it). The CLI wiring lives in the `ExtractFixture` subcommand handler (`CliSubcommand.ExtractFixture`); the trace target basename matches `SuggestFilename(fixture)` minus `.json`.

**(c) Sync the FIXTURES.md actionType table.** The engine emits `EngineAction` subtypes whose `ActionTypeString` fallback lowercases the type name: `UseAethernet`→`"useaethernet"`, `HandOver`→`"handover"`, and `AttunementStep` dispatch surfaces via the existing canonical strings. `TraceConstants` (`questforge-tools/.../TraceConstants.cs:13-14`) already defines `ActionAttune="attune"`, `ActionHandover="handover"`, `ActionUseAethernet="useaethernet"`. FIXTURES.md's actionType table (lines ~146-156) currently lists only `navigate`/`interact`/`wait`/`awaitUser`/`done`. Add rows for `attune`, `handover`, `useaethernet` so the doc matches what the engine + extractor emit. (This is a doc-only edit in §7.)

### 3.8 Q7 — Repos & layout (what changes where)

| Repo | Changes |
|---|---|
| **questforge** | `QuestForge.Adapters.Fakes/Replay/InertNavigator.cs`, `InertTeleporter.cs`, `InertInteractor.cs` (new). `QuestForge.Engine.Tests/Replay/States/IFixtureState.cs` (new), `TraceReplayFixtureState.cs` (new), `SimpleLinearAcceptanceState.cs` (refactor to implement `IFixtureState`). `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs` (dispatch change + `sourceTrace` field on the `EngineFixture` record + starvation catch). `docs/FIXTURES.md` (sourceTrace convention, trace-backed subsection, actionType table, version → `1.1.0`). |
| **questforge-tools** | `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs` (FilenameLookup expansion + fallback rule). `QuestForge.Tools.Trace/Cli/` (`ExtractFixture` handler co-emits `.trace.jsonl`; new `--with-trace` flag in `CliArgs`/`CliArgsParser`). Tests in `QuestForge.Tools.Trace.Tests`. |
| **questforge-data** | `quests/arr/msq/65644-close-to-home.json` (new — must pass `qf-validate`). `fixtures/engine/with-attunement.json` + `fixtures/engine/with-attunement.trace.jsonl` (new; the trace is the real session filtered to the engine runId). CI already checks out questforge-data (FIXTURES.md §"CI integration"); no workflow change needed — the existing `dotnet test QuestForge.Engine.Tests` picks up the new fixture automatically. |

### 3.9 Q8 — Proof fixture: **real 65644 + a real in-game trace** (65644 added to questforge-data)

**Decision:** prove the harness with quest **65644 "Close to Home"** — a real, multi-zone ARR MSQ recorded in-game — committed to questforge-data alongside a single clean engine-run trace. This **supersedes** the earlier 66130-synthetic approach: 65644 is now being added to questforge-data, and a real `done` trace exists (recorded after the decision-debounce landed, so it is small).

**Two-part questforge-data commit:**
1. **The quest:** `quests/arr/msq/65644-close-to-home.json`, from the authored copy in the plugin config dir. It **must pass `qf-validate`** — validate and fix any structural/predicate/capability errors before commit (e.g. `lastVerifiedPatch` currently `"unknown"`, support status). This is the first richer quest in the corpus.
2. **The fixture + trace:** `fixtures/engine/with-attunement.json` + `fixtures/engine/with-attunement.trace.jsonl`. The fixture has **no `StateFactories` entry** → it runs via the generic replay path. The filename matches what `extract-fixture`'s `SuggestFilename` yields for 65644's capability set (§3.7a: `step:attune` is the highest-priority distinguishing tag); its `capabilities` array still lists every shape (travel/accept/attune/hand-over-item/talk/turn-in + predicates), so the coverage matrix fills multiple cells from this one fixture.

**The source trace.** A real `/qf run 65644` recording exists: `session-20260525-023222.jsonl`, a single engine run **`20260525-023230-5aaadbb3`**, outcome **`done`**, captured *after* the decision-debounce (#47) — 26 deduped decision transitions, 1 `run.start`. It is a **session file** that also contains `inspect`-mode noise, so the committed `with-attunement.trace.jsonl` is that file **filtered to the engine runId** (`20260525-023230-5aaadbb3`), dropping all `inspect`/other-run events. Every observation in the committed file then belongs to the one run — so `TraceReplayFixtureState` needs no runId awareness at replay time.

**expectedTransitions (26, generated by `extract-fixture` from the filtered trace; canonical lowercase actionType):**
```
accept-quest              interact
accept-quest-65647        navigate
accept-quest-65647        interact
npc-dialogue-to-zone-129  navigate
npc-dialogue-to-zone-129  interact
attune-aetheryte-8        interact
attune-aetheryte-8        navigate
attune-aetheryte-8        interact
hand-over-item-2000104    navigate
hand-over-item-2000104    handover
travel-step               navigate
attune-aetheryte-42       interact
attune-aetheryte-42       navigate
attune-aetheryte-42       interact
attune-aetheryte-48       navigate
attune-aetheryte-48       interact
talk-to-npc-1000926       navigate
talk-to-npc-1000926       interact
aethernet-to-zone-129     navigate
aethernet-to-zone-129     useaethernet
npc-dialogue-to-zone-128  interact
npc-dialogue-to-zone-128  navigate
npc-dialogue-to-zone-128  interact
turn-in-quest             interact
turn-in-quest             navigate
turn-in-quest             interact
```
`terminalOutcome: "done"`. The `interact → navigate → interact` "flapping" on the attune/talk/turn-in steps is real fail-open-then-navigate engine behavior captured in the trace (a tick where player position was momentarily unread → fail-open `Interact`, then `Navigate` once position is available). The replay must reproduce it byte-for-byte — and should, because record and replay run the **identical pure engine** over the same observation sequence (see §11 risk 2).

**The scripted `simple-linear-acceptance.json` (66130) stays** as a second, independent fixture, so CI now has both a scripted and a generic-replay fixture passing in one run.

---

## 4. `TraceReplayFixtureState` construction (detailed)

```csharp
// QuestForge.Engine.Tests/Replay/States/TraceReplayFixtureState.cs
internal sealed class TraceReplayFixtureState : IFixtureState
{
    public IGameStateProvider GameState { get; }
    public IQuestState        QuestState { get; }
    public INavigator         Navigator  { get; } = new InertNavigator();
    public ITeleporter        Teleporter { get; } = new InertTeleporter();
    public IInteractor        Interactor { get; } = new InertInteractor();
    public ICombat            Combat     { get; } = new FakeCombat();
    public IGearManager       Gear       { get; } = new FakeGearManager();
    public IMinigameSkipper   Minigames  { get; } = new FakeMinigameSkipper();
    public IDialogueResolver  Dialogue   { get; } = new FakeDialogueResolver();
    public ITimingProfile     Timing     { get; } = new FakeTimingProfile();

    private TraceReplayFixtureState(IReadOnlyList<ObservationEvent> observations)
    {
        GameState  = new ReplayGameStateProvider(observations);
        QuestState = new ReplayQuestState(observations);
    }

    public static TraceReplayFixtureState FromTraceFile(string tracePath)
    {
        var observations = TraceReader.ReadFile<ObservationEvent>(tracePath);
        if (observations.Count == 0)
            throw new InvalidDataException(
                $"Trace '{Path.GetFileName(tracePath)}' contains no observation events; " +
                $"a trace-backed fixture requires recorded engine inputs.");
        return new TraceReplayFixtureState(observations);
    }

    // No-op: the ObservationScanner advances as the engine reads recorded observations.
    // There is no fake state to flip — post-action state is already in the trace.
    public void OnTick(EngineAction action, int tick) { }
}
```

Both `ReplayGameStateProvider` and `ReplayQuestState` are constructed from the **same** observation list but each holds its **own** `ObservationScanner` (independent cursors). This matches the existing replay-test pattern and is correct: each provider only ever reads its own methods' observations, and the scanner keys by `(method, arg)`, so the cursors do not interfere.

---

## 5. Acceptance criteria (Tester writes failing tests from these)

Tests live in:
- **`QuestForge.Engine.Tests`** (questforge) — groups A, B, C, D, E. New file `QuestForge.Engine.Tests/Replay/TraceReplayFixtureStateTests.cs` for unit-level (A, B); the dispatch/parametric behavior (C, D) is exercised by extending/observing `EngineFixtureTests`; the proof fixture (E) is a data commit verified by the parametric theory.
- **`QuestForge.Adapters.Fakes.Tests`** (questforge) — group B inert-adapter unit tests. If no such test project exists, place these in `QuestForge.Engine.Tests` instead (the inert adapters are in the Fakes assembly, referenced by the engine tests).
- **`QuestForge.Tools.Trace.Tests`** (questforge-tools) — group F (extractor FilenameLookup + trace co-emission).

### Group A — `TraceReplayFixtureState` construction (`QuestForge.Engine.Tests`)

- **A1 (builds from a trace with observations)** — Given a JSONL trace file containing ≥1 `ObservationEvent` (e.g. a `GetPlayerZone` observation), when `TraceReplayFixtureState.FromTraceFile(path)`, then a non-null state is returned and `state.GameState is ReplayGameStateProvider`, `state.QuestState is ReplayQuestState`.
- **A2 (action adapters are the inert/stateless types)** — For a built `TraceReplayFixtureState`, assert `state.Navigator is InertNavigator`, `state.Teleporter is InertTeleporter`, `state.Interactor is InertInteractor`, and `state.Combat is FakeCombat`, `state.Gear is FakeGearManager`, `state.Minigames is FakeMinigameSkipper`, `state.Dialogue is FakeDialogueResolver`, `state.Timing is FakeTimingProfile`.
- **A3 (empty trace → InvalidDataException)** — Given a JSONL trace file with run.start/run.end/decision events but **zero** `ObservationEvent`s, when `FromTraceFile`, then `Assert.Throws<InvalidDataException>` whose message mentions "no observation".
- **A4 (OnTick is a no-op)** — Given a built `TraceReplayFixtureState`, when `state.OnTick(new EngineAction.Navigate(...), 0)` is called, then it does not throw and reading `state.GameState.GetPlayerZone(ct)` afterward returns the same first recorded observation as before `OnTick` was called (proves `OnTick` does not advance the scanner). 
- **A5 (reads consume recorded observations)** — Given a trace with a recorded `GetPlayerZone` observation whose value is zone 182, when `await state.GameState.GetPlayerZone(ct)`, then the result is `Result.Ok(new ZoneId(182))` (proves the replay provider is wired to the trace, via the existing `ReplayGameStateProvider` behavior).

### Group B — inert no-op action adapters (`QuestForge.Adapters.Fakes.Tests` or `QuestForge.Engine.Tests`)

- **B1 (InertNavigator never mutates, returns Arrived)** — Given `new InertNavigator()`, when `await NavigateTo(new WorldPosition(99,0,99), new NavigationOptions(), ct)`, then result is `Result.Ok(NavigationOutcome.Arrived)`. `IsNavigating` → `Result.Ok(false)`. `Stop` → success. `GetNavmeshInfo(zone)` → `NavmeshStatus.Ready`. The adapter has a parameterless constructor (no `FakeGameStateProvider` dependency).
- **B2 (InertTeleporter returns Arrived, no state)** — Given `new InertTeleporter()`, when `await TeleportToAetheryte(new AetheryteId(1), ct)`, then `Result.Ok(TeleportOutcome.Arrived)`. `UseReturn` / `TeleportToAethernet` → `Arrived`. `IsTeleportAvailable` → true. Cooldowns → `TimeSpan.Zero`. `GetHomeAetheryte` → `Result.Ok((AetheryteId?)null)`. Parameterless constructor.
- **B3 (InertInteractor benign success, no quest mutation)** — Given `new InertInteractor()`, when `await AcceptQuest(new QuestId(66130), ct)` then `CompleteQuest(...)`, both return `Result.Ok(Unit)` and there is **no** `IQuestState` to mutate (parameterless constructor; the adapter holds no quest state). `InteractWith` → `InteractOutcome.DialogueOpened`; `AdvanceDialogue` → `DialogueOutcome.Advanced`; `HandOverItem` → `HandOverOutcome.HandedOver`; `EnterSinglePlayerDuty` → `SpdEntryOutcome.Entered`.
- **B4 (inert adapters drive the engine without derailing)** — Wire a `QuestEngine` with a `ReplayGameStateProvider`/`ReplayQuestState` built from a minimal recorded trace for 66130 and the three inert adapters; `StartQuest(quest66130); BeginRun("b4");` and tick once — assert the engine returns a non-throwing `EngineAction` (does not crash on a null/derailed action-adapter path). (This is a thin integration smoke; the full assertion is group C/E.)

### Group C — `EngineFixtureTests` fallback dispatch (`QuestForge.Engine.Tests`)

These assert the dispatch logic in §3.5. Because the parametric theory is data-driven, the Tester adds **focused unit tests** of the dispatch helpers (extracted as `internal static` methods on `EngineFixtureTests` or a small `FixtureDispatch` helper so they are unit-testable without a full questforge-data checkout), plus assertions on the live theory where data is present.

- **C1 (scripted fixture still uses the scripted path)** — Given `fixtureName == "simple-linear-acceptance"` and a `StateFactories` entry exists, when the dispatch runs, then it selects the scripted `SimpleLinearAcceptanceState` (not the trace path), even if a `.trace.jsonl` sibling also exists. (Scripted registration wins — order in §3.5.)
- **C2 (no scripted + trace present → generic path)** — Given `fixtureName == "x"` with no `StateFactories` entry but a sibling `x.trace.jsonl` exists, when `TryResolveSourceTrace` runs, then it returns the absolute sibling path; dispatch selects `TraceReplayFixtureState`.
- **C3 (no scripted + no trace → skip)** — Given `fixtureName == "y"` with no `StateFactories` entry and no `y.trace.jsonl` and no `sourceTrace` field, when dispatch runs, then `Assert.Skip` fires with a message naming both `y.trace.jsonl` and the `sourceTrace` field and `EngineFixtureTests.StateFactories`.
- **C4 (explicit `sourceTrace` field overrides the sibling convention)** — Given a fixture whose JSON has `"sourceTrace": "fixtures/engine/shared.trace.jsonl"`, when `TryResolveSourceTrace` runs against a questforge-data root containing that file, then the resolved path is `<dataRoot>/fixtures/engine/shared.trace.jsonl` (the field wins over any same-basename sibling).
- **C5 (`sourceTrace` resolution is questforge-data-root-relative, forward-slashed)** — Given `"sourceTrace": "fixtures/engine/a.trace.jsonl"`, when resolved on a Windows path, then `Path.DirectorySeparatorChar` substitution is applied (mirrors the existing `questFile` resolution at `Quest66130ReplayTests.cs:84`) so the file is found.
- **C6 (`EngineFixture` record deserializes the new optional field)** — Given fixture JSON **with** `"sourceTrace": "x.trace.jsonl"`, when deserialized into the `EngineFixture` record (with a new `string? SourceTrace` property, `[JsonPropertyName("sourceTrace")]`), then `fixture.SourceTrace == "x.trace.jsonl"`. Given JSON **omitting** `sourceTrace`, then `fixture.SourceTrace == null` (backward-compatible — `1.0.0` fixtures still deserialize).

### Group D — starvation → actionable error path (`QuestForge.Engine.Tests`)

- **D1 (starvation is caught and re-thrown as an actionable Assert.Fail)** — Construct a `TraceReplayFixtureState` from a **deliberately truncated** trace that lacks a `(method, arg)` pair the engine for 66130 will read (e.g. omit all `GetQuestSequence` observations for quest 66130). Drive the engine through the fixture loop. Assert the test fails with a message containing "OBSERVATION STARVATION" and "re-record" and the trace filename — **not** an uncaught `ReplayObservationStarvationException` and **not** a transition-mismatch message. (The Tester can assert by invoking the harness's tick-wrapper helper and asserting it surfaces the starvation as the actionable failure — extract the wrapper as a unit-testable method, or assert on the thrown `Xunit.Sdk` failure message.)
- **D2 (decision regression is distinct from starvation)** — Construct a valid, fully-populated trace for 66130 but a fixture whose `expectedTransitions` deliberately omit the final `(talk-to-momodi, interact)`. Drive the engine. Assert the failure is a **transition-count / sequence mismatch** (`Assert.Equal` on count or element), **not** the starvation message — proving the two failure modes produce different, distinguishable diagnostics.
- **D3 (count-only safety break still applies)** — With a trace that would drive **more** transitions than the fixture's `expectedTransitions.Length + 10`, assert the loop breaks at the safety bound and the count assertion fails cleanly (no infinite loop / no 50,000-tick hang). (Mirrors the existing safety break at `Quest66130ReplayTests.cs:150`.)

### Group E — proof fixture replays green (questforge-data data commit; verified by the theory)

- **E1 (committed trace is a single clean run)** — The committed `with-attunement.trace.jsonl` contains exactly one runId, exactly one `run.start` (quest 65644), a `run.end` with outcome `done`, and **no** `inspect`/`passive`/other-run events. (A data-integrity assertion over the committed file — guards against committing the raw unfiltered session trace.)
- **E2 (65644 fixture replays to its expected transitions)** — Given the committed `with-attunement.json` (no `StateFactories` entry) + `with-attunement.trace.jsonl`, when the parametric `EngineFixtureTests` runs it via the generic path, then the collected transitions equal the 26 transitions in §3.9 and the terminal tick yields `EngineAction.Done` (`terminalOutcome: "done"`).
- **E3 (scripted and generic fixtures both green)** — With questforge-data present, both `simple-linear-acceptance.json` (scripted, 66130) and `with-attunement.json` (generic replay, 65644) pass in the same `dotnet test QuestForge.Engine.Tests` run — proving the generic path runs faithfully alongside the scripted one.
- **E4 (questforge-data absent → both skip, no failure)** — When `FixtureLocator.TryGetQuestForgeDataRoot()` returns null (data repo not cloned), `AllEngineFixtures()` yields no cases and no fixture test fails (existing behavior preserved).
- **E5 (65644 quest passes qf-validate)** — `quests/arr/msq/65644-close-to-home.json` validates clean under `qf-validate` (no structural, predicate, or capability errors). Verified in the questforge-data PR / CI; a prerequisite for the fixture's `questFile` to resolve.

### Group F — extractor FilenameLookup expansion & trace co-emission (`QuestForge.Tools.Trace.Tests`)

- **F1 (accept shape → with-accept.json)** — Given a `FixtureModel` whose `Capabilities` contain exactly `step:accept, step:talk, step:travel`, when `SuggestFilename`, then `"with-accept.json"`.
- **F2 (turn-in shape → with-turn-in.json)** — `step:talk, step:travel, step:turn-in` → `"with-turn-in.json"`.
- **F3 (attune shape → with-attunement.json)** — `step:attune, step:travel` → `"with-attunement.json"`.
- **F4 (hand-over-item shape → with-hand-over-item.json)** — `step:hand-over-item, step:talk, step:travel` → `"with-hand-over-item.json"`.
- **F5 (interact-object / pickup-item shapes)** — `step:interact-object, step:travel` → `"with-interact-object.json"`; `step:pickup-item, step:travel` → `"with-pickup-item.json"`.
- **F6 (existing shapes unchanged)** — `step:talk, step:travel` → `"simple-linear-acceptance.json"`; `step:duty` → `"with-dungeon.json"`; `step:spd` → `"with-spd.json"`; `step:branch` → `"with-branching.json"`; `step:fragment` → `"with-fragments.json"` (regression guard — the five existing mappings still resolve).
- **F7 (multi-shape fallback by distinguishing capability)** — Given a `FixtureModel` with a capability set that matches **no** exact entry (e.g. `step:accept, step:attune, step:hand-over-item, step:talk, step:travel, step:turn-in` — the 65644 shape), when `SuggestFilename`, then it returns the highest-priority distinguishing tag's filename per §3.7 (here `with-attunement.json`, since `step:attune` outranks turn-in/accept/hand-over-item in the priority list) — **not** the bare `"simple-linear-acceptance.json"` default.
- **F8 (unknown shape → default)** — Given a `FixtureModel` with only an unmapped capability (e.g. `step:cutscene` alone), when `SuggestFilename`, then `"simple-linear-acceptance.json"` (the documented fallback).
- **F9 (extract-fixture co-emits the source trace)** — Given an input `<run>.jsonl` and `--with-trace` (default for `extract-fixture`), when the CLI extracts a fixture, then in the output directory there is both `<suggested-basename>.json` and `<suggested-basename>.trace.jsonl`, and the `.trace.jsonl` is byte-identical to the input `<run>.jsonl`.
- **F10 (--with-trace off suppresses the copy)** — Given `--no-trace` (or `--with-trace` absent when overridden off), when extracting, then only the fixture JSON is written; no `.trace.jsonl` is produced. (Locks the flag's effect.)

---

## 6. Validation / failure-message table

| Condition | Where | Surfaced as | Message essence |
|---|---|---|---|
| No scripted state + no trace + no `sourceTrace` | `EngineFixtureTests` dispatch | `Assert.Skip` | "no scripted state machine and no source trace (looked for `<name>.trace.jsonl` and a `sourceTrace` field)…" |
| Trace file exists but has zero observations | `TraceReplayFixtureState.FromTraceFile` | `InvalidDataException` | "Trace `<name>` contains no observation events…" |
| Engine reads a `(method,arg)` never recorded | per-tick wrapper in `EngineFixtureTests` | `Assert.Fail` | "OBSERVATION STARVATION, not a decision regression… re-record the trace… Do NOT 'fix' the engine." |
| Engine's transitions ≠ `expectedTransitions` | existing `Assert.Equal` block | `Assert.Equal` failure | transition count / `stepId` / `actionType` mismatch (existing) |
| `questFile` missing in questforge-data | existing `Assert.True(File.Exists…)` | `Assert.True` failure (hard) | existing FIXTURES.md behavior — unchanged |
| `sourceTrace` field set but file missing | `TryResolveSourceTrace` | falls through to sibling, then `Assert.Skip` (file genuinely absent) | the skip message names both resolution attempts |

---

## 7. FIXTURES.md edits (questforge)

1. **Bump fixture format version to `1.1.0`** (new optional `sourceTrace` field is additive — FIXTURES.md §"Future extensions" classifies a new field as a minor bump). Update the example `schemaVersion` reference text; note that `1.0.0` fixtures remain valid (missing `sourceTrace` → null).
2. **Add `sourceTrace` to the field-reference table** (after `terminalOutcome`): `| sourceTrace | path string | Optional | Path to the source JSONL trace (engine inputs) for the generic replay harness, relative to questforge-data root, forward slashes. When omitted, the harness uses the <name>.trace.jsonl sibling convention. |`
3. **New subsection "Trace-backed fixtures (generic replay harness)"** documenting: the two-file model (`<name>.json` + `<name>.trace.jsonl`); that the trace supplies recorded observations (inputs) and the engine's decisions are compared to `expectedTransitions` (the §1.1 regression argument); the read-pattern maintenance rule (§3.6); and the re-record workflow on starvation.
4. **Extend the actionType canonical-strings table** (FIXTURES.md ~line 146) with rows: `attune` (AttunementStep dispatch), `handover` (`EngineAction.HandOver`), `useaethernet` (`EngineAction.UseAethernet`) — matching `TraceConstants` and the `ActionTypeString` fallback.
5. **Path case-sensitivity note** for `sourceTrace`, alongside the existing `questFile` note (~line 227).
6. **State-machine dispatch section** (FIXTURES.md ~line 207) update: document that a fixture without a registered state machine now falls back to the generic trace-replay harness when a source trace is present, and only skips when neither exists.

---

## 8. Implementation order

**Phase A — Inert adapters (`questforge`, `QuestForge.Adapters.Fakes`), 0.5 day.** Add `InertNavigator`, `InertTeleporter`, `InertInteractor`. Make group B pass. Done before C/D (the replay state depends on them).

**Phase B — `IFixtureState` + refactor + `TraceReplayFixtureState` (`questforge`, `QuestForge.Engine.Tests`), 1 day.** Add `IFixtureState`; refactor `SimpleLinearAcceptanceState` to implement it (existing scripted fixture must stay green — run `dotnet test --filter EngineFixture` to confirm); add `TraceReplayFixtureState`. Make group A pass. Done before C.

**Phase C — Dispatch change + `EngineFixture.SourceTrace` field + starvation catch (`questforge`, `QuestForge.Engine.Tests`), 0.5 day.** Implement the §3.5 three-way dispatch, the `sourceTrace` resolution helper, and the starvation→Assert.Fail wrapper. Make groups C and D pass.

**Phase D — Proof fixture (`questforge-data` commit; no generator), 0.5 day.** (1) Add `quests/arr/msq/65644-close-to-home.json` from the authored copy; run `qf-validate` and fix any errors (E5). (2) Filter the real session trace `session-20260525-023222.jsonl` to the engine runId `20260525-023230-5aaadbb3` → commit as `fixtures/engine/with-attunement.trace.jsonl` (E1). (3) Produce `fixtures/engine/with-attunement.json` via `qf-trace extract-fixture` on the filtered trace (or hand-author from the §3.9 transitions); write a real `description`. Make group E pass. (Requires Phases A–C green; the manual runId filter is fine, or use the Phase E `--with-trace` co-emission which filters automatically.)

**Phase E — Extractor parity (`questforge-tools`), 0.5 day.** Expand `FilenameLookup` + fallback rule; add `--with-trace` co-emission to the `ExtractFixture` CLI handler. Make group F pass. Independent of A–D (separate repo) once the trace-naming convention (§3.1) is fixed.

**Phase F — Docs (`questforge`).** Apply the FIXTURES.md edits (§7). No tests; reviewed in PR.

---

## 9. Done criteria

1. `InertNavigator`/`InertTeleporter`/`InertInteractor` exist in `QuestForge.Adapters.Fakes/Replay/`, have parameterless constructors, never mutate any state, and return benign success for every interface member (group B green).
2. `IFixtureState` exists; `SimpleLinearAcceptanceState` implements it; the **existing scripted `simple-linear-acceptance.json` fixture still passes** with no change to its asserted transitions (Phase B regression check + E3).
3. `TraceReplayFixtureState.FromTraceFile` builds a state from a JSONL trace using `ReplayGameStateProvider`/`ReplayQuestState` + inert adapters, throws `InvalidDataException` on an observation-less trace, and `OnTick` is a no-op (group A green).
4. `EngineFixtureTests` dispatch: scripted entry wins; else generic replay if a trace resolves (sibling convention or `sourceTrace` field); else `Assert.Skip` with an actionable message (group C green). The `EngineFixture` record gained an optional `sourceTrace` field that is backward-compatible with `1.0.0` fixtures.
5. Observation starvation surfaces as an `Assert.Fail` explicitly naming "OBSERVATION STARVATION" and "re-record", distinct from a transition-mismatch failure (group D green).
6. Quest `65644-close-to-home.json` (qf-validate-clean) and the generic fixture `with-attunement.json` + its runId-filtered `.trace.jsonl` are committed to questforge-data; the fixture **passes via the generic path** producing the 26 transitions in §3.9, and both it and the scripted `simple-linear-acceptance.json` pass in one CI run (group E green). Adding a future fixture is now a two-file commit with zero new test code.
7. `qf-trace extract-fixture` co-emits the source `.trace.jsonl` next to the fixture JSON (default on), and `SuggestFilename` resolves accept/turn-in/attune/hand-over-item/interact-object/pickup-item shapes plus a multi-shape fallback; the five existing mappings are unchanged (group F green).
8. FIXTURES.md documents the `sourceTrace` field + trace-backed harness + read-pattern rule + the `attune`/`handover`/`useaethernet` actionType rows; fixture format version is `1.1.0`.

---

## 10. Exclusions

This plan does **NOT** include:

- **New quest *content* beyond 65644.** Only 65644 (already authored, recorded in-game) is added to questforge-data, validated by `qf-validate`; no other quests are authored. (The earlier deferral of 65644 is lifted — see §3.9.)
- **Replacing the scripted fixture.** `simple-linear-acceptance.json` (scripted) stays; the generic proof fixture is added alongside it.
- **Timing-deterministic replay / reseeding from `runId`.** The harness asserts transitions, not timing; `FakeTimingProfile` is used unchanged (§3.4).
- **A new replay-provider or scanner.** The existing `ReplayGameStateProvider`/`ReplayQuestState`/`ObservationScanner`/`TraceReader` are reused as-is — no changes to them.
- **Asserting Navigate destinations / action parameters.** Fixtures still assert only `(stepId, actionType)` (FIXTURES.md "No parameter assertions"). The inert adapters' specific destinations are irrelevant.
- **Cross-file capability verification / `qf-trace validate-fixture` changes.** Out of scope; the FilenameLookup expansion is suggestion-only.
- **Branching/multi-path fixtures, mid-quest `initialState` values.** The generic harness inherits the linear-only, `fresh`-only limitations of the current fixture format.
- **In-game trace recording in CI.** The proof trace is a real in-game recording produced *once* by the author and committed as data (filtered to the run's runId); CI replays the committed trace and never records in-game.
- **Live `TraceMode` switching or new trace event types.** The harness consumes existing `ObservationEvent`/`DecisionEvent`/`RunStart`/`RunEnd`.
- **Schema (`QuestForge.Schema`) changes.** This plan touches test harness code, fakes, the extractor, and docs — not the quest schema. (The fixture *format* version bump is in FIXTURES.md only, independent of the quest `schemaVersion`.)

---

## 11. Open questions / risks

1. **Two trace cursors over one observation list.** `ReplayGameStateProvider` and `ReplayQuestState` each hold an independent `ObservationScanner` over the same list. This is the existing pattern (verified in `ReplayGameStateProviderTests`/`ReplayQuestStateTests`) and is correct because the two providers read disjoint method sets. No risk, noted for the Builder.
2. **Replay must reproduce the recorded decisions exactly.** The 65644 trace includes `interact → navigate → interact` "flapping" (fail-open when position is momentarily unread, then navigate). Replay feeds the recorded observations back through the *identical* pure engine, so the decisions should reproduce byte-for-byte; the `ObservationScanner`'s scan-forward + last-seen fallback absorbs read count/order drift. If E2 diverges, the Builder must determine whether it is a genuine determinism gap (fix the engine) or benign replay-vs-record noise — and only in the latter case set `expectedTransitions` to the replay's actual output (the fixture then still guards future engine changes). Empirically confirm on the first replay run.
6. **Committed-trace hygiene & qf-validate (prerequisites for group E).** The committed `.trace.jsonl` MUST be filtered to the single engine runId (`20260525-023230-5aaadbb3`) — otherwise replay reads foreign `inspect`-run observations. And `65644-close-to-home.json` MUST pass `qf-validate` before the questforge-data PR is mergeable — validate early (E5), since the authored copy has placeholder fields (e.g. `lastVerifiedPatch: "unknown"`).
3. **`sourceTrace` field vs convention precedence.** §3.1 fixes the field as an override that wins over the sibling. If a future fixture both sets `sourceTrace` and has a same-basename sibling, the field wins (C4). Documented to avoid ambiguity.
4. **Extractor `--with-trace` default.** Defaulting ON for `extract-fixture` means the tool always produces both artifacts CI needs. If a contributor only wants the JSON, `--no-trace` opts out (F10). The Builder confirms the flag naming against the existing `CliArgs` conventions.
5. **questforge-data CI checkout already wired.** FIXTURES.md §"CI integration" shows the data repo is checked out; the new two-file fixture needs no workflow edit. If LFS is used for traces, ensure `lfs: true` on the checkout (the JSONL traces are small text, so LFS is likely unnecessary; flagged only if trace size grows).

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the acceptance criteria in §5.
- Happy paths: 13 scenarios (A1, A2, A4, A5, B1, B2, B3, C1, C2, E1, E2, E3, F6)
- Edge cases: 14 scenarios (A3, B4, C4, C5, C6, D3, E4, E5, F1, F2, F3, F4, F5, F7)
- Error cases: 6 scenarios (C3, D1, D2, F8, F9, F10)
- Expected total: ~33 tests — ~5 in a new `TraceReplayFixtureStateTests` (group A), ~4 inert-adapter tests (group B), ~9 dispatch/starvation tests around `EngineFixtureTests` (groups C, D), ~5 proof-fixture/theory assertions (group E: real 65644 fixture + the questforge-data quest+trace commit), ~10 in `QuestForge.Tools.Trace.Tests` (group F, questforge-tools).
