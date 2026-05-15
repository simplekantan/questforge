# Phase 7 Implementation Plan: Canonical Trace + Replay Harness

**Status:** ready for implementation
**Input docs:** docs/NEXT_STEPS.md §Phase 7, docs/TRACE_FORMAT.md §6 (Determinism), docs/TRACE_FORMAT.md §"Known divergences from this spec", docs/ADAPTERS.md §4.8 (recording proxy), docs/PHASE_6_PLAN.md Appendix A (naming question deferred), docs/ARCHITECTURE.md (testability boundary)
**Output:** A replay test (xUnit, runs in CI without a game) that loads the Phase 6 canonical trace fixture, drives `QuestEngine` against trace-backed `ReplayGameStateProvider` + `ReplayQuestState` plus non-recording fakes, and asserts the engine emits the same sequence of `EngineAction` types in the same order as the trace's `decision` events. CI fails when engine logic regresses on any committed canonical trace.
**Predecessor:** Phase 6 complete — plugin loads in Dalamud, quest 66130 completes end-to-end, trace files written to `pluginConfigs\QuestForge\traces\<runId>.jsonl`. 103 Phase 5 tests + Phase 6 plumbing all green.

---

## Goal restated

Phase 6 made the engine produce real traces against real Dalamud adapters. Phase 7 closes the loop: those traces become the regression-test corpus for the engine itself.

The deliverable is a single xUnit test that proves the loop works end-to-end on quest 66130. Once one canonical trace replays cleanly, every subsequent quest added to the corpus follows the same pattern: capture a real trace in-game, copy it to `questforge-data/quests/...`, add one line to the test theory data, ship.

The unit of regression detection is the **engine action sequence** — the ordered list of `decision.actionType` values the engine emits, given the recorded sequence of game-state observations. If a future engine change starts emitting `Wait` where it used to emit `Interact`, the replay test fails with the specific quest and the specific divergence point.

