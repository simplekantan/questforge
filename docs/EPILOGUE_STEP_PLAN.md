# Epilogue Steps -- Architect Spec

**Status:** Draft
**Phase:** 11 (Corpus Expansion)
**Feature:** Post-completion epilogue fragment execution
**Slice:** 1 of 4 (Architect Spec)
**Author:** QuestForge System Architect
**Date:** 2026-06-22

---

## 1. Header

**Input documents:**
- `docs/SCHEMA.md` -- quest definition schema, step types, fragment composition
- `docs/ARCHITECTURE.md` -- three-layer architecture, engine testability boundary
- `CLAUDE.md` -- fixed slice order, TDD role separation
- Plan file `abstract-sleeping-quiche.md` -- motivating context (class quest soul crystal problem)

**Output (CI behavior changes):**
- `dotnet test QuestForge.Schema.Tests` gains 2 new round-trip tests for `QuestDefinition.Epilogue`
- `dotnet test QuestForge.Engine.Tests` gains ~20 new tests covering epilogue cursor walk, fragment expansion, step dispatch, confirmed-step isolation, and edge cases
- All existing engine tests remain green (no regressions)

**Phase dependencies:**
- Fragment expansion (`ExpandSteps`) -- implemented (Phase 4)
- `NotifyEquipBestGearComplete` two-phase protocol -- implemented (Phase 11)
- `_fireOnceDispatchedIds` for RegisterGearsetStep -- implemented (Phase 11)
- All step types that epilogue steps reference (EquipGearForQuestStep, ChangeJobStep, EquipBestGearStep, RegisterGearsetStep, TeleportStep) -- implemented (Phase 11)
- `ProcessActiveResume` sub-loop pattern -- implemented (resume-point fragments)

---

## 2. Problem Statement

When a class quest completes (e.g., quest 66585 "Pride and Duty" awards the Warrior soul crystal), the engine's `IsQuestComplete` gate in `ResolveAction` (line 490-494) returns `Done` immediately. The scheduler then polls for the next quest -- but it cannot find one because the game requires the player to be on the new job. The necessary setup steps (equip soul crystal, change job, equip best gear, register gearset) have nowhere to run.

A fragment like `common/setup-warrior` encodes this sequence, but there is no mechanism to execute steps after `IsQuestComplete` returns true and before the engine emits `Done`.

**Motivating example:**
```json
{
  "id": 66585,
  "name": "Pride and Duty",
  "category": "class",
  "epilogue": [
    {
      "type": "fragment",
      "ref": "common/setup-warrior",
      "id": "epilogue-setup-warrior"
    }
  ],
  "sequences": [ ... ]
}
```

Where `common/setup-warrior` expands to:
```json
[
  { "type": "equip-gear-for-quest", "id": "equip-crystal", "itemIds": [4553] },
  { "type": "change-job", "id": "switch-to-war", "jobId": 3 },
  { "type": "equip-best-gear", "id": "gear-up" },
  { "type": "register-gearset", "id": "save-gearset" }
]
```

**Closest analogs in the engine:**
- `ProcessActiveResume` -- a secondary cursor walk (no sequence/zone gating, uses its own confirmed-step set, walks an expanded step array, dispatches via `ResolveActionForStep` for sync arms). Epilogue is structurally similar: a secondary walk after the primary quest completes.
- Fragment expansion via `ExpandSteps` -- epilogue reuses this unchanged.

---

## 3. Dependency Graph

```
QuestForge.Schema                  (Epilogue property on QuestDefinition)
    |
    v
QuestForge.Engine                  (epilogue state, ResolveEpilogueAction)
    |
    v
QuestForge.Engine.Tests            (~20 tests)
    |
QuestForge.Schema.Tests            (2 round-trip tests)
```

Build order: Schema -> Engine -> Tests. All in one PR.

---

## 4. Architectural Decisions

### EP1: Epilogue is a property on QuestDefinition, not a separate file

**Decision:** Add an optional `Epilogue` property (`Step[]?`) directly on `QuestDefinition`.

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public Step[]? Epilogue { get; init; }
```

**Alternatives considered:**
- Separate `EpilogueDefinition` file referenced by path. Rejected: epilogue is tightly coupled to the quest (it runs the instant that specific quest completes). Indirection adds file management burden for 1-4 steps. The fragment mechanism already handles shared logic (`common/setup-warrior`); the epilogue array is the quest-specific invocation site.
- A new field on `QuestSequence` (e.g., `PostCompletionSteps`). Rejected: epilogue runs after the entire quest completes, not after a single sequence block. Putting it on `QuestSequence` implies per-sequence semantics that do not exist.

**What breaks if violated:** If someone puts epilogue on `QuestSequence`, every sequence block could theoretically have a post-block epilogue, creating ambiguity about which one runs when the quest completes (the last one? all of them?). The quest-level placement makes it unambiguous: quest complete triggers exactly one epilogue.

**Testability:** Round-trip test on `QuestDefinition` serializes/deserializes the `Epilogue` field. Null omission confirmed via JSON absence check.

### EP2: Epilogue uses its own confirmed-step set, separate from `_confirmedStepIds`

**Decision:** Introduce `private readonly HashSet<string> _epilogueConfirmedStepIds = new()` alongside the existing `_confirmedStepIds`.

**Rationale:** The main `_confirmedStepIds` is cleared on sequence changes (line 584). During epilogue, no sequence changes should occur (the quest is already complete), but using a separate set provides defense-in-depth: even if some unexpected state change triggers the sequence-change clearing logic, epilogue progress is preserved.

**Concrete surface area:**
```csharp
private readonly HashSet<string> _epilogueConfirmedStepIds = new();
```

**Alternatives considered:**
- Reusing `_confirmedStepIds` with a guard that skips clearing during epilogue. Rejected: coupling epilogue to the clearing logic means a future change to the clearing code could regress epilogue. Separate set is ~1 line of code with zero coupling risk.

**What breaks if violated:** If `_confirmedStepIds` is shared, any adapter read that causes `_lastKnownSequence != currentSeq` during epilogue (game reporting sequence 255 for a completed quest, for instance) would clear all epilogue progress, causing an infinite loop of re-dispatching already-completed steps.

### EP3: Epilogue cursor walk reuses the existing async pre-arm chain, not `ResolveActionForStep`

**Decision:** The epilogue cursor walk (`ResolveEpilogueAction`) uses the same step-type dispatch pattern as the main cursor walk (the `if (step is XxxStep) { ... }` chain in `ResolveAction`). However, it does NOT call `ResolveAction` itself. Instead, it is a new private async method `ResolveEpilogueAction` that contains its own step-type dispatch matching the main loop's async pre-arms, limited to the step types that are meaningful in an epilogue context.

**Method signature:**
```csharp
private async Task<(EngineAction action, string? stepId)> ResolveEpilogueAction(
    Step[] epilogueSteps, WorldPosition? playerPos, CancellationToken ct)
