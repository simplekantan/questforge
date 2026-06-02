# Trace Format Spec Compliance Plan

**Status:** ready to implement
**Input docs:** docs/TRACE_FORMAT.md (spec, especially sections 4 and 5.1), docs/NEXT_STEPS.md (Phase 11 context), issue #15
**Output:** Trace events emitted by the plugin match TRACE_FORMAT.md section 4: every JSONL line has `v`, `seq`, `ts` envelope fields plus a `data` wrapper for type-specific payload. `run.start` carries expanded metadata. Old traces break (acceptable; re-recording planned). One PR for `questforge` repo, paired PR for `questforge-tools`.
**Phase dependencies:** none (refactoring of existing infrastructure)

---

## Dependency graph

```
questforge repo (this PR)
  QuestForge.Adapters/Tracing/  <-- TraceEvent base, all 7 derived types, TraceEventJsonContext
  QuestForge.Adapters/Tracing/TraceSession.cs  <-- seq/ts stamping
  QuestForge.Adapters/Tracing/TraceWriter.cs  <-- serialization unchanged (delegates to context)
  QuestForge.Engine/QuestEngine.cs  <-- construction sites update
  QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs  <-- construction sites update
  QuestForge.Plugin/EngineHost.cs  <-- construction sites + run.start metadata
  QuestForge.Plugin/Authoring/AuthoringHost.cs  <-- construction sites
  QuestForge.Engine.Tests/Tracing/  <-- all trace tests rewritten
  QuestForge.Adapters.Fakes/FakeTraceWriter.cs  <-- unchanged (takes TraceEvent)

questforge-tools repo (paired PR)
  QuestForge.Tools.Trace/Parsing/TraceEventParser.cs  <-- reads new shape
  QuestForge.Tools.Trace/SnapshotState.cs  <-- reads from data sub-objects
  QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs  <-- reads from data sub-objects
  QuestForge.Tools.Trace/Quest/TraceToQuestExtractor.cs  <-- reads from data sub-objects
  QuestForge.Tools.Trace.Tests/  <-- test updates
```

**Build order:** Schema types (TraceEvent hierarchy) first --> TraceSession stamping --> construction sites --> tests --> tools repo.

---

## Architectural decisions

### TSC1: Envelope fields on TraceEvent base; `At` removed

**Decision:** Remove the `DateTimeOffset At` positional parameter from `TraceEvent`. Replace with three init-only properties: `int V = 1`, `int Seq`, `long Ts`. Callers no longer pass timestamps; `TraceSession` stamps `Seq` and `Ts` at write time.

**Alternatives considered:**
- Keep `At` alongside new fields: rejected because `At` is redundant (wall-clock is captured once in `run.start.data.wallClockUtc` per spec section 4.1) and creates two competing time representations.
- Make `Seq`/`Ts` nullable and only set by `TraceSession`: rejected because every serialized event must have them; nullable would emit `null` when bypassing `TraceSession` (e.g. direct `TraceWriter` usage in tests).

**Concrete C# surface area:**

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartEvent),        "run.start")]
[JsonDerivedType(typeof(RunEndEvent),          "run.end")]
[JsonDerivedType(typeof(ObservationEvent),     "observation")]
[JsonDerivedType(typeof(DecisionEvent),        "decision")]
[JsonDerivedType(typeof(ActionSubmittedEvent), "action.submitted")]
[JsonDerivedType(typeof(ActionCompletedEvent), "action.completed")]
[JsonDerivedType(typeof(StepRecordedEvent),    "step.recorded")]
public abstract record TraceEvent
{
    [JsonPropertyOrder(-3)] public int V { get; init; } = 1;
    [JsonPropertyOrder(-2)] public int Seq { get; init; }
    [JsonPropertyOrder(-1)] public long Ts { get; init; }

    [JsonIgnore] public abstract string Type { get; }
}
```

**What breaks if violated:** Events without `v`/`seq`/`ts` fail the spec section 9.3 validation contract (`seq` strict monotonic from 0). Replay and analysis tools that depend on `seq` ordering will malfunction.

**Testability:** Unit tests verify serialized JSON contains `"v":1,"seq":N,"ts":N` at the top level for every event type.

### TSC2: `RunId` promoted to base class

**Decision:** Add `string RunId` as a required init-only property on `TraceEvent` base. Remove `RunId` from all derived type positional parameters. `ObservationEvent.RunId` was `string?`; on the base it is `string` (non-nullable). Callers that previously passed `null` for observation RunId must pass `""` or the current run ID.

**Alternatives considered:**
- Keep `RunId` nullable on base to match `ObservationEvent`'s old signature: rejected because the spec says `runId` is required on every event (section 4), and nullable would allow violations silently.
- Keep `RunId` per-type: rejected because it creates 7 duplicate declarations and the spec mandates it at the envelope level.

**Concrete C# surface area:**

```csharp
public abstract record TraceEvent
{
    // ... V, Seq, Ts from TSC1
    public string RunId { get; init; } = "";
}
```

**What breaks if violated:** Tools that filter by `RunId` across event types would need per-type casting. The `runId` consistency check in `qf-trace validate` (spec section 9.3) would be harder to implement.

**Testability:** Serialized JSON always contains `"runId":"..."` at the envelope level for all event types.

### TSC3: Data wrapper via nested record types

**Decision:** Each derived event type gets a nested `Data` record class and a `Data` property. Type-specific fields move from positional parameters to the `Data` record. The `[JsonPolymorphic]` discriminator puts `type` at top level alongside `v`, `seq`, `ts`, `runId`. The `Data` property serializes as a `"data":{...}` sub-object.

**Alternatives considered:**
- Custom `JsonConverter` that restructures flat events into envelope+data: rejected because it fights STJ source-gen and is fragile.
- Keep flat (no `data` wrapper): rejected because the spec explicitly requires it (section 4) and the Known Divergences table tracks this as a deferred item.

**Concrete C# surface area (all 7 types):**

```csharp
public sealed record RunStartEvent : TraceEvent
{
    [JsonIgnore] public override string Type => "run.start";
    public RunStartData Data { get; init; } = default!;

