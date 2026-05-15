# Phase 5 Implementation Plan: Trace Recorder + Recording Proxy

**Status:** ready to implement
**Input docs:** docs/TRACE_FORMAT.md (full spec), docs/NEXT_STEPS.md §Phase 5, docs/DESIGN.md §5/§10, docs/ARCHITECTURE.md (Trace & Replay diagram)
**Output:** running the Phase 4 quest test produces a readable JSONL trace file containing the minimal six event types.
**Predecessor:** Phase 4 complete — `QuestEngine`, `PredicateEvaluator`, `ExpectEvaluator`, `EngineAction` shipped; 49 tests green.
**Architect review:** incorporated (2026-05-15)

---

## Goal restated

Phase 5 adds the recording side of the trace subsystem. The engine code from Phase 4 already stores an `ITraceWriter` in `_trace` but never calls `Write`. Phase 5:

1. Replaces the `ITraceWriter.Write(object evt)` stub with a typed event hierarchy (six event kinds).
2. Adds a `TraceWriter` implementation in `QuestForge.Engine` that produces JSONL with crash-safe write semantics.
3. Adds a recording proxy that wraps `IGameStateProvider` and `IQuestState`, emitting `observation` events for every adapter read.
4. Wires the engine to emit `run.start`, `decision`, and `run.end` events.
5. Updates the test harness so that `action.submitted` / `action.completed` are emitted around adapter calls the harness executes on behalf of the engine.

Replay (reading traces back) is **out of scope** — that is Phase 7. Phase 5 produces files; Phase 7 consumes them.

---

## Dependency graph

One repo, strict project build order:

```
QuestForge.Adapters          ← TraceEvent hierarchy, updated ITraceWriter
   └── consumed by ↓
QuestForge.Engine            ← TraceWriter (file/stream impl), engine emits run.start/decision/run.end
   └── consumed by ↓
QuestForge.Engine.Tests      ← test harness emits action.submitted/.completed, asserts trace content
QuestForge.Adapters.Fakes    ← FakeTraceWriter updated to new Write signature
QuestForge.Adapters.Tests    ← uses recording proxy over a FakeGameStateProvider
```

The recording proxy lives in **`QuestForge.Adapters.Fakes`** (alongside other in-memory adapter implementations used by tests and the test harness). The production proxy used by the Dalamud plugin is identical code; in Phase 6 we will hoist it into `QuestForge.Adapters` proper if needed. For Phase 5, keeping it next to the other fakes lets tests construct it freely.

**Build order:** Adapter types first (event hierarchy + ITraceWriter signature change) → Engine impl (TraceWriter) → Recording proxy → engine integration → harness integration.

---

## Architectural decisions (read before coding)

### 1. The event hierarchy lives in `QuestForge.Adapters`, not `QuestForge.Engine`

`TraceEvent` and its sealed subtypes go in `QuestForge.Adapters` because:

- `ITraceWriter` is already there.
- The recording proxy (which lives outside the engine) needs to construct `ObservationEvent` without referencing the engine.
- The engine references `QuestForge.Adapters` but the adapters layer never references the engine (architectural invariant).

```csharp
// QuestForge.Adapters/Tracing/TraceEvent.cs
namespace QuestForge.Adapters.Tracing;

public abstract record TraceEvent(string Type, DateTimeOffset At);
```

Each of the six subtypes is `sealed` and lives in the same namespace.

### 2. `ITraceWriter.Write` becomes typed; this is a breaking change

```csharp
// before (Phase 4 stub)
public interface ITraceWriter { void Write(object evt); }

// after (Phase 5)
public interface ITraceWriter { void Write(TraceEvent evt); }
```

**Call sites affected:**
- `_trace.Write(...)` in `QuestEngine` — currently zero call sites; new calls written in Phase 5.
- `NullTraceWriter` in `QuestForge.Engine.Tests/Fakes/` — signature update only, body remains empty.
- `FakeTraceWriter` in `QuestForge.Adapters.Fakes/FakeTraceWriter.cs` — change `List<object>` to `List<TraceEvent>` and update method signature. The lock pattern and accessors remain.

No quest data, no schema, no other adapter is affected.

### 3. The concrete `TraceWriter` implementation lives in `QuestForge.Engine`

The file-writing and JSONL-formatting class is the real implementation of `ITraceWriter`. It belongs in `QuestForge.Engine` because:

- It has no Dalamud dependency. It opens a `Stream` and writes UTF-8 bytes.
- The engine project is where infrastructure that the engine owns lives.
- The plugin layer (Phase 6) will construct it with a `FileStream` over the trace directory; Phase 5 tests construct it with a `MemoryStream`.

```csharp
// QuestForge.Engine/Tracing/TraceWriter.cs
namespace QuestForge.Engine.Tracing;

public sealed class TraceWriter : ITraceWriter, IDisposable { ... }
```

### 4. The recording proxy is a hand-written decorator, not source-generated

Two reasons to keep it hand-written for Phase 5:

1. The proxy needs to know **what** to record. Per `TRACE_FORMAT.md` §7.1, the recording proxy enforces an allowlist; the privacy guarantee is "the proxy refuses to record" excluded fields. Hand-writing each method ensures that allowlist is reviewable. (The full allowlist enforcement work is Phase 8+; Phase 5 records everything the two interfaces return, but the *shape* — one method, one observation — is set now.)
2. A source-generator adds a build-time surface we do not need before Phase 6.

```csharp
// QuestForge.Adapters.Fakes/Recording/RecordingGameStateProvider.cs
public sealed class RecordingGameStateProvider : IGameStateProvider
{
    private readonly IGameStateProvider _inner;
    private readonly ITraceWriter _trace;
    private readonly TimeProvider _clock;

    public RecordingGameStateProvider(IGameStateProvider inner, ITraceWriter trace, TimeProvider? clock = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        var result = await _inner.GetPlayerZone(ct);
        _trace.Write(new ObservationEvent(nameof(GetPlayerZone), Argument: null, Value: SerializableValue(result), _clock.GetUtcNow()));
        return result;
    }
    // ... one method per IGameStateProvider member; same shape
}
```

