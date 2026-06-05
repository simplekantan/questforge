# Fragment Authoring Plan: Record and Export Reusable Step Fragments

**Status:** ready to implement
**Input docs:** docs/SCHEMA.md (FragmentDefinition), docs/AUTHORING.md, issue #165
**Output:** `/qf author-fragment <fragmentId>` records steps and exports FragmentDefinition JSON files
**Phase dependencies:** Phase 9 (authoring infrastructure) -- complete; Phase 11 (corpus expansion) -- in progress

---

## Dependency graph

All changes land in the `questforge` repo. No tools-repo or data-repo changes are required for v1.

```
QuestForge.Engine/Authoring/
  FragmentDraft.cs          (NEW)  -- parallel to QuestDraft
  FragmentDraftManager.cs   (NEW)  -- separate manager for fragment drafts
  IFragmentDraftStorage.cs  (NEW)  -- storage interface for fragment drafts
  DraftValidator.cs         (MOD)  -- add ValidateFragment(FragmentDraft) overload

QuestForge.Adapters.Dalamud/Authoring/
  FileFragmentDraftStorage.cs  (NEW)  -- file-backed impl of IFragmentDraftStorage

QuestForge.Plugin/Authoring/
  AuthoringHost.cs          (MOD)  -- add fragment mode support

QuestForge.Plugin/Commands/
  QfCommand.cs              (MOD)  -- add /qf author-fragment <id>

QuestForge.Plugin/UI/Authoring/
  AuthoringSessionPanel.cs  (MOD)  -- fragment mode rendering
  ExportDialog.cs           (MOD)  -- fragment export path

QuestForge.Engine.Tests/Authoring/
  FragmentDraftTests.cs     (NEW)  -- unit tests for FragmentDraft
  DraftValidatorFragmentTests.cs (NEW)  -- validator tests for fragment drafts
```

**Build order:** FragmentDraft + IFragmentDraftStorage first, then FragmentDraftManager, then DraftValidator fragment overload, then plugin wiring.

---

## Architectural decisions

### FA1 -- FragmentDraft is a separate class, not a subclass of QuestDraft

**Alternatives considered:**
- (A) Make QuestDraft generic or abstract and share: rejected because QuestDraft has QuestId, Category, Expansion, Chain, and sequence-number grouping in MoveStepUp/MoveStepDown. Fragment drafts have none of this. Sharing would require nulling out half the fields or adding `if (isFragment)` guards throughout.
- (B) Make FragmentDraft extend QuestDraft: rejected because the inheritance would expose QuestId, Category, Expansion, and ToQuestDefinition() on fragment drafts, all of which are meaningless.
- (C) New standalone class: chosen. FragmentDraft has `FragmentId` (string), a flat step list, and `ToFragmentDefinition()`. The step mutation API (AddStep, RemoveStep, ReplaceStep, GetStep, MoveStepUp, MoveStepDown) mirrors QuestDraft but MoveStepUp/MoveStepDown operate on the flat list (no sequence-number grouping).

**What breaks if violated:** QuestDraft changes (new quest-specific metadata) would drag FragmentDraft along. Conversely, fragment-specific changes (future: parameter inference) would pollute QuestDraft.

**Concrete surface:**
```csharp
public sealed class FragmentDraft
{
    public string FragmentId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastModifiedAt { get; private set; }
    public IReadOnlyList<DraftStep> Steps { get; }

    public FragmentDraft(string fragmentId, DateTimeOffset createdAt);

    public void AddStep(DraftStep step, DateTimeOffset now);
    public bool RemoveStep(string stepId, DateTimeOffset now);
    public bool ReplaceStep(string stepId, DraftStep newStep, DateTimeOffset now);
    public DraftStep? GetStep(string stepId);
    public bool MoveStepUp(string stepId, DateTimeOffset now);
    public bool MoveStepDown(string stepId, DateTimeOffset now);

    public FragmentDefinition ToFragmentDefinition();

    internal static FragmentDraft CreateForTest(
        string fragmentId, DateTimeOffset createdAt,
        IEnumerable<DraftStep> steps);
}
```

**Testability:** Pure C# class in QuestForge.Engine, no Dalamud dependency, fully unit-testable.

### FA2 -- DraftStep reuse: SequenceNumber is set to 0 for fragment steps

