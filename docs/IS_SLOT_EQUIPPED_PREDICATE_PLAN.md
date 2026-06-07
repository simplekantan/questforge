# `isSlotEquipped` Predicate Plan

**Status:** ready for test creation
**Scope:** Add `isSlotEquipped(slotIndex)` predicate to `PredicateEvaluator`; register in tools-repo `FunctionRegistry`; add to authoring dropdown
**Estimated effort:** ~45 minutes implementation + tests
**Branch:** `feat/is-slot-equipped-predicate` (questforge); paired branch in questforge-tools

---

## Background

Quest 65999 "Dressed to Call" requires the player to equip gear in specific slots (head, body, hands, legs, feet) before a turn-in step. Authors need a predicate that checks whether a given equipment slot is occupied, e.g. `isSlotEquipped(2)` for head. The adapter method `IGameStateProvider.GetEquippedItemLevelForSlot(int slotIndex, CancellationToken ct)` already exists in all implementations (Dalamud, Fake, Recording, Replay). It returns item level > 0 when a slot is occupied, 0 when empty (ItemId == 0). This plan wires a predicate that calls this existing method.

**Slot index reference (FFXIV `EquippedItems` container order):**

| Index | Slot |
|-------|------|
| 0 | MainHand |
| 1 | OffHand |
| 2 | Head |
| 3 | Body |
| 4 | Hands |
| 5 | Waist |
| 6 | Legs |
| 7 | Feet |
| 8 | Ears |
| 9 | Neck |
| 10 | Wrists |
| 11 | Ring (L) |
| 12 | Ring (R) |
| 13 | SoulCrystal |

---

## Dependency graph

```
1. questforge-tools (predicate language)
   +-- QuestForge.Predicates/FunctionRegistry.cs  <-- add ONE signature
       (Parser.cs, Lexer.cs, PredicateChecker.cs -- NO change)
       (QuestForge.Tools.Validator/PredicateValidator.cs -- NO change; generic)

2. questforge (engine + authoring UI)
   +-- QuestForge.Engine/Predicates/PredicateEvaluator.cs  <-- add ONE switch arm
   +-- QuestForge.Plugin/UI/Authoring/PredicateOptions.cs  <-- add template entry
       (IGameStateProvider -- NO change; GetEquippedItemLevelForSlot already exists)
       (FakeGameStateProvider -- NO change; SetEquippedItemLevelForSlot already exists)
```

**Build order:** Either repo first -- the engine matches on bare name strings, not the registry. Tools-first is preferred so the validator accepts quest data using the new predicate before the runtime path exists.

---

## Architectural decisions

### SE1 -- Function name is `isSlotEquipped`, not `slotHasItem` or `isEquipSlotOccupied`

The `is*` prefix convention is used for boolean predicates throughout the registry (`isQuestComplete`, `isQuestAccepted`, `isAttuned`, `isPlayerJob`, `isAetherCurrentAttuned`). `isSlotEquipped` reads naturally as "is slot N equipped?" and follows this convention. `playerHasEquipped` was the precedent for item-id-based checks, but `isSlotEquipped` is slot-index-based (a different axis), so a different name avoids confusion.

**Rejected:** `slotHasItem` -- inconsistent with the `is*` boolean convention. `isEquipSlotOccupied` -- too verbose; every other predicate uses short names.

### SE2 -- Arity is `Fixed(1)` with parameter type `[Int]`, return type `Bool`

The predicate takes exactly one argument: the slot index (0--13). It returns a boolean: true if the slot has an item, false if empty.

```csharp
// FunctionRegistry.cs -- new entry
new("isSlotEquipped", new Fixed(1), [Int], Bool),
```

**Registry count goes 41 -> 42.**

### SE3 -- No slot-index range validation in the checker

Unlike `questVariable` (where the index range 0--5 is a hard game constraint), equipment slot indices are stable at 0--13 but could theoretically expand. More importantly, the adapter method `GetEquippedItemLevelForSlot` already handles out-of-range indices gracefully (Dalamud impl returns 0; Fake impl returns 0 for unknown keys). Adding a checker range gate would require maintaining a magic constant and would not prevent runtime harm. The checker validates arity and type only.

