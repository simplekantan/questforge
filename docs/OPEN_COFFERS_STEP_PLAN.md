# OpenCoffersStep Implementation Plan

**Status:** ready for test creation

**Slice:** Combined 2-3 (schema + engine + adapter + Dalamud impl + EngineHost dispatch + tooling catch-up + predicate). No authoring inference (per user decision 8 -- always explicitly authored).

**Input docs:**
- `docs/EQUIP_GEAR_FOR_QUEST_STEP_PLAN.md` -- closest analog (single-item-per-tick dispatch, implicit postcondition pattern -- but OpenCoffers uses explicit Expect instead)
- `docs/EQUIP_BEST_GEAR_STEP_PLAN.md` -- secondary analog (empty step, no inference)
- `QuestForge.Adapters/State/IGameStateProvider.cs` -- existing methods including `GetFreeInventorySlots`
- `QuestForge.Engine/Predicates/PredicateEvaluator.cs` -- existing predicates
- `QuestForge.Adapters.Dalamud/Items/DalamudItemUser.cs` -- reference for `ActionManager.UseAction` calls
- `FFXIVClientStructs/ActionManager.cs` -- `UseAction(ActionType, uint, ulong, uint extraParam, ...)` signature confirmed
- `FFXIVClientStructs/InventoryManager.cs` -- `GetInventoryContainer`, `GetInventorySlot`, `GetEmptySlotsInBag`
- `QuestForge.Schema/Step.cs` -- current step hierarchy (26 step types)
- `QuestForge.Engine/QuestEngine.cs` -- dispatch arm pattern, pre-arm guard pattern
- `QuestForge.Engine/EngineAction.cs` -- existing action records
- `QuestForge.Engine/Authoring/DraftValidator.cs` -- W1 suppression guard
- `QuestForge.Plugin/EngineHost.cs` -- dispatch arm + dismount exemption list
- `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` -- RunToCompletion + HarnessEngine patterns

**Output (CI behavior):** Adding `{ "type": "open-coffers", "id": "open-coffers" }` to a quest dispatches `EngineAction.OpenCoffer` from `QuestEngine`. Each tick opens one coffer (single-item dispatch). The step has no implicit postcondition -- authors write `"expect": "not inventoryHasCoffers()"` to gate advancement. W1 fires if Expect is missing. The `inventoryHasCoffers()` predicate is a new zero-arg boolean on `IGameStateProvider`. Engine unit tests in `QuestForge.Engine.Tests/Engine/OpenCoffersStepTests.cs` cover all dispatch arms.

---

## Dependency graph

```
QuestForge.Schema
  └── OpenCoffersStep (NEW empty sealed class, discriminator "open-coffers")
  └── QuestForgeJsonContext: [JsonSerializable(typeof(OpenCoffersStep))]
       ↓
QuestForge.Adapters
  ├── Items/ICofferOpener.cs (NEW 2-method interface)
  ├── Items/CofferIdentifier.cs (NEW pure helper)
  └── State/IGameStateProvider.cs: HasCoffers(ct) (NEW method)
       ↓
QuestForge.Adapters.Fakes
  ├── Items/FakeCofferOpener.cs (NEW fake)
  └── State/FakeGameStateProvider.cs: SetHasCoffers(bool) + HasCoffers(ct) (NEW)
       ↓
QuestForge.Engine
  ├── EngineAction.OpenCoffer (NEW record)
  ├── QuestEngine: ResolveOpenCoffers async pre-arm + dispatch arm (6a9)
  └── Predicates/PredicateEvaluator.cs: "inventoryHasCoffers" function
       ↓
QuestForge.Engine.Tests
  ├── Engine/OpenCoffersStepTests.cs (OC1-OC12)
  ├── Schema/RoundTripTests.cs (OpenCoffersStep round-trip)
  └── Helpers/EngineTestHarness.cs (RunToCompletion arm for OpenCoffer)
       ↓
QuestForge.Adapters.Dalamud
  └── Items/DalamudCofferOpener.cs (NEW)
       ↓
QuestForge.Plugin
  └── EngineHost.cs (field + ctor + BeginRun + DispatchAction arm)
       ↓
questforge-tools (paired PR)
  ├── TraceConstants: ActionOpenCoffer
  ├── CapabilityInferrer: step:open-coffers entry
  ├── FilenameLookup: new entry
  ├── DistinguishingCapPriority: new entry
  └── FunctionRegistry: inventoryHasCoffers entry
```

**Build order:**
1. Schema: `OpenCoffersStep` + JSON context + round-trip test.
2. Adapter interface: `ICofferOpener` + `CofferIdentifier` pure helper + `IGameStateProvider.HasCoffers`.
3. Fake: `FakeCofferOpener` + `FakeGameStateProvider.HasCoffers`.
4. Engine: `EngineAction.OpenCoffer` + `ResolveOpenCoffers` pre-arm + dispatch arm.
5. Predicate: `inventoryHasCoffers` in `PredicateEvaluator`.
6. EngineTestHarness: `RunToCompletion` arm for `OpenCoffer`.
7. Engine tests OC1-OC12.
8. `DalamudCofferOpener`: Lumina scan + `ActionManager.UseAction(Item, ...)`.
9. `EngineHost`: field + ctor + BeginRun + dispatch arm.
10. Tooling catch-up (paired PR).

---

## Architectural decisions

### OC-1: Empty schema -- no properties

