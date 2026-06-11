# SinglePlayerDutyStep -- Authoring Inference Plan (Slice 5)

**Status:** Draft
**Phase:** 11 (Corpus Expansion)
**Step type discriminator:** `"single-player-duty"`
**Slice:** 5 of 6 (Authoring Inference)
**Author:** QuestForge System Architect
**Date:** 2026-06-11

---

## 1. Header

**Input documents:**
- `docs/DUTY_STEP_SPLIT_PLAN.md` -- slice 1 architect spec (schema, engine, validator)
- `docs/DUTY_STEP_SPLIT_SLICE3_PLAN.md` -- slice 3 Dalamud impl + tooling
- `CLAUDE.md` -- fixed slice order, Slice 5 requirements, polling-not-hooks mandate
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs` -- current rule chain (24 rules)
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` -- snapshot properties
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` -- On/Consumed pattern
- `QuestForge.Plugin.Tracing/UIObserver.cs` -- addon polling patterns
- `QuestForge.Plugin/Authoring/AuthoringHost.cs` -- ConsumeAllSignals, PreviewInference

**Output (CI behavior changes):**
- `dotnet test QuestForge.Engine.Tests` gains ~18 new tests in `SinglePlayerDutyInferenceTests.cs`
- `dotnet test QuestForge.Plugin.Tests` gains ~6 new tests (UO_CFC*) appended to `UIObserverTests.cs`
- No existing tests are affected -- this is a purely additive inference rule
- `StepInferenceEngine` gains Rule 2.8 (SpdEntryDetected)
- `StepFactory` gains `"single-player-duty"` arm
- `GameStateSnapshot` gains `SpdEntryDetected` nullable property
- `SnapshotAggregator` gains `OnSpdEntryDetected` / `OnSpdEntryConsumed` methods
- `IGameProbe` gains `GetCurrentContentFinderConditionId` accessor
- `UIObserver` gains `PollContentFinderCondition` state machine (momentary-state pattern)
- `AuthoringHost.PreviewInference` diag line extended with `SpdEntryDetected`
- `AuthoringHost.ConsumeAllSignals` calls `OnSpdEntryConsumed`

**Phase dependencies:**
- SinglePlayerDutyStep schema (slice 2, merged)
- SinglePlayerDutyStep engine resolve + host dispatch (slice 2 + 3, merged)
- SinglePlayerDutyStep validator rules E30-E33, W15 (slice 2, merged)
- InteractObjectStep inference (implemented: Rule 3.5o, ObjectInteractedSignal)
- TalkStep inference (implemented: Rule 3, QuestSequenceChange; Rule 7, DialogueInteraction)
- SelectYesno tracking (implemented: PollSelectYesno, SelectYesnoConfirmed)

---

## 2. Signal Research

### 2.1 The detection problem

Single Player Duty (SPD) entry is a multi-stage flow:

1. Player interacts with an NPC (talk entry), an EventObj (interact entry), or walks into a trigger area (proximity entry)
2. A SelectYesno or SelectString dialog appears (confirm entry / choose difficulty)
3. Player confirms
4. Loading screen -- `CurrentContentFinderConditionId` transitions from 0 to a non-zero value
5. Player is inside the SPD instance (`InstanceKind.SinglePlayerDuty`)

The challenge: the inference engine must produce a `SinglePlayerDutyStep` that contains:
- `ContentFinderConditionId` (the CFC ID of the SPD)
- `EntryKind` (talk / interact / proximity)
- `EntryTargetId` (NPC BaseId or EventObj BaseId for talk/interact; null for proximity)
- `EntryPosition` (world position of the entry trigger)

The CFC ID is only available AFTER entering the instance. But the entry information (NPC/object interaction, player position) is available BEFORE entering. The inference engine must combine signals from both sides of the loading screen.

### 2.2 Available FFXIVClientStructs fields

**Primary signal: `GameMain.Instance()->CurrentContentFinderConditionId` (ushort)**
- Located at offset `0x4114` in `GameMain`
- Value is 0 in the open world
- Transitions to a non-zero CFC row ID when entering any instanced content (dungeons, trials, SPDs)
- Transitions back to 0 when exiting the instance
- Available from `FFXIVClientStructs.FFXIV.Client.Game.GameMain`

**Secondary signals considered and rejected:**

| Signal | Source | Why rejected |
|--------|--------|--------------|
| `Conditions.BoundByDuty` (offset 34) | `FFXIVClientStructs.FFXIV.Client.Game.Conditions` | Boolean only -- tells us the player is in a duty but not WHICH duty. Insufficient for populating `ContentFinderConditionId`. |
| `Conditions.BoundByDuty56` (offset 56) | Same | Same problem -- boolean, no CFC ID. |
| `GameMain.IsInInstanceArea()` | Member function | Boolean only. |
| `GameMain.CurrentTerritoryIntendedUseId` | Offset `0x410C` | Gives `TerritoryIntendedUse` enum, not CFC ID. Could correlate via Lumina but adds fragile indirection. |
| Director / QuestBattle structs | Various | Deeply nested, version-fragile, and the CFC ID on GameMain already gives us what we need directly. |

**Conclusion:** `GameMain.CurrentContentFinderConditionId` is the correct, stable, minimal signal. It is already exposed in the PlayerStatePanel (this branch), confirming it reads correctly at runtime.

### 2.3 Combining pre-entry and post-entry information

The key insight: when the player enters an SPD, we need information from TWO moments in time:

1. **Before entry** (captured in the "before" snapshot and live aggregator state):
   - `LastNpcInteracted` + `LastNpcPosition` -- the NPC the player talked to (talk entry)
   - `ObjectInteracted` -- the EventObj the player interacted with (interact entry)
   - `SelectYesnoConfirmed` -- the player confirmed the duty entry dialog
   - Player position -- where the player was standing (proximity entry)

2. **After entry** (captured by the CFC ID transition):
   - `CurrentContentFinderConditionId` -- which SPD was entered
   - Zone change -- the player is now in the SPD instance zone

The inference engine already has access to both before and after snapshots. The CFC ID transition fires as a new signal on the after snapshot; the entry context is already available from existing signals (NPC target, object interaction, yesno confirmation).

### 2.4 Polling pattern selection

**Momentary state** -- matches the `PollPlayerEmote` pattern:
- CFC ID goes from 0 (open world) to N (inside instance) to 0 (exited instance)
- First non-zero observation fires immediately (the player just entered an SPD)
- Same non-zero value persisting = no re-fire (player is still inside)
- Transition to 0 silently resets (player exited, ready for next detection)

This is NOT a monotonic counter (like `ResponseGlobalSequence`). The value can return to 0 and then transition to a different non-zero value for a different SPD.

---

## 3. Architectural Decisions

### SPDI1: SpdEntryDetectedSignal record on GameStateSnapshot

**Decision:** Add a `SpdEntryDetectedSignal` record and a nullable `SpdEntryDetected` property on `GameStateSnapshot`. The signal carries only the `ContentFinderConditionId` (ushort). Entry context (NPC, object, position) is derived from existing snapshot fields by the inference engine.

**Alternatives rejected:**
- **Composite signal carrying NPC ID + CFC ID:** The NPC/object information is already on the snapshot (`LastNpcInteracted`, `ObjectInteracted`, player position). Duplicating it in a composite signal would create two sources of truth. The inference engine already pattern-matches across snapshot fields -- adding one more field is consistent.
- **No signal record -- bare ushort property:** A signal record is the established pattern for all signals that survive `ResetDeltas` and require explicit consumption (ActionCompleted, EmoteCompleted, ItemUsed, etc.). Using a bare ushort would break the consume pattern.

**Concrete surface area:**
```csharp
// GameStateSnapshot.cs -- new signal record
public sealed record SpdEntryDetectedSignal(ushort ContentFinderConditionId);

