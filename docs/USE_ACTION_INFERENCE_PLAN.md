# UseActionStep Authoring Inference Plan

**Status:** ready for test creation (with one open question — see §F O1)
**Input docs:**
- `docs/TELEPORT_INFERENCE_PLAN.md` (closest analog spec format)
- `docs/USE_ACTION_STEP_PLAN.md` (engine-side spec; pinned: `Schema.ActionType` enum values, `UseActionStep { ActionType, ActionId, TargetNpcId? }` shape)
- `docs/USE_ACTION_DALAMUD_PLAN.md` (`ToFFXIVActionType` mapping; DA-3 `KeyItem→EventItem` rename)
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs` (existing rule cascade — Rules 1..9 plus 4.0/2.5/2.7)
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` (`TeleportCompleted: AetheryteId?` analog at line 127)
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` (`OnTeleportCompleted` / `OnTeleportConsumed` pattern at lines 488-494)
- `QuestForge.Engine/Authoring/InferredFrom.cs` (the enum — add one value)
- `QuestForge.Engine/Authoring/StepFactory.cs` (`"teleport"` arm at line 125; `"use-action"` arm is NEW)
- `QuestForge.Plugin.Tracing/UIObserver.cs` (`PollTeleportAddonOpen` analog at line 588; new `PollPlayerActionEffect` mirrors `PollAethernetDestination`)
- `QuestForge.Plugin/Authoring/AuthoringHost.cs` (`OnTerritoryChanged` line 266; `RecordStep` consume calls at lines 257-259; `[QF-DIAG] PreviewInference:` line 187)
- `QuestForge.Plugin.Tracing/IGameProbe.cs` (interface gets a new method)
- `QuestForge.Plugin/Tracing/DalamudGameProbe.cs` (implementation gets the cast-info read; pattern mirrors `GetPlayerPosition`)
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\Character\CastInfo.cs` (canonical struct: `ResponseGlobalSequence` at 0x4C, `ResponseActionId` at 0x48, `ResponseActionType` at 0x44 — a `FFXIVClientStructs.FFXIV.Client.Game.ActionType` byte)
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\Character\BattleChara.cs` (CastInfo at offset 0x2790)
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\ActionManager.cs:395-417` (canonical `ActionType` enum — verified members)
- `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` (`ToFFXIVActionType`; we need the reverse `FromFFXIVActionType`)
- `QuestForge.Schema/SharedValueTypes.cs` (Schema `ActionType` enum — three members: Action, GeneralAction, KeyItem)

**Output (CI behavior):** When the player presses a game action during an Author-mode recording session, the snapshot field `ActionCompleted` becomes non-null with the `Schema.ActionType`, `ActionId`, and optional `TargetBaseId`. `StepInferenceEngine.Infer` returns a `use-action`-typed `InferenceResult` with `SuggestedExpect = null` (author MUST write the postcondition per `USE_ACTION_STEP_PLAN.md` Decision UA4), `Confidence.High`, `InferredFrom.ActionCompleted`. Confirming the Record-Step modal records a `UseActionStep { ActionType, ActionId, TargetNpcId? }` into the draft. CI red → CI green when (a) the snapshot field, (b) the aggregator setter/consumer, (c) the new pure reverse-mapping helper, (d) the new inference rule, (e) the StepFactory `"use-action"` arm, (f) the UIObserver poller, and (g) the `RecordStep` clearing are wired up.

This plan covers **engine-side (xUnit-testable)** wiring and **UIObserver-side polling**. The CastInfo read in `DalamudGameProbe` is a thin Dalamud-bound shell (no test); the polling state machine, the reverse mapping, and the inference rule are all testable without Dalamud.

---

## Dependency graph

```
QuestForge.Adapters.Actions
  └── ActionTypeMapper (NEW: pure helper — uint→Schema.ActionType?)
        ↓ used by ↓
QuestForge.Engine.Authoring
  ├── GameStateSnapshot           (add ActionCompleted property + ActionCompletedSignal record)
  ├── InferredFrom (enum)         (add ActionCompleted)
  ├── SnapshotAggregator          (add OnActionCompleted / OnActionConsumed)
  └── StepInferenceEngine         (add Rule 3.5 — ActionCompleted, fires AFTER Rule 3 quest-sequence, BEFORE Rule 4.0 teleport)
        ↓
QuestForge.Engine.Authoring.StepFactory  (route stepType="use-action" → UseActionStep)
        ↓
QuestForge.Plugin.Authoring.AuthoringHost.RecordStep  (clear ActionCompleted after consume)
        ↓
QuestForge.Plugin.Tracing.IGameProbe                   (add GetLastActionEffect())
        ↓
QuestForge.Plugin.Tracing.UIObserver.PollPlayerActionEffect   (new poller; tracks last-seen ResponseGlobalSequence)
        ↓
QuestForge.Plugin.Tracing.DalamudGameProbe.GetLastActionEffect   (Dalamud-bound: reads LocalPlayer.CastInfo.Response*)
```

**Build order:**
1. Pure reverse-mapper in `QuestForge.Adapters/Actions/ActionTypeMapper.cs` (testable in `QuestForge.Adapters.Tests`).
2. `ActionCompletedSignal` record + `GameStateSnapshot.ActionCompleted` property + `InferredFrom.ActionCompleted` enum value — engine surface, no Dalamud.
3. `SnapshotAggregator.OnActionCompleted` / `OnActionConsumed` — engine surface.
4. `StepInferenceEngine` Rule 3.5 — engine surface.
5. `StepFactory.Build` `"use-action"` arm — engine surface.
6. `AuthoringHost.RecordStep` consume call — one-line edit.
7. `IGameProbe.GetLastActionEffect()` + `FakeGameProbe` extension — interface + fake.
8. `UIObserver.PollPlayerActionEffect` — testable in `QuestForge.Plugin.Tests` against `FakeGameProbe` + `FakeTargetProbe`.
9. `DalamudGameProbe.GetLastActionEffect` — concrete Dalamud impl, smoke-tested in-game.

Steps 1-7 are pure xUnit. Step 8 is `QuestForge.Plugin.Tests` against fakes. Step 9 is manual smoke.

---

## Architectural decisions (read before coding)

### Decision UAI1 — Snapshot field is `ActionCompletedSignal?`, a richer record than the `AetheryteId?` precedent

`TeleportCompleted: AetheryteId?` is a single-value snapshot because the destination zone is recoverable from `AetheryteZoneMap` (one extra lookup). `ActionCompleted` carries THREE values (`ActionType`, `ActionId`, `TargetBaseId?`) that are NOT recoverable from any other field on the snapshot — there is no global `ActionTypeMap` or `LastActionTarget` we could fall back to. Inlining them as separate `Action*` fields on the snapshot would force every aggregator method that touches them to either pass three uints around or define a (Type, Id, Target) tuple anyway, so the record is the simpler shape.

**Concrete shape:**

```csharp
// QuestForge.Engine/Authoring/GameStateSnapshot.cs — appended near TeleportCompleted
//
// Records that the player used a game action (combat ability, general action, or quest key item)
// during this recording window. Set by SnapshotAggregator.OnActionCompleted, which is driven by
// UIObserver.PollPlayerActionEffect reading LocalPlayer.CastInfo.Response* fields. Cleared by
// OnActionConsumed (called from RecordStep) so it does not bleed into the next window.
//
// TargetBaseId is the BNpcBase/ENpcBase row id of the action's target (null = self-cast / no target).
// ActionType is the SCHEMA-side enum, NOT FFXIVClientStructs (testability boundary).
public sealed record ActionCompletedSignal(
    QuestForge.Schema.ActionType ActionType,
    uint ActionId,
    uint? TargetBaseId);

public ActionCompletedSignal? ActionCompleted { get; init; }
```

Rejected alternatives:
- **Three separate snapshot fields** (`ActionCompletedType`, `ActionCompletedId`, `ActionCompletedTargetBaseId`). Forces every consumer to read all three and disambiguate "are these from the same event?" via null-checking each field. The record gives an atomic presence check.
- **Reuse `ActionStatus`** (the union at `QuestForge.Adapters/Actions/ActionStatus.cs`). That union answers "can we use this action right now" (Ready/OnCooldown/Unusable) — a different question. Co-opting it for "was this action just used" would conflate two semantically distinct shapes.

**Why `TargetBaseId: uint?` and not `NpcId?` (the typed wrapper):** the schema-side `UseActionStep.TargetNpcId` is `uint?` (per `USE_ACTION_STEP_PLAN.md` Decision UA3 — "kept as `uint?` to match the existing `NpcLocation.NpcId: uint` convention"). Matching that here means `StepFactory.Build` can pass the snapshot value through with no conversion.

**What breaks if violated:** if `ActionCompleted` is split into three separate fields, the priority check in Rule 3.5 has to read all three (or one chosen sentinel) to detect presence, and the aggregator's `OnActionConsumed` has to null all three. Atomic field is strictly simpler.

### Decision UAI2 — Pure reverse-mapper `ActionTypeMapper.FromFFXIVActionType(uint) → Schema.ActionType?`

The reverse of `ActionExecutorLogic.ToFFXIVActionType` (in `QuestForge.Adapters.Dalamud/Actions/`). Three rules:

1. **Lives in `QuestForge.Adapters/Actions/ActionTypeMapper.cs`**, NOT in `QuestForge.Adapters.Dalamud`. This is the testability split (analog of `ActionStatusInterpreter` in `USE_ACTION_DALAMUD_PLAN.md` Decision DAD-3). The forward mapper has to live in `QuestForge.Adapters.Dalamud` because it returns the FFXIVClientStructs enum; the reverse mapper takes a `uint` (the wire byte, which is what `CastInfo.ResponseActionType` holds — see below) and returns `Schema.ActionType?`, neither of which requires FFXIVClientStructs.
2. **Input is `uint`** (the canonical wire byte), NOT `FFXIVClientStructs.FFXIV.Client.Game.ActionType`. Caller (UIObserver via IGameProbe) reads the byte from `CastInfo.ResponseActionType` and forwards as `uint`. This keeps the engine-side helper free of FFXIVClientStructs.
3. **Returns `Schema.ActionType?`** — null for any unsupported game-side type (Item, EventAction, BuddyAction, Mount, etc.). The UIObserver poller treats null as "skip observation."

**Concrete shape:**

```csharp
// QuestForge.Adapters/Actions/ActionTypeMapper.cs
namespace QuestForge.Adapters.Actions;

using QuestForge.Schema;

/// <summary>
/// Pure reverse-mapping helper: converts the FFXIVClientStructs ActionType byte (as a uint,
/// to keep this assembly Dalamud-free) into the schema-side ActionType enum the snapshot,
/// inference engine, and serializer use.
///
/// CANONICAL VALUES (verified against FFXIVClientStructs ActionManager.cs:395-417):
///   1 = Action         → Schema.ActionType.Action          (combat abilities, weaponskills, spells)
///   3 = EventItem      → Schema.ActionType.KeyItem         (quest key items used as actions)
///   5 = GeneralAction  → Schema.ActionType.GeneralAction   (mount, sprint, teleport, return)
///
/// All other values (0=None, 2=Item, 4=EventAction, 6=BuddyAction, 7=MainCommand, 8=Companion,
/// 9=CraftAction, 10=Unk10, 11=PetAction, 12=Unk12, 13=Mount, 14=PvPAction, 15=FieldMarker,
/// 16=ChocoboRaceAbility, 17=ChocoboRaceItem, 18=Unk18, 19=BgcArmyAction, 20=Ornament)
/// return null — they are NOT authorable as use-action steps in v1 and the poller skips them.
///
/// Inverse of ActionExecutorLogic.ToFFXIVActionType (in QuestForge.Adapters.Dalamud).
/// If Schema.ActionType gains a new member, BOTH mappers must be updated.
/// </summary>
public static class ActionTypeMapper
{
    public static ActionType? FromFFXIVActionType(uint ffxivActionType) => ffxivActionType switch
    {
        1u => ActionType.Action,
        3u => ActionType.KeyItem,        // FFXIVClientStructs name is "EventItem" (Decision DAD-4)
        5u => ActionType.GeneralAction,
        _  => null,                       // unsupported: poller skips observation
    };
}
```

Rejected alternatives:
- **Map all values, throw on unknown.** Would crash the poller on any auto-attack or arbitrary game-side action. The poller must be defensive — actions fire all the time in normal play.
- **Map all values, return a sentinel `Schema.ActionType.Unknown`.** Pollutes the schema enum with a value that never serializes correctly and would also force StepFactory / inference to special-case it. Returning null is the standard "skip this observation" surface used elsewhere (e.g. `IAddonProbe.GetTelepotTownDestinationId` returns `uint?`).

**What breaks if violated:** if the mapper throws instead of returning null, the UIObserver poller crashes the framework thread when the player uses an auto-attack or pet command. If it returns a sentinel, the engine inference rule fires with a value StepFactory cannot route, producing a default TalkStep.

### Decision UAI3 — Aggregator setter/consumer mirror the teleport pair exactly

```csharp
// QuestForge.Engine/Authoring/SnapshotAggregator.cs
private ActionCompletedSignal? _actionCompleted;

// In the Current property's object-initializer block (alongside TeleportCompleted):
ActionCompleted = _actionCompleted,

/// <summary>
/// Called by UIObserver.PollPlayerActionEffect when a new ResponseGlobalSequence is observed
/// (the player just used an action this frame). Records the action's type, id, and optional
/// target (from TargetManager.Target at the time of the read). Survives ResetDeltas; cleared by
/// OnActionConsumed (called from AuthoringHost.RecordStep).
///
/// Does NOT update LastNpcInteracted, LastAttuned, or any other unrelated field — the action's
/// target is captured into the signal record, not bled into the general NPC interaction state.
/// </summary>
public void OnActionCompleted(
    QuestForge.Schema.ActionType actionType,
    uint actionId,
    uint? targetBaseId)
    => _actionCompleted = new ActionCompletedSignal(actionType, actionId, targetBaseId);

/// <summary>
/// Called at the end of RecordStep (and from UIObserver.ResetWindowState for symmetry) to
/// consume the action-completed signal so it does not bleed into the next recording window.
/// Mirrors OnTeleportConsumed exactly.
/// </summary>
public void OnActionConsumed() => _actionCompleted = null;
```

**Why no side effects on other state:** unlike `OnAethernetTeleportCompleted` (which sets `LastAethernetShardInteracted` so Rule 2.5 can see the destination shard), action-use has no analog — the target NPC of an action is NOT the same as "the player interacted with that NPC for dialogue/quest purposes." Setting `LastNpcInteracted` from `OnActionCompleted` would cause spurious Rule 7 (NpcInteracted-changed) talk-step inference on subsequent windows.

**What breaks if violated:** if `OnActionCompleted` sets `_lastNpcInteracted = new NpcId(targetBaseId.Value)`, the next recording window's Rule 7 sees the action target as a new interacted NPC and emits a wrong `talk` step.

### Decision UAI4 — New inference rule "Rule 3.5 — ActionCompleted" fires AFTER Rule 3 (QuestSequence advanced), BEFORE Rule 4.0 (TeleportCompleted)

**Priority placement is load-bearing.** The cascade in `StepInferenceEngine.Infer` currently runs:
- 1: QuestCompleted → 2: QuestAccepted → 2.1: ForeignQuestAccepted → 2.2: Combat → 2.2b: Purchase → 2.3: KeyItemsAdded → 2.4: KeyItemsRemoved → 2.5: Attune → 2.6: InventoryHash → **3: QuestSequence advanced** → **4.0: TeleportCompleted** → 4: Zone changed → 2.7: AethernetTeleportCompleted (same zone) → 5: QuestFlags → 6: DialogueAnswer → 7: NpcInteracted → 8: Movement → 9: Empty.

Insertion site: **between Rule 3 (QuestSequence) and Rule 4.0 (TeleportCompleted)**, labelled "Rule 3.5".

**Why AFTER Rule 3 (QuestSequence advance wins over ActionCompleted):** Per UAI4 question in §I — if the player uses an action AND the quest sequence advances in the same window (e.g. the action satisfies an objective), the **quest-sequence change is more authoritative** as a step intent: Rule 3 produces a `talk` step (because sequence advances usually follow NPC interaction) with the *real* postcondition (`questSequence(qid) >= N`), which is what the author actually wants to encode. The UseAction is implementation detail; the author can manually re-shape the suggested step if they prefer a `use-action` skeleton.

Actually — re-examining: if the action IS the quest objective (e.g. "use Heavy Swing on these rocks"), Rule 3 produces a `talk` step which is wrong. **Reverting this decision after consideration:** sequence-advance after a use-action SHOULD produce a use-action step (not talk), because the author's intent is "this is the action I used." Rule 3.5 should fire BEFORE Rule 3.

**Final ordering decision: Rule 3.5 fires BEFORE Rule 3 (immediately after Rule 2.6 InventoryHash).** Reasons:

1. **Rule 3 (QuestSequence advanced) defaults to `talk` step type.** That is wrong for a use-action quest objective. The author would have to delete the talk inference and re-author manually.
2. **Pickup-item / Combat (Rules 2.3, 2.2) already win over Rule 3** for the same reason — they're more specific than the catch-all sequence-advance.
3. **ActionCompleted is just as specific:** it carries a definitive "the player did THIS action with THIS id on THIS target." That is strictly more specific than "the quest sequence advanced" (which gives no information about HOW).
4. **Use-action steps frequently advance quest sequence** as their natural postcondition — that's how the engine knows the step succeeded. Treating sequence-advance as the dominant signal would mean use-action inference never fires for the most common use case.

**Final placement: between Rule 2.6 (InventoryHash) and Rule 3 (QuestSequence advanced).** Concrete location: insert immediately above the `// Rule 3: QuestSequence advanced` comment at line 264 of the current `StepInferenceEngine.cs`.

```csharp
// Rule 3.5 — ActionCompleted
// Fires when UIObserver.PollPlayerActionEffect detected that the player used a supported
// game action (Action / GeneralAction / KeyItem) during this recording window.
//
// PRIORITY: above Rule 3 (QuestSequence advanced). The author's intent when using an action
// during a quest is almost always "this is the action step" — not "this is a generic talk
// that happened to advance the sequence." A use-action that advances the sequence is the
// MOST common case (the engine uses the sequence change as the natural postcondition);
// firing Rule 3 first would draft a `talk` step that the author would have to delete.
//
// PRIORITY: below Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6 because those represent
// distinct authoring intents (turn-in, accept, combat, purchase, key-item exchange, attunement,
// inventory-diff exchange) where the action use is incidental and not the primary step.
//
// PRIORITY: above Rule 4.0 (TeleportCompleted) because some "teleport" general actions (id 5)
// could in principle be observed both via the addon-open inference AND via this rule. The
// snapshot-field guards (see §F O1) ensure the two signals are mutually exclusive in practice,
// but if both fire, the ActionCompleted signal is the more specific (we know the exact action
// id and target, not just "a teleport happened nearby"). Defensive ordering.
//
// CONFIDENCE: High — the player demonstrably pressed the button.
// EXPECT: null — author MUST write the postcondition (per USE_ACTION_STEP_PLAN.md Decision UA4:
// there is no universal "did this action's effect land?" predicate. The engine's stateless
// retry handles the action firing; the author's Expect is what tells the engine when to stop.)
if (after.ActionCompleted is { } actionSignal)
{
    var stepIdSuffix = actionSignal.TargetBaseId is { } tid
        ? $"{actionSignal.ActionId}-on-{tid}"
        : $"{actionSignal.ActionId}";
    return new InferenceResult(
        StepType:        "use-action",
        SuggestedStepId: $"use-action-{stepIdSuffix}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.ActionCompleted,
        Notes:           "Author MUST write the Expect predicate (no universal action postcondition).");
}
```

