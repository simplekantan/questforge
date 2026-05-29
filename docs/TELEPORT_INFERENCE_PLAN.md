# TeleportStep Authoring Inference Plan

**Status:** ready for test creation (with one open question — see §F)
**Input docs:**
- `docs/TELEPORT_STEP_PLAN.md` (engine-side TeleportStep — already shipped)
- `docs/AUTHORING.md` §Record-Step inference
- `docs/SCHEMA.md` §TeleportStep
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs` (existing rule-table)
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` (existing aethernet analog)
- `QuestForge.Plugin.Tracing/UIObserver.cs` `PollAethernetDestination` (existing polling-state-machine analog)

**Output (CI behaviour):** When the player teleports manually via the FFXIV "Teleport" general action during an Author-mode recording session, the snapshot field `TeleportCompleted` becomes non-null with the destination aetheryte ID, and `StepInferenceEngine.Infer` returns a `teleport`-typed `InferenceResult` whose `SuggestedExpect = "playerZone() == <expectedZone>"` (matching the engine's synthesised expect for `TeleportStep`). Confirming the Record-Step modal records a `TeleportStep { AetheryteId = N }` into the draft. CI red → CI green when (a) the snapshot field, (b) the aggregator setter/consumer, (c) the new inference rule, and (d) the `RecordStep` clearing are wired up.

This plan covers **engine-side (xUnit-testable)** wiring and **UIObserver-side polling**. The exact teleport addon name(s) require in-game discovery — see §F O1; the UIObserver tests are written against a `FakeAddonProbe` regardless of which addon name production uses, so the spec is unblocked.

---

## Dependency graph

```
QuestForge.Engine.Authoring
  ├── GameStateSnapshot              (add TeleportCompleted property)
  ├── SnapshotAggregator             (add OnTeleportCompleted / OnTeleportConsumed)
  ├── InferredFrom (enum)            (add TeleportCompleted)
  └── StepInferenceEngine            (add Rule 4.0 — TeleportCompleted, fires before Rule 4)
        ↓
QuestForge.Engine.Authoring.StepFactory  (route stepType="teleport" → TeleportStep)
        ↓
QuestForge.Plugin.Authoring.AuthoringHost.RecordStep  (clear TeleportCompleted after consume)
        ↓
QuestForge.Plugin.Tracing.UIObserver.PollTeleportDestination  (new poller; mirrors PollAethernetDestination)
        ↓
QuestForge.Plugin.Tracing.IAddonProbe   (optional: add probe accessor for the teleport addon — see §F O1)
        ↓
QuestForge.Plugin.Tracing.DalamudAddonProbe  (concrete impl over GameGui)
```

**Build order:**
1. Engine surface (snapshot field + aggregator setters + inference rule + StepFactory arm) — fully testable in `QuestForge.Engine.Tests` with no Dalamud.
2. `IAddonProbe` extension(s) — accessor methods for the teleport addon (only if name(s) are known; otherwise stop here, run §F discovery, then resume).
3. `UIObserver.PollTeleportDestination` — testable in `QuestForge.Plugin.Tests` against `FakeAddonProbe`.
4. `DalamudAddonProbe` concrete probe — manually validated in-game.
5. `AuthoringHost.RecordStep` clearing — small edit, covered by existing host tests if any (otherwise smoke-test).

---

## Architectural decisions (read before coding)

### Decision I1 — `TeleportCompleted: AetheryteId?` is a separate snapshot field, not folded into `AethernetTeleportCompleted`

`AethernetTeleportCompleted: AethernetHop?` already exists, signaling an **intra-region shard hop** opened via the `TelepotTown` menu. Cross-region **teleport** uses a *different* addon flow and produces a different draft step type (`TeleportStep`, not `TravelStep` with `RouteHint.Aethernet`). Mixing the two would force every Rule-4 consumer to disambiguate after the fact and would break the established invariant "TelepotTown ⇒ aethernet, Teleport menu ⇒ teleport."

Rejected alternatives:
- *Single `LastTeleportSignal` union record* with a `Kind` tag. Adds an enum + pattern matching everywhere `AethernetTeleportCompleted` is currently read. The two events never co-occur in a single recording window (the addons are mutually exclusive) so the separation costs nothing.
- *Overload `AethernetTeleportCompleted` with a sentinel `From == null` value* meaning "this was a regular teleport." Ambiguous: `AethernetHop.From` is *legitimately* nullable for aethernet (when the departure shard is uncaptured). Overloading the sentinel breaks the aethernet path.

**Concrete shape (the simplest type that suffices):**

```csharp
// QuestForge.Engine/Authoring/GameStateSnapshot.cs — appended non-positional property
// Cleared by OnTeleportConsumed in RecordStep so it does not bleed into the next window.
public AetheryteId? TeleportCompleted { get; init; }
```

`AetheryteId` here is `QuestForge.Adapters.Types.AetheryteId` — the same type already used by `LastAethernetShardInteracted` and `LastAttuned`. No richer record is required because:

- The destination zone is recoverable via `AetheryteZoneMap.TryGetZone(id, out var zone)` (already populated from Lumina at plugin start).
- Cost/gil-deducted is not needed for inference — see Decision I7.
- The source zone is `before.Zone` in the inference window; not a separate field.

If later we need richer data (e.g. distinguishing "actually arrived" vs "menu confirmed but stuck loading"), upgrade to `record class TeleportCompletion(AetheryteId To, …)` — non-breaking because the field is non-positional.

### Decision I2 — Aggregator setter/consumer mirror the aethernet pair exactly

```csharp
// QuestForge.Engine/Authoring/SnapshotAggregator.cs
private AetheryteId? _teleportCompleted;

public GameStateSnapshot Current => /* …existing init… */ with { TeleportCompleted = _teleportCompleted };
//  (in practice: add `TeleportCompleted = _teleportCompleted,` to the existing object-initializer list
//   inside the Current property body — mirrors AethernetTeleportCompleted)

/// <summary>
/// Called by UIObserver.PollTeleportDestination when the Teleport menu closes after a
/// destination was selected. Records the destination aetheryte for inference. Survives
/// ResetDeltas; cleared by OnTeleportConsumed (called from AuthoringHost.RecordStep).
/// </summary>
public void OnTeleportCompleted(AetheryteId destination) => _teleportCompleted = destination;

/// <summary>
/// Called at the end of RecordStep to consume the completed teleport so it does not bleed
/// into the next recording window. Mirrors OnAethernetTeleportConsumed exactly.
/// </summary>
public void OnTeleportConsumed() => _teleportCompleted = null;
```

`OnTeleportCompleted` does **not** update `LastAethernetShardInteracted` (unlike `OnAethernetTeleportCompleted`, which does — for Rule 2.5/2.7 attune detection). A cross-region teleport is not an attunement: the player can teleport only to *already-attuned* aetherytes, so spuriously firing Rule 2.5 from a teleport would draft a wrong `attune` step.

**What breaks if violated:** if `_teleportCompleted` were re-used for an aethernet hop or vice-versa, the new Rule 4.0 (Decision I3) would fire on a non-teleport event and draft a wrong step type.

### Decision I3 — New inference rule fires **before** Rule 4 (aethernet/dialog), labelled "Rule 4.0 — Teleport completed"

`StepInferenceEngine.Infer` is a top-down priority cascade. The new rule sits at the head of the zone-change block. It checks `after.TeleportCompleted` *before* the existing aethernet/dialog/zone-change sub-cases, so a real teleport is never mis-classified as `travel`.

**Why before Rule 4 and not earlier:** Rules 1–3 (QuestCompleted / QuestAccepted / ForeignQuestAccepted) take precedence over *any* travel event, because finishing or accepting a quest is always the dominant signal even if it coincides with a teleport. Combat (2.2), purchase (2.2b), pickup/handover (2.3–2.6), and attunement (2.5) likewise must not be overridden by an incidental teleport landing in the same window. The earliest correct slot is **immediately above the `if (after.Zone != before.Zone)` block** at line 287 of the current `StepInferenceEngine.cs`.

```csharp
// Rule 4.0 — Teleport completed
// Fires when the Teleport menu closes after a destination was selected and the resulting
// zone change has been observed. Takes priority over the Rule-4 aethernet/dialog sub-cases
// because TeleportCompleted is the definitive cross-region teleport signal.
//
// WHY before Rule 4: AethernetTeleportCompleted (TelepotTown) and TeleportCompleted (cross-region)
// are mutually exclusive in production, but the snapshot fields are independent. A defensive check
// ensures that even if both somehow get set in the same window (e.g. an erroneous polling
// transition), the *teleport* signal wins — it is the more specific event (a specific
// AetheryteId destination, with a known zone), while AethernetTeleportCompleted may legitimately
// have a null From and only carries a shard id.
//
// Zone-change requirement: cross-region teleport always changes zone. If TeleportCompleted is
// set but Zone did not change, treat as a stale signal — fall through (do NOT return a teleport
// inference for a same-zone snapshot). Defensive: in production this should never happen because
// the poller only sets the field after observing the destination addon close.
if (after.TeleportCompleted is { } teleAetheryteId
    && after.Zone != before.Zone)
{
    return new InferenceResult(
        StepType:        "teleport",
        SuggestedStepId: $"teleport-to-{teleAetheryteId.Value}",
        SuggestedExpect: $"playerZone() == {after.Zone.Value}",
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.TeleportCompleted,
        Notes:           null);
}
```

**Why `playerZone() == after.Zone.Value` and not `AetheryteZoneMap.TryGetZone(...)`:**
- The engine already does `SynthesizeTeleportExpect` on the step at quest-start time — it will pull the expected zone from `AetheryteZoneMap` regardless of what the author wrote.
- Using the *observed* `after.Zone.Value` keeps the inference engine free of any dependency on `AetheryteZoneMap` (which lives in `QuestForge.Engine.Travel` — same project, but currently `StepInferenceEngine` reads nothing from there; preserving that means the inference engine remains a pure transformation over snapshots).
- If `AetheryteZoneMap` and the observed `after.Zone` disagree (rare: Lumina map error or Lifestream lands on a sub-zone), the *observed* value is the safer authored predicate — re-replaying the resulting quest would still pass on the same character/patch.

### Decision I4 — `InferredFrom.TeleportCompleted` is a new enum value

Existing values: `ZoneChange, QuestFlagChange, QuestSequenceChange, DialogueInteraction, QuestAccepted, QuestCompleted, AttunementChange, MovementChange, Manual, None, InventoryChange, Combat, Purchase`.

Adding `TeleportCompleted` keeps the existing taxonomy intact and lets downstream consumers (trace events, UI badge colours, future analytics) differentiate teleport-derived steps from aethernet-derived ones (which use `ZoneChange`). Rejected alternative: reusing `ZoneChange` — would collapse the distinction in the trace and prevent us from tightening the diagnostic later.

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
    TeleportCompleted,   // NEW
}
```

### Decision I5 — `StepFactory.Build` gains a `"teleport"` arm

Mirrors the existing arms (e.g. `"attune"`). Reads `after.TeleportCompleted.Value` to populate `TeleportStep.AetheryteId`. If `after.TeleportCompleted` is null at the moment `StepFactory.Build` is called for `stepType == "teleport"` (defensive — the inference engine only returns "teleport" when the field is set), fall back to `AetheryteId(0)` so the Builder's validator catches it later; do not throw.

```csharp
"teleport" => new TeleportStep
{
    Id = stepId,
    Expect = expectValue,           // user-edited or null (engine synthesises at quest-start)
    Zone = zoneStr,                  // after.Zone — the destination zone
    RequiredZone = ZoneStr(before?.Zone.Value ?? 0u),  // source zone (where the teleport was initiated)
    AetheryteId = new QuestForge.Schema.AetheryteId(after?.TeleportCompleted?.Value ?? 0u)
},
```

**WHY `RequiredZone = before.Zone`:** the teleport is initiated from the source zone. `RequiredZone` is the engine's pre-step gate; encoding the *source* zone (not the destination) prevents the engine from skipping the step if the player is already at the destination — but actually, for teleport, being already at the destination *should* satisfy the synthesised Expect (`playerZone() == N`) and skip the step before any pre-flight runs. The RequiredZone is mostly cosmetic for teleport but encoding the source preserves authoring intent ("this teleport is supposed to fire while you are in zone M").

If after deeper review (Tester or Builder discovers a contradiction) `RequiredZone` should be `null` for teleport — defer to that. The point here is: do not silently set `RequiredZone = zoneStr` (the *destination* zone) — that would gate the step on its own postcondition.

### Decision I6 — `AuthoringHost.RecordStep` clears `TeleportCompleted` after consuming, mirroring the aethernet pattern

After `_aggregator.OnAethernetTeleportConsumed()` (already present at the end of `RecordStep`), add:

```csharp
_aggregator.OnTeleportConsumed();
```

Same lifecycle as the aethernet event: survives `ResetDeltas` so it remains visible to `PreviewInference`, cleared only after the author confirms the modal so that subsequent ticks (which may still poll a stale "menu was open" state on UIObserver) do not bleed the consumed signal into the next window.

`UIObserver.ResetWindowState` should *also* call `_aggregator?.OnTeleportConsumed()` for symmetry with `OnAethernetTeleportConsumed`, so the per-window reset path is internally consistent.

### Decision I7 — Cost/gil deducted is **not** captured as a corroborating signal

Vendor cost detection (`PurchaseDetection.GilDropped`) is already part of the purchase span and works because a vendor exposes the gil change *after* the purchase. A teleport's gil cost is also observable (it always deducts gil), but the inference rule does not need it:

- Cost varies per character (Aetheryte Tickets, FC privileges, faction bonuses) — using it as a *required* signal would create false negatives.
- The TeleportCompleted addon-close event is already definitive — there is no ambiguity to resolve with a corroborator.
- Tracking gil-drop opens up false-positives from concurrent gil sinks (just bought something, retainer venture cost, etc.).

If the addon-name discovery (§F O1) is inconclusive, we may need a corroborator — defer that to the Builder.

### Decision I8 — `PollTeleportDestination` mirrors `PollAethernetDestination` exactly

```csharp
// QuestForge.Plugin.Tracing/UIObserver.cs — new every-frame poller, added next to PollAethernetDestination
// REQUIRES: IAddonProbe gains accessors for the teleport addon. Names below are PLACEHOLDERS pending §F O1.
//   IsAddonOpen("Teleport")                         — provisional name
//   GetSelectedItemIndex("Teleport")                 — same primitive as TelepotTown
//   GetTeleportDestinationId(idx) → uint?           — NEW probe method; idx → Aetheryte.RowId
//
// Naming: the "teleport addon" candidate set (per §F O1) is { "Teleport", "TeleportTown", a confirm
// dialog like "TeleportConfirm" or "SelectYesno" overlaid on top }. Use the discovered name; do not
// hardcode "Teleport" without confirmation.