// GameStateSnapshot.cs -- new non-positional init-only property
public SpdEntryDetectedSignal? SpdEntryDetected { get; init; }
```

**What breaks if violated:** If someone uses a bare ushort, there is no consume method pattern and the value cannot be null-checked idiomatically. If someone duplicates entry context into the signal, the inference engine must decide which NPC ID to trust when the signal's NPC ID disagrees with `LastNpcInteracted`.

**Testability:** Tests construct snapshots via `with { SpdEntryDetected = new SpdEntryDetectedSignal(123) }`.

### SPDI2: Signal survives ResetDeltas, cleared by OnSpdEntryConsumed

**Decision:** `_spdEntryDetected` survives `SnapshotAggregator.ResetDeltas()`. It is cleared only by `OnSpdEntryConsumed()`, called from `AuthoringHost.ConsumeAllSignals()` and `UIObserver.ResetWindowState()`.

**Rationale:** SPD entry is a player-initiated atomic event (like ActionCompleted, EmoteCompleted). The author enters the SPD, waits for the loading screen, arrives inside, then clicks Record. If `ResetDeltas` cleared the signal (as it does for per-window flags like `RequestAddonSeen`), opening the Record modal would destroy the evidence that an SPD was entered. This matches the survive-reset pattern used by all other action-type signals.

**What breaks if violated:** If cleared in `ResetDeltas`, the author would need to click Record before the loading screen completes (impossible) or the signal would be lost.

**Concrete surface area:**
```csharp
// SnapshotAggregator.cs
private SpdEntryDetectedSignal? _spdEntryDetected;

public void OnSpdEntryDetected(ushort cfcId)
    => _spdEntryDetected = new SpdEntryDetectedSignal(cfcId);

public void OnSpdEntryConsumed() => _spdEntryDetected = null;

// In Current property builder:
SpdEntryDetected = _spdEntryDetected
```

### SPDI3: UIObserver.PollContentFinderCondition -- momentary-state pattern

**Decision:** Add `PollContentFinderCondition()` as an every-frame poller in `UIObserver.OnFrameworkUpdate`. Use the momentary-state pattern (matching `PollPlayerEmote`):
- Track `_lastObservedCfcId` (ushort, initialized to 0)
- On each frame: read `IGameProbe.GetCurrentContentFinderConditionId()`
- Same value as last observation: no action
- Transition from 0 to non-zero N: fire `_aggregator?.OnSpdEntryDetected(N)` + write trace observation
- Transition from N to 0: silently reset `_lastObservedCfcId = 0` (no fire)
- Transition from N to different M: fire for M (player somehow entered a different instance without returning to 0 -- defensive)

**Alternatives rejected:**
- **Heartbeat poller (250ms):** SPD entry involves a loading screen that takes 2-5 seconds. The CFC ID is stable once set. Heartbeat timing is fine for detection reliability. HOWEVER, we use every-frame to be consistent with other action-detection pollers and to minimize the window where the signal could be missed if the author is very fast with the Record modal. The cost is negligible (one ushort read per frame).
- **Monotonic counter pattern (silent baseline):** CFC ID is not monotonic. It goes 0 -> N -> 0 -> M. The first non-zero value is meaningful (the player just entered), not a baseline to skip.

**Why every-frame, not heartbeat:** The CFC ID read is a single field dereference (`GameMain.Instance()->CurrentContentFinderConditionId`). No allocation, no iteration. Placing it in the every-frame section alongside `PollPlayerEmote` and `PollPlayerActionEffect` keeps the detection latency minimal and the code pattern consistent.

**Concrete surface area:**
```csharp
// UIObserver.cs -- tracking field
private ushort _lastObservedCfcId;