```

**Why not extract a shared `ProcessStepDispatch` method:** The plan suggested extracting a shared method to avoid duplication. After reviewing the main cursor walk (lines 618-878), this is impractical. The main loop interleaves 15+ concerns: confirmed-step cursor, expect evaluation, skipIf, resume-point triggers, death recovery, combat defense, sequence-change detection, zone gating. Epilogue needs only 4 of these: confirmed-step cursor, expect evaluation, skipIf, and step-type dispatch. Extracting a shared method would require parameterizing all 11 conditional behaviors, resulting in a method signature with 8+ boolean/callback parameters -- worse than the duplication it avoids.

**Why not call `ResolveAction` and let the completion gate re-enter:** The completion gate (`IsQuestComplete` at line 490) returns `Done` immediately. Calling `ResolveAction` from epilogue would hit this gate and loop back to `Done`, creating an infinite cycle. The epilogue must bypass the completion gate entirely.

**Step types supported in epilogue:**
1. `EquipGearForQuestStep` -- soul crystal equip (primary use case)
2. `ChangeJobStep` -- job switch after crystal equip (primary use case)
3. `EquipBestGearStep` -- gear up on new job (primary use case, uses `NotifyEquipBestGearComplete`)
4. `RegisterGearsetStep` -- save the gearset (primary use case, fire-once via `_fireOnceDispatchedIds`)
5. `TeleportStep` -- travel after quest completion (secondary use case: quest ends far from where next quest starts)
6. All step types handled by `ResolveActionForStep` (sync switch) -- TravelStep, TalkStep, AcceptStep, TurnInStep, InteractObjectStep, AttunementStep, AethernetStep, HandOverItemStep, CutsceneStep, etc.
7. `OpenCoffersStep` -- open reward coffers after completion (secondary use case)

**Unsupported in epilogue (returns AwaitUser):**
- `CombatStep` -- no combat objectives after quest completion
- `DutyStep`, `DungeonTrialStep`, `SinglePlayerDutyStep` -- no instanced duties after quest completion
- `WaitStep` -- time-based waits are not meaningful in epilogue; they complicate state management for minimal benefit

Any step type not explicitly handled falls through to `ResolveActionForStep` for sync dispatch, matching the main loop's fallthrough at line 876-877. Unknown types hit the `_ => throw NotSupportedException` default arm, which is the correct behavior.

**What breaks if violated:** If someone tries to share the dispatch by calling `ResolveAction`, the `IsQuestComplete` gate creates an infinite `Done` loop. If someone adds a shared `ProcessStepDispatch` extraction, the resulting abstraction will be so parameterized it becomes harder to understand than the two separate loops.

**Testability:** Each supported step type gets its own test scenario in the epilogue test file, verifying that the correct `EngineAction` is emitted.

### EP4: Epilogue does NOT activate during NG+ replay

**Decision:** When `replayActive` is true, the `IsQuestComplete` gate is already skipped (line 490-496). The engine falls through to the live-sequence loop. Epilogue entry is gated on `IsQuestComplete` returning true, which only happens in the `!replayActive` branch. Therefore, epilogue never activates during NG+.

No additional code is needed for this -- it falls out naturally from the existing control flow. The test scenario (E11) verifies this invariant.

### EP5: `NotifyEquipBestGearComplete` routes to the correct confirmed-step set

**Decision:** Modify `NotifyEquipBestGearComplete` to route to `_epilogueConfirmedStepIds` when `_inEpilogue` is true:

```csharp
public void NotifyEquipBestGearComplete(string stepId)
{
    if (_inEpilogue)
        _epilogueConfirmedStepIds.Add(stepId);
    else
        _confirmedStepIds.Add(stepId);
}
```

**Rationale:** The two-phase protocol for `EquipBestGearStep` requires the host to call `NotifyEquipBestGearComplete(stepId)` after the gear equip finishes. During epilogue, this notification must target the epilogue confirmed set, not the main one.

**What breaks if violated:** If `NotifyEquipBestGearComplete` always writes to `_confirmedStepIds`, the epilogue cursor walk (which reads `_epilogueConfirmedStepIds`) will never see the confirmation. The `EquipBestGearStep` will re-dispatch indefinitely.

**Testability:** Test E9 verifies that `NotifyEquipBestGearComplete` during epilogue correctly confirms the step.

### EP6: `_fireOnceDispatchedIds` is shared between main loop and epilogue

**Decision:** `_fireOnceDispatchedIds` is NOT duplicated for epilogue. The same `HashSet<string>` is used.

**Rationale:** `_fireOnceDispatchedIds` is cleared only on sequence changes (line 585). During epilogue, no sequence changes occur (the quest is complete). The epilogue step IDs are scoped (e.g., `epilogue-setup-warrior:save-gearset`) and cannot collide with main-loop step IDs. Sharing the set is safe and avoids introducing a parallel set for a single-purpose use.

**What breaks if violated:** If someone duplicates the set, the two copies diverge if a `RegisterGearsetStep` runs in both the main loop and epilogue of the same quest (unlikely but possible). The shared set correctly prevents re-dispatch.

### EP7: `FindStepById` must also search epilogue steps

**Decision:** Extend `FindStepById` to search `_epilogueSteps` after searching sequences:

```csharp
private Step? FindStepById(string? stepId)
{
    if (stepId is null || _quest is null) return null;
    foreach (var seq in _quest.Sequences)
        foreach (var step in seq.Steps)
            if (step.Id == stepId) return step;
    if (_epilogueSteps is not null)
        foreach (var step in _epilogueSteps)
            if (step.Id == stepId) return step;
    return null;
}
```

**Rationale:** `FindStepById` is called by `ResolveObstacleRecovery` (navigation watchdog recovery). During epilogue, if a `TravelStep` stalls and the watchdog fires `CastReturn`, the recovery logic needs to find the step by ID to read its `Recover` field. Without this, recovery during epilogue navigation would fail silently (null step, default to `UseReturn`). This is acceptable but not ideal.

### EP8: `BeginRun` clears epilogue state

**Decision:** Add to `BeginRun`:

```csharp
_inEpilogue = false;
_epilogueConfirmedStepIds.Clear();
```

**Rationale:** Each run starts fresh. If a previous run was interrupted during epilogue, the next run must not inherit that epilogue state.

### EP9: Epilogue expansion happens in `StartQuest`

**Decision:** Expand epilogue steps during `StartQuest`, after sequence expansion, using the same `usageCount` dictionary:

```csharp
_epilogueSteps = quest.Epilogue is { Length: > 0 }
    ? ExpandSteps(quest.Epilogue, fragments, usageCount).ToArray()
    : null;
