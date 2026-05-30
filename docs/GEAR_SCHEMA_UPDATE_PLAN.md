# Gear Step Schema Update Plan (Issue #122)

**Status:** ready for test creation
**Input docs:** `docs/GEAR_RESEARCH.md` Section 4, `docs/GEAR_INTERFACE_SPLIT_PLAN.md`
**Prerequisite:** #121 (interface split) merged
**Output:** Three schema step types updated from placeholder shapes to final shapes; two dead types deleted; DraftValidator rules added; tools-repo mirror kept in sync
**Scope:** Schema refactor only. No engine dispatch, no Dalamud impl, no inference.

---

## 1. Dependency graph

```
questforge repo (this PR):
  QuestForge.Schema/Step.cs                ← update 3 step classes
  QuestForge.Schema/SharedValueTypes.cs    ← delete GearItem, GearConstraints
  QuestForge.Schema/QuestForgeJsonContext.cs ← no change (registrations stay)
  QuestForge.Schema.Tests/RoundTripTests.cs ← replace 3 placeholder tests
  QuestForge.Engine/Authoring/DraftValidator.cs ← add E16, E17, E18 rules
  QuestForge.Engine.Tests/Authoring/DraftValidatorGearTests.cs ← new test file

questforge-tools repo (paired PR):
  QuestForge.Schema/Step.cs                ← mirror same changes
  QuestForge.Schema/SharedValueTypes.cs    ← mirror deletions
```

**Build order:** Schema changes first (both repos), then DraftValidator rules, then tests. Both PRs must land together.

---

## 2. Architectural decisions

### GS1: EquipGearForQuestStep uses `uint[] ItemIds`, not `GearItem[]`

**Current:** `public GearItem[] Items { get; init; } = [];` where `GearItem(string Slot, uint ItemId)`.

**New:**
```csharp
public sealed class EquipGearForQuestStep : Step
{
    /// <summary>
    /// Item IDs to equip. The adapter determines the target slot from each item's
    /// EquipSlotCategory (Lumina lookup). For rings, the adapter picks the first
    /// available ring slot.
    /// </summary>
    public uint[] ItemIds { get; init; } = [];
}
```

**Rationale:** Given an ItemId, the target equipment slot is deterministic from the item's `EquipSlotCategory` in the Lumina Item sheet. Making the author specify the slot creates an opportunity for mismatches and requires the validator to cross-reference Lumina data. Rings can go in either slot, but the adapter picks the first available (matching Questionable's pattern).

**Alternatives rejected:**
- Keep `GearItem(Slot, ItemId)` for explicit slot control. Rejected: no real quest requires "equip this ring specifically in the LEFT slot." The game does not care which ring slot. Adding slot adds authoring complexity and validator complexity for zero benefit.
- Use a wrapper type `GearItemId(uint Value)` instead of raw `uint`. Rejected: existing schema precedent (`UseItemStep.ItemId`, `UseEmoteStep.EmoteId`, `UseActionStep.ActionId`) is raw `uint`. Consistency wins.

**What breaks if violated:** If slot is kept in the schema, every authored quest must specify it, the validator must cross-reference Lumina to check it, and ring-slot mismatches become a class of bug.

**Testability:** Round-trip test verifies `uint[]` serializes as a JSON array of numbers.

### GS2: EquipBestGearStep is empty (no properties)

**Current:** `public GearConstraints? Constraints { get; init; }` where `GearConstraints(int? MinItemLevel)`.

**New:**
```csharp
public sealed class EquipBestGearStep : Step { }
```

**Rationale:** `MinItemLevel` is speculative. No real quest says "equip best gear but only above ilvl X." The step means "equip the best gear for your current job." Strategy (Stylist vs vanilla `RecommendEquipModule`) is user configuration, not quest data. If gear is insufficient, the duty's ilvl gate is the enforcement point, not the quest schema.

