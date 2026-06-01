# InteractObjectStep + PickupItemStep Implementation Plan

**Status:** ready to implement
**Input docs:** `docs/SCHEMA.md` ss4.6, `docs/ADAPTERS.md` ss3.1/ss3.3/ss4/ss8, `docs/ARCHITECTURE.md`, `docs/NEXT_STEPS.md` ss11
**Output:** engine dispatches `InteractObject` for both `interact-object` and `pickup-item` step types; `IObjectInteractor` adapter is the new focused interface; two new predicates (`isAetherCurrentAttuned`, `npcExistsNearby`); tooling catch-up lands in the same slice.
**Phase dependencies:** Phase 11 (corpus expansion). Requires existing `InteractObjectStep`/`PickupItemStep` schema placeholders, `InteractableId` strong-typed identifier in `Adapters.Types`, `IGameStateProvider.FindInteractable`, `IInteractor.InteractWithObject`.

---

## Dependency graph

Two repos, strict build order per slice:

```
Slice 2 — Engine + Schema + Validator (questforge repo)
  QuestForge.Schema            ← flatten InteractObjectStep/PickupItemStep, delete InteractableTarget
  QuestForge.Adapters          ← new IObjectInteractor interface
  QuestForge.Adapters.Fakes    ← FakeObjectInteractor
  QuestForge.Engine            ← EngineAction.InteractObject, dispatch arm, ResolveInteractObject
  QuestForge.Engine.Tests      ← InteractObjectStepTests, DraftValidatorInteractObjectTests
  QuestForge.Schema.Tests      ← update RoundTripTests for new flat shapes

Slice 3 — Dalamud impl + EngineHost dispatch + tooling (questforge + questforge-tools repos)
  questforge:
    QuestForge.Adapters.Dalamud ← DalamudObjectInteractor
    QuestForge.Plugin           ← EngineHost dispatch arm, Debug accessor
  questforge-tools:
    QuestForge.Schema           ← mirror schema changes (flatten InteractableTarget out)
    QuestForge.Tools.Trace      ← TraceConstants, CapabilityInferrer (already done), FilenameLookup (already done)
    QuestForge.Predicates       ← FunctionRegistry entries for isAetherCurrentAttuned, npcExistsNearby
```

---

## Architectural decisions

### IO1: Two step types, one action, one adapter

**Decision:** `InteractObjectStep` and `PickupItemStep` both dispatch `EngineAction.InteractObject(InteractableId Target, Step? Origin)`. They share `IObjectInteractor` as their adapter. The step-type distinction exists for authoring clarity and capability tagging.

**Alternatives considered:**
- Widening `EngineAction.Interact(NpcId)` to accept `InteractableId`. Rejected: `InteractableId` and `NpcId` are distinct strong-typed IDs for a reason. Widening conflates object interaction (EventObj) with NPC interaction (EventNpc), which have different ObjectTable lookup paths and different game-side targeting semantics.
- Using `IInteractor.InteractWithObject` directly without a new adapter. Rejected: `IInteractor` is already the largest interface (20+ methods). A focused `IObjectInteractor` with a single method matches the adapter pattern used for `IItemUser`, `ICofferOpener`, `IEmoteExecutor`, `IChatSender`.

**What breaks if violated:** coercing `InteractableId` into `NpcId` (the current shim at line 770 of `QuestEngine.cs`) silently passes the wrong ID type through `InteractWith(NpcId)`, which looks up in the NPC table instead of the EventObj table. The game may target the wrong entity or find nothing.

**Testability:** `FakeObjectInteractor` records calls with `InteractableId`, making it type-safe and inspectable.

```csharp
// EngineAction.cs
public sealed record InteractObject(InteractableId Target, Step? Origin = null) : EngineAction;

// IObjectInteractor.cs (QuestForge.Adapters/Interaction/)
public interface IObjectInteractor
{
    Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct);
}
```

### IO2: Flat schema -- delete InteractableTarget

**Decision:** Flatten both step types from `InteractableTarget Target` (a record with `InteractableId`, `Zone`, `Position`) to three direct properties: `uint InteractableId`, `int Zone` (on Step base via inherited `Zone`), `Position3 Position`. Actually, after reviewing the schema, `Zone` is already on the base `Step` class and `Position` needs to be on the step for implied navigation. So the step shape becomes:

```csharp
public sealed class InteractObjectStep : Step
{
    public uint InteractableId { get; init; }
    public Position3? Position { get; init; }
}

public sealed class PickupItemStep : Step
{
    public uint InteractableId { get; init; }
    public Position3? Position { get; init; }
}
```

**Why not keep InteractableTarget?** The record adds indirection without adding value. NPC steps use `NpcLocation(NpcId, Zone, Position)` because NPCs genuinely have a zone+position tuple. But for interact-object/pickup-item, `Zone` is already on the base `Step` class and the only additional fields are `InteractableId` and `Position`. Flattening makes JSON authoring simpler and aligns with how `UseEmoteStep`, `SayChatMessageStep`, and `UseActionStep` carry their target fields directly.