**Decision:** `OpenCoffersStep : Step { }` is a sealed empty class with no properties. The discriminator is `"open-coffers"`. The step opens ALL coffers in inventory -- no filter, no item ID list, no count.

```csharp
// QuestForge.Schema/Step.cs (append after RegisterGearsetStep)
public sealed class OpenCoffersStep : Step { }
```

```csharp
// Step class attribute list (append after RegisterGearsetStep)
[JsonDerivedType(typeof(OpenCoffersStep), "open-coffers")]
```

```csharp
// QuestForgeJsonContext.cs (append after RegisterGearsetStep)
[JsonSerializable(typeof(OpenCoffersStep))]
```

**Rationale:** Coffers are identified entirely at runtime by the adapter (Lumina item filter). The schema does not need to enumerate them. This mirrors `EquipBestGearStep` (empty, no properties).

**What breaks if violated:** If the step carried a coffer item ID, the author would need to know exact item IDs at authoring time, which is impractical since coffer contents vary by dungeon.

**Testability:** Tests can construct `new OpenCoffersStep { Id = "open-coffers" }` with no configuration.

### OC-2: Single coffer per tick

**Decision:** `EngineAction.OpenCoffer` carries a single `uint ItemId` (plus `Step? Origin`). When multiple coffers exist in inventory, the engine opens the first one per tick. The game's cast bar naturally throttles (~2-3 seconds per coffer).

```csharp
// QuestForge.Engine/EngineAction.cs (append after RegisterGearset)
public sealed record OpenCoffer(uint ItemId, Step? Origin = null) : EngineAction;
```

**Rationale:** Matches the EquipGearForQuestStep pattern (Decision EG-1). Single-item-per-tick is stateless: each tick scans inventory, finds the first coffer, opens it. No per-step state in QuestEngine.

**Rejected alternative:** Batch `OpenCoffer(uint[] ItemIds)` that opens all coffers in one dispatch. Rejected for the same reasons as EG-1: partial failure handling, adapter-internal delays, and EngineTestHarness complexity.

**What breaks if violated:** If the action carries multiple IDs, the adapter must handle inter-item delays internally and the EngineHost dispatch arm needs a loop.

**Testability:** Tests script `FakeCofferOpener.SetCofferItemIds([...])` and verify one-at-a-time dispatch.

### OC-3: No implicit postcondition -- Expect-only advancement

**Decision:** Unlike `EquipGearForQuestStep` (which self-confirms via implicit postcondition), `OpenCoffersStep` has NO implicit postcondition. The engine cannot verify "all coffers opened" because the adapter's `GetCofferItemIds` list changes as coffers are opened. The step relies on authored `Expect` for advancement.

Consequence: authors write `"expect": "not inventoryHasCoffers()"` to gate advancement. If the step has no `Expect`, the engine will re-fire `OpenCoffer` every tick until no coffers remain, then emit `Wait` (no coffers, nothing to open, Expect not checked). This spin-loop is harmless but wasteful. W1 warns the author.

**Why not implicit postcondition:** The pre-arm returns `Wait("no coffers in inventory")` when `GetCofferItemIds` returns empty. This is a reasonable termination signal, but it does NOT self-confirm -- the step cursor does not advance without Expect. This is by design: after opening all coffers, the game may need additional quest-state changes before the step should be considered complete. The Expect gives authors control.

**Rejected alternative:** Self-confirm when `GetCofferItemIds` returns empty (all coffers opened). Rejected because "no coffers left" does not necessarily mean the step is done -- the player might need to equip the revealed gear, or a quest flag might need to advance. Forcing an explicit Expect is safer.

**What breaks if violated:** If the step self-confirms on empty coffer list, subsequent steps that depend on quest state changes triggered by opening coffers may fire prematurely.

### OC-4: Pre-flight guards in `ResolveOpenCoffers`

**Decision:** Four guards in priority order:

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. No adapter wired | `_cofferOpener is null` | `AwaitUser("OpenCoffersStep dispatched but no ICofferOpener wired")` |
| 2. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `Wait("player casting; deferring open-coffers")` |
| 3. Inventory full | `GetFreeInventorySlots(ct) == 0` | `AwaitUser("inventory full — free space before opening coffers")` |
| 4. No coffers in inventory | `GetCofferItemIds(ct)` returns empty | `Wait("no coffers in inventory")` |

After guards pass, emit `EngineAction.OpenCoffer(firstCofferId, Origin: step)`.

```csharp
private async Task<EngineAction> ResolveOpenCoffers(OpenCoffersStep step, CancellationToken ct)
{
    if (_cofferOpener is null)
        return new EngineAction.AwaitUser(
            "OpenCoffersStep dispatched but no ICofferOpener wired — host must supply one");

    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring open-coffers", Origin: step);

    var slotsResult = await _gameState.GetFreeInventorySlots(ct);
    if (slotsResult is Result<int>.Success { Value: 0 })
        return new EngineAction.AwaitUser(
            "inventory full — free space before opening coffers");

    var coffersResult = await _cofferOpener.GetCofferItemIds(ct);
    if (coffersResult is not Result<IReadOnlyList<uint>>.Success { Value: var cofferIds }
        || cofferIds.Count == 0)
        return new EngineAction.Wait("no coffers in inventory", Origin: step);

    return new EngineAction.OpenCoffer(cofferIds[0], Origin: step);
}
```