**Alternatives rejected:**
- Keep `GearConstraints` with `MinItemLevel`. Rejected: no consumer exists, no real quest uses it, it adds a type that must be maintained.
- Add a `JobId` field for "equip best gear for job X." Rejected: that is two steps (`change-job` then `equip-best-gear`). Composition beats overloading.

**What breaks if violated:** A `GearConstraints` type must be maintained, serialized, validated, and tested for no consumer.

### GS3: ChangeJobStep uses `uint JobId`, not `string Job`

**Current:** `public string Job { get; init; } = default!;`

**New:**
```csharp
public sealed class ChangeJobStep : Step
{
    /// <summary>
    /// ClassJob row ID from the game's ClassJob sheet.
    /// Example: 32 = Dark Knight, 19 = Paladin, 24 = White Mage.
    /// </summary>
    public uint JobId { get; init; }
}
```

**Rationale:** String job names are locale-sensitive and ambiguous ("DRK" vs "Dark Knight" vs "Chevalier noir"). Per `feedback_locale_stable_quest_identifiers.md`, QuestForge prefers numeric IDs. Existing precedent: `UseEmoteStep.EmoteId`, `UseItemStep.ItemId`, `UseActionStep.ActionId` are all `uint`.

**Alternatives rejected:**
- Keep `string Job` with a validator rule that checks known abbreviations. Rejected: abbreviations are locale-dependent; an English abbreviation list breaks for Japanese users.
- Use the adapter-layer `JobId` wrapper type. Rejected: schema types use raw `uint` for all game-data IDs (established pattern). The engine converts `uint` to the adapter wrapper at dispatch time.

**What breaks if violated:** Quest files authored in one locale cannot be used in another. Validator needs a locale-specific job name table.

### GS4: Delete `GearItem` and `GearConstraints` from SharedValueTypes.cs

**Types to delete:**
```csharp
// SharedValueTypes.cs line 150-154
public record GearItem(string Slot, uint ItemId);
public record GearConstraints(int? MinItemLevel = null);
```

**Verification:** Grep for `GearItem` and `GearConstraints` across both repos confirms the only consumers are `EquipGearForQuestStep.Items` and `EquipBestGearStep.Constraints` (both being replaced), `docs/ADAPTERS.md` (documentation reference, not code), and `docs/GEAR_RESEARCH.md` (documentation). No engine, plugin, or adapter code references these types.

**Note:** The adapter-layer `GearItem` type mentioned in `docs/ADAPTERS.md` Section 11.6 (`public record GearItem(ItemId Item, int ItemLevel, ...)`) was already deleted in #121. That was a different type in a different namespace. The schema-side `GearItem` being deleted here is the one in `QuestForge.Schema`.

### GS5: All three step classes become `sealed`

**Current:** `public class EquipGearForQuestStep : Step`, `public class EquipBestGearStep : Step`, `public class ChangeJobStep : Step` (unsealed).

**New:** All three become `public sealed class`. This matches the convention established by `SayChatMessageStep`, `UseEmoteStep`, `UseItemStep`, and `UseActionStep`. Step subtypes are leaf types -- nothing inherits from them.

**What breaks if violated:** Nothing today, but leaving them unsealed implies they are extension points, which they are not.

### GS6: DraftValidator error codes E16-E18

Next available error codes after E15 are E16, E17, E18. No new warning codes needed (these step types are not spin-loop-prone).

| Code | Step Type | Condition | Message |
|------|-----------|-----------|---------|
| E16 | `EquipGearForQuestStep` | `ItemIds.Length == 0` | `Step '{stepId}' is an EquipGearForQuestStep with empty ItemIds array.` |
| E17 | `EquipGearForQuestStep` | Any element in `ItemIds == 0` | `Step '{stepId}' is an EquipGearForQuestStep with ItemIds containing a zero value at index {idx}.` |
| E18 | `ChangeJobStep` | `JobId == 0` | `Step '{stepId}' is a ChangeJobStep with JobId == 0.` |

**EquipBestGearStep:** No step-specific rules. The step has no properties beyond the base `Step` fields. Base rules (E1-E5, W1-W6) still apply.