// UIObserver.cs -- reset
// In ResetWindowState():
_lastObservedCfcId = 0;
_aggregator?.OnSpdEntryConsumed();

// IGameProbe.cs -- new method
ushort? GetCurrentContentFinderConditionId();

// DalamudGameProbe.cs -- new method
public ushort? GetCurrentContentFinderConditionId()
{
    var gm = GameMain.Instance();
    return gm != null ? gm->CurrentContentFinderConditionId : null;
}
```

**What breaks if violated:** If heartbeat-only, a very fast author could open the Record modal during the 250ms window after entry before the heartbeat fires. The signal would be available on the next heartbeat but the "before" snapshot might not see it. Every-frame eliminates this timing risk.

### SPDI4: IGameProbe.GetCurrentContentFinderConditionId returns nullable ushort

**Decision:** The return type is `ushort?`. Null means "GameMain unavailable" (no character loaded). 0 means "not in any instanced content." Non-zero means the CFC row ID of the current instance.

**Rationale:** Matches the pattern of `GetPlayerEmoteId()` (returns `ushort?`, null = no LocalPlayer) and `GetCurrentClassJobId()` (returns `byte?`, null = PlayerState unavailable). The nullability distinguishes "cannot read" from "value is zero."

**Concrete surface area:**
```csharp
// IGameProbe.cs
ushort? GetCurrentContentFinderConditionId();
```

### SPDI5: InferredFrom.SpdEntryDetected enum value

**Decision:** Add `SpdEntryDetected` to the `InferredFrom` enum. This is the standard pattern for all inference signals.

**Concrete surface area:**
```csharp
// InferredFrom.cs
public enum InferredFrom
{
    // ... existing values ...
    SpdEntryDetected,
}
```

### SPDI6: StepInferenceEngine Rule 2.8 -- SpdEntryDetected

**Decision:** Add Rule 2.8 between Rule 2.7 (intra-zone aethernet) and Rule 3.5s (SayChatMessageSent). This rule fires when `after.SpdEntryDetected` is non-null and `before.SpdEntryDetected` is null.

**Priority rationale:**
- **Below Rule 1 (QuestCompleted):** A quest completing inside an SPD should produce a turn-in, not a duty step.
- **Below Rule 2 (QuestAccepted):** Accepting a quest should not be overshadowed by SPD entry.
- **Below Rule 2.2 (Combat):** Kill-correlated combat within an SPD might co-occur with the zone transition. Combat is more specific when the objective nibble bumps.
- **Below Rule 2.5 (Attune):** Attunement is a same-zone interaction signal that should not be overshadowed.
- **Below Rule 2.7 (Intra-zone aethernet):** Aethernet is a same-zone signal that should not be overshadowed.
- **Above Rule 3.5s (SayChatMessageSent) and all lower rules:** SPD entry is a high-confidence, structurally distinct event. Once the CFC ID transitions, the intent is unambiguous. The entry target/object was the trigger; the chat message or emote that might have been in the same window was incidental.
- **Above Rule 3 (QuestSequence advanced):** An SPD entry ALWAYS advances the quest sequence (the game transitions to the SPD zone, and the sequence typically bumps). Without Rule 2.8 beating Rule 3, every SPD entry would be mis-inferred as a "talk" step.

**EntryKind inference logic within Rule 2.8:**
1. If `after.ObjectInteracted` is non-null: `EntryKind = Interact`, `EntryTargetId = ObjectInteracted.InteractableId`, `EntryPosition` from ObjectInteracted coordinates.
2. Else if `after.LastNpcInteracted` (or `before.LastNpcInteracted`) is non-null AND `after.SelectYesnoConfirmed` is true: `EntryKind = Talk`, `EntryTargetId = LastNpcInteracted.Value`, `EntryPosition` from LastNpcPosition.
3. Else: `EntryKind = Proximity`, `EntryTargetId = null`, `EntryPosition` from before.Position (where the player was standing before entry).

**Why SelectYesnoConfirmed as a talk-entry guard:** Every SPD entry involves either a SelectYesno ("Enter this duty?") or a SelectString (difficulty selection). For talk-entry SPDs, the player talks to an NPC, which opens one of these prompts. `SelectYesnoConfirmed` being true in the same window as NPC interaction is strong evidence of a talk-initiated SPD. Without this guard, a stale `LastNpcInteracted` from a prior NPC conversation would incorrectly produce a talk-entry SPD when the actual entry was proximity-based.

However, there is a subtlety: if the player interacts with the NPC and the SelectString (difficulty) dialog opens instead of SelectYesno, `SelectYesnoConfirmed` would be false. The DialogueOptionSelected signal handles this case. So the actual guard is: `SelectYesnoConfirmed || DialogueOptionSelected.HasValue`.

**Confidence:** High. The CFC ID transition is unambiguous -- no false positives.

**SuggestedExpect:** null. SPD postconditions vary per quest (sequence advance, flag set, quest complete). The author MUST write the postcondition.

**Concrete surface area:**
```csharp
// StepInferenceEngine.cs -- new Rule 2.8
// Rule 2.8: SPD entry detected (CurrentContentFinderConditionId transitioned 0 -> N)
if (after.SpdEntryDetected is { } spdSignal
    && before.SpdEntryDetected is null)
{
    SpdEntryKind entryKind;
    uint? entryTargetId;
    Position3 entryPosition;

    if (after.ObjectInteracted is { } objSig)
    {
        entryKind = SpdEntryKind.Interact;
        entryTargetId = objSig.InteractableId;
        entryPosition = new Position3(objSig.X, objSig.Y, objSig.Z);
    }
    else if ((after.LastNpcInteracted ?? before.LastNpcInteracted) is { } npcId
        && (after.SelectYesnoConfirmed || after.DialogueOptionSelected.HasValue))
    {
        entryKind = SpdEntryKind.Talk;
        entryTargetId = npcId.Value;
        var npcPos = after.LastNpcPosition ?? before.LastNpcPosition;
        entryPosition = npcPos is { } np
            ? new Position3(np.X, np.Y, np.Z)
            : new Position3(before.Position.X, before.Position.Y, before.Position.Z);
    }
    else
    {
        entryKind = SpdEntryKind.Proximity;
        entryTargetId = null;
        entryPosition = new Position3(
            before.Position.X, before.Position.Y, before.Position.Z);
    }

    return new InferenceResult(
        StepType: "single-player-duty",
        SuggestedStepId: $"spd-{spdSignal.ContentFinderConditionId}",
        SuggestedExpect: null,
        Confidence: Confidence.High,
        InferredFrom: InferredFrom.SpdEntryDetected,
        Notes: $"Author MUST write the Expect predicate. " +
               $"CfcId={spdSignal.ContentFinderConditionId}, " +
               $"EntryKind={entryKind}, EntryTargetId={entryTargetId}");
}
```

### SPDI7: StepFactory "single-player-duty" arm

**Decision:** Add a `"single-player-duty"` case to `StepFactory.Build`. The factory reads `SpdEntryDetected.ContentFinderConditionId` from the after snapshot, and derives `EntryKind`, `EntryTargetId`, and `EntryPosition` using the same logic as Rule 2.8.

**Concrete surface area:**
```csharp
// StepFactory.cs -- new case
"single-player-duty" => BuildSinglePlayerDutyStep(stepId, expectValue, before, after),
```

Where `BuildSinglePlayerDutyStep` is a private helper that:
1. Reads `after?.SpdEntryDetected?.ContentFinderConditionId ?? 0`
2. Determines `EntryKind` using the same object/npc/proximity priority as Rule 2.8
3. Returns a `SinglePlayerDutyStep` with all four fields populated

### SPDI8: _lastObservedCfcId is reset in ResetWindowState, NOT persisted across windows

**Decision:** `_lastObservedCfcId` is reset to 0 in `UIObserver.ResetWindowState()`.

**Rationale:** When the author opens a new recording window (e.g., after recording the SPD step, they are now inside the instance and want to record subsequent steps), the CFC ID is still non-zero. Without resetting, the poller would not re-fire for the current CFC ID because `_lastObservedCfcId` already matches. This is correct: the CFC ID transition signal was already captured by the previous recording window. The author does not need to see it again.

However, there is a subtlety: if the author enters a SECOND SPD (unlikely but possible in chained quest sequences), the CFC ID would transition 0 -> M (a new value). The poller would detect this naturally because M != `_lastObservedCfcId` (which was reset to 0). So resetting in `ResetWindowState` is safe.

The consume call `_aggregator?.OnSpdEntryConsumed()` in `ResetWindowState` clears the aggregator's signal independently, matching the established pattern for all action signals.

### SPDI9: Zone-change interaction with Rule 2.8

**Decision:** Rule 2.8 fires based on `SpdEntryDetected` alone, regardless of whether the zone also changed. The zone change is expected (SPDs are in different zones), but it is NOT a precondition for the rule.

**Rationale:** The CFC ID transition is the definitive SPD entry signal. Adding a zone-change guard would create a timing dependency: if the aggregator captures the CFC ID transition before the zone-change event propagates (or vice versa), the rule might not fire. The CFC ID alone is sufficient and unambiguous.

**What this means for Rule 4 (zone change):** When both Rule 2.8 and Rule 4 could fire (SPD entry causes a zone change), Rule 2.8 fires first (it is higher priority). Rule 4 never sees the snapshot because Rule 2.8 already returned a result.

### SPDI10: AuthoringHost.PreviewInference diagnostic line

**Decision:** The `[QF-DIAG] PreviewInference` log line is extended with `SpdEntryDetected={after.SpdEntryDetected?.ContentFinderConditionId}`.

**Rationale:** This is the primary debugging tool for in-game smoke testing (Slice 6). Without it, the author has no way to confirm the CFC ID polling is working.

### SPDI11: Proximity entry fallback -- uses before.Position, not after.Position

**Decision:** For proximity-entry SPDs (no NPC, no object interaction), `EntryPosition` is set from `before.Position`, not `after.Position`.

**Rationale:** When entering a proximity-triggered SPD, the "after" position is inside the SPD instance (a completely different zone). The "before" position is where the player was standing in the open world when the proximity trigger fired. This is the position the engine needs to navigate to for re-entry.

### SPDI12: No interact/talk step consumed on SPD inference

**Decision:** When Rule 2.8 fires and the entry kind is "interact" or "talk," the `ObjectInteracted` and `LastNpcInteracted` signals are NOT consumed. They remain on the snapshot for potential debugging, but the inference result is `"single-player-duty"`, not `"interact-object"` or `"talk"`.

**Rationale:** Rule 2.8 beats the lower-priority rules (3.5o for ObjectInteracted, 7 for LastNpcInteracted). Consuming the signals would be unnecessary since they are never reached. The consume pattern only matters for signals that survive ResetDeltas and might bleed into the next window -- but the next window starts fresh via ResetDeltas (for ObjectInteracted) or the signals are naturally overwritten by new observations.

---

## 4. Task Breakdown

### Task 1: GameStateSnapshot + Signal Record

**File:** `QuestForge.Engine/Authoring/GameStateSnapshot.cs`

1. Add `SpdEntryDetectedSignal` sealed record:
```csharp
public sealed record SpdEntryDetectedSignal(ushort ContentFinderConditionId);
```

2. Add non-positional init-only property to `GameStateSnapshot`:
```csharp
public SpdEntryDetectedSignal? SpdEntryDetected { get; init; }
```

### Task 2: InferredFrom enum value

**File:** `QuestForge.Engine/Authoring/InferredFrom.cs`

Add `SpdEntryDetected` at the end of the enum:
```csharp
SpdEntryDetected,
```

### Task 3: SnapshotAggregator

**File:** `QuestForge.Engine/Authoring/SnapshotAggregator.cs`

1. Add field: `private SpdEntryDetectedSignal? _spdEntryDetected;`
2. Add `OnSpdEntryDetected(ushort cfcId)` method (sets the field)
3. Add `OnSpdEntryConsumed()` method (clears the field)
4. Wire `SpdEntryDetected = _spdEntryDetected` into the `Current` property builder
5. The field is NOT cleared in `ResetDeltas()` (survives window reset, per SPDI2)

### Task 4: StepInferenceEngine Rule 2.8

**File:** `QuestForge.Engine/Authoring/StepInferenceEngine.cs`

Insert Rule 2.8 after Rule 2.7 (intra-zone aethernet) and before Rule 3.5s (SayChatMessageSent). See SPDI6 for the full implementation.

### Task 5: StepFactory "single-player-duty" arm

**File:** `QuestForge.Engine/Authoring/StepFactory.cs`

Add `"single-player-duty"` case to the switch. Implement `BuildSinglePlayerDutyStep` private helper:

```csharp
private static SinglePlayerDutyStep BuildSinglePlayerDutyStep(
    string stepId,
    ExpectValue? expectValue,
    GameStateSnapshot? before,
    GameStateSnapshot? after)
{
    var cfcId = after?.SpdEntryDetected?.ContentFinderConditionId ?? 0;

    SpdEntryKind entryKind;
    uint? entryTargetId;
    Position3 entryPosition;

    if (after?.ObjectInteracted is { } objSig)
    {
        entryKind = SpdEntryKind.Interact;
        entryTargetId = objSig.InteractableId;
        entryPosition = new Position3(objSig.X, objSig.Y, objSig.Z);
    }
    else if ((after?.LastNpcInteracted ?? before?.LastNpcInteracted) is { } npcId
        && ((after?.SelectYesnoConfirmed == true) || after?.DialogueOptionSelected.HasValue == true))
    {
        entryKind = SpdEntryKind.Talk;
        entryTargetId = npcId.Value;
        var npcPos = after?.LastNpcPosition ?? before?.LastNpcPosition;
        entryPosition = npcPos is { } np
            ? new Position3(np.X, np.Y, np.Z)
            : new Position3(before?.Position.X ?? 0, before?.Position.Y ?? 0, before?.Position.Z ?? 0);
    }
    else
    {
        entryKind = SpdEntryKind.Proximity;
        entryTargetId = null;
        entryPosition = new Position3(
            before?.Position.X ?? 0, before?.Position.Y ?? 0, before?.Position.Z ?? 0);
    }

    return new SinglePlayerDutyStep
    {
        Id = stepId,
        Expect = expectValue,
        ContentFinderConditionId = cfcId,
        EntryKind = entryKind,
        EntryTargetId = entryTargetId,
        EntryPosition = entryPosition
    };
}
```

### Task 6: IGameProbe accessor

**File:** `QuestForge.Plugin.Tracing/IGameProbe.cs`

Add:
```csharp
/// <summary>
/// Returns GameMain.Instance()->CurrentContentFinderConditionId (ushort).
/// 0 = not in instanced content. Non-zero = CFC row ID of the current instance.
/// null when GameMain is unavailable.
/// </summary>
ushort? GetCurrentContentFinderConditionId();
```

### Task 7: DalamudGameProbe implementation

**File:** `QuestForge.Plugin/Tracing/DalamudGameProbe.cs`

Add:
```csharp
public ushort? GetCurrentContentFinderConditionId()
{
    var gm = GameMain.Instance();
    return gm != null ? gm->CurrentContentFinderConditionId : null;
}
```

### Task 8: UIObserver PollContentFinderCondition

**File:** `QuestForge.Plugin.Tracing/UIObserver.cs`

1. Add tracking field: `private ushort _lastObservedCfcId;`

2. Add `PollContentFinderCondition()` method:
```csharp
private void PollContentFinderCondition()
{
    if (_gameProbe is null) return;

    var current = _gameProbe.GetCurrentContentFinderConditionId();
    if (current is null) return;
    var cfcId = current.Value;

    if (cfcId == _lastObservedCfcId) return;

    var previousCfcId = _lastObservedCfcId;
    _lastObservedCfcId = cfcId;

    // Only fire on 0 -> non-zero transition (entry). Non-zero -> 0 (exit) is silent.
    if (cfcId == 0) return;

    var now   = _clock.UtcNow;
    var runId = CurrentRunId;
    WriteObservation("ContentFinderConditionChanged", cfcId, previousCfcId, runId, now);
    _aggregator?.OnSpdEntryDetected(cfcId);
}
```

3. Wire into `OnFrameworkUpdate()` in the every-frame section (after `PollInventoryEventAddon`, before the heartbeat gate):
```csharp
PollContentFinderCondition();
```

4. Add to `ResetWindowState()`:
```csharp
_lastObservedCfcId = 0;
_aggregator?.OnSpdEntryConsumed();
```

### Task 9: AuthoringHost changes

**File:** `QuestForge.Plugin/Authoring/AuthoringHost.cs`

1. Add `_aggregator.OnSpdEntryConsumed()` to `ConsumeAllSignals()`.

2. Extend the `[QF-DIAG] PreviewInference` log line with:
```
SpdEntryDetected={after.SpdEntryDetected?.ContentFinderConditionId}
```

### Task 10: FakeGameProbe extension

**File:** `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (FakeGameProbe class)

