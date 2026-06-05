# Reward Selection Implementation Plan

**Status:** ready for test creation
**Input docs:**
- GitHub issue #167: "feat: quest reward selection with configurable priority"
- `docs/ADAPTERS.md` SS5.3 (existing `RewardSelectionStrategy` enum and reward selection spec)
- `docs/SCHEMA.md` (RewardOverride record)
- `QuestForge.Adapters/State/IQuestState.cs` (existing `QuestReward` record, `RewardSelectionStrategy` enum, `GetAvailableQuestRewards`)
- `QuestForge.Adapters/State/IGameStateProvider.cs` (`IsItemEquipped`, `GetCurrentJob`, equipped container)
- `QuestForge.Adapters/Gear/EquipSlotResolver.cs` (EquipSlotCategory -> slot index mapping)
- `QuestForge.Adapters/Items/CofferIdentifier.cs` (existing `IsCoffer(uint, uint)` predicate for gear coffers)
- `QuestForge.Adapters/Interaction/IInteractor.cs` (`SelectQuestReward` stub, `CompleteQuest`)
- `QuestForge.Adapters.Dalamud/Interaction/DalamudInteractor.cs` (lines 182-183: stub returns `Result.Fail`)
- `QuestForge.Adapters.Dalamud/State/DalamudQuestState.cs` (line 240: `GetAvailableQuestRewards` returns empty)
- `QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs` (lines 392-407: `IsItemEquipped` iterates 14 equipped slots)
- `QuestForge.Plugin/EngineHost.cs` (lines 487-515: Interact dispatch; lines 674-681: Wait dispatch; both call `CompleteQuest`)
- `QuestForge.Plugin/PluginConfig.cs` (no reward settings yet)
- `QuestForge.Schema/SharedValueTypes.cs` (line 142: `RewardOverride(string Strategy, uint? ItemId)`)
- `QuestForge.Schema/QuestDefinition.cs` (line 16: `RewardOverride? RewardOverride`)
- `QuestForge.Engine/EngineAction.cs` (no reward-related action needed)
- `QuestForge.Adapters.Fakes/State/FakeQuestState.cs` (`SetQuestRewards`, but `GetAvailableQuestRewards` ignores the quest-keyed dict and returns empty)
- `QuestForge.Adapters.Fakes/State/FakeGameStateProvider.cs` (`SetItemEquipped`, `SetJob`)

**Output (CI behavior):** After this plan is implemented, the `RewardPrioritizer` class is covered by engine-level unit tests in `QuestForge.Engine.Tests`. All 7 priority tiers, custom ordering, quest-level overrides, and graceful degradation are verified. CI red when any prioritization logic breaks. The Dalamud adapter implementations (`GetAvailableQuestRewards`, `SelectQuestReward`, `GetEquippedItemLevelForSlot`) are wired up but not unit-testable (Dalamud-only). EngineHost wiring inserts reward selection inline before `CompleteQuest` in the Interact and Wait dispatch arms.

> **Scope note:** This plan covers Slice 1 only (engine + adapter + config). Slice 2 (ConfigWindow reorderable UI) is deferred to a separate PR.

---

## Dependency graph

```
QuestForge.Adapters
   |-- QuestReward gains EquipSlotCategory, ItemActionId, ItemUiCategoryId fields (record enhancement)
   |-- IGameStateProvider gains GetEquippedItemLevelForSlot method
   |-- RewardPriority enum (new, 7 values, in QuestForge.Adapters/State/)
   |-- CofferIdentifier.IsCoffer (existing, in QuestForge.Adapters/Items/)
   |      consumed by v
   v
QuestForge.Adapters.Fakes
   |-- FakeQuestState.GetAvailableQuestRewards returns scripted rewards (fix)
   |-- FakeGameStateProvider gains SetEquippedItemLevelForSlot / GetEquippedItemLevelForSlot
   |      consumed by v
   v
QuestForge.Engine (new file: QuestForge.Engine/Rewards/RewardPrioritizer.cs)
   |-- Pure C# prioritization logic; no Dalamud dependency
   |-- GearCoffer tier calls CofferIdentifier.IsCoffer (adapter utility, no Dalamud dep)
   |      consumed by v
   v
QuestForge.Engine.Tests
   |-- RewardPrioritizerTests.cs (~30+ tests)
   v
QuestForge.Plugin
   |-- PluginConfig.RewardPriorityOrder (List<RewardPriority>)
   |-- EngineHost: reward selection wired inline in Interact + Wait dispatch
   v
QuestForge.Adapters.Dalamud
   |-- DalamudQuestState.GetAvailableQuestRewards parses JournalResult AtkValues
   |     (now also reads ItemAction.RowId + ItemUICategory.RowId from Lumina)
   |-- DalamudInteractor.SelectQuestReward fires ReceiveEvent
   |-- DalamudGameStateProvider.GetEquippedItemLevelForSlot reads equipped container
```

**Build order:**
1. `QuestReward` record enhancement (+ `ItemActionId`, `ItemUiCategoryId`) + `RewardPriority` enum (7 values) + `IGameStateProvider.GetEquippedItemLevelForSlot`
2. Fakes: scriptable rewards on `FakeQuestState`, equipped ilvl on `FakeGameStateProvider`
3. `RewardPrioritizer` in `QuestForge.Engine/Rewards/`
4. Tests R1-R30
5. `PluginConfig.RewardPriorityOrder`
6. EngineHost wiring (Interact + Wait arms)
7. Dalamud implementations (no unit tests, in-game smoke)

---

## Architectural decisions (read before coding)

### Decision RS1 -- `RewardPrioritizer` lives in `QuestForge.Engine/Rewards/`, not `QuestForge.Adapters/`

The prioritizer consumes adapter types (`QuestReward`, `JobId`, `RewardPriority`) but is **decision logic**, not adapter infrastructure. It belongs in the engine layer because:

1. **Testability.** Engine tests can exercise it directly via `FakeGameStateProvider` without Dalamud.
2. **Symmetry.** The engine already hosts decision logic that consumes adapter types (e.g., `QuestEngine.ResolveTeleportAction` consumes `TravelCapability`).
3. **Dependency direction.** `QuestForge.Engine` depends on `QuestForge.Adapters` (interfaces); placing the prioritizer in the engine keeps the dependency arrow pointing the right way.

**Alternative rejected: `QuestForge.Adapters/`** -- the adapter project is for interface definitions and pure utilities (like `EquipSlotResolver`). The prioritizer has branching business logic with 7 tiers and configurable ordering; it is not a stateless utility.

**What breaks if violated:** if placed in `QuestForge.Adapters`, the engine project cannot reference it without a circular dependency, forcing the wiring into EngineHost (plugin layer) instead of being testable.

