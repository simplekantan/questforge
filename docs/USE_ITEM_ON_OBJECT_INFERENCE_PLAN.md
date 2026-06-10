# UseItemOnObjectStep -- Authoring Inference Plan (Slice 5)

**Status:** Approved
**Phase:** 11 (Corpus Expansion)
**Step type discriminator:** `"use-item-on-object"`
**Slice:** 5 of 6 (Authoring Inference)
**Author:** QuestForge System Architect
**Date:** 2026-06-10

---

## 1. Header

**Input documents:**
- `docs/USE_ITEM_ON_OBJECT_STEP_PLAN.md` -- slice 1 architect spec (schema, engine, validator)
- `docs/USE_ITEM_ON_OBJECT_SLICE3_PLAN.md` -- slice 3 Dalamud impl + tooling
- `CLAUDE.md` -- fixed slice order, Slice 5 requirements, polling-not-hooks mandate
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs` -- current rule chain
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` -- snapshot properties
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` -- On/Consumed pattern
- `QuestForge.Plugin.Tracing/UIObserver.cs` -- addon polling patterns
- `QuestForge.Plugin/Authoring/AuthoringHost.cs` -- ConsumeAllSignals

**Output (CI behavior changes):**
- `dotnet test QuestForge.Engine.Tests` gains ~16 new tests in `UseItemOnObjectInferenceTests.cs`
- `dotnet test QuestForge.Plugin.Tests` gains ~4 new tests (UO_RA*, UO_IE*) appended to `UIObserverTests.cs`
- Rule 2.4 (hand-over-item) now requires `RequestAddonSeen` -- existing test UII8 continues to pass because it sets `KeyItemsRemoved` without `RequestAddonSeen`, which causes Rule 2.4 to skip, but the snapshot also lacks `InventoryEventAddonSeen` so Rule 3.5n skips too, and `ItemUsed` alone fires Rule 3.5i -- still `"use-item"`, not `"hand-over-item"`. **This is a deliberate behavior change**: bare `KeyItemsRemoved + ItemUsed` (no addon context) now falls through to Rule 3.5i instead of Rule 2.4.
- Existing test UII8 must be updated: add `RequestAddonSeen = true` to the after snapshot so it continues to assert `"hand-over-item"`. This models the real game flow where the Request addon opens during a hand-over.
- Existing test OI-I4 is unaffected: it sets `ObjectInteracted + ItemUsed` without `InventoryEventAddonSeen` (defaults to false), so Rule 3.5n skips and Rule 3.5i fires, yielding `"use-item"` as before.

**Phase dependencies:**
- UseItemOnObjectStep schema + engine (slice 2, merged)
- UseItemOnObjectStep Dalamud impl (slice 3, merged)
- UseItemStep inference (implemented: Rule 3.5i, ItemUsedSignal, UIObserver.PollPlayerActionEffect)
- InteractObjectStep inference (implemented: Rule 3.5o, ObjectInteractedSignal, UIObserver.PollEventObjInteraction)
- HandOverItemStep inference (implemented: Rule 2.4, KeyItemsRemoved)

---

## 2. Problem Statement

The use-item-on-object game flow produces three inference signals in the same recording window:

1. `ObjectInteracted` (player targeted an EventObj + OccupiedInEvent transitioned true)
2. `ItemUsed` (CastInfo.ResponseGlobalSequence incremented, ActionType.EventItem)
3. `KeyItemsRemoved` (key item count decreased -- the item was consumed)

Without disambiguation, Rule 2.4 (KeyItemsRemoved -> `"hand-over-item"`) fires first and misidentifies the step. Two prior designs were rejected:

- **Design A (compound ObjectInteracted + ItemUsed):** Rule 2.4 fires before any compound check because key item removal is detected at the heartbeat poller level, before the item-used signal lands.
- **Design B (InventoryEventCompletedSignal composite):** The InventoryEvent addon closes BEFORE the ItemUsed action effect fires (timing verified in-game). A signal that requires both addon-close AND ItemUsed in the same frame cannot be constructed reliably.

**Solution: Boolean addon flags.** Track which addon was seen during the recording window as simple booleans on `GameStateSnapshot`. The flags are set when the addon opens (every-frame poll) and persist until consumed. No close-transition timing is required.

---

## 3. Architectural Decisions

### S5-D1: Two boolean flags on GameStateSnapshot, not a single AddonContext enum

**Decision:** Add `RequestAddonSeen` and `InventoryEventAddonSeen` as independent `bool` properties on `GameStateSnapshot`.

**Alternatives rejected:**
- Single `AddonContext` enum (`None | Request | InventoryEvent`): mutually exclusive semantics are wrong -- theoretically both could open in the same window (NPC chain that does a hand-over then redirects to an EventObj).
- Nullable signal records (like `ActionCompletedSignal`): overkill -- the addons carry no structured data we need. A bool is sufficient.

**Concrete surface area:**
```csharp
// GameStateSnapshot.cs -- two new non-positional init-only properties
public bool RequestAddonSeen { get; init; }
public bool InventoryEventAddonSeen { get; init; }
```

**What breaks if violated:** If someone uses a signal record instead, the inference engine must pattern-match against a non-null record rather than checking a bool. Extra allocation per snapshot for zero additional information. Booleans default to false in `with` expressions, which is the correct initial state -- a nullable record would require explicit null checks everywhere.

**Testability:** Tests construct snapshots via `with { RequestAddonSeen = true }` -- zero friction.

### S5-D2: Flags are per-window (cleared in ResetDeltas), not survive-reset

**Decision:** Both `_requestAddonSeen` and `_inventoryEventAddonSeen` are cleared in `SnapshotAggregator.ResetDeltas()`.

**Rationale:** These flags answer "was this addon seen in the current recording window?" A new recording window (OpenRecordModal -> ResetDeltas) must start clean. Unlike `ActionCompleted` or `EmoteCompleted` which survive ResetDeltas because they represent player-initiated atomic events that should not be lost across window boundaries, addon visibility is ambient state -- if the addon was open BEFORE this window started, seeing it again is a new observation for this window.

**What breaks if violated:** If flags survive ResetDeltas, a stale `RequestAddonSeen=true` from a prior window would cause Rule 2.4 to fire incorrectly on key item removals that happen later without any Request addon interaction.

**Alternatives rejected:**
- Survive-reset + consume pattern (like ActionCompleted): would require consume calls in AuthoringHost.ConsumeAllSignals and UIObserver.ResetWindowState. This is more complex for zero benefit since the addon might still be open at RecordStep time, meaning a consume-on-record would falsely clear it.

**Implementation note (reviewer):** The builder added consume methods (`OnRequestAddonSeenConsumed`, `OnInventoryEventAddonSeenConsumed`) and wired them into both `ConsumeAllSignals` and `ResetWindowState`. This deviates from the original "no consume" decision but is harmless: the flags are re-set by the next frame tick if the addon is still open, and `ResetDeltas` still clears at window start as designed. The belt-and-suspenders approach does not break any test or runtime scenario.

**Concrete surface area:**
```csharp
// SnapshotAggregator.ResetDeltas() -- add:
_requestAddonSeen = false;
_inventoryEventAddonSeen = false;
```

### S5-D3: Guard Rule 2.4 with RequestAddonSeen, not a negative InventoryEventAddonSeen guard

**Decision:** Change Rule 2.4's condition from `KeyItemsRemoved is { Count: > 0 }` to `KeyItemsRemoved is { Count: > 0 } && after.RequestAddonSeen`.

**Rationale:** The Request addon is the definitive signal that a hand-over interaction occurred. If the Request addon was NOT open, key item removal was NOT caused by a hand-over -- it was caused by something else (item-on-object use, NPC dialogue consumption, etc.).

**Alternatives rejected:**
- `&& !after.InventoryEventAddonSeen`: negative guard is fragile. If a third addon type consumes key items in the future, we would need to add another negation. The positive guard (`RequestAddonSeen`) is self-documenting: "this removal was a hand-over because the hand-over UI was used."
- No guard change + rely on rule ordering: impossible. Rule 2.4 is above Rule 3.5n by design (key item removal is a higher-priority signal). We cannot move Rule 2.4 below Rule 3.5n because that would break normal hand-over-item inference.

**What breaks if violated:** Without the guard, any quest where a key item is consumed via the InventoryEvent addon (use-item-on-object flow) would incorrectly infer `"hand-over-item"` instead of `"use-item-on-object"`.

**Concrete surface area:**
```csharp
// StepInferenceEngine.cs -- Rule 2.4:
if (after.KeyItemsRemoved is { Count: > 0 } removedItems && after.RequestAddonSeen)
```

### S5-D4: New Rule 3.5n positioned between Rule 3.5s (SayChatMessageSent) and Rule 3.5i (ItemUsed)

**Decision:** Insert Rule 3.5n at the position between Rule 3.5s and Rule 3.5i. Condition: `after.InventoryEventAddonSeen && after.ItemUsed is { } itemSig`.

**Rationale for position:**
- ABOVE Rule 3.5i (ItemUsed): when both `InventoryEventAddonSeen` and `ItemUsed` are set, the use-item-on-object flow is more specific than bare item use. Rule 3.5i is the catch-all for item use; 3.5n is the specific case where it was done through the InventoryEvent addon.
- BELOW Rule 3.5s (SayChatMessageSent): chat input is always intentional and unambiguous; it should never be overshadowed by addon flags.
- BELOW Rules 1, 2, 2.1, 2.2, 2.2b, 2.3: quest completion, acceptance, combat, purchase, key item gained all take precedence.
- BELOW Rule 2.4 (KeyItemsRemoved with RequestAddonSeen guard): when both Request and InventoryEvent were seen (unlikely but defensive), Rule 2.4 fires first -- hand-over wins.

**What breaks if violated:** If Rule 3.5n is placed below Rule 3.5i, it can never fire because Rule 3.5i would always consume the `ItemUsed` signal first (3.5i only requires `ItemUsed`, 3.5n requires `InventoryEventAddonSeen + ItemUsed`).

**Concrete surface area:**
```csharp
// StepInferenceEngine.cs -- new Rule 3.5n, between Rule 3.5s and Rule 3.5i:
// Rule 3.5n: UseItemOnObject -- InventoryEvent addon seen + item used
if (after.InventoryEventAddonSeen && after.ItemUsed is { } itemSig)
{
    var objSig = after.ObjectInteracted;
    return new InferenceResult(
        StepType:        "use-item-on-object",
        SuggestedStepId: $"use-item-{itemSig.ItemId}-on-{objSig?.InteractableId ?? 0}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.InventoryEventItemUsed,
        Notes:           $"Author MUST write the Expect predicate (no universal use-item-on-object postcondition). " +
                         $"Kind={itemSig.Kind}, ItemId={itemSig.ItemId}, " +
                         $"InteractableId={objSig?.InteractableId ?? 0}");
}
```

### S5-D5: New InferredFrom enum value: InventoryEventItemUsed

**Decision:** Add `InferredFrom.InventoryEventItemUsed` to the `InferredFrom` enum.

**Alternatives rejected:**
- Reuse `InferredFrom.ItemUsed`: would make it impossible to distinguish use-item-on-object from use-item in diagnostics and trace analysis.
- Reuse `InferredFrom.ObjectInteracted`: the primary signal is the addon + item use, not the object interaction alone.
- Compound name `AddonItemUsed`: too vague -- does not identify WHICH addon.

**Concrete surface area:**
```csharp
// InferredFrom.cs -- append:
InventoryEventItemUsed,
```

### S5-D6: StepFactory "use-item-on-object" arm reads ObjectInteracted + ItemUsed

**Decision:** The StepFactory arm for `"use-item-on-object"` reads `InteractableId` and position from `after.ObjectInteracted`, and `Kind` + `ItemId` from `after.ItemUsed`.

**Fallback behavior:** If `ObjectInteracted` is null (defensive), `InteractableId` defaults to 0 and position defaults to `(0,0,0)`. If `ItemUsed` is null (defensive), `Kind` defaults to `ItemKind.KeyItem` and `ItemId` defaults to 0. These are validator-catchable zero values (E27, E28).

**Implementation note (reviewer):** The builder omitted `Zone` and `RequiredZone` from the StepFactory arm, consistent with peer action-type steps (`use-item`, `use-action`, `use-emote`). The builder also used inline `expect is not null` check instead of the pre-computed `expectValue` local; functionally equivalent since inference always passes null for this step's expect.

**Concrete surface area:**
```csharp
// StepFactory.cs -- new arm in the switch:
"use-item-on-object" => new QuestForge.Schema.UseItemOnObjectStep
{
    Id = stepId,
    Expect = expectValue,
    Zone = zoneStr,
    RequiredZone = zoneStr,
    InteractableId = after?.ObjectInteracted?.InteractableId ?? 0u,
    Position = after?.ObjectInteracted is { } uio
        ? new Position3(uio.X, uio.Y, uio.Z)
        : new Position3(0, 0, 0),
    Kind = after?.ItemUsed?.Kind ?? QuestForge.Schema.ItemKind.KeyItem,
    ItemId = after?.ItemUsed?.ItemId ?? 0u,
},
```

### S5-D7: UIObserver polls via IAddonProbe.IsAddonOpen -- no new interface methods

**Decision:** The two new UIObserver pollers (`PollRequestAddon`, `PollInventoryEventAddon`) use the existing `IAddonProbe.IsAddonOpen(string addonName)` method. No new methods on `IAddonProbe` or `IGameProbe` are needed.

**Rationale:** `IsAddonOpen` already abstracts addon visibility polling. The addon names `"Request"` and `"InventoryEvent"` are FFXIV addon identifiers (same naming convention as `"TelepotTown"`, `"SelectIconString"`, `"SelectYesno"`, `"Teleport"` already polled by UIObserver). FakeAddonProbe already supports arbitrary addon names via `OpenAddon(string)`.

**What breaks if violated:** Adding new methods to `IAddonProbe` would require updating both `DalamudAddonProbe` (production) and `FakeAddonProbe` (tests), plus the `UIObserver` constructor contract changes. This is unnecessary churn.

### S5-D8: Pollers are every-frame, not heartbeat-throttled

**Decision:** `PollRequestAddon` and `PollInventoryEventAddon` run in the every-frame block of `OnFrameworkUpdate`, alongside the other addon pollers.

**Rationale:** The InventoryEvent addon can open and close within a single heartbeat interval (250ms) during fast quest interactions. Missing it would cause Rule 3.5n to skip (InventoryEventAddonSeen stays false) and the step would misidentify as bare `"use-item"`. Every-frame polling ensures the flag is set even for short-lived addon appearances.

**Cost:** Two `IsAddonOpen` calls per frame. These are `AtkUnitBase.IsVisible` reads -- a pointer dereference + bit check. Negligible performance impact.

### S5-D9: UII8 existing test update is mandatory

**Decision:** The existing test `UII8` (`UseItemInference_LosesToKeyItemsRemoved_Rule24Wins_UII8`) must be updated to add `RequestAddonSeen = true` to the after snapshot.

**Rationale:** UII8 asserts that `KeyItemsRemoved + ItemUsed` infers `"hand-over-item"`. With the Rule 2.4 guard change (S5-D3), this combination WITHOUT `RequestAddonSeen` now falls through to Rule 3.5i (`"use-item"`). This is correct behavior: bare key item removal without the Request addon is NOT a hand-over. The test must model the complete hand-over game flow by including `RequestAddonSeen = true`.

**What breaks if not done:** UII8 fails. The assertion `StepType == "hand-over-item"` would get `"use-item"` instead.

### S5-D10: AuthoringHost.PreviewInference diagnostic line extended

**Decision:** The `[QF-DIAG] PreviewInference` log line in AuthoringHost is extended with `RequestAddonSeen={after.RequestAddonSeen} InventoryEventAddonSeen={after.InventoryEventAddonSeen}`.

**Rationale:** This is the primary debugging tool for in-game smoke testing (Slice 6). Without it, the author has no way to confirm the addon flags are being set correctly.

---

## 4. Task Breakdown

### Task S5-T1: GameStateSnapshot -- two new boolean properties

**File:** `QuestForge.Engine/Authoring/GameStateSnapshot.cs`

Add two non-positional init-only properties after the existing `ObjectInteracted` property:

```csharp
// Non-positional. Set when UIObserver.PollRequestAddon detects that the Request
// (hand-over) addon was visible at any point during this recording window.
// Cleared by ResetDeltas (per-window lifecycle).
public bool RequestAddonSeen { get; init; }

