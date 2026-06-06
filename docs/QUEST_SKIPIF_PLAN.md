# Quest-Level SkipIf Plan

**Status:** ready to implement
**Input docs:** SCHEMA.md (predicate language), ARCHITECTURE.md (three-layer boundary)
**Output:** Scheduler skips mutually-exclusive starting city quests instead of attempting quests in unreachable zones.
**Phase dependencies:** Phase 11 (scheduler and predicate evaluator already exist and are in use)

---

## Problem statement

FFXIV's three starting city quests (65643 Coming to Limsa Lominsa, 66130 Coming to Ul'dah, 66104 Coming to Gridania/Close to Home) are mutually exclusive -- a character completes exactly one based on starting city. Lumina's prerequisite data does not encode this exclusivity, so the scheduler treats all three as available for a new character and attempts quests in unreachable zones.

A secondary bug compounds the problem: `DalamudQuestState.WhyUnavailable` evaluates `missingPrereqs.Count == nonZeroPrereqCount` for `PreviousQuestJoin=1` (AtLeastOne). When a quest has zero non-zero prerequisites, this becomes `0 == 0 = true`, falsely marking prerequisites as incomplete.

---

## Architectural decisions

### QS1 -- SkipIf is a nullable string on QuestDefinition, not an ExpectValue

**Decision:** `public string? SkipIf { get; init; }` on `QuestDefinition` in both `questforge` and `questforge-tools` Schema projects. This matches the existing `QuestSequence.SkipIf` field (line 50 of `QuestDefinition.cs`), which is also a bare `string?` evaluated via `ExpectEvaluator.Evaluate(string?, ct)`.

**Rejected alternative:** Using `ExpectValue?` (the structured all/any form). `QuestSequence.SkipIf` already uses bare string, and the predicate language supports `or`/`and` connectives natively, making the structured form unnecessary for this use case.

**C# surface:**
```csharp
// QuestForge.Schema/QuestDefinition.cs
public record class QuestDefinition
{
    // ... existing fields ...
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkipIf { get; init; }
}
```

**What breaks if violated:** Using `ExpectValue?` would require a custom JSON shape for quest-level skipIf that differs from sequence-level skipIf, creating an inconsistency in the authoring experience.

### QS2 -- IQuestDataProvider gains GetSkipIf(QuestId)

**Decision:** Add `string? GetSkipIf(QuestId quest)` to `IQuestDataProvider`. The data provider reads skipIf from the quest file at index time, not from Lumina. Returns `null` for unknown quest IDs or quests without skipIf.

**Rejected alternative:** Passing `QuestDefinition` objects through the scheduler. The scheduler deliberately works with decomposed fields via `IQuestDataProvider` to avoid coupling to the full schema type.

**C# surface:**
```csharp
// IQuestDataProvider.cs
string? GetSkipIf(QuestId quest);
```

```csharp
// LuminaQuestDataProvider.cs -- Entry record gains SkipIf field
private sealed record Entry(
    int? Tier, uint ClassJobCategoryId, int RequiredLevel,
    IReadOnlyList<QuestId> Prerequisites, PrerequisiteJoin PrereqJoin,
    int SortKey, string Category, string? SkipIf);
```

```csharp
// FakeQuestDataProvider.cs -- QuestEntry record gains SkipIf field
private sealed record QuestEntry(
    int? Tier, int SortKey, JobId? ClassJob,
    QuestId[] Prerequisites, PrerequisiteJoin Join, string? SkipIf);

// Builder gains skipIf parameter
public FakeQuestDataProvider WithQuest(
    QuestId id, int? tier, int sortKey = 0, JobId? classJob = null,
    QuestId[]? prereqs = null, PrerequisiteJoin join = PrerequisiteJoin.All,
    string? skipIf = null)
```

**Testability:** `FakeQuestDataProvider` exposes skipIf via the existing builder pattern; no new fake class needed.

### QS3 -- Scheduler evaluates skipIf via a predicate delegate, not a full ExpectEvaluator

**Decision:** The scheduler receives a `Func<string, CancellationToken, Task<bool>>?` delegate (nullable; null = no predicate evaluation = no skipping). This avoids pulling `PredicateEvaluator`, `IGameStateProvider`, and `PredicateParser` into the scheduler's constructor, which would be a large dependency expansion for a single field check.

