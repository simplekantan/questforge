# Phase 9 Plan — Authoring Mode

**Status:** TDD specification — ready for test creation
**Owners:** QuestForge maintainers
**Related:** [AUTHORING.md](./AUTHORING.md), [SCHEMA.md](./SCHEMA.md), [NEXT_STEPS.md](./NEXT_STEPS.md), [ADAPTERS.md](./ADAPTERS.md)

---

## 0. Purpose and scope

Phase 9 delivers in-plugin authoring tooling so contributors can produce a valid quest definition by playing through the quest once with recording active. Both sub-modes from `AUTHORING.md` ship together:

- **Inspect mode** — passive debug panels (player state, quest state, interaction)
- **Author mode** — active recording with step inference, draft management, validation, and export

The deliverable is: *open Author mode, play the quest, click Record after each meaningful action, click Export, get a valid `QuestDefinition` JSON*.

Inference rules are deliberately simple in this phase — `travel`/`talk`/`accept`/`turn-in` cover the empirical majority of recorded actions. Step types not directly inferable (e.g. `combat`, `duty`, `fragment`) are reached via Override in the record modal.

### Architectural invariant (non-negotiable)

`QuestForge.Engine` must not reference Dalamud. All pure logic — snapshot diffing, inference, draft mutation, draft serialization, validation reuse — lives in `QuestForge.Engine.Authoring` and is unit-testable in `QuestForge.Engine.Tests` without a game. Only ImGui rendering and Dalamud event subscription live in `QuestForge.Plugin`.

---

## 1. New types and interfaces

### 1.1 `QuestForge.Engine.Authoring` namespace

All pure C#. No Dalamud references. Lives in `QuestForge.Engine/Authoring/`.

#### 1.1.1 `IAuthoringObserver`

The plugin implements this and is called by Dalamud event handlers. The authoring engine receives game-event signals only through this interface — never a concrete Dalamud type.

```csharp
public interface IAuthoringObserver
{
    void OnZoneChanged(ZoneId zone, WorldPosition position);
    void OnPlayerMoved(WorldPosition position);
    void OnQuestAccepted(QuestId quest);
    void OnQuestCompleted(QuestId quest);
    void OnQuestSequenceChanged(QuestId quest, int newSequence);
    void OnQuestFlagsChanged(QuestId quest, uint newFlags);
    void OnInteraction(NpcId npc, WorldPosition npcPosition);
    void OnDialogueChoice(string promptSheetRef, string answerSheetRef);
    void OnInventoryChanged(uint inventoryHash);
}
```

**Rationale.** The plugin layer wires Dalamud framework events, `ClientState`, `IGameInteropProvider` hooks (or polling), and addon callbacks to these methods. The authoring engine never knows what a `Dalamud.Game.ClientState.IClientState` is — it only sees the typed signals.

Adapter identifier types (`ZoneId`, `NpcId`, `QuestId`) are imported from `QuestForge.Adapters.Types`, which `QuestForge.Engine` already depends on.

#### 1.1.2 `GameStateSnapshot`

A pure value record. Two snapshots (before/after) are the only input to `StepInferenceEngine`.

```csharp
public sealed record GameStateSnapshot(
    DateTimeOffset CapturedAt,
    ZoneId Zone,
    WorldPosition Position,
    QuestId? ActiveQuest,            // the quest currently being authored
    int QuestSequence,               // 0 if no active quest
    uint QuestFlags,                 // 0 if no active quest
    bool QuestAccepted,
    bool QuestCompleted,
    NpcId? LastNpcInteracted,
    WorldPosition? LastNpcPosition,
    string? LastDialoguePrompt,      // sheet reference
    string? LastDialogueAnswer,      // sheet reference
    uint InventoryHash);             // change-detection only, opaque
```

Snapshots are immutable. `CapturedAt` is supplied by the caller (not `DateTimeOffset.UtcNow` internally) so tests are deterministic.

#### 1.1.3 `InferredFrom`

```csharp
public enum InferredFrom
{
    ZoneChange,
    QuestFlagChange,
    QuestSequenceChange,
    DialogueInteraction,
    QuestAccepted,
    QuestCompleted,
    Manual,
    None
}
```

#### 1.1.4 `Confidence`

```csharp
public enum Confidence { High, Medium, Low }
```

#### 1.1.5 `InferenceResult`

The output of `StepInferenceEngine.Infer(before, after)`. A pure value, no side effects.

`SuggestedExpect` uses a `string` (the raw predicate expression, e.g. `"questSequence(2054) >= 1"`) rather than a schema expect type. The recording modal turns this string into the correct `StepExpect` subtype when building `DraftStep.Raw`. Keeping it as a string here avoids coupling the inference engine to the schema type hierarchy.

```csharp
public sealed record InferenceResult(
    string StepType,                 // "" (empty) when Confidence == Low
    string SuggestedStepId,          // "" when no inference
    string? SuggestedExpect,         // null when no inference; raw predicate string
    Confidence Confidence,
    InferredFrom InferredFrom,
    string? Notes)                   // free-text hint shown in modal
{
    public static InferenceResult Empty { get; } = new(
        StepType: "",
        SuggestedStepId: "",
        SuggestedExpect: null,
        Confidence: Confidence.Low,
        InferredFrom: InferredFrom.None,
        Notes: null);
}
```

#### 1.1.6 `DraftStep`

One step under construction in a `QuestDraft`. The `Raw` field is the schema-typed `Step` instance once the author has confirmed the recording in the modal.

```csharp
public sealed record DraftStep(
    string StepId,
    string StepType,
    int SequenceNumber,              // captured at record time
    InferredFrom InferredFrom,
    GameStateSnapshot ObservedBefore,
    GameStateSnapshot ObservedAfter,
    string? SuggestedExpect,         // raw predicate string, same as InferenceResult.SuggestedExpect
    string? Notes,
    Step? Raw);                      // null until RecordStep populates it from modal confirmation
```

`StepId` must be unique within a draft; `DraftManager` enforces this.