**Why `SuggestedExpect = null` (not a placeholder like `"TODO: author Expect"`):** the engine treats `Expect == null` as "step never satisfies, re-emit forever" — the loud failure mode. Synthesising a placeholder predicate (e.g. `"questSequence(0) >= 1"`) would be a lie that might accidentally satisfy on some quests. Null is honest.

**Why the `Notes` string:** the Record-Step modal surfaces `InferenceResult.Notes` in the UI. Telling the author "you need to write the Expect" up front is friendlier than letting them ship a draft that loops at runtime.

### Decision UAI5 — `InferredFrom.ActionCompleted` is a new enum value

Existing values (per `InferredFrom.cs`): `ZoneChange, QuestFlagChange, QuestSequenceChange, DialogueInteraction, QuestAccepted, QuestCompleted, AttunementChange, MovementChange, Manual, None, InventoryChange, Combat, Purchase, TeleportCompleted`.

Adding `ActionCompleted` keeps the existing taxonomy intact and lets downstream consumers (trace events, UI badge colours, future analytics) differentiate action-derived steps from teleport-derived ones (which use `TeleportCompleted`).

```csharp
public enum InferredFrom
{
    ZoneChange,
    QuestFlagChange,
    QuestSequenceChange,
    DialogueInteraction,
    QuestAccepted,
    QuestCompleted,
    AttunementChange,
    MovementChange,
    Manual,
    None,
    InventoryChange,
    Combat,
    Purchase,
    TeleportCompleted,
    ActionCompleted,    // NEW
}
```

Rejected alternative: reusing `DialogueInteraction` — would collapse the distinction in the trace and prevent us from filtering out action-only inferences later.

### Decision UAI6 — `StepFactory.Build` gains a `"use-action"` arm

Mirrors the existing arms. Reads `after.ActionCompleted` to populate the `UseActionStep` fields. If `after.ActionCompleted` is null at the moment `StepFactory.Build` is called for `stepType == "use-action"` (defensive — the inference engine only returns "use-action" when the field is set), fall back to `UseActionStep { ActionType = ActionType.Action, ActionId = 0, TargetNpcId = null }` so the Builder's validator catches it later; do not throw.

```csharp
"use-action" => new UseActionStep
{
    Id = stepId,
    Expect = expectValue,           // null in v1 inference (Decision UA4); author edits in modal
    ActionType = after?.ActionCompleted?.ActionType ?? QuestForge.Schema.ActionType.Action,
    ActionId = after?.ActionCompleted?.ActionId ?? 0u,
    TargetNpcId = after?.ActionCompleted?.TargetBaseId
},
```

**Why no `Zone` / `RequiredZone` on UseActionStep:** the schema-side `UseActionStep` (per `Step.cs:169-175`) inherits only `Id` and `Expect` from `Step` — it has NO `Zone` field, NO `RequiredZone`, NO `Target` location. The engine relies entirely on the cursor walk and authored `Expect` for ordering; spatial constraints are encoded by an upstream `TravelStep` if needed.

This contrasts with `TeleportStep` which sets `Zone` and `RequiredZone` to encode source/destination — UseActionStep is shapeless in that respect, and the spec is explicit about it.

**What breaks if violated:** if the StepFactory throws on null `ActionCompleted` instead of falling back, a defensive caller (e.g. a Tester invoking Build("use-action", …, after: emptySnapshot)) crashes the test harness instead of producing a validator-rejectable draft.

### Decision UAI7 — `AuthoringHost.RecordStep` clears `ActionCompleted` after consuming

After the existing three consume calls in `RecordStep` (lines 257-259):

```csharp
_aggregator.OnAethernetTeleportConsumed();
_aggregator.OnDialogueOptionConsumed();
_aggregator.OnTeleportConsumed();
_aggregator.OnActionConsumed();   // NEW
```

Same lifecycle as the teleport event: survives `ResetDeltas` so it remains visible to `PreviewInference`, cleared only after the author confirms the modal.

`UIObserver.ResetWindowState` should ALSO call `_aggregator?.OnActionConsumed()` for symmetry with the existing three (lines 176-178). This keeps the per-window reset path internally consistent.

```csharp
// UIObserver.ResetWindowState — alongside existing consume calls
_aggregator?.OnAethernetTeleportConsumed();
_aggregator?.OnDialogueOptionConsumed();
_aggregator?.OnTeleportConsumed();
_aggregator?.OnActionConsumed();   // NEW
```

### Decision UAI8 — UIObserver poller `PollPlayerActionEffect` mirrors `PollAethernetDestination` shape (sequence-based, not addon-based)

The signal source is fundamentally different from the aethernet / teleport pollers:
- Aethernet / teleport pollers watch an **addon-open transition** (a menu opens, then closes; the close edge fires the event).
- Action-effect poller watches a **monotonically-increasing sequence counter** (`CastInfo.ResponseGlobalSequence`); the rising edge fires the event.

```csharp
// QuestForge.Plugin.Tracing/UIObserver.cs — new every-frame poller, added next to PollAethernetDestination
//
// REQUIRES: IGameProbe gains GetLastActionEffect() returning (uint sequence, uint ffxivActionType, uint actionId)?.
// The probe returns null when there is no LocalPlayer or no recent ActionEffect.
//
// State: _lastObservedActionSequence tracks the most recently consumed ResponseGlobalSequence.
// First-ever read sets the baseline (no event fired). Subsequent reads where sequence increases
// fire OnActionCompleted exactly once per increment. Wraparound (uint counter) is impossible in
// practice within a session — even at 60 FPS for 24 hours that's only ~5.2 million ticks.

// Field declarations
private uint? _lastObservedActionSequence;

private void PollPlayerActionEffect()
{
    if (_gameProbe is null) return;

    var probe = _gameProbe.GetLastActionEffect();
    if (probe is null) return;
    var (sequence, ffxivActionType, actionId) = probe.Value;

    // First observation: establish baseline only. No event fired.
    // WHY: at session start, the LocalPlayer may already have a non-zero ResponseGlobalSequence
    // from a pre-recording action (the player chained an action immediately before clicking
    // "Author Mode"). Treating the first observation as an event would draft a step for an
    // action the author has no intention of including.
    if (_lastObservedActionSequence is null)
    {
        _lastObservedActionSequence = sequence;
        return;
    }

    // No new effect this frame.
    if (sequence == _lastObservedActionSequence) return;

    // A new sequence value — the player used an action. Reverse-map; skip if unsupported.
    _lastObservedActionSequence = sequence;
    var schemaType = QuestForge.Adapters.Actions.ActionTypeMapper.FromFFXIVActionType(ffxivActionType);
    if (schemaType is null) return;   // auto-attack / pet command / etc.

    // Capture the current target's BaseId (null = self-cast / no target). Uses the same
    // ITargetProbe contract that drives PollTargetNpc — but accepting BOTH EventNpc and
    // BattleNpc (an action's target can be a hostile mob, an NPC, or null).
    uint? targetBaseId = null;
    var hostile      = _targetProbe?.GetBattleNpcTarget();
    var interactable = _targetProbe?.GetInteractableNpcTarget();
    if (hostile is { } h)
        targetBaseId = h.BaseId;
    else if (interactable is { } i)
        targetBaseId = i.BaseId;
    // If neither — self-cast, leave targetBaseId = null.

    var now   = _clock.UtcNow;
    var runId = CurrentRunId;
    WriteObservation("ActionCompleted",
        actionId,
        new { actionType = (int)schemaType.Value, targetBaseId = targetBaseId ?? 0u },
        runId, now);
    _aggregator?.OnActionCompleted(schemaType.Value, actionId, targetBaseId);
}
```

Add the call into `OnFrameworkUpdate` alongside the other every-frame pollers:

```csharp
PollAethernetDestination();
PollTeleportAddonOpen();
PollDialogueOption();
PollSelectYesno();
PollTargetNpc();
PollPlayerActionEffect();   // NEW
```

`ResetWindowState` adds:
```csharp
_lastObservedActionSequence = null;   // re-baseline on next observation
_aggregator?.OnActionConsumed();      // mirrors OnAethernetTeleportConsumed
```

**Why also re-baseline on ResetWindowState (not just clear the aggregator field):** if the author records a step, then immediately uses another action before the next heartbeat, the poller sees the new (higher) sequence. Without re-baseline, the comparison is `newSeq > _lastObservedActionSequence`, which still fires — correct behaviour. The re-baseline matters when the author exits/re-enters Author mode: the new session's first observation must NOT fire as an event (it's pre-session state).

**Cancellation handling (the player started a cast but the cast was cancelled before completion):** `ResponseGlobalSequence` is only incremented when ActionEffect lands. A cancelled cast does not increment the sequence, so no event fires. This is the intended behaviour: the player did not actually USE the action, so no step should be inferred. Pinned by U6 (no observation when the probe returns null) and the polling state-machine UO_K2.

**Why fire on sequence-change, not on `CastInfo.IsCasting` transition:** instant actions (most combat abilities) have no cast bar — they go directly to ActionEffect. Watching `IsCasting` would miss them entirely. The Response* fields are populated for BOTH instant and non-instant actions when ActionEffect arrives (per the comment in `CastInfo.cs:22`: "fields below (Response*) are set when ActionEffect is received").

