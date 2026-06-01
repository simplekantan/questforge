# InteractObjectStep Authoring Inference Plan (Slice 5)

**Status:** ready for test creation
**Input docs:**
- `docs/INTERACT_OBJECT_STEP_PLAN.md` (engine-side spec; "Scope guard for authoring inference" section identified candidate signals)
- `docs/USE_ACTION_INFERENCE_PLAN.md` (closest polling analog: `PollPlayerActionEffect`, monotonic counter pattern)
- `docs/USE_EMOTE_INFERENCE_PLAN.md` (momentary state pattern analog: `PollPlayerEmote`)
- `docs/USE_ITEM_INFERENCE_PLAN.md` (item-first routing precedent: check specific signal before generic)
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs` (current rule cascade 1..9 + 2.2..2.7, 3.5s/3.5i/3.5j/3.5k/3.5g/3.5e/3.5)
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` (existing signals: ActionCompletedSignal, EmoteCompletedSignal, ItemUsedSignal, etc.)
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` (OnXxxCompleted/OnXxxConsumed pattern)
- `QuestForge.Engine/Authoring/InferredFrom.cs` (21 values; next free: `ObjectInteracted`)
- `QuestForge.Engine/Authoring/StepFactory.cs` (`"interact-object"` arm at line 215, currently uses npcId/npcPos -- needs InteractableId from signal)
- `QuestForge.Plugin.Tracing/UIObserver.cs` (every-frame PollTargetNpc at line 1050; heartbeat PollBattleNpcTarget at line 517)
- `QuestForge.Plugin.Tracing/ITargetProbe.cs` (4 methods: GetInteractableNpcTarget, GetInteractableNpcPreviousTarget, GetAetheryteTarget, GetBattleNpcTarget)
- `QuestForge.Plugin/Tracing/DalamudTargetProbe.cs` (filters by ObjectKind: EventNpc/BattleNpc for interactable NPC, Aetheryte for aetheryte, BattleNpc for hostile)
- `QuestForge.Plugin.Tracing/IGameProbe.cs` (current interface: 13 methods)
- `QuestForge.Plugin/Tracing/DalamudGameProbe.cs` (reads CastInfo.Response* fields via GetLastActionEffect)
- `QuestForge.Plugin/Authoring/AuthoringHost.cs` (RecordStep consume calls at lines 270-279; PreviewInference diag log at line 200)
- `QuestForge.Adapters.Dalamud/Interaction/DalamudInteractor.cs` (InteractWithObject: finds ObjectKind EventObj/Treasure/Aetheryte in ObjectTable)
- `FFXIVClientStructs/FFXIV/Client/Game/Conditions.cs` (OccupiedInQuestEvent at offset 32; OccupiedInEvent at offset 31)

**Output (CI behavior):** When the player interacts with an EventObj (quest sparkle, aether current, door lever, etc.) during an Author-mode recording session, the snapshot field `ObjectInteracted` becomes non-null carrying the EventObj's `BaseId`. `StepInferenceEngine.Infer` returns an `interact-object`-typed `InferenceResult` with `SuggestedExpect = null` (author MUST write the postcondition), `Confidence.High`, `InferredFrom.ObjectInteracted`. Confirming the Record-Step modal records an `InteractObjectStep { InteractableId = signal.InteractableId }` into the draft. CI red to CI green when (a) the snapshot field, (b) the aggregator setter/consumer, (c) the new inference rule, (d) the StepFactory `"interact-object"` arm fix, (e) the ITargetProbe extension, (f) the UIObserver poller, and (g) the RecordStep/ResetWindowState clearing are wired up.

---

## Signal research findings

### The three candidates and why OccupiedInQuestEvent was rejected

**Candidate A: `ConditionFlag.OccupiedInQuestEvent` (offset 32) -- REJECTED**

`Conditions.OccupiedInQuestEvent` (bool at offset 32 in the `Conditions` struct) transitions false-to-true when the player enters a quest event handler and true-to-false when it completes. This covers EventObj interactions that trigger quest cutscenes or dialogue.

Rejected for three reasons:
1. **False positives.** `OccupiedInQuestEvent` fires for ANY quest event, not just EventObj interactions. Talking to an NPC (EventNpc) during a quest also sets this flag. The existing NPC interaction is already captured by `PollTargetNpc` -> `OnInteraction` -> Rule 7. Using `OccupiedInQuestEvent` would produce duplicate signals for NPC interactions and require disambiguation logic.
2. **Missing target identity.** When `OccupiedInQuestEvent` transitions to true, the player's hard target may already be null (the game clears the target once the event handler takes over). Capturing the BaseId at the exact frame of the transition is fragile -- the target could be an NPC, an EventObj, or already cleared.
3. **Not all EventObj interactions are quest events.** Aether currents, for example, do NOT set `OccupiedInQuestEvent` -- they set `OccupiedInEvent` (offset 31, a broader flag). The flag is too narrow for some interactions and too broad for others.

**Candidate B: `CastInfo.ResponseGlobalSequence` (reuse PollPlayerActionEffect) -- REJECTED**

The existing action-effect poller detects when the player fires an action, including some EventObj interactions that use a cast bar. However:
1. **Not all EventObj interactions trigger an ActionEffect.** Many quest interactions (clicking a sparkle, opening a door) are handled by the event system directly with no cast bar, no action, and no Response* field update.
2. **No reliable way to distinguish EventObj interaction from other actions.** The `ResponseActionType` for EventObj interactions varies (sometimes EventItem=3, sometimes None=0). The poller cannot know the target was an EventObj vs an NPC without reading the target at the exact frame.
3. The existing `PollPlayerActionEffect` already routes item and action signals to their respective inference paths. Adding EventObj detection here would create a tangled dispatch.

**Candidate C: EventObj target capture + OccupiedInEvent transition -- SELECTED**

The chosen approach combines two signals:
1. **ITargetProbe.GetEventObjTarget() -- new method.** Captures the player's hard target when it is an `ObjectKind.EventObj` entity, returning the `BaseId` (the interactable data ID used in quest definitions).
2. **`Conditions.OccupiedInEvent` (offset 31) transition detection.** `OccupiedInEvent` is the broader flag that fires for ALL event interactions (quest events, aether currents, cutscenes, etc.). When it transitions false-to-true AND the player's hard target is an EventObj, the interaction has begun.

**Why this combination works:**
- The EventObj target capture provides the `BaseId` (= InteractableId) needed for the drafted step.
- The `OccupiedInEvent` transition confirms the player actually interacted (not just targeted the object).
- The target is captured every frame (before `OccupiedInEvent` fires), so the BaseId is reliably available when the transition occurs.
- Filtering to `ObjectKind.EventObj` targets eliminates false positives from NPC interactions (EventNpc), aetheryte attunements (Aetheryte), and hostile targeting (BattleNpc).

**Why not just EventObj target alone?** Targeting an EventObj does not mean interacting with it. The player may target an object, decide not to interact, and walk away. The `OccupiedInEvent` transition serves as the confirmation that the interaction was initiated.

### Edge case: rapid re-interaction

When the player interacts with the same EventObj twice (e.g., retry after failure), `OccupiedInEvent` transitions true-to-false-to-true. The poller tracks the most recent confirmed interaction. Last-write-wins: if two interactions happen in the same recording window, the second one overwrites the first. This matches the ItemUsed/ActionCompleted pattern.

### Edge case: aether currents

Aether currents are `ObjectKind.EventObj` entities. They DO set `OccupiedInEvent` and DO have a `BaseId` that maps to the AetherCurrent Lumina sheet. The inference engine will emit `interact-object` for aether current interactions. The author can (a) leave it as interact-object with an `expect: "isAetherCurrentAttuned(dataId)"` predicate, or (b) edit the step type. This is consistent with the plan: "Default to interact-object; author edits if needed."

### Edge case: Treasure chests

`DalamudInteractor.InteractWithObject` also accepts `ObjectKind.Treasure`. The ITargetProbe extension should capture Treasure objects alongside EventObj so chest interactions are also detected. The BaseId for Treasure objects maps to BNpcBase (the chest's visual model), which may or may not be the InteractableId used in quest definitions. Low risk: treasure chest steps are rare in quest automation.

---

## Architectural decisions

### OI-INF-1: Signal = EventObj target + OccupiedInEvent transition

**Decision:** The signal is a two-part composite: (1) an EventObj hard target captured by `ITargetProbe.GetEventObjTarget()` each frame, and (2) a `Conditions.OccupiedInEvent` false-to-true transition polled each frame by `IGameProbe.IsOccupiedInEvent()`.

When `OccupiedInEvent` transitions false-to-true AND the most recently captured EventObj target has a non-zero BaseId, the interaction is confirmed and `OnObjectInteracted(baseId)` fires on the aggregator.

**Alternatives considered:**
- OccupiedInQuestEvent alone: rejected (see signal research -- false positives from NPC events, missing target identity).
- CastInfo.ResponseGlobalSequence: rejected (not all EventObj interactions trigger ActionEffect).
- EventObj target change alone (without OccupiedInEvent): rejected (targeting != interacting).

**What breaks if violated:** Using `OccupiedInQuestEvent` instead of `OccupiedInEvent` would miss aether current interactions. Using target capture without the OccupiedInEvent guard would fire on mere targeting without interaction.

**Testability:** The signal research establishes that `OccupiedInEvent` is the broadest event-participation flag. In tests, `FakeGameProbe.SetOccupiedInEvent(bool)` simulates the flag state. The poller tests verify the two-signal composite: EventObj target + OccupiedInEvent transition.

```csharp
// IGameProbe.cs -- new method
bool IsOccupiedInEvent();