#### 1.1.7 `QuestDraft`

The in-memory document. Tracks steps in insertion order; serialization groups them by `SequenceNumber`.

```csharp
public sealed class QuestDraft
{
    public QuestId QuestId { get; }
    public string? QuestName { get; set; }
    public string Category { get; set; } = "msq";   // mutable; UI sets it
    public string Expansion { get; set; } = "arr";
    public string? LastVerifiedPatch { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastModifiedAt { get; private set; }
    public IReadOnlyList<DraftStep> Steps => _steps;

    public QuestDraft(QuestId questId, DateTimeOffset createdAt);

    /// <exception cref="InvalidOperationException">Thrown when stepId already exists in the draft.</exception>
    public void AddStep(DraftStep step, DateTimeOffset now);
    public bool RemoveStep(string stepId, DateTimeOffset now);
    public bool ReplaceStep(string stepId, DraftStep newStep, DateTimeOffset now);
    public DraftStep? GetStep(string stepId);

    /// <summary>
    /// Convert draft to a schema-valid QuestDefinition.
    /// Throws DraftSerializationException if any step lacks a Raw value.
    /// Steps are grouped into QuestSequence objects by SequenceNumber, ascending.
    /// </summary>
    public QuestDefinition ToQuestDefinition();
}

public sealed class DraftSerializationException(string message) : Exception(message);
```

`AddStep`, `RemoveStep`, `ReplaceStep` update `LastModifiedAt` from the supplied `now` parameter (deterministic for tests).

#### 1.1.8 `StepInferenceEngine`

Pure C# class. Single public method. No state. Threadsafe.

```csharp
public sealed class StepInferenceEngine
{
    /// <summary>
    /// Compare two snapshots and produce a suggested DraftStep template.
    /// The 'after' snapshot must have CapturedAt >= 'before'.
    /// </summary>
    public InferenceResult Infer(GameStateSnapshot before, GameStateSnapshot after);
}
```

The full inference rule table is §2 below.

#### 1.1.9 `SnapshotAggregator`

Accumulates `IAuthoringObserver` callbacks into a current `GameStateSnapshot`. Lives in `QuestForge.Engine.Authoring` so it can be unit-tested without Dalamud. `AuthoringHost` delegates all observer calls to it.

```csharp
public sealed class SnapshotAggregator
{
    public SnapshotAggregator(QuestId? activeQuest);

    // Current snapshot (updated on each observer call)
    public GameStateSnapshot Current { get; }

    // IAuthoringObserver methods — delegates from AuthoringHost
    public void OnZoneChanged(ZoneId zone, WorldPosition position);
    public void OnPlayerMoved(WorldPosition position);
    public void OnQuestAccepted(QuestId quest);
    public void OnQuestCompleted(QuestId quest);
    public void OnQuestSequenceChanged(QuestId quest, int newSequence);
    public void OnQuestFlagsChanged(QuestId quest, uint newFlags);
    public void OnInteraction(NpcId npc, WorldPosition npcPosition);
    public void OnDialogueChoice(string promptSheetRef, string answerSheetRef);
    public void OnInventoryChanged(uint inventoryHash);
}
```

Calls to `OnQuestSequenceChanged` and `OnQuestFlagsChanged` for a quest ID that does not match `activeQuest` are silently ignored — the aggregator only tracks state for the quest being authored.

#### 1.1.10 `IDraftStorage`

Abstracts I/O. Two implementations:
- `FileDraftStorage` in `QuestForge.Plugin` (writes JSON to `pluginConfigs/QuestForge/drafts/`)
- `FakeDraftStorage` in `QuestForge.Adapters.Fakes` for tests

```csharp
public interface IDraftStorage
{
    Task<Result<bool>> Save(QuestId quest, QuestDraft draft, CancellationToken ct);
    Task<Result<QuestDraft?>> Load(QuestId quest, CancellationToken ct);
    Task<Result<IReadOnlyList<QuestId>>> ListDrafts(CancellationToken ct);
    Task<Result<bool>> Delete(QuestId quest, CancellationToken ct);

    /// <summary>
    /// Copy current draft file to .bak.001 (rotating older backups).
    /// Keeps the most recent 5 backups; deletes .bak.006 and beyond.
    /// Rotation: before writing the new .bak.001, shift existing backups
    /// (.bak.001 → .bak.002, .bak.002 → .bak.003, ..., .bak.005 → deleted).
    /// </summary>
    Task<Result<bool>> CreateBackup(QuestId quest, CancellationToken ct);
}
```

`Result<T>` is the project's existing outcome type. `Save`/`Delete`/`CreateBackup` return `Result<bool>` (true = success) since the project has no `Unit` sentinel. The signatures use `Task` because `FileDraftStorage` performs file I/O; `FakeDraftStorage` returns completed tasks.

#### 1.1.10 `DraftManager`

In-memory cache plus auto-save orchestration. The plugin owns one `DraftManager`. Tests construct one with `FakeDraftStorage`.

```csharp
public sealed class DraftManager
{
    public DraftManager(
        IDraftStorage storage,
        IClock clock,                       // testable now()
        TimeSpan autoSaveInterval = default,// default 60s
        int backupKeepCount = 5);

    /// <summary>
    /// Load from storage if a draft exists; otherwise create a new empty draft.
    /// If storage returns a Failure (I/O error), treat as not-found and create a new draft.
    /// The new draft is NOT automatically persisted — call SaveNow explicitly.
    /// </summary>
    public Task<QuestDraft> GetOrCreate(QuestId quest, CancellationToken ct);
    public Task<QuestDraft?> Get(QuestId quest, CancellationToken ct);
    public IReadOnlyList<QuestId> ActiveDraftIds { get; }

    /// <summary>Persist draft and rotate backups.</summary>
    public Task SaveNow(QuestId quest, CancellationToken ct);

    /// <summary>Save if dirty AND elapsed >= autoSaveInterval. No-op otherwise.</summary>
    public Task MaybeAutoSave(QuestId quest, CancellationToken ct);

    public Task DiscardDraft(QuestId quest, CancellationToken ct);

    /// <summary>Mark dirty — called by AuthoringHost after every mutation.</summary>
    public void MarkDirty(QuestId quest);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
```