```

**Rationale:** Sharing `usageCount` across sequences and epilogue ensures globally unique scoped IDs when the same fragment is used in both contexts. For example, if `common/setup-warrior` is used in sequence 3 AND in the epilogue, the second usage gets `#2` scoping.

**What breaks if violated:** If epilogue expansion uses a separate `usageCount`, scoped IDs could collide with sequence-expanded IDs, causing the cursor to skip steps incorrectly.

### EP10: Epilogue does NOT support the navigation watchdog, resume points, death recovery, combat defense, or zone gating

**Decision:** The epilogue cursor walk is minimal:
1. Confirmed-step check (using `_epilogueConfirmedStepIds`)
2. Expect evaluation (confirm and skip if true)
3. SkipIf evaluation (skip but do not confirm)
4. Step-type dispatch (async pre-arms for step types that need them, sync fallthrough for the rest)
5. Returns `Done` when all steps are confirmed or skipped

**Not included:**
- Navigation watchdog (jump/return recovery) -- epilogue steps are short-range or non-navigational
- Resume-point fragments -- epilogue itself IS a kind of post-completion fragment; nesting is unnecessary
- Death recovery teleport -- if the player dies during epilogue, the steps are idempotent and will recover naturally on the next tick
- Combat defense -- no combat objectives exist in epilogue context
- Zone gating (`RequiredZone`) -- epilogue steps do not have zone prerequisites; they run wherever the player is when the quest completes
- Sequence-change detection -- the quest is complete; no more sequence advances occur

**Rationale:** The primary use case is 4 steps (equip crystal, change job, equip best gear, register gearset) that take < 5 seconds combined. Adding recovery infrastructure for this is over-engineering. If future quests need complex epilogue behavior, the feature can be extended.

### EP11: `_inEpilogue` is a boolean field, not an enum state

**Decision:** `private bool _inEpilogue;` -- a simple flag.

**Alternatives considered:**
- An `EnginePhase` enum (`Running`, `Epilogue`, `Done`). Rejected: the engine already has implicit phase tracking via `_quest != null` and `_runId != null`. Adding an enum creates a parallel state machine that must be kept in sync. A boolean is sufficient for the binary "are we in epilogue or not" question.

**What breaks if violated:** An enum would require updating every place that checks engine state (currently implicit). A boolean can be checked locally in `ResolveAction` and `NotifyEquipBestGearComplete` without touching any other code.

### EP12: Trace events during epilogue use the same `_runId`

**Decision:** Epilogue steps emit decision events and action events under the same `_runId` as the quest run. The `run.end` event is deferred until epilogue completes (or an `AwaitUser` is returned from epilogue).

**Concrete change:** The `Done` trace emission (line 410-413) moves to after epilogue resolution. If epilogue is active, `ResolveAction` returns the epilogue action (not `Done`), so the existing trace emission code paths work unchanged -- `Done` is only returned after epilogue finishes.

### EP13: Empty or null epilogue produces `Done` immediately -- zero behavioral change for existing quests

**Decision:** When `Epilogue` is null or empty (`Length == 0`), the engine behaves exactly as today: `IsQuestComplete` returns true, engine returns `Done`. No new code paths are entered. This is the regression guard.