Add:
```csharp
// -- UO_CFC*: ContentFinderCondition scripting --
private ushort? _currentCfcId = 0;

public void SetCurrentContentFinderConditionId(ushort cfcId) => _currentCfcId = cfcId;
public void ClearCurrentContentFinderConditionId() => _currentCfcId = null;

public ushort? GetCurrentContentFinderConditionId() => _currentCfcId;
```

---

## 5. Given-When-Then Test Specifications

### 5.1 Inference engine tests (`SinglePlayerDutyInferenceTests.cs`)

**SPDI-I1: Talk-entry SPD with NPC + SelectYesno**
- **Given:** before snapshot has `Zone=140, Position=(10,0,20), SpdEntryDetected=null`.
  After snapshot has `Zone=500, SpdEntryDetected=new(123), LastNpcInteracted=NpcId(1000100), LastNpcPosition=(15,0,25), SelectYesnoConfirmed=true`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="single-player-duty"`, `InferredFrom=SpdEntryDetected`, `Confidence=High`, `SuggestedExpect=null`, `SuggestedStepId="spd-123"`. Notes contain "CfcId=123" and "EntryKind=Talk" and "EntryTargetId=1000100".

**SPDI-I2: Interact-entry SPD with EventObj**
- **Given:** before snapshot has `Zone=140, Position=(10,0,20), SpdEntryDetected=null`.
  After snapshot has `Zone=500, SpdEntryDetected=new(456), ObjectInteracted=new(2001234, 81.5f, 7f, 32.2f)`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="single-player-duty"`, `InferredFrom=SpdEntryDetected`, `Confidence=High`. Notes contain "EntryKind=Interact" and "EntryTargetId=2001234".

