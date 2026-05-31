# DalamudGearEquipper Implementation Spec

**Status:** ready for implementation
**Scope:** Flesh out `DalamudGearEquipper` from its stub. This is a Dalamud-only shell with no new pure logic to extract (the pure helper `EquipSlotResolver` is already implemented and tested).
**Parent plan:** `docs/EQUIP_GEAR_FOR_QUEST_STEP_PLAN.md` Decision EG-7
**Input docs:**
- `docs/GEAR_RESEARCH.md` Section 2.1 (MoveItemSlot mechanism, container search order)
- `QuestForge.Adapters/Gear/IGearEquipper.cs` (2-method interface)
- `QuestForge.Adapters/Gear/EquipSlotResolver.cs` (pure helper, already done)
- `QuestForge.Adapters.Dalamud/Gear/DalamudGearEquipper.cs` (current stub)
- `QuestForge.Adapters.Dalamud/Items/DalamudItemUser.cs` (closest analog)
- FFXIVClientStructs: `InventoryManager`, `InventoryItem`, `InventoryType` enum

**Scope guard:** This spec covers ONLY the `DalamudGearEquipper` class. It does NOT cover:
- Engine dispatch (`QuestEngine.ResolveEquipGear`) -- already designed in EG-3
- `EngineHost` dispatch arm -- already designed in EG-8
- Authoring inference -- already designed in EG-10
- Tooling catch-up -- already designed in EG-11
- `EquipSlotResolver` -- already implemented and tested

---

## Decisions

### DGE-1: Container search order

**Decision:** Search for the target item across containers in the Questionable-established order (from GEAR_RESEARCH.md Section 2.1):

```csharp
private static readonly InventoryType[] SearchOrder =
[
    InventoryType.ArmoryMainHand,   // 3500
    InventoryType.ArmoryOffHand,    // 3200
    InventoryType.ArmoryHead,       // 3201
    InventoryType.ArmoryBody,       // 3202
    InventoryType.ArmoryHands,      // 3203
    InventoryType.ArmoryLegs,       // 3205
    InventoryType.ArmoryFeets,      // 3206
    InventoryType.ArmoryEar,        // 3207
    InventoryType.ArmoryNeck,       // 3208
    InventoryType.ArmoryWrist,      // 3209
    InventoryType.ArmoryRings,      // 3300
    InventoryType.ArmorySoulCrystal,// 3400
    InventoryType.Inventory1,       // 0
    InventoryType.Inventory2,       // 1
    InventoryType.Inventory3,       // 2
    InventoryType.Inventory4,       // 3
];
```

Armoury containers are searched first because items there are already designated as equipment. Player inventory is the fallback.

**Note:** `InventoryType.EquippedItems` (1000) is NOT in the search list. If the item is already equipped, `IsItemEquipped` returns true and the engine never calls `EquipItem`. If somehow `EquipItem` is called for an already-equipped item, the search will not find it in armoury/inventory, and the method returns `EquipOutcome.NoChange` (see DGE-5).

### DGE-2: Lumina lookup for EquipSlotCategory

**Decision:** At construction time, do NOT preload the entire Item sheet. Instead, perform a single-row lookup per `EquipItem` call using `_svc.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()` and indexing by `itemId`.

```csharp
var itemSheet = _svc.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
var itemRow = itemSheet?.GetRowOrDefault(itemId);
if (itemRow is null)
    return Result.Ok(EquipOutcome.ItemNotFound);

var equipSlotCategory = itemRow.Value.EquipSlotCategory.RowId;
```

**Rationale:** Unlike emotes (where we preload all ~200 rows for O(1) lookup), the Item sheet has ~40,000 rows. Preloading is wasteful. Single-row lookup via `GetRowOrDefault` is O(1) in Lumina's internal dictionary and is called at most once per engine tick.

**What breaks if violated:** Preloading 40K rows at construction adds ~50-100ms startup cost and ~10MB memory for data QuestForge rarely queries. The per-call lookup is negligible.

### DGE-3: Ring slot handling