### Decision UAI9 — `IGameProbe.GetLastActionEffect()` is the new probe surface

Following the established `IGameProbe` shape (existing methods are simple tuples or primitives):

```csharp
// QuestForge.Plugin.Tracing/IGameProbe.cs — append one method
public interface IGameProbe
{
    IReadOnlyList<(ushort QuestId, byte Seq, byte Flags, IReadOnlyList<byte> Variables)> GetNormalQuests();
    bool IsAetheryteUnlocked(uint rowId);
    IEnumerable<uint> GetAllAetheryteRowIds();
    IReadOnlyList<(uint ItemId, int Qty)> GetKeyItemSlots();
    (float X, float Y, float Z, int Zone)? GetPlayerPosition();
    (uint Sequence, uint FfxivActionType, uint ActionId)? GetLastActionEffect();   // NEW
}
```

**Why a single method returning a tuple, not three separate accessors:** the three values come from the same struct read (`LocalPlayer.CastInfo.Response*`). Splitting into three calls would either (a) cause a race where two consecutive reads observe two different actions (sequence N then sequence N+1 mid-read), or (b) force the probe to cache a snapshot — premature abstraction. A single tuple-returning method is the simplest correct shape.

**Why `uint FfxivActionType` (not `byte`):** the underlying enum is `uint`-typed in FFXIVClientStructs (per `ActionManager.cs:395` — `public enum ActionType : uint`). `CastInfo.ResponseActionType` stores it as a single byte (offset 0x44), but the canonical type is uint and the reverse-mapper takes a uint. Widening at the probe boundary is free.

**Why null when no LocalPlayer:** matches `GetPlayerPosition`'s contract (line 14 of `IGameProbe.cs` — returns null when LocalPlayer is null). The poller's `if (probe is null) return;` handles it.

**Concrete Dalamud implementation:**

```csharp
// QuestForge.Plugin/Tracing/DalamudGameProbe.cs — append
public unsafe (uint Sequence, uint FfxivActionType, uint ActionId)? GetLastActionEffect()
{
    var player = _objectTable.LocalPlayer;
    if (player is null) return null;
    // The local player IS a BattleChara (Character → BattleChara inheritance per BattleChara.cs).
    // Cast via Dalamud's address to access the CastInfo at offset 0x2790.
    var bc = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)player.Address;
    if (bc is null) return null;
    ref var castInfo = ref bc->CastInfo;
    // ResponseGlobalSequence is the canonical signal: monotonically increases per action.
    // ResponseActionType byte is the FFXIVClientStructs ActionType enum (uint cast).
    // ResponseActionId is the action id (Lumina Action/EventItem/GeneralAction row).
    return (castInfo.ResponseGlobalSequence, (uint)castInfo.ResponseActionType, castInfo.ResponseActionId);
}
```

**Why we read via `player.Address` cast rather than `LocalPlayer.CastInfo`:** the Dalamud `IPlayerCharacter` wrapper does not expose `Response*` fields (only the live cast bar fields). The cast to the FFXIVClientStructs struct is the same pattern `DalamudCombatProbe` uses to access `CurrentHp` (line 41-42 of `DalamudCombatProbe.cs`: `obj is not IBattleChara bc … bc.CurrentHp`) — but for fields not surfaced on the Dalamud wrapper, the raw struct pointer is the only path.

### Decision UAI10 — Trace observation event method is `"ActionCompleted"`

The trace stream is consumed by `qf-trace extract-quest` (Phase 10) to reconstruct quest definitions. Using a distinct method name lets the extractor route the event to a `UseActionStep` arm directly. Mirroring the existing `WriteObservation("TeleportCompleted", destId, 0u, runId, now)` pattern:

```csharp
WriteObservation("ActionCompleted",
    actionId,
    new { actionType = (int)schemaType.Value, targetBaseId = targetBaseId ?? 0u },
    runId, now);
```

- `argument` carries the action id (the most identifying primitive).
- `value` is a structured object with `actionType` (schema-side int) and `targetBaseId` (0 = no target / self-cast).

**Why `actionType` as int, not string:** the trace consumer is downstream of the schema enum; using the int avoids any JSON-naming-policy discrepancy. The JSON-string form is reserved for the quest-data file (where authoring readability matters).

**Why `targetBaseId ?? 0u` and not omit-when-null:** dedup logic in `TraceSession.WriteObservation` compares the serialized value; a stable shape (always-present `targetBaseId`) means two consecutive identical action uses dedup cleanly (TraceSession suppresses unchanged values).

### Decision UAI11 — Target read is "current target at the time of the read" (TargetManager.Target via ITargetProbe), NOT the action's own targetId

`CastInfo` carries a `TargetId` field (offset 0x10, `GameObjectId`) AND `ResponseTargetIds` (a fixed-size array at offset 0x58). Both reflect the action's actual target. **We do NOT use them.** Reasons:

1. **Schema authors target NPCs by BaseId (the static template id), not by GameObjectId (the per-instance id).** `CastInfo.TargetId` is a `GameObjectId` (a `ulong` wrapping the per-instance id) — we would have to resolve it back to a BaseId via ObjectTable scan, which is fragile (target may have despawned in the few frames between cast and read).
2. **The author's intent ("which NPC am I targeting for this action?") is reliably captured by `TargetManager.Target.BaseId`** (which `DalamudTargetProbe.GetBattleNpcTarget` / `GetInteractableNpcTarget` already return). At the moment the player presses an action button on a target, that target IS `TargetManager.Target`.
3. **Self-cast handling is simpler.** If `TargetManager.Target` is null, the action is self-cast (or AoE with no target lock); both cases map to `TargetBaseId = null`. Reading `CastInfo.TargetId` would yield the LocalPlayer's own GameObjectId for self-cast, requiring a "is this me?" comparison that adds noise.

**What breaks if violated:** if we read `CastInfo.TargetId` and ObjectTable-resolve it to BaseId, we get the wrong target on every queued action where the player has retargeted before the cast lands. Author intent is always "the target I had when I pressed the button," which is what `TargetManager.Target` captures.

### Decision UAI12 — Diagnostic log line extension

The existing `[QF-DIAG] PreviewInference:` line in `AuthoringHost.PreviewInference` (line 187) already lists `AethernetTeleportCompleted`, `TeleportCompleted`, `DialogueOptionSelected`, `DialogueNpcSource`, plus shard/npc state. Append `ActionCompleted` so the same diagnostic exposure exists:

```csharp
_services.Log.Debug($"[QF-DIAG] PreviewInference: zone {before.Zone.Value}→{after.Zone.Value} " +
    $"AethernetTeleportCompleted={after.AethernetTeleportCompleted?.To.Value} " +
    $"TeleportCompleted={after.TeleportCompleted?.Value} " +
    $"ActionCompleted={after.ActionCompleted?.ActionId} " +     // NEW
    $"DialogueOptionSelected={after.DialogueOptionSelected} " +
    $"DialogueNpcSource={after.DialogueNpcSource?.NpcId} " +
    $"isAethernet_before_shard={before.LastAethernetShardInteracted?.Value} " +
    $"isAethernet_before_npc={before.LastNpcInteracted?.Value}");
```

**Why only the ActionId:** the diagnostic line is single-line; adding ActionType+Id+Target would push it past readable width. The ActionId is the most diagnostic single value (matches Lumina rows in `/xldata`).

### Decision UAI13 — `FakeGameProbe` extension (Plugin.Tests)

