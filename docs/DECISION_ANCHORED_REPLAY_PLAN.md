# Decision-Anchored (Segmented) Trace-Replay Implementation Plan

**Status:** ready for test creation
**Input docs:** docs/FIXTURE_REPLAY_HARNESS_PLAN.md (the harness this supersedes in part), docs/FIXTURES.md, docs/TRACE_FORMAT.md, the existing replay infra in `QuestForge.Adapters.Fakes/Replay/`, `QuestForge.Engine/QuestEngine.cs` (`ResolveAction` read pattern), `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs` (the `EngineFixtureTests` driver), the real fixture `questforge-data/fixtures/engine/with-attunement.{json,trace.jsonl}`.
**Output:** the generic trace-replay harness actually replays a full multi-decision quest run to completion. The flagship fixture `with-attunement.json` (quest 65644, 26 decision transitions) replays end-to-end to `done` in `dotnet test QuestForge.Engine.Tests` — the proof that is failing today (it terminates after **1** transition). After this change, adding an engine regression fixture remains a two-file data commit (`<name>.json` + `<name>.trace.jsonl`) with zero hand-scripted state, and CI runs that fixture as a real regression gate.
**Spans one repo:** `questforge` only (`QuestForge.Adapters.Fakes/Replay/` + `QuestForge.Engine.Tests/Replay/`). No schema, no questforge-tools, no questforge-data changes (the committed data files already exist and are correct).

**This plan SUPERSEDES:**
- `docs/FIXTURE_REPLAY_HARNESS_PLAN.md` **§4** (the naive `TraceReplayFixtureState` whose `OnTick` is a pure no-op and whose providers consume one observation per read). The construction surface (`TraceReplayFixtureState.FromTraceFile(path)`) is preserved; the *internals* and the `OnTick` contract change.
- `docs/FIXTURE_REPLAY_HARNESS_PLAN.md` **§11 risk 2** ("Replay must reproduce the recorded decisions exactly"). That risk assumed a uniform scanner would reproduce decisions; this plan establishes *why* a uniform scanner cannot, and replaces it with the segmented driver.

All other parts of `FIXTURE_REPLAY_HARNESS_PLAN.md` (inert adapters §3.3, `IFixtureState` §3.2, the three-way dispatch §3.5, the starvation→`Assert.Fail` wrapper, the extractor parity §3.7) remain in force and are already implemented.

---

## 1. Problem and root-cause evidence

### 1.1 Symptom

The generic harness (`EngineFixtureTests.EngineProducesExpectedTransitions`, driven by `TraceReplayFixtureState`) was intended to replay a recorded trace's **observations** (engine inputs) through the *current* engine and assert the engine's **decisions** match the fixture's recorded `expectedTransitions`. The first real fixture — `with-attunement.json`, quest 65644, a full ARR-MSQ run with **26** decision transitions — replays to **1** transition (`accept-quest / interact`) then the engine returns `Done`. The transition-count assertion fails: expected 26, actual 1.

### 1.2 Root cause (confirmed against the committed trace)

`ObservationScanner.Next(method, arg)` (`QuestForge.Adapters.Fakes/Replay/ObservationScanner.cs:40`) uses a single monotonic cursor: it scan-forwards from `_cursor` to the first matching `(method, serialized-arg)`, advances the cursor **past it**, and falls back to the last-seen value only once the cursor is exhausted. **One recorded value is consumed per matching read.**

The recording proxy deduplicates observations on value-change: it emits a `(method,arg)` observation only when the returned value differs from the last emitted one. Measured on the committed `with-attunement.trace.jsonl` (5871 lines total: 1 `run.start`, 5842 `observation`, 26 `decision`, 2 `run.end`):

| Method | recorded obs count | distinct values | temporal class |
|---|---|---|---|
| `GetPlayerPosition` | 5788 | 5780 | **continuous** (one per tick) |
| `GetUiState` | 30 | — | mostly-gated |
| `IsAetheryteAttuned` | 6 | — | **step-gated** |
| `GetPlayerZone` | 5 | — | **step-gated** (zone changes rarely) |
| `GetQuestSequence` | 4 | `0, 0, 1, 255` | **step-gated** |
| `IsQuestAccepted` | 3 | — | **step-gated** |
| `IsQuestComplete` | 2 | `false, true` | **step-gated** |
| `GetItemCount` | 2 | — | **step-gated** |

The fatal pair is `IsQuestComplete(65644)`: exactly **two** recorded observations — `false` (line 3, before decision 1 at line 8) then `true` (line 5869, after the last decision and immediately before `run.end`). The engine reads `IsQuestComplete` **first** in `ResolveAction` (`QuestEngine.cs:310`) and returns `Done` when it is `true`.

On replay under the consume-one-per-read scanner:
- **Tick 1:** `IsQuestComplete` read → cursor returns the first value `false`. Engine proceeds, dispatches step 1 → decision `accept-quest / interact`. ✔ (transition 1)
- **Tick 2:** `IsQuestComplete` read → cursor has advanced past `false`; the only remaining value is `true`. Engine returns `Done`. ✘ (run ends at transition 1 of 26).

`GetQuestSequence(65644)` (`0,0,1,255`) has the identical defect: its deduped scalar advances `0 → 1 → 255` over three reads, far faster than the engine's 26-decision cadence, so even without the `IsQuestComplete` short-circuit the sequence block matching would desync.

The flaw was latent because the per-method `ReplayGameStateProviderTests` / `ReplayQuestStateTests` / `ObservationScannerTests` each exercise one or two reads in isolation; none drove a full multi-decision engine run to completion.