**W1 suppression:** `EquipGearForQuestStep`, `EquipBestGearStep`, and `ChangeJobStep` do NOT suppress W1. Unlike `UseActionStep`/`UseEmoteStep`/`SayChatMessageStep`/`UseItemStep`, these steps are not spin-loop-prone. The engine will execute them once and move on. W1 fires normally if `Expect` is null, which is appropriate since these steps benefit from postcondition verification (e.g., "is the item actually equipped?").

---

## 3. Exact new schema shapes

### 3.1 EquipGearForQuestStep (Step.cs)

```csharp
public sealed class EquipGearForQuestStep : Step
{
    /// <summary>
    /// Item IDs to equip. The adapter determines the target slot from each item's
    /// EquipSlotCategory (Lumina lookup). For rings, the adapter picks the first
    /// available ring slot.
    /// </summary>
    public uint[] ItemIds { get; init; } = [];
}
```

**JSON discriminator:** `"equip-gear-for-quest"` (unchanged).

**JSON shape example:**
```json
{
  "type": "equip-gear-for-quest",
  "id": "equip-quest-armor",
  "itemIds": [12345, 12346],
  "expect": "playerHasEquipped(12345)"
}
```

### 3.2 EquipBestGearStep (Step.cs)

```csharp
public sealed class EquipBestGearStep : Step { }
```

**JSON discriminator:** `"equip-best-gear"` (unchanged).

**JSON shape example:**
```json
{
  "type": "equip-best-gear",
  "id": "gear-up"
}
```

### 3.3 ChangeJobStep (Step.cs)

```csharp
public sealed class ChangeJobStep : Step
{
    /// <summary>
    /// ClassJob row ID from the game's ClassJob sheet.
    /// Example: 32 = Dark Knight, 19 = Paladin, 24 = White Mage.
    /// </summary>
    public uint JobId { get; init; }
}
```

**JSON discriminator:** `"change-job"` (unchanged).

**JSON shape example:**
```json
{
  "type": "change-job",
  "id": "switch-to-paladin",
  "jobId": 19,
  "expect": "currentJob() == 19"
}
```

---

## 4. Types deleted

### 4.1 GearItem (SharedValueTypes.cs line 150-151)

```csharp
// DELETE
public record GearItem(string Slot, uint ItemId);
```

**Grep verification:** The builder must run `grep -rn "GearItem" --include="*.cs"` across both repos and confirm no remaining code consumers (only doc references in .md files).

### 4.2 GearConstraints (SharedValueTypes.cs line 154)

```csharp
// DELETE
public record GearConstraints(int? MinItemLevel = null);
```

**Grep verification:** Same as above.

---

## 5. DraftValidator rules

### 5.1 E16 -- EquipGearForQuestStep with empty ItemIds

```csharp
// E16: EquipGearForQuestStep with empty ItemIds
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is EquipGearForQuestStep eg && eg.ItemIds.Length == 0)
    {
        errors.Add(new DraftValidationError("E16",
            $"Step '{steps[i].StepId}' is an EquipGearForQuestStep with empty ItemIds array.",
            [i]));
    }
}
```

### 5.2 E17 -- EquipGearForQuestStep with zero value in ItemIds

```csharp
// E17: EquipGearForQuestStep with zero value in ItemIds
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is EquipGearForQuestStep eg)
    {
        for (var j = 0; j < eg.ItemIds.Length; j++)
        {
            if (eg.ItemIds[j] == 0)
            {
                errors.Add(new DraftValidationError("E17",
                    $"Step '{steps[i].StepId}' is an EquipGearForQuestStep with ItemIds containing a zero value at index {j}.",
                    [i]));
            }
        }
    }
}
```

### 5.3 E18 -- ChangeJobStep with JobId == 0

```csharp
// E18: ChangeJobStep with JobId == 0
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is ChangeJobStep cj && cj.JobId == 0)
    {
        errors.Add(new DraftValidationError("E18",
            $"Step '{steps[i].StepId}' is a ChangeJobStep with JobId == 0.",
            [i]));
    }
}
```

