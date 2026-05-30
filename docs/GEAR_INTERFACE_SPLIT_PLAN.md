# Gear Interface Split Plan

**Status:** ready to implement
**Issue:** #121
**Input docs:** `docs/GEAR_RESEARCH.md` (Section 3.3 split recommendation), `QuestForge.Adapters/Gear/IGearManager.cs`
**Output:** `IGearManager` removed. Three focused interfaces created. All existing tests pass after migration. No new step dispatch, no new engine actions.
**Phase dependencies:** None (pure refactor of stubs). Must land before #117, #118, #119.

---

## Dependency graph

Single repo (`questforge`), no cross-repo changes.

```
1. QuestForge.Adapters/Gear/
   ├── IGearEquipper.cs       (new — 2 methods)
   ├── IBestGearEquipper.cs   (new — 2 methods)
   ├── IJobChanger.cs         (new — 2 methods)
   └── IGearManager.cs        (DELETED)

2. QuestForge.Adapters.Fakes/Gear/
   ├── FakeGearEquipper.cs    (new)
   ├── FakeBestGearEquipper.cs(new)
   ├── FakeJobChanger.cs      (new)
   └── FakeGearManager.cs     (DELETED)

3. QuestForge.Adapters.Dalamud/Gear/
   ├── DalamudGearEquipper.cs    (new stub)
   ├── DalamudBestGearEquipper.cs(new stub)
   ├── DalamudJobChanger.cs      (new stub)
   └── DalamudGearManager.cs     (DELETED)

4. QuestForge.Engine/QuestEngine.cs
   └── ctor: remove `IGearManager gear`, add 3 optional params

5. QuestForge.Engine.Tests/
   ├── Helpers/EngineTestHarness.cs — swap FakeGearManager for 3 fakes
   ├── Engine/QuestEngineConstructorTests.cs — update GetAllArgs
   └── Replay/States/*.cs — swap IGearManager for 3 interfaces

6. QuestForge.Adapters.Tests/Fakes/
   ├── FakeGearEquipperTests.cs     (new, replaces FakeGearManagerTests.cs)
   ├── FakeBestGearEquipperTests.cs (new)
   ├── FakeJobChangerTests.cs       (new)
   └── FakeGearManagerTests.cs      (DELETED)

7. QuestForge.Plugin/EngineHost.cs — replace one DalamudGearManager with 3 stubs
```

**Build order:** Interfaces first (Adapters) -> Fakes + Dalamud stubs -> Engine ctor change -> Test harness + test updates -> Plugin wiring.

---

## Architectural decisions

### GS-1: Delete IGearManager entirely rather than narrowing it

**Decision:** Delete `IGearManager` and all 15 methods. Do NOT keep a narrowed version for repair.