### Decision RS2 -- Equipped ilvl accessor is a new method on `IGameStateProvider`, not a new focused interface

Add `GetEquippedItemLevelForSlot(int slotIndex, CancellationToken ct) -> Result<int>` to `IGameStateProvider`.

**Rationale:**
1. `IGameStateProvider` already owns equipped-item queries (`IsItemEquipped`). Equipped ilvl is the same domain.
2. A focused `IEquippedGearProvider` with one method is interface pollution. The "many small interfaces" pattern (like `IActionExecutor`, `IMount`) applies when the concern has its own lifecycle, faking needs, and multiple methods. A single read-only query does not.
3. The Dalamud implementation is 10 lines of code reading `InventoryManager.GetInventorySlot(EquippedItems, slotIndex)->ItemId` then looking up the item level from Lumina -- trivially co-located with the existing equipped-item reads.

**Alternative rejected: `IEquippedGearProvider`** -- would require a new fake, new constructor parameter on `QuestEngine`, new recording proxy wrapper. All for a single `int` query. Disproportionate surface area.

**Alternative rejected: passing all 14 equipped ilvls as a snapshot** -- wasteful; the prioritizer needs at most 5 slot lookups (one per reward). Per-slot lazy lookup matches the existing pattern (`IsItemEquipped` is also per-item).

**Concrete shape:**
```csharp
// Added to IGameStateProvider
Task<Result<int>> GetEquippedItemLevelForSlot(int slotIndex, CancellationToken ct);
```

Returns `Result.Ok(0)` for empty slots. Returns `Result.Fail` if slot index is out of range (0-13) or the inventory system is unavailable.

**What breaks if violated:** if this becomes a focused interface, every existing test that constructs a `QuestEngine` or `EngineTestHarness` must be updated to pass the new dependency. Adding a method to `IGameStateProvider` requires only updating `FakeGameStateProvider` (one file).

### Decision RS3 -- `QuestReward` gains `EquipSlotCategory`, `ItemActionId`, and `ItemUiCategoryId` directly on the record

The `QuestReward` record is enhanced with three new optional positional parameters:

```csharp
public record QuestReward(
    int Index,
    ItemId Item,
    int Quantity,
    int ItemLevel,
    long VendorPrice,
    IReadOnlyList<JobId> RestrictedJobs,
    bool IsUntradable,
    uint EquipSlotCategory = 0,   // 0 = non-equipment (consumable, materia, currency token)
    uint ItemActionId = 0,        // from Lumina Item.ItemAction.RowId
    uint ItemUiCategoryId = 0     // from Lumina Item.ItemUICategory.RowId
);
```

**Rationale for `EquipSlotCategory`:** the prioritizer needs to know which gear slot a reward occupies to compare against the player's currently equipped item level in that slot. `EquipSlotCategory` is the canonical field (from Lumina's Item sheet) that `EquipSlotResolver.GetTargetSlot` already maps. Passing it directly on the reward avoids a second Lumina lookup at prioritization time.

**Rationale for `ItemActionId` + `ItemUiCategoryId`:** these two fields are consumed by `CofferIdentifier.IsCoffer()` to detect gear coffers (e.g., "Ironworks Coffer (IL 50)"). These items have `ItemAction.RowId` in {1085, 388} and `ItemUICategory.RowId == 61`. Passing them on the record means the prioritizer can call the existing `CofferIdentifier` without needing Lumina at runtime. Both fields default to 0, which makes `IsCoffer` return false -- correct for non-coffer items.

**`EquipSlotCategory = 0` default:** non-equipment items (Allagan Bronze Pieces, materia, consumables) have EquipSlotCategory 0, which `EquipSlotResolver.GetTargetSlot` maps to `null`. The prioritizer uses this to distinguish equippable from non-equippable rewards.

**Alternative rejected: separate slot-mapping dictionary passed to the prioritizer** -- adds a parameter to every test setup call for no benefit; the information naturally belongs on the reward.

**Alternative rejected: passing icon IDs for coffer detection** -- icon IDs are fragile (see RS4) and do not survive expansion changes. Semantic Lumina fields are strictly better.

**What breaks if violated:** the prioritizer would need a `Func<ItemId, uint>` for slot category lookup and a `Func<ItemId, (uint, uint)>` for coffer fields, complicating test setup and forcing async Lumina reads at prioritization time.

### Decision RS4 -- "Gear Coffer" and "Gil Sack" are separate priority tiers

The original design conflated two distinct reward categories under a single "GearCoffer" tier:

1. **Gear coffers** (e.g., "Ironworks Coffer (IL 50)") -- items with ItemAction.RowId in {1085, 388} and ItemUICategory.RowId == 61. These have zero vendor price and contain job-appropriate gear when opened.
2. **Gil sacks** (Allagan Bronze/Silver/Gold Piece) -- non-equipment, tradable, positive vendor price. These are vendor-fodder.

These are fundamentally different rewards with different player motivations:
- Leveling players want coffers ranked high (free gear upgrade via /use).
- Gil-focused players want Allagan Pieces ranked high (immediate vendor value).

**Decision:** split into two priority tiers with distinct detection logic:

- **`GearCoffer`** -- matches items where `CofferIdentifier.IsCoffer(reward.ItemActionId, reward.ItemUiCategoryId)` returns true. This reuses the existing `QuestForge.Adapters/Items/CofferIdentifier.cs` which already encodes the correct ItemAction and ItemUICategory checks. The detection survives expansion changes because it uses semantic Lumina fields, not hardcoded icon arrays.
- **`GilSack`** -- matches items where `EquipSlotCategory == 0 && VendorPrice > 0 && !IsUntradable`. This captures Allagan Pieces and similar vendor-sellable non-equipment rewards.

New default priority order (7 tiers):
1. BiggestUpgrade
2. HighestGilValue
3. GearCoffer
4. GilSack
5. EquippableGear
6. UnequippableGear
7. AnythingElse

**Alternative rejected: icon-based detection (TextAdvance approach)** -- hardcoded icon ID arrays (e.g., 26001 for Allagan Pieces) require manual updates every expansion when new reward items are added. Semantic Lumina field checks via `CofferIdentifier` and the vendor-price heuristic survive expansion changes without code updates.

**Alternative rejected: keeping a single conflated tier** -- users cannot independently control coffer vs. vendor-fodder priority. A leveling Paladin who wants the gear coffer would also get Allagan Pieces at the same priority level, and vice versa.

**Cost of the split:** one extra enum value, one extra tier evaluator (a one-liner calling `CofferIdentifier.IsCoffer`), and two additional fields on `QuestReward` (both with default 0, backward-compatible). Minimal.

