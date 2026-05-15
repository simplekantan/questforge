# Phase 3 Implementation Plan: Adapter Interfaces and In-Memory Fakes

**Status:** ready to implement
**Input docs:** docs/ADAPTERS.md (full spec), docs/ARCHITECTURE.md (three-layer separation), docs/SPIKE_NOTES.md (Phase 0 IPC findings), docs/NEXT_STEPS.md §Phase 3
**Output:** `QuestForge.Engine` (Phase 4) can be written and unit-tested against a complete adapter contract, with no game or Dalamud dependency, in CI.
**Architect review:** incorporated (2026-05-15)

---

## Dependency graph

Three new projects in the `questforge` plugin repo (NOT questforge-tools):

```
questforge (this repo)
   QuestForge.Schema           already exists (Phase 1)
   QuestForge.Schema.Tests     already exists (Phase 1)
   QuestForge.Adapters         NEW: interfaces + cross-cutting types, no Dalamud dependency
       consumed by
   QuestForge.Adapters.Fakes   NEW: in-memory test doubles, references QuestForge.Adapters
       consumed by
   QuestForge.Adapters.Tests   NEW: xUnit verifying fakes behave correctly
```

**Build order:** Adapters (interfaces + types) -> Fakes -> Tests.

`questforge-tools` knows nothing about adapters. The validator runs against schema types only. The adapter layer is the seam between the engine and the Dalamud world; tools live entirely outside that seam.

---

## Architectural decisions (read before coding)

### 1. Adapter projects live in the plugin repo, not in tools

Adapters are part of the plugin architecture. They will get concrete Dalamud-backed implementations in Phase 6 (`QuestForge.Adapters.Dalamud`). The interface project must live where those implementations will live so they share a solution and a release cadence.

questforge-tools is a separate concern: it validates quest JSON. It has no reason to know what `INavigator` looks like.

### 2. All 10 adapters get interfaces and fakes in Phase 3

The engine (Phase 4) starts with `travel` and `talk` steps only, which exercise `INavigator`, `IInteractor`, `IGameStateProvider`, `IQuestState`, `ITeleporter`, `IDialogueResolver`, and `ITimingProfile`. The other three (`ICombat`, `IGearManager`, `IMinigameSkipper`) are not used by the Phase 4 engine.

We still build all 10 because:
- The engine constructor signature is fixed in ADAPTERS.md §14 — `QuestEngine` takes all of them. Building only some means changing that signature later.
- Step types added in Phase 10+ (`combat`, `equip-gear-for-quest`, `minigame`) require these adapters. Phase 4 wiring is easier if every dependency already exists.
- Fakes for unused adapters can be minimal — see §2.5 of this plan.

### 3. `readonly record struct` for strong-typed identifiers, not `using` aliases

ADAPTERS.md §3.1 specifies all IDs as `readonly record struct`. `using NpcId = System.UInt32;` (type alias) would compile but:
- It is file-scoped — every file that uses `NpcId` must repeat the alias.
- It does not actually create a distinct type. `NpcId` and `ZoneId` are both `uint`; the compiler will not catch `nav.FindNpc(zoneId)`.
- It cannot carry methods, attributes, or be used as a generic constraint.

`readonly record struct NpcId(uint Value)`:
- Compiler enforces `NpcId` vs `ZoneId` at every call site.
- Value equality and `ToString` come for free from `record`.
- Zero heap allocation; same memory layout as `uint`.
- One declaration in `QuestForge.Adapters/Types/Identifiers.cs`, used everywhere.

The runtime cost is one `.Value` extraction at every Dalamud boundary in Phase 6. That is a trivial cost for the type safety the engine gets across the entire codebase.

### 4. `Result<T>` is written by hand, not a NuGet dependency

ADAPTERS.md §3.4 specifies the shape. We extend it with a `Unit` type and a `.Value` shorthand to resolve two issues: (a) `public abstract record Result` and `public static class Result` cannot share a name in C#, and (b) `result.Value` is ergonomic at call sites.

```csharp
// Unit type — zero-sized, for void-returning adapter operations
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

// Generic result for value-returning operations
public abstract record Result<T>
{
    public sealed record Success(T Value) : Result<T>;
    public sealed record Failure(string Reason, string? Detail = null) : Result<T>;

    public bool IsSuccess => this is Success;

    // ValueOrDefault: returns default on Failure (safe)
    public T? ValueOrDefault => this is Success s ? s.Value : default;

    // Value: throws on Failure (use when callers have already checked IsSuccess)
    public T Value => this is Success s
        ? s.Value
        : throw new InvalidOperationException($"Result is Failure: {((Failure)this).Reason}");
}

// Static helpers — no naming conflict because there is no Result record
public static class Result
{
    public static Result<T>.Success  Ok<T>(T value)                             => new(value);
    public static Result<T>.Failure  Fail<T>(string reason, string? detail = null) => new(reason, detail);

    // Void convenience — callers write Result.Ok() / Result.Fail("reason")
    public static Result<Unit>.Success  Ok()                                    => new(Unit.Value);
    public static Result<Unit>.Failure  Fail(string reason, string? detail = null) => new(reason, detail);
}
```

Void-returning adapter methods return `Task<Result<Unit>>`. The ADAPTERS.md spec used a non-generic `Result` abstract record for these; `Result<Unit>` is the equivalent without the naming conflict. Call sites write `Result.Ok()` and `Result.Fail("reason")` — identical ergonomics, one fewer type to learn.