`IClock` does not yet exist in the codebase. Introduce it in `QuestForge.Engine.Authoring`. The production implementation is `SystemClock` (wraps `DateTimeOffset.UtcNow`); `FakeClock` in `QuestForge.Adapters.Fakes/Authoring/` is used in tests and advances time only when explicitly told to.

### 1.2 `QuestForge.Plugin` (Dalamud-side)

All Dalamud event wiring and ImGui rendering. These classes are **not** unit-tested in `QuestForge.Engine.Tests` — they exist only when Dalamud is loaded.

#### 1.2.1 `AuthoringHost`

The plugin-side counterpart to `EngineHost`. Owns:
- `DraftManager`
- `StepInferenceEngine` (singleton)
- The currently-active draft (`QuestId?`)
- The current authoring sub-mode (`Off | Inspect | Author`)
- The most recent `GameStateSnapshot` (for "before" capture)

Subscribes to Dalamud events:
- `IClientState.TerritoryChanged` → `OnZoneChanged`
- Quest journal polling (250ms heartbeat) → `OnQuestSequenceChanged`, `OnQuestFlagsChanged`
- `ITargetManager` interaction events → `OnInteraction`
- Addon callbacks (Talk, SelectString, SelectYesno) → `OnDialogueChoice`

Implements `IAuthoringObserver` itself, routing events to internal state updates and to `StepInferenceEngine` when a record is requested.

```csharp
public sealed class AuthoringHost : IAuthoringObserver, IDisposable
{
    public AuthoringMode Mode { get; private set; }   // Off | Inspect | Author
    public QuestId? AuthoringTarget { get; private set; }

    public void EnterInspectMode();
    public void EnterAuthorMode(QuestId target);
    public void ExitAuthoring();   // returns to Off

    /// <summary>Captures the current snapshot as the "before" for the next Record.</summary>
    public GameStateSnapshot OpenRecordModal();

    /// <summary>
    /// Captures "after" snapshot, calls inference, returns the suggestion for the modal to display.
    /// </summary>
    public InferenceResult PreviewInference(GameStateSnapshot before);

    /// <summary>
    /// Author confirmed the modal. Build a DraftStep from the (possibly user-edited) inference
    /// and append it to the active draft. Triggers a SaveNow.
    /// </summary>
    public Task RecordStep(
        GameStateSnapshot before,
        InferenceResult inference,
        string finalStepId,
        string? finalExpect,    // raw predicate string, possibly edited by author in modal
        string? notes,
        Step rawStep,           // fully constructed by modal from inference + author edits
        CancellationToken ct);

    // IAuthoringObserver implementation (Dalamud event handlers call these)
    public void OnZoneChanged(ZoneId zone, WorldPosition position) { ... }
    public void OnPlayerMoved(WorldPosition position) { ... }
    public void OnQuestAccepted(QuestId quest) { ... }
    // ...
}

public enum AuthoringMode { Off, Inspect, Author }
```

#### 1.2.2 Debug panels (ImGui windows)

Three independently-toggleable Dalamud `Window` instances:

- `PlayerStatePanel` — `AUTHORING.md` §4.1. Zone, position, job, mount, combat, HP, instance kind, copy-as-JSON. Reads `EngineHost.GetGameStateSummary` style data; 250ms heartbeat configurable.
- `QuestStatePanel` — `AUTHORING.md` §4.2. All accepted quests with sequence + flags. Recent change highlighted.
- `InteractionPanel` — `AUTHORING.md` §4.3. Current target, current allowlisted UI addon, last interaction, last dialogue choice + sheet references.

Each is a `Window` registered with Dalamud's `WindowSystem` (same pattern as `MainWindow`).

#### 1.2.3 Authoring UI

- `AuthoringSessionPanel` — `AUTHORING.md` §6.3. Draft view with per-step `[edit]`/`[delete]`, per-session controls (`Record next step`, `Validate now`, `Export draft`, `Pause`, `Discard`). Reads from `DraftManager`; writes via `AuthoringHost`.
- `RecordStepModal` — `AUTHORING.md` §5.1. Shown when user clicks `Record next step`. Calls `AuthoringHost.PreviewInference` to populate the modal. On `[Record]` button, calls `RecordStep`.
- `StepEditModal` — opens when user clicks `[edit]` on an existing step. Displays JSON; allows hand-edit and "Re-record" option.
- `ExportDialog` — `AUTHORING.md` §10.1. File picker, validate-before-export checkbox, optional open-after-export.

#### 1.2.4 `FileDraftStorage`

```csharp
public sealed class FileDraftStorage : IDraftStorage
{
    public FileDraftStorage(string drafterRoot);   // %APPDATA%\XIVLauncher\pluginConfigs\QuestForge\drafts\
    // ...
}
```

Filename convention: `{questId}.draft.json`, backups `{questId}.draft.json.bak.{n:000}` where `001` is most recent. Uses `System.Text.Json` with the existing `QuestForgeJsonContext` for `QuestDefinition` round-tripping. Draft format on disk is a JSON envelope that includes the draft metadata plus the partial `QuestDefinition`-shaped payload.

---

## 2. Step inference rules

`StepInferenceEngine.Infer(before, after)` evaluates the snapshot delta in **priority order**. The first matching rule wins; subsequent matches are not evaluated (with the documented exception of zone-change-as-prelude).