    public sealed record RunStartData
    {
        public uint QuestId { get; init; }
        public string SchemaVer { get; init; } = "1.0";
        public string? PluginVer { get; init; }
        public string? PatchVer { get; init; }
        public DateTimeOffset? WallClockUtc { get; init; }
        public object? EngineConfig { get; init; }
        public string? PrecedingRunId { get; init; }
        public object? NewGamePlus { get; init; }
    }
}

public sealed record RunEndEvent : TraceEvent
{
    [JsonIgnore] public override string Type => "run.end";
    public RunEndData Data { get; init; } = default!;

    public sealed record RunEndData
    {
        public string Outcome { get; init; } = "";
    }
}

public sealed record ObservationEvent : TraceEvent
{
    [JsonIgnore] public override string Type => "observation";
    public ObservationData Data { get; init; } = default!;

    public sealed record ObservationData
    {
        public string Method { get; init; } = "";
        public JsonElement? Argument { get; init; }
        public JsonElement? Value { get; init; }
    }
}

public sealed record DecisionEvent : TraceEvent
{
    [JsonIgnore] public override string Type => "decision";
    public DecisionData Data { get; init; } = default!;

    public sealed record DecisionData
    {
        public string? StepId { get; init; }
        public string ActionType { get; init; } = "";
    }
}

public sealed record ActionSubmittedEvent : TraceEvent
{
    [JsonIgnore] public override string Type => "action.submitted";
    public ActionSubmittedData Data { get; init; } = default!;

    public sealed record ActionSubmittedData
    {
        public string ActionType { get; init; } = "";
        public JsonElement? Parameters { get; init; }
    }
}

public sealed record ActionCompletedEvent : TraceEvent
{
    [JsonIgnore] public override string Type => "action.completed";
    public ActionCompletedData Data { get; init; } = default!;

    public sealed record ActionCompletedData
    {
        public string ActionType { get; init; } = "";
        public string Outcome { get; init; } = "";
    }
}

public sealed record StepRecordedEvent : TraceEvent
{
    [JsonIgnore] public override string Type => "step.recorded";
    public StepRecordedData Data { get; init; } = default!;

    public sealed record StepRecordedData
    {
        public string StepId { get; init; } = "";
        public int SequenceNumber { get; init; }
        public JsonElement Step { get; init; }
    }
}
```

**What breaks if violated:** If a derived type keeps flat fields instead of `Data`, the serialized JSON will have payload at the envelope level, violating section 4. The tools repo parser would need to handle two shapes.

**Testability:** Round-trip tests verify `"data":{...}` appears in serialized JSON and fields inside `data` are correct.

### TSC4: `QuestSchemaId` removed from `RunStartEvent`

**Decision:** The current `RunStartEvent` has both `QuestId` and `QuestSchemaId` (which is always set to the same value as `QuestId`). The spec section 5.1 uses `questId` and `schemaVer` (the quest schema version string, not a numeric ID). Remove `QuestSchemaId` and replace with `SchemaVer` (string) inside `RunStartData`.

**Alternatives considered:**
- Keep `QuestSchemaId` alongside `SchemaVer`: rejected because `QuestSchemaId` is redundant (always == `QuestId`) and not in the spec.

**What breaks if violated:** Tools that read `questSchemaId` will need to be updated (they already do in this paired PR), but no external consumers exist.

### TSC5: TraceSession stamps `Seq` and `Ts` at write time

**Decision:** `TraceSession` gains a `Stopwatch` (started on file open) and a `long _nextSeq` counter (reset on file open). When `Write(TraceEvent evt)` is called, TraceSession uses `with { Seq = _nextSeq++, Ts = _stopwatch.ElapsedMilliseconds }` to stamp the event before passing to the inner writer. Callers construct events without `Seq`/`Ts` (they default to 0).

**Alternatives considered:**
- Callers stamp their own `Seq`/`Ts`: rejected because multiple callers (engine, recording proxy, authoring host, EngineHost) would need coordinated counters, which is error-prone and violates the spec's "monotonic per-run" requirement.
- `TraceWriter` stamps instead of `TraceSession`: rejected because `TraceWriter` is a low-level stream wrapper with no knowledge of run boundaries; `TraceSession` already manages file open/close and dedup.

**Concrete C# surface area (additions to TraceSession):**

```csharp
// New private fields
private long _nextSeq;
private System.Diagnostics.Stopwatch? _stopwatch;