Extend the existing `FakeGameProbe` (currently in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` lines 96-140) with a scriptable `GetLastActionEffect`:

```csharp
public sealed class FakeGameProbe : IGameProbe
{
    // ... existing fields ...
    private (uint, uint, uint)? _nextActionEffect;

    public void SetLastActionEffect(uint sequence, uint ffxivActionType, uint actionId)
        => _nextActionEffect = (sequence, ffxivActionType, actionId);

    public void ClearLastActionEffect() => _nextActionEffect = null;

    // ... existing methods ...

    public (uint Sequence, uint FfxivActionType, uint ActionId)? GetLastActionEffect()
        => _nextActionEffect;
}
```

`SetLastActionEffect` is **sticky** (mirrors `_unlockedAetherytes` semantics — the probe represents "what the game currently is"). Tests that need to simulate "the player presses an action" call `SetLastActionEffect(seq, type, id)` then `framework.Tick()` — the poller observes the new sequence and fires.

A separate `FakeTargetProbe` already exists in `QuestForge.Plugin.Tests` (referenced by combat-forwarding tests); reuse it for target injection.

### Decision UAI14 — No re-record cascade impact (additive observation)

Per the user's MEMORY note: "Adding engine reads starves existing fixtures → re-record cascade." This plan introduces:
- A **new probe method** (`GetLastActionEffect`) called every frame by UIObserver.
- A **new observation event** (`"ActionCompleted"`).

**Why this does NOT cascade existing fixtures:** the engine's `QuestEngine.Tick` is unchanged — `IActionExecutor` is still consulted only when a `UseActionStep` is at the cursor. The new probe call is in the **authoring poller**, not in the engine. Engine replay fixtures (which capture engine reads) are unaffected. UIObserver-fixture replays would see new `"ActionCompleted"` observation events, but the UIObserver test suite uses `FakeGameProbe` (the new method defaults to returning null when `SetLastActionEffect` is not called), so existing tests stay green.

If a future trace-replay test exists that records UIObserver output against a real game session, the new `"ActionCompleted"` events would appear in new fixtures only — no existing fixture is re-recorded.

---

## Snapshot field summary

| Field (new or existing) | Type | Set by | Cleared by | Survives ResetDeltas? |
|---|---|---|---|---|
| `ActionCompleted` (NEW) | `ActionCompletedSignal?` | `OnActionCompleted` | `OnActionConsumed` | yes |
| `TeleportCompleted` (existing) | `AetheryteId?` | `OnTeleportCompleted` | `OnTeleportConsumed` | yes |
| `AethernetTeleportCompleted` (existing) | `AethernetHop?` | `OnAethernetTeleportCompleted` | `OnAethernetTeleportConsumed` | yes |
| `DialogueOptionSelected` (existing) | `int?` | `OnDialogueOptionSelected` | `OnDialogueOptionConsumed` | yes |

The new `ActionCompleted` shares the **per-window event lifecycle** of the other three: cleared in `RecordStep` (and defensively in `ResetWindowState`), survives `ResetDeltas` between the "before" capture and the "after" Preview.

---

## Inference rule table — updated

| Rule | Trigger condition | Step type | InferredFrom | Confidence |
|---|---|---|---|---|
| 1 | QuestCompleted false→true | turn-in | QuestCompleted | High |
| 2 | QuestAccepted false→true | accept | QuestAccepted | High |
| 2.1 | ForeignQuestAccepted set | accept | QuestAccepted | High |
| 2.2 | KillCorrelatedTargets non-empty | combat | Combat | Med/Low |
| 2.2b | PurchaseDetected with item delta + currency drop | purchase-item | Purchase | Med/Low |
| 2.3 | KeyItemsAdded non-empty | pickup-item | DialogueInteraction | Medium |
| 2.4 | KeyItemsRemoved non-empty | hand-over-item | DialogueInteraction | Medium |
| 2.5 | LastAethernetShardInteracted changed, same zone, NPC == shard | attune | AttunementChange | High |
| 2.6 | InventoryHash changed, KeyItems diff non-empty | pickup/handover/talk | InventoryChange | Medium |
| **3.5 (NEW)** | **after.ActionCompleted set** | **use-action** | **ActionCompleted** | **High** |
| 3   | QuestSequence advanced | talk | QuestSequenceChange | High |
| 4.0 | after.TeleportCompleted set AND zone changed | teleport | TeleportCompleted | High |
| 4   | Zone changed (aethernet / NPC dialogue / shard / catch-all) | travel | ZoneChange | High |
| 2.7 | AethernetTeleportCompleted set, same zone | travel | ZoneChange | High |
| 5   | QuestFlags changed, sequence unchanged | talk | QuestFlagChange | Medium |
| 6   | LastDialogueAnswer changed | talk | DialogueInteraction | Medium |
| 7   | LastNpcInteracted changed | talk | DialogueInteraction | Low |
| 8   | Player moved >5u, same zone | travel | MovementChange | Low |
| 9   | nothing matched | Empty | None | Low |

Rule 3.5 occupies the position **immediately before Rule 3** in the source file. It does NOT require any zone-change or quest-state condition — the presence of `after.ActionCompleted` alone is sufficient.

---

## Task breakdown

### Task UAI-T1 — Adapters: `ActionTypeMapper.FromFFXIVActionType`

1. Create `QuestForge.Adapters/Actions/ActionTypeMapper.cs` with the static helper per Decision UAI2.
2. The class is `public static` so `QuestForge.Plugin.Tracing` (a Dalamud-bound assembly) and `QuestForge.Adapters.Tests` (a Dalamud-free test assembly) both consume it.
3. No new file in `QuestForge.Adapters.Dalamud` — the reverse mapper lives only in `QuestForge.Adapters`.

### Task UAI-T2 — Engine: `ActionCompletedSignal` record + `GameStateSnapshot.ActionCompleted`

1. Edit `QuestForge.Engine/Authoring/GameStateSnapshot.cs`.
2. Add the `ActionCompletedSignal` record near the top (alongside `AethernetHop`, `PurchaseDetection`, `KillCorrelation`):
   ```csharp
   public sealed record ActionCompletedSignal(
       QuestForge.Schema.ActionType ActionType,
       uint ActionId,
       uint? TargetBaseId);
   ```
3. Append a non-positional property at the end of the snapshot:
   ```csharp
   // Non-positional. Set when UIObserver.PollPlayerActionEffect observes that the player used a
   // supported game action (Action / GeneralAction / KeyItem) during this recording window.
   // Cleared by OnActionConsumed in RecordStep so it does not bleed into the next window.
   public ActionCompletedSignal? ActionCompleted { get; init; }
   ```
4. No other existing tests should change behaviour (additive property + additive record).

### Task UAI-T3 — Engine: `InferredFrom.ActionCompleted`

1. Edit `QuestForge.Engine/Authoring/InferredFrom.cs`.
2. Append `ActionCompleted,` to the enum per Decision UAI5.

### Task UAI-T4 — Engine: `SnapshotAggregator.OnActionCompleted` / `OnActionConsumed`

1. Edit `QuestForge.Engine/Authoring/SnapshotAggregator.cs`.
2. Add the backing field `private ActionCompletedSignal? _actionCompleted;`.
3. Add `ActionCompleted = _actionCompleted,` to the object-initializer in the `Current` property body (alongside `TeleportCompleted = _teleportCompleted`).
4. Add the setter and consumer methods per Decision UAI3.
5. Do **not** touch `_lastNpcInteracted`, `_lastAttuned`, `_lastAethernetShardInteracted`, or any other unrelated field.
6. Do **not** clear in `ResetDeltas` (survives per-window lifecycle; only `OnActionConsumed` clears).

### Task UAI-T5 — Engine: `StepInferenceEngine` Rule 3.5

1. Edit `QuestForge.Engine/Authoring/StepInferenceEngine.cs`.
2. Insert the rule per Decision UAI4 immediately above the existing `// Rule 3: QuestSequence advanced` comment (line 264).
3. Confidence: `Confidence.High`. SuggestedExpect: `null`. Notes: the canonical "author must write Expect" string.
4. SuggestedStepId pattern: `$"use-action-{ActionId}-on-{TargetBaseId}"` when target present, `$"use-action-{ActionId}"` when self-cast.

### Task UAI-T6 — Engine: `StepFactory` `"use-action"` arm

1. Edit `QuestForge.Engine/Authoring/StepFactory.cs`.
2. Add the `"use-action"` arm per Decision UAI6 — placed in the existing `stepType switch` block alongside `"teleport"`, `"attune"`, etc.
3. Confirm `using QuestForge.Schema;` is already in scope (yes — line 3).

### Task UAI-T7 — Plugin: `AuthoringHost.RecordStep` clearing + diagnostic log

1. Edit `QuestForge.Plugin/Authoring/AuthoringHost.cs`.
2. At the end of `RecordStep`, alongside the existing three consume calls (lines 257-259), add:
   ```csharp
   _aggregator.OnActionConsumed();   // NEW
   ```
3. Extend the `[QF-DIAG] PreviewInference:` line per Decision UAI12 with `ActionCompleted={after.ActionCompleted?.ActionId}`.

### Task UAI-T8 — Plugin.Tracing: `IGameProbe.GetLastActionEffect()` + `FakeGameProbe` extension

1. Edit `QuestForge.Plugin.Tracing/IGameProbe.cs` — append `(uint Sequence, uint FfxivActionType, uint ActionId)? GetLastActionEffect();`.
2. Edit `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` `FakeGameProbe` (lines 96-140) — add the `_nextActionEffect` field, the `SetLastActionEffect` / `ClearLastActionEffect` helpers, and the `GetLastActionEffect` accessor per Decision UAI13.

### Task UAI-T9 — Plugin.Tracing: `UIObserver.PollPlayerActionEffect` + ResetWindowState

1. Edit `QuestForge.Plugin.Tracing/UIObserver.cs`.
2. Add the per-window state field `private uint? _lastObservedActionSequence;`.
3. Add `PollPlayerActionEffect()` method per Decision UAI8.
4. Wire the call into `OnFrameworkUpdate` after `PollTargetNpc` (the last every-frame poller).
5. Wire into `ResetWindowState`: `_lastObservedActionSequence = null;` and `_aggregator?.OnActionConsumed();`.

### Task UAI-T10 — Plugin: `DalamudGameProbe.GetLastActionEffect`

1. Edit `QuestForge.Plugin/Tracing/DalamudGameProbe.cs`.
2. Implement per Decision UAI9, using the `BattleChara*` cast pattern from `DalamudCombatProbe` (line 41).
3. **Add `using FFXIVClientStructs.FFXIV.Client.Game.Character;`** at the top (for `BattleChara`).
4. Manual smoke test in-game: enter Author mode for any quest, use an action, confirm the trace contains `"ActionCompleted"` with the correct ActionId.

---

## Validation rules (this plan adds none)

Validator rules for `structural/use-action-*` are deferred to a validator-side PR (per `USE_ACTION_STEP_PLAN.md` Decision UA7). The authoring path is downstream of validation: a draft containing a `UseActionStep { ActionId = 0 }` (defensive fallback in Decision UAI6) will be caught when the draft is exported.

---

## Given-When-Then test scenarios

Tests are split into three files:

| File | Scenarios | Test type |
|---|---|---|
| `QuestForge.Adapters.Tests/Actions/ActionTypeMapperTests.cs` | UAI-M1..M5 | Pure mapper unit tests |
| `QuestForge.Engine.Tests/Authoring/UseActionInferenceTests.cs` | UAI1..UAI11 | Inference engine + aggregator + StepFactory |
| `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` | UO_K1..UO_K6 | UIObserver polling state-machine |

### Mapper tests (`QuestForge.Adapters.Tests/Actions/ActionTypeMapperTests.cs`)

#### UAI-M1 — `FromFFXIVActionType(1u)` → `Schema.ActionType.Action`

```csharp
[Fact]
public void FromFFXIVActionType_Action_MapsToAction()
{
    var result = ActionTypeMapper.FromFFXIVActionType(1u);
    Assert.Equal(QuestForge.Schema.ActionType.Action, result);
}
```

**Given:** uint 1 (FFXIVClientStructs Action). **When:** map. **Then:** `Schema.ActionType.Action`.

#### UAI-M2 — `FromFFXIVActionType(5u)` → `Schema.ActionType.GeneralAction`

```csharp
[Fact]
public void FromFFXIVActionType_GeneralAction_MapsToGeneralAction()
{
    var result = ActionTypeMapper.FromFFXIVActionType(5u);
    Assert.Equal(QuestForge.Schema.ActionType.GeneralAction, result);
}
```

**Given:** uint 5 (FFXIVClientStructs GeneralAction). **When:** map. **Then:** `Schema.ActionType.GeneralAction`.

#### UAI-M3 — `FromFFXIVActionType(3u)` → `Schema.ActionType.KeyItem` (rename verification)

```csharp
[Fact]
public void FromFFXIVActionType_EventItemValue3_MapsToKeyItem()
{
    // CRITICAL: FFXIVClientStructs value 3 is named "EventItem", but the schema renames
    // it to "KeyItem" (matching the in-game terminology). This test pins the round-trip
    // through the rename — UAI-M3 here is the read direction; DA3 (in USE_ACTION_DALAMUD_PLAN)
    // pins the write direction.
    var result = ActionTypeMapper.FromFFXIVActionType(3u);
    Assert.Equal(QuestForge.Schema.ActionType.KeyItem, result);
}
```

**Given:** uint 3 (FFXIVClientStructs EventItem). **When:** map. **Then:** `Schema.ActionType.KeyItem`.

#### UAI-M4 — `FromFFXIVActionType(2u)` (Item) → null

```csharp
[Fact]
public void FromFFXIVActionType_Item_ReturnsNull_NotMappedToUseActionStep()
{
    // Item (value 2) is FFXIVClientStructs's inventory-item bucket. Per Decision UA8 in
    // USE_ACTION_STEP_PLAN, inventory items belong to UseItemStep, NOT UseActionStep.
    // The mapper returns null so the poller skips the observation.
    var result = ActionTypeMapper.FromFFXIVActionType(2u);
    Assert.Null(result);
}
```

**Given:** uint 2 (FFXIVClientStructs Item). **When:** map. **Then:** null.

#### UAI-M5 — Theory: all other game-side values → null

```csharp
[Theory]
[InlineData(0u)]   // None
[InlineData(4u)]   // EventAction
[InlineData(6u)]   // BuddyAction
[InlineData(7u)]   // MainCommand
[InlineData(8u)]   // Companion
[InlineData(9u)]   // CraftAction
[InlineData(10u)]  // Unk10
[InlineData(11u)]  // PetAction
[InlineData(13u)]  // Mount
[InlineData(14u)]  // PvPAction
[InlineData(15u)]  // FieldMarker
[InlineData(16u)]  // ChocoboRaceAbility
[InlineData(19u)]  // BgcArmyAction
[InlineData(20u)]  // Ornament
[InlineData(99u)]  // bogus / future
public void FromFFXIVActionType_UnsupportedValue_ReturnsNull(uint ffxivType)
{
    Assert.Null(ActionTypeMapper.FromFFXIVActionType(ffxivType));
}
```

**Given:** any unsupported game-side ActionType. **When:** map. **Then:** null (poller skips).

### Inference-engine tests (`QuestForge.Engine.Tests/Authoring/UseActionInferenceTests.cs`)

For all tests below, helpers `MakeSnapshot(...)` and `MakeAggregator(...)` follow the same patterns as `TeleportInferenceTests` and `AethernetInferenceTests` — the Tester picks the exact factory signature.

#### UAI1 — Happy path, no target: ActionCompleted set → infers `use-action` step

**Given:**
- `before = MakeSnapshot()` (no quest changes, no zone change)
- `after  = before with { ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-action"`
- `result.SuggestedStepId == "use-action-31"`
- `result.SuggestedExpect == null`
- `result.Confidence == Confidence.High`
- `result.InferredFrom == InferredFrom.ActionCompleted`
- `result.Notes` is non-null and contains the substring `"Expect"`.

#### UAI2 — Happy path, with NPC target: ActionCompleted with TargetBaseId → step has TargetBaseId in stepId

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, 2001234u) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-action"`
- `result.SuggestedStepId == "use-action-31-on-2001234"`
- `result.SuggestedExpect == null`
- `result.InferredFrom == InferredFrom.ActionCompleted`