**Why no InCombat guard:** Opening coffers only happens after dungeons, in a safe zone. Combat is not a realistic scenario during coffer opening. If it happens, the UseAction(Item) call will fail naturally and the engine retries next tick. The guard is omitted to avoid unnecessary reads.

**Why inventory-full is AwaitUser, not Wait:** An inventory-full condition cannot resolve on its own -- the player must manually discard or move items. `AwaitUser` stops the run so the player can act. This is a user-resolvable blocker, not a transient condition.

**What breaks if violated:** If guard 3 (inventory full) is Wait instead of AwaitUser, the engine spin-loops indefinitely waiting for a condition that cannot self-resolve.

### OC-5: `_lastResolvedStep` is NOT set in `ResolveOpenCoffers`

Mirrors Decision UA13 / UE12 / EG-4 / EB-4. `OpenCoffersStep` has no properties and no `DialogueChoices`. The `Origin: step` field on `EngineAction.OpenCoffer` carries context for trace consumers.

### OC-6: Dismount exemption -- OpenCoffer is NOT exempt

**Decision:** `OpenCoffer` is NOT in the lazy-dismount exemption list. Opening coffers (item use via `ActionManager.UseAction(ActionType.Item)`) is rejected while mounted. The player must dismount first. The lazy-dismount hook naturally handles this: after a Navigate, the next non-exempt action triggers dismount.

This is the standard behavior -- most step types are NOT exempt. Only Navigate, Teleport, EquipGear, EquipBestGear, and RegisterGearset are exempt.

**No code change required:** The existing dismount exemption check (`is not EngineAction.Navigate and not EngineAction.Teleport and not EngineAction.EquipGear and not EngineAction.EquipBestGear and not EngineAction.RegisterGearset`) already does NOT include `EngineAction.OpenCoffer`, so the dismount fires.

Pinned by tests OC8 (mounted + prior Navigate: dismount fires before OpenCoffer) and OC9 (standalone mounted: dismount does NOT fire -- lazy-dismount only triggers after Navigate).

### OC-7: `ICofferOpener` interface -- two methods, placed in `Items/`

**Decision:** `ICofferOpener` lives in `QuestForge.Adapters/Items/` (alongside `IItemUser`). Coffers are inventory items, not gear.

```csharp
// QuestForge.Adapters/Items/ICofferOpener.cs
namespace QuestForge.Adapters.Items;

public interface ICofferOpener
{
    /// <summary>
    /// Scans player inventory (bags 1-4) for coffer items.
    /// A coffer is an item where ItemAction.RowId is 1085 or 388
    /// AND ItemUICategory.RowId == 61 (Miscellany).
    /// Returns their item IDs (may contain duplicates for stacked coffers).
    /// </summary>
    Task<Result<IReadOnlyList<uint>>> GetCofferItemIds(CancellationToken ct);

    /// <summary>
    /// Opens one coffer by using it as an item.
    /// Calls ActionManager.UseAction(ActionType.Item, itemId, 0xE0000000, 65535).
    /// Success means the game accepted the use request (cast bar started).
    /// </summary>
    Task<Result<Unit>> OpenCoffer(uint itemId, CancellationToken ct);
}
```

**Why not put `HasCoffers` on `ICofferOpener`:** The predicate evaluator only has access to `IGameStateProvider`. Putting `HasCoffers` on `ICofferOpener` would require threading the adapter through the predicate system, which violates the architecture. `HasCoffers` must be on `IGameStateProvider`.

**Logic duplication:** Both `ICofferOpener.GetCofferItemIds` (Dalamud impl) and `IGameStateProvider.HasCoffers` (Dalamud impl) perform a Lumina inventory scan with the coffer filter. This duplication is acceptable: the pure filter logic is extracted into `CofferIdentifier` (Decision OC-8) and shared by both Dalamud implementations.

### OC-8: Pure helper extraction -- `CofferIdentifier`

**Decision:** Extract the coffer identification filter as a static pure helper for unit testing. Lives in `QuestForge.Adapters/Items/`.

```csharp
// QuestForge.Adapters/Items/CofferIdentifier.cs
namespace QuestForge.Adapters.Items;

/// <summary>
/// Pure predicate: determines if a Lumina Item row represents a treasure coffer.
/// Coffer = ItemAction.RowId in {1085, 388} AND ItemUICategory.RowId == 61.
/// Excludes 367 (Triple Triad card packs).
/// </summary>
public static class CofferIdentifier
{
    /// <summary>Known ItemAction row IDs that represent gear coffers.</summary>
    private static readonly HashSet<uint> CofferActionIds = [1085, 388];

    /// <summary>ItemUICategory row ID for Miscellany.</summary>
    private const uint MiscellanyCategory = 61;

    /// <summary>
    /// Returns true if an item with the given ItemAction.RowId and ItemUICategory.RowId
    /// is a gear coffer that should be opened.
    /// </summary>
    public static bool IsCoffer(uint itemActionRowId, uint itemUiCategoryRowId)
        => CofferActionIds.Contains(itemActionRowId) && itemUiCategoryRowId == MiscellanyCategory;
}
```

**Rationale:** Follows the `ActionStatusInterpreter` / `EmoteCommandResolver` / `EquipSlotResolver` precedent. Pure logic extracted for unit testing per `feedback_tdd_even_for_adapters.md`.