### Decision RS5 -- `RewardOverride.Strategy` string maps to `RewardPriority` via case-insensitive parse; `SpecificItem` is a distinct strategy

The schema has `RewardOverride(string Strategy, uint? ItemId)`. The engine has `RewardPriority` enum. Mapping:

| `RewardOverride.Strategy` string | Behavior |
|---|---|
| `"specificItem"` | Find reward matching `ItemId`. If not found, fall through to global config. |
| `"biggestUpgrade"` | Single-tier eval: biggest ilvl delta only. |
| `"highestGilValue"` | Single-tier eval: highest VendorPrice * Quantity. |
| `"gearCoffer"` | Single-tier eval: first reward where `CofferIdentifier.IsCoffer()` returns true. |
| `"gilSack"` | Single-tier eval: first non-equipment tradable vendor-sellable reward. |
| `"equippableGear"` | Single-tier eval: first equippable reward matching current job. |
| `"unequippableGear"` | Single-tier eval: first gear not matching current job. |
| `"anythingElse"` | Pick index 0 (first reward). |

The string is parsed with `Enum.TryParse<RewardPriority>(strategy, ignoreCase: true, ...)`. `"specificItem"` is handled as a special case before the parse because it is not a `RewardPriority` enum value -- it uses `ItemId` to find a specific reward.

**What breaks if violated:** if the strategy string were an enum in the schema, quest JSON files would need to import a C# enum name. Keeping it as a string in the schema preserves forward compatibility -- new strategies can be added without a schema version bump.

### Decision RS6 -- `RewardPrioritizer` takes `Func<int, Task<Result<int>>>` for ilvl lookup, not `IGameStateProvider`

The prioritizer is a pure decision function. It should not hold a reference to the full game state provider. Instead, it accepts a delegate for the one query it needs:

```csharp
// QuestForge.Engine/Rewards/RewardPrioritizer.cs
public static class RewardPrioritizer
{
    /// <summary>
    /// Selects the best reward index according to the priority order.
    /// Returns null if rewards is empty.
    /// </summary>
    public static async Task<int?> SelectBestReward(
        IReadOnlyList<QuestReward> rewards,
        IReadOnlyList<RewardPriority> priorityOrder,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct);

    /// <summary>
    /// Applies a quest-level RewardOverride. Returns null if the override cannot be satisfied
    /// (e.g., specificItem not found among rewards), in which case the caller falls back to
    /// the global priority list.
    /// </summary>
    public static async Task<int?> ApplyOverride(
        IReadOnlyList<QuestReward> rewards,
        RewardOverride rewardOverride,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct);
}
```

**Rationale:**
1. **Testability.** Tests pass `slotIndex => Task.FromResult(Result.Ok(someIlvl))` -- no fake needed.
2. **Single Responsibility.** The prioritizer does not know about game state; it knows about rewards and one lookup function.
3. **Async only where needed.** The `Func` is async because the Dalamud impl reads game memory; in tests, it returns `Task.FromResult` synchronously.

**Alternative rejected: passing `IGameStateProvider`** -- ties the prioritizer to the full interface, making tests construct a large fake when they only need ilvl. Also prevents the prioritizer from being `static` (it would need to store the dependency).

**What breaks if violated:** making the prioritizer non-static forces it into the engine's constructor, threading it through `QuestEngine` and `EngineTestHarness` -- unnecessary for a decision function that is called from `EngineHost`, not the engine's HSM.

### Decision RS7 -- Reward selection failure: retry once, then fall through to CompleteQuest

When `SelectQuestReward` returns `Result.Failure`:

1. **First failure:** log a warning and retry once (the addon may not have been ready).
2. **Second failure:** log an error and proceed to `CompleteQuest` anyway. The game may auto-select the first reward, or the quest may not actually require selection (e.g., single reward quests auto-select).

**Rationale:** blocking the entire quest on reward selection failure is disproportionate. The worst outcome of a wrong reward is getting a less-optimal item; the worst outcome of blocking is the automation stalling indefinitely.

**No AwaitUser.** Unlike action unusability (where the user must intervene), reward selection failure is recoverable -- the game handles missing selection gracefully for most quests.

**Concrete implementation in EngineHost:**
```csharp
// In Interact dispatch, after TryFillRequestAddon, before CompleteQuest:
var rewardsResult = await _questState.GetAvailableQuestRewards(ct);
if (rewardsResult is Result<IReadOnlyList<QuestReward>>.Success { Value: var rewards }
    && rewards.Count > 0)
{
    int? bestIndex = /* prioritize via RewardPrioritizer */;
    if (bestIndex is not null)
    {
        var selectResult = await _interactor.SelectQuestReward(bestIndex.Value, ct);
        if (selectResult.IsFailure)
        {
            // Retry once
            await Task.Delay(200, ct);
            await _interactor.SelectQuestReward(bestIndex.Value, ct);
        }
    }
}
await _interactor.CompleteQuest(_currentQuestId, ct);
```

### Decision RS8 -- The existing `RewardSelectionStrategy` enum is obsolete; replaced by `RewardPriority`

The existing `RewardSelectionStrategy` enum in `IQuestState.cs` (values: `FirstAvailable`, `SpecificItem`, `HighestIlvlForCurrentJob`, etc.) was a Phase 3 placeholder that was never consumed by any engine or adapter code. It is **deleted** in this plan and replaced by:

```csharp
// QuestForge.Adapters/State/RewardPriority.cs (new file)
namespace QuestForge.Adapters.State;

using System.Text.Json.Serialization;

/// <summary>
/// Priority tiers for quest reward selection. Evaluated in order; first tier
/// that produces a winner wins. Used by both the global config priority list
/// and quest-level RewardOverride single-tier evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RewardPriority>))]
public enum RewardPriority
{
    /// <summary>
    /// Among equippable rewards, pick the one with the largest item level increase
    /// over currently equipped gear in that slot. Falls through if no upgrade exists.
    /// </summary>
    BiggestUpgrade,

    /// <summary>
    /// Pick the reward with the highest VendorPrice * Quantity.
    /// </summary>
    HighestGilValue,

    /// <summary>
    /// Pick a gear coffer: an openable container that yields job-appropriate gear.
    /// Detected via CofferIdentifier.IsCoffer(ItemActionId, ItemUiCategoryId).
    /// ItemAction.RowId in {1085, 388} AND ItemUICategory.RowId == 61.
    /// </summary>
    GearCoffer,

    /// <summary>
    /// Pick a gil sack: a non-equipment, tradable, vendor-sellable reward
    /// (e.g., Allagan Bronze/Silver/Gold Piece).
    /// Detected via EquipSlotCategory == 0 AND VendorPrice > 0 AND !IsUntradable.
    /// </summary>
    GilSack,

    /// <summary>
    /// Pick an equippable reward that matches the current job's ClassJobCategory.
    /// </summary>
    EquippableGear,

    /// <summary>
    /// Pick an equippable reward that does NOT match the current job.
    /// </summary>
    UnequippableGear,

    /// <summary>
    /// Fallback: pick the first reward by index.
    /// </summary>
    AnythingElse
}
```

