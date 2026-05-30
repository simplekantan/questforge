# Gear-Equip Mechanics and Stylist IPC Research

**Status:** Research spike for issue #116
**Date:** 2026-05-30
**Scope:** Read-only analysis of Stylist IPC, FFXIVClientStructs gear APIs, Questionable behavior, and QuestForge IGearManager audit

---

## Section 1: Stylist IPC Findings

### 1.1 IPC Gate Inventory

Stylist's IPC is defined in `Stylist/Services/IpcProvider.cs` using ECommons' `[EzIPC]` attribute convention. The prefix is `"Stylist"` (from `Stylist.json` InternalName). Each method name becomes `Stylist.<MethodName>`.

| IPC Gate Name | Signature | Behavior |
|---|---|---|
| `Stylist.UpdateGearsetIfNeededEx` | `Action<int, bool?, bool?>` | Updates gearset at `gearsetIndex` with best-available items, optionally pulling from player inventory. `moveItemsFromInventory`: null = respect user config. `shouldEquip`: true = always equip afterward, false = never equip, null = use user prefs. |
| `Stylist.UpdateGearsetIfNeeded` | `Action<int, bool?>` | **Obsolete.** Delegates to `UpdateGearsetIfNeededEx(gearsetIndex, moveItemsFromInventory, null)`. |
| `Stylist.UpdateCurrentGearsetEx` | `Action<bool?, bool?>` | Same as `UpdateGearsetIfNeededEx` but operates on `RaptureGearsetModule.CurrentGearsetIndex`. No-ops if no valid gearset is currently equipped (index == 255 or invalid). |
| `Stylist.UpdateCurrentGearset` | `Action<bool?>` | **Obsolete.** Delegates to `UpdateCurrentGearsetEx(moveItemsFromInventory, null)`. |
| `Stylist.IsBusy` | `Func<bool>` | Returns `P.TaskManager.IsBusy`. True while Stylist's internal task queue (item moves, re-equip) is still processing. |

**Total: 5 IPC gates (2 deprecated, 3 active).**

### 1.2 What "Equip Optimized Gear" Means in Stylist

Stylist does NOT use the game's `RecommendEquipModule`. Instead, it implements its own gear-scoring algorithm in `Utils.GetBestItemForJob()`:

1. **Scans all armoury chest containers + optionally player inventory** for items equippable by the target job.
2. **Filters by level**: items above the player's current level for that job are excluded.
3. **Primary sort: item level** (higher wins).
4. **Tiebreaker: weighted stat comparison** using per-role stat priority tables (e.g., tanks: STR > DET > TEN > DHR > CRIT > SKS; healers: MND > PIE > SPS > DET > CRIT > SPS).
5. **Forced items**: A hardcoded list of special items (Brand-new equipment at specific level thresholds) gets priority when their conditions are met.
6. **Ring deduplication**: Right ring is chosen first; left ring excludes the already-chosen right ring descriptor.
7. **Inventory-to-armoury move**: If the best item is in player inventory (not armoury chest), Stylist queues an `ItemMover` operation to move it to the appropriate armoury container via `InventoryManager.MoveItemSlot()` before equipping.
8. **Gearset mutation**: Stylist directly writes to `GearsetEntry.Items[]` fields in memory, updating ItemId, GlamourId, Stains, Materia, and MateriaGrades. Then calls `RaptureGearsetModule.EquipGearset()` to apply the modified gearset.
9. **Asynchronous completion**: The equip and item-move operations are queued in Stylist's TaskManager. `IsBusy` returns true until all queued operations complete.

**Key insight**: Stylist does NOT equip items directly. It modifies the gearset entry in memory, then calls the native `EquipGearset()` to apply it. This means it always works through the gearset system.

### 1.3 Availability Detection