// Non-positional. Set when UIObserver.PollInventoryEventAddon detects that the
// InventoryEvent (use-item-on-object) addon was visible at any point during this
// recording window. Cleared by ResetDeltas (per-window lifecycle).
public bool InventoryEventAddonSeen { get; init; }
```

### Task S5-T2: SnapshotAggregator -- backing fields, setters, wire to Current, clear in ResetDeltas

**File:** `QuestForge.Engine/Authoring/SnapshotAggregator.cs`

Add backing fields:

```csharp
private bool _requestAddonSeen;
private bool _inventoryEventAddonSeen;
```

Add setter methods:

```csharp
/// <summary>
/// Called by UIObserver.PollRequestAddon every frame when the Request addon is visible.
/// Sets the flag to true (idempotent). Cleared by ResetDeltas (per-window lifecycle).
/// </summary>
public void OnRequestAddonSeen() => _requestAddonSeen = true;

/// <summary>
/// Called by UIObserver.PollInventoryEventAddon every frame when the InventoryEvent
/// addon is visible. Sets the flag to true (idempotent). Cleared by ResetDeltas
/// (per-window lifecycle).
/// </summary>
public void OnInventoryEventAddonSeen() => _inventoryEventAddonSeen = true;
```

Wire into `Current` property (object initializer block):

```csharp
RequestAddonSeen         = _requestAddonSeen,
InventoryEventAddonSeen  = _inventoryEventAddonSeen,
```

Clear in `ResetDeltas()`:

```csharp
_requestAddonSeen = false;
_inventoryEventAddonSeen = false;
```

### Task S5-T3: InferredFrom enum -- add InventoryEventItemUsed

**File:** `QuestForge.Engine/Authoring/InferredFrom.cs`

Append after `ObjectInteracted`:

```csharp
InventoryEventItemUsed,
```

### Task S5-T4: StepInferenceEngine -- guard Rule 2.4 + insert Rule 3.5n

**File:** `QuestForge.Engine/Authoring/StepInferenceEngine.cs`

**Change 1:** Guard Rule 2.4 (line ~145):

From:
```csharp
if (after.KeyItemsRemoved is { Count: > 0 } removedItems)
```

To:
```csharp
if (after.KeyItemsRemoved is { Count: > 0 } removedItems && after.RequestAddonSeen)
```

**Change 2:** Insert Rule 3.5n between Rule 3.5s (SayChatMessageSent, ~line 286) and Rule 3.5i (ItemUsed, ~line 289):

```csharp
// Rule 3.5n: UseItemOnObject -- InventoryEvent addon seen + item used
// Fires when UIObserver.PollInventoryEventAddon detected the InventoryEvent addon
// was visible during this recording window AND the player used an item.
//
// PRIORITY: above Rule 3.5i (ItemUsed). When InventoryEventAddonSeen is true AND
// ItemUsed is set, the use-item-on-object flow is the more specific inference.
// Rule 3.5i (bare ItemUsed) is the catch-all; 3.5n handles the addon-mediated case.
// PRIORITY: below Rule 3.5s (SayChatMessageSent) -- chat is always intentional.
// PRIORITY: below Rule 2.4 (KeyItemsRemoved + RequestAddonSeen) -- hand-over wins
// when both addons were seen (defensive; unlikely in practice).
// CONFIDENCE: High. EXPECT: null -- author MUST write the postcondition.
if (after.InventoryEventAddonSeen && after.ItemUsed is { } itemSig)
{
    var objSig = after.ObjectInteracted;
    return new InferenceResult(
        StepType:        "use-item-on-object",
        SuggestedStepId: $"use-item-{itemSig.ItemId}-on-{objSig?.InteractableId ?? 0}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.InventoryEventItemUsed,
        Notes:           $"Author MUST write the Expect predicate (no universal use-item-on-object postcondition). " +
                         $"Kind={itemSig.Kind}, ItemId={itemSig.ItemId}, " +
                         $"InteractableId={objSig?.InteractableId ?? 0}");
}
```

### Task S5-T5: StepFactory -- add "use-item-on-object" arm

**File:** `QuestForge.Engine/Authoring/StepFactory.cs`

Add new arm to the switch, after `"use-item"`:

```csharp
"use-item-on-object" => new QuestForge.Schema.UseItemOnObjectStep
{
    Id = stepId,
    Expect = expectValue,
    Zone = zoneStr,
    RequiredZone = zoneStr,
    InteractableId = after?.ObjectInteracted?.InteractableId ?? 0u,
    Position = after?.ObjectInteracted is { } uio
        ? new Position3(uio.X, uio.Y, uio.Z)
        : new Position3(0, 0, 0),
    Kind = after?.ItemUsed?.Kind ?? QuestForge.Schema.ItemKind.KeyItem,
    ItemId = after?.ItemUsed?.ItemId ?? 0u,
},
```

### Task S5-T6: UIObserver -- two new every-frame pollers

**File:** `QuestForge.Plugin.Tracing/UIObserver.cs`

Add two new private methods:

```csharp
private void PollRequestAddon()
{
    if (_addonProbe is null) return;
    if (_addonProbe.IsAddonOpen("Request"))
        _aggregator?.OnRequestAddonSeen();
}

