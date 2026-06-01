# SPD Retry: EntryTargetId/EntryPosition on DutyStep + EnterSinglePlayerDuty Enrichment

**Status:** ready to implement
**Input docs:** Issue #142, docs/SINGLE_PLAYER_DUTY_PLAN.md (original SPD design), docs/SCHEMA.md, docs/ADAPTERS.md
**Output:** SPD retry after failure works autonomously: engine re-navigates to entry NPC, re-interacts, re-enters duty. Existing tests updated for new action shape.
**Phase dependencies:** Builds on implemented SPD support (SINGLE_PLAYER_DUTY_PLAN.md) and DutyStep schema.

---

## Dependency graph

```
Single PR — questforge repo (schema + engine action + pre-arm + tests)

  Schema:    DutyStep gains EntryTargetId (uint?) and EntryPosition (Position3?)
  Engine:    EngineAction.EnterSinglePlayerDuty gains ContentFinderConditionId, EntryTargetId, EntryPosition
             ResolveSpd reads new fields from step and passes to action record
  Tests:     New scenarios in SinglePlayerDutyStepTests.cs; existing tests updated for new action shape
  Validator: E22 for EntryTargetId == 0; no W-rule changes (SPD already not W1-suppressed)
  Plugin:    EngineHost dispatch arm reads entry fields for retry navigation+interact

Paired PR — questforge-tools repo
  Schema:    DutyStep mirror gains EntryTargetId and EntryPosition
```

---

## Architectural decisions

### R1: Two new optional fields on DutyStep -- EntryTargetId and EntryPosition

```csharp
public sealed class DutyStep : Step
{
    public string Kind { get; init; } = default!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ContentFinderConditionId { get; init; }

    /// <summary>
    /// DataId of the NPC that triggers SPD entry. Used by EngineHost for autonomous
    /// retry after SPD failure: navigate to EntryPosition, interact with EntryTargetId.
    /// Null when the quest author omits it (legacy quests, or when the preceding TalkStep
    /// already handles the NPC interaction for initial entry).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? EntryTargetId { get; init; }

    /// <summary>
    /// World-space position of the entry NPC. Used for implied navigation before re-interact.
    /// Null is valid: when EntryTargetId is set but EntryPosition is null, EngineHost
    /// emits Interact without preceding Navigate (assumes player is already near the NPC).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? EntryPosition { get; init; }
}
```

**Why on DutyStep, not on EnterSinglePlayerDuty only:** The entry target is quest-level authoring data, not engine runtime state. It belongs in the schema so quest authors can declare it and the validator can check it. The engine reads it from the step and passes it through the action record to EngineHost.

**Why optional:** Backwards compatibility. Existing quest files with `DutyStep(kind: "spd")` lack these fields. When absent, retry falls back to the existing behavior: engine re-dispatches `EnterSinglePlayerDuty` without navigation/interact, relying on the preceding TalkStep in the cursor walk to handle re-approach. This fallback is correct but slower (the cursor walks through confirmed steps first).

**Why Position3? not NpcLocation:** `NpcLocation` bundles `NpcId + Zone + Position3`. The zone is redundant here (the player is already in the zone after being ejected from the SPD), and `NpcId` is already captured by `EntryTargetId`. Using `Position3` keeps the schema lean and avoids confusion about which NpcId is canonical.

**Rejected alternative:** A dedicated `SpdEntryTarget` record. Rejected because it adds a type for exactly two fields. The flat fields on `DutyStep` are simpler to author, validate, and serialize.

**What breaks if violated:** If these were non-nullable, all existing SPD quest files would fail deserialization. If they were on a separate type, quest authors would need nested JSON objects for a simple concept.

### R2: EnterSinglePlayerDuty action enrichment

Current shape:
```csharp
public sealed record EnterSinglePlayerDuty(Step? Origin = null) : EngineAction;
```

New shape:
```csharp
public sealed record EnterSinglePlayerDuty(
    uint? ContentFinderConditionId,
    uint? EntryTargetId,
    Position3? EntryPosition,
    Step? Origin = null) : EngineAction;
```