**InteractableTarget consumers:** Before deleting `InteractableTarget` from `SharedValueTypes.cs`, grep confirms it is used only in:
- `InteractObjectStep.Target` (this file)
- `PickupItemStep.Target` (this file)
- `StepFactory.Build` (will be updated)
- `ImpliedNavigationTests` (will be updated)

`DutyTrigger.InteractableId` is a plain `uint?`, not an `InteractableTarget`, so it is unaffected.

**Alternatives considered:**
- Keeping `InteractableTarget` but making it a thin wrapper. Rejected: adds one indirection level for no benefit, and is inconsistent with the flat pattern established by `UseEmoteStep.EmoteId`, `UseActionStep.ActionId`, etc.

**What breaks if violated:** existing quest files that reference `"target": { "interactableId": N, "zone": M, "position": {...} }` must be migrated. The round-trip test will catch the shape change. StepFactory and ImpliedNavigationTests must be updated to use the flat fields.

### IO3: IObjectInteractor is separate from IInteractor

**Decision:** New interface `QuestForge.Adapters/Interaction/IObjectInteractor.cs` alongside `IInteractor.cs`. Single method: `Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct)`.

**Why not add to IInteractor?** `IInteractor` already has `InteractWithObject(InteractableId, ct)` returning `Result<InteractOutcome>`. However, the user's decision (item 3) explicitly says to create a new focused adapter separate from IInteractor. This follows the pattern of recent step-type slices where each step gets its own focused adapter (`IItemUser`, `ICofferOpener`, `IEmoteExecutor`, `IChatSender`).

Wait -- re-reading `IInteractor.cs`, it already HAS `InteractWithObject(InteractableId obj, CancellationToken ct)` returning `Result<InteractOutcome>`. The user wants a *new* `IObjectInteractor` that wraps this with a simpler return type (`Result<Unit>` instead of `Result<InteractOutcome>`), and potentially adds object-specific behavior like ObjectTable lookup by BaseId with ObjectKind.EventObj filter.

**Concrete surface:**

```csharp
// QuestForge.Adapters/Interaction/IObjectInteractor.cs
namespace QuestForge.Adapters.Interaction;

public interface IObjectInteractor
{
    Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct);
}
```

**Testability:** `FakeObjectInteractor` with `RecordedCalls`, `ScriptNextFailure`, `Reset()`, mirroring `FakeActionExecutor`.

### IO4: EngineAction.InteractObject replaces the NpcId shim

**Decision:** The current `QuestEngine.cs` line 770 coerces `InteractableId` into `NpcId` and dispatches `EngineAction.Interact`. This is replaced with:

```csharp
InteractObjectStep interactObj =>
    ResolveInteractOrNavigate(
        step, interactObj.Position, playerPos,
        new EngineAction.InteractObject(new InteractableId(interactObj.InteractableId), Origin: step)),
```

`PickupItemStep` dispatches identically (same action, different step type origin).

**ResolveInteractOrNavigate:** The existing helper works for any action type. The `interactAction` parameter is `EngineAction`, not a specific subtype. The `InteractObject` case passes its `Position` (nullable) for implied navigation and the `InteractObject` action for the in-range case. When `Position` is null, emit the action directly (fail-open).

### IO5: ResolveInteractObject pre-arm

**Decision:** No dedicated `ResolveInteractObject` async pre-arm is needed. The existing `ResolveActionForStep` switch pattern (synchronous, in `QuestEngine.cs`) handles both step types via `ResolveInteractOrNavigate`. The `InteractObject` action is dispatched identically to `Interact` -- navigate if out of range, interact if in range. No casting check, no combat check at the pre-arm level (those are handled by the general tick-level guards).

**Alternatives considered:**
- Adding a `ResolveInteractObject` pre-arm mirroring `ResolveTeleportAction`. Rejected: `InteractObject` has no async pre-conditions that differ from the default. Teleport has the `no adapter` guard; UseAction has `casting` and `cooldown` guards. Object interaction is a simple target-then-interact, same as `Interact(NpcId)`.

**What about combat? Can you interact with objects in combat?** Yes, FFXIV allows interacting with EventObj entities while in combat (e.g., clicking an aether current while enemies are nearby). The game may reject the interaction contextually (e.g., the object requires a casting bar that is interrupted by damage), but that is a game-side rejection, not something the engine should guard against. The engine's existing global defense (engage enemies before advancing) handles the common case.

### IO6: NOT dismount-exempt

**Decision:** `EngineAction.InteractObject` is NOT added to the lazy-dismount exemption list. The current exemption is `action is not EngineAction.Navigate and not EngineAction.Teleport and not EngineAction.EquipGear and not EngineAction.EquipBestGear and not EngineAction.RegisterGearset`. Interacting with a world object while mounted is rejected by the game (EventObj interaction requires dismounting). The lazy-dismount hook fires automatically before the `InteractObject` dispatch because `InteractObject` is not in the exemption list.

No test pair needed for this decision -- the existing dismount test infrastructure covers it. The mounted+prior-Navigate test pattern (from UseAction UA8/UA9) applies.

### IO7: Dialogue after interaction