**What breaks if violated:** keeping both enums creates confusion about which is authoritative. No code references `RewardSelectionStrategy` today, so deletion is clean.

### Decision RS9 -- Tie-breaking is deterministic: lowest index wins

Within a priority tier, if multiple rewards score equally (e.g., two rewards with the same ilvl delta), the reward with the lowest `Index` (position in the JournalResult addon) wins. This is:

1. Deterministic across runs (same rewards, same selection).
2. Simple to implement (`OrderBy(score).ThenBy(index)`).
3. Matches player intuition (leftmost reward is "default").

### Decision RS10 -- `FakeQuestState.GetAvailableQuestRewards` is fixed to return scripted per-quest rewards

The current implementation ignores the `_rewards` dictionary and always returns an empty list. This is a bug from the Phase 3 stub era. Fix:

```csharp
public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    Record(nameof(GetAvailableQuestRewards));
    // GetAvailableQuestRewards is not keyed by quest --- it returns whatever rewards
    // are currently visible in the JournalResult addon. Use SetAvailableRewards
    // (non-quest-keyed) to script the return value.
    return Task.FromResult<Result<IReadOnlyList<QuestReward>>>(Result.Ok(_availableRewards));
}
```

**Design note:** `GetAvailableQuestRewards` is NOT keyed by quest ID -- the real addon shows rewards for whatever quest turn-in is currently active. The fake should mirror this with a non-keyed `SetAvailableRewards(IReadOnlyList<QuestReward>)` setter. The existing `SetQuestRewards(QuestId, ...)` was wrong (quest-keyed) but never called by production code, so remove it.

### Decision RS11 -- `BiggestUpgrade` gracefully degrades when ilvl lookup fails

When `getEquippedIlvlForSlot` returns `Result.Failure` for a given slot:

1. That reward is skipped for the BiggestUpgrade tier (treated as "no upgrade computable").
2. If ALL equippable rewards fail their ilvl lookup, the BiggestUpgrade tier produces no winner and falls through to the next tier.
3. A warning is logged (via a return value, not an exception) so the caller can surface it.

This matches the project's `Result<T>` discipline: routine failures (game memory unavailable, empty slot) do not throw exceptions.

### Decision RS12 -- `RewardPrioritizer` is stateless and static

The prioritizer is a pure function with no mutable state. All inputs are parameters. This means:

1. No constructor, no lifecycle.
2. Called directly from EngineHost (not threaded through QuestEngine).
3. Tests call the static methods directly -- no setup beyond constructing reward lists.

**Why not in QuestEngine:** reward selection is an EngineHost-level concern (inline in the dispatch loop, like `TryFillRequestAddon`). The engine's HSM does not model it as a step or action; it is invisible to the engine. This matches the architecture: the engine emits `Interact`, the host does NPC interaction + dialogue + request fill + **reward selection** + complete.

---

## Component breakdown

### Component 1: `QuestReward` record enhancement

```csharp
// QuestForge.Adapters/State/IQuestState.cs
public record QuestReward(
    int Index,
    ItemId Item,
    int Quantity,
    int ItemLevel,
    long VendorPrice,
    IReadOnlyList<JobId> RestrictedJobs,
    bool IsUntradable,
    uint EquipSlotCategory = 0,   // NEW: 0 = non-equipment
    uint ItemActionId = 0,        // NEW: from Lumina Item.ItemAction.RowId
    uint ItemUiCategoryId = 0     // NEW: from Lumina Item.ItemUICategory.RowId
);
```

The default values ensure backward compatibility with any existing construction sites. `ItemActionId` and `ItemUiCategoryId` are consumed by the GearCoffer tier evaluator via `CofferIdentifier.IsCoffer()`.

### Component 2: `IGameStateProvider.GetEquippedItemLevelForSlot`

```csharp
// Added to IGameStateProvider
Task<Result<int>> GetEquippedItemLevelForSlot(int slotIndex, CancellationToken ct);
```

### Component 3: `RewardPriority` enum

New file: `QuestForge.Adapters/State/RewardPriority.cs` (see RS8 above). **7 values:** BiggestUpgrade, HighestGilValue, GearCoffer, GilSack, EquippableGear, UnequippableGear, AnythingElse.

### Component 4: `RewardPrioritizer`

New file: `QuestForge.Engine/Rewards/RewardPrioritizer.cs`

```csharp
namespace QuestForge.Engine.Rewards;

using QuestForge.Adapters.Items;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Schema;

public static class RewardPrioritizer
{
    public static readonly IReadOnlyList<RewardPriority> DefaultPriorityOrder = new[]
    {
        RewardPriority.BiggestUpgrade,
        RewardPriority.HighestGilValue,
        RewardPriority.GearCoffer,
        RewardPriority.GilSack,
        RewardPriority.EquippableGear,
        RewardPriority.UnequippableGear,
        RewardPriority.AnythingElse
    };

    public static async Task<int?> SelectBestReward(
        IReadOnlyList<QuestReward> rewards,
        IReadOnlyList<RewardPriority> priorityOrder,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct);

    public static async Task<int?> ApplyOverride(
        IReadOnlyList<QuestReward> rewards,
        RewardOverride rewardOverride,
        JobId currentJob,
        Func<int, Task<Result<int>>> getEquippedIlvlForSlot,
        CancellationToken ct);

    // Internal per-tier evaluators (private, tested indirectly via SelectBestReward)
    // BiggestUpgrade: for each equippable reward matching currentJob, compute
    //   delta = reward.ItemLevel - equippedIlvl(EquipSlotResolver.GetTargetSlot(reward.EquipSlotCategory))
    //   pick max positive delta; tie-break by lowest Index
    // HighestGilValue: max of (VendorPrice * Quantity); tie-break by lowest Index
    // GearCoffer: first reward where CofferIdentifier.IsCoffer(reward.ItemActionId, reward.ItemUiCategoryId)
    //   returns true; tie-break by lowest Index
    // GilSack: first reward where EquipSlotCategory == 0 && VendorPrice > 0 && !IsUntradable
    // EquippableGear: first reward where IsEquippableByJob(reward, currentJob) && EquipSlotCategory != 0
    // UnequippableGear: first reward where EquipSlotCategory != 0 && !IsEquippableByJob(reward, currentJob)
    // AnythingElse: reward at Index 0
}
```