#### UAI3 — Self-cast: ActionCompleted with TargetBaseId = null → step id has no "-on-" suffix

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.GeneralAction, 4u, null) }` (Sprint, self-cast)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-action"`
- `result.SuggestedStepId == "use-action-4"` (NO `-on-` suffix)
- `result.InferredFrom == InferredFrom.ActionCompleted`

Pins Decision UAI4's step-id pattern.

#### UAI4 — Priority over Rule 3 (QuestSequence advanced): ActionCompleted wins over sequence-advance

**Given:**
- `before = MakeSnapshot(questSequence: 1)`
- `after  = before with { QuestSequence = 2, ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, 2001234u) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-action"` (NOT `"talk"`)
- `result.InferredFrom == InferredFrom.ActionCompleted` (NOT `QuestSequenceChange`)

**Rationale (justifies the priority order per Decision UAI4):** use-action that advances the sequence is the most common authoring case for `UseActionStep`; firing Rule 3 first would draft a `talk` step the author would have to delete.

#### UAI5 — Priority over Rule 4.0 (TeleportCompleted): ActionCompleted wins when both set (defensive)

**Given:**
- `before = MakeSnapshot(zone: ZoneId(132))`
- `after  = before with { Zone = ZoneId(129), ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.GeneralAction, 5u, null), TeleportCompleted = new AetheryteId(8) }`
- (defensive — production should never set both, but the fields are independent)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-action"` (NOT `"teleport"`)
- `result.SuggestedStepId == "use-action-5"`
- `result.InferredFrom == InferredFrom.ActionCompleted`

Pins Decision UAI4's "Rule 3.5 above Rule 4.0" ordering.

#### UAI6 — Priority below Rule 1 (QuestCompleted): turn-in wins over ActionCompleted

**Given:**
- `before = MakeSnapshot(questCompleted: false)`
- `after  = before with { QuestCompleted = true, ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, null) }`
- (player pressed an action that triggered the turn-in)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "turn-in"` (Rule 1)
- `result.InferredFrom == InferredFrom.QuestCompleted`

Pins "earlier rules take precedence over Rule 3.5."

#### UAI7 — Priority below Rule 2.3 (KeyItemsAdded): pickup-item wins over ActionCompleted

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { KeyItemsAdded = new[] { 2001u }, ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "pickup-item"` (Rule 2.3)
- `result.InferredFrom == InferredFrom.DialogueInteraction`

Pins the placement above Rule 3 but below the specific Rule-2.x family.

#### UAI8 — Aggregator: `OnActionCompleted` sets the field; `Current.ActionCompleted` returns the same signal

**Given:** `var agg = new SnapshotAggregator(activeQuest: null, clock: new FakeClock(T0));`

**When:**
- `agg.OnActionCompleted(QuestForge.Schema.ActionType.Action, 31u, 2001234u);`
- `var snap = agg.Current;`

**Then:**
- `snap.ActionCompleted is not null`
- `snap.ActionCompleted.ActionType == QuestForge.Schema.ActionType.Action`
- `snap.ActionCompleted.ActionId == 31u`
- `snap.ActionCompleted.TargetBaseId == 2001234u`

#### UAI9 — Aggregator: `OnActionConsumed` clears the field; `OnActionCompleted` does NOT side-effect other state

**Given:**
- `var agg = new SnapshotAggregator(null, new FakeClock(T0));`
- `agg.OnActionCompleted(QuestForge.Schema.ActionType.Action, 31u, 2001234u);`

**When:**
- `agg.OnActionConsumed();`
- `var snap = agg.Current;`

**Then:**
- `snap.ActionCompleted is null`
- `snap.LastNpcInteracted is null` (action did NOT bleed into NPC-interaction state — pins Decision UAI3)
- `snap.LastAttuned is null`
- `snap.LastAethernetShardInteracted is null`

#### UAI10 — Aggregator: `ResetDeltas` does NOT clear `ActionCompleted`

**Given:** `agg.OnActionCompleted(QuestForge.Schema.ActionType.Action, 31u, null);`

**When:** `agg.ResetDeltas();`

**Then:** `agg.Current.ActionCompleted is not null` (survives `ResetDeltas`; only `OnActionConsumed` clears).

Pins Decision UAI3's lifecycle.

#### UAI11 — `StepFactory.Build("use-action", …)` produces a `UseActionStep` with snapshot fields populated

**Given:**
- `after = MakeSnapshot() with { ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, 2001234u) }`

**When:** `var step = StepFactory.Build("use-action", "use-action-31-on-2001234", null, after);`

**Then:**
- `step is UseActionStep us`
- `us.Id == "use-action-31-on-2001234"`
- `us.ActionType == QuestForge.Schema.ActionType.Action`
- `us.ActionId == 31u`
- `us.TargetNpcId == 2001234u`
- `us.Expect is null`

#### UAI11b — `StepFactory.Build("use-action", …)` defensive: ActionCompleted null → ActionId(0), no throw

**Given:** `after = MakeSnapshot()` (ActionCompleted is null)

**When:** `var step = StepFactory.Build("use-action", "use-action-X", null, after);`

**Then:**
- `step is UseActionStep us`
- `us.ActionId == 0u` (defensive fallback; validator will catch)
- `us.TargetNpcId is null`
- `us.ActionType == QuestForge.Schema.ActionType.Action` (the default arm)
- No exception thrown.