| # | Condition on (before → after) | Inferred `StepType` | Suggested `expect` | Confidence | `InferredFrom` |
|---|---|---|---|---|---|
| 1 | `after.QuestCompleted && !before.QuestCompleted` | `turn-in` | `isQuestComplete(id)` | High | QuestCompleted |
| 2 | `after.QuestAccepted && !before.QuestAccepted` | `accept` | `isQuestAccepted(id)` | High | QuestAccepted |
| 3 | `after.QuestSequence > before.QuestSequence` (active quest unchanged) | `talk` (default); if `LastNpcInteracted` is null in both before and after, emit `talk` with a `Notes` hint: "No NPC observed — consider overriding to `interact-object` if the action was with an EventObject" | `questSequence(id) >= afterSeq` | High | QuestSequenceChange |
| 4 | `after.Zone != before.Zone` (no quest change) | `travel` | `playerZone() == afterZoneValue` | High | ZoneChange |
| 5 | `after.QuestFlags != before.QuestFlags && after.QuestSequence == before.QuestSequence` | `talk` | `questFlag(id, bit)` for **lowest** flipped bit | Medium | QuestFlagChange |
| 6 | `after.LastDialogueAnswer != before.LastDialogueAnswer && rules 1-5 did not match` | `talk` | `questSequence(id) >= currentSeq` | Medium | DialogueInteraction |
| 7 | `after.LastNpcInteracted != before.LastNpcInteracted && rules 1-6 did not match` | `talk` | (empty — author writes manually) | Low | DialogueInteraction |
| 8 | None of the above | `""` (empty) | `null` | Low | None |

### 2.1 Ambiguity: simultaneous zone + sequence change

If both rule 3 and rule 4 would match (player teleported AND quest advanced), rule 3 wins (quest sequence change is the dominant signal). The modal shows a `Notes` value:

> "Zone also changed from {beforeZone} to {afterZone}. Consider inserting a separate `travel` step before this one."

The author can split into two steps using two record actions, or accept the single inferred step and edit the JSON later.

### 2.2 Suggested step IDs

Step IDs are author-friendly slugs derived from context. The engine produces a candidate; the author can override in the modal.

| `StepType` | Suggested slug |
|---|---|
| `travel` | `travel-to-zone-{afterZone}` |
| `talk` | `talk-to-npc-{npcId}` (if `LastNpcInteracted` set) else `talk-step-{n}` |
| `accept` | `accept-quest` |
| `turn-in` | `turn-in-quest` |
| `interact-object` | `interact-object-{n}` |
| empty | `step-{n}` |

`{n}` is the next ordinal not yet used in the draft.

### 2.3 Single-predicate policy

`StepInferenceEngine` always emits a **single** predicate string in `SuggestedExpect`, not an `AllExpect`. The priority rules in §2 ensure one signal wins; supplementary signals appear in `Notes` only. The author manually combines predicates using `AllExpect` in the edit modal if needed.

**Rationale:** AllExpect construction requires knowing the exact predicate syntax the author wants, which is a UI concern (edit modal), not an inference concern. Keeping inference output as a single string simplifies both `StepInferenceEngine` and its tests.

### 2.4 Flag-bit identification

When `QuestFlags` change without a sequence change, the engine identifies the **lowest** bit position that flipped (XOR `before ^ after`, count trailing zeros). The predicate is `questFlag(questId, bitIndex)`. If multiple bits flipped, the modal `Notes` field mentions:

> "Multiple flag bits changed: bits {a, b, c}. Suggested predicate uses bit {a} (lowest)."

---

## 3. Draft serialization

### 3.1 Draft-on-disk vs export

A draft is **not** a `QuestDefinition`. A draft is an envelope:

```json
{
  "draftSchemaVersion": "1.0.0",
  "questId": 2054,
  "questName": "Ishgardian Justice",
  "category": "msq",
  "expansion": "heavensward",
  "lastVerifiedPatch": "7.4",
  "createdAt": "2026-05-15T18:42:11Z",
  "lastModifiedAt": "2026-05-15T19:13:55Z",
  "steps": [
    {
      "stepId": "talk-to-witness-a",
      "stepType": "talk",
      "sequenceNumber": 1,
      "inferredFrom": "QuestSequenceChange",
      "observedBefore": { ... GameStateSnapshot ... },
      "observedAfter":  { ... GameStateSnapshot ... },
      "suggestedExpect": "questSequence(2054) >= 1",
      "notes": null,
      "raw": { "type": "talk", "id": "talk-to-witness-a", ... }
    }
  ]
}
```

The envelope is round-trippable so an author can pause, resume, and ship partial work.

Export (`ToQuestDefinition()`) discards the envelope metadata and emits the canonical `QuestDefinition` shape.

### 3.2 `DraftStep` → `Step` mapping

`DraftStep.Raw` already holds a fully-typed `Step` subclass (constructed by `RecordStepModal` from the inferred type plus author edits). `ToQuestDefinition` does not re-infer; it copies `Raw` straight through. The only transformation is grouping by `SequenceNumber`.

If `Raw` is `null` for any step (the author opened a draft envelope that was saved mid-modal, before `RecordStep` populated `Raw`), `ToQuestDefinition` throws `DraftSerializationException`.

### 3.3 Sequence grouping

```csharp
public QuestDefinition ToQuestDefinition()
{
    var grouped = _steps
        .GroupBy(s => s.SequenceNumber)
        .OrderBy(g => g.Key)
        .Select(g => new QuestSequence
        {
            Sequence = g.Key,
            Steps = g.Select(s => s.Raw!).ToArray()
        })
        .ToArray();

    return new QuestDefinition
    {
        SchemaVersion = "1.0.0",
        Id = QuestId.Value,
        Name = QuestName ?? "",
        Expansion = Expansion,
        Category = Category,
        Enabled = true,
        SupportStatus = new SupportStatus { Implementation = "partial", KnownIssues = [] },
        LastVerifiedPatch = LastVerifiedPatch ?? "unknown",
        Requirements = new Requirements(),
        AcceptFrom = InferAcceptFrom(),   // from first 'accept' step, or null with validation error
        Sequences = grouped,
    };
}
```

`InferAcceptFrom` scans for the first `AcceptStep` in the draft and copies its `Target` into the top-level `AcceptFrom`. If none exists, returns `null` and validation flags this as an error before export.

### 3.4 Required vs optional at draft time vs export time

