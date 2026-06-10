# UseItemOnObjectStep -- Architect Spec

**Status:** Draft
**Phase:** 11 (Corpus Expansion)
**Step type discriminator:** `"use-item-on-object"`
**Slice:** 1 of 6 (Architect Spec)
**Author:** QuestForge System Architect
**Date:** 2026-06-10

---

## 1. Header

**Input documents:**
- `docs/SCHEMA.md` -- step type taxonomy, common step fields, predicate language
- `docs/ADAPTERS.md` -- adapter interfaces, `IInteractor`, `IObjectInteractor`, `IGameStateProvider`
- `docs/ARCHITECTURE.md` -- three-layer architecture, engine testability boundary
- `CLAUDE.md` -- fixed slice order, TDD role separation, new step type checklist

**Output (CI behavior changes):**
- `dotnet test QuestForge.Engine.Tests` gains ~17 new tests covering UseItemOnObjectStep dispatch, guards, edge cases, and validator rules
- `dotnet test QuestForge.Schema.Tests` gains 1 new round-trip test
- `dotnet test QuestForge.Engine.Tests --filter "DraftValidatorUseItemOnObject"` validates E27, E28, W13

**Phase dependencies:**
- InteractObjectStep (implemented) -- provides `ResolveInteractOrNavigate` pattern and `EngineAction.InteractObject`
- UseItemStep (implemented) -- provides `ItemKind` enum, `IItemUser` adapter
- HandOverItemStep (implemented) -- provides approach+interact+addon-drive dispatch pattern
- DraftValidator E/W rules up to E26/W12 (implemented)

---

## 2. Problem Statement

Many FFXIV quests require the player to:

1. Approach a world EventObj (glowing pillar, door, device)
2. Target and interact with the EventObj (right-click) -- this opens the `InventoryEvent` addon
3. The `InventoryEvent` addon shows a filtered view of the player's key items
4. Use the correct key item from within that addon (right-click item -> "Use")
5. The quest variable advances

This flow is structurally different from `UseItemStep`, which fires an item via `ActionManager.UseAction` in a single stateless API call -- it never approaches, never targets, never interacts, and never opens any addon. The new step type `UseItemOnObjectStep` captures this distinct interaction pattern.

**Closest analogs:**
- **Engine side:** `InteractObjectStep` -- approach via `ResolveInteractOrNavigate`, emit action when close
- **Host dispatch side:** `HandOverItemStep` -- interact with entity, then drive an addon
- **Guard chain:** `UseActionStep` -- casting guard, cooldown guard, action status
- **Schema fields:** `UseItemStep` -- `Kind`, `ItemId` field precedent

---

## 3. Dependency Graph

```
QuestForge.Schema                (UseItemOnObjectStep class)
    |
    v
QuestForge.Engine                (EngineAction.UseItemOnObject + engine resolver)
    |
    v
QuestForge.Engine.Tests          (17+ tests)
    |
QuestForge.Schema.Tests          (1 round-trip test)
    |
QuestForge.Engine/Authoring      (DraftValidator E27, E28, W13)
```

Build order: Schema -> Engine -> Tests. All in one PR per Slice 2 guidelines.

---

## 4. Architectural Decisions

### UIO1: New step type, not a UseItemStep variant

**Decision:** Introduce `UseItemOnObjectStep` as a distinct step type with discriminator `"use-item-on-object"`.

**Alternatives considered:**
- Adding a 4th target mode (`"eventObject"`) to `UseItemStep`. Rejected: `UseItemStep` fires items via `ActionManager.UseAction` in one API call. This step requires approach -> interact -> drive addon -> use item -- a fundamentally different flow. Overloading `UseItemStep` makes the engine resolver a complex multi-path switch and muddies the clear "one step type = one interaction pattern" design.
- Using `InteractObjectStep` + `UseItemStep` in sequence. Rejected: the two are coupled -- the item can only be used from within the `InventoryEvent` addon context that the interact opens. Splitting into two steps means the engine needs cross-step state tracking for "is the InventoryEvent addon still open." A single step type keeps the flow atomic.

**What breaks if violated:** If someone adds a 4th `UseItemStep` target mode, the engine resolver for `UseItemStep` grows a code path that needs implied navigation, object interaction, and addon driving -- all of which are foreign to the `UseItemStep` pattern. Tests would need to cover both the old stateless path and the new multi-phase path, leading to combinatorial explosion.

