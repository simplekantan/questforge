# Quest-Data-Driven Fixtures Plan

**Status:** ready to implement
**Input docs:** docs/FIXTURES.md, docs/SCHEMA.md, docs/ARCHITECTURE.md
**Output:** all 5 existing fixtures pass in CI without trace files; new fixtures require zero per-quest hand-scripting
**Phase dependencies:** Phase 7 (fixture harness), Phase 11 (step types)

---

## Motivation

The current fixture replay infrastructure has three structural problems:

1. **Navigation starvation.** The trace contains hundreds of position observations between Navigate and Interact decisions. In replay, position is frozen within a segment, so the engine loops on Navigate forever (the `playerNear` predicate never becomes true).

2. **Combat overshooting.** The `SegmentedObservationScanner` overshoots quest-variable observations during rapid engage decision matching. Combat steps produce many decisions per segment; the scanner cursor drifts out of sync.

3. **Fragility.** Any new engine read (a new `GetSomething()` call) starves all existing traces — `ReplayObservationStarvationException`. Quest data changes (new steps, fragments) invalidate fixtures because the transition sequence changes but the trace does not.

Result: only 1 of 3 trace-backed fixtures works (the other 2 skip with starvation warnings), and the only fixture that actually runs uses a hand-scripted state machine that does not scale. The approach described below eliminates all three problems.

---

## Dependency graph

```
1. QuestForge.Engine.Tests/Replay/States/
   +-- QuestDataDrivenState.cs  <-- the new generic state machine (implements IFixtureState)
       +-- depends on: FakeGameStateProvider, FakeQuestState (existing)
       +-- depends on: PredicateAnalyzer (new pure-logic helper)
       +-- consumed by: EngineFixtureTests.cs (replaces both StateFactories and TraceReplayFixtureState)

2. QuestForge.Engine.Tests/Replay/
   +-- PredicateAnalyzer.cs  <-- extracts state mutations from expect predicate strings
       +-- depends on: QuestForge.Predicates (existing parser)

3. QuestForge.Engine.Tests/Replay/
   +-- EngineFixtureTests.cs  <-- modified dispatch: data-driven path becomes default
```

**Build order:** PredicateAnalyzer first (pure logic, unit-testable in isolation) -> QuestDataDrivenState -> EngineFixtureTests dispatch change -> verify all 5 fixtures pass -> remove trace-replay code.

---

## Architectural decisions

### QD1: The state machine derives game-state mutations from expect predicates

**Decision.** When the engine dispatches an action for step S, the state machine parses S's `expect` predicate and mutates the fakes to satisfy it. For example, if step "talk-to-wymond" has `expect: "questSequence(66130) >= 1"`, then after the engine dispatches Interact for that step, the state machine calls `QuestState.SetQuestSequence(new QuestId(66130), 1)`.

**Alternatives considered.**
- (A) Replay observations from the trace. This is the current approach and it is broken (motivation above).
- (B) Hand-script one state machine per fixture. `SimpleLinearAcceptanceState` works but has 100 lines for a 4-transition quest. A 30-transition quest like close-to-home-marauder would need 500+ lines and break on any quest file edit.
- (C) Derive from step type only (e.g. "after Interact, advance quest sequence by 1"). Too coarse -- the engine does not advance sequence by a fixed increment; the actual mutation depends on the game server's response, which is encoded in the quest file's expect predicates.

**Why (C) alone is insufficient and (QD1) wins.** The expect predicate IS the specification of what the game server does. If `expect: "questSequence(65644) >= 2"`, the game will set sequence to at least 2 when the step completes. The quest author already encoded this knowledge. The state machine just makes it happen in the fake.

**Concrete C# surface area.**

```csharp
/// Extracts concrete state mutations from a parsed expect predicate.
/// Returns a list of mutations that, when applied to the fakes, will make
/// the predicate evaluate to true.
internal static class PredicateAnalyzer
{
    public static IReadOnlyList<StateMutation> ExtractMutations(
        ExpectValue? expect, QuestId activeQuestId);
}

internal abstract record StateMutation
{
    internal sealed record SetQuestSequence(QuestId Quest, int Value) : StateMutation;
    internal sealed record SetQuestComplete(QuestId Quest) : StateMutation;
    internal sealed record SetQuestAccepted(QuestId Quest) : StateMutation;
    internal sealed record SetPlayerZone(int Zone) : StateMutation;
    internal sealed record SetPlayerPosition(float X, float Y, float Z) : StateMutation;
    internal sealed record SetPlayerNear(float X, float Y, float Z, float Radius) : StateMutation;
    internal sealed record SetQuestFlag(QuestId Quest, int Bit, bool Value) : StateMutation;
    internal sealed record SetQuestVariable(QuestId Quest, int Index, int Value, Nibble Nibble) : StateMutation;
    internal sealed record SetAttuned(uint AetheryteId) : StateMutation;
    internal sealed record SetPlayerHasItem(uint ItemId, int Quantity) : StateMutation;
    internal sealed record SetNotPredicate(StateMutation Inner) : StateMutation;
    // objectExistsInRange: simulated by adding an NPC/interactable at the player's position
    // (within range). The predicate signature is objectExistsInRange(dataId, range) --
    // it checks DistanceToPlayer, so placing the object at the player position satisfies it.
    internal sealed record SetObjectExistsInRange(uint DataId, float Range) : StateMutation;
    // npcExistsNearby / objectExists: simulated by adding an NPC or interactable
    internal sealed record SetNpcExistsNearby(uint DataId) : StateMutation;
    // Additional state mutations for predicates used in skipIf or expect
    internal sealed record SetSlotEquipped(int SlotIndex, int ItemLevel) : StateMutation;
    internal sealed record SetItemEquipped(uint ItemId) : StateMutation;
    internal sealed record SetInCombat(bool Value) : StateMutation;
    internal sealed record SetGearsetExistsForJob(uint JobId) : StateMutation;
    internal sealed record SetHasCoffers(bool Value) : StateMutation;
    internal sealed record SetAetherCurrentAttuned(uint DataId) : StateMutation;
}
```

**What breaks if violated.** If someone adds a new predicate function to the engine without adding a corresponding `StateMutation` case, the `PredicateAnalyzer` returns an empty list for that predicate, the fake state never changes, and the engine spin-loops until the safety overrun breaks the test. This is a loud, obvious failure.

**Testability.** `PredicateAnalyzer` is a pure function (string in, mutations out). Full coverage with unit tests, no fakes needed.

### QD2: Navigation is instant -- Navigate dispatches teleport the player to the destination

**Decision.** When the engine dispatches `EngineAction.Navigate(destination, ...)`, the state machine immediately sets `GameState.SetPosition(destination)`. The next tick, the engine's `playerNear` predicate evaluates to true and the step is confirmed.