// ITargetProbe.cs -- new method
(uint BaseId, float X, float Y, float Z, int Zone)? GetEventObjTarget();
```

### OI-INF-2: ObjectInteractedSignal record

**Decision:** New signal record on `GameStateSnapshot`:

```csharp
// GameStateSnapshot.cs
public sealed record ObjectInteractedSignal(uint InteractableId, float X, float Y, float Z);
```

The signal carries:
- `InteractableId` -- the EventObj's `BaseId` (matches `InteractObjectStep.InteractableId`).
- `X, Y, Z` -- the EventObj's world position at the moment of interaction detection (used by StepFactory to populate `InteractObjectStep.Position`).

**Why carry position?** The object's position is needed to populate `InteractObjectStep.Position` for implied navigation. Without it, StepFactory would need to fall back to the player's position (which is only "near" the object, not at it). Capturing the EventObj's position from ITargetProbe gives the exact coordinates.

**Why not carry Zone?** Zone is already on the base `GameStateSnapshot.Zone` field. StepFactory reads it from `after.Zone`.

```csharp
// GameStateSnapshot.cs -- non-positional init-only property
public ObjectInteractedSignal? ObjectInteracted { get; init; }
```

### OI-INF-3: Aggregator lifecycle -- survives ResetDeltas

**Decision:** `_objectInteracted` is NOT cleared in `ResetDeltas`. It survives across recording windows. It is cleared ONLY by `OnObjectInteractedConsumed()` (called from `AuthoringHost.RecordStep` and `UIObserver.ResetWindowState`).

This follows the pattern established by ActionCompleted, EmoteCompleted, SayChatMessageSent, ItemUsed, EquipmentChanged, JobChanged, and GearsetRegistered.

```csharp
// SnapshotAggregator.cs
private ObjectInteractedSignal? _objectInteracted;