**Testability:** A dedicated step type gets its own test file (`UseItemOnObjectStepTests.cs`) with focused scenarios. No existing `UseItemStep` tests are affected.

### UIO2: Schema shape

**Decision:** The step class has four required domain fields:

```csharp
public sealed class UseItemOnObjectStep : Step
{
    public uint InteractableId { get; init; }
    public Position3 Position { get; init; } = default!;
    public ItemKind Kind { get; init; }
    public uint ItemId { get; init; }
}
```

**Fields:**
- `InteractableId` (uint, required) -- the EventObj the player interacts with to open the `InventoryEvent` addon.
- `Position` (Position3, required) -- world-space position of the EventObj. Required because the engine must navigate to it. Unlike `InteractObjectStep.Position` which is nullable (optional navigation hint), this is non-nullable because the entire point of the step is approach+interact.
- `Kind` (ItemKind, required) -- `KeyItem` or `InventoryItem`. Mirrors `UseItemStep.Kind`.
- `ItemId` (uint, required) -- Lumina item ID of the key item to use.

**Why Position is required (not nullable):** `InteractObjectStep` makes Position nullable because some interact-object steps are authored without position (the preceding TravelStep handles navigation). But `UseItemOnObjectStep` inherently requires approach -- the player must be near the EventObj to interact. Making Position required ensures the engine always has navigation coordinates. The validator catches missing Position at deserialization time (JSON will fail to deserialize a null Position3 into a non-nullable record field).

**Why no TargetNpcId or TargetPosition:** The target is always an EventObj identified by `InteractableId`. There is no NPC variant of this interaction pattern (that would be `HandOverItemStep`). There is no ground-position variant (that would be `UseItemStep` with `TargetPosition`).

### UIO3: Engine action record

**Decision:** New discriminated case on `EngineAction`:

```csharp
public sealed record UseItemOnObject(
    InteractableId Target,
    ItemKind Kind,
    uint ItemId,
    Step? Origin = null) : EngineAction;
```

**Alternatives considered:**
- Reusing `EngineAction.InteractObject` and relying on the step's Origin to carry the item context. Rejected: the host dispatch arm needs `Kind` and `ItemId` to drive the addon. Encoding this in the Origin step and casting it in `EngineHost.DispatchAction` is fragile and couples the host to a specific step type.
- Composing `EngineAction.InteractObject` followed by `EngineAction.UseItem`. Rejected: the engine emits one action per tick. Emitting two consecutive actions within one step requires multi-phase state tracking in the engine, which is not how any existing step works. The stateless retry pattern (engine re-emits the same action until Expect confirms completion) requires a single action that the host dispatch arm handles atomically.

**What breaks if violated:** If someone reuses `InteractObject`, the host dispatch arm for `InteractObject` fires `InteractWithObject` and `AdvanceDialogue` -- it does not use any item. The item would never be used.

### UIO4: Engine resolver -- hybrid of InteractObjectStep and UseItemStep

**Decision:** The resolver for `UseItemOnObjectStep` is a two-phase pattern:

1. **Phase 1 (navigation):** Uses `ResolveInteractOrNavigate` with the step's `Position` and the `UseItemOnObject` action as the close-enough action. If the player is far, emits `Navigate`. If close, falls through to Phase 2.
2. **Phase 2 (action):** Emits `EngineAction.UseItemOnObject` with the interactable ID, kind, and item ID.

The resolver also includes guards before emitting the action:
- IObjectInteractor null check -> AwaitUser (consistent with InteractObjectStep)
- Player casting -> Wait (consistent with UseActionStep, UseItemStep)
- Action cooldown -> Wait (consistent with all action-emitting steps)

```csharp
// In QuestEngine.cs -- new async pre-arm, between UseItemStep and EquipGearForQuestStep
if (step is UseItemOnObjectStep useItemOnObjStep)
{
    var useItemOnObj = await ResolveUseItemOnObject(useItemOnObjStep, playerPos, ct);
    return (useItemOnObj, step.Id, playerPos);
}
```