What stays out of scope:
- Real adapter regression testing (that's E2E, requires a running game)
- Trace format reconciliation work beyond what replay actually needs (the Phase 5/6 flat-payload shape stays — replay reads it as-is)
- Full `TRACE_FORMAT.md` spec compliance (`seq`, `ts`, `v`, `data` wrapper, `engineConfig`, action.submitted/completed pairs in production traces) — deferred until a replay-driven need surfaces
- Cross-quest comparison, statistical replay, fuzzing — Phase 10+

---

## Architectural decisions (read before coding)

### 1. Replay providers live in `QuestForge.Adapters.Fakes`, not a new project

**Decision:** put `ReplayGameStateProvider` and `ReplayQuestState` (plus `TraceReader`) in a new `Replay/` folder under `QuestForge.Adapters.Fakes`. No new project.

**Rationale:**

The replay providers are conceptually test-only adapter implementations. They have no production caller. Test fixtures plug them into the same DI shape used by `EngineTestHarness`. Creating a `QuestForge.Adapters.Replay` project would:

1. Add a third project that depends on `QuestForge.Adapters` for the interface contracts, on `QuestForge.Adapters.Tracing` for the trace event types (already in `QuestForge.Adapters`), and on `System.Text.Json` for deserialization — all of which `QuestForge.Adapters.Fakes` already references.
2. Require `QuestForge.Engine.Tests.csproj` to add another `ProjectReference` for one folder of code.
3. Buy nothing in terms of testability isolation — `Adapters.Fakes` is already test-scoped (the production plugin should not be referencing it, even though Phase 6 does for the recording proxies — see decision 2).

The "Fakes" name is awkward for code used in replay regression tests, but the boundary cost of moving is real and the boundary value is low. Phase 7 takes the same pragmatic stance Phase 6 took: keep the awkward name, hoist the recording proxies (decision 2), revisit naming when there's a forcing function.

**Alternatives considered and rejected:**

- *New `QuestForge.Adapters.Replay` project.* Three project files for code that always coexists with fakes. Rejected for churn-without-benefit.
- *Replay providers in `QuestForge.Engine.Tests/Replay/`.* They could in principle live in the test project. Rejected because `qf-trace replay` (a future CLI tool in `questforge-tools`) will want to consume them outside the test runner. Keeping them in `Adapters.Fakes` makes them library-callable.
- *Replay providers in `QuestForge.Adapters` proper.* Same problem as putting the recording proxies there — they're not part of the engine's contractual world. Rejected.

### 2. Recording proxies hoist from `QuestForge.Adapters.Fakes` to `QuestForge.Adapters`

PHASE_6_PLAN.md §A deferred this; Phase 7 does it.

**Where:** create `QuestForge.Adapters/Recording/RecordingGameStateProvider.cs` and `QuestForge.Adapters/Recording/RecordingQuestState.cs`. Namespace becomes `QuestForge.Adapters.Recording`.

**Rationale:**

These proxies wrap production-side adapter interfaces (`IGameStateProvider`, `IQuestState`) using production-side primitives (`ITraceWriter`, `TraceEvent` derivatives). They are not test fakes. Phase 6 used them in `EngineHost.cs` in the live plugin. Their location in `Adapters.Fakes` is a vestige of having been built first in Phase 5 when the only consumer was the engine test harness.

Hoisting them now (rather than later) is timed deliberately: Phase 7 is when Phase 6's awkwardness becomes acute, because the replay-side counterparts (`ReplayGameStateProvider`) want to share file paths with the recording-side proxies in any reader's mental model. Splitting them across `Adapters.Recording` and `Adapters.Fakes.Replay` is acceptable; leaving them split across `Adapters.Fakes.Recording` and `Adapters.Fakes.Replay` is not — both being "in fakes" muddies which one is production-grade.

**Files to update after the hoist:**

| File | Change |
|---|---|
| `QuestForge.Adapters.Fakes/Recording/RecordingGameStateProvider.cs` | Move to `QuestForge.Adapters/Recording/`. Change namespace from `QuestForge.Adapters.Fakes.Recording` to `QuestForge.Adapters.Recording`. |
| `QuestForge.Adapters.Fakes/Recording/RecordingQuestState.cs` | Same — move and rename namespace. |
| `QuestForge.Plugin/EngineHost.cs` | Change `using QuestForge.Adapters.Fakes.Recording;` to `using QuestForge.Adapters.Recording;`. |
| `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` | Same `using` swap. |
| `QuestForge.Engine.Tests/Recording/RecordingGameStateProviderTests.cs` | Same. |
| `QuestForge.Engine.Tests/Recording/RecordingQuestStateTests.cs` | Same. |
| `QuestForge.Engine.Tests/Recording/RecordingProxyCoverageTests.cs` | Same. |
| `QuestForge.Adapters.Fakes/Recording/AdapterCall.cs` | **Stays in Fakes.** It's a test-side capability used by `FakeNavigator.RecordedNavigationRequests` and similar. Not consumed by the hoisted recording proxies. |
| `QuestForge.Adapters.Fakes/Recording/CallLog.cs` | **Stays in Fakes.** Same reason. |

The `Recording/` folder remains in `Adapters.Fakes` for `AdapterCall` and `CallLog` (used by fakes like `FakeNavigator` to record per-method calls for test inspection — unrelated to trace-format recording).

**Compatibility note:** `RecordingGameStateProvider` and `RecordingQuestState` already depend only on types in `QuestForge.Adapters` (`IGameStateProvider`, `ITraceWriter`, `ObservationEvent`, `Result<T>`, the identifier types). No transitive dependency on `Adapters.Fakes` exists — the hoist is purely a namespace move.

**Grep verification before merging:** after the hoist, `grep -r "Adapters.Fakes.Recording.Recording"` across the solution should return zero hits.

### 3. The Phase 6 trace format is the input contract — replay does NOT require format reconciliation first

The "Known divergences" section of `TRACE_FORMAT.md` lists seven Phase 5–6 simplifications versus the full spec. The temptation in Phase 7 is to fix all of them first ("the spec says `seq`, so let's add `seq` before building replay"). This plan **rejects** that approach.

**Decision:** the replay harness reads Phase 6 traces as they currently exist. Flat payload, no `seq`, no `ts`, no `v` field, no `data` wrapper, no `action.submitted`/`action.completed` pairs. The recorder is not modified in Phase 7.

**Rationale:**

The Phase 6 trace contains everything replay actually needs:
- Run identity (`runId`, `questId`) for instantiation
- Ordered observations (line order is the implicit `seq` — sufficient for in-order replay)
- Ordered decisions (`decision.actionType`, `decision.stepId`) for the assertion target

What the divergences omit (`engineConfig`, `wallClockUtc`, `dataHash`, action.submitted/completed pairs, monotonic `ts`) is genuinely not used by the minimum-viable replay. `engineConfig` matters when the engine grows config-affecting decision paths (Phase 7+); `action.submitted`/`action.completed` matter for action-lifecycle determinism checks, which are deferred. `ts` matters only for timing-sensitive replay, which §6.6 of the trace format spec explicitly excludes from scope.

Adding `seq`, `ts`, `v`, and the `data` wrapper to the recorder is a **separate refactor** that can land in Phase 7 if convenient, but it is not a precondition for replay. The recorder change touches every `TraceEvent` subtype and every `TraceWriter` test; replay touches none of them. Sequencing them as two PRs lets either land without blocking the other.

**Forward-compatibility:** the `TraceReader` introduced in this phase deserializes via `TraceEventJsonContext`, which already handles the polymorphic discriminator. If `seq`/`ts`/`v` are later added to the event records as new optional fields, old fixtures continue to deserialize (System.Text.Json ignores missing fields when defaults exist). The replay harness must not require these fields to be present — it must derive sequence from line order and ignore `ts` entirely.

### 4. Replay providers fail loudly on starvation; succeed silently on under-consumption

The trace records observations the engine made at recording time. At replay time, the engine may:

- **Read the same fields in the same order** → happy path, every recorded observation is consumed in turn.
- **Read fewer fields than recorded** → trace has surplus observations the engine never asked for this run. **This is acceptable.** Surplus observations are unconsumed; the test does not assert exhaustive consumption. (A future "strict replay" mode might, but v1 doesn't.)
- **Read more fields than recorded** → trace is exhausted. The engine has started reading state it didn't read at recording time. **This is a regression — fail loudly.** Throw `ReplayObservationStarvationException` naming the method that was called with no observation available.
- **Read a different field next** → trace's next observation has `method != requested`. **This is a regression — fail loudly.** Throw `ReplayObservationOrderException` showing the expected vs actual method names and the index in the observation stream.

The strict-order check is what makes replay catch regressions. If a new engine version starts calling `GetPlayerPosition` before `GetPlayerZone` (or vice versa), the recorded order no longer matches and the test fails. This is the desired behavior per `TRACE_FORMAT.md` §6.4:

> Queries for fields not in the recorded observation fail loudly — this indicates the engine has started reading state it didn't read at recording time, which is a real regression to surface.

The implementation walks a single `int _cursor` across the flat observation list, scanning forward to find a matching `(method, argument)` pair on each adapter call. The exact matching strategy is documented in §6 below.

**Argument matching:** observations record an `argument` field (e.g., `GetQuestSequence` records `argument: {"value": 66130}`). The replay provider must match both the method name and the serialized argument. Two consecutive `GetQuestSequence` calls with different `QuestId` arguments are distinct entries that consume distinct observations. JSON-element equality (`JsonElement.GetRawText() == requestedRawText`) is the comparison — the recording proxy serialized the request the same way.

### 5. Fixture discovery uses a path-walking convention rooted at the test assembly

The canonical trace fixture for quest 66130 will live at:

```
<repo-root>/../questforge-data/quests/arr/msq/66130-canonical-trace-phase6.jsonl
```

The test project must locate this file at runtime. Three deployment shapes are in scope:

1. **Local developer:** repo cloned at `C:\Users\me\src\questforge\` with sibling `questforge-data\` checked out.
2. **CI (GitHub Actions on `questforge`):** `actions/checkout` puts `questforge` in `$GITHUB_WORKSPACE`. A separate step (`actions/checkout` with `repository: <org>/questforge-data` and `path: questforge-data`) places the data repo in `$GITHUB_WORKSPACE/questforge-data`. Both end up siblings.
3. **`dotnet test` from anywhere:** the test discovers fixtures relative to the assembly location, not the current directory.

**Decision:** the fixture path is resolved by walking upward from `AppContext.BaseDirectory` (the test bin folder) until a directory containing `questforge-data/quests` is found, then appending the relative fixture path. If no parent directory contains it, the test is **skipped** (via `Assert.Skip(...)`, the xUnit v3 API — the test project uses `xunit.v3` per `QuestForge.Engine.Tests.csproj`) with a clear message naming what was expected and where.

A single helper centralizes this:

```csharp
// QuestForge.Engine.Tests/Replay/FixtureLocator.cs (sketch)
internal static class FixtureLocator
{
    /// <summary>
    /// Walks upward from the test assembly directory until a sibling 'questforge-data'
    /// directory is found. Returns the absolute path to the requested fixture inside it,
    /// or null if no questforge-data sibling exists at any ancestor depth.
    /// </summary>
    public static string? TryFindQuestForgeDataFixture(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "questforge-data", relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static string LocateOrSkip(string relativePath)
    {
        var path = TryFindQuestForgeDataFixture(relativePath);
        if (path is null)
            Assert.Skip(
                $"questforge-data fixture not found: {relativePath}. " +
                $"Expected a sibling 'questforge-data' directory at some ancestor of {AppContext.BaseDirectory}. " +
                $"On developer machines: clone questforge-data next to questforge. " +
                $"In CI: ensure the questforge-data checkout step ran.");
        return path!;
    }
}
```

`Assert.Skip` availability is confirmed by the test project's `xunit.v3` package reference (version 2.0.0). No additional NuGet dependency is needed.

**CI wiring (.github/workflows/test.yml additions):**

```yaml
- uses: actions/checkout@v4
  with:
    path: questforge

- uses: actions/checkout@v4
  with:
    repository: <org>/questforge-data
    path: questforge-data
    # public repo; no token needed unless we go private later
```

Then `dotnet test questforge/QuestForge.Engine.Tests/` finds `$GITHUB_WORKSPACE/questforge-data/quests/...` by walking up from the test bin folder. The replay test runs as part of the standard test suite.

**Why skip-on-missing rather than fail-on-missing:** local developers without `questforge-data` should still be able to run `dotnet test` for engine/adapter/schema work without blockers. CI guarantees presence via the explicit checkout step. The skip message names exactly what's missing so the failure is actionable.

### 6. The replay assertion is on `decision.actionType` strings, not deep equality

The trace's `DecisionEvent` carries `runId`, `stepId`, `actionType`, `at`. The replay assertion compares the sequence of `actionType` strings (and optionally `stepId`) emitted by the engine against the recorded sequence.

**Decision:** assert on ordered `(stepId, actionType)` pairs. Both fields must match, in order. Parameters (e.g., the exact `WorldPosition` of a `Navigate` action) are **not** asserted in v1.

**Rationale:**

The trace's `DecisionEvent` does not carry action parameters (only the type name). Asserting on parameters would require either:
1. Extending `DecisionEvent` to record full parameters — defers Phase 7 behind a format change.
2. Re-deriving parameters from quest data during replay — possible but duplicates engine logic in the test.

Phase 7 takes the looser assertion intentionally. The sequence of `(stepId, actionType)` pairs is a strong invariant: if the engine emits `Navigate, Interact, Navigate, Interact` against the same observations, and the recorded trace shows `Navigate, Interact, Navigate, Interact` for the same step IDs, the engine logic for action *type* selection is regression-free. Parameter regressions (e.g., navigating to the wrong coordinates) require a parameter-aware extension, which is left for Phase 7+.

The recorded `decision.actionType` is the `EngineAction` subtype name as written by `QuestEngine.Tick` (e.g., `"Navigate"`, `"Interact"`, `"Wait"`). The test re-derives it as `action.GetType().Name`. String comparison is brittle to rename — that's intentional. Renaming `EngineAction.Navigate` is an engine-level breaking change that should re-record canonical traces.

**The `Wait` and `Done` cases:**

- The engine emits `Wait` between meaningful actions while waiting for postconditions; the trace records each `Wait` decision. Replay must match them. `Wait` decisions are part of the sequence.
- The engine emits `Done` once; **no `decision` event is recorded for `Done`** — `QuestEngine.Tick` (line 96) emits only a `run.end` event when it returns `EngineAction.Done`. The trace's `decision` events therefore end at the last `Wait`/`Interact`/`Navigate` before completion. The replay test handles this by driving one extra tick after consuming all recorded `decision` events and asserting the engine returns `EngineAction.Done` (matching `run.end.outcome == "done"`).
- The engine emits `AwaitUser` to terminate runs that need manual intervention; the recorded trace shows the corresponding `decision` plus a `run.end` with `outcome == "awaitUser"`. Replay must end at the same point with the same outcome — handled by the same "one extra tick after the loop" pattern.

### 7. The replay test runs the live engine — not a snapshot of an older engine

The point of replay is to test the *current* engine against historical traces. The test instantiates `QuestEngine` from current `QuestForge.Engine` and drives it with the current `ReplayGameStateProvider`.

Future engine changes that legitimately produce different action sequences (e.g., adding `equip-gear-for-quest` step support that emits a new pre-step `EquipGear` action) will cause replay failures on quests whose canonical traces predate the change. This is intended: the failure prompts the maintainer to either:

1. Update the quest file (most likely the new step type is opt-in per quest).
2. Re-record the canonical trace against the new engine and commit it.
3. Conclude the engine change is a regression and fix the engine.

The replay test is a regression *detector*, not a fixture-stability oracle. Sequence-breaking engine changes are *expected* occasionally; the workflow handles them by re-recording. The CI failure mode names the diverging quest, the index, and the expected/actual `(stepId, actionType)` pair to make the choice obvious.

### 8. Phase 6 traces have a known engine quirk: thousands of per-tick decisions

Reading the actual Phase 6 trace at `pluginConfigs\QuestForge\traces\<recent-run>.jsonl` (12,634 lines for one canonical run): the Phase 6 plugin tick rate produces a `decision` event on every framework tick, alongside multiple `observation` events per tick (roughly five observations per tick). For a 12,634-line trace, that breaks down to **~2,500 `decision` events** and **~10,000 `observation` events** — about 2,500 ticks total. For quest 66130, this means many consecutive `Navigate` decisions while the player is walking, followed by `Wait` decisions while dialogue advances, etc.

This is **expected** and matches how the engine actually runs: `QuestEngine.Tick` returns the same `Navigate` repeatedly while `IsNavigating` is true, because the postcondition (player at target) is not yet satisfied. Each tick produces one `decision` event in the trace.

**Replay matches this faithfully.** The test does not collapse consecutive identical decisions. It drives the engine for as many ticks as there are decisions in the trace. The assertion is element-wise equality across the full sequence.

This makes the replay test moderately expensive in tick-count (~2,500 decisions for quest 66130) but still cheap in wall-clock (all fakes, all synchronous). Estimated single-test runtime: under 5 seconds on CI hardware. If this becomes a problem, a Phase 7+ optimization can collapse runs of identical decisions on both sides before comparison, or sample at a stride. Phase 7 takes the brute-force approach for correctness clarity.

**Implication for the test fixture:** the canonical trace file is the unmodified Phase 6 output. No collapsing, no editing. The fixture name `66130-canonical-trace-phase6.jsonl` makes the provenance clear; future re-records would be `-phase8.jsonl` etc., with promotion via PR per `TRACE_FORMAT.md` §3.

---

## Tasks

### Task 1 — Copy the canonical trace (manual, by the user)

Not part of the build. The user copies one trace file:

```
SOURCE: C:\Users\publi\AppData\Roaming\XIVLauncher\pluginConfigs\QuestForge\traces\<chosen-run>.jsonl
DEST:   C:\Users\publi\RiderProjects\questforge-data\quests\arr\msq\66130-canonical-trace-phase6.jsonl
```

The chosen `<chosen-run>` is the most recent trace whose final non-empty line is `{"type":"run.end","outcome":"done",...}`. The user verifies this by reading the last non-empty line of the file. The user is producing a new, smaller trace with recent fixes applied (cutscene-skip and `IsReady` adjustments); the previously-existing 8 MB trace from before those fixes is **not** the canonical fixture.

**Convention:** the fixture lives in the same directory tree as the quest definition will, so a PR adding a new quest naturally includes its trace fixture as a sibling file. Phase 6's `66130-coming-to-uldah.json` (if/when committed to `questforge-data`) will live at `quests/arr/msq/66130-coming-to-uldah.json`; this trace is its sibling.

**Git LFS:** per `TRACE_FORMAT.md` §3, trace files are tracked with LFS. The user runs `git lfs track "*.jsonl"` in `questforge-data` if not already configured, then `git add .gitattributes 66130-canonical-trace-phase6.jsonl` and commits. This is also outside the codebase scope of Phase 7.

The plan acknowledges this is a manual step; no code or script automates it.

### Task 2 — Hoist recording proxies to `QuestForge.Adapters.Recording`

**Files moved:**

- `QuestForge.Adapters.Fakes/Recording/RecordingGameStateProvider.cs` → `QuestForge.Adapters/Recording/RecordingGameStateProvider.cs`
- `QuestForge.Adapters.Fakes/Recording/RecordingQuestState.cs` → `QuestForge.Adapters/Recording/RecordingQuestState.cs`

**Code changes inside the moved files:**

- Change `namespace QuestForge.Adapters.Fakes.Recording;` → `namespace QuestForge.Adapters.Recording;`
- Remove any `using QuestForge.Adapters.Fakes.*` lines (there are none — verified by inspecting the existing files).

**Files with `using` updates:**

| File | Before | After |
|---|---|---|
| `QuestForge.Plugin/EngineHost.cs` | `using QuestForge.Adapters.Fakes.Recording;` | `using QuestForge.Adapters.Recording;` |
| `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` | `using QuestForge.Adapters.Fakes.Recording;` | `using QuestForge.Adapters.Recording;` |
| `QuestForge.Engine.Tests/Recording/RecordingGameStateProviderTests.cs` | `using QuestForge.Adapters.Fakes.Recording;` | `using QuestForge.Adapters.Recording;` |
| `QuestForge.Engine.Tests/Recording/RecordingQuestStateTests.cs` | `using QuestForge.Adapters.Fakes.Recording;` | `using QuestForge.Adapters.Recording;` |
| `QuestForge.Engine.Tests/Recording/RecordingProxyCoverageTests.cs` | `using QuestForge.Adapters.Fakes.Recording;` | `using QuestForge.Adapters.Recording;` |

**Done criteria for Task 2:**

- `grep -r "QuestForge.Adapters.Fakes.Recording.Recording"` across the solution returns zero results (the substring `Fakes.Recording.Recording` is the diagnostic — `AdapterCall` and `CallLog` legitimately live in `Adapters.Fakes.Recording.`).
- `dotnet build` succeeds.
- All 103 Phase 5 tests + Phase 6 tests continue to pass with no source change beyond the namespace move and `using` swaps.

### Task 3 — `TraceReader` in `QuestForge.Adapters.Fakes.Replay`

**File:** `QuestForge.Adapters.Fakes/Replay/TraceReader.cs`

**Purpose:** stream a JSONL trace file into a sequence of typed `TraceEvent` instances using the same source-generated `TraceEventJsonContext` the recorder uses.

**Sketch:**

```csharp
namespace QuestForge.Adapters.Fakes.Replay;

public sealed class TraceReader
{
    /// <summary>
    /// Reads a JSONL trace file. Each non-empty line is deserialized into a TraceEvent
    /// using TraceEventJsonContext. Lines that fail to parse throw — replay is
    /// intentionally strict on its inputs.
    /// </summary>
    public static IReadOnlyList<TraceEvent> ReadFile(string path)
    {
        var events = new List<TraceEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = JsonSerializer.Deserialize<TraceEvent>(line, TraceEventJsonContext.Default.TraceEvent)
                      ?? throw new InvalidDataException($"Trace line deserialized to null: {line}");
            events.Add(evt);
        }
        return events;
    }

    /// <summary>
    /// Convenience: read only the events of a specific subtype, in document order.
    /// </summary>
    public static IReadOnlyList<T> ReadFile<T>(string path) where T : TraceEvent
        => ReadFile(path).OfType<T>().ToList();
}
```

**Behaviors:**

- *Happy path:* given a Phase 6 trace with N lines, returns N `TraceEvent` instances in document order. Polymorphic discrimination via the `"type"` field is handled by `TraceEventJsonContext` (already configured with `[JsonPolymorphic]`).
- *Empty trailing line:* skipped (handles the `\n`-terminated file convention without producing a null event).
- *Malformed line:* throws `JsonException` or `InvalidDataException`. Replay must not silently swallow parse errors.
- *Phase 6 flat-payload events:* deserialize directly into `ObservationEvent`, `DecisionEvent`, `RunStartEvent`, `RunEndEvent` records as defined today. No `data` wrapper handling needed because the records' properties are top-level.

The reader is intentionally not async — fixtures are small (≤10 MB hard cap per `TRACE_FORMAT.md` §10) and reading happens once at test arrange time, not per-tick.

### Task 4 — `ReplayGameStateProvider` and `ReplayQuestState`

**Files:**

- `QuestForge.Adapters.Fakes/Replay/ReplayGameStateProvider.cs`
- `QuestForge.Adapters.Fakes/Replay/ReplayQuestState.cs`
- `QuestForge.Adapters.Fakes/Replay/ReplayJsonOptions.cs`

**Shared design:** both providers serve recorded observations in trace order. They share infrastructure (the cursor advancement logic, the argument comparison) via a small internal helper.

**Shared `JsonSerializerOptions` — `ReplayJsonOptions.Default`:**

Argument serialization on the replay side must produce byte-identical output to the recording proxy's serialization. Otherwise `JsonElement.GetRawText()` comparison will spuriously fail. The fix is one shared options instance used by both sides:

```csharp
namespace QuestForge.Adapters.Fakes.Replay;

internal static class ReplayJsonOptions
{
    /// <summary>
    /// Must match the recording proxy's serialization options exactly so that
    /// argument JSON produced at replay time is byte-equal to argument JSON
    /// captured at recording time. JsonElement.GetRawText() equality is the
    /// comparison key in ObservationScanner.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
```

The recording proxies (`RecordingGameStateProvider`, `RecordingQuestState`) must use this same `JsonSerializerOptions` instance (or one configured identically) when they serialize the `argument` payload onto `ObservationEvent`. Phase 7's Task 2 hoist should verify and align this — if the recording side currently constructs its own options inline, switch it to `ReplayJsonOptions.Default` (which can be moved to a shared location accessible from both `Adapters.Recording` and `Adapters.Fakes.Replay`, e.g., a small `QuestForge.Adapters.Tracing.TraceJsonOptions` static class that both sides reference).

**Sketch — the shared scanner:**

```csharp
namespace QuestForge.Adapters.Fakes.Replay;

internal sealed class ObservationScanner
{
    private readonly IReadOnlyList<ObservationEvent> _observations;
    private int _cursor;

    public ObservationScanner(IReadOnlyList<ObservationEvent> observations)
    {
        _observations = observations;
    }

    /// <summary>
    /// Finds the next observation matching (method, argument) starting from _cursor.
    /// Advances _cursor past it on success. Throws on starvation or order violation.
    /// </summary>
    public ObservationEvent Next(string method, object? argument)
    {
        var requestedArg = argument is null
            ? null
            : (JsonElement?)JsonSerializer.SerializeToElement(argument, ReplayJsonOptions.Default);

        for (var i = _cursor; i < _observations.Count; i++)
        {
            var obs = _observations[i];
            if (obs.Method != method) continue;
            if (!ArgumentEquals(obs.Argument, requestedArg)) continue;
            _cursor = i + 1;
            return obs;
        }

        throw new ReplayObservationStarvationException(
            $"No remaining observation for method '{method}' " +
            $"with argument {requestedArg?.GetRawText() ?? "null"} " +
            $"after cursor {_cursor}/{_observations.Count}.");
    }

    private static bool ArgumentEquals(JsonElement? a, JsonElement? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Value.GetRawText() == b.Value.GetRawText();
    }
}
```

**Design notes on the scanner:**

- **Linear forward scan**, not random access. This enforces order: an out-of-order request crosses observations of the wrong method and either matches one further along (silently consuming what would have been a later observation — undesirable) or fails at the end (loud failure — desirable). To get the loud failure cleanly, the scanner could optionally fail when `_observations[_cursor].Method != method` (strict mode). Phase 7 starts with the looser variant (scan to find the next match) because the recorder may interleave observations from different methods within a single engine tick, and the engine doesn't promise method-ordering between ticks. If practice shows the loose scan masks real regressions, tighten to strict.
- **Cursor advancement is monotonic.** Replayed observations are consumed in order; the cursor never rewinds.
- **Argument equality is text-based.** `JsonElement.GetRawText()` returns the original JSON substring; two equivalent JSON values produced by the same serializer should be identical strings. This matches the recording proxy's serialization path: same `JsonSerializerOptions` instance for both sides (the `ReplayJsonOptions.Default` defined above).

**Sketch — `ReplayGameStateProvider`:**

```csharp
namespace QuestForge.Adapters.Fakes.Replay;

public sealed class ReplayGameStateProvider : IGameStateProvider
{
    private readonly ObservationScanner _scanner;

    public ReplayGameStateProvider(IReadOnlyList<ObservationEvent> observations)
    {
        _scanner = new ObservationScanner(observations);
    }

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetPlayerZone), null);
        var zone = obs.Value!.Value.GetProperty("value").GetUInt32();
        return Task.FromResult<Result<ZoneId>>(new Result<ZoneId>.Success(new ZoneId(zone)));
    }

    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
    {
        var obs = _scanner.Next(nameof(GetPlayerPosition), null);
        var v = obs.Value!.Value;
        var pos = new WorldPosition(
            v.GetProperty("x").GetSingle(),
            v.GetProperty("y").GetSingle(),
            v.GetProperty("z").GetSingle());
        return Task.FromResult<Result<WorldPosition>>(new Result<WorldPosition>.Success(pos));
    }

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    { /* same shape — Next(nameof(GetQuestSequence), quest), deserialize obs.Value */ }

    // ... one method per IGameStateProvider member.

    // Failure path: if the recorded observation's Value is the failure shape
    // {failure: "...", detail: "..."}, materialize a Result<T>.Failure.
}
```

**Failure materialization:** the recording proxy serializes failures as `{"failure": "...", "detail": "..."}` (see `RecordingGameStateProvider.Unwrap` lines 60–66). The replay provider must invert this: if `obs.Value` has a `"failure"` property, return `Result.Fail<T>(reason, detail)`. Otherwise, deserialize the success payload into `T`.

A small helper in `ReplayGameStateProvider` handles this for every method:

```csharp
private static Result<T> Materialize<T>(JsonElement? value)
{
    if (value is null) throw new InvalidDataException("Observation value is null");
    var v = value.Value;
    if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("failure", out var f))
    {
        var detail = v.TryGetProperty("detail", out var d) ? d.GetString() : null;
        return Result.Fail<T>(f.GetString() ?? "unknown", detail);
    }
    var payload = JsonSerializer.Deserialize<T>(v, ReplayJsonOptions.Default);
    return new Result<T>.Success(payload!);
}
```

**`ReplayQuestState`:** identical shape, wrapping `IQuestState`. The 10 `IQuestState` methods get the same `Next(...)` + `Materialize<T>(...)` treatment.

### Task 5 — Exception types

**File:** `QuestForge.Adapters.Fakes/Replay/ReplayExceptions.cs`

```csharp
namespace QuestForge.Adapters.Fakes.Replay;