**Rejected alternative (A):** Injecting `ExpectEvaluator` directly. This would require the scheduler to depend on `PredicateParser` and `PredicateEvaluator`, both of which live in `QuestForge.Engine.Predicates`. The scheduler is in the same project so no assembly boundary issue, but the constructor already has 5 parameters and `ExpectEvaluator` pulls in `IGameStateProvider` + `IQuestState` which the scheduler already has -- the delegate avoids the redundant surface.

**Rejected alternative (B):** A new `ISkipIfEvaluator` interface. Over-abstraction for one call site; a delegate is sufficient and testable.

**C# surface:**
```csharp
// QuestScheduler constructor gains:
Func<string, CancellationToken, Task<bool>>? evaluateSkipIf = null

// In TrySelectCandidate, before any IsQuestComplete/IsQuestAvailable calls:
var skipIf = _questData.GetSkipIf(q);
if (skipIf is not null && _evaluateSkipIf is not null)
{
    try
    {
        if (await _evaluateSkipIf(skipIf, ct))
        {
            _logger.LogDebug("Quest {QuestId} skipped by skipIf predicate.", q);
            return null;
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Quest {QuestId} skipIf evaluation failed; treating as not-skipped.", q);
    }
}
```

**What breaks if violated:** Making `evaluateSkipIf` non-nullable would break every existing test that constructs a `QuestScheduler` without predicate support.

**Testability:** Tests pass a simple lambda `(pred, ct) => Task.FromResult(pred.Contains("66130"))` or similar; no need to wire up the full predicate parser for scheduler unit tests.

### QS4 -- SkipIf evaluation failures are non-fatal (treat as not-skipped)

**Decision:** If the skipIf predicate fails to parse or evaluate, log a warning and treat the quest as not-skipped. This prevents a malformed quest file from permanently blocking the scheduler.

**Rejected alternative:** Treating parse failure as "skip" (too aggressive; hides authoring bugs) or as a hard error (would halt the scheduler for one bad quest file).

### QS5 -- SkipIf is checked first in TrySelectCandidate, before IsQuestComplete

**Decision:** The skipIf check goes at the very top of `TrySelectCandidate`, before the `IsQuestComplete` call. Rationale: skipIf is a quest-file-level exclusion that should prevent all game-state queries for quests that the author has declared irrelevant. This also prevents the scheduler from descending into prerequisite chains for skipped quests.

**What breaks if violated:** Checking skipIf after IsQuestComplete wastes async calls to game state for quests that should never be considered.

### QS6 -- The 0==0 fix is a guard clause in DalamudQuestState.WhyUnavailable

**Decision:** Add an early guard: when `nonZeroPrereqCount == 0`, `prerequisiteIncomplete` is `false` regardless of join type. This fixes the degenerate case without changing the logic for quests that actually have prerequisites.

**C# surface:**
```csharp
// DalamudQuestState.cs, after computing nonZeroPrereqCount:
var prerequisiteIncomplete = nonZeroPrereqCount == 0
    ? false
    : row.PreviousQuestJoin == 0
        ? missingPrereqs.Count > 0
        : missingPrereqs.Count == nonZeroPrereqCount;
```

**Testability:** This is in the Dalamud adapter (requires game state). The fix is simple enough that code review suffices; no unit test is practical without mocking Lumina sheets. The scheduler tests in QS7-QS8 exercise the downstream effect.

### QS7 -- QuestFileIndex passes skipIf through to LuminaQuestDataProvider

**Decision:** `QuestFileIndex` already reads the full `QuestDefinition` to extract `Category`. It will additionally extract `SkipIf` and pass it alongside category data. The constructor parameter changes from `IReadOnlyDictionary<QuestId, string>` to a richer type.

**C# surface:**
```csharp
// QuestFileIndex.cs
internal IReadOnlyDictionary<QuestId, QuestFileMetadata> Metadata => _metadataById;

internal record QuestFileMetadata(string Category, string? SkipIf);
```

```csharp
// LuminaQuestDataProvider constructor changes:
public LuminaQuestDataProvider(
    IDataManager dataManager,
    IReadOnlyDictionary<QuestId, QuestFileMetadata> questMetadata)
```