private void PollInventoryEventAddon()
{
    if (_addonProbe is null) return;
    if (_addonProbe.IsAddonOpen("InventoryEvent"))
        _aggregator?.OnInventoryEventAddonSeen();
}
```

Wire into `OnFrameworkUpdate` in the every-frame block (after `PollEventObjInteraction`):

```csharp
PollRequestAddon();
PollInventoryEventAddon();
```

### Task S5-T7: AuthoringHost -- extend PreviewInference diagnostic

**File:** `QuestForge.Plugin/Authoring/AuthoringHost.cs`

Add to the `[QF-DIAG] PreviewInference` log line:

```
RequestAddonSeen={after.RequestAddonSeen} InventoryEventAddonSeen={after.InventoryEventAddonSeen}
```

### Task S5-T8: Update existing test UII8

**File:** `QuestForge.Engine.Tests/Authoring/UseItemInferenceTests.cs`

Update the `after` snapshot in `UseItemInference_LosesToKeyItemsRemoved_Rule24Wins_UII8`:

```csharp
var after  = before with
{
    KeyItemsRemoved = [2000456u],
    RequestAddonSeen = true,  // <-- NEW: model the Request addon being open during hand-over
    ItemUsed = new ItemUsedSignal(
        QuestForge.Schema.ItemKind.KeyItem,
        ItemId:       2000456u,
        TargetBaseId: null,
        TargetPosition: null)
};
```

---

## 5. Given-When-Then Specs

### 5.1 StepInferenceEngine Tests

**File:** `QuestForge.Engine.Tests/Authoring/UseItemOnObjectInferenceTests.cs`

#### S5-T1: Happy path -- InventoryEventAddonSeen + ItemUsed -> "use-item-on-object"

```
Given: before = baseline snapshot (zone 131, no changes)
       after  = before with {
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null),
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20)
       }
