# Phase 10 Plan — `qf-trace` Trace Extractor CLI

**Status:** specification — ready for test creation
**Related:** `docs/NEXT_STEPS.md` §Phase 10, `docs/FIXTURES.md`, `docs/TRACE_FORMAT.md`, `docs/AUTHORING.md`
**Target repo:** `questforge-tools` (alongside `qf-validate`)
**Target language:** C# / .NET 10

---

## 1. Scope and goals

Phase 10 delivers four `qf-trace` subcommands that close the authoring/regression loop:

1. **`extract-fixture`** — turn a `.jsonl` trace into a fixture JSON draft
2. **`validate-fixture`** — cross-check a committed fixture against its referenced quest file
3. **`list-fixtures`** — enumerate fixtures and show capability coverage / gaps
4. **`extract-quest`** — turn a `.jsonl` trace into a `QuestDefinition` draft

The CLI is a thin shell over four pure, fully testable library classes in `QuestForge.Tools.Trace`.

### 1.1 Out of scope for Phase 10

- Trace **redaction** (`qf-trace redact`) — already specified in `TRACE_FORMAT.md` §7; deferred
- Trace **replay** (`qf-trace replay`) — requires reworked replay harness; deferred
- Trace **validation** (`qf-trace validate`) — `seq` monotonic / engine-seed integrity (`TRACE_FORMAT.md` §9.3); deferred
- **NPC name / zone name** resolution via Lumina — comes from the runtime plugin, not the CLI tool
- **`Requirements`** inference (level, job, prereqs) — not present in the trace; remains a manual edit
- Schema migration (`qf-trace migrate`) — deferred until a trace `v` bump is required
- Authoring-mode draft import / export — handled in Phase 9 by `DraftManager`
- **Phase 5–6 trace shape compatibility.** Phase 10 reads the trace shape produced by the Phase 7+ recorder only: top-level `type` discriminator, top-level `runId`, flat payload fields per `TraceEventJsonContext`. Phase 5–6 divergences listed in `TRACE_FORMAT.md` §"Known divergences" are not back-supported.

---

## 2. Project layout

Three new projects in `questforge-tools`, plus solution updates and cross-repo references.

```
questforge-tools/
  questforge-tools.slnx                          (updated — add 3 projects)
  qf-validate/                                   (existing)
  QuestForge.Tools.Validator/                    (existing)
  QuestForge.Tools.Validator.Tests/              (existing)
  QuestForge.Predicates/                         (existing)
  QuestForge.Predicates.Tests/                   (existing)
  QuestForge.Schema/                             (existing — local copy of canonical schema)
  qf-trace/                                      (NEW — CLI entry point)
    qf-trace.csproj
    Program.cs
  QuestForge.Tools.Trace/                        (NEW — library)
    QuestForge.Tools.Trace.csproj
    Parsing/
      TraceEventParser.cs
    SnapshotState.cs
    Fixture/
      FixtureModel.cs                            (POCO matching FIXTURES.md §format)
      TraceToFixtureExtractor.cs
      FixtureValidator.cs
      FixtureValidationResult.cs
      FixtureListEntry.cs
      ListFixturesCommand.cs
    Quest/
      TraceToQuestExtractor.cs
      QuestDraftResult.cs
    Capabilities/
      CapabilityInferrer.cs
  QuestForge.Tools.Trace.Tests/                  (NEW — xUnit)
    QuestForge.Tools.Trace.Tests.csproj
    Fixtures/                                    (test data — JSONL traces + quest files)
    SnapshotStateTests.cs
    TraceEventParserTests.cs
    TraceToFixtureExtractorTests.cs
    FixtureValidatorTests.cs
    TraceToQuestExtractorTests.cs
    CapabilityInferrerTests.cs
```

### 2.1 Project references

**`QuestForge.Tools.Trace.csproj`** (relative to `questforge-tools/`):

```xml
<ProjectReference Include="..\..\questforge\QuestForge.Adapters\QuestForge.Adapters.csproj" />
<ProjectReference Include="..\..\questforge\QuestForge.Engine\QuestForge.Engine.csproj" />
```

> **WHY no local `QuestForge.Schema` reference here:** `QuestForge.Engine` already references the main-repo `QuestForge.Schema`. Adding the local `questforge-tools` copy as a second reference would load two distinct Schema assemblies, causing `InvalidCastException` when passing `QuestDefinition` between them at runtime. `QuestForge.Tools.Trace` gets Schema types transitively through `QuestForge.Engine`. The local `QuestForge.Schema` copy in `questforge-tools` is kept for `qf-validate` and `QuestForge.Tools.Validator` only (those tools do not reference Engine).