Pins Decision UAI6's defensive behaviour.

### UIObserver tests (`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`)

These tests use the existing `BuildFixtureWithAggregator` helper and the `FakeGameProbe` extension from Task UAI-T8. Test naming follows `UO_X*` convention; new group letter is `UO_K`.

For target injection, the Tester adds a `FakeTargetProbe` to the fixture builder (or extends the existing one). The simplest path: pass a `FakeTargetProbe` as an additional optional parameter to `BuildFixtureWithAggregator`, defaulting to one that returns null for all target queries.

#### UO_K1 — First observation establishes baseline; no event fires

**Given:**
- `var (obs, fw, ap, gp, clock, writer, _, agg) = BuildFixtureWithAggregator();`
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);`
  (the LocalPlayer has a non-zero sequence from before the session)

**When:** `fw.Tick();` (first tick after authoring started)

**Then:**
- NO `ObservationEvent` with `Method == "ActionCompleted"` written.
- `agg.Current.ActionCompleted is null`.

Pins Decision UAI8's first-observation baseline.

#### UO_K2 — Second observation with unchanged sequence: no event

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);`
- `fw.Tick();` (baseline established)

**When:** `fw.Tick();` (probe still returns the same sequence)

**Then:**
- NO `ObservationEvent` with `Method == "ActionCompleted"` written (sequence unchanged).
- `agg.Current.ActionCompleted is null`.

#### UO_K3 — Sequence increments → fires OnActionCompleted exactly once

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);`
- `fw.Tick();` (baseline = 100)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 1u, actionId: 31u);`

**When:** `fw.Tick();`

**Then:**
- Exactly one `ObservationEvent` with `Method == "ActionCompleted"` and `Argument == 31u`.
- `agg.Current.ActionCompleted is not null`
- `agg.Current.ActionCompleted.ActionType == QuestForge.Schema.ActionType.Action`
- `agg.Current.ActionCompleted.ActionId == 31u`
- `agg.Current.ActionCompleted.TargetBaseId is null` (no target probe set up).

#### UO_K4 — Multiple sequence increments → fires once per increment

**Given:**
- `gp.SetLastActionEffect(seq: 100, type: 1, id: 31); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(seq: 101, type: 1, id: 31); fw.Tick();` (event 1)
- `gp.SetLastActionEffect(seq: 102, type: 1, id: 35); fw.Tick();` (event 2 — different action)

**When:** all three ticks have completed.

**Then:**
- Two `ObservationEvent` entries with `Method == "ActionCompleted"`.
- First: `Argument == 31u`.
- Second: `Argument == 35u`.
- `agg.Current.ActionCompleted.ActionId == 35u` (latest wins).

#### UO_K5 — Unsupported ActionType (e.g. Item = 2): no event, but sequence DOES advance

**Given:**
- `gp.SetLastActionEffect(seq: 100, type: 1, id: 31); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(seq: 101, type: 2u, id: 999); fw.Tick();` (Item — unsupported)
- `gp.SetLastActionEffect(seq: 102, type: 1, id: 31); fw.Tick();` (Action — supported)

**Then:**
- Exactly ONE `ObservationEvent` with `Method == "ActionCompleted"` (the supported one at seq 102).
- The unsupported observation at seq 101 was skipped (ActionTypeMapper returned null).
- BUT the baseline _lastObservedActionSequence advanced past 101 (so the seq 102 read correctly sees `102 > 101`, NOT `102 > 100`).
- `agg.Current.ActionCompleted.ActionId == 31u` (the supported one).

**Critical sub-assertion:** the poller must update `_lastObservedActionSequence` even when the type is unsupported — otherwise a subsequent supported action at seq 102 would fire twice (once for the implicit 101 → 102 delta, once for the 100 → 102 delta). The implementation does `_lastObservedActionSequence = sequence;` BEFORE the type-mapper check.

#### UO_K6 — `ResetWindowState` clears the last-observed sequence: next observation is treated as baseline

**Given:**
- `gp.SetLastActionEffect(seq: 100, type: 1, id: 31); fw.Tick();` (baseline = 100)
- `obs.ResetWindowState();` (e.g. modal opened; window reset)
- `gp.SetLastActionEffect(seq: 200, type: 1, id: 31);` (large jump)

**When:** `fw.Tick();`

**Then:**
- NO `ObservationEvent` with `Method == "ActionCompleted"` written (the post-reset tick is the new baseline).
- `agg.Current.ActionCompleted is null` (also cleared by the `OnActionConsumed()` call in ResetWindowState).

Pins Decision UAI8's "re-baseline on ResetWindowState" behaviour.

#### UO_K7 — `IGameProbe` is null → no observation, no NRE

**Given:** UIObserver constructed with `gameProbe: null` (existing `UO_A2` pattern).

**When:** multiple `fw.Tick()` calls.

**Then:**
- NO `ObservationEvent` with `Method == "ActionCompleted"` written.
- No exception thrown.

#### UO_K8 — Target probe returns BattleNpc: TargetBaseId is the BattleNpc BaseId

**Given:**
- Fixture extended with a `FakeTargetProbe`.
- `fakeTarget.SetBattleNpcTarget(BaseId: 5005u, X: 0, Y: 0, Z: 0, Zone: 132);`
- `gp.SetLastActionEffect(seq: 100, type: 1, id: 31); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(seq: 101, type: 1, id: 31);`

**When:** `fw.Tick();`

**Then:**
- One `ObservationEvent` with `Method == "ActionCompleted"`, `Argument == 31u`.
- `agg.Current.ActionCompleted.TargetBaseId == 5005u`.

#### UO_K9 — Target probe returns no target: TargetBaseId is null (self-cast)

**Given:**
- Fixture's `FakeTargetProbe` returns null for all queries.
- `gp.SetLastActionEffect(seq: 100, type: 5, id: 4); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(seq: 101, type: 5, id: 4);` (Sprint, self-cast)

**When:** `fw.Tick();`

**Then:**
- One `ObservationEvent` with `Method == "ActionCompleted"`, `Argument == 4u`.
- `agg.Current.ActionCompleted.TargetBaseId is null`.

#### UO_K10 — Target probe returns BOTH a BattleNpc and an interactable: BattleNpc wins (priority)