**What breaks if violated:** If the filter is inline in the Dalamud adapter, it cannot be unit-tested without a live game. Any filter logic error (e.g., including Triple Triad packs) would only surface in manual testing.

### OC-9: `IGameStateProvider.HasCoffers` -- new method for predicate support

**Decision:** Add `HasCoffers` to `IGameStateProvider`. Returns `Result<bool>` -- true if any coffer items exist in player inventory.

```csharp
// IGameStateProvider.cs (append to Inventory section)
Task<Result<bool>> HasCoffers(CancellationToken ct);
```

The Dalamud implementation performs the same Lumina inventory scan as `DalamudCofferOpener.GetCofferItemIds` but short-circuits on the first match (returns bool, not list). Both use `CofferIdentifier.IsCoffer` for the filter.

The Fake implementation adds a simple boolean toggle:

```csharp
// FakeGameStateProvider.cs (new field + setter + method)
private bool _hasCoffers;
public void SetHasCoffers(bool value) { lock (_lock) _hasCoffers = value; }

public Task<Result<bool>> HasCoffers(CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    Record("HasCoffers");
    lock (_lock)
        return Task.FromResult<Result<bool>>(Result.Ok(_hasCoffers));
}
```

### OC-10: `inventoryHasCoffers()` predicate function

**Decision:** New zero-arg boolean predicate. Authors write `"expect": "not inventoryHasCoffers()"` to gate advancement after all coffers are opened.

```csharp
// PredicateEvaluator.cs (append to function switch)
"inventoryHasCoffers" => (await _gameState.HasCoffers(ct)).ValueOrThrow,
```

**Tools-repo:** Add to `FunctionRegistry`:
```csharp
new("inventoryHasCoffers", new Fixed(0), [], Bool),
```

### OC-11: DalamudCofferOpener implementation design

**`GetCofferItemIds(ct)` implementation:**

1. Get `InventoryManager.Instance()`. If null, return `Result.Fail("inventoryManagerUnavailable")`.
2. Scan the 4 player inventory bags: `Inventory1`, `Inventory2`, `Inventory3`, `Inventory4`.
3. For each non-empty slot, look up the item in the Lumina `Item` sheet via `DataManager.GetExcelSheet<Item>().GetRow(itemId)`.
4. Read `item.ItemAction.RowId` and `item.ItemUICategory.RowId`.
5. Call `CofferIdentifier.IsCoffer(itemActionRowId, itemUiCategoryRowId)`.
6. Collect matching item IDs into a list.
7. Return `Result.Ok(list)`.

**`OpenCoffer(uint itemId, ct)` implementation:**

```csharp
public unsafe Task<Result<Unit>> OpenCoffer(uint itemId, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    var am = ActionManager.Instance();
    if (am is null)
        return Task.FromResult<Result<Unit>>(
            Result.Fail("actionManagerUnavailable", "ActionManager.Instance() returned null"));

    // ActionType.Item = 2
    // targetId = 0xE0000000 (self)
    // extraParam = 0xFFFF (65535) = "use from hotbar / unspecified slot"
    var ok = am->UseAction(ActionType.Item, itemId, 0xE0000000, 65535);
    return Task.FromResult<Result<Unit>>(ok
        ? Result.Ok()
        : Result.Fail("UseAction returned false", $"itemId={itemId}"));
}
```