**Decision:** EngineHost dispatch arm for `InteractObject` calls `TryCutsceneSkipConfirm()` followed by `_interactor.AdvanceDialogue(ct)`. Some EventObj interactions trigger cutscenes or dialogue (e.g., clicking an aether current triggers a brief cutscene). The cursor-walk pattern handles subsequent dialogue ticks. This mirrors the `Interact` dispatch arm but without `AcceptQuest`/`CompleteQuest` calls (object interaction never triggers quest accept/complete).

### IO8: No implicit postcondition

**Decision:** `InteractObject` and `PickupItem` steps rely entirely on the step's `Expect` field for advancement. There is no implicit postcondition like "object became inactive" or "item appeared in inventory". This matches the design principle: explicit postconditions, not trust.

**W1 NOT suppressed.** Unlike `UseActionStep` and `UseEmoteStep` (which suppress W1 because they have step-specific spin-loop warnings W7/W8), `InteractObjectStep` and `PickupItemStep` do NOT suppress W1. Authors should write Expect. The interact-then-advance pattern is identical to talk/accept/turn-in, all of which require Expect.

### IO9: Two new predicates

#### isAetherCurrentAttuned(dataId)

**Decision:** New predicate function `isAetherCurrentAttuned(dataId)` checks whether a specific Aether Current has been unlocked. This is distinct from `isAttuned(id)` which checks aetheryte/aethernet shard attunement.

**ClientStructs source:** `PlayerState.IsAetherCurrentUnlocked(uint aetherCurrentId)` where `aetherCurrentId` is the RowId of the AetherCurrent sheet (an EventId). The implementation reads `UnlockedAetherCurrentsBitArray.Get((int)aetherCurrentId - 0x2B0000)`.

**Surface area:**

```csharp
// IGameStateProvider.cs -- new method
Task<Result<bool>> IsAetherCurrentAttuned(uint aetherCurrentDataId, CancellationToken ct);
```

```csharp
// PredicateEvaluator.cs -- new case in EvaluateFunction switch
"isAetherCurrentAttuned" => (await _gameState.IsAetherCurrentAttuned(
    (uint)(long)args[0], ct)).ValueOrThrow,
```

```csharp
// FunctionRegistry.cs (questforge-tools) -- new entry
new("isAetherCurrentAttuned", new Fixed(1), [Int], Bool),
```

**Why not reuse `isAttuned`?** `isAttuned` is for aetheryte/aethernet shard attunement (reads UIState.IsAetheryteUnlocked). Aether Currents are a completely separate game system (collected to unlock flight in a zone). Different sheet, different ClientStructs field, different game state. Conflating them would be a correctness bug.

#### npcExistsNearby(dataId)

**Decision:** New predicate function `npcExistsNearby(dataId)` checks whether an NPC or EventObj with the given BaseId exists in the ObjectTable (i.e., is loaded and nearby). This is useful as a `skipIf` or `expect` condition for objects that despawn after interaction.

**Surface area:**

`IGameStateProvider` already has `FindNpc(NpcId)` and `FindInteractable(InteractableId)`. The predicate wraps both: try `FindNpc` first, then `FindInteractable`. Returns true if either finds a match. The name `npcExistsNearby` is slightly misleading for EventObj, but it matches the FFXIV community's loose use of "NPC" for any targetable entity. A more precise name like `entityExistsNearby` was considered but rejected for consistency with the schema's existing naming patterns.

```csharp
// PredicateEvaluator.cs -- new case
"npcExistsNearby" => await EvaluateNpcExistsNearby((long)args[0], ct),
```

```csharp
private async Task<object> EvaluateNpcExistsNearby(long dataId, CancellationToken ct)
{
    var npcResult = await _gameState.FindNpc(new NpcId((uint)dataId), ct);
    if (npcResult is Result<NpcReference?>.Success { Value: not null })
        return true;
    var objResult = await _gameState.FindInteractable(new InteractableId((uint)dataId), ct);
    return objResult is Result<InteractableReference?>.Success { Value: not null };
}
```

```csharp
// FunctionRegistry.cs (questforge-tools) -- new entry
new("npcExistsNearby", new Fixed(1), [Int], Bool),
```

### IO10: DraftValidator rules

**E19:** `InteractObjectStep.InteractableId == 0` -- error. Cannot interact with a zero-ID object.
**E20:** `PickupItemStep.InteractableId == 0` -- error. Cannot pick up a zero-ID object.

No E-rules for explicit-zero optional NPC targets because neither step type has an optional NPC target field.

W1 is NOT suppressed for these step types (decision IO8). No new W-rules.

### IO11: EngineHost dispatch arm

```csharp
case EngineAction.InteractObject io:
    DebounceLog($"interactobject:{io.Target.Value}",
        $"[InteractObject] interactableId={io.Target.Value}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _objectInteractor.InteractWithObject(io.Target, ct);
    await _interactor.AdvanceDialogue(ct);
    break;
```

Placement: between the existing `Interact` case and the `HandOver` case in `DispatchAction`.

### IO12: DalamudObjectInteractor implementation

The Dalamud implementation delegates to the existing `IInteractor.InteractWithObject`:

```csharp
// QuestForge.Adapters.Dalamud/Interaction/DalamudObjectInteractor.cs
public sealed class DalamudObjectInteractor : IObjectInteractor
{
    private readonly DalamudInteractor _interactor;

    public DalamudObjectInteractor(DalamudInteractor interactor)
        => _interactor = interactor;

    public async Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct)
    {
        var result = await _interactor.InteractWithObject(target, ct);
        return result switch
        {
            Result<InteractOutcome>.Success => Result.Ok(),
            Result<InteractOutcome>.Failure f => Result.Fail(f.Reason, f.Detail),
            _ => Result.Fail("unexpected")
        };
    }
}
```

**Why wrap `DalamudInteractor` instead of going directly to ClientStructs?** `DalamudInteractor.InteractWithObject` already handles the ObjectTable lookup by BaseId, targeting via `TargetManager`, and `TargetSystem.InteractWithObject` call. Wrapping it avoids code duplication and keeps the EventObj targeting logic in one place. The new adapter is a thin facade that converts `Result<InteractOutcome>` to `Result<Unit>`.

**No pure helper to extract.** The ObjectTable lookup is a Dalamud API call, not extractable logic. There is no enum mapping or status interpretation that warrants a separate pure-logic class.

### IO13: Recording proxy decision

**Decision:** No `RecordingObjectInteractor` wrapper. `IObjectInteractor` is a write-only adapter (fire-and-forget object interaction). The `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` already capture the write. This matches the precedent set by `IActionExecutor`, `IEmoteExecutor`, `IChatSender`, and `IItemUser` -- none of which have recording wrappers.

### IO14: Tooling catch-up -- scope and status

**CapabilityInferrer:** Already has entries for `InteractObjectStep` and `PickupItemStep` (confirmed in source). No change needed.

**TraceConstants:** Add `ActionInteractObject = "interactobject"`. No behavior change (`IsTerminalAction` only uses `done`/`awaituser`).

**FilenameLookup:** Already has entries for both step types (confirmed in source):
- `(["step:interact-object", "step:travel"], "with-interact-object.json")`
- `(["step:pickup-item", "step:travel"], "with-pickup-item.json")`

**DistinguishingCapPriority:** Already has entries for both (confirmed in source).

**FunctionRegistry:** Add two new entries:
- `new("isAetherCurrentAttuned", new Fixed(1), [Int], Bool)`
- `new("npcExistsNearby", new Fixed(1), [Int], Bool)`

**FIXTURES.md:** Add `actionType` row for `interactobject`. Capabilities table already has both step types.

### IO15: Schema JSON shape change

The old shape:
```json
{
  "type": "interact-object",
  "id": "ring-the-bell",
  "target": { "interactableId": 2001500, "zone": 134, "position": {"x": 1, "y": 2, "z": 3} },
  "expect": "questFlag(65657, 5)"
}
```

The new shape:
```json
{
  "type": "interact-object",
  "id": "ring-the-bell",
  "interactableId": 2001500,
  "zone": "134",
  "position": {"x": 1, "y": 2, "z": 3},
  "expect": "questFlag(65657, 5)"
}
```

Note: `zone` is already a base Step field (inherited). `position` is a new field on the step itself.

---

## Task breakdown

### Task 1 -- Schema changes (Slice 2)

**1.1** Flatten `InteractObjectStep`:
```csharp
public sealed class InteractObjectStep : Step
{
    public uint InteractableId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? Position { get; init; }
}
```

**1.2** Flatten `PickupItemStep`:
```csharp
public sealed class PickupItemStep : Step
{
    public uint InteractableId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Position3? Position { get; init; }
}
```

**1.3** Delete `InteractableTarget` record from `SharedValueTypes.cs`.

**1.4** Update `QuestForgeJsonContext.cs` if needed (already has entries for both types).

**1.5** Update `RoundTripTests.cs` to use new flat shape.

**1.6** Update `StepFactory.Build` for `"interact-object"` and `"pickup-item"` cases to use flat fields.

**1.7** Update `ImpliedNavigationTests.cs` test B5 to use flat step shape.

### Task 2 -- IObjectInteractor interface + fake (Slice 2)

**2.1** Create `QuestForge.Adapters/Interaction/IObjectInteractor.cs`:
```csharp
namespace QuestForge.Adapters.Interaction;

public interface IObjectInteractor
{
    Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct);
}
```

**2.2** Create `QuestForge.Adapters.Fakes/Interaction/FakeObjectInteractor.cs`:
```csharp
public sealed class FakeObjectInteractor : IObjectInteractor
{
    public record InteractCall(InteractableId Target, DateTimeOffset At) : AdapterCall(At);
    public CallLog<InteractCall> RecordedCalls { get; } = new();

    private (string Reason, string? Detail)? _nextFailure;

    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextFailure = null;
    }

    public Task<Result<Unit>> InteractWithObject(InteractableId target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new InteractCall(target, DateTimeOffset.UtcNow));
        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }
}
```

### Task 3 -- EngineAction.InteractObject + engine dispatch (Slice 2)