**Job matching:** `IsEquippableByJob(QuestReward reward, JobId currentJob)` returns true if `reward.RestrictedJobs` is empty (equippable by all jobs) OR `reward.RestrictedJobs.Contains(currentJob)`.

**GearCoffer tier reuses existing `CofferIdentifier`:** The `QuestForge.Adapters/Items/CofferIdentifier.cs` class already has `public static bool IsCoffer(uint itemActionRowId, uint itemUiCategoryRowId)` which checks `CofferActionIds.Contains(itemActionRowId) && itemUiCategoryRowId == MiscellanyCategory` (where CofferActionIds = {1085, 388} and MiscellanyCategory = 61). The GearCoffer tier evaluator is a one-liner: `CofferIdentifier.IsCoffer(reward.ItemActionId, reward.ItemUiCategoryId)`. No new detection logic needed.

### Component 5: `PluginConfig.RewardPriorityOrder`

```csharp
// Added to PluginConfig
public List<RewardPriority> RewardPriorityOrder { get; set; } = new(RewardPrioritizer.DefaultPriorityOrder);
```

### Component 6: EngineHost wiring

In `DispatchAction`, for both `case EngineAction.Interact` and `case EngineAction.Wait`:

```csharp
// After TryFillRequestAddon (Interact) or after AdvanceDialogue (Wait),
// before CompleteQuest:
await TrySelectQuestReward(ct);
await _interactor.CompleteQuest(_currentQuestId, ct);
```

Where `TrySelectQuestReward` is a new private method:

```csharp
private async Task TrySelectQuestReward(CancellationToken ct)
{
    var rewardsResult = await _questState.GetAvailableQuestRewards(ct);
    if (rewardsResult is not Result<IReadOnlyList<QuestReward>>.Success { Value: var rewards }
        || rewards.Count == 0)
        return;

    var jobResult = await _gameState.GetCurrentJob(ct);
    var currentJob = jobResult is Result<JobId>.Success { Value: var j } ? j : new JobId(0);

    // Quest-level override takes precedence
    int? bestIndex = null;
    if (_currentQuestDef?.RewardOverride is { } rewardOverride)
    {
        bestIndex = await RewardPrioritizer.ApplyOverride(
            rewards, rewardOverride, currentJob,
            slot => _gameState.GetEquippedItemLevelForSlot(slot, ct), ct);
    }

    // Fall back to global config priority list
    bestIndex ??= await RewardPrioritizer.SelectBestReward(
        rewards, _config.RewardPriorityOrder, currentJob,
        slot => _gameState.GetEquippedItemLevelForSlot(slot, ct), ct);

    if (bestIndex is null) return;

    DebounceLog("reward-select", $"[Reward] selected index={bestIndex.Value}");
    var selectResult = await _interactor.SelectQuestReward(bestIndex.Value, ct);
    if (selectResult.IsFailure)
    {
        await Task.Delay(200, ct);
        await _interactor.SelectQuestReward(bestIndex.Value, ct);
    }
}
```

### Component 7: Dalamud implementations

**`DalamudQuestState.GetAvailableQuestRewards`:**
- Read `JournalResult` addon via `GameGui.GetAddonByName("JournalResult")`
- If addon is null/not ready, return empty list
- Read `AtkValues[81]` for count (up to 5)
- For each reward `i` in `0..count-1`:
  - `ItemId = AtkValues[82+i]` (uint)
  - `IconId = AtkValues[88+i]` (uint, not exposed on QuestReward -- not needed)
  - `Quantity = AtkValues[93+i]` (int)
  - `Name = AtkValues[98+i]` (string, not exposed on QuestReward -- not needed)
  - Lumina lookup on Item sheet for: `ItemLevel`, `PriceLow` (vendor price), `ClassJobCategory`, `EquipSlotCategory`, `IsUntradable`, **`ItemAction.RowId`** (-> `ItemActionId`), **`ItemUICategory.RowId`** (-> `ItemUiCategoryId`)
  - `RestrictedJobs` derived from `ClassJobCategory` row (same pattern as `JobCategoryHelper`)
  - `VendorPrice = PriceLow` (the vendor buy-back price per unit)
  - `ItemActionId = item.ItemAction.Value.RowId` (0 if no ItemAction row)
  - `ItemUiCategoryId = item.ItemUICategory.Value.RowId` (0 if no ItemUICategory row)

**`DalamudInteractor.SelectQuestReward`:**
- Get `JournalResult` addon
- Fire `ReceiveEvent(eventType: 9, eventParam: 7 + slotIndex)` on the `AtkComponentJournalCanvas`
- Return `Result.Ok(Unit.Value)` on success

**`DalamudGameStateProvider.GetEquippedItemLevelForSlot`:**
- Validate `slotIndex` in 0..13
- Read `InventoryManager.GetInventorySlot(EquippedItems, slotIndex)`
- If slot is null or ItemId is 0, return `Result.Ok(0)`
- Lumina lookup `Item[slot->ItemId].LevelItem.Value.RowId` for the item level
- Return `Result.Ok(itemLevel)`

### Component 8: Fake updates

**`FakeQuestState`:**
- Remove quest-keyed `_rewards` dictionary
- Add `private IReadOnlyList<QuestReward> _availableRewards = Array.Empty<QuestReward>()`
- Add `SetAvailableRewards(IReadOnlyList<QuestReward> rewards)`
- `GetAvailableQuestRewards` returns `_availableRewards`

**`FakeGameStateProvider`:**
- Add `private readonly Dictionary<int, int> _equippedIlvls = new()`
- Add `SetEquippedItemLevelForSlot(int slotIndex, int itemLevel)`
- Add `GetEquippedItemLevelForSlot(int slotIndex, CancellationToken ct)` implementation

---

## Given-When-Then specifications

### R1: Empty rewards -- no selection, CompleteQuest proceeds

**Given:** `rewards` is an empty list; `priorityOrder` is the default order.
**When:** `SelectBestReward` is called.
**Then:** returns `null`.

### R2: Single reward -- always selected regardless of priority order

**Given:** `rewards` contains one reward (Index=0, any properties); `priorityOrder` is the default order.
**When:** `SelectBestReward` is called.
**Then:** returns `0`.

**Implementation detail:** the `AnythingElse` tier always catches a single reward. If the user has reordered priorities to exclude `AnythingElse`, even then the single reward should be selected by whichever tier matches. If NO tier matches (pathological: empty priority list), returns `null`.