**Why.** The fixture tests engine *decisions*, not navigation pathing. The decision to navigate is what matters -- the 200 ticks of walking add no coverage. Making navigation instant eliminates the starvation problem entirely.

**What breaks.** If someone writes a quest where the expect predicate is NOT a `playerNear` check but instead something like `questSequence(X) >= N` on a TravelStep, the Navigate response alone will not confirm the step. But this is correct -- in that case, the step will be confirmed by the subsequent Interact that changes the quest sequence. The state machine does not need special handling.

**Edge case: cross-zone navigation.** A TravelStep with `destination.zone` different from the current zone includes `playerZone() == N` in its expect. The state machine sets both position AND zone when processing Navigate for a step whose expect includes a `playerZone` mutation.

### QD3: Combat advances quest variables to satisfy expect predicates

**Decision.** When the engine dispatches `EngineAction.Engage(...)` for a `CombatStep`, the state machine does NOT respond immediately. Instead, after the first Engage transition is recorded for a given combat step, the state machine applies that step's expect mutations (typically `questVariable` or `questSequence` changes). This gives the engine one tick to observe the engage, then the combat "completes" on the next tick.

**Why.** Combat target selection is non-deterministic (the `CombatController` picks targets based on hostile actor lists). The fixture already filters out engage transitions from deterministic comparison (the `expectedDeterministic` filter in `EngineFixtureTests`). What matters is that combat dispatches and completes -- not which targets were selected.

**Intentional simplification.** Applying all combat mutations after the first Engage means the engine's `CombatController` is not exercised realistically -- there is no partial combat progress (e.g. killing 2 of 3 enemies incrementally advancing a nibble). This is deliberate: fixture tests verify that combat dispatches and the engine progresses past it. Testing incremental combat variable advancement would require simulating hostile actor lists, kill events, and multi-tick nibble progression, which is out of scope for data-driven fixtures. If incremental combat testing becomes necessary, it belongs in dedicated `CombatController` unit tests with hand-scripted actor lists, not in fixture tests.

**Concrete behavior.**

```csharp
// In QuestDataDrivenState.OnTick:
if (action is EngineAction.Engage && _pendingCombatMutations.Count > 0)
{
    // Apply on first engage tick for this step, not immediately.
    // The _pendingCombatMutations are populated when the engine first
    // reaches the CombatStep (in the step-matching logic).
    ApplyMutations(_pendingCombatMutations);
    _pendingCombatMutations.Clear();
}
```

### QD4: The fixture JSON format does not change

**Decision.** The existing fixture format (`schemaVersion`, `description`, `initialState`, `capabilities`, `questFile`, `expectedTransitions`, `terminalOutcome`, `sourceTrace`) is unchanged. The `sourceTrace` field becomes purely archival -- it is no longer read by the test harness. No re-extraction is needed.

**Alternatives considered.**
- (A) Add a new `stateMachine: "data-driven"` field. Unnecessary -- data-driven is the only sensible default going forward.
- (B) Remove the `sourceTrace` field. Breaking change for existing fixtures. Instead, just stop reading it.

**What breaks.** Nothing. Existing fixture JSON files work as-is.

### QD5: The three-way dispatch becomes two-way (data-driven default, scripted override)

**Decision.** The `EngineFixtureTests` dispatch changes from:

1. Scripted (hand-written) -> 2. Trace-replay -> 3. Skip

to:

1. Data-driven (new, default for all fixtures) -> 2. Scripted override (for fixtures that need non-standard behavior)

The trace-replay path (`TraceReplayFixtureState`, `SegmentedObservationScanner`, `ReplayGameStateProvider`, `ReplayQuestState`) is retained in the codebase (not deleted) but no longer called from the fixture harness. It remains available for the `qf-trace replay` CLI and local debugging.

**Why.** Data-driven works for all quest shapes (linear, branching, combat, attunement, cross-zone). No fixture needs trace replay once the data-driven path exists. Removing the trace-replay path from the harness eliminates the starvation failure mode entirely.

### QD6: initialState drives initial fake configuration

**Decision.** The `initialState` field in the fixture JSON configures the fake game state before the engine starts. Currently only `"fresh"` exists. The data-driven state machine interprets it as:

- `"fresh"`: Quest not accepted. Player in the zone of the quest's `acceptFrom` NPC, **at the NPC's position**. Quest sequence = 0. No quest flags set.

Placing the player AT the NPC position means the engine will only emit a Navigate action if the step's logic requires it (e.g. the NPC is in a different zone, or the step target is a different NPC). This avoids forcing a spurious Navigate transition at the start of every fixture. If a future fixture needs the player to start far away from the NPC, a new `initialState` value (e.g. `"fresh-distant"`) can be added at that time.

The quest definition's `acceptFrom` provides the zone and position. This replaces the hardcoded initial state in `SimpleLinearAcceptanceState`.

**Concrete C# surface area.**

```csharp
// In QuestDataDrivenState constructor:
private void ConfigureInitialState(QuestDefinition quest, string initialState)
{
    switch (initialState)
    {
        case "fresh":
            _gameState.SetZone(new ZoneId(quest.AcceptFrom!.Zone));
            // Player starts AT the NPC position. The engine will navigate
            // only if the first step's target is elsewhere.
            var npcPos = quest.AcceptFrom.Position;
            _gameState.SetPosition(new WorldPosition(npcPos.X, npcPos.Y, npcPos.Z));
            _questState.SetQuestSequence(new QuestId(quest.Id), 0);
            _questState.SetQuestStatus(new QuestId(quest.Id), QuestStatus.Available);
            break;
        default:
            throw new InvalidOperationException(
                $"Unknown initialState '{initialState}' in fixture.");
    }
}
```

### QD7: Step-action matching uses a decision-driven cursor that scans by step ID

**Decision.** The state machine maintains a cursor over the quest's expanded step list (sequences flattened, fragments expanded). The cursor advances based on step IDs from the engine's emitted `DecisionEvent`, NOT linearly.

When the fixture harness observes a `DecisionEvent` with `StepId = X`, it passes `X` to the state machine. The state machine scans forward from the current cursor position to find the `StepPlan` whose `Step.Id == X`. This handles three cases that a linear cursor cannot:

1. **`skipIf` steps.** Steps whose `skipIf` evaluates to true are silently skipped by the engine (the engine `continue`s past them in its step loop). The engine never emits a decision for these steps. The cursor scan jumps over them naturally because no decision with their ID ever arrives.

2. **Fragment-expanded steps.** Fragment expansion can produce steps with IDs that differ from the original quest definition. The scan-by-ID approach matches correctly regardless of expansion.

3. **Sequence block skipping.** If a sequence block's `skipIf` evaluates true, the engine skips all steps in that block. The cursor scan jumps past the entire block.