**Rationale:**
- `_gear` is stored in `QuestEngine` but **never called** (grep confirms only 2 hits: field declaration and ctor assignment). It is dead code.
- Repair (#120) is a future concern. When it arrives, it will create `IGearConditionInspector` + `IGearRepairer` from scratch with clean signatures (the research doc Section 3.4 already sketches these). Creating narrowed stubs now is speculative.
- Keeping a narrowed `IGearManager` means every test site must still construct it even though nothing uses it.

**What breaks if violated:** If someone later tries to add repair methods to one of the three new interfaces, the interface grows beyond its step-type scope. The decision is enforced by the interface names (`IGearEquipper` is clearly not the place for `RepairAtNpc`).

**Rejected alternative:** Keep `IGearManager` narrowed to 5 repair methods. Rejected because it forces every `QuestEngine` constructor call to pass a value nobody reads, and creates a placeholder interface whose design will change when #120 actually happens.

### GS-2: Three focused interfaces, each mapping to exactly one step type

**Decision:**

```csharp
// QuestForge.Adapters/Gear/IGearEquipper.cs
namespace QuestForge.Adapters.Gear;

public interface IGearEquipper
{
    Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct);
    Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct);
}

// QuestForge.Adapters/Gear/IBestGearEquipper.cs
namespace QuestForge.Adapters.Gear;

public interface IBestGearEquipper
{
    Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct);
    Task<Result<bool>> IsStylistAvailable(CancellationToken ct);
}

// QuestForge.Adapters/Gear/IJobChanger.cs
namespace QuestForge.Adapters.Gear;

public interface IJobChanger
{
    Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct);
    Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct);
}
```

**Rationale:**
- Matches the established pattern: `IItemUser` (2 methods for UseItemStep), `IActionExecutor` (2 methods for UseActionStep), `IEmoteExecutor` (1 method for UseEmoteStep).
- Each interface is consumed by exactly one step type's engine slice (#117, #118, #119 respectively).
- The engine injects each as an optional `?` parameter, consistent with all post-Phase-3 adapters.

**What breaks if violated:** If methods from multiple step types share an interface, the fake for one step type must implement methods for another, and tests become coupled to unrelated step types.

### GS-3: Use `uint itemId` (not `ItemId` wrapper) in IGearEquipper

**Decision:** `IGearEquipper.EquipItem` and `IsItemEquipped` take `uint itemId`, not `ItemId`.

**Rationale:** Consistency with the schema-to-adapter boundary. `UseItemStep.ItemId` is `uint`, `UseActionStep.ActionId` is `uint`, `UseEmoteStep.EmoteId` is `uint`. The adapter-layer `ItemId` wrapper is used in the `IInteractor` / `IVendor` APIs (older Phase 3 design). New focused interfaces follow the `uint` convention.

**What breaks if violated:** The engine dispatch code would need `new ItemId(step.ItemIds[i])` wrapping at every call site, adding noise.

**Testability:** Fakes accept `uint` directly; no wrapper construction in test setup.

### GS-4: Remove EquipSlot from EquipItem signature

**Decision:** `IGearEquipper.EquipItem(uint itemId, CancellationToken ct)` has NO `EquipSlot` parameter.

**Rationale:** The target equipment slot is deterministic from the item's `EquipSlotCategory` in the Lumina Item sheet (see GEAR_RESEARCH.md Section 4.1). Making the caller specify the slot creates authoring errors and requires cross-referencing Lumina data in the validator. The adapter determines the slot internally, matching Questionable's `EquipItem.GetEquipSlot()` pattern.

**Ring ambiguity:** A ring can go in either slot. The adapter picks the first available ring slot (right, then left). No quest requires "equip specifically in left ring slot."

**What breaks if violated:** If `EquipSlot` is in the signature, the schema needs a `GearItem(slot, itemId)` compound type, the validator needs a cross-reference rule, and authors can specify a slot that doesn't match the item's category.

### GS-5: Remove speculative supporting types

**Decision:** Delete these types from the `IGearManager.cs` file (they become orphans after the split):

| Type | Disposition | Reason |
|------|-------------|--------|
| `EquippedItem` record | DELETE | Only used by `GetEquippedGear()` which is removed |
| `EquippedItemCondition` record | DELETE | Only used by `GetEquippedGearCondition()` which is removed |
| `GearItem` record | DELETE | Only used by `GetAvailableGear()` which is removed |
| `EquipSlot` enum | DELETE | Removed from all signatures (GS-4); no remaining consumer |
| `GearSelectionMethod` enum | DELETE | No consumer; Stylist-vs-vanilla is adapter-internal (GS-2) |
| `RepairPreference` enum | DELETE | Repair is #120; will be redesigned there |
| `EquipOutcome` enum | KEEP (move to `IGearEquipper.cs`) | Used by `IGearEquipper` and `IBestGearEquipper` |
| `JobChangeOutcome` enum | KEEP (move to `IJobChanger.cs`) | Used by `IJobChanger` |
| `RepairOutcome` enum | DELETE | No consumer after `IGearManager` deletion; #120 will recreate |

**What breaks if violated:** Keeping dead types creates confusion about what's actually used and makes `IGearEquipper.cs` appear to have dependencies it doesn't.

### GS-6: QuestEngine ctor — remove `IGearManager gear`, add 3 optional params

**Decision:**

```csharp
public QuestEngine(
    IGameStateProvider gameState,
    IQuestState questState,
    INavigator navigator,
    ITeleporter teleporter,
    IInteractor interactor,
    ICombat combat,
    IMinigameSkipper minigames,       // <-- gear was here, now removed
    IDialogueResolver dialogue,
    ITimingProfile timing,
    ITraceWriter trace,
    ILogger<QuestEngine> logger,
    TimeProvider? clock = null,
    IVendor? vendor = null,
    IActionExecutor? actionExecutor = null,
    IEmoteExecutor? emoteExecutor = null,
    IChatSender? chatSender = null,
    IItemUser? itemUser = null,
    IGearEquipper? gearEquipper = null,
    IBestGearEquipper? bestGearEquipper = null,
    IJobChanger? jobChanger = null)
```

**Rationale:**
- `IGearManager` was a required (non-null) param but never used. Removing it is safe.
- The 3 new interfaces are optional (null = AwaitUser at dispatch time), matching `IVendor?`, `IActionExecutor?`, etc.
- Placement: after `itemUser`, alphabetical within the gear group.

**What breaks if violated:** If `IGearManager` stays required, every existing test that constructs a `QuestEngine` directly must still pass a value nobody reads.

**Migration:** Every call site that currently passes `gear` (or `GearManager`) removes that argument. Since the new params are optional and defaulted to null, no call site needs to pass them until the step types land.

### GS-7: EngineTestHarness — expose 3 fakes, remove FakeGearManager

**Decision:**

```csharp
// Replace:
public FakeGearManager GearManager { get; } = new FakeGearManager();

// With:
public FakeGearEquipper GearEquipper { get; } = new FakeGearEquipper();
public FakeBestGearEquipper BestGearEquipper { get; } = new FakeBestGearEquipper();
public FakeJobChanger JobChanger { get; } = new FakeJobChanger();
```

And in the `QuestEngine` constructor call:

```csharp
// Remove: GearManager from the positional args
// Add to the named args:
gearEquipper: GearEquipper,
bestGearEquipper: BestGearEquipper,
jobChanger: JobChanger
```

**No RunToCompletion dispatch arms yet:** This is a refactor only. No new `EngineAction` subtypes are added, so no new `case` arms in `RunToCompletion`. Those come in #117/#118/#119.

### GS-8: IFixtureState and replay states — replace IGearManager property

**Decision:** `IFixtureState.Gear` (type `IGearManager`) is removed. Replace with three properties:

```csharp
public interface IFixtureState
{
    // ... existing properties ...
    IGearEquipper      GearEquipper      { get; }
    IBestGearEquipper  BestGearEquipper  { get; }
    IJobChanger        JobChanger        { get; }
}
```

Implementing classes (`TraceReplayFixtureState`, `SimpleLinearAcceptanceState`, and the test-only stub in `TraceReplayFixtureStateTests.cs`) are updated correspondingly.

**Rationale:** `IFixtureState` feeds into `QuestEngine` construction. If the engine ctor no longer takes `IGearManager`, `IFixtureState` must not expose it.

### GS-9: Commit GEAR_RESEARCH.md alongside the split

**Decision:** `docs/GEAR_RESEARCH.md` is currently uncommitted in the working tree. It must be committed as part of this PR (or a preceding prep PR) since the plan references it.

---

## File layout

### New files

| File | Project | Contents |
|------|---------|----------|
| `QuestForge.Adapters/Gear/IGearEquipper.cs` | Adapters | Interface + `EquipOutcome` enum |
| `QuestForge.Adapters/Gear/IBestGearEquipper.cs` | Adapters | Interface (reuses `EquipOutcome` from `IGearEquipper.cs`) |
| `QuestForge.Adapters/Gear/IJobChanger.cs` | Adapters | Interface + `JobChangeOutcome` enum |
| `QuestForge.Adapters.Fakes/Gear/FakeGearEquipper.cs` | Fakes | Recording + scripting fake |
| `QuestForge.Adapters.Fakes/Gear/FakeBestGearEquipper.cs` | Fakes | Recording + scripting fake |
| `QuestForge.Adapters.Fakes/Gear/FakeJobChanger.cs` | Fakes | Recording + scripting fake |
| `QuestForge.Adapters.Dalamud/Gear/DalamudGearEquipper.cs` | Dalamud | Stub returning failures |
| `QuestForge.Adapters.Dalamud/Gear/DalamudBestGearEquipper.cs` | Dalamud | Stub returning failures |
| `QuestForge.Adapters.Dalamud/Gear/DalamudJobChanger.cs` | Dalamud | Stub returning failures |
| `QuestForge.Adapters.Tests/Fakes/FakeGearEquipperTests.cs` | Adapters.Tests | Tests for the new fake |
| `QuestForge.Adapters.Tests/Fakes/FakeBestGearEquipperTests.cs` | Adapters.Tests | Tests for the new fake |
| `QuestForge.Adapters.Tests/Fakes/FakeJobChangerTests.cs` | Adapters.Tests | Tests for the new fake |

### Modified files

| File | Change |
|------|--------|
| `QuestForge.Engine/QuestEngine.cs` | Remove `_gear` field, remove `IGearManager gear` ctor param, add 3 optional params |
| `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` | Replace `FakeGearManager` with 3 fakes, update ctor call |
| `QuestForge.Engine.Tests/Engine/QuestEngineConstructorTests.cs` | Update `GetAllArgs` tuple, update ctor call |
| `QuestForge.Engine.Tests/Replay/States/IFixtureState.cs` | Replace `IGearManager Gear` with 3 interface properties |
| `QuestForge.Engine.Tests/Replay/States/TraceReplayFixtureState.cs` | Replace `FakeGearManager` with 3 fakes |
| `QuestForge.Engine.Tests/Replay/States/SimpleLinearAcceptanceState.cs` | Replace `FakeGearManager` with 3 fakes |
| `QuestForge.Engine.Tests/Replay/TraceReplayFixtureStateTests.cs` | Update mock state type, update ctor calls |
| `QuestForge.Plugin/EngineHost.cs` | Replace `DalamudGearManager _gear` with 3 fields, update `BeginRun` |

### Deleted files

| File | Reason |
|------|--------|
| `QuestForge.Adapters/Gear/IGearManager.cs` | Replaced by 3 focused interfaces |
| `QuestForge.Adapters.Fakes/Gear/FakeGearManager.cs` | Replaced by 3 focused fakes |
| `QuestForge.Adapters.Dalamud/Gear/DalamudGearManager.cs` | Replaced by 3 focused stubs |
| `QuestForge.Adapters.Tests/Fakes/FakeGearManagerTests.cs` | Replaced by 3 focused test files |

---

## Concrete type definitions

### IGearEquipper.cs

```csharp
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IGearEquipper
{
    Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct);
    Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct);
}

public enum EquipOutcome { Equipped, NoChange, InCombat, InInstance, ItemNotFound, Failed }
```

### IBestGearEquipper.cs

```csharp
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IBestGearEquipper
{
    Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct);
    Task<Result<bool>> IsStylistAvailable(CancellationToken ct);
}
```

Note: `EquipOutcome` is defined in `IGearEquipper.cs` and shared by `IBestGearEquipper`. Both files are in the same namespace so no additional using is needed.

### IJobChanger.cs

```csharp
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Gear;

public interface IJobChanger
{
    Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct);
    Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct);
}

public enum JobChangeOutcome { Changed, GearsetNotFound, JobNotUnlocked, InCombat, InInstance, Failed }
```

### FakeGearEquipper.cs

```csharp
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Gear;

public sealed class FakeGearEquipper : IGearEquipper
{
    public record EquipCall(uint ItemId, DateTimeOffset At) : AdapterCall(At);

    public CallLog<EquipCall> RecordedCalls { get; } = new();

    private readonly Dictionary<uint, bool> _equippedItems = new();
    private (string Reason, string? Detail)? _nextFailure;
    private EquipOutcome? _nextOutcome;

    public void ScriptNextResult(EquipOutcome outcome) => _nextOutcome = outcome;
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);
    public void SetItemEquipped(uint itemId, bool equipped) => _equippedItems[itemId] = equipped;

    public void Reset()
    {
        RecordedCalls.Clear();
        _equippedItems.Clear();
        _nextFailure = null;
        _nextOutcome = null;
    }

    public Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new EquipCall(itemId, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<EquipOutcome>>(Result.Fail(f.Reason, f.Detail));
        }

        var outcome = _nextOutcome ?? EquipOutcome.Equipped;
        _nextOutcome = null;
        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var equipped = _equippedItems.TryGetValue(itemId, out var v) && v;
        return Task.FromResult<Result<bool>>(Result.Ok(equipped));
    }
}
```

### FakeBestGearEquipper.cs

```csharp
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Gear;

public sealed class FakeBestGearEquipper : IBestGearEquipper
{
    public record EquipBestCall(DateTimeOffset At) : AdapterCall(At);

    public CallLog<EquipBestCall> RecordedCalls { get; } = new();

    private bool _stylistAvailable;
    private (string Reason, string? Detail)? _nextFailure;
    private EquipOutcome? _nextOutcome;

    public void ScriptNextResult(EquipOutcome outcome) => _nextOutcome = outcome;
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);
    public void SetStylistAvailable(bool available) => _stylistAvailable = available;

    public void Reset()
    {
        RecordedCalls.Clear();
        _stylistAvailable = false;
        _nextFailure = null;
        _nextOutcome = null;
    }

    public Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new EquipBestCall(DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<EquipOutcome>>(Result.Fail(f.Reason, f.Detail));
        }

        var outcome = _nextOutcome ?? EquipOutcome.Equipped;
        _nextOutcome = null;
        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(_stylistAvailable));
    }
}
```

### FakeJobChanger.cs

```csharp
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Gear;

public sealed class FakeJobChanger : IJobChanger
{
    public record ChangeJobCall(JobId Job, DateTimeOffset At) : AdapterCall(At);

    public CallLog<ChangeJobCall> RecordedCalls { get; } = new();

    private readonly HashSet<uint> _availableGearsets = new();
    private (string Reason, string? Detail)? _nextFailure;
    private JobChangeOutcome? _nextOutcome;

    public void ScriptNextResult(JobChangeOutcome outcome) => _nextOutcome = outcome;
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);
    public void AddGearsetForJob(JobId job) => _availableGearsets.Add(job.Value);
    public void RemoveGearsetForJob(JobId job) => _availableGearsets.Remove(job.Value);

    public void Reset()
    {
        RecordedCalls.Clear();
        _availableGearsets.Clear();
        _nextFailure = null;
        _nextOutcome = null;
    }

    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new ChangeJobCall(job, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<JobChangeOutcome>>(Result.Fail(f.Reason, f.Detail));
        }

        var outcome = _nextOutcome ?? JobChangeOutcome.Changed;
        _nextOutcome = null;
        return Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(_availableGearsets.Contains(job.Value)));
    }
}
```

### DalamudGearEquipper.cs (stub)

```csharp
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudGearEquipper : IGearEquipper
{
    public DalamudGearEquipper(PluginServices svc) { }

    public Task<Result<EquipOutcome>> EquipItem(uint itemId, CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    public Task<Result<bool>> IsItemEquipped(uint itemId, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
```

### DalamudBestGearEquipper.cs (stub)

```csharp
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudBestGearEquipper : IBestGearEquipper
{
    public DalamudBestGearEquipper(PluginServices svc) { }

    public Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct)
        => Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Failed));

    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
```

### DalamudJobChanger.cs (stub)

```csharp
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudJobChanger : IJobChanger
{
    public DalamudJobChanger(PluginServices svc) { }

    public Task<Result<JobChangeOutcome>> ChangeToJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<JobChangeOutcome>>(Result.Ok(JobChangeOutcome.Failed));

    public Task<Result<bool>> GearsetExistsForJob(JobId job, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));
}
```

---

## Migration path

### QuestEngine.cs

1. Remove `using QuestForge.Adapters.Gear;` — wait, still needed for the new interfaces (same namespace).
2. Remove `private readonly IGearManager _gear;` field.
3. Remove `IGearManager gear` from ctor positional params (it was between `ICombat combat` and `IMinigameSkipper minigames`).
4. Remove `_gear = gear ?? throw new ArgumentNullException(nameof(gear));` from ctor body.
5. Add 3 optional params at the end of the ctor signature (after `IItemUser? itemUser = null`):
   - `IGearEquipper? gearEquipper = null`
   - `IBestGearEquipper? bestGearEquipper = null`
   - `IJobChanger? jobChanger = null`
6. Add 3 private readonly fields: `_gearEquipper`, `_bestGearEquipper`, `_jobChanger`.
7. Assign in ctor body (no null-check, they are optional).

### EngineTestHarness.cs

1. Replace `public FakeGearManager GearManager { get; } = new FakeGearManager();` with three properties.
2. In the ctor, update the `QuestEngine` constructor call:
   - Remove `GearManager` from the positional args (between `Combat` and `MinigameSkipper`).
   - Add `gearEquipper: GearEquipper, bestGearEquipper: BestGearEquipper, jobChanger: JobChanger` to named args.

### QuestEngineConstructorTests.cs

1. Update `GetAllArgs` tuple: replace `FakeGearManager gear` with `FakeGearEquipper gearEquipper, FakeBestGearEquipper bestGearEquipper, FakeJobChanger jobChanger`.
2. Update ctor call in `CreateEngine`: remove `gear` positional, add 3 named params.
3. If any test specifically tested null-gear-throws, that test is removed (gear is now optional).

### IFixtureState.cs

1. Replace `IGearManager Gear { get; }` with:
   - `IGearEquipper GearEquipper { get; }`
   - `IBestGearEquipper BestGearEquipper { get; }`
   - `IJobChanger JobChanger { get; }`

### TraceReplayFixtureState.cs, SimpleLinearAcceptanceState.cs

1. Replace `public IGearManager Gear { get; } = new FakeGearManager();` with three properties backed by the three fakes.

### TraceReplayFixtureStateTests.cs

1. Replace `Assert.IsType<FakeGearManager>(state.Gear)` with three assertions.
2. Replace `new FakeGearManager()` constructor args with three fakes.
3. Update the mock `IFixtureState` implementation (the test-only class around line 1244) to expose the three properties.

### EngineHost.cs

1. Replace `private readonly DalamudGearManager _gear;` with:
   - `private readonly DalamudGearEquipper _gearEquipper;`
   - `private readonly DalamudBestGearEquipper _bestGearEquipper;`
   - `private readonly DalamudJobChanger _jobChanger;`
2. In constructor, replace `_gear = new DalamudGearManager(services);` with three constructions.
3. In `BeginRun`, replace `_gear` in the `QuestEngine` constructor call:
   - Remove `_gear` from positional args.
   - Add `gearEquipper: _gearEquipper, bestGearEquipper: _bestGearEquipper, jobChanger: _jobChanger` to named args.
4. No `Debug` accessor for gear adapters (none exists today for `_gear`; decision deferred to step-type slices).

### FakeGearManagerTests.cs

Deleted entirely. Replaced by 3 new test files (see test scenarios below).

---

## Test scenarios

### FakeGearEquipper (GE-T1 through GE-T7)

**GE-T1: Default EquipItem returns Equipped and records call**
- Given: a fresh `FakeGearEquipper`
- When: `EquipItem(12345, ct)` is called
- Then: result is `Ok(EquipOutcome.Equipped)`, `RecordedCalls.Count == 1`, recorded call has `ItemId == 12345`

**GE-T2: ScriptNextResult overrides outcome**
- Given: `ScriptNextResult(EquipOutcome.InCombat)` called
- When: `EquipItem(1, ct)` is called
- Then: result is `Ok(EquipOutcome.InCombat)`
- When: `EquipItem(2, ct)` is called again (no new script)
- Then: result is `Ok(EquipOutcome.Equipped)` (default restored)

**GE-T3: ScriptNextFailure returns failure result**
- Given: `ScriptNextFailure("inCombat", "cannot equip while fighting")` called
- When: `EquipItem(1, ct)` is called
- Then: result is `Fail` with reason `"inCombat"`, call IS recorded

**GE-T4: IsItemEquipped defaults to false**
- Given: a fresh `FakeGearEquipper`
- When: `IsItemEquipped(999, ct)` is called
- Then: result is `Ok(false)`

**GE-T5: SetItemEquipped makes IsItemEquipped return true**
- Given: `SetItemEquipped(42, true)` called
- When: `IsItemEquipped(42, ct)` is called
- Then: result is `Ok(true)`
- When: `IsItemEquipped(99, ct)` is called (different item)
- Then: result is `Ok(false)`

**GE-T6: Reset clears everything**
- Given: `EquipItem(1, ct)` called, `SetItemEquipped(1, true)` called, `ScriptNextResult(InCombat)` called
- When: `Reset()` called
- Then: `RecordedCalls.Count == 0`, `IsItemEquipped(1, ct)` returns `false`, `EquipItem(2, ct)` returns `Equipped` (not InCombat)

**GE-T7: CancellationToken cancellation throws**
- Given: a cancelled token
- When: `EquipItem(1, cancelledToken)` is called
- Then: `OperationCanceledException` is thrown

### FakeBestGearEquipper (BE-T1 through BE-T6)

**BE-T1: Default EquipBestGear returns Equipped and records call**
- Given: a fresh `FakeBestGearEquipper`
- When: `EquipBestGear(ct)` is called
- Then: result is `Ok(EquipOutcome.Equipped)`, `RecordedCalls.Count == 1`

**BE-T2: ScriptNextResult overrides outcome, consumed after one call**
- Given: `ScriptNextResult(EquipOutcome.NoChange)` called
- When: `EquipBestGear(ct)` is called
- Then: result is `Ok(EquipOutcome.NoChange)`
- When: `EquipBestGear(ct)` is called again
- Then: result is `Ok(EquipOutcome.Equipped)` (default)

**BE-T3: ScriptNextFailure returns failure**
- Given: `ScriptNextFailure("unavailable")` called
- When: `EquipBestGear(ct)` is called
- Then: result is `Fail` with reason `"unavailable"`

**BE-T4: IsStylistAvailable defaults to false**
- Given: a fresh `FakeBestGearEquipper`
- When: `IsStylistAvailable(ct)` is called
- Then: result is `Ok(false)`

**BE-T5: SetStylistAvailable makes IsStylistAvailable return true**
- Given: `SetStylistAvailable(true)` called
- When: `IsStylistAvailable(ct)` is called
- Then: result is `Ok(true)`

**BE-T6: Reset clears everything**
- Given: `EquipBestGear(ct)` called, `SetStylistAvailable(true)` called
- When: `Reset()` called
- Then: `RecordedCalls.Count == 0`, `IsStylistAvailable(ct)` returns `false`

### FakeJobChanger (JC-T1 through JC-T7)

**JC-T1: Default ChangeToJob returns Changed and records call**
- Given: a fresh `FakeJobChanger`
- When: `ChangeToJob(new JobId(19), ct)` is called
- Then: result is `Ok(JobChangeOutcome.Changed)`, `RecordedCalls.Count == 1`, recorded call has `Job.Value == 19`

**JC-T2: ScriptNextResult overrides outcome, consumed after one call**
- Given: `ScriptNextResult(JobChangeOutcome.GearsetNotFound)` called
- When: `ChangeToJob(new JobId(19), ct)` is called
- Then: result is `Ok(JobChangeOutcome.GearsetNotFound)`
- When: `ChangeToJob(new JobId(19), ct)` is called again
- Then: result is `Ok(JobChangeOutcome.Changed)` (default)

**JC-T3: ScriptNextFailure returns failure**
- Given: `ScriptNextFailure("timeout")` called
- When: `ChangeToJob(new JobId(19), ct)` is called
- Then: result is `Fail` with reason `"timeout"`

**JC-T4: GearsetExistsForJob defaults to false**
- Given: a fresh `FakeJobChanger`
- When: `GearsetExistsForJob(new JobId(32), ct)` is called
- Then: result is `Ok(false)`

**JC-T5: AddGearsetForJob makes GearsetExistsForJob return true**
- Given: `AddGearsetForJob(new JobId(32))` called
- When: `GearsetExistsForJob(new JobId(32), ct)` is called
- Then: result is `Ok(true)`
- When: `GearsetExistsForJob(new JobId(19), ct)` is called (different job)
- Then: result is `Ok(false)`

**JC-T6: RemoveGearsetForJob reverses AddGearsetForJob**
- Given: `AddGearsetForJob(new JobId(32))` called, then `RemoveGearsetForJob(new JobId(32))` called
- When: `GearsetExistsForJob(new JobId(32), ct)` is called
- Then: result is `Ok(false)`

**JC-T7: Reset clears everything**
- Given: `ChangeToJob(new JobId(19), ct)` called, `AddGearsetForJob(new JobId(32))` called
- When: `Reset()` called
- Then: `RecordedCalls.Count == 0`, `GearsetExistsForJob(new JobId(32), ct)` returns `false`

### Migration sanity (MS-T1 through MS-T3)

**MS-T1: QuestEngine ctor accepts null gear adapters without error**
- Given: all required params provided, all 3 gear params omitted (defaulted to null)
- When: `QuestEngine` is constructed
- Then: no exception thrown

**MS-T2: QuestEngine ctor accepts populated gear adapters**
- Given: all required params provided, all 3 gear params provided with fakes
- When: `QuestEngine` is constructed
- Then: no exception thrown

**MS-T3: Existing engine flow tests pass unchanged**
- This is implicit: all existing tests in `QuestForge.Engine.Tests/` that use `EngineTestHarness` must compile and pass after the harness migration. No test behavior changes.

---

## Implementation order

### Phase A: Interface definitions (< 1 hour)

1. Create `IGearEquipper.cs`, `IBestGearEquipper.cs`, `IJobChanger.cs` in `QuestForge.Adapters/Gear/`.
2. Delete `IGearManager.cs`.
3. Build `QuestForge.Adapters` to verify — expect compile errors downstream.

**Done before B:** The interfaces compile in isolation.

### Phase B: Fakes + Dalamud stubs (< 1 hour)

1. Create `FakeGearEquipper.cs`, `FakeBestGearEquipper.cs`, `FakeJobChanger.cs` in `QuestForge.Adapters.Fakes/Gear/`.
2. Delete `FakeGearManager.cs`.
3. Create `DalamudGearEquipper.cs`, `DalamudBestGearEquipper.cs`, `DalamudJobChanger.cs` in `QuestForge.Adapters.Dalamud/Gear/`.
4. Delete `DalamudGearManager.cs`.
5. Build Fakes and Dalamud projects — expect compile errors in Engine + Plugin.

**Done before C:** Fakes and Dalamud stubs compile.

### Phase C: Engine + Plugin wiring (< 1 hour)

1. Update `QuestEngine.cs` ctor: remove `IGearManager gear`, add 3 optional params, remove `_gear` field.
2. Update `EngineHost.cs`: replace `_gear` with 3 fields, update construction and `BeginRun`.
3. Build — expect compile errors in test projects.

**Done before D:** Engine and Plugin compile.

### Phase D: Test migration (< 1 hour)

1. Update `EngineTestHarness.cs`.
2. Update `QuestEngineConstructorTests.cs`.
3. Update `IFixtureState.cs` and all implementing classes.
4. Update `TraceReplayFixtureStateTests.cs`.
5. Delete `FakeGearManagerTests.cs`.
6. Create `FakeGearEquipperTests.cs`, `FakeBestGearEquipperTests.cs`, `FakeJobChangerTests.cs`.
7. `dotnet test` all test projects.

**Done:** All tests pass. No compile warnings related to gear.

**Total estimated time: 2-3 hours.**

---

## Done criteria

1. `dotnet build` succeeds with zero warnings related to gear types.
2. `dotnet test QuestForge.Engine.Tests` passes — all existing engine/replay tests pass without modification to their test logic (only infrastructure wiring changes).
3. `dotnet test QuestForge.Adapters.Tests` passes — 3 new fake test files with the scenarios above pass; `FakeGearManagerTests.cs` is gone.
4. No file in the repository references `IGearManager`, `FakeGearManager`, or `DalamudGearManager`.
5. `EquipSlot`, `EquippedItem`, `EquippedItemCondition`, `GearItem`, `GearSelectionMethod`, `RepairPreference`, `RepairOutcome` types do not exist anywhere in the codebase.
6. `QuestEngine` constructor has zero required parameters related to gear.
7. `EngineTestHarness` exposes `GearEquipper`, `BestGearEquipper`, `JobChanger` properties (not `GearManager`).
8. `docs/GEAR_RESEARCH.md` is committed alongside this change.

---

## Exclusions

- **No new EngineAction subtypes.** The `EquipGear`, `EquipBest`, `ChangeJob` engine actions are added by #117, #118, #119 respectively.
- **No new step dispatch.** The engine switch statement is not modified (no new step types handled).
- **No validator rules.** No new validation is added; this is a pure refactor.
- **No repair interfaces.** `IGearConditionInspector` and `IGearRepairer` are deferred to #120.
- **No schema changes.** `Step.cs` is not modified; the step type classes for gear are added by their respective issues.
- **No RunToCompletion dispatch arms.** The harness does not gain new `case` arms.
- **No EngineHost.DispatchAction arms.** The plugin does not gain new dispatch logic.
- **No Debug accessors.** No `DebugGearEquipper` etc. until the step-type slices need them.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 9 scenarios (GE-T1, GE-T4, GE-T5, BE-T1, BE-T4, BE-T5, JC-T1, JC-T4, JC-T5)
- Edge cases: 7 scenarios (GE-T2, GE-T6, BE-T2, BE-T6, JC-T2, JC-T6, JC-T7)
- Error cases: 5 scenarios (GE-T3, GE-T7, BE-T3, JC-T3, MS-T1)
- Migration sanity: 2 scenarios (MS-T2, MS-T3 is implicit)
- Expected total: ~23 test methods across FakeGearEquipperTests, FakeBestGearEquipperTests, FakeJobChangerTests in QuestForge.Adapters.Tests
