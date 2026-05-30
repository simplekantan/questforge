# UseItemStep Authoring Inference Plan (Slice 5)

**Status:** ready for test creation
**Input docs:**
- `docs/USE_ITEM_STEP_PLAN.md` (engine-side spec; Decision UI21 identified signal research as the FIRST task)
- `docs/USE_ACTION_INFERENCE_PLAN.md` (closest analog: `ActionCompletedSignal`, Rule 3.5, `PollPlayerActionEffect`)
- `docs/USE_EMOTE_INFERENCE_PLAN.md` (second analog: `EmoteCompletedSignal`, Rule 3.5e, `PollPlayerEmote`)
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs` (current rule cascade 1..9 + 2.5/2.6/2.7/3.5s/3.5e/3.5)
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` (`ActionCompletedSignal`, `EmoteCompletedSignal`, `SayChatMessageSentSignal`)
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` (`OnActionCompleted`/`OnActionConsumed`, `OnEmoteCompleted`/`OnEmoteConsumed`, `OnSayChatMessageSent`/`OnSayChatMessageConsumed`)
- `QuestForge.Engine/Authoring/InferredFrom.cs` (17 values; next free: `ItemUsed`)
- `QuestForge.Engine/Authoring/StepFactory.cs` (`"use-action"`, `"use-emote"`, `"say-chat-message"` arms)
- `QuestForge.Plugin.Tracing/UIObserver.cs` (`PollPlayerActionEffect` at line 636, `PollPlayerEmote` at line 674)
- `QuestForge.Plugin.Tracing/IGameProbe.cs` (current interface: 9 methods)
- `QuestForge.Plugin/Tracing/DalamudGameProbe.cs` (`GetLastActionEffect` at line 79)
- `QuestForge.Plugin/Authoring/AuthoringHost.cs` (`RecordStep` consume calls at lines 270-275; `PreviewInference` at line 200)
- `QuestForge.Adapters/Actions/ActionTypeMapper.cs` (maps FFXIV ActionType 1/3/5 to Schema.ActionType; returns null for 2=Item)
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\ActionManager.cs` (ActionType enum: Item=2, EventItem=3)
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\Character\CastInfo.cs` (Response* fields at 0x40-0x4C; TargetLocation at 0x20)
- `QuestForge.Schema/SharedValueTypes.cs` (`ItemKind` enum: KeyItem, InventoryItem; `Position3` record)
- `QuestForge.Schema/Step.cs:165-173` (`UseItemStep { Kind, ItemId, TargetNpcId?, TargetPosition? }`)

**Output (CI behavior):** When the player uses an item (key item or inventory item) during an Author-mode recording session, the snapshot field `ItemUsed` becomes non-null with the `ItemKind`, `ItemId`, optional `TargetBaseId`, and optional `TargetPosition` (for ground-targeted items). `StepInferenceEngine.Infer` returns a `use-item`-typed `InferenceResult` with `SuggestedExpect = null` (author MUST write the postcondition), `Confidence.High`, `InferredFrom.ItemUsed`. Confirming the Record-Step modal records a `UseItemStep { Kind, ItemId, TargetNpcId?, TargetPosition? }` into the draft. CI red to CI green when (a) the snapshot field, (b) the aggregator setter/consumer, (c) the new inference rule, (d) the StepFactory `"use-item"` arm, (e) the UIObserver poller extension, and (f) the `RecordStep` clearing are wired up.

This plan covers **engine-side (xUnit-testable)** wiring and **UIObserver-side polling extension**. The `CastInfo.Response*` read in `DalamudGameProbe` is already implemented (`GetLastActionEffect`); the new work is routing item-type action effects to a separate signal and adding `TargetLocation` to the probe return.

---

## Signal research findings

### What ActionManager/CastInfo exposes for item use

The signal research (Decision UI21 in `USE_ITEM_STEP_PLAN.md`) identified three candidate approaches. Here are the findings:

**Candidate 1: `CastInfo.Response*` fields (via ActionManager's ActionEffect pipeline) -- SELECTED**

`CastInfo` (`CastInfo.cs`) has `Response*` fields set when ActionEffect is received (offset comment at line 22: "fields below (Response*) are set when ActionEffect is received"):

- `ResponseGlobalSequence` (offset 0x4C, `uint`) -- monotonically increasing counter per action effect
- `ResponseActionType` (offset 0x44, `ActionType`) -- the FFXIVClientStructs `ActionType` enum
- `ResponseActionId` (offset 0x48, `uint`) -- the action/item ID

The `ActionType` enum (`ActionManager.cs:395-417`) includes:
- `Item = 2` -- inventory items (potions, food, throwables)
- `EventItem = 3` -- quest key items

Both item types go through `ActionManager.UseAction(ActionType, actionId, ...)` and generate ActionEffect packets that populate `Response*` fields. This means `ResponseGlobalSequence` increments for item use exactly as it does for combat actions.

**The existing `PollPlayerActionEffect` already reads these fields.** It calls `ActionTypeMapper.FromFFXIVActionType(ffxivActionType)` which:
- Maps 1 (Action) to `Schema.ActionType.Action` -- routed to `ActionCompletedSignal`
- Maps 3 (EventItem) to `Schema.ActionType.KeyItem` -- routed to `ActionCompletedSignal` (currently produces a `use-action` inference, not `use-item`)
- Maps 5 (GeneralAction) to `Schema.ActionType.GeneralAction` -- routed to `ActionCompletedSignal`
- Returns null for 2 (Item) -- **currently SKIPPED**

This is the pivotal discovery: key item use (EventItem=3) already triggers the ActionCompleted signal but is routed to `use-action` inference (Rule 3.5). Inventory item use (Item=2) is silently dropped.

**Candidate 2: Snapshot delta (`KeyItemsRemoved` / `InventoryItemDelta`) -- REJECTED**

`KeyItemsRemoved` is already plumbed (Rule 2.4 fires `hand-over-item`). Using it for use-item would conflict: the same key-item removal that happens during a hand-over also happens during some key-item uses (the item is consumed). Disambiguating would require knowing whether the removal was caused by an NPC dialogue (hand-over) or by `ActionManager.UseAction(EventItem, ...)` (use-item). The CastInfo signal directly captures the ActionManager call, making it strictly more specific.

For inventory items, no `InventoryItemDelta` signal exists today. Building one would require polling InventoryManager for regular inventory changes (expensive; high noise from quest rewards, vendor purchases, gear changes). The CastInfo signal is cheaper and more targeted.

**Candidate 3: ActionManager.LastUsedActionSequence -- NOT USABLE**

`ActionManager.LastUsedActionSequence` (offset 0x120, `ushort`) tracks the most recent `UseAction` call's sequence number. However, this is client-side (pre-server-response) and does NOT carry the ActionType or ActionId of what was used. The `CastInfo.Response*` fields are strictly superior: they're set when the server confirms the action landed, they carry the full ActionType + ActionId, and they use a `uint` counter (wider than the `ushort`).

### Chosen signal

**`CastInfo.ResponseGlobalSequence` + `ResponseActionType` + `ResponseActionId`** -- the same fields already read by `DalamudGameProbe.GetLastActionEffect()`. The change is in the UIObserver poller: instead of passing all action effects through `ActionTypeMapper.FromFFXIVActionType` (which drops Item=2 and routes EventItem=3 to ActionCompleted), the poller must first check whether the ActionType is an item type (2 or 3) and route to a new `ItemUsedSignal` / `OnItemUsed` path before falling through to the existing ActionCompleted path.

Additionally, `GetLastActionEffect()` gains a fourth return field: `TargetLocation` (see Decision UI-INF-16).

### AoE ground-position capture via `CastInfo.TargetLocation`

`CastInfo.TargetLocation` (offset 0x20, `Vector3`) carries the ground-target position for AoE-targeted actions and item uses. The previous version of this plan declared reading this field at ActionEffect time as "unreliable" -- that was incorrect.

**Corrected finding:** `CastInfo` is a single 0x170-byte struct. `TargetLocation` lives at offset 0x20; the `Response*` fields (`ResponseActionType`, `ResponseActionId`, `ResponseGlobalSequence`) start at offset 0x40. These are separate field regions within the same struct -- they coexist and do NOT overwrite each other. When `ResponseGlobalSequence` increments (ActionEffect received from the server), `TargetLocation` from that same cast is still populated in the struct. The existing `DalamudGameProbe.GetLastActionEffect()` already reads from `bc->CastInfo` at line 85 -- adding `castInfo.TargetLocation` is one additional field read from the same struct reference.

**Disambiguation rule for ground-targeted vs non-ground use:** for ground-targeted item use (e.g., placing an AoE marker on terrain), `TargetLocation` carries the `(X, Y, Z)` world position the player targeted. For non-ground item use (e.g., using a key item on an NPC, using a potion on self), `TargetLocation` is `(0, 0, 0)`. The all-zeros sentinel is reliable because no valid world coordinate in FFXIV is exactly `(0, 0, 0)` -- the game world has non-zero vertical offsets everywhere.

**Implementation:** `GetLastActionEffect()` returns the `TargetLocation` `Vector3` as three additional floats. The UIObserver converts the `Vector3` to `Position3?`: if any component is non-zero, construct `new Position3(X, Y, Z)`; if all three are zero, yield `null`. This null-vs-populated distinction flows through `ItemUsedSignal.TargetPosition` and into `StepFactory`'s `"use-item"` arm, populating `UseItemStep.TargetPosition` automatically.

---

## Dependency graph

```
QuestForge.Adapters/Items
  +-- ItemKindMapper (NEW: pure helper -- uint -> Schema.ItemKind?)
        | used by |