**Decision:** When `EquipSlotResolver.GetTargetSlot` returns slot 11 (right ring, from EquipSlotCategory 12), check whether the right ring slot already holds the same unique item. If so, use slot 12 (left ring) instead.

The "same unique item" check compares `ItemId` at `EquippedItems` slot 11 against the item being equipped. This handles the case where the player already has one copy of a ring equipped in the right slot and the quest requires equipping a second copy in the left slot.

```csharp
if (targetSlot == 11) // right ring
{
    var rightRingItem = im->GetInventorySlot(InventoryType.EquippedItems, 11);
    if (rightRingItem is not null && rightRingItem->ItemId == itemId)
        targetSlot = 12; // fall back to left ring
}
```

**Why not "prefer empty slot":** Quest-required ring equips always target a specific item ID. If the right ring slot holds a different item, we want to replace it (the swap is handled by `MoveItemSlot` with `a6=true`). Only when the right ring holds the *same* item ID do we need the left slot.

### DGE-4: EquipItem implementation (full flow)

```
unsafe EquipItem(uint itemId, CancellationToken ct):
  1. ct.ThrowIfCancellationRequested()
  2. Get InventoryManager.Instance() -- if null, return Result.Fail("inventoryManagerUnavailable")
  3. Lumina lookup: get EquipSlotCategory for itemId (see DGE-2)
     - If item row not found: return Result.Ok(EquipOutcome.ItemNotFound)
  4. EquipSlotResolver.GetTargetSlot(equipSlotCategory)
     - If null (unmappable category): return Result.Ok(EquipOutcome.Failed)
  5. Ring slot adjustment (see DGE-3)
  6. Search containers (see DGE-1) for itemId:
     - For each container in SearchOrder:
       - GetInventoryContainer(containerType) -- skip if null
       - Iterate slots 0..container->Size-1:
         - GetInventorySlot(containerType, slotIndex) -- skip if null
         - If slot->ItemId == itemId: found = (containerType, slotIndex); break both loops
     - If not found: return Result.Ok(EquipOutcome.ItemNotFound)
  7. MoveItemSlot(srcContainer, srcSlot, InventoryType.EquippedItems, (ushort)targetSlot, true)
  8. Return Result.Ok(EquipOutcome.Equipped)
```

**Return type note:** `MoveItemSlot` returns `int`. The return value is not well-documented in ClientStructs. We do not interpret it -- we always return `EquipOutcome.Equipped` after the call. The engine's implicit postcondition (`IsItemEquipped` check on the next tick) is the authoritative success verification, matching the postcondition-discipline pattern.

**Why `a6 = true`:** The last parameter enables swap behavior. Without it, moving an item to an occupied equipment slot may fail silently. With `true`, the existing equipped item is swapped to the source container. This matches Questionable's and Stylist's usage.

### DGE-5: IsItemEquipped implementation

```
unsafe IsItemEquipped(uint itemId, CancellationToken ct):
  1. ct.ThrowIfCancellationRequested()
  2. Get InventoryManager.Instance() -- if null, return Result.Fail("inventoryManagerUnavailable")
  3. Scan EquippedItems container (14 slots):
     - For slotIndex 0..13:
       - GetInventorySlot(InventoryType.EquippedItems, slotIndex)
       - If slot is not null and slot->ItemId == itemId: return Result.Ok(true)
  4. Return Result.Ok(false)
```

**Why iterate manually instead of `GetItemCountInContainer`:** `GetItemCountInContainer` exists on `InventoryManager`, but `IsItemEquipped` is a boolean question. Iterating 14 slots with pointer reads is trivial and avoids any ambiguity about whether `GetItemCountInContainer` counts HQ/NQ variants separately.

### DGE-6: Error handling and EquipOutcome mapping