### 5.4 No W1 suppression change

The W1 guard (`step.Raw is not UseActionStep and not UseEmoteStep and not SayChatMessageStep and not UseItemStep`) is NOT extended. These gear steps are not spin-loop-prone, so W1 fires normally.

---

## 6. Round-trip test scenarios

### 6.1 EquipGearForQuestStep (replaces existing test at line 501-512)

**GS-RT1: EquipGearForQuestStep round-trips with ItemIds**
- Given: `EquipGearForQuestStep { Id = "equip-quest-armor", ItemIds = [12345u, 12346u], Expect = "playerHasEquipped(12345)" }`
- When: serialized as `Step` then deserialized
- Then: result is `EquipGearForQuestStep` with `ItemIds.Length == 2`, `ItemIds[0] == 12345u`, `ItemIds[1] == 12346u`

**GS-RT2: EquipGearForQuestStep discriminator in JSON**
- Given: same step as GS-RT1
- When: serialized
- Then: compact JSON contains `"type":"equip-gear-for-quest"` and `"itemIds":[12345,12346]`

**GS-RT3: EquipGearForQuestStep with empty ItemIds defaults**
- Given: JSON `{ "type": "equip-gear-for-quest", "id": "x" }` (no itemIds field)
- When: deserialized
- Then: `ItemIds` is `[]` (empty array, no exception)

### 6.2 EquipBestGearStep (replaces existing test at line 515-527)

**GS-RT4: EquipBestGearStep round-trips (empty body)**
- Given: `EquipBestGearStep { Id = "gear-up" }`
- When: serialized as `Step` then deserialized
- Then: result is `EquipBestGearStep` with `Id == "gear-up"`

**GS-RT5: EquipBestGearStep discriminator in JSON**
- Given: same step as GS-RT4
- When: serialized
- Then: compact JSON contains `"type":"equip-best-gear"`
- Then: compact JSON does NOT contain `"constraints"` (property removed)

### 6.3 ChangeJobStep (replaces existing test at line 529-540)

**GS-RT6: ChangeJobStep round-trips with uint JobId**
- Given: `ChangeJobStep { Id = "switch-to-paladin", JobId = 19u, Expect = "currentJob() == 19" }`
- When: serialized as `Step` then deserialized
- Then: result is `ChangeJobStep` with `JobId == 19u`

**GS-RT7: ChangeJobStep discriminator and camelCase in JSON**
- Given: same step as GS-RT6
- When: serialized
- Then: compact JSON contains `"type":"change-job"` and `"jobId":19`
- Then: compact JSON does NOT contain `"job"` as a string-valued property

**GS-RT8: ChangeJobStep with missing JobId defaults to 0**
- Given: JSON `{ "type": "change-job", "id": "x" }` (no jobId field)
- When: deserialized
- Then: `JobId == 0u` (default, no exception -- validator catches this)

---

## 7. Given-When-Then specs for DraftValidator

All tests go in `QuestForge.Engine.Tests/Authoring/DraftValidatorGearTests.cs`.

### GS-V1: E16 -- EquipGearForQuestStep with empty ItemIds

- Given: A `QuestDraft` with one step: `EquipGearForQuestStep { ItemIds = [] }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` contains exactly one entry with `Code == "E16"` and `StepIndices == [1]`
- And: the message contains "empty ItemIds"

### GS-V2: E16 suppressed when ItemIds has elements

- Given: A `QuestDraft` with one step: `EquipGearForQuestStep { ItemIds = [12345u] }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` does NOT contain any entry with `Code == "E16"`

### GS-V3: E17 -- zero value in ItemIds

- Given: A `QuestDraft` with one step: `EquipGearForQuestStep { ItemIds = [12345u, 0u, 67890u] }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` contains exactly one entry with `Code == "E17"` and `StepIndices == [1]`
- And: the message contains "index 1" (the position of the zero)

### GS-V4: E17 fires for each zero