```csharp
private async Task<EngineAction> ResolveUseItemOnObject(
    UseItemOnObjectStep step, WorldPosition? playerPos, CancellationToken ct)
{
    // Guard 0: IObjectInteractor required
    if (_objectInteractor is null)
        return new EngineAction.AwaitUser(
            "UseItemOnObjectStep dispatched but no IObjectInteractor wired");

    // Guard 1: Navigate if player is far from the object
    var action = new EngineAction.UseItemOnObject(
        new InteractableId(step.InteractableId),
        step.Kind,
        step.ItemId,
        Origin: step);

    var navOrAction = ResolveInteractOrNavigate(step, step.Position, playerPos, action);
    if (navOrAction is EngineAction.Navigate)
        return navOrAction;

    // Guard 2: Player casting -> Wait
    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring use-item-on-object", Origin: step);

    // Guard 3: Action cooldown -> Wait
    var cooldown = CheckActionCooldown(step);
    if (cooldown is not null) return cooldown;

    RecordActionFired(step);
    return action;
}
```

**Rationale for guard order:**
1. Adapter null check first -- no point reading state if we can't dispatch.
2. Navigation before casting/cooldown -- if the player is far away, casting state is irrelevant (they need to move first).
3. Casting before cooldown -- casting is a hard blocker regardless of cooldown state.
4. Cooldown last -- only matters when the player is close and not casting.

This matches the `InteractObjectStep` pattern for navigation and the `UseItemStep` pattern for guards.

**Note:** `_lastResolvedStep` is NOT set in this async pre-arm (consistent with TeleportStep, PurchaseItemStep, UseActionStep, UseEmoteStep precedent).

### UIO5: No new adapter interface needed

**Decision:** No `IUseItemOnObjectExecutor` interface. The host dispatch arm composes existing adapters:

1. `_objectInteractor.InteractWithObject(target)` -- opens the InventoryEvent addon
2. `ActionManager.UseAction(ActionType.KeyItem, itemId)` -- uses the item within the addon context

The second call is the key insight from in-game research: once the interact establishes the event context, `ActionManager.UseAction(ActionType.KeyItem, itemId)` works exactly as if the player clicked "Use" in the InventoryEvent addon. This is the same mechanism the game uses internally.

**Alternatives considered:**
- New `IInventoryEventDriver` adapter interface with `UseKeyItemInInventoryEvent(uint itemId)`. Rejected: this would be a single-method interface wrapping a single `ActionManager.UseAction` call. The adapter layer adds no abstraction value here -- the call is identical to what `IItemUser` already does, just in a different context.
- Having the engine emit two actions (InteractObject then UseItem). Rejected: see UIO3 above.

**What breaks if violated:** If someone creates a new adapter interface, they add an interface file, a fake, a Dalamud impl, a harness property, and a ctor param -- all for one line of code. The builder TDD burden is disproportionate.

**Testability implication:** Engine tests only verify that `EngineAction.UseItemOnObject` is emitted with the correct fields. The multi-step host dispatch (interact + use item) is tested in-game (Slice 4). Engine tests do NOT test host dispatch logic -- that is consistent with every other step type.

### UIO6: EngineHost dispatch arm

**Decision:** The dispatch arm in `EngineHost.DispatchAction` handles `EngineAction.UseItemOnObject` as follows:

```csharp
case EngineAction.UseItemOnObject uio:
    DebounceLog(
        $"useitemonobject:{uio.Target.Value}:{uio.Kind}:{uio.ItemId}",
        $"[UseItemOnObject] interactableId={uio.Target.Value} kind={uio.Kind} itemId={uio.ItemId}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _objectInteractor.InteractWithObject(uio.Target, ct);
    await _itemUser.UseItem(uio.Kind, uio.ItemId, null, null, ct);
    break;
```

**Pattern:** This mirrors the `HandOver` dispatch arm:
1. Stop navigator if navigating (standard pre-action cleanup)
2. `TryCutsceneSkipConfirm()` (standard)
3. `InteractWithObject` to open the InventoryEvent addon
4. `UseItem` to fire the key item within the established event context

**Stateless retry:** If the InventoryEvent addon is not yet open when `UseItem` fires, the call may fail. The engine retries statelessly next tick -- it will re-emit `UseItemOnObject`, which will re-interact and re-attempt the item use. This is the same stateless retry pattern as every other action step type.

**IItemUser reuse:** The `_itemUser.UseItem(kind, itemId, null, null, ct)` call reuses the existing `IItemUser` interface. The `TargetNpcId` and `TargetPosition` are both null because the target is the EventObj already interacted with -- the game's event context handles the targeting.