// In OpenFileUnderLock: start stopwatch, reset seq
_nextSeq = 0;
_stopwatch = System.Diagnostics.Stopwatch.StartNew();

// In CloseFileUnderLock: stop stopwatch
_stopwatch?.Stop();
_stopwatch = null;

// In Write/WriteUnderLock: stamp before delegating
var stamped = evt with { Seq = (int)_nextSeq++, Ts = _stopwatch?.ElapsedMilliseconds ?? 0 };
_writer.Write(stamped);
```

**What breaks if violated:** Without stamping, `seq` would be 0 on every event, breaking the monotonic ordering contract. Without `ts`, diagnostic timing information is lost.

**Testability:** Tests verify `Seq` is monotonically increasing across multiple `Write` calls, and `Ts` is non-negative and non-decreasing.

### TSC6: `run.start` expanded metadata

**Decision:** `RunStartData` gains these fields per spec section 5.1:
- `pluginVer` (string?) -- read from assembly version at the call site (EngineHost/AuthoringHost); null in tests
- `patchVer` (string?) -- FFXIV game patch version; null in tests/engine
- `wallClockUtc` (DateTimeOffset?) -- the single wall-clock timestamp; null after redaction
- `engineConfig` (object?) -- nullable, null for now (no EngineDecisionConfig type yet)
- `precedingRunId` (string?) -- null for now
- `newGamePlus` (object?) -- null for now

**Not added yet:** `dataVer` and `dataHash` are excluded per user decision (no data repo integration yet).

**Concrete C# surface area:** See `RunStartData` in TSC3 above.

**What breaks if violated:** Traces without `pluginVer`/`patchVer` are harder to triage in bug reports. Missing `wallClockUtc` prevents wall-clock reconstruction via `qf-trace timestamp`.

### TSC7: Construction site migration strategy

**Decision:** All ~30 construction sites in the `questforge` repo that create `TraceEvent` subtype instances are updated in one pass. The migration is mechanical:

Before:
```csharp
new RunStartEvent(RunId: runId, QuestId: quest.Id, QuestSchemaId: quest.Id, At: DateTimeOffset.UtcNow)
```

After:
```csharp
new RunStartEvent
{
    RunId = runId,
    Data = new RunStartEvent.RunStartData
    {
        QuestId = quest.Id,
        PluginVer = _pluginVersion,
        PatchVer = _patchVersion,
        WallClockUtc = DateTimeOffset.UtcNow
    }
}
```

Before:
```csharp
new DecisionEvent(RunId: _runId, StepId: stepId, ActionType: action.GetType().Name, At: DateTimeOffset.UtcNow)
```

After:
```csharp
new DecisionEvent
{
    RunId = _runId,
    Data = new DecisionEvent.DecisionData { StepId = stepId, ActionType = action.GetType().Name }
}
```

`Seq`, `Ts`, and `V` are omitted at construction (defaults: `V=1`, `Seq=0`, `Ts=0`); `TraceSession` stamps them at write time per TSC5.

**What breaks if violated:** Compilation errors from removed positional parameters. This is intentional -- the compiler catches all sites.

### TSC8: WriteObservation creates nested Data

**Decision:** `TraceSession.WriteObservation` constructs `ObservationEvent` with the new shape:

```csharp
WriteUnderLock(new ObservationEvent
{
    RunId = runId,
    Data = new ObservationEvent.ObservationData
    {
        Method = method,
        Argument = argument is { ValueKind: not JsonValueKind.Undefined } ? argument : null,
        Value = value
    }
});
```

The dedup key remains `(method, argRawText)` with `valueRaw` comparison. The `RunId` parameter to `WriteObservation` is still required (callers pass it).

### TSC9: TraceEventJsonContext registrations

**Decision:** All 7 nested `Data` record types must be registered in `TraceEventJsonContext`:

```csharp
[JsonSerializable(typeof(TraceEvent))]
[JsonSerializable(typeof(StepRecordedEvent))]
[JsonSerializable(typeof(RunStartEvent.RunStartData))]
[JsonSerializable(typeof(RunEndEvent.RunEndData))]
[JsonSerializable(typeof(ObservationEvent.ObservationData))]
[JsonSerializable(typeof(DecisionEvent.DecisionData))]
[JsonSerializable(typeof(ActionSubmittedEvent.ActionSubmittedData))]
[JsonSerializable(typeof(ActionCompletedEvent.ActionCompletedData))]
[JsonSerializable(typeof(StepRecordedEvent.StepRecordedData))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
public partial class TraceEventJsonContext : JsonSerializerContext { }
```

**What breaks if violated:** STJ source-gen will silently fall back to reflection for unregistered types, which may not work in AOT/trimmed scenarios and could produce incorrect JSON.

### TSC10: Tools repo parser migration

**Decision:** `TraceEventParser` in `questforge-tools` is updated to read the new shape. The `data` sub-object is handled automatically by STJ deserialization (the C# types define the shape). The heuristic fallback path (no `type` discriminator) is simplified or removed -- all production traces will have the new shape, and test traces should too.

The parser must also handle old-shape traces gracefully during the transition window by falling back: if `"data"` property is missing but flat fields are present, log a warning and skip (not crash). This is a courtesy for any locally-saved traces; it is NOT a backwards-compatibility guarantee.

**What breaks if violated:** `qf-trace extract-fixture`, `extract-quest`, `validate-fixture` would all fail to read new traces.

### TSC11: Debounce key migration

**Decision:** The decision debounce in `TraceSession` currently uses `(d.RunId, d.StepId ?? "", d.ActionType)` where `d` is a `DecisionEvent` with flat fields. After TSC3, these become `(evt.RunId, d.Data.StepId ?? "", d.Data.ActionType)`. `RunId` moves to the base (`evt.RunId`); `StepId` and `ActionType` move to `Data`.

The `RunStartEvent` dedup key changes from `rs.RunId` to `evt.RunId` (same semantics, different access path since `RunId` is now on the base).

---

## Task 1 -- TraceEvent base class refactoring

### 1.1 Remove `At` parameter, add envelope properties

File: `QuestForge.Adapters/Tracing/TraceEvent.cs`

Remove the `DateTimeOffset At` positional parameter. Add `V`, `Seq`, `Ts`, `RunId` as init-only properties with `[JsonPropertyOrder]` to ensure envelope fields appear first in JSON output.

### 1.2 Refactor all 7 derived event types

Files:
- `QuestForge.Adapters/Tracing/RunStartEvent.cs`
- `QuestForge.Adapters/Tracing/RunEndEvent.cs`
- `QuestForge.Adapters/Tracing/ObservationEvent.cs`
- `QuestForge.Adapters/Tracing/DecisionEvent.cs`
- `QuestForge.Adapters/Tracing/ActionSubmittedEvent.cs`
- `QuestForge.Adapters/Tracing/ActionCompletedEvent.cs`
- `QuestForge.Adapters/Tracing/StepRecordedEvent.cs`

Remove all positional parameters (they previously included `RunId`, type-specific fields, and `At`). Each type becomes a non-positional `sealed record` with a nested `Data` record and a `Data` property.

### 1.3 Update TraceEventJsonContext

File: `QuestForge.Adapters/Tracing/TraceEventJsonContext.cs`

Register all 7 `Data` record types.

### 1.4 Update TraceSession

File: `QuestForge.Adapters/Tracing/TraceSession.cs`

- Add `_nextSeq` (long) and `_stopwatch` (Stopwatch?) fields
- In `OpenFileUnderLock`: reset `_nextSeq = 0`, start `_stopwatch`
- In `CloseFileUnderLock`: stop and null `_stopwatch`
- In `Write` and `WriteUnderLock`: stamp `evt with { Seq = (int)_nextSeq++, Ts = _stopwatch?.ElapsedMilliseconds ?? 0 }` before passing to inner writer
- Update debounce key access: `d.Data.StepId`, `d.Data.ActionType` (from `d.StepId`, `d.ActionType`)
- Update `RunStartEvent` dedup: access `evt.RunId` (now on base)
- Update `WriteObservation`: construct `ObservationEvent` with nested `Data`

---

## Task 2 -- Construction site migration (questforge repo)

### 2.1 QuestEngine.cs (~5 sites)

- `EmitRunStartIfNeeded`: `new RunStartEvent { RunId = _runId, Data = new(...) { QuestId = _quest.Id, SchemaVer = "1.0" } }`
- `Tick` terminal actions: `new RunEndEvent { RunId = _runId, Data = new() { Outcome = "done" } }`
- `Tick` decision events: `new DecisionEvent { RunId = _runId, Data = new() { StepId = stepId, ActionType = action.GetType().Name } }`

### 2.2 EngineHost.cs (~5 sites)

- `BeginRun`: `RunStartEvent` with `PluginVer`, `PatchVer`, `WallClockUtc`
- `EndRun`: `RunEndEvent`
- `EmitActionSubmitted`: `ActionSubmittedEvent`
- `EmitActionCompleted`: `ActionCompletedEvent`

### 2.3 AuthoringHost.cs (~8 sites)

- `RunStartEvent`, `RunEndEvent`, `ActionSubmittedEvent`, `ActionCompletedEvent`, `StepRecordedEvent` construction

### 2.4 EngineTestHarness.cs (~20 sites)

- All `EmitActionSubmitted`/`EmitActionCompleted` calls
- `RunStartEvent`, `RunEndEvent` in harness setup

### 2.5 Test files (~50 sites across TraceEventTests, TraceWriterTests, TraceSessionTests, CombatFixtureTests, TraceReplayFixtureStateTests, QuestVariablesTraceEngineTests)

All `new RunStartEvent(...)`, `new DecisionEvent(...)`, etc. change to object initializer syntax with nested `Data`.

---

## Task 3 -- Tools repo migration

### 3.1 TraceEventParser.cs

- The STJ deserialization path works automatically with the new types (they define the `data` sub-object shape)
- The heuristic fallback (`DetectAndDeserialize`) needs updating: the flat-field heuristics (checking for `questId`, `method`, `stepId` at the top level) must check inside `data` instead
- Add a guard: if `root.TryGetProperty("data", out _)` succeeds, use the polymorphic context path directly; the heuristic is only for traces WITHOUT a `type` discriminator AND without `data` (legacy/test)
- Add a deprecation warning for old-shape traces

### 3.2 SnapshotState.cs

- All reads of `ObservationEvent.Method`, `.Argument`, `.Value` change to `.Data.Method`, `.Data.Argument`, `.Data.Value`
- All reads of `DecisionEvent.ActionType`, `.StepId` change to `.Data.ActionType`, `.Data.StepId`
- All reads of `ActionSubmittedEvent.ActionType`, `.Parameters` change to `.Data.ActionType`, `.Data.Parameters`
- All reads of `ActionCompletedEvent.ActionType`, `.Outcome` change to `.Data.ActionType`, `.Data.Outcome`

### 3.3 TraceToFixtureExtractor.cs

- `RunStartEvent.QuestId` changes to `.Data.QuestId`
- `RunEndEvent.Outcome` changes to `.Data.Outcome`
- `DecisionEvent.StepId` / `.ActionType` change to `.Data.StepId` / `.Data.ActionType`

### 3.4 TraceToQuestExtractor.cs

- Same pattern as 3.3: all type-specific field accesses go through `.Data`

### 3.5 Tests

- All test traces (inline JSONL strings) must be updated to the new shape
- Test assertions accessing event fields must go through `.Data`

---

## Task 4 -- TRACE_FORMAT.md Known Divergences table update

Update the Known Divergences table at the bottom of `docs/TRACE_FORMAT.md`:

| Spec field / feature | Current behaviour | Status |
|---|---|---|
| `seq` (monotonic sequence number per event) | **Emitted.** `TraceSession` stamps monotonic seq starting at 0. | Reconciled. |
| `ts` (monotonic offset in ms from `run.start`) | **Emitted.** `TraceSession` stamps from `Stopwatch` started at file open. | Reconciled. |
| `data` sub-object wrapper | **Emitted.** All event types use `Data` nested record. | Reconciled. |
| `v` (format version) | **Emitted.** Every event carries `v: 1`. | Reconciled. |
| `run.start` metadata fields (`pluginVer`, `patchVer`, `wallClockUtc`, `engineConfig`, `precedingRunId`, `newGamePlus`) | **Emitted** (partially). `pluginVer` and `patchVer` from EngineHost; `wallClockUtc` from call site; `engineConfig`/`precedingRunId`/`newGamePlus` always null. `dataVer`/`dataHash` still not emitted (no data repo). | Partially reconciled. |
| `runId` in every event | **Emitted** as a base-class property at the envelope level. | Reconciled. |

---

## Given-When-Then specifications

### TS1: RunStartEvent serialization shape

**Given** a `RunStartEvent` with `RunId="abc"`, `V=1`, `Seq=0`, `Ts=0`, `Data.QuestId=65`, `Data.SchemaVer="1.0"`, `Data.PluginVer="0.4.1"`, `Data.PatchVer="7.51"`, `Data.WallClockUtc=2026-06-02T12:00:00Z`
**When** serialized via `TraceEventJsonContext.Default.TraceEvent`
**Then** the JSON string contains:
- `"v":1` at the top level
- `"seq":0` at the top level
- `"ts":0` at the top level
- `"type":"run.start"` at the top level
- `"runId":"abc"` at the top level
- `"data":{` containing `"questId":65`
- `"data":{` containing `"pluginVer":"0.4.1"`
- `"data":{` containing `"patchVer":"7.51"`
- The string does NOT contain a top-level `"questId"` (it must be inside `data`)
- The string does NOT contain `"at":` (removed)

### TS2: RunEndEvent serialization shape

**Given** a `RunEndEvent` with `RunId="abc"`, `Data.Outcome="done"`
**When** serialized
**Then** JSON contains `"type":"run.end"`, `"runId":"abc"`, `"data":{"outcome":"done"}`

### TS3: ObservationEvent serialization shape

**Given** an `ObservationEvent` with `RunId="abc"`, `Data.Method="GetPlayerZone"`, `Data.Value=130` (as JsonElement)
**When** serialized
**Then** JSON contains `"type":"observation"`, `"runId":"abc"`, `"data":{"method":"GetPlayerZone",...,"value":130}`
- Does NOT contain a top-level `"method"` key

### TS4: DecisionEvent serialization shape

**Given** a `DecisionEvent` with `RunId="abc"`, `Data.StepId="go-to-npc"`, `Data.ActionType="Navigate"`
**When** serialized
**Then** JSON contains `"type":"decision"`, `"runId":"abc"`, `"data":{"stepId":"go-to-npc","actionType":"Navigate"}`

### TS5: DecisionEvent with null StepId

**Given** a `DecisionEvent` with `Data.StepId = null`
**When** serialized
**Then** JSON contains `"data":{"stepId":null,"actionType":"..."}` (null serialized inside data)

### TS6: ActionSubmittedEvent serialization shape

**Given** an `ActionSubmittedEvent` with `Data.ActionType="Navigate"`, `Data.Parameters={"x":10}` (JsonElement)
**When** serialized
**Then** JSON contains `"type":"action.submitted"`, `"data":{"actionType":"Navigate","parameters":{"x":10}}`

### TS7: ActionCompletedEvent serialization shape

**Given** an `ActionCompletedEvent` with `Data.ActionType="Navigate"`, `Data.Outcome="Arrived"`
**When** serialized
**Then** JSON contains `"type":"action.completed"`, `"data":{"actionType":"Navigate","outcome":"Arrived"}`

### TS8: StepRecordedEvent serialization shape

**Given** a `StepRecordedEvent` with `Data.StepId="talk-npc"`, `Data.SequenceNumber=0`, `Data.Step=<JsonElement>`
**When** serialized
**Then** JSON contains `"type":"step.recorded"`, `"data":{"stepId":"talk-npc","sequenceNumber":0,"step":{...}}`

### TS9: Round-trip deserialization for all 7 types

**Given** each of the 7 event types constructed and serialized via `TraceEventJsonContext.Default.TraceEvent`
**When** deserialized back via the same context
**Then** the resulting object matches the original (record equality), and the concrete type is correct (`Assert.IsType`)

### TS10: Envelope field ordering in JSON

**Given** a `DecisionEvent` with `V=1`, `Seq=5`, `Ts=123`
**When** serialized
**Then** the JSON string starts with `{"v":1,"seq":5,"ts":123,"type":"decision","runId":"...","data":{` (envelope fields appear before data)

### TS11: TraceSession stamps seq monotonically

**Given** a `TraceSession` in `QuestRun` mode with `OnQuestRunStart` called
**When** 5 events are written (RunStart, Observation, Decision, ActionSubmitted, ActionCompleted)
**Then** the inner `FakeTraceWriter` receives 5 events with `Seq` values 0, 1, 2, 3, 4 respectively

### TS12: TraceSession stamps ts non-negatively

**Given** a `TraceSession` in `QuestRun` mode with `OnQuestRunStart` called
**When** 3 events are written with a small delay between them
**Then** all events have `Ts >= 0` and `Ts` values are non-decreasing

### TS13: TraceSession resets seq on file reopen

**Given** a `TraceSession` in `QuestRun` mode:
- `OnQuestRunStart(q)` -> write 3 events (seq 0,1,2) -> `OnQuestRunEnd()`
- `OnQuestRunStart(q)` -> write 2 events
**Then** the second file's events have `Seq` values 0, 1 (reset on new file)

### TS14: TraceSession stamps events passed via WriteObservation

**Given** a `TraceSession` with file open
**When** `WriteObservation(...)` is called
**Then** the inner writer receives an `ObservationEvent` with `Seq > 0` (if not the first event) and `Ts >= 0`

### TS15: Decision debounce with new Data shape

**Given** a `TraceSession` with file open
**When** two identical `DecisionEvent`s are written (same `RunId`, same `Data.StepId`, same `Data.ActionType`)
**Then** only one reaches the inner writer (debounce works with `Data` accessor)

### TS16: Decision debounce -- different StepId in Data

**Given** a `TraceSession` with file open
**When** `DecisionEvent` with `Data.StepId="a"` then `DecisionEvent` with `Data.StepId="b"` (same ActionType)
**Then** both reach the inner writer

### TS17: RunStartEvent dedup with base RunId

**Given** a `TraceSession` with file open
**When** two `RunStartEvent`s with the same `RunId` are written
**Then** only one reaches the inner writer

### TS18: run.start metadata fields present

**Given** a `RunStartEvent` constructed with `PluginVer="0.4.1"`, `PatchVer="7.51"`, `WallClockUtc=2026-06-02T12:00:00Z`, `EngineConfig=null`, `PrecedingRunId=null`, `NewGamePlus=null`
**When** serialized
**Then** JSON `data` object contains all 7 fields: `questId`, `schemaVer`, `pluginVer`, `patchVer`, `wallClockUtc`, `engineConfig`, `precedingRunId`, `newGamePlus` (nullable ones serialized as `null`)

### TS19: run.start metadata with null optional fields

**Given** a `RunStartEvent` with only `QuestId` and `SchemaVer` set (all others null/default)
**When** serialized
**Then** JSON `data` object contains `"pluginVer":null`, `"engineConfig":null`, etc.

### TS20: V defaults to 1

**Given** a `DecisionEvent` constructed with default `V` (not explicitly set)
**When** serialized
**Then** JSON contains `"v":1`

### TS21: TraceWriter byte cap applies to new shape

**Given** an `ObservationEvent` whose serialized form (with envelope + data wrapper) exceeds 4096 bytes
**When** `TraceWriter.Write` is called
**Then** `InvalidOperationException` is thrown containing "4096-byte cap"

### TS22: Concurrent writes still produce valid events with stamped seq

**Given** a `TraceSession` in `Always` mode with 50 concurrent `Write` calls
**When** all complete
**Then** the inner writer received 50 events, and `Seq` values are a permutation of 0..49 (no duplicates, no gaps)

### TS23: RunEndEvent bypass still works with new shape

**Given** a `TraceSession` in `Recording` mode with gate closed (after `OnConfirmRecordStep`) but file open
**When** `Write(new RunEndEvent { RunId = "r", Data = new() { Outcome = "done" } })` is called
**Then** the event reaches the inner writer (bypass still works)

### TS24: Observation dedup works with nested Data

**Given** a `TraceSession` with file open
**When** `WriteObservation` is called twice with the same `(method, argument, value)`
**Then** only one `ObservationEvent` reaches the inner writer

---

## Implementation order

### Phase A -- Type definitions (estimated: 3-4 hours)

1. Refactor `TraceEvent.cs`: remove `At`, add `V`/`Seq`/`Ts`/`RunId`
2. Refactor all 7 derived event types: add nested `Data` records, remove positional params
3. Update `TraceEventJsonContext.cs` with all `Data` type registrations
4. **Gate: project compiles** (it won't yet -- construction sites are broken, but the type changes are complete)

Done-before-next: All type files compile in isolation (ignoring downstream compilation errors).

### Phase B -- TraceSession stamping (estimated: 2-3 hours)

1. Add `_nextSeq`, `_stopwatch` fields to `TraceSession`
2. Wire into `OpenFileUnderLock` (reset seq, start stopwatch)
3. Wire into `CloseFileUnderLock` (stop stopwatch)
4. Stamp in `Write` and `WriteUnderLock` with `evt with { Seq = ..., Ts = ... }`
5. Update debounce key access: `d.Data.StepId`, `d.Data.ActionType`
6. Update `RunStartEvent` dedup: `evt.RunId`
7. Update `WriteObservation` to construct `ObservationEvent` with nested `Data`

Done-before-next: `TraceSession` compiles.

### Phase C -- Construction site migration (estimated: 4-6 hours)

1. `QuestEngine.cs` (~5 sites)
2. `EngineHost.cs` (~5 sites)
3. `AuthoringHost.cs` (~8 sites)
4. `EngineTestHarness.cs` (~20 sites)
5. All test files (~50 sites)
6. Any remaining compilation errors

Done-before-next: `dotnet build` succeeds for the entire solution.

### Phase D -- Tests (estimated: 4-6 hours)

1. Rewrite `TraceEventTests.cs` for new shapes (TS1-TS10, TS18-TS21)
2. Rewrite `TraceWriterTests.cs` for new shapes (TS21)
3. Rewrite `TraceSessionTests.cs` for new shapes (TS11-TS17, TS22-TS24)
4. Update `CombatFixtureTests.cs`, `TraceReplayFixtureStateTests.cs`, `QuestVariablesTraceEngineTests.cs`
5. `dotnet test` green for all engine tests

Done-before-next: All tests pass.

### Phase E -- TRACE_FORMAT.md update (estimated: 30 min)

1. Update Known Divergences table (Task 4)

### Phase F -- Tools repo (paired PR) (estimated: 3-4 hours)

1. `TraceEventParser.cs` -- update for new shape
2. `SnapshotState.cs` -- `.Data.Field` access pattern
3. `TraceToFixtureExtractor.cs` -- `.Data.Field` access pattern
4. `TraceToQuestExtractor.cs` -- `.Data.Field` access pattern
5. Update all test traces and assertions
6. `dotnet test` green for tools repo

Done-before-next: Both repos pass CI.

---

## Done criteria

1. `dotnet build` succeeds for the `questforge` repo with zero warnings (TreatWarningsAsErrors)
2. `dotnet test QuestForge.Engine.Tests` passes all tests including updated trace tests
3. `dotnet test QuestForge.Adapters.Tests` passes (if any trace-related tests exist there)
4. Serialized trace events match the spec section 4 shape: `{"v":1,"seq":N,"ts":N,"type":"...","runId":"...","data":{...}}`
5. `run.start` events include `pluginVer`, `patchVer`, `wallClockUtc`, `engineConfig`, `precedingRunId`, `newGamePlus` in the `data` sub-object
6. `seq` is monotonically increasing from 0 within each trace file
7. `ts` is non-negative and sourced from a monotonic clock (Stopwatch)
8. TRACE_FORMAT.md Known Divergences table updated -- `seq`, `ts`, `v`, `data` wrapper rows marked "Reconciled"
9. Tools repo (`questforge-tools`) paired PR: `dotnet test QuestForge.Tools.Trace.Tests` passes with the new event shape
10. Old-shape traces are explicitly not supported (no backwards compatibility shim)

---

## What this plan does NOT include

- **`dataVer` and `dataHash` fields** -- per user decision, deferred until data repo integration
- **`EngineDecisionConfig` type definition** -- `engineConfig` is serialized as `null` for now; the type will be defined when replay determinism requires it
- **`precedingRunId` tracking** -- always `null`; requires session-level run history which is not yet implemented
- **`newGamePlus` detection** -- always `null`; requires NG+ state detection which is out of scope
- **Observation event `node` field** -- the spec section 5.2 shows `"node":"go-to-momodi"` inside observation data; the current implementation uses `"method"` instead; this is a separate schema evolution item
- **Exceptional event types** (recovery.triggered, adapter.error, engine.error, player.died) -- not yet implemented; adding envelope to them is trivial when they are added
- **Diagnostic event types** (dialogue.resolved, reward.selected, etc.) -- same as above
- **`qf-trace validate` enforcement of new shape** -- the validator will be updated separately when it is built
- **Backwards-compatible reading of old traces** -- old traces will break; re-recording is planned
- **`run.end` expanded metadata** (`durationMs`, `stepsCompleted`, `recoveriesTriggered`) -- the current `RunEndData` only carries `Outcome`; expanding it is a separate item

---

## File-by-file change list

### questforge repo

| File | Change |
|---|---|
| `QuestForge.Adapters/Tracing/TraceEvent.cs` | Remove `At` param; add `V`, `Seq`, `Ts`, `RunId` init props with `[JsonPropertyOrder]` |
| `QuestForge.Adapters/Tracing/RunStartEvent.cs` | Non-positional record; nested `RunStartData` with expanded metadata |
| `QuestForge.Adapters/Tracing/RunEndEvent.cs` | Non-positional record; nested `RunEndData` |
| `QuestForge.Adapters/Tracing/ObservationEvent.cs` | Non-positional record; nested `ObservationData` |
| `QuestForge.Adapters/Tracing/DecisionEvent.cs` | Non-positional record; nested `DecisionData` |
| `QuestForge.Adapters/Tracing/ActionSubmittedEvent.cs` | Non-positional record; nested `ActionSubmittedData` |
| `QuestForge.Adapters/Tracing/ActionCompletedEvent.cs` | Non-positional record; nested `ActionCompletedData` |
| `QuestForge.Adapters/Tracing/StepRecordedEvent.cs` | Non-positional record; nested `StepRecordedData` |
| `QuestForge.Adapters/Tracing/TraceEventJsonContext.cs` | Register all 7 `Data` types |
| `QuestForge.Adapters/Tracing/TraceSession.cs` | Add `_nextSeq`/`_stopwatch`; stamp in Write/WriteUnderLock; update debounce key access; update WriteObservation |
| `QuestForge.Adapters/Tracing/TraceWriter.cs` | No structural change (serialization delegates to context) |
| `QuestForge.Adapters/ITraceWriter.cs` | No change |
| `QuestForge.Adapters.Fakes/FakeTraceWriter.cs` | No change (takes `TraceEvent`) |
| `QuestForge.Engine/QuestEngine.cs` | ~5 construction sites updated |
| `QuestForge.Plugin/EngineHost.cs` | ~5 construction sites updated; add `_pluginVersion`/`_patchVersion` for run.start metadata |
| `QuestForge.Plugin/Authoring/AuthoringHost.cs` | ~8 construction sites updated |
| `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` | ~20 construction sites updated |
| `QuestForge.Engine.Tests/Tracing/TraceEventTests.cs` | Full rewrite for new shapes |
| `QuestForge.Engine.Tests/Tracing/TraceWriterTests.cs` | Update construction + assertions |
| `QuestForge.Engine.Tests/Tracing/TraceSessionTests.cs` | Update construction + assertions; add seq/ts stamping tests |
| `QuestForge.Engine.Tests/Combat/CombatFixtureTests.cs` | Update trace event construction |
| `QuestForge.Engine.Tests/Replay/TraceReplayFixtureStateTests.cs` | Update trace event construction + field access |
| `QuestForge.Engine.Tests/Engine/QuestVariablesTraceEngineTests.cs` | Update trace event construction |
| `docs/TRACE_FORMAT.md` | Update Known Divergences table |

### questforge-tools repo

| File | Change |
|---|---|
| `QuestForge.Tools.Trace/Parsing/TraceEventParser.cs` | Update for `data` sub-object; simplify heuristic fallback |
| `QuestForge.Tools.Trace/SnapshotState.cs` | `.Data.Method` / `.Data.Value` / etc. access pattern |
| `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs` | `.Data.QuestId` / `.Data.Outcome` / `.Data.StepId` / `.Data.ActionType` |
| `QuestForge.Tools.Trace/Quest/TraceToQuestExtractor.cs` | `.Data.Method` / `.Data.StepId` / `.Data.ActionType` / etc. |
| `QuestForge.Tools.Trace.Tests/*` | Update all inline JSONL + field assertions |

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Task TS1-TS24.
- Happy paths: 10 scenarios (TS1-TS8, TS18, TS20)
- Edge cases: 8 scenarios (TS5, TS10, TS13, TS14, TS19, TS22, TS23, TS24)
- Error cases: 1 scenario (TS21)
- Structural/round-trip: 5 scenarios (TS9, TS11, TS12, TS15-TS17)
- Expected total: ~35 tests in QuestForge.Engine.Tests (some TS scenarios map to multiple xUnit test methods, e.g. TS9 = 7 round-trip tests)