- Given: A `QuestDraft` with one step: `EquipGearForQuestStep { ItemIds = [0u, 12345u, 0u] }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` contains exactly two entries with `Code == "E17"`
- And: one message contains "index 0", the other contains "index 2"

### GS-V5: E16 and E17 do not fire simultaneously for empty array

- Given: A `QuestDraft` with one step: `EquipGearForQuestStep { ItemIds = [] }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` contains E16 but NOT E17 (empty array has no elements to check)

### GS-V6: E18 -- ChangeJobStep with JobId == 0

- Given: A `QuestDraft` with one step: `ChangeJobStep { JobId = 0 }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` contains exactly one entry with `Code == "E18"` and `StepIndices == [1]`
- And: the message contains "JobId == 0"

### GS-V7: E18 suppressed when JobId is nonzero

- Given: A `QuestDraft` with one step: `ChangeJobStep { JobId = 19 }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` does NOT contain any entry with `Code == "E18"`

### GS-V8: EquipBestGearStep has no step-specific errors

- Given: A `QuestDraft` with one step: `EquipBestGearStep { }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `errors` does NOT contain E16, E17, or E18
- And: `warnings` may contain W1 (missing Expect) -- this is expected and correct

### GS-V9: W1 fires for EquipGearForQuestStep without Expect

- Given: A `QuestDraft` with one step: `EquipGearForQuestStep { ItemIds = [12345u], Expect = null }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `warnings` contains exactly one entry with `Code == "W1"` for that step

### GS-V10: W1 fires for ChangeJobStep without Expect

- Given: A `QuestDraft` with one step: `ChangeJobStep { JobId = 19, Expect = null }` (and a valid AcceptStep)
- When: `DraftValidator.Validate(draft)` is called
- Then: `warnings` contains exactly one entry with `Code == "W1"` for that step

---

## 8. Tools-repo changes

### 8.1 QuestForge.Schema/Step.cs

Mirror the exact same changes as the main repo:
- `EquipGearForQuestStep`: replace `GearItem[] Items` with `uint[] ItemIds`, add `sealed`
- `EquipBestGearStep`: remove `GearConstraints? Constraints`, add `sealed`
- `ChangeJobStep`: replace `string Job` with `uint JobId`, add `sealed`

### 8.2 QuestForge.Schema/SharedValueTypes.cs

Delete `GearItem` (line 113) and `GearConstraints` (line 117).

### 8.3 QuestForge.Schema/QuestForgeJsonContext.cs

No change needed. `[JsonSerializable(typeof(EquipGearForQuestStep))]`, `[JsonSerializable(typeof(EquipBestGearStep))]`, and `[JsonSerializable(typeof(ChangeJobStep))]` remain. The `GearItem` and `GearConstraints` types are not independently registered (they were serialized as part of the parent step types via source-gen).

### 8.4 CapabilityInferrer.cs

No change needed. The `StepCapabilities` dictionary entries reference the step types by `typeof()`, and the type names are unchanged. The entries at lines 27-29 remain valid.

### 8.5 Structural validator

No changes needed. The structural validator in `questforge-tools` has no gear-step-specific rules. The new E16/E17/E18 rules live in `DraftValidator` (engine-side authoring validation), not in the tools-repo structural validator.

---

## 9. Tester task list

1. **T1:** Write `GS-RT1` through `GS-RT8` in `QuestForge.Schema.Tests/RoundTripTests.cs`, replacing the three existing placeholder tests (`EquipGearForQuestStep_RoundTrips`, `EquipBestGearStep_RoundTrips`, `ChangeJobStep_RoundTrips`). All 8 tests should fail to compile (RED) until the builder updates the schema.

2. **T2:** Write `GS-V1` through `GS-V10` in `QuestForge.Engine.Tests/Authoring/DraftValidatorGearTests.cs`. Use the same `QuestDraft` construction pattern as existing `DraftValidator*Tests.cs` files. All 10 tests should fail to compile (RED) until the builder adds E16/E17/E18.