**Why ContentFinderConditionId on the action:** Currently absent from `EnterSinglePlayerDuty` (only `EnterDuty` carries it). Adding it now enables EngineHost logging and future retry-counter keying. It is nullable because SPD quest files authored before ContentFinderConditionId was added may lack it.

**Why carry entry fields on the action, not read from Origin:** `EngineHost.DispatchAction` must not downcast `Origin` to `DutyStep` and read schema fields. The action record is the contract between engine and host. The engine resolves schema fields into the action; the host consumes the action. This preserves the testability boundary.

**What breaks if violated:** If EngineHost reads `Origin` as `DutyStep`, it couples the host to schema types. If the schema shape changes, the host breaks. The action record is the stable interface.

**Existing test impact:** All existing tests that construct or pattern-match `EnterSinglePlayerDuty` must be updated. There are 11 tests in `SinglePlayerDutyStepTests.cs` that assert `IsType<EngineAction.EnterSinglePlayerDuty>` or construct the action. The new positional parameters mean these tests will fail to compile until updated.

### R3: ResolveSpd reads entry fields from DutyStep

```csharp
private async Task<EngineAction> ResolveSpd(DutyStep step, CancellationToken ct)
{
    if (_questBattleRunner is null)
        return new EngineAction.AwaitUser(
            "DutyStep(kind:spd) dispatched but no IQuestBattleRunner configured -- host must supply one");

    var availResult = await _questBattleRunner.IsBossModAvailable(ct);
    if (availResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            "BossMod required for Single Player Duties. Complete manually or install BossMod.");

    return new EngineAction.EnterSinglePlayerDuty(
        ContentFinderConditionId: step.ContentFinderConditionId,
        EntryTargetId: step.EntryTargetId,
        EntryPosition: step.EntryPosition,
        Origin: step);
}
```

**Key invariant preserved:** `_lastResolvedStep` is NOT set in `ResolveSpd` (matches Teleport/Purchase/UseAction/UseEmote precedent -- async pre-arms never set `_lastResolvedStep`).

### R4: EngineHost retry logic -- BoundByDuty check via ConditionFlag

The core question is: when EngineHost receives `EnterSinglePlayerDuty` with `EntryTargetId` set, how does it know whether the player is in the overworld (needs navigate+interact) versus inside the duty (needs BossMod start only)?

**Decision: Use `ConditionFlag.BoundByDuty` from `ICondition`.**

```csharp
case EngineAction.EnterSinglePlayerDuty espd:
    DebounceLog(
        $"enterspd:{espd.Origin?.Id}",
        $"[EnterSinglePlayerDuty] stepId={espd.Origin?.Id ?? "(unknown)"}" +
        $" cfcId={espd.ContentFinderConditionId}" +
        $" entryTarget={espd.EntryTargetId}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);

    // Retry path: if entry target is known AND player is in overworld (not inside duty),
    // navigate to entry NPC and interact to re-enter.
    if (espd.EntryTargetId is { } entryNpc
        && !_services.Condition[ConditionFlag.BoundByDuty])
    {
        // Navigate to entry position if provided and out of range
        if (espd.EntryPosition is { } pos)
        {
            var target = new WorldPosition(pos.X, pos.Y, pos.Z);
            var playerPos = (await _gameState.GetPlayerPosition(ct)).ValueOrDefault;
            if (playerPos is { } pp && pp.DistanceTo(target) > 3.0f)
                await _navigator.NavigateTo(target, new NavigationOptions(StoppingDistance: 3.0f), ct);
        }
        // Interact with entry NPC
        await _interactor.InteractWith(new NpcId(entryNpc), ct);
    }

    _activeSpdStepId = espd.Origin?.Id;
    TryCutsceneSkipConfirm();
    TryHandleDifficultySelect(); // NOW wired -- handles retry difficulty selection
    await _questBattleRunner.StartDuty(ct);
    await _interactor.AdvanceDialogue(ct);
    break;
```