**Why NOT a linear cursor.** A linear cursor assumes every step in the plan produces exactly one decision. With `skipIf`, that assumption fails: the engine skips steps silently, but the linear cursor would still be pointing at the skipped step, waiting for a decision that never comes. The cursor would desynchronize and the state machine would apply mutations for the wrong step.

**Concrete C# surface area.**

The `OnTransitionRecorded` callback signature changes to accept the step ID from the `DecisionEvent`:

```csharp
// In IFixtureState (or as a new overload):
void OnTransitionRecorded(EngineAction action, int tick, string? stepId);

// In QuestDataDrivenState:
void OnTransitionRecorded(EngineAction action, int tick, string? stepId)
{
    if (stepId is null) return; // null stepId = engine-internal (loading zone wait, combat defense)

    // Scan forward from current cursor to find the step with this ID.
    for (int i = _cursor; i < _stepPlans.Count; i++)
    {
        if (_stepPlans[i].Step.Id == stepId)
        {
            _cursor = i;
            break;
        }
    }

    var currentPlan = _stepPlans[_cursor];
    if (IsPrimaryActionForStep(currentPlan.Step, action))
    {
        _cursor++;
        if (_cursor < _stepPlans.Count)
            PrepareForStep(_stepPlans[_cursor]);
    }
}
```

The fixture harness passes `newDecision.Data.StepId` to `OnTransitionRecorded`. This value is already available in the harness's tick loop (extracted from `DecisionEvent`).

**Step-type-to-expected-action mapping.**

| Step type | Expected action(s) | State mutation on action |
|---|---|---|
| `TravelStep` | Navigate | SetPosition to destination |
| `TalkStep` | Navigate (if implied), Interact | Apply expect mutations |
| `AcceptStep` | Navigate (if implied), Interact | SetQuestAccepted + apply expect mutations |
| `TurnInStep` | Navigate (if implied), Interact | Apply expect mutations (typically isQuestComplete) |
| `CombatStep` | Engage (one or more) | Apply expect mutations after first Engage |
| `AttunementStep` | Navigate (if implied), Interact | SetAttuned(target) + apply expect mutations |
| `HandOverItemStep` | Navigate (if implied), HandOver | Apply expect mutations |
| `CutsceneStep` | Wait | No mutation (cutscene skip is engine-internal) |
| `WaitStep` | Wait | Clock advance past duration |
| `PurchaseItemStep` | Navigate (if implied), Purchase | Apply expect mutations (typically playerHasItem) |
| `TeleportStep` | Teleport | SetPlayerZone to destination zone |
| `UseActionStep` | Navigate (if implied), UseAction | Apply expect mutations |
| `UseEmoteStep` | Navigate (if implied), UseEmote | Apply expect mutations |
| `SayChatMessageStep` | SayChatMessage | Apply expect mutations |
| `EquipGearForQuestStep` | EquipGear (per item) | SetItemEquipped per item |
| `EquipBestGearStep` | EquipBestGear | NotifyEquipBestGearComplete |
| `ChangeJobStep` | ChangeJob | SetJob |
| `RegisterGearsetStep` | RegisterGearset | No mutation (fire-once self-confirms) |
| `OpenCoffersStep` | OpenCoffer | SetHasCoffers(false) |
| `InteractObjectStep` | Navigate (if implied), InteractObject | Apply expect mutations |
| `PickupItemStep` | Navigate (if implied), InteractObject | Apply expect mutations |
| `DutyStep (spd)` | EnterSinglePlayerDuty | Apply expect mutations |
| `DutyStep (duty)` | EnterDuty | Apply expect mutations |
| `FragmentStep` | N/A (expanded by engine before state machine sees it) | N/A |
| `BranchStep` | N/A (engine selects branch; steps inside are flattened) | N/A |

### QD8: The state machine handles implied navigation generically

**Decision.** Many step types (talk, accept, turn-in, attune, hand-over-item, use-action, use-emote, interact-object, pickup-item) emit Navigate before the primary action when the player is beyond `StopDistance`. The state machine handles this generically: when the engine dispatches Navigate and the current cursor step is NOT a TravelStep, the state machine sets the player position to the step's target position (from `NpcLocation.Position`, `Location.Position`, or similar).

The cursor does NOT advance on implied navigation. It stays on the same step until the primary action (Interact, HandOver, UseAction, etc.) is dispatched.

**Concrete pattern.**

```csharp
// In OnTick:
if (action is EngineAction.Navigate nav && _currentStep is not TravelStep)
{
    // Implied navigation -- move player to the step's target position
    var targetPos = GetTargetPosition(_currentStep);
    if (targetPos is not null)
        _gameState.SetPosition(new WorldPosition(targetPos.X, targetPos.Y, targetPos.Z));
}
```

### QD9: Aethernet travel steps produce a zone-loading wait transition

**Decision.** Several existing fixtures include `{ "actionType": "wait" }` (null stepId) transitions after UseAethernet actions. These occur because the engine detects a loading-zone UI state after the aethernet teleport. The state machine simulates this: when the engine dispatches UseAethernet, the state machine:
1. Sets the player zone/position to the destination
2. Sets `UiState.LoadingZone = true`
3. Sets `_loadingZoneCountdown = 1`
4. On the next tick, the countdown decrements to 0 and clears `LoadingZone`

This produces exactly one `Wait("loading zone")` transition: the engine sees `LoadingZone = true` on the tick immediately after UseAethernet, emits one Wait, then on the following tick the countdown has cleared and the engine proceeds normally.

**Countdown value = 1 rationale.** The engine checks `LoadingZone` once per tick. With countdown = 1: tick N dispatches UseAethernet (state machine sets LoadingZone = true, countdown = 1); tick N+1 sees LoadingZone = true, emits Wait, state machine's OnTick decrements countdown to 0 and clears LoadingZone; tick N+2 sees LoadingZone = false, proceeds to next step. This yields exactly 1 wait transition in the deduplicated output, matching the existing fixture expectations.

**Why.** The engine checks `ui.LoadingZone` at the top of `ResolveAction` and returns `Wait("loading zone")` when true. Without this simulation, the expected `wait` transitions in existing fixtures would not be produced, and the deterministic comparison would fail.

### QD10: The state machine uses OnTick (every tick) not OnTransitionRecorded (distinct transitions only)

**Decision.** `QuestDataDrivenState.OnTick` is the primary callback. `OnTransitionRecorded` handles cursor advancement via step-ID scan (per QD7). The state machine reacts to every engine action via OnTick for state mutations, and uses OnTransitionRecorded for cursor progression.

**Why.** Distinct-transition-only advancement misses repeated actions within a step (e.g. multiple Navigate ticks before arriving). The state machine needs to respond on the first Navigate dispatch, not wait for a distinct transition.

### QD11: PredicateAnalyzer handles compound predicates (and/or/not)

