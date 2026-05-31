# isItemEquipped Predicate Plan

**Status:** ready to implement
**Scope:** Add `IsItemEquipped(ItemId, ct)` to `IGameStateProvider`; wire `playerHasEquipped(itemId)` in `PredicateEvaluator`
**Estimated effort:** ~30 minutes implementation + tests
**Branch:** `feat/is-item-equipped-predicate`

---

## Background

`playerHasEquipped` is already registered in `FunctionRegistry.cs` (tools repo) with signature `(Int, String?) -> Bool`, but has no runtime implementation in `PredicateEvaluator`. The Dalamud-side logic already exists in `DalamudGearEquipper.IsItemEquipped` -- it scans the 14 `EquippedItems` inventory slots. This plan promotes that read to `IGameStateProvider` (where all game-state reads live) and wires the predicate evaluator.

---

## Decisions

**IE-1: Use `playerHasEquipped`, not `isItemEquipped`.** The function name `playerHasEquipped` is already registered in `FunctionRegistry.cs`. Renaming would require a coordinated tools-repo change and would break any quest files already using the registered name. The user-facing predicate is `playerHasEquipped(itemId)`.

**IE-2: Add `IsItemEquipped(ItemId, CancellationToken)` to `IGameStateProvider`.** This is a game-state read ("is this item in the equipment container?") which fits `IGameStateProvider`'s responsibility. The engine must not depend on `IGearEquipper` for reads.

```csharp
// IGameStateProvider.cs — new method in the Inventory section
Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct);
```

**IE-3: Ignore the optional second argument (slot name) for now.** The `FunctionRegistry` registers `playerHasEquipped` as `OptionalTail(1, 1)` with types `[Int, String]`. The second arg is a slot-name filter (e.g. `"mainhand"`). This plan implements the 1-arg form only. The 2-arg form is deferred -- `PredicateEvaluator` throws `NotSupportedException` if a second argument is provided. This avoids needing to define a slot-name enum and keeps scope tight.

**IE-4: Dalamud implementation reuses `DalamudGearEquipper.IsItemEquipped` logic verbatim.** Scan `InventoryManager.GetInventorySlot(InventoryType.EquippedItems, 0..13)` for `item.ItemId == itemId`. Copy the logic rather than calling the `IGearEquipper` instance (no cross-adapter dependency).

**IE-5: No recording-proxy starvation risk.** `IsItemEquipped` is called only during predicate evaluation (postcondition checks / `Expect` / skip conditions). These are already recorded by `RecordingGameStateProvider`'s generic `Record<T>` method. Existing fixtures will not call `IsItemEquipped`, so no starvation -- new fixtures that use `playerHasEquipped` predicates will record the observation naturally.

---

## Implementation changes (5 files + evaluator)

### 1. `IGameStateProvider.cs` -- add method

```csharp
// In the Inventory section, after GetItemCount:
Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct);
```

### 2. `DalamudGameStateProvider.cs` -- Dalamud impl

```csharp
public unsafe Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    var im = InventoryManager.Instance();
    if (im is null)
        return Task.FromResult<Result<bool>>(
            Result.Fail<bool>("noInventoryManager", "InventoryManager.Instance() returned null"));

    for (var i = 0; i < 14; i++)
    {
        var slot = im->GetInventorySlot(InventoryType.EquippedItems, i);
        if (slot is not null && slot->ItemId == item.Value)
            return Task.FromResult<Result<bool>>(Result.Ok(true));
    }
    return Task.FromResult<Result<bool>>(Result.Ok(false));
}
```

### 3. `FakeGameStateProvider.cs` -- fake impl

Add a `HashSet<ItemId> _equippedItems` field with `SetItemEquipped(ItemId, bool)` setter. Implementation:

```csharp
private readonly HashSet<ItemId> _equippedItems = [];

public void SetItemEquipped(ItemId item, bool equipped)
{
    lock (_lock)
    {
        if (equipped) _equippedItems.Add(item);
        else _equippedItems.Remove(item);
    }
}

public Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    Record(nameof(IsItemEquipped));
    lock (_lock) return Task.FromResult<Result<bool>>(Result.Ok(_equippedItems.Contains(item)));
}
```