We could pull in `OneOf`, `LanguageExt`, or `FluentResults`. We do not, because:
- The type is ~25 lines. The dependency cost exceeds the value.
- `Result<T>` participates in trace recording (the engine matches on `Reason`). A custom type lets us add trace-friendly helpers without convincing a library author.

### 5. `Task<Result<T>>`, not `ValueTask<Result<T>>`

ADAPTERS.md §2.2 specifies async on every IO-touching method. The choice between `Task` and `ValueTask` is open.

We choose `Task<Result<T>>` because:
- Every interface method is intended to be wrapped by the recording proxy (ADAPTERS.md §4.8). The proxy awaits the inner call and writes a trace event. There is no synchronous fast path.
- `ValueTask` is mostly useful for hot-path methods that complete synchronously most of the time. Adapter methods marshal to the Dalamud framework thread on every call — they are never synchronous.
- `Task` is the default convention; `ValueTask` has documented usage hazards (can only be awaited once, etc.) that show up in proxy and replay code.

`ITimingProfile` is the documented exception (ADAPTERS.md §10.1) — its methods are synchronous because they are pure functions of seeded RNG and are called every action.

### 6. Fakes are scriptable AND observable

The done-criteria test in NEXT_STEPS.md establishes the pattern:

```csharp
state.SetZone(132);                                      // scriptable
state.SetPosition(new WorldPosition(10, 0, 20));         // scriptable
// ... engine runs against the fake ...
Assert.Equal(1, nav.RecordedNavigationRequests.Count);   // observable
```

Every fake exposes both:
- **`SetX(...)`** / **`Script(...)`** methods that mutate the fake's internal state before the engine runs.
- **`RecordedX` / `Received...` properties** that expose an append-only list of calls the engine made, for post-run assertions.

The naming convention is uniform across all 10 fakes — see §2.5 of this plan.

### 7. Fakes honor `CancellationToken`

Every async fake method begins with `ct.ThrowIfCancellationRequested()`. Reason: the Phase 4 engine has a stop button. If a unit test passes a cancellation token, the fake must throw `OperationCanceledException`, not silently complete. This catches infinite-loop bugs in engine recovery ladders during testing.

The fakes do not introduce delays. They complete synchronously (returning `Task.FromResult(...)`) when not cancelled, so engine tests run fast.

### 8. The `Fakes` project is a normal class library, not a `<IsTestProject>true</IsTestProject>` project

The Phase 4 engine tests will reference `QuestForge.Adapters.Fakes` directly. Marking it as a test project would prevent that. The fakes are production-quality test infrastructure, not tests themselves; `QuestForge.Adapters.Tests` is the project that uses xUnit.

### 9. No `IRecordingProxy` in Phase 3

ADAPTERS.md §4.8 describes a `RecordingGameStateProvider` that wraps any `IGameStateProvider` and emits trace events. This is Phase 5 work. Phase 3 builds only the inner interface and the fake implementation. The proxy is added without changing the interface contract.

---

## Task 1 — `QuestForge.Adapters` project

### 1.1 Project scaffold

Add to `QuestForge.sln`:

```
QuestForge.Adapters/
    QuestForge.Adapters.csproj
    Types/
        Identifiers.cs          strong-typed record structs
        Geometry.cs             WorldPosition
        References.cs           NpcReference, InteractableReference, InteractableKind
        Result.cs               Result<T>, Result, helpers
    State/
        IGameStateProvider.cs   + PlayerStateSnapshot, MountState, InstanceKind,
                                  DutyAvailability, NewGamePlusState, UiState,
                                  DialogueState, TravelCapability
        IQuestState.cs          + QuestStatus, QuestUnlockReason, QuestReward,
                                  RewardSelectionStrategy
    Movement/
        INavigator.cs           + NavigationOptions, NavigationOutcome, NavmeshInfo,
                                  NavmeshStatus
        ITeleporter.cs          + TeleportOutcome
    Interaction/
        IInteractor.cs          + InteractOutcome, DialogueOutcome, DutyEntryOutcome,
                                  SpdEntryOutcome, DutyDifficulty, DutyFallbackPolicy,
                                  UseItemOutcome, InteractableOrNpc, AsNpc, AsObject,
                                  ChatChannel
        IDialogueResolver.cs    + GameLanguage
    Combat/
        ICombat.cs              + CombatOutcome, UseActionOutcome, CombatPluginInfo
    Gear/
        IGearManager.cs         + EquippedItem, EquippedItemCondition, GearItem,
                                  EquipSlot, EquipOutcome, JobChangeOutcome,
                                  RepairOutcome, GearSelectionMethod, RepairPreference
    Timing/
        ITimingProfile.cs       + StimulusType, SessionContext
    Minigames/
        IMinigameSkipper.cs     + MinigameKind, SkipOutcome
    ITraceWriter.cs             minimal stub; Phase 5 replaces with JSONL implementation
```

**`QuestForge.Adapters.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>QuestForge.Adapters</RootNamespace>
  </PropertyGroup>
</Project>
```

**No references to:** Dalamud, ECommons, FFXIVClientStructs, Lumina, or `QuestForge.Schema`. Adapters must be game-agnostic and schema-agnostic. (The engine bridges between schema-shaped quest definitions and adapter-shaped commands. The adapter layer does not know what a "quest definition" is.)