public void OnObjectInteracted(uint interactableId, float x, float y, float z)
    => _objectInteracted = new ObjectInteractedSignal(interactableId, x, y, z);

public void OnObjectInteractedConsumed() => _objectInteracted = null;
```

The `Current` property includes `ObjectInteracted = _objectInteracted` in the init block.

### OI-INF-4: InferredFrom.ObjectInteracted

**Decision:** New enum value `ObjectInteracted` appended to `InferredFrom.cs` (after `GearsetRegistered`).

```csharp
// InferredFrom.cs -- append
ObjectInteracted,
```

### OI-INF-5: Inference rule placement -- Rule 3.5o, below GearsetRegistered (3.5k), above EquipmentChanged (3.5g)

**Decision:** New rule `3.5o` (o for object) placed between Rule 3.5k (GearsetRegistered) and Rule 3.5g (EquipmentChanged) in the `StepInferenceEngine.Infer` method.

**Why this placement?**
- ABOVE EquipmentChanged (3.5g): Equipment changes are less specific. An object interaction should win over an incidental equipment change in the same recording window.
- ABOVE EmoteCompleted (3.5e): Object interaction is the more deliberate authoring intent when both fire.
- ABOVE ActionCompleted (3.5): Object interaction is the more deliberate intent when both fire (some EventObj interactions trigger secondary actions).
- BELOW GearsetRegistered (3.5k): Gearset creation is a distinct, deliberate action. If the player creates a gearset AND interacts with an EventObj in the same window, the gearset creation is the more specific signal.
- BELOW JobChanged (3.5j): Job changes are more specific.
- BELOW ItemUsed (3.5i): Item use (key item on an EventObj) is the more specific signal.
- BELOW SayChatMessageSent (3.5s): Chat is always intentional.
- BELOW all Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6, 2.7.
- BELOW Rule 3.5s, 3.5i, 3.5j, 3.5k.
- ABOVE Rule 3.5g, 3.5e, 3.5.
- ABOVE Rule 3 (QuestSequence advanced): object interaction is more specific than the catch-all "talk" step.

**Concrete rule:**

```csharp
// Rule 3.5o -- ObjectInteracted
// Fires when UIObserver.PollEventObjInteraction detected that the player interacted with an
// EventObj (quest sparkle, aether current, door lever, etc.) during this recording window.
//
// PRIORITY: above Rule 3.5g (EquipmentChanged). Object interaction is the more deliberate signal.
// PRIORITY: below Rule 3.5k (GearsetRegistered). Gearset creation is more specific.
// CONFIDENCE: High. EXPECT: null -- author MUST write the postcondition.
if (after.ObjectInteracted is { } objSignal)
{
    return new InferenceResult(
        StepType:        "interact-object",
        SuggestedStepId: $"interact-object-{objSignal.InteractableId}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.ObjectInteracted,
        Notes:           "Author MUST write the Expect predicate (no universal object interaction postcondition).");
}
```

**Default step type is `interact-object`, not `pickup-item`.** The inference engine cannot distinguish the two at detection time (both are EventObj interactions). The author edits the step type to `pickup-item` in the Record-Step modal if appropriate. This matches the plan's design (IO1: two step types, one action, one adapter).

### OI-INF-6: StepFactory `"interact-object"` arm -- use signal, not npcId

**Decision:** The existing StepFactory `"interact-object"` arm at line 215 uses `npcId` (from `after.LastNpcInteracted`) and `npcPos` (from `after.LastNpcPosition`) to populate `InteractableId` and `Position`. This is WRONG for EventObj interactions because:
- `PollTargetNpc` only captures `ObjectKind.EventNpc` and `ObjectKind.BattleNpc` targets. EventObj targets are NOT captured by it.
- `LastNpcInteracted` will contain whatever EventNpc was targeted before the EventObj (the last NPC the player talked to), which is the wrong entity.

The fix: when `after.ObjectInteracted` is non-null, use the signal's `InteractableId` and position. Fall back to the existing npcId-based logic when no signal is present (for backward compatibility with traces recorded before this slice).

```csharp
"interact-object" => new InteractObjectStep
{
    Id = stepId,
    Expect = expectValue,
    Zone = zoneStr,
    RequiredZone = zoneStr,
    InteractableId = after?.ObjectInteracted?.InteractableId ?? npcId,
    Position = after?.ObjectInteracted is { } oi
        ? new Position3(oi.X, oi.Y, oi.Z)
        : npcPos
},
"pickup-item" => new PickupItemStep
{
    Id = stepId,
    Expect = expectValue,
    Zone = zoneStr,
    RequiredZone = zoneStr,
    InteractableId = after?.ObjectInteracted?.InteractableId ?? npcId,
    Position = after?.ObjectInteracted is { } pi
        ? new Position3(pi.X, pi.Y, pi.Z)
        : npcPos
},
```

### OI-INF-7: ITargetProbe.GetEventObjTarget() -- new method

**Decision:** Add a new method to `ITargetProbe` that returns the current hard target if its `ObjectKind == EventObj` (or `Treasure`, per DalamudInteractor precedent):

```csharp
// ITargetProbe.cs
/// <summary>
/// Current hard target if its ObjectKind is EventObj or Treasure, otherwise null.
/// Used by PollEventObjInteraction to capture the target's BaseId before the
/// OccupiedInEvent transition fires.
/// </summary>
(uint BaseId, float X, float Y, float Z, int Zone)? GetEventObjTarget();
```

**DalamudTargetProbe implementation:**

```csharp
public (uint BaseId, float X, float Y, float Z, int Zone)? GetEventObjTarget()
{
    var t = _targetManager.Target;
    if (t?.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure)) return null;
    var p = t.Position;
    return (t.BaseId, p.X, p.Y, p.Z, (int)_clientState.TerritoryType);
}
```

**What about `ObjectKind.Treasure`?** DalamudInteractor.InteractWithObject already accepts Treasure alongside EventObj (line 51). Including Treasure in the target probe ensures treasure chest interactions are also detected. This is low risk: treasure chests in quest automation are rare, and the author can adjust the step type if needed.

### OI-INF-8: IGameProbe.IsOccupiedInEvent() -- new method

**Decision:** Add a new method to `IGameProbe` that reads `Conditions.OccupiedInEvent` (offset 31):

```csharp
// IGameProbe.cs
/// <summary>
/// Returns true when the player is occupied in an event (quest interaction, aether current,
/// cutscene trigger, etc.). Read from Conditions.OccupiedInEvent (offset 31).
/// </summary>
bool IsOccupiedInEvent();
```

**DalamudGameProbe implementation:**

```csharp
public bool IsOccupiedInEvent()
{
    unsafe
    {
        var cond = Conditions.Instance();
        return cond != null && cond->OccupiedInEvent;
    }
}
```

### OI-INF-9: UIObserver polling -- PollEventObjInteraction, every-frame

**Decision:** New every-frame poller `PollEventObjInteraction()` added to `OnFrameworkUpdate` after `PollTargetNpc` and before the heartbeat throttle check.

The poller is a two-state machine:
1. **Every frame:** read `ITargetProbe.GetEventObjTarget()`. If non-null, latch the BaseId and position as `_lastEventObjBaseId` / `_lastEventObjPosition`. This ensures the EventObj's identity is captured BEFORE the game clears the target on interaction.
2. **Every frame:** read `IGameProbe.IsOccupiedInEvent()`. On transition false-to-true:
   - If `_lastEventObjBaseId` is non-zero, fire `OnObjectInteracted(_lastEventObjBaseId, x, y, z)` on the aggregator and emit a trace observation `"ObjectInteracted"`.
   - Clear `_lastEventObjBaseId` so the same interaction does not re-fire.
3. On transition true-to-false: no action (the interaction is complete, but the signal was already captured).

**Why every-frame, not heartbeat?** The OccupiedInEvent flag can transition within a single frame. At 250ms heartbeat, the transition may be missed entirely if the event is short (e.g., aether current attunement completes in ~500ms, leaving a narrow window). Every-frame polling ensures the transition is always detected.

**State fields:**

```csharp
// UIObserver.cs -- new tracking fields
private uint _lastEventObjBaseId;
private (float X, float Y, float Z, int Zone) _lastEventObjPosition;
private bool _lastOccupiedInEvent;
```

**Poller:**

```csharp
private void PollEventObjInteraction()
{
    if (_targetProbe is null || _gameProbe is null) return;

    // Latch EventObj target every frame (before OccupiedInEvent check)
    var eventObj = _targetProbe.GetEventObjTarget();
    if (eventObj.HasValue)
    {
        _lastEventObjBaseId = eventObj.Value.BaseId;
        _lastEventObjPosition = (eventObj.Value.X, eventObj.Value.Y, eventObj.Value.Z, eventObj.Value.Zone);
    }

    // Detect OccupiedInEvent transition
    var occupied = _gameProbe.IsOccupiedInEvent();
    if (occupied && !_lastOccupiedInEvent && _lastEventObjBaseId != 0)
    {
        var now = _clock.UtcNow;
        var runId = CurrentRunId;
        WriteObservation("ObjectInteracted", _lastEventObjBaseId,
            new { x = _lastEventObjPosition.X, y = _lastEventObjPosition.Y,
                  z = _lastEventObjPosition.Z, zone = _lastEventObjPosition.Zone },
            runId, now);
        _aggregator?.OnObjectInteracted(
            _lastEventObjBaseId,
            _lastEventObjPosition.X,
            _lastEventObjPosition.Y,
            _lastEventObjPosition.Z);
        _lastEventObjBaseId = 0; // prevent re-fire
    }
    _lastOccupiedInEvent = occupied;
}
```

**Why clear `_lastEventObjBaseId` after fire?** Prevents double-fire if the player targets the same EventObj again before the OccupiedInEvent flag drops. The target is re-latched on the next frame if the player re-targets.

### OI-INF-10: ResetWindowState and consume sequence

**Decision:** `ResetWindowState` clears:
- `_lastEventObjBaseId = 0` (prevent stale EventObj target from bleeding)
- `_lastOccupiedInEvent = false` (reset transition detector)
- `_aggregator?.OnObjectInteractedConsumed()` (clear the signal)

`RecordStep` in `AuthoringHost` calls `_aggregator.OnObjectInteractedConsumed()` alongside the existing consume sequence (after `OnGearsetRegisteredConsumed()`).

```csharp
// AuthoringHost.cs RecordStep -- append to consume sequence
_aggregator.OnObjectInteractedConsumed();
```

### OI-INF-11: PreviewInference diagnostic log

**Decision:** Extend the `[QF-DIAG] PreviewInference:` log line in `AuthoringHost.PreviewInference` with `ObjectInteracted={after.ObjectInteracted?.InteractableId}`.

### OI-INF-12: FakeGameProbe and FakeTargetProbe extensions

**Decision:** Test fakes need new methods:

```csharp
// FakeGameProbe (in QuestForge.Plugin.Tests or wherever the fake lives)
private bool _occupiedInEvent;
public void SetOccupiedInEvent(bool value) => _occupiedInEvent = value;
public bool IsOccupiedInEvent() => _occupiedInEvent;
```

```csharp
// FakeTargetProbe
private (uint BaseId, float X, float Y, float Z, int Zone)? _eventObjTarget;
public void SetEventObjTarget(uint baseId, float x, float y, float z, int zone)
    => _eventObjTarget = (baseId, x, y, z, zone);