```csharp
if (completeResult is Result<bool>.Success { Value: true })
{
    if (_epilogueSteps is { Length: > 0 } && !_inEpilogue)
    {
        _inEpilogue = true;
        // Fall through to ResolveEpilogueAction below
    }
    else if (!_inEpilogue)
    {
        return (new EngineAction.Done(), null, null);
    }
    // else: _inEpilogue is true -- fall through to ResolveEpilogueAction
}
```

---

## 5. Task Breakdown

### Task 1: Schema (QuestForge.Schema/QuestDefinition.cs)

**Deliverables:**

1. Add `Epilogue` property to `QuestDefinition`:
```csharp
[System.Text.Json.Serialization.JsonIgnore(
    Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
public Step[]? Epilogue { get; init; }
```

### Task 2: Engine state fields (QuestForge.Engine/QuestEngine.cs)

**Deliverables:**

1. New fields near line 75:
```csharp
private Step[]? _epilogueSteps;
private bool _inEpilogue;
private readonly HashSet<string> _epilogueConfirmedStepIds = new();
```

2. In `StartQuest` (after sequence expansion, around line 179):
```csharp
_epilogueSteps = quest.Epilogue is { Length: > 0 }
    ? ExpandSteps(quest.Epilogue, fragments, usageCount).ToArray()
    : null;
```

3. In `BeginRun` (around line 371):
```csharp
_inEpilogue = false;
_epilogueConfirmedStepIds.Clear();
```

### Task 3: Completion gate modification (QuestForge.Engine/QuestEngine.cs)

**Deliverables:**

1. Modify the `IsQuestComplete` gate (line 490-494) to enter epilogue instead of returning `Done`:
```csharp
if (!replayActive)
{
    var completeResult = await _questState.IsQuestComplete(questId, ct);
    if (completeResult is Result<bool>.Success { Value: true })
    {
        if (_epilogueSteps is { Length: > 0 } && !_inEpilogue)
        {
            _inEpilogue = true;
        }

        if (_inEpilogue)
        {
            return await ResolveEpilogueAction(_epilogueSteps!, playerPos, ct);
        }

        return (new EngineAction.Done(), null, null);
    }
}
```

Note: When `_inEpilogue` is already true (set on a previous tick), we skip `IsQuestComplete` re-check overhead and go directly to epilogue resolution. But structurally this is still inside the `!replayActive` block, so NG+ skips it entirely (EP4).

### Task 4: Epilogue cursor walk (QuestForge.Engine/QuestEngine.cs)

**Deliverables:**

1. New private method `ResolveEpilogueAction`:

```csharp
private async Task<(EngineAction action, string? stepId, WorldPosition? playerPos)>
    ResolveEpilogueAction(Step[] epilogueSteps, WorldPosition? playerPos, CancellationToken ct)
{
    // Read UiState once (needed by sync dispatch arms like CutsceneStep, TravelStep).
    var uiResult = await _gameState.GetUiState(ct);
    var ui = uiResult is Result<UiState>.Success { Value: var uiValue }
        ? uiValue
        : new UiState(false, false, false, false, false, false, false, false, null);

    if (ui.LoadingZone)
        return (new EngineAction.Wait("loading zone"), null, playerPos);

    // Refresh player position for implied navigation checks.
    var posResult = await _gameState.GetPlayerPosition(ct);
    playerPos = posResult is Result<WorldPosition>.Success { Value: var p }
        ? p : (WorldPosition?)null;

    foreach (var step in epilogueSteps)
    {
        // 1. Confirmed check.
        if (_epilogueConfirmedStepIds.Contains(step.Id))
            continue;

        // 2. Expect: if true, confirm and skip.
        if (step.Expect is not null && await _expectEvaluator.Evaluate(step.Expect, ct))
        {
            _epilogueConfirmedStepIds.Add(step.Id);
            continue;
        }

        // 3. SkipIf: skip without confirming.
        if (step.SkipIf is not null && await _expectEvaluator.Evaluate(step.SkipIf, ct))
            continue;

        // 4. Async pre-arms for step types that need them.
        //    These match the main loop's pattern exactly.
        if (step is EquipGearForQuestStep equipStep)
        {
            var equipAction = await ResolveEquipGear(equipStep, ct);
            if (equipAction is null)
            {
                _epilogueConfirmedStepIds.Add(step.Id);
                continue;
            }
            return (equipAction, step.Id, playerPos);
        }

        if (step is EquipBestGearStep bestGearStep)
        {
            var bestGearAction = await ResolveEquipBestGear(bestGearStep, ct);
            return (bestGearAction, step.Id, playerPos);
        }

        if (step is ChangeJobStep changeJobStep)
        {
            var changeJobAction = await ResolveChangeJob(changeJobStep, ct);
            if (changeJobAction is null)
            {
                _epilogueConfirmedStepIds.Add(step.Id);
                continue;
            }
            return (changeJobAction, step.Id, playerPos);
        }

        if (step is RegisterGearsetStep)
        {
            if (_fireOnceDispatchedIds.Contains(step.Id))
            {
                _epilogueConfirmedStepIds.Add(step.Id);
                continue;
            }
            var registerAction = await ResolveRegisterGearset((RegisterGearsetStep)step, ct);
            if (registerAction is not EngineAction.Wait)
                _fireOnceDispatchedIds.Add(step.Id);
            return (registerAction, step.Id, playerPos);
        }

        if (step is TeleportStep teleportStep)
        {
            var teleportAction = await ResolveTeleportAction(teleportStep, ct);
            return (teleportAction, step.Id, playerPos);
        }

        if (step is OpenCoffersStep openCoffersStep)
        {
            var openCoffersAction = await ResolveOpenCoffers(openCoffersStep, ct);
            return (openCoffersAction, step.Id, playerPos);
        }

        if (step is PurchaseItemStep purchaseStep)
        {
            var purchaseAction = await ResolvePurchaseAction(purchaseStep, playerPos, ct);
            return (purchaseAction, step.Id, playerPos);
        }

        if (step is UseActionStep useActionStep)
        {
            var useAction = await ResolveUseAction(useActionStep, playerPos, ct);
            return (useAction, step.Id, playerPos);
        }

        if (step is UseEmoteStep useEmoteStep)
        {
            var useEmote = await ResolveUseEmote(useEmoteStep, playerPos, ct);
            return (useEmote, step.Id, playerPos);
        }

        if (step is UseItemStep useItemStep)
        {
            var useItem = await ResolveUseItem(useItemStep, ct);
            return (useItem, step.Id, playerPos);
        }

        if (step is UseItemOnObjectStep useItemOnObjStep)
        {
            var useItemOnObj = await ResolveUseItemOnObject(useItemOnObjStep, playerPos, ct);
            return (useItemOnObj, step.Id, playerPos);
        }

        // 5. Sync fallthrough for all other step types.
        _lastResolvedStep = step;
        return (ResolveActionForStep(step, ui, playerPos), step.Id, playerPos);
    }

    // All epilogue steps confirmed or skipped -- epilogue complete.
    return (new EngineAction.Done(), null, playerPos);
}
```