When:  engine.Infer(before, after)
Then:  StepType == "use-item-on-object"
       SuggestedStepId == "use-item-2000456-on-2001500"
       SuggestedExpect == null
       Confidence == High
       InferredFrom == InventoryEventItemUsed
       Notes contains "Expect" (author must write postcondition)
       Notes contains "ItemId=2000456"
       Notes contains "InteractableId=2001500"
```

#### S5-T2: KeyItemsRemoved WITHOUT RequestAddonSeen -- Rule 2.4 skipped, falls through

```
Given: before = baseline snapshot
       after  = before with {
           KeyItemsRemoved = [2000456u]
           (NO RequestAddonSeen, NO InventoryEventAddonSeen, NO ItemUsed)
       }
When:  engine.Infer(before, after)
Then:  StepType != "hand-over-item" (Rule 2.4 skipped because RequestAddonSeen is false)
       (falls through to later rules -- likely Rule 9 Empty or Rule 5/6/7 depending on other state)
```

#### S5-T3: KeyItemsRemoved + RequestAddonSeen -> "hand-over-item" (existing behavior preserved)

```
Given: before = baseline snapshot
       after  = before with {
           KeyItemsRemoved = [2000456u],
           RequestAddonSeen = true
       }
When:  engine.Infer(before, after)
Then:  StepType == "hand-over-item"
       InferredFrom == DialogueInteraction
       SuggestedExpect contains "not(playerHasItem(2000456))"