### R3: BiggestUpgrade selects the largest positive ilvl delta

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=30, EquipSlotCategory=1, RestrictedJobs=[JobId(1)] },
    { Index=1, ItemLevel=50, EquipSlotCategory=3, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1) (Gladiator)
- Equipped ilvl: slot 0 (MainHand, from EquipSlotCategory 1) = 25, slot 2 (Head, from EquipSlotCategory 3) = 20
- `priorityOrder` = [BiggestUpgrade]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (delta 30 > delta 5).

### R4: BiggestUpgrade with no positive delta falls through

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=10, EquipSlotCategory=1, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1)
- Equipped ilvl: slot 0 = 50 (already better)
- `priorityOrder` = [BiggestUpgrade, HighestGilValue]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (BiggestUpgrade produces no winner; HighestGilValue selects index 0).

### R5: BiggestUpgrade skips rewards not equippable by current job

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=80, EquipSlotCategory=1, RestrictedJobs=[JobId(19)] },  // BLM only
    { Index=1, ItemLevel=30, EquipSlotCategory=3, RestrictedJobs=[JobId(1)] }    // GLA
  ]
- `currentJob` = JobId(1) (GLA)
- Equipped ilvl: slot 0 = 10, slot 2 = 10
- `priorityOrder` = [BiggestUpgrade]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (index 0 skipped because wrong job; index 1 has delta 20).

### R6: BiggestUpgrade skips rewards with EquipSlotCategory 0 (non-equipment)

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=0, EquipSlotCategory=0, VendorPrice=100 },  // Allagan Piece
    { Index=1, ItemLevel=30, EquipSlotCategory=1, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1)
- Equipped ilvl: slot 0 = 10
- `priorityOrder` = [BiggestUpgrade]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (index 0 has no equip slot).

### R7: HighestGilValue selects highest VendorPrice * Quantity

**Given:**
- `rewards` = [
    { Index=0, VendorPrice=50, Quantity=3 },   // 150
    { Index=1, VendorPrice=200, Quantity=1 },   // 200
    { Index=2, VendorPrice=100, Quantity=1 }    // 100
  ]
- `priorityOrder` = [HighestGilValue]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (200 > 150 > 100).

### R8: HighestGilValue tie-break by lowest index

**Given:**
- `rewards` = [
    { Index=0, VendorPrice=100, Quantity=1 },
    { Index=1, VendorPrice=100, Quantity=1 }
  ]
- `priorityOrder` = [HighestGilValue]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (tie broken by lower index).

### R9: GearCoffer selects reward identified by CofferIdentifier.IsCoffer

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=1, VendorPrice=50, IsUntradable=false },                                        // equipment
    { Index=1, EquipSlotCategory=0, VendorPrice=0, IsUntradable=false, ItemActionId=1085, ItemUiCategoryId=61 },  // gear coffer (Ironworks Coffer)
    { Index=2, EquipSlotCategory=0, VendorPrice=200, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 }     // Allagan Piece (NOT a coffer)
  ]
- `priorityOrder` = [GearCoffer]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (CofferIdentifier.IsCoffer(1085, 61) == true; index 2 has ItemActionId=0 so IsCoffer returns false).

### R10: GearCoffer tier produces no winner when no rewards match

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=1, VendorPrice=50, IsUntradable=false },                                       // equipment, not a coffer
    { Index=1, EquipSlotCategory=0, VendorPrice=200, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 }    // Allagan Piece, not a coffer
  ]
- `priorityOrder` = [GearCoffer, AnythingElse]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (GearCoffer falls through -- no rewards have coffer ItemAction; AnythingElse catches).

### R11: GilSack selects non-equipment tradable vendor-sellable reward

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=1, VendorPrice=50, IsUntradable=false },                                        // equipment (EquipSlotCategory != 0)
    { Index=1, EquipSlotCategory=0, VendorPrice=0, IsUntradable=false, ItemActionId=1085, ItemUiCategoryId=61 },  // gear coffer (VendorPrice == 0)
    { Index=2, EquipSlotCategory=0, VendorPrice=200, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 }     // Allagan Piece -- GilSack match
  ]
- `priorityOrder` = [GilSack]

**When:** `SelectBestReward` is called.
**Then:** returns `2` (EquipSlotCategory==0, VendorPrice>0, !IsUntradable).

### R12: GilSack rejects untradable non-equipment

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, VendorPrice=100, IsUntradable=true },   // untradable token
    { Index=1, EquipSlotCategory=3, RestrictedJobs=[JobId(1)] }             // equipment
  ]
- `priorityOrder` = [GilSack, AnythingElse]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (GilSack falls through -- index 0 is untradable; AnythingElse catches index 0).

### R13: GearCoffer vs GilSack ordering matters when user reorders priorities

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, VendorPrice=0, IsUntradable=false, ItemActionId=1085, ItemUiCategoryId=61 },  // gear coffer
    { Index=1, EquipSlotCategory=0, VendorPrice=500, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 }     // Allagan Gold Piece (GilSack)
  ]
- `priorityOrder` = [GilSack, GearCoffer]  // user prefers gil over coffers

**When:** `SelectBestReward` is called.
**Then:** returns `1` (GilSack evaluates first and matches the Allagan Gold Piece).

**Given (reversed):**
- Same rewards.
- `priorityOrder` = [GearCoffer, GilSack]  // user prefers coffers over gil

**When:** `SelectBestReward` is called.
**Then:** returns `0` (GearCoffer evaluates first and matches the Ironworks Coffer).

### R14: EquippableGear selects first reward matching current job

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=1, RestrictedJobs=[JobId(19)] },   // BLM
    { Index=1, EquipSlotCategory=3, RestrictedJobs=[JobId(1)] },    // GLA
    { Index=2, EquipSlotCategory=5, RestrictedJobs=[] }             // all jobs
  ]
- `currentJob` = JobId(1)
- `priorityOrder` = [EquippableGear]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (first equippable matching GLA; index 2 also matches but index 1 is lower).

### R15: EquippableGear skips non-equipment rewards

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, RestrictedJobs=[], VendorPrice=100 },  // Allagan Piece
    { Index=1, EquipSlotCategory=3, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1)
- `priorityOrder` = [EquippableGear]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (index 0 is non-equipment).

### R16: UnequippableGear selects gear not matching current job

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=1, RestrictedJobs=[JobId(1)] },    // GLA (matches)
    { Index=1, EquipSlotCategory=3, RestrictedJobs=[JobId(19)] }    // BLM (doesn't match)
  ]