**3.1** Add to `EngineAction.cs`:
```csharp
public sealed record InteractObject(InteractableId Target, Step? Origin = null) : EngineAction;
```

**3.2** Add `IObjectInteractor?` as an optional ctor param on `QuestEngine`:
```csharp
IObjectInteractor? objectInteractor = null
```
Store as `_objectInteractor`. Null means AwaitUser at dispatch (consistent with other optional adapters).

**3.3** Replace the `InteractObjectStep` arm in `ResolveActionForStep`:
```csharp
InteractObjectStep interactObj when interactObj.Position is not null =>
    ResolveInteractOrNavigate(
        step, interactObj.Position, playerPos,
        new EngineAction.InteractObject(new InteractableId(interactObj.InteractableId), Origin: step)),

InteractObjectStep interactObj =>
    new EngineAction.InteractObject(new InteractableId(interactObj.InteractableId), Origin: step),

PickupItemStep pickup when pickup.Position is not null =>
    ResolveInteractOrNavigate(
        step, pickup.Position, playerPos,
        new EngineAction.InteractObject(new InteractableId(pickup.InteractableId), Origin: step)),

PickupItemStep pickup =>
    new EngineAction.InteractObject(new InteractableId(pickup.InteractableId), Origin: step),
```

**3.4** Add no-adapter guard: in the step dispatch or in a pre-arm check, if `_objectInteractor is null`, emit `AwaitUser("IObjectInteractor not configured")`. This follows the pattern of other optional adapters. Implementation: add an early check in `ResolveActionForStep` or in the `Tick` method's pre-dispatch section, mirroring how `_actionExecutor is null` is handled.

### Task 4 -- EngineTestHarness updates (Slice 2)

**4.1** Add `FakeObjectInteractor ObjectInteractor` property to `EngineTestHarness`.

**4.2** Pass `objectInteractor: ObjectInteractor` to `QuestEngine` constructor.

**4.3** Add `case EngineAction.InteractObject io:` arm in `RunToCompletion`:
```csharp
case EngineAction.InteractObject io:
    actions.Add(action);
    EmitActionSubmitted("InteractObject",
        JsonSerializer.SerializeToElement(io.Target, _jsonOpts));
    await ObjectInteractor.InteractWithObject(io.Target, ct);
    EmitActionCompleted("InteractObject",
        JsonSerializer.SerializeToElement(io.Target, _jsonOpts));
    break;
```

### Task 5 -- Predicates (Slice 2)

**5.1** Add `IsAetherCurrentAttuned(uint aetherCurrentDataId, CancellationToken ct)` to `IGameStateProvider`.

**5.2** Add stub implementation in `FakeGameStateProvider`:
```csharp
private readonly HashSet<uint> _aetherCurrents = new();
public void SetAetherCurrentAttuned(uint dataId, bool attuned = true)
{
    if (attuned) _aetherCurrents.Add(dataId);
    else _aetherCurrents.Remove(dataId);
}
public Task<Result<bool>> IsAetherCurrentAttuned(uint dataId, CancellationToken ct)
    => Task.FromResult<Result<bool>>(Result.Ok(_aetherCurrents.Contains(dataId)));
```

**5.3** Add `"isAetherCurrentAttuned"` case in `PredicateEvaluator.EvaluateFunction`:
```csharp
"isAetherCurrentAttuned" => (await _gameState.IsAetherCurrentAttuned(
    (uint)(long)args[0], ct)).ValueOrThrow,
```

**5.4** Add `"npcExistsNearby"` case in `PredicateEvaluator.EvaluateFunction`:
```csharp
"npcExistsNearby" => await EvaluateNpcExistsNearby((long)args[0], ct),
```

With helper:
```csharp
private async Task<object> EvaluateNpcExistsNearby(long dataId, CancellationToken ct)
{
    var npcResult = await _gameState.FindNpc(new NpcId((uint)dataId), ct);
    if (npcResult is Result<NpcReference?>.Success { Value: not null })
        return true;
    var objResult = await _gameState.FindInteractable(new InteractableId((uint)dataId), ct);
    return objResult is Result<InteractableReference?>.Success { Value: not null };
}
```

**5.5** Add `RecordingGameStateProvider` pass-through for `IsAetherCurrentAttuned`.

**5.6** Add `ReplayGameStateProvider` entry for `IsAetherCurrentAttuned` (if the replay provider uses a method-name map).

### Task 6 -- DraftValidator rules (Slice 2)

**6.1** Add E19:
```csharp
// E19: InteractObjectStep with InteractableId == 0
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is InteractObjectStep io && io.InteractableId == 0)
    {
        errors.Add(new DraftValidationError("E19",
            $"Step '{steps[i].StepId}' is an InteractObjectStep with InteractableId == 0.",
            [i]));
    }
}
```

**6.2** Add E20:
```csharp
// E20: PickupItemStep with InteractableId == 0
for (var i = 0; i < steps.Count; i++)
{
    if (steps[i].Raw is PickupItemStep pi && pi.InteractableId == 0)
    {
        errors.Add(new DraftValidationError("E20",
            $"Step '{steps[i].StepId}' is a PickupItemStep with InteractableId == 0.",
            [i]));
    }
}
```