QuestForge.Engine.Authoring
  |-- GameStateSnapshot           (add ItemUsed property + ItemUsedSignal record)
  |-- InferredFrom (enum)         (add ItemUsed)
  |-- SnapshotAggregator          (add OnItemUsed / OnItemUsedConsumed)
  +-- StepInferenceEngine         (add Rule 3.5i -- ItemUsed, fires ABOVE Rule 3.5e EmoteCompleted)
        |
QuestForge.Engine.Authoring.StepFactory  (route stepType="use-item" -> UseItemStep)
        |
QuestForge.Plugin.Authoring.AuthoringHost.RecordStep  (clear ItemUsed after consume)
        |
QuestForge.Plugin.Tracing.UIObserver.PollPlayerActionEffect  (extend: route Item/EventItem to OnItemUsed)
        |
QuestForge.Plugin.Tracing.IGameProbe  (extend GetLastActionEffect return with TargetLocation)
        |
QuestForge.Plugin/Tracing/DalamudGameProbe  (read castInfo.TargetLocation alongside Response* fields)
```

**Build order:**
1. Pure mapper in `QuestForge.Adapters/Items/ItemKindMapper.cs` (testable in `QuestForge.Adapters.Tests`).
2. `ItemUsedSignal` record + `GameStateSnapshot.ItemUsed` property + `InferredFrom.ItemUsed` enum value -- engine surface, no Dalamud.
3. `SnapshotAggregator.OnItemUsed` / `OnItemUsedConsumed` -- engine surface.
4. `StepInferenceEngine` Rule 3.5i -- engine surface.
5. `StepFactory.Build` `"use-item"` arm -- engine surface.
6. `AuthoringHost.RecordStep` consume call -- one-line edit.
7. Extend `IGameProbe.GetLastActionEffect()` return type to include `TargetLocation` (3 floats).
8. Extend `DalamudGameProbe.GetLastActionEffect()` to read `castInfo.TargetLocation`.
9. Extend `FakeGameProbe.SetLastActionEffect()` to accept target location.
10. Extend `PollPlayerActionEffect` to route item ActionTypes to `OnItemUsed` (with `TargetPosition`) instead of skipping/misrouting.
11. `UIObserver.ResetWindowState` adds `_aggregator?.OnItemUsedConsumed();`.

Steps 1-5 are pure xUnit. Step 6 is a one-line plugin edit. Steps 7-11 are testable in `QuestForge.Plugin.Tests` against `FakeGameProbe`.

---

## Architectural decisions (read before coding)

### Decision UI-INF-1 -- Extend `GetLastActionEffect()` probe return with `TargetLocation`; no new IGameProbe method

The existing `IGameProbe.GetLastActionEffect()` returns `(uint Sequence, uint FfxivActionType, uint ActionId)?`. This must be extended to `(uint Sequence, uint FfxivActionType, uint ActionId, float TargetLocationX, float TargetLocationY, float TargetLocationZ)?` to carry the `CastInfo.TargetLocation` vector.

**Why extend the existing method instead of adding a new one:** `TargetLocation` (offset 0x20) and `Response*` fields (offset 0x40+) are read from the same `CastInfo` struct reference. Reading them in one call guarantees they correspond to the same cast. A separate probe method would require a second `bc->CastInfo` dereference, risking a stale value if the struct is updated between the two reads (the sequence incremented between read-1 and read-2).

**Why three floats instead of `System.Numerics.Vector3`:** `IGameProbe` lives in the plugin layer, not the schema layer. `Position3` is a schema type. Passing raw floats at the probe boundary keeps the interface dependency-free; the UIObserver converts `(float, float, float)` to `Position3?` at the consumption site.

**Backward compatibility:** all existing callers destructure the 3-tuple `(sequence, ffxivActionType, actionId)`. Extending to a 6-tuple requires updating all destructuring sites. There are exactly two: `UIObserver.PollPlayerActionEffect` (modified in this plan) and the existing tests (updated in Decision UI-INF-14). No other callers exist.

**Rejected alternative: new `IGameProbe.GetLastItemUsed()` method.** Would duplicate the exact same `CastInfo` read. The probe already returns the ActionType byte; the caller should discriminate, not the probe.

**What breaks if violated:** adding a separate probe method means two concurrent reads of the same `CastInfo` fields, with potential for one to see a stale value.

### Decision UI-INF-2 -- Pure mapper `ItemKindMapper.FromFFXIVActionType(uint) -> Schema.ItemKind?`

A new static helper in `QuestForge.Adapters/Items/ItemKindMapper.cs`. Maps the FFXIVClientStructs ActionType byte to the schema-side `ItemKind` enum:

```csharp
// QuestForge.Adapters/Items/ItemKindMapper.cs
namespace QuestForge.Adapters.Items;

using QuestForge.Schema;

/// <summary>
/// Pure reverse-mapping helper: converts the FFXIVClientStructs ActionType byte (as uint)
/// into the schema-side ItemKind enum. Returns null for any non-item ActionType.
///
/// CANONICAL VALUES (verified against FFXIVClientStructs ActionManager.cs:395-417):
///   2 = Item       -> Schema.ItemKind.InventoryItem  (potions, food, throwables)
///   3 = EventItem  -> Schema.ItemKind.KeyItem        (quest key items used as actions)
///
/// All other values return null -- they are not items.
///
/// RELATIONSHIP TO ActionTypeMapper: ActionTypeMapper maps 1->Action, 3->KeyItem, 5->GeneralAction.
/// ItemKindMapper maps 2->InventoryItem, 3->KeyItem. Value 3 (EventItem) is mapped by BOTH:
///   - ActionTypeMapper routes it to ActionCompleted (use-action inference)
///   - ItemKindMapper routes it to ItemUsed (use-item inference)
/// The UIObserver poller MUST check ItemKindMapper FIRST so item use is routed to ItemUsed,
/// not ActionCompleted. This is the priority mechanism (Decision UI-INF-6).
/// </summary>
public static class ItemKindMapper
{
    public static ItemKind? FromFFXIVActionType(uint ffxivActionType) => ffxivActionType switch
    {
        2u => ItemKind.InventoryItem,  // FFXIVClientStructs "Item"
        3u => ItemKind.KeyItem,        // FFXIVClientStructs "EventItem"
        _  => null,
    };
}
```

**Why a separate class from `ActionTypeMapper`:** the two mappers answer different questions. `ActionTypeMapper` answers "is this a use-action step?" `ItemKindMapper` answers "is this a use-item step?" The fact that EventItem=3 appears in both is the precise overlap that Decision UI-INF-6 resolves via priority ordering in the poller.

**Why `uint` input:** matches `ActionTypeMapper` convention. The FFXIVClientStructs enum is `uint`-typed; `CastInfo.ResponseActionType` is stored as a byte but widened at the probe boundary.

**What breaks if violated:** if `ItemKindMapper` is merged into `ActionTypeMapper`, the single class would need to return a union type (ActionType | ItemKind | null) which conflates two orthogonal classification schemes.

### Decision UI-INF-3 -- Snapshot field is `ItemUsedSignal?`, a record carrying Kind + ItemId + TargetBaseId + TargetPosition

```csharp
// QuestForge.Engine/Authoring/GameStateSnapshot.cs -- appended near SayChatMessageSent

/// Records that the player used an item (key item or inventory item) during this recording
/// window. Set by SnapshotAggregator.OnItemUsed, which is driven by
/// UIObserver.PollPlayerActionEffect reading CastInfo.Response* fields and routing
/// ActionType.Item (2) and ActionType.EventItem (3) to this signal. Cleared by
/// OnItemUsedConsumed (called from RecordStep) so it does not bleed into the next window.
///
/// Kind discriminates KeyItem vs InventoryItem (maps to the Dalamud-side ActionType choice).
/// ItemId is the EventItem or Item row id (Lumina sheet row).
/// TargetBaseId is the BNpcBase / ENpcBase row id of the item's target
/// (null = self-cast / no target / target not in ObjectTable).
/// TargetPosition is the ground-target world position for AoE-targeted item use
/// (null = non-ground-targeted / all CastInfo.TargetLocation components are zero).
public sealed record ItemUsedSignal(
    QuestForge.Schema.ItemKind Kind,
    uint ItemId,
    uint? TargetBaseId,
    QuestForge.Schema.Position3? TargetPosition);

public ItemUsedSignal? ItemUsed { get; init; }
```

**Why separate from `ActionCompletedSignal`:** the two signals route to different inference rules (Rule 3.5 for ActionCompleted, Rule 3.5i for ItemUsed) and produce different step types (`use-action` vs `use-item`). Overloading `ActionCompletedSignal` with a union ActionType/ItemKind field would force every ActionCompleted consumer to check "is this really an action or an item?" -- leaking the item concern into the action path.

**Why `ItemKind` not `uint ffxivActionType`:** the signal lives in the engine layer which must never reference FFXIVClientStructs types. `ItemKind` (from `QuestForge.Schema`) is the schema-side enum. The mapping happens at the UIObserver boundary (Decision UI-INF-6).

**Why `Position3?` not raw floats:** the signal is an engine-layer type consumed by `StepFactory`. `Position3` is a schema type (`QuestForge.Schema.SharedValueTypes`), so it can flow directly into `UseItemStep.TargetPosition` without conversion. The raw-float-to-`Position3?` conversion happens once at the UIObserver boundary.

**What breaks if violated:** if `ActionCompletedSignal` is reused, Rule 3.5 (ActionCompleted) would fire for item use and produce `use-action` steps with `ActionType.KeyItem` -- which is exactly the current behavior for EventItem=3, and it's WRONG. The whole point of this plan is to route item use to `use-item` inference.

### Decision UI-INF-4 -- Aggregator setter/consumer mirror the ActionCompleted pair

```csharp
// QuestForge.Engine/Authoring/SnapshotAggregator.cs
private ItemUsedSignal? _itemUsed;