- `currentJob` = JobId(1)
- `priorityOrder` = [UnequippableGear]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (gear that doesn't match current job).

### R17: AnythingElse always returns index 0

**Given:** `rewards` contains 3 rewards with indices 0, 1, 2; `priorityOrder` = [AnythingElse].
**When:** `SelectBestReward` is called.
**Then:** returns `0`.

### R18: Custom priority ordering -- higher tier wins

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, VendorPrice=500, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 },   // Allagan (GilSack match)
    { Index=1, EquipSlotCategory=3, RestrictedJobs=[JobId(1)], ItemLevel=50 } // equippable gear
  ]
- `currentJob` = JobId(1)
- Equipped ilvl: slot 2 = 10
- `priorityOrder` = [EquippableGear, GilSack, BiggestUpgrade]  // custom: EquippableGear first

**When:** `SelectBestReward` is called.
**Then:** returns `1` (EquippableGear tier evaluates first and finds a match).

### R19: Custom priority ordering -- reversed from default

**Given:**
- Same rewards as R18.
- `priorityOrder` = [GilSack, EquippableGear]  // GilSack first

**When:** `SelectBestReward` is called.
**Then:** returns `0` (GilSack evaluates first and matches the Allagan Piece).

### R20: Quest-level RewardOverride with SpecificItem

**Given:**
- `rewards` = [
    { Index=0, Item=ItemId(100) },
    { Index=1, Item=ItemId(200) },
    { Index=2, Item=ItemId(300) }
  ]
- `rewardOverride` = `RewardOverride("specificItem", ItemId: 200)`

**When:** `ApplyOverride` is called.
**Then:** returns `1` (matches ItemId 200).

### R21: Quest-level RewardOverride with SpecificItem not found

**Given:**
- `rewards` = [
    { Index=0, Item=ItemId(100) }
  ]
- `rewardOverride` = `RewardOverride("specificItem", ItemId: 999)`

**When:** `ApplyOverride` is called.
**Then:** returns `null` (item not found; caller falls back to global config).

### R22: Quest-level RewardOverride with named strategy

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, VendorPrice=500, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 },
    { Index=1, EquipSlotCategory=3, RestrictedJobs=[JobId(1)], ItemLevel=50 }
  ]
- `rewardOverride` = `RewardOverride("highestGilValue", ItemId: null)`
- `currentJob` = JobId(1)

**When:** `ApplyOverride` is called.
**Then:** returns `0` (single-tier eval of HighestGilValue; Allagan Piece has higher value).

### R23: Quest-level RewardOverride with "gearCoffer" strategy

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, VendorPrice=500, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 },   // Allagan Piece (GilSack, not GearCoffer)
    { Index=1, EquipSlotCategory=0, VendorPrice=0, IsUntradable=false, ItemActionId=388, ItemUiCategoryId=61 }    // gear coffer
  ]
- `rewardOverride` = `RewardOverride("gearCoffer", ItemId: null)`

**When:** `ApplyOverride` is called.
**Then:** returns `1` (single-tier eval of GearCoffer; only index 1 matches CofferIdentifier.IsCoffer(388, 61)).

### R24: Quest-level RewardOverride with "gilSack" strategy

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, VendorPrice=0, IsUntradable=false, ItemActionId=1085, ItemUiCategoryId=61 },  // gear coffer (not GilSack)
    { Index=1, EquipSlotCategory=0, VendorPrice=300, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 }     // Allagan Piece
  ]
- `rewardOverride` = `RewardOverride("gilSack", ItemId: null)`

**When:** `ApplyOverride` is called.
**Then:** returns `1` (single-tier eval of GilSack; only index 1 matches EquipSlotCategory==0 && VendorPrice>0 && !IsUntradable).

### R25: Equipped ilvl lookup failure -- BiggestUpgrade skips that reward

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=50, EquipSlotCategory=1, RestrictedJobs=[JobId(1)] },
    { Index=1, ItemLevel=30, EquipSlotCategory=3, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1)
- `getEquippedIlvlForSlot`: slot 0 returns `Result.Fail("unavailable")`; slot 2 returns `Result.Ok(10)`
- `priorityOrder` = [BiggestUpgrade]

**When:** `SelectBestReward` is called.
**Then:** returns `1` (index 0 skipped due to ilvl lookup failure; index 1 has delta 20).

### R26: All ilvl lookups fail -- BiggestUpgrade tier falls through

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=50, EquipSlotCategory=1, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1)
- `getEquippedIlvlForSlot`: always returns `Result.Fail("unavailable")`
- `priorityOrder` = [BiggestUpgrade, AnythingElse]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (BiggestUpgrade falls through; AnythingElse catches).

### R27: Empty priority list returns null

**Given:** `rewards` = [{ Index=0 }]; `priorityOrder` is empty.
**When:** `SelectBestReward` is called.
**Then:** returns `null`.

### R28: BiggestUpgrade with unmappable EquipSlotCategory (e.g., 15)

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=50, EquipSlotCategory=15, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1)
- `priorityOrder` = [BiggestUpgrade, AnythingElse]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (EquipSlotResolver returns null for category 15; BiggestUpgrade skips; AnythingElse catches).

### R29: Mixed equippable/unequippable/coffer/gilsack with full priority chain

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=1, RestrictedJobs=[JobId(19)] },                                                 // BLM only (UnequippableGear for GLA)
    { Index=1, EquipSlotCategory=0, VendorPrice=100, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 },    // Allagan (GilSack)
    { Index=2, EquipSlotCategory=5, RestrictedJobs=[JobId(1)] }                                                   // GLA body (EquippableGear)
  ]
- `currentJob` = JobId(1)
- `priorityOrder` = [EquippableGear, GilSack, UnequippableGear, AnythingElse]

**When:** `SelectBestReward` is called.
**Then:** returns `2` (EquippableGear finds index 2 -- GLA body armor).

### R30: RewardOverride with unknown strategy string returns null

**Given:**
- `rewards` = [{ Index=0 }]
- `rewardOverride` = `RewardOverride("invalidStrategy", null)`

**When:** `ApplyOverride` is called.
**Then:** returns `null` (unrecognized strategy; caller falls back to global config).

### R31: BiggestUpgrade tie-break by lowest index when deltas are equal

**Given:**
- `rewards` = [
    { Index=0, ItemLevel=30, EquipSlotCategory=1, RestrictedJobs=[JobId(1)] },
    { Index=1, ItemLevel=30, EquipSlotCategory=3, RestrictedJobs=[JobId(1)] }
  ]
- `currentJob` = JobId(1)
- Equipped ilvl: slot 0 = 10, slot 2 = 10
- `priorityOrder` = [BiggestUpgrade]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (both have delta 20; tie broken by lowest index).

### R32: RestrictedJobs empty means equippable by all jobs

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=5, RestrictedJobs=[] }  // all jobs
  ]