3. **T3:** Verify that a `dotnet build` of `QuestForge.Schema` succeeds after the builder's changes -- specifically that `GearItem` and `GearConstraints` are no longer referenced anywhere in compiled code.

---

## 10. Builder task list

1. **B1:** Update `EquipGearForQuestStep` in `QuestForge.Schema/Step.cs`: `class` to `sealed class`, `GearItem[] Items` to `uint[] ItemIds`.

2. **B2:** Update `EquipBestGearStep` in `QuestForge.Schema/Step.cs`: `class` to `sealed class`, remove `GearConstraints? Constraints` property.

3. **B3:** Update `ChangeJobStep` in `QuestForge.Schema/Step.cs`: `class` to `sealed class`, `string Job` to `uint JobId`.

4. **B4:** Delete `GearItem` record and `GearConstraints` record from `QuestForge.Schema/SharedValueTypes.cs` (lines 150-154, including surrounding comments).

5. **B5:** Add E16, E17, E18 rules to `QuestForge.Engine/Authoring/DraftValidator.cs` (after E15, before W1). Use the exact code patterns from Section 5.

6. **B6:** Mirror B1-B4 in `questforge-tools/QuestForge.Schema/Step.cs` and `questforge-tools/QuestForge.Schema/SharedValueTypes.cs`.

7. **B7:** Run `dotnet build` on both repos to confirm no remaining references to deleted types.

8. **B8:** Run `dotnet test` on both repos to confirm all tests pass.

---

## 11. Scope guard -- what this plan does NOT include

- **No engine dispatch.** `QuestEngine` does not yet have a switch arm for `EquipGearForQuestStep`, `EquipBestGearStep`, or `ChangeJobStep`. That is tracked in issues #117, #118, #119.
- **No Dalamud adapter implementation.** `DalamudGearEquipper`, `DalamudBestGearEquipper`, `DalamudJobChanger` are stubs. That is future work.
- **No authoring inference.** Detection signals for gear changes and job changes are documented in `docs/GEAR_RESEARCH.md` Section 5 but not implemented here.
- **No structural validator rules** in the tools-repo. The tools-repo validator validates quest-file-level structure; step-field-level validation for authored drafts lives in `DraftValidator` (engine side).
- **No EngineAction subtypes.** `EquipGear`, `EquipBest`, `ChangeJob` actions are added by their respective issues.
- **No EngineTestHarness changes.** The harness is updated when engine dispatch is added.
- **No predicate functions.** `playerHasEquipped()`, `currentJob()` etc. are tracked separately.

---

## Done criteria

1. `dotnet build` succeeds for `QuestForge.Schema`, `QuestForge.Engine`, `QuestForge.Engine.Tests`, and `QuestForge.Schema.Tests` with no warnings.
2. `dotnet test QuestForge.Schema.Tests` passes -- all 8 new round-trip tests green (GS-RT1 through GS-RT8).
3. `dotnet test QuestForge.Engine.Tests` passes -- all 10 new DraftValidator tests green (GS-V1 through GS-V10).
4. `GearItem` and `GearConstraints` types do not exist in any `.cs` file in either repo (grep returns zero matches).
5. `dotnet build` succeeds for `questforge-tools` with no warnings.
6. JSON discriminators `"equip-gear-for-quest"`, `"equip-best-gear"`, and `"change-job"` are unchanged (verified by GS-RT2, GS-RT5, GS-RT7).

---

## Ready for test creation

Tester: Write failing tests from the GWT specs in Sections 6 and 7.
- Happy paths: 6 scenarios (GS-RT1, GS-RT4, GS-RT6, GS-V2, GS-V7, GS-V8)
- Edge cases: 6 scenarios (GS-RT2, GS-RT3, GS-RT5, GS-RT7, GS-RT8, GS-V5)
- Error cases: 6 scenarios (GS-V1, GS-V3, GS-V4, GS-V6, GS-V9, GS-V10)
- Expected total: ~18 tests across QuestForge.Schema.Tests and QuestForge.Engine.Tests