// In the Current property's object-initializer block (alongside SayChatMessageSent):
ItemUsed = _itemUsed,

/// Called by UIObserver.PollPlayerActionEffect when a new ResponseGlobalSequence is observed
/// AND the ResponseActionType is Item (2) or EventItem (3). Records the item kind, id,
/// optional target, and optional ground-target position. Survives ResetDeltas; cleared by
/// OnItemUsedConsumed.
/// Does NOT update LastNpcInteracted or any other unrelated field (mirrors Decision UAI3).
public void OnItemUsed(QuestForge.Schema.ItemKind kind, uint itemId, uint? targetBaseId,
                       QuestForge.Schema.Position3? targetPosition)
    => _itemUsed = new ItemUsedSignal(kind, itemId, targetBaseId, targetPosition);

/// Called at the end of RecordStep (and from UIObserver.ResetWindowState) to consume
/// the item-used signal. Mirrors OnActionConsumed exactly.
public void OnItemUsedConsumed() => _itemUsed = null;
```

**Why no side effects on other state:** an item's target NPC is NOT the same as "the player interacted with that NPC for dialogue/quest purposes." Setting `LastNpcInteracted` from `OnItemUsed` would cause spurious Rule 7 (NpcInteracted-changed) talk-step inference.

### Decision UI-INF-5 -- New inference rule "Rule 3.5i -- ItemUsed" fires ABOVE Rule 3.5e (EmoteCompleted) and Rule 3.5 (ActionCompleted)

**Priority placement:**

The current cascade (post-SayChatMessage inference):
```
... -> 2.6: InventoryHash -> 3.5s: SayChatMessageSent -> 3.5e: EmoteCompleted -> 3.5: ActionCompleted -> 3: QuestSequence -> ...
```

Insert Rule 3.5i **between Rule 3.5s (SayChatMessageSent) and Rule 3.5e (EmoteCompleted)**.

**Why ABOVE Rule 3.5e (EmoteCompleted):** item use and emote use are mutually exclusive in practice (items go through ActionManager; emotes go through chat commands). If both fire in the same window (defensive), item use is the more specific authoring intent -- using an item on a quest NPC is always deliberate, while an emote could be incidental (/sit while waiting for the item effect).

**Why ABOVE Rule 3.5 (ActionCompleted):** this is the KEY priority decision. EventItem=3 currently routes to BOTH `ActionTypeMapper` (returning `ActionType.KeyItem`) AND `ItemKindMapper` (returning `ItemKind.KeyItem`). The poller resolves this by checking `ItemKindMapper` first (Decision UI-INF-6), so `ActionCompleted` is never set for item use. But defensively, if both signals were somehow set, item use should win (it's more specific -- the item identity is the author's intent, not "an action was used").

**Why ABOVE Rule 3 (QuestSequence advanced):** identical reasoning to UAI4. An item use that advances the quest sequence is the most common authoring case for `UseItemStep`. Firing Rule 3 first would draft a `talk` step.

**Why BELOW Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6, 3.5s:** those represent distinct authoring intents where item use is incidental.

**CRITICAL: Why BELOW Rule 2.4 (KeyItemsRemoved):** when a key item is used AND consumed (removed from inventory), Rule 2.4 fires for the removal. This is correct: `hand-over-item` is the appropriate step type when item removal is the observable game state change. The `use-item` inference fires only when the item is USED but NOT removed (the common case: key items that trigger an effect but remain in inventory). If the key item IS removed, Rule 2.4 fires first and short-circuits; Rule 3.5i never runs. This is the natural disambiguation.

```csharp
// Rule 3.5i -- ItemUsed
// Fires when UIObserver.PollPlayerActionEffect detected that the player used an item
// (ActionType.Item or ActionType.EventItem) during this recording window.
//
// PRIORITY: above Rule 3.5e (EmoteCompleted) and Rule 3.5 (ActionCompleted).
// Item use is more specific than emote/action when both fire in the same window.
// PRIORITY: above Rule 3 (QuestSequence advanced) -- use-item is more specific than
// the catch-all sequence-advance which defaults to "talk".
// PRIORITY: below Rule 2.4 (KeyItemsRemoved) -- when a key item is used AND consumed,
// the removal signal fires hand-over-item instead (natural disambiguation).
// PRIORITY: below Rule 3.5s (SayChatMessageSent) -- chat is always intentional.
//
// CONFIDENCE: High -- the player demonstrably used the item (ActionEffect received).
// EXPECT: null -- author MUST write the postcondition (no universal item postcondition).
// TARGETPOSITION: populated from CastInfo.TargetLocation when any component is non-zero
// (ground-targeted item use); null for non-ground use (all components zero).
if (after.ItemUsed is { } itemSignal)
{
    var stepIdSuffix = itemSignal.TargetBaseId is { } tid
        ? $"{itemSignal.ItemId}-on-{tid}"
        : $"{itemSignal.ItemId}";
    return new InferenceResult(
        StepType:        "use-item",
        SuggestedStepId: $"use-item-{stepIdSuffix}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.ItemUsed,
        Notes:           $"Author MUST write the Expect predicate (no universal item postcondition). Kind={itemSignal.Kind}, ItemId={itemSignal.ItemId}");
}
```

**Why `SuggestedExpect = null`:** identical reasoning to UAI4 and UEI4. Null is honest; synthesizing a placeholder would be a lie.

**Why the `Notes` string includes `Kind=` and `ItemId=`:** the Record-Step modal surfaces `Notes` in the UI. Telling the author which kind and item id was detected helps them write the Expect predicate.

### Decision UI-INF-6 -- UIObserver `PollPlayerActionEffect` gains item-first routing with TargetPosition

The existing `PollPlayerActionEffect` currently calls `ActionTypeMapper.FromFFXIVActionType(ffxivActionType)` for all observed sequences. This maps EventItem=3 to `ActionType.KeyItem` and routes to `OnActionCompleted`.

**The fix:** insert an `ItemKindMapper.FromFFXIVActionType(ffxivActionType)` check BEFORE the existing `ActionTypeMapper` call. If the result is non-null, route to `OnItemUsed` (with `TargetPosition` derived from the probe's `TargetLocation` floats) and return. Otherwise fall through to the existing ActionCompleted path.

```csharp
private void PollPlayerActionEffect()
{
    if (_gameProbe is null) return;

    var probe = _gameProbe.GetLastActionEffect();
    if (probe is null) return;
    var (sequence, ffxivActionType, actionId, tlX, tlY, tlZ) = probe.Value;

    if (_lastObservedActionSequence is null)
    {
        _lastObservedActionSequence = sequence;
        return;
    }

    if (sequence == _lastObservedActionSequence) return;

    _lastObservedActionSequence = sequence;

    // NEW: check for item use FIRST. Item ActionTypes (2=Item, 3=EventItem) route to
    // ItemUsed signal, not ActionCompleted. This is critical because EventItem=3 was
    // previously mapped by ActionTypeMapper to ActionType.KeyItem and misrouted to
    // ActionCompleted (producing a use-action step instead of use-item).
    var itemKind = QuestForge.Adapters.Items.ItemKindMapper.FromFFXIVActionType(ffxivActionType);
    if (itemKind is not null)
    {
        uint? targetBaseId = CaptureTargetBaseId();
        // Convert CastInfo.TargetLocation to Position3? -- non-zero = ground target.
        QuestForge.Schema.Position3? targetPosition =
            (tlX != 0f || tlY != 0f || tlZ != 0f)
                ? new QuestForge.Schema.Position3(tlX, tlY, tlZ)
                : null;
        var now   = _clock.UtcNow;
        var runId = CurrentRunId;
        WriteObservation("ItemUsed",
            actionId,
            new { kind = (int)itemKind.Value, targetBaseId = targetBaseId ?? 0u },
            runId, now);
        _aggregator?.OnItemUsed(itemKind.Value, actionId, targetBaseId, targetPosition);
        return;   // Do NOT fall through to ActionCompleted
    }

    // Existing ActionCompleted path (unchanged)
    var schemaType = QuestForge.Adapters.Actions.ActionTypeMapper.FromFFXIVActionType(ffxivActionType);
    if (schemaType is null) return;

    {
        uint? targetBaseId = CaptureTargetBaseId();
        var now   = _clock.UtcNow;
        var runId = CurrentRunId;
        WriteObservation("ActionCompleted",
            actionId,
            new { actionType = (int)schemaType.Value, targetBaseId = targetBaseId ?? 0u },
            runId, now);
        _aggregator?.OnActionCompleted(schemaType.Value, actionId, targetBaseId);
    }
}
```

**Extract `CaptureTargetBaseId()` helper:** the target-reading logic (check hostile BattleNpc first, then interactable EventNpc, else null) is now called from two branches. Extract it into a private helper to avoid duplication:

```csharp
private uint? CaptureTargetBaseId()
{
    uint? targetBaseId = null;
    var hostile      = _targetProbe?.GetBattleNpcTarget();
    var interactable = _targetProbe?.GetInteractableNpcTarget();
    if (hostile is { } h)
        targetBaseId = h.BaseId;
    else if (interactable is { } i)
        targetBaseId = i.BaseId;
    return targetBaseId;
}
```

**Why item-first, not action-first:** EventItem=3 is currently mapped by BOTH `ItemKindMapper` (to `ItemKind.KeyItem`) and `ActionTypeMapper` (to `ActionType.KeyItem`). The priority must be item-first because the step type for quest key items is `use-item`, not `use-action`. Decision UA8 in `USE_ACTION_STEP_PLAN.md` explicitly says: "UseActionStep.ActionType never carries Item. Inventory items go through UseItemStep."

**BREAKING CHANGE to existing behavior:** previously, using a key item during authoring produced an `ActionCompleted` signal with `ActionType.KeyItem`, which the inference engine routed to Rule 3.5 and produced a `use-action` step. After this change, it produces an `ItemUsed` signal routed to Rule 3.5i producing a `use-item` step. This is intentional and correct -- key item use should always infer as `use-item`, not `use-action`.

**Impact on existing ActionCompleted tests (UO_K*):** the `UO_K*` tests in `UIObserverTests.cs` test `PollPlayerActionEffect` with `ffxivActionType: 1u` (Action) and `ffxivActionType: 5u` (GeneralAction). These are unaffected because `ItemKindMapper.FromFFXIVActionType(1u)` and `(5u)` both return null, so the existing ActionCompleted path fires. The affected tests are `UO_K5` (which uses `ffxivActionType: 2u` and expects "no event" -- Item=2 now produces an ItemUsed event) and any test using `ffxivActionType: 3u`. See Decision UI-INF-14.

**What breaks if violated:** if ActionTypeMapper is checked first (existing order), EventItem=3 routes to ActionCompleted and produces `use-action` steps for key items -- the exact bug this plan fixes.

### Decision UI-INF-7 -- `InferredFrom.ItemUsed` is a new enum value

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
    ActionCompleted,
    EmoteCompleted,
    SayChatMessageSent,
    ItemUsed,    // NEW
}
```

