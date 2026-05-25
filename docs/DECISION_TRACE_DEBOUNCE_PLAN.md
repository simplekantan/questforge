# Decision Trace Debounce Implementation Plan

**Status:** ready for test creation
**Repo:** `questforge` only (no `questforge-tools` / `questforge-data` changes)
**Output:** trace files stop carrying per-tick `decision` spam. A `DecisionEvent` is written only when its `(RunId, StepId, ActionType)` differs from the last decision written to the current file — collapsing runs of identical consecutive decisions (e.g. ~2,000 repeated `navigate` ticks) to a single transition event. Lossless for every consumer; in-memory test traces are unaffected.

---

## 1. Problem

The engine emits one `DecisionEvent` per tick for every non-terminal action (`QuestEngine.Tick` → `TraceSafe(new DecisionEvent(...))`). During navigation the engine emits `navigate` every tick for thousands of ticks until the player arrives, so a single real run produces tens of thousands of near-identical decision lines. A recorded 65644 session held **10,200** decision events (vs 6,738 observations), making trace files large (~2.8 MB / 16,945 lines) and noisy.

Observations are already deduplicated on value-change (`RecordingGameStateProvider.cs:52-56` and `TraceSession.WriteObservation`). Decisions are not.

## 2. Why debouncing decisions is lossless

`DecisionEvent` carries only `(RunId, StepId, ActionType, At)` — **no action parameters** (`QuestForge.Adapters/Tracing/DecisionEvent.cs`). Two consecutive `navigate` decisions on the same step are byte-identical except `At`. Collapsing them discards only the repeat-count and intermediate timestamps, which every consumer already ignores:

- **`TraceToFixtureExtractor`** (questforge-tools) builds `expectedTransitions` by deduping consecutive identical `(stepId, actionType)` pairs — pre-debounced input yields an identical result.
- **The trace-replay fixture harness** consumes only `ObservationEvent`s; the engine re-derives decisions. Decisions in the file are irrelevant to replay.
- **`docs/FIXTURES.md`** explicitly states fixtures do not assert tick counts.

This mirrors the observation dedup exactly: observations emit on value-change; decisions emit on `(StepId, ActionType)`-change.

## 3. Fixed design decisions

- **Debounce, not full dedup.** Suppress only a decision identical to the **immediately preceding** decision written to the file. A transition that legitimately recurs later (e.g. `navigate → interact → navigate`) is preserved — all three are written. This matches the fixture extractor's "consecutive" dedup semantics.
- **Layer: `TraceSession.Write` (production file path only).** The engine keeps emitting every decision via `_trace.Write(...)`. `TraceSession` is the production write hub and already owns dedup state (`_dedup`, cleared on file open). Debounce lives there.
- **Test harness is untouched.** `EngineFixtureTests` (and the planned generic replay harness) use `CapturingTraceWriter` directly — a different `ITraceWriter` that never routes through `TraceSession`. Its loop uses "no new `DecisionEvent` this tick = terminal" (`Quest66130ReplayTests.cs:142`), which **requires** a per-tick decision stream. Because debounce lives in `TraceSession`, the in-memory harness still sees every decision and its terminal detection is unchanged. This is the load-bearing reason the debounce must NOT live in the engine's emission.
- **Always-on, no config flag.** Lossless for transitions, so there is no debug value in a verbose mode (matching observation dedup, which is unconditional).
- **Key includes `RunId`.** In `Always` mode one file spans multiple runs; keying on `(RunId, StepId, ActionType)` ensures a new run's first decision is never suppressed by the previous run's last decision. File reopen also clears the state.

## 4. Mechanism (`QuestForge.Adapters/Tracing/TraceSession.cs`)

Add one field and a guard in `Write`:

```csharp
// Last DecisionEvent written to the current file, for consecutive-debounce.
// Key: (RunId, StepId ?? "", ActionType). Null when none written since file open.
private (string RunId, string StepId, string ActionType)? _lastDecision;
```