public sealed class ReplayObservationStarvationException : Exception
{
    public ReplayObservationStarvationException(string message) : base(message) { }
}

public sealed class ReplayDecisionMismatchException : Exception
{
    public ReplayDecisionMismatchException(string message) : base(message) { }
}
```

`ReplayDecisionMismatchException` is thrown by the test (not by the replay providers) when the engine emits a decision that doesn't match the recorded sequence. It carries a structured message naming the index, expected `(stepId, actionType)`, and actual `(stepId, actionType)`.

### Task 6 — The replay test

**File:** `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs`

**Fake adapter prerequisites — verified present:**

All the test-side fake adapters used by the replay test already exist in `QuestForge.Adapters.Fakes/` and are reused as-is. No new stub work is needed:

| Fake | File |
|---|---|
| `FakeCombat` | `QuestForge.Adapters.Fakes/Combat/FakeCombat.cs` |
| `FakeGearManager` | `QuestForge.Adapters.Fakes/Gear/FakeGearManager.cs` |
| `FakeDialogueResolver` | `QuestForge.Adapters.Fakes/Interaction/FakeDialogueResolver.cs` |
| `FakeMinigameSkipper` | `QuestForge.Adapters.Fakes/Minigames/FakeMinigameSkipper.cs` |
| `FakeTimingProfile` | `QuestForge.Adapters.Fakes/Timing/FakeTimingProfile.cs` |
| `FakeNavigator` | `QuestForge.Adapters.Fakes/Movement/FakeNavigator.cs` |
| `FakeTeleporter` | `QuestForge.Adapters.Fakes/Movement/FakeTeleporter.cs` |
| `FakeInteractor` | `QuestForge.Adapters.Fakes/Interaction/FakeInteractor.cs` |
| `FakeGameStateProvider` | `QuestForge.Adapters.Fakes/State/FakeGameStateProvider.cs` |
| `FakeQuestState` | `QuestForge.Adapters.Fakes/State/FakeQuestState.cs` |

(Verified via `Glob QuestForge.Adapters.Fakes/**/Fake*.cs` at plan-authoring time.) If any of these grow new abstract members in the future, this checklist requires re-verification.

**Sketch:**

```csharp
namespace QuestForge.Engine.Tests.Replay;