// State (mirrors the aethernet block)
private bool   _teleportMenuWasOpen;
private uint?  _pendingTeleportDestId;

private void PollTeleportDestination()
{
    if (_addonProbe is null) return;

    var menuIsOpen = _addonProbe.IsAddonOpen(TeleportAddonName); // see §F O1

    if (!menuIsOpen)
    {
        // Menu transitioned open → closed.
        if (_teleportMenuWasOpen && _pendingTeleportDestId.HasValue)
        {
            var destId = _pendingTeleportDestId.Value;
            var now    = _clock.UtcNow;
            var runId  = CurrentRunId;
            WriteObservation("TeleportCompleted", destId, 0u, runId, now);
            _aggregator?.OnTeleportCompleted(new QuestForge.Adapters.Types.AetheryteId(destId));
        }
        _teleportMenuWasOpen     = false;
        _pendingTeleportDestId   = null;
        return;
    }

    _teleportMenuWasOpen = true;

    // Latch the selected destination while the menu is open.
    var selectedIdx = _addonProbe.GetSelectedItemIndex(TeleportAddonName);
    if (selectedIdx.HasValue && selectedIdx.Value >= 0)
    {
        var destId = _addonProbe.GetTeleportDestinationId(selectedIdx.Value);
        if (destId.HasValue) _pendingTeleportDestId = destId;
    }
}
```

Add the call into `OnFrameworkUpdate` alongside the other every-frame pollers:

```csharp
PollAethernetDestination();
PollTeleportDestination();   // NEW
PollDialogueOption();
…
```

`ResetWindowState` adds:
```csharp
_teleportMenuWasOpen     = false;
_pendingTeleportDestId   = null;
_aggregator?.OnTeleportConsumed();   // symmetry with OnAethernetTeleportConsumed
```

**WHY fire on addon-close, not on zone-change:** the addon-close event is observable in the same tick the menu is dismissed (player clicked Confirm or game auto-confirmed). The zone change follows asynchronously after the loading screen. Firing on addon-close lets the snapshot be ready for `PreviewInference` even if the player opens the Record-Step modal mid-loading-screen — by the time the zone change is observed, both `Zone` and `TeleportCompleted` are set, and Rule 4.0 sees a consistent (post-zone-change, post-teleport-event) snapshot. Firing on zone-change instead would require a separate "pending teleport id" buffer that survives across UIObserver heartbeats — strictly worse.

**Cancellation handling:** menu opens then closes with no `selectedIdx` ever set → `_pendingTeleportDestId` stays null → the close branch is a no-op. Same as aethernet's cancellation handling. Tested by U-4 in §E.

### Decision I9 — `IAddonProbe` gains a single new method; existing methods are reused

Following the `GetTelepotTownDestinationId(int idx)` pattern:

```csharp
// QuestForge.Plugin.Tracing/IAddonProbe.cs — append one method
public interface IAddonProbe
{
    bool IsAddonOpen(string addonName);
    int? GetSelectedItemIndex(string addonName);
    string? GetTelepotTownDestinationName(int idx);
    uint?   GetTelepotTownDestinationId(int idx);
    uint?   GetTeleportDestinationId(int idx);          // NEW: resolves teleport-menu idx → Aetheryte.RowId
}
```

`IsAddonOpen` and `GetSelectedItemIndex` are addon-name-parameterised already, so they cover the teleport addon for free once the name is known. Only the per-addon `GetTeleportDestinationId` accessor needs adding — the data layout in the Teleport addon's `AtkValues` is almost certainly different from `TelepotTown` (different addon, different schema), and resolving an idx to an Aetheryte RowId is per-addon logic.

The `FakeAddonProbe` in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (lines 66–89) gains:

```csharp
private readonly Dictionary<int, uint?> _teleportDestinationIds = new();
public void SetTeleportDestination(int idx, uint? rowId) => _teleportDestinationIds[idx] = rowId;
public uint? GetTeleportDestinationId(int idx) =>
    _teleportDestinationIds.TryGetValue(idx, out var v) ? v : null;