### 1.2 Namespace layout

```
QuestForge.Adapters             interfaces (one per file, named for the interface)
QuestForge.Adapters.Types       Result, identifiers, geometry, references
QuestForge.Adapters.State       enums and records for state interfaces
QuestForge.Adapters.Movement    enums and records for movement interfaces
QuestForge.Adapters.Interaction enums and records for interaction interfaces
QuestForge.Adapters.Combat      enums and records for combat interfaces
QuestForge.Adapters.Gear        enums and records for gear interfaces
QuestForge.Adapters.Timing      enums and records for timing
QuestForge.Adapters.Minigames   enums and records for minigame skipping
```

All 10 interface types live in the root `QuestForge.Adapters` namespace. Their supporting enums/records live in topical sub-namespaces. This matches the engine's expected `using QuestForge.Adapters;` for interfaces with topical `using` lines where supporting types are needed.

### 1.3 Strong-typed identifiers (`Types/Identifiers.cs`)

Exactly as ADAPTERS.md §3.1:

```csharp
namespace QuestForge.Adapters.Types;

public readonly record struct ZoneId(uint Value);
public readonly record struct NpcId(uint Value);
public readonly record struct QuestId(uint Value);
public readonly record struct AetheryteId(uint Value);
public readonly record struct AethernetId(uint Value);
public readonly record struct ItemId(uint Value);
public readonly record struct JobId(uint Value);
public readonly record struct DialogueOptionId(uint Value);
public readonly record struct InteractableId(uint Value);
public readonly record struct DutyId(uint Value);
```

Phase 3 introduces all 10 identifiers from the spec. None are added later; the contract is closed for v1.

### 1.4 `Result<T>` (`Types/Result.cs`)

Exactly as ADAPTERS.md §3.4 plus the `Result.Ok` / `Result.Fail` static helpers from decision §4 above.

### 1.5 Interfaces

One file per interface, in the layout above. Each file contains:
- The interface itself
- Supporting enums and records that appear only in that interface's signatures

Cross-cutting types (`WorldPosition`, `NpcReference`, etc.) live in `Types/`.

The interface signatures are taken from ADAPTERS.md sections 4-13, which is updated in the same commit as the interface implementation. One translation applies throughout: **any method returning `Task<Result>` (non-generic) in older versions of ADAPTERS.md is `Task<Result<Unit>>` in the implementation** — the non-generic `Result` abstract record is removed to eliminate the C# naming conflict with `public static class Result`. ADAPTERS.md §3.4 is authoritative for the current type definitions.

### 1.6 What this project does NOT contain

- No implementations (those are Fakes for Phase 3, Dalamud for Phase 6)
- No constants or magic numbers (those belong to implementations)
- No XML doc comments beyond the spec text — the spec is the documentation. Reference ADAPTERS.md section numbers in `<see cref>` only if needed for navigation; do not duplicate prose.

---

## Task 2 — `QuestForge.Adapters.Fakes` project

### 2.1 Project scaffold

```
QuestForge.Adapters.Fakes/
    QuestForge.Adapters.Fakes.csproj
    State/
        FakeGameStateProvider.cs
        FakeQuestState.cs
    Movement/
        FakeNavigator.cs
        FakeTeleporter.cs
    Interaction/
        FakeInteractor.cs
        FakeDialogueResolver.cs
    Combat/
        FakeCombat.cs
    Gear/
        FakeGearManager.cs
    Timing/
        FakeTimingProfile.cs       deterministic seeded; not a "fake" of distributions,
                                   but a usable timing profile for tests
    Minigames/
        FakeMinigameSkipper.cs
    Recording/
        AdapterCall.cs             base record for recorded calls
        CallLog<T>.cs              shared thread-safe append-only list type
    FakeTraceWriter.cs             collects Write() calls for assertion; satisfies ITraceWriter
```

**`QuestForge.Adapters.Fakes.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\QuestForge.Adapters\QuestForge.Adapters.csproj" />
  </ItemGroup>
</Project>
```

### 2.2 Naming conventions across all fakes

| Member kind | Naming | Example |
|---|---|---|
| Scriptable scalar state | `SetX(value)` | `state.SetZone(new ZoneId(132))` |
| Scriptable scripted-result | `ScriptX(input, result)` | `nav.ScriptNextResult(NavigationOutcome.Arrived)` |
| Scriptable error injection | `FailNextX(reason)` | `nav.FailNextWith("vnavmesh-not-ready")` |
| Recorded calls | `RecordedX` (immutable view) | `nav.RecordedNavigationRequests` |
| Reset between tests | `Reset()` | `nav.Reset()` |

The done-criteria sample mixes `SetZone` (scriptable state) and `RecordedNavigationRequests` (observable history). Both patterns are required on every fake.

### 2.3 Recording infrastructure (`Recording/`)

Every fake records every call made against it. A shared type holds the recording:

```csharp
namespace QuestForge.Adapters.Fakes.Recording;

public abstract record AdapterCall(DateTimeOffset At);

public sealed class CallLog<T> where T : AdapterCall
{
    private readonly List<T> _calls = new();
    private readonly object _lock = new();

    public void Add(T call)
    {
        lock (_lock) _calls.Add(call);
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_lock) return _calls.ToArray();
    }

    public int Count
    {
        get { lock (_lock) return _calls.Count; }
    }

    public void Clear()
    {
        lock (_lock) _calls.Clear();
    }
}
```