### Decision UI-INF-8 -- `StepFactory.Build` gains a `"use-item"` arm

```csharp
"use-item" => new UseItemStep
{
    Id = stepId,
    Expect = expectValue,
    Kind = after?.ItemUsed?.Kind ?? QuestForge.Schema.ItemKind.KeyItem,
    ItemId = after?.ItemUsed?.ItemId ?? 0u,
    TargetNpcId = after?.ItemUsed?.TargetBaseId,
    TargetPosition = after?.ItemUsed?.TargetPosition,
},
```

**Why `Kind` defaults to `KeyItem`:** defensive fallback when `ItemUsed` is null (should never happen -- inference engine only returns "use-item" when the field is set). KeyItem is more common in quest scenarios than InventoryItem; the validator catches `ItemId == 0` (E13) regardless.

**Why `TargetPosition` flows from the signal:** `ItemUsedSignal.TargetPosition` is populated by the UIObserver from `CastInfo.TargetLocation` (non-zero = ground target, zero = null). `StepFactory` passes it through directly. No manual authoring required for ground-targeted items.

### Decision UI-INF-9 -- `AuthoringHost.RecordStep` clears `ItemUsed` after consuming

After the existing six consume calls in `RecordStep` (lines 270-275):

```csharp
_aggregator.OnAethernetTeleportConsumed();
_aggregator.OnDialogueOptionConsumed();
_aggregator.OnTeleportConsumed();
_aggregator.OnActionConsumed();
_aggregator.OnEmoteConsumed();
_aggregator.OnSayChatMessageConsumed();
_aggregator.OnItemUsedConsumed();   // NEW
```

`UIObserver.ResetWindowState` also calls `_aggregator?.OnItemUsedConsumed();` for symmetry.

### Decision UI-INF-10 -- Diagnostic log line extension

Extend the `[QF-DIAG] PreviewInference:` line with `ItemUsed`:

```csharp
$"ItemUsed={after.ItemUsed?.ItemId} " +     // NEW -- appended after SayChatMessageSent
```

**Why only the ItemId:** the diagnostic line is single-line; adding Kind+Id+Target would push it past readable width. The ItemId is the most diagnostic single value (maps to Lumina EventItem/Item rows).

### Decision UI-INF-11 -- Trace observation event method is `"ItemUsed"`

```csharp
WriteObservation("ItemUsed",
    actionId,
    new { kind = (int)itemKind.Value, targetBaseId = targetBaseId ?? 0u },
    runId, now);
```

- `argument` carries the item id (the identifying primitive).
- `value` is a structured object with `kind` (schema-side ItemKind int) and `targetBaseId` (0 = no target / self-cast).

### Decision UI-INF-12 -- `FakeGameProbe` extension: add `TargetLocation` to `SetLastActionEffect`

The existing `FakeGameProbe.SetLastActionEffect(uint sequence, uint ffxivActionType, uint actionId)` must be extended with three float parameters for `TargetLocation`:

```csharp
public void SetLastActionEffect(uint sequence, uint ffxivActionType, uint actionId,
                                float targetLocationX = 0f, float targetLocationY = 0f,
                                float targetLocationZ = 0f)
```

The three floats default to `0f` so all existing callers (which pass only 3 args) continue to compile and run without changes -- they get `(0,0,0)` which maps to `TargetPosition = null` (non-ground use).

**Why default parameters instead of an overload:** an overload `SetLastActionEffect(uint, uint, uint)` calling `SetLastActionEffect(uint, uint, uint, 0f, 0f, 0f)` would work but adds a forwarding layer for no benefit. Default parameters are simpler and the fake is test-only code.

The `GetLastActionEffect()` return type changes to `(uint Sequence, uint FfxivActionType, uint ActionId, float TargetLocationX, float TargetLocationY, float TargetLocationZ)?` matching the updated `IGameProbe` signature.

### Decision UI-INF-13 -- `ActionTypeMapper` must stop mapping EventItem=3

Currently `ActionTypeMapper.FromFFXIVActionType(3u)` returns `ActionType.KeyItem`. With the new item-first routing (Decision UI-INF-6), this branch is never reached in production (ItemKindMapper returns non-null for value 3, and the poller returns before calling ActionTypeMapper). However, leaving the mapping in `ActionTypeMapper` is misleading and creates a latent bug if someone calls it directly.

**Decision: REMOVE the `3u => ActionType.KeyItem` mapping from `ActionTypeMapper`.** Change it to:

```csharp
public static ActionType? FromFFXIVActionType(uint ffxivActionType) => ffxivActionType switch
{
    1u => ActionType.Action,
    5u => ActionType.GeneralAction,
    _  => null,                       // EventItem (3) now routed by ItemKindMapper
};
```

**Why not keep both:** if someone calls `ActionTypeMapper.FromFFXIVActionType(3u)` directly (e.g., in a future trace-replay path), they'd get `ActionType.KeyItem` and produce a `use-action` step with `ActionType.KeyItem` -- incorrect (should be `use-item`). Removing the mapping makes the error obvious (null return = "this isn't an action type") rather than silently producing the wrong step type.

**Impact on existing tests:** `UAI-M3` in `ActionTypeMapperTests.cs` tests `FromFFXIVActionType(3u) -> ActionType.KeyItem`. This test must be updated to expect `null`. New `UAI-M3` asserts `FromFFXIVActionType(3u) == null`.

**Impact on existing UIObserver tests:** `UO_K*` tests that use `ffxivActionType: 3u` (EventItem) now produce an `ItemUsed` signal instead of `ActionCompleted`. See Decision UI-INF-14.

### Decision UI-INF-14 -- Existing test updates required

This plan changes behavior for EventItem=3 routing and adds a `TargetLocation` field to `GetLastActionEffect`. The following existing tests must be updated:

1. **`ActionTypeMapperTests.UAI-M3`** (`FromFFXIVActionType_EventItemValue3_MapsToKeyItem`): change expected result from `ActionType.KeyItem` to `null`. Rename to `FromFFXIVActionType_EventItemValue3_ReturnsNull_RoutedByItemKindMapper`.

2. **`UO_K5`** (`UnsupportedActionType_NoEvent_SequenceStillAdvances`): this test uses `ffxivActionType: 2u` (Item) and expects "no event." With item-first routing, Item=2 now produces an `ItemUsed` event. The test must be updated: tick 2 (seq=101, type=2) now produces an `"ItemUsed"` observation (not "no event"), and tick 3 (seq=102, type=1) produces an `"ActionCompleted"` observation. The critical invariant that the sequence advances past 101 is preserved (the item-first branch still sets `_lastObservedActionSequence = sequence` before routing). Rename to `UO_K5_ItemActionType_ProducesItemUsedEvent_SequenceAdvances`.

3. **Any other `UO_K*` tests that use `ffxivActionType: 3u`:** if any exist, they must be updated to expect `ItemUsed` signal instead of `ActionCompleted`. Tester should grep `UO_K` for `3u` usage.

4. **All existing `SetLastActionEffect` call sites in `UIObserverTests.cs`:** the method signature gains three optional float parameters (defaulting to 0f). Because these are default parameters, existing callers do NOT need source changes -- they compile as-is. However, the `GetLastActionEffect()` return type changes from a 3-tuple to a 6-tuple, so any tests that destructure the return value explicitly (rather than going through `FakeGameProbe`) must update their destructuring. In practice, no tests call `GetLastActionEffect()` directly -- they go through the UIObserver polling loop which is updated in this plan.

5. **`UseActionInferenceTests` tests that use `ActionType.KeyItem`:** if any snapshot-level inference tests set `ActionCompleted` with `ActionType.KeyItem`, they remain valid (the inference engine still routes `ActionCompleted` to Rule 3.5 regardless of the ActionType value). The change is upstream in the poller, not the inference engine.

### Decision UI-INF-15 -- No re-record cascade impact (routing change within existing observation)