In `Write(TraceEvent evt)`, after the file-open and gate checks, before `_writer.Write(evt)`:

```csharp
if (evt is DecisionEvent d)
{
    var key = (d.RunId, d.StepId ?? "", d.ActionType);
    if (_lastDecision == key) return;   // consecutive-identical → suppress
    _lastDecision = key;
}
```

Clear `_lastDecision` whenever the dedup cache is cleared, i.e. in `OpenFileUnderLock` alongside `_dedup.Clear()`:

```csharp
if (clearDedup)
{
    _dedup.Clear();
    _lastDecision = null;   // NEW
}
```

Notes:
- The guard only inspects `DecisionEvent`; `RunStartEvent`/`RunEndEvent`/`ObservationEvent`/`ActionSubmittedEvent`/`ActionCompletedEvent`/`StepRecordedEvent` pass through unchanged.
- Non-decision events between two identical decisions do **not** reset `_lastDecision` — so `decision(nav) → observation → decision(nav)` still suppresses the second `nav` (correct: the fixture extractor treats them as consecutive decisions).
- `WriteUnderLock` (the observation path used by `WriteObservation`) is **not** modified — decisions never flow through it.
- All access stays inside the existing `_lock`.

## 5. Optional related cleanup (in scope if cheap, else defer)

The session showed **duplicate `run.start`** events (4 for 2 runs): `EngineHost.BeginRun` writes a `RunStartEvent` and the engine's `EmitRunStartIfNeeded` writes another. The same `Write` guard can suppress a second `run.start` for a `RunId` already started in the current file:

```csharp
if (evt is RunStartEvent rs)
{
    if (_lastRunStart == rs.RunId) return;
    _lastRunStart = rs.RunId;
}
```

Decision applied: **include this** (it is the same one-field pattern and removes a second source of trace noise the user flagged). Tested by group B. If it proves entangled with run lifecycle, split it to a follow-up — the decision-debounce (§4) is the primary deliverable and must not be blocked by it.

## 6. Acceptance criteria

Tests live in `QuestForge.Engine.Tests/Tracing/TraceSessionTests.cs` (extend the existing file). Use the `TraceSession` constructor's injectable `writerFactory` to supply a capturing in-memory `ITraceWriter`, then assert what reached it. Open a file via the appropriate lifecycle call for the mode under test (e.g. `OnQuestRunStart` in `QuestRun` mode, or `ChangeMode(Always)`).

### Group A — decision debounce

- **A1 (consecutive identical decisions collapse to one)** — Open a file; `Write` three `DecisionEvent(run, "travel-to-x", "navigate")` in a row; assert the inner writer received exactly **one** decision.
- **A2 (different stepId is a new transition)** — `Write` `decision(run, "a", "navigate")` then `decision(run, "b", "navigate")`; assert **both** written.
- **A3 (same stepId, different actionType is a new transition)** — `decision(run, "a", "navigate")` then `decision(run, "a", "interact")`; assert **both** written.
- **A4 (recurring transition after a different one is preserved — debounce, not full dedup)** — `navigate(a) → interact(a) → navigate(a)`; assert **three** decisions written (the second `navigate(a)` is not suppressed because `interact(a)` came between).
- **A5 (non-decision events between identical decisions do not reset debounce)** — `decision(run, "a", "navigate")`, then an `ObservationEvent`, then `decision(run, "a", "navigate")`; assert exactly **one** decision (and the observation) written.
- **A6 (non-decision events pass through unchanged)** — `Write` a `RunStartEvent`, `ObservationEvent`, `RunEndEvent`; assert all three reach the inner writer (debounce never touches them).
- **A7 (file reopen clears debounce)** — In `QuestRun` mode: `OnQuestRunStart`; `decision(run, "a", "navigate")`; `OnQuestRunEnd` (closes file); `OnQuestRunStart` again (new file); `decision(run, "a", "navigate")`; assert the new file's writer received the decision (not suppressed across files).
- **A8 (different runId is not cross-suppressed)** — In `Always` mode (single file): `decision("run1", "a", "navigate")` then `decision("run2", "a", "navigate")`; assert **both** written (key includes RunId).