### Task 5: NotifyEquipBestGearComplete routing

**Deliverables:**

1. Modify `NotifyEquipBestGearComplete`:
```csharp
public void NotifyEquipBestGearComplete(string stepId)
{
    if (_inEpilogue)
        _epilogueConfirmedStepIds.Add(stepId);
    else
        _confirmedStepIds.Add(stepId);
}
```

### Task 6: FindStepById extension

**Deliverables:**

1. Extend `FindStepById` to search `_epilogueSteps`:
```csharp
private Step? FindStepById(string? stepId)
{
    if (stepId is null || _quest is null) return null;
    foreach (var seq in _quest.Sequences)
        foreach (var step in seq.Steps)
            if (step.Id == stepId) return step;
    if (_epilogueSteps is not null)
        foreach (var step in _epilogueSteps)
            if (step.Id == stepId) return step;
    return null;
}
```

### Task 7: HarnessEngine.NotifyEquipBestGearComplete passthrough

The `HarnessEngine` wrapper already delegates `NotifyEquipBestGearComplete` to the inner engine (line 506 of EngineTestHarness.cs). No change needed -- the routing fix in Task 5 flows through automatically.

---

## 6. Given-When-Then Specs

### Schema Tests: `QuestForge.Schema.Tests/RoundTripTests.cs`

---

#### EP_T1: QuestDefinition with Epilogue serializes and deserializes

**Given:**
- A `QuestDefinition` with `Epilogue` containing one `EquipGearForQuestStep` (Id="equip-crystal", ItemIds=[4553])

**When:** Serialize the `QuestDefinition` to JSON via `QuestForgeJsonContext.QuestFileOptions`, then deserialize back

**Then:**
- Result is `QuestDefinition`
- `Epilogue` is not null
- `Epilogue.Length` is 1
- `Epilogue[0]` is `EquipGearForQuestStep`
- Cast to `EquipGearForQuestStep` -- `ItemIds[0]` is 4553
- `Id` is "equip-crystal"

---

#### EP_T2: QuestDefinition without Epilogue round-trips with null

**Given:**
- A `QuestDefinition` with `Epilogue` set to null (the default)

**When:** Serialize to JSON, then deserialize back

**Then:**
- `Epilogue` is null
- The JSON string does NOT contain the key `"epilogue"` (omitted by `WhenWritingNull`)

---

### Engine Tests: `QuestForge.Engine.Tests\Engine\EpilogueExecutionTests.cs` (new file)

---

#### EP_T3: Quest completes with no epilogue -- Done immediately (regression guard)

**Given:**
- Quest 90001 with one sequence block (Sequence=0), one TalkStep (expect="questSequence(90001) >= 1")
- QuestSequence is 1 (expect satisfied)
- Quest is complete (`IsQuestComplete` returns true)
- `Epilogue` is null

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Done`
- No other actions were emitted before Done

This test is critical: it proves that adding the Epilogue feature does not change behavior for any existing quest.

---

#### EP_T4: Quest completes with empty epilogue -- Done immediately

**Given:**
- Quest 90002 with `Epilogue = []` (empty array, not null)
- Quest is complete
- All sequence steps confirmed

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Done`

---

#### EP_T5: Quest completes with epilogue -- epilogue steps execute before Done

**Given:**
- Quest 90003 with one sequence block (Sequence=0, one TalkStep expect="questSequence(90003) >= 1")
- QuestSequence is 1 (talk step confirmed)
- Quest is complete
- Epilogue contains one `ChangeJobStep` (Id="switch-job", JobId=3)
- Player's current job is JobId 1 (not 3)

**When:** Tick 1: Engine.Tick()

**Then:**
- Action is `EngineAction.ChangeJob` with Job=JobId(3)
- Action is NOT `EngineAction.Done`

**When:** Between ticks: set player job to 3 (simulating the host executing the job change)

**When:** Tick 2: Engine.Tick()

**Then:**
- Action is `EngineAction.Done`
- The engine went through epilogue, confirmed the ChangeJobStep via implicit postcondition, then emitted Done.

---