| Field | Required at draft time | Required at export time | Notes |
|---|---|---|---|
| `stepId` | Yes (set on Add) | Yes | Must be unique within draft |
| `stepType` | Yes | Yes | |
| `expect` | No | Yes (warning if missing) | Author may save without one and revisit |
| `sequenceNumber` | Yes (captured at record time) | Yes | |
| `raw` | No (null until modal confirmed) | Yes | `DraftSerializationException` if null at export |
| `acceptFrom` | No | Yes | Inferred from first `accept` step |
| `supportStatus` | No | Yes | Defaulted on export to `partial` |
| `lastVerifiedPatch` | No | Yes (defaulted to `"unknown"`) | |

### 3.5 Validation rules at `[Validate now]` and pre-export

`DraftValidator.Validate(draft)` returns `(errors, warnings)`:

**Errors (blocking by default):**

- E1: Duplicate `stepId` within the draft.
- E2: `Raw == null` for any step (modal abandoned mid-record).
- E3: Step `expect` predicate string fails the Phase 2 predicate parser (`PredicateEvaluator`-adjacent validator from `questforge-tools`/`QuestForge.Schema`).
- E4: No `accept` step found anywhere in the draft.
- E5: A predicate references an unknown function (`questSequnece` typo → did-you-mean from existing state-function registry).
- E6: A `talk` step's `Target.NpcId == 0` or its `Target.Zone < 0`.

**Warnings (non-blocking; export proceeds with confirmation):**

- W1: Step has no `expect`.
- W2: Step has no `notes` and `Confidence == Low`.
- W3: A `travel` step is the last step in the last sequence (usually unintentional).
- W4: Two consecutive steps have the same `Target.NpcId` (might be a redundant recording).
- W5: A sequence is empty (rare; usually means the author deleted all steps in it).

The validator reuses `QuestForge.Schema.PredicateExpect`/`AllExpect`/`AnyExpect` and the predicate-parser entrypoint already used by Phase 1-2 tools. No new parser is written for Phase 9.

---

## 4. Snapshot timing — when "before" and "after" are captured

The recording flow is explicit; no implicit snapshotting on every Dalamud frame.

### 4.1 "Before" snapshot

Captured at exactly the moment the author opens the record modal:

1. Author clicks `[+ Record next step]` (or presses the configured hotkey).
2. `AuthoringSessionPanel` calls `AuthoringHost.OpenRecordModal()`.
3. `AuthoringHost` reads the current `GameStateSnapshot` from its internal cache (which the panel heartbeat keeps fresh) and returns it. This becomes the modal's stored "before".

The modal displays nothing yet — the author goes off and performs the in-game action.

### 4.2 "After" snapshot

Captured when the author clicks `[✓ Record]` in the modal:

1. Author finishes their in-game action and returns to the modal.
2. Author clicks `[✓ Record]`.
3. Modal calls `AuthoringHost.PreviewInference(beforeSnapshot)`.
4. `AuthoringHost` captures the **current** snapshot as "after", calls `StepInferenceEngine.Infer(before, after)`, and returns the `InferenceResult`.
5. Modal renders the inference (suggested step type, expect, ID, notes). Author can override before final confirmation.
6. Author clicks `[✓ Record]` again to confirm (or `[Cancel]` to abandon).

**Why two clicks.** The first `[✓ Record]` runs inference; the second confirms the (possibly user-edited) result. This gives the author a chance to override the inferred step type, edit `expect`, edit `stepId`, etc. before the step is appended to the draft.

### 4.3 Multi-step actions in one recording

For a sequence like "talk to NPC, advance dialogue twice, take a cutscene", the author records **one** step that captures the entire interaction. The "before" snapshot is from before the talk; the "after" snapshot is after the cutscene. The inferred predicate is whatever delta the engine sees (most likely `questSequence(id) >= newSeq`).

If the author wants finer-grained steps, they record more frequently. The engine does not auto-decompose.

### 4.4 Cancellation semantics

- `[✗ Cancel]` discards both snapshots and the modal closes without mutation.
- Closing the modal via the window X button is equivalent to Cancel.
- Mode-switching to Inspect or Off while a modal is open implicitly cancels.

### 4.5 Snapshot freshness in panels