**Decision.** `PredicateAnalyzer.ExtractMutations` recursively walks the parsed AST. For `and`, it extracts mutations from both branches. For `or`, it extracts mutations from the first branch only (satisfying one branch is sufficient). For `not`, it wraps the inner mutation in `SetNotPredicate` (which the applier inverts -- e.g. `not isQuestComplete(X)` means do NOT set quest complete).

The parser is the existing `QuestForge.Predicates` recursive-descent parser. `PredicateAnalyzer` consumes the `PredicateAst` output.

**Edge case: `not` predicates in expect.** A `not` in an expect predicate means "this condition should remain false." The state machine does not need to actively make it false -- it just does not apply the inner mutation. For example, `expect: "not isQuestComplete(65644)"` means the quest should NOT be complete; the state machine leaves quest status unchanged.

### QD12: EquipBestGear two-phase completion is handled in the fixture harness, not the state machine

**Decision.** The existing `EngineFixtureTests` loop already calls `engine.NotifyEquipBestGearComplete(stepId)` when it sees an `EquipBestGear` action. This remains unchanged. The state machine does not need to handle this -- the harness handles it.

### QD13: The state machine pre-computes a step plan at construction time

**Decision.** At construction, `QuestDataDrivenState` loads the quest definition, expands fragments (using the same `QuestEngine.StartQuest` logic or by calling `StartQuest` and inspecting the result), and builds an ordered list of `(Step step, IReadOnlyList<StateMutation> mutations)` pairs. This pre-computation means `OnTick` is a simple cursor walk, not a per-tick parse operation.

**Why.** Parsing predicates on every tick would be wasteful and harder to debug. Pre-computation also enables the constructor to fail fast if a predicate cannot be analyzed.

**Concrete C# surface area.**

```csharp
internal sealed class QuestDataDrivenState : IFixtureState
{
    private readonly FakeGameStateProvider _gameState;
    private readonly FakeQuestState _questState;
    private readonly ManualTimeProvider _clock;
    private readonly IReadOnlyList<StepPlan> _stepPlans;
    private int _cursor;
    private bool _awaitingPrimaryAction; // true after implied nav, waiting for Interact/etc.
    private List<StateMutation>? _pendingCombatMutations;
    private int _loadingZoneCountdown; // ticks of simulated loading zone remaining

    internal sealed record StepPlan(
        Step Step,
        IReadOnlyList<StateMutation> Mutations);

    // ... IFixtureState implementation ...
}
```

### QD14: Quest variable nibble mutations use byte-level read-modify-write

**Decision.** The `SetQuestVariable` mutation carries a `Nibble` field (`Whole`, `Low`, or `High`) that specifies which part of the byte to set. `FakeQuestState.SetQuestVariables` operates on full 6-byte arrays. To set a single nibble, `ApplyMutations` must:

1. Read the current 6-byte array via `GetQuestVariables`
2. Modify the target nibble of the byte at `Index`
3. Write the full array back via `SetQuestVariables`

This mirrors how the game server works: quest variable bytes are split into low (bits 0-3) and high (bits 4-7) nibbles, and the predicates `questVariableLow(id, index)` and `questVariableHigh(id, index)` read them independently.

**Concrete C# surface area.**

```csharp
private void ApplyVariableMutation(StateMutation.SetQuestVariable v)
{
    // Read current variables (default: 6 zero bytes)
    var current = _questState.GetQuestVariables(v.Quest, CancellationToken.None)
        .GetAwaiter().GetResult().ValueOrThrow;
    var bytes = current.ToArray(); // mutable copy

    switch (v.Nibble)
    {
        case Nibble.Whole:
            bytes[v.Index] = (byte)v.Value;
            break;
        case Nibble.Low:
            bytes[v.Index] = (byte)((bytes[v.Index] & 0xF0) | (v.Value & 0x0F));
            break;
        case Nibble.High:
            bytes[v.Index] = (byte)((bytes[v.Index] & 0x0F) | ((v.Value & 0x0F) << 4));
            break;
    }

    _questState.SetQuestVariables(v.Quest, bytes);
}
```

**Why.** The engine's `PredicateEvaluator.EvaluateQuestVariable` method already reads the full byte and extracts the nibble. Setting the nibble correctly in the fake ensures the predicate evaluates to true. Setting the whole byte when only a nibble predicate is used would overwrite the other nibble, which could break a compound expect like `"questVariableLow(Q, 0) >= 2 and questVariableHigh(Q, 0) >= 1"`.

### QD15: objectExistsInRange uses the existing NPC/interactable fake infrastructure

**Decision.** The `objectExistsInRange(dataId, range)` predicate (note: two arguments, NOT four -- the predicate checks distance from the *player*, not from a fixed coordinate) is satisfied by adding an NPC or interactable to the fake with `DistanceToPlayer <= range`. The `FakeGameStateProvider.NpcExistsNearby` method already checks both `_npcs` (by `NpcId.Value == dataId`) and `_interactables` (by `InteractableId.Value == dataId`). The `objectExistsInRange` evaluator in `PredicateEvaluator` calls `FindNpc` and `FindInteractable` and checks `DistanceToPlayer`.