**Why `ConditionFlag.BoundByDuty`:** This is the game's own flag indicating the player is inside instanced content. It is false immediately after ejection. It requires no new adapter interface -- `ICondition` is already injected into EngineHost via `PluginServices`. It is a simple boolean check, not an async call.

**Rejected alternative A: Track state in EngineHost (`_spdState` enum).** Rejected because it duplicates game state. The game already tracks this via ConditionFlag. An EngineHost enum would need to be kept in sync with the game and would be wrong after edge cases (plugin reload during duty, manual duty exit via Duty Finder).

**Rejected alternative B: Check `IQuestBattleRunner.IsBossModAvailable` + started state.** Rejected because BossMod availability is independent of whether the player is inside a duty. BossMod can be available while the player is in the overworld.

**Why `TryHandleDifficultySelect()` is now wired here:** The method exists but is never called (verified in codebase). On retry, `DifficultySelectYesNo` appears after the interact. Calling it in the dispatch arm handles it immediately. On first entry, the addon is not open so the call is a no-op (the method guards on addon visibility).

**What breaks if violated:** If we track state instead of reading `ConditionFlag`, a plugin reload during an SPD would leave the state stale. The player would be inside the duty but the engine would think they're in the overworld, causing a spurious navigate+interact.

### R5: ConditionFlag.BoundByDuty lives in Plugin layer, not Engine

The engine (`QuestForge.Engine`) must never reference Dalamud types. `ConditionFlag.BoundByDuty` is a Dalamud enum. The engine's job is to resolve which action to emit. The host's job is to execute the action, which includes deciding whether to navigate+interact based on the player's duty state.

**This is why the entry fields are on the action record, not consumed by the engine.** The engine passes them through. EngineHost reads them and makes the BoundByDuty decision.

**What breaks if violated:** If the engine imported `ConditionFlag`, it would gain a Dalamud dependency, breaking the testability boundary. Engine tests would require a game client.

### R6: Implied navigation in EngineHost retry -- await NavigateTo, not emit Navigate

In the retry path, EngineHost calls `_navigator.NavigateTo` directly (await), rather than having the engine emit a `Navigate` action. This is intentional: the engine already emitted `EnterSinglePlayerDuty` as the action for this tick. The navigate+interact is a sub-sequence within the dispatch arm, not a separate engine decision.

**Why this is correct:** The engine's cursor is on the DutyStep. It will keep emitting `EnterSinglePlayerDuty` until Expect is satisfied. Each dispatch is an opportunity for EngineHost to execute the navigate+interact sub-sequence. If navigation fails, the next tick will try again.

**Rejected alternative:** Have the engine detect overworld state and emit Navigate/Interact/EnterSinglePlayerDuty as a sequence. Rejected because: (a) the engine would need BoundByDuty, violating R5; (b) the engine would need multi-action emission per tick, which the architecture does not support; (c) the engine's DutyStep cursor walk already handles retry via re-dispatch.

### R7: DraftValidator E22 -- EntryTargetId == 0

```csharp
// E22: DutyStep with EntryTargetId == 0 (null is OK; explicit 0 is invalid)
if (steps[i].Raw is DutyStep { EntryTargetId: 0 })
{
    errors.Add(new DraftValidationError("E22",
        $"Step '{steps[i].StepId}' has EntryTargetId=0 which is invalid. " +
        "Use null to omit entry target, or provide a valid NPC DataId.",
        [i]));
}
```

**Why this rule:** Follows the pattern of E8/E10/E14 (explicit-zero NPC targets). A DataId of 0 is never valid in FFXIV. The author likely meant to omit the field (null) or forgot to fill in the real value.

**No E-rule for EntryPosition:** Position3 has no concept of "zero is invalid" -- `(0,0,0)` is a valid world coordinate (albeit unusual). The validator does not reject it.

### R8: No new W-rule for missing Expect on SPD

The existing W1 guard already fires for `DutyStep(kind: "spd")` when Expect is absent. The SPD kind is not in the W1 suppression list (only `kind: "duty"` has its own W11). No new warning rule is needed.

### R9: No new adapter interface