### 1.3 The deeper nuance the design MUST handle: two observation temporal semantics

A single uniform scanner cannot serve both of the following, which is the crux of the redesign:

- **Continuous / per-tick observations** — `GetPlayerPosition` (and to a lesser extent `GetUiState`). The engine reads these every tick and they change every tick as the player moves. They MUST advance per-read so the engine "sees" the player move far → near and flips `Navigate` → `Interact` (via the distance-≤-`stopDistance` check in `QuestEngine.ResolveInteractOrNavigate`, `QuestEngine.cs:498`, and the `playerNear` predicate in `PredicateEvaluator`). The deduped position stream (one obs per distinct position) drives arrival correctly **only** under per-read consumption.
- **Step-gated observations** — `IsQuestComplete`, `GetQuestSequence`, `IsQuestAccepted`, `IsAetheryteAttuned`, `GetItemCount`. These stay constant for many ticks and change only when the game advances a step/sequence (after an interaction completes). They must be **pinned** within a decision/step window and advance only at the right boundary — NOT per-read. Consuming them per-read is exactly the bug in §1.2.

### 1.4 The signal the design exploits: decisions are interleaved with observations in record order

The trace is JSONL in **record order**. `DecisionEvent`s are interleaved with `ObservationEvent`s, and the trace's record order is *identical to the engine's per-tick read order*. Verified on lines 1-18 of the committed trace:

```
run.start
obs GetQuestSequence=0     ┐
obs IsQuestComplete=false   │ segment 1 (inputs current while
obs GetUiState              │ the engine decided "accept-quest / Interact")
obs GetPlayerPosition       │
obs GetPlayerZone           │
obs IsQuestAccepted         ┘
decision accept-quest / Interact         ← decision 1 (boundary)
obs GetQuestFlags          ┐
obs GetUiState / GetPlayerPosition × n   │ segment 2
obs GetQuestSequence       │
obs IsQuestAccepted        ┘
decision accept-quest-65647 / Navigate   ← decision 2 (boundary)
...
```

**The observations recorded between decision K-1 and decision K are exactly the engine inputs that were current while the engine produced decision K.** Decision-event line positions (8, 18, 123, 126, 294, 302, 304, 1238, …) bracket the observations into segments; gated values land at segment boundaries (`IsQuestComplete=true` at line 5869, inside the *last* segment, right before `run.end`) while continuous positions fill the body of each segment.

This is the structural property the segmented driver exploits: replay segment K serves segment K's observations, and the harness advances the segment cursor exactly when the engine emits a new transition.

---

## 2. Fixed design decisions (do not relitigate)

### 2.1 Decision-anchored (segmented) replay

The trace is partitioned into **segments** by its `DecisionEvent` boundaries. **Segment K** = the observations recorded *after* decision K-1 (or after `run.start` for K=1) and *before* decision K. Replay drives the engine through segment K's observations until the engine emits a decision; the harness then advances the replay to segment K+1. The number of segments equals the number of recorded decisions (26 for `with-attunement`), plus a terminal tail (the observations after the last decision, up to `run.end`, which carry `IsQuestComplete=true` and drive the terminal `Done`).

### 2.2 Each segment serves its DECIDING state — the last value at-or-before the decision boundary

There is **no per-method classification** and **no per-read walk**. Because decisions are debounced (one recorded `DecisionEvent` per distinct transition), each Navigate and each Interact is already its own recorded decision → its own segment. The engine therefore never needs to "walk" positions within a segment to arrive; it only needs the state that was current when that segment's decision was made. One rule, applied uniformly to every `(method, arg)` pair:

> **A read of `(method, arg)` in segment K returns the most-recent observation for that pair recorded BEFORE segment K's decision boundary — the last value at index `< segmentEnd[K]` (the "deciding value"). Repeated reads within a segment return the SAME value. `AdvanceSegment` moves the boundary forward, so later segments may serve a newer value.**

This is the *deciding-state-per-segment* model. Why it is correct without a per-read walk:
- The decision recorded at segment K's boundary was made by the engine from the values it had **last** read before emitting it. Serving those last-at-or-before-boundary values reproduces decision K.
- A **step-gated** pair (`IsQuestComplete`) keeps its last-at-or-before value: `false` for every segment until the terminal tail, where `true` (recorded after the final decision) falls within the boundary → drives `Done`.
- A **continuous** pair (`GetPlayerPosition`) likewise serves its last-at-or-before-boundary value — the position that *caused* this segment's decision (far → Navigate; near → Interact). Each Navigate/Interact being a separate segment, no within-segment walk is needed.

Starvation is reserved for a `(method, arg)` pair that **never appears anywhere** in the trace (read-pattern drift), per §3.1.

> **Supersedes the earlier windowed-walk model.** An earlier draft had the scanner *walk* observations per-read within a segment window. Implementation showed that mechanism is unnecessary and harmful to keep: debounced decisions already segment every transition, so the deciding-value-per-segment rule above is both correct and simpler. The per-read walk, the per-pair forward cursor, and any trace pre-collapse are removed; the scanner serves `LastAtOrBefore(segmentEnd)` directly.

### 2.3 The harness advances the segment cursor on each NEW transition, in lockstep with the engine