`RecordingQuestState` is the same pattern over `IQuestState`.

**Why these two interfaces and not the others?** Per `TRACE_FORMAT.md` §5.2, observations record what the engine *read from game state* during a tick. Action-issuing adapters (`INavigator`, `IInteractor`, etc.) are recorded by `action.submitted` / `action.completed` events, not observations. The split is deliberate: reads become observations, writes become action events. Phase 5 wraps only `IGameStateProvider` and `IQuestState`; the other adapters are not wrapped by the proxy.

### 5. The engine emits `decision`; the harness emits `action.submitted` / `action.completed`

Per `TRACE_FORMAT.md` §5.3, action lifecycle events are emitted by **the engine** in the real system. In Phase 5 the engine returns an `EngineAction` but does not itself call the adapter — the plugin's outer loop calls the adapter and reports completion. For Phase 5, the test harness (`EngineTestHarness.RunToCompletion`) plays the plugin's role:

- The engine emits `decision` immediately before returning an action from `Tick`.
- The harness emits `action.submitted` before calling the adapter and `action.completed` after the adapter call returns.

Phase 6 moves the action.submitted/completed emission into a real plugin loop. The split is documented in code comments.

**Why not have the engine emit action.submitted itself?** The engine does not actually submit the action — it returns it. Emitting "submitted" when the action has only been *proposed* would be incorrect per the spec ("`action.submitted` is emitted when the plugin layer calls the adapter").

### 6. `BeginRun(string runId)` is a new method alongside `StartQuest`

`StartQuest(quest)` sets the quest definition. `BeginRun(runId)` sets the run context and triggers `run.start` emission on the next `Tick`. The methods are independent:

- Tests that do not care about traces call `StartQuest` only — no run.start is emitted (today's behavior).
- Tests that do care call both `StartQuest(quest)` and `BeginRun(runId)`.

```csharp
public sealed class QuestEngine
{
    public void StartQuest(QuestDefinition quest) { ... }
    public void BeginRun(string runId)
    {
        if (string.IsNullOrEmpty(runId)) throw new ArgumentException(...);
        _runId = runId;
        _runStartEmitted = false; // emitted on next Tick that produces a decision
    }
}
```

**Rationale:**
- Keeps the Phase 4 method surface unchanged for existing tests.
- The engine has no `RunId` field today; adding it via `BeginRun` is additive.
- `run.start` is emitted lazily on the first `Tick` after `BeginRun`, so the engine can capture meaningful state in `wallClockUtc` rather than a stale value from before the first observation.

**Calling `BeginRun` without `StartQuest` first:** the next `Tick` returns the same `AwaitUser("no quest loaded")` it returns today, and **no events** are emitted. `run.start` requires a known `questId`, which only exists after `StartQuest`.

### 7. Crash-safety: flush per event, single-line writes

Per `TRACE_FORMAT.md` §9.1:

- Open with `FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)` for production; `MemoryStream` in tests.
- Wrap in `StreamWriter` with `AutoFlush = false`. Manual flush per event gives more control than `AutoFlush`.
- Per write: serialize event to a `StringBuilder`, append `\n`, write in a single call, call `Flush()`.
- Hard cap: the serialized event must be ≤ 4096 bytes. Exceeding the cap throws `InvalidOperationException` (a recorder bug per spec).

```csharp
public void Write(TraceEvent evt)
{
    var json = JsonSerializer.Serialize<TraceEvent>(evt, TraceEventJsonContext.Default.TraceEvent);
    if (json.Length > MaxEventBytes)
        throw new InvalidOperationException($"Trace event exceeds 4096-byte cap: {json.Length} bytes for {evt.Type}");
    lock (_writeLock)
    {
        _writer.Write(json);
        _writer.Write('\n');
        _writer.Flush();
        _stream.Flush();
    }
}
```

The double flush (`_writer.Flush()` then `_stream.Flush()`) ensures bytes hit the underlying stream before the next event begins. For `FileStream`, this is enough for the crash-recovery contract in §9.2 (mid-write crashes leave at most a single partial trailing line, discarded silently on read).

### 8. `ObservationEvent` serialization for `Result<T>`

`ObservationEvent.Value` holds `JsonElement?` — a pre-serialized fragment of the adapter call's return value. Most adapter methods return `Task<Result<T>>`. The proxy unwraps and pre-serializes the result before storing it in the event (see §3.4 for why `JsonElement?` rather than `object?`):

```csharp
private static JsonElement? SerializableValue<T>(Result<T> result) => result switch
{
    Result<T>.Success { Value: var v } => JsonSerializer.SerializeToElement(v, _jsonOpts),
    Result<T>.Failure f               => JsonSerializer.SerializeToElement(new { failure = f.Reason, detail = f.Detail }, _jsonOpts),
    _                                  => null
};
```

On Failure, the observation records `{"failure": "reason", "detail": "..."}` — the actual game-state value is unavailable, so a failure marker is recorded instead. This is enough for replay to detect "the engine queried state when the recording had a failure" mismatches in Phase 7.

### 9. JSON source-generation context for trace events

Add a source-gen context for the event hierarchy. This prevents reflection at write time, keeps the writer hot path tight, and surfaces missing registrations at build time:

```csharp
// QuestForge.Adapters/Tracing/TraceEventJsonContext.cs
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartEvent),         "run.start")]
[JsonDerivedType(typeof(RunEndEvent),           "run.end")]
[JsonDerivedType(typeof(ObservationEvent),      "observation")]
[JsonDerivedType(typeof(DecisionEvent),         "decision")]
[JsonDerivedType(typeof(ActionSubmittedEvent),  "action.submitted")]
[JsonDerivedType(typeof(ActionCompletedEvent),  "action.completed")]
public abstract record TraceEvent(string Type, DateTimeOffset At);

[JsonSerializable(typeof(TraceEvent))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]                       // single-line per event
public partial class TraceEventJsonContext : JsonSerializerContext { }
```

**Note:** `WriteIndented = false` is required — JSONL is line-oriented and indented JSON would span multiple lines. This is the inverse of `QuestForgeJsonContext`'s `WriteIndented = true` setting; the two contexts have different purposes.

The discriminator property is `type` per `TRACE_FORMAT.md` §4 (top-level `type` field).

### 10. Phase 5 emits a minimal `run.start` shape

Per scope: `EngineDecisionConfig` is not built in Phase 5. The `run.start` event records only the fields the engine can produce today:

| Field | Value in Phase 5 |
|-------|------------------|
| `runId` | from `BeginRun` |
| `questId` | from `_quest.Id` |
| `questSchemaId` | from `_quest.Id` (alias — the schema does not have a separate schemaId today) |
| `wallClockUtc` | `DateTimeOffset.UtcNow` at first tick after BeginRun |
| (everything else) | omitted — fields like `pluginVer`, `dataHash`, `engineConfig` are filled in Phase 6+ |

The handoff to Phase 6/7 is documented inline: the C# record carries optional properties for the spec fields, which serialize as `null` and are populated as later phases add the data.

### 11. `seq` and `ts` are deferred to Phase 7

Per `TRACE_FORMAT.md` §4, every event has `v`, `seq`, `ts`, `type`, `runId`, `data`. Phase 5 emits:

- `type`: required, present.
- `runId`: required, present (from `BeginRun`).
- `v`: hard-coded `1` on every event.
- `at` (DateTimeOffset, not the spec's monotonic offset): present. `ts` in the spec is monotonic from `run.start`; Phase 5 records absolute UTC and lets Phase 7 derive `ts` if needed. The `at` field is at the `TraceEvent` base record level.
- `seq`: **not emitted in Phase 5**. Sequencing is implicit by line order in the file. Phase 7 adds `seq` once replay actually depends on it; until then, line order is sufficient.
- `data`: the typed payload is the rest of the record's properties, flattened at the JSON top level (not wrapped in a `data` sub-object as the spec example shows).

**Spec divergence to note:** Phase 5 trace output is **structurally simpler** than the spec example in `TRACE_FORMAT.md` §A. It is a strict subset — no `seq`, no `data` wrapper, no `ts` offset. Phase 7's first job before wiring replay is to reconcile: either bump the recorder to spec-shape or bump the spec to record-shape. Documented as a known divergence in `docs/TRACE_FORMAT.md` once Phase 5 lands (out of scope for this plan to update the spec).

The minimal shape that drops out of Phase 5:

```json
{"type":"run.start","at":"2026-05-15T12:34:56.789Z","runId":"a1b2c3d4","questId":66130,"questSchemaId":66130}
{"type":"observation","at":"2026-05-15T12:34:56.801Z","runId":"a1b2c3d4","method":"GetPlayerZone","value":182}
{"type":"decision","at":"2026-05-15T12:34:56.812Z","runId":"a1b2c3d4","stepId":"travel-to-wymond","actionType":"Navigate"}
{"type":"action.submitted","at":"2026-05-15T12:34:56.820Z","runId":"a1b2c3d4","actionType":"Navigate","parameters":{"x":35.56,"y":4.0,"z":-151.18}}
{"type":"action.completed","at":"2026-05-15T12:34:57.401Z","runId":"a1b2c3d4","actionType":"Navigate","outcome":"Arrived"}
...
{"type":"run.end","at":"2026-05-15T12:35:02.108Z","runId":"a1b2c3d4","outcome":"done"}
```

### 12. The engine must not crash if trace writes throw

The engine's responsibility is producing correct decisions. If a trace write fails (disk full, stream closed by test cleanup, etc.), the engine logs the error and continues. The recorder is best-effort from the engine's perspective.

```csharp
private void TraceSafe(TraceEvent evt)
{
    try { _trace.Write(evt); }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Trace write failed for event {Type}; continuing without trace", evt.Type);
    }
}
```

All engine-side `_trace.Write(...)` calls go through `TraceSafe`. The recording proxy makes the same guarantee — a write failure inside the proxy does not propagate to the engine reading state.

---

## Task 1 — `QuestForge.Adapters`: event hierarchy + ITraceWriter

### 1.1 New folder: `QuestForge.Adapters/Tracing/`

Files added:

```
QuestForge.Adapters/Tracing/
├── TraceEvent.cs                  ← abstract base + JsonPolymorphic
├── RunStartEvent.cs               ← sealed record
├── RunEndEvent.cs                 ← sealed record
├── ObservationEvent.cs            ← sealed record
├── DecisionEvent.cs               ← sealed record
├── ActionSubmittedEvent.cs        ← sealed record
├── ActionCompletedEvent.cs        ← sealed record
└── TraceEventJsonContext.cs       ← source-gen context
```

### 1.2 Type definitions

```csharp
// TraceEvent.cs
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tracing;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartEvent),        "run.start")]
[JsonDerivedType(typeof(RunEndEvent),          "run.end")]
[JsonDerivedType(typeof(ObservationEvent),     "observation")]
[JsonDerivedType(typeof(DecisionEvent),        "decision")]
[JsonDerivedType(typeof(ActionSubmittedEvent), "action.submitted")]
[JsonDerivedType(typeof(ActionCompletedEvent), "action.completed")]
public abstract record TraceEvent(string Type, DateTimeOffset At);

// RunStartEvent.cs
public sealed record RunStartEvent(
    string RunId,
    uint QuestId,
    uint QuestSchemaId,
    DateTimeOffset At
) : TraceEvent("run.start", At);

// RunEndEvent.cs
public sealed record RunEndEvent(
    string RunId,
    string Outcome,           // "done" | "awaitUser" | "stopped" — Phase 5 emits "done" or "awaitUser"
    DateTimeOffset At
) : TraceEvent("run.end", At);

// ObservationEvent.cs
public sealed record ObservationEvent(
    string RunId,
    string Method,            // e.g. "GetPlayerZone", "GetQuestSequence"
    JsonElement? Argument,    // null for parameterless reads; serialized argument otherwise
    JsonElement? Value,       // serialized success value, OR {"failure":"…","detail":"…"} on Failure
    DateTimeOffset At
) : TraceEvent("observation", At);

// DecisionEvent.cs
public sealed record DecisionEvent(
    string RunId,
    string? StepId,           // null when engine has no current step (e.g. Done/AwaitUser before any work)
    string ActionType,        // "Navigate" | "Interact" | "Wait" | "AwaitUser" | "Done"
    DateTimeOffset At
) : TraceEvent("decision", At);

// ActionSubmittedEvent.cs
public sealed record ActionSubmittedEvent(
    string RunId,
    string ActionType,        // matches DecisionEvent.ActionType for the same tick
    JsonElement? Parameters,  // for Navigate: serialized WorldPosition; for Interact: NpcId; else null
    DateTimeOffset At
) : TraceEvent("action.submitted", At);

// ActionCompletedEvent.cs
public sealed record ActionCompletedEvent(
    string RunId,
    string ActionType,
    string Outcome,           // free-form: "Arrived" | "Failed: timeout" | "Interacted" etc.
    DateTimeOffset At
) : TraceEvent("action.completed", At);
```

### 1.3 ITraceWriter signature update

```csharp
// QuestForge.Adapters/ITraceWriter.cs
using QuestForge.Adapters.Tracing;

namespace QuestForge.Adapters;

public interface ITraceWriter
{
    void Write(TraceEvent evt);
}
```

### 1.4 NullTraceWriter and FakeTraceWriter updates

```csharp
// QuestForge.Engine.Tests/Fakes/NullTraceWriter.cs
public sealed class NullTraceWriter : ITraceWriter
{
    public void Write(TraceEvent evt) { }
}

// QuestForge.Adapters.Fakes/FakeTraceWriter.cs
public sealed class FakeTraceWriter : ITraceWriter
{
    private readonly List<TraceEvent> _events = new();
    private readonly object _lock = new();

    public void Write(TraceEvent evt) { lock (_lock) _events.Add(evt); }
    public IReadOnlyList<TraceEvent> RecordedEvents { get { lock (_lock) return _events.ToArray(); } }
    public int Count { get { lock (_lock) return _events.Count; } }
    public void Reset() { lock (_lock) _events.Clear(); }
}
```

**Mandatory gate before writing any TraceWriter or proxy code:** the solution must build green with the new signature. All existing Phase 4 tests must still pass (49 tests, none of which exercise `Write`).

---

## Task 2 — `QuestForge.Engine`: TraceWriter implementation

### 2.1 New folder: `QuestForge.Engine/Tracing/`

```
QuestForge.Engine/Tracing/
└── TraceWriter.cs
```

### 2.2 TraceWriter contract

```csharp
public sealed class TraceWriter : ITraceWriter, IDisposable
{
    private const int MaxEventBytes = 4096;
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private readonly bool _ownsStream;
    private bool _disposed;

    /// <summary>Construct over an arbitrary stream. Used by tests with MemoryStream.</summary>
    public TraceWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: leaveOpen)
        {
            NewLine = "\n"
        };
        _ownsStream = !leaveOpen;
    }

    /// <summary>Open an append-mode file. Used by the plugin layer in Phase 6.</summary>
    public static TraceWriter OpenFile(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new TraceWriter(stream);
    }

    public void Write(TraceEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = JsonSerializer.Serialize(evt, TraceEventJsonContext.Default.TraceEvent);
        if (json.Length > MaxEventBytes)
            throw new InvalidOperationException(
                $"Trace event exceeds {MaxEventBytes}-byte cap: {json.Length} bytes for type '{evt.Type}'");
        lock (_writeLock)
        {
            _writer.Write(json);
            _writer.Write('\n');
            _writer.Flush();
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
        if (_ownsStream) _stream.Dispose();
    }
}
```

### 2.3 Crash-safety semantics covered

- One `Write` call = one `lock` + serialize + write + flush. Concurrent writers serialize through the lock.
- A crash before `_stream.Flush()` returns may leave a partial line (no terminating `\n`). On read, that partial line is discarded silently per `TRACE_FORMAT.md` §9.2.
- A crash between `_writer.Write(json)` and `_writer.Write('\n')` is impossible inside the lock (they are one logical write from the recorder's perspective; the OS may interleave page writes but the partial-line tolerance handles it).
- `TraceWriter` does **not** participate in privacy redaction. That is `qf-trace redact` in Phase 8+.

---

## Task 3 — `QuestForge.Adapters.Fakes`: recording proxies

### 3.1 New folder additions

```
QuestForge.Adapters.Fakes/Recording/
├── AdapterCall.cs                 ← already exists
├── CallLog.cs                     ← already exists
├── RecordingGameStateProvider.cs  ← NEW
└── RecordingQuestState.cs         ← NEW
```

### 3.2 RecordingGameStateProvider

One method per `IGameStateProvider` member. Every method:

1. Calls the inner adapter once.
2. Emits one `ObservationEvent` with `Method = nameof(...)`, `Value = SerializableValue(result)`.
3. Returns the inner result unchanged.

```csharp
public sealed class RecordingGameStateProvider : IGameStateProvider
{
    private readonly IGameStateProvider _inner;
    private readonly ITraceWriter _trace;
    private readonly Func<string?> _runIdAccessor;     // engine assigns runId via BeginRun; proxy reads it lazily
    private readonly TimeProvider _clock;

    public RecordingGameStateProvider(
        IGameStateProvider inner,
        ITraceWriter trace,
        Func<string?> runIdAccessor,
        TimeProvider? clock = null) { ... }

    public async Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
    {
        var result = await _inner.GetPlayerZone(ct);
        Record(nameof(GetPlayerZone), argument: null, result);
        return result;
    }

    public async Task<Result<int>> GetJobLevel(JobId job, CancellationToken ct)
    {
        var result = await _inner.GetJobLevel(job, ct);
        Record(nameof(GetJobLevel), argument: job, result);
        return result;
    }
    // ... 28 more methods, same shape
}
```

**Why `Func<string?>` for runId rather than capturing it at construction?** The proxy is constructed once when the engine is wired (in tests, in the harness; in Phase 6, in the plugin). `BeginRun(runId)` is called *later*, on each new run. The proxy reads the current runId from the engine each time it emits an event. If `BeginRun` has not been called yet, `runIdAccessor()` returns `null`; the event still serializes (`runId` is a string property, nullable in the record), and Phase 7's replay handles trailing-pre-BeginRun observations as expected.

### 3.3 RecordingQuestState

Identical pattern for `IQuestState`:

```csharp
public sealed class RecordingQuestState : IQuestState
{
    public async Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        var result = await _inner.GetQuestSequence(quest, ct);
        Record(nameof(GetQuestSequence), argument: quest, result);
        return result;
    }
    // ... 9 more methods
}
```

### 3.4 SerializableValue helper

Lives in the same folder as the proxies (private static on each proxy class, sharing a `JsonSerializerOptions` instance):

```csharp
private static readonly JsonSerializerOptions _jsonOpts = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

private static JsonElement? SerializableValue<T>(Result<T> result) => result switch
{
    Result<T>.Success { Value: var v } => JsonSerializer.SerializeToElement(v, _jsonOpts),
    Result<T>.Failure f               => JsonSerializer.SerializeToElement(
                                             new { failure = f.Reason, detail = f.Detail }, _jsonOpts),
    _                                  => null
};
```

**Why `JsonElement?` instead of `object?`:** STJ source-generated contexts must know all serialized types at compile time; `object?` cannot be registered because its concrete type is unknown at code-gen time. `JsonElement` is a STJ-native value type that source-gen handles directly. The proxy does a small reflection-based pre-serialization step here (using `_jsonOpts`), producing a self-contained JSON fragment stored in the event record. When `TraceWriter` then serializes the entire event via the source-gen context, `JsonElement?` is a registered type and serializes without reflection. This is the standard pattern for "opaque typed payload in a source-gen event hierarchy."

---

## Task 4 — `QuestForge.Engine`: emit run.start / decision / run.end

### 4.1 Engine state additions

```csharp
public sealed class QuestEngine
{
    private QuestDefinition? _quest;
    private string? _runId;                  // NEW — set by BeginRun
    private bool _runStartEmitted;           // NEW — gates run.start emission
    private string? _lastDecisionStepId;     // NEW — captured when we walk the step list

    public void StartQuest(QuestDefinition quest)
    {
        _quest = quest ?? throw new ArgumentNullException(nameof(quest));
    }

    public void BeginRun(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId must be non-empty", nameof(runId));
        _runId = runId;
        _runStartEmitted = false;
    }

    public string? CurrentRunId => _runId;   // read by the recording proxy accessor and test harness
}
```

### 4.2 Tick emits decision events

`Tick` emits exactly one `decision` event per call after computing the action, **except** when `_quest is null` and `_runId is null` (both not set → nothing to record). When `_runId` is set but `_quest` is null, the engine returns `AwaitUser("no quest loaded")` but emits no events because there is no questId.

Pseudocode for the modified `Tick`:

```csharp
public async Task<EngineAction> Tick(CancellationToken ct)
{
    if (_quest is null) return new EngineAction.AwaitUser("no quest loaded");

    EmitRunStartIfNeeded();   // emits run.start exactly once per BeginRun

    var (action, stepId) = await ResolveAction(ct);  // refactored from existing body

    TraceSafe(new DecisionEvent(
        RunId: _runId ?? string.Empty,
        StepId: stepId,
        ActionType: action.GetType().Name,
        At: DateTimeOffset.UtcNow));

    if (action is EngineAction.Done)
        TraceSafe(new RunEndEvent(_runId ?? string.Empty, Outcome: "done", DateTimeOffset.UtcNow));

    return action;
}
```

The existing step-walking logic becomes `ResolveAction(ct)` and returns `(action, stepId)` so the decision event records which step produced it.

### 4.3 Tracking step IDs

In Phase 4 the engine walks `matchingBlock.Steps` and returns the first unsatisfied step. Capture `step.Id` at the return site:

```csharp
foreach (var step in matchingBlock.Steps)
{
    if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct)) continue;
    if (step.SkipIf is not null && await _expectEvaluator.Evaluate(step.SkipIf, ct)) continue;
    return (ResolveActionForStep(step), step.Id);
}
return (new EngineAction.Wait("..."), stepId: null);
```

### 4.4 run.end emission rules

- `EngineAction.Done` → emit `run.end` with `Outcome = "done"`. Set `_runStartEmitted = false` so a subsequent `BeginRun` on the same engine instance gets a fresh `run.start`.
- `EngineAction.AwaitUser` after `BeginRun` → emit `run.end` with `Outcome = "awaitUser"`. The engine is yielding control to the user; Phase 5 treats this as terminal for trace purposes.
- `EngineAction.Wait` and other actions → no `run.end`.

**Open design note:** "yielding to the user" is not the same as "the run ended". Phase 5 errs on the side of recording a clear terminus; Phase 6 may revisit by introducing an explicit `EndRun()` method on the engine.

### 4.5 EmitRunStartIfNeeded

```csharp
private void EmitRunStartIfNeeded()
{
    if (_runStartEmitted || _runId is null || _quest is null) return;
    TraceSafe(new RunStartEvent(
        RunId: _runId,
        QuestId: _quest.Id,
        QuestSchemaId: _quest.Id,
        At: DateTimeOffset.UtcNow));
    _runStartEmitted = true;
}
```

---

## Task 5 — `EngineTestHarness`: emit action.submitted / action.completed

### 5.1 Updated harness wiring

```csharp
public sealed class EngineTestHarness
{
    public FakeGameStateProvider GameState { get; }
    public FakeQuestState QuestState { get; }
    // ... unchanged ...
    public FakeTraceWriter TraceWriter { get; } = new FakeTraceWriter();   // CHANGED — was NullTraceWriter
    public QuestEngine Engine { get; }

    public EngineTestHarness()
    {
        GameState = new FakeGameStateProvider();
        QuestState = new FakeQuestState();

        // Wrap with recording proxies. The proxy reads runId from the engine via accessor.
        IGameStateProvider gameStateForEngine = new RecordingGameStateProvider(
            GameState, TraceWriter, () => Engine?.CurrentRunId);
        IQuestState questStateForEngine = new RecordingQuestState(
            QuestState, TraceWriter, () => Engine?.CurrentRunId);

        // ... fakes for navigator/teleporter/interactor wired against the underlying GameState/QuestState ...

        Engine = new QuestEngine(
            gameStateForEngine,
            questStateForEngine,
            Navigator, Teleporter, Interactor, /* ... */,
            TraceWriter,
            NullLogger<QuestEngine>.Instance);
    }
}
```

**Construction-order subtlety:** `Engine` is assigned after the proxy is constructed, so the proxy's `runIdAccessor` lambda captures `this.Engine` lazily. `Engine?.CurrentRunId` returns null until `BeginRun` is called.

### 5.2 RunToCompletion emits action.submitted/.completed

```csharp
public async Task<List<EngineAction>> RunToCompletion(int maxTicks = 10)
{
    var actions = new List<EngineAction>();
    var ct = CancellationToken.None;

    for (var i = 0; i < maxTicks; i++)
    {
        var action = await Engine.Tick(ct);
        switch (action)
        {
            case EngineAction.Done:
                return actions;

            case EngineAction.AwaitUser au:
                throw new InvalidOperationException($"Engine returned AwaitUser unexpectedly at tick {i + 1}: {au.Reason}");

            case EngineAction.Navigate nav:
                actions.Add(action);
                EmitActionSubmitted("Navigate", nav.Destination);
                var navResult = await Navigator.NavigateTo(nav.Destination, nav.Options, ct);
                EmitActionCompleted("Navigate", DescribeNavOutcome(navResult));
                break;

            case EngineAction.Interact interact:
                actions.Add(action);
                EmitActionSubmitted("Interact", interact.Target);
                var interactResult = await Interactor.InteractWith(interact.Target, ct);
                EmitActionCompleted("Interact", DescribeInteractOutcome(interactResult));
                break;

            case EngineAction.Wait:
                break;

            default:
                throw new InvalidOperationException($"Unhandled EngineAction: {action.GetType().Name}");
        }
    }
    throw new InvalidOperationException($"Engine did not reach Done within {maxTicks} ticks.");
}

private void EmitActionSubmitted(string actionType, JsonElement? parameters) =>
    TraceWriter.Write(new ActionSubmittedEvent(
        Engine.CurrentRunId ?? string.Empty, actionType, parameters, DateTimeOffset.UtcNow));

private void EmitActionCompleted(string actionType, string outcome) =>
    TraceWriter.Write(new ActionCompletedEvent(
        Engine.CurrentRunId ?? string.Empty, actionType, outcome, DateTimeOffset.UtcNow));
```

---

## Task 6 — Given-When-Then specifications

### 6.1 TraceEvent value semantics

**Record equality:**
- Given two `RunStartEvent` instances with identical `RunId`, `QuestId`, `QuestSchemaId`, `At` → they compare equal (record `==` and `.Equals`).
- Given two `RunStartEvent` instances differing in any field → they compare unequal.

**Polymorphic serialization round-trip:**
- Given an `ObservationEvent` with `Method = "GetPlayerZone"`, `Value = (object)182`, `RunId = "abcd1234"` → JSON serializes with `"type":"observation"` and `"method":"GetPlayerZone"`.
- Given the same JSON → deserializes back to an `ObservationEvent` (via `TraceEvent` polymorphic context) with `Method == "GetPlayerZone"`.

**run.end outcome:**
- Given a `RunEndEvent("rid","done", now)` → JSON contains `"outcome":"done"`.

### 6.2 TraceWriter

**Single line per Write:**
- Given a fresh `TraceWriter` over a `MemoryStream` and a `RunStartEvent`,
  When `Write(evt)` is called,
  Then the stream contains exactly one line terminated by `\n`.

**Three writes → three lines:**
- Given a fresh `TraceWriter`,
  When `Write` is called three times with distinct events,
  Then reading the stream produces exactly 3 newline-terminated lines, each parseable as JSON with a `type` field.

**JSONL line is single-line JSON:**
- Given any single event written,
  Then the resulting line contains no embedded `\n` characters (verifies `WriteIndented = false`).

**Flush per write:**
- Given a `TraceWriter` constructed over a `MemoryStream`,
  When `Write` returns,
  Then the stream's `Position` reflects the bytes already written (no buffered bytes still pending).

**Disposed writer rejects writes:**
- Given a `TraceWriter` that has been disposed,
  When `Write` is called,
  Then `ObjectDisposedException` is thrown.

**Oversized event rejected:**
- Given an `ObservationEvent` whose serialized form exceeds 4096 bytes (e.g., a synthetic value with 5000 chars),
  When `Write` is called,
  Then `InvalidOperationException` is thrown with a message containing "4096-byte cap".

**Append mode preserves prior content:**
- Given a `TraceWriter` opened over a `FileStream` in append mode pointing at a file with existing lines,
  When new events are written and the writer disposed,
  Then reading the file produces existing lines followed by new ones, in order.

### 6.3 Recording proxy

**Pass-through return value:**
- Given a `FakeGameStateProvider` configured `SetZone(ZoneId(182))`,
  When `RecordingGameStateProvider.GetPlayerZone(ct)` is called,
  Then it returns `Result<ZoneId>.Success(182)` — identical to what the inner fake returns.

**One observation per call:**
- Given a `FakeGameStateProvider` and a `FakeTraceWriter`,
  When `RecordingGameStateProvider.GetPlayerZone(ct)` is called once,
  Then `FakeTraceWriter.Count == 1` and the single event is an `ObservationEvent` with `Method == "GetPlayerZone"`.

**Argument capture:**
- Given `RecordingQuestState.GetQuestSequence(new QuestId(66130), ct)` called,
  Then the emitted `ObservationEvent.Argument` equals `new QuestId(66130)`.

**Inner called exactly once:**
- Given a `FakeGameStateProvider` whose `RecordedReads` is initially empty,
  When the proxy's `GetPlayerZone` is called,
  Then `FakeGameStateProvider.RecordedReads.Count == 1` — no double-reads.

**Failure value recorded as failure marker:**
- Given a `FakeGameStateProvider` configured to return `Result<ZoneId>.Failure("not ready")` for `GetPlayerZone`,
  When the proxy is called,
  Then the `ObservationEvent.Value` is non-null and JSON-serializes with a `failure` property equal to `"not ready"`.

**RunId from accessor:**
- Given the proxy is constructed with `runIdAccessor = () => "fixed-id"`,
  When any proxy method is called,
  Then the emitted observation has `RunId == "fixed-id"`.

**RunId null before BeginRun:**
- Given the proxy is constructed with `runIdAccessor = () => null` (BeginRun never called),
  When a proxy method is called,
  Then the emitted observation has `RunId == null` (or empty string, per implementation choice — pick one and assert it).

**Trace write failure does not propagate:**
- Given an `ITraceWriter` that throws `IOException` on every `Write`,
  When a proxy method is called,
  Then the proxy returns the inner adapter's result without throwing.

### 6.4 Engine integration

**BeginRun + Tick produces run.start then observations then decision:**
- Given a harness with quest 66130 loaded and `BeginRun("test-run-1")` called,
  When `Tick` is called once with sequence at 0 and player far from Wymond,
  Then `FakeTraceWriter.RecordedEvents` contains, in order:
  1. exactly one `RunStartEvent` with `RunId == "test-run-1"`, `QuestId == 66130`
  2. one or more `ObservationEvent`s (emitted by the recording proxy as the engine reads sequence, quest status, etc.)
  3. exactly one `DecisionEvent` with `ActionType == "Navigate"`, `StepId == "travel-to-wymond"`

  `run.start` is the first event in the list because `EmitRunStartIfNeeded()` is the first statement in `Tick` (before any adapter reads that trigger proxy observations). The test asserts `RunStartEvent` index == 0.

**No run.start without BeginRun:**
- Given a harness with quest loaded and `BeginRun` NOT called,
  When `Tick` is called,
  Then `FakeTraceWriter.RecordedEvents` contains no `RunStartEvent`. (Observations and decisions also have `RunId == null` per §6.3 last item.)

**Done emits run.end:**
- Given the harness in the state where the next tick will return `Done` (quest status = Complete, BeginRun called),
  When `Tick` is called,
  Then `FakeTraceWriter.RecordedEvents` contains exactly one `RunEndEvent` with `Outcome == "done"` and it is the last event in the list.

**AwaitUser emits run.end with awaitUser:**
- Given a harness where `BeginRun("rid")` has been called but the engine encounters an unrecoverable condition causing `AwaitUser`,
  When `Tick` is called,
  Then a `RunEndEvent` with `Outcome == "awaitUser"` is emitted.

**Subsequent BeginRun resets run.start emission:**
- Given a harness that has completed one run (Done emitted),
  When `BeginRun("rid-2")` is called and `Tick` runs,
  Then a fresh `RunStartEvent` with `RunId == "rid-2"` is emitted.

**Tick without StartQuest emits nothing:**
- Given a harness with NO quest loaded,
  When `Tick` is called,
  Then `FakeTraceWriter.Count == 0`. (Engine returns AwaitUser but emits no event because there is no questId.)

**BeginRun with empty string throws:**
- Given an engine,
  When `BeginRun("")` is called,
  Then `ArgumentException` is thrown.

**BeginRun with null throws:**
- Given an engine,
  When `BeginRun(null!)` is called,
  Then `ArgumentException` is thrown.

### 6.5 End-to-end: quest 66130 with real TraceWriter

**Complete trace shape:**
- Given the `EndToEnd_FullFlow_FourActionsBeforeDone` test wired with a real `TraceWriter` over a `MemoryStream`,
  When the test runs to completion,
  Then the stream's content, parsed line-by-line as JSON, contains:
  - exactly one event with `type == "run.start"`
  - exactly four events with `type == "decision"` and `actionType ∈ {"Navigate","Interact"}`
  - exactly four events with `type == "action.submitted"`
  - exactly four events with `type == "action.completed"`
  - at least one event with `type == "observation"`
  - exactly one event with `type == "run.end"` with `outcome == "done"` as the **last** line

**Ordering invariant:**
- `run.start` is line 1.
- `run.end` is the last line.
- For each of the four actions: a `decision` event precedes the corresponding `action.submitted`, which precedes `action.completed`.

**Readability:**
- The output is valid JSONL (each line independently parseable, no embedded newlines inside JSON, file ends with `\n`).

### 6.6 TDD test counts

| Area | Happy paths | Edge cases | Error cases | Total |
|---|---|---|---|---|
| `TraceEvent` records | 6 | 2 | 0 | 8 |
| `TraceWriter` | 4 | 4 | 3 | 11 |
| `RecordingGameStateProvider` | 6 | 3 | 2 | 11 |
| `RecordingQuestState` | 4 | 2 | 1 | 7 |
| `QuestEngine.BeginRun` + emissions | 5 | 3 | 2 | 10 |
| End-to-end 66130 with TraceWriter | 2 | 1 | 0 | 3 |

**Expected total: ~50 new tests.** Phase 5 brings the suite from 49 → ~99 tests.

---

## Task 7 — Done criteria

1. The Phase 4 test `EndToEnd_FullFlow_FourActionsBeforeDone` is re-run with a `TraceWriter` writing to a `MemoryStream`.
2. The stream content is JSONL — every line is independently parseable JSON.
3. The trace contains the six event types in the expected order (see §6.5).
4. A new test reads the stream content back and asserts the structure programmatically.
5. All 49 Phase 4 tests still pass.
6. The new ~50 Phase 5 tests pass.
7. The plan's known divergences from `TRACE_FORMAT.md` (missing `seq`, missing `ts` offset, flat record shape vs. `data` wrapper) are documented in a `## Known divergences from TRACE_FORMAT.md` section appended to that spec — to be reconciled in Phase 7.

---

## Implementation order (TDD)

**Phase A — Types and signatures (build green, no behavior change)**
1. Create `QuestForge.Adapters/Tracing/` folder.
2. Define `TraceEvent` base + 6 sealed subtypes.
3. Define `TraceEventJsonContext`.
4. Update `ITraceWriter.Write` to take `TraceEvent`.
5. Update `NullTraceWriter` and `FakeTraceWriter` to new signature.
6. **Gate:** `dotnet build` green; all 49 existing tests pass.

**Phase B — Tester writes failing tests**
1. Tests for `TraceEvent` record equality and round-trip JSON (§6.1, 8 tests).
2. Tests for `TraceWriter` (§6.2, 11 tests).
3. Tests for `RecordingGameStateProvider` and `RecordingQuestState` (§6.3, 18 tests).
4. Tests for engine `BeginRun` and event emission (§6.4, 10 tests).
5. End-to-end tests with real `TraceWriter` (§6.5, 3 tests).
6. **All new tests RED.**

**Phase C — Builder implements**
1. `TraceWriter` (Task 2) → makes §6.2 tests green.
2. `RecordingGameStateProvider`, `RecordingQuestState`, `SerializableValue` helper (Task 3) → makes §6.3 tests green.
3. Engine `BeginRun`, `EmitRunStartIfNeeded`, decision/run.end emission (Task 4) → makes §6.4 tests green.
4. Harness wiring updates (Task 5) → makes §6.5 end-to-end tests green.
5. `TraceEventJsonContext` polymorphic round-trip → makes §6.1 tests green.

**Phase D — Reviewer**
1. Verify the architectural invariant: `QuestForge.Engine` references only `QuestForge.Adapters`, never the proxies in `QuestForge.Adapters.Fakes`. The proxy is wired by the test harness and (in Phase 6) by the plugin layer.
2. Verify no `_trace.Write` call inside the engine throws under any test scenario.
3. Verify the trace file from the end-to-end test, when written to disk, is `cat`-readable as expected.
4. Verify `WriteIndented = false` is set on the JSON context (no multi-line events).

---

## What Phase 5 does NOT include

- The eleven diagnostic event types (`dialogue.resolved`, `gear.repair`, `duty.retry`, etc.) — Phase 6+/8+.
- The exceptional event types (`recovery.triggered`, `adapter.error`, `engine.error`, `player.died`) — Phase 6+ when recovery and death handling land.
- `seq` and `ts` (monotonic offset) fields — Phase 7 when replay determinism requires them.
- The `data` sub-object wrapper from the spec — Phase 7 (see §11 known divergence).
- `EngineDecisionConfig` in `run.start` — Phase 7+ once the field set is defined.
- `pluginVer`, `dataVer`, `dataHash`, `patchVer`, `wallClockUtc` (absolute), `newGamePlus`, `precedingRunId` in `run.start` — Phase 6 (plugin metadata) and Phase 7 (data hash).
- Trace file rotation, the 10 MB hard cap, multi-part traces — Phase 8+.
- Privacy redaction (`qf-trace redact`) and the recording proxy allowlist — Phase 8.
- Replay (`ReplayGameStateProvider`, `ReplayQuestState`, `qf-trace replay`, CI replay) — Phase 7.
- The `qf-trace` CLI in `questforge-tools` — Phase 7.

---

## Risks and mitigations

**Risk: spec divergence becomes permanent.**
Phase 5 emits a structurally simpler trace than `TRACE_FORMAT.md` specifies. If Phase 7 launches and the divergence is not reconciled, replay will need a translation layer or the spec will be stretched. **Mitigation:** the `## Known divergences` section added to `TRACE_FORMAT.md` is non-negotiable in Phase 5 done criteria. Phase 7's first task is to reconcile.

**Risk: the recording proxy's hand-written method-per-method shape rots.**
Adding a new method to `IGameStateProvider` without updating the proxy will silently lose observations. **Mitigation:** Phase 5 adds a test that uses reflection to assert every public method on `IGameStateProvider` is overridden in `RecordingGameStateProvider`, and likewise for `IQuestState`. Out-of-sync interfaces fail the test.

**Risk: trace writes block the engine's tick.**
`Flush` is synchronous; on slow disks, a tick could take many milliseconds longer than the engine's normal cadence. **Mitigation:** acknowledged but not fixed in Phase 5. The engine is not yet running against a real game clock; tick latency is irrelevant in unit tests. Phase 6 considers async buffering once real-world latency surfaces.

**Risk: `JsonElement?` pre-serialization adds a reflection step per observation.**
`SerializableValue<T>` calls `JsonSerializer.SerializeToElement(v, _jsonOpts)` for each adapter result. This is a reflection-based call inside the proxy. Per tick the engine emits ~5–10 observations, so ~10–20 reflection serializations. This is acceptable for Phase 5 tests; Phase 6 may revisit by adding explicit source-gen for the most common value types (int, bool, ZoneId). **Mitigation:** documented; not optimized in Phase 5.