#### EP_T6: Epilogue with FragmentStep expands and executes

**Given:**
- Fragment "common/test-setup" containing two steps:
  - `EquipGearForQuestStep` (Id="equip-item", ItemIds=[4553])
  - `ChangeJobStep` (Id="switch-job", JobId=3)
- Quest 90004 with epilogue containing one `FragmentStep` (Id="epilogue-setup", Ref="common/test-setup")
- Quest is complete
- All sequence steps confirmed
- Player: item 4553 is NOT equipped, current job is 1

**When:** Tick 1: Engine.Tick()

**Then:**
- Action is `EngineAction.EquipGear` with ItemId=4553
- stepId contains the scoped fragment ID (e.g., "epilogue-setup:equip-item")

**When:** Between ticks: mark item 4553 as equipped

**When:** Tick 2: Engine.Tick()

**Then:**
- Action is `EngineAction.ChangeJob` with Job=JobId(3)

**When:** Between ticks: set player job to 3

**When:** Tick 3: Engine.Tick()

**Then:**
- Action is `EngineAction.Done`

---

#### EP_T7: Epilogue steps support expect/skipIf

**Given:**
- Quest 90005, complete, epilogue contains:
  - Step A: `ChangeJobStep` (Id="maybe-switch", JobId=3, SkipIf="playerJobId() == 3")
  - Step B: `EquipBestGearStep` (Id="gear-up")
- Player's current job is already 3

**When:** Tick 1: Engine.Tick()

**Then:**
- Step A is skipped (skipIf satisfied)
- Action is `EngineAction.EquipBestGear` (Step B)
- NOT `EngineAction.ChangeJob`

---

#### EP_T8: Epilogue uses separate confirmed-step set -- not cleared by sequence logic

**Given:**
- Quest 90006, epilogue contains:
  - `EquipGearForQuestStep` (Id="equip-crystal", ItemIds=[4553], Expect="isSlotEquipped(13)")
  - `ChangeJobStep` (Id="switch-job", JobId=3)
- Quest is complete
- Player: isSlotEquipped(13) is true (crystal already equipped)

**When:** Tick 1: Engine.Tick()

**Then:**
- Step "equip-crystal" is confirmed by expect evaluation
- Action is `EngineAction.ChangeJob` (second epilogue step)
- The confirmation of "equip-crystal" used `_epilogueConfirmedStepIds`, not `_confirmedStepIds`

This test verifies that epilogue confirmations are stored in the correct set. The indirect proof is that the second step is reached (if the confirmation went to the wrong set, the cursor would still be on step 1 next tick because `_epilogueConfirmedStepIds` would be empty).

---

#### EP_T9: EquipBestGear in epilogue uses NotifyEquipBestGearComplete correctly

**Given:**
- Quest 90007, complete, epilogue contains:
  - `EquipBestGearStep` (Id="gear-up")
  - `RegisterGearsetStep` (Id="save-gearset")
- Player is not casting, not in combat

**When:** Tick 1: Engine.Tick()

**Then:**
- Action is `EngineAction.EquipBestGear`
- Origin is the EquipBestGearStep

**When:** Between ticks: call `Engine.NotifyEquipBestGearComplete("gear-up")`

**When:** Tick 2: Engine.Tick()

**Then:**
- `EquipBestGearStep` is confirmed (NotifyEquipBestGearComplete routed to `_epilogueConfirmedStepIds`)
- Action is `EngineAction.RegisterGearset` (next epilogue step)

**When:** Tick 3: Engine.Tick()

**Then:**
- `RegisterGearsetStep` is self-confirmed (fire-once dispatched on tick 2)
- Action is `EngineAction.Done`

---

#### EP_T10: RegisterGearsetStep fire-once works in epilogue

**Given:**
- Quest 90008, complete, epilogue contains only `RegisterGearsetStep` (Id="save-gearset")
- Player is not casting, not in combat

**When:** Tick 1: Engine.Tick()

**Then:**
- Action is `EngineAction.RegisterGearset`
- `_fireOnceDispatchedIds` now contains "save-gearset"

**When:** Tick 2: Engine.Tick()

**Then:**
- `RegisterGearsetStep` is self-confirmed (fire-once already dispatched)
- Action is `EngineAction.Done`

---

#### EP_T11: Epilogue does not activate during NG+ replay

**Given:**
- Quest 90009 with epilogue containing `ChangeJobStep` (Id="switch-job", JobId=3)
- NG+ is active (`GetNewGamePlusState` returns IsActive=true, IsSuspended=false)
- Quest sequence is 255 (NG+ complete sequence)
- QuestSequence block exists for 255 with one step (expect already satisfied)

**When:** Engine.Tick()

**Then:**
- `IsQuestComplete` is NOT called (replayActive=true skips the gate)
- Action is `EngineAction.Wait` ("all steps in current sequence satisfied; awaiting game sequence advance")
- NOT `EngineAction.ChangeJob` -- epilogue does not run during NG+

---

#### EP_T12: BeginRun clears epilogue state between runs

**Given:**
- Quest 90010, complete, epilogue with `ChangeJobStep` (Id="switch-job", JobId=3)
- Run 1: tick produces `EngineAction.ChangeJob` (epilogue entered)
- Run 2: new `BeginRun` called before completion

**When:** Tick in run 2 (quest not yet complete)

**Then:**
- `_inEpilogue` is false (cleared by BeginRun)
- Engine resolves the normal step cursor, not epilogue
- Action corresponds to the current sequence step, not the epilogue step

This test verifies that `BeginRun` properly resets `_inEpilogue` and `_epilogueConfirmedStepIds`.