`IFixtureState` gains an explicit segment-advance signal. The `EngineFixtureTests` loop already records a deduped `(stepId, actionType)` transition whenever the engine emits a `DecisionEvent` that differs from the previous one. **Each time it records a NEW transition, it tells the replay state to advance to the next segment** (via `OnTick`, whose contract changes from "no-op" to "advance segment cursor on a new decision"). The continuous-position cursor is *not* reset across segments — it advances monotonically through the whole trace — but its per-read reach is bounded by the current `segmentEnd`, so it cannot run ahead of the engine's decision cadence.

### 2.4 A decision the engine emits that does NOT match the expected segment is a transition mismatch, NOT starvation

The two failure surfaces stay distinct (per `FIXTURE_REPLAY_HARNESS_PLAN.md` §3.5, preserved):
- **Decision regression:** the engine ticks fine but the collected transition list ≠ `expectedTransitions` → the existing `Assert.Equal` failures (count / `stepId` / `actionType`). The segment driver never *forces* a particular decision; it only serves observations. If a regressed engine emits a different decision, the segment still advances and the mismatch surfaces in the final `Assert.Equal`.
- **Observation starvation:** the engine reads a `(method, arg)` pair that **never appears anywhere** in the trace → `ReplayObservationStarvationException` → the `WrapTickForStarvation` actionable `Assert.Fail` ("OBSERVATION STARVATION… re-record…"). This is read-pattern drift, not a decision regression.

The segmented driver introduces **no third failure mode** and **must not** convert a benign within-segment exhaustion into starvation: when a continuous pair runs out *within* the current segment window, it pins to the last in-window value (it does NOT throw). Starvation remains reserved for a pair never recorded at all.

### 2.5 Construction surface is preserved; the scanner is the unit that changes

`TraceReplayFixtureState.FromTraceFile(string path)` stays the public entry point and still returns an `IFixtureState`. `ReplayGameStateProvider(IReadOnlyList<ObservationEvent>)` and `ReplayQuestState(IReadOnlyList<ObservationEvent>)` keep their constructors. The change is concentrated in **(a) a new segmented scanner** that the providers delegate to, and **(b)** the providers and the fixture state being built from the *full* trace (decisions included) rather than an observation-only projection, so the scanner can compute segment boundaries.

The legacy `ObservationScanner` (consume-one-per-read) is **retained unchanged** so its three existing unit tests (`ObservationScannerTests`) stay green; the providers no longer use it for fixture replay. (Decision deferred to the Builder: either keep `ObservationScanner` as-is and add a sibling `SegmentedObservationScanner`, or have the providers take a small scanner interface — see §3.)

### 2.6 No timing reseeding, no schema change, no new trace event types

`FakeTimingProfile` (zero-delay) is used unchanged. The harness asserts the transition *sequence*, not tick counts. No `runId` reseeding. No changes to `ObservationEvent` / `DecisionEvent` / `RunStartEvent` / `RunEndEvent`.

---

## 3. Component changes

### 3.1 New: `SegmentedObservationScanner` (`QuestForge.Adapters.Fakes/Replay/SegmentedObservationScanner.cs`)

The heart of the change. Replaces the consume-one-per-read model with the windowed model of §2.2.

```csharp
namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Decision-anchored scanner. Built from the FULL trace (observations + decisions in
/// record order). Partitions observations into segments at DecisionEvent boundaries.
///
/// Within the current segment window, a read of (method, arg) returns the next
/// not-yet-consumed observation for that pair inside the window; if none remain inside,
/// it returns the last value seen for that pair at or before the window end (pinned).
///
/// Continuous pairs (many obs per segment) advance per-read within the window.
/// Step-gated pairs (0..1 obs per segment) pin automatically.
/// No per-method classification — the behavior is derived from observation density.
/// </summary>
public sealed class SegmentedObservationScanner
{
    // Flattened observation list (decisions stripped) plus the index, into that list,
    // at which each segment ends (i.e. the count of observations recorded before
    // decision K). _segmentEnds[k] = number of observations before the (k+1)-th decision.
    private readonly IReadOnlyList<ObservationEvent> _observations;
    private readonly IReadOnlyList<int> _segmentEnds;   // exclusive upper bounds, one per decision; last entry = _observations.Count (terminal tail)

    private int _segment;                                 // current segment index (0-based)
    private readonly Dictionary<string, int> _perPairCursor = new();   // key → next index to try for that pair
    private readonly Dictionary<string, ObservationEvent> _lastSeen = new();

    public SegmentedObservationScanner(IReadOnlyList<TraceEvent> trace) { /* build _observations + _segmentEnds */ }

    public int SegmentCount => _segmentEnds.Count;          // == number of decisions (+ terminal tail handled by last bound)
    public int CurrentSegment => _segment;

    /// <summary>Advance to the next segment. Called by the harness when a NEW transition is recorded.</summary>
    public void AdvanceSegment()
    {
        if (_segment < _segmentEnds.Count - 1) _segment++;
        // Clamp at the last segment (the terminal tail) so post-final reads see IsQuestComplete=true.
    }

    /// <summary>Window end (exclusive) for the current segment.</summary>
    private int SegmentEnd => _segmentEnds[_segment];

    public ObservationEvent Next(string method, object? argument)
    {
        var key = BuildKey(method, argument);            // same encoding as ObservationScanner
        var start = _perPairCursor.TryGetValue(key, out var c) ? c : 0;

        // 1. Next not-yet-consumed obs for this pair INSIDE the current window.
        for (var i = Math.Max(start, 0); i < SegmentEnd; i++)
        {
            var obs = _observations[i];
            if (obs.Method != method || !ArgEquals(obs.Argument, argument)) continue;
            _perPairCursor[key] = i + 1;                 // advance per-read (continuous walks forward)
            _lastSeen[key] = obs;
            return obs;
        }

        // 2. None remaining inside the window → pin to the last value at or before SegmentEnd.
        //    (For a gated pair with its sole obs already consumed, or recorded before the window.)
        var pinned = LastAtOrBefore(method, argument, SegmentEnd);
        if (pinned is not null) { _lastSeen[key] = pinned; return pinned; }
        if (_lastSeen.TryGetValue(key, out var last)) return last;

        // 3. Truly never recorded anywhere → starvation (unchanged semantics).
        throw new ReplayObservationStarvationException(
            $"No observation for method '{method}' with argument ... in or before segment {_segment}.");
    }
}
```