### Task 7 -- Dalamud adapter + EngineHost dispatch (Slice 3)

**7.1** Create `QuestForge.Adapters.Dalamud/Interaction/DalamudObjectInteractor.cs` wrapping `DalamudInteractor.InteractWithObject`.

**7.2** EngineHost field declaration:
```csharp
private readonly DalamudObjectInteractor _objectInteractor;
```

**7.3** Construct in EngineHost constructor:
```csharp
_objectInteractor = new DalamudObjectInteractor(_interactor);
```

**7.4** Pass to QuestEngine in BeginRun:
```csharp
objectInteractor: _objectInteractor
```

**7.5** Dispatch arm in DispatchAction (between Interact and HandOver):
```csharp
case EngineAction.InteractObject io:
    DebounceLog($"interactobject:{io.Target.Value}",
        $"[InteractObject] interactableId={io.Target.Value}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _objectInteractor.InteractWithObject(io.Target, ct);
    await _interactor.AdvanceDialogue(ct);
    break;
```

**7.6** Add Debug accessor:
```csharp
public IObjectInteractor DebugObjectInteractor => _objectInteractor;
```

### Task 8 -- Tooling catch-up (Slice 3, questforge-tools)

**8.1** Add `ActionInteractObject = "interactobject"` to `TraceConstants.cs`.

**8.2** Add `isAetherCurrentAttuned` and `npcExistsNearby` to `FunctionRegistry.cs`:
```csharp
new("isAetherCurrentAttuned", new Fixed(1), [Int], Bool),
new("npcExistsNearby",        new Fixed(1), [Int], Bool),
```

**8.3** Mirror schema changes in the tools-repo copy of `QuestForge.Schema`: flatten `InteractObjectStep`, `PickupItemStep`, delete `InteractableTarget`.

**8.4** Update `FIXTURES.md`: add `interactobject` to the `actionType` canonical strings table.

### Task 9 -- Dalamud predicate implementations (Slice 3)

**9.1** Implement `DalamudGameStateProvider.IsAetherCurrentAttuned`:
```csharp
public Task<Result<bool>> IsAetherCurrentAttuned(uint aetherCurrentDataId, CancellationToken ct)
{
    unsafe
    {
        var playerState = PlayerState.Instance();
        return Task.FromResult<Result<bool>>(
            Result.Ok(playerState->IsAetherCurrentUnlocked(aetherCurrentDataId)));
    }
}
```

---

## Validation rule table

| Rule | Code | Condition | Message |
|------|------|-----------|---------|
| InteractObjectStep with InteractableId == 0 | E19 | `step.Raw is InteractObjectStep { InteractableId: 0 }` | "Step '{stepId}' is an InteractObjectStep with InteractableId == 0." |
| PickupItemStep with InteractableId == 0 | E20 | `step.Raw is PickupItemStep { InteractableId: 0 }` | "Step '{stepId}' is a PickupItemStep with InteractableId == 0." |

**W1 is NOT suppressed** for `InteractObjectStep` or `PickupItemStep`. Authors should provide Expect predicates.

---

## Given-When-Then specifications

### Engine dispatch tests (InteractObjectStepTests.cs)

**IO-T1: InteractObjectStep, player in range, emits InteractObject**
- Given: quest with one InteractObjectStep (InteractableId=99001, Position=(10,0,10)), player at (10,0,10), zone=132, expect=`questVariable(65,0) >= 1`
- When: engine ticks
- Then: emits `EngineAction.InteractObject { Target = InteractableId(99001) }`
- And: `FakeObjectInteractor.RecordedCalls` has exactly one call with `Target.Value == 99001`

**IO-T2: InteractObjectStep, player out of range, emits Navigate**
- Given: quest with one InteractObjectStep (InteractableId=99001, Position=(100,0,100)), player at (0,0,0), zone=132
- When: engine ticks
- Then: emits `EngineAction.Navigate` to destination (100,0,100) with StopDistance=3.0

**IO-T3: PickupItemStep, player in range, emits InteractObject (same action)**
- Given: quest with one PickupItemStep (InteractableId=2001234, Position=(5,0,5)), player at (5,0,5), zone=134, expect=`questFlag(65657, 3)`
- When: engine ticks
- Then: emits `EngineAction.InteractObject { Target = InteractableId(2001234) }`

**IO-T4: InteractObjectStep, no adapter configured, emits AwaitUser**
- Given: quest with InteractObjectStep, engine constructed WITHOUT objectInteractor (null)
- When: engine ticks
- Then: emits `EngineAction.AwaitUser` with reason containing "IObjectInteractor"

**IO-T5: InteractObjectStep, Position is null, emits InteractObject directly**
- Given: quest with InteractObjectStep (InteractableId=99001, Position=null), player at (0,0,0)
- When: engine ticks
- Then: emits `EngineAction.InteractObject { Target = InteractableId(99001) }` (no Navigate)