**Rejected:** Adding a `slot-index-out-of-range` checker error for indices outside 0--13. The cost/benefit is poor: the Dalamud adapter already returns 0 for invalid slots (meaning the predicate evaluates to `false`, which is the correct answer for "is a nonexistent slot equipped?"), and the 14-slot layout has been stable across FFXIV expansions. If a range check is wanted later, it can be added without breaking changes.

### SE4 -- Engine evaluation: `GetEquippedItemLevelForSlot > 0` means "equipped"

The existing adapter returns item level for the slot, with 0 meaning empty (the Dalamud impl checks `ItemId == 0` and returns 0 in that case). The predicate evaluates to `true` when the returned value is > 0.

```csharp
// PredicateEvaluator.cs -- new arm in EvaluateFunction switch
"isSlotEquipped" =>
    (await _gameState.GetEquippedItemLevelForSlot((int)(long)args[0], ct)).ValueOrThrow > 0,
```

This is a single line. The cast chain `(int)(long)args[0]` matches every other Int-argument predicate in the evaluator (e.g. `playerHasItem`, `isAttuned`).

### SE5 -- No recording/replay changes needed

`GetEquippedItemLevelForSlot` is already recorded by `RecordingGameStateProvider` and replayed by `ReplayGameStateProvider`. The predicate rides the existing observation channel. No new `(method, arg)` key pattern is introduced beyond what already exists.

### SE6 -- Authoring UI: add `isSlotEquipped(0)` template to PredicateOptions

Add a single template `isSlotEquipped(0)` to the dropdown list. Authors edit the `0` to the desired slot index. This follows the same pattern as `playerHasEquipped(0)` and `playerHasItem(0)`.

```csharp
// PredicateOptions.cs -- add after the playerHasEquipped entry
$"isSlotEquipped({0})",
```

**Rejected:** Adding 14 entries (one per slot) -- too noisy; the template with `0` is sufficient and matches the existing pattern.

### SE7 -- No validator rules needed

This is a boolean predicate with a single Int argument. The generic validator already validates arity and type via the registry. No step-type-specific E#/W# rules are needed (this is a predicate, not a step type).

---

## Per-file change list

### questforge-tools

| File | Change |
|------|--------|
| `QuestForge.Predicates/FunctionRegistry.cs` | Add one entry: `new("isSlotEquipped", new Fixed(1), [Int], Bool)`. Count 41 -> 42. |
| `QuestForge.Predicates/Parser.cs`, `Lexer.cs`, `PredicateChecker.cs` | **No change** (parser accepts any identifier; checker validates via registry). |
| `QuestForge.Tools.Validator/PredicateValidator.cs` | **No change** (generic). |

### questforge

| File | Change |
|------|--------|
| `QuestForge.Engine/Predicates/PredicateEvaluator.cs` | Add `"isSlotEquipped"` arm in `EvaluateFunction` switch (SE4). |
| `QuestForge.Plugin/UI/Authoring/PredicateOptions.cs` | Add `isSlotEquipped(0)` template (SE6). |
| `IGameStateProvider.cs` | **No change** (`GetEquippedItemLevelForSlot` exists). |
| `FakeGameStateProvider.cs` | **No change** (`SetEquippedItemLevelForSlot` exists). |
| `RecordingGameStateProvider.cs`, `ReplayGameStateProvider.cs` | **No change** (SE5). |

---

## Given-When-Then specifications

### Registry (questforge-tools -- `FunctionRegistryTests`)

**S1 -- Signature is correct.**
Given: `FunctionRegistry.TryGet("isSlotEquipped", out var sig)`.
When: inspecting the returned signature.
Then: `found == true`, `sig.Name == "isSlotEquipped"`, `sig.Arity` is `Fixed` with `Count == 1`, `sig.ParameterTypes == [Int]`, `sig.ReturnType == Bool`.

**S2 -- Registry count is 42.**
Given: `FunctionRegistry.All`.
When: reading `Count`.
Then: `42`.

**S3 -- Typo suggestion works.**
Given: `FunctionRegistry.SuggestSimilar("isSlotEquiped")` (single `p`).
When: inspecting the result.
Then: contains `"isSlotEquipped"`.

### Parser (questforge-tools -- `ParserTests`)

**S4 -- Simple call parses.**
Given: `"isSlotEquipped(3)"`.
When: parsed.
Then: `IsSuccess == true`, `Ast` is `FunctionCall("isSlotEquipped", [IntLiteral(3)])`.