**`qf-trace.csproj`**:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <RootNamespace>qf_trace</RootNamespace>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\QuestForge.Tools.Trace\QuestForge.Tools.Trace.csproj" />
</ItemGroup>
```

**`QuestForge.Tools.Trace.Tests.csproj`**:

```xml
<ProjectReference Include="..\QuestForge.Tools.Trace\QuestForge.Tools.Trace.csproj" />
<ProjectReference Include="..\..\questforge\QuestForge.Adapters.Fakes\QuestForge.Adapters.Fakes.csproj" />
```

### 2.2 Cross-repo reference assumptions

Both `questforge` and `questforge-tools` are checked out in the same parent directory (typical solo-dev layout in `C:\Users\publi\RiderProjects\`). The `..\..\questforge\...` relative paths assume this. CI builds in `questforge-tools` will:

1. `actions/checkout@v4` `simplekantan/questforge` to `../questforge`
2. `actions/checkout@v4` `simplekantan/questforge-tools` to `./` (default)
3. `dotnet build questforge-tools.slnx`

The validator already references the schema cross-repo via the local `QuestForge.Schema` copy. `QuestForge.Engine` and `QuestForge.Adapters` are pure C# (engine-must-not-reference-Dalamud invariant from `CLAUDE.md`), so cross-repo reference is safe.

### 2.3 Solution file update

`questforge-tools.slnx` adds three lines:

```xml
<Project Path="qf-trace/qf-trace.csproj" />
<Project Path="QuestForge.Tools.Trace/QuestForge.Tools.Trace.csproj" />
<Project Path="QuestForge.Tools.Trace.Tests/QuestForge.Tools.Trace.Tests.csproj" />
```

---

## 3. Trace event shape (Phase 7+) — recap

The Phase 7+ trace format produced by `RecordingGameStateProvider` / `RecordingQuestState` (and emitted by the test harness for `action.*` events) is a JSONL stream of polymorphic `TraceEvent` records. Each line is a top-level JSON object with a `"type"` discriminator. Concrete shapes (see `QuestForge.Adapters.Tracing.*`):

| Type discriminator | Concrete record | Required fields beyond `type`, `at` |
|---|---|---|
| `run.start` | `RunStartEvent` | `runId`, `questId` (uint), `questSchemaId` (uint) |
| `run.end` | `RunEndEvent` | `runId`, `outcome` (string — e.g. `"done"`, `"awaitUser"`, `"failure"`, `"stopped"`) |
| `decision` | `DecisionEvent` | `runId`, `stepId` (string, nullable), `actionType` (string — `"navigate"`, `"interact"`, `"wait"`, `"awaitUser"`, `"done"`) |
| `action.submitted` | `ActionSubmittedEvent` | `runId`, `actionType` (string, **PascalCase** in current emit: `"Navigate"` / `"Interact"`), `parameters` (`JsonElement?`) |
| `action.completed` | `ActionCompletedEvent` | `runId`, `actionType`, `outcome` (string) |
| `observation` | `ObservationEvent` | `runId` (nullable), `method` (string — PascalCase C# method name, e.g. `"GetPlayerZone"`), `argument` (`JsonElement?`), `value` (`JsonElement?`) |

**Casing inconsistency note for testers:** `DecisionEvent.ActionType` is lowercase (`"navigate"`); `ActionSubmittedEvent.ActionType` as emitted by the test harness is PascalCase (`"Navigate"`). Both forms appear in real traces. `TraceToQuestExtractor` must compare action-type strings **case-insensitively**.

### 3.1 Observation `Value` shapes (the values `RecordingGameStateProvider` / `RecordingQuestState` serialize)

Observation values are `Result<T>` success/failure payloads. Primitive `T` values (`int`, `uint`, `bool`) serialize as a raw JSON literal. Struct `T` values (`ZoneId`, `WorldPosition`) serialize as a JSON object with camel-cased properties. Failures serialize as `{"failure": "<Reason>", "detail": "<Detail>"}`. The `SnapshotState` accumulator must skip failures silently (detect by presence of `"failure"` key at value root).

| Method (string) | Argument shape | Success Value shape |
|---|---|---|
| `GetPlayerZone` | null | `{ "value": <uint> }` (ZoneId) |
| `GetPlayerPosition` | null | `{ "x": <float>, "y": <float>, "z": <float> }` (WorldPosition) |
| `GetQuestSequence` | `{ "value": <uint> }` (QuestId) | `<int>` (raw number) |
| `GetQuestFlags` | `{ "value": <uint> }` (QuestId) | `<uint>` (raw number) |
| `IsQuestAccepted` | `{ "value": <uint> }` (QuestId) | `<bool>` |
| `IsQuestComplete` | `{ "value": <uint> }` (QuestId) | `<bool>` |

Other observation methods (e.g. `GetPlayerState`, `GetNearbyNpcs`) are recorded but are not consumed by `SnapshotState` in Phase 10.

### 3.2 `ActionSubmittedEvent.Parameters` shape — Phase 10 contract

The spec (per Phase 10 prompt) defines what `extract-quest` *consumes*:

**Navigate**:
```json
{ "destination": {"x": 44.7, "y": 4.0, "z": -148.7}, "zone": 182, "options": { "stoppingDistance": 3.0, "useFlight": true } }
```

**Interact**:
```json
{ "target": 1014875 }
```

**Done / Wait / AwaitUser**: `parameters` is `null` or `{}`.

`TraceToQuestExtractor` defensively also accepts the test-harness shape (`{"x":...,"y":...,"z":...}` flat for Navigate, `{"value": <npcId>}` for Interact) and prefers the spec shape when both keys are present. The deserializer probes for `destination` first, then falls back to top-level `x`/`y`/`z`. For Interact it probes `target` then `value`.

---

## 4. `SnapshotState` — accumulating game-state class

**Purpose:** replay a trace's `observation` events into a running `GameStateSnapshot`, so `StepInferenceEngine.Infer(before, after)` can be invoked at each action boundary.

**Single responsibility:** consume `ObservationEvent`s in `seq` (file) order, mutate fields, and produce immutable `GameStateSnapshot` snapshots on demand.

### 4.1 Interface

```csharp
namespace QuestForge.Tools.Trace;

public sealed class SnapshotState
{
    // Run-scoped quest filter — only observations whose argument quest ID matches
    // are applied to QuestSequence/QuestFlags/QuestAccepted/QuestCompleted.
    private readonly QuestId _activeQuest;

    public SnapshotState(QuestId activeQuest);

    // Mutable accumulated state — defaults: zero / false / null / origin.
    public ZoneId Zone { get; private set; }                        // default: ZoneId(0)
    public WorldPosition Position { get; private set; }             // default: (0,0,0)
    public int QuestSequence { get; private set; }                  // default: 0
    public uint QuestFlags { get; private set; }                    // default: 0
    public bool QuestAccepted { get; private set; }                 // default: false
    public bool QuestCompleted { get; private set; }                // default: false
    public NpcId? LastNpcInteracted { get; private set; }           // default: null
    public WorldPosition? LastNpcPosition { get; private set; }     // default: null