public sealed class Quest66130ReplayTests
{
    [Fact]
    public async Task Quest66130_CanonicalTracePhase6_EngineEmitsSameDecisionSequence()
    {
        // Arrange — locate the fixture
        var tracePath = FixtureLocator.LocateOrSkip(
            "quests/arr/msq/66130-canonical-trace-phase6.jsonl");

        // Load the trace
        var events = TraceReader.ReadFile(tracePath);
        var runStart = events.OfType<RunStartEvent>().Single();
        var observations = events.OfType<ObservationEvent>().ToList();
        var recordedDecisions = events.OfType<DecisionEvent>().ToList();
        var runEnd = events.OfType<RunEndEvent>().SingleOrDefault();

        Assert.Equal(66130u, runStart.QuestId);
        Assert.NotEmpty(observations);
        Assert.NotEmpty(recordedDecisions);

        // Split observations into GameState vs QuestState by method name.
        // The 22-method IGameStateProvider and 10-method IQuestState don't overlap;
        // a static set of method names per interface is the discriminator.
        var (gameStateObs, questStateObs) = SplitByInterface(observations);

        var replayGameState = new ReplayGameStateProvider(gameStateObs);
        var replayQuestState = new ReplayQuestState(questStateObs);

        // Non-recording fakes for the rest. These adapters' methods are not asserted in
        // Phase 7; they accept whatever the engine sends and return defaults.
        var navigator = new FakeNavigator(new FakeGameStateProvider()); // dummy backing state
        var teleporter = new FakeTeleporter(new FakeGameStateProvider());
        var interactor = new FakeInteractor(new FakeGameStateProvider(), new FakeQuestState());
        var combat = new FakeCombat();
        var gear = new FakeGearManager();
        var minigame = new FakeMinigameSkipper();
        var dialogue = new FakeDialogueResolver();
        var timing = new FakeTimingProfile();

        // Capturing trace writer — buffers DecisionEvents in-memory so the test can read
        // (stepId, actionType) directly off each tick instead of inferring it from the
        // engine's returned EngineAction (which doesn't expose stepId).
        var capturingTrace = new CapturingTraceWriter();

        var engine = new QuestEngine(
            replayGameState, replayQuestState,
            navigator, teleporter, interactor, combat, gear, minigame, dialogue,
            timing, capturingTrace, NullLogger<QuestEngine>.Instance);

        // Load the quest. Reuse the Phase 4 fixture loading convention — the quest
        // definition for 66130 already exists at Fixtures/66130.json (copied to the
        // test bin folder via the csproj's <None Update="Fixtures\66130.json"> entry).
        var quest = LoadQuest66130();
        engine.StartQuest(quest);
        engine.BeginRun(runStart.RunId);

        // Act — drive the engine for as many ticks as there are recorded decisions.
        // After each tick, read the latest DecisionEvent off the capturing trace writer
        // (the engine wrote it during Tick) to get (stepId, actionType).
        var actualDecisions = new List<(string? StepId, string ActionType)>();
        var ct = CancellationToken.None;

        for (var i = 0; i < recordedDecisions.Count; i++)
        {
            var capturedBefore = capturingTrace.Events.OfType<DecisionEvent>().Count();
            var action = await engine.Tick(ct);
            var capturedAfter = capturingTrace.Events.OfType<DecisionEvent>().ToList();

            // The engine emits exactly one decision per Tick that returns a non-Done action.
            // If no new DecisionEvent was emitted, the engine reached a terminal state early
            // (Done) — break and let the post-loop assertion handle it.
            if (capturedAfter.Count == capturedBefore)
                break;

            var latest = capturedAfter[^1];
            actualDecisions.Add((latest.StepId, latest.ActionType));
        }

        // Assert — every recorded decision matches an actual decision, in order
        Assert.Equal(recordedDecisions.Count, actualDecisions.Count);
        for (var i = 0; i < recordedDecisions.Count; i++)
        {
            var expected = (recordedDecisions[i].StepId, recordedDecisions[i].ActionType);
            var actual = actualDecisions[i];
            if (expected != actual)
                throw new ReplayDecisionMismatchException(
                    $"Decision divergence at index {i}: " +
                    $"expected ({expected.StepId ?? "<null>"}, {expected.ActionType}), " +
                    $"got ({actual.StepId ?? "<null>"}, {actual.ActionType}). " +
                    $"Trace runId={runStart.RunId}, questId={runStart.QuestId}.");
        }

        // Terminal state — drive one final tick. QuestEngine.Tick emits a run.end event
        // (not a decision event) when it returns EngineAction.Done; the recorded trace
        // has the corresponding run.end with outcome "done" or "awaitUser".
        var terminalAction = await engine.Tick(ct);

        if (runEnd is not null)
        {
            switch (runEnd.Outcome)
            {
                case "done":
                    Assert.IsType<EngineAction.Done>(terminalAction);
                    break;
                case "awaitUser":
                    Assert.IsType<EngineAction.AwaitUser>(terminalAction);
                    break;
                default:
                    Assert.Fail($"Unhandled recorded run.end.outcome: '{runEnd.Outcome}'.");
                    break;
            }
        }
        else
        {
            // No recorded run.end — accept either terminal action; the harness can't
            // pick. (In practice every committed canonical trace ends with run.end.)
            Assert.True(terminalAction is EngineAction.Done or EngineAction.AwaitUser,
                $"Engine returned non-terminal action {terminalAction.GetType().Name} " +
                $"after consuming all {recordedDecisions.Count} recorded decisions.");
        }
    }