**IO-T6: InteractObjectStep, expect satisfied on entry, skips step**
- Given: quest with InteractObjectStep, expect=`questVariable(65,0) >= 1`, questVariable already returns 1
- When: engine ticks
- Then: step is skipped (engine advances to next step or Done)

**IO-T7: InteractObjectStep, custom stopDistance, Navigate uses it**
- Given: quest with InteractObjectStep (Position=(50,0,50), StopDistance=7.0), player at (0,0,0)
- When: engine ticks
- Then: emits `EngineAction.Navigate` with `Options.StoppingDistance == 7.0`

**IO-T8: PickupItemStep with Position null, emits InteractObject directly**
- Given: quest with PickupItemStep (InteractableId=2001234, Position=null)
- When: engine ticks
- Then: emits `EngineAction.InteractObject` directly (no Navigate)

### Predicate tests

**IO-P1: isAetherCurrentAttuned returns true when attuned**
- Given: `FakeGameStateProvider.SetAetherCurrentAttuned(2818048, true)`
- When: evaluate `isAetherCurrentAttuned(2818048)`
- Then: returns true

**IO-P2: isAetherCurrentAttuned returns false when not attuned**
- Given: no aether currents set
- When: evaluate `isAetherCurrentAttuned(2818048)`
- Then: returns false

**IO-P3: npcExistsNearby returns true when NPC exists**
- Given: `FakeGameStateProvider.SetNearbyNpc(NpcId(1000789), position, distance)`
- When: evaluate `npcExistsNearby(1000789)`
- Then: returns true

**IO-P4: npcExistsNearby returns true when interactable exists (not NPC)**
- Given: `FakeGameStateProvider.SetNearbyInteractable(InteractableId(2001234), position, distance)`
- When: evaluate `npcExistsNearby(2001234)`
- Then: returns true (falls through NPC miss to interactable hit)

**IO-P5: npcExistsNearby returns false when neither exists**
- Given: no NPCs or interactables set
- When: evaluate `npcExistsNearby(9999999)`
- Then: returns false

### DraftValidator tests (DraftValidatorInteractObjectTests.cs)

**IO-V1: E19 fires for InteractObjectStep with InteractableId == 0**
- Given: draft with one InteractObjectStep where InteractableId = 0
- When: Validate(draft)
- Then: errors contains E19

**IO-V2: E20 fires for PickupItemStep with InteractableId == 0**
- Given: draft with one PickupItemStep where InteractableId = 0
- When: Validate(draft)
- Then: errors contains E20

**IO-V3: No E19/E20 for valid InteractableId**
- Given: draft with InteractObjectStep(InteractableId=2001234) and PickupItemStep(InteractableId=2001500)
- When: Validate(draft)
- Then: no E19 or E20 errors

**IO-V4: W1 fires for InteractObjectStep with no Expect**
- Given: draft with InteractObjectStep where Expect is null
- When: Validate(draft)
- Then: warnings contains W1 for that step

**IO-V5: W1 fires for PickupItemStep with no Expect**
- Given: draft with PickupItemStep where Expect is null
- When: Validate(draft)
- Then: warnings contains W1 for that step

### Schema round-trip tests

**IO-R1: InteractObjectStep flat shape round-trips**
- Given: InteractObjectStep with InteractableId=2001500, Position=(1,2,3), Zone="134", Expect="questFlag(65657, 5)"
- When: serialize then deserialize
- Then: all fields match, no `target` wrapper object

**IO-R2: PickupItemStep flat shape round-trips**
- Given: PickupItemStep with InteractableId=2001234, Position=(81.5,7.0,32.2), Zone="134", Expect="questFlag(65657, 3)"
- When: serialize then deserialize
- Then: all fields match, no `target` wrapper object

**IO-R3: InteractObjectStep with null Position round-trips**
- Given: InteractObjectStep with InteractableId=2001500, Position=null
- When: serialize then deserialize
- Then: Position is null, `position` key absent from JSON output

### Tools-repo tests

**IO-TT1: FunctionRegistry knows isAetherCurrentAttuned**
- Given: FunctionRegistry.All
- When: lookup "isAetherCurrentAttuned"
- Then: found, arity Fixed(1), param [Int], return Bool

**IO-TT2: FunctionRegistry knows npcExistsNearby**
- Given: FunctionRegistry.All
- When: lookup "npcExistsNearby"
- Then: found, arity Fixed(1), param [Int], return Bool

**IO-TT3: TraceConstants.ActionInteractObject is "interactobject"**
- Assertion: `TraceConstants.ActionInteractObject == "interactobject"`

---

## Implementation order

### Phase A -- Schema flatten (1-2 hours)
1. Flatten `InteractObjectStep` and `PickupItemStep` in `Step.cs`
2. Delete `InteractableTarget` from `SharedValueTypes.cs`
3. Update `StepFactory.Build` for both step types
4. Update `RoundTripTests.cs`
5. Update `ImpliedNavigationTests.cs` test B5
6. Build passes, all existing tests pass