**S5 -- Negation parses.**
Given: `"not isSlotEquipped(1)"`.
When: parsed.
Then: `IsSuccess == true`, `Ast` is `Not(FunctionCall("isSlotEquipped", [IntLiteral(1)]))`.

### Checker (questforge-tools -- `PredicateCheckerTests`)

**S6 -- Happy path, valid call.**
Given: `"isSlotEquipped(3)"`.
When: checked.
Then: no semantic errors.

**S7 -- Wrong arity, too many args.**
Given: `"isSlotEquipped(3, 5)"`.
When: checked.
Then: exactly one `arity-mismatch`.

**S8 -- Wrong arity, zero args.**
Given: `"isSlotEquipped()"`.
When: checked.
Then: exactly one `arity-mismatch`.

**S9 -- Wrong type, string arg.**
Given: `"isSlotEquipped(\"head\")"`.
When: checked.
Then: exactly one `type-mismatch`.

**S10 -- Composable with and.**
Given: `"isSlotEquipped(2) and isSlotEquipped(3)"`.
When: checked.
Then: no semantic errors.

### Validator end-to-end (questforge-tools -- `PredicateValidatorTests`)

**S11 -- Happy path through validator.**
Given: a valid quest with a step whose `SkipIf` is `isSlotEquipped(2)`.
When: `Validate` is called.
Then: no errors.

### Engine evaluation (questforge -- `QuestForge.Engine.Tests`)

**S12 -- Slot equipped returns true.**
Given: `FakeGameStateProvider.SetEquippedItemLevelForSlot(3, 55)` (body slot, ilvl 55).
When: evaluating `isSlotEquipped(3)`.
Then: `true`.

**S13 -- Slot empty returns false.**
Given: no `SetEquippedItemLevelForSlot` calls (all slots default to 0).
When: evaluating `isSlotEquipped(3)`.
Then: `false`.

**S14 -- Different slots are independent.**
Given: `SetEquippedItemLevelForSlot(2, 30)` (head equipped), no other slots set.
When: evaluating `isSlotEquipped(2)`.
Then: `true`.
When: evaluating `isSlotEquipped(3)`.
Then: `false`.

**S15 -- Negation works.**
Given: `SetEquippedItemLevelForSlot(3, 55)`.
When: evaluating `not isSlotEquipped(3)`.
Then: `false`.

**S16 -- Conjunction of multiple slots (quest 65999 use case).**
Given: `SetEquippedItemLevelForSlot(2, 30)`, `SetEquippedItemLevelForSlot(3, 30)`, `SetEquippedItemLevelForSlot(4, 30)`, `SetEquippedItemLevelForSlot(6, 30)`, `SetEquippedItemLevelForSlot(7, 30)`.
When: evaluating `isSlotEquipped(2) and isSlotEquipped(3) and isSlotEquipped(4) and isSlotEquipped(6) and isSlotEquipped(7)`.
Then: `true`.

**S17 -- Conjunction fails when one slot is empty.**
Given: same as S16 but `SetEquippedItemLevelForSlot(6, 0)` (legs empty).
When: evaluating `isSlotEquipped(2) and isSlotEquipped(3) and isSlotEquipped(4) and isSlotEquipped(6) and isSlotEquipped(7)`.
Then: `false`.

**S18 -- Out-of-range slot index returns false (not throw).**
Given: no slots set.
When: evaluating `isSlotEquipped(99)`.
Then: `false` (adapter returns 0 for unknown slot -> predicate is false).

**S19 -- Slot 0 (MainHand) works.**
Given: `SetEquippedItemLevelForSlot(0, 10)`.
When: evaluating `isSlotEquipped(0)`.
Then: `true`.

**S20 -- Slot 13 (SoulCrystal) boundary.**
Given: `SetEquippedItemLevelForSlot(13, 1)`.
When: evaluating `isSlotEquipped(13)`.
Then: `true`.

**S21 -- Composition with playerHasEquipped.**
Given: `SetEquippedItemLevelForSlot(3, 55)` and `SetItemEquipped(new ItemId(4567), true)`.
When: evaluating `isSlotEquipped(3) and playerHasEquipped(4567)`.
Then: `true`.