---

#### EP_T13: AwaitUser in epilogue stops the run

**Given:**
- Quest 90011, complete, epilogue contains `EquipGearForQuestStep` (Id="equip-thing", ItemIds=[9999])
- `IGearEquipper` is NOT wired (null)

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "IGearEquipper"
- The epilogue step's adapter-null guard fires just as it would in the main loop

---

#### EP_T14: Epilogue with TravelStep navigates correctly

**Given:**
- Quest 90012, complete, epilogue contains:
  - `TravelStep` (Id="go-somewhere", Destination=(100, 0, 200), Zone=182, Expect="playerZone() == 182")
- Player is at (0, 0, 0) in zone 182

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Navigate` with destination (100, 0, 200)
- NOT `EngineAction.Done`

---

#### EP_T15: Epilogue completes all steps then returns Done

**Given:**
- Quest 90013, complete, epilogue contains:
  - `ChangeJobStep` (Id="switch", JobId=3) -- player already on job 3
  - `EquipBestGearStep` (Id="gear-up")
- Player job is already 3

**When:** Tick 1: Engine.Tick()

**Then:**
- `ChangeJobStep` is self-confirmed (already on correct job)
- Action is `EngineAction.EquipBestGear`

**When:** Between ticks: call `Engine.NotifyEquipBestGearComplete("gear-up")`

**When:** Tick 2: Engine.Tick()

**Then:**
- Action is `EngineAction.Done`

---

#### EP_T16: Epilogue with all steps already satisfied returns Done immediately

**Given:**
- Quest 90014, complete, epilogue contains:
  - `ChangeJobStep` (Id="switch", JobId=3, Expect="playerJobId() == 3") -- player already on job 3
- Expect is satisfied on first evaluation

**When:** Engine.Tick()

**Then:**
- `ChangeJobStep` is confirmed by expect
- Action is `EngineAction.Done` on the SAME tick (no intermediate actions)

This is the "fast path" -- epilogue steps that are already satisfied do not delay the Done signal.

---

#### EP_T17: Full integration test -- class quest with soul crystal epilogue

**Given:**
- Quest 90015 (simulating a class quest) with:
  - Sequence 0: `AcceptStep` (expect="questSequence(90015) >= 1")
  - Sequence 1: `TalkStep` (expect="questSequence(90015) >= 255")
  - Epilogue:
    - `FragmentStep` (Id="setup", Ref="common/test-warrior-setup")
- Fragment "common/test-warrior-setup" contains:
  - `EquipGearForQuestStep` (Id="equip-crystal", ItemIds=[4553])
  - `ChangeJobStep` (Id="switch-job", JobId=3)
  - `RegisterGearsetStep` (Id="save-gearset")
- Wired callbacks: interact advances sequence, equip auto-succeeds, job change sets player job

**When:** `RunToCompletion` (harness drives the engine to Done)

**Then:**
- Actions contain: Interact (accept), Interact (talk), EquipGear(4553), ChangeJob(3), RegisterGearset
- Final action is Done
- No AwaitUser was emitted
- Total ticks <= 10

This is the end-to-end integration test proving the motivating use case works.

---

#### EP_T18: Epilogue with TeleportStep dispatches correctly

**Given:**
- Quest 90016, complete, epilogue contains:
  - `TeleportStep` (Id="tp-home", AetheryteId=8)
- Player is not in combat

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Teleport` with Destination=AetheryteId(8)

---

#### EP_T19: Epilogue fragment expansion uses shared usageCount with sequences

**Given:**
- Fragment "nav/shared" containing one `TravelStep` (Id="travel", Dest=(10,0,10), Expect="playerZone()==100")
- Quest 90017 with:
  - Sequence 0: contains a `FragmentStep` (Id="seq-use", Ref="nav/shared")
  - Epilogue: contains a `FragmentStep` (Id="epi-use", Ref="nav/shared")
- Player zone is 100 (all expects satisfied)
- Quest is complete

**When:** Engine.Tick()

**Then:**
- The sequence fragment step was expanded as "seq-use:travel"
- The epilogue fragment step was expanded as "epi-use:travel"
- Both IDs are distinct (no collision)
- Engine proceeds to Done (all steps confirmed via expect)

This test verifies EP9 (shared usageCount).

---

#### EP_T20: Epilogue handles multiple step types in sequence

**Given:**
- Quest 90018, complete, epilogue contains:
  - `EquipGearForQuestStep` (Id="equip", ItemIds=[4553])
  - `ChangeJobStep` (Id="switch", JobId=3)
  - `EquipBestGearStep` (Id="gear-up")
  - `RegisterGearsetStep` (Id="save")
- Wired: item 4553 not equipped, job is 1

**When:** Drive engine tick-by-tick, applying side effects between ticks

**Then:**
- Tick 1: `EngineAction.EquipGear(4553)` -- mark equipped
- Tick 2: `EngineAction.ChangeJob(3)` -- set job to 3
- Tick 3: `EngineAction.EquipBestGear` -- call NotifyEquipBestGearComplete
- Tick 4: `EngineAction.RegisterGearset`
- Tick 5: `EngineAction.Done` (RegisterGearset self-confirmed via fire-once)

This is the "four-step warrior setup" motivating example, driven manually tick-by-tick.

---

#### EP_T21: Epilogue persists across ticks (re-entry)

**Given:**
- Quest 90019, complete, epilogue contains:
  - `ChangeJobStep` (Id="switch", JobId=3, Expect="playerJobId() == 3")
- Player job is 1 (not 3 yet)