**Given:**
- Fixture's `FakeTargetProbe.SetBattleNpcTarget(5005u, …)` AND `SetInteractableNpcTarget(1234567u, …)`.
- (defensive — in real Dalamud only one is the current hard target; we pick BattleNpc-wins for explicitness)
- `gp.SetLastActionEffect(seq: 100, type: 1, id: 31); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(seq: 101, type: 1, id: 31);`

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ActionCompleted.TargetBaseId == 5005u` (BattleNpc wins per Decision UAI8's branch order: `if (hostile is { } h) targetBaseId = h.BaseId; else if (interactable is { } i) ...`).

### Plan-level scenario classification

| Scenario | File | Type |
|---|---|---|
| UAI-M1..M5 | `ActionTypeMapperTests` | pure mapper |
| UAI1..UAI7 | `UseActionInferenceTests` | inference engine |
| UAI8..UAI10 | `UseActionInferenceTests` | aggregator |
| UAI11..UAI11b | `UseActionInferenceTests` | StepFactory |
| UO_K1..K10 | `UIObserverTests` | UIObserver + FakeGameProbe + FakeTargetProbe |

**Total: 5 mapper + 11 inference/aggregator/factory + 10 UIObserver = 26 new tests.**

---

## F. Open questions / discovery items

### O1 — Does `ResponseGlobalSequence` increment for every action category we care about?

**Status:** unknown until in-game validation.

The FFXIVClientStructs comment (`CastInfo.cs:22`) says Response* fields are set when ActionEffect is received. ActionEffect packets are sent for:
- **Combat abilities** (`ActionType.Action`) — both instant and cast-time, both player-initiated and otherwise.
- **General actions** — confirmed for some (e.g. Mount Roulette, the existing `DalamudMount.Mount()` call uses `ActionManager.UseAction(GeneralAction, 9)` which triggers an effect). Teleport (id 5) and Return (id 8) likely also fire ActionEffect on cast completion.
- **Key items (`EventItem`)** — strongly likely (every "use this item from the quest log" action goes through the same ActionManager path).

What might NOT increment ResponseGlobalSequence:
- **Auto-attacks**: per the spec hint, auto-attacks may share the same packet path. If they DO increment the sequence, every melee swing during combat would trigger an inference event for the next Author-mode Record. **Mitigation:** the player rarely intends to author an auto-attack as a use-action step (auto-attacks have no `ActionId` the author would write). Even if the sequence ticks every auto-attack, the inference fires `use-action-attack-0` (or similar bogus id), the author sees the wrong suggestion in the Record modal, and either picks a different inference or deletes the draft step. NOT a blocker for v1.
  - **If this becomes a problem in smoke:** add a guard in `PollPlayerActionEffect` — `if (actionId < 100u) return;` (auto-attack IDs are typically < 10). The exact threshold needs in-game verification. Defer.
- **Cancelled casts**: as noted in Decision UAI8, cancelled casts do NOT increment the sequence. Confirmed by the FFXIVClientStructs comment.
- **AoE ground-targeted actions**: same code path; should increment the sequence on cast completion.

**Discovery recommendation:**
1. Manual in-game: enter Inspect mode (which now polls the new field via `[QF-DIAG]` log), perform each of {auto-attack, combat ability, mount, teleport, key item use}, observe whether each produces an `[QF-DIAG]` entry with a different ActionId.
2. If auto-attacks DO show up: add the filter mentioned above. Document in `USE_ACTION_INFERENCE_PLAN.md` revision.

**Resolution unblocks Phase E** (in-game smoke). Phase A-D (engine surface + tests) are unblocked.

### O2 — Self-target detection: `TargetManager.Target` may be null OR may be the LocalPlayer object

If the player has self-targeted (`/target <me>`), `TargetManager.Target` IS the LocalPlayer. Our `FakeTargetProbe` returns BaseId = LocalPlayer.BaseId for that case, which would cause `TargetBaseId = <player BaseId>` — wrong (we want null for self-cast).

**Mitigation:**
- `DalamudTargetProbe.GetInteractableNpcTarget` filters on `ObjectKind is EventNpc or BattleNpc`. LocalPlayer is `ObjectKind == Player`, so the filter already rejects it. The probe returns null.
- `GetBattleNpcTarget` filters on `ObjectKind == BattleNpc`. LocalPlayer is `Player`, not `BattleNpc`. Filter rejects.
- Net: a self-targeted player produces null from both probe methods → `TargetBaseId = null` → correct self-cast inference.

**No code change required.** Pinned by UO_K9 indirectly (no targets set → null TargetBaseId).

### O3 — Race between sequence read and target read

The poller does:
1. Read `_gameProbe.GetLastActionEffect()` (returns `(seq, type, id)`).
2. Read `_targetProbe.GetBattleNpcTarget()` / `GetInteractableNpcTarget()`.

Between steps 1 and 2, the player could have retargeted (e.g. tab-target hit). The captured target would be the new target, not the target at the moment of the action.

**Acceptable in v1.** The frame is ~16 ms; the chance of a meaningful retarget mid-frame is low. If smoke surfaces a real bug ("draft shows wrong target on action immediately followed by tab-target"), we have two fixes:
- Cache the `TargetManager.Target` snapshot inside `GetLastActionEffect` itself (probe-side capture).
- Use `CastInfo.TargetId` (the action's actual target) — rejected in Decision UAI11, but could be revisited.

Defer. Document. Not a release blocker.

### O4 — `ResponseGlobalSequence` wraparound

`uint` counter; even at 60 events per second, wraparound is at ~828 days of continuous activity. Not a practical concern within a session. The baseline reset on session start handles it implicitly.

### O5 — Should `IGameProbe.GetLastActionEffect` also be called by `Inspect` mode (passive trace)?

Currently `UIObserver` calls all every-frame pollers regardless of mode — the `WriteObservation` calls write to the trace per the TraceSession gate. If TraceMode is `Always` or `Authoring`, the `"ActionCompleted"` events appear in the trace even outside Author mode. This is the same posture as `"AethernetTeleportCompleted"` and `"TeleportCompleted"`.

**Decision: yes, fire in Inspect mode too** (matches existing pattern). The aggregator forwarding is gated by `_aggregator is not null` — in Inspect mode there IS an aggregator (`EnterInspectModeCore` calls `SetAggregator(_aggregator, "inspect")`) so the snapshot field updates, but `RecordStep` is not invoked in Inspect mode so the field is never consumed. The Record modal in Inspect mode is read-only anyway.

---

## Implementation order

**Phase A — Mapper + engine surface (15 min, all xUnit-testable)**
1. Task UAI-T1: `ActionTypeMapper.FromFFXIVActionType` in `QuestForge.Adapters`.
2. Tester: write UAI-M1..M5 in `ActionTypeMapperTests`. Make red, implement, green.
3. Task UAI-T2: `ActionCompletedSignal` record + `GameStateSnapshot.ActionCompleted` property.
4. Task UAI-T3: `InferredFrom.ActionCompleted`.
5. Task UAI-T4: `SnapshotAggregator.OnActionCompleted` / `OnActionConsumed` methods + `Current` initializer.
6. Tester: write UAI8..UAI10 (aggregator + snapshot tests). Red, implement, green.

**Phase B — Inference rule (10 min)**
1. Task UAI-T5: insert Rule 3.5 in `StepInferenceEngine` (between Rule 2.6 and Rule 3).
2. Tester: write UAI1..UAI7 (inference tests with priority pinning). Red, implement, green.

**Phase C — StepFactory (5 min)**
1. Task UAI-T6: add `"use-action"` arm to `StepFactory.Build`.
2. Tester: write UAI11, UAI11b. Red, implement, green.

**Phase D — AuthoringHost clearing + diagnostic (2 min)**
1. Task UAI-T7: add `_aggregator.OnActionConsumed();` to `RecordStep` and extend `[QF-DIAG]` line.
2. No new test in this plan (covered structurally by UAI-T4's aggregator tests; the host wiring is a one-line edit verified in-game).

**Phase E — UIObserver tests + impl (30 min)**
1. Task UAI-T8: extend `IGameProbe` and `FakeGameProbe`.
2. Tester: write UO_K1..K10. Red.
3. Task UAI-T9: implement `PollPlayerActionEffect`, `ResetWindowState` updates. Green.
4. Note: tests UO_K8..K10 require a `FakeTargetProbe` that supports `SetBattleNpcTarget` / `SetInteractableNpcTarget`. The fake likely already exists in `QuestForge.Plugin.Tests/Tracing/UIObserverCombatForwardingTests.cs` (per the grep result for `DalamudTargetProbe`); extend if necessary.

**Phase F — Dalamud probe + in-game smoke (BLOCKED on §F O1 verification)**
1. Task UAI-T10: implement `DalamudGameProbe.GetLastActionEffect`.
2. Manual in-game test: enter Inspect mode, perform {auto-attack, combat ability, mount, teleport, key-item-use}, observe the `[QF-DIAG]` log line for each — confirm `ActionCompleted` shows the expected ActionId.
3. If auto-attacks pollute: add the `actionId < 100u` (or similar) filter in `PollPlayerActionEffect`. Document threshold in the source.
4. Enter Author mode for any quest containing a use-action requirement, press the action, open Record modal — confirm the modal shows `use-action` inference with the correct ActionId and TargetNpcId.

---

## Done criteria

1. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~ActionTypeMapperTests` reports all 5 mapper tests green.
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~UseActionInferenceTests` reports all 11 inference/aggregator/factory tests green.
3. `dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverTests&FullyQualifiedName~UO_K"` reports all 10 UIObserver tests green.
4. No regression in existing `StepInferenceEngineTests`, `TeleportInferenceTests`, `AethernetInferenceTests`, `AethernetStepFactoryTests`, or `UIObserverTests` (UO_A–UO_J).
5. The trace stream emitted during a use-action contains an `ObservationEvent` with `Method == "ActionCompleted"`, `Argument == <actionId>`, and value object containing `actionType` and `targetBaseId`.
6. **In-game smoke (after Phase F):** With Author mode enabled for any quest, pressing a combat action produces a draft `UseActionStep { ActionType, ActionId, TargetNpcId }` in the recorded steps. The author edits the `Expect` field; the draft validates.

---

## Exclusions (what this plan does NOT include)

- **Validator rules** for `structural/use-action-*` — deferred to a validator-side PR (per `USE_ACTION_STEP_PLAN.md` Decision UA7).
- **Auto-attack filtering** — flagged in §F O1; if smoke shows auto-attacks pollute, add a simple actionId threshold in the poller. Not gating release.
- **Cast-completion vs cast-cancel discrimination** — `ResponseGlobalSequence` only increments on completion, so cancelled casts produce no event. No special handling needed.
- **Positional / AoE action handling** — UseActionStep schema has no positional / ground-target field; if a quest needs ground-targeted actions, that is a schema extension (`UseActionStep.GroundTarget: Position3?`) outside this plan.
- **Action chaining inference** ("the player pressed actions A then B then C, infer a multi-action step") — out of scope. Each action gets its own draft step.
- **Trace-side extractor** (`qf-trace extract-quest` route for `"ActionCompleted"` events) — Phase 10 follow-up.
- **Re-firing on the same sequence** — the polling state-machine is "increment-edge fires; same value ignored." This means if the player uses the same action twice, only the second use produces an inference (the first one was consumed/cleared on Record). Correct behaviour for the per-window inference model.
- **`CastInfo.TargetId` (the action's actual target)** — explicitly rejected (Decision UAI11). We use `TargetManager.Target` at the time of the read.
- **Pet / minion / chocobo actions** — `BuddyAction` / `PetAction` / `Companion` enum values return null from the mapper. Not authorable in v1.
- **PvP actions** — `PvPAction` (value 14) returns null. Not authorable.
- **`ResponseSpellId`-based deduplication** — we use `ResponseGlobalSequence` exclusively. Two different actions at the same sequence is impossible (per FFXIVClientStructs comment line 28: "monotonously increasing").

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 4 scenarios (UAI1, UAI2, UAI3, UO_K3)
- Edge cases: 9 scenarios (UAI4, UAI5, UAI8, UAI9, UAI10, UAI11, UO_K4, UO_K6, UO_K8, UO_K10)
- Error / no-op cases: 8 scenarios (UAI6, UAI7, UAI11b, UO_K1, UO_K2, UO_K5, UO_K7, UO_K9)
- Priority pinning: 3 scenarios (UAI4, UAI5, UAI6, UAI7) — covers above/below for Rules 1, 2.3, 3, 4.0
- Mapper tests: 5 scenarios (UAI-M1, UAI-M2, UAI-M3, UAI-M4, UAI-M5)
- Expected total: ~26 tests across three files:
  - 5 in `QuestForge.Adapters.Tests/Actions/ActionTypeMapperTests.cs`
  - 11 in `QuestForge.Engine.Tests/Authoring/UseActionInferenceTests.cs`
  - 10 in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (as new `UO_K*` group)

Builder is **fully unblocked for Phases A-E**. Phase F (in-game smoke) requires the `DalamudGameProbe.GetLastActionEffect` impl plus §F O1 verification (does ResponseGlobalSequence increment for the categories we care about?).