    /// <summary>Apply one observation. Returns false if the method is unrecognised.</summary>
    public bool Apply(ObservationEvent ev);

    /// <summary>Capture an immutable snapshot at the given timestamp.</summary>
    public GameStateSnapshot ToSnapshot(DateTimeOffset at);

    /// <summary>Convenience overload used by TraceToQuestExtractor.</summary>
    public GameStateSnapshot ToSnapshot(DateTimeOffset at, QuestId activeQuest);

    /// <summary>Record an interaction action (called from extractor, not from observations).</summary>
    public void RecordInteract(NpcId target);
}
```

### 4.2 Observation method → field mapping

| `ev.Method` | `ev.Argument` | `ev.Value` | Field update |
|---|---|---|---|
| `GetPlayerZone` | null | `{"value":N}` | `Zone = new ZoneId(N)` |
| `GetPlayerPosition` | null | `{"x":X,"y":Y,"z":Z}` | `Position = new WorldPosition(X,Y,Z)` |
| `GetQuestSequence` | `{"value":Q}` | `<int>` | iff `Q == _activeQuest.Value` → `QuestSequence = value` |
| `GetQuestFlags` | `{"value":Q}` | `<uint>` | iff `Q == _activeQuest.Value` → `QuestFlags = value` |
| `IsQuestAccepted` | `{"value":Q}` | `<bool>` | iff `Q == _activeQuest.Value` → `QuestAccepted = value` |
| `IsQuestComplete` | `{"value":Q}` | `<bool>` | iff `Q == _activeQuest.Value` → `QuestCompleted = value` |
| any other | * | * | ignored (returns false from `Apply`) |

**Parsing rules:**
- `ev.Value` is `JsonElement?`. If null → `Apply` returns true (counted as recognised) but performs no mutation.
- A failure-shaped value (`{"failure": "...", "detail": "..."}`) → `Apply` returns true, performs no mutation. Detection: presence of a `"failure"` property at the value root.
- Type mismatches (e.g. `GetPlayerZone` value is `<bool>`) → swallow and skip; do not throw. Goal: tolerate slightly malformed traces.

### 4.3 `ToSnapshot`

Produces a `GameStateSnapshot` (from `QuestForge.Engine.Authoring`) with:

| Snapshot field | Source |
|---|---|
| `CapturedAt` | the `at` argument |
| `Zone` | `this.Zone` |
| `Position` | `this.Position` |
| `ActiveQuest` | `_activeQuest` (or override from `ToSnapshot(at, q)`) |
| `QuestSequence` | `this.QuestSequence` |
| `QuestFlags` | `this.QuestFlags` |
| `QuestAccepted` | `this.QuestAccepted` |
| `QuestCompleted` | `this.QuestCompleted` |
| `LastNpcInteracted` | `this.LastNpcInteracted` |
| `LastNpcPosition` | `this.LastNpcPosition` |
| `LastDialoguePrompt` | `null` (not recovered from trace in Phase 10) |
| `LastDialogueAnswer` | `null` (not recovered from trace in Phase 10) |
| `InventoryHash` | `0u` (not recovered from trace in Phase 10) |

### 4.4 Design rationale

- **Mutable accumulator + immutable snapshot.** Mirrors `SnapshotAggregator` in `QuestForge.Engine.Authoring` which already follows this pattern. Tests can probe internal state directly without invoking the inference engine.
- **`_activeQuest` filter.** Trace observations may include other quests' `GetQuestSequence` calls (e.g. scheduler polls). Filtering by quest ID at the accumulator level keeps inference clean.
- **Last-value-wins.** Matches the recording proxy's dedup semantics — a value only appears in the trace when it changes, so the most recent observation is always the current truth.

---

## 5. `TraceEventParser` — JSONL reader

```csharp
namespace QuestForge.Tools.Trace.Parsing;

public static class TraceEventParser
{
    /// <summary>
    /// Reads a JSONL trace file. Lines that are blank or fail to deserialize
    /// are skipped with a warning written to the supplied logger (or stderr).
    /// </summary>
    public static IReadOnlyList<TraceEvent> ReadFile(string path, TextWriter? warnings = null);

    /// <summary>Reads from a stream (used in tests).</summary>
    public static IReadOnlyList<TraceEvent> ReadStream(Stream stream, TextWriter? warnings = null);

    /// <summary>Reads from raw text.</summary>
    public static IReadOnlyList<TraceEvent> ReadText(string jsonl, TextWriter? warnings = null);
}
```

**Implementation:** `JsonSerializer.Deserialize<TraceEvent>(line, TraceEventJsonContext.Default.TraceEvent)`. Unknown `type` discriminators are skipped (`JsonException` is caught and counted). Missing trailing newline on the final line is **not** an error per `TRACE_FORMAT.md` §9.2.

---

## 6. Operation 1 — `TraceToFixtureExtractor` (`extract-fixture`)

### 6.1 Purpose

Read a trace and produce a `FixtureModel` matching `FIXTURES.md` §format. Surface in CLI as:

```
qf-trace extract-fixture <trace.jsonl> [--quest-data <dir>] [--stdout] [--out <path>]
```

### 6.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Fixture;

public sealed class TraceToFixtureExtractor
{
    public TraceToFixtureExtractor(string? questDataRoot = null);

    public Result<FixtureModel> Extract(IReadOnlyList<TraceEvent> events);

    /// <summary>Suggested filename based on capability shape, e.g. "simple-linear-acceptance.json".</summary>
    public string SuggestFilename(FixtureModel fixture);
}

public sealed record FixtureModel(
    string SchemaVersion,            // "1.0.0"
    string Description,              // "TODO: add description"
    string InitialState,             // "fresh"
    IReadOnlyList<string> Capabilities,
    string QuestFile,                // forward-slash relative to questDataRoot
    IReadOnlyList<TransitionEntry> ExpectedTransitions,
    string? TerminalOutcome);        // "done" | "awaitUser" | null when no RunEnd seen

public sealed record TransitionEntry(string? StepId, string ActionType);
```