The three debug panels poll on a 250ms heartbeat by default (configurable to 1000ms or off). `AuthoringHost` caches the most recent `GameStateSnapshot`. The snapshot returned by `OpenRecordModal()` is at most 250ms stale, which is fine for inference (zone/quest/flag deltas don't move that fast).

---

## 5. Test scenarios (Tester reads from here)

All tests live in `QuestForge.Engine.Tests/Authoring/`. They construct `GameStateSnapshot` records directly and pass them to `StepInferenceEngine.Infer`, or wire `DraftManager` to `FakeDraftStorage`. **No Dalamud, no game, no plugin process required.**

### 5.1 Inference tests (target: `StepInferenceEngine`)

1. **`InferTravel_WhenZoneChanges`**
   Before: zone 100 at (10,0,10). After: zone 200 at (50,0,50). No quest delta.
   Expected: `StepType=travel`, `SuggestedExpect = PredicateExpect("playerZone() == 200")`, `Confidence=High`, `InferredFrom=ZoneChange`.

2. **`InferAccept_WhenQuestAcceptedFires`**
   Before: `QuestAccepted=false`. After: `QuestAccepted=true`.
   Expected: `StepType=accept`, `SuggestedExpect = PredicateExpect("isQuestAccepted(2054)")`, `Confidence=High`, `InferredFrom=QuestAccepted`.

3. **`InferTurnIn_WhenQuestCompletedFires`**
   Before: `QuestCompleted=false`. After: `QuestCompleted=true`.
   Expected: `StepType=turn-in`, `SuggestedExpect = PredicateExpect("isQuestComplete(2054)")`, `Confidence=High`, `InferredFrom=QuestCompleted`.

4. **`InferTalk_WhenSequenceAdvances`**
   Before: seq 0. After: seq 1, same zone, `LastNpcInteracted=1014875`.
   Expected: `StepType=talk`, `SuggestedExpect = PredicateExpect("questSequence(2054) >= 1")`, `Confidence=High`, `InferredFrom=QuestSequenceChange`, `SuggestedStepId="talk-to-npc-1014875"`.

5. **`InferFlag_WhenFlagBitFlipsWithoutSequence`**
   Before: flags=0x00. After: flags=0x04 (bit 2 set), seq unchanged.
   Expected: `StepType=talk`, `SuggestedExpect = PredicateExpect("questFlag(2054, 2)")`, `Confidence=Medium`, `InferredFrom=QuestFlagChange`.

6. **`InferFlag_LowestBitWins_WhenMultipleBitsFlip`**
   Before: flags=0x00. After: flags=0x14 (bits 2 and 4 set).
   Expected: `SuggestedExpect = PredicateExpect("questFlag(2054, 2)")`, `Notes` contains "Multiple flag bits changed: bits 2, 4".

7. **`InferTalk_FromDialogueOnly_WhenNoOtherDelta`**
   Before: same as after except `LastDialogueAnswer` differs.
   Expected: `StepType=talk`, `Confidence=Medium`, `InferredFrom=DialogueInteraction`, `SuggestedExpect = PredicateExpect("questSequence(2054) >= 0")`.

8. **`InferTalk_LowConfidence_WhenOnlyNpcChanged`**
   Before: `LastNpcInteracted=null`. After: `LastNpcInteracted=1014875`. No quest/zone/dialogue delta.
   Expected: `StepType=talk`, `Confidence=Low`, `SuggestedExpect=null`, `InferredFrom=DialogueInteraction`.

9. **`NoInference_WhenNothingChanged`**
   Before == after.
   Expected: `InferenceResult.Empty` (Confidence=Low, StepType="", InferredFrom=None).

10. **`PriorityRule_SequenceChangeBeatsZoneChange`**
    Before: zone 100, seq 0. After: zone 200, seq 1.
    Expected: `StepType=talk` (rule 3 wins), `SuggestedExpect` is the questSequence predicate, `Notes` mentions zone change as preceding-travel hint.

11. **`PriorityRule_AcceptBeatsSequenceChange`**
    Before: `QuestAccepted=false`, seq 0. After: `QuestAccepted=true`, seq 1.
    Expected: `StepType=accept` (rule 2 wins over rule 3 because accept is observed first), `InferredFrom=QuestAccepted`.

12. **`PriorityRule_CompletedBeatsAccept`**
    Before: accepted=true, completed=false. After: accepted=true, completed=true.
    Expected: `StepType=turn-in`.

13. **`MultiSignalExpect_WhenSeqAdvancesAndZoneChanges`**
    Before: zone 100, seq 0. After: zone 200, seq 1.
    Expected: `StepType=talk`, `SuggestedExpect="questSequence(2054) >= 1"` (single predicate — rule 3 wins), `Notes` contains the zone-change hint ("Zone also changed from 100 to 200"). No `AllExpect` is emitted.

### 5.2 Draft mutation tests (target: `QuestDraft`, `DraftManager`)

14. **`DraftStep_AddAndRetrieveInOrder`**
    Add three steps with stepIds A, B, C. `draft.Steps` returns them in insertion order.

15. **`DraftStep_Remove_RemovesByStepId`**
    Add A, B, C. Remove B. `draft.Steps` returns [A, C].

16. **`DraftStep_Remove_ReturnsFalse_WhenStepIdMissing`**
    Add A. Remove "nonexistent". Returns false. `draft.Steps` unchanged.

17. **`DraftStep_Replace_PreservesOrderAndUpdatesLastModified`**
    Add A, B, C at t0. At t1 > t0, replace B with B'. Order remains [A, B', C]. `LastModifiedAt == t1`.

18. **`DraftStep_Add_RejectsDuplicateStepId`**
    Add step with id="X". Add another step with id="X". Second `AddStep` throws `InvalidOperationException` (per §1.1.7 XML doc). `draft.Steps` still contains exactly one step with id="X".

19. **`DraftManager_GetOrCreate_ReturnsExistingDraft`**
    Save a draft for quest 2054. `GetOrCreate(2054)` returns the same draft, not a new one.

20. **`DraftManager_GetOrCreate_CreatesNewDraft_WhenStorageEmpty`**
    `FakeDraftStorage` has no drafts. `GetOrCreate(2054)` creates a new draft and returns it.

21. **`DraftManager_MaybeAutoSave_Skips_WhenIntervalNotElapsed`**
    Set autoSaveInterval = 60s. Mark dirty at t0. Call MaybeAutoSave at t0+30s. `FakeDraftStorage.SaveCount == 0`.

22. **`DraftManager_MaybeAutoSave_Persists_WhenIntervalElapsed`**
    Mark dirty at t0. Call MaybeAutoSave at t0+61s. `FakeDraftStorage.SaveCount == 1`.

23. **`DraftManager_MaybeAutoSave_Skips_WhenClean`**
    No mutation since last save. MaybeAutoSave at t0+100s. SaveCount unchanged.

24. **`BackupRotation_KeepsLatestFive`**
    Call `CreateBackup` 6 times. `FakeDraftStorage` retains backups numbered 001..005; original 001 (now-displaced) was rotated and the oldest deleted.

25. **`BackupRotation_AssignsLowestNumberToMostRecent`**
    Call `CreateBackup` 3 times. The most recent backup is `.bak.001`; the oldest is `.bak.003`.

### 5.3 Serialization / round-trip tests (target: `QuestDraft.ToQuestDefinition`, `IDraftStorage`)

26. **`ToQuestDefinition_GroupsStepsBySequenceNumber`**
    Add steps with sequenceNumber values [0, 0, 1, 1, 2]. Resulting `QuestDefinition.Sequences` has three `QuestSequence` entries with sequence values 0, 1, 2 and matching step counts.

27. **`ToQuestDefinition_OrdersSequencesAscending`**
    Add a step with sequenceNumber=2 before a step with sequenceNumber=0. Output sequences ordered 0, 1, 2 (1 may be empty if no steps).