**SPDI-I3: Proximity-entry SPD (no NPC, no object, no dialog)**
- **Given:** before snapshot has `Zone=140, Position=(50,0,60), SpdEntryDetected=null`.
  After snapshot has `Zone=500, SpdEntryDetected=new(789), LastNpcInteracted=null, ObjectInteracted=null, SelectYesnoConfirmed=false, DialogueOptionSelected=null`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="single-player-duty"`, `InferredFrom=SpdEntryDetected`, `Confidence=High`. Notes contain "EntryKind=Proximity" and "EntryTargetId=".

**SPDI-I4: SpdEntryDetected on both before and after -- no fire (already inside)**
- **Given:** before snapshot has `SpdEntryDetected=new(123)`. After snapshot has `SpdEntryDetected=new(123)`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result does NOT have `StepType="single-player-duty"`. Falls through to lower rules (zone change, sequence change, etc.).

**SPDI-I5: Rule priority -- SpdEntryDetected beats QuestSequence advance (Rule 3)**
- **Given:** before has `QuestSequence=1, SpdEntryDetected=null`. After has `QuestSequence=2, SpdEntryDetected=new(100), LastNpcInteracted=NpcId(1000100), SelectYesnoConfirmed=true`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="single-player-duty"`, NOT `StepType="talk"`.