public void ClearEventObjTarget() => _eventObjTarget = null;
public (uint BaseId, float X, float Y, float Z, int Zone)? GetEventObjTarget()
    => _eventObjTarget;
```

---

## Task breakdown

### Task 1 -- Signal record + snapshot field (Engine)

**1.1** Add `ObjectInteractedSignal` record to `GameStateSnapshot.cs`:
```csharp
public sealed record ObjectInteractedSignal(uint InteractableId, float X, float Y, float Z);
```

**1.2** Add non-positional init-only property to `GameStateSnapshot`:
```csharp
public ObjectInteractedSignal? ObjectInteracted { get; init; }
```

### Task 2 -- Aggregator (Engine)

**2.1** Add `_objectInteracted` field to `SnapshotAggregator`:
```csharp
private ObjectInteractedSignal? _objectInteracted;
```

**2.2** Add `OnObjectInteracted` and `OnObjectInteractedConsumed` methods:
```csharp
public void OnObjectInteracted(uint interactableId, float x, float y, float z)
    => _objectInteracted = new ObjectInteractedSignal(interactableId, x, y, z);

public void OnObjectInteractedConsumed() => _objectInteracted = null;
```

**2.3** Add `ObjectInteracted = _objectInteracted` to the `Current` property's init block.

### Task 3 -- InferredFrom enum (Engine)

**3.1** Append `ObjectInteracted` to `InferredFrom.cs` after `GearsetRegistered`.

### Task 4 -- Inference rule 3.5o (Engine)

**4.1** Add Rule 3.5o in `StepInferenceEngine.Infer()` between Rule 3.5k (GearsetRegistered) and Rule 3.5g (EquipmentChanged):
```csharp
// Rule 3.5o -- ObjectInteracted
if (after.ObjectInteracted is { } objSignal)
{
    return new InferenceResult(
        StepType:        "interact-object",
        SuggestedStepId: $"interact-object-{objSignal.InteractableId}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.ObjectInteracted,
        Notes:           "Author MUST write the Expect predicate (no universal object interaction postcondition).");
}
```

### Task 5 -- StepFactory fix (Engine)

**5.1** Update `"interact-object"` and `"pickup-item"` arms to use `ObjectInteracted` signal when available:
```csharp
"interact-object" => new InteractObjectStep
{
    Id = stepId,
    Expect = expectValue,
    Zone = zoneStr,
    RequiredZone = zoneStr,
    InteractableId = after?.ObjectInteracted?.InteractableId ?? npcId,
    Position = after?.ObjectInteracted is { } oi
        ? new Position3(oi.X, oi.Y, oi.Z)
        : npcPos
},
```
Same pattern for `"pickup-item"`.

### Task 6 -- ITargetProbe.GetEventObjTarget (Plugin.Tracing)

**6.1** Add `GetEventObjTarget()` to `ITargetProbe`:
```csharp
(uint BaseId, float X, float Y, float Z, int Zone)? GetEventObjTarget();
```

**6.2** Implement in `DalamudTargetProbe`:
```csharp
public (uint BaseId, float X, float Y, float Z, int Zone)? GetEventObjTarget()
{
    var t = _targetManager.Target;
    if (t?.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure)) return null;
    var p = t.Position;
    return (t.BaseId, p.X, p.Y, p.Z, (int)_clientState.TerritoryType);
}
```

### Task 7 -- IGameProbe.IsOccupiedInEvent (Plugin.Tracing)

**7.1** Add `IsOccupiedInEvent()` to `IGameProbe`:
```csharp
bool IsOccupiedInEvent();
```

**7.2** Implement in `DalamudGameProbe`:
```csharp
public bool IsOccupiedInEvent()
{
    unsafe
    {
        var cond = Conditions.Instance();
        return cond != null && cond->OccupiedInEvent;
    }
}
```

### Task 8 -- UIObserver PollEventObjInteraction (Plugin.Tracing)

**8.1** Add tracking fields:
```csharp
private uint _lastEventObjBaseId;
private (float X, float Y, float Z, int Zone) _lastEventObjPosition;
private bool _lastOccupiedInEvent;
```

**8.2** Add `PollEventObjInteraction()` poller method (see OI-INF-9 for full implementation).

**8.3** Wire into `OnFrameworkUpdate` after `PollGearsetCount()` and before the heartbeat throttle check.

**8.4** Extend `ResetWindowState` with:
```csharp
_lastEventObjBaseId = 0;
_lastOccupiedInEvent = false;
_aggregator?.OnObjectInteractedConsumed();
```

### Task 9 -- AuthoringHost (Plugin)

**9.1** Append `_aggregator.OnObjectInteractedConsumed()` to the consume sequence in `RecordStep` (after `OnGearsetRegisteredConsumed()`).

**9.2** Extend `PreviewInference` diagnostic log line with `ObjectInteracted={after.ObjectInteracted?.InteractableId}`.

### Task 10 -- FakeGameProbe and FakeTargetProbe extensions (Tests)

**10.1** Add `SetOccupiedInEvent` / `IsOccupiedInEvent` to `FakeGameProbe`.

**10.2** Add `SetEventObjTarget` / `ClearEventObjTarget` / `GetEventObjTarget` to `FakeTargetProbe`.

---

## Given-When-Then specifications

### Inference rule tests (InteractObjectInferenceTests.cs in QuestForge.Engine.Tests/Authoring/)

**OI-I1: ObjectInteracted fires rule 3.5o -- happy path**
- Given: `before` snapshot with no ObjectInteracted; `after` snapshot with `ObjectInteracted = new(2001500, 10f, 5f, 20f)`
- When: `StepInferenceEngine.Infer(before, after)`
- Then: result has `StepType == "interact-object"`, `SuggestedStepId == "interact-object-2001500"`, `SuggestedExpect == null`, `Confidence == High`, `InferredFrom == ObjectInteracted`
- And: result.Notes contains "Author MUST write the Expect predicate"

**OI-I2: ObjectInteracted loses to QuestCompleted (Rule 1)**
- Given: `before` with QuestCompleted=false; `after` with QuestCompleted=true AND ObjectInteracted = new(2001500, 10f, 5f, 20f)
- When: `Infer(before, after)`
- Then: result has `StepType == "turn-in"`, `InferredFrom == QuestCompleted`

**OI-I3: ObjectInteracted loses to QuestAccepted (Rule 2)**
- Given: `before` with QuestAccepted=false; `after` with QuestAccepted=true AND ObjectInteracted set
- When: `Infer(before, after)`
- Then: result has `StepType == "accept"`, `InferredFrom == QuestAccepted`

**OI-I4: ObjectInteracted loses to ItemUsed (Rule 3.5i)**
- Given: `after` with both ObjectInteracted = new(2001500, ...) AND ItemUsed = new(KeyItem, 1234, null, null)
- When: `Infer(before, after)`
- Then: result has `StepType == "use-item"`, `InferredFrom == ItemUsed`

**OI-I5: ObjectInteracted loses to JobChanged (Rule 3.5j)**
- Given: `after` with both ObjectInteracted set AND JobChanged = new(1, 22)
- When: `Infer(before, after)`
- Then: result has `StepType == "change-job"`, `InferredFrom == JobChanged`

**OI-I6: ObjectInteracted loses to GearsetRegistered (Rule 3.5k)**
- Given: `after` with both ObjectInteracted set AND GearsetRegistered = new(3, 4)
- When: `Infer(before, after)`
- Then: result has `StepType == "register-gearset"`, `InferredFrom == GearsetRegistered`

**OI-I7: ObjectInteracted beats EquipmentChanged (Rule 3.5g)**
- Given: `after` with both ObjectInteracted = new(2001500, ...) AND EquipmentChanged = new([12345])
- When: `Infer(before, after)`
- Then: result has `StepType == "interact-object"`, `InferredFrom == ObjectInteracted`

**OI-I8: ObjectInteracted beats EmoteCompleted (Rule 3.5e)**
- Given: `after` with both ObjectInteracted = new(2001500, ...) AND EmoteCompleted = new(50, null)
- When: `Infer(before, after)`
- Then: result has `StepType == "interact-object"`, `InferredFrom == ObjectInteracted`

**OI-I9: ObjectInteracted beats ActionCompleted (Rule 3.5)**
- Given: `after` with both ObjectInteracted = new(2001500, ...) AND ActionCompleted = new(Action, 7, null)
- When: `Infer(before, after)`
- Then: result has `StepType == "interact-object"`, `InferredFrom == ObjectInteracted`

**OI-I10: ObjectInteracted beats QuestSequence advance (Rule 3)**
- Given: `before` with QuestSequence=1; `after` with QuestSequence=2 AND ObjectInteracted = new(2001500, ...)
- When: `Infer(before, after)`
- Then: result has `StepType == "interact-object"`, `InferredFrom == ObjectInteracted`

**OI-I11: No ObjectInteracted, no other signal -- falls through to Rule 9 (Empty)**
- Given: before and after identical (no changes, no signals)
- When: `Infer(before, after)`
- Then: result == `InferenceResult.Empty`

### StepFactory tests (InteractObjectInferenceTests.cs -- same file, separate section)

**OI-F1: StepFactory builds InteractObjectStep with signal InteractableId and position**
- Given: stepType="interact-object", after.ObjectInteracted = new(2001500, 10f, 5f, 20f), zone=134
- When: `StepFactory.Build("interact-object", "interact-1", null, after)`
- Then: result is InteractObjectStep with InteractableId=2001500, Position=(10,5,20), Zone="134"

**OI-F2: StepFactory builds InteractObjectStep with fallback to npcId when no signal**
- Given: stepType="interact-object", after.ObjectInteracted = null, after.LastNpcInteracted = NpcId(5555), after.LastNpcPosition = (1,2,3)
- When: `StepFactory.Build("interact-object", "interact-1", null, after)`
- Then: result is InteractObjectStep with InteractableId=5555, Position=(1,2,3)

**OI-F3: StepFactory builds PickupItemStep with signal InteractableId and position**
- Given: stepType="pickup-item", after.ObjectInteracted = new(2001234, 81.5f, 7f, 32.2f), zone=134
- When: `StepFactory.Build("pickup-item", "pickup-1", null, after)`
- Then: result is PickupItemStep with InteractableId=2001234, Position=(81.5,7,32.2), Zone="134"

### Aggregator tests (append to existing SnapshotAggregatorTests or new file)

**OI-A1: OnObjectInteracted sets ObjectInteracted on Current snapshot**
- Given: fresh SnapshotAggregator
- When: `OnObjectInteracted(2001500, 10f, 5f, 20f)`
- Then: `Current.ObjectInteracted` is `new ObjectInteractedSignal(2001500, 10, 5, 20)`

**OI-A2: OnObjectInteractedConsumed clears the signal**
- Given: aggregator with ObjectInteracted set
- When: `OnObjectInteractedConsumed()`
- Then: `Current.ObjectInteracted` is null

**OI-A3: ObjectInteracted survives ResetDeltas**
- Given: aggregator with ObjectInteracted set
- When: `ResetDeltas()`
- Then: `Current.ObjectInteracted` is still non-null (signal preserved)

**OI-A4: Last-write-wins on multiple OnObjectInteracted calls**
- Given: `OnObjectInteracted(100, 1, 2, 3)` then `OnObjectInteracted(200, 4, 5, 6)`
- Then: `Current.ObjectInteracted?.InteractableId == 200`

### UIObserver polling tests (append to UIObserverTests.cs)

**UO_OI1: EventObj targeted + OccupiedInEvent transition fires ObjectInteracted**
- Given: FakeTargetProbe returns EventObjTarget(BaseId=2001500, X=10, Y=5, Z=20, Zone=134); FakeGameProbe.IsOccupiedInEvent() returns false
- When: one frame tick (target is latched)
- And: FakeGameProbe.SetOccupiedInEvent(true); one more frame tick (transition detected)
- Then: aggregator.Current.ObjectInteracted is new(2001500, 10, 5, 20)
- And: trace observation "ObjectInteracted" is written with argument=2001500

**UO_OI2: No EventObj target + OccupiedInEvent transition does NOT fire**
- Given: FakeTargetProbe returns no EventObjTarget (null); FakeGameProbe.IsOccupiedInEvent() returns false
- When: FakeGameProbe.SetOccupiedInEvent(true); one frame tick
- Then: aggregator.Current.ObjectInteracted is null

**UO_OI3: EventObj targeted but no OccupiedInEvent transition does NOT fire**
- Given: FakeTargetProbe returns EventObjTarget(BaseId=2001500, ...); FakeGameProbe.IsOccupiedInEvent() returns false
- When: multiple frame ticks (target latched, but no transition)
- Then: aggregator.Current.ObjectInteracted is null

**UO_OI4: Same EventObj does not re-fire after initial detection**
- Given: EventObj 2001500 already detected (OI fired)
- When: OccupiedInEvent transitions true-to-false-to-true (same target)
- Then: aggregator.Current.ObjectInteracted is STILL the first signal (not re-fired because BaseId was cleared)
- Note: actually, after true-to-false, if the EventObj is re-targeted (PollEventObjInteraction latches it again) and OccupiedInEvent transitions false-to-true again, it SHOULD fire again. The test verifies that the _lastEventObjBaseId is cleared after the first fire, and only re-fires if the target is re-latched.

**UO_OI5: ResetWindowState clears EventObj tracking state**
- Given: EventObj was detected (ObjectInteracted signal set)
- When: ResetWindowState()
- Then: _lastEventObjBaseId is 0, aggregator.Current.ObjectInteracted is null

**UO_OI6: Different EventObj replaces the latched target**
- Given: FakeTargetProbe returns EventObjTarget(BaseId=2001500, ...) on frame 1
- When: FakeTargetProbe returns EventObjTarget(BaseId=2001600, ...) on frame 2
- And: FakeGameProbe.SetOccupiedInEvent(true) on frame 3
- Then: aggregator.Current.ObjectInteracted?.InteractableId == 2001600 (last target wins)

**UO_OI7: OccupiedInEvent already true at start (baseline) does not fire**
- Given: FakeGameProbe.IsOccupiedInEvent() returns true on the first frame; FakeTargetProbe returns EventObjTarget
- When: frame tick (no transition -- was already true)
- Then: aggregator.Current.ObjectInteracted is null
- Note: The poller initializes `_lastOccupiedInEvent = false`. On the first frame, if OccupiedInEvent is already true, this is a false-to-true transition and WILL fire if an EventObj target is latched. This is intentional -- if the observer starts mid-interaction, the signal fires once. Confirm this is desired behavior.

Actually, re-evaluating OI-INF-9: `_lastOccupiedInEvent` starts as `false` (default). If the game is already in an event when UIObserver starts, the first frame sees a false-to-true transition. This is acceptable: the worst case is a spurious fire that the author dismisses. The alternative (initializing `_lastOccupiedInEvent` from a probe read in the constructor) adds complexity for marginal benefit. Keeping `_lastOccupiedInEvent = false` as default and accepting the first-frame edge case.

Revised OI7:
**UO_OI7: OccupiedInEvent true on first frame with EventObj target fires (first-frame edge case)**
- Given: FakeGameProbe.IsOccupiedInEvent() returns true; FakeTargetProbe returns EventObjTarget(BaseId=2001500, ...)
- When: first frame tick
- Then: aggregator.Current.ObjectInteracted?.InteractableId == 2001500 (transition from default false to true)

---

## Implementation order

### Phase A -- Engine-side signal (30 min)
1. Add `ObjectInteractedSignal` record to `GameStateSnapshot.cs`
2. Add `ObjectInteracted` init-only property to `GameStateSnapshot`
3. Add `_objectInteracted`, `OnObjectInteracted`, `OnObjectInteractedConsumed` to `SnapshotAggregator`
4. Wire `ObjectInteracted = _objectInteracted` into `SnapshotAggregator.Current`
5. Append `ObjectInteracted` to `InferredFrom.cs`
Done-before-next: `dotnet build QuestForge.Engine` passes.

### Phase B -- Inference rule + StepFactory fix (1 hour)
1. Add Rule 3.5o to `StepInferenceEngine.Infer()` between 3.5k and 3.5g
2. Update `"interact-object"` and `"pickup-item"` arms in `StepFactory.Build()` to prefer `ObjectInteracted` signal
3. Write inference tests (OI-I1 through OI-I11)
4. Write StepFactory tests (OI-F1 through OI-F3)
5. Write aggregator tests (OI-A1 through OI-A4)
Done-before-next: `dotnet test QuestForge.Engine.Tests` passes.

### Phase C -- ITargetProbe + IGameProbe extension (30 min)
1. Add `GetEventObjTarget()` to `ITargetProbe`
2. Add `IsOccupiedInEvent()` to `IGameProbe`
3. Implement in `DalamudTargetProbe` and `DalamudGameProbe`
4. Extend `FakeTargetProbe` and `FakeGameProbe`
Done-before-next: `dotnet build QuestForge.Plugin` passes.

### Phase D -- UIObserver poller (1 hour)
1. Add tracking fields to `UIObserver`
2. Add `PollEventObjInteraction()` method
3. Wire into `OnFrameworkUpdate`
4. Extend `ResetWindowState`
5. Write polling tests (UO_OI1 through UO_OI7)
Done-before-next: `dotnet test QuestForge.Plugin.Tests` passes.

### Phase E -- AuthoringHost consume + diag log (15 min)
1. Add `OnObjectInteractedConsumed()` to RecordStep consume sequence
2. Extend PreviewInference diagnostic log
Done-before-next: `dotnet build QuestForge.Plugin` passes, all tests pass.

---

## Done criteria

1. `GameStateSnapshot.ObjectInteracted` field exists and is populated by `SnapshotAggregator.OnObjectInteracted`.
2. `SnapshotAggregator.OnObjectInteractedConsumed()` clears the signal; `ResetDeltas` does NOT clear it.
3. `InferredFrom.ObjectInteracted` enum value exists.
4. `StepInferenceEngine` Rule 3.5o fires for `after.ObjectInteracted` non-null, producing `StepType="interact-object"` with `Confidence.High`.
5. Rule 3.5o loses to Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6, 3.5s, 3.5i, 3.5j, 3.5k; beats Rules 3.5g, 3.5e, 3.5, 3, 4, 5, 6, 7, 8.
6. `StepFactory` `"interact-object"` arm uses `ObjectInteracted.InteractableId` and position when signal is present; falls back to `npcId`/`npcPos` when null.
7. `ITargetProbe.GetEventObjTarget()` returns the current hard target when `ObjectKind == EventObj` or `ObjectKind == Treasure`.
8. `IGameProbe.IsOccupiedInEvent()` reads `Conditions.OccupiedInEvent`.
9. `UIObserver.PollEventObjInteraction()` fires `OnObjectInteracted` when EventObj target is latched AND `OccupiedInEvent` transitions false-to-true.
10. `AuthoringHost.RecordStep` calls `OnObjectInteractedConsumed()` in its consume sequence.
11. `PreviewInference` diagnostic log includes `ObjectInteracted=N`.
12. `dotnet build` and `dotnet test` pass for all projects.

---

## Exclusions

- **pickup-item disambiguation.** The inference engine always drafts `interact-object`. The author edits to `pickup-item` if appropriate. Automatic disambiguation would require tracking inventory changes correlated with EventObj interactions -- deferred.
- **ConditionFlag.OccupiedInQuestEvent.** Intentionally NOT used. See signal research for rationale.
- **CastInfo.ResponseGlobalSequence for EventObj.** Intentionally NOT used as the primary signal. EventObj interactions may or may not trigger ActionEffect; the OccupiedInEvent flag is the universal confirmation.
- **qf-trace / tooling changes.** No tooling catch-up needed for this slice. The `ObjectInteracted` trace observation is a new event type but does not affect existing trace extraction or capability inference (those already handle `interact-object` and `pickup-item` step types from Slice 3).
- **In-game smoke (Slice 6).** Not in this plan.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Tasks OI-I1 through OI-I11, OI-F1 through OI-F3, OI-A1 through OI-A4, and UO_OI1 through UO_OI7.
- Happy paths: 5 scenarios (OI-I1, OI-F1, OI-F3, OI-A1, UO_OI1)
- Edge cases: 10 scenarios (OI-I7, OI-I8, OI-I9, OI-I10, OI-F2, OI-A3, OI-A4, UO_OI4, UO_OI6, UO_OI7)
- Error/priority cases: 10 scenarios (OI-I2, OI-I3, OI-I4, OI-I5, OI-I6, OI-I11, OI-A2, UO_OI2, UO_OI3, UO_OI5)
- Expected total: ~25 tests in QuestForge.Engine.Tests + QuestForge.Plugin.Tests