```

### Decision I10 — Trace observation event method is `"TeleportCompleted"` (separate from `"AethernetTeleportCompleted"`)

The trace stream is consumed by `qf-trace extract-quest` (Phase 10) to reconstruct quest definitions. Using a distinct method name lets the extractor route the event to a `TeleportStep` arm directly, parallel to how `AethernetTeleportCompleted` is routed to an aethernet-travel arm. Mirroring the existing `WriteObservation("AethernetTeleportCompleted", toId, _pendingAethernetFromId ?? 0u, …)` pattern:

```csharp
WriteObservation("TeleportCompleted", destId, 0u, runId, now);
```

`argument` carries the destination aetheryte id; `value` is `0u` (placeholder for future enrichment, e.g. cost). Decision T1's "no richer record needed" applies: keep this minimal.

### Decision I11 — Do **not** auto-update `LastAttuned` from `OnTeleportCompleted`

`AethernetTeleportCompleted` sets `_lastAethernetShardInteracted` as a side effect so Rule 2.5/2.7 see the shard. Teleport must NOT do this — the destination aetheryte is a *main* aetheryte (not a shard) and the player by definition is already attuned to it (otherwise the teleport would have been refused). Setting `_lastAttuned` would risk inferring a spurious `attune` step on the next Record cycle.

---

## Snapshot field summary

| Field (new or existing) | Type | Set by | Cleared by | Survives ResetDeltas? |
|---|---|---|---|---|
| `TeleportCompleted` (NEW) | `AetheryteId?` | `OnTeleportCompleted` | `OnTeleportConsumed` | yes |
| `AethernetTeleportCompleted` (existing) | `AethernetHop?` | `OnAethernetTeleportCompleted` | `OnAethernetTeleportConsumed` | yes |
| `LastAethernetShardInteracted` (existing) | `AetheryteId?` | `OnAethernetShardTargeted` / `OnAethernetTeleportCompleted` | `ResetDeltas` only | no |
| `LastAttuned` (existing) | `AetheryteId?` | `OnAttunementChanged` | never (session-scoped) | yes |

The new `TeleportCompleted` shares the **aethernet event lifecycle** (per-window event, cleared in `RecordStep`), not the attunement lifecycle.

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
| 3   | QuestSequence advanced | talk | QuestSequenceChange | High |
| **4.0 (NEW)** | **after.TeleportCompleted set AND zone changed** | **teleport** | **TeleportCompleted** | **High** |
| 4   | Zone changed (aethernet / NPC dialogue / shard / catch-all) | travel | ZoneChange | High |
| 2.7 | AethernetTeleportCompleted set, same zone | travel | ZoneChange | High |
| 5   | QuestFlags changed, sequence unchanged | talk | QuestFlagChange | Medium |
| 6   | LastDialogueAnswer changed | talk | DialogueInteraction | Medium |
| 7   | LastNpcInteracted changed | talk | DialogueInteraction | Low |
| 8   | Player moved >5u, same zone | travel | MovementChange | Low |
| 9   | nothing matched | Empty | None | Low |

Rule 4.0 occupies the position **immediately before** Rule 4 in the source file. It does **not** run when `Zone` is unchanged — if `TeleportCompleted` is set but `Zone == Zone`, fall through; the existing Rule 9 (nothing matched) handles the no-op case.

---

## Task breakdown

### Task IT-1 — Engine: `GameStateSnapshot` adds `TeleportCompleted`

1. Edit `QuestForge.Engine/Authoring/GameStateSnapshot.cs`.
2. Append a non-positional property:
   ```csharp
   // Non-positional. Set when a cross-region teleport completes this recording window
   // (Teleport menu closed after a destination was selected). Cleared by OnTeleportConsumed
   // in RecordStep so it does not bleed into the next inference window.
   // Distinct from AethernetTeleportCompleted (which signals an intra-region shard hop).
   public AetheryteId? TeleportCompleted { get; init; }
   ```
3. No other existing tests should change behaviour (additive property).

### Task IT-2 — Engine: `SnapshotAggregator.OnTeleportCompleted` / `OnTeleportConsumed`

1. Edit `QuestForge.Engine/Authoring/SnapshotAggregator.cs`.
2. Add the backing field `private AetheryteId? _teleportCompleted;`.
3. Add `TeleportCompleted = _teleportCompleted,` to the object-initializer in the `Current` property body, alongside `AethernetTeleportCompleted = _aethernetTeleportCompleted`.
4. Add the setter and consumer methods per Decision I2.
5. Do **not** touch `LastAttuned` or `_lastAethernetShardInteracted` from these methods (Decision I11).
6. Do **not** clear in `ResetDeltas` (survives per-window lifecycle; only `OnTeleportConsumed` clears).

### Task IT-3 — Engine: `InferredFrom.TeleportCompleted` enum value

1. Edit `QuestForge.Engine/Authoring/InferredFrom.cs`.
2. Append `TeleportCompleted,` to the enum (Decision I4).

### Task IT-4 — Engine: `StepInferenceEngine.Rule 4.0`

1. Edit `QuestForge.Engine/Authoring/StepInferenceEngine.cs`.
2. Insert the rule per Decision I3 immediately above the existing `if (after.Zone != before.Zone)` block.
3. Predicate references `after.Zone.Value` — do not import `AetheryteZoneMap`.
4. The `SuggestedStepId` pattern is `teleport-to-{aetheryteId}` so that two consecutive teleports to different aetherytes do not collide.

### Task IT-5 — Engine: `StepFactory` `"teleport"` arm

1. Edit `QuestForge.Engine/Authoring/StepFactory.cs`.
2. Add the `"teleport"` arm per Decision I5 — placed in the existing `stepType switch` block alongside `"attune"`.
3. Confirm `using QuestForge.Schema;` is already in scope (yes — line 3).

### Task IT-6 — Plugin: `AuthoringHost.RecordStep` clearing

1. Edit `QuestForge.Plugin/Authoring/AuthoringHost.cs`.
2. At the end of `RecordStep`, alongside the existing two consume calls:
   ```csharp
   _aggregator.OnAethernetTeleportConsumed();
   _aggregator.OnDialogueOptionConsumed();
   _aggregator.OnTeleportConsumed();   // NEW
   ```

### Task IT-7 — Plugin: `IAddonProbe` extension + `FakeAddonProbe` extension

1. Edit `QuestForge.Plugin.Tracing/IAddonProbe.cs` — add `uint? GetTeleportDestinationId(int idx);`.
2. Edit `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (the `FakeAddonProbe` inside) — add the `_teleportDestinationIds` dict, the `SetTeleportDestination` helper, and the `GetTeleportDestinationId` accessor.
3. `DalamudAddonProbe` implementation is deferred — see §F O1 (Builder needs the addon name and AtkValues layout to implement).