DraftStep already has `SequenceNumber` as a required field. For fragment steps, this is always 0. The alternative of making SequenceNumber nullable was rejected because:
- It would require changing every existing DraftStep consumer to handle null.
- The value 0 is a harmless no-op for fragments (ToFragmentDefinition ignores it).
- Fragment mode in AuthoringHost simply hardcodes `SequenceNumber: 0` when building the DraftStep.

**What breaks if violated:** Nothing structurally. If someone sets a non-zero SequenceNumber on a fragment draft step, ToFragmentDefinition ignores it anyway (it reads Raw, not SequenceNumber). But the UI should not display sequence grouping.

### FA3 -- FragmentDraftManager is a separate class from DraftManager

**Alternatives considered:**
- (A) Generic `DraftManager<TDraft, TKey>`: rejected. QuestDraft is keyed by QuestId (value type), FragmentDraft is keyed by string. The generic constraints and dual-key storage complicate the code for no real benefit.
- (B) Add fragment methods to existing DraftManager: rejected. DraftManager has Dictionary<QuestId, QuestDraft> fields, dirty tracking keyed by QuestId, and auto-save logic all typed to QuestDraft. Adding string-keyed fragment fields would create a second parallel set of everything.
- (C) Separate FragmentDraftManager: chosen. It mirrors DraftManager's structure but is keyed by string (fragmentId) and manages FragmentDraft instances.

**Concrete surface:**
```csharp
public sealed class FragmentDraftManager
{
    public FragmentDraftManager(
        IFragmentDraftStorage storage,
        IClock clock,
        TimeSpan autoSaveInterval = default,
        int backupKeepCount = 5);

    public Task<FragmentDraft> GetOrCreate(string fragmentId, CancellationToken ct);
    public Task<FragmentDraft?> Get(string fragmentId, CancellationToken ct);
    public IReadOnlyList<string> ActiveDraftIds { get; }
    public Task SaveNow(string fragmentId, CancellationToken ct);
    public Task MaybeAutoSave(string fragmentId, CancellationToken ct);
    public Task DiscardDraft(string fragmentId, CancellationToken ct);
    public void MarkDirty(string fragmentId);
}
```

### FA4 -- IFragmentDraftStorage is a separate interface from IDraftStorage

IDraftStorage is typed to `QuestId` and `QuestDraft`. Rather than making a generic `IDraftStorage<TKey, TDraft>` (which would break the existing FileDraftStorage and every test that uses it), a parallel interface is cleaner:

```csharp
public interface IFragmentDraftStorage
{
    Task<Result<bool>> Save(string fragmentId, FragmentDraft draft, CancellationToken ct);
    Task<Result<FragmentDraft?>> Load(string fragmentId, CancellationToken ct);
    Task<Result<IReadOnlyList<string>>> ListDrafts(CancellationToken ct);
    Task<Result<bool>> Delete(string fragmentId, CancellationToken ct);
    Task<Result<bool>> CreateBackup(string fragmentId, CancellationToken ct);
}
```

**Storage path:** `{draftsRoot}/fragments/{fragmentId}.draft.json` where fragmentId slashes become directory separators. E.g. fragmentId `"travel/teleport-to-limsa"` stores at `{draftsRoot}/fragments/travel/teleport-to-limsa.draft.json`.

**What breaks if violated:** Nothing at compile time, but merging the two interfaces would require QuestDraft and FragmentDraft to share a base type or use object, which violates type safety.

### FA5 -- AuthoringHost gains fragment mode as a parallel track, not a mode enum value

**Alternatives considered:**
- (A) Add `AuthoringMode.FragmentAuthor` to the enum: complicates every existing switch on AuthoringMode (UI, commands, host). Every panel that checks `Mode == AuthoringMode.Author` would need `|| Mode == AuthoringMode.FragmentAuthor`.
- (B) Keep `AuthoringMode.Author` and add `bool IsFragmentMode` + `string? FragmentTarget`: chosen. The existing Author mode infrastructure (UIObserver, snapshot aggregator, inference engine, trace emission) works identically for fragments. The only differences are: (1) what draft is being built (FragmentDraft vs QuestDraft), (2) what the target label is, (3) how export works. A bool flag and a nullable string cleanly encode this.