**When:** Tick 1: Engine.Tick() -- returns ChangeJob
**When:** Tick 2: Engine.Tick() -- (job still 1, no side effect applied)

**Then:**
- Tick 2 still returns `EngineAction.ChangeJob` (not Done)
- The engine correctly re-enters the epilogue path on the second tick (the `_inEpilogue` flag persists)
- IsQuestComplete is still true but the engine does not emit Done because `_inEpilogue` is set

---

#### EP_T22: Casting guard applies during epilogue EquipBestGear

**Given:**
- Quest 90020, complete, epilogue contains `EquipBestGearStep` (Id="gear-up")
- Player is casting

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Wait`
- Reason contains "casting"
- NOT `EngineAction.EquipBestGear` (casting guard in `ResolveEquipBestGear` fires)

---

## 7. Implementation Order

### Phase A: Schema (est. 15 min)

1. Add `Epilogue` property to `QuestDefinition`
2. Add round-trip tests EP_T1 and EP_T2

**Done before Phase B:** `dotnet test QuestForge.Schema.Tests` passes with both new round-trip tests.

### Phase B: Engine state + completion gate + epilogue walker (est. 2-3 hours)

1. Add `_epilogueSteps`, `_inEpilogue`, `_epilogueConfirmedStepIds` fields
2. Add epilogue expansion in `StartQuest`
3. Add epilogue clearing in `BeginRun`
4. Modify the `IsQuestComplete` gate to enter epilogue
5. Add `ResolveEpilogueAction` method
6. Modify `NotifyEquipBestGearComplete` to route by `_inEpilogue`
7. Extend `FindStepById` to search `_epilogueSteps`

**Done before Phase C:** `dotnet build QuestForge.Engine` succeeds.

### Phase C: Engine Tests (est. 3-4 hours)

1. Create `EpilogueExecutionTests.cs` with EP_T3 through EP_T22
2. All 20 engine tests pass
3. All existing engine tests remain green

**Done before complete:** `dotnet test QuestForge.Engine.Tests` -- all green.

---

## 8. Done Criteria

1. `dotnet build` succeeds for all projects
2. `dotnet test QuestForge.Schema.Tests` passes -- 2 new round-trip tests green
3. `dotnet test QuestForge.Engine.Tests --filter EpilogueExecutionTests` passes -- 20 engine tests green
4. `dotnet test QuestForge.Engine.Tests` passes -- all existing tests still pass (no regressions)
5. `QuestDefinition` with `Epilogue: null` serializes to JSON that does NOT contain the `"epilogue"` key
6. `QuestDefinition` with `Epilogue: [ChangeJobStep]` serializes/deserializes with the step preserved
7. A quest with no epilogue returns `Done` on completion -- identical to current behavior
8. A quest with epilogue steps executes them before emitting `Done`
9. `NotifyEquipBestGearComplete` routes to the correct confirmed-step set based on `_inEpilogue`
10. `_fireOnceDispatchedIds` correctly gates `RegisterGearsetStep` re-dispatch during epilogue
11. Fragment expansion in epilogue uses globally unique scoped IDs (shared `usageCount`)
12. Epilogue does not activate during NG+ replay

---

## 9. Exclusions

This spec explicitly does NOT include:

1. **EngineHost dispatch changes** -- the epilogue emits the same `EngineAction` types as the main loop. `EngineHost.DispatchAction` already handles all of them. No dispatch changes needed.
2. **Scheduler changes** -- the scheduler polls for the next quest after `Done` is emitted. The epilogue delays `Done` until setup is finished, so the scheduler naturally sees the correct job state when it polls. No scheduler code changes.
3. **Authoring inference for epilogue** -- determining what steps belong in an epilogue is an authoring-time decision, not an inference-time one. Deferred.
4. **DraftValidator rules for epilogue** -- the epilogue steps are validated by the same rules as any other steps (E1-E33, W1-W15). No epilogue-specific validation rules are needed. If a future requirement surfaces (e.g., "epilogue must not contain CombatStep"), it can be added then.
5. **Navigation watchdog during epilogue** -- epilogue steps are short-range or non-navigational. If a TravelStep stalls, the stateless retry will recover. Jump/return recovery is unnecessary overhead.
6. **Resume-point fragments during epilogue** -- epilogue IS a post-completion fragment. Nesting resume points inside it is not a current use case.
7. **Death recovery during epilogue** -- if the player dies during the (typically 2-5 second) epilogue, the idempotent steps will recover on revival without special handling.
8. **Combat defense during epilogue** -- no combat objectives exist post-completion.
9. **Tools-repo catch-up** -- no new step type or action type is introduced; existing `CapabilityInferrer` and `TraceConstants` entries cover all step types used in epilogues.
10. **`_lastResolvedStep` management in epilogue** -- the epilogue cursor walk sets `_lastResolvedStep` only for the sync fallthrough path (line 876 equivalent), matching the main loop's behavior. This enables `CurrentYesNoAnswer` to work for any TalkStep or TurnInStep that might appear in an epilogue (unlikely but supported).

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Section 6.
- Happy paths: 8 scenarios (EP_T1, EP_T2, EP_T5, EP_T6, EP_T9, EP_T15, EP_T17, EP_T20)
- Edge cases: 8 scenarios (EP_T4, EP_T7, EP_T8, EP_T11, EP_T16, EP_T19, EP_T21, EP_T22)
- Error/regression cases: 4 scenarios (EP_T3, EP_T10, EP_T12, EP_T13)
- Supplemental: 2 scenarios (EP_T14, EP_T18)
- Expected total: ~22 tests in QuestForge.Engine.Tests + QuestForge.Schema.Tests