```

#### S5-T4: Only ItemUsed, no InventoryEventAddonSeen -> "use-item" (existing behavior preserved)

```
Given: before = baseline snapshot
       after  = before with {
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null)
           (InventoryEventAddonSeen = false -- default)
       }
When:  engine.Infer(before, after)
Then:  StepType == "use-item"
       InferredFrom == ItemUsed
```

#### S5-T5: InventoryEventAddonSeen + ItemUsed + KeyItemsRemoved (no Request) -> Rule 3.5n fires

```
Given: before = baseline snapshot
       after  = before with {
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null),
           KeyItemsRemoved = [2000456u],
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20)
           (RequestAddonSeen = false)
       }
When:  engine.Infer(before, after)
Then:  StepType == "use-item-on-object" (Rule 2.4 skipped: no RequestAddonSeen; Rule 3.5n fires)
       InferredFrom == InventoryEventItemUsed
```

#### S5-T6: InventoryEventAddonSeen but NO ItemUsed -> Rule 3.5n skipped

```
Given: before = baseline snapshot
       after  = before with {
           InventoryEventAddonSeen = true,
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20)
           (ItemUsed = null)
       }
When:  engine.Infer(before, after)
Then:  StepType != "use-item-on-object" (Rule 3.5n requires both flags)
       StepType == "interact-object" (falls through to Rule 3.5o)
       InferredFrom == ObjectInteracted