Each fake declares its own call subtypes:

```csharp
public sealed record NavigationRequest(
    DateTimeOffset At,
    WorldPosition Destination,
    NavigationOptions Options
) : AdapterCall(At);
```

Recorded calls are exposed as `IReadOnlyList<NavigationRequest> RecordedNavigationRequests => _navLog.Snapshot();`. Tests get an immutable snapshot; they cannot mutate the fake's history.

The lock is required because the Phase 4 engine may invoke adapters from multiple tasks (postcondition polling can race with action issuance). Unit tests will not exercise this concurrency, but the Phase 5 trace recorder will, and the lock costs nothing.

### 2.4 Cancellation pattern

Every async fake method:

```csharp
// Signature matches ADAPTERS.md §6 exactly: destination + options + ct
public Task<Result<NavigationOutcome>> NavigateTo(
    WorldPosition destination,
    NavigationOptions options,
    CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    _navLog.Add(new NavigationRequest(DateTimeOffset.UtcNow, destination, options));

    // Default behavior: pretend we arrived. Test can override via ScriptNextResult.
    var result = _nextNavigationResult ?? Result.Ok(NavigationOutcome.Arrived);
    _nextNavigationResult = null;  // consume one-shot script

    return Task.FromResult(result);
}
```

`ct.ThrowIfCancellationRequested()` is called *before* recording the call. A cancelled call should not appear in the log.

### 2.5 Fake design per adapter

The plan specifies *what* each fake exposes, not *how* it implements the interface. The "how" is mechanical given the patterns in §2.2-2.4 above.

#### `FakeGameStateProvider` — state reader

**Scriptable:**
- `SetZone(ZoneId)`, `SetPosition(WorldPosition)`, `SetJob(JobId, int level)`
- `SetInCombat(bool)`, `SetMountState(MountState)`, `SetDead(bool)`
- `SetInstanceKind(InstanceKind)`, `SetNewGamePlusState(NewGamePlusState)`
- `AddNpc(NpcReference)`, `RemoveNpc(NpcId)`
- `AddInteractable(InteractableReference)`, `SetInteractableActive(InteractableId, bool)`
- `SetAetheryteAttuned(AetheryteId, bool)`, `SetUiState(UiState)`
- `SetInventory(ItemId, int count)`, `SetFreeInventorySlots(int)`, `SetGil(long)`
- `SetTravelCapability(ZoneId, TravelCapability)`

**Observable:**
- `RecordedReads` — every method call recorded with the method name and any arguments. Used by Phase 5 recording-proxy tests.

**Default state:** zone 0, position (0,0,0), job 1, level 1, alive, not in combat, dismounted, `InstanceKind.None`, no NPCs, no interactables, no items in inventory, zero gil, maximum free inventory slots, NG+ inactive. Tests script only what they care about.

#### `FakeQuestState` — state reader

**Scriptable:**
- `SetQuestStatus(QuestId, QuestStatus)`, `SetQuestSequence(QuestId, int)`
- `SetQuestFlags(QuestId, uint)`, `SetQuestFlagBit(QuestId, int bit, bool value)`
- `AddAcceptedQuest(QuestId)`, `RemoveAcceptedQuest(QuestId)`
- `SetQuestRewards(QuestId, IReadOnlyList<QuestReward>)`
- `SetWhyUnavailable(QuestId, QuestUnlockReason)`

**Observable:** as above.

#### `FakeNavigator` — action executor