### UIO7: Lazy dismount -- UseItemOnObject IS subject to lazy-dismount

**Decision:** `UseItemOnObject` is NOT in the lazy-dismount exemption list. The player must be dismounted to interact with EventObjs.

The exemption list currently exempts only `Navigate` and `Teleport`:
```csharp
is not EngineAction.Navigate and not EngineAction.Teleport
```

`UseItemOnObject` is not added to this list. The standard lazy-dismount hook in `EngineHost.DispatchAction` fires before the action dispatches, dismounting the player.

**Rationale:** The player cannot interact with world objects while mounted. Consistent with `InteractObject`, `HandOver`, `UseAction`, `UseEmote`, and all other interaction-type actions.

### UIO8: No InCombat guard

**Decision:** No `IsPlayerInCombat` check in the resolver. Consistent with `UseActionStep`, `UseItemStep`, `UseEmoteStep`, `SayChatMessageStep`.

**Rationale:** Some quests require using items on objects during combat scenarios (post-kill event triggers, etc.). Adding a combat guard would break these quests.

### UIO9: Validator rules

**Decision:** Three new validator rules:

| Code | Type | Condition | Message |
|------|------|-----------|---------|
| E27 | Error | `UseItemOnObjectStep.InteractableId == 0` | "Step '{stepId}' is a UseItemOnObjectStep with InteractableId == 0." |
| E28 | Error | `UseItemOnObjectStep.ItemId == 0` | "Step '{stepId}' is a UseItemOnObjectStep with ItemId == 0." |
| W13 | Warning | `UseItemOnObjectStep.Expect is null` | "Step '{stepId}' is a UseItemOnObjectStep with no 'expect' predicate -- without it the engine will spin-loop re-emitting the action. Add an expect predicate." |

Additionally, `UseItemOnObjectStep` is added to the W1 suppression guard:
```csharp
&& step.Raw is not UseActionStep
    and not UseEmoteStep
    and not SayChatMessageStep
    and not UseItemStep
    and not UseItemOnObjectStep   // <-- NEW
    and not EquipGearForQuestStep
    and not ChangeJobStep
    and not AethernetStep
```

This prevents W1 from double-firing alongside W13 on the same step.

**Position validation:** Position is structurally required (non-nullable `Position3`). If omitted from JSON, deserialization fails before the validator runs. No E-rule needed for Position.

### UIO10: Recording proxy decision

**Decision:** No `RecordingUseItemOnObjectExecutor` wrapper needed. The `action.submitted` / `action.completed` events emitted by `EngineHost.DispatchAction` already capture the write-only dispatch. This is a write-only adapter pattern (same as UseAction, UseEmote, UseItem, SayChatMessage).

### UIO11: InventoryEvent addon details (from in-game research)

The `InventoryEvent` addon (`FFXIVClientStructs.FFXIV.Client.UI.AddonInventoryEvent`) is the filtered key-item view that opens when interacting with certain EventObjs. Key findings:

- **AtkValues:** 8 values; `[0]` is Int:1, `[3]` is UInt:16 (likely a filter/category mask), `[4]` is Bool:True.
- **Components:** 5 RadioButton components for inventory tabs, plus a Button at node #13.
- **`IsVisible` and `IsReady`** are both true when the addon is open.
- **`FocusNode` is null** (unlike the Request addon which has a clickable FocusNode).
- **Key finding:** Once the interact opens the addon and establishes the event context, `ActionManager.UseAction(ActionType.KeyItem, itemId)` fires the item use. No need to programmatically navigate the addon UI. This is the simplest and most robust approach.

### UIO12: JSON discriminator

**Decision:** The discriminator string is `"use-item-on-object"` (kebab-case, consistent with all other multi-word step types: `"interact-object"`, `"pickup-item"`, `"hand-over-item"`, `"use-action"`, `"use-emote"`, `"use-item"`, `"equip-best-gear"`, `"equip-gear-for-quest"`, `"say-chat-message"`, `"change-job"`, `"register-gearset"`, `"open-coffers"`, `"purchase-item"`).

---

## 5. Validation Rule Table