`Result<T>` is `QuestForge.Adapters.Types.Result<T>` (the same union used throughout the engine).

### 6.3 Algorithm

1. Find the single `RunStartEvent`. If absent → `Result.Failure("no-run-start", "trace contains no run.start event")`.
2. Capture `RunStart.RunId`, `RunStart.QuestId`.
3. Iterate events in file order. **Skip events whose `runId` differs from `RunStart.RunId`** (defensive).
4. For each `DecisionEvent`:
   - Build `(stepId, actionType)` where `actionType` is normalised to lowercase
   - Skip terminal actions (`"done"`, `"awaitUser"`) — they belong in `terminalOutcome`, not in transitions
   - Append to `expectedTransitions` **only if** different from the last appended tuple (string equality on both fields, `null` stepIds compare equal)
5. Find the last `RunEndEvent`. `terminalOutcome` = `RunEnd.Outcome` **as-is** (do NOT lowercase — `"done"` is already lowercase and `"awaitUser"` is camelCase per `FIXTURES.md`), or `null` if absent.
6. Resolve `questFile`:
   - If `questDataRoot` is null → `questFile = "quests/UNKNOWN/{questId}.json"` (placeholder)
   - Otherwise: search `questDataRoot/quests/**/{questId}-*.json` and `{questId}.json`. First match wins. Path returned as `"quests/.../filename.json"` with forward slashes, relative to `questDataRoot`. If no match → placeholder path with a TODO note in description.
7. Compute `capabilities` via `CapabilityInferrer.Infer(quest)` if quest file loaded; empty list otherwise.
8. `description = "TODO: add description"`.
9. `initialState = "fresh"` (Phase 10 only emits this value).
10. `schemaVersion = "1.0.0"`.

### 6.4 `SuggestFilename` heuristic

Lowercase, dash-joined cap tags after stripping the `step:` / `predicate:` / `engine:` prefix. Examples:

| Capability set | Suggested filename |
|---|---|
| `step:travel`, `step:talk`, `predicate:playerNear`, `predicate:questSequence` | `simple-linear-acceptance.json` |
| `step:duty` | `with-dungeon.json` |
| `step:branch` | `with-branching.json` |

Initial implementation uses a small static lookup table mapping the **sorted set of step types** to a canonical filename, with `simple-linear-acceptance.json` as the fallback. Suggested table (extend as new shapes arrive):

```csharp
{
    { ["step:travel","step:talk"],          "simple-linear-acceptance.json" },
    { ["step:duty"],                        "with-dungeon.json" },
    { ["step:branch"],                      "with-branching.json" },
    { ["step:fragment"],                    "with-fragments.json" },
    { ["step:spd"],                         "with-spd.json" },
}
```

---

## 7. Operation 2 — `FixtureValidator` (`validate-fixture`)

### 7.1 Purpose

Cross-validate a committed fixture file against its referenced quest definition.

```
qf-trace validate-fixture <fixture.json> [--quest-data <dir>] [--fail-on-warning]
```

### 7.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Fixture;

public sealed class FixtureValidator
{
    public FixtureValidator(string questDataRoot);

    public FixtureValidationResult Validate(FixtureModel fixture);

    /// <summary>Convenience: load and validate a JSON file from disk.</summary>
    public FixtureValidationResult ValidateFile(string fixturePath);
}

public sealed record FixtureValidationResult(
    IReadOnlyList<FixtureValidationIssue> Errors,
    IReadOnlyList<FixtureValidationIssue> Warnings)
{
    public bool IsClean => Errors.Count == 0 && Warnings.Count == 0;
    public bool HasErrors => Errors.Count > 0;
}

public sealed record FixtureValidationIssue(
    string Code,            // e.g. "fixture/quest-file-missing"
    string Message,
    string? StepId = null);