28. **`ToQuestDefinition_ThrowsWhenAnyRawIsNull`**
    Add a step with `Raw=null`. `ToQuestDefinition` throws `DraftSerializationException`.

29. **`ToQuestDefinition_InfersAcceptFromFromFirstAcceptStep`**
    Draft contains an `AcceptStep` with `Target = NpcLocation(1000789, 128, (9.5, 40, 14.2))`. Output `QuestDefinition.AcceptFrom` equals that location.

30. **`ToQuestDefinition_ProducesValidSchema_FullRoundTrip`**
    Build a draft with one accept step, one travel step, one talk step, one turn-in step. Call `ToQuestDefinition`. Serialize to JSON via `QuestForgeJsonContext`. Deserialize. Resulting `QuestDefinition` equals the original.

31. **`FakeDraftStorage_SaveThenLoad_RoundTrips`**
    Save a draft with 3 steps. Load. Loaded draft has 3 steps with identical IDs, types, sequence numbers, and raw `Step` payloads.

32. **`FakeDraftStorage_ListDrafts_ReturnsAllSavedQuestIds`**
    Save drafts for quests 100, 200, 300. `ListDrafts` returns those three IDs (order unspecified — Tester uses set equality).

33. **`FakeDraftStorage_Delete_RemovesDraftAndAllBackups`**
    Save draft 2054, call `CreateBackup` twice, then `Delete`. Subsequent `Load` returns null; `ListDrafts` does not include 2054.

### 5.4 Validation tests (target: `DraftValidator`)

34. **`Validation_PassesForValidDraft`**
    Draft with one `accept`, one `talk`, one `turn-in`, all with valid `expect` predicates. Errors empty, warnings empty.

35. **`Validation_FailsForDuplicateStepIds`**
    Two steps share `stepId="x"`. Errors contains E1 with both step indices.

36. **`Validation_FailsForMissingAcceptStep`**
    Draft contains only `talk` and `turn-in`. Errors contains E4.

37. **`Validation_FailsForUnparseablePredicate`**
    Step has `expect = PredicateExpect("questSequnece(2054) >= 3")` (typo).
    Errors contains E5 with did-you-mean suggestion.

38. **`Validation_WarnsForMissingExpect`**
    Step has `expect = null`. Warnings contains W1.

39. **`Validation_WarnsForEmptySequence`**
    Draft has steps with sequenceNumbers [0, 2] (gap at 1). Per §3.3, gaps produce simply missing sequences — no placeholder inserted. W5 is **not** emitted for a numeric gap in sequence numbers. W5 is only emitted when a sequence group exists in the draft but contains zero steps (e.g. a step was added with sequenceNumber=1 then deleted, leaving an empty group). This test: no W5 emitted. Warnings empty.

40. **`Validation_FailsForRawNullStep`**
    Step with `Raw == null`. Errors contains E2.

### 5.5 Snapshot / observer tests (target: `SnapshotAggregator`)

`AuthoringHost` lives in `QuestForge.Plugin` and is not unit-testable. Its **state-cache** logic — accumulating observer callbacks into the most-recent `GameStateSnapshot` — is extracted into `SnapshotAggregator` in `QuestForge.Engine.Authoring`. `AuthoringHost` owns a `SnapshotAggregator` and delegates all `IAuthoringObserver` callbacks to it.

41. **`SnapshotAggregator_OnZoneChanged_UpdatesZoneAndPosition`**
    Aggregator at zone 100. Call `OnZoneChanged(zone=200, pos=(50,0,50))`. `Current.Zone == 200`, `Current.Position == (50,0,50)`.

42. **`SnapshotAggregator_OnQuestSequenceChanged_UpdatesSequence`**
    Aggregator with QuestId=2054, seq=0. `OnQuestSequenceChanged(2054, 2)`. `Current.QuestSequence == 2`.

43. **`SnapshotAggregator_OnQuestSequenceChanged_Ignored_WhenDifferentQuest`**
    Aggregator authoring quest 2054. `OnQuestSequenceChanged(9999, 5)`. `Current.QuestSequence` unchanged.

44. **`SnapshotAggregator_OnDialogueChoice_UpdatesLastPromptAndAnswer`**
    Empty aggregator. `OnDialogueChoice("TEXT_Q1", "TEXT_A1")`. `Current.LastDialoguePrompt == "TEXT_Q1"`, `Current.LastDialogueAnswer == "TEXT_A1"`.

45. **`SnapshotAggregator_OnQuestAccepted_SetsAcceptedFlag`**
    `OnQuestAccepted(2054)`. `Current.QuestAccepted == true`.

### 5.6 End-to-end pure-logic tests

46. **`AuthorOneQuest_EndToEnd_ProducesValidDefinition`**
    Simulate the full happy path in a single test:
    - Build snapshots for zone change → accept → talk → talk → turn-in.
    - For each transition, call `Infer`, build a `DraftStep` from the result (synthesizing a `Step` from the inference), and `AddStep`.
    - Call `ToQuestDefinition`.
    - Assert the result has 4 sequences (or however many SequenceNumbers were captured), each with the expected step types in order.
    - Assert `QuestForgeJsonContext` round-trip succeeds.

### Estimated test counts

- Happy paths: **20 scenarios** (1-13, 14-15, 19-20, 26, 29-30, 34)
- Edge cases: **20 scenarios** (16-18, 21-25, 27-28, 31-33, 35-37, 39, 41-45)
- Error cases: **5 scenarios** (28, 35, 36, 37, 40)
- End-to-end: **1 scenario** (46)
- **Expected total: ~46 tests**

---

## 6. Out of scope for Phase 9

Explicitly **not** delivered in this phase. Each is a deliberate deferral, not a forgotten requirement.