The `GetLastActionEffect` probe call frequency and the `"ActionCompleted"` observation continue to fire for ActionType values 1 and 5. The new `"ItemUsed"` observation fires for values 2 and 3. No existing trace fixtures capture item use (the poller previously skipped value 2 and misrouted value 3). New fixtures will contain `"ItemUsed"` events; no existing fixture is re-recorded.

### Decision UI-INF-16 -- `DalamudGameProbe.GetLastActionEffect()` reads `CastInfo.TargetLocation`

The existing implementation at line 79 reads `bc->CastInfo` and returns `(ResponseGlobalSequence, ResponseActionType, ResponseActionId)`. The change is minimal:

```csharp
public (uint Sequence, uint FfxivActionType, uint ActionId,
        float TargetLocationX, float TargetLocationY, float TargetLocationZ)? GetLastActionEffect()
{
    var player = _objectTable.LocalPlayer;
    if (player is null) return null;
    var bc = (BattleChara*)player.Address;
    if (bc is null) return null;
    ref var castInfo = ref bc->CastInfo;
    return (castInfo.ResponseGlobalSequence,
            (uint)castInfo.ResponseActionType,
            castInfo.ResponseActionId,
            castInfo.TargetLocation.X,
            castInfo.TargetLocation.Y,
            castInfo.TargetLocation.Z);
}
```

`CastInfo.TargetLocation` is at offset 0x20 (`Vector3`). The `Response*` fields start at offset 0x40. They are independent field regions within the same 0x170-byte struct -- reading one does not affect the other. The `TargetLocation` from the cast that triggered the ActionEffect is still populated when `ResponseGlobalSequence` increments.

**Why this is safe:** the FFXIVClientStructs `CastInfo` struct layout (verified in `CastInfo.cs`) places `TargetLocation` in the cast-parameter block (0x00-0x3C) and `Response*` in the action-effect block (0x40-0x58). The cast-parameter block is written when `IsCasting` transitions true (cast starts). The response block is written when ActionEffect is received from the server (cast confirms). Between cast-start and action-effect, neither block overwrites the other. By the time our poller reads (triggered by `ResponseGlobalSequence` changing), both blocks contain data from the same cast.

---

## Snapshot field summary

| Field (new or existing) | Type | Set by | Cleared by | Survives ResetDeltas? |
|---|---|---|---|---|
| `ItemUsed` (NEW) | `ItemUsedSignal?` | `OnItemUsed` | `OnItemUsedConsumed` | yes |
| `ActionCompleted` (existing) | `ActionCompletedSignal?` | `OnActionCompleted` | `OnActionConsumed` | yes |
| `EmoteCompleted` (existing) | `EmoteCompletedSignal?` | `OnEmoteCompleted` | `OnEmoteConsumed` | yes |
| `SayChatMessageSent` (existing) | `SayChatMessageSentSignal?` | `OnSayChatMessageSent` | `OnSayChatMessageConsumed` | yes |

---

## Inference rule table -- updated

| Rule | Trigger condition | Step type | InferredFrom | Confidence |
|---|---|---|---|---|
| 1 | QuestCompleted false->true | turn-in | QuestCompleted | High |
| 2 | QuestAccepted false->true | accept | QuestAccepted | High |
| 2.1 | ForeignQuestAccepted set | accept | QuestAccepted | High |
| 2.2 | KillCorrelatedTargets non-empty | combat | Combat | Med/Low |
| 2.2b | PurchaseDetected with item delta + currency drop | purchase-item | Purchase | Med/Low |
| 2.3 | KeyItemsAdded non-empty | pickup-item | DialogueInteraction | Medium |
| 2.4 | KeyItemsRemoved non-empty | hand-over-item | DialogueInteraction | Medium |
| 2.5 | LastAethernetShardInteracted changed, same zone | attune | AttunementChange | High |
| 2.6 | InventoryHash changed, KeyItems diff non-empty | pickup/handover/talk | InventoryChange | Medium |
| 3.5s | after.SayChatMessageSent set | say-chat-message | SayChatMessageSent | High |
| **3.5i (NEW)** | **after.ItemUsed set** | **use-item** | **ItemUsed** | **High** |
| 3.5e | after.EmoteCompleted set | use-emote | EmoteCompleted | High |
| 3.5 | after.ActionCompleted set | use-action | ActionCompleted | High |
| 3 | QuestSequence advanced | talk | QuestSequenceChange | High |
| 4.0 | TeleportCompleted set AND zone changed | teleport | TeleportCompleted | High |
| 4 | Zone changed | travel | ZoneChange | High |
| 2.7 | AethernetTeleportCompleted set, same zone | travel | ZoneChange | High |
| 5 | QuestFlags changed, sequence unchanged | talk | QuestFlagChange | Medium |
| 6 | LastDialogueAnswer changed | talk | DialogueInteraction | Medium |
| 7 | LastNpcInteracted changed | talk | DialogueInteraction | Low |
| 8 | Player moved >5u, same zone | travel | MovementChange | Low |
| 9 | nothing matched | Empty | None | Low |

---

## Task breakdown

### Task UI-INF-T1 -- Adapters: `ItemKindMapper.FromFFXIVActionType`

1. Create `QuestForge.Adapters/Items/ItemKindMapper.cs` with the static helper per Decision UI-INF-2.
2. Modify `QuestForge.Adapters/Actions/ActionTypeMapper.cs`: remove the `3u => ActionType.KeyItem` arm per Decision UI-INF-13.

### Task UI-INF-T2 -- Engine: `ItemUsedSignal` record + `GameStateSnapshot.ItemUsed`

1. Edit `QuestForge.Engine/Authoring/GameStateSnapshot.cs`.
2. Add the `ItemUsedSignal` record near the bottom (alongside `SayChatMessageSentSignal`):
   ```csharp
   public sealed record ItemUsedSignal(
       QuestForge.Schema.ItemKind Kind,
       uint ItemId,
       uint? TargetBaseId,
       QuestForge.Schema.Position3? TargetPosition);
   ```
3. Append a non-positional property at the end of the snapshot (after `SayChatMessageSent`):
   ```csharp
   public ItemUsedSignal? ItemUsed { get; init; }
   ```

### Task UI-INF-T3 -- Engine: `InferredFrom.ItemUsed`

1. Edit `QuestForge.Engine/Authoring/InferredFrom.cs`.
2. Append `ItemUsed,` to the enum per Decision UI-INF-7.

### Task UI-INF-T4 -- Engine: `SnapshotAggregator.OnItemUsed` / `OnItemUsedConsumed`

1. Edit `QuestForge.Engine/Authoring/SnapshotAggregator.cs`.
2. Add the backing field `private ItemUsedSignal? _itemUsed;` (near `_sayChatMessageSent`).
3. Add `ItemUsed = _itemUsed,` to the object-initializer in the `Current` property body (after `SayChatMessageSent`).
4. Add the setter and consumer methods per Decision UI-INF-4.
5. Do NOT clear in `ResetDeltas` (survives per-window lifecycle; only `OnItemUsedConsumed` clears).

### Task UI-INF-T5 -- Engine: `StepInferenceEngine` Rule 3.5i

1. Edit `QuestForge.Engine/Authoring/StepInferenceEngine.cs`.
2. Insert the rule per Decision UI-INF-5 immediately above the existing `// Rule 3.5e -- EmoteCompleted` comment (currently at line 289).
3. Confidence: `Confidence.High`. SuggestedExpect: `null`. Notes: includes Kind and ItemId.
4. SuggestedStepId pattern: `$"use-item-{ItemId}-on-{TargetBaseId}"` when target present, `$"use-item-{ItemId}"` when self-cast.

### Task UI-INF-T6 -- Engine: `StepFactory` `"use-item"` arm

1. Edit `QuestForge.Engine/Authoring/StepFactory.cs`.
2. Add the `"use-item"` arm per Decision UI-INF-8 -- placed in the switch block alongside `"use-action"` and `"use-emote"`.

### Task UI-INF-T7 -- Plugin: `AuthoringHost.RecordStep` clearing + diagnostic log

1. Edit `QuestForge.Plugin/Authoring/AuthoringHost.cs`.
2. At the end of `RecordStep`, after the existing six consume calls (lines 270-275), add:
   ```csharp
   _aggregator.OnItemUsedConsumed();   // NEW
   ```
3. Extend the `[QF-DIAG] PreviewInference:` line per Decision UI-INF-10 with `ItemUsed={after.ItemUsed?.ItemId}`.

### Task UI-INF-T8 -- Plugin.Tracing: extend `IGameProbe` + `DalamudGameProbe` + `FakeGameProbe`

1. Edit `QuestForge.Plugin.Tracing/IGameProbe.cs`: change `GetLastActionEffect()` return type from `(uint Sequence, uint FfxivActionType, uint ActionId)?` to `(uint Sequence, uint FfxivActionType, uint ActionId, float TargetLocationX, float TargetLocationY, float TargetLocationZ)?`.
2. Edit `QuestForge.Plugin/Tracing/DalamudGameProbe.cs`: add `castInfo.TargetLocation.X/Y/Z` to the return tuple per Decision UI-INF-16.
3. Edit `FakeGameProbe` in `UIObserverTests.cs`: extend `SetLastActionEffect` with three optional float params defaulting to `0f` per Decision UI-INF-12. Update `GetLastActionEffect()` to return the 6-tuple.

### Task UI-INF-T9 -- Plugin.Tracing: extend `UIObserver.PollPlayerActionEffect` + ResetWindowState