| Code | Level | Step Type | Condition | Suppress | Message |
|------|-------|-----------|-----------|----------|---------|
| E27 | Error | UseItemOnObjectStep | `InteractableId == 0` | -- | "Step '{stepId}' is a UseItemOnObjectStep with InteractableId == 0." |
| E28 | Error | UseItemOnObjectStep | `ItemId == 0` | -- | "Step '{stepId}' is a UseItemOnObjectStep with ItemId == 0." |
| W13 | Warning | UseItemOnObjectStep | `Expect is null` | Suppresses W1 for this type | "Step '{stepId}' is a UseItemOnObjectStep with no 'expect' predicate -- without it the engine will spin-loop re-emitting the action. Add an expect predicate." |

---

## 6. Task Breakdown

### Task 1: Schema (QuestForge.Schema)

**Deliverables:**

1. Add `UseItemOnObjectStep` sealed class to `Step.cs`:
```csharp
public sealed class UseItemOnObjectStep : Step
{
    public uint InteractableId { get; init; }
    public Position3 Position { get; init; } = default!;
    public ItemKind Kind { get; init; }
    public uint ItemId { get; init; }
}
```

2. Add `[JsonDerivedType(typeof(UseItemOnObjectStep), "use-item-on-object")]` to the `Step` attribute list.

3. Add `[JsonSerializable(typeof(UseItemOnObjectStep))]` to `QuestForgeJsonContext.cs`.

4. Add JSON round-trip test in `QuestForge.Schema.Tests/RoundTripTests.cs`:
```csharp
[Fact]
public void UseItemOnObjectStep_RoundTrips()
{
    var step = new UseItemOnObjectStep
    {
        Id = "use-key-on-device",
        InteractableId = 2001500u,
        Position = new Position3(81.5f, 7.0f, 32.2f),
        Kind = ItemKind.KeyItem,
        ItemId = 2002001u,
        Expect = new PredicateExpect { Predicate = "questFlag(65657, 5)" }
    };

    var result = RoundTrip(step);

    Assert.Equal("use-key-on-device", result.Id);
    Assert.Equal(2001500u, result.InteractableId);
    Assert.Equal(81.5f, result.Position.X);
    Assert.Equal(ItemKind.KeyItem, result.Kind);
    Assert.Equal(2002001u, result.ItemId);
}
```

### Task 2: Engine Action (QuestForge.Engine)

**Deliverables:**

1. Add `UseItemOnObject` record to `EngineAction.cs`:
```csharp
public sealed record UseItemOnObject(
    InteractableId Target,
    ItemKind Kind,
    uint ItemId,
    Step? Origin = null) : EngineAction;
```

### Task 3: Engine Resolver (QuestForge.Engine/QuestEngine.cs)

**Deliverables:**

1. Add async pre-arm in the step dispatch chain (after UseItemStep, before EquipGearForQuestStep):
```csharp
// 6a5. UseItemOnObjectStep async arm
if (step is UseItemOnObjectStep useItemOnObjStep)
{
    var useItemOnObj = await ResolveUseItemOnObject(useItemOnObjStep, playerPos, ct);
    return (useItemOnObj, step.Id, playerPos);
}
```

2. Add `ResolveUseItemOnObject` method (see UIO4 for full implementation).

### Task 4: EngineTestHarness (QuestForge.Engine.Tests/Helpers)

**Deliverables:**

1. Add `case EngineAction.UseItemOnObject:` arm in `RunToCompletion`'s action dispatch. No adapter call needed -- the engine test harness only needs to not crash on the action type. The host dispatch arm (which actually calls adapters) is in `EngineHost`, not the test harness.

### Task 5: DraftValidator (QuestForge.Engine/Authoring/DraftValidator.cs)

**Deliverables:**

1. Add E27 rule: `UseItemOnObjectStep.InteractableId == 0`
2. Add E28 rule: `UseItemOnObjectStep.ItemId == 0`
3. Add W13 rule: `UseItemOnObjectStep.Expect is null` (message must contain "spin-loop")
4. Add `UseItemOnObjectStep` to W1 suppression guard

### Task 6: Tests (QuestForge.Engine.Tests)

See Section 7 (Given-When-Then Specs) for all test scenarios.

---

## 7. Given-When-Then Specs

### Engine Tests: `QuestForge.Engine.Tests/Engine/UseItemOnObjectStepTests.cs`

---