**Key parameter notes from FFXIVClientStructs research:**
- `extraParam: 0xFFFF (65535)` means "unspecified inventory slot" -- the game finds the item itself. This matches what AutoDuty uses and what the `DalamudItemUser` pattern shows for items used from hotbar.
- `targetId: 0xE0000000` is the default "no target / self" value.
- `ActionType.Item` is the correct native enum value (the C# type is `ActionType` in FFXIVClientStructs).

**`DalamudGameStateProvider.HasCoffers(ct)` implementation:**

Same Lumina scan as `GetCofferItemIds` but returns `true` on first match (short-circuit). Both implementations share `CofferIdentifier.IsCoffer` for the filter predicate.

### OC-12: EngineHost dispatch arm for OpenCoffer

```csharp
case EngineAction.OpenCoffer oc:
    DebounceLog(
        $"opencoffer:{oc.ItemId}",
        $"[OpenCoffer] itemId={oc.ItemId}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _cofferOpener.OpenCoffer(oc.ItemId, ct);
    break;
```

Placement: after the `RegisterGearset` arm, before the `Wait` arm.

### OC-13: W1 is NOT suppressed for OpenCoffersStep

**Decision:** W1 DOES fire for `OpenCoffersStep` with no `Expect`. This is by design (per user decision 10): authors should always write `"expect": "not inventoryHasCoffers()"` for this step type because there is no implicit postcondition.

**No code change to the W1 guard:** The existing W1 guard fires for step types not in the suppression list. `OpenCoffersStep` is not in the list, so W1 fires automatically. The existing guard reads:
```csharp
if (step.Raw.Expect is null && step.Raw is not UseActionStep and not UseEmoteStep
    and not SayChatMessageStep and not UseItemStep and not EquipGearForQuestStep
    and not ChangeJobStep)
```

`OpenCoffersStep` is not in this list, so W1 fires. No change needed.

### OC-14: Recording-proxy decision -- no `RecordingCofferOpener` needed

Per CLAUDE.md Slice 3 contract: "Write-only adapters don't need a RecordingXxxExecutor wrapper." `ICofferOpener.OpenCoffer` is write-only. `ICofferOpener.GetCofferItemIds` is a read used only in the pre-arm to find the next coffer -- the result is captured indirectly by whether `OpenCoffer` is emitted. `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` capture the write.

### OC-15: Debug accessor

Add `ICofferOpener DebugCofferOpener => _cofferOpener;` to EngineHost alongside the existing debug accessors.

### OC-16: No DraftValidator step-specific rules

`OpenCoffersStep` is an empty step with no properties. There are no fields to validate (no ItemIds, no ActionId, no TargetNpcId). No new E-rules needed.

W1 fires for missing Expect (Decision OC-13). No step-specific W11+ warning needed -- W1's generic message is sufficient.

### OC-17: Dispatch arm in QuestEngine -- no self-confirm

**Decision:** The dispatch arm in the step-dispatch switch always returns the action. No implicit postcondition, no null return, no self-confirm.

```csharp
// In the step-dispatch switch, after RegisterGearsetStep arm (6a8):
// 6a9. OpenCoffersStep async arm — no implicit postcondition (relies on authored Expect).
if (step is OpenCoffersStep openCoffersStep)
{
    var openCoffersAction = await ResolveOpenCoffers(openCoffersStep, ct);
    return (openCoffersAction, step.Id);
}
```

The return type of `ResolveOpenCoffers` is `Task<EngineAction>` (non-nullable). When no coffers remain, it returns `Wait("no coffers in inventory")`. The Expect in the cursor walk gates advancement.

### OC-18: EngineHost construction wiring

```csharp
// Field declaration (after _gearsetManager):
private readonly DalamudCofferOpener _cofferOpener;

// Constructor (after _gearsetManager):
_cofferOpener = new DalamudCofferOpener(services);

// BeginRun (in QuestEngine constructor call):
cofferOpener: _cofferOpener

// QuestEngine ctor: new optional parameter
ICofferOpener? cofferOpener = null
```

### OC-19: Tooling catch-up scope

**CapabilityInferrer:** Add `[typeof(OpenCoffersStep)] = "step:open-coffers"` to `StepCapabilities`.

**TraceConstants:** Add `ActionOpenCoffer = "opencoffer"` (from `EngineAction.OpenCoffer.GetType().Name.ToLowerInvariant()`). No behavior change (`IsTerminalAction` only uses `done`/`awaituser`).

**FilenameLookup:** Add exact-shape entry:
```csharp
(["step:open-coffers", "step:talk", "step:travel"], "with-open-coffers.json"),
```

**DistinguishingCapPriority:** Add entry after `step:register-gearset`, before `step:teleport`:
```csharp
("step:open-coffers", "with-open-coffers.json"),
```

Priority rationale: Opening coffers is a post-dungeon housekeeping action, less shape-defining than gear/job changes but more so than teleport/purchase.

**FunctionRegistry:** Add entry:
```csharp
new("inventoryHasCoffers", new Fixed(0), [], Bool),
```

**FIXTURES.md:** Add `step:open-coffers` row to capabilities table and `"opencoffer"` row to actionType canonical strings table.

---

## Validation rule table

No new validation rules. `OpenCoffersStep` has no properties to validate.

| Rule | Code | Severity | Check | Applies to OpenCoffersStep? |
|---|---|---|---|---|
| W1 (missing Expect) | W1 | Warning | `step.Expect is null` | **YES** -- NOT suppressed (per OC-13) |

---

## Given-When-Then test scenarios

### Schema round-trip test (`QuestForge.Schema.Tests/RoundTripTests.cs`)

#### OC_RT1 -- OpenCoffersStep round-trip serialization

**Given:**
- `OpenCoffersStep { Id = "open-coffers", Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" } }`

**When:** Serialize as `Step`, then deserialize back as `Step`.

**Then:**
- Result is `OpenCoffersStep`.
- `Id == "open-coffers"`.
- `Expect` is `PredicateExpect` with `Predicate == "not inventoryHasCoffers()"`.

### Pure helper tests (`QuestForge.Adapters.Tests/Items/CofferIdentifierTests.cs`)

#### OC_CI1 -- ItemAction 1085 + Miscellany 61 is a coffer

**Given:** `itemActionRowId = 1085`, `itemUiCategoryRowId = 61`.

**When:** `CofferIdentifier.IsCoffer(1085, 61)`.

**Then:** Returns `true`.

#### OC_CI2 -- ItemAction 388 + Miscellany 61 is a coffer

**Given:** `itemActionRowId = 388`, `itemUiCategoryRowId = 61`.

**When:** `CofferIdentifier.IsCoffer(388, 61)`.

**Then:** Returns `true`.

#### OC_CI3 -- ItemAction 367 (Triple Triad) + Miscellany 61 is NOT a coffer

**Given:** `itemActionRowId = 367`, `itemUiCategoryRowId = 61`.

**When:** `CofferIdentifier.IsCoffer(367, 61)`.

**Then:** Returns `false`.

#### OC_CI4 -- ItemAction 1085 + wrong category is NOT a coffer

**Given:** `itemActionRowId = 1085`, `itemUiCategoryRowId = 10` (not Miscellany).

**When:** `CofferIdentifier.IsCoffer(1085, 10)`.

**Then:** Returns `false`.

#### OC_CI5 -- ItemAction 0 + any category is NOT a coffer

**Given:** `itemActionRowId = 0`, `itemUiCategoryRowId = 61`.

**When:** `CofferIdentifier.IsCoffer(0, 61)`.

**Then:** Returns `false`.

### Engine tests (`QuestForge.Engine.Tests/Engine/OpenCoffersStepTests.cs`)

All tests follow the established pattern. Quest with one OpenCoffersStep in sequence 0. AcceptStep present to satisfy E4.

#### OC1 -- Happy path, coffer in inventory -- emits OpenCoffer

**Given:**
- Player not casting.
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.GameState.SetFreeInventorySlots(10)` (plenty of space).
- `harness.CofferOpener.SetCofferItemIds([44001u])`.
- `harness.GameState.SetHasCoffers(true)` (predicate reports coffers exist).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.OpenCoffer` with `ItemId == 44001u`, `Origin != null`.

#### OC2 -- No coffers in inventory -- Wait, no OpenCoffer emitted

**Given:**
- Player not casting.
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.GameState.SetFreeInventorySlots(10)`.
- `harness.CofferOpener.SetCofferItemIds([])` (empty -- no coffers).
- `harness.GameState.SetHasCoffers(false)`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"no coffers"`.

#### OC3 -- Player casting -- Wait, no OpenCoffer emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.CofferOpener.SetCofferItemIds([44001u])`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"player casting"`.

#### OC4 -- Inventory full -- AwaitUser

**Given:**
- Player not casting.
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.GameState.SetFreeInventorySlots(0)` (inventory full).
- `harness.CofferOpener.SetCofferItemIds([44001u])`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.AwaitUser` whose `Reason` contains `"inventory full"`.

#### OC5 -- No adapter wired -- AwaitUser

**Given:**
- A `QuestEngine` constructed WITHOUT `cofferOpener` (null default).
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.

**When:** `engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.AwaitUser` whose `Reason` contains `"no ICofferOpener wired"`.

#### OC6 -- Multiple coffers, first one dispatched

**Given:**
- Player not casting.
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.GameState.SetFreeInventorySlots(10)`.
- `harness.CofferOpener.SetCofferItemIds([44001u, 44002u, 44003u])`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.OpenCoffer` with `ItemId == 44001u` (first in list).

#### OC7 -- Multi-tick integration: open all coffers then advance

**Given:**
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.GameState.SetFreeInventorySlots(10)`.
- Initially: `harness.CofferOpener.SetCofferItemIds([44001u, 44002u])`, `harness.GameState.SetHasCoffers(true)`.

**When:**
1. Tick 1 --> `EngineAction.OpenCoffer(44001u)`.
2. `harness.CofferOpener.SetCofferItemIds([44002u])` (44001 opened, removed from list).
3. Tick 2 --> `EngineAction.OpenCoffer(44002u)`.
4. `harness.CofferOpener.SetCofferItemIds([])`, `harness.GameState.SetHasCoffers(false)` (all opened).
5. Tick 3.

**Then:**
- Tick 3: Expect (`not inventoryHasCoffers()`) is true in the cursor walk. Step is confirmed via Expect. Returns `EngineAction.Wait` ("all steps in current sequence satisfied").

#### OC8 -- Mounted + prior Navigate: lazy-dismount DOES fire before OpenCoffer (NOT exempt)

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep to `(200, 0, 0)` with `Expect = "playerZone() == 130"`.
  2. OpenCoffersStep with `Expect = "not inventoryHasCoffers()"`.
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).
- `harness.CofferOpener.SetCofferItemIds([44001u])`.
- `harness.GameState.SetFreeInventorySlots(10)`.
- `harness.GameState.SetHasCoffers(true)`.

**When:**
1. Tick 1 --> `EngineAction.Navigate`. `_lastDispatchedWasNavigate = true`.
2. `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect satisfied).
3. Tick 2.