This change does not add any new adapter interface. The retry logic uses existing adapters:
- `INavigator.NavigateTo` for navigation to entry NPC
- `IInteractor.InteractWith` for NPC interaction
- `ICondition[ConditionFlag.BoundByDuty]` for duty state check (Dalamud-native, not adapter)

**Rejected alternative:** An `ISpdEntryHandler` interface in QuestForge.Adapters. Rejected because the retry logic is EngineHost-specific orchestration, not a reusable adapter concern. Creating an interface for it would be over-abstraction.

### R10: EntryPosition without EntryTargetId is harmless

If a quest author sets `EntryPosition` but not `EntryTargetId`, the position is ignored. EngineHost's retry path only activates when `EntryTargetId` is present. No validation error is emitted for this case -- it is not harmful, just unused. The validator could warn, but the complexity is not worth it for an edge case that has no runtime consequence.

---

## Validation rules

| Rule | Code | Trigger | Suppression |
|------|------|---------|-------------|
| `EntryTargetId == 0` on any DutyStep | E22 | `step is DutyStep { EntryTargetId: 0 }` | None |

**Existing rules affected:** None. W1 already covers missing Expect on SPD. E21 is specific to `kind: "duty"` and `ContentFinderConditionId == 0`.

---

## Given-When-Then specifications

### R-S1: Happy path -- SPD with EntryTargetId, action carries fields

**Given:** A quest with sequence 0 containing a single `DutyStep`:
```csharp
new DutyStep
{
    Id = "spd-with-entry",
    Kind = "spd",
    ContentFinderConditionId = 830,
    EntryTargetId = 1045123,
    EntryPosition = new Position3(100f, 20f, -50f),
    Expect = new PredicateExpect { Predicate = "questSequence(70020) >= 3" }
}
```
FakeQuestBattleRunner.BossModAvailable = true. Quest 70020 at sequence 0.

**When:** Engine ticks once.

**Then:** Action is `EnterSinglePlayerDuty` with:
- `ContentFinderConditionId == 830`
- `EntryTargetId == 1045123`
- `EntryPosition == new Position3(100f, 20f, -50f)`
- `Origin.Id == "spd-with-entry"`

### R-S2: SPD without EntryTargetId -- action carries nulls

**Given:** Same as R-S1 but `EntryTargetId = null`, `EntryPosition = null`.

**When:** Engine ticks once.

**Then:** Action is `EnterSinglePlayerDuty` with:
- `ContentFinderConditionId == 830`
- `EntryTargetId == null`
- `EntryPosition == null`
- `Origin.Id == "spd-with-entry"`

### R-S3: SPD without ContentFinderConditionId -- action carries null CFC

**Given:** A DutyStep(kind: "spd") with no `ContentFinderConditionId` (legacy quest file). `EntryTargetId` is set.

**When:** Engine ticks once.

**Then:** Action is `EnterSinglePlayerDuty` with `ContentFinderConditionId == null`. Engine does not AwaitUser (CFC is optional for SPDs -- only required for kind "duty").

### R-S4: Existing S1 test updated -- action shape change

**Given:** The happy-path test from the original SPD plan (S1 in SINGLE_PLAYER_DUTY_PLAN.md). Quest with TalkStep + DutyStep.

**When:** Engine ticks past the TalkStep to the DutyStep.

**Then:** Action is `EnterSinglePlayerDuty`. The assertion now also verifies `ContentFinderConditionId`, `EntryTargetId`, and `EntryPosition` match the step's values (all null in the original S1 since the step does not set them).

### R-S5: Existing S2 test -- BossMod unavailable still returns AwaitUser

**Given:** Same as original S2. BossModAvailable = false. DutyStep with new fields set.

**When:** Engine ticks.

**Then:** `AwaitUser` with message containing "BossMod". The entry fields are never read because the pre-arm short-circuits before constructing the action.

### R-S6: Existing S5 test -- retry re-dispatches with same entry fields

**Given:** DutyStep with `EntryTargetId = 5000`, `EntryPosition = new Position3(10, 0, 20)`. FakeQuestBattleRunner.BossModAvailable = true. Quest sequence does not advance for 3 ticks.