1. Edit `QuestForge.Plugin.Tracing/UIObserver.cs`.
2. Modify `PollPlayerActionEffect` per Decision UI-INF-6: destructure the 6-tuple; insert `ItemKindMapper.FromFFXIVActionType` check before the existing `ActionTypeMapper` call; if non-null, convert `TargetLocation` to `Position3?` and route to `OnItemUsed`; return.
3. Extract `CaptureTargetBaseId()` private helper to deduplicate target-reading logic.
4. Wire into `ResetWindowState`: add `_aggregator?.OnItemUsedConsumed();` alongside the existing six consume calls.

### Task UI-INF-T10 -- Update existing tests

1. Edit `QuestForge.Adapters.Tests/Actions/ActionTypeMapperTests.cs`.
2. Update `UAI-M3` (`FromFFXIVActionType_EventItemValue3_MapsToKeyItem`): change expected result to `null`. Rename to `FromFFXIVActionType_EventItemValue3_ReturnsNull_NowRoutedByItemKindMapper`.
3. Update `UO_K5` in `UIObserverTests.cs`: Item=2 now produces an `ItemUsed` event (see Decision UI-INF-14).

---

## Scope guard -- files that change and files that must NOT change

### Files that CHANGE

| File | Change |
|---|---|
| `QuestForge.Adapters/Items/ItemKindMapper.cs` | NEW -- pure mapper |
| `QuestForge.Adapters/Actions/ActionTypeMapper.cs` | MODIFY -- remove `3u => ActionType.KeyItem` arm |
| `QuestForge.Engine/Authoring/GameStateSnapshot.cs` | MODIFY -- add `ItemUsedSignal` record (with `TargetPosition`) + `ItemUsed` property |
| `QuestForge.Engine/Authoring/InferredFrom.cs` | MODIFY -- add `ItemUsed` value |
| `QuestForge.Engine/Authoring/SnapshotAggregator.cs` | MODIFY -- add field + setter (with `TargetPosition` param) + consumer + Current initializer |
| `QuestForge.Engine/Authoring/StepInferenceEngine.cs` | MODIFY -- insert Rule 3.5i |
| `QuestForge.Engine/Authoring/StepFactory.cs` | MODIFY -- add `"use-item"` arm (with `TargetPosition`) |
| `QuestForge.Plugin/Authoring/AuthoringHost.cs` | MODIFY -- add consume call + extend diag log |
| `QuestForge.Plugin.Tracing/IGameProbe.cs` | MODIFY -- extend `GetLastActionEffect` return type with 3 TargetLocation floats |
| `QuestForge.Plugin/Tracing/DalamudGameProbe.cs` | MODIFY -- read `castInfo.TargetLocation.X/Y/Z` in return tuple |
| `QuestForge.Plugin.Tracing/UIObserver.cs` | MODIFY -- extend PollPlayerActionEffect (6-tuple destructure, TargetPosition conversion) + ResetWindowState |
| `QuestForge.Adapters.Tests/Actions/ActionTypeMapperTests.cs` | MODIFY -- update UAI-M3 |
| `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` | MODIFY -- extend FakeGameProbe; update UO_K5 |

### Files that must NOT change

| File | Reason |
|---|---|
| `QuestForge.Schema/Step.cs` | UseItemStep schema already has the correct shape from Slice 2 |
| `QuestForge.Schema/SharedValueTypes.cs` | `ItemKind` enum and `Position3` record already exist |
| `QuestForge.Engine/QuestEngine.cs` | Engine dispatch is Slice 2 concern; inference is independent |
| `QuestForge.Engine/EngineAction.cs` | `EngineAction.UseItem` already exists from Slice 2 |
| `QuestForge.Adapters/Items/IItemUser.cs` | Interface already exists from Slice 2 |
| `QuestForge.Adapters.Fakes/Items/FakeItemUser.cs` | Fake already exists from Slice 2 |

---

## Given-When-Then test scenarios

Tests are split into three files:

| File | Scenarios | Test type |
|---|---|---|
| `QuestForge.Adapters.Tests/Items/ItemKindMapperTests.cs` | UI-M1..M4 | Pure mapper unit tests |
| `QuestForge.Engine.Tests/Authoring/UseItemInferenceTests.cs` | UII1..UII15 | Inference engine + aggregator + StepFactory |
| `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` | UO_M1..UO_M10 | UIObserver polling routing |

### Mapper tests (`QuestForge.Adapters.Tests/Items/ItemKindMapperTests.cs`)

#### UI-M1 -- `FromFFXIVActionType(2u)` -> `ItemKind.InventoryItem`

**Given:** uint 2 (FFXIVClientStructs Item).
**When:** `ItemKindMapper.FromFFXIVActionType(2u)`.
**Then:** `ItemKind.InventoryItem`.

#### UI-M2 -- `FromFFXIVActionType(3u)` -> `ItemKind.KeyItem`

**Given:** uint 3 (FFXIVClientStructs EventItem).
**When:** `ItemKindMapper.FromFFXIVActionType(3u)`.
**Then:** `ItemKind.KeyItem`.

#### UI-M3 -- Theory: non-item ActionTypes -> null

```csharp
[Theory]
[InlineData(0u)]   // None
[InlineData(1u)]   // Action (routed by ActionTypeMapper, not ItemKindMapper)
[InlineData(4u)]   // EventAction
[InlineData(5u)]   // GeneralAction (routed by ActionTypeMapper)
[InlineData(6u)]   // BuddyAction
[InlineData(7u)]   // MainCommand
[InlineData(13u)]  // Mount
[InlineData(99u)]  // bogus / future
public void FromFFXIVActionType_NonItemValue_ReturnsNull(uint ffxivType)
{
    Assert.Null(ItemKindMapper.FromFFXIVActionType(ffxivType));
}
```

#### UI-M4 -- `ActionTypeMapper.FromFFXIVActionType(3u)` now returns null (updated UAI-M3)

**Given:** uint 3 (FFXIVClientStructs EventItem).
**When:** `ActionTypeMapper.FromFFXIVActionType(3u)`.
**Then:** `null` (no longer maps to `ActionType.KeyItem`; item routing handled by `ItemKindMapper`).

This test replaces the existing `UAI-M3` which expected `ActionType.KeyItem`.

### Inference-engine tests (`QuestForge.Engine.Tests/Authoring/UseItemInferenceTests.cs`)

#### UII1 -- Happy path, key item, no target, no ground position: ItemUsed set -> infers `use-item` step

**Given:**
- `before = MakeSnapshot()` (no quest changes, no zone change)
- `after = before with { ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, null, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-item"`
- `result.SuggestedStepId == "use-item-2000456"`
- `result.SuggestedExpect == null`
- `result.Confidence == Confidence.High`
- `result.InferredFrom == InferredFrom.ItemUsed`
- `result.Notes` contains `"Expect"` and `"ItemId=2000456"`

#### UII2 -- Happy path, inventory item, with NPC target

**Given:**
- `before = MakeSnapshot()`
- `after = before with { ItemUsed = new ItemUsedSignal(ItemKind.InventoryItem, 4554u, 1000789u, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-item"`
- `result.SuggestedStepId == "use-item-4554-on-1000789"`
- `result.SuggestedExpect == null`
- `result.InferredFrom == InferredFrom.ItemUsed`

#### UII3 -- Self-cast: TargetBaseId null -> step id has no "-on-" suffix

**Given:**
- `before = MakeSnapshot()`
- `after = before with { ItemUsed = new ItemUsedSignal(ItemKind.InventoryItem, 4554u, null, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.SuggestedStepId == "use-item-4554"` (NO `-on-` suffix)

#### UII4 -- Priority over Rule 3 (QuestSequence advanced): ItemUsed wins

**Given:**
- `before = MakeSnapshot(questSequence: 1)`
- `after = before with { QuestSequence = 2, ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, 1000789u, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-item"` (NOT `"talk"`)
- `result.InferredFrom == InferredFrom.ItemUsed` (NOT `QuestSequenceChange`)

#### UII5 -- Priority over Rule 3.5 (ActionCompleted): ItemUsed wins when both set (defensive)

**Given:**
- `before = MakeSnapshot()`
- `after = before with { ActionCompleted = new ActionCompletedSignal(Schema.ActionType.Action, 31u, null), ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, null, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-item"` (NOT `"use-action"`)
- `result.InferredFrom == InferredFrom.ItemUsed`

#### UII6 -- Priority over Rule 3.5e (EmoteCompleted): ItemUsed wins when both set (defensive)

**Given:**
- `before = MakeSnapshot()`
- `after = before with { EmoteCompleted = new EmoteCompletedSignal(17u, null), ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, null, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-item"` (NOT `"use-emote"`)
- `result.InferredFrom == InferredFrom.ItemUsed`

#### UII7 -- Priority below Rule 1 (QuestCompleted): turn-in wins over ItemUsed

**Given:**
- `before = MakeSnapshot(questCompleted: false)`
- `after = before with { QuestCompleted = true, ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, null, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "turn-in"` (Rule 1)
- `result.InferredFrom == InferredFrom.QuestCompleted`

#### UII8 -- Priority below Rule 2.4 (KeyItemsRemoved): hand-over-item wins over ItemUsed

**Given:**
- `before = MakeSnapshot()`
- `after = before with { KeyItemsRemoved = new[] { 2000456u }, ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, null, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "hand-over-item"` (Rule 2.4)
- `result.InferredFrom == InferredFrom.DialogueInteraction`

Pins the natural disambiguation: when a key item is used AND consumed (removed), hand-over-item wins.

#### UII9 -- Priority below Rule 3.5s (SayChatMessageSent): chat wins over ItemUsed