**SPDI-I6: Rule priority -- QuestCompleted (Rule 1) beats SpdEntryDetected**
- **Given:** before has `QuestCompleted=false, SpdEntryDetected=null`. After has `QuestCompleted=true, SpdEntryDetected=new(100)`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="turn-in"`, NOT `StepType="single-player-duty"`.

**SPDI-I7: Rule priority -- QuestAccepted (Rule 2) beats SpdEntryDetected**
- **Given:** before has `QuestAccepted=false, SpdEntryDetected=null`. After has `QuestAccepted=true, SpdEntryDetected=new(100)`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="accept"`, NOT `StepType="single-player-duty"`.

**SPDI-I8: Talk-entry with SelectString (difficulty dialog) instead of SelectYesno**
- **Given:** before has `SpdEntryDetected=null`. After has `SpdEntryDetected=new(123), LastNpcInteracted=NpcId(1000100), LastNpcPosition=(15,0,25), DialogueOptionSelected=1, SelectYesnoConfirmed=false`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="single-player-duty"`, Notes contain "EntryKind=Talk".

**SPDI-I9: NPC present but no dialog confirmation -- falls back to proximity**
- **Given:** before has `LastNpcInteracted=NpcId(1000100), SpdEntryDetected=null`. After has `SpdEntryDetected=new(123), LastNpcInteracted=NpcId(1000100), SelectYesnoConfirmed=false, DialogueOptionSelected=null, ObjectInteracted=null`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="single-player-duty"`, Notes contain "EntryKind=Proximity". The stale NPC is not used without dialog confirmation.

**SPDI-I10: ObjectInteracted beats NPC + dialog (interact-entry priority)**
- **Given:** before has `SpdEntryDetected=null`. After has `SpdEntryDetected=new(123), ObjectInteracted=new(2001234, 81.5f, 7f, 32.2f), LastNpcInteracted=NpcId(1000100), SelectYesnoConfirmed=true`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has Notes containing "EntryKind=Interact" (ObjectInteracted wins over NPC+dialog).