```

### 7.3 Validation rules (errors)

| Code | Condition | Message template |
|---|---|---|
| `fixture/quest-file-missing` | `Path.Combine(questDataRoot, fixture.QuestFile)` does not exist | `quest file not found: {questFile}` |
| `fixture/quest-file-unreadable` | File exists but `QuestFileLoader.Load` throws | `failed to parse quest file: {ex.Message}` |
| `fixture/step-id-unknown` | `transition.StepId` is non-null and not present in any step of any sequence in the loaded quest | `step id '{stepId}' not found in quest {questId}` (one issue per offending transition) |
| `fixture/terminal-outcome-unknown` | `fixture.TerminalOutcome` not in the allowlist `{"done", "awaitUser"}` | `terminalOutcome '{outcome}' is not one of: done, awaitUser` |
| `fixture/schema-version-unsupported` | `fixture.SchemaVersion` not in `{"1.0.0"}` | `schemaVersion '{v}' is not supported by this tool` |

### 7.4 Validation rules (warnings)

| Code | Condition | Message template |
|---|---|---|
| `fixture/capability-missing` | A `step:X` capability is implied by the quest's actual step types but missing from `fixture.Capabilities`. Similarly for `predicate:X`. | `quest uses '{cap}' but capabilities list does not include it` |
| `fixture/capability-extra` | A capability is in `fixture.Capabilities` but the quest does not use the corresponding step type or predicate function. | `capabilities list includes '{cap}' but quest does not use it` |
| `fixture/initial-state-unknown` | `fixture.InitialState` not in `{"fresh"}` | `initialState '{s}' is not recognised; expected 'fresh'` |

Capability comparison delegates to `CapabilityInferrer.Infer(quest)`.

### 7.5 CLI exit codes

- `0` — no errors, no warnings
- `1` — at least one error
- `2` — no errors, at least one warning, **and** `--fail-on-warning` was passed
- `0` — no errors, warnings, but `--fail-on-warning` not passed (mirrors `qf-validate`)

---

## 8. Operation 3 — `ListFixtures` (`list-fixtures`)

### 8.1 Purpose

Tabulate all `fixtures/engine/*.json` files, their capabilities, and whether their `questFile` resolves.

```
qf-trace list-fixtures [--quest-data <dir>] [--format text|json]
```

### 8.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Fixture;

public sealed class ListFixturesCommand
{
    public ListFixturesCommand(string questDataRoot);

    public IReadOnlyList<FixtureListEntry> Enumerate();

    /// <summary>Gap report: step types / predicates that appear in any quest in
    /// questforge-data/quests but are not covered by any fixture's capabilities list.</summary>
    public IReadOnlyList<string> ComputeGaps(IReadOnlyList<FixtureListEntry> fixtures);
}

public sealed record FixtureListEntry(
    string FixtureFile,                 // forward-slash, relative to questDataRoot
    IReadOnlyList<string> Capabilities,
    bool QuestFileExists,
    string? QuestFile);
```

### 8.3 Algorithm

1. Enumerate `Directory.EnumerateFiles(Path.Combine(questDataRoot, "fixtures/engine"), "*.json")`.
2. For each file: load as `FixtureModel`; record capability list and quest-file existence.
3. `ComputeGaps`: walk `questDataRoot/quests/**/*.json`, run `CapabilityInferrer.Infer` on each, collect the union, then subtract the union of capabilities across all fixtures. The remaining set is the gap.

### 8.4 Output (text format)

```
fixture                              quest                                        capabilities
-----------------------------------  -------------------------------------------  -----------------------------------------------------
simple-linear-acceptance.json        quests/arr/msq/66130-coming-to-uldah.json    [OK] step:travel, step:talk, predicate:playerNear, ...
with-dungeon.json                    [MISSING] quests/.../000000.json              [WARN] step:duty

Gaps (uncovered capabilities):
  step:cutscene
  predicate:inCombat
```

### 8.5 Out of scope for testing

`ListFixtures` is integration-only (filesystem walk). No unit tests required per the Phase 10 prompt. A single smoke test that the dispatch from `qf-trace list-fixtures` invokes the right code path is enough.

---

## 9. Operation 4 — `TraceToQuestExtractor` (`extract-quest`)

### 9.1 Purpose

Read a trace and produce a `QuestDefinition` draft with `supportStatus.implementation = "partial"`.

```
qf-trace extract-quest <trace.jsonl> [--quest-data <dir>] [--out <file>]
```

### 9.2 Interface

```csharp
namespace QuestForge.Tools.Trace.Quest;

public sealed class TraceToQuestExtractor
{
    public TraceToQuestExtractor(StepInferenceEngine? inference = null);

    public Result<QuestDraftResult> Extract(IReadOnlyList<TraceEvent> events);
}

public sealed record QuestDraftResult(
    QuestDefinition Definition,
    IReadOnlyList<string> Todos);    // ordered list of TODO labels for human review