#### UIO_T1: Happy path -- player close, emits UseItemOnObject with correct fields

**Given:**
- Player position is (10, 0, 10) in zone 132
- QuestSequence for quest 95001 is 0
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95001, 3)"
- ObjectInteractor is wired

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.UseItemOnObject`
- `Target` is `InteractableId(99001)`
- `Kind` is `ItemKind.KeyItem`
- `ItemId` is `2002001`
- `Origin` is not null
- `Origin` is the UseItemOnObjectStep

---

#### UIO_T2: Player far from object -- emits Navigate first

**Given:**
- Player position is (0, 0, 0) in zone 132
- UseItemOnObjectStep with InteractableId=99001, Position=(100, 0, 100), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95002, 3)"
- ObjectInteractor is wired

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Navigate`
- Destination is approximately (100, 0, 100)
- StoppingDistance is 3.0 (default)

---

#### UIO_T3: Player close with custom StopDistance honored

**Given:**
- Player position is (10, 0, 10) in zone 132
- UseItemOnObjectStep with InteractableId=99001, Position=(14, 0, 10), StopDistance=5.0, Kind=KeyItem, ItemId=2002001, Expect="questFlag(95003, 3)"
- Player is within 5.0 units of position (distance = 4.0)
- ObjectInteractor is wired

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.UseItemOnObject` (NOT Navigate, because distance < StopDistance)

---

#### UIO_T4: Player casting -- Wait

**Given:**
- Player position is (10, 0, 10) in zone 132
- Player is casting (GameState.Casting = true)
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95004, 3)"
- ObjectInteractor is wired

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Wait`
- Reason contains "casting"

---

#### UIO_T5: Action cooldown active -- Wait

**Given:**
- Player position is (10, 0, 10) in zone 132
- Player is NOT casting
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95005, 3)"
- ObjectInteractor is wired
- Action cooldown has not elapsed (previous action fired within cooldown window)

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Wait`
- Reason contains "cooldown"

---

#### UIO_T6: Cancellation propagates

**Given:**
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95006, 3)"
- ObjectInteractor is wired
- CancellationToken is already cancelled

**When:** Engine.Tick(cancelledToken)

**Then:**
- `OperationCanceledException` is thrown (or `TaskCanceledException`)

---

#### UIO_T7: Mounted + prior Navigate -- lazy-dismount fires

**Given:**
- Player position is (100, 0, 100) in zone 132 (far from object)
- Player is mounted
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95007, 3)"
- ObjectInteractor is wired

**When:** Engine.Tick() returns Navigate, then simulate arrival (player now at (10, 0, 10)), then Engine.Tick() again

**Then:**
- First tick: `EngineAction.Navigate` (mount is OK during navigation)
- Second tick: `EngineAction.UseItemOnObject` (the harness's DispatchAction hook should have dismounted)

Note: The lazy-dismount is a `EngineHost`/`HarnessEngine` concern. The engine test verifies the correct action type is emitted; the harness's mount hook verifies dismount fires. This test uses `RunToCompletion` or the `HarnessEngine.Tick` wrapper which includes mount hooks.

---

#### UIO_T8: Standalone + mounted -- no dismount before UseItemOnObject emission

**Given:**
- Player position is (10, 0, 10) in zone 132 (close to object)
- Player is mounted
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95008, 3)"
- ObjectInteractor is wired
- No prior Navigate action in this tick

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.UseItemOnObject`
- Mount.DismountCallCount is 0 at engine level (dismount happens in host dispatch, not engine resolution)

Note: This mirrors the U9 / UE7 pattern -- the engine does not dismount; `EngineHost.DispatchAction`'s pre-switch hook handles dismount for non-exempt actions.

---

#### UIO_T9: Expect already satisfied -- step skipped

**Given:**
- QuestFlag(95009, 3) is already set (Expect is satisfied before first tick)
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95009, 3)"
- Quest has a second step after this one (e.g., a TalkStep)

**When:** Engine.Tick()

**Then:**
- Engine skips the UseItemOnObjectStep entirely
- The returned action corresponds to the NEXT step (not UseItemOnObject)

---

#### UIO_T10: Two-tick integration: UseItemOnObject fires, Expect satisfies, step completes

**Given:**
- Player position is (10, 0, 10) in zone 132
- QuestSequence for quest 95010 is 0
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95010, 3)"
- Quest has a second step (e.g., TalkStep with Expect="questSequence(95010) >= 1")
- ObjectInteractor is wired