Key invariants the Builder must preserve:
- **Per-read advancement is bounded by `SegmentEnd`.** The loop never reads index `>= SegmentEnd`, so a continuous pair cannot leak the next segment's values into the current decision.
- **`LastAtOrBefore` scans `[0, SegmentEnd)` for the most recent matching obs.** This is the pin. For `IsQuestComplete`, in segments 1..25 the only matching obs (`false` at index 0) is at-or-before every `SegmentEnd`, so it pins `false`; in the terminal tail, `true` (recorded near the end) falls at-or-before the final `SegmentEnd`, so it pins `true`.
- **`AdvanceSegment` clamps at the last segment** so reads after the engine's final non-terminal decision still see the terminal-tail observations (`IsQuestComplete=true`, `GetQuestSequence=255`) that drive `Done`.
- **The terminal tail is a real segment.** When building `_segmentEnds`, append a final entry `= _observations.Count` so the observations after the last `DecisionEvent` form the terminal window. (If a `run.end` follows the last decision with no trailing observations, the last decision's window already contains the gated final values — the Builder confirms against the actual trace; see §8 risk 1.)

### 3.2 Changed: `ReplayGameStateProvider` / `ReplayQuestState`

Both currently hold a `private readonly ObservationScanner _scanner;` and call `_scanner.Next(...)`. Change to delegate to the segmented scanner, **sharing one scanner instance across both providers** (unlike today's two independent cursors), because segment advancement and the position window are global to the run:

```csharp
public ReplayGameStateProvider(SegmentedObservationScanner scanner)  // overload
{ _scanner = scanner; }
```

Retain the existing `IReadOnlyList<ObservationEvent>` constructor for backward compatibility with the per-method `ReplayGameStateProviderTests` / `ReplayQuestStateTests` (those construct a provider from a hand-built observation list and expect consume-one-per-read semantics). The Builder picks one of two equivalent strategies:

- **(A) Overload + shared scanner.** Add a constructor taking `SegmentedObservationScanner`; keep the `IReadOnlyList<ObservationEvent>` constructor wrapping the legacy `ObservationScanner`. `TraceReplayFixtureState` builds one shared `SegmentedObservationScanner` and passes it to both providers. **Preferred** — keeps the legacy tests untouched.
- **(B) Small `IObservationSource` seam.** Extract `ObservationEvent Next(string, object?)` to an interface implemented by both scanners; providers take the interface. More invasive; only if (A) proves awkward.

The per-method method bodies (`_scanner.Next(nameof(GetPlayerZone), null)` etc.) are otherwise unchanged.

### 3.3 Changed: `TraceReplayFixtureState` (`QuestForge.Engine.Tests/Replay/States/TraceReplayFixtureState.cs`)

`FromTraceFile` now reads the **full** trace (not just observations), builds ONE `SegmentedObservationScanner`, and wires both providers to it. `OnTick` changes from a no-op to a segment-advance signal.

```csharp
internal sealed class TraceReplayFixtureState : IFixtureState
{
    private readonly SegmentedObservationScanner _scanner;
    // ... GameState/QuestState built from _scanner; inert action adapters unchanged ...

    private TraceReplayFixtureState(IReadOnlyList<TraceEvent> trace)
    {
        _scanner   = new SegmentedObservationScanner(trace);
        GameState  = new ReplayGameStateProvider(_scanner);
        QuestState = new ReplayQuestState(_scanner);
    }

    public static TraceReplayFixtureState FromTraceFile(string tracePath)
    {
        var trace = TraceReader.ReadFile(tracePath);                 // FULL trace, decisions included
        var obsCount = trace.OfType<ObservationEvent>().Count();
        if (obsCount == 0)
            throw new InvalidDataException(
                $"Trace '{Path.GetFileName(tracePath)}' contains no observation events; " +
                $"a trace-backed fixture requires recorded engine inputs.");
        return new TraceReplayFixtureState(trace);
    }

    // CHANGED CONTRACT: advance the replay to the next segment whenever the harness records
    // a NEW transition (it calls OnTick once per recorded transition — see §3.4). Continuous
    // reads within the new segment now resolve to that segment's positions; step-gated reads
    // pin to the segment's value.
    public void OnTick(EngineAction action, int tick) => _scanner.AdvanceSegment();
}
```

> **Subtlety the Builder must get right (and the Tester pins):** the harness calls `OnTick` once per *recorded new transition*, NOT once per engine tick. The engine may tick several times within one segment (e.g. emitting the same `(stepId, navigate)` decision repeatedly as the player closes distance — these collapse to one transition). Those repeat ticks must stay in the *same* segment so the position cursor keeps walking the recorded approach. Therefore `AdvanceSegment` is driven by the harness's "new transition recorded" event, and the engine's intra-segment repeat ticks do not advance the segment. See §3.4.

### 3.4 Changed: `EngineFixtureTests` driver loop (`Quest66130ReplayTests.cs`)

Today (`Quest66130ReplayTests.cs:160`) `state.OnTick(action, tick)` is called **every** tick. Move it so the segment advances **only when a new transition is recorded**:

```csharp
var pair = (newDecision.StepId, ActionTypeString(action));
if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
{
    actualTransitions.Add(pair);
    state.OnTick(action, tick);   // ADVANCE SEGMENT — once per NEW transition only
}
// (when the decision repeats the previous transition, do NOT advance: stay in-segment)
```

Rationale: the segment boundary corresponds to a *change* of decision, which is exactly the recorded-decision cadence. Repeated identical decisions (the engine re-emitting `navigate` for the same step while approaching) belong to the same recorded segment, and the within-segment position walk must continue serving that segment's later (closer) positions until the engine flips to `interact`.

For the **scripted** `SimpleLinearAcceptanceState`, `OnTick` must keep firing in a way that preserves today's behavior. Two options; the Builder picks:
- **(A)** Keep calling `state.OnTick(action, tick)` every tick for scripted states and gate only the segment-advance inside `TraceReplayFixtureState.OnTick` (i.e. add a second method `OnTransitionRecorded()` to `IFixtureState` that the harness calls only on a new transition; scripted state's `OnTransitionRecorded` is a no-op, replay state's advances the segment; `OnTick` stays per-tick for scripted mutation). **Preferred** — does not change the scripted state's tick cadence at all.
- **(B)** Move the single `OnTick` to fire only on new transitions and verify `SimpleLinearAcceptanceState` still produces its expected transitions under that cadence (it scripts state changes keyed off the action it observes, which only meaningfully changes on a new transition — likely safe, but must be re-verified).

`IFixtureState` change for option (A):

```csharp
internal interface IFixtureState
{
    // ... existing adapter members ...
    void OnTick(EngineAction action, int tick);          // per-tick (scripted mutation)
    void OnTransitionRecorded(EngineAction action, int tick);   // NEW — once per new transition
}
```

`SimpleLinearAcceptanceState.OnTransitionRecorded` → `{ }`. `TraceReplayFixtureState.OnTransitionRecorded` → `_scanner.AdvanceSegment();` (and its `OnTick` becomes the no-op). The harness calls `OnTick` every tick (unchanged) and `OnTransitionRecorded` only when it appends to `actualTransitions`.

### 3.5 What does NOT change

- `ObservationScanner` (legacy) — untouched; its 4 unit tests stay green (regression criterion §4 group R).
- `ObservationMaterializer`, `ReplayJsonOptions`, `TraceReader.ReadFile` / `ReadFile<T>`, `ReplayExceptions`, `CapturingTraceWriter`, `FixtureLocator`.
- Inert adapters (`InertNavigator`/`InertTeleporter`/`InertInteractor`), `FakeCombat`/`FakeGearManager`/`FakeMinigameSkipper`/`FakeDialogueResolver`/`FakeTimingProfile`.
- The three-way dispatch in `EngineFixtureTests`, `TryResolveSourceTrace`, `WrapTickForStarvation`, `SafetyOverrunCount`, `DeserializeFixtureForTest`, the `EngineFixture.SourceTrace` field.
- `QuestEngine`, `PredicateEvaluator`, schema, the scripted `simple-linear-acceptance.json` fixture, and all questforge-data files (the committed `with-attunement.{json,trace.jsonl}` are already correct).

---

## 4. Acceptance criteria (Tester writes failing tests from these)

Tests live in `QuestForge.Engine.Tests` (the Fakes assembly's replay types are referenced there; same pattern as the existing replay tests). Groups:
- **S** — `SegmentedObservationScanner` unit tests (new file `QuestForge.Engine.Tests/Replay/SegmentedObservationScannerTests.cs`).
- **P** — provider/state wiring (`TraceReplayFixtureStateTests.cs`, extending the existing file).
- **H** — harness segment-advance protocol (around `EngineFixtureTests`).
- **E** — end-to-end proof (the parametric theory over the committed `with-attunement` fixture).
- **R** — regression (existing tests + scripted fixture stay green).

### Group S — `SegmentedObservationScanner` (decision-anchored windowing)

- **S1 (segment count == decision count, terminal tail included)** — Given a trace of `[run.start, obs×a, decision1, obs×b, decision2, obs×c, run.end]`, when a `SegmentedObservationScanner` is built, then `SegmentCount` equals 3 (two decisions + the terminal tail after decision2). Pin the exact tail-handling the Builder chooses, then assert it.
- **S2 (step-gated value pins across segments, then flips in the terminal tail)** — Given a trace where `IsQuestComplete(Q)` is recorded `false` in segment 1 (before decision1) and `true` only in the terminal tail (after the last decision), and nowhere else: in segment 0/1 `Next("IsQuestComplete", Q)` returns `false` on the **first and every repeated read within the segment** (it never advances to `true`); after `AdvanceSegment` reaches the terminal tail, `Next("IsQuestComplete", Q)` returns `true`. **This is the direct regression test for §1.2.**
- **S3 (deciding value is STABLE within a segment — repeated reads return the same value)** — Given a trace with three `GetPlayerPosition` observations `p1,p2,p3` all recorded before decision 1 (segment 1's boundary), when `Next("GetPlayerPosition", null)` is called three times **without** advancing the segment, then it returns `p3` (the last-at-or-before the boundary) on **every** call — NOT `p1` then `p2` then `p3`. Proves there is no per-read walk: a segment serves its single deciding value.
- **S4 (AdvanceSegment updates the served deciding value; a segment never serves a later segment's value)** — Given `GetPlayerPosition` recorded `p1` before decision 1 and `p2` before decision 2: while at segment 1, repeated `Next("GetPlayerPosition", null)` returns `p1` (never `p2`); after `AdvanceSegment`, `Next` returns `p2`. Proves the served value tracks the current segment boundary and cannot leak the next segment's value.
- **S5 (gated pin uses last-at-or-before window end, not the first ever)** — Given `GetQuestSequence(Q)` recorded `0` (segment 1), `0` (segment 2), `1` (segment 3): while at segment 3, `Next("GetQuestSequence", Q)` returns `1` (the most recent at-or-before the segment-3 window end), not `0`. Proves `LastAtOrBefore` semantics.
- **S6 (AdvanceSegment clamps at the last segment)** — Given a scanner at the terminal segment, calling `AdvanceSegment()` again does not throw and leaves `CurrentSegment` unchanged; subsequent reads still resolve terminal-tail values. Proves post-final reads remain valid (so the engine's terminal `IsQuestComplete=true` → `Done` read does not starve).
- **S7 (truly-absent pair → starvation, distinct from within-window exhaustion)** — Given a scanner whose trace never records `GetGil` anywhere, `Next("GetGil", null)` throws `ReplayObservationStarvationException`. Contrast: a continuous pair exhausted within the window pins (S4) and does NOT throw. Pins the §2.4 distinction at the scanner level.
- **S8 (argument keying)** — Given `IsAetheryteAttuned(8)=false` and `IsAetheryteAttuned(42)=true` recorded in the same segment, `Next("IsAetheryteAttuned", 8)` and `Next("IsAetheryteAttuned", 42)` resolve to the correct per-argument values (independent per-pair cursors), matching the existing `(method, serialized-arg)` keying.

### Group P — provider/state wiring

- **P1 (FromTraceFile builds a segmented-backed state)** — `TraceReplayFixtureState.FromTraceFile(path)` returns a state whose `GameState is ReplayGameStateProvider` and `QuestState is ReplayQuestState`, both backed by a **single shared** `SegmentedObservationScanner` (assert shared-instance behavior: a `GetQuestSequence` read via `QuestState` and a `GetPlayerPosition` read via `GameState` both observe the same `CurrentSegment` after an `OnTransitionRecorded`). Preserves the §2.5 construction surface.
- **P2 (empty trace → InvalidDataException)** — A trace with `run.start`/`decision`/`run.end` but zero `ObservationEvent`s throws `InvalidDataException` mentioning "no observation" (unchanged from `FIXTURE_REPLAY_HARNESS_PLAN.md` A3; the message contract is preserved even though the internal reader now reads the full trace).
- **P3 (OnTick is no longer the segment driver; OnTransitionRecorded is)** — Under option (A): calling `state.OnTick(action, tick)` repeatedly does NOT change `CurrentSegment`; calling `state.OnTransitionRecorded(action, tick)` advances it by one. (If the Builder chooses option (B), restate as: `OnTick` advances the segment and the harness only calls it on a new transition — the Tester writes against the chosen contract, which §6 done-criteria pins.)
- **P4 (gated read through the provider pins within a segment)** — Build a `TraceReplayFixtureState` from a trace where `IsQuestComplete(Q)=false` (segment 1) and `=true` (terminal tail). Without advancing the segment, call `QuestState.IsQuestComplete(Q)` twice → both `false`. Advance to the terminal segment → returns `true`. The provider-level mirror of S2.

### Group H — harness segment-advance protocol

- **H1 (segment advances once per NEW transition, not per tick)** — Drive the engine in the harness loop with a fake/stub `IFixtureState` recording how many times `OnTransitionRecorded` is invoked. Across a run that produces N deduped transitions over M ticks (M > N, because repeated identical decisions occur), assert `OnTransitionRecorded` was called exactly N times (once per recorded transition) while `OnTick` was called M times (every tick). Proves §3.4.
- **H2 (repeated identical decision stays in-segment)** — Construct a minimal trace+quest where the engine emits `navigate` for the same step on two consecutive ticks (player still far) then `interact` (player arrived). Assert the harness records two transitions (`navigate`, `interact`) and advanced the segment exactly twice — i.e. the two `navigate` ticks did NOT each advance the segment. (The second `navigate` tick must serve the *same* segment's next position, letting the engine close distance.)
- **H3 (engine decision not matching the expected segment surfaces as a transition mismatch, not starvation)** — Take the committed `with-attunement` data but feed the parametric assertion an `expectedTransitions` list with one element deliberately altered (in a unit-level harness invocation, not by mutating the committed file). Assert the failure is an `Assert.Equal` mismatch on `stepId`/`actionType` (the existing assertion block), NOT a `ReplayObservationStarvationException` and NOT the "OBSERVATION STARVATION" `Assert.Fail`. Locks §2.4.

### Group E — end-to-end proof (the failing case, now passing)

- **E1 (the 26-transition fixture replays to done — THE PROOF)** — With questforge-data present, the parametric `EngineFixtureTests.EngineProducesExpectedTransitions` running `with-attunement.json` (no `StateFactories` entry → generic segmented-replay path, backed by the sibling `with-attunement.trace.jsonl`) collects exactly the **26** transitions listed in `with-attunement.json` `expectedTransitions`, in order, and the terminal tick yields `EngineAction.Done` (`terminalOutcome: "done"`). This is the criterion that fails today (1 transition) and must pass after the redesign.
- **E2 (committed trace is a single clean run)** — A data-integrity assertion over `with-attunement.trace.jsonl`: exactly one `runId` (`20260525-023230-5aaadbb3`), exactly one `run.start` (quest 65644), a `run.end` with outcome `done`, exactly **26** `DecisionEvent`s, and `IsQuestComplete(65644)` recorded with values `[false, true]` in that order. Guards the input the proof depends on.
- **E3 (scripted and generic fixtures both green in one run)** — Both `simple-linear-acceptance.json` (scripted, 66130) and `with-attunement.json` (generic segmented replay, 65644) pass in the same `dotnet test QuestForge.Engine.Tests` invocation.
- **E4 (questforge-data absent → both skip, no failure)** — When `FixtureLocator.TryGetQuestForgeDataRoot()` is null, `AllEngineFixtures()` yields no cases and no fixture test fails (existing behavior; the existing E4 test stays valid).

### Group R — regression (no behavior loss)

- **R1 (legacy ObservationScanner unit tests unchanged)** — The four `ObservationScannerTests` (`Next_FirstObservationMatches…`, `Next_SkipForwardMatch…`, `Next_ArgumentMismatch…`, `Next_CursorExhausted…`) still pass: the legacy `ObservationScanner` is untouched and still backs the `IReadOnlyList<ObservationEvent>` provider constructors used by those tests.
- **R2 (legacy provider unit tests unchanged)** — `ReplayGameStateProviderTests` and `ReplayQuestStateTests` (which construct providers from a hand-built observation list and expect consume-one-per-read) still pass via the retained `IReadOnlyList<ObservationEvent>` constructor (strategy A) or the interface seam (strategy B).
- **R3 (TraceReader / FixtureLocator tests unchanged)** — `TraceReaderTests`, `FixtureLocatorTests` unaffected.
- **R4 (scripted fixture transitions unchanged)** — `simple-linear-acceptance.json` produces the identical transition sequence and terminal outcome it does today; the scripted `SimpleLinearAcceptanceState.OnTick` per-tick cadence is preserved (option A) or re-verified (option B).
- **R5 (FIXTURE_REPLAY_HARNESS_PLAN groups A/B/C/D still green)** — The existing `TraceReplayFixtureStateTests` A2/B1/B2/B3/B4/C1-C6/D2/D3/E4 pass unchanged. **A4 changes** (it asserts `OnTick` does NOT advance the scanner): under the new contract `OnTick` is still a no-op for advancement (advancement moved to `OnTransitionRecorded`), so A4 stays valid as written; the Tester confirms A4 still holds, and **A1/A5 stay green** (a single read with no segment advance returns the first recorded value, identical to before). D1 (truncated trace → starvation) stays valid because a never-recorded pair still starves.

---

## 5. Implementation order

**Phase A — `SegmentedObservationScanner` (Fakes), 1 day.** Add the new scanner with segment building, the windowed `Next`, `AdvanceSegment`, `SegmentCount`/`CurrentSegment`. Make group S pass. Leave `ObservationScanner` untouched. Done before B.

**Phase B — Provider + state wiring (Fakes + Engine.Tests), 0.5 day.** Add the `SegmentedObservationScanner` constructor overload to both providers (strategy A); change `TraceReplayFixtureState` to read the full trace, build one shared scanner, and move advancement to `OnTransitionRecorded`. Make groups P pass; confirm R1/R2 stay green. Done before C.

**Phase C — Harness protocol (Engine.Tests), 0.5 day.** Add `OnTransitionRecorded` to `IFixtureState` (no-op in `SimpleLinearAcceptanceState`); change the `EngineFixtureTests` loop to call `OnTransitionRecorded` only when it appends a new transition, keeping `OnTick` per-tick. Make group H + R4 pass. Done before D.

**Phase D — End-to-end (data already committed), 0.25 day.** Run the parametric theory; make E1/E2/E3 pass. If E1 diverges from the recorded 26 transitions, diagnose per §8 risk 2 (determinism gap vs benign noise) before touching `expectedTransitions`. Phase D is the proof gate.

---

## 6. Done criteria

1. `SegmentedObservationScanner` exists in `QuestForge.Adapters.Fakes/Replay/`, partitions a full trace at `DecisionEvent` boundaries, serves continuous pairs per-read within a window and pins step-gated pairs to the last-at-or-before-window value, clamps `AdvanceSegment` at the terminal segment, and throws `ReplayObservationStarvationException` only for a pair never recorded anywhere (group S green). No per-method continuous/gated classification exists in the codebase.
2. `ReplayGameStateProvider`/`ReplayQuestState` can be constructed from a shared `SegmentedObservationScanner`; the legacy `IReadOnlyList<ObservationEvent>` constructor is retained so `ObservationScanner`-backed unit tests are unaffected (R1, R2 green).
3. `TraceReplayFixtureState.FromTraceFile` reads the full trace, builds one shared segmented scanner, throws `InvalidDataException` on an observation-less trace, and advances the segment via `OnTransitionRecorded` (P1-P4 green). The public construction surface is unchanged.
4. The `EngineFixtureTests` loop advances the segment exactly once per recorded new transition and calls `OnTick` every tick; repeated identical decisions stay in one segment (H1, H2 green).
5. A regressed engine decision surfaces as the existing transition `Assert.Equal` mismatch, distinct from observation starvation (H3 green); the §2.4 two-failure-mode separation is preserved.
6. **`with-attunement.json` (quest 65644) replays via the generic path to its 26 transitions and terminal `Done` in `dotnet test QuestForge.Engine.Tests`** (E1 green) — the previously-failing 1-transition case now completes. Both it and the scripted `simple-linear-acceptance.json` pass in one CI run (E3). With questforge-data absent, both skip with no failure (E4).
7. All pre-existing replay/harness tests stay green: `ObservationScannerTests`, `ReplayGameStateProviderTests`, `ReplayQuestStateTests`, `TraceReaderTests`, `FixtureLocatorTests`, and the surviving `TraceReplayFixtureStateTests` groups A/B/C/D (group R green).
8. `docs/FIXTURE_REPLAY_HARNESS_PLAN.md` §4 and §11 risk-2 are annotated as superseded by this plan (one-line pointers; the load-bearing detail lives here).

---

## 7. Exclusions

This plan does **NOT** include:

- **Per-method continuous/step-gated classification.** Rejected by design (§2.2) in favor of the structural windowed rule. No `IsStepGated(string method)` table is added.
- **Changes to `ObservationScanner` (legacy).** It is retained verbatim to keep its unit tests and the per-method provider tests green. The fixture-replay path simply stops using it.
- **Schema, `QuestForge.Engine`, `QuestForge.Tools.*`, or questforge-data changes.** The engine read pattern, the committed quest, fixture, and trace are all correct; only the replay scanner/driver in `questforge` changes.
- **New trace event types, `TraceMode` work, or `runId` reseeding / timing-deterministic replay.** Out of scope (consistent with the superseded plan §3.4).
- **Asserting Navigate destinations or action parameters.** Fixtures still assert only `(stepId, actionType)`.
- **Multi-run trace handling.** The committed trace is already filtered to one `runId`; the segmented scanner assumes a single-run trace (E2 guards this). Multi-run partitioning is not added.
- **Branching/multi-path fixtures or mid-quest `initialState`.** Inherited linear-only, `fresh`-only limitations remain.
- **The extractor parity (`questforge-tools` FilenameLookup, `--with-trace`).** Already delivered by `FIXTURE_REPLAY_HARNESS_PLAN.md` §3.7; untouched here.

---

## 8. Risks

1. **Terminal-tail boundary modeling.** The gated final values (`IsQuestComplete=true` at trace line 5869, `GetQuestSequence=255`) sit *after* the last `DecisionEvent` and just before `run.end`. The Builder must ensure the terminal segment's window includes them so the engine's final-tick `IsQuestComplete` read returns `true` and yields `Done`. Verify empirically: build the scanner on the committed trace, advance to the last segment, assert `Next("IsQuestComplete", 65644)` → `true`. If the last decision's window already includes the `true` obs (no separate tail needed), the extra terminal segment is harmless (it clamps). Pin the chosen model in S1/S6.
2. **Replay must reproduce the recorded 26 decisions exactly (supersedes old §11 risk 2).** The trace includes `interact → navigate → interact` "flapping" on attune/talk/turn-in steps (real fail-open-when-position-momentarily-unread behavior). Under the segmented model, each such flap is its own recorded transition → its own segment, so the within-segment position serves the recorded sequence and the *identical* pure engine should reproduce it. If E1 diverges, determine whether it is a genuine determinism gap (fix the engine) or benign replay-vs-record noise; only in the latter case adjust `expectedTransitions` to the replay's actual output (the fixture then still guards future engine changes). Confirm on the first Phase-D run.
3. **Within-segment position exhaustion when the engine ticks more times than the segment has positions.** If the engine ticks K times in a segment but the segment recorded only J < K distinct positions, reads J+1..K pin to the last in-window position — correct (the player has stopped moving and the engine should now flip to `interact`/confirm). The Builder must ensure pinning (not starvation) here (S4). This is the mechanism that lets a short segment still drive arrival.
4. **Shared scanner vs two cursors.** Today each provider holds an independent `ObservationScanner`. The segmented model shares ONE scanner across both providers so segment state is global. The per-pair cursors are still keyed by `(method, arg)`, so `GameState` and `QuestState` reads do not interfere (they read disjoint method sets) — but the segment index and the position window are now shared, which is the intended behavior. Strategy A (overload) keeps the legacy two-cursor constructor for the old unit tests, avoiding a behavior change there.
5. **`OnTick` vs `OnTransitionRecorded` cadence for scripted states.** Moving segment advancement to `OnTransitionRecorded` (option A) leaves the scripted `SimpleLinearAcceptanceState.OnTick` per-tick cadence intact, so R4 is low-risk. If the Builder instead folds advancement into a transition-gated `OnTick` (option B), R4 must be re-verified because the scripted state's mutation timing changes. Option A is preferred precisely to de-risk this.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the acceptance criteria in §4.
- Happy paths: 7 scenarios (S1, S3, S5, P1, E1, E3, R4)
- Edge cases: 12 scenarios (S2, S4, S6, S8, P3, P4, H1, H2, E2, E4, R1, R2)
- Error cases: 4 scenarios (S7, H3, P2, R5)
- Expected total: ~23 tests — ~8 in a new `SegmentedObservationScannerTests` (group S), ~4 in `TraceReplayFixtureStateTests` additions (group P), ~3 harness-protocol tests around `EngineFixtureTests` (group H), ~4 end-to-end/data assertions via the parametric theory (group E), ~5 regression guards confirming existing suites stay green (group R, mostly already-written tests re-run).