- **Check if Stylist is installed**: Call `Stylist.IsBusy`. If the IPC gate doesn't exist (throws `IpcNotReadyError` or `IpcError`), Stylist is not installed.
- **Questionable's pattern**: Wraps the call in try/catch for `IpcError`, logs once on first failure, then silently degrades. This is the established Dalamud ecosystem pattern.
- **No version check IPC**: Stylist does not expose a version gate. The `UpdateGearsetIfNeededEx` vs `UpdateGearsetIfNeeded` split is the only versioning — older Stylist versions may not have the `Ex` variants.

### 1.4 Caveats

- **Combat**: Stylist itself does no combat check. However, `RaptureGearsetModule.EquipGearset()` at the game level will fail if in combat. The caller must gate this.
- **Instances**: No explicit instance check in Stylist. Gear changes generally work inside instances (the game allows it), but item moves from inventory to armoury may behave differently.
- **Cutscenes**: Stylist does no cutscene check. Gear changes during cutscenes are undefined behavior.
- **Busy polling**: After calling `UpdateCurrentGearsetEx`, the caller must poll `IsBusy` until it returns false before assuming the operation completed. Stylist's task queue processes item moves one per framework tick with throttling.
- **Blacklisted gearsets**: Stylist respects a per-character blacklist. If the target gearset is blacklisted, the update silently no-ops.

---

## Section 2: Native Game Mechanisms (No Stylist)

### 2.1 Equip Specific Item

**Primary mechanism: `InventoryManager.MoveItemSlot()`**

```csharp
// FFXIVClientStructs signature:
public partial int MoveItemSlot(
    InventoryType srcContainer, ushort srcSlot,
    InventoryType dstContainer, ushort dstSlot,
    bool a6 = false);
```

To equip a specific item:
1. **Find the item** by scanning armoury containers and player inventory for the target ItemId.
2. **Determine the target equipment slot** from the item's `EquipSlotCategory` row in the Lumina Item sheet. The mapping (from Questionable's `EquipItem.GetEquipSlot()`):
   - EquipSlotCategory 1-11 map to equipment slot index 0-10 (MainHand through Wrist)
   - EquipSlotCategory 12 (Ring) maps to slots 11 or 12 (right ring / left ring)
   - EquipSlotCategory 13 (two-hand weapon) maps to slot 0 (MainHand)
   - EquipSlotCategory 17 (soul crystal) maps to slot 13
3. **Call `MoveItemSlot(srcContainer, srcSlot, InventoryType.EquippedItems, targetSlot, true)`**. The last parameter (`true`) is important for equipment swaps.
4. **Verify** by reading back the equipped items container.

**Search order for source item** (from Questionable, which is the established pattern):
```
ArmoryMainHand, ArmoryOffHand, ArmoryHead, ArmoryBody, ArmoryHands,
ArmoryLegs, ArmoryFeets, ArmoryEar, ArmoryNeck, ArmoryWrist,
ArmoryRings, ArmorySoulCrystal,
Inventory1, Inventory2, Inventory3, Inventory4
```

**Important**: There is no single "EquipItem(itemId)" function in ClientStructs. Equipping is always a container-move operation. The caller must locate the item and determine the target slot.

### 2.2 Equip Recommended Gear (Native)

**`RecommendEquipModule` in ClientStructs:**

```csharp
public unsafe partial struct RecommendEquipModule {
    public static RecommendEquipModule* Instance();
    public bool IsUpdating;
    public bool IsSetupForDifferentClassJob;
    public FixedSizeArray14<Pointer<InventoryItem>> RecommendedItems;

    public partial bool SetupForClassJob(byte classJobId);
    public partial void EquipRecommendedGear();
    public partial void Clear();
}
```