**SPDI-I11: NPC from before snapshot used when after.LastNpcInteracted is null**
- **Given:** before has `LastNpcInteracted=NpcId(1000200), LastNpcPosition=(20,0,30), SpdEntryDetected=null`. After has `SpdEntryDetected=new(123), LastNpcInteracted=null, SelectYesnoConfirmed=true`.
- **When:** `Infer(before, after)` is called.
- **Then:** Result has Notes containing "EntryKind=Talk" and "EntryTargetId=1000200".

**SPDI-I12: Rule priority -- Combat (Rule 2.2) beats SpdEntryDetected when nibble bumps present**
- **Given:** before has `SpdEntryDetected=null`. After has `SpdEntryDetected=new(100), KillCorrelatedTargets` with one entry (dataId=999, FinalValue=2).
- **When:** `Infer(before, after)` is called.
- **Then:** Result has `StepType="combat"`, NOT `StepType="single-player-duty"`.

### 5.2 StepFactory tests (`SinglePlayerDutyInferenceTests.cs`, factory section)

**SPDI-F1: Factory produces SinglePlayerDutyStep with talk entry**
- **Given:** `stepType="single-player-duty"`, after snapshot has `SpdEntryDetected=new(123), LastNpcInteracted=NpcId(1000100), LastNpcPosition=(15,0,25), SelectYesnoConfirmed=true`.
- **When:** `StepFactory.Build(...)` is called.
- **Then:** Result is `SinglePlayerDutyStep` with `ContentFinderConditionId=123`, `EntryKind=Talk`, `EntryTargetId=1000100`, `EntryPosition=(15,0,25)`.

**SPDI-F2: Factory produces SinglePlayerDutyStep with interact entry**
- **Given:** `stepType="single-player-duty"`, after snapshot has `SpdEntryDetected=new(456), ObjectInteracted=new(2001234, 81.5f, 7f, 32.2f)`.
- **When:** `StepFactory.Build(...)` is called.
- **Then:** Result is `SinglePlayerDutyStep` with `ContentFinderConditionId=456`, `EntryKind=Interact`, `EntryTargetId=2001234`, `EntryPosition=(81.5, 7, 32.2)`.

**SPDI-F3: Factory produces SinglePlayerDutyStep with proximity entry**
- **Given:** `stepType="single-player-duty"`, before snapshot has `Position=(50,0,60)`, after snapshot has `SpdEntryDetected=new(789), ObjectInteracted=null, LastNpcInteracted=null`.
- **When:** `StepFactory.Build(...)` is called.
- **Then:** Result is `SinglePlayerDutyStep` with `ContentFinderConditionId=789`, `EntryKind=Proximity`, `EntryTargetId=null`, `EntryPosition=(50,0,60)`.

**SPDI-F4: Factory with null SpdEntryDetected defaults CfcId to 0**
- **Given:** `stepType="single-player-duty"`, after snapshot has `SpdEntryDetected=null`.
- **When:** `StepFactory.Build(...)` is called.
- **Then:** Result is `SinglePlayerDutyStep` with `ContentFinderConditionId=0` (draft validator will catch this as E30).

**SPDI-F5: Factory interact-entry beats talk-entry when both signals present**
- **Given:** `stepType="single-player-duty"`, after has `SpdEntryDetected=new(123), ObjectInteracted=new(2001234, 10f, 0f, 20f), LastNpcInteracted=NpcId(1000100), SelectYesnoConfirmed=true`.
- **When:** `StepFactory.Build(...)` is called.
- **Then:** Result is `SinglePlayerDutyStep` with `EntryKind=Interact`.

**SPDI-F6: Factory talk-entry uses before.LastNpcPosition when after is null**
- **Given:** `stepType="single-player-duty"`, before has `LastNpcInteracted=NpcId(1000200), LastNpcPosition=(20,0,30)`, after has `SpdEntryDetected=new(123), LastNpcInteracted=null, LastNpcPosition=null, SelectYesnoConfirmed=true`.
- **When:** `StepFactory.Build(...)` is called.
- **Then:** Result is `SinglePlayerDutyStep` with `EntryKind=Talk`, `EntryTargetId=1000200`, `EntryPosition=(20,0,30)`.

### 5.3 UIObserver polling tests (`UIObserverTests.cs`, UO_CFC section)

**UO_CFC1: CFC ID 0 -> N fires OnSpdEntryDetected**
- **Given:** A UIObserver with aggregator set. `FakeGameProbe.SetCurrentContentFinderConditionId(0)`. Tick once to establish baseline.
- **When:** `FakeGameProbe.SetCurrentContentFinderConditionId(123)`. Tick once.
- **Then:** `aggregator.Current.SpdEntryDetected` is `new SpdEntryDetectedSignal(123)`. Trace contains observation "ContentFinderConditionChanged" with value 123.

**UO_CFC2: CFC ID stays at N -- no re-fire**
- **Given:** CFC ID already at 123 (after UO_CFC1 scenario). Aggregator consumed the signal (`OnSpdEntryConsumed`).
- **When:** Tick again (CFC ID still 123).
- **Then:** `aggregator.Current.SpdEntryDetected` remains null (was consumed). No new trace observation.

**UO_CFC3: CFC ID N -> 0 -- silent reset, no fire**
- **Given:** CFC ID at 123 (from UO_CFC1).
- **When:** `FakeGameProbe.SetCurrentContentFinderConditionId(0)`. Tick once.
- **Then:** No new `SpdEntryDetected` signal fires. Trace does NOT contain a new "ContentFinderConditionChanged" observation for value 0.