**Given:**
- `before = MakeSnapshot()`
- `after = before with { SayChatMessageSent = new SayChatMessageSentSignal("hello", null), ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, null, null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "say-chat-message"` (Rule 3.5s)
- `result.InferredFrom == InferredFrom.SayChatMessageSent`

#### UII10 -- Aggregator: OnItemUsed sets the field (with TargetPosition); OnItemUsedConsumed clears; ResetDeltas does NOT clear

**Given:** `var agg = new SnapshotAggregator(activeQuest: null, clock: new FakeClock(T0));`

**When (sub-A):**
- `agg.OnItemUsed(ItemKind.KeyItem, 2000456u, 1000789u, new Position3(100.5f, 20.0f, -30.3f));`
- `var snap = agg.Current;`

**Then (sub-A):**
- `snap.ItemUsed is not null`
- `snap.ItemUsed.Kind == ItemKind.KeyItem`
- `snap.ItemUsed.ItemId == 2000456u`
- `snap.ItemUsed.TargetBaseId == 1000789u`
- `snap.ItemUsed.TargetPosition is not null`
- `snap.ItemUsed.TargetPosition.X == 100.5f`
- `snap.ItemUsed.TargetPosition.Y == 20.0f`
- `snap.ItemUsed.TargetPosition.Z == -30.3f`

**When (sub-B):**
- `agg.ResetDeltas();`
- `var snap2 = agg.Current;`

**Then (sub-B):**
- `snap2.ItemUsed is not null` (survives ResetDeltas)

**When (sub-C):**
- `agg.OnItemUsedConsumed();`
- `var snap3 = agg.Current;`

**Then (sub-C):**
- `snap3.ItemUsed is null`
- `snap3.LastNpcInteracted is null` (no side effects on unrelated state)

#### UII10b -- Aggregator: OnItemUsed with null TargetPosition (non-ground use)

**Given:** `var agg = new SnapshotAggregator(activeQuest: null, clock: new FakeClock(T0));`

**When:**
- `agg.OnItemUsed(ItemKind.KeyItem, 2000456u, 1000789u, null);`
- `var snap = agg.Current;`

**Then:**
- `snap.ItemUsed is not null`
- `snap.ItemUsed.TargetPosition is null`

#### UII11 -- `StepFactory.Build("use-item", ...)` produces a `UseItemStep` with snapshot fields populated (no ground target)

**Given:**
- `after = MakeSnapshot() with { ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, 1000789u, null) }`

**When:** `var step = StepFactory.Build("use-item", "use-item-2000456-on-1000789", null, after);`

**Then:**
- `step is UseItemStep ui`
- `ui.Id == "use-item-2000456-on-1000789"`
- `ui.Kind == ItemKind.KeyItem`
- `ui.ItemId == 2000456u`
- `ui.TargetNpcId == 1000789u`
- `ui.TargetPosition is null`
- `ui.Expect is null`

#### UII11b -- `StepFactory.Build("use-item", ...)` with ground-target position: TargetPosition populated

**Given:**
- `after = MakeSnapshot() with { ItemUsed = new ItemUsedSignal(ItemKind.InventoryItem, 4554u, null, new Position3(100.5f, 20.0f, -30.3f)) }`

**When:** `var step = StepFactory.Build("use-item", "use-item-4554", null, after);`

**Then:**
- `step is UseItemStep ui`
- `ui.TargetPosition is not null`
- `ui.TargetPosition.X == 100.5f`
- `ui.TargetPosition.Y == 20.0f`
- `ui.TargetPosition.Z == -30.3f`
- `ui.TargetNpcId is null` (ground-target, no NPC)

#### UII12 -- `StepFactory.Build("use-item", ...)` defensive: ItemUsed null -> ItemId(0), no throw

**Given:** `after = MakeSnapshot()` (ItemUsed is null)

**When:** `var step = StepFactory.Build("use-item", "use-item-X", null, after);`

**Then:**
- `step is UseItemStep ui`
- `ui.ItemId == 0u` (defensive fallback; validator catches via E13)
- `ui.Kind == ItemKind.KeyItem` (defensive default)
- `ui.TargetNpcId is null`
- `ui.TargetPosition is null`
- No exception thrown.

#### UII13 -- Inference with ground-target position: ItemUsed carries TargetPosition, infers use-item

**Given:**
- `before = MakeSnapshot()`
- `after = before with { ItemUsed = new ItemUsedSignal(ItemKind.KeyItem, 2000456u, null, new Position3(150.0f, 10.0f, -50.0f)) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-item"`
- `result.SuggestedStepId == "use-item-2000456"` (no NPC target, so no `-on-` suffix)
- `result.InferredFrom == InferredFrom.ItemUsed`
- `result.Confidence == Confidence.High`

(The inference rule itself does not inspect TargetPosition -- it fires on `ItemUsed is not null`. The position flows through StepFactory to the drafted step.)

### UIObserver tests (`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`)

These tests use the existing `BuildFixtureWithAggregator` helper and the existing `FakeGameProbe`. New group letter is `UO_M`.

#### UO_M1 -- Item ActionType (value 2) fires OnItemUsed, NOT OnActionCompleted

**Given:**
- `var (obs, fw, ap, gp, clock, writer, _, agg) = BuildFixtureWithAggregator();`
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);`
- `fw.Tick();` (baseline = 100)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 2u, actionId: 4554u);` (inventory item use)

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ItemUsed is not null`
- `agg.Current.ItemUsed.Kind == ItemKind.InventoryItem`
- `agg.Current.ItemUsed.ItemId == 4554u`
- `agg.Current.ItemUsed.TargetPosition is null` (TargetLocation defaults to 0,0,0)
- `agg.Current.ActionCompleted is null` (NOT routed to ActionCompleted)
- Exactly one `ObservationEvent` with `Method == "ItemUsed"` and `Argument == 4554u`.
- Zero `ObservationEvent`s with `Method == "ActionCompleted"` from this tick.

#### UO_M2 -- EventItem ActionType (value 3) fires OnItemUsed, NOT OnActionCompleted

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);`
- `fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 3u, actionId: 2000456u);` (key item use)

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ItemUsed is not null`
- `agg.Current.ItemUsed.Kind == ItemKind.KeyItem`
- `agg.Current.ItemUsed.ItemId == 2000456u`
- `agg.Current.ActionCompleted is null`
- Exactly one `ObservationEvent` with `Method == "ItemUsed"`.

This is the critical test: EventItem=3 now routes to ItemUsed, not ActionCompleted.

#### UO_M3 -- Regular Action (value 1) still fires OnActionCompleted, NOT OnItemUsed

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);`
- `fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 1u, actionId: 35u);`

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ActionCompleted is not null`
- `agg.Current.ActionCompleted.ActionId == 35u`
- `agg.Current.ItemUsed is null` (NOT routed to ItemUsed)
- Exactly one `ObservationEvent` with `Method == "ActionCompleted"`.

Pins that the existing ActionCompleted path is unaffected for non-item ActionTypes.

#### UO_M4 -- GeneralAction (value 5) still fires OnActionCompleted, NOT OnItemUsed

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u);`
- `fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 5u, actionId: 4u);` (Sprint)

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ActionCompleted is not null`
- `agg.Current.ActionCompleted.ActionType == Schema.ActionType.GeneralAction`
- `agg.Current.ItemUsed is null`

#### UO_M5 -- Item followed by Action: both signals set correctly (different windows)

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 2u, actionId: 4554u); fw.Tick();` (item use)
- `gp.SetLastActionEffect(sequence: 102u, ffxivActionType: 1u, actionId: 35u); fw.Tick();` (action use)

**Then:**
- After tick 2: `agg.Current.ItemUsed.ItemId == 4554u`, `agg.Current.ActionCompleted is null`.
- After tick 3: `agg.Current.ItemUsed.ItemId == 4554u` (not consumed), `agg.Current.ActionCompleted.ActionId == 35u`.

Pins that item and action signals are independent and don't clear each other.