```

The CLI serialises `Definition` with `QuestForgeJsonContext.QuestFileOptions` and prints `Todos` separately so authors know what to fix.

### 9.3 Algorithm (the trace replay loop)

Inputs: ordered list of `TraceEvent` (from `TraceEventParser`).

1. **Locate** the unique `RunStartEvent`. Failure → `Result.Failure("no-run-start", ...)`.
2. **Initialise** `var snapshot = new SnapshotState(new QuestId(runStart.QuestId));`
3. **Pre-roll**: apply every `ObservationEvent` between `RunStart` and the first `DecisionEvent` to `snapshot`. This sets the "starting state".
4. **Index** decisions in order: `decisions = events.OfType<DecisionEvent>().ToList()`.
5. **For each** `decision` at index `i`:
   - `before = snapshot.ToSnapshot(decision.At, activeQuest)`
   - `submitted = first ActionSubmittedEvent appearing after decision (by index) with the same runId`
   - `completed = first ActionCompletedEvent appearing after submitted`
   - **Advance** `snapshot` by applying every `ObservationEvent` strictly between `completed` (exclusive) and `decisions[i+1]` (exclusive), or to end-of-trace if `i` is last
   - If `submitted.ActionType.Equals("Interact", OICompare)` → call `snapshot.RecordInteract(npcId)` **before** the advance so the inference engine sees the interacted NPC on the "after" side (matches Phase 9 ordering — see `SnapshotAggregator.HandleInteractor`)
   - `afterAt = i + 1 < decisions.Count ? decisions[i + 1].At : (runEnd?.At ?? completed?.At ?? decision.At)`
   - `after = snapshot.ToSnapshot(afterAt, activeQuest)`
   - `inference = StepInferenceEngine.Infer(before, after)`
   - Build a concrete `Step` (see §9.4) using `submitted.Parameters` + `inference`
   - Capture the quest sequence number that was observed **at decision time** (i.e. `before.QuestSequence`) — call this `groupKey`
   - Append `(groupKey, step)` to a working list
6. **Group** the working list into `QuestSequence` objects:
   - Stable group by `groupKey` preserving order
   - Each group → `new QuestSequence { Sequence = groupKey, Steps = [...] }`
7. **Assemble** the `QuestDefinition` (see §9.5).
8. **Collect TODOs** during assembly (NPC name placeholders, missing zone names, etc.).

### 9.4 Step construction from `ActionSubmittedEvent`

| `submitted.ActionType` (case-insensitive) | Step subtype | Step field assignments |
|---|---|---|
| `"navigate"` | `TravelStep` | `Destination = new TravelDestination(zone, new Position3(x, y, z))` where `x/y/z` come from `parameters.destination.{x,y,z}` (with fallback to top-level `x/y/z`), `zone` from `parameters.zone` (fallback `0`); `Id` from `inference.SuggestedStepId` or `$"travel-{i}"`; `Expect` from `inference.SuggestedExpect` (if non-null) as `new PredicateExpect(...)`; `StopDistance = parameters.options.stoppingDistance` if present |
| `"interact"` | `TalkStep` *if* `inference.StepType == "talk"` or default; `AcceptStep` if `inference.InferredFrom == QuestAccepted`; `TurnInStep` if `inference.InferredFrom == QuestCompleted` | `Target = new NpcLocation(npcId, snapshot.Zone.Value, default-position)` where `npcId` comes from `parameters.target` (fallback `parameters.value`); `Id` from `inference.SuggestedStepId`; `Expect` from `inference.SuggestedExpect` |
| `"done"` | **skip** — do not emit a step | `"done"` represents `EngineAction.Done` (engine concluding the run), not a turn-in action. The turn-in step is already captured by the preceding `"interact"` decision where `InferredFrom == QuestCompleted`. |
| anything else | generic `Step` with `Id` from `inference.SuggestedStepId` | best-effort — surfaces as a TODO |

**Notes for testers:**
- `Position3` and `TravelDestination` are `QuestForge.Schema` records — `record TravelDestination(int Zone, Position3? Position = null, uint? AetheryteId = null)`.
- `NpcLocation` is `record NpcLocation(uint NpcId, int Zone, Position3 Position)`. When the position is unknown from the trace, use `new Position3(0, 0, 0)` and add a TODO.
- `PredicateExpect` wraps a raw predicate string; `ExpectValue` is the abstract parent; both live in `QuestForge.Schema`.

### 9.5 `QuestDefinition` assembly

```csharp
return new QuestDefinition
{
    SchemaVersion    = "1.0.0",         // must match existing quest files
    Id               = runStart.QuestId,
    Name             = "TODO",                                  // → Todos: "name (Lumina lookup)"
    Expansion        = "TODO",                                  // → Todos: "expansion"
    Category         = "TODO",                                  // → Todos: "category"
    Enabled          = true,
    SupportStatus    = new SupportStatus { Implementation = "partial", KnownIssues = [] },
    LastVerifiedPatch= "TODO",                                  // → Todos: "lastVerifiedPatch"
    Requirements     = new Requirements(),                      // → Todos: "requirements (level, job, prereqs)"
    AcceptFrom       = inferredAcceptNpcLocation ?? new NpcLocation(0, 0, new Position3(0,0,0)), // → Todos: "acceptFrom NPC name"
    Sequences        = sequences.ToArray()
};
```

`Todos` is a flat string list summarising every TODO field — printed by the CLI after writing the JSON so the user has a checklist.

### 9.6 Sequence grouping rule

The `groupKey` is `before.QuestSequence` at decision time. Consecutive decisions sharing the same group key fall into the same `QuestSequence`. The first group key encountered will typically be `0` (pre-acceptance) and increment as the quest progresses. Sequence numbers in the final `Sequences[]` array equal the `groupKey` value (no normalisation).

### 9.7 Edge cases

- **No matching `ActionSubmittedEvent`** for a decision → skip that decision (do not synthesise an empty step). Emit a TODO: `"decision at seq N had no action.submitted; skipped"`.
- **Decision after `RunEnd`** → ignored (defensive).
- **`parameters` is `null`** for navigate/interact → emit a placeholder step with TODO and a confidence note.

---

## 10. `CapabilityInferrer`

```csharp
namespace QuestForge.Tools.Trace.Capabilities;