    private static QuestDefinition LoadQuest66130()
    {
        // Same convention as Phase 4 tests (AwaitUserTests, BeginRunTests, etc.):
        // the fixture is copied next to the test assembly via the csproj's
        // <None Update="Fixtures\66130.json"> CopyToOutputDirectory entry.
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "66130.json"));
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions)
               ?? throw new InvalidOperationException("Failed to deserialize fixture 66130.json");
    }

    private static (List<ObservationEvent> gameState, List<ObservationEvent> questState)
        SplitByInterface(IReadOnlyList<ObservationEvent> observations)
    {
        var questStateMethods = new HashSet<string>
        {
            nameof(IQuestState.GetQuestStatus),
            nameof(IQuestState.GetQuestSequence),
            nameof(IQuestState.GetQuestFlags),
            nameof(IQuestState.IsQuestFlagSet),
            nameof(IQuestState.IsQuestAccepted),
            nameof(IQuestState.IsQuestComplete),
            nameof(IQuestState.IsQuestAvailable),
            nameof(IQuestState.WhyUnavailable),
            nameof(IQuestState.GetAcceptedQuests),
            nameof(IQuestState.GetAvailableQuestRewards),
        };
        var gs = new List<ObservationEvent>();
        var qs = new List<ObservationEvent>();
        foreach (var obs in observations)
        {
            if (questStateMethods.Contains(obs.Method)) qs.Add(obs);
            else gs.Add(obs);
        }
        return (gs, qs);
    }

    private sealed class CapturingTraceWriter : ITraceWriter
    {
        public List<TraceEvent> Events { get; } = new();
        public void Write(TraceEvent evt) => Events.Add(evt);
    }
}
```

**Why `CapturingTraceWriter` is required:** `QuestEngine.Tick` returns an `EngineAction` value but does not expose the `stepId` alongside it — the step ID is only written into the `DecisionEvent` the engine emits to its `ITraceWriter`. To assert on `(stepId, actionType)` pairs, the test plugs in a `CapturingTraceWriter` that buffers events in-memory, then reads the most recent `DecisionEvent` off it after each tick.

**Why the post-loop "one final tick":** `QuestEngine.Tick` emits *only* a `run.end` event (not a `decision` event) when it returns `EngineAction.Done` (`QuestEngine.cs` line 96). The trace's `decision` events therefore end at the last `Wait`/`Interact`/`Navigate` before completion. After driving as many ticks as there are recorded decisions, one extra tick fires the terminal action, which is asserted against `runEnd.Outcome`.

### Task 7 — CI wiring

**File:** `.github/workflows/test.yml` (or wherever the project's CI lives — Phase 1 set up the validator's CI; Phase 7 adds the replay test alongside).

**Additions:**

```yaml
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          path: questforge

      - uses: actions/checkout@v4
        with:
          repository: <github-org>/questforge-data
          path: questforge-data
          lfs: true   # trace files are LFS-tracked

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore + build
        working-directory: questforge
        run: dotnet build --configuration Release

      - name: Run tests (includes replay)
        working-directory: questforge
        run: dotnet test --configuration Release --no-build --logger "trx;LogFileName=test-results.trx"

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: questforge/**/*.trx
```

**Notes:**

- `lfs: true` is essential — without it, the LFS-tracked trace files are present as small pointer-content stubs rather than real JSONL, and the replay test fails on parse.
- The `<github-org>` placeholder is set when `questforge-data` is created on GitHub. Until then, the test skips locally (per the `FixtureLocator` fallback).
- The CI does *not* require Dalamud or a running game — `dotnet test` against the engine + fakes + replay providers is pure managed code.
- The plugin DLL build (`QuestForge.Plugin`) requires Dalamud SDK; that build step may need a separate runner with the SDK installed or simply be skipped in CI (only the engine and adapters are exercised by tests). The existing Phase 6 build configuration covers this — Phase 7 does not modify it.

### Task 8 — Given-When-Then specifications

#### 8.1 `TraceReader.ReadFile`

**Happy path:**
Given a JSONL file with 7 events (1 run.start, 4 observation, 1 decision, 1 run.end), each on its own line, terminated by `\n`.
When `TraceReader.ReadFile(path)` is called.
Then it returns a `List<TraceEvent>` of length 7 with subtypes matching the discriminator (`RunStartEvent`, four `ObservationEvent`, `DecisionEvent`, `RunEndEvent`) in document order.

**Edge — empty file:**
Given an empty file (0 bytes).
When `ReadFile` is called.
Then it returns an empty list. No exception.

**Edge — trailing empty line:**
Given a valid trace ending with `\n\n` (one extra newline).
When `ReadFile` is called.
Then the trailing empty line is skipped silently. List length matches the event count.

**Error — malformed JSON line:**
Given a file where line 3 is `{not valid json`.
When `ReadFile` is called.
Then `JsonException` is thrown. The exception's message references the malformed content (caller decides whether to enrich with the file path).

**Error — unknown discriminator value:**
Given a line `{"type":"unknown.event","at":"2026-05-15T..."}`.
When `ReadFile` is called.
Then `JsonException` from `TraceEventJsonContext` (System.Text.Json polymorphic deserialization rejects unknown discriminators by default).

#### 8.2 `ObservationScanner.Next`

**Happy path — first observation matches:**
Given a scanner over `[obs(GetPlayerZone, null), obs(GetQuestSequence, {value:66130})]`, cursor at 0.
When `Next("GetPlayerZone", null)` is called.
Then it returns the first observation and advances cursor to 1.

**Happy path — second observation matches (skip-forward):**
Given the same scanner.
When `Next("GetQuestSequence", new QuestId(66130))` is called (without first calling for GetPlayerZone).
Then the scanner scans forward, returns the second observation, advances cursor to 2. The first observation is silently consumed (no longer reachable). *Phase 7 acceptable behavior; revisit if it masks regressions.*

**Edge — argument mismatch fails to match:**
Given a scanner with `[obs(GetQuestSequence, {value:66130})]`, cursor 0.
When `Next("GetQuestSequence", new QuestId(66104))` is called.
Then no match is found; `ReplayObservationStarvationException` is thrown with a message naming the method and the argument.

**Error — cursor exhausted:**
Given a scanner with no remaining observations matching the request.
When `Next("GetPlayerZone", null)` is called.
Then `ReplayObservationStarvationException` is thrown.

#### 8.3 `ReplayGameStateProvider.GetPlayerZone`

**Happy path:**
Given a replay provider over `[ObservationEvent(method:"GetPlayerZone", value:{"value":182})]`.
When `GetPlayerZone(ct)` is awaited.
Then it returns `Result<ZoneId>.Success(new ZoneId(182))`. The observation cursor advances past entry 0.

**Failure materialization:**
Given an observation with `value: {"failure":"noLocalPlayer","detail":"ObjectTable[0] null"}`.
When `GetPlayerZone(ct)` is awaited.
Then it returns `Result<ZoneId>.Failure(Reason: "noLocalPlayer", Detail: "ObjectTable[0] null")`.

**Starvation:**
Given a replay provider whose observation list contains no `GetPlayerZone` entries.
When `GetPlayerZone(ct)` is awaited.
Then `ReplayObservationStarvationException` propagates (not caught by the provider).

#### 8.4 `ReplayQuestState.GetQuestSequence`

**Happy path with argument:**
Given a replay provider over `[ObservationEvent(method:"GetQuestSequence", argument:{"value":66130}, value:0)]`.
When `GetQuestSequence(new QuestId(66130), ct)` is awaited.
Then it returns `Result<int>.Success(0)`. The argument JSON matches; the integer value 0 is deserialized from the observation.

**Argument mismatch (different QuestId):**
Given the same provider.
When `GetQuestSequence(new QuestId(66104), ct)` is awaited.
Then `ReplayObservationStarvationException` is thrown because the only observation has a different argument.

#### 8.5 `FixtureLocator.TryFindQuestForgeDataFixture`

**Happy path — sibling directory exists:**
Given a directory layout where `<some-ancestor>/questforge-data/quests/arr/msq/66130-canonical-trace-phase6.jsonl` exists.
When `TryFindQuestForgeDataFixture("quests/arr/msq/66130-canonical-trace-phase6.jsonl")` is called.
Then the absolute path to that file is returned.

**Edge — file does not exist:**
Given `questforge-data` is not present at any ancestor.
When `TryFindQuestForgeDataFixture(...)` is called.
Then `null` is returned (no exception).

**Edge — `questforge-data` exists but the requested file doesn't:**
Given a `questforge-data` directory exists but the file is missing.
When `TryFindQuestForgeDataFixture(...)` is called.
Then `null` is returned (the file check fails; the walk continues, finds nothing, returns null).

**`LocateOrSkip` on missing fixture:**
Given `TryFindQuestForgeDataFixture(...)` would return `null`.
When `LocateOrSkip(...)` is called.
Then `Assert.Skip(...)` is invoked (xUnit v3 API, available via the `xunit.v3` package referenced in `QuestForge.Engine.Tests.csproj`). The test is recorded as skipped, not failed, with the diagnostic message naming the expected sibling layout.

#### 8.6 End-to-end replay (the done criterion)

**Happy path — quest 66130 replays cleanly:**

Given:
- A `66130-canonical-trace-phase6.jsonl` fixture is present in `questforge-data/quests/arr/msq/`.
- The current engine code is unmodified from Phase 6.

When: `Quest66130_CanonicalTracePhase6_EngineEmitsSameDecisionSequence` is executed.

Then:
- The fixture is located by `FixtureLocator`.
- The trace is parsed via `TraceReader` into ~12,634 events for the canonical run — roughly 1 `run.start`, ~10,000 `observation`s, ~2,500 `decision`s, and 1 `run.end`.
- The engine, driven by replay providers + non-recording fakes + capturing trace writer, ticks ~2,500 times (once per recorded decision) and emits the same `(stepId, actionType)` pair at each index.
- After the main loop, one final `await engine.Tick(ct)` returns `EngineAction.Done`, matching `runEnd.Outcome == "done"`. (No `DecisionEvent` is recorded for this terminal tick — `QuestEngine.Tick` writes only a `run.end` for `Done`.)
- The test passes.

**Regression detection (failure path used by the test author to validate the harness):**

Given:
- The engine's `ResolveAction` is deliberately broken — e.g., always returns `Wait` regardless of state.

When the test runs.

Then `ReplayDecisionMismatchException` is thrown at the first divergence (index 0, where the trace expected `Navigate` and the engine emitted `Wait`). The exception message names index, expected, actual, and quest ID.

**Regression detection — terminal action wrong:**

Given:
- The engine reaches the end of the recorded decision sequence but returns `EngineAction.Wait` on the post-loop final tick (instead of `Done`).

When the test runs.

Then the post-loop `Assert.IsType<EngineAction.Done>(terminalAction)` fails with a clear xUnit message naming the actual returned type.

---

## Done criteria

Phase 7 is complete when **all** the following hold:

1. **Hoist:** `RecordingGameStateProvider` and `RecordingQuestState` live in `QuestForge.Adapters/Recording/` with namespace `QuestForge.Adapters.Recording`. All call sites updated. `grep -r "QuestForge.Adapters.Fakes.Recording.Recording"` returns zero matches.
2. **Build:** `dotnet build` succeeds across the solution.
3. **Existing tests green:** all Phase 5 + Phase 6 tests continue to pass.
4. **`TraceReader` tests green:** the four happy-path + edge-case tests for `TraceReader` from §8.1 pass.
5. **`ReplayGameStateProvider`/`ReplayQuestState` tests green:** the happy-path + failure-materialization + starvation tests for both providers pass.
6. **Replay test green locally:** with the canonical fixture committed in `questforge-data`, `Quest66130_CanonicalTracePhase6_EngineEmitsSameDecisionSequence` passes when run from a developer machine with `questforge-data` checked out as a sibling. The ~2,500-decision sequence completes in under 5 seconds.
7. **Replay test green in CI:** GitHub Actions workflow (with `questforge-data` checkout step including `lfs: true`) runs the replay test and it passes.
8. **Regression detection demonstrated:** a temporary engine break (revert `QuestEngine.ResolveAction` to a known-wrong shape) produces a CI failure that names quest 66130, the divergence index, and the expected/actual `(stepId, actionType)` pair. Verify by branch + revert; do not merge the break.

---

## Implementation order

**Phase A — Hoist (½ day)**

1. Move `RecordingGameStateProvider.cs` and `RecordingQuestState.cs` from `QuestForge.Adapters.Fakes/Recording/` to `QuestForge.Adapters/Recording/`.
2. Update the namespace inside both files.
3. Update `using` statements in `EngineHost.cs` and the four affected test files.
4. `dotnet build && dotnet test` — verify nothing broke.
5. Commit. (Small, mechanical, easy to review.)

**Phase B — `TraceReader` + tests (½ day)**

1. Add `QuestForge.Adapters.Fakes/Replay/TraceReader.cs`.
2. Add `QuestForge.Engine.Tests/Replay/TraceReaderTests.cs` with the five §8.1 scenarios.
3. `dotnet test --filter "FullyQualifiedName~Replay.TraceReader"`.
4. Commit.

**Phase C — `ObservationScanner` + replay providers + their tests (1-2 days)**

1. Add `QuestForge.Adapters.Fakes/Replay/ReplayJsonOptions.cs` (and align the recording proxy to use the same options instance, if not already aligned).
2. Add `QuestForge.Adapters.Fakes/Replay/ObservationScanner.cs`.
3. Add `ReplayExceptions.cs`.
4. Add `ReplayGameStateProvider.cs` and `ReplayQuestState.cs`, implementing all 22 + 10 interface methods using the `Next` + `Materialize<T>` pattern.
5. Add unit tests for the scanner (§8.2), `ReplayGameStateProvider.GetPlayerZone` (§8.3), and `ReplayQuestState.GetQuestSequence` (§8.4). At least one happy-path + one starvation per interface method is overkill; the spotcheck targets above plus one parameterized test cycling through all 22+10 methods is sufficient.
6. Commit.

**Phase D — `FixtureLocator` (½ day)**

1. Add `QuestForge.Engine.Tests/Replay/FixtureLocator.cs`.
2. Add tests per §8.5 (using temp directories with crafted layouts).
3. Commit.

**Phase E — End-to-end replay test (1 day)**

1. User performs Task 1 (manual trace copy into `questforge-data`).
2. Add `QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs` (with the `CapturingTraceWriter` inner class).
3. Run locally; iterate until green.
4. Commit.

**Phase F — CI wiring (½ day)**

1. Add the `questforge-data` checkout step to `.github/workflows/test.yml`.
2. Push branch; observe CI green.
3. Demonstrate regression detection by branching, breaking the engine, observing CI red with the named divergence, reverting. (Do not merge the break.)
4. Commit the CI changes only.

**Phase G — PR (½ day)**

PR description includes:
- A link to the new fixture in `questforge-data`.
- A screenshot or log excerpt of the test passing.
- A screenshot or log excerpt of the deliberate regression test (proving the harness catches what it's supposed to catch).
- A note that all 103 Phase 5 + Phase 6 tests still pass.

**Total estimate: 4-6 days of focused weekend time.** Within the 2-3 week budget of `NEXT_STEPS.md` §Phase 7, with slack for the inevitable rabbit holes around argument serialization edge cases and CI LFS configuration.

---

## What Phase 7 does NOT include

- **Format reconciliation.** No work to add `seq`, `ts`, `v`, or `data` wrapping to the recorder. The Phase 6 trace shape is read as-is. Spec reconciliation is a separate Phase 7+ task with its own PR.
- **`action.submitted` / `action.completed` in the production trace.** Not emitted by `EngineHost.DispatchAction` today; not required for replay. Phase 8 or later.
- **Engine config serialization in `run.start`.** Replay uses default engine instantiation; no `engineConfig` round-trip yet. Acceptable for v1 because the engine has no config-affecting decision paths beyond what's hardcoded.
- **A second canonical quest.** Quest 66130 only. Phase 10+ adds quests + traces incrementally; each addition is one fixture and one line in the test theory data.
- **Strict observation ordering.** The scanner uses loose scan-forward matching; tight `_observations[_cursor].Method != method` enforcement is deferred.
- **Action-parameter assertions.** Only `(stepId, actionType)` is compared. Asserting on the actual `WorldPosition` of `Navigate` etc. requires extending `DecisionEvent` and is left for Phase 7+.
- **The `qf-trace replay` CLI tool.** `questforge-tools` will eventually have this, sharing the replay provider code. Phase 7 ships only the xUnit harness; the CLI is later.
- **Trace validation (`qf-trace validate`).** Per `TRACE_FORMAT.md` §9.3. Separate tool, separate phase.
- **Trace redaction (`qf-trace redact`).** Per `TRACE_FORMAT.md` §7. Separate tool, separate phase.
- **Replay against the Dalamud-backed adapters.** That's E2E, not replay. Out of scope forever (different test tier).

---

## Appendix A — Why not bump the trace format `v` first?

The temptation in Phase 7 is to "do replay properly" — add `seq`, `ts`, `v`, `data`-wrap the payload, emit `action.submitted`/`action.completed` from `EngineHost.DispatchAction`, serialize `engineConfig`, then build replay against the upgraded format. This is rejected.

Reasons:

1. **Cost.** Every event type changes shape. `TraceWriter` tests change. `TraceReader` would need version awareness. Recording-proxy tests change. Phase 6's golden trace at `pluginConfigs\QuestForge\traces\` becomes unreadable without a migration path.
2. **Risk.** Replay is the load-bearing capability we're trying to validate. Coupling it to a format change doubles the failure surface — when a test fails, the cause could be replay logic or format logic.
3. **Sequencing.** The reasons to upgrade the format (better debuggability, spec compliance, multi-version tooling) are real but not blocked by replay. They can land later as their own PR, after Phase 7 has proven replay works.
4. **YAGNI.** None of the deferred fields are *used* by minimum-viable replay. Adding them now is speculative work.

Phase 7+ tasks: emit `seq` and `ts`, wrap in `data`, add `v`, emit action lifecycle pairs, serialize `engineConfig`. Each is one or two PRs against `QuestForge.Adapters.Tracing` types. They become more attractive once trace size matters (rotation, redaction tooling) or replay determinism leans on `engineConfig` matching.

---

## Appendix B — Risks and mitigations

| Risk | Mitigation |
|---|---|
| `questforge-data` doesn't exist yet on GitHub when Phase 7 lands | Local skip via `FixtureLocator` keeps developer workflow unblocked. CI wiring is a no-op until the repo exists; the workflow can be PR'd before the data repo and tagged "pending data repo creation." |
| Argument JSON round-trip is not byte-stable across .NET versions | Use a single `JsonSerializerOptions` instance (`ReplayJsonOptions.Default`, defined in Task 4), configured to camelCase + no indenting, in both the recording proxy and the replay scanner. Same instance, same bytes, byte equality holds. |
| Future engine changes cause spurious replay failures | Documented in decision 7: re-record canonical traces when engine behavior legitimately changes. The failure mode is loud and informative. |
| LFS bandwidth/storage limits on free GitHub plans | Trace files are ~1 MB each (`TRACE_FORMAT.md` §10 soft target). One canonical per quest × ~50 quests = ~50 MB lifetime. Well under the free LFS quota. Revisit if corpus grows past ~500 traces. |
| Replay test runtime grows linearly with corpus size | Each quest's test is under 5 s on CI hardware (~2,500 ticks × all-synchronous fakes). 50 quests = ~4 minutes of CI test time. Acceptable. If runtime becomes a problem, parallelize via `[Theory]` with one quest per inline data row and the xUnit parallel runner. |
| The Phase 6 trace's per-tick decision count makes the test slow | Synchronous fakes + in-memory operations keep wall-clock low even with ~2,500 ticks. Estimated: under 5 seconds on a 2024-era laptop. If this regresses, the loose scanner can be tightened to skip duplicate consecutive decisions (collapsing runs on both sides) — but only if needed. |
| Hoisting recording proxies breaks Phase 6 plugin DLL | The hoist is a pure namespace move; Phase 6 plugin recompiles cleanly after the `using` swap. Verified by `dotnet build` in Phase A done criteria. |