**When:**
- Tick 1: Engine.Tick() -- emits UseItemOnObject
- Between ticks: set questFlag(95010, 3) = true (simulating the game advancing)
- Tick 2: Engine.Tick() -- Expect now satisfied

**Then:**
- Tick 1 returns `EngineAction.UseItemOnObject`
- Tick 2 returns an action for the NEXT step (not UseItemOnObject again)

---

#### UIO_T11: IObjectInteractor not wired -- AwaitUser

**Given:**
- Player position is (10, 0, 10) in zone 132
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95011, 3)"
- ObjectInteractor is NOT wired (null)

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "IObjectInteractor"

---

#### UIO_T12: Null playerPos -- fail-open, emits UseItemOnObject

**Given:**
- Player position is null (GameState returns failure for position)
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=KeyItem, ItemId=2002001, Expect="questFlag(95012, 3)"
- ObjectInteractor is wired

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.UseItemOnObject` (not Navigate)
- This is the fail-open behavior of `ResolveInteractOrNavigate` when playerPos is null

---

#### UIO_T13: InventoryItem kind -- emits correct Kind field

**Given:**
- Player position is (10, 0, 10) in zone 132
- UseItemOnObjectStep with InteractableId=99001, Position=(10, 0, 10), Kind=InventoryItem, ItemId=5001, Expect="questFlag(95013, 3)"
- ObjectInteractor is wired

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.UseItemOnObject`
- `Kind` is `ItemKind.InventoryItem`
- `ItemId` is `5001`

---

### Validator Tests: `QuestForge.Engine.Tests/Authoring/DraftValidatorUseItemOnObjectTests.cs`

---

#### UIO_T14: E27 -- InteractableId == 0

**Given:**
- QuestDraft with a single UseItemOnObjectStep where InteractableId = 0

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E27"
- Message contains "InteractableId == 0"

---

#### UIO_T15: E28 -- ItemId == 0

**Given:**
- QuestDraft with a single UseItemOnObjectStep where ItemId = 0

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E28"
- Message contains "ItemId == 0"

---

#### UIO_T16: W13 -- no Expect, spin-loop warning

**Given:**
- QuestDraft with a single UseItemOnObjectStep where Expect is null

**When:** DraftValidator.Validate(draft)

**Then:**
- Warnings contains exactly one entry with Code == "W13"
- Message contains "spin-loop"
- W1 is NOT present in warnings (suppressed for this step type)

---

#### UIO_T17: Valid step -- no E27, E28, or W13

**Given:**
- QuestDraft with a single UseItemOnObjectStep where InteractableId = 99001, ItemId = 2002001, Expect = "questFlag(65, 3)"

**When:** DraftValidator.Validate(draft)

**Then:**
- No errors with code E27 or E28
- No warnings with code W13
- (W1 also absent because the step has an Expect)

---

### Schema Tests: `QuestForge.Schema.Tests/RoundTripTests.cs`

---

#### UIO_T18: JSON round-trip preserves all fields

**Given:**
- UseItemOnObjectStep with Id="use-key-on-device", InteractableId=2001500, Position=(81.5, 7.0, 32.2), Kind=KeyItem, ItemId=2002001, Expect="questFlag(65657, 5)"

**When:** Serialize to JSON as `Step`, then deserialize back

**Then:**
- Result is `UseItemOnObjectStep`
- All field values are preserved
- `type` discriminator in JSON is `"use-item-on-object"`

---

## 8. Implementation Order

### Phase A: Schema + EngineAction (est. 30 min)

1. Add `UseItemOnObjectStep` to `Step.cs`
2. Add `[JsonDerivedType]` attribute
3. Add `[JsonSerializable]` in `QuestForgeJsonContext.cs`
4. Add `EngineAction.UseItemOnObject` to `EngineAction.cs`
5. Add round-trip test in `RoundTripTests.cs`

**Done before Phase B:** `dotnet test QuestForge.Schema.Tests` passes with the new round-trip test.

### Phase B: Engine Resolver (est. 1 hour)

1. Add `ResolveUseItemOnObject` method to `QuestEngine.cs`
2. Add async pre-arm `if (step is UseItemOnObjectStep ...)` in the step dispatch chain
3. Add `case EngineAction.UseItemOnObject:` arm in `EngineTestHarness`'s `RunToCompletion`