**UO_CFC4: CFC ID 0 -> N -> 0 -> M -- second entry fires**
- **Given:** CFC ID transitions 0 -> 123 (first entry, consumed). Then 123 -> 0 (exit). Then 0 -> 456 (second entry).
- **When:** Full sequence of SetCurrentContentFinderConditionId + Tick calls.
- **Then:** After the final tick, `aggregator.Current.SpdEntryDetected` is `new SpdEntryDetectedSignal(456)`.

**UO_CFC5: ResetWindowState clears _lastObservedCfcId and consumes signal**
- **Given:** CFC ID at 123 (signal fired). Aggregator has `SpdEntryDetected=new(123)`.
- **When:** `observer.ResetWindowState()` is called.
- **Then:** `aggregator.Current.SpdEntryDetected` is null. Internal `_lastObservedCfcId` is 0 (verified indirectly: setting CFC ID back to 123 and ticking fires a new signal).

**UO_CFC6: null GameProbe -- poller is a no-op**
- **Given:** UIObserver constructed with `gameProbe: null`.
- **When:** Tick once.
- **Then:** No exceptions. No trace observations for "ContentFinderConditionChanged".

---

## 6. Implementation Order

### Phase A: Engine-side pure C# (no Dalamud dependency)
**Duration estimate:** 1-2 hours
**Deliverables:**
1. `SpdEntryDetectedSignal` record on `GameStateSnapshot` (Task 1)
2. `InferredFrom.SpdEntryDetected` enum value (Task 2)
3. `SnapshotAggregator.OnSpdEntryDetected` / `OnSpdEntryConsumed` (Task 3)
4. `StepInferenceEngine` Rule 2.8 (Task 4)
5. `StepFactory` `"single-player-duty"` arm (Task 5)

**Done before Phase B:** All 18 inference + factory tests (SPDI-I1 through SPDI-I12, SPDI-F1 through SPDI-F6) pass.

### Phase B: Plugin-side polling (Dalamud-dependent)
**Duration estimate:** 1-2 hours
**Deliverables:**
1. `IGameProbe.GetCurrentContentFinderConditionId()` (Task 6)
2. `DalamudGameProbe` implementation (Task 7)
3. `UIObserver.PollContentFinderCondition` + wiring + reset (Task 8)
4. `FakeGameProbe` extension (Task 10)

**Done before Phase C:** All 6 UIObserver tests (UO_CFC1 through UO_CFC6) pass.

### Phase C: AuthoringHost wiring
**Duration estimate:** 30 minutes
**Deliverables:**
1. `ConsumeAllSignals` calls `OnSpdEntryConsumed` (Task 9)
2. `PreviewInference` diag line extended (Task 9)

**Done before Slice 6:** All existing authoring tests continue to pass. The diag line includes CFC ID information.

---

## 7. Done Criteria

1. `dotnet test QuestForge.Engine.Tests --filter "SinglePlayerDutyInferenceTests"` passes with 18 tests (12 inference + 6 factory).
2. `dotnet test QuestForge.Plugin.Tests --filter "UO_CFC"` passes with 6 tests.
3. All existing tests continue to pass unchanged (no regressions from Rule 2.8 insertion).
4. `StepInferenceEngine.Infer()` returns `StepType="single-player-duty"` when `SpdEntryDetected` transitions from null to non-null, with correct `EntryKind` discrimination.
5. `StepFactory.Build("single-player-duty", ...)` returns a `SinglePlayerDutyStep` with all four fields populated from snapshot signals.
6. `UIObserver` detects CFC ID transitions (0 -> N) and propagates them to the aggregator without double-firing.
7. `AuthoringHost.PreviewInference` log line shows `SpdEntryDetected=N` when inside an SPD instance.
8. `AuthoringHost.ConsumeAllSignals` clears the SPD entry signal after recording.

---

## 8. Exclusions

1. **DungeonTrialStep inference:** This spec covers only SinglePlayerDutyStep. DungeonTrialStep inference (detecting Duty Finder / Duty Support entry) is a separate future slice. The CFC ID polling infrastructure added here will be reused.
2. **SPD difficulty selection inference:** The authored step does not capture which difficulty was chosen. The engine handles difficulty at runtime via `PreferredDutyDifficulty` and `DutyFailurePolicy`. Authoring does not need to infer difficulty.
3. **SPD exit detection:** Detecting when the player exits an SPD (CFC ID N -> 0) is not part of authoring inference. The engine handles exit via InstanceKind polling in the run loop.
4. **Multi-SPD chaining in a single recording window:** If the author enters two SPDs in one recording window (extremely unlikely), only the last CFC ID is captured. This is acceptable for v1.
5. **qf-trace SnapshotState extension:** `SinglePlayerDutyStep` is single-tick (CFC ID transition -> infer). No multi-stage observation semantics. `SnapshotState.cs` does not need extension (per CLAUDE.md "When extract-quest needs per-step work" guidance).

---

## Ready for Test Creation

Tester: Write failing tests from the GWT specs in Tasks 4, 5, 8.
- Happy paths: 8 scenarios (SPDI-I1, I2, I3, I5, I8, F1, F2, F3, UO_CFC1, UO_CFC4)
- Edge cases: 8 scenarios (SPDI-I4, I9, I10, I11, F4, F5, F6, UO_CFC2, UO_CFC3, UO_CFC5)
- Error cases: 4 scenarios (SPDI-I6, I7, I12, UO_CFC6)
- Expected total: ~24 tests in QuestForge.Engine.Tests + QuestForge.Plugin.Tests