| Condition | Return value | Rationale |
|---|---|---|
| `InventoryManager.Instance()` is null | `Result.Fail("inventoryManagerUnavailable", ...)` | Genuine system error, not routine. Uses `Fail` not `Ok(Failed)`. |
| Lumina Item row not found for itemId | `Result.Ok(EquipOutcome.ItemNotFound)` | Routine: bad quest data or item removed by patch. |
| `EquipSlotResolver.GetTargetSlot` returns null | `Result.Ok(EquipOutcome.Failed)` | Routine: item has an unmappable EquipSlotCategory (furniture, consumable, etc.). |
| Item not found in any searched container | `Result.Ok(EquipOutcome.ItemNotFound)` | Routine: player doesn't have the item. |
| `MoveItemSlot` called successfully | `Result.Ok(EquipOutcome.Equipped)` | Engine verifies actual success via `IsItemEquipped` on next tick. |

**Unused `EquipOutcome` values for this method:** `InCombat` and `InInstance` are not returned by the adapter. The engine's pre-arm (EG-3) handles these guards before the adapter is called. The enum values exist for potential future use by `IBestGearEquipper` or other gear adapters.

### DGE-7: Constructor -- store PluginServices, no preloading

```csharp
public sealed class DalamudGearEquipper : IGearEquipper
{
    private readonly PluginServices _svc;

    public DalamudGearEquipper(PluginServices svc) => _svc = svc;
    // ... methods
}
```

No Lumina preloading (see DGE-2 rationale). No state fields. Both methods are stateless -- each call does its own lookup. This is the same pattern as `DalamudItemUser` and `DalamudActionExecutor`.

### DGE-8: No additional pure logic to extract

**Explicit statement per TDD-even-for-adapters rule:** The only pure logic in gear equipping is the `EquipSlotCategory -> slot index` mapping, which is already extracted as `EquipSlotResolver.GetTargetSlot` and tested in `QuestForge.Adapters.Tests/Gear/EquipSlotResolverTests.cs`.

The remaining logic is:
- Container iteration (requires `InventoryManager` pointers -- Dalamud-only)
- Lumina row lookup (requires `IDataManager` -- Dalamud-only)
- `MoveItemSlot` call (unsafe FFXIVClientStructs -- Dalamud-only)
- `EquippedItems` slot scan (requires `InventoryManager` pointers -- Dalamud-only)

There is no new pure function to extract. The ring-slot fallback (DGE-3) is three lines of pointer comparison and does not warrant a separate testable unit.

---

## Test scenarios

Since all logic is Dalamud-only (unsafe pointer operations against game memory), there are no new unit tests to write. The existing test coverage is:

- **`EquipSlotResolverTests`** (already passing): covers the pure `GetTargetSlot` mapping for all EquipSlotCategory values including edge cases (0, 14-16, 18+, ring, two-hand, soul crystal).
- **`EquipGearForQuestStepTests`** (from parent plan EG-1 through EG-12): engine-level tests against `FakeGearEquipper` that verify dispatch, implicit postcondition, pre-flight guards, multi-item sequencing, and dismount exemption.

**In-game smoke test (Slice 4 from parent plan):** The in-game verification covers:
1. Single item equip -- item moves from armoury to equipment slot.
2. Item found in player inventory (not armoury) -- still equips correctly.
3. Ring equip -- goes to right ring slot.
4. Second ring of same ID -- goes to left ring slot when right already has one.
5. Item not in inventory -- engine logs the `ItemNotFound` outcome and retries next tick.
6. Item already equipped -- `IsItemEquipped` returns true, engine self-confirms, no `EquipItem` call.
7. Equipment swap -- existing item in the target slot moves to armoury/inventory (swap via `a6=true`).

---

## Implementation checklist

1. Add `using` directives: `Lumina.Excel.Sheets`, `FFXIVClientStructs.FFXIV.Client.Game`.
2. Add `SearchOrder` static array (DGE-1).
3. Implement `EquipItem` as `unsafe` method following DGE-4 flow.
4. Implement `IsItemEquipped` as `unsafe` method following DGE-5 flow.
5. Verify the build compiles with `dotnet build QuestForge.Adapters.Dalamud`.
6. Smoke test in-game per the scenarios above.