**Done before Phase C:** `dotnet build QuestForge.Engine` and `dotnet build QuestForge.Engine.Tests` succeed.

### Phase C: Engine Tests (est. 2 hours)

1. Create `UseItemOnObjectStepTests.cs` with UIO_T1 through UIO_T13
2. All 13 engine tests pass

**Done before Phase D:** `dotnet test QuestForge.Engine.Tests --filter UseItemOnObjectStepTests` -- all green.

### Phase D: Validator Rules + Tests (est. 45 min)

1. Add E27, E28, W13 rules in `DraftValidator.cs`
2. Add `UseItemOnObjectStep` to W1 suppression
3. Create `DraftValidatorUseItemOnObjectTests.cs` with UIO_T14 through UIO_T17
4. All 4 validator tests pass

**Done before complete:** `dotnet test QuestForge.Engine.Tests --filter DraftValidatorUseItemOnObject` -- all green.

---

## 9. Done Criteria

1. `dotnet build` succeeds for all projects (Schema, Engine, Engine.Tests, Schema.Tests, Adapters, Adapters.Fakes)
2. `dotnet test QuestForge.Schema.Tests` passes -- UseItemOnObjectStep round-trip test green
3. `dotnet test QuestForge.Engine.Tests --filter UseItemOnObjectStepTests` passes -- 13 engine tests green
4. `dotnet test QuestForge.Engine.Tests --filter DraftValidatorUseItemOnObject` passes -- 4 validator tests green
5. `dotnet test QuestForge.Engine.Tests` passes -- all existing tests still pass (no regressions)
6. JSON serialized output of UseItemOnObjectStep contains `"type": "use-item-on-object"` as the first property
7. E27 fires on InteractableId == 0; E28 fires on ItemId == 0; W13 fires on null Expect with "spin-loop" in message; W1 does NOT fire on UseItemOnObjectStep with null Expect

---

## 10. Exclusions

This spec explicitly does NOT include:

1. **EngineHost dispatch arm** -- that is Slice 3 (Dalamud impl). The engine emits the action; the host dispatches it.
2. **Dalamud adapter implementation** -- Slice 3.
3. **Authoring inference** -- Slice 5. Signal research for detecting "player used item from InventoryEvent addon" is deferred.
4. **Tools-repo catch-up** -- Slice 3 (must land in the same slice as Dalamud impl per project invariant).
5. **In-game smoke test** -- Slice 4.
6. **Multi-item variant** -- if a quest requires using multiple items on the same object, the author creates multiple UseItemOnObjectStep instances. No `Items: uint[]` array variant in v1.
7. **InventoryEvent addon tab selection** -- if the addon has multiple tabs and the item is not on the default tab, this is deferred. The `ActionManager.UseAction(ActionType.KeyItem, itemId)` approach bypasses addon tab navigation entirely, so this is unlikely to be needed.

---

## 11. Open Questions (for future slices)

1. **Does `ActionManager.UseAction(ActionType.KeyItem, itemId)` work reliably for `InventoryItem` kind within the InventoryEvent context?** KeyItem is confirmed; InventoryItem needs in-game testing (Slice 4).
2. **Are there quests where the InventoryEvent addon requires explicit tab navigation before the item becomes usable?** If so, a `TabIndex` field may be needed on the step. Deferred until a concrete quest surfaces the need.
3. **Authoring inference signal:** What game-side signal can `UIObserver` poll to detect that the player used a key item from an InventoryEvent addon? Candidates: `ActionManager.UseAction` result, InventoryEvent addon visibility transition, key item removal from inventory. Research deferred to Slice 5.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Section 7.
- Happy paths: 5 scenarios (UIO_T1, UIO_T3, UIO_T10, UIO_T13, UIO_T17)
- Edge cases: 5 scenarios (UIO_T2, UIO_T7, UIO_T8, UIO_T9, UIO_T12)
- Error cases: 6 scenarios (UIO_T4, UIO_T5, UIO_T6, UIO_T11, UIO_T14, UIO_T15)
- Warning cases: 2 scenarios (UIO_T16, UIO_T18)
- Expected total: ~18 tests in QuestForge.Engine.Tests + QuestForge.Schema.Tests
