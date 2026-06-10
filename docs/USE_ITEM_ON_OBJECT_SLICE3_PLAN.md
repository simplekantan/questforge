# UseItemOnObjectStep -- Slice 3: Dalamud Impl + Tooling Catch-up

**Status:** Draft
**Phase:** 11 (Corpus Expansion)
**Step type discriminator:** `"use-item-on-object"`
**Slice:** 3 of 6 (Dalamud Impl + Tooling Catch-up)
**Author:** QuestForge System Architect
**Date:** 2026-06-10

---

## 1. Header

**Input documents:**
- `docs/USE_ITEM_ON_OBJECT_STEP_PLAN.md` -- Slice 1 architect spec (decisions UIO1--UIO12)
- `docs/FIXTURES.md` -- capabilities table and actionType canonical strings table
- `CLAUDE.md` -- Slice 3 requirements, tooling catch-up invariant, recording proxy decision tree

**Output (CI behavior changes):**
- `dotnet test QuestForge.Tools.Trace.Tests` gains 4 new tests for CapabilityInferrer and TraceToFixtureExtractor
- `docs/FIXTURES.md` gains two new table rows (capabilities + actionType)
- EngineHost gains `case EngineAction.UseItemOnObject:` dispatch arm (no CI test -- Dalamud-bound, smoke-tested in Slice 4)

**Phase dependencies:**
- Slice 2 merged: `UseItemOnObjectStep` in schema, `EngineAction.UseItemOnObject` in engine, `ResolveUseItemOnObject` resolver, `EngineTestHarness` dispatch arm, validator rules E27/E28/W13 -- all implemented and passing
- `IObjectInteractor.InteractWithObject` -- implemented (`DalamudObjectInteractor`)
- `IItemUser.UseItem` -- implemented (`DalamudItemUser`)
- `CapabilityInferrer.StepCapabilities` dict -- needs new entry
- `TraceToFixtureExtractor.FilenameLookup` / `DistinguishingCapPriority` -- needs new entries
- `TraceConstants` -- needs new constant

---

## 2. Dependency Graph

```
questforge repo (Dalamud impl):
    EngineHost.cs  (new dispatch arm)
        depends on: _objectInteractor (DalamudObjectInteractor)
                    _itemUser         (DalamudItemUser)
                    both already constructed in EngineHost ctor

questforge-tools repo (tooling catch-up):
    CapabilityInferrer.cs        (new StepCapabilities entry)
    TraceToFixtureExtractor.cs   (new FilenameLookup + DistinguishingCapPriority entries)
    TraceConstants.cs             (new ActionUseItemOnObject constant)
    Tests/                        (4 new tests)

questforge repo (docs):
    docs/FIXTURES.md              (two new table rows)
```

Build order: The questforge-repo changes and the questforge-tools changes are independent of each other and can land as paired PRs in any order. Both must land in the same slice per the project invariant (`feedback_tooling_catchup_never_deferred.md`).

---

## 3. Architectural Decisions

### S3_1: EngineHost dispatch arm shape

**Decision:** The dispatch arm for `EngineAction.UseItemOnObject` composes two existing adapters atomically:

```csharp
case EngineAction.UseItemOnObject uio:
    DebounceLog(
        $"useitemonobject:{uio.Target.Value}:{uio.Kind}:{uio.ItemId}",
        $"[UseItemOnObject] interactableId={uio.Target.Value} kind={uio.Kind} itemId={uio.ItemId}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _objectInteractor.InteractWithObject(uio.Target, ct);
    await _itemUser.UseItem(uio.Kind, uio.ItemId, null, null, ct);
    break;
```