```

#### S5-T7: Rule 3.5n beats quest sequence advance (Rule 3)

```
Given: before = baseline snapshot (questSequence = 1)
       after  = before with {
           QuestSequence = 2,
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null),
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20)
       }
When:  engine.Infer(before, after)
Then:  StepType == "use-item-on-object" (NOT "talk")
       InferredFrom == InventoryEventItemUsed
```

#### S5-T8: Rule 3.5n loses to QuestCompleted (Rule 1)

```
Given: before = baseline snapshot (questCompleted = false)
       after  = before with {
           QuestCompleted = true,
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null),
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20)
       }
When:  engine.Infer(before, after)
Then:  StepType == "turn-in" (Rule 1 wins)
       InferredFrom == QuestCompleted
```

#### S5-T9: InventoryItem kind preserved in inference

```
Given: before = baseline snapshot
       after  = before with {
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(InventoryItem, 4554, null, null),
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20)
       }
When:  engine.Infer(before, after)
Then:  StepType == "use-item-on-object"
       Notes contains "Kind=InventoryItem"
       Notes contains "ItemId=4554"
```

#### S5-T10: ObjectInteracted present -> InteractableId in step ID

```
Given: before = baseline snapshot
       after  = before with {
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null),
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20)
       }
When:  engine.Infer(before, after)
Then:  SuggestedStepId == "use-item-2000456-on-2001500"
```

#### S5-T11: ObjectInteracted absent -> InteractableId=0 in step ID

```
Given: before = baseline snapshot
       after  = before with {
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null)
           (ObjectInteracted = null)
       }
When:  engine.Infer(before, after)
Then:  SuggestedStepId == "use-item-2000456-on-0"
```

#### S5-T12: Rule 3.5n loses to SayChatMessageSent (Rule 3.5s)

```
Given: before = baseline snapshot
       after  = before with {
           SayChatMessageSent = SayChatMessageSentSignal("hello", null),
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null)
       }
When:  engine.Infer(before, after)
Then:  StepType == "say-chat-message" (Rule 3.5s wins)
       InferredFrom == SayChatMessageSent
```

#### S5-T13: Rule 3.5n loses to QuestAccepted (Rule 2)

```
Given: before = baseline snapshot (questAccepted = false)
       after  = before with {
           QuestAccepted = true,
           InventoryEventAddonSeen = true,
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null)
       }
