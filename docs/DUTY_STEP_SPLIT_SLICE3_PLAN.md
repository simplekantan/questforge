# DutyStep Split Slice 3: EngineHost Dispatch + Tools-Repo Catch-Up

**Status:** Draft
**Phase:** 11 (Corpus Expansion)
**Slice:** 3 of 6 (Dalamud impl + tools-repo catch-up)
**Author:** QuestForge System Architect
**Date:** 2026-06-11

---

## 1. Header

**Input documents:**
- `docs/DUTY_STEP_SPLIT_PLAN.md` -- full architect spec (DS1-DS43); especially DS9 (host dispatch for EnterSinglePlayerDuty with entry-kind), DS10 (EnterDuty unchanged), DS11-DS12 (cleanup unchanged), DS13 (lazy dismount), DS14 (no recording proxy), DS24 (tools-repo catch-up)
- `QuestForge.Plugin/EngineHost.cs` -- current dispatch arms for `EnterSinglePlayerDuty` (line 722) and `EnterDuty` (line 736), cleanup logic for `_activeSpdStepId` / `_activeDutyStepId`
- `QuestForge.Schema/Step.cs` -- `DungeonTrialStep`, `SinglePlayerDutyStep`, `SpdEntryKind` already exist (Slice 2)
- `QuestForge.Engine/EngineAction.cs` -- `EnterSinglePlayerDuty` and `EnterDuty` records unchanged
- `questforge-tools/QuestForge.Schema/Step.cs` -- tools-repo schema (does NOT yet have the new types)
- `questforge-tools/QuestForge.Tools.Trace/Capabilities/CapabilityInferrer.cs` -- current step-type dict
- `questforge-tools/QuestForge.Tools.Trace/Fixture/TraceToFixtureExtractor.cs` -- FilenameLookup + DistinguishingCapPriority
- `questforge-tools/QuestForge.Tools.Trace/TraceConstants.cs` -- existing `ActionEnterSinglePlayerDuty` and `ActionEnterDuty`
- `questforge-tools/QuestForge.Tools.Validator/StructuralValidator.cs` -- `ValidateDutyStep` for old DutyStep

**Output (CI behavior changes):**
- `dotnet test QuestForge.Tools.Trace.Tests` gains ~9 new tests covering capability inference, fixture filename suggestion, and structural validation for the two new step types
- `dotnet test QuestForge.Tools.Validator.Tests` gains ~10 new tests for structural validation of DungeonTrialStep and SinglePlayerDutyStep
- Old `DutyStep` tests in tools-repo continue to pass (DutyStep is NOT removed from tools-repo in this slice -- coexistence)
- **No new tests for EngineHost** -- EngineHost is plugin code that runs in Dalamud; changes are verified via in-game smoke (Slice 4)

**Phase dependencies:**
- Slice 2 complete: `DungeonTrialStep`, `SinglePlayerDutyStep`, `SpdEntryKind` exist in main repo schema
- `DalamudObjectInteractor` exists at `QuestForge.Adapters.Dalamud/Interaction/DalamudObjectInteractor.cs`
- `IObjectInteractor.InteractWithObject(InteractableId, CancellationToken)` exists
- `_objectInteractor` field declared on EngineHost (line 68)
- `EnterSinglePlayerDuty` dispatch arm exists at EngineHost line 722 (to be modified)
- `EnterDuty` dispatch arm exists at EngineHost line 736 (unchanged)

---

## 2. Dependency Graph

```
questforge repo (EngineHost changes)
    |
    +-- S3_D1-S3_D8: EngineHost.DispatchAction entry-kind dispatch for EnterSinglePlayerDuty
    |
    v
questforge-tools repo (catch-up, paired PR)
    |
    +-- S3_D9-S3_D12: Schema mirror (DungeonTrialStep, SinglePlayerDutyStep, SpdEntryKind)
    +-- S3_D13-S3_D14: CapabilityInferrer + tests
    +-- S3_D15-S3_D17: TraceToFixtureExtractor (FilenameLookup, DistinguishingCapPriority) + tests
    +-- S3_D18: TraceConstants verification
    +-- S3_D19-S3_D21: StructuralValidator (new step-type rules, replace ValidateDutyStep) + tests
    +-- S3_D22: QuestForgeJsonContext updates
    +-- S3_D23: FIXTURES.md documentation updates
```

Build order: Tools-repo schema changes first (types must compile), then CapabilityInferrer, then TraceToFixtureExtractor, then StructuralValidator. EngineHost changes are independent. Both PRs must merge together.

---

## 3. Architectural Decisions

### S3_D1: EnterSinglePlayerDuty dispatch arm -- add entry-kind dispatch

**Decision:** The existing `case EngineAction.EnterSinglePlayerDuty espd:` arm at EngineHost.cs line 722 is modified to dispatch the entry mechanism based on the step's `EntryKind`. The current arm blindly calls `_questBattleRunner.StartDuty()` + `_interactor.AdvanceDialogue()` with no entry mechanism. The new arm reads `EntryKind` from the `Origin` step and dispatches:

```csharp
case EngineAction.EnterSinglePlayerDuty espd:
    DebounceLog(
        $"enterspd:{espd.Origin?.Id}",
        $"[EnterSinglePlayerDuty] stepId={espd.Origin?.Id ?? "(unknown)"}" +
        $" cfcId={espd.ContentFinderConditionId}" +
        $" entryTarget={espd.EntryTargetId}" +
        $" entryKind={GetEntryKind(espd)}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    _activeSpdStepId = espd.Origin?.Id;
    TryCutsceneSkipConfirm();

    // Entry mechanism dispatch based on step's EntryKind
    var entryKind = GetEntryKind(espd);
    switch (entryKind)
    {
        case SpdEntryKind.Talk:
            if (espd.EntryTargetId is { } talkNpcId)
                await _interactor.InteractWith(new NpcId(talkNpcId), ct);
            break;
        case SpdEntryKind.Interact:
            if (espd.EntryTargetId is { } objId)
                await _objectInteractor.InteractWithObject(
                    new InteractableId(objId), ct);
            break;
        case SpdEntryKind.Proximity:
            // No interaction needed -- area trigger initiates the SPD.
            break;
    }

    await _questBattleRunner.StartDuty(ct);
    await _interactor.AdvanceDialogue(ct);
    break;
```

**Alternatives considered:**
- Adding `EntryKind` as a field on `EngineAction.EnterSinglePlayerDuty`. Rejected per DS9: the action record should not widen for information only the host needs. The host reads it from `Origin`.
- Branching in the engine resolver instead of the host. Rejected per DS7: entry-kind dispatch is a host concern; the engine emits the same action regardless of entry kind.

**What breaks if violated:** If someone keeps the current blindly-call-StartDuty pattern, SPD entry via interact-object or proximity fails. Talk-entry SPDs also miss the explicit NPC interaction (currently relying on implicit proximity + dialogue).

**Testability:** No unit tests -- EngineHost is plugin code. Verified via in-game smoke (Slice 4). The engine-side tests (Slice 2) verify the action is emitted correctly; this slice verifies the host dispatches it correctly.

### S3_D2: GetEntryKind helper method

**Decision:** A private static helper reads `EntryKind` from the `Origin` step:

```csharp
private static SpdEntryKind GetEntryKind(EngineAction.EnterSinglePlayerDuty espd) =>
    espd.Origin is SinglePlayerDutyStep spd ? spd.EntryKind : SpdEntryKind.Talk;
```

**Fallback to `Talk`:** If `Origin` is somehow not a `SinglePlayerDutyStep` (impossible in practice given the engine only emits `EnterSinglePlayerDuty` from `ResolveSinglePlayerDuty`), the fallback to `Talk` is the safest default -- it calls `InteractWith` on the entry target, which is the pre-existing behavior.

**Why static:** The method has no side effects and no instance state dependencies. Static prevents accidental coupling to EngineHost fields.

### S3_D3: DebounceLog message update -- add entryKind

**Decision:** The `DebounceLog` message for `EnterSinglePlayerDuty` is updated to include `entryKind`. Currently:

```
[EnterSinglePlayerDuty] stepId=... cfcId=... entryTarget=...
```

Becomes:

```
[EnterSinglePlayerDuty] stepId=... cfcId=... entryTarget=... entryKind=talk
```

This aids debugging by showing which entry mechanism was selected. The debounce key remains `enterspd:{stepId}` -- no change needed since the key is per-step, not per-field.

### S3_D4: EnterDuty dispatch arm -- no changes

**Decision:** The existing `case EngineAction.EnterDuty ed:` arm at EngineHost.cs line 736 is unchanged. It already correctly resolves CFC to territory type via `_cfcResolver.GetTerritoryType()` and calls `_dutyRunner.StartDuty(territoryType)`. The engine now emits `EnterDuty` from `ResolveDungeonTrial(DungeonTrialStep)` instead of from the old `ResolveDungeonTrial(DutyStep)`, but the action record and dispatch are identical.

### S3_D5: SPD cleanup tracking -- no changes

**Decision:** The existing `_activeSpdStepId` tracking and cleanup logic at EngineHost.cs lines 393-399 is unchanged. The cleanup fires when the engine advances past the SPD step (emits anything other than `EnterSinglePlayerDuty` or `Wait`). This works identically for `SinglePlayerDutyStep`.

### S3_D6: Duty cleanup tracking -- no changes

**Decision:** The existing `_activeDutyStepId` tracking and cleanup logic at EngineHost.cs lines 402-409 is unchanged. Same reasoning as S3_D5.

### S3_D7: Lazy dismount -- no changes needed

**Decision:** Neither `EnterSinglePlayerDuty` nor `EnterDuty` appear in the exemption list at EngineHost.cs line 419. They are already NOT exempt. No change needed. The player must be dismounted to interact with NPCs, objects, or enter instanced content.

### S3_D8: Recording proxy -- no new wrappers

**Decision:** No `RecordingXxx` wrappers are added for either step type. Both are write-only adapter patterns. The `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` already capture the dispatch. Consistent with DS14.

### S3_D9: Tools-repo schema -- add DungeonTrialStep and SinglePlayerDutyStep

**Decision:** Mirror the main repo's new types into `questforge-tools/QuestForge.Schema/Step.cs`:

1. Add `SpdEntryKind` enum with `[JsonConverter]` and `[JsonStringEnumMemberName]` attributes:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<SpdEntryKind>))]
public enum SpdEntryKind
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("talk")]
    Talk,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("interact")]
    Interact,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("proximity")]
    Proximity
}
```

2. Add `DungeonTrialStep`:

```csharp
public sealed class DungeonTrialStep : Step
{
    public uint ContentFinderConditionId { get; init; }
}
```

3. Add `SinglePlayerDutyStep`:

```csharp
public sealed class SinglePlayerDutyStep : Step
{
    public uint ContentFinderConditionId { get; init; }
    public SpdEntryKind EntryKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? EntryTargetId { get; init; }
    public Position3 EntryPosition { get; init; } = default!;
}
```

4. Add `[JsonDerivedType]` attributes to the `Step` class:

```csharp
[JsonDerivedType(typeof(DungeonTrialStep),      "dungeon-trial")]
[JsonDerivedType(typeof(SinglePlayerDutyStep),   "single-player-duty")]
```

**Why NOT remove DutyStep from tools-repo:** The old `DutyStep` coexists in the tools-repo for now. Existing quest files in `questforge-data` may still use `"type": "duty"`. The tools-repo validator must continue to validate both old and new types until the data migration PR lands. Removing `DutyStep` from the tools-repo happens in the migration slice, not this one.

### S3_D10: Tools-repo QuestForgeJsonContext -- add new serializable types

**Decision:** Add the following to `questforge-tools/QuestForge.Schema/QuestForgeJsonContext.cs`:

```csharp
[JsonSerializable(typeof(DungeonTrialStep))]
[JsonSerializable(typeof(SinglePlayerDutyStep))]
[JsonSerializable(typeof(SpdEntryKind))]
```

Do NOT remove `[JsonSerializable(typeof(DutyStep))]` -- DutyStep coexists.

### S3_D11: Tools-repo schema -- add UseItemOnObjectStep if missing

**Decision:** Verify that `UseItemOnObjectStep` already exists in the tools-repo schema (it does, at line 184-191 of `questforge-tools/QuestForge.Schema/Step.cs`). No action needed for this type.

### S3_D12: Tools-repo schema -- no AetheryteId type needed for SpdEntryKind

**Decision:** `SpdEntryKind` is a new enum with no external type dependencies. `AetheryteId` is already present in the tools-repo schema. `InteractableId` is not referenced by `SinglePlayerDutyStep` (the schema uses `uint? EntryTargetId`, not a strong-typed ID). No additional type additions needed.

### S3_D13: CapabilityInferrer -- add entries for new step types

**Decision:** Add two new entries to `StepCapabilities`:

```csharp
[typeof(DungeonTrialStep)]       = "step:dungeon-trial",
[typeof(SinglePlayerDutyStep)]   = "step:single-player-duty",
```

**Do NOT remove `[typeof(DutyStep)] = "step:duty"`:** The old DutyStep entry stays because DutyStep still exists in the tools-repo schema. Old quest files with `"type": "duty"` still deserialize as `DutyStep` and must emit `step:duty`.

**Do NOT remove the DutyStep kind branching:** The special-case code at CapabilityInferrer.cs lines 66-74 that emits `step:spd` / `step:dungeon-trial` for `DutyStep.Kind` stays. This allows fixtures extracted from quests that still use old `DutyStep` to get the correct distinguishing capability. It will be removed when `DutyStep` is removed from the tools-repo schema.

**What breaks if violated:** If the new entries are missing, quests using `DungeonTrialStep` or `SinglePlayerDutyStep` get no step capability tag. Fixtures extracted from those quests would have an incomplete capability list, and `SuggestFilename` would fall through to the wrong default.

### S3_D14: CapabilityInferrer -- no special-case branching needed for new types

**Decision:** Unlike `DutyStep` which needed `Kind`-based sub-tagging (`step:spd`, `step:dungeon-trial`), the new types are their own classes with their own discriminators. The standard `StepCapabilities` dictionary lookup is sufficient. No special-case branching needed for `DungeonTrialStep` or `SinglePlayerDutyStep`.

### S3_D15: TraceToFixtureExtractor FilenameLookup -- add new entries

**Decision:** Add two new entries to `FilenameLookup`:

```csharp
(["step:dungeon-trial"], "with-dungeon-trial.json"),
(["step:single-player-duty"], "with-spd.json"),
```

Wait -- `(["step:dungeon-trial"], "with-dungeon-trial.json")` already exists at line 47. Only `(["step:single-player-duty"], "with-spd.json")` needs to be added.

**Why reuse `"with-spd.json"` for single-player-duty:** The fixture file `with-spd.json` already exists and tests SPD functionality. The capability tag changes from `step:spd` (old DutyStep branching) to `step:single-player-duty` (new type), but the fixture filename stays the same because the underlying game mechanic is the same.

**Do NOT remove old entries:** `(["step:duty"], "with-dungeon.json")` and `(["step:spd"], "with-spd.json")` stay for backward compatibility with quests still using old `DutyStep`.

### S3_D16: TraceToFixtureExtractor DistinguishingCapPriority -- add new entry

**Decision:** Add to `DistinguishingCapPriority`:

```csharp
("step:single-player-duty", "with-spd.json"),
```

Position: immediately after `("step:dungeon-trial", "with-dungeon-trial.json")`. Single-player duties are as shape-defining as dungeon/trials.

**Do NOT remove old entries:** `("step:duty", "with-dungeon.json")` and `("step:spd", "with-spd.json")` stay.

### S3_D17: TraceToFixtureExtractor -- verify existing dungeon-trial entry covers DungeonTrialStep

**Decision:** The `FilenameLookup` entry `(["step:dungeon-trial"], "with-dungeon-trial.json")` at line 47 already covers `DungeonTrialStep` exactly. The `DistinguishingCapPriority` entry `("step:dungeon-trial", "with-dungeon-trial.json")` at line 57 also already exists. No changes needed for dungeon-trial fixture suggestion.

### S3_D18: TraceConstants -- no changes needed

**Decision:** `TraceConstants.cs` already has:

```csharp
internal const string ActionEnterSinglePlayerDuty = "entersingleplayerduty";
internal const string ActionEnterDuty             = "enterduty";
```

These constants document what `EngineAction.EnterSinglePlayerDuty.GetType().Name.ToLowerInvariant()` produces. The new step types emit the same `EngineAction` subtypes as the old `DutyStep`, so no new constants are needed.

### S3_D19: StructuralValidator -- add DungeonTrialStep validation

**Decision:** Add a `ValidateDungeonTrialStep` method to `StructuralValidator.cs` and wire it into the `CheckStepTypeRules` switch:

```csharp
case DungeonTrialStep dungeonTrial:
    ValidateDungeonTrialStep(dungeonTrial, scope, ctx, errors);
    break;