**Placement in DispatchAction:** Between `case EngineAction.InteractObject` (line 571) and `case EngineAction.HandOver` (line 581). This groups all object-interaction dispatch arms together. Logically `UseItemOnObject` is closest to `InteractObject` (both target an EventObj) but with an additional item-use step (like `HandOver`'s additional addon driving).

**Alternatives considered:**
- Placing after `UseItem` (line 642). Rejected: `UseItem` fires `_itemUser.UseItem` directly without prior `InteractWithObject`. Grouping with `InteractObject` makes the shared `_objectInteractor` usage visible at a glance.
- Adding `AdvanceDialogue` after `InteractWithObject` (like the `InteractObject` arm does). Rejected: the InventoryEvent addon is not a dialogue addon. `AdvanceDialogue` would attempt to click through a non-dialogue UI. The item-use call (`_itemUser.UseItem`) is the correct follow-up, not dialogue advancement.
- Using `IInteractor.UseItemOnObject(ItemId, InteractableId, ct)` which already exists on the interface. Rejected: that method is a Phase 6 stub (`notImplemented`). The two-call composition (`InteractWithObject` + `UseItem`) is proven to work in-game per UIO11 research notes. Using the stub would require implementing it first, adding no value over the composition.

**What breaks if violated:** If someone calls `_interactor.UseItemOnObject` instead of the two-call composition, the dispatch hits the Phase 6 stub and silently fails with `notImplemented`. The item is never used and the engine spin-loops.

**Debounce key format:** `useitemonobject:{target}:{kind}:{itemId}` -- includes all three identifying fields so different items on different objects are logged independently, but repeated ticks against the same object+item are suppressed.

### S3_2: Lazy-dismount exemption -- NOT exempt

**Decision:** `EngineAction.UseItemOnObject` is NOT added to the lazy-dismount exemption list.

The current exemption list (line 417 of `EngineHost.cs`) is:
```csharp
if (_lastDispatchedActionWasNavigate
    && action is not EngineAction.Navigate
    and not EngineAction.Teleport
    and not EngineAction.EquipGear
    and not EngineAction.EquipBestGear
    and not EngineAction.RegisterGearset
    and not EngineAction.Jump)
```

`UseItemOnObject` is not in this list, so the lazy-dismount hook fires normally -- the player is dismounted before the dispatch arm runs. This is correct because:

1. The player cannot interact with EventObjs while mounted (the game silently ignores the interact attempt).
2. This is consistent with `InteractObject`, `HandOver`, `UseAction`, `UseEmote`, and all other interaction-type actions.

**What breaks if violated:** If someone exempts `UseItemOnObject`, the player stays mounted after navigation, the `InteractWithObject` call is ignored by the game, and the engine spin-loops.

**Pinning:** This decision is already tested by UIO_T7 and UIO_T8 in the Slice 2 engine tests. UIO_T7 verifies lazy-dismount fires after Navigate -> UseItemOnObject transition. UIO_T8 verifies that a standalone UseItemOnObject (no prior Navigate) does not trigger dismount at the engine level (dismount is a host concern).

### S3_3: Recording proxy -- NOT needed

**Decision:** No `RecordingUseItemOnObjectExecutor` wrapper. The write-only adapter pattern applies:

1. `EngineHost.DispatchAction` already emits `action.submitted` and `action.completed` trace events around every dispatch case via the shared `CapturingTraceWriter`. These events capture the action type (`"useitemonobject"` via `GetType().Name.ToLowerInvariant()`) and parameters.
2. There are no adapter reads worth recording -- both `InteractWithObject` and `UseItem` are fire-and-forget writes.

**Precedent:** Same decision as UseAction, UseEmote, UseItem, SayChatMessage, EquipGear, ChangeJob, RegisterGearset, OpenCoffer -- all write-only adapters without recording wrappers.

**What breaks if violated:** If someone adds a recording wrapper, they add a class, a constructor dependency, and wiring code in `EngineHost` -- all for zero additional trace information beyond what `action.submitted`/`action.completed` already capture.

### S3_4: CapabilityInferrer entry

**Decision:** Add to `CapabilityInferrer.StepCapabilities`:

```csharp
[typeof(UseItemOnObjectStep)] = "step:use-item-on-object",
```

**Placement:** After the `[typeof(OpenCoffersStep)]` entry (alphabetical by type name within the dictionary literal). The dictionary is not sorted, but convention places new entries at the end of the existing block.

**Tag format:** `step:use-item-on-object` -- kebab-case matching the JSON discriminator, consistent with all other step capability tags.

### S3_5: FilenameLookup and DistinguishingCapPriority entries

**Decision: FilenameLookup entry:**

```csharp
(["step:talk", "step:travel", "step:use-item-on-object"], "with-use-item-on-object.json"),
```

The array is sorted alphabetically (standard for all FilenameLookup entries). This covers the common quest shape: talk to NPC, travel to EventObj, use item on object.

**Placement:** After the `(["step:open-coffers", ...], "with-open-coffers.json")` entry, before `(["step:dungeon-trial"], ...)`. Grouped with the other catch-up entries.

**Decision: DistinguishingCapPriority entry:**

```csharp
("step:use-item-on-object", "with-use-item-on-object.json"),
```

**Placement:** Between `step:use-item` and `step:say-chat-message` in the priority list. Rationale: `use-item-on-object` is more distinguishing than `say-chat-message` (it involves object interaction + item use, a richer capability shape) but less distinguishing than `use-item` (which covers the broader single-call item-use pattern).

Current priority ordering around the insertion point:
```
("step:use-action",     "with-use-action.json"),
("step:use-emote",      "with-use-emote.json"),
("step:use-item",       "with-use-item.json"),
("step:use-item-on-object", "with-use-item-on-object.json"),   // <-- NEW
("step:say-chat-message", "with-say-chat-message.json"),
```

**What breaks if violated:** If the FilenameLookup entry is omitted, `SuggestFilename` falls through to the DistinguishingCapPriority fallback for exact-shape matches, producing the correct filename but via the slower path. If the DistinguishingCapPriority entry is omitted, multi-shape quests containing `step:use-item-on-object` (but not matching any exact FilenameLookup set) fall through to the final `"simple-linear-acceptance.json"` default -- a misleading filename suggestion.

### S3_6: TraceConstants entry

**Decision:** Add to `TraceConstants`:

```csharp
internal const string ActionUseItemOnObject = "useitemonobject";
```

**Derivation:** `EngineAction.UseItemOnObject.GetType().Name` produces `"UseItemOnObject"`. `ToLowerInvariant()` produces `"useitemonobject"`. This matches the convention for all existing constants.

**Placement:** After `ActionInteractObject` (alphabetical within the action constants block, but really just after the most recently added constant).

**Behavioral impact:** None. `IsTerminalAction` only checks `done`/`awaituser`. This constant is purely documentary -- it catalogs what `DecisionEvent.ActionType.ToLowerInvariant()` will emit for this action type.

### S3_7: FIXTURES.md documentation updates

**Decision:** Two table updates in `docs/FIXTURES.md`:

**Capabilities table (around line 124, after `step:open-coffers`):**
```
| `step:use-item-on-object` | `UseItemOnObjectStep` -- approach EventObj, interact to open InventoryEvent, use key item |
```

**actionType canonical strings table (around line 187, after `"interactobject"`):**
```
| `"useitemonobject"` | `EngineAction.UseItemOnObject` | `UseItemOnObjectStep` dispatch -- interact with EventObj, then use key/inventory item within the InventoryEvent event context. |
```

### S3_8: No StepEditModal / authoring dropdown changes in this slice

**Decision:** Authoring mode changes (step inference, UIObserver polling, StepEditModal dropdown entry) are explicitly deferred to Slice 5 (Authoring Inference). This slice adds only the Dalamud dispatch arm and tooling entries.

**Rationale:** Slice 5 requires signal research to determine how to detect "player used item from InventoryEvent addon" -- that research should not block the Dalamud impl and tooling catch-up.

---

## 4. Task Breakdown

### Task 1: EngineHost dispatch arm (questforge repo)

**File:** `QuestForge.Plugin/EngineHost.cs`

**Deliverables:**

1. Add `case EngineAction.UseItemOnObject uio:` arm in `DispatchAction` between `InteractObject` and `HandOver`:

```csharp
case EngineAction.UseItemOnObject uio:
    DebounceLog(
        $"useitemonobject:{uio.Target.Value}:{uio.Kind}:{uio.ItemId}",
        $"[UseItemOnObject] interactableId={uio.Target.Value} kind={uio.Kind} itemId={uio.ItemId}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _objectInteractor.InteractWithObject(uio.Target, ct);
    await _itemUser.UseItem(uio.Kind, uio.ItemId, null, null, ct);
    break;
```

No new field declarations, constructor changes, or `BeginRun` wiring needed -- `_objectInteractor` and `_itemUser` are already constructed at lines 151 and 144 respectively.

No `Debug*` accessor needed -- `DebugObjectInteractor` and `DebugItemUser` already exist for the underlying adapters.

### Task 2: CapabilityInferrer entry (questforge-tools repo)

**File:** `QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs`

**Deliverable:** Add one line to `StepCapabilities`:

```csharp
[typeof(UseItemOnObjectStep)] = "step:use-item-on-object",
```

### Task 3: TraceToFixtureExtractor entries (questforge-tools repo)

**File:** `QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs`

**Deliverables:**

1. Add to `FilenameLookup` array:

```csharp
(["step:talk", "step:travel", "step:use-item-on-object"], "with-use-item-on-object.json"),
```

2. Add to `DistinguishingCapPriority` array (between `step:use-item` and `step:say-chat-message`):

```csharp
("step:use-item-on-object", "with-use-item-on-object.json"),
```

### Task 4: TraceConstants entry (questforge-tools repo)

**File:** `QuestForge.Tools.Trace/TraceConstants.cs`

**Deliverable:** Add one line:

```csharp
internal const string ActionUseItemOnObject = "useitemonobject"; // lowercased from "UseItemOnObject"
```

### Task 5: FIXTURES.md updates (questforge repo)

**File:** `docs/FIXTURES.md`

**Deliverables:**

1. Add row to capabilities table (after `step:open-coffers`, before `step:minigame`):

```
| `step:use-item-on-object` | `UseItemOnObjectStep` -- approach EventObj, interact to open InventoryEvent, use key item |
```

2. Add row to actionType canonical strings table (after `"interactobject"`):

```
| `"useitemonobject"` | `EngineAction.UseItemOnObject` | `UseItemOnObjectStep` dispatch -- interact with EventObj, then use key/inventory item within the InventoryEvent event context. |
```

3. Add fixture naming convention entry (after `with-open-coffers.json`):

```
with-use-item-on-object.json    # quest with UseItemOnObjectStep (approach EventObj + InventoryEvent item use)
```

### Task 6: Tests (questforge-tools repo)

See Section 5 (Given-When-Then Specs) for all test scenarios.

---

## 5. Given-When-Then Specs

All tests live in `QuestForge.Tools.Trace.Tests/`.

---

### S3_T1: CapabilityInferrer includes `step:use-item-on-object` for a quest with UseItemOnObjectStep

**File:** `CapabilityInferrerTests.cs`

**Given:**
- A `QuestDefinition` with one sequence containing a single `UseItemOnObjectStep`:
  - Id = `"use-potion-on-device"`
  - InteractableId = `2001500u`
  - Position = `(81.5f, 7.0f, 32.2f)`
  - Kind = `ItemKind.KeyItem`
  - ItemId = `2002001u`
  - Expect = `PredicateExpect { Predicate = "questFlag(95001, 3)" }`

**When:** `CapabilityInferrer.Infer(quest)`

**Then:**
- Result contains `"step:use-item-on-object"`
- Result contains `"predicate:questFlag"` (extracted from the Expect predicate)
- Result is sorted alphabetically

**Test pattern:** Mirror `Infer_QuestWithPurchaseItemStep_EmitsStepPurchaseItem` (line 745 of `CapabilityInferrerTests.cs`).

---

### S3_T2: CapabilityInferrer does NOT include `step:use-item-on-object` for a quest without it

**File:** `CapabilityInferrerTests.cs`

**Given:**
- The quest 66130 test definition (TravelStep + TalkStep only, already used by `Infer_Quest66130_ReturnsExpectedCapabilities_Sorted`)

**When:** `CapabilityInferrer.Infer(quest)`

**Then:**
- Result does NOT contain `"step:use-item-on-object"`

**Note:** This test is arguably redundant (the existing 66130 test already asserts the exact capability list). However, it explicitly pins the negative case: adding `UseItemOnObjectStep` to StepCapabilities does not cause it to appear in quests that do not use it. The tester may choose to implement this as an additional assertion within the existing 66130 test rather than a standalone test.

---

### S3_T3: SuggestFilename returns `with-use-item-on-object.json` for exact capability match

**File:** `FixtureHarnessTests.cs`

**Given:**
- A `FixtureModel` with Capabilities = `["step:talk", "step:travel", "step:use-item-on-object"]` (sorted alphabetically)

**When:** `extractor.SuggestFilename(fixture)`

**Then:**
- Result == `"with-use-item-on-object.json"`

**Test pattern:** Mirror `F1_SuggestFilename_AcceptShape_ReturnsWithAccept` (line 37 of `FixtureHarnessTests.cs`). Use the `MakeFixture(params string[] capabilities)` helper.

---

### S3_T4: SuggestFilename returns `with-use-item-on-object.json` via DistinguishingCapPriority fallback

**File:** `FixtureHarnessTests.cs`

**Given:**
- A `FixtureModel` with Capabilities = `["step:accept", "step:talk", "step:travel", "step:use-item-on-object"]`
- This set does NOT match any exact FilenameLookup entry (it has 4 caps, not 3)

**When:** `extractor.SuggestFilename(fixture)`

**Then:**
- Result == `"with-use-item-on-object.json"` (from DistinguishingCapPriority fallback)
- NOT `"with-accept.json"` (because `step:use-item-on-object` ranks higher than `step:accept` in the priority list -- `step:accept` is not even in the priority list at all, it falls below everything)

**Rationale:** This tests the fallback path specifically. The exact-match test (S3_T3) covers the happy path; this test ensures multi-shape quests with `use-item-on-object` as the most distinguishing cap get the right filename suggestion.

**Test pattern:** Mirror `F7_SuggestFilename_MultiShapeNoExactMatch_FallsBackToHighestPriorityTag` (line 172 of `FixtureHarnessTests.cs`).

---

## 6. Implementation Order

### Phase A: questforge-tools changes (est. 45 min)

1. Add `[typeof(UseItemOnObjectStep)] = "step:use-item-on-object"` to `CapabilityInferrer.StepCapabilities`
2. Add FilenameLookup entry to `TraceToFixtureExtractor`
3. Add DistinguishingCapPriority entry to `TraceToFixtureExtractor`
4. Add `ActionUseItemOnObject` constant to `TraceConstants`
5. Write and run tests S3_T1 through S3_T4

**Done before Phase B:** `dotnet test QuestForge.Tools.Trace.Tests` passes with 4 new tests green. No regressions in existing tests.

### Phase B: questforge EngineHost dispatch arm (est. 30 min)

1. Add `case EngineAction.UseItemOnObject uio:` arm in `EngineHost.DispatchAction`
2. Verify `dotnet build QuestForge.Plugin` succeeds
3. Verify no existing tests are broken: `dotnet test QuestForge.Engine.Tests`

**Done before Phase C:** `dotnet build` succeeds for the plugin project. All existing engine tests pass.

### Phase C: FIXTURES.md documentation (est. 15 min)

1. Add capabilities table row for `step:use-item-on-object`
2. Add actionType table row for `"useitemonobject"`
3. Add fixture naming convention entry for `with-use-item-on-object.json`

**Done after Phase C:** `docs/FIXTURES.md` has three new entries.

---

## 7. Done Criteria

1. `dotnet build QuestForge.Plugin` succeeds -- `EngineHost.cs` compiles with the new dispatch arm
2. `dotnet test QuestForge.Engine.Tests` passes -- no regressions from EngineHost changes (engine tests do not exercise EngineHost, but build must succeed)
3. `dotnet test QuestForge.Tools.Trace.Tests` passes with 4 new tests:
   - S3_T1: CapabilityInferrer emits `step:use-item-on-object` for quest with UseItemOnObjectStep
   - S3_T2: CapabilityInferrer does NOT emit `step:use-item-on-object` for quest without it
   - S3_T3: SuggestFilename exact match -> `with-use-item-on-object.json`
   - S3_T4: SuggestFilename fallback -> `with-use-item-on-object.json`
4. `TraceConstants.ActionUseItemOnObject == "useitemonobject"` (matches `EngineAction.UseItemOnObject.GetType().Name.ToLowerInvariant()`)
5. `docs/FIXTURES.md` contains rows for `step:use-item-on-object` in the capabilities table and `"useitemonobject"` in the actionType table
6. No new adapter interfaces, fakes, or recording proxies introduced
7. Lazy-dismount exemption list unchanged -- `UseItemOnObject` is subject to lazy-dismount (pinned by existing UIO_T7/UIO_T8 engine tests)

---

## 8. Exclusions

This spec explicitly does NOT include:

1. **New adapter interfaces** -- per decision UIO5/S3_3, no `IUseItemOnObjectExecutor` or recording proxy. The dispatch composes `_objectInteractor.InteractWithObject` + `_itemUser.UseItem`.
2. **Authoring inference** -- deferred to Slice 5. Signal research for detecting InventoryEvent addon item use is not part of this slice.
3. **StepEditModal / authoring dropdown** -- deferred to Slice 5.
4. **In-game smoke test** -- that is Slice 4, after this slice lands.
5. **`IInteractor.UseItemOnObject` implementation** -- the existing Phase 6 stub on `DalamudInteractor` is NOT used. The two-call composition is the correct approach per UIO5/UIO11. The stub remains as dead code; cleanup is a separate concern.
6. **Quest-data fixture** -- a `with-use-item-on-object.json` fixture in `questforge-data` is not created in this slice. It requires a real quest trace (Slice 4) or manual authoring.
7. **SnapshotState changes in questforge-tools** -- `UseItemOnObjectStep` is a single-tick signal (interact + use item), not multi-stage. No per-step extension to `SnapshotState.cs` is needed (per CLAUDE.md extract-quest guidance).

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Section 5.
- Happy paths: 2 scenarios (S3_T1, S3_T3)
- Edge cases: 1 scenario (S3_T4 -- fallback priority path)
- Negative cases: 1 scenario (S3_T2 -- absence of capability)
- Expected total: ~4 tests in QuestForge.Tools.Trace.Tests