When:  engine.Infer(before, after)
Then:  StepType == "accept" (Rule 2 wins)
       InferredFrom == QuestAccepted
```

#### S5-T14: Both RequestAddonSeen AND InventoryEventAddonSeen + KeyItemsRemoved -> Rule 2.4 wins

```
Given: before = baseline snapshot
       after  = before with {
           RequestAddonSeen = true,
           InventoryEventAddonSeen = true,
           KeyItemsRemoved = [2000456u],
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null)
       }
When:  engine.Infer(before, after)
Then:  StepType == "hand-over-item" (Rule 2.4 fires first because RequestAddonSeen is true)
       InferredFrom == DialogueInteraction
```

### 5.2 StepFactory Tests

#### S5-F1: Builds UseItemOnObjectStep from ObjectInteracted + ItemUsed + InventoryEventAddonSeen

```
Given: after = baseline snapshot with {
           ObjectInteracted = ObjectInteractedSignal(2001500, 10.5f, 5.0f, 20.3f),
           ItemUsed = ItemUsedSignal(KeyItem, 2000456, null, null),
           InventoryEventAddonSeen = true
       }
When:  StepFactory.Build("use-item-on-object", "use-item-2000456-on-2001500", null, after)
Then:  step is UseItemOnObjectStep
       InteractableId == 2001500
       Position == (10.5, 5.0, 20.3)
       Kind == KeyItem
       ItemId == 2000456
       Expect is null
```

#### S5-F2: Null signals -> zero defaults (defensive)

```
Given: after = baseline snapshot (ObjectInteracted = null, ItemUsed = null)
When:  StepFactory.Build("use-item-on-object", "use-item-X", null, after)
Then:  step is UseItemOnObjectStep (no exception)
       InteractableId == 0
       Position == (0, 0, 0)
       Kind == KeyItem (defensive default)
       ItemId == 0
```

#### S5-F3: InventoryItem kind flows through

```
Given: after = baseline snapshot with {
           ObjectInteracted = ObjectInteractedSignal(2001500, 10, 5, 20),
           ItemUsed = ItemUsedSignal(InventoryItem, 4554, null, null)
       }
When:  StepFactory.Build("use-item-on-object", "use-item-4554-on-2001500", null, after)
Then:  step is UseItemOnObjectStep
       Kind == InventoryItem
       ItemId == 4554
```

### 5.3 SnapshotAggregator Tests

#### S5-A1: OnRequestAddonSeen sets flag

```
Given: fresh SnapshotAggregator
When:  OnRequestAddonSeen()
Then:  Current.RequestAddonSeen == true
```

#### S5-A2: OnInventoryEventAddonSeen sets flag

```
Given: fresh SnapshotAggregator
When:  OnInventoryEventAddonSeen()
Then:  Current.InventoryEventAddonSeen == true
```

#### S5-A3: ResetDeltas clears both flags

```
Given: SnapshotAggregator with both flags set
When:  ResetDeltas()
Then:  Current.RequestAddonSeen == false
       Current.InventoryEventAddonSeen == false
```

#### S5-A4: Flags are idempotent -- calling twice does not change state

```
Given: fresh SnapshotAggregator
When:  OnRequestAddonSeen() called twice
Then:  Current.RequestAddonSeen == true (no exception, no double-set)
```

### 5.4 UIObserver Tests

#### S5-UO1: Request addon open -> RequestAddonSeen=true on snapshot

```
Given: UIObserver with aggregator set
       FakeAddonProbe.OpenAddon("Request")
When:  framework.Tick()
Then:  aggregator.Current.RequestAddonSeen == true
```

#### S5-UO2: InventoryEvent addon open -> InventoryEventAddonSeen=true on snapshot

```
Given: UIObserver with aggregator set
       FakeAddonProbe.OpenAddon("InventoryEvent")
When:  framework.Tick()
Then:  aggregator.Current.InventoryEventAddonSeen == true
```

#### S5-UO3: Neither addon open -> both false

```
Given: UIObserver with aggregator set (no addons open)
When:  framework.Tick()
Then:  aggregator.Current.RequestAddonSeen == false
       aggregator.Current.InventoryEventAddonSeen == false
```

#### S5-UO4: ResetWindowState does NOT directly clear addon flags (they clear in ResetDeltas)

```
Given: UIObserver with aggregator set
       FakeAddonProbe.OpenAddon("Request"), framework.Tick() (flag set)
       FakeAddonProbe.CloseAddon("Request") (addon closed)
When:  observer.ResetWindowState()
       framework.Tick() (addon still closed)
Then:  aggregator.Current.RequestAddonSeen still reflects whatever ResetDeltas did
       (ResetWindowState calls aggregator consume methods but addon flags are cleared
       in ResetDeltas which is called separately from OpenRecordModal)
NOTE:  This test verifies that ResetWindowState does not throw and that the flag
       lifecycle is controlled by ResetDeltas, not ResetWindowState.