- `currentJob` = JobId(99)  // any job
- `priorityOrder` = [EquippableGear]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (empty RestrictedJobs = equippable by all).

### R33: GearCoffer with ItemAction 388 (alternate coffer action ID)

**Given:**
- `rewards` = [
    { Index=0, EquipSlotCategory=0, VendorPrice=0, IsUntradable=false, ItemActionId=388, ItemUiCategoryId=61 },  // coffer via action 388
    { Index=1, EquipSlotCategory=0, VendorPrice=500, IsUntradable=false, ItemActionId=0, ItemUiCategoryId=0 }    // Allagan Piece
  ]
- `priorityOrder` = [GearCoffer]

**When:** `SelectBestReward` is called.
**Then:** returns `0` (CofferIdentifier.IsCoffer(388, 61) == true).

---

## Implementation order

### Phase A -- Adapter types (0.5 days)

1. Enhance `QuestReward` record with `EquipSlotCategory`, `ItemActionId`, and `ItemUiCategoryId` parameters (all with default 0)
2. Add `GetEquippedItemLevelForSlot` to `IGameStateProvider`
3. Create `RewardPriority` enum (7 values) in `QuestForge.Adapters/State/RewardPriority.cs`
4. Delete obsolete `RewardSelectionStrategy` enum from `IQuestState.cs`
5. Update any compilation errors from the enum deletion (grep for `RewardSelectionStrategy`)

**Done before Phase B starts:** all adapter types compile; no test changes yet.

### Phase B -- Fakes (0.5 days)

1. Fix `FakeQuestState.GetAvailableQuestRewards` (RS10): replace quest-keyed dict with non-keyed `_availableRewards`
2. Add `SetAvailableRewards(IReadOnlyList<QuestReward>)` setter
3. Add `SetEquippedItemLevelForSlot(int, int)` and `GetEquippedItemLevelForSlot` to `FakeGameStateProvider`
4. Add default implementation for `GetEquippedItemLevelForSlot` that returns `Result.Ok(0)` for valid slots

**Done before Phase C starts:** fakes compile; tests still pass (no behavioral change to existing tests).

### Phase C -- RewardPrioritizer + tests (2 days)

1. Create `QuestForge.Engine/Rewards/RewardPrioritizer.cs` with `SelectBestReward` and `ApplyOverride`
2. Write tests R1-R33 in `QuestForge.Engine.Tests/Rewards/RewardPrioritizerTests.cs`
3. Implement the prioritizer tier-by-tier, making tests pass

**Done before Phase D starts:** all 33 tests green; `dotnet test QuestForge.Engine.Tests` passes.

### Phase D -- PluginConfig + EngineHost wiring (1 day)

1. Add `RewardPriorityOrder` to `PluginConfig`
2. Add `TrySelectQuestReward` private method to `EngineHost`
3. Wire into Interact dispatch (after `TryFillRequestAddon`, before `CompleteQuest`)
4. Wire into Wait dispatch (after `AdvanceDialogue`, before `CompleteQuest`)
5. Add `_currentQuestDef` field to EngineHost (or access via existing quest loader) for `RewardOverride`

**Done before Phase E starts:** EngineHost compiles; reward selection is wired but Dalamud stubs still return empty/fail.

### Phase E -- Dalamud implementations (1 day)

1. Implement `DalamudQuestState.GetAvailableQuestRewards` (parse JournalResult AtkValues + Lumina lookup for ItemAction.RowId and ItemUICategory.RowId)
2. Implement `DalamudInteractor.SelectQuestReward` (fire ReceiveEvent)
3. Implement `DalamudGameStateProvider.GetEquippedItemLevelForSlot` (read equipped container + Lumina)
4. In-game smoke test

**Done:** reward selection fires in-game during quest turn-in.

---

## Done criteria

1. `RewardPrioritizer.SelectBestReward` correctly selects rewards according to configurable 7-tier priority, verified by 33+ unit tests.
2. `RewardPrioritizer.ApplyOverride` correctly handles `specificItem`, `gearCoffer`, `gilSack`, and other named strategy overrides.
3. `QuestReward` record includes `EquipSlotCategory`, `ItemActionId`, and `ItemUiCategoryId` with backward-compatible defaults of 0.
4. `IGameStateProvider.GetEquippedItemLevelForSlot` exists and is implemented in `FakeGameStateProvider` and `DalamudGameStateProvider`.
5. `RewardSelectionStrategy` enum is deleted; `RewardPriority` enum (7 values) is its replacement.
6. `FakeQuestState.GetAvailableQuestRewards` returns scriptable rewards (not hardcoded empty).
7. EngineHost Interact and Wait dispatch arms call `TrySelectQuestReward` before `CompleteQuest`.
8. `DalamudQuestState.GetAvailableQuestRewards` parses JournalResult AtkValues and resolves `ItemActionId` + `ItemUiCategoryId` from Lumina.
9. `DalamudInteractor.SelectQuestReward` fires ReceiveEvent on the addon.
10. `PluginConfig.RewardPriorityOrder` defaults to the 7-tier order (BiggestUpgrade, HighestGilValue, GearCoffer, GilSack, EquippableGear, UnequippableGear, AnythingElse).
11. GearCoffer tier reuses existing `CofferIdentifier.IsCoffer()` -- no new detection logic.
12. `dotnet test QuestForge.Engine.Tests` passes with all new and existing tests green.

---

## What this plan does NOT include

- **ConfigWindow UI** for reorderable priority list (Slice 2, separate PR)
- **Trace event** for reward selection (`reward.selected`) -- deferred until trace format gains a slot for it
- **Recording proxy** for reward reads -- `GetAvailableQuestRewards` is already captured by `RecordingQuestState`; `GetEquippedItemLevelForSlot` will need a proxy entry, but this is mechanical and follows the existing pattern
- **Validator rules** for `RewardOverride.Strategy` string values -- deferred to tools-repo catch-up (the strategy string is validated at runtime, not schema time)
- **Market board pricing** -- explicitly out of v1 scope per ADAPTERS.md SS5.3
- **Authoring inference** for reward selection -- no authoring signal needed; reward choice is a config/policy decision, not an inferred step

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in this plan.
- Happy paths: 17 scenarios (R1, R2, R3, R7, R9, R11, R13 [two sub-cases], R14, R17, R18, R20, R22, R23, R24, R29, R32, R33)
- Edge cases: 8 scenarios (R4, R5, R6, R8, R27, R28, R30, R31)
- Error cases: 8 scenarios (R10, R12, R15, R16, R19, R21, R25, R26)
- Expected total: ~33 tests in QuestForge.Engine.Tests/Rewards/RewardPrioritizerTests.cs