**When:** Engine ticks 3 times. Then quest sequence advances. Engine ticks again.

**Then:** All 3 `EnterSinglePlayerDuty` actions carry `EntryTargetId == 5000` and `EntryPosition == new Position3(10, 0, 20)`. Fourth tick: step confirmed (Wait emitted). The entry fields are stable across retry dispatches.

### R-S7: JSON round-trip -- DutyStep with entry fields

**Given:** A `DutyStep` with all new fields set:
```csharp
new DutyStep
{
    Id = "spd-entry-rt",
    Kind = "spd",
    ContentFinderConditionId = 830,
    EntryTargetId = 1045123,
    EntryPosition = new Position3(100f, 20f, -50f)
}
```

**When:** Serialize as `Step`, deserialize back.

**Then:** Round-trip produces equal object. JSON contains `"entryTargetId": 1045123` and `"entryPosition": { "x": 100, "y": 20, "z": -50 }`.

### R-S8: JSON round-trip -- DutyStep without entry fields, fields absent from JSON

**Given:** A `DutyStep` with `EntryTargetId = null`, `EntryPosition = null`.

**When:** Serialize as `Step`.

**Then:** JSON does NOT contain `"entryTargetId"` or `"entryPosition"` (verified by `DoesNotContain`). `[JsonIgnore(WhenWritingNull)]` is honored.

### R-S9: DraftValidator E22 -- EntryTargetId == 0

**Given:** A draft with one DutyStep(kind: "spd") with `EntryTargetId = 0`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** Errors include E22 with message containing "EntryTargetId=0".

### R-S10: DraftValidator E22 -- EntryTargetId null is not an error

**Given:** A draft with one DutyStep(kind: "spd") with `EntryTargetId = null`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** No E22 error. (Other rules may fire, e.g. W1 for missing Expect, but E22 specifically does not.)

### R-S11: DraftValidator E22 -- EntryTargetId valid (non-zero, non-null)

**Given:** A draft with one DutyStep(kind: "spd") with `EntryTargetId = 5000`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** No E22 error.

### R-S12: Existing tests compile with updated action shape

**Given:** All 11 existing tests in `SinglePlayerDutyStepTests.cs` (S1-S10b).

**When:** Tests are updated to account for the new positional parameters on `EnterSinglePlayerDuty`.

**Then:** All tests compile and pass. Assertions on `IsType<EngineAction.EnterSinglePlayerDuty>` still work. Tests that don't set entry fields on their DutyStep see `null` in the action's entry fields.

---

## Implementation order

### Phase A -- Schema change (0.5 day)

1. Add `EntryTargetId` and `EntryPosition` to `DutyStep` in `QuestForge.Schema/Step.cs` with `[JsonIgnore(WhenWritingNull)]`
2. Add `[JsonSerializable(typeof(DutyStep))]` is already registered in `QuestForgeJsonContext.cs` -- verify no changes needed
3. Write round-trip tests R-S7 and R-S8 in `QuestForge.Schema.Tests/RoundTripTests.cs`
4. Mirror the change in `questforge-tools/QuestForge.Schema/Step.cs`

**Done before Phase B.**

### Phase B -- EngineAction shape change (0.5 day)

1. Update `EngineAction.EnterSinglePlayerDuty` in `QuestForge.Engine/EngineAction.cs`:
   ```csharp
   public sealed record EnterSinglePlayerDuty(
       uint? ContentFinderConditionId,
       uint? EntryTargetId,
       Position3? EntryPosition,
       Step? Origin = null) : EngineAction;
   ```