### Task IT-8 — Plugin: `UIObserver.PollTeleportDestination`

1. Edit `QuestForge.Plugin.Tracing/UIObserver.cs`.
2. Add the per-window state fields (`_teleportMenuWasOpen`, `_pendingTeleportDestId`).
3. Add `PollTeleportDestination()` method per Decision I8.
4. Wire the call into `OnFrameworkUpdate` immediately after `PollAethernetDestination`.
5. Wire the reset into `ResetWindowState` (clear both state fields + call `OnTeleportConsumed`).
6. Define `TeleportAddonName` as `const string` at the top of the class. **Initial value `"Teleport"` is a placeholder** — Builder must confirm via §F O1 discovery before merging the production implementation. Tests use whatever name is passed; production code must verify.

### Task IT-9 — Plugin: `DalamudAddonProbe.GetTeleportDestinationId`

**BLOCKED on §F O1.** When the addon name and AtkValues layout are confirmed, implement following the `GetTelepotTownDestinationName` + `GetTelepotTownDestinationId` pattern:
1. Look up the addon by name.
2. Read the AtkValue at the documented offset (TBD — discover via `/qf debug addon <name>`).
3. Resolve the destination name → `Aetheryte.RowId` using a Lumina lookup (the same `Aetheryte` sheet already used in `GetAethernetNameMap`, but filtered to `row.IsAetheryte == true` — main aetherytes, not shards).