#### UO_M6 -- ResetWindowState clears ItemUsed

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 2u, actionId: 4554u); fw.Tick();` (item use)
- `agg.Current.ItemUsed is not null` (verify)

**When:** `obs.ResetWindowState();`

**Then:**
- `agg.Current.ItemUsed is null` (consumed by ResetWindowState)

#### UO_M7 -- Item use with interactable NPC target: TargetBaseId captured

**Given:**
- Fixture extended with `FakeTargetProbe`.
- `fakeTarget.SetInteractableNpcTarget(BaseId: 1000789u, X: 0f, Y: 0f, Z: 0f, Zone: 132);`
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 3u, actionId: 2000456u);`

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ItemUsed.TargetBaseId == 1000789u`

#### UO_M8 -- Item use with no target: TargetBaseId is null

**Given:**
- Fixture's `FakeTargetProbe` returns null for all queries.
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 2u, actionId: 4554u);`

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ItemUsed.TargetBaseId is null`

#### UO_M9 -- Ground-targeted item use: non-zero TargetLocation -> TargetPosition populated

**Given:**
- Fixture's `FakeTargetProbe` returns null for all queries (no NPC target -- ground target).
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 3u, actionId: 2000456u, targetLocationX: 150.0f, targetLocationY: 10.0f, targetLocationZ: -50.0f);`

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ItemUsed is not null`
- `agg.Current.ItemUsed.TargetPosition is not null`
- `agg.Current.ItemUsed.TargetPosition.X == 150.0f`
- `agg.Current.ItemUsed.TargetPosition.Y == 10.0f`
- `agg.Current.ItemUsed.TargetPosition.Z == -50.0f`
- `agg.Current.ItemUsed.TargetBaseId is null` (no NPC target)

Pins that `CastInfo.TargetLocation` is captured for ground-targeted item use.

#### UO_M10 -- Non-ground item use: zero TargetLocation -> TargetPosition is null

**Given:**
- `gp.SetLastActionEffect(sequence: 100u, ffxivActionType: 1u, actionId: 31u); fw.Tick();` (baseline)
- `gp.SetLastActionEffect(sequence: 101u, ffxivActionType: 3u, actionId: 2000456u, targetLocationX: 0f, targetLocationY: 0f, targetLocationZ: 0f);`

**When:** `fw.Tick();`

**Then:**
- `agg.Current.ItemUsed is not null`
- `agg.Current.ItemUsed.TargetPosition is null` (all-zeros sentinel = non-ground use)

Pins the disambiguation rule: `(0, 0, 0)` is the "no ground target" sentinel.

---

## Implementation order

**Phase A -- Mapper + engine surface (20 min, all xUnit-testable)**
1. Task UI-INF-T1: `ItemKindMapper` in `QuestForge.Adapters/Items/`. Update `ActionTypeMapper` (remove 3u arm).
2. Tester: write UI-M1..M4 in `ItemKindMapperTests`. Update `UAI-M3` in `ActionTypeMapperTests`. Red, implement, green.
3. Task UI-INF-T2: `ItemUsedSignal` record (with `TargetPosition` field) + `GameStateSnapshot.ItemUsed` property.
4. Task UI-INF-T3: `InferredFrom.ItemUsed`.
5. Task UI-INF-T4: `SnapshotAggregator.OnItemUsed` / `OnItemUsedConsumed` methods + `Current` initializer.
6. Tester: write UII10, UII10b (aggregator tests). Red, implement, green.

**Phase B -- Inference rule + StepFactory (10 min)**
1. Task UI-INF-T5: insert Rule 3.5i in `StepInferenceEngine` (between Rule 3.5s and Rule 3.5e).
2. Task UI-INF-T6: add `"use-item"` arm to `StepFactory.Build` (with `TargetPosition = after?.ItemUsed?.TargetPosition`).
3. Tester: write UII1..UII9 (inference tests with priority pinning) + UII11, UII11b, UII12, UII13 (StepFactory + ground-target tests). Red, implement, green.

**Phase C -- AuthoringHost clearing + diagnostic (2 min)**
1. Task UI-INF-T7: add `_aggregator.OnItemUsedConsumed();` to `RecordStep` and extend `[QF-DIAG]` line.
2. No new test (covered structurally by aggregator test; one-line edit verified in-game).

**Phase D -- Probe extension + UIObserver extension + tests (40 min)**
1. Task UI-INF-T8: extend `IGameProbe.GetLastActionEffect()` return type; update `DalamudGameProbe`; extend `FakeGameProbe`.
2. Tester: write UO_M1..UO_M10 (including tests that pin EventItem=3 rerouting and ground-target capture). Red.
3. Task UI-INF-T9: modify `PollPlayerActionEffect` per Decision UI-INF-6 (item-first routing, 6-tuple destructure, TargetLocation-to-Position3 conversion, extract `CaptureTargetBaseId`). Wire `OnItemUsedConsumed` into `ResetWindowState`. Green.
4. Task UI-INF-T10: update existing `UAI-M3` test and `UO_K5` test.

**Phase E -- In-game smoke (combined with Slice 4)**
1. Enter Author mode for any quest containing a key-item use requirement.
2. Use the key item on an NPC. Confirm the `[QF-DIAG] PreviewInference:` log line shows `ItemUsed=<itemId>`.
3. Open Record modal. Confirm the modal shows `use-item` inference with the correct `Kind` and `ItemId`.
4. Verify the drafted step has `TargetNpcId` set and `TargetPosition` null (non-ground use, `CastInfo.TargetLocation` all zeros).
5. Use a ground-targeted item (AoE key item on terrain). Confirm the drafted step has `TargetPosition` populated with the world coordinates.
6. Use an inventory item (potion). Confirm `ItemUsed` shows the potion's Item row id.
7. Use a combat action. Confirm `ActionCompleted` fires (not `ItemUsed`).

---

## Done criteria

1. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~ItemKindMapperTests` reports all 4 mapper tests green.
2. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~ActionTypeMapperTests` reports all tests green (including the updated UAI-M3).
3. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~UseItemInferenceTests` reports all 15 inference/aggregator/factory tests green.
4. `dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverTests&FullyQualifiedName~UO_M"` reports all 10 UIObserver tests green.
5. No regression in existing `UseActionInferenceTests`, `UseEmoteInferenceTests`, `SayChatMessageInferenceTests`, `TeleportInferenceTests`, `AethernetInferenceTests`, or `UIObserverTests` (UO_A through UO_L, including updated UO_K5).
6. The trace stream emitted during a key-item use contains an `ObservationEvent` with `Method == "ItemUsed"`, `Argument == <itemId>`, and value object containing `kind` and `targetBaseId`.
7. The trace stream emitted during a key-item use does NOT contain an `ObservationEvent` with `Method == "ActionCompleted"` for that same sequence (verifies the rerouting).
8. **In-game smoke (Phase E):** With Author mode enabled for any quest, using a key item produces a draft `UseItemStep { Kind, ItemId, TargetNpcId?, TargetPosition? }` in the recorded steps. Ground-targeted item use populates `TargetPosition`; non-ground use leaves it null. The author edits the `Expect` field; the draft validates.

---

## Exclusions (what this plan does NOT include)

- **New `IGameProbe` method.** The existing `GetLastActionEffect()` is extended (not replaced) to include `TargetLocation` floats. No new method surface.
- **Inventory-item consumption tracking.** Whether the item was consumed (quantity decreased) is not captured. The engine handles this via `Expect` predicate evaluation.
- **Auto-repeat filtering.** If the player spams potions, each use fires a separate `ItemUsed` signal. The per-window model (only the latest signal survives) naturally deduplicates within a recording window.
- **Trace-side extractor** (`qf-trace extract-quest` route for `"ItemUsed"` events) -- Phase 10 follow-up.
- **`RecordingItemUser` wrapper.** Not needed per Decision UI20 in `USE_ITEM_STEP_PLAN.md` (write-only adapter).
- **Validator rules.** Already shipped in Slice 2 (E13, E14, E15, W10).
- **Schema changes.** `UseItemStep` and `ItemKind` already have the correct shape from Slice 2.
- **`ActionTypeMapper` value 3 restoration.** The removal of the `3u => ActionType.KeyItem` mapping is intentional and permanent. If a future use case needs to map EventItem=3 back to `ActionType.KeyItem`, it should use `ItemKindMapper` and convert at the call site.

---

## Open questions / discovery items

### O1 -- Does `ResponseGlobalSequence` increment for inventory item use (ActionType.Item=2)?

**Status:** unknown until in-game validation.

The FFXIVClientStructs comment says Response* fields are set when ActionEffect is received. Inventory item use (potions, food) goes through `ActionManager.UseAction(ActionType.Item, itemId, ...)` and generates ActionEffect packets. Strong expectation: yes, the sequence increments.

**If it does NOT increment:** inventory item inference would silently fail (no sequence change, no event). Mitigation: add a second signal source for inventory items (e.g., inventory count decrease polling). This would be a follow-up plan, not a blocker for key-item inference.

**Discovery:** Phase E in-game smoke verifies this. Engine tests (Phases A-D) are unblocked regardless.

### O2 -- Does using a key item mid-combat cause interference with combat-step inference?

**Status:** unlikely but requires consideration.

If the player uses a key item while in combat, both the combat span (Rule 2.2) and the item signal (Rule 3.5i) could fire. Rule 2.2 fires first (above Rule 3.5i in the cascade), so combat wins. This is correct: the author's primary intent during combat is the combat step, and the key item use is incidental (captured by a separate recording window if the author clicks Record between fights).

**No code change required.** The priority cascade handles this naturally.

### O3 -- Will auto-attacks pollute ItemUsed?

**No.** Auto-attacks have `ActionType == Action (1)`, not `Item (2)` or `EventItem (3)`. `ItemKindMapper.FromFFXIVActionType(1u)` returns null, so auto-attacks never route to `OnItemUsed`.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in the Given-When-Then test scenarios section.
- Happy paths: 4 scenarios (UII1, UII2, UII3, UII13)
- Edge cases: 9 scenarios (UII4, UII5, UII6, UII10, UII10b, UII11, UII11b, UO_M5, UO_M7)
- Error / no-op cases: 6 scenarios (UII7, UII8, UII9, UII12, UO_M6, UO_M8)
- Priority pinning: 6 scenarios (UII4, UII5, UII6, UII7, UII8, UII9)
- Ground-target tests: 4 scenarios (UII11b, UII13, UO_M9, UO_M10)
- Mapper tests: 4 scenarios (UI-M1, UI-M2, UI-M3, UI-M4)
- UIObserver routing: 10 scenarios (UO_M1..UO_M10)
- Expected total: ~29 tests across three files:
  - 4 in `QuestForge.Adapters.Tests/Items/ItemKindMapperTests.cs`
  - 15 in `QuestForge.Engine.Tests/Authoring/UseItemInferenceTests.cs`
  - 10 in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (as new `UO_M*` group)
  - Plus 1 updated test in `QuestForge.Adapters.Tests/Actions/ActionTypeMapperTests.cs` (UAI-M3)
  - Plus 1 updated test in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (UO_K5)

Builder is **fully unblocked for Phases A-D**. Phase E (in-game smoke) verifies O1 (does ResponseGlobalSequence increment for inventory items?) and ground-target capture reliability.