### Group B — run.start dedup (§5)

- **B1 (duplicate run.start for same runId suppressed)** — Open a file; `Write` two `RunStartEvent` with the same `RunId`; assert exactly **one** written.
- **B2 (run.start for a different runId passes)** — Two `RunStartEvent` with different `RunId`s; assert **both** written.
- **B3 (run.start dedup cleared on file reopen)** — `run.start(r1)`, close, reopen, `run.start(r1)`; assert the reopened file received it.

### Group C — consumer-safety regression (no behavior change elsewhere)

- **C1 (observation dedup still works)** — Existing `WriteObservation` dedup behavior is unchanged (regression guard over the existing tests).
- **C2 (`EngineFixtureTests` unaffected)** — The scripted `simple-linear-acceptance` fixture still passes: it uses `CapturingTraceWriter`, so it sees every per-tick decision and its terminal detection is unchanged. (Run `dotnet test QuestForge.Engine.Tests --filter EngineFixture`.)
- **C3 (gate/file-open semantics preserved)** — A decision written while the gate is closed or no file is open is still dropped exactly as before; debounce does not change drop behavior (the debounce guard runs only after the existing checks).

## 7. Implementation order

1. **RED:** Add A/B/C tests to `TraceSessionTests.cs`; confirm A/B fail (debounce not implemented), C pass.
2. **GREEN:** Add `_lastDecision` (+ `_lastRunStart`) fields, the `Write` guards, and the `OpenFileUnderLock` clears. Make A/B green.
3. **REFACTOR/VERIFY:** Full `dotnet test QuestForge.Engine.Tests` green (esp. `EngineFixtureTests`, `TraceSessionTests`, `TraceEndToEndTests`, `RecordingProxyCoverageTests`). Spot-check `questforge-tools` `QuestForge.Tools.Trace.Tests` still green (extractor builds its own traces, should be unaffected).

## 8. Done criteria

1. Consecutive identical `DecisionEvent`s are collapsed to one in `TraceSession`-written files; distinct transitions and recurring-after-different transitions are preserved (group A green).
2. Duplicate `run.start` for the same run is suppressed (group B green).
3. Debounce state resets on file open and is keyed by `RunId`; no cross-run or cross-file suppression.
4. `CapturingTraceWriter`-based tests (the fixture harness) see the full per-tick decision stream — `EngineFixtureTests` and all existing trace tests pass unchanged (group C green).
5. No `questforge-tools` or `questforge-data` change required.

## 9. Exclusions

- **No engine change.** `QuestEngine` keeps emitting a `DecisionEvent` per tick; suppression is purely a trace-file concern in `TraceSession`.
- **No observation-dedup change.** Already implemented; untouched.
- **No config flag / verbose mode.** Debounce is unconditional.
- **No new trace event types, no schema change, no fixture-format change.**
- **Not the fixture-replay harness** (`docs/FIXTURE_REPLAY_HARNESS_PLAN.md`) — this is a prerequisite cleanup so re-recorded traces are born small; the two are independent PRs.
- **No retroactive slimming of existing trace files** (the prior 65644 session stays as-is; the next recording benefits).

## 10. Risks

1. **A consumer that genuinely needs per-tick decision density in the file.** None known: the fixture extractor and replay both ignore decision repetition, and the in-memory harness bypasses `TraceSession`. `TraceToQuestExtractor` walks decisions positionally but wants one decision per transition (debounced input is what it prefers); confirm its tests stay green as the regression check.
2. **`run.start` dedup (§5) interacting with run lifecycle** (e.g. a legitimate re-start of the same runId in one file). Mitigation: if entangled, split §5 to a follow-up; §4 is the primary deliverable.

✅ READY FOR TEST CREATION — ~14 tests (A1–A8, B1–B3, C1–C3) in `QuestForge.Engine.Tests/Tracing/TraceSessionTests.cs`.