If the addon exposes the Aetheryte RowId directly (some addons do), skip the name-resolution dictionary entirely.

---

## Validation rules (this plan adds none)

The validator-side `structural/teleport-aetheryte-id-missing` and `structural/teleport-aetheryte-not-in-map` rules are still deferred (per `TELEPORT_STEP_PLAN.md` §Validation rules). The authoring path is downstream of validation: a draft containing a `TeleportStep { AetheryteId = 0 }` (defensive fallback in Decision I5) will be caught when the draft is exported.

---

## Given-When-Then test scenarios

Tests are split into two files:
- **`QuestForge.Engine.Tests/Authoring/TeleportInferenceTests.cs`** — pure inference-engine + snapshot + aggregator + StepFactory tests (no Dalamud, no UIObserver).
- **`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`** — UIObserver tests extending the existing fixture; add new `[Fact]` methods alongside `UO_F*` (aethernet) tests.

The classification (engine vs UIObserver) is noted on each scenario.

### Inference-engine tests (engine project)

#### T1 — Happy path: zone change + TeleportCompleted set → infers `teleport` step

**Test class:** `TeleportInferenceTests`
**Given:**
- `before = MakeSnapshot(zone: ZoneId(132), teleportCompleted: null)` (Old Gridania)
- `after  = MakeSnapshot(zone: ZoneId(129), teleportCompleted: new AetheryteId(8))` (Limsa Lower Decks; aetheryte 8)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "teleport"`
- `result.SuggestedStepId == "teleport-to-8"`
- `result.SuggestedExpect == "playerZone() == 129"`
- `result.Confidence == Confidence.High`
- `result.InferredFrom == InferredFrom.TeleportCompleted`
- `result.Notes == null`

#### T2 — Priority over Aethernet: both `TeleportCompleted` and `AethernetTeleportCompleted` set, teleport wins

**Test class:** `TeleportInferenceTests`
**Given:**
- `before = MakeSnapshot(zone: ZoneId(132))`
- `after  = MakeSnapshot(zone: ZoneId(129), teleportCompleted: new AetheryteId(8),
                          aethernetTeleportCompleted: new AethernetHop(null, new AethernetId(99)))`
  - (defensive — production should never set both, but the fields are independent)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "teleport"` (NOT `"travel"`)
- `result.SuggestedStepId == "teleport-to-8"`
- `result.InferredFrom == InferredFrom.TeleportCompleted`

**Rationale (justifies the priority order):** TeleportCompleted carries a **specific main aetheryte ID** with a known destination zone; AethernetTeleportCompleted may have null `From` and only points at a shard. The teleport signal is strictly more specific.

#### T3 — Priority over NPC dialog: zone change + TeleportCompleted + DialogueOptionSelected, teleport wins

**Test class:** `TeleportInferenceTests`
**Given:**
- `before = MakeSnapshot(zone: ZoneId(132), lastNpcInteracted: new NpcId(1234567))`
- `after  = MakeSnapshot(zone: ZoneId(129), teleportCompleted: new AetheryteId(8),
                          dialogueOptionSelected: 0,
                          dialogueNpcSource: new NpcLocation(1234567, 132, new Position3(0,0,0)))`
  - (the player happened to have selected a dialogue option moments before teleporting; defensive)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "teleport"` (NOT `"travel"` with npc-dialogue-hint)
- `result.InferredFrom == InferredFrom.TeleportCompleted`

#### T4 — No-op: zone change without TeleportCompleted → falls through to existing Rule 4

**Test class:** `TeleportInferenceTests`
**Given:**
- `before = MakeSnapshot(zone: ZoneId(132))`
- `after  = MakeSnapshot(zone: ZoneId(129), teleportCompleted: null)`
  - (regular cross-zone walk, no teleport)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "travel"` (Rule 4 catch-all)
- `result.SuggestedStepId == "travel-to-zone-129"`
- `result.InferredFrom == InferredFrom.ZoneChange`

This pins "Rule 4.0 falls through when TeleportCompleted is null."

#### T5 — Same-zone TeleportCompleted set (defensive) → falls through (Rule 4.0 zone-change guard)

**Test class:** `TeleportInferenceTests`
**Given:**
- `before = MakeSnapshot(zone: ZoneId(132), position: new WorldPosition(0,0,0))`
- `after  = MakeSnapshot(zone: ZoneId(132), position: new WorldPosition(0,0,0),
                          teleportCompleted: new AetheryteId(8))`
  - (zone did not change — stale signal; player teleported to where they already were, or the addon emitted a phantom close)

**When:** `engine.Infer(before, after)`