**Then:**
- Tick 2: lazy-dismount fires because `EngineAction.OpenCoffer` is NOT exempt.
- `harness.Mount.DismountCallCount >= 1`.

Pins Decision OC-6.

#### OC9 -- Standalone + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: OpenCoffersStep with `Expect = "not inventoryHasCoffers()"`.
- Player mounted (`SetMountState(MountState.Mounted)`).
- `harness.CofferOpener.SetCofferItemIds([44001u])`.
- `harness.GameState.SetFreeInventorySlots(10)`.
- `harness.GameState.SetHasCoffers(true)`.

**When:** Tick once.

**Then:**
- Returns `EngineAction.OpenCoffer(44001u)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount only triggers after Navigate).

#### OC10 -- Expect already satisfied (no coffers) -- step skipped in cursor walk

**Given:**
- `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.GameState.SetHasCoffers(false)` (predicate `not inventoryHasCoffers()` evaluates true).
- `harness.CofferOpener.SetCofferItemIds([])`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits in cursor walk; step confirmed; no more steps).
- The step is confirmed (confirmed set contains the step ID).
- `harness.CofferOpener.OpenCofferCalls.Count == 0` (adapter never called).

#### OC11 -- Cancellation propagates

**Given:**
- OpenCoffersStep as OC1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

#### OC12 -- RunToCompletion integration: open two coffers then advance

**Given:**
- Quest: AcceptStep + OpenCoffersStep `{ Expect = PredicateExpect("not inventoryHasCoffers()") }`.
- `harness.GameState.SetFreeInventorySlots(10)`.
- `harness.GameState.SetHasCoffers(true)`.
- `harness.CofferOpener.SetCofferItemIds([44001u, 44002u])`.
- AcceptStep wired to auto-satisfy on interact.
- In the RunToCompletion loop, after each `OpenCoffer(itemId)` dispatch, remove the opened item from the coffer list. After the last coffer is opened, set `SetHasCoffers(false)`.

**When:** `harness.RunToCompletion(maxTicks: 15)`.

**Then:**
- `actions` contains at least two `OpenCoffer` entries.
- `actions[indexOfFirstOpenCoffer].ItemId == 44001u`.
- `actions[indexOfSecondOpenCoffer].ItemId == 44002u`.
- RunToCompletion succeeds (all steps satisfied, sequence-advance Wait returned).

Note: This test requires the EngineTestHarness `RunToCompletion` arm for `OpenCoffer` to call `CofferOpener.OpenCoffer` and update the coffer list. The implementation is:

```csharp
case EngineAction.OpenCoffer oc:
    actions.Add(action);
    EmitActionSubmitted("OpenCoffer",
        JsonSerializer.SerializeToElement(new { itemId = oc.ItemId }, _jsonOpts));
    var ocResult = await CofferOpener.OpenCoffer(oc.ItemId, ct);
    EmitActionCompleted("OpenCoffer",
        ocResult.IsSuccess ? "Done" : "Failed");
    break;