```

```csharp
private static void ValidateDungeonTrialStep(
    DungeonTrialStep step, ValidationScope scope, ValidationContext ctx,
    List<ValidationError> errors)
{
    if (step.ContentFinderConditionId == 0)
        errors.Add(E(ctx, "structural/dungeon-trial-cfc-zero", scope.ToString(),
            $"Step '{step.Id}': DungeonTrialStep 'contentFinderConditionId' is 0; " +
            "a valid ContentFinderCondition row ID is required.",
            stepId: step.Id));
}
```

**Error code:** `structural/dungeon-trial-cfc-zero` -- mirrors the engine-side E29 from DraftValidator but uses the tools-repo's `structural/` prefix convention. The tools-repo validator uses `structural/` prefixed codes, not bare `E29` codes (those are engine-side DraftValidator codes).

**Why not mirror E29 verbatim:** The tools-repo `StructuralValidator` and the engine-side `DraftValidator` are separate validators with separate code namespaces. The tools-repo uses descriptive kebab-case codes (`structural/purchase-item-id-zero`), not numbered codes (`E6`). Consistency with the existing tools-repo pattern is more important than matching the engine-side numbering.

### S3_D20: StructuralValidator -- add SinglePlayerDutyStep validation

**Decision:** Add a `ValidateSinglePlayerDutyStep` method:

```csharp
case SinglePlayerDutyStep spd:
    ValidateSinglePlayerDutyStep(spd, scope, ctx, errors);
    break;