### 4. `RecordingGameStateProvider.cs` -- recording proxy

```csharp
public async Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct)
{
    var result = await _inner.IsItemEquipped(item, ct);
    Record(nameof(IsItemEquipped), item, result);
    return result;
}
```

### 5. `ReplayGameStateProvider.cs` -- replay impl

```csharp
public Task<Result<bool>> IsItemEquipped(ItemId item, CancellationToken ct)
{
    var obs = ScanNext(nameof(IsItemEquipped), item);
    return Task.FromResult(Materialize<bool>(obs.Value));
}
```

### 6. `PredicateEvaluator.cs` -- wire the function

Add to the `EvaluateFunction` switch, after `playerHasItem`:

```csharp
"playerHasEquipped" when args.Length == 1 =>
    (await _gameState.IsItemEquipped(new ItemId((uint)(long)args[0]), ct)).ValueOrThrow,
"playerHasEquipped" when args.Length == 2 =>
    throw new NotSupportedException(
        "playerHasEquipped(itemId, slotName) slot filter is not yet implemented. Use playerHasEquipped(itemId)."),
```

---

## Tools-repo impact

**None.** `playerHasEquipped` is already registered in `FunctionRegistry.cs` with the correct signature. The predicate checker already validates arity and argument types. No tools-repo PR is needed.

---

## Test scenarios

**T1 -- Happy path: item is equipped, predicate returns true.**
Given: `FakeGameStateProvider` has item 4567 equipped.
When: engine evaluates `playerHasEquipped(4567)`.
Then: result is `true`.

**T2 -- Happy path: item is NOT equipped, predicate returns false.**
Given: `FakeGameStateProvider` has NO items equipped.
When: engine evaluates `playerHasEquipped(4567)`.
Then: result is `false`.

**T3 -- Negation works.**
Given: `FakeGameStateProvider` has item 4567 equipped.
When: engine evaluates `not playerHasEquipped(4567)`.
Then: result is `false`.

**T4 -- Composition with playerHasItem.**
Given: `FakeGameStateProvider` has item 4567 in inventory (count 1) AND equipped.
When: engine evaluates `playerHasItem(4567) and playerHasEquipped(4567)`.
Then: result is `true`.

**T5 -- Two-arg form throws NotSupportedException.**
Given: any state.
When: engine evaluates `playerHasEquipped(4567, "mainhand")`.
Then: throws `NotSupportedException` with message containing "slot filter is not yet implemented".

**T6 -- Used in Expect postcondition.**
Given: a quest step with `Expect: "playerHasEquipped(1234)"` and item 1234 is equipped.
When: engine evaluates the step's postcondition.
Then: postcondition passes (step completes).

**T7 -- Postcondition fails when item not equipped.**
Given: a quest step with `Expect: "playerHasEquipped(1234)"` and item 1234 is NOT equipped.
When: engine evaluates the step's postcondition.
Then: postcondition fails (engine retries / spin-loops).

**T8 -- FakeGameStateProvider.SetItemEquipped toggle.**
Given: `SetItemEquipped(item, true)` then `SetItemEquipped(item, false)`.
When: `IsItemEquipped(item)` is called.
Then: returns `false`.

---

## Scope guard -- explicitly excluded

- **Slot-name filtering** (`playerHasEquipped(itemId, "mainhand")`) -- deferred; throws if used.
- **Changes to `IGearEquipper`** -- this plan does not modify the gear equipping interface.
- **HQ item distinction** -- `InventoryItem.ItemId` does not encode HQ; matching is by base item id only.
- **Validator rules** -- no new E#/W# rules needed; this is a predicate function, not a step type.
- **Authoring inference** -- predicates are not inferred; they are authored. No authoring-mode changes.

---

## Done criteria

1. `IGameStateProvider.IsItemEquipped` compiles in all 5 implementations (Dalamud, Fake, Recording, Replay, interface).
2. `PredicateEvaluator` resolves `playerHasEquipped(itemId)` to a bool via `IGameStateProvider.IsItemEquipped`.
3. All T1-T8 tests pass in `QuestForge.Engine.Tests`.
4. `dotnet build` succeeds across all projects (no missing interface members).
5. Two-arg form throws `NotSupportedException` (T5).