```

---

## 6. Implementation Order

### Phase A: Engine-side changes (pure C#, no Dalamud)

**Duration:** ~1 hour
**Tasks:** S5-T1, S5-T2, S5-T3, S5-T4, S5-T5, S5-T8

1. Add `RequestAddonSeen` and `InventoryEventAddonSeen` to `GameStateSnapshot` (S5-T1)
2. Add backing fields, setters, wire to `Current`, clear in `ResetDeltas` in `SnapshotAggregator` (S5-T2)
3. Add `InferredFrom.InventoryEventItemUsed` (S5-T3)
4. Guard Rule 2.4 + insert Rule 3.5n in `StepInferenceEngine` (S5-T4)
5. Add `"use-item-on-object"` arm in `StepFactory` (S5-T5)
6. Update UII8 test (S5-T8)

**Done gate:** `dotnet test QuestForge.Engine.Tests` passes. All S5-T* and S5-F* and S5-A* tests green.

### Phase B: Plugin-side changes (UIObserver + AuthoringHost)

**Duration:** ~30 minutes
**Tasks:** S5-T6, S5-T7

1. Add `PollRequestAddon` and `PollInventoryEventAddon` to `UIObserver` (S5-T6)
2. Wire into `OnFrameworkUpdate` every-frame block (S5-T6)
3. Extend `PreviewInference` diagnostic log line (S5-T7)

**Done gate:** `dotnet test QuestForge.Plugin.Tests` passes. All S5-UO* tests green.

### Phase C: Full build + test

**Duration:** ~10 minutes

1. `dotnet build` (all projects)
2. `dotnet test QuestForge.Engine.Tests`
3. `dotnet test QuestForge.Plugin.Tests`
4. `dotnet test QuestForge.Schema.Tests` (no changes expected, regression check)

---

## 7. Done Criteria

1. `dotnet test QuestForge.Engine.Tests --filter "UseItemOnObjectInference"` passes all ~16 new tests
2. `dotnet test QuestForge.Engine.Tests --filter "UseItemInference"` passes (UII8 updated)
3. `dotnet test QuestForge.Engine.Tests --filter "InteractObjectInference"` passes (OI-I4 unaffected)
4. `dotnet test QuestForge.Plugin.Tests --filter "UO_RA\|UO_IE"` passes all ~4 new tests
5. `dotnet build` succeeds with zero warnings in `QuestForge.Engine` and `QuestForge.Plugin.Tracing`
6. `StepInferenceEngine.Infer` returns `"use-item-on-object"` when `InventoryEventAddonSeen=true` and `ItemUsed` is set
7. `StepInferenceEngine.Infer` returns `"hand-over-item"` ONLY when `RequestAddonSeen=true` and `KeyItemsRemoved` is non-empty
8. `StepFactory.Build("use-item-on-object", ...)` produces a `UseItemOnObjectStep` with correct field population
9. `AuthoringHost.PreviewInference` log line includes `RequestAddonSeen=` and `InventoryEventAddonSeen=` fields

---

## 8. Exclusions

- **No new IAddonProbe methods.** Existing `IsAddonOpen(string)` is sufficient.
- **No new IGameProbe methods.** Addon visibility is polled through `IAddonProbe`, not `IGameProbe`.
- **No DalamudAddonProbe changes.** The existing `IsAddonOpen` implementation already supports arbitrary addon names -- `"Request"` and `"InventoryEvent"` work via `AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(name)->IsVisible`.
- **No trace event changes.** The addon flags are ambient state on the snapshot, not discrete events. They do not produce new trace observations.
- ~~**No ConsumeAllSignals changes.**~~ **Reviewer note:** The builder added consume methods and wired them into ConsumeAllSignals and ResetWindowState. This deviates from the original exclusion but is harmless (see S5-D2 implementation note).
- **No StepInferenceEngine signal for Request addon closing.** The boolean flag persists -- no close-transition tracking needed.
- **Slice 6 (in-game smoke) not included in this plan.** Manual testing is a separate step.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in sections 5.1 through 5.4.
- Happy paths: 4 scenarios (S5-T1, S5-T9, S5-F1, S5-F3)
- Edge cases: 8 scenarios (S5-T5, S5-T6, S5-T7, S5-T10, S5-T11, S5-A4, S5-UO3, S5-UO4)
- Error/priority cases: 8 scenarios (S5-T2, S5-T3, S5-T4, S5-T8, S5-T12, S5-T13, S5-T14, S5-F2)
- Aggregator tests: 4 scenarios (S5-A1, S5-A2, S5-A3, S5-A4)
- UIObserver tests: 4 scenarios (S5-UO1, S5-UO2, S5-UO3, S5-UO4)
- Expected total: ~24 tests across UseItemOnObjectInferenceTests.cs + UIObserverTests.cs (plus UII8 update in UseItemInferenceTests.cs)