**How it works** (observed from Questionable's `EquipRecommended`):
1. Call `SetupForClassJob(currentClassJobId)` to compute recommended items.
2. Poll `IsUpdating` until false (the computation is asynchronous).
3. Optionally check if all recommended items are already equipped (compare `RecommendedItems` against `EquippedItems` container).
4. Call `EquipRecommendedGear()` to execute the swap.
5. Wait ~1 second for the game to process the equip.

**Comparison to Stylist**: The native recommended gear algorithm is simpler (no stat weighting, no forced items, no inventory pulling). It only considers items already in the armoury chest. For leveling purposes, it's adequate. For endgame stat optimization, Stylist is better.

### 2.3 Gearset Operations

**`RaptureGearsetModule` in ClientStructs** provides a complete gearset API:

| Method | Signature | Purpose |
|---|---|---|
| `Instance()` | `RaptureGearsetModule*` | Singleton access |
| `GetGearset(int id)` | `GearsetEntry*` | Read gearset by index (0-99) |
| `IsValidGearset(int id)` | `bool` | Check if gearset exists and is within player's allowed range |
| `EquipGearset(int id, byte glamourPlateId = 0)` | `int` (0 = success, -1 = fail) | Equip a saved gearset |
| `CreateGearset()` | `int` (id or 255 = fail) | Save current gear as new gearset |
| `UpdateGearset(int id)` | `int` | Overwrite gearset with currently equipped gear |
| `DeleteGearset(int id)` | `void` | Delete a gearset |
| `FindGearsetIdByName(Utf8String*)` | `int` | Case-sensitive prefix search |
| `CurrentGearsetIndex` | `int` field | Currently equipped gearset (-1 or 255 if none) |
| `NumGearsets` | `byte` field | Count of gearsets |

**`GearsetEntry` structure:**
- `Id` (byte): gearset index
- `ClassJob` (byte): the job/class ID for this gearset
- `ItemLevel` (short): cached average item level
- `Flags` (GearsetFlag): Exists, MainHandMissing, HeadgearVisible, WeaponsVisible, VisorEnabled
- `Items` (FixedSizeArray14\<GearsetItem\>): 14 equipment slots (MainHand through SoulStone, including Belt which is legacy/unused)
- `Name` (FixedSizeArray48\<byte\>): gearset name as UTF-8

**`GearsetItemIndex` enum** maps slot positions: MainHand=0, OffHand=1, Head=2, Body=3, Hands=4, Belt=5 (legacy), Legs=6, Feet=7, Ears=8, Neck=9, Wrists=10, RingRight=11, RingLeft=12, SoulStone=13.

### 2.4 Job/Class Change

Job changing in FFXIV is accomplished by **equipping a gearset for the target job**. There is no separate "change job" API.

**Questionable's approach** (in `SwitchClassJob.cs`):
1. Check `PlayerState.Instance()->CurrentClassJobId` to see if already on the target job.
2. Iterate all 100 gearset slots to find one with `ClassJob == targetJobId`.
3. Call `RaptureGearsetModule.EquipGearset(gearsetEntry.Id)`.
4. If no gearset found, throw an error (Questionable does not auto-create gearsets).

**Without a gearset** (theoretical, not recommended):
- Equip the appropriate weapon (changes class).
- Equip the soul crystal (upgrades class to job, e.g., Gladiator -> Paladin).
- This would require knowing the weapon/crystal item IDs and using `MoveItemSlot()`.
- Not worth implementing. If the user has unlocked a job, they have a gearset.

**`PlayerState` relevant fields:**
```csharp
[FieldOffset(0x7E)] public byte CurrentClassJobId;  // Row ID of ClassJob sheet
```

### 2.5 Repair Mechanisms

**`RepairManager` in ClientStructs:**

```csharp
public unsafe partial struct RepairManager {
    public static partial RepairManager* Instance();

    // Repair a single item
    public partial bool RepairItem(
        InventoryType itemToRepairInventory,
        ushort itemToRepairSlot,
        bool isNpc);

    // Repair all items in a category
    public partial bool RepairAllItems(
        bool isNpc,
        int inventoryIndex,  // 7 = Equipped, 0 = Main/Off, 1 = Head/Body/Hands, ...
        byte arg0);

    // Repair all equipped items
    public partial bool RepairEquipped(int inventoryTypeEquipped, bool isNpc, byte arg0);
    public bool RepairEquipped(bool isNpc) => RepairEquipped(1000, isNpc, 0);
}
```

**NPC repair**: Requires being near a mender NPC with the repair dialog open. The `isNpc = true` path charges gil.

**Self-repair**: Available to all DoH classes since patch 2.28 (no class switch needed). Consumes Dark Matter. `isNpc = false` path. Works inside instances. Can exceed 100% condition (up to 199%).

**For QuestForge**: Repair is tracked in issue #120 and is independent of the gear-equip steps. The `RepairManager` API is straightforward. The harder problem is finding the nearest mender NPC, which requires a Lumina-derived NPC registry (already noted in ADAPTERS.md section 12.3).

### 2.6 Gear Condition Reading

`InventoryItem` has a `Condition` field (percentage, where 30000 = 100%). Reading equipped gear condition requires iterating `InventoryType.EquippedItems` and reading each item's condition value.

---

## Section 3: IGearManager Audit

### 3.1 Method-to-Step-Type Mapping

| Method | Used By | Notes |
|---|---|---|
| `GetEquippedGear()` | Inspection/authoring | Could be useful for authoring inference |
| `GetAvailableGear(job)` | None currently | Speculative. Stylist handles this internally |
| `GetAverageItemLevel()` | Potential precondition check | Low priority |
| `IsItemEquipped(item, slot)` | `equip-gear-for-quest` postcondition | Needed |
| `GearsetExistsForJob(job)` | `change-job` pre-check | Needed |
| `EquipItem(item, slot)` | `equip-gear-for-quest` | Needed |
| `EquipRecommendedGear()` | `equip-best-gear` (vanilla path) | Needed |
| `EquipBestGearViaStylist()` | `equip-best-gear` (Stylist path) | Needed |
| `IsStylistAvailable()` | `equip-best-gear` dispatch | Needed |
| `ApplyGearset(gearsetId)` | `change-job` internal | Indirect; `ChangeToJob` uses it |
| `ChangeToJob(job)` | `change-job` | Needed |
| `GetEquippedGearCondition()` | Auto-repair inspection | Repair scope (issue #120) |
| `GetLowestEquippedCondition()` | Auto-repair threshold check | Repair scope (issue #120) |
| `RepairAtNpc(npc)` | Auto-repair | Repair scope (issue #120) |
| `RepairWithSelf()` | Auto-repair | Repair scope (issue #120) |
| `CanRepairWithSelf()` | Auto-repair fallback check | Repair scope (issue #120) |

### 3.2 Speculative Methods

- `GetAvailableGear(JobId, ct)` — No consumer in any step type. Stylist handles gear scoring internally; the engine never needs to enumerate available gear. **Recommend removing.**
- `GetEquippedGear(ct)` — No step type calls this directly. Authoring inference could use a cheaper `GetEquippedItemIds()` instead. **Recommend deferring to authoring phase.**
- `GetAverageItemLevel(ct)` — Could be useful for precondition checks, but no step currently uses it. **Recommend deferring.**

### 3.3 Interface Split Recommendation

**Split IGearManager into focused interfaces**, matching the established pattern (`IItemUser`, `IActionExecutor`, `IEmoteExecutor`, `IChatSender`):

#### `IGearEquipper` — equip-gear-for-quest step

```csharp
public interface IGearEquipper
{
    Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct);
    Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct);
}
```

**Key change**: Remove `EquipSlot` from the signature. The adapter should determine the target slot from the item's EquipSlotCategory (Lumina lookup), not the caller. This matches Questionable's pattern and eliminates a class of authoring errors.

**Key change**: Use `uint itemId` directly instead of `ItemId` wrapper for the item parameter, consistent with how `UseItemStep.ItemId` is `uint`. (Or keep `ItemId` wrapper — this is a minor consistency call. The current codebase uses `ItemId` in the adapter but `uint` in schema.)

#### `IBestGearEquipper` — equip-best-gear step

```csharp
public interface IBestGearEquipper
{
    Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct);
    Task<Result<bool>> IsStylistAvailable(CancellationToken ct);
}
```

**Rationale**: The engine calls `EquipBestGear()` which internally checks configuration (vanilla vs Stylist), tries Stylist if available and preferred, falls back to native `RecommendEquipModule` otherwise. The engine does NOT need to know about the strategy — that's an adapter-internal concern.

`IsStylistAvailable` remains exposed so the engine can log/report which path was used, but the dispatch decision lives inside the adapter.

#### `IJobChanger` — change-job step

```csharp
public interface IJobChanger
{
    Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct);
    Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct);
}
```

**Rationale**: `ChangeToJob` finds the gearset and calls `EquipGearset` internally. `GearsetExistsForJob` is a pre-check so the engine can produce a useful error before attempting the change.

#### `IGearConditionInspector` + `IGearRepairer` — repair (issue #120, future)

```csharp
public interface IGearConditionInspector
{
    Task<Result<int>> GetLowestEquippedCondition(CancellationToken ct);
}

public interface IGearRepairer
{
    Task<Result<RepairOutcome>> RepairEquipped(bool preferSelfRepair, CancellationToken ct);
    Task<Result<bool>> CanSelfRepair(CancellationToken ct);
}
```

**Simplification**: Collapse `RepairAtNpc` + `RepairWithSelf` into `RepairEquipped(bool preferSelfRepair)`. The adapter handles mender-NPC finding and fallback internally. The engine should not need to know about mender NPCs — that's navigation/world-knowledge that belongs in the adapter.

**Not implemented now** — tracked in issue #120.

### 3.4 Summary of Recommendation

| Current | Proposed | Step Type |
|---|---|---|
| `IGearManager` (15 methods) | `IGearEquipper` (2 methods) | `equip-gear-for-quest` |
| | `IBestGearEquipper` (2 methods) | `equip-best-gear` |
| | `IJobChanger` (2 methods) | `change-job` |
| | `IGearConditionInspector` (1 method) | Auto-repair precondition (future) |
| | `IGearRepairer` (2 methods) | Auto-repair action (future) |

**What breaks with the monolith**: Nothing is broken today (everything is a stub). But keeping the monolith means:
- Every step type's fake must implement 15 methods, most of which it doesn't use.
- The engine constructor accumulates parameters (already at ~10 adapters).
- Tests for `equip-gear-for-quest` must construct a full `FakeGearManager` with repair stubs.

**Migration path**: Since `DalamudGearManager` and `FakeGearManager` are pure stubs today, the split is free. No behavioral code to migrate.

---

## Section 4: Schema Shape Recommendations

### 4.1 EquipGearForQuestStep

**Current shape:**
```csharp
public class EquipGearForQuestStep : Step
{
    public GearItem[] Items { get; init; } = [];
}
public record GearItem(string Slot, uint ItemId);
```

**Problem**: The `Slot` field is redundant. Given an `ItemId`, the target slot is deterministic (from the item's `EquipSlotCategory` in the Lumina Item sheet). Making the author specify the slot creates an opportunity for errors and requires the validator to cross-reference Lumina data.

**Exception**: Rings. A ring can go in either RingRight or RingLeft. But even here, the engine can pick the first available ring slot (matching Questionable's behavior).

**Recommended shape:**
```csharp
public sealed class EquipGearForQuestStep : Step
{
    /// <summary>
    /// Item IDs to equip. The engine determines the target slot from each item's
    /// EquipSlotCategory. For rings, the engine picks the first available ring slot.
    /// </summary>
    public uint[] ItemIds { get; init; } = [];
}
```

**Rationale**: Simpler schema, fewer authoring errors, no need for the `GearItem` record type (which can be removed from `SharedValueTypes.cs`). The `GearConstraints` record can also be removed if `EquipBestGearStep` is simplified (see below).

**Alternative considered**: Keep `GearItem` with slot for explicit control. Rejected because no real quest requires "equip this ring specifically in the LEFT slot" — the game doesn't care which ring slot.

### 4.2 EquipBestGearStep

**Current shape:**
```csharp
public class EquipBestGearStep : Step
{
    public GearConstraints? Constraints { get; init; }
}
public record GearConstraints(int? MinItemLevel = null);
```

**Problem**: `MinItemLevel` is speculative. No real quest says "equip your best gear but only if it's above ilvl X." The step means "equip the best gear you have for your current job." If the result is below some threshold, the quest itself won't be completable (duty ilvl gate), which is a separate concern.

**Recommended shape:**
```csharp
public sealed class EquipBestGearStep : Step
{
    // No additional properties needed.
    // The engine calls IBestGearEquipper.EquipBestGear().
    // Strategy (vanilla vs Stylist) is a plugin configuration, not a quest property.
}
```

**Rationale**: The step is a simple command: "make sure the player has their best gear on." The how (Stylist vs vanilla) is user preference. The what-if-not-good-enough is handled by the duty's ilvl gate, not the quest schema.

If a future quest needs "equip best gear for a specific job" (implying a job change first), that's a `change-job` step followed by `equip-best-gear` — two separate steps in sequence.

### 4.3 ChangeJobStep

**Current shape:**
```csharp
public class ChangeJobStep : Step
{
    public string Job { get; init; } = default!;
}
```

**Problem**: `string Job` is locale-sensitive and ambiguous. Is "DRK" the abbreviation? "Dark Knight" the full name? What locale? Per the `feedback_locale_stable_quest_identifiers.md` memory, QuestForge prefers numeric IDs.

**Recommended shape:**
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

**Alternatively**, use the adapter-layer `JobId` type if schema types are allowed to reference it (currently schema types use raw `uint` for IDs like `ItemId`, `EmoteId`, `ActionId`). For consistency with the existing pattern (`UseEmoteStep.EmoteId` is `uint`, `UseItemStep.ItemId` is `uint`), use `uint`.

**Authoring UX**: The authoring UI and validator can resolve the numeric ID to a human-readable name for display, but the schema carries the stable numeric ID.

---

## Section 5: Detection Signals for Authoring Inference

### 5.1 Equip Item Detection

**Observable signals:**
- **Equipment snapshot delta**: Compare `InventoryType.EquippedItems` before and after. Any item ID change in any slot is detectable.
- **`RaptureGearsetModule.CurrentGearsetIndex`**: May change if the equip triggers a gearset update (unlikely for manual single-item equips).
- **`PlayerState.CurrentClassJobId`**: Changes only if the equipped weapon changes the class/job.

**Recommended polling approach:**
- Snapshot all 14 equipment slots (item IDs) on each `OnFrameworkUpdate`.
- On any delta, emit an `EquipmentChangedSignal` with the set of changed slots and their old/new item IDs.
- The `StepInferenceEngine` inspects the delta: if exactly the quest-required items appeared in equipment, infer `equip-gear-for-quest`.

**Complexity**: Low. Equipment changes are infrequent and the snapshot is cheap (14 item ID reads from a known memory layout).

### 5.2 Best Gear Detection

**Observable signals:**
- Same equipment snapshot delta as above. When multiple slots change simultaneously (more than 1-2 items), it's likely a "recommended gear" or gearset equip rather than a manual single-item equip.
- Cannot distinguish "user clicked Recommended Gear button" from "user equipped a gearset" from the equipment delta alone.

**Recommended approach**: Do not auto-infer `equip-best-gear`. This step is always explicitly authored. The trigger is a quest requirement ("equip your best gear"), which is known at authoring time from the quest text, not from observing the player. The authoring UI should offer a button to insert this step manually.

### 5.3 Job Change Detection

**Observable signals:**
- **`PlayerState.CurrentClassJobId`**: Straightforward. Poll each frame; on change, emit `JobChangedSignal(oldJobId, newJobId)`.
- **`RaptureGearsetModule.CurrentGearsetIndex`**: Also changes when a gearset is equipped for a different job.

**Recommended polling approach:**
- Track `CurrentClassJobId` in `UIObserver`.
- On change, fire `OnJobChanged(oldJobId, newJobId)` into the aggregator.
- `StepInferenceEngine` infers `change-job` when a job change is detected and the quest context expects it.

**Complexity**: Very low. Single byte read per frame.

### 5.4 Summary Table

| Step Type | Detection Signal | Polling Cost | Auto-Infer? |
|---|---|---|---|
| `equip-gear-for-quest` | Equipment slot delta (14 uint reads) | Low | Yes |
| `equip-best-gear` | Multi-slot equipment delta | Low | No (explicit authoring) |
| `change-job` | `PlayerState.CurrentClassJobId` change | Trivial | Yes |

---

## Appendix A: Questionable's Gear Handling (Behavioral Reference)

Questionable implements three gear-related interaction types:

1. **EquipItem** (`EquipItem.cs`): Finds item by ID across all armoury + inventory containers, determines target equipment slot from Lumina's `EquipSlotCategory`, calls `InventoryManager.MoveItemSlot()` to swap it into the equipment container. Retries up to 3 times with 1-second delays. Handles ring slot ambiguity by preferring empty slots, then falling back to the first slot.

2. **EquipRecommended** (`EquipRecommended.cs`): Two paths based on user config:
   - **Vanilla**: `RecommendEquipModule.SetupForClassJob()` -> poll `IsUpdating` -> `EquipRecommendedGear()`.
   - **Stylist**: `RaptureGearsetModule.UpdateGearset(currentIndex)` to save current gear, then `StylistIpc.UpdateGearset()` which calls `Stylist.UpdateCurrentGearsetEx(true, true)`. Polls `StylistIpc.IsBusy` until complete.

3. **SwitchClassJob** (`SwitchClassJob.cs`): Iterates all 100 gearset slots, finds first with matching ClassJob byte, calls `RaptureGearsetModule.EquipGearset()`. Throws if no gearset found.

4. **UpdateGearset** (`UpdateGearset.cs`): Saves currently equipped gear to the gearset via `RaptureGearsetModule.UpdateGearset()`. Used after equip operations to keep gearsets in sync.

**Note**: Questionable issues an `EquipRecommended` before every duty/combat step (via `BeforeDutyOrInstance` factory), not just when explicitly authored. This is a UX choice QuestForge may or may not replicate.

## Appendix B: Stylist's Item Move Mechanism

Stylist's `ItemMover` service processes one item move per framework tick with throttling:
1. Reads the queued item's current inventory position.
2. Validates the item still exists at that position with the expected ItemId.
3. Determines the target armoury container from the item's `EquipSlotCategory`.
4. Finds the first empty slot in the target container.
5. Calls `InventoryManager.MoveItemSlot(srcType, srcSlot, dstType, dstSlot, true)`.
6. Retries up to 10 times per item before giving up.

After all items are moved, Stylist optionally "unmoves" items that were displaced (moving replaced items back to player inventory), controlled by user config `C.UnmoveItems`.

## Appendix C: Relevant FFXIVClientStructs Type Locations

| Type | File Path (relative to FFXIVClientStructs root) |
|---|---|
| `RaptureGearsetModule` | `FFXIVClientStructs/FFXIV/Client/UI/Misc/RaptureGearsetModule.cs` |
| `RecommendEquipModule` | `FFXIVClientStructs/FFXIV/Client/UI/Misc/RecommendEquipModule.cs` |
| `InventoryManager` | `FFXIVClientStructs/FFXIV/Client/Game/InventoryManager.cs` |
| `PlayerState` | `FFXIVClientStructs/FFXIV/Client/Game/UI/PlayerState.cs` |
| `RepairManager` | `FFXIVClientStructs/FFXIV/Client/Game/RepairManager.cs` |