**Concrete additions to AuthoringHost:**
```csharp
// New fields
private FragmentDraftManager _fragmentDraftManager;
public bool IsFragmentMode { get; private set; }
public string? FragmentTarget { get; private set; }
public FragmentDraftManager FragmentDraftManager => _fragmentDraftManager;

// New methods
public void EnterFragmentAuthorMode(string fragmentId);
// RecordStep checks IsFragmentMode and appends to FragmentDraft instead of QuestDraft
// ExitAuthoring resets IsFragmentMode and FragmentTarget
```

**What breaks if violated:** If we added a new enum value, every `switch (Mode)` in AuthoringSessionPanel, QfCommand, and the host itself would need updating, and missing a case would silently fall through to default (no compiler warning on non-exhaustive switch in C#).

### FA6 -- ToFragmentDefinition throws DraftSerializationException on null Raw, same as QuestDraft

Consistency with `QuestDraft.ToQuestDefinition()`. The author must confirm every step in the record modal before exporting.

```csharp
public FragmentDefinition ToFragmentDefinition()
{
    var nullStep = _steps.FirstOrDefault(s => s.Raw is null);
    if (nullStep is not null)
        throw new DraftSerializationException(
            $"Step '{nullStep.StepId}' has no Raw value ...");

    return new FragmentDefinition
    {
        SchemaVersion = "1.0.0",
        FragmentId = FragmentId,
        Parameters = [],   // v1: no parameter inference
        Steps = _steps.Select(s => s.Raw!).ToArray()
    };
}
```

### FA7 -- MoveStepUp/MoveStepDown in FragmentDraft operate on the flat list (no sequence grouping)

QuestDraft's move operations are scoped to steps within the same SequenceNumber. FragmentDraft has no sequences -- all steps are in one flat list. MoveStepUp swaps with the previous step; MoveStepDown swaps with the next step. Index 0 cannot move up; last index cannot move down.

```csharp
public bool MoveStepUp(string stepId, DateTimeOffset now)
{
    var index = _steps.FindIndex(s => s.StepId == stepId);
    if (index < 1) return false;
    (_steps[index], _steps[index - 1]) = (_steps[index - 1], _steps[index]);
    LastModifiedAt = now;
    return true;
}
```

### FA8 -- DraftValidator.ValidateFragment returns only fragment-applicable rules

Not all QuestDraft validation rules apply to fragments:
- **E1 (duplicate stepId):** applies
- **E2 (null Raw):** applies
- **E3/E5 (predicate parse):** applies
- **E4 (no accept step):** does NOT apply -- fragments are not quests
- **E6-E22 (step-type-specific zero checks):** all apply -- these are universal step validity checks
- **W1 (no expect):** applies
- **W2 (no notes + InferredFrom.None):** applies
- **W3 (last step is travel):** applies (fragment ending with travel is likely unintentional)
- **W4 (consecutive same-NPC talks):** applies
- **W6 (empty quest name):** does NOT apply
- **W7-W11 (spin-loop warnings):** all apply

New fragment-specific rules:
- **EF1 (empty FragmentId):** `FragmentId` is null or whitespace
- **EF2 (invalid FragmentId format):** must match `^[a-z][a-z0-9/-]*[a-z0-9]$` (lowercase slug with optional path separators, no trailing slash)
- **WF1 (no steps):** fragment draft has zero steps -- warn, not error (draft in progress)

**Concrete surface:**
```csharp
public sealed class DraftValidator
{
    // Existing
    public (IReadOnlyList<DraftValidationError>, IReadOnlyList<DraftValidationWarning>) Validate(QuestDraft draft);

    // New
    public (IReadOnlyList<DraftValidationError>, IReadOnlyList<DraftValidationWarning>) ValidateFragment(FragmentDraft draft);
}
```

Internally, the shared step-level validation is extracted into a private method `ValidateSteps(IReadOnlyList<DraftStep> steps)` that both `Validate` and `ValidateFragment` call. Quest-specific rules (E4, W6) are only in `Validate`. Fragment-specific rules (EF1, EF2, WF1) are only in `ValidateFragment`.

### FA9 -- FragmentId validation regex

`^[a-z][a-z0-9/-]*[a-z0-9]$` with minimum length 3 (at least start char, one middle char, end char). This matches existing fragment IDs like `travel/teleport-to-limsa-lominsa`. Single-segment IDs like `dismount` are also valid. The regex rejects: leading slash, trailing slash, uppercase, spaces, dots, consecutive slashes.

### FA10 -- No parameter support in v1

`ToFragmentDefinition()` always emits `Parameters = []`. Future work will add parameter inference (detecting which NPC IDs or positions vary across usages). This is explicitly out of scope.

---

## Task 1 -- FragmentDraft class

**File:** `QuestForge.Engine/Authoring/FragmentDraft.cs`

Implement the class specified in FA1. Key behaviors:
- AddStep throws InvalidOperationException on duplicate StepId (same as QuestDraft)
- MoveStepUp/MoveStepDown operate on flat list (FA7)
- ToFragmentDefinition throws DraftSerializationException on null Raw (FA6)
- CreateForTest bypasses AddStep uniqueness check (same as QuestDraft.CreateForTest)

## Task 2 -- IFragmentDraftStorage interface

**File:** `QuestForge.Engine/Authoring/IFragmentDraftStorage.cs`

Interface specified in FA4. Pure interface, no implementation in Engine project.

## Task 3 -- FragmentDraftManager class

**File:** `QuestForge.Engine/Authoring/FragmentDraftManager.cs`

Mirrors DraftManager (FA3). Same auto-save and backup rotation logic, keyed by string instead of QuestId.

## Task 4 -- DraftValidator fragment overload

**File:** `QuestForge.Engine/Authoring/DraftValidator.cs` (modify)

Extract shared step validation into `ValidateSteps`. Add `ValidateFragment(FragmentDraft)` method per FA8. Add EF1, EF2, WF1 rules.

## Task 5 -- FileFragmentDraftStorage (plugin layer)

**File:** `QuestForge.Adapters.Dalamud/Authoring/FileFragmentDraftStorage.cs`

Same DTO pattern as FileDraftStorage. Fragment drafts stored at `{draftsRoot}/fragments/{fragmentId}.draft.json`. Uses the same `DraftStepFileDto` and `SnapshotFileDto` as FileDraftStorage (extract to shared types or duplicate -- prefer duplication for now since the DTOs are internal).

## Task 6 -- AuthoringHost fragment mode (plugin layer)

**File:** `QuestForge.Plugin/Authoring/AuthoringHost.cs` (modify)

Add `EnterFragmentAuthorMode(string fragmentId)` per FA5. Modify `RecordStep` to check `IsFragmentMode` and route to FragmentDraft. Modify `ExitAuthoring` to clear fragment state.

## Task 7 -- QfCommand wiring (plugin layer)

**File:** `QuestForge.Plugin/Commands/QfCommand.cs` (modify)

Add `/qf author-fragment <fragmentId>` and `/qf author-fragment stop` command handlers.

## Task 8 -- UI changes (plugin layer)

**Files:** `AuthoringSessionPanel.cs`, `ExportDialog.cs` (modify)

AuthoringSessionPanel: when `IsFragmentMode`, show "Fragment: {fragmentId}" header, hide sequence grouping, render flat step list. ExportDialog: detect fragment mode, call `ToFragmentDefinition()`, write to `fragments/` directory.

---

## Validation rule table

| Rule | Code | Condition | Applies to |
|------|------|-----------|------------|
| E1 | `E1` | Duplicate stepId in draft | Quest + Fragment |
| E2 | `E2` | Step has null Raw | Quest + Fragment |
| E3 | `E3` | Predicate parse failure | Quest + Fragment |
| E4 | `E4` | No accept step | Quest only |
| E5 | `E5` | Unknown predicate function | Quest + Fragment |
| E6-E22 | `E6`-`E22` | Step-type-specific zero/invalid checks | Quest + Fragment |
| EF1 | `EF1` | FragmentId is null/whitespace | Fragment only |
| EF2 | `EF2` | FragmentId does not match `^[a-z][a-z0-9/-]*[a-z0-9]$` or length < 3 | Fragment only |
| W1 | `W1` | Step has no expect (with type exclusions) | Quest + Fragment |
| W2 | `W2` | No notes and InferredFrom.None | Quest + Fragment |
| W3 | `W3` | Last step is TravelStep | Quest + Fragment |
| W4 | `W4` | Consecutive same-NPC TalkSteps | Quest + Fragment |
| W6 | `W6` | Empty/placeholder quest name | Quest only |
| W7-W11 | `W7`-`W11` | Spin-loop warnings for stateless steps | Quest + Fragment |
| WF1 | `WF1` | Fragment has zero steps | Fragment only |

---

## Given-When-Then specifications

### 5.1 FragmentDraft -- step management

**F1 -- AddStep appends in order**
- Given: a new FragmentDraft with fragmentId `"travel/test-fragment"`
- When: three steps `"step-a"`, `"step-b"`, `"step-c"` are added in order
- Then: `draft.Steps` returns `["step-a", "step-b", "step-c"]` in that order
- Then: `draft.LastModifiedAt` reflects the timestamp of the last AddStep call

**F2 -- AddStep rejects duplicate StepId**
- Given: a FragmentDraft with one step `"step-a"`
- When: AddStep is called with another step whose StepId is `"step-a"`
- Then: InvalidOperationException is thrown with message containing `"step-a"`
- Then: Steps.Count remains 1

**F3 -- RemoveStep removes by StepId**
- Given: a FragmentDraft with steps `["step-a", "step-b", "step-c"]`
- When: RemoveStep(`"step-b"`, now) is called
- Then: returns true
- Then: Steps contains `["step-a", "step-c"]`
- Then: LastModifiedAt is updated

**F4 -- RemoveStep returns false for nonexistent StepId**
- Given: a FragmentDraft with steps `["step-a"]`
- When: RemoveStep(`"step-x"`, now) is called
- Then: returns false
- Then: Steps.Count remains 1
- Then: LastModifiedAt is NOT updated

**F5 -- ReplaceStep swaps step at same position**
- Given: a FragmentDraft with steps `["step-a", "step-b", "step-c"]`
- When: ReplaceStep(`"step-b"`, newStep with StepId `"step-b-v2"`, now) is called
- Then: returns true
- Then: Steps[1].StepId is `"step-b-v2"`
- Then: Steps.Count is still 3

**F6 -- ReplaceStep returns false for nonexistent StepId**
- Given: a FragmentDraft with steps `["step-a"]`
- When: ReplaceStep(`"step-x"`, newStep, now) is called
- Then: returns false

**F7 -- GetStep returns step by StepId**
- Given: a FragmentDraft with step `"step-a"`
- When: GetStep(`"step-a"`) is called
- Then: returns the DraftStep with StepId `"step-a"`

**F8 -- GetStep returns null for nonexistent StepId**
- Given: a FragmentDraft with step `"step-a"`
- When: GetStep(`"step-x"`) is called
- Then: returns null

### 5.2 FragmentDraft -- MoveStepUp / MoveStepDown (flat list)

**F9 -- MoveStepUp swaps with previous**
- Given: a FragmentDraft with steps `["step-a", "step-b", "step-c"]`
- When: MoveStepUp(`"step-b"`, now) is called
- Then: returns true
- Then: Steps order is `["step-b", "step-a", "step-c"]`

**F10 -- MoveStepUp at index 0 returns false**
- Given: a FragmentDraft with steps `["step-a", "step-b"]`
- When: MoveStepUp(`"step-a"`, now) is called
- Then: returns false
- Then: Steps order unchanged

**F11 -- MoveStepDown swaps with next**
- Given: a FragmentDraft with steps `["step-a", "step-b", "step-c"]`
- When: MoveStepDown(`"step-b"`, now) is called
- Then: returns true
- Then: Steps order is `["step-a", "step-c", "step-b"]`

**F12 -- MoveStepDown at last index returns false**
- Given: a FragmentDraft with steps `["step-a", "step-b"]`
- When: MoveStepDown(`"step-b"`, now) is called
- Then: returns false
- Then: Steps order unchanged

**F13 -- MoveStepUp with nonexistent StepId returns false**
- Given: a FragmentDraft with steps `["step-a"]`
- When: MoveStepUp(`"step-x"`, now) is called
- Then: returns false

### 5.3 FragmentDraft -- ToFragmentDefinition

**F14 -- ToFragmentDefinition produces valid FragmentDefinition**
- Given: a FragmentDraft with fragmentId `"travel/teleport-to-limsa"` and two steps: a TeleportStep and a TravelStep, both with non-null Raw
- When: ToFragmentDefinition() is called
- Then: returns a FragmentDefinition with:
  - SchemaVersion = `"1.0.0"`
  - FragmentId = `"travel/teleport-to-limsa"`
  - Parameters = empty array
  - Steps = two elements, both matching the Raw steps in order

**F15 -- ToFragmentDefinition throws on null Raw**
- Given: a FragmentDraft (via CreateForTest) with one step whose Raw is null
- When: ToFragmentDefinition() is called
- Then: throws DraftSerializationException with message containing the step's StepId

**F16 -- ToFragmentDefinition with empty steps**
- Given: a FragmentDraft with zero steps
- When: ToFragmentDefinition() is called
- Then: returns a FragmentDefinition with Steps = empty array (not an error at this layer; the validator catches this)

**F17 -- ToFragmentDefinition ignores SequenceNumber**
- Given: a FragmentDraft (via CreateForTest) with steps at SequenceNumber 0, 1, and 2
- When: ToFragmentDefinition() is called
- Then: Steps array contains all three steps in insertion order (not grouped by sequence)

### 5.4 DraftValidator -- fragment-specific rules

**F18 -- EF1: empty FragmentId**
- Given: a FragmentDraft with `FragmentId = ""`
- When: ValidateFragment is called
- Then: errors contains one entry with Code `"EF1"` and message mentioning "FragmentId"

**F19 -- EF1: whitespace-only FragmentId**
- Given: a FragmentDraft with `FragmentId = "  "`
- When: ValidateFragment is called
- Then: errors contains one entry with Code `"EF1"`

**F20 -- EF2: uppercase in FragmentId**
- Given: a FragmentDraft with `FragmentId = "Travel/MyFragment"`
- When: ValidateFragment is called
- Then: errors contains one entry with Code `"EF2"`

**F21 -- EF2: trailing slash**
- Given: a FragmentDraft with `FragmentId = "travel/"`
- When: ValidateFragment is called
- Then: errors contains one entry with Code `"EF2"`

**F22 -- EF2: too short (single char)**
- Given: a FragmentDraft with `FragmentId = "a"`
- When: ValidateFragment is called
- Then: errors contains one entry with Code `"EF2"` (minimum length 3)

**F23 -- EF2: valid fragmentId passes**
- Given: a FragmentDraft with `FragmentId = "travel/teleport-to-limsa-lominsa"`
- When: ValidateFragment is called (with valid steps)
- Then: no EF1 or EF2 errors

**F24 -- EF2: single-segment valid**
- Given: a FragmentDraft with `FragmentId = "dismount-before-talk"`
- When: ValidateFragment is called
- Then: no EF1 or EF2 errors

**F25 -- WF1: empty steps**
- Given: a FragmentDraft with zero steps
- When: ValidateFragment is called
- Then: warnings contains one entry with Code `"WF1"` mentioning "no steps"

**F26 -- E4 does NOT fire for fragments**
- Given: a FragmentDraft with one TalkStep (no AcceptStep)
- When: ValidateFragment is called
- Then: no error with Code `"E4"`

**F27 -- W6 does NOT fire for fragments**
- Given: a FragmentDraft (no QuestName concept)
- When: ValidateFragment is called
- Then: no warning with Code `"W6"`

**F28 -- Shared rules still fire for fragments: E1 (duplicate stepId)**
- Given: a FragmentDraft (via CreateForTest) with two steps both having StepId `"talk-a"`
- When: ValidateFragment is called
- Then: errors contains one entry with Code `"E1"`

**F29 -- Shared rules still fire for fragments: W1 (no expect)**
- Given: a FragmentDraft with a TalkStep that has Expect = null
- When: ValidateFragment is called
- Then: warnings contains one entry with Code `"W1"` for that step

**F30 -- Shared rules still fire for fragments: E6 (TalkStep NpcId == 0)**
- Given: a FragmentDraft with a TalkStep whose Target.NpcId == 0
- When: ValidateFragment is called
- Then: errors contains one entry with Code `"E6"`

### 5.5 FragmentDraftManager (if tested -- mirrors DraftManager)

**F31 -- GetOrCreate returns new draft when storage is empty**
- Given: a FragmentDraftManager with InMemory storage that returns null for any fragmentId
- When: GetOrCreate(`"travel/test"`, ct) is called
- Then: returns a FragmentDraft with FragmentId `"travel/test"` and zero steps

**F32 -- GetOrCreate caches and returns same instance**
- Given: a FragmentDraftManager
- When: GetOrCreate(`"travel/test"`, ct) is called twice
- Then: both calls return the same object reference

**F33 -- SaveNow persists to storage**
- Given: a FragmentDraftManager with a recording storage
- When: GetOrCreate, AddStep, MarkDirty, then SaveNow are called
- Then: storage.Save was called with the fragmentId and draft

---

## Implementation order

### Phase A -- Core types (engine layer)

1. `FragmentDraft` class (Task 1)
2. `IFragmentDraftStorage` interface (Task 2)
3. `FragmentDraftManager` class (Task 3)
4. Tests for FragmentDraft (F1-F17)

**Done gate:** All FragmentDraft unit tests pass.

### Phase B -- Validator extension

1. Refactor `DraftValidator` to extract `ValidateSteps` private method
2. Add `ValidateFragment(FragmentDraft)` method
3. Add EF1, EF2, WF1 rules
4. Tests for fragment validation (F18-F30)

**Done gate:** All fragment validator tests pass. Existing quest validator tests still pass (refactoring did not break them).

### Phase C -- Storage implementation (plugin layer)

1. `FileFragmentDraftStorage` (Task 5)
2. Tests for FragmentDraftManager with FakeFragmentDraftStorage (F31-F33)

**Done gate:** Storage round-trip works. Manager tests pass.

### Phase D -- Plugin wiring (plugin layer)

1. AuthoringHost fragment mode (Task 6)
2. QfCommand handler (Task 7)
3. UI changes (Task 8)

**Done gate:** `/qf author-fragment travel/test` enters fragment mode, steps can be recorded, Export produces valid FragmentDefinition JSON.

---

## Done criteria

1. `FragmentDraft` is a fully tested class in `QuestForge.Engine/Authoring/` with AddStep, RemoveStep, ReplaceStep, GetStep, MoveStepUp, MoveStepDown, and ToFragmentDefinition.
2. `DraftValidator.ValidateFragment(FragmentDraft)` runs all shared step rules (E1-E22, W1-W11) plus fragment-specific rules (EF1, EF2, WF1), and does NOT run quest-specific rules (E4, W6).
3. All existing `DraftValidatorTests` continue to pass unchanged (refactoring ValidateSteps did not break anything).
4. `FragmentDraftManager` manages fragment drafts with auto-save, backup rotation, and dirty tracking, keyed by string fragmentId.
5. `/qf author-fragment <fragmentId>` enters fragment authoring mode; `/qf author-fragment stop` exits.
6. AuthoringSessionPanel shows "Fragment: {fragmentId}" and a flat step list (no sequence grouping) when in fragment mode.
7. ExportDialog produces a valid `FragmentDefinition` JSON file in the `fragments/` directory.
8. At least 33 tests in `QuestForge.Engine.Tests/Authoring/` covering FragmentDraft and fragment validation.

---

## Exclusions -- what this plan does NOT include

- **Parameter inference:** ToFragmentDefinition always emits `Parameters = []`. Parameter support is deferred to a future issue.
- **Fragment nesting validation:** The structural validator's `structural/fragment-nested` rule (fragments cannot reference other fragments) is already implemented in the data-repo validator. This plan does not add DraftValidator checks for FragmentStep within a FragmentDraft. If needed, it can be added later.
- **Fragment-specific step inference:** The StepInferenceEngine is unchanged. Fragment steps are inferred identically to quest steps.
- **Tools-repo changes:** No `qf-validate` or `qf-trace` changes. Fragment drafts are an authoring concern, not a CI concern.
- **Fragment loading/execution changes:** The engine's fragment loading (EngineHost.LoadFragments) is unchanged. This plan only covers authoring (recording + export).
- **Trace emission for fragment authoring:** The existing trace events (action.submitted, action.completed, step.recorded) are emitted identically in fragment mode. No new event types are needed. The runId label will be `"author-fragment-{fragmentId}-{timestamp}"` to distinguish from quest authoring runs.