public static class CapabilityInferrer
{
    public static IReadOnlyList<string> Infer(QuestDefinition quest);
}
```

### 10.1 Rules

For every `Step` in every `QuestSequence`:
- Emit `"step:<discriminator>"` using the JSON discriminator string from `Step.cs` (`travel`, `talk`, `accept`, `turn-in`, `interact-object`, `pickup-item`, `combat`, `duty` (split into `step:duty` and `step:spd` when `kind=="spd"`), `cutscene`, `say-chat-message`, `use-emote`, `use-item`, `use-action`, `equip-gear-for-quest`, `equip-best-gear`, `change-job`, `minigame`, `await-user`, `branch`, `fragment`)
- Walk `step.Expect` and `step.SkipIf` recursively (through `AllExpect` / `AnyExpect`) and for every leaf `PredicateExpect`, extract the function name (substring before `(`) → emit `"predicate:<name>"`
- For `BranchStep`, also walk every branch case's `When` predicate
- Emit `"engine:branching"` iff any `BranchStep` is present
- Emit `"engine:fragments"` iff any `FragmentStep` is present

Return capabilities sorted alphabetically, de-duplicated.

---

## 11. CLI dispatch (`qf-trace/Program.cs`)

Mirrors the `qf-validate` argument-parse style (no third-party CLI lib). Top-level routing:

```
qf-trace extract-fixture <trace.jsonl> [--quest-data <dir>] [--stdout] [--out <path>]
qf-trace validate-fixture <fixture.json> [--quest-data <dir>] [--fail-on-warning]
qf-trace list-fixtures [--quest-data <dir>] [--format text|json]
qf-trace extract-quest <trace.jsonl> [--quest-data <dir>] [--out <path>]
qf-trace --help
```

Exit-code conventions:
- `0` — success / clean validation
- `1` — usage error or fatal error (missing file, unparseable trace)
- `2` — `validate-fixture` clean errors but warnings + `--fail-on-warning`

Output JSON (when writing `QuestDefinition`) uses `QuestForgeJsonContext.QuestFileOptions` (indented, `UnsafeRelaxedJsonEscaping` per the rationale in that file).

---

## 12. Test scenarios

All tests are **xUnit** in `QuestForge.Tools.Trace.Tests`. No filesystem dependencies except for fixture inputs under `QuestForge.Tools.Trace.Tests/Fixtures/`.

### 12.1 `extract-fixture` — `TraceToFixtureExtractorTests`

**Scenario 1 — Empty trace.**
Given: an empty event list.
When: `Extract([])`.
Then: returns `Result.Failure("no-run-start", "trace contains no run.start event")`.

**Scenario 2 — RunStart but no RunEnd.**
Given: a trace with `RunStart`, one `Decision(navigate, "step-1")`, and no `RunEnd`.
When: `Extract(events)`.
Then: returns `Success` with `TerminalOutcome == null` and one transition `("step-1", "navigate")`.

**Scenario 3 — Single Navigate + RunEnd done.**
Given: `RunStart(quest=42)` → `Decision("travel-to-x", "navigate")` → `RunEnd("done")`.
When: `Extract`.
Then: `expectedTransitions == [("travel-to-x", "navigate")]`, `terminalOutcome == "done"`.

**Scenario 4 — Decision deduplication.**
Given: `RunStart` → `Decision("s1", "navigate")` × 4 → `Decision("s1", "interact")` × 2 → `RunEnd("done")`.
When: `Extract`.
Then: `expectedTransitions == [("s1","navigate"), ("s1","interact")]` (only consecutive identical pairs collapse).

**Scenario 5 — Consecutive different transitions preserved.**
Given: 6 decisions `(A,nav) (A,int) (B,nav) (B,int) (B,nav) (B,int)` between RunStart and RunEnd.
When: `Extract`.
Then: `expectedTransitions` has all 6 entries (no two adjacent are identical).

**Scenario 6 — RunId mismatch filter.**
Given: `RunStart(runId="aaa", quest=1)` → `Decision(runId="bbb", "s-other", "navigate")` → `Decision(runId="aaa", "s-mine", "navigate")` → `RunEnd(runId="aaa", "done")`.
When: `Extract`.
Then: only the `runId="aaa"` decision appears in transitions.

**Scenario 7 — `questFile` resolution from disk.**
Given: trace with `RunStart(quest=66130)`, `questDataRoot` pointing at a temp directory containing `quests/arr/msq/66130-coming-to-uldah.json`.
When: `Extract`.
Then: `fixture.QuestFile == "quests/arr/msq/66130-coming-to-uldah.json"` (forward slashes).

### 12.2 `validate-fixture` — `FixtureValidatorTests`

**Scenario 8 — Valid fixture.**
Given: a fixture whose `questFile` exists, transitions reference real step IDs, capabilities exactly match what the quest uses, terminal outcome is `"done"`.
When: `Validate`.
Then: `IsClean == true` (zero errors, zero warnings).

**Scenario 9 — Missing `questFile`.**
Given: fixture references `quests/missing.json`; directory does not contain that file.
When: `Validate`.
Then: errors include `fixture/quest-file-missing`.

**Scenario 10 — Unknown step ID.**
Given: fixture has transition `("does-not-exist", "navigate")`; quest file's step IDs are `["step-a","step-b"]`.
When: `Validate`.
Then: errors include `fixture/step-id-unknown` with `StepId == "does-not-exist"`.

**Scenario 11 — Unknown terminal outcome.**
Given: fixture `terminalOutcome == "exploded"`.
When: `Validate`.
Then: errors include `fixture/terminal-outcome-unknown`.

**Scenario 12 — Missing capability warning.**
Given: quest uses `step:cutscene` but fixture's capabilities list omits it.
When: `Validate`.
Then: warnings include `fixture/capability-missing` for `step:cutscene`.

**Scenario 13 — Extra capability warning.**
Given: fixture lists `step:combat` but the referenced quest contains no `CombatStep`.
When: `Validate`.
Then: warnings include `fixture/capability-extra` for `step:combat`.

### 12.3 `extract-quest` — `TraceToQuestExtractorTests`

For these tests, build minimal in-memory JSONL fixtures rather than reading from disk; use `TraceEventParser.ReadText`.

**Scenario 14 — Navigate action produces TravelStep.**
Given: a trace with `RunStart(quest=66130)`, observation `GetPlayerZone=182`, observation `GetPlayerPosition=(10,0,10)`, `Decision(null,"navigate")`, `ActionSubmitted("Navigate", parameters={"destination":{"x":44.7,"y":4.0,"z":-148.7},"zone":182,"options":{"stoppingDistance":3.0}})`, `ActionCompleted("Navigate","Arrived")`, observation `GetPlayerPosition=(44.7,4.0,-148.7)`, `Decision(null, "done")`, `RunEnd("done")`.
When: `Extract`.
Then: `Definition.Sequences[0].Steps[0]` is `TravelStep` with `Destination.Zone == 182` and `Destination.Position == new Position3(44.7f, 4.0f, -148.7f)`.

**Scenario 15 — Interact action produces TalkStep with NpcId.**
Given: trace with `ActionSubmitted("Interact", parameters={"target": 1014875})` plus the supporting observations.
When: `Extract`.
Then: produced step is `TalkStep` with `Target.NpcId == 1014875u`.

**Scenario 16 — Sequence advance observation populates `Expect`.**
Given: `before.QuestSequence == 0`, `after.QuestSequence == 1` across an Interact decision (the trace contains a `GetQuestSequence` observation flipping 0 → 1 after the interact's action.completed).
When: `Extract`.
Then: the resulting `TalkStep.Expect` is a `PredicateExpect` whose source equals `"questSequence(66130) >= 1"`.

**Scenario 17 — Zone change observation sets TravelStep zone.**
Given: trace observes `GetPlayerZone=182` before a Navigate and `GetPlayerZone=128` after the Navigate completes.
When: `Extract`.
Then: the next `TravelStep.Destination.Zone == 128` (taken from `after.Zone` if no `parameters.zone` is present; else `parameters.zone`).

**Scenario 18 — Quest completed observation produces TurnInStep.**
Given: trace observes `IsQuestComplete(66130) = true` after the last Interact's `ActionCompleted`.
When: `Extract`.
Then: the last step in the last sequence is a `TurnInStep` whose `Expect` is `isQuestComplete(66130)` (the `StepInferenceEngine` Rule 1 path).

**Scenario 19 — Sequence grouping by `before.QuestSequence`.**
Given: a trace with five decisions whose `before.QuestSequence` values, in order, are `0, 0, 1, 1, 1`.
When: `Extract`.
Then: `Definition.Sequences.Length == 2`; first group has 2 steps with `Sequence == 0`; second group has 3 steps with `Sequence == 1`.

**Scenario 20 — Consecutive decisions in same sequence stay grouped.**
Given: three consecutive decisions all with `before.QuestSequence == 2`.
When: `Extract`.
Then: a single `QuestSequence { Sequence = 2, Steps.Length = 3 }` is emitted.

### 12.4 `SnapshotState` — `SnapshotStateTests`

**Scenario 21 — `GetPlayerZone` updates Zone.**
Given: a `SnapshotState` initialised with quest 66130.
When: `Apply(ObservationEvent(method="GetPlayerZone", argument=null, value={"value": 182}))`.
Then: `state.Zone == new ZoneId(182u)` and `Apply` returns `true`.

**Scenario 22 — `GetQuestSequence` for wrong questId is ignored.**
Given: `SnapshotState(new QuestId(66130))`, initial `QuestSequence == 0`.
When: `Apply(ObservationEvent(method="GetQuestSequence", argument={"value": 12345}, value=5))`.
Then: `state.QuestSequence == 0` (unchanged); `Apply` returns `true` (the method is recognised; the filter just declines to mutate).

**Scenario 23 — `GetQuestFlags` for correct questId updates flags.**
Given: `SnapshotState(new QuestId(66130))`.
When: `Apply(ObservationEvent(method="GetQuestFlags", argument={"value": 66130}, value=0x0F))`.
Then: `state.QuestFlags == 0x0Fu`.

**Scenario 24 — Last-value-wins for repeated observations.**
Given: `SnapshotState(new QuestId(66130))`.
When: apply `GetQuestSequence` returning `1`, then `2`, then `3` (all for quest 66130).
Then: `state.QuestSequence == 3`.

**Scenario 25 — `ToSnapshot` captures accumulated state at timestamp.**
Given: state after applying `GetPlayerZone=182`, `GetPlayerPosition=(1,2,3)`, `GetQuestSequence(66130)=4`, `IsQuestAccepted(66130)=true`.
When: `state.ToSnapshot(DateTimeOffset.Parse("2026-05-16T12:00:00Z"))`.
Then: snapshot has `CapturedAt == that timestamp`, `Zone == ZoneId(182)`, `Position == WorldPosition(1,2,3)`, `QuestSequence == 4`, `QuestAccepted == true`, `ActiveQuest == new QuestId(66130)`.

### 12.5 Additional sanity tests (out of the 25; nice-to-have)

- `CapabilityInferrer` returns `["step:travel","step:talk","predicate:playerZone","predicate:questSequence"]` for quest 66130 (sorted).
- `TraceEventParser` skips blank lines and malformed lines, returning everything else.
- `SuggestFilename` returns `simple-linear-acceptance.json` for `[step:travel, step:talk]`.

---

## 13. Estimated test count summary

- Happy path scenarios: 11 (1–3, 8, 14–17, 21, 23, 25)
- Edge cases: 9 (4–7, 11–13, 19–20, 22, 24)
- Error cases: 5 (1 reformulated, 9, 10, plus malformed-trace and missing-action-submitted from §9.7)

**Expected total: ~25 tests** (matches the 25 named scenarios; a Tester may add 3–5 more sanity tests as listed in §12.5, putting the realistic deliverable at ~28–30 tests).

---

## 14. Done criteria

- All 25 named scenarios pass as xUnit tests in `QuestForge.Tools.Trace.Tests`.
- `qf-trace --help` lists all four subcommands.
- Running `qf-trace extract-fixture` against the canonical quest 66130 trace under `pluginConfigs/QuestForge/traces/` produces output byte-identical (modulo `description`) to the committed `simple-linear-acceptance.json` fixture.
- Running `qf-trace validate-fixture fixtures/engine/simple-linear-acceptance.json` against `questforge-data` reports clean.
- `qf-trace list-fixtures` prints the current single fixture and lists known capability gaps (all step types beyond `step:travel` / `step:talk`).
- `qf-trace extract-quest` against the same trace round-trips through the schema deserializer without error and reports a TODO list including at least `name`, `expansion`, `category`, `lastVerifiedPatch`, `requirements`, and `acceptFrom NPC name`.

---

## READY FOR TEST CREATION

Tester: Write comprehensive test suite from these behaviors.
- Happy paths: 11 scenarios
- Edge cases: 9 scenarios
- Error cases: 5 scenarios
- Expected total: ~25 tests (28–30 with optional sanity additions in §12.5)