```

### DraftValidator W1 test

#### OC13 -- W1 fires for OpenCoffersStep without Expect

**Given:**
- `QuestDraft` with `OpenCoffersStep { Expect = null }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `warnings` contains an entry with `Code == "W1"` for the open-coffers step.

#### OC14 -- W1 does NOT fire for OpenCoffersStep with Expect

**Given:**
- `QuestDraft` with `OpenCoffersStep { Expect = PredicateExpect("not inventoryHasCoffers()") }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `warnings` does NOT contain any entry with `Code == "W1"` for the open-coffers step.

### Predicate test (`QuestForge.Engine.Tests/Predicates/PredicateEvaluatorTests.cs`)

#### OC_P1 -- inventoryHasCoffers() returns true when coffers exist

**Given:**
- `harness.GameState.SetHasCoffers(true)`.

**When:** Evaluate predicate `"inventoryHasCoffers()"`.

**Then:** Result is `true`.

#### OC_P2 -- inventoryHasCoffers() returns false when no coffers

**Given:**
- `harness.GameState.SetHasCoffers(false)`.

**When:** Evaluate predicate `"inventoryHasCoffers()"`.

**Then:** Result is `false`.

#### OC_P3 -- not inventoryHasCoffers() evaluates correctly

**Given:**
- `harness.GameState.SetHasCoffers(true)`.

**When:** Evaluate predicate `"not inventoryHasCoffers()"`.

**Then:** Result is `false`.

---

## Implementation order

### Phase A -- Schema + Adapter interfaces + Pure helper (45 min)

1. Add `OpenCoffersStep` to `Step.cs` (sealed empty class, discriminator `"open-coffers"`).
2. Add `[JsonSerializable(typeof(OpenCoffersStep))]` to `QuestForgeJsonContext.cs`.
3. Add `CofferIdentifier.cs` to `QuestForge.Adapters/Items/`.
4. Add `ICofferOpener.cs` to `QuestForge.Adapters/Items/`.
5. Add `HasCoffers(CancellationToken ct)` to `IGameStateProvider`.
6. Add `FakeCofferOpener.cs` to `QuestForge.Adapters.Fakes/Items/`.
7. Add `SetHasCoffers(bool)` + `HasCoffers(ct)` to `FakeGameStateProvider`.
8. Schema round-trip test OC_RT1.
9. Pure helper tests OC_CI1-OC_CI5.

**Done before B:** `dotnet build` succeeds. Round-trip + CofferIdentifier tests green.

### Phase B -- Engine dispatch + predicate (1 hour)

1. Append `EngineAction.OpenCoffer(uint ItemId, Step? Origin = null)` to `EngineAction.cs`.
2. Add `ICofferOpener? _cofferOpener` optional ctor param to `QuestEngine`.
3. Add `ResolveOpenCoffers` method to `QuestEngine.cs` per Decision OC-4.
4. Add dispatch arm (6a9) to the step-dispatch switch.
5. Add `"inventoryHasCoffers"` to `PredicateEvaluator.EvaluateFunction`.
6. **Tester writes OC1-OC6, OC10, OC11** (single-tick dispatch tests). Red until builder implements steps 1-5.
7. **Tester writes OC_P1-OC_P3** (predicate tests). Red until builder implements step 5.

**Done before C:** Engine + predicate tests green. `dotnet test QuestForge.Engine.Tests` passes.

### Phase C -- EngineTestHarness wiring + integration tests (30 min)

1. Add `FakeCofferOpener CofferOpener` property to `EngineTestHarness`.
2. Pass `cofferOpener: CofferOpener` to `QuestEngine` ctor in harness.
3. Add `case EngineAction.OpenCoffer oc:` arm to `RunToCompletion`.
4. **Tester writes OC7, OC8, OC9, OC12** (multi-tick, mount, RunToCompletion).
5. **Tester writes OC13, OC14** (W1 validator tests). Green immediately (no code change needed).

**Done before D:** All engine tests green.

### Phase D -- DalamudCofferOpener + EngineHost (45 min)

1. Add `DalamudCofferOpener.cs` to `QuestForge.Adapters.Dalamud/Items/`.
2. Add `HasCoffers(ct)` to `DalamudGameStateProvider` (Lumina scan + `CofferIdentifier.IsCoffer`).
3. Add `_cofferOpener` field + ctor + BeginRun passthrough to `EngineHost`.
4. Add dispatch arm to `EngineHost.DispatchAction` per Decision OC-12.
5. Add `ICofferOpener DebugCofferOpener => _cofferOpener;` per Decision OC-15.

**Done before E:** `dotnet build QuestForge.Plugin` succeeds.

### Phase E -- Tooling catch-up (paired PR, 30 min)

1. Add `[typeof(OpenCoffersStep)] = "step:open-coffers"` to `CapabilityInferrer.StepCapabilities`.
2. Add `ActionOpenCoffer = "opencoffer"` to `TraceConstants.cs`.
3. Add FilenameLookup entry per Decision OC-19.
4. Add DistinguishingCapPriority entry per Decision OC-19.
5. Add `new("inventoryHasCoffers", new Fixed(0), [], Bool)` to `FunctionRegistry`.
6. Write tests for the new entries in `QuestForge.Tools.Trace.Tests/`.
7. Update `docs/FIXTURES.md` with `step:open-coffers` and `"opencoffer"` rows.

**Done:** `dotnet test` in both repos green.

**Total estimated time: ~3.5 hours across schema, engine, Dalamud, and tooling.**

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~OpenCoffersStepTests` reports all 12 engine tests green (OC1-OC12).
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~DraftValidator` reports OC13 and OC14 green (W1 behavior).
3. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~PredicateEvaluator` reports OC_P1-OC_P3 green.
4. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~CofferIdentifier` reports OC_CI1-OC_CI5 green.
5. Schema round-trip test OC_RT1 green.
6. A quest with `{ "type": "open-coffers", "id": "open-coffers", "expect": "not inventoryHasCoffers()" }` dispatches `EngineAction.OpenCoffer` from `QuestEngine`.
7. The step advances only when the authored `Expect` evaluates true. Without `Expect`, W1 warns the author.
8. `DalamudCofferOpener.OpenCoffer` calls `ActionManager.UseAction(ActionType.Item, itemId, 0xE0000000, 65535)`.
9. `DalamudCofferOpener.GetCofferItemIds` scans inventory bags 1-4 and filters via `CofferIdentifier.IsCoffer`.
10. `EngineHost.DispatchAction` has a `case EngineAction.OpenCoffer` arm that calls `_cofferOpener.OpenCoffer`.
11. `inventoryHasCoffers()` predicate evaluates to `true` when `IGameStateProvider.HasCoffers` returns true.
12. Lazy-dismount fires before `OpenCoffer` (not exempt from dismount).
13. `dotnet build` succeeds in both `questforge` and `questforge-tools` repos with no `TreatWarningsAsErrors` regressions.
14. `questforge-tools` TraceConstants has `ActionOpenCoffer = "opencoffer"`.
15. `questforge-tools` FunctionRegistry has `inventoryHasCoffers` with `Fixed(0)`, `[]`, `Bool`.
16. `questforge-tools` CapabilityInferrer has `[typeof(OpenCoffersStep)] = "step:open-coffers"`.
17. No regression in existing tests.

---

## Exclusions

- **Authoring inference.** Per user decision 8: always explicitly authored. No polling signal for "player opened a coffer."
- **Implicit postcondition.** No self-confirm when coffer list is empty. Relies on authored `Expect`.
- **Gearset switching.** Open coffers as current job (user decision 9).
- **Coffer content inspection.** The engine does not inspect what gear is inside the coffer. It opens all coffers blindly.
- **Auto-equip after opening.** Opening coffers only reveals gear in inventory. Equipping it is a separate `EquipGearForQuestStep` or `EquipBestGearStep`.
- **In-game smoke test.** Manual Slice 4 verification (not part of this CI-focused plan).
- **Per-coffer item tracking.** The `FakeCofferOpener.SetCofferItemIds` list is the full list each tick. The test manually removes opened coffers between ticks. No automatic "remove on open" in the fake.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 3 scenarios (OC1, OC7, OC12)
- Edge cases: 5 scenarios (OC2, OC6, OC8, OC9, OC10)
- Error / wait cases: 3 scenarios (OC3, OC4, OC5)
- Cancellation: 1 scenario (OC11)
- Validator: 2 scenarios (OC13, OC14)
- Predicate: 3 scenarios (OC_P1-OC_P3)
- Pure helper: 5 scenarios (OC_CI1-OC_CI5)
- Schema round-trip: 1 scenario (OC_RT1)
- Expected total:
  - `QuestForge.Engine.Tests/Engine/OpenCoffersStepTests.cs`: 12 tests (OC1-OC12)
  - `QuestForge.Engine.Tests/Authoring/DraftValidatorOpenCoffersTests.cs`: 2 tests (OC13-OC14)
  - `QuestForge.Engine.Tests/Predicates/PredicateEvaluatorTests.cs`: 3 tests (OC_P1-OC_P3, appended to existing file)
  - `QuestForge.Schema.Tests/RoundTripTests.cs`: 1 test (OC_RT1, appended to existing file)
  - `QuestForge.Adapters.Tests/Items/CofferIdentifierTests.cs`: 5 tests (OC_CI1-OC_CI5)
  - Grand total: ~23 tests