**S22 -- Used in Expect postcondition.**
Given: a quest step with `Expect: "isSlotEquipped(3)"` and slot 3 has ilvl 55.
When: engine evaluates the step's postcondition.
Then: postcondition passes (step completes).

**S23 -- Used in SkipIf guard.**
Given: a quest step with `SkipIf: "isSlotEquipped(3)"` and slot 3 has ilvl 55.
When: engine evaluates the skip condition.
Then: step is skipped.

---

## PR slicing

### PR-A -- `questforge-tools` (registry only)
- Branch: `feat/is-slot-equipped-predicate` in `questforge-tools`.
- Adds one registry entry (SE2). Parser, checker, validator unchanged.
- Tester deliverables: S1--S3 (registry), S4--S5 (parser), S6--S10 (checker), S11 (validator).
- CI-gated test projects: `QuestForge.Predicates.Tests`, `QuestForge.Tools.Validator.Tests`.

### PR-B -- `questforge` (engine evaluator + authoring UI)
- Branch: `feat/is-slot-equipped-predicate` in `questforge`.
- Adds the `EvaluateFunction` switch arm (SE4) and the `PredicateOptions` template (SE6).
- Tester deliverables: S12--S23.
- CI-gated test project: `QuestForge.Engine.Tests`.

### Dependency / order
PR-A should merge first (the registry is the contract). PR-B compiles independently (the engine arm matches on the bare name string). Both can be reviewed in parallel.

---

## Build / test commands

```bash
# questforge-tools (PR-A)
dotnet test C:/Users/publi/RiderProjects/questforge-tools/QuestForge.Predicates.Tests
dotnet test C:/Users/publi/RiderProjects/questforge-tools/QuestForge.Tools.Validator.Tests

# questforge (PR-B)
dotnet test C:/Users/publi/RiderProjects/questforge/QuestForge.Engine.Tests
```

---

## Done criteria

1. `FunctionRegistry` exposes `isSlotEquipped` as `Fixed(1) [Int] -> Bool`; `FunctionRegistry.All.Count == 42` (S1, S2 green).
2. `isSlotEquipped(3)` parses to `FunctionCall("isSlotEquipped", [IntLiteral(3)])` with no parser edits (S4 green).
3. The checker accepts `isSlotEquipped(N)` and rejects wrong arity/type (S6--S10 green).
4. The generic validator accepts a quest using `isSlotEquipped` with no `PredicateValidator` change (S11 green).
5. The engine evaluates `isSlotEquipped(N)` as `GetEquippedItemLevelForSlot(N) > 0` (S12--S21 green).
6. Conjunction of multiple slot checks works for the quest 65999 use case (S16, S17 green).
7. Out-of-range slot index returns false without throwing (S18 green).
8. `PredicateOptions.cs` includes `isSlotEquipped(0)` in the authoring dropdown.
9. No edits to `IGameStateProvider`, `FakeGameStateProvider`, recording/replay proxies, schema types, or `PredicateValidator`.

---

## Exclusions

- **Slot-name string overload** (e.g. `isSlotEquipped("head")`) -- deferred. Authors use the integer index; a human-readable mapping could be added later.
- **Checker range validation** for slot indices -- not added (SE3). The adapter handles out-of-range gracefully.
- **New adapter interface methods** -- `GetEquippedItemLevelForSlot` already exists everywhere.
- **Recording/replay changes** -- the existing observation channel is reused (SE5).
- **Authoring inference** -- predicates are not inferred; they are authored. No authoring-mode changes.
- **SCHEMA.md documentation update** -- optional follow-up; not gating.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 10 scenarios (S1, S4, S6, S10, S11, S12, S16, S19, S22, S23)
- Edge cases: 7 scenarios (S2, S3, S5, S14, S17, S18, S20)
- Error cases: 4 scenarios (S7, S8, S9, S21 partial)
- Expected total: ~23 tests --
  - questforge-tools `QuestForge.Predicates.Tests`: ~3 in `FunctionRegistryTests` (S1--S3), ~2 in `ParserTests` (S4--S5), ~5 in `PredicateCheckerTests` (S6--S10)
  - questforge-tools `QuestForge.Tools.Validator.Tests`: ~1 in `PredicateValidatorTests` (S11)
  - questforge `QuestForge.Engine.Tests`: ~12 in `IsSlotEquippedPredicateTests` (S12--S23)