```

```csharp
private static void ValidateSinglePlayerDutyStep(
    SinglePlayerDutyStep step, ValidationScope scope, ValidationContext ctx,
    List<ValidationError> errors)
{
    // structural/spd-cfc-zero -- mirrors engine-side E30
    if (step.ContentFinderConditionId == 0)
        errors.Add(E(ctx, "structural/spd-cfc-zero", scope.ToString(),
            $"Step '{step.Id}': SinglePlayerDutyStep 'contentFinderConditionId' is 0; " +
            "a valid ContentFinderCondition row ID is required.",
            stepId: step.Id));

    // structural/spd-entry-target-null -- mirrors engine-side E31
    if (step.EntryKind is SpdEntryKind.Talk or SpdEntryKind.Interact
        && step.EntryTargetId is null)
        errors.Add(E(ctx, "structural/spd-entry-target-null", scope.ToString(),
            $"Step '{step.Id}': EntryKind '{step.EntryKind}' requires 'entryTargetId' " +
            "but it is null. Talk and Interact entry kinds require an entry target.",
            stepId: step.Id));

    // structural/spd-entry-target-zero -- mirrors engine-side E32
    if (step.EntryKind is SpdEntryKind.Talk or SpdEntryKind.Interact
        && step.EntryTargetId == 0)
        errors.Add(E(ctx, "structural/spd-entry-target-zero", scope.ToString(),
            $"Step '{step.Id}': EntryKind '{step.EntryKind}' has 'entryTargetId' == 0; " +
            "provide a valid NPC/EventObj DataId.",
            stepId: step.Id));

    // structural/spd-proximity-has-target -- mirrors engine-side E33
    if (step.EntryKind == SpdEntryKind.Proximity && step.EntryTargetId is not null)
        errors.Add(E(ctx, "structural/spd-proximity-has-target", scope.ToString(),
            $"Step '{step.Id}': EntryKind 'proximity' but 'entryTargetId' is set. " +
            "Proximity entries use area triggers, not interactable targets.",
            stepId: step.Id));
}
```

**Why four rules, not five:** The engine-side DraftValidator has W14/W15 (missing Expect warnings). The tools-repo `StructuralValidator` does not warn on missing Expect for any step type -- that is an engine-authoring concern, not a structural schema concern. If someone wants to add it later, they can, but it would be a separate decision.

### S3_D21: StructuralValidator -- keep old ValidateDutyStep

**Decision:** The existing `ValidateDutyStep` method and its `case DutyStep duty:` arm remain. Old quest files with `"type": "duty"` must still validate. When `DutyStep` is removed from the tools-repo schema (migration slice), this code is removed too.

### S3_D22: FIXTURES.md documentation updates

**Decision:** Update `docs/FIXTURES.md` in the main repo:

1. **Step capabilities table:** Add two new rows:
   - `step:dungeon-trial` -- `DungeonTrialStep` (replaces `DutyStep` with `kind: duty`)
   - `step:single-player-duty` -- `SinglePlayerDutyStep` (replaces `DutyStep` with `kind: spd`)
   
   Keep the old `step:duty` and `step:spd` rows with a note "(legacy -- use `step:dungeon-trial` / `step:single-player-duty` for new quests)".

2. **actionType canonical strings table:** Update the Notes column for existing entries:
   - `"entersingleplayerduty"` notes: change from `DutyStep(kind:"spd")` to `SinglePlayerDutyStep` (or `DutyStep(kind:"spd")` for legacy)
   - `"enterduty"` notes: change from `DutyStep(kind:"duty")` to `DungeonTrialStep` (or `DutyStep(kind:"duty")` for legacy)

3. **Fixture naming convention section:** Add:
   - `with-dungeon-trial.json` is already listed. No change needed.
   - No new fixture file for `with-single-player-duty.json` -- the existing `with-spd.json` covers this shape.

### S3_D23: No new EngineHost fields or properties

**Decision:** No new fields, properties, or constructor parameters are added to `EngineHost`. All required adapters (`_interactor`, `_objectInteractor`, `_questBattleRunner`) already exist as fields. The `GetEntryKind` helper is a static method that takes the action as a parameter.

### S3_D24: IObjectInteractor null safety for interact entry kind

**Decision:** If `_objectInteractor` were null, the `InteractWithObject` call would throw a `NullReferenceException`. However, `_objectInteractor` is always initialized in the EngineHost constructor (line 153: `_objectInteractor = new DalamudObjectInteractor(_interactor);`). It is never null. No null guard is added.

**Why not add a defensive null check:** Adding a null check would be dead code that misleads readers into thinking `_objectInteractor` can be null. The field is assigned in the constructor and never reassigned. If someone refactors the constructor to make it nullable, the compiler will flag the dereference.

### S3_D25: AdvanceDialogue after entry for all entry kinds

**Decision:** After the entry-kind switch, the host always calls `_questBattleRunner.StartDuty(ct)` followed by `_interactor.AdvanceDialogue(ct)`. This is unchanged from the current behavior (line 732-733), except that now the entry mechanism fires before these calls.

For `Proximity`, no interaction occurs in the switch body. The `StartDuty` and `AdvanceDialogue` calls handle the post-trigger prompts (difficulty selection, etc.) that appear after the area trigger fires.

For `Talk` and `Interact`, the NPC/object interaction may open dialogue or a confirmation prompt. `StartDuty` activates BossMod, and `AdvanceDialogue` handles any remaining prompts.

### S3_D26: No changes to ExtractOriginStep

**Decision:** The `ExtractOriginStep` switch expression at EngineHost.cs line 959 already has:
```csharp
EngineAction.EnterSinglePlayerDuty a => a.Origin,
EngineAction.EnterDuty a             => a.Origin,
```

No changes needed.

---

## 4. Tools-Repo Task Breakdown

### Task 1: Schema mirror (questforge-tools/QuestForge.Schema/Step.cs)

**Deliverables:**

1. Add `SpdEntryKind` enum with JSON converter attributes.
2. Add `DungeonTrialStep` sealed class with `ContentFinderConditionId`.
3. Add `SinglePlayerDutyStep` sealed class with all four fields.
4. Add `[JsonDerivedType]` attributes for both new types to the `Step` class.
5. Keep all existing types including `DutyStep`.
6. Update `QuestForgeJsonContext.cs` with `[JsonSerializable]` entries for the three new types.

### Task 2: CapabilityInferrer (questforge-tools/QuestForge.Tools.Trace/Capabilities/)

**Deliverables:**

1. Add `[typeof(DungeonTrialStep)] = "step:dungeon-trial"` to `StepCapabilities`.
2. Add `[typeof(SinglePlayerDutyStep)] = "step:single-player-duty"` to `StepCapabilities`.
3. Keep existing `[typeof(DutyStep)] = "step:duty"` entry.
4. Keep existing DutyStep kind branching (lines 66-74).

### Task 3: TraceToFixtureExtractor (questforge-tools/QuestForge.Tools.Trace/Fixture/)

**Deliverables:**

1. Add `(["step:single-player-duty"], "with-spd.json")` to `FilenameLookup`.
2. Verify `(["step:dungeon-trial"], "with-dungeon-trial.json")` already exists (it does -- line 47).
3. Add `("step:single-player-duty", "with-spd.json")` to `DistinguishingCapPriority`, immediately after the `step:dungeon-trial` entry.

### Task 4: StructuralValidator (questforge-tools/QuestForge.Tools.Validator/)

**Deliverables:**

1. Add `case DungeonTrialStep:` arm to `CheckStepTypeRules` switch.
2. Add `ValidateDungeonTrialStep` private static method.
3. Add `case SinglePlayerDutyStep:` arm to `CheckStepTypeRules` switch.
4. Add `ValidateSinglePlayerDutyStep` private static method.
5. Keep existing `case DutyStep:` arm.

### Task 5: Tests for all tools-repo changes

See Section 6 (Given-When-Then Specs).

---

## 5. Validation Rule Table (Tools-Repo StructuralValidator)

### New Rules

| Code | Level | Step Type | Condition | Message |
|------|-------|-----------|-----------|---------|
| `structural/dungeon-trial-cfc-zero` | Error | DungeonTrialStep | `ContentFinderConditionId == 0` | "Step '{stepId}': DungeonTrialStep 'contentFinderConditionId' is 0; a valid ContentFinderCondition row ID is required." |
| `structural/spd-cfc-zero` | Error | SinglePlayerDutyStep | `ContentFinderConditionId == 0` | "Step '{stepId}': SinglePlayerDutyStep 'contentFinderConditionId' is 0; a valid ContentFinderCondition row ID is required." |
| `structural/spd-entry-target-null` | Error | SinglePlayerDutyStep | `EntryKind in (Talk, Interact)` AND `EntryTargetId is null` | "Step '{stepId}': EntryKind '{kind}' requires 'entryTargetId' but it is null. Talk and Interact entry kinds require an entry target." |
| `structural/spd-entry-target-zero` | Error | SinglePlayerDutyStep | `EntryKind in (Talk, Interact)` AND `EntryTargetId == 0` | "Step '{stepId}': EntryKind '{kind}' has 'entryTargetId' == 0; provide a valid NPC/EventObj DataId." |
| `structural/spd-proximity-has-target` | Error | SinglePlayerDutyStep | `EntryKind is Proximity` AND `EntryTargetId is not null` | "Step '{stepId}': EntryKind 'proximity' but 'entryTargetId' is set. Proximity entries use area triggers, not interactable targets." |

### Unchanged Rules

| Code | Status |
|------|--------|
| `structural/duty-missing-required-field` | Kept -- applies to old DutyStep |

---

## 6. Given-When-Then Specs

### CapabilityInferrer Tests: `CapabilityInferrerTests.cs` (additions)

---

#### S3_CI_T1: DungeonTrialStep emits step:dungeon-trial capability

**Given:**
- QuestDefinition with one DungeonTrialStep (ContentFinderConditionId=2, Expect="questSequence(90001) >= 3")

**When:** CapabilityInferrer.Infer(quest)

**Then:**
- Result contains "step:dungeon-trial"
- Result contains "predicate:questSequence" (from the Expect predicate)

---

#### S3_CI_T2: SinglePlayerDutyStep emits step:single-player-duty capability

**Given:**
- QuestDefinition with one SinglePlayerDutyStep (ContentFinderConditionId=830, EntryKind=Talk, EntryTargetId=1045123, EntryPosition=(10,0,10), Expect="questSequence(91001) >= 3")

**When:** CapabilityInferrer.Infer(quest)

**Then:**
- Result contains "step:single-player-duty"
- Result contains "predicate:questSequence"

---

#### S3_CI_T3: Old DutyStep still emits step:duty (coexistence)

**Given:**
- QuestDefinition with one DutyStep (Kind="duty", ContentFinderConditionId=2)

**When:** CapabilityInferrer.Infer(quest)

**Then:**
- Result contains "step:duty"
- Result contains "step:dungeon-trial" (from the Kind="duty" branching)
- Result does NOT contain "step:single-player-duty"

---

#### S3_CI_T4: Quest with both DungeonTrialStep and SinglePlayerDutyStep emits both capabilities

**Given:**
- QuestDefinition with two steps: DungeonTrialStep and SinglePlayerDutyStep

**When:** CapabilityInferrer.Infer(quest)

**Then:**
- Result contains both "step:dungeon-trial" and "step:single-player-duty"
- Result is sorted alphabetically: "step:dungeon-trial" appears before "step:single-player-duty"

---

### TraceToFixtureExtractor Tests: `TraceToFixtureExtractorTests.cs` (additions)

---

#### S3_FX_T1: SuggestFilename for single-player-duty capability returns with-spd.json

**Given:**
- FixtureModel with Capabilities = ["step:single-player-duty"]

**When:** SuggestFilename(fixture)

**Then:**
- Returns "with-spd.json"

---

#### S3_FX_T2: SuggestFilename for dungeon-trial capability returns with-dungeon-trial.json

**Given:**
- FixtureModel with Capabilities = ["step:dungeon-trial"]

**When:** SuggestFilename(fixture)

**Then:**
- Returns "with-dungeon-trial.json"

---

#### S3_FX_T3: SuggestFilename -- single-player-duty wins over old step:spd in priority

**Given:**
- FixtureModel with Capabilities = ["step:single-player-duty", "step:talk", "step:travel"]
  (no exact match in FilenameLookup)

**When:** SuggestFilename(fixture)

**Then:**
- Returns "with-spd.json" (from DistinguishingCapPriority fallback, "step:single-player-duty" is higher priority than "step:talk" or "step:travel")

---

#### S3_FX_T4: SuggestFilename -- dungeon-trial wins over single-player-duty when both present

**Given:**
- FixtureModel with Capabilities = ["step:dungeon-trial", "step:single-player-duty", "step:travel"]
  (no exact match in FilenameLookup)

**When:** SuggestFilename(fixture)

**Then:**
- Returns "with-dungeon-trial.json" (from DistinguishingCapPriority fallback, "step:dungeon-trial" appears before "step:single-player-duty" in the priority list)

---

### StructuralValidator Tests: `DungeonTrialStepValidationTests.cs` (new file)

---

#### S3_SV_DT_T1: DungeonTrialStep valid -- no errors

**Given:**
- QuestDefinition with DungeonTrialStep where ContentFinderConditionId = 2, Expect = "questSequence(90001) >= 3"

**When:** StructuralValidator.Validate(quest)

**Then:**
- No errors with code "structural/dungeon-trial-cfc-zero"

---

#### S3_SV_DT_T2: DungeonTrialStep CFC == 0 -- error

**Given:**
- QuestDefinition with DungeonTrialStep where ContentFinderConditionId = 0

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains exactly one entry with Code == "structural/dungeon-trial-cfc-zero"
- Message contains "contentFinderConditionId" and "0"

---

#### S3_SV_DT_T3: DungeonTrialStep CFC non-zero -- no error

**Given:**
- QuestDefinition with DungeonTrialStep where ContentFinderConditionId = 167

**When:** StructuralValidator.Validate(quest)

**Then:**
- No errors with code "structural/dungeon-trial-cfc-zero"

---

### StructuralValidator Tests: `SinglePlayerDutyStepValidationTests.cs` (new file)

---

#### S3_SV_SPD_T1: Valid SinglePlayerDutyStep (talk entry) -- no errors

**Given:**
- QuestDefinition with SinglePlayerDutyStep where:
  - ContentFinderConditionId = 830
  - EntryKind = Talk
  - EntryTargetId = 1045123
  - EntryPosition = (10, 0, 10)
  - Expect = "questSequence(91001) >= 3"

**When:** StructuralValidator.Validate(quest)

**Then:**
- No errors with any `structural/spd-*` code

---

#### S3_SV_SPD_T2: Valid SinglePlayerDutyStep (proximity, null target) -- no errors

**Given:**
- QuestDefinition with SinglePlayerDutyStep where:
  - ContentFinderConditionId = 832
  - EntryKind = Proximity
  - EntryTargetId = null
  - EntryPosition = (10, 0, 10)

**When:** StructuralValidator.Validate(quest)

**Then:**
- No errors with any `structural/spd-*` code

---

#### S3_SV_SPD_T3: CFC == 0 -- error

**Given:**
- QuestDefinition with SinglePlayerDutyStep where ContentFinderConditionId = 0, EntryKind = Talk, EntryTargetId = 5000

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains entry with Code == "structural/spd-cfc-zero"

---

#### S3_SV_SPD_T4: Talk entry, EntryTargetId null -- error

**Given:**
- QuestDefinition with SinglePlayerDutyStep where EntryKind = Talk, EntryTargetId = null, ContentFinderConditionId = 830

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains entry with Code == "structural/spd-entry-target-null"
- Message contains "Talk" and "null"

---

#### S3_SV_SPD_T5: Interact entry, EntryTargetId null -- error

**Given:**
- QuestDefinition with SinglePlayerDutyStep where EntryKind = Interact, EntryTargetId = null, ContentFinderConditionId = 831

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains entry with Code == "structural/spd-entry-target-null"
- Message contains "Interact"

---

#### S3_SV_SPD_T6: Talk entry, EntryTargetId == 0 -- error

**Given:**
- QuestDefinition with SinglePlayerDutyStep where EntryKind = Talk, EntryTargetId = 0, ContentFinderConditionId = 830

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains entry with Code == "structural/spd-entry-target-zero"
- Message contains "0"

---

#### S3_SV_SPD_T7: Interact entry, EntryTargetId == 0 -- error

**Given:**
- QuestDefinition with SinglePlayerDutyStep where EntryKind = Interact, EntryTargetId = 0, ContentFinderConditionId = 831

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains entry with Code == "structural/spd-entry-target-zero"

---

#### S3_SV_SPD_T8: Proximity entry with non-null EntryTargetId -- error

**Given:**
- QuestDefinition with SinglePlayerDutyStep where EntryKind = Proximity, EntryTargetId = 5000, ContentFinderConditionId = 832

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains entry with Code == "structural/spd-proximity-has-target"
- Message contains "proximity" and "entryTargetId"

---

#### S3_SV_SPD_T9: Multiple errors fire simultaneously -- CFC zero + entry target null

**Given:**
- QuestDefinition with SinglePlayerDutyStep where ContentFinderConditionId = 0, EntryKind = Talk, EntryTargetId = null

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contain both "structural/spd-cfc-zero" and "structural/spd-entry-target-null"

---

#### S3_SV_SPD_T10: Proximity entry with null target and valid CFC -- no errors

**Given:**
- QuestDefinition with SinglePlayerDutyStep where EntryKind = Proximity, EntryTargetId = null, ContentFinderConditionId = 833

**When:** StructuralValidator.Validate(quest)

**Then:**
- No errors with any `structural/spd-*` code

---

### Old DutyStep Validation Coexistence Test (addition to existing file)

---

#### S3_SV_LEGACY_T1: Old DutyStep with kind "duty" and null CFC still fires structural/duty-missing-required-field

**Given:**
- QuestDefinition with DutyStep where Kind = "duty", ContentFinderConditionId = null

**When:** StructuralValidator.Validate(quest)

**Then:**
- Errors contains entry with Code == "structural/duty-missing-required-field"
- (Confirms the old rule is not broken by the new code)

---

## 7. Implementation Order

### Phase A: Tools-repo schema mirror (est. 30 min)

1. Add `SpdEntryKind` enum to `questforge-tools/QuestForge.Schema/Step.cs`
2. Add `DungeonTrialStep` class
3. Add `SinglePlayerDutyStep` class
4. Add `[JsonDerivedType]` attributes
5. Update `QuestForgeJsonContext.cs`
6. `dotnet build questforge-tools/QuestForge.Schema` succeeds

**Done before Phase B:** Schema compiles. All existing tests still pass.

### Phase B: CapabilityInferrer + tests (est. 30 min)

1. Add two new entries to `StepCapabilities`
2. Add tests S3_CI_T1 through S3_CI_T4
3. `dotnet test QuestForge.Tools.Trace.Tests --filter "CapabilityInferrerTests"` passes

**Done before Phase C.**

### Phase C: TraceToFixtureExtractor + tests (est. 30 min)

1. Add `FilenameLookup` entry for `step:single-player-duty`
2. Add `DistinguishingCapPriority` entry for `step:single-player-duty`
3. Add tests S3_FX_T1 through S3_FX_T4
4. `dotnet test QuestForge.Tools.Trace.Tests --filter "TraceToFixtureExtractorTests"` passes

**Done before Phase D.**

### Phase D: StructuralValidator + tests (est. 1 hour)

1. Add `ValidateDungeonTrialStep` and `ValidateSinglePlayerDutyStep` methods
2. Wire into `CheckStepTypeRules` switch
3. Create `DungeonTrialStepValidationTests.cs` with S3_SV_DT_T1-T3
4. Create `SinglePlayerDutyStepValidationTests.cs` with S3_SV_SPD_T1-T10
5. Add legacy coexistence test S3_SV_LEGACY_T1
6. `dotnet test QuestForge.Tools.Validator.Tests` passes

**Done before Phase E.**

### Phase E: EngineHost dispatch changes (est. 30 min)

1. Add `GetEntryKind` static helper
2. Modify `case EngineAction.EnterSinglePlayerDuty` dispatch arm
3. Update `DebounceLog` message
4. `dotnet build QuestForge.Plugin` succeeds

**Done before Phase F.**

### Phase F: FIXTURES.md documentation (est. 15 min)

1. Update step capabilities table
2. Update actionType notes
3. `docs/FIXTURES.md` committed

---

## 8. Done Criteria

1. `dotnet build` succeeds for `QuestForge.Plugin` (EngineHost compiles with new dispatch arm).
2. `dotnet build` succeeds for all `questforge-tools` projects.
3. `dotnet test QuestForge.Tools.Trace.Tests` passes -- all existing + 8 new tests green.
4. `dotnet test QuestForge.Tools.Validator.Tests` passes -- all existing + 14 new tests green.
5. `CapabilityInferrer.StepCapabilities` contains `[typeof(DungeonTrialStep)] = "step:dungeon-trial"` and `[typeof(SinglePlayerDutyStep)] = "step:single-player-duty"`.
6. `TraceToFixtureExtractor.SuggestFilename` returns `"with-spd.json"` for a fixture with `["step:single-player-duty"]` capability.
7. `TraceToFixtureExtractor.SuggestFilename` returns `"with-dungeon-trial.json"` for a fixture with `["step:dungeon-trial"]` capability.
8. `StructuralValidator` emits `structural/dungeon-trial-cfc-zero` for `DungeonTrialStep` with CFC == 0.
9. `StructuralValidator` emits `structural/spd-cfc-zero` for `SinglePlayerDutyStep` with CFC == 0.
10. `StructuralValidator` emits `structural/spd-entry-target-null` for Talk/Interact with null target.
11. `StructuralValidator` emits `structural/spd-entry-target-zero` for Talk/Interact with zero target.
12. `StructuralValidator` emits `structural/spd-proximity-has-target` for Proximity with non-null target.
13. Old `DutyStep` validation (`structural/duty-missing-required-field`) still fires correctly.
14. EngineHost `case EngineAction.EnterSinglePlayerDuty` arm dispatches entry mechanism based on `SpdEntryKind`: Talk calls `_interactor.InteractWith`, Interact calls `_objectInteractor.InteractWithObject`, Proximity does nothing.
15. EngineHost `case EngineAction.EnterDuty` arm is unchanged.
16. `docs/FIXTURES.md` capabilities table includes `step:dungeon-trial` and `step:single-player-duty`.
17. Both PRs (questforge + questforge-tools) are created and ready to merge together.

---

## 9. Exclusions

This spec explicitly does NOT include:

1. **Removal of old DutyStep from tools-repo** -- the old type coexists until the data migration PR lands in `questforge-data`. Removing it from the tools-repo schema is part of the migration slice.
2. **Removal of old CapabilityInferrer DutyStep branching** -- stays until DutyStep is removed.
3. **Removal of old StructuralValidator ValidateDutyStep** -- stays until DutyStep is removed.
4. **Removal of old FilenameLookup/DistinguishingCapPriority entries for step:duty/step:spd** -- stays for backward compatibility.
5. **Authoring inference for duty entry** -- Slice 5 deliverable.
6. **In-game smoke test** -- Slice 4 deliverable.
7. **Quest data migration** -- separate paired PR in questforge-data.
8. **Engine-side changes** -- Slice 2 already complete; no engine changes in Slice 3.
9. **No new unit tests for EngineHost** -- plugin code is tested via in-game smoke (Slice 4).
10. **TraceConstants changes** -- the existing constants already cover both action types.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Section 6.
- Happy paths: 6 scenarios (S3_CI_T1, S3_CI_T2, S3_FX_T1, S3_FX_T2, S3_SV_DT_T1, S3_SV_SPD_T1)
- Edge cases: 7 scenarios (S3_CI_T3, S3_CI_T4, S3_FX_T3, S3_FX_T4, S3_SV_SPD_T2, S3_SV_SPD_T9, S3_SV_SPD_T10)
- Error cases: 9 scenarios (S3_SV_DT_T2, S3_SV_DT_T3, S3_SV_SPD_T3, S3_SV_SPD_T4, S3_SV_SPD_T5, S3_SV_SPD_T6, S3_SV_SPD_T7, S3_SV_SPD_T8, S3_SV_LEGACY_T1)
- Expected total: ~22 tests in QuestForge.Tools.Trace.Tests + QuestForge.Tools.Validator.Tests