**Rejected alternative:** A second dictionary `IReadOnlyDictionary<QuestId, string?>` for skipIf. Two parallel dictionaries are fragile; a single metadata record is cleaner.

### QS8 -- Plugin.cs wires the delegate from the existing ExpectEvaluator

**Decision:** `Plugin.cs` (or `EngineHost.cs`) constructs the delegate by capturing the existing `ExpectEvaluator` instance:

```csharp
Func<string, CancellationToken, Task<bool>> evaluateSkipIf =
    (predicate, ct) => expectEvaluator.Evaluate(predicate, ct);
```

This is passed to the `QuestScheduler` constructor. No new types or wiring needed.

---

## Dependency graph

```
1. Schema change (both repos, parallel)
   a. questforge: QuestDefinition.SkipIf
   b. questforge-tools: QuestDefinition.SkipIf (mirror)

2. IQuestDataProvider.GetSkipIf + FakeQuestDataProvider (depends on 1a)

3. QuestScheduler skipIf evaluation (depends on 2)

4. LuminaQuestDataProvider + QuestFileIndex metadata (depends on 1a, 2)

5. DalamudQuestState 0==0 fix (independent of 1-4)

6. Plugin.cs wiring (depends on 3, 4)

7. Quest data files (questforge-data, depends on 1b)
```

---

## Task breakdown

### Task 1 -- Schema: add SkipIf to QuestDefinition

**Deliverable:** Add `SkipIf` property to `QuestDefinition` in both repos.

```csharp
// QuestForge.Schema/QuestDefinition.cs (questforge repo)
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? SkipIf { get; init; }
```

Same change in `questforge-tools/QuestForge.Schema/QuestDefinition.cs`.

### Task 2 -- IQuestDataProvider: add GetSkipIf

**Deliverable:** New method on the interface + fake implementation.

```csharp
// IQuestDataProvider.cs
string? GetSkipIf(QuestId quest);
```

```csharp
// FakeQuestDataProvider.cs
public FakeQuestDataProvider WithQuest(
    QuestId id, int? tier, int sortKey = 0, JobId? classJob = null,
    QuestId[]? prereqs = null, PrerequisiteJoin join = PrerequisiteJoin.All,
    string? skipIf = null)
{
    _quests[id] = new QuestEntry(tier, sortKey, classJob, prereqs ?? [], join, skipIf);
    return this;
}

public string? GetSkipIf(QuestId quest)
    => _quests.TryGetValue(quest, out var e) ? e.SkipIf : null;
```

### Task 3 -- QuestScheduler: evaluate skipIf

**Deliverable:** New constructor parameter + skipIf check in `TrySelectCandidate`.

```csharp
// New constructor parameter (optional, default null):
Func<string, CancellationToken, Task<bool>>? evaluateSkipIf = null

// At top of TrySelectCandidate, before IsQuestComplete:
var skipIf = _questData.GetSkipIf(q);
if (skipIf is not null && _evaluateSkipIf is not null)
{
    try
    {
        if (await _evaluateSkipIf(skipIf, ct))
        {
            _logger.LogDebug("Quest {QuestId} skipped by skipIf predicate.", q);
            return null;
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Quest {QuestId} skipIf evaluation failed; treating as not-skipped.", q);
    }
}
```

### Task 4 -- LuminaQuestDataProvider + QuestFileIndex metadata

**Deliverable:** `QuestFileIndex` extracts skipIf, passes metadata record to `LuminaQuestDataProvider`.

### Task 5 -- DalamudQuestState: fix 0==0 degenerate case

**Deliverable:** Guard clause in `WhyUnavailable`.

```csharp
var prerequisiteIncomplete = nonZeroPrereqCount == 0
    ? false
    : row.PreviousQuestJoin == 0
        ? missingPrereqs.Count > 0
        : missingPrereqs.Count == nonZeroPrereqCount;
```

### Task 6 -- Plugin wiring

**Deliverable:** Wire `evaluateSkipIf` delegate from existing `ExpectEvaluator` into `QuestScheduler` constructor.

### Task 7 -- Quest data: add skipIf to starting city quests (questforge-data)

**Deliverable:** Add `skipIf` field to quest JSON files:

- `65643-coming-to-limsa-lominsa.json`: `"skipIf": "isQuestComplete(66130) or isQuestComplete(66104)"`
- `66130-coming-to-uldah.json`: `"skipIf": "isQuestComplete(65643) or isQuestComplete(66104)"`
- `66104-close-to-home-gladiator.json`: `"skipIf": "isQuestComplete(65643) or isQuestComplete(66130)"`

Note: Quest 66104 is "Close to Home" (Gridania start), despite the filename saying "gladiator". The Gridania starting quest's actual ID should be verified. The three mutually-exclusive "Coming to X" quests are 65643 (Limsa), 66130 (Ul'dah), and the Gridania equivalent. If 66104 is actually the Ul'dah class quest (Close to Home - Gladiator), the correct Gridania quest ID needs to be looked up.

---

## Given-When-Then specifications

### S1 -- SkipIf true: quest is skipped entirely

> **Given** three Tier 3 quests: Q(100) with skipIf="isQuestComplete(200)", Q(101) with no skipIf, Q(102) with no skipIf. Q(100) sortKey=0, Q(101) sortKey=1, Q(102) sortKey=2. All are available (not complete, not locked).
> **And** the evaluateSkipIf delegate returns true for any predicate containing "200".
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `Q(101)` -- Q(100) was skipped, Q(101) is the next available.

### S2 -- SkipIf false: quest is NOT skipped

> **Given** one Tier 3 quest Q(100) with skipIf="isQuestComplete(200)". Q(100) is available.
> **And** the evaluateSkipIf delegate returns false for all predicates.
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `Q(100)` -- skipIf evaluated to false, quest proceeds normally.

### S3 -- SkipIf null: no evaluation, quest proceeds

> **Given** one Tier 3 quest Q(100) with no skipIf (null). Q(100) is available.
> **And** an evaluateSkipIf delegate is provided.
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `Q(100)`. The evaluateSkipIf delegate is never called (verify via call count if using a tracking lambda).

### S4 -- SkipIf evaluation throws: quest is NOT skipped (non-fatal)

> **Given** one Tier 3 quest Q(100) with skipIf="badPredicate()". Q(100) is available.
> **And** the evaluateSkipIf delegate throws `Exception("parse error")` for this predicate.
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `Q(100)` -- the exception is caught, quest proceeds as if skipIf were null.

### S5 -- No evaluateSkipIf delegate: skipIf field ignored

> **Given** one Tier 3 quest Q(100) with skipIf="isQuestComplete(200)". Q(100) is available.
> **And** the QuestScheduler was constructed with `evaluateSkipIf: null`.
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `Q(100)` -- skipIf is present but no evaluator was provided, so the quest is not skipped.

### S6 -- SkipIf prevents prerequisite chain descent

> **Given** two Tier 3 quests: Q(100) with skipIf="isQuestComplete(200)" (sortKey=0), Q(101) available (sortKey=1).
> **And** Q(100) is NOT complete and NOT available (locked behind prereqs).
> **And** the evaluateSkipIf delegate returns true for Q(100)'s skipIf.
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `Q(101)`. The scheduler does NOT call `IsQuestComplete` or `WhyUnavailable` for Q(100) -- the skipIf check short-circuits before any game-state queries.

### S7 -- Mutual exclusion: simulates starting city scenario

> **Given** three Tier 3 quests modeling starting cities:
> - Q(65643) skipIf="isQuestComplete(66130) or isQuestComplete(66104)", sortKey=0
> - Q(66130) skipIf="isQuestComplete(65643) or isQuestComplete(66104)", sortKey=1
> - Q(66104) skipIf="isQuestComplete(65643) or isQuestComplete(66130)", sortKey=2
>
> **And** Q(65643) is complete. Q(66130) and Q(66104) are available.
> **And** the evaluateSkipIf delegate evaluates `isQuestComplete(N)` by checking a set `{65643}`.
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `null` (no quest to run) -- Q(65643) is complete, Q(66130) and Q(66104) are skipped because `isQuestComplete(65643)` is true.

### S8 -- Existing tests still pass with null evaluateSkipIf

> **Given** all existing `QuestSchedulerTests` continue to construct the scheduler without the `evaluateSkipIf` parameter (it defaults to null).
>
> **When** the existing test suite runs.
>
> **Then** all existing tests pass unchanged. The optional parameter does not break binary compatibility.

### S9 -- SkipIf on Tier 1 quest