Takes a `FakeGameStateProvider` in its constructor (so navigation can mutate the player's position).

**Scriptable:**
- `ScriptNextResult(NavigationOutcome)` — overrides default success
- `ScriptResultFor(WorldPosition, NavigationOutcome)` — keyed by destination
- `FailNextWith(string reason, string? detail = null)` — produces `Result.Fail`
- `SetNavmeshInfo(ZoneId, NavmeshInfo)`
- `SetTeleportPlayerOnArrival(bool)` — default true; when true, a successful `NavigateTo` calls `state.SetPosition(destination)`

**Observable:**
- `RecordedNavigationRequests` — list of `NavigationRequest(destination, options, at)`
- `IsNavigating` — explicitly settable via `SetIsNavigating(bool)`; not derived from `NavigateTo` / `Stop` because the fake completes synchronously and there is no in-flight state to observe. The engine polls this to detect whether navigation is still running; tests set it directly to simulate that condition.

**Done criteria 1 (state reader category):**
```csharp
var state = new FakeGameStateProvider();
state.SetZone(new ZoneId(132));
state.SetPosition(new WorldPosition(10, 0, 20));
var result = await state.GetPlayerPosition(CancellationToken.None);
Assert.Equal(new WorldPosition(10, 0, 20), (result as Result<WorldPosition>.Success)!.Value);
```

**Done criteria 2 (action executor category — from NEXT_STEPS.md, updated to typed API):**
```csharp
var state = new FakeGameStateProvider();
state.SetZone(new ZoneId(132));
state.SetPosition(new WorldPosition(10, 0, 20));
var nav = new FakeNavigator(state);
var result = await nav.NavigateTo(new WorldPosition(50, 0, 50), new NavigationOptions(), CancellationToken.None);
Assert.Equal(NavigationOutcome.Arrived, result.Value);   // .Value throws on Failure — safe here
Assert.Equal(1, nav.RecordedNavigationRequests.Count);
```
NEXT_STEPS.md simplified the signature. Three updates from the actual ADAPTERS.md §6 signature:
- `SetZone` takes `ZoneId` — no raw-primitive overloads.
- `NavigateTo` requires a `NavigationOptions` argument (record with defaulted fields — `new NavigationOptions()` gives stopping distance 3.0f, mount and flight enabled).
- `result.Value` compiles because `Result<T>` exposes `.Value` (throws on `Failure`) from §4 above.

#### `FakeTeleporter` — action executor

Takes a `FakeGameStateProvider` in its constructor (teleport relocates the player to the aetheryte's known zone/position).

**Scriptable:**
- `ScriptNextResult(TeleportOutcome)`
- `SetHomeAetheryte(AetheryteId?)`
- `SetReturnCooldown(TimeSpan)`, `SetTeleportCooldown(TimeSpan)`
- `RegisterAetheryte(AetheryteId, ZoneId, WorldPosition, long gilCost)`

**Observable:** `RecordedTeleports`, `RecordedReturns`.

#### `FakeInteractor` — action executor (largest fake)

Takes both `FakeGameStateProvider` and `FakeQuestState` in its constructor. Accepting a quest mutates quest-specific state (sequence, flags, status) via `FakeQuestState`, and game-visible state (UI, inventory) via `FakeGameStateProvider`.

**Scriptable:**
- `ScriptNextInteractResult(InteractOutcome)`
- `ScriptDialogueSequence(params DialogueOutcome[])` — consumed one at a time
- `OnSelectDialogueOption(Action<DialogueOptionId>)` — callback so tests can mutate state in response
- `OnAcceptQuest(QuestId, Action)`, `OnCompleteQuest(QuestId, Action)` — callbacks for state transitions
- `SetSpdResult(SpdEntryOutcome)`

**Observable:** `RecordedInteractions`, `RecordedDialogues`, `RecordedQuestLifecycleCalls`, etc.

The default behavior for `AcceptQuest(questId)` calls `_questState.AddAcceptedQuest(questId)` and `_questState.SetQuestStatus(questId, QuestStatus.Accepted)` on the injected `FakeQuestState` — most engine tests should not have to wire this manually.

#### `FakeDialogueResolver` — state reader

**Scriptable:**
- `RegisterText(string sheetReference, GameLanguage, string text)`
- `RegisterOption(string sheetReference, DialogueOptionId)`
- `SetActiveLanguage(GameLanguage)`

**Observable:** `RecordedResolutions`.

Default behavior: any unregistered sheet reference returns `Result.Fail("sheet-reference-not-found")`. This forces tests to set up the data they need explicitly — they cannot accidentally pass with a typo.

#### `FakeCombat` — minimal stub

The Phase 4 engine does not use this. The fake exists for the constructor signature.

**Scriptable:** `ScriptNextResult(CombatOutcome)`, `SetCombatPluginAvailable(bool)`.

**Observable:** `RecordedEngagements`.

Default behavior: returns `CombatOutcome.TargetDefeated` for any engagement call. `IsCombatPluginAvailable` returns true.

#### `FakeGearManager` — minimal stub

The Phase 4 engine does not use this.

**Scriptable:** `SetEquipped(EquipSlot, EquippedItem)`, `SetLowestCondition(int)`, `ScriptEquipResult(EquipOutcome)`, `ScriptRepairResult(RepairOutcome)`.

**Observable:** `RecordedEquips`, `RecordedRepairs`, `RecordedJobChanges`.

Default behavior: returns `EquipOutcome.NoChange` for every equip call, `RepairOutcome.Repaired` for every repair, `Equipped` slots = empty list. Condition reports 100%.

#### `FakeMinigameSkipper` — minimal stub

The Phase 4 engine does not use this.

**Scriptable:** `SetKindSkippable(MinigameKind, bool)`, `ScriptSkipResult(MinigameKind, SkipOutcome)`.

**Observable:** `RecordedSkipAttempts`.

Default behavior: `IsKindSkippable` returns false for all kinds; `SkipMinigame` returns `SkipOutcome.Unsupported`. This matches v1 spec (ADAPTERS.md §13.1 — only Sniping is implemented, gated by user opt-in which fakes assume is false).

#### `FakeTimingProfile` — deterministic synchronous

Unlike the other fakes, this one is essentially a real implementation. The "fake" aspect is that it produces deterministic output rather than sampled distributions.

**Scriptable:**
- `SetDelayFor(StimulusType, TimeSpan)` — fixed delay per stimulus
- `SetDecisionDelay(TimeSpan)` — fixed delay regardless of choice count
- `SetInterActionGap(TimeSpan)` — fixed gap
- `ScriptShouldTakeBreak(bool)` — overrides break logic

**Observable:** None — the methods are pure and synchronous. Tests assert on engine behavior, not on what the engine asked timing for.

**Default values:** all delays = `TimeSpan.Zero`, `InterActionGap = TimeSpan.FromMilliseconds(50)` (the documented floor from ADAPTERS.md §10.5), `ShouldTakeBreak = false`. Tests run with no real-time waits.

**Done criteria 3 (timing category):**
```csharp
var timing = new FakeTimingProfile();
timing.SetDelayFor(StimulusType.Dialogue, TimeSpan.FromMilliseconds(500));
Assert.Equal(TimeSpan.FromMilliseconds(500), timing.ReactionDelay(StimulusType.Dialogue));
Assert.Equal(TimeSpan.FromMilliseconds(50), timing.InterActionGap());
```

---

## Task 3 — `QuestForge.Adapters.Tests` project

### 3.1 Project scaffold

```
QuestForge.Adapters.Tests/
    QuestForge.Adapters.Tests.csproj
    Types/
        ResultTests.cs
        IdentifierTests.cs
    Fakes/
        FakeGameStateProviderTests.cs
        FakeQuestStateTests.cs
        FakeNavigatorTests.cs
        FakeTeleporterTests.cs
        FakeInteractorTests.cs
        FakeDialogueResolverTests.cs
        FakeCombatTests.cs
        FakeGearManagerTests.cs
        FakeTimingProfileTests.cs
        FakeMinigameSkipperTests.cs
    Integration/
        DoneCriteriaTest.cs       NEXT_STEPS.md verbatim
        EngineConstructorSurfaceTest.cs   verifies all 10 fakes can construct a
                                          mock engine-shaped delegate
```

**`QuestForge.Adapters.Tests.csproj`:** xUnit, references both `QuestForge.Adapters` and `QuestForge.Adapters.Fakes`.

### 3.2 What these tests cover

These tests verify the **fakes**, not the interfaces. They prove:

1. **Scriptable state actually round-trips.** `state.SetZone(new ZoneId(132))` followed by `state.GetPlayerZone(ct)` returns `new ZoneId(132)`.
2. **Recorded calls capture the right data.** A call to `nav.NavigateTo(pos, opts, ct)` appears in `RecordedNavigationRequests` with the same `pos` and `opts`.
3. **Cancellation throws.** Calling an adapter with a pre-cancelled token throws `OperationCanceledException` and does not record the call.
4. **Default behaviors are documented.** A fresh `FakeNavigator` returns `Arrived`. A fresh `FakeDialogueResolver` returns `sheet-reference-not-found`. The tests pin these.
5. **Cross-fake wiring works.** `FakeNavigator` constructed with a `FakeGameStateProvider`, on `NavigateTo(dest)`, updates `state.GetPlayerPosition(...)` to `dest` (when `SetTeleportPlayerOnArrival(true)`).
6. **Result<T> pattern-matching is ergonomic.** A handful of tests exercise the `is Success` and `is Failure` patterns to confirm the type works under realistic call sites.

These tests are NOT engine tests. The engine does not exist yet. We are proving the test infrastructure works before we depend on it.

### 3.3 Given-When-Then specifications

#### 3.3.1 `Result<T>`

Given `Result.Ok(42)` → `IsSuccess` is true, `ValueOrDefault` is 42.

Given `Result.Fail<int>("not-found")` → `IsSuccess` is false, `ValueOrDefault` is 0.

Given two `Result.Ok(42)` → records compare equal.

Given `Result.Fail<int>("not-found", "detail-a")` and `Result.Fail<int>("not-found", "detail-b")` → not equal (Detail participates in equality).

#### 3.3.2 Identifiers

Given `new NpcId(123)` and `new NpcId(123)` → equal.

Given `new NpcId(123)` and `new ZoneId(123)` → compile error if assigned to the same variable; no implicit conversion.

Given `default(NpcId)` → `Value` is 0.

#### 3.3.3 `FakeGameStateProvider`

Given a fresh provider → `GetPlayerZone(ct)` returns `Result.Ok(new ZoneId(0))`.

Given `SetZone(new ZoneId(132))` then `GetPlayerZone(ct)` → returns `Result.Ok(new ZoneId(132))`.

Given `AddNpc(npc1)` then `FindNpc(npc1.Id, ct)` → returns `Result.Ok(npc1)`.

Given `AddNpc(npc1)` then `FindNpc(new NpcId(999), ct)` → returns `Result.Ok(null)`.

Given a pre-cancelled token → throws `OperationCanceledException`.

#### 3.3.4 `FakeNavigator`

Given `NavigateTo(dest, opts, ct)` on a fresh navigator wired to a state → returns `Result.Ok(NavigationOutcome.Arrived)`, records 1 request, and `state.GetPlayerPosition` now returns `dest`.

Given `FailNextWith("navmesh-not-ready")` then `NavigateTo(...)` → returns `Result<NavigationOutcome>.Failure("navmesh-not-ready", null)`, records 1 request, and `state.GetPlayerPosition` is unchanged.

Given `ScriptNextResult(NavigationOutcome.Interrupted)` then `NavigateTo(...)` → returns `Interrupted`, position unchanged.

Given `SetTeleportPlayerOnArrival(false)` then `NavigateTo(...)` → returns `Arrived`, but `state.GetPlayerPosition` is unchanged.

Given a pre-cancelled token → throws, `RecordedNavigationRequests.Count == 0`.

#### 3.3.5 `FakeInteractor`

Given `AcceptQuest(new QuestId(66130), ct)` on a fresh interactor wired to a state → `state.IsQuestAccepted(...)` becomes true and `state.GetQuestStatus(...)` is `Accepted`.

Given `OnAcceptQuest(qid, () => state.SetQuestSequence(qid, 1))` then `AcceptQuest(qid)` → both the default state transition AND the callback fire.

Given `ScriptDialogueSequence(Advanced, Advanced, DialogueClosed)` then three `AdvanceDialogue` calls → returns those three outcomes in order.

#### 3.3.6 `FakeTimingProfile`

Given fresh profile → `ReactionDelay(any)` returns `TimeSpan.Zero`, `InterActionGap()` returns 50ms.

Given `SetDelayFor(StimulusType.Dialogue, TimeSpan.FromMilliseconds(500))` → `ReactionDelay(Dialogue)` returns 500ms, `ReactionDelay(CombatStart)` still returns Zero.

#### 3.3.7 Done-criteria integration test (verbatim from NEXT_STEPS.md)

The exact code block from §"Done when" of Phase 3 in NEXT_STEPS.md, asserting both the `NavigationOutcome.Arrived` result and `RecordedNavigationRequests.Count == 1`.

#### 3.3.8 Engine-constructor surface test

Construct a `record EngineDependencies(...)` that takes all 10 adapter interfaces by type. Instantiate it with all 10 fakes. Assert it compiles and runs. This is a smoke test that the constructor signature in ADAPTERS.md §14.1 is wired correctly across the interfaces and fakes.

---

## Task 4 — Solution wiring

### 4.1 `QuestForge.sln` updates

Add three projects with appropriate GUIDs:
- `QuestForge.Adapters`
- `QuestForge.Adapters.Fakes`
- `QuestForge.Adapters.Tests`

Add a "Future phases — not yet implemented" solution folder containing empty stubs:
- `QuestForge.Engine` — empty csproj, no source files. Phase 4 fills it.
- `QuestForge.Adapters.Dalamud` — empty csproj. Phase 6 fills it.
- `QuestForge.Plugin` — empty csproj. Phase 6 fills it.

The placeholders are added now so the solution reflects the planned architecture and so adding code to them later does not require solution file changes mid-PR. Empty csprojs build cleanly and produce no output.

### 4.2 `ITraceWriter` in `QuestForge.Adapters`

ADAPTERS.md §14.1 defines `ITraceWriter` as part of the engine constructor signature. The Phase 3 engine-constructor surface test (§3.3.8) references it. Rather than defining a stub in the test project and moving it in Phase 5, declare a minimal interface in `QuestForge.Adapters` now:

```csharp
namespace QuestForge.Adapters;

/// <summary>Append-only event sink. Implemented by TraceWriter in Phase 5.</summary>
public interface ITraceWriter
{
    void Write(object evt);   // Phase 5 refines this to a typed event hierarchy
}
```

Phase 5 refines `Write`'s parameter to a typed event type. **Call sites affected:**
- `FakeTraceWriter.Write` — needs updating (1 line)
- `RecordingGameStateProvider.Write` — needs updating (1 line)
- The engine constructor signature does **not** change — it still takes `ITraceWriter` by interface

The stub exists so the Phase 3 constructor surface test compiles today. Updating two implementations in Phase 5 is the expected cost of a typed event hierarchy. `QuestForge.Adapters.Fakes` adds a `FakeTraceWriter` alongside the other fakes.

### 4.3 `Directory.Build.props` — no changes required

The existing props file applies `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable` to all projects. `TreatWarningsAsErrors` is per-project (existing convention).

---

## Task 5 — Done criteria

Phase 3 is done when **all of the following** pass:

1. `dotnet build` succeeds with zero warnings (warnings are errors per project config).
2. `dotnet test QuestForge.Adapters.Tests` passes with green.
3. The verbatim done-criteria test from NEXT_STEPS.md (§3.3.7 above) is in the test project and green.
4. Each adapter category has at least one passing test as described in §2.5:
   - State reader: `FakeGameStateProvider` round-trips `SetZone` → `GetPlayerZone`
   - Action executor: the NEXT_STEPS.md verbatim test
   - Timing: `FakeTimingProfile` returns scripted delays deterministically
5. The engine-constructor surface test (§3.3.8) compiles and runs, proving all 10 fakes satisfy the engine's constructor shape.
6. No project in the Phase 3 set references Dalamud, ECommons, FFXIVClientStructs, Lumina, or any IPC client library. `grep -r "Dalamud" QuestForge.Adapters*` returns nothing.

---

## Implementation order

**Phase A — Adapters project (interfaces only)**
1. Create `QuestForge.Adapters` csproj
2. Add `Types/Result.cs`, `Types/Identifiers.cs`, `Types/Geometry.cs`, `Types/References.cs`
3. Add the 10 interface files in their topical sub-folders, one at a time
4. Build succeeds; no test coverage yet
5. Commit: "Adapter interfaces complete"

**Phase B — Fakes project (test doubles)**
1. Create `QuestForge.Adapters.Fakes` csproj
2. Add `Recording/CallLog.cs`, `Recording/AdapterCall.cs`, `FakeTraceWriter.cs`
   - Commit: "Fakes: recording infrastructure + FakeTraceWriter"
3. State readers (no inter-fake dependencies):
   - `FakeGameStateProvider`, `FakeQuestState`, `FakeTimingProfile`, `FakeDialogueResolver`
   - Commit: "Fakes: state reader fakes (GameState, QuestState, Timing, Dialogue)"
4. Action executors (depend on state fakes):
   - `FakeNavigator`, `FakeTeleporter` (depend on `FakeGameStateProvider`)
   - `FakeInteractor` (depends on `FakeGameStateProvider`, `FakeQuestState`)
   - Commit: "Fakes: action executor fakes (Navigator, Teleporter, Interactor)"
5. Minimal stubs (Phase 4 engine does not use these):
   - `FakeCombat`, `FakeGearManager`, `FakeMinigameSkipper`
   - Commit: "Fakes: minimal stubs (Combat, GearManager, MinigameSkipper)"
6. Build succeeds; no test coverage yet

**Phase C — Tests project**
1. Create `QuestForge.Adapters.Tests` csproj with xUnit
2. Add `Types/ResultTests.cs` and `Types/IdentifierTests.cs`
   - Commit: "Tests: Result<T> and identifier types"
3. Add per-fake tests in the same order as Phase B
   - Commit: "Tests: state reader fakes (GameState, QuestState, Timing, Dialogue)"
   - Commit: "Tests: action executor fakes (Navigator, Teleporter, Interactor)"
   - Commit: "Tests: minimal stub fakes (Combat, GearManager, MinigameSkipper)"
4. Add the integration tests (`DoneCriteriaTest.cs`, `EngineConstructorSurfaceTest.cs`)
   - Commit: "Tests: integration — done-criteria and constructor surface"
5. All tests green

**Phase D — Solution placeholders**
1. Add empty csprojs for `QuestForge.Engine`, `QuestForge.Adapters.Dalamud`, `QuestForge.Plugin`
2. Wire them into the solution under a "Future phases — not yet implemented" folder (matches §4.1)
3. Build succeeds; no test changes
4. Commit: "Phase 4/6 project placeholders"

---

## What Phase 3 does NOT include

- **No Dalamud-backed implementations.** `QuestForge.Adapters.Dalamud` is an empty placeholder. Phase 6 fills it.
- **No engine.** `QuestForge.Engine` is an empty placeholder. Phase 4 fills it.
- **No recording proxy.** `RecordingGameStateProvider` (ADAPTERS.md §4.8) is Phase 5.
- **No trace implementation.** `ITraceWriter` is declared as a minimal stub in `QuestForge.Adapters` (§4.3) so the engine constructor surface test compiles. Phase 5 replaces the stub with the full JSONL implementation without changing call sites.
- **No IPC calls.** Nothing in the Phase 3 projects talks to vnavmesh, Lifestream, TextAdvance, BossMod, or any other plugin. The fakes are pure in-memory.
- **No quest-schema integration.** `QuestForge.Adapters` does not reference `QuestForge.Schema`. The engine (Phase 4) is what bridges between schema-shaped quest definitions and adapter-shaped commands.
- **No `EngineDecisionConfig` type.** ADAPTERS.md §9.2 and §12.1 reference this. It is a Phase 4 type, lives in `QuestForge.Engine`, and is not part of the adapter layer.
- **No real predicate evaluation.** Predicates exist in `questforge-tools` (Phase 2). The engine will consume them in Phase 4. The fakes do not parse predicates.
- **No persistence.** Fakes are in-memory only. No JSON, no files.

---

## Appendix A: Cross-references to ADAPTERS.md

| Plan section | ADAPTERS.md section |
|---|---|
| §1.3 Strong-typed identifiers | §3.1 |
| §1.4 Result<T> | §3.4 |
| §1.5 IGameStateProvider | §4 |
| §1.5 IQuestState | §5 |
| §1.5 INavigator | §6 |
| §1.5 ITeleporter | §7 |
| §1.5 IInteractor | §8 |
| §1.5 ICombat | §9 |
| §1.5 ITimingProfile | §10 |
| §1.5 IDialogueResolver | §11 |
| §1.5 IGearManager | §12 |
| §1.5 IMinigameSkipper | §13 |
| §1.1 / §4.2 ITraceWriter stub | §14.1 |
| §3.3.8 Engine constructor surface | §14.1 |

## Appendix B: Risks and mitigations

**Risk 1: Spec drift between ADAPTERS.md and the implemented interfaces.**
ADAPTERS.md is the contract; the code mirrors it. Any divergence found during implementation triggers a doc update in the same commit. Do not silently diverge.

**Risk 2: Fake defaults that hide engine bugs.**
A `FakeNavigator` that always returns `Arrived` could mask engine bugs that depend on navigation failure recovery. Mitigation: the engine tests in Phase 4 must scriptedly inject failures via `ScriptNextResult` / `FailNextWith` — they cannot rely on defaults for failure-path coverage. The Phase 4 plan must call this out.

**Risk 3: Fakes diverge from real adapter behavior.**
The fakes are abstractions of behavior, not simulations. They cannot capture, for example, vnavmesh's async pathfinding gap from SPIKE_NOTES.md §"vnavmesh". Phase 6's Dalamud-backed adapters carry that complexity. The engine writes against the contract (`NavigationOutcome.Arrived`) and the real adapter is responsible for delivering that contract regardless of underlying complexity. If the engine is found in Phase 6 to depend on vnavmesh-specific timing, the contract is broken — fix the adapter, not the engine.

**Risk 4: `Result<T>` ergonomics in engine code.**
The `is Success` / `is Failure` pattern is verbose. If Phase 4 engine code becomes hard to read, we can add helpers in `QuestForge.Adapters/Types/Result.cs` (`Match`, `Map`, `Bind`) without changing the type. Do not add helpers preemptively — let real call sites drive the API.