**Then:**
- `result == InferenceResult.Empty` (Rule 9 — nothing matched; Rule 4.0's zone-change guard rejects the input, no other rule applies because all the no-op rules also reject this snapshot pair).

This pins Decision I3's "if Zone unchanged, fall through."

#### T6 — Rule 1 (QuestCompleted) wins over Rule 4.0

**Test class:** `TeleportInferenceTests`
**Given:**
- `before = MakeSnapshot(zone: ZoneId(132), questCompleted: false)`
- `after  = MakeSnapshot(zone: ZoneId(129), questCompleted: true,
                          teleportCompleted: new AetheryteId(8))`
  - (player teleported back to Limsa, where the turn-in NPC handed in the quest — both fire in the same window)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "turn-in"` (Rule 1)
- `result.InferredFrom == InferredFrom.QuestCompleted`

This pins "earlier rules take precedence over Rule 4.0" — matches how earlier rules already win over Rule 4 (aethernet/dialog/catch-all).

#### T7 — Aggregator: `OnTeleportCompleted` sets the field; `Current.TeleportCompleted` returns the same id

**Test class:** `TeleportInferenceTests` (or a separate `SnapshotAggregatorTests` slot)
**Given:** `var agg = new SnapshotAggregator(activeQuest: null, clock: new FakeClock(T0));`

**When:**
- `agg.OnTeleportCompleted(new AetheryteId(8));`
- `var snap = agg.Current;`

**Then:**
- `snap.TeleportCompleted == new AetheryteId(8)`.

#### T8 — Aggregator: `OnTeleportConsumed` clears the field

**Test class:** `TeleportInferenceTests`
**Given:** `agg.OnTeleportCompleted(new AetheryteId(8));` (T7's setup)

**When:** `agg.OnTeleportConsumed();`

**Then:** `agg.Current.TeleportCompleted is null`.

#### T9 — Aggregator: `OnTeleportCompleted` does NOT update `LastAttuned`

**Test class:** `TeleportInferenceTests`
**Given:** `var agg = new SnapshotAggregator(null, new FakeClock(T0));` (LastAttuned starts null)

**When:** `agg.OnTeleportCompleted(new AetheryteId(8));`

**Then:**
- `agg.Current.TeleportCompleted == new AetheryteId(8)`
- `agg.Current.LastAttuned is null` (pins Decision I11 — teleport must not set attunement state)
- `agg.Current.LastAethernetShardInteracted is null` (pins Decision I2 — teleport must not set shard state)

#### T10 — Aggregator: `ResetDeltas` does NOT clear `TeleportCompleted`

**Test class:** `TeleportInferenceTests`
**Given:** `agg.OnTeleportCompleted(new AetheryteId(8));`

**When:** `agg.ResetDeltas();`

**Then:** `agg.Current.TeleportCompleted == new AetheryteId(8)` (survives `ResetDeltas`; only `OnTeleportConsumed` clears).

#### T11 — `StepFactory.Build("teleport", …)` produces a `TeleportStep` with the aetheryte id from the snapshot

**Test class:** `TeleportInferenceTests`
**Given:**
- `before = MakeSnapshot(zone: ZoneId(132), position: new WorldPosition(5, 0, 10))`
- `after  = MakeSnapshot(zone: ZoneId(129), teleportCompleted: new AetheryteId(8))`

**When:** `var step = StepFactory.Build("teleport", "teleport-to-8", "playerZone() == 129", after, before);`

**Then:**
- `step is TeleportStep ts`
- `ts.Id == "teleport-to-8"`
- `ts.AetheryteId.Value == 8u`
- `ts.Expect is PredicateExpect { Predicate: "playerZone() == 129" }`
- `ts.Zone == "129"` (destination)
- `ts.RequiredZone == "132"` (source)

#### T12 — `StepFactory.Build("teleport", …)` defensive: TeleportCompleted null → AetheryteId(0)

**Test class:** `TeleportInferenceTests`
**Given:** `after = MakeSnapshot(zone: ZoneId(129), teleportCompleted: null)`

**When:** `var step = StepFactory.Build("teleport", "teleport-to-X", null, after);`

**Then:**
- `step is TeleportStep ts`
- `ts.AetheryteId.Value == 0u` (defensive fallback; validator will catch)
- No exception thrown.

Pins Decision I5's defensive behaviour.

### UIObserver tests (plugin tests project)

These tests use the existing `BuildFixtureWithAggregator` helper and the `FakeAddonProbe` extension from Task IT-7. Test naming follows `UO_X*` convention; new group letter is `UO_J`.

#### U1 (UO_J1) — Menu opens, no immediate observation

**Test class:** `UIObserverTests`
**Given:**
- `var (obs, fw, ap, gp, clock, writer, _, _) = BuildFixtureWithAggregator();`
- `ap.OpenAddon(TeleportAddonName);  ap.SetSelectedIndex(TeleportAddonName, 0);  ap.SetTeleportDestination(0, 8u);`

**When:** `fw.Tick();`

**Then:**
- No `ObservationEvent` with `Method == "TeleportCompleted"` written.
- Aggregator: `aggregator.Current.TeleportCompleted is null`.

Mirrors `UO_F1`.

#### U2 (UO_J2) — Menu opens then closes with selection → fires `TeleportCompleted`

**Test class:** `UIObserverTests`
**Given:**
- `ap.OpenAddon(TeleportAddonName);  ap.SetSelectedIndex(TeleportAddonName, 0);  ap.SetTeleportDestination(0, 8u);`
- `fw.Tick();` (menu observed open, destination latched as `8u`)
- `ap.CloseAddon(TeleportAddonName);` (menu closes)

**When:** `fw.Tick();` (close-transition fires)

**Then:**
- An `ObservationEvent` with `Method == "TeleportCompleted"`, `Argument == 8u`.
- `aggregator.Current.TeleportCompleted == new AetheryteId(8)`.

Mirrors `UO_F2`.

#### U3 (UO_J3) — Menu closes without a selection ever set → no observation, field stays null

**Test class:** `UIObserverTests`
**Given:**
- `ap.OpenAddon(TeleportAddonName);` (no SetSelectedIndex, no SetTeleportDestination)
- `fw.Tick();` (menu open observed; nothing latched)
- `ap.CloseAddon(TeleportAddonName);`

**When:** `fw.Tick();`

**Then:**
- No `ObservationEvent` with `Method == "TeleportCompleted"`.
- `aggregator.Current.TeleportCompleted is null`.

Mirrors `UO_F3`. Pins "cancellation = no signal."

#### U4 (UO_J4) — `IAddonProbe` is null → no observation, no NRE

**Test class:** `UIObserverTests`
**Given:** UIObserver constructed with `addonProbe: null` (existing `UO_A2` pattern).

**When:** `fw.Tick();` (multiple ticks)

**Then:**
- No `ObservationEvent` with `Method == "TeleportCompleted"`.
- No exception thrown.

Mirrors `UO_F4`.

#### U5 (UO_J5) — `ResetWindowState` clears the in-progress menu flag

**Test class:** `UIObserverTests`
**Given:**
- `ap.OpenAddon(TeleportAddonName);  ap.SetSelectedIndex(TeleportAddonName, 0);  ap.SetTeleportDestination(0, 8u);`
- `fw.Tick();` (menu latched as open)
- `obs.ResetWindowState();` (mid-recording state reset)
- `ap.CloseAddon(TeleportAddonName);` (menu closes)

**When:** `fw.Tick();`

**Then:**
- No `ObservationEvent` with `Method == "TeleportCompleted"` (the close-transition fired but `_teleportMenuWasOpen` was reset to false → close branch is a no-op).
- `aggregator.Current.TeleportCompleted is null`.

Mirrors `UO_B2`. Pins Decision I8's `ResetWindowState` symmetry.

#### U6 (UO_J6) — `ResetWindowState` also calls `OnTeleportConsumed` (defensive)

**Test class:** `UIObserverTests`
**Given:**
- (force a stale field) `aggregator.OnTeleportCompleted(new AetheryteId(8));`
- Pre-condition: `aggregator.Current.TeleportCompleted == new AetheryteId(8)`.

**When:** `obs.ResetWindowState();`

**Then:** `aggregator.Current.TeleportCompleted is null` (the reset clears any stale value, mirroring `OnAethernetTeleportConsumed` in the existing `ResetWindowState`).

#### U7 (UO_J7) — Destination latched mid-menu wins over earlier latched destination

**Test class:** `UIObserverTests`
**Given:**
- `ap.OpenAddon(TeleportAddonName);`
- `ap.SetSelectedIndex(TeleportAddonName, 0);  ap.SetTeleportDestination(0, 8u);`
- `fw.Tick();` (latches 8u)
- `ap.SetSelectedIndex(TeleportAddonName, 1);  ap.SetTeleportDestination(1, 56u);`
- `fw.Tick();` (re-latches 56u)
- `ap.CloseAddon(TeleportAddonName);`

**When:** `fw.Tick();`

**Then:**
- `aggregator.Current.TeleportCompleted == new AetheryteId(56)` (the most recent latch wins; player scrolled through the list and confirmed the second destination).

Pins "the latch is most-recent-wins" — important because the addon may briefly show an unselected state at open.

#### U8 (UO_J8) — Destination idx with null mapping → no observation

**Test class:** `UIObserverTests`
**Given:**
- `ap.OpenAddon(TeleportAddonName);  ap.SetSelectedIndex(TeleportAddonName, 0);`
  - (idx is set, but `SetTeleportDestination(0, null)` — accessor returns null because the row didn't resolve)
- `fw.Tick();` (latched nothing because `destId is null`)
- `ap.CloseAddon(TeleportAddonName);`

**When:** `fw.Tick();`

**Then:**
- No `ObservationEvent` with `Method == "TeleportCompleted"`.
- `aggregator.Current.TeleportCompleted is null`.

Pins Decision I8's `if (destId.HasValue) _pendingTeleportDestId = destId;` guard.

### Plan-level scenario classification

| Scenario | File | Type |
|---|---|---|
| T1–T6 | `TeleportInferenceTests` | inference engine |
| T7–T10 | `TeleportInferenceTests` | aggregator |
| T11–T12 | `TeleportInferenceTests` | StepFactory |
| U1–U8 | `UIObserverTests` | UIObserver + FakeAddonProbe |

Total: 12 inference-side + 8 UIObserver-side = **20 new tests**.

---

## F. Open questions / discovery items

### O1 — Addon name(s) for the FFXIV Teleport menu (RESOLVED — `Teleport`)

**Status:** RESOLVED. Confirmed in-game via `/qf debug addon Teleport`: the addon name is exactly `"Teleport"` and it exposes a flat list of region/zone/destination entries in `AtkValues`. Sample dump:

```
Addon 'Teleport' — 1480 AtkValues:
  [7]  String = "La Noscea"                  ← region header
  [14] String = "Limsa Lominsa Lower Decks"  ← destination name
  [16] Int    = 100                          ← cost in gil
  [22] String = "Middle La Noscea"           ← region (next row)
  [23] String = "Summerford Farms"           ← destination
  [24] Int    = 145                          ← cost
```

The AtkValues layout uses ~8 slots per visible row (region/zone/destination/cost). The destination Aetheryte RowId is not directly visible in the dump — the Builder will need to either (a) read it from a different AtkValue slot not shown above, or (b) resolve destination strings to Aetheryte RowIds via Lumina (same pattern as `DalamudAddonProbe.GetAethernetNameMap`).

**Builder no longer blocked.** Tasks IT-8 and IT-9 can proceed: `TeleportAddonName = "Teleport"`. AtkValues layout investigation happens during Phase F implementation.

**Candidate names** (best guesses, ordered by likelihood):
- `"Teleport"` — the general-action menu showing the list of attuned aetherytes.
- `"TeleportConfirm"` — possible confirmation sub-addon shown before warp.
- `"SelectYesno"` — generic confirm overlay reused by the teleport flow.
- `"TeleportSelectMode"` / `"TeleportTownMap"` — speculative.

**Discovery recommendation:**
1. Add a `/qf debug enumerate-addons` chat command that lists every currently-open addon (similar to the existing `/qf debug addon <name>` but enumerating instead of probing by name). Add this to `QuestForge.Plugin/Commands/QfCommand.cs` near the existing `case "debug" when parts.Length >= 3 && parts[1] == "addon":` handler.
   - Implementation: walk `RaptureAtkUnitManager.Instance()->AllLoadedUnitsList` (FFXIVClientStructs) and print each `AtkUnitBase.NameString` that is `IsVisible`. The `enumerator` is a 5-line helper.
2. Run in-game: open the Teleport general action → observe console; pick a destination → observe console at each step (selection, confirm, loading screen).
3. For each candidate addon, also run `/qf debug addon <name>` (existing command) to dump its `AtkValues` and locate which slot holds the selected destination's name or RowId.

**Alternative discovery (no new command):**
- Use `/xldata ai` (Dalamud's built-in addon inspector) while the Teleport menu is open. This already enumerates open addons and is what `DalamudAddonProbe`'s existing implementations were derived from.
- Manual user testing using the existing `/qf debug addon Teleport` (or guessed name).

**Resolution path:** once the addon name and the AtkValue layout are known, Task IT-9 unblocks. If the addon does not expose the destination Aetheryte RowId directly (only a name string), build a `Dictionary<string, uint>` from Lumina's `Aetheryte` sheet filtered to `IsAetheryte == true` (the master crystals), mirroring `DalamudAddonProbe.GetAethernetNameMap`.

### O2 — Text-command teleport (`/teleport <name>`) — same addon or different?

**Status:** unknown. Two possibilities:
- The text command opens the same `"Teleport"` addon (or whatever it is) and our poller handles it for free.
- The text command bypasses the menu entirely and goes straight into the warp — no addon-close event would fire, so no inference signal is produced.

If (b), we have two options:
- Accept that `/teleport` is not auto-inferred. Document this limitation; the author can still manually add the step via the Record-Step modal's freeform editor.
- Detect via a different signal: poll for a zone change immediately preceded by gil decrement, with no aethernet menu involved. This is the Decision I7 "use cost as corroborator" path we rejected; revisit only if (a) does not hold and (b) Authors complain.

Recommendation: defer. Document the limitation; do not gold-plate now.

### O3 — Confirmation dialog handling

If the Teleport flow includes a confirmation sub-addon (e.g. for cross-DC teleport that warns about cost), the close-transition of the main `"Teleport"` addon may fire *before* the player confirms. In that case:
- The poller would fire `TeleportCompleted` on the main close, but the player might cancel the confirm and end up not teleporting.
- The subsequent zone-change check in Rule 4.0 would not fire (Zone unchanged) → falls through to Rule 9 (Empty). Net: no false-positive step inferred.

This is **safe** by construction (the zone-change guard catches it), so no extra handling required. Tested by T5.

If the confirmation addon's existence and naming is discovered during O1, mention it in code comments so future maintainers know the flow.

### O4 — Should the inference rule require a minimum zone-change delay, in case the menu close fires before `Zone` updates?

**Likely no.** `Zone` is updated synchronously via the `IClientState.TerritoryChanged` event subscribed in `AuthoringHost`. The Teleport menu close fires via the addon-close transition, which is a polled state — there is a 1-frame race window where the menu has closed but the loading screen has not yet ended.

In practice: `PreviewInference` is called when the author clicks Record in the modal, which happens *after* the loading screen — by then both `Zone` and `TeleportCompleted` are coherent. The race is closed by the user's reaction time (they cannot click Record during a loading screen).

If a regression appears in-game where Rule 4.0 fires with stale `Zone == before.Zone`, the fall-through behaviour (T5) is the safe default. No fix required.

---

## Implementation order

**Phase A — Engine surface (10 min, all xUnit-testable)**
1. Task IT-1: `GameStateSnapshot.TeleportCompleted` property.
2. Task IT-3: `InferredFrom.TeleportCompleted` enum value.
3. Task IT-2: `SnapshotAggregator.OnTeleportCompleted` / `OnTeleportConsumed` methods + `Current` initializer.
4. Tester: write T7–T10 (aggregator + snapshot tests). Make them red, then implement IT-1–IT-3 to green.

**Phase B — Inference rule (10 min)**
1. Task IT-4: insert Rule 4.0 in `StepInferenceEngine`.
2. Tester: write T1–T6 (inference tests). Make them red, then implement IT-4 to green.

**Phase C — StepFactory (5 min)**
1. Task IT-5: add `"teleport"` arm to `StepFactory.Build`.
2. Tester: write T11–T12. Make them red, then implement to green.

**Phase D — AuthoringHost clearing (2 min)**
1. Task IT-6: add `_aggregator.OnTeleportConsumed();` to `RecordStep`.
2. No new test in this plan (covered structurally by IT-2's aggregator tests; the host wiring is a one-line edit verified in-game).

**Phase E — UIObserver tests + impl (30 min)**
1. Task IT-7: extend `IAddonProbe` and `FakeAddonProbe`.
2. Tester: write U1–U8 (`UO_J*`). Red.
3. Task IT-8: implement `PollTeleportDestination`, `ResetWindowState` updates. Green.
4. **Use placeholder `TeleportAddonName = "Teleport"`** in the production code; document that this is unconfirmed pending §F O1.

**Phase F — Dalamud probe (BLOCKED on §F O1)**
1. Builder runs `/qf debug enumerate-addons` discovery in-game (per §F O1).
2. Update `TeleportAddonName` constant to the confirmed name.
3. Implement Task IT-9: `DalamudAddonProbe.GetTeleportDestinationId`.
4. Manual in-game smoke test: teleport from Limsa to Gridania → confirm draft contains `TeleportStep { AetheryteId = 2 }` (Gridania) with `Expect = playerZone() == 132`.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~TeleportInferenceTests` reports all 12 inference-side tests green.
2. `dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverTests&FullyQualifiedName~UO_J"` reports all 8 UIObserver tests green.
3. No regression in existing `StepInferenceEngineTests`, `AethernetInferenceTests`, `AethernetStepFactoryTests`, or `UIObserverTests` (UO_A–UO_I).
4. The trace stream emitted during a teleport contains an `ObservationEvent` with `Method == "TeleportCompleted"` and `Argument == <aetheryteId>`.
5. **In-game smoke (after Phase F):** With Author mode enabled for any quest, manually teleporting to a known main aetheryte produces a draft `TeleportStep { AetheryteId = N }` in the recorded steps, with the synthesised expect satisfied on arrival.

---

## Exclusions (what this plan does NOT include)

- **Discovery of the production teleport addon name** — flagged as §F O1; Builder runs in-game discovery before Phase F.
- **Text-command teleport** (`/teleport <name>`) — see §F O2; deferred unless evidence shows it produces a different signal worth handling.
- **Confirmation-dialog handling** — see §F O3; the zone-change guard makes any confirmation-cancellation a safe no-op.
- **Cost / gil deduction tracking** — Decision I7. Defer until/unless the primary addon signal proves unreliable.
- **NG+ / DT cross-DC teleport semantics** — deferred. The plan handles the common-case intra-world teleport only.
- **`/qf debug enumerate-addons` chat command** — suggested in §F O1 but optional (the Builder may use `/xldata ai` instead). If implemented, it is a 5-line helper that lists `AtkUnitBase.NameString` for every visible addon.
- **Trace-side extractor work** (`qf-trace extract-quest` route for `TeleportCompleted`) — Phase 10 follow-up; out of scope here.
- **`TeleportStep` validator rules** — still deferred per `TELEPORT_STEP_PLAN.md` §Validation rules.
- **A separate `Confidence.Medium` arm for cost-only inference** — Decision I7 rejected; if added later, slot below Rule 4.0 with a distinct `InferredFrom.TeleportInferredFromCost` value.
- **Live re-targeting of an already-attuned aetheryte** as an attune step — already prevented by the existing Rule 2.5 guards; not exacerbated by this plan because Decision I11 forbids `OnTeleportCompleted` from touching `LastAttuned` / `LastAethernetShardInteracted`.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 2 scenarios (T1, T7) + 1 UIObserver happy path (U2)
- Edge cases: 7 scenarios (T2, T3, T8, T9, T10, T11) + 4 UIObserver edge cases (U1, U5, U6, U7)
- Error / no-op cases: 3 scenarios (T4, T5, T12) + 3 UIObserver error cases (U3, U4, U8)
- Priority pinning: 1 scenario (T6)
- Expected total: ~20 tests (12 in `QuestForge.Engine.Tests/Authoring/TeleportInferenceTests.cs`, 8 in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` as new `UO_J*` group)

Builder is **blocked on §F O1 addon-name discovery** before Tasks IT-8 (production `TeleportAddonName` const) and IT-9 (`DalamudAddonProbe.GetTeleportDestinationId`). All other tasks (IT-1–IT-7) are fully unblocked and testable.