> **Given** a Tier 1 quest Q(100) with skipIf="isQuestComplete(200)" and classJob matching current job.
> **And** the evaluateSkipIf delegate returns true.
> **And** a Tier 3 quest Q(300) is available.
>
> **When** `NextQuestToRun` is called.
>
> **Then** the result is `Q(300)` -- the Tier 1 quest was skipped, scheduler falls through to Tier 3.

### S10 -- FakeQuestDataProvider.GetSkipIf returns null for unregistered quest

> **Given** a `FakeQuestDataProvider` with no quests registered.
>
> **When** `GetSkipIf(Q(999))` is called.
>
> **Then** the result is `null`.

### S11 -- Schema round-trip: skipIf serializes and deserializes

> **Given** a `QuestDefinition` with `SkipIf = "isQuestComplete(66130) or isQuestComplete(66104)"`.
>
> **When** serialized to JSON and deserialized back.
>
> **Then** the deserialized `SkipIf` matches the original string. The JSON output contains `"skipIf": "isQuestComplete(66130) or isQuestComplete(66104)"`. When `SkipIf` is null, the `skipIf` key is absent from JSON output (due to `JsonIgnoreCondition.WhenWritingNull`).

---

## Implementation order

### Phase A -- Schema + interface (1-2 hours)

1. Add `SkipIf` to `QuestDefinition` in both repos (Task 1)
2. Add `GetSkipIf` to `IQuestDataProvider` (Task 2)
3. Update `FakeQuestDataProvider` with skipIf support (Task 2)
4. Write round-trip test for QuestDefinition with skipIf (S11)

**Done before Phase B:** Schema compiles in both repos. `FakeQuestDataProvider` has `GetSkipIf`.

### Phase B -- Scheduler logic + tests (2-3 hours)

1. Write failing tests for S1-S10
2. Add `evaluateSkipIf` parameter to `QuestScheduler` constructor
3. Implement skipIf check in `TrySelectCandidate`
4. All tests green

**Done before Phase C:** `dotnet test QuestForge.Engine.Tests` passes with new scheduler tests.

### Phase C -- Dalamud wiring (1-2 hours)

1. Fix `DalamudQuestState.WhyUnavailable` 0==0 bug (Task 5)
2. Update `QuestFileIndex` to extract skipIf metadata (Task 4, QS7)
3. Update `LuminaQuestDataProvider` constructor and `GetSkipIf` impl (Task 4)
4. Wire delegate in Plugin.cs / EngineHost.cs (Task 6)

**Done before Phase D:** Plugin compiles with skipIf wiring.

### Phase D -- Quest data (30 minutes)

1. Add `skipIf` to starting city quest JSON files (Task 7)
2. Verify quest data passes validation (`qf-validate`)

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests` passes with all new scheduler skipIf tests (S1-S10)
2. Schema round-trip test passes for QuestDefinition with skipIf (S11)
3. All existing `QuestSchedulerTests` pass unchanged (S8 -- backward compatibility)
4. `DalamudQuestState.WhyUnavailable` no longer returns `PrerequisiteIncomplete=true` for quests with zero non-zero prerequisites and `PreviousQuestJoin=1`
5. Starting city quest JSON files contain `skipIf` predicates
6. Plugin compiles with skipIf evaluation wired through to the scheduler

---

## Exclusions

- **Validator rules for skipIf predicates** -- predicate syntax validation is a Phase 2+ concern; not adding new validator rules in this slice.
- **Authoring inference for skipIf** -- no UI or inference engine changes; skipIf is hand-authored.
- **DraftValidator changes** -- no new E#/W# rules for quest-level skipIf.
- **questforge-tools trace/fixture changes** -- skipIf has no trace emission footprint.
- **SkipIf on manual chain (Tier 0)** -- the manual chain is user-specified and should not be auto-skipped. SkipIf only applies to auto-selected quests (Tiers 1, 3, 4, 5).

---

```
READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in S1-S11.
- Happy paths: 4 scenarios (S1, S2, S3, S7)
- Edge cases: 4 scenarios (S5, S6, S9, S10)
- Error cases: 1 scenario (S4)
- Backward compat: 1 scenario (S8)
- Schema: 1 scenario (S11)
- Expected total: ~11 tests in QuestForge.Engine.Tests + QuestForge.Schema.Tests
```