2. Update `ResolveSpd` in `QuestEngine.cs` to pass new fields from `DutyStep` to the action record
3. Update `EngineTestHarness` dispatch arm for `EnterSinglePlayerDuty` to log new fields
4. Fix all compilation errors in existing `SinglePlayerDutyStepTests.cs` (the record's positional parameters changed)

**Done before Phase C.**

### Phase C -- Validator rule (0.5 day)

1. Add E22 rule to `DraftValidator.cs`
2. Write tests R-S9, R-S10, R-S11 in `QuestForge.Engine.Tests/Authoring/DraftValidatorDutyTests.cs`

**Done before Phase D.**

### Phase D -- New engine tests (1 day)

1. Write R-S1 through R-S6 in `SinglePlayerDutyStepTests.cs`
2. Verify R-S12: all existing tests pass with the updated action shape
3. Run full test suite: `dotnet test QuestForge.Engine.Tests`

**Done before Phase E.**

### Phase E -- EngineHost retry logic (1 day, separate PR)

1. Update `EngineHost.DispatchAction` for `EnterSinglePlayerDuty`:
   - Read `EntryTargetId` and `EntryPosition` from action
   - Check `ConditionFlag.BoundByDuty` to determine overworld vs inside-duty
   - If overworld + entry target: NavigateTo + InteractWith
   - Wire `TryHandleDifficultySelect()` call
2. Update debounce log to include `cfcId` and `entryTarget`
3. Manual in-game verification

**Done before Phase F.**

### Phase F -- Tools-repo mirror (0.5 day)

1. Mirror `DutyStep` changes in `questforge-tools/QuestForge.Schema/Step.cs`
2. No other tools changes needed (no new action type; `TraceConstants.ActionEnterSinglePlayerDuty` already exists)

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests` passes with all R-S1 through R-S12 scenarios green
2. `dotnet test QuestForge.Schema.Tests` passes with R-S7 and R-S8 round-trip tests green
3. `dotnet test QuestForge.Engine.Tests` passes with all 11 existing `SinglePlayerDutyStepTests` updated and green
4. `EngineAction.EnterSinglePlayerDuty` carries `ContentFinderConditionId`, `EntryTargetId`, `EntryPosition` -- verified by new tests
5. DraftValidator E22 catches `EntryTargetId == 0` and passes `EntryTargetId == null` without error
6. JSON serialization of `DutyStep` with entry fields: fields present when set, absent when null
7. `questforge-tools/QuestForge.Schema/Step.cs` mirrors the new fields on `DutyStep`
8. `TryHandleDifficultySelect()` is wired in EngineHost's `EnterSinglePlayerDuty` dispatch arm (manual verification)
9. EngineHost retry path checks `ConditionFlag.BoundByDuty` before navigate+interact (manual verification + code review)

---

## What this plan does NOT include

- **New adapter interfaces** -- retry uses existing `INavigator`, `IInteractor`, `ICondition`
- **BoundByDuty on IGameStateProvider** -- the check is Dalamud-native (`ICondition`), not an adapter method; only EngineHost (Plugin layer) uses it
- **MaxDutyRetries counter** -- infinite retry remains the default; a counter is a separate future concern
- **IDutyRunner changes** -- the BossMod adapter is untouched; `StartDuty`/`StopDuty` signatures are unchanged
- **ICfcResolver changes** -- CFC resolution is for `kind: "duty"` (dungeons/trials), not SPDs
- **Death recovery routing** -- handled by existing stateless retry architecture
- **Authoring inference for entry fields** -- entry target/position are quest-level data authored manually; inference would require tracking which NPC triggered the SPD entry, which is a separate concern
- **`TryHandleDifficultySelect` FireCallback verification** -- the builder must verify exact AtkValue layout in-game before the Dalamud PR goes live (already noted in SINGLE_PLAYER_DUTY_PLAN.md SPD6)
- **Engine-level overworld detection** -- the engine does not know about `BoundByDuty`; this is EngineHost's concern (R5)

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in R-S1 through R-S12.
- Happy paths: 4 scenarios (R-S1, R-S2, R-S3, R-S4)
- Edge cases: 4 scenarios (R-S6, R-S7, R-S8, R-S12)
- Error/validation cases: 4 scenarios (R-S5, R-S9, R-S10, R-S11)
- Expected total: ~12-15 tests across SinglePlayerDutyStepTests.cs, RoundTripTests.cs, DraftValidatorDutyTests.cs