The `SetObjectExistsInRange` mutation creates an `InteractableReference` with the given `dataId` and `DistanceToPlayer = 0` (placing it at the player's position, which is always within any positive range). Similarly, `SetNpcExistsNearby` adds an NPC with `DistanceToPlayer = 0`.

**Why not pass coordinates?** The predicate `objectExistsInRange(dataId, range)` does not take coordinates -- it measures distance from the player's current position. The state machine only needs to ensure the object exists and is close enough. Placing it at distance 0 is the simplest guarantee.

---

## Task 1 -- PredicateAnalyzer

### 1.1 Deliverables

A new file `QuestForge.Engine.Tests/Replay/PredicateAnalyzer.cs` containing the `PredicateAnalyzer` static class and the `StateMutation` discriminated union.

### 1.2 Predicate-to-mutation mapping

| Predicate function | StateMutation produced |
|---|---|
| `questSequence(id) >= N` | `SetQuestSequence(QuestId(id), N)` |
| `questSequence(id) == N` | `SetQuestSequence(QuestId(id), N)` |
| `isQuestComplete(id)` | `SetQuestComplete(QuestId(id))` |
| `isQuestAccepted(id)` | `SetQuestAccepted(QuestId(id))` |
| `playerZone() == N` | `SetPlayerZone(N)` |
| `playerNear({x:X,y:Y,z:Z}, R)` | `SetPlayerNear(X, Y, Z, R)` |
| `questFlag(id, bit)` | `SetQuestFlag(QuestId(id), bit, true)` |
| `questVariable(id, index) >= N` | `SetQuestVariable(QuestId(id), index, N, Nibble.Whole)` |
| `questVariable(id, index) == N` | `SetQuestVariable(QuestId(id), index, N, Nibble.Whole)` |
| `questVariableLow(id, index) >= N` | `SetQuestVariable(QuestId(id), index, N, Nibble.Low)` |
| `questVariableLow(id, index) == N` | `SetQuestVariable(QuestId(id), index, N, Nibble.Low)` |
| `questVariableHigh(id, index) >= N` | `SetQuestVariable(QuestId(id), index, N, Nibble.High)` |
| `questVariableHigh(id, index) == N` | `SetQuestVariable(QuestId(id), index, N, Nibble.High)` |
| `isAttuned(id)` | `SetAttuned(id)` |
| `playerHasItem(id, qty)` | `SetPlayerHasItem(id, qty)` |
| `playerHasItem(id)` | `SetPlayerHasItem(id, 1)` |
| `objectExistsInRange(dataId, range)` | `SetObjectExistsInRange(dataId, range)` |
| `npcExistsNearby(dataId)` | `SetNpcExistsNearby(dataId)` |
| `objectExists(dataId)` | `SetNpcExistsNearby(dataId)` |
| `isSlotEquipped(slotIndex)` | `SetSlotEquipped(slotIndex, 1)` (sets ilvl to 1, which is > 0) |
| `playerHasEquipped(itemId)` | `SetItemEquipped(itemId)` |
| `playerInCombat` | `SetInCombat(true)` |
| `jobGearsetExists(jobId)` | `SetGearsetExistsForJob(jobId)` |
| `inventoryHasCoffers()` | `SetHasCoffers(true)` |
| `isAetherCurrentAttuned(id)` | `SetAetherCurrentAttuned(id)` |
| `isDiscipleOfWar` | Empty list (job is set via initial state or ChangeJobStep) |
| `isDiscipleOfMagic` | Empty list (job is set via initial state or ChangeJobStep) |
| `isPlayerJob(jobId)` | Empty list (job is set via ChangeJobStep; skipIf usage is safe because fake defaults to job 1 = GLA which is DoW) |
| `playerJobId` | Empty list (numeric job comparison; handled by ChangeJobStep) |
| `not <inner>` | Wraps inner mutation in `SetNotPredicate` |
| `A and B` | Concatenates mutations from A and B |
| `A or B` | Returns mutations from A only |

**Unsupported predicates** (return empty list, log a warning): any predicate function not in the table above. The test will detect the problem when the engine spin-loops.

**Rationale for empty-list predicates.** `isDiscipleOfWar`, `isDiscipleOfMagic`, `isPlayerJob`, and `playerJobId` are job-identity predicates. The fake game state defaults to job 1 (Gladiator, a Disciple of War). These predicates appear in `skipIf` conditions (e.g. "skip this marauder-specific step if the player is not a marauder") and occasionally in `expect`. When they appear in `expect`, the job has already been set by a preceding `ChangeJobStep`. The `PredicateAnalyzer` does not need to produce mutations for them because the state machine handles job changes through the `ChangeJob` action dispatch. Returning an empty list is safe: if the predicate is in an `expect` and the job is wrong, the engine will spin-loop and the safety overrun will catch it -- a clear signal that a `ChangeJobStep` is missing from the quest definition.

### 1.3 Implementation notes

The analyzer parses the predicate string using the existing `PredicateParser.Parse(string)` from `QuestForge.Predicates`, then walks the resulting `PredicateAst`. It does NOT evaluate against game state -- it extracts structural information only.

For comparison operators: `>=` and `==` both produce the same mutation (set the value to the compared operand). `>` produces `value + 1`. `<=` and `<` are unusual in expect predicates (they mean "stay below X") and produce no mutation (the state machine does not need to make the condition true by reducing a value).

The `Nibble` enum is reused from `QuestForge.Engine.Predicates.PredicateEvaluator` (it is `internal` to `QuestForge.Engine`, so it is accessible from `QuestForge.Engine.Tests` via `InternalsVisibleTo`). The analyzer maps `questVariable` -> `Nibble.Whole`, `questVariableLow` -> `Nibble.Low`, `questVariableHigh` -> `Nibble.High`.

---

## Task 2 -- QuestDataDrivenState

### 2.1 Deliverables

A new file `QuestForge.Engine.Tests/Replay/States/QuestDataDrivenState.cs` implementing `IFixtureState`.

### 2.2 Constructor

```csharp
internal static QuestDataDrivenState Create(
    QuestDefinition quest,
    IReadOnlyDictionary<string, FragmentDefinition>? fragments,
    string initialState)
```

1. Instantiates `FakeGameStateProvider`, `FakeQuestState`, `FakeNavigator`, `FakeTeleporter`, `FakeInteractor`, and all other fakes.
2. Calls `ConfigureInitialState(quest, initialState)` (per QD6).
3. Expands fragments by creating a throw-away `QuestEngine`, calling `StartQuest(quest, fragments)`, and reading back the expanded sequences. (Alternative: duplicate the expansion logic. The engine reference is acceptable because `QuestEngine` is in the same repo and its constructor does not side-effect adapters.)
4. Iterates through expanded sequences in order, calling `PredicateAnalyzer.ExtractMutations(step.Expect, ...)` for each step.
5. Stores the result as `IReadOnlyList<StepPlan>`.

### 2.3 OnTick behavior

```
OnTick(action, tick):
  // Handle loading-zone countdown (QD9)
  if _loadingZoneCountdown > 0:
    _loadingZoneCountdown--
    if _loadingZoneCountdown == 0:
      _gameState.SetUiState(new UiState(loadingZone: false, ...))
    return

  // If cursor is past the end, no more mutations to apply
  if _cursor >= _stepPlans.Count:
    return

  currentPlan = _stepPlans[_cursor]

  // Combat: apply mutations after first Engage (QD3)
  if action is Engage && _pendingCombatMutations is not null:
    ApplyMutations(_pendingCombatMutations)
    // Also clear InCombat on the same tick so the engine exits combat
    _gameState.SetInCombat(false)
    _pendingCombatMutations = null
    return

  // Navigate for TravelStep: move player to destination (QD2)
  if action is Navigate && currentPlan.Step is TravelStep ts:
    _gameState.SetPosition(WorldPosition(ts.Destination.Position))
    if ts.Destination.Zone != 0:
      _gameState.SetZone(ZoneId(ts.Destination.Zone))
    // Cursor advances on transition, not here
    return

  // Implied navigation for non-TravelStep (QD8)
  if action is Navigate && currentPlan.Step is not TravelStep:
    targetPos = GetTargetPosition(currentPlan.Step)
    if targetPos is not null:
      _gameState.SetPosition(WorldPosition(targetPos))
    _awaitingPrimaryAction = true
    return

  // Teleport: move player to destination zone (QD7 table)
  if action is Teleport tp:
    ApplyMutations(currentPlan.Mutations)
    return

  // UseAethernet: zone change + loading zone simulation (QD9)
  if action is UseAethernet ua:
    ApplyMutations(currentPlan.Mutations)
    _gameState.SetUiState(new UiState(loadingZone: true, ...))
    _loadingZoneCountdown = 1
    return

  // Primary actions: apply expect mutations
  if action is Interact or HandOver or UseAction or UseEmote
     or SayChatMessage or InteractObject or Purchase
     or EquipGear or EquipBestGear or ChangeJob
     or RegisterGearset or OpenCoffer
     or EnterSinglePlayerDuty or EnterDuty:
    ApplyMutations(currentPlan.Mutations)
    _awaitingPrimaryAction = false
    return
```

### 2.4 OnTransitionRecorded behavior

Called once per new distinct `(stepId, actionType)` pair. Advances the cursor by scanning for the step ID from the engine's decision (per QD7):

```
OnTransitionRecorded(action, tick, stepId):
  if stepId is null:
    return  // engine-internal decision (loading zone wait, combat defense)

  // Scan forward from current cursor to find the step with this ID.
  for i = _cursor to _stepPlans.Count - 1:
    if _stepPlans[i].Step.Id == stepId:
      _cursor = i
      break

  currentPlan = _stepPlans[_cursor]

  // When the engine records a transition for the current step's primary action,
  // advance the cursor to the next step.
  if IsPrimaryActionForStep(currentPlan.Step, action):
    _cursor++
    if _cursor < _stepPlans.Count:
      PrepareForStep(_stepPlans[_cursor])
```

Where `PrepareForStep` sets up combat mutations if the next step is a CombatStep, etc.

### 2.5 ApplyMutations

```csharp
private void ApplyMutations(IReadOnlyList<StateMutation> mutations)
{
    foreach (var m in mutations)
    {
        switch (m)
        {
            case StateMutation.SetQuestSequence sq:
                _questState.SetQuestSequence(sq.Quest, sq.Value);
                break;
            case StateMutation.SetQuestComplete qc:
                _questState.SetQuestStatus(qc.Quest, QuestStatus.Complete);
                break;
            case StateMutation.SetQuestAccepted qa:
                _questState.SetQuestStatus(qa.Quest, QuestStatus.Accepted);
                _questState.AddAcceptedQuest(qa.Quest);
                break;
            case StateMutation.SetPlayerZone z:
                _gameState.SetZone(new ZoneId(z.Zone));
                break;
            case StateMutation.SetPlayerPosition p:
                _gameState.SetPosition(new WorldPosition(p.X, p.Y, p.Z));
                break;
            case StateMutation.SetPlayerNear pn:
                _gameState.SetPosition(new WorldPosition(pn.X, pn.Y, pn.Z));
                break;
            case StateMutation.SetQuestFlag f:
                _questState.SetQuestFlagBit(f.Quest, f.Bit, f.Value);
                break;
            case StateMutation.SetQuestVariable v:
                ApplyVariableMutation(v);
                break;
            case StateMutation.SetAttuned a:
                _gameState.SetAetheryteAttuned(new AetheryteId(a.AetheryteId), true);
                break;
            case StateMutation.SetPlayerHasItem item:
                _gameState.SetItemCount(new ItemId(item.ItemId), item.Quantity);
                break;
            case StateMutation.SetObjectExistsInRange obj:
                // Add an interactable at the player's position (distance = 0, always within range)
                _gameState.AddInteractable(new InteractableReference(
                    new InteractableId(obj.DataId), "fixture-object", 0f));
                break;
            case StateMutation.SetNpcExistsNearby npc:
                // Add an NPC at the player's position (distance = 0)
                _gameState.AddNpc(new NpcReference(
                    new NpcId(npc.DataId), "fixture-npc", 0f));
                break;
            case StateMutation.SetSlotEquipped slot:
                _gameState.SetEquippedItemLevelForSlot(slot.SlotIndex, slot.ItemLevel);
                break;
            case StateMutation.SetItemEquipped equipped:
                _gameState.SetItemEquipped(new ItemId(equipped.ItemId), true);
                break;
            case StateMutation.SetInCombat combat:
                _gameState.SetInCombat(combat.Value);
                break;
            case StateMutation.SetGearsetExistsForJob gearset:
                _gameState.SetGearsetExistsForJob(gearset.JobId, true);
                break;
            case StateMutation.SetHasCoffers coffers:
                _gameState.SetHasCoffers(coffers.Value);
                break;
            case StateMutation.SetAetherCurrentAttuned ac:
                _gameState.SetAetherCurrentAttuned(ac.DataId, true);
                break;
            case StateMutation.SetNotPredicate:
                break; // Do nothing -- "not X" means X should remain false
        }
    }
}
```

---

## Task 3 -- EngineFixtureTests dispatch change

### 3.1 Deliverables

Modify `EngineFixtureTests.EngineProducesExpectedTransitions` to use the data-driven path as default.

### 3.2 New dispatch logic

```csharp
// Two-way dispatch (QD5):
IFixtureState state;
if (StateFactories.TryGetValue(fixtureName, out var scripted))
{
    state = scripted();  // (1) scripted override
}
else
{
    state = QuestDataDrivenState.Create(quest, fragments, fixture.InitialState);  // (2) data-driven default
}
```

### 3.3 Remove trace-replay dependencies

- Remove `TryResolveSourceTrace` call
- Remove `TraceReplayFixtureState.FromTraceFile` call
- Remove the `Assert.Skip` fallback for missing traces
- Remove `TryAdvanceForDecision` calls from the tick loop
- Remove `WrapTickForStarvation` (starvation is impossible without trace replay)

The `TraceReplayFixtureState`, `SegmentedObservationScanner`, `ReplayGameStateProvider`, `ReplayQuestState`, and `TraceReader` classes are NOT deleted -- they remain for the `qf-trace replay` CLI. They are simply no longer referenced from the fixture harness.

### 3.4 Wire step ID into OnTransitionRecorded

The fixture harness tick loop must pass the `DecisionEvent.Data.StepId` to `OnTransitionRecorded`:

```csharp
// In the tick loop, where a new distinct transition is detected:
if (actualTransitions.Count == 0 || actualTransitions[^1] != pair)
{
    actualTransitions.Add(pair);
    state.OnTransitionRecorded(action, tick, newDecision.Data.StepId);
}
```

### 3.5 Remove SimpleLinearAcceptanceState

Once the data-driven path handles the simple-linear-acceptance fixture, the hand-scripted state machine and its `StateFactories` entry are deleted. The `StateFactories` dictionary is retained (empty) as an escape hatch for hypothetical future fixtures that need non-standard behavior.

---

## Task 4 -- Migration of existing fixtures

### 4.1 Fixture inventory

| Fixture | Current status | Data-driven expectation |
|---|---|---|
| `simple-linear-acceptance.json` | Works (scripted) | Works (data-driven replaces scripted) |
| `close-to-home-marauder.json` | Skips (starvation) | Works (data-driven) |
| `65998-on-to-summerford.json` | Skips (starvation) | Works (data-driven) |

### 4.2 Migration steps

1. Implement `QuestDataDrivenState` and `PredicateAnalyzer`.
2. Run `simple-linear-acceptance` through the data-driven path. Assert same transitions.
3. Run `close-to-home-marauder` through the data-driven path. This is the acid test -- 32 transitions, attunement, hand-over, cross-zone travel, aethernet.
4. Run `65998-on-to-summerford` through the data-driven path.
5. Delete `SimpleLinearAcceptanceState`.
6. Remove trace-replay dispatch from `EngineFixtureTests`.

### 4.3 No fixture JSON changes required

Per QD4, the fixture files are unchanged. The `sourceTrace` field and `.trace.jsonl` sidecar files remain but are no longer consumed.

---

## Given-When-Then specifications

### T1: PredicateAnalyzer -- simple questSequence comparison

**Given** predicate string `"questSequence(66130) >= 1"` and active quest ID 66130
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns exactly one mutation: `SetQuestSequence(QuestId(66130), 1)`

### T2: PredicateAnalyzer -- isQuestComplete

**Given** predicate string `"isQuestComplete(66130)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns exactly one mutation: `SetQuestComplete(QuestId(66130))`

### T3: PredicateAnalyzer -- compound and predicate

**Given** predicate string `"playerZone() == 182 and playerNear({x:35.56,y:4.0,z:-151.18}, 3)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns two mutations: `SetPlayerZone(182)` and `SetPlayerNear(35.56, 4.0, -151.18, 3)`

### T4: PredicateAnalyzer -- not predicate

**Given** predicate string `"not isQuestComplete(65644)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetNotPredicate(SetQuestComplete(QuestId(65644)))`

### T5: PredicateAnalyzer -- or predicate (first branch only)

**Given** predicate string `"questSequence(65644) >= 2 or isQuestComplete(65644)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetQuestSequence(QuestId(65644), 2)` (first branch only)

### T6: PredicateAnalyzer -- isQuestAccepted

**Given** predicate string `"isQuestAccepted(65644)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetQuestAccepted(QuestId(65644))`

### T7: PredicateAnalyzer -- questFlag

**Given** predicate string `"questFlag(65644, 3)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetQuestFlag(QuestId(65644), 3, true)`

### T8: PredicateAnalyzer -- isAttuned

**Given** predicate string `"isAttuned(8)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetAttuned(8)`

### T9: PredicateAnalyzer -- playerHasItem

**Given** predicate string `"playerHasItem(2000104, 1)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetPlayerHasItem(2000104, 1)`

### T10: PredicateAnalyzer -- null expect

**Given** null `ExpectValue`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns empty list

### T11: PredicateAnalyzer -- unknown function

**Given** predicate string `"someNewFunction(42) == true"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns empty list (no crash, no exception)

### T12: PredicateAnalyzer -- questVariable (whole byte)

**Given** predicate string `"questVariable(65644, 0) >= 3"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetQuestVariable(QuestId(65644), 0, 3, Nibble.Whole)`

### T13: PredicateAnalyzer -- AllExpect compound

**Given** `AllExpect { All = ["questSequence(65644) >= 2", "questFlag(65644, 1)"] }`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns two mutations: `SetQuestSequence(QuestId(65644), 2)` and `SetQuestFlag(QuestId(65644), 1, true)`

### T14: PredicateAnalyzer -- AnyExpect compound

**Given** `AnyExpect { Any = ["questSequence(65644) >= 2", "isQuestComplete(65644)"] }`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns one mutation: `SetQuestSequence(QuestId(65644), 2)` (first branch only)

### T15: QuestDataDrivenState -- simple linear quest (66130)

**Given** quest definition 66130 loaded, `initialState = "fresh"`
**When** engine runs to completion through `QuestDataDrivenState`
**Then** produces transitions: `(travel-to-wymond, navigate)`, `(talk-to-wymond, interact)`, `(travel-to-momodi, navigate)`, `(talk-to-momodi, interact)` and terminal outcome `done`

### T16: QuestDataDrivenState -- cross-zone travel with attunement (65644)

**Given** quest definition 65644 loaded with fragments, `initialState = "fresh"`
**When** engine runs to completion through `QuestDataDrivenState`
**Then** produces the 32-transition sequence from `close-to-home-marauder.json` and terminal outcome `done`

### T17: QuestDataDrivenState -- aethernet produces wait transition

**Given** quest with an aethernet step (e.g. quest 65644, step "aethernet-intra-zone-49")
**When** engine dispatches UseAethernet
**Then** the next engine decision is `Wait("loading zone")` (null stepId), matching the fixture's `{ "actionType": "wait" }` entry. Exactly one wait transition appears in the deduplicated output (not two).

### T18: QuestDataDrivenState -- navigate instant arrival for TravelStep

**Given** engine dispatches `Navigate(destination)` for a TravelStep
**When** `OnTick` is called
**Then** `GameState.GetPlayerPosition()` returns `destination` on the next call

### T19: QuestDataDrivenState -- implied navigation for TalkStep

**Given** engine dispatches `Navigate(npcPos)` for a TalkStep (player was far from NPC)
**When** `OnTick` is called
**Then** player position is set to the NPC position, but the step cursor does NOT advance (waiting for the Interact action)

### T20: QuestDataDrivenState -- AcceptStep sets quest accepted

**Given** current step is `AcceptStep` with `expect: "isQuestAccepted(65644)"`
**When** engine dispatches Interact for this step
**Then** `QuestState.IsQuestAccepted(QuestId(65644))` returns true on the next call

### T21: QuestDataDrivenState -- PurchaseItemStep with synthesized expect

**Given** a PurchaseItemStep with `itemId: 123, quantity: 5` and no authored expect
**When** engine dispatches Purchase
**Then** `GameState.GetItemCount(ItemId(123))` returns 5 (from synthesized `playerHasItem(123,5)`)

### T22: QuestDataDrivenState -- initial state for fresh quest

**Given** quest 66130, `initialState = "fresh"`
**When** `QuestDataDrivenState.Create` returns
**Then** player zone is 182, quest sequence is 0, quest status is Available, player position equals the acceptFrom NPC position (not offset)

### T23: PredicateAnalyzer -- questVariableLow nibble

**Given** predicate string `"questVariableLow(65847, 0) >= 3"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns exactly one mutation: `SetQuestVariable(QuestId(65847), 0, 3, Nibble.Low)`

### T24: PredicateAnalyzer -- questVariableHigh nibble

**Given** predicate string `"questVariableHigh(65847, 1) >= 1"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns exactly one mutation: `SetQuestVariable(QuestId(65847), 1, 1, Nibble.High)`

### T25: PredicateAnalyzer -- isSlotEquipped

**Given** predicate string `"isSlotEquipped(3)"`
**When** `PredicateAnalyzer.ExtractMutations` is called
**Then** returns exactly one mutation: `SetSlotEquipped(3, 1)` (slotIndex = 3, itemLevel = 1 -- any positive value satisfies `ilvl > 0`)

### T26: QuestDataDrivenState -- step with skipIf that evaluates true

**Given** a quest with three steps in sequence: step A (talk, no skipIf), step B (talk, `skipIf: "questFlag(Q, 1)"`), step C (talk, no skipIf). Quest flag bit 1 is set to true in the fake state (e.g. by step A's expect mutations).
**When** engine runs through `QuestDataDrivenState`
**Then** the engine emits decisions for step A and step C only (step B is skipped). The state machine's cursor advances from A directly to C via the step-ID scan in `OnTransitionRecorded` -- it never tries to match against step B.

### T27: PredicateAnalyzer -- questVariableLow nibble mutation preserves high nibble

**Given** quest variable byte at index 0 is `0x51` (high nibble = 5, low nibble = 1). Predicate string `"questVariableLow(65847, 0) >= 3"`.
**When** `ApplyVariableMutation` is called with `SetQuestVariable(QuestId(65847), 0, 3, Nibble.Low)`
**Then** the byte at index 0 becomes `0x53` (high nibble 5 preserved, low nibble set to 3). `GetQuestVariables` returns a 6-byte array where byte 0 is `0x53`.

### T28: PredicateAnalyzer -- questVariableHigh nibble mutation preserves low nibble

**Given** quest variable byte at index 1 is `0x02` (high nibble = 0, low nibble = 2). Predicate string `"questVariableHigh(65847, 1) >= 1"`.
**When** `ApplyVariableMutation` is called with `SetQuestVariable(QuestId(65847), 1, 1, Nibble.High)`
**Then** the byte at index 1 becomes `0x12` (high nibble set to 1, low nibble 2 preserved). `GetQuestVariables` returns a 6-byte array where byte 1 is `0x12`.

---

## Implementation order

### Phase A -- PredicateAnalyzer (1-2 days)

1. Define `StateMutation` discriminated union (including `Nibble` field on `SetQuestVariable`, and new mutation types: `SetObjectExistsInRange`, `SetNpcExistsNearby`, `SetSlotEquipped`, `SetItemEquipped`, `SetInCombat`, `SetGearsetExistsForJob`, `SetHasCoffers`, `SetAetherCurrentAttuned`)
2. Implement `PredicateAnalyzer.ExtractMutations` for all predicate functions in the mapping table
3. Write unit tests T1-T14, T23-T25, T27-T28
4. All tests green

**Done before Phase B starts.**

### Phase B -- QuestDataDrivenState (2-3 days)

1. Implement `QuestDataDrivenState` constructor (initial state at NPC position per QD6, step plan pre-computation)
2. Implement `OnTick` (action-to-mutation dispatch, loading zone countdown = 1 per QD9)
3. Implement `OnTransitionRecorded` (step-ID scan cursor advancement per QD7)
4. Implement `ApplyMutations` (including nibble-aware variable writes per QD14, objectExistsInRange per QD15, and all new mutation types)
5. Wire into `EngineFixtureTests` alongside existing paths (three-way: data-driven, scripted, trace-replay)
6. Run `simple-linear-acceptance` through data-driven path -- passes (T15)
7. Run `close-to-home-marauder` through data-driven path -- passes (T16)
8. Run `65998-on-to-summerford` through data-driven path -- passes
9. Write remaining unit tests T17-T22, T26

**Done before Phase C starts.**

### Phase C -- Cleanup (1 day)

1. Remove `SimpleLinearAcceptanceState` and its `StateFactories` entry
2. Remove trace-replay dispatch from `EngineFixtureTests`
3. Remove `WrapTickForStarvation` wrapper
4. Verify all 3 fixtures pass (no skips)
5. Verify existing engine unit tests still pass (`dotnet test QuestForge.Engine.Tests`)

**Done when all CI is green.**

**Estimated total: 4-6 days.**

---

## Done criteria

1. All 3 fixture files in `questforge-data/fixtures/engine/` pass in CI without trace files (no skips, no starvation).
2. `dotnet test QuestForge.Engine.Tests` passes with zero skipped fixture tests.
3. Adding a new fixture for any quest requires only: (a) a quest JSON file in `questforge-data`, (b) a fixture JSON file with `expectedTransitions` -- no per-quest C# code.
4. New engine reads (e.g. a new `GetSomething()` adapter call) do not break existing fixtures.
5. `PredicateAnalyzer` has unit test coverage for all predicate functions in the mapping table, including `questVariableLow`, `questVariableHigh`, `isSlotEquipped`, `objectExistsInRange`, and `npcExistsNearby`.
6. The `SegmentedObservationScanner`, `ReplayGameStateProvider`, `ReplayQuestState`, and `TraceReader` classes still compile and are available for `qf-trace replay` (not deleted).
7. Nibble mutations correctly preserve the other nibble when writing (verified by T27 and T28).

---

## Exclusions

- **Combat target selection testing.** The data-driven state machine does not simulate hostile actor lists or CombatController target selection. Combat fixtures test that combat dispatches and completes; target order is non-deterministic and not asserted.
- **Branch step fixtures.** No branch-step quest exists in the corpus yet. Branch step support in `QuestDataDrivenState` is deferred until a quest uses it.
- **Minigame step fixtures.** No minigame quest exists in the corpus yet. Deferred.
- **Duty step fixtures.** No duty quest with a fixture exists yet. The `QuestDataDrivenState` table includes duty support, but it will not be tested until a fixture is created.
- **Deleting trace replay code.** The trace-replay infrastructure (`SegmentedObservationScanner`, etc.) is retained for `qf-trace replay` and local debugging. Only the fixture harness reference is removed.
- **`qf-trace extract-fixture` changes.** The extraction tool continues to produce fixtures in the same format. No changes needed.
- **New initialState values.** Only `"fresh"` is implemented. Mid-quest fixtures (e.g. `"sequence:5"`) are deferred.
- **Incremental combat variable testing.** Partial combat progress (e.g. nibble increments per kill) is not tested by fixtures. See QD3 rationale.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 12 scenarios (T1-T3, T6, T8-T9, T12-T13, T15, T18, T23-T24)
- Edge cases: 10 scenarios (T4-T5, T14, T17, T19, T21, T25-T28)
- Error cases: 6 scenarios (T10-T11, T16, T20, T22, T26)
- Expected total: ~30 tests in QuestForge.Engine.Tests (split between Replay/PredicateAnalyzerTests.cs and Replay/QuestDataDrivenStateTests.cs)