- **Fragment recording.** Authors record flat per-sequence steps. To use a fragment, hand-edit the exported JSON (`AUTHORING.md` §11).
- **Branch recording.** Same — no branching workflow in the modal. Hand-add `BranchStep` post-export.
- **Git/GitHub PR submission.** Author exports to a local file path; everything beyond is manual. (`AUTHORING.md` §10.2.)
- **Bulk recording without modal confirmation.** No "batch mode" in v1. Every recorded step requires modal confirmation.
- **Multi-language sheet reference verification.** The interaction panel can *display* all four shipped languages (read-only browsing), but the validator does not warn on EN-only resolution. CI handles this post-PR.
- **Automated `qf-trace extract-fixture` tool.** Phase 10 ships the fixture extractor. Phase 9 produces drafts only.
- **Quest data validation against current player state.** Authors may be on a different patch/character/job than the eventual user. Validation runs against game data only.
- **Live editing of running automation.** Author mode and engine execution are mutually exclusive (`AUTHORING.md` §2.2).
- **Automated quest abandonment.** Engine never abandons quests. Authoring respects the same rule (`AUTHORING.md` §7.3).
- **Authoring lifecycle lazy-load.** Lazy-load is a `AUTHORING.md` §3 nicety; v1 may simply construct `AuthoringHost` eagerly. Idle-unload is deferred.
- **Cross-character session continuity.** v1 assumes one character per authoring session.

---

## 7. Implementation order

Each step produces something testable as soon as it's built. Stop and add tests before moving to the next.

### Step 1 — `GameStateSnapshot` + `StepInferenceEngine`

Pure value types, pure function. **All §5.1 tests (13 tests) become writable immediately.** No other dependencies needed.

- `GameStateSnapshot` record
- `InferredFrom` enum, `Confidence` enum
- `InferenceResult` record + `InferenceResult.Empty`
- `StepInferenceEngine.Infer` (the rule table from §2)

**Done when:** all 13 inference tests pass.

### Step 2 — `QuestDraft` + `DraftManager` + `IDraftStorage` + `FakeDraftStorage`

Pure logic on top of Step 1. **All §5.2 and §5.3 tests (20 tests) become writable.**

- `DraftStep` record
- `QuestDraft` class with Add/Remove/Replace
- `IDraftStorage` interface
- `FakeDraftStorage` in `QuestForge.Adapters.Fakes`
- `IClock` interface (if not already present) + `FakeClock`
- `DraftManager` with auto-save timing
- `QuestDraft.ToQuestDefinition` serializer

**Done when:** all 20 draft tests pass, including the round-trip via `QuestForgeJsonContext`.

### Step 3 — `DraftValidator`

Reuses Phase 1-2 predicate parser. **All §5.4 tests (7 tests) become writable.**

- `DraftValidator.Validate(draft) → (errors, warnings)`
- Error codes E1-E6, warning codes W1-W5

**Done when:** all 7 validation tests pass and the validator integrates with the existing predicate-parser entrypoint without duplicating grammar logic.

### Step 4 — `SnapshotAggregator`

Extracted pure-logic portion of `AuthoringHost`. **All §5.5 tests (5 tests) become writable.**

- `SnapshotAggregator` class implementing the snapshot-cache state machine
- Subset of `IAuthoringObserver` methods that mutate cached snapshot

**Done when:** all 5 aggregator tests pass.

### Step 5 — `FileDraftStorage` (Dalamud-side I/O)

Not unit-tested directly; simple file I/O wrapping `IDraftStorage`. Test manually by reading/writing to a temp directory in a one-off integration test under `QuestForge.Engine.Tests/Integration/` if desired.

**Done when:** `FileDraftStorage.Save` round-trips through `FakeDraftStorage`-equivalent tests, with the path-conventions from `AUTHORING.md` §6.2.

### Step 6 — `AuthoringHost`

Implements `IAuthoringObserver`, owns `DraftManager`, `StepInferenceEngine`, `SnapshotAggregator`. Wires Dalamud events via a thin adapter inside `QuestForge.Plugin`.

**Done when:** the host correctly routes Dalamud events to the aggregator, exposes `OpenRecordModal`/`PreviewInference`/`RecordStep`, and §5.6 (end-to-end test 46) passes against a script that drives `IAuthoringObserver` directly.

### Step 7 — Debug panels (ImGui)

`PlayerStatePanel`, `QuestStatePanel`, `InteractionPanel`. No automated tests; manual smoke test by launching the plugin and observing the panels populate correctly.

**Done when:** all three panels render and update on the 250ms heartbeat with copy-as-JSON buttons functional.

### Step 8 — Authoring UI (ImGui)

`AuthoringSessionPanel`, `RecordStepModal`, `StepEditModal`, `ExportDialog`. No automated tests; manual smoke test by walking through the §0 deliverable.

**Done when:** an author can play one quest end-to-end in Author mode and produce a JSON file that loads via `QuestFileLoader.Load` without errors.

### Step 9 — Export + pre-export validation

Wire `[📤 Export draft]` to `DraftValidator` + `ToQuestDefinition` + filesystem write. Reuses Step 3.

**Done when:** export refuses to write when validation errors exist (unless the author confirms "Export anyway"), and the exported file passes Phase 1's schema validator and Phase 2's predicate parser.

---

## 8. Handoff to Tester

The Tester reads §5 and writes failing xUnit tests for each named scenario **before** any code in §7 is implemented. Test file layout:

```
QuestForge.Engine.Tests/
  Authoring/
    StepInferenceEngineTests.cs        // §5.1 — 13 tests
    QuestDraftTests.cs                 // §5.2 — 11 tests
    DraftSerializationTests.cs         // §5.3 — 8 tests
    DraftValidatorTests.cs             // §5.4 — 7 tests
    SnapshotAggregatorTests.cs         // §5.5 — 5 tests
    AuthoringEndToEndTests.cs          // §5.6 — 1 test
```

`FakeDraftStorage` and `FakeClock` belong in `QuestForge.Adapters.Fakes` (a new `Authoring/` subfolder) so they're available to the Engine.Tests project.

---

✅ READY FOR TEST CREATION

Tester: Write comprehensive test suite from these behaviors.
- Happy paths: 20 scenarios
- Edge cases: 20 scenarios
- Error cases: 5 scenarios
- End-to-end: 1 scenario
- Expected total: ~46 tests
