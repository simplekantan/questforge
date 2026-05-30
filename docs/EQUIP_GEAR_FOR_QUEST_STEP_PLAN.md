# EquipGearForQuestStep Implementation Plan (Issue #117)

**Status:** ready for test creation

**Slice:** Combined 2-5 (schema+validator already done in #122; this plan covers engine dispatch, Dalamud impl, EngineHost wiring, tooling catch-up, and authoring inference).

**Input docs:**
- `docs/GEAR_RESEARCH.md` -- Section 2.1 (equip via `MoveItemSlot`), Section 5.1 (equipment snapshot delta detection)
- `docs/GEAR_SCHEMA_UPDATE_PLAN.md` -- Decisions GS1-GS6 (schema shape, validator rules already landed)
- `docs/GEAR_INTERFACE_SPLIT_PLAN.md` -- Decisions GS-1 through GS-9 (interface split already landed)
- `docs/USE_ITEM_STEP_PLAN.md` -- closest analog (multi-field step, pre-flight guards, adapter shape)
- `docs/USE_EMOTE_INFERENCE_PLAN.md` -- inference pattern (momentary-state polling, no baseline)
- `QuestForge.Schema/Step.cs:183-191` -- `EquipGearForQuestStep { uint[] ItemIds }`
- `QuestForge.Adapters/Gear/IGearEquipper.cs` -- 2-method interface (`EquipItem`, `IsItemEquipped`)
- `QuestForge.Adapters.Fakes/Gear/FakeGearEquipper.cs` -- fake with recording + scripting
- `QuestForge.Adapters.Dalamud/Gear/DalamudGearEquipper.cs` -- stub returning `Failed`
- `QuestForge.Engine/QuestEngine.cs:44,92` -- `_gearEquipper` field + optional ctor param (already wired)
- `QuestForge.Engine/EngineAction.cs` -- existing action records
- `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs:46` -- `GearEquipper` property (already wired)
- `QuestForge.Plugin/EngineHost.cs:54,124,235` -- `_gearEquipper` field + construction + BeginRun passthrough (already wired)
- `QuestForge.Engine/Authoring/DraftValidator.cs:177-199` -- E16 (empty ItemIds) + E17 (zero in ItemIds) already landed

**Output (CI behavior):** Adding `{ "type": "equip-gear-for-quest", "id": "equip-quest-armor", "itemIds": [12345, 12346] }` to a quest dispatches `EngineAction.EquipGear` from `QuestEngine`. Each tick equips the first unequipped item from the array. When all items report equipped, the step's implicit postcondition is met and the cursor advances. Engine unit tests in `QuestForge.Engine.Tests/Engine/EquipGearForQuestStepTests.cs` cover all dispatch arms. The Dalamud adapter (`DalamudGearEquipper`) is fleshed out from its stub to perform the actual `InventoryManager.MoveItemSlot` call. Authoring inference detects equipment slot changes via a 14-slot snapshot delta in `UIObserver`.

---

## Dependency graph

```
QuestForge.Engine
  ├── EngineAction.EquipGear (NEW record)
  ├── QuestEngine: ResolveEquipGear async pre-arm + dispatch arm (6a5)
  └── QuestEngine: implicit postcondition (all ItemIds equipped) before Expect
       ↓
QuestForge.Engine.Tests
  ├── Engine/EquipGearForQuestStepTests.cs (EG1-EG12)
  └── Helpers/EngineTestHarness.cs (RunToCompletion arm for EquipGear)
       ↓
QuestForge.Adapters.Dalamud
  └── Gear/DalamudGearEquipper.cs (fleshed out from stub)
       ↓
QuestForge.Plugin
  └── EngineHost.cs (DispatchAction arm for EquipGear)
       ↓
QuestForge.Engine.Authoring (inference)
  ├── GameStateSnapshot: EquipmentChangedSignal? EquipmentChanged
  ├── SnapshotAggregator: OnEquipmentChanged / OnEquipmentChangedConsumed
  ├── InferredFrom: EquipmentChanged enum value
  ├── StepInferenceEngine: Rule 3.5g — EquipmentChanged
  └── StepFactory: "equip-gear-for-quest" arm
       ↓
QuestForge.Plugin.Tracing
  ├── IGameProbe: GetEquippedItemIds()
  └── UIObserver: PollEquipmentChange (14-slot snapshot delta)
       ↓
questforge-tools (paired PR)
  ├── TraceConstants: ActionEquipGear
  ├── CapabilityInferrer: already has entry (no change)
  ├── FilenameLookup: new entry
  └── DistinguishingCapPriority: new entry
```

**Build order:**
1. `EngineAction.EquipGear` record in `EngineAction.cs`.
2. `QuestEngine`: `ResolveEquipGear` pre-arm + dispatch arm (6a5).
3. `EngineTestHarness`: `RunToCompletion` arm for `EquipGear`.
4. Engine tests EG1-EG12.
5. `DalamudGearEquipper`: flesh out from stub.
6. `EngineHost`: dispatch arm.
7. Inference: snapshot signal, aggregator, inference rule, step factory, UIObserver poller, IGameProbe extension.
8. Tooling catch-up (paired PR).

---

## Architectural decisions

### EG-1: Single-item dispatch per tick, not batch

**Decision:** `EngineAction.EquipGear` carries a single `uint ItemId` (plus `Step? Origin`). When an `EquipGearForQuestStep` has multiple items in `ItemIds`, the engine equips the first unequipped item on each tick. The implicit postcondition (all items equipped) gates advancement.

```csharp
// QuestForge.Engine/EngineAction.cs (append after UseItem)
public sealed record EquipGear(
    uint ItemId,
    Step? Origin = null) : EngineAction;
```

**Rationale:**
- Matches the stateless-retry pattern used by every other step type. Each tick is idempotent: read state, find the first unequipped item, emit `EquipGear(thatItemId)`.
- The game processes `MoveItemSlot` synchronously within a single framework tick, but animation/server confirmation may take 1-2 additional ticks. Single-item-per-tick gives the game time to settle.
- No per-step state in `QuestEngine` (no "which item index am I on" counter). The engine re-scans the `ItemIds` array on every tick.

**Rejected alternative:** Batch `EquipGear(uint[] ItemIds)` that equips all items in one dispatch. Rejected because:
1. The Dalamud adapter would need an internal loop with inter-item delays (complexity in the adapter).
2. If one item fails mid-batch, retry semantics become "start from the beginning" vs "continue from the failure" -- the single-item approach avoids this entirely.
3. The EngineHost dispatch arm would need to track partial completion state.

**What breaks if violated:** If the action carries the full array, the adapter must handle partial failures and the EngineTestHarness RunToCompletion arm needs a loop. Every test becomes more complex for no behavioral benefit.

**Testability:** Tests script `FakeGearEquipper.SetItemEquipped(itemId, true)` after each dispatch to simulate the game confirming the equip. Multi-item tests tick multiple times and verify items are equipped one at a time.

### EG-2: Implicit postcondition -- all ItemIds equipped gates advancement (before Expect)

**Decision:** `ResolveEquipGear` checks `IsItemEquipped` for every item in `ItemIds` before consulting the authored `Expect`. If all items are equipped, the step is implicitly complete -- the engine confirms it and moves on (or evaluates `Expect` if present).

This is implemented by synthesizing an implicit postcondition check in the async pre-arm, not by modifying the Expect-evaluation loop. The pre-arm calls `IsItemEquipped` for each item; if all return true, it returns `Wait("all items equipped; awaiting sequence advance")` which the main cursor loop interprets as "this step is done" because the Expect (if any) will also evaluate true on the next tick.

**More precisely:** the implicit postcondition is implemented via a helper that returns `true` when all items report equipped. In the dispatch arm (item 6a5 in the step-dispatch switch), if the implicit postcondition is met, the step is confirmed directly (add to `_confirmedStepIds` and `continue` to the next step). This matches the WaitStep pattern (self-confirming in the dispatch arm).

```csharp
// In the step-dispatch switch, after UseItemStep arm:
if (step is EquipGearForQuestStep equipStep)
{
    var equipAction = await ResolveEquipGear(equipStep, ct);
    if (equipAction is null)
    {
        // All items already equipped -- self-confirm.
        _confirmedStepIds.Add(step.Id);
        continue;
    }
    return (equipAction, step.Id);
}
```

**Rationale:**
- Unlike UseEmoteStep or UseActionStep, EquipGearForQuestStep has a natural, deterministic postcondition: the items are equipped. The adapter provides `IsItemEquipped` for exactly this purpose.
- Requiring every quest author to write `"expect": "playerHasEquipped(12345) and playerHasEquipped(12346)"` when the step already carries the ItemIds is redundant and error-prone.
- The Expect field remains available for additional postconditions (e.g. quest sequence advance after equipping).

**Rejected alternative:** Rely entirely on authored `Expect`. Rejected because it imposes authoring burden for a mechanically deterministic postcondition, and because `playerHasEquipped()` is not yet a predicate function (adding it just to satisfy this pattern is circular).

**What breaks if violated:** If no implicit postcondition exists, every `EquipGearForQuestStep` without an authored `Expect` becomes a spin-loop (engine equips the item, doesn't know it succeeded, equips again next tick). The W1 warning fires for missing Expect, but the real fix is the implicit check.

### EG-3: Pre-flight guards in `ResolveEquipGear`

**Decision:** Three guards in priority order:

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. No adapter wired | `_gearEquipper is null` | `AwaitUser("EquipGearForQuestStep dispatched but no IGearEquipper wired")` |
| 2. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `Wait("player casting; deferring equip-gear")` |
| 3. Player in combat | `IsPlayerInCombat` | `Wait("player in combat; deferring equip-gear")` |

After guards pass, the method scans `ItemIds` for the first unequipped item (via `IsItemEquipped`). If all items are already equipped, it returns `null` (signaling the dispatch arm to self-confirm). Otherwise it emits `EquipGear(firstUnequippedItemId, Origin: step)`.

```csharp
private async Task<EngineAction?> ResolveEquipGear(EquipGearForQuestStep step, CancellationToken ct)
{
    if (_gearEquipper is null)
        return new EngineAction.AwaitUser(
            "EquipGearForQuestStep dispatched but no IGearEquipper wired — host must supply one");

    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring equip-gear", Origin: step);

    var inCombatResult = await _gameState.IsPlayerInCombat(ct);
    if (inCombatResult is Result<bool>.Success { Value: true })
        return new EngineAction.Wait("player in combat; deferring equip-gear", Origin: step);

    // Scan for first unequipped item.
    foreach (var itemId in step.ItemIds)
    {
        var equipped = await _gearEquipper.IsItemEquipped(itemId, ct);
        if (equipped is Result<bool>.Success { Value: false })
            return new EngineAction.EquipGear(itemId, Origin: step);
    }

    // All items equipped — implicit postcondition met.
    return null;
}
```

**Why InCombat guard (unlike UseItemStep/UseEmoteStep):** GEAR_RESEARCH.md Section 1.4 confirms `RaptureGearsetModule.EquipGearset()` fails in combat. `MoveItemSlot()` (the mechanism for single-item equip) likely has the same restriction. The game silently rejects equipment changes during combat, which would cause the stateless retry to spin-loop on combat-busy mobs. Guarding in the engine prevents this.

**Rejected alternative:** No InCombat guard (let the adapter fail and retry). Rejected because equipment-change failure during combat is guaranteed and permanent until combat ends. A Wait is cheaper than an adapter round-trip per tick.

**What breaks if violated:** If the InCombat guard is omitted, the engine spams `EquipGear` actions during combat, each failing silently. The adapter call count balloons and the debounced log floods.

### EG-4: `_lastResolvedStep` is NOT set in `ResolveEquipGear`

Mirrors Decision UA13 / UE12 / UI14. `EquipGearForQuestStep` does not carry `DialogueChoices`, so `ExtractYesNo` returns null. The `Origin: step` field on `EngineAction.EquipGear` carries the context for trace consumers.

### EG-5: Dismount exemption -- EquipGear IS exempt

**Decision:** `EquipGear` IS in the lazy-dismount exemption list. The game allows equipment changes while mounted (as long as the player is not in combat). Forcing a dismount before equipping would be unnecessary and disruptive (e.g., mid-flight gear swap after a travel step).

**Code change required:** extend the exemption check from `is not EngineAction.Navigate and not EngineAction.Teleport` to `is not EngineAction.Navigate and not EngineAction.Teleport and not EngineAction.EquipGear`.

Pinned by tests EG7 (mounted + prior Navigate: dismount does NOT fire before EquipGear) and EG8 (standalone + mounted: dismount does NOT fire).

### EG-6: Recording-proxy decision -- no `RecordingGearEquipper` needed

Per CLAUDE.md Slice 3 contract: "Write-only adapters don't need a RecordingXxxExecutor wrapper." `IGearEquipper` has one write method (`EquipItem`) and one read method (`IsItemEquipped`). The read is used only in the pre-arm to check implicit postcondition -- it is not an "observation" worth recording (the outcome is already captured by whether `EquipGear` is emitted or the step self-confirms). `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` capture the write.

### EG-7: DalamudGearEquipper implementation design

**`EquipItem(uint itemId, ct)`** implementation:

1. Search for the item across all armoury containers + player inventory in the Questionable-established order:
   ```
   ArmoryMainHand, ArmoryOffHand, ArmoryHead, ArmoryBody, ArmoryHands,
   ArmoryLegs, ArmoryFeets, ArmoryEar, ArmoryNeck, ArmoryWrist,
   ArmoryRings, ArmorySoulCrystal,
   Inventory1, Inventory2, Inventory3, Inventory4
   ```
2. If not found: return `Result.Ok(EquipOutcome.ItemNotFound)`.
3. Look up the item's `EquipSlotCategory` from the Lumina `Item` sheet to determine the target equipment slot. The mapping (from GEAR_RESEARCH.md Section 2.1):
   - Categories 1-11 map to equipment slot indices 0-10
   - Category 12 (Ring) maps to slot 11 or 12 (prefer empty; fall back to right ring)
   - Category 13 (two-hand weapon) maps to slot 0 (MainHand)
   - Category 17 (soul crystal) maps to slot 13
4. Call `InventoryManager.MoveItemSlot(srcContainer, srcSlot, InventoryType.EquippedItems, targetSlot, true)`.
5. Return `Result.Ok(EquipOutcome.Equipped)`.

**`IsItemEquipped(uint itemId, ct)`** implementation:
1. Scan `InventoryType.EquippedItems` (14 slots).
2. If any slot has `ItemId == itemId`: return `Result.Ok(true)`.
3. Otherwise: return `Result.Ok(false)`.

**Pure helper extraction:** The `EquipSlotCategory -> equipment slot index` mapping is a pure function that can be unit-tested. Extract as `EquipSlotResolver.GetTargetSlot(uint equipSlotCategory) -> int?` in `QuestForge.Adapters/Gear/`. This follows the `EmoteCommandResolver` / `ActionStatusInterpreter` precedent.

### EG-8: EngineHost dispatch arm for EquipGear

```csharp
case EngineAction.EquipGear eg:
    DebounceLog(
        $"equipgear:{eg.ItemId}",
        $"[EquipGear] itemId={eg.ItemId}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _gearEquipper.EquipItem(eg.ItemId, ct);
    break;
```

Placement: after the `UseItem` arm (EngineHost.cs line ~513), before the `Wait` arm.

### EG-9: W1 suppression for EquipGearForQuestStep

**Decision:** DO extend the W1 suppression guard to include `EquipGearForQuestStep`.

**Current** (from GEAR_SCHEMA_UPDATE_PLAN.md Decision GS6): W1 was NOT suppressed for gear steps because "these steps are not spin-loop-prone." However, Decision EG-2 introduces an implicit postcondition that makes `Expect` truly optional for this step type. Without suppression, every `EquipGearForQuestStep` with no `Expect` triggers W1, which is misleading because the engine will NOT spin-loop (it checks `IsItemEquipped` internally).

**New W1 guard:**
```csharp
if (step.Raw.Expect is null
    && step.Raw is not UseActionStep
    and not UseEmoteStep
    and not SayChatMessageStep
    and not UseItemStep
    and not EquipGearForQuestStep)  // NEW
```

This does NOT add a step-specific warning (no W11). The implicit postcondition makes the step self-completing; W1 is simply irrelevant.

**What breaks if violated:** If W1 is not suppressed, every authored `EquipGearForQuestStep` without `Expect` generates a misleading warning. Authors will learn to ignore W1, eroding its value for step types that genuinely need `Expect`.

### EG-10: Authoring inference -- equipment snapshot delta

**Detection signal:** Poll the 14 equipment slots (`InventoryType.EquippedItems`) once per framework tick. Compare item IDs against the previous snapshot. When any slot's item ID changes, emit `EquipmentChangedSignal` with the set of new item IDs.

**Signal record:**
```csharp
// GameStateSnapshot.cs
public sealed record EquipmentChangedSignal(IReadOnlyList<uint> NewItemIds);
```

`NewItemIds` contains the item IDs that appeared in equipment slots where they were not present in the previous snapshot. For a single-item equip, this is typically one item. For a gearset equip or "recommended gear," it could be many items.

**Disambiguation from `equip-best-gear`:** Per GEAR_RESEARCH.md Section 5.2, `equip-best-gear` is not auto-inferred. The `EquipmentChanged` signal infers `equip-gear-for-quest` regardless of how many items changed. If the author performed a gearset change rather than a single-item equip, the inferred step will have all changed items in `ItemIds` -- the author can edit the draft to correct it.

**Polling approach (momentary-state pattern):**

```csharp
// IGameProbe extension
IReadOnlyList<uint>? GetEquippedItemIds();  // returns 14 item IDs (0 for empty slots)
```

```
UIObserver.PollEquipmentChange():
  - First observation (baseline): store snapshot, no fire.
  - Subsequent observations: diff against stored snapshot.
    - If any slot changed: compute set of new item IDs, fire OnEquipmentChanged(newItemIds).
    - Update stored snapshot.
  - ResetWindowState: call OnEquipmentChangedConsumed(); do NOT reset baseline (equipment state persists).
```

**Inference rule placement:** Rule 3.5g -- between Rule 3.5i (ItemUsed) and Rule 3.5e (EmoteCompleted). Equipment changes are more specific than emotes but less specific than item use (a player might use an item that then triggers an equip).

**Priority rationale:** Equipment changes during quest gameplay are infrequent and intentional. If both ItemUsed and EquipmentChanged fire in the same window, the ItemUsed signal takes priority (Rule 3.5i is higher).

```csharp
// StepInferenceEngine, Rule 3.5g
if (after.EquipmentChanged is { } equipSignal)
{
    return new InferenceResult(
        StepType:        "equip-gear-for-quest",
        SuggestedStepId: $"equip-gear-{string.Join("-", equipSignal.NewItemIds.Take(3))}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.EquipmentChanged,
        Notes:           $"Equipment changed: {equipSignal.NewItemIds.Count} item(s) equipped.");
}
```

**StepFactory `"equip-gear-for-quest"` arm:**
```csharp
"equip-gear-for-quest" => new EquipGearForQuestStep
{
    Id = stepId,
    Expect = expectValue,
    ItemIds = after?.EquipmentChanged?.NewItemIds.ToArray() ?? []
},
```

### EG-11: Tooling catch-up scope

**CapabilityInferrer:** Already has `[typeof(EquipGearForQuestStep)] = "step:equip-gear-for-quest"` at line 27. No change.

**TraceConstants:** Add `ActionEquipGear = "equipgear"` (from `EngineAction.EquipGear.GetType().Name.ToLowerInvariant()`). No behavior change (IsTerminalAction only uses done/awaituser).

**FilenameLookup:** Add exact-shape entry:
```csharp
(["step:equip-gear-for-quest", "step:talk", "step:travel"], "with-equip-gear-for-quest.json"),
```

**DistinguishingCapPriority:** Add entry after `step:use-item`, before `step:say-chat-message`:
```csharp
("step:equip-gear-for-quest", "with-equip-gear-for-quest.json"),
```

Priority rationale: Equipment changes are more distinguishing than chat messages and emotes (they directly affect game state), but less distinguishing than item use (which has a broader signal surface).

### EG-12: Debug accessor

Add `IGearEquipper DebugGearEquipper => _gearEquipper;` to EngineHost alongside the existing Debug accessors (DebugEmoteExecutor, DebugChatSender, DebugItemUser). Useful for `/qf debug` subcommands.

---

## Validation rule table

All rules already landed in #122. Reproduced for reference:

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| Empty ItemIds | E16 | Error | `EquipGearForQuestStep.ItemIds.Length == 0` | -- |
| Zero in ItemIds | E17 | Error | `EquipGearForQuestStep.ItemIds[j] == 0` | -- |
| W1 (missing Expect) | W1 | Warning | `step.Expect is null` | **Extended**: `not (... or EquipGearForQuestStep)` per EG-9 |

---

## Given-When-Then test scenarios

### Engine tests (`QuestForge.Engine.Tests/Engine/EquipGearForQuestStepTests.cs`)

All tests follow the established pattern. Quest with one EquipGearForQuestStep in sequence 0. AcceptStep present to satisfy E4.

#### EG1 -- Happy path, single item, not equipped -- emits EquipGear

**Given:**
- Player not casting, not in combat.
- `EquipGearForQuestStep { ItemIds = [12345u] }`. No authored `Expect`.
- `harness.GearEquipper.SetItemEquipped(12345u, false)`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.EquipGear` with `ItemId == 12345u`, `Origin != null`.

#### EG2 -- Happy path, single item, already equipped -- step self-confirms

**Given:**
- Player not casting, not in combat.
- `EquipGearForQuestStep { ItemIds = [12345u] }`. No authored `Expect`.
- `harness.GearEquipper.SetItemEquipped(12345u, true)`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (step confirmed, no more steps, waiting for sequence advance).
- The step is confirmed (confirmed set contains the step ID).

#### EG3 -- Multi-item, first unequipped -- emits EquipGear for first unequipped item

**Given:**
- Player not casting, not in combat.
- `EquipGearForQuestStep { ItemIds = [11111u, 22222u, 33333u] }`. No authored `Expect`.
- `harness.GearEquipper.SetItemEquipped(11111u, true)` (first item already equipped).
- `harness.GearEquipper.SetItemEquipped(22222u, false)` (second item not equipped).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.EquipGear` with `ItemId == 22222u` (skipped 11111 because it's equipped; emits first unequipped).

#### EG4 -- Multi-item integration: equip all items across multiple ticks

**Given:**
- `EquipGearForQuestStep { ItemIds = [11111u, 22222u] }`. No authored `Expect`.
- Both items initially unequipped.

**When:**
1. Tick 1 --> `EngineAction.EquipGear(11111u)`.
2. `harness.GearEquipper.SetItemEquipped(11111u, true)` (simulate game confirming).
3. Tick 2 --> `EngineAction.EquipGear(22222u)`.
4. `harness.GearEquipper.SetItemEquipped(22222u, true)` (simulate game confirming).
5. Tick 3.

**Then:**
- Tick 3 returns `EngineAction.Wait` (all items equipped, step self-confirmed).

#### EG5 -- Player casting -- Wait, no EquipGear emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- `EquipGearForQuestStep { ItemIds = [12345u] }`. Item not equipped.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"player casting"`.

#### EG6 -- Player in combat -- Wait, no EquipGear emitted

**Given:**
- `harness.GameState.SetInCombat(true)`.
- `EquipGearForQuestStep { ItemIds = [12345u] }`. Item not equipped.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"in combat"`.

#### EG7 -- Mounted + prior Navigate: lazy-dismount does NOT fire before EquipGear (exempt)

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep to `(200, 0, 0)` with `Expect = "playerZone() == 130"`.
  2. EquipGearForQuestStep `{ ItemIds = [12345u] }`. Item not equipped.
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).

**When:**
1. Tick 1 --> `EngineAction.Navigate`. `_lastDispatchedWasNavigate = true`.
2. `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect satisfies).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.EquipGear(12345u)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount did NOT fire — EquipGear is exempt).

Pins Decision EG-5.

#### EG8 -- Standalone EquipGear + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: EquipGearForQuestStep `{ ItemIds = [12345u] }`. Item not equipped.
- Player mounted (`SetMountState(MountState.Mounted)`).

**When:** Tick once.

**Then:**
- Returns `EngineAction.EquipGear(12345u)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount bound to prior Navigate).

#### EG9 -- No adapter wired -- AwaitUser

**Given:**
- A `QuestEngine` constructed WITHOUT `gearEquipper` (null default).
- `EquipGearForQuestStep { ItemIds = [12345u] }`.

**When:** `engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.AwaitUser` whose `Reason` contains `"no IGearEquipper wired"`.

#### EG10 -- Authored Expect already satisfied + all items equipped -- step skipped

**Given:**
- `EquipGearForQuestStep { ItemIds = [12345u], Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(new AetheryteId(8), true)` (predicate true before step runs).
- `harness.GearEquipper.SetItemEquipped(12345u, true)`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits in cursor walk; step confirmed).

#### EG11 -- Authored Expect NOT satisfied but all items equipped -- step NOT yet confirmed

**Given:**
- `EquipGearForQuestStep { ItemIds = [12345u], Expect = PredicateExpect("questFlag(82111, 3)") }`.
- `harness.GearEquipper.SetItemEquipped(12345u, true)` (item equipped).
- Quest flag 3 NOT set (predicate false).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- The dispatch arm runs (Expect was false in the cursor walk). `ResolveEquipGear` scans items, finds all equipped, returns null. The dispatch arm self-confirms (adds to `_confirmedStepIds`), continues to the next step.
- Returns `EngineAction.Wait` (no more steps).

This verifies that the implicit postcondition fires even when authored Expect would not satisfy. The step is confirmed by the dispatch arm, not by the Expect loop. The authored Expect is evaluated in the cursor walk BEFORE the dispatch arm -- if it returns false, the dispatch arm runs and the implicit postcondition can still confirm the step.

#### EG12 -- Cancellation propagates

**Given:**
- EquipGearForQuestStep as EG1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

### RunToCompletion integration test

#### EG13 -- Full RunToCompletion with two items

**Given:**
- Quest: AcceptStep + EquipGearForQuestStep `{ ItemIds = [11111u, 22222u] }`. AcceptStep Expect auto-satisfies. EquipGear has no authored Expect.
- Both items unequipped. AcceptStep wired to auto-satisfy on interact.
- In the RunToCompletion loop, after each `EquipGear(itemId)` dispatch, script `harness.GearEquipper.SetItemEquipped(itemId, true)`.

**When:** `harness.RunToCompletion(maxTicks: 10)`.

**Then:**
- `actions` contains at least two `EquipGear` entries.
- `actions[indexOfFirstEquipGear].ItemId == 11111u`.
- `actions[indexOfSecondEquipGear].ItemId == 22222u`.
- RunToCompletion succeeds (Done returned).

Note: This test requires the EngineTestHarness `RunToCompletion` arm for `EquipGear` to call `GearEquipper.EquipItem` and then `SetItemEquipped`. The Tester may implement the side-effect via a callback or inline in the arm.

### Inference tests (`QuestForge.Engine.Tests/Authoring/EquipGearInferenceTests.cs`)

#### EGI1 -- EquipmentChanged signal fires inference rule 3.5g

**Given:**
- `before` snapshot with no EquipmentChanged.
- `after` snapshot with `EquipmentChanged = new EquipmentChangedSignal([12345u])`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "equip-gear-for-quest"`.
- `result.InferredFrom == InferredFrom.EquipmentChanged`.
- `result.Confidence == Confidence.High`.

#### EGI2 -- EquipmentChanged with multiple items

**Given:**
- `after` snapshot with `EquipmentChanged = new EquipmentChangedSignal([11111u, 22222u])`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "equip-gear-for-quest"`.
- `result.SuggestedStepId` starts with `"equip-gear-"`.

#### EGI3 -- StepFactory builds EquipGearForQuestStep from inference result

**Given:**
- `InferenceResult` with `StepType = "equip-gear-for-quest"`, `SuggestedExpect = null`.
- `after` snapshot with `EquipmentChanged = new EquipmentChangedSignal([12345u, 67890u])`.

**When:** `StepFactory.Build(result, after)`.

**Then:**
- Result is `EquipGearForQuestStep` with `ItemIds` containing `[12345u, 67890u]`.

#### EGI4 -- EquipmentChanged has lower priority than ItemUsed

**Given:**
- `after` snapshot with BOTH `ItemUsed = new ItemUsedSignal(...)` AND `EquipmentChanged = new EquipmentChangedSignal([12345u])`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "use-item"` (ItemUsed has higher priority per Rule 3.5i > 3.5g).

### UIObserver polling tests (`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`)

#### UO_M1 -- First equipment observation sets baseline, no fire

**Given:**
- `FakeGameProbe.GetEquippedItemIds()` returns `[100, 200, 300, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]`.
- UIObserver created, no prior poll.

**When:** `OnFrameworkUpdate` fires once.

**Then:**
- `_aggregator.OnEquipmentChanged` was NOT called (first observation is silent baseline).

#### UO_M2 -- Subsequent observation with changed slot fires OnEquipmentChanged

**Given:**
- Baseline established: `[100, 200, 0, ...]`.
- `FakeGameProbe.GetEquippedItemIds()` returns `[100, 200, 500, ...]` (slot 2 changed from 0 to 500).

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnEquipmentChanged` called with `newItemIds = [500]`.

#### UO_M3 -- Multiple slots changed fires OnEquipmentChanged with all new IDs

**Given:**
- Baseline: `[100, 200, 300, ...]`.
- Next observation: `[100, 999, 888, ...]` (slots 1 and 2 changed).

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnEquipmentChanged` called with `newItemIds` containing `999` and `888`.

#### UO_M4 -- Same equipment across ticks does not re-fire

**Given:**
- Baseline established with `[100, 200, 300, ...]`.
- Same values returned on next poll.

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnEquipmentChanged` NOT called.

#### UO_M5 -- ResetWindowState calls OnEquipmentChangedConsumed but does NOT reset baseline

**Given:**
- Equipment changed and OnEquipmentChanged fired.

**When:** `ResetWindowState()` called, then another `OnFrameworkUpdate` with the same equipment.

**Then:**
- `_aggregator.OnEquipmentChangedConsumed` was called.
- `_aggregator.OnEquipmentChanged` NOT called on the subsequent tick (baseline was NOT reset; same equipment = no change).

### DraftValidator W1 suppression test

#### EG14 -- W1 suppressed for EquipGearForQuestStep without Expect

**Given:**
- `QuestDraft` with `EquipGearForQuestStep { ItemIds = [12345u], Expect = null }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `warnings` does NOT contain any entry with `Code == "W1"` for the equip step.
- `errors` does NOT contain E16 or E17 (ItemIds is non-empty, no zeros).

---

## Implementation order

### Phase A -- EngineAction + Engine dispatch (1 hour)

1. Append `EngineAction.EquipGear(uint ItemId, Step? Origin = null)` to `EngineAction.cs` (after `UseItem`).
2. Add `ResolveEquipGear` method to `QuestEngine.cs` per Decision EG-3.
3. Add dispatch arm (6a5) to the step-dispatch switch in `ResolveAction`, after the `UseItemStep` arm. Self-confirms when `ResolveEquipGear` returns null.
4. Extend W1 suppression in `DraftValidator.cs` to include `EquipGearForQuestStep` per Decision EG-9.
5. **Tester writes EG1, EG2, EG3, EG5, EG6, EG9, EG10, EG11, EG12** (single-tick dispatch tests). Red until builder implements steps 1-4.

**Done before B:** Engine tests green. `dotnet test QuestForge.Engine.Tests` passes.

### Phase B -- EngineTestHarness wiring (30 min)

1. Add `case EngineAction.EquipGear eg:` arm to `RunToCompletion` in `EngineTestHarness.cs`:
   ```csharp
   case EngineAction.EquipGear eg:
       actions.Add(action);
       EmitActionSubmitted("EquipGear",
           JsonSerializer.SerializeToElement(new { itemId = eg.ItemId }, _jsonOpts));
       var egResult = await GearEquipper.EquipItem(eg.ItemId, ct);
       EmitActionCompleted("EquipGear",
           egResult.IsSuccess ? egResult.ValueOrThrow.ToString() : "Failed");
       break;
   ```
2. **Tester writes EG4, EG7, EG8, EG13** (multi-tick and mount tests). Red until builder implements step 1.
3. **Tester writes EG14** (W1 suppression). Green after Phase A step 4.

**Done before C:** All engine tests green.

### Phase C -- DalamudGearEquipper (1 hour)

1. Extract `EquipSlotResolver.GetTargetSlot(uint equipSlotCategory) -> int?` into `QuestForge.Adapters/Gear/EquipSlotResolver.cs` per Decision EG-7. Pure function, unit-testable.
2. Write tests for `EquipSlotResolver` in `QuestForge.Adapters.Tests/Gear/EquipSlotResolverTests.cs`.
3. Flesh out `DalamudGearEquipper.EquipItem` per Decision EG-7.
4. Flesh out `DalamudGearEquipper.IsItemEquipped`.

**Done before D:** `dotnet build QuestForge.Adapters.Dalamud` succeeds. `EquipSlotResolver` tests green.

### Phase D -- EngineHost dispatch arm (15 min)

1. Add `case EngineAction.EquipGear eg:` arm to `EngineHost.DispatchAction` per Decision EG-8.
2. Add `IGearEquipper DebugGearEquipper => _gearEquipper;` per Decision EG-12.
3. `dotnet build QuestForge.Plugin` succeeds.

**Done before E:** Plugin compiles.

### Phase E -- Authoring inference (1.5 hours)

1. Add `EquipmentChangedSignal` record to `GameStateSnapshot.cs`.
2. Add `EquipmentChanged` property to `GameStateSnapshot` (non-positional init-only).
3. Add `InferredFrom.EquipmentChanged` enum value.
4. Add `OnEquipmentChanged` / `OnEquipmentChangedConsumed` to `SnapshotAggregator`.
5. Add Rule 3.5g to `StepInferenceEngine`.
6. Add `"equip-gear-for-quest"` arm to `StepFactory`.
7. Add `GetEquippedItemIds()` to `IGameProbe` + `FakeGameProbe`.
8. Add `PollEquipmentChange` to `UIObserver`.
9. Wire `PollEquipmentChange` into `OnFrameworkUpdate`.
10. Wire `OnEquipmentChangedConsumed` into `ResetWindowState`.
11. Wire `OnEquipmentChangedConsumed` into `AuthoringHost.RecordStep` consume sequence.
12. Wire `PreviewInference` diagnostic log extension.
13. Add `DalamudGameProbe.GetEquippedItemIds` implementation.
14. **Tester writes EGI1-EGI4, UO_M1-UO_M5** (inference + polling tests).

**Done before F:** Inference tests green.

### Phase F -- Tooling catch-up (paired PR, 30 min)

1. Add `ActionEquipGear = "equipgear"` to `TraceConstants.cs`.
2. Add FilenameLookup entry per Decision EG-11.
3. Add DistinguishingCapPriority entry per Decision EG-11.
4. Write tests for the new entries in `QuestForge.Tools.Trace.Tests/`.
5. `dotnet test` in tools repo green.

**Total estimated time: ~5 hours across engine, Dalamud, inference, and tooling.**

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~EquipGearForQuestStepTests` reports all 14 engine tests green (EG1-EG14).
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~EquipGearInference` reports all 4 inference tests green (EGI1-EGI4).
3. `dotnet test QuestForge.Plugin.Tests --filter FullyQualifiedName~UO_M` reports all 5 polling tests green (UO_M1-UO_M5).
4. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~EquipSlotResolver` reports pure-helper tests green.
5. A quest with `{ "type": "equip-gear-for-quest", "itemIds": [12345, 67890] }` dispatches `EquipGear(12345)` on the first tick when both items are unequipped.
6. When both items report equipped (via `IsItemEquipped`), the step self-confirms without needing an authored `Expect`.
7. `DalamudGearEquipper.EquipItem` calls `InventoryManager.MoveItemSlot` with the correct target slot determined from the item's `EquipSlotCategory`.
8. `EngineHost.DispatchAction` has a `case EngineAction.EquipGear` arm that calls `_gearEquipper.EquipItem`.
9. W1 does NOT fire for `EquipGearForQuestStep` with `Expect = null`.
10. `StepInferenceEngine` infers `"equip-gear-for-quest"` when `EquipmentChanged` signal is present.
11. `StepFactory` builds `EquipGearForQuestStep` with correct `ItemIds` from the `EquipmentChanged` signal.
12. `UIObserver.PollEquipmentChange` detects equipment slot deltas via `IGameProbe.GetEquippedItemIds()`.
13. `dotnet build` succeeds in both `questforge` and `questforge-tools` repos with no `TreatWarningsAsErrors` regressions.
14. `questforge-tools` TraceConstants has `ActionEquipGear = "equipgear"`.
15. No regression in UseItemStepTests, UseEmoteStepTests, UseActionStepTests, SayChatMessageStepTests, TeleportStepTests, PurchaseItemStepTests, AttunementStepTests, or any existing test.

---

## Exclusions

- **`EquipBestGearStep` engine dispatch.** Tracked in issue #118. Separate step type, separate adapter (`IBestGearEquipper`).
- **`ChangeJobStep` engine dispatch.** Tracked in issue #119. Separate step type, separate adapter (`IJobChanger`).
- **Repair interfaces (`IGearConditionInspector`, `IGearRepairer`).** Tracked in issue #120.
- **Schema changes.** Already landed in #122. `EquipGearForQuestStep { uint[] ItemIds }` is final.
- **Interface split.** Already landed in #121. `IGearEquipper` with 2 methods is final.
- **DraftValidator E16/E17 rules.** Already landed in #122.
- **Fake (`FakeGearEquipper`) and Dalamud stub.** Already landed in #121.
- **`playerHasEquipped()` predicate function.** Not needed because the implicit postcondition (Decision EG-2) handles the check internally. If a future need arises, it can be added independently.
- **Gearset update after equip.** Questionable calls `RaptureGearsetModule.UpdateGearset()` after equipping items to keep the gearset in sync. Deferred -- this is a nice-to-have for v2 (prevents the gearset from going stale). The quest will work without it.
- **Ring slot preference.** For rings, the adapter picks the first available ring slot (right then left). No schema-level control for "equip in left ring slot specifically."
- **In-game smoke test.** Manual Slice 4 verification (not part of this CI-focused plan).

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 5 scenarios (EG1, EG2, EG4, EG10, EG13)
- Edge cases: 5 scenarios (EG3, EG7, EG8, EG11, EG14)
- Error / wait cases: 3 scenarios (EG5, EG6, EG9)
- Cancellation: 1 scenario (EG12)
- Inference: 4 scenarios (EGI1-EGI4)
- Polling: 5 scenarios (UO_M1-UO_M5)
- Expected total:
  - `QuestForge.Engine.Tests/Engine/EquipGearForQuestStepTests.cs`: 14 tests (EG1-EG14)
  - `QuestForge.Engine.Tests/Authoring/EquipGearInferenceTests.cs`: 4 tests (EGI1-EGI4)
  - `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`: 5 tests (UO_M1-UO_M5, appended to existing file)
  - `QuestForge.Adapters.Tests/Gear/EquipSlotResolverTests.cs`: ~5-8 tests (pure helper, Tester decides count)
  - Grand total: ~28-31 tests