### Phase B -- Adapter + Engine (2-3 hours)
1. Create `IObjectInteractor` interface
2. Create `FakeObjectInteractor`
3. Add `EngineAction.InteractObject` to `EngineAction.cs`
4. Add `IObjectInteractor?` ctor param to `QuestEngine`
5. Replace `InteractObjectStep` arm in `ResolveActionForStep` (remove NpcId shim)
6. Add `PickupItemStep` arm in `ResolveActionForStep`
7. Add no-adapter guard
8. Update `EngineTestHarness` (property, ctor, dispatch arm)
9. Write `InteractObjectStepTests.cs` (IO-T1 through IO-T8)

### Phase C -- Predicates (1-2 hours)
1. Add `IsAetherCurrentAttuned` to `IGameStateProvider`
2. Add stub in `FakeGameStateProvider`
3. Add `RecordingGameStateProvider` pass-through
4. Add both predicates to `PredicateEvaluator`
5. Write predicate tests (IO-P1 through IO-P5)

### Phase D -- Validator (30 min)
1. Add E19, E20 to `DraftValidator`
2. Write `DraftValidatorInteractObjectTests.cs` (IO-V1 through IO-V5)

### Phase E -- Dalamud + EngineHost (Slice 3, 1-2 hours)
1. Create `DalamudObjectInteractor`
2. Wire into `EngineHost` (field, ctor, BeginRun, dispatch arm, Debug accessor)
3. Implement `DalamudGameStateProvider.IsAetherCurrentAttuned` via ClientStructs

### Phase F -- Tooling catch-up (Slice 3, 1 hour)
1. Mirror schema changes in tools-repo
2. Add `TraceConstants.ActionInteractObject`
3. Add both predicates to `FunctionRegistry`
4. Update `FIXTURES.md`
5. Write tools-repo tests (IO-TT1 through IO-TT3)

---

## Done criteria

1. `InteractObjectStep` and `PickupItemStep` use flat schema (no `InteractableTarget` wrapper) and round-trip correctly.
2. `InteractableTarget` record is deleted from `SharedValueTypes.cs`.
3. Engine dispatches `EngineAction.InteractObject(InteractableId)` for both step types; the NpcId shim at QuestEngine.cs line 770 is removed.
4. `IObjectInteractor` interface exists with `FakeObjectInteractor` for tests.
5. `DalamudObjectInteractor` is wired into `EngineHost` with dispatch arm, navigator stop, cutscene skip, and dialogue advance.
6. `isAetherCurrentAttuned(dataId)` predicate works in both engine and validator (FunctionRegistry).
7. `npcExistsNearby(dataId)` predicate works in both engine and validator (FunctionRegistry).
8. DraftValidator emits E19 for `InteractObjectStep.InteractableId == 0` and E20 for `PickupItemStep.InteractableId == 0`.
9. W1 fires for both step types when Expect is null.
10. `TraceConstants.ActionInteractObject` documented.
11. All existing tests continue to pass (no regressions from schema flatten or dispatch changes).
12. `dotnet build` and `dotnet test` pass in both repos.

---

## Exclusions

- **Authoring inference for interact-object/pickup-item** -- deferred to Slice 5 per the fixed slice order. Signal research (EventObj interaction detection via ConditionFlag or ObjectTable polling) is out of scope for this plan.
- **Multi-target interact-object** -- `targets` array support for multiple interactables is not added. Only single-target `InteractableId` is in scope.
- **`interactableActive(id)` predicate changes** -- this predicate already exists in the FunctionRegistry. No changes needed.
- **RecordingObjectInteractor** -- not needed (write-only adapter; covered by action.submitted events).
- **In-game smoke test** -- Slice 4, not in this plan.
- **questforge-data schema migration** -- no quest files in the data repo currently use `interact-object` or `pickup-item`, so no migration is needed. If any exist, they must be updated to the flat shape.

---

## Scope guard for authoring inference (Slice 5, future plan)

The following signals are candidates for Slice 5 research. They are documented here for continuity but are NOT in scope:

- **ConditionFlag.OccupiedInQuestEvent**: transitions `false -> true -> false` during EventObj interactions. May serve as the primary signal for detecting interact-object completion.
- **ObjectTable polling**: monitoring EventObj despawn (for pickup-item) or state change (for interact-object) after interaction.
- **Distinguish interact-object vs pickup-item during inference**: both mechanisms are identical from the game's perspective. Default to `interact-object`; author edits to `pickup-item` if appropriate.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Tasks IO-T1 through IO-T8, IO-P1 through IO-P5, IO-V1 through IO-V5, IO-R1 through IO-R3, and IO-TT1 through IO-TT3.
- Happy paths: 8 scenarios (IO-T1, IO-T3, IO-T5, IO-T6, IO-T8, IO-P1, IO-P3, IO-R1)
- Edge cases: 7 scenarios (IO-T2, IO-T5, IO-T7, IO-T8, IO-P4, IO-R2, IO-R3)
- Error cases: 8 scenarios (IO-T4, IO-P2, IO-P5, IO-V1, IO-V2, IO-V4, IO-V5, IO-V3)
- Expected total: ~26 tests across QuestForge.Engine.Tests, QuestForge.Schema.Tests, and QuestForge.Tools.Trace.Tests
