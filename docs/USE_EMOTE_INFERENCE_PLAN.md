# UseEmoteStep Authoring Inference Plan

**Status:** ready for test creation (with one open question — see §F O1)

**Input docs:**
- `docs/USE_ACTION_INFERENCE_PLAN.md` (the closest analog spec — UAI1..UAI14, UO_K1..K10; mirror its decisions wherever applicable)
- `docs/USE_EMOTE_STEP_PLAN.md` (engine side — `IEmoteExecutor`, `EngineAction.UseEmote`, `ResolveUseEmote` already shipped; pinned `UseEmoteStep { EmoteId, TargetNpcId?, Motion }` shape in §UE3)
- `docs/USE_EMOTE_DALAMUD_PLAN.md` (Dalamud impl already shipped; pinned `Chat.SendMessage` + Lumina preload)
- `QuestForge.Engine/Authoring/StepInferenceEngine.cs:264-284` — existing Rule 3.5 (`ActionCompleted`) is the placement reference; the new Rule 3.5e sits immediately above it
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs:17-20,143-146` — `ActionCompletedSignal` record + `ActionCompleted` property is the structural analog for `EmoteCompletedSignal` + `EmoteCompleted`
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs:499-515` — `OnActionCompleted` / `OnActionConsumed` pair is the structural analog
- `QuestForge.Engine/Authoring/InferredFrom.cs:19` — `ActionCompleted` enum value; we add `EmoteCompleted`
- `QuestForge.Engine/Authoring/StepFactory.cs:137-144` — `"use-action"` arm is the structural analog for the new `"use-emote"` arm
- `QuestForge.Plugin.Tracing/UIObserver.cs:619-655` — `PollPlayerActionEffect` is the structural analog for the new `PollPlayerEmote`
- `QuestForge.Plugin.Tracing/UIObserver.cs:181,186,222` — `_lastObservedActionSequence = null;` + `OnActionConsumed()` + `PollPlayerActionEffect();` are the three reset/wire points
- `QuestForge.Plugin/Authoring/AuthoringHost.cs:200,273` — `[QF-DIAG] PreviewInference:` line and `RecordStep` consume calls
- `QuestForge.Plugin.Tracing/IGameProbe.cs:15` — `GetLastActionEffect` signature is the surface analog
- `QuestForge.Plugin/Tracing/DalamudGameProbe.cs:76-84` — `GetLastActionEffect` implementation is the cast-pointer pattern reference
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\Control\EmoteController.cs` — canonical struct: `EmoteId: ushort @ 0x14`, `Target: GameObjectId @ 0x18`, `IsEmoting()` and `IsInEmoteLoop()` member functions
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\Character\Character.cs:18` — `EmoteController` field at offset `0x630` on `Character`
- `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs:96-156` — existing `FakeGameProbe` is the extension site; the `_nextActionEffect` pattern transfers 1:1
- `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs:2853-3140` — the `UO_K1..K10` test group is the layout reference for the new `UO_L*` group
- `QuestForge.Plugin.Tests/Tracing/UIObserverCombatForwardingTests.cs:38-65` — `FakeTargetProbe` definition (shared via UIObserverTests assembly)

**Output (CI behavior):** When the player triggers an emote during an Author-mode recording session, the snapshot field `EmoteCompleted` becomes non-null with the `EmoteId` (Lumina row) and optional `TargetBaseId`. `StepInferenceEngine.Infer` returns a `use-emote`-typed `InferenceResult` with `SuggestedExpect = null` (author MUST write the postcondition per `USE_EMOTE_STEP_PLAN.md` Decision UE4), `Confidence.High`, and `InferredFrom.EmoteCompleted`. Confirming the Record-Step modal records a `UseEmoteStep { EmoteId, TargetNpcId?, Motion = true }` into the draft. CI red → CI green when (a) the snapshot field, (b) the aggregator setter/consumer, (c) the new inference rule, (d) the StepFactory `"use-emote"` arm, (e) the UIObserver poller, and (f) the `RecordStep` clearing are wired up.

This plan covers **engine-side (xUnit-testable)** wiring and **UIObserver-side polling**. The `EmoteController.EmoteId` read in `DalamudGameProbe` is a thin Dalamud-bound shell (no test); the polling state machine, the inference rule, and the StepFactory arm are all testable without Dalamud.

---

## Dependency graph

```
QuestForge.Engine.Authoring
  ├── GameStateSnapshot           (add EmoteCompleted property + EmoteCompletedSignal record)
  ├── InferredFrom (enum)         (add EmoteCompleted)
  ├── SnapshotAggregator          (add OnEmoteCompleted / OnEmoteConsumed)
  └── StepInferenceEngine         (add Rule 3.5e — EmoteCompleted, fires immediately ABOVE existing Rule 3.5 ActionCompleted)
        ↓
QuestForge.Engine.Authoring.StepFactory  (route stepType="use-emote" → UseEmoteStep with Motion = true)
        ↓
QuestForge.Plugin.Authoring.AuthoringHost.RecordStep  (clear EmoteCompleted after consume)
        ↓
QuestForge.Plugin.Tracing.IGameProbe                   (add GetPlayerEmoteId())
        ↓
QuestForge.Plugin.Tracing.UIObserver.PollPlayerEmote   (new poller; tracks last-observed emoteId)
        ↓
QuestForge.Plugin.Tracing.DalamudGameProbe.GetPlayerEmoteId   (Dalamud-bound: reads LocalPlayer.EmoteController.EmoteId)
```

**Build order:**
1. `EmoteCompletedSignal` record + `GameStateSnapshot.EmoteCompleted` property + `InferredFrom.EmoteCompleted` enum value — engine surface, no Dalamud.
2. `SnapshotAggregator.OnEmoteCompleted` / `OnEmoteConsumed` — engine surface.
3. `StepInferenceEngine` Rule 3.5e — engine surface (placement: immediately above the existing Rule 3.5 ActionCompleted).
4. `StepFactory.Build` `"use-emote"` arm — engine surface.
5. `AuthoringHost.RecordStep` consume call — one-line edit.
6. `IGameProbe.GetPlayerEmoteId()` + `FakeGameProbe` extension — interface + fake.
7. `UIObserver.PollPlayerEmote` — testable in `QuestForge.Plugin.Tests` against `FakeGameProbe` + `FakeTargetProbe`.
8. `DalamudGameProbe.GetPlayerEmoteId` — concrete Dalamud impl, smoke-tested in-game.

Steps 1-6 are pure xUnit. Step 7 is `QuestForge.Plugin.Tests` against fakes. Step 8 is manual smoke.

---

## Architectural decisions (read before coding)

### Decision UEI1 — Snapshot field is `EmoteCompletedSignal?`, a record-shaped value matching the ActionCompleted precedent

`EmoteId` alone would not capture enough state — the target NPC is a meaningful signal for the StepFactory (`UseEmoteStep.TargetNpcId`). Two values warrant a record over two parallel snapshot fields (same reasoning as Decision UAI1 for `ActionCompletedSignal`).

**Concrete shape:**

```csharp
// QuestForge.Engine/Authoring/GameStateSnapshot.cs — appended near ActionCompleted
//
// Records that the player triggered an emote during this recording window. Set by
// SnapshotAggregator.OnEmoteCompleted, which is driven by UIObserver.PollPlayerEmote
// reading LocalPlayer.EmoteController.EmoteId. Cleared by OnEmoteConsumed (called from
// AuthoringHost.RecordStep) so it does not bleed into the next recording window.
//
// EmoteId is the Lumina Emote sheet RowId (matches UseEmoteStep.EmoteId).
// TargetBaseId is the BNpcBase / ENpcBase row id of the emote's target
// (null = self-cast / no target / target not in ObjectTable).
public sealed record EmoteCompletedSignal(
    uint EmoteId,
    uint? TargetBaseId);

public EmoteCompletedSignal? EmoteCompleted { get; init; }
```

**Why `EmoteId: uint` not `ushort` (the FFXIVClientStructs type):** `UseEmoteStep.EmoteId` is `uint` (per `QuestForge.Schema/Step.cs:159`). The widening at the snapshot boundary keeps the StepFactory arm a direct passthrough with no cast.

**Why `TargetBaseId: uint?` not `NpcId?`:** matches `ActionCompletedSignal.TargetBaseId` (Decision UAI1). The schema-side `UseEmoteStep.TargetNpcId` is also `uint?` (`Step.cs:161`), so the StepFactory passes through without conversion.

**Rejected alternatives:**
- **Two separate fields** `EmoteCompletedId: uint?` + `EmoteCompletedTargetBaseId: uint?`. Forces consumers to read both and disambiguate. Atomic record is strictly simpler.
- **Reuse `ActionCompleted`** with a synthetic ActionType. Emotes go through a different code path (chat command, not `ActionManager`) and authoring would lose the distinction in the trace — the `qf-trace extract-quest` pipeline routes on `"EmoteCompleted"` vs `"ActionCompleted"` event names.
- **Include `Motion: bool` in the signal.** The signal observes game state; the game does not expose the motion-suffix decision (see Decision UEI6 below). Recording the field on the signal would imply we observed something we didn't.

**What breaks if violated:** if the field is split into two separate slots, the priority check in Rule 3.5e must guard against both being set or only one being set (race window during reset). Atomic field is the established pattern.

### Decision UEI2 — There is NO new pure-logic seam in this slice

Mirroring `USE_EMOTE_DALAMUD_PLAN.md` Decision DED-1 (the analog cited in the brief's §B):

Candidates considered and rejected:

| Bit | Why it's not a unit-testable seam |
|---|---|
| "Is this emote interesting?" filter (e.g. skip auto-pose / sit) | We do NOT filter in v1. Every observed emote start fires the inference. Spam emotes like `/sit` are real player intent worth surfacing; if the author doesn't want them they delete the draft. A filter would be one if-statement; the only thing being tested would be the list of skip-ids, which is a configuration question, not logic. Defer until smoke shows a real spam problem (§F O2). |
| StepFactory `"use-emote"` arm | Already covered structurally by the existing `"use-action"` factory test pattern. The arm is a property-mapping with one defensive fallback (`?? 0u`). Existing factory-test infrastructure (UEI11/UEI11b style) covers it. |
| The `Motion` field default (always `true`) | A literal `Motion = true` in the StepFactory arm; verification is "test reads back the value as true." Not a separate helper. |

**Recommendation:** no new pure helpers; no new test files at the adapters layer. All testable surface lives in `QuestForge.Engine.Tests` and `QuestForge.Plugin.Tests`.

**What breaks if violated:** chasing coverage with a `FilterInterestingEmoteIds(uint)` static would invent a static helper that no other code uses, force a configuration discussion (which ids are "uninteresting"?) into a v1 plan, and surface a feature decision as code structure. Better to defer until smoke evidence demands it.

### Decision UEI3 — Aggregator setter/consumer mirror the ActionCompleted pair exactly

```csharp
// QuestForge.Engine/Authoring/SnapshotAggregator.cs — appended near the ActionCompleted methods
private EmoteCompletedSignal? _emoteCompleted;

// In the Current property's object-initializer block (alongside ActionCompleted):
EmoteCompleted = _emoteCompleted,

/// <summary>
/// Called by UIObserver.PollPlayerEmote when a new non-zero EmoteId is observed on the local player
/// (the player just started an emote this frame). Records the emote id and optional target
/// (from TargetManager.Target at the time of the read). Survives ResetDeltas; cleared by
/// OnEmoteConsumed (called from AuthoringHost.RecordStep).
///
/// Does NOT update LastNpcInteracted, LastAttuned, or any other unrelated field — the target NPC
/// of an emote is NOT the same as "the player interacted with that NPC for dialogue/quest purposes."
/// Setting LastNpcInteracted from here would cause spurious Rule 7 (NpcInteracted-changed) talk-step
/// inference on subsequent windows. Mirrors the ActionCompleted no-side-effects discipline (Decision UAI3).
/// </summary>
public void OnEmoteCompleted(uint emoteId, uint? targetBaseId)
    => _emoteCompleted = new EmoteCompletedSignal(emoteId, targetBaseId);

/// <summary>
/// Called at the end of RecordStep (and from UIObserver.ResetWindowState for symmetry) to consume
/// the emote-completed signal so it does not bleed into the next recording window.
/// Mirrors OnActionConsumed exactly.
/// </summary>
public void OnEmoteConsumed() => _emoteCompleted = null;
```

**Why no side effects on other state:** see the docstring. The same reasoning as Decision UAI3 — an emote target is not a dialogue interaction.

**Why not clear in `ResetDeltas`:** the field follows the per-window lifecycle (set during recording window, consumed at modal-confirm). Clearing in `ResetDeltas` would make the modal-preview unable to see the emote when the heartbeat tick reset deltas between Record-click and modal-display.

**What breaks if violated:** if `OnEmoteCompleted` sets `_lastNpcInteracted = new NpcId(targetBaseId.Value)`, the next recording window's Rule 7 sees the emote target as a "newly interacted NPC" and emits a wrong `talk` step.

### Decision UEI4 — New inference rule "Rule 3.5e — EmoteCompleted" fires IMMEDIATELY ABOVE Rule 3.5 (ActionCompleted)

**Priority placement is load-bearing.** The cascade in `StepInferenceEngine.Infer` (post-PR #104) runs:
- 1: QuestCompleted → 2: QuestAccepted → 2.1: ForeignQuestAccepted → 2.2: Combat → 2.2b: Purchase → 2.3: KeyItemsAdded → 2.4: KeyItemsRemoved → 2.5: Attune → 2.6: InventoryHash → **3.5: ActionCompleted** → 3: QuestSequence advanced → 4.0: TeleportCompleted → 4: Zone changed → 2.7: AethernetTeleportCompleted (same zone) → 5: QuestFlags → 6: DialogueAnswer → 7: NpcInteracted → 8: Movement → 9: Empty.

Insertion site: **between Rule 2.6 (InventoryHash) and Rule 3.5 (ActionCompleted)**, labelled "Rule 3.5e" (the "e" suffix denotes "emote" and explicitly marks it as the priority-higher sibling of Rule 3.5).

**Why ABOVE Rule 3 (QuestSequence advance):** identical reasoning to Decision UAI4. An emote that advances the sequence is the most common authoring case for UseEmoteStep (the NPC's scripted reaction advances the sequence as the natural postcondition). Firing Rule 3 first would draft a `talk` step the author would have to delete.

**Why ABOVE Rule 3.5 (ActionCompleted):** the brief flags this as §E "tie-breaker with ActionCompleted." In practice the two signals are mutually exclusive (emotes go through `Chat.SendMessage` → game's emote handler → `EmoteController.EmoteId`; actions go through `ActionManager.UseAction` → `CastInfo.Response*`). The two fields are independent in the snapshot, however, and a defensive priority is required for the case where both fire in the same window (e.g. queued emote that lands on the same frame as an action effect — possible at 60 fps).

**Why Emote wins when both fire:** the brief recommends "emote wins if EmoteCompleted is set, because emotes are more specific." Concurring:
- Emotes are RARER in normal play than actions (auto-attacks alone increment `ResponseGlobalSequence` constantly per §F O1 of the UseAction plan). If both signals fire, the emote is the deliberate authoring action; the action is more likely incidental.
- The author's intent when they see "I pressed `/cheer` AND a Mount Roulette landed" is almost always the cheer (the mount is a separate step in the cursor walk).
- Reverse priority would mean emotes are silently swallowed during combat (where actions fire constantly); emote-priority preserves the author's intent.

**Why BELOW Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6:** identical reasoning to UAI4. Those rules represent distinct authoring intents (turn-in, accept, combat, purchase, key-item exchange, attunement, inventory-diff exchange) where the emote is incidental and not the primary step.

**Final placement: between Rule 2.6 (InventoryHash) and Rule 3.5 (ActionCompleted).** Concrete location: insert immediately above the `// Rule 3.5 — ActionCompleted` comment at line 264 of `StepInferenceEngine.cs`.

```csharp
// Rule 3.5e — EmoteCompleted
// Fires when UIObserver.PollPlayerEmote detected that the player started an emote during this
// recording window (LocalPlayer.EmoteController.EmoteId transitioned from 0 (or another id) to
// a new non-zero id).
//
// PRIORITY: above Rule 3.5 (ActionCompleted). Emote and action signals are mutually exclusive
// in practice (different game code paths), but if both fire in the same window the emote is
// the more deliberate authoring intent — actions can fire incidentally (auto-attack tick,
// pet command); emotes always reflect a player text command.
// PRIORITY: above Rule 3 (QuestSequence advanced) — use-emote is more specific than the
// catch-all sequence-advance which defaults to "talk". Use-emote that advances the sequence
// (NPC reaction script flips a flag) is the MOST common case.
// PRIORITY: below Rules 1, 2, 2.1, 2.2, 2.2b, 2.3, 2.4, 2.5, 2.6 — those represent distinct
// authoring intents where an emote use would be incidental.
//
// CONFIDENCE: High — the player demonstrably typed the slash command (the emote played).
// EXPECT: null — author MUST write the postcondition (Decision UE4 of USE_EMOTE_STEP_PLAN.md:
// there is no universal "did the emote's NPC reaction land?" predicate).
if (after.EmoteCompleted is { } emoteSignal)
{
    var stepIdSuffix = emoteSignal.TargetBaseId is { } tid
        ? $"{emoteSignal.EmoteId}-on-{tid}"
        : $"{emoteSignal.EmoteId}";
    return new InferenceResult(
        StepType:        "use-emote",
        SuggestedStepId: $"use-emote-{stepIdSuffix}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.EmoteCompleted,
        Notes:           "Author MUST write the Expect predicate (no universal emote postcondition).");
}
```

**Why `SuggestedExpect = null` (not a placeholder):** identical reasoning to Decision UAI4. The engine treats `Expect == null` as "step never satisfies, re-emit forever" — the loud failure mode. Synthesising a placeholder predicate would be a lie that might accidentally satisfy on some quests. Null is honest.

**Why the `Notes` string:** the Record-Step modal surfaces `InferenceResult.Notes` in the UI. Telling the author "you need to write the Expect" up front is friendlier than letting them ship a draft that loops at runtime.

### Decision UEI5 — `InferredFrom.EmoteCompleted` is a new enum value

Existing values (per `InferredFrom.cs` post-PR #104): `ZoneChange, QuestFlagChange, QuestSequenceChange, DialogueInteraction, QuestAccepted, QuestCompleted, AttunementChange, MovementChange, Manual, None, InventoryChange, Combat, Purchase, TeleportCompleted, ActionCompleted`.

Adding `EmoteCompleted` keeps the existing taxonomy intact and lets downstream consumers (trace events, UI badge colours, future analytics) differentiate emote-derived steps from action-derived ones.

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
    EmoteCompleted,    // NEW
}
```

**Rejected alternative:** reusing `ActionCompleted` — would collapse the emote/action distinction in trace data and prevent filtering downstream.

### Decision UEI6 — `Motion` field is always inferred as `true` (the canonical authoring default)

The brief's §H frames the tradeoff: inference cannot tell whether the player typed `/cheer` vs `/cheer motion` — the in-game animation is identical, and `EmoteController.EmoteId` is the same value either way.

**Decision: always infer `Motion = true`.** Reasons:

1. **`UseEmoteStep.Motion`'s default in the schema is `true`** (per `Step.cs:162`, pinned by Decision UE2 of `USE_EMOTE_STEP_PLAN.md`). Inference mirrors the schema default — no surprise.
2. **Motion-suppressed broadcast is the right behavior for quest automation.** A quest that fires `/cheer` and broadcasts "You cheer!" to Say is noise for the user. Default-true matches the expected runtime behavior.
3. **Authors who want broadcast can flip the field in the modal.** The modal's free-form JSON edit lets the author override; the suggestion is starting from the correct default.
4. **The chat-hook inference (out of scope, §F O3) would later observe the literal command** and could promote `Motion = false` precisely. Until then, the default is honest.

**What breaks if violated:** inferring `Motion = false` would draft quests whose first execution spams the player's chat channel with "You cheer!" / "You salute!" — a UX papercut at no benefit.

**Concrete code: see Decision UEI7 below.**

### Decision UEI7 — `StepFactory.Build` gains a `"use-emote"` arm; Motion = true literal

Mirrors the `"use-action"` arm at `StepFactory.cs:137-144`. Reads `after.EmoteCompleted` to populate the `UseEmoteStep` fields. If `after.EmoteCompleted` is null at the moment `StepFactory.Build` is called for `stepType == "use-emote"` (defensive — the inference engine only returns "use-emote" when the field is set), fall back to `UseEmoteStep { EmoteId = 0, TargetNpcId = null, Motion = true }` so the Builder's validator catches it later; do not throw.

```csharp
// QuestForge.Engine/Authoring/StepFactory.cs — append to the switch block, near the "use-action" arm
"use-emote" => new UseEmoteStep
{
    Id = stepId,
    Expect = expectValue,           // null in v1 inference (Decision UE4); author edits in modal
    EmoteId = after?.EmoteCompleted?.EmoteId ?? 0u,
    TargetNpcId = after?.EmoteCompleted?.TargetBaseId,
    Motion = true                    // Decision UEI6 — always infer the canonical authoring default
},
```

**Why no `Zone` / `RequiredZone` / `Target` location:** `UseEmoteStep` (per `Step.cs:157-163`) inherits only `Id` and `Expect` from `Step` — it has NO `Zone` field, NO `RequiredZone`, NO `Target` location. The engine relies on the cursor walk and authored `Expect` for ordering; spatial constraints are encoded by an upstream `TravelStep` if needed. Same posture as `UseActionStep`.

**Why `Motion = true` written explicitly (not `Motion = default` or omitted):** the field IS the schema default (`true`), but writing it explicitly in the StepFactory arm pins the inference behaviour at the call site. If a future schema change flips the default, the StepFactory's intent stays unambiguous. Costs one line.

**What breaks if violated:** if the StepFactory throws on null `EmoteCompleted`, a defensive caller (Tester invoking `Build("use-emote", …, after: emptySnapshot)`) crashes the test harness instead of producing a validator-rejectable draft. If `Motion` is implicitly `default`, a future schema flip to `Motion = false` silently changes inference behaviour without test coverage.

### Decision UEI8 — `AuthoringHost.RecordStep` clears `EmoteCompleted` after consuming

After the existing four consume calls in `RecordStep` (lines 270-273):

```csharp
_aggregator.OnAethernetTeleportConsumed();
_aggregator.OnDialogueOptionConsumed();
_aggregator.OnTeleportConsumed();
_aggregator.OnActionConsumed();
_aggregator.OnEmoteConsumed();   // NEW
```

Same lifecycle as the other four: survives `ResetDeltas` so it remains visible to `PreviewInference`, cleared only after the author confirms the modal.

`UIObserver.ResetWindowState` should ALSO call `_aggregator?.OnEmoteConsumed()` for symmetry with the existing four (lines 183-186). This keeps the per-window reset path internally consistent.

```csharp
// UIObserver.ResetWindowState — alongside existing consume calls
_aggregator?.OnAethernetTeleportConsumed();
_aggregator?.OnDialogueOptionConsumed();
_aggregator?.OnTeleportConsumed();
_aggregator?.OnActionConsumed();
_aggregator?.OnEmoteConsumed();   // NEW
```

### Decision UEI9 — UIObserver poller `PollPlayerEmote` is a transition watcher (NOT a sequence counter)

The signal source is fundamentally different from the action-effect poller:
- **Action-effect poller** watches a **monotonically-increasing `ResponseGlobalSequence`**; rising edge fires.
- **Emote poller** watches `EmoteController.EmoteId`, which is **set to the active emote's RowId while the emote plays, and resets to 0 when the emote ends.**

The brief's §D precisely captures the state machine: track `_lastObservedEmoteId: ushort` (we use `uint` to match `IGameProbe.GetPlayerEmoteId() → ushort?` widened at the boundary). Each frame:
- `current == _lastObserved` → no-op (no transition).
- `current == 0` → reset `_lastObserved` to 0 (player not emoting); no fire.
- `current != 0 AND current != _lastObserved` → transition; capture target, fire, update `_lastObserved`.

This catches the three meaningful transitions:
- `0 → X` — emote started from idle (the primary case).
- `X → Y` (`X ≠ 0`, `Y ≠ 0`) — emote switched directly (rare; e.g. cancel-and-restart). Fires for `Y`.
- `X → 0` — emote ended. Does NOT fire (the brief's §K notes "the END of an emote isn't an event").

```csharp
// QuestForge.Plugin.Tracing/UIObserver.cs — new every-frame poller, added next to PollPlayerActionEffect
//
// REQUIRES: IGameProbe gains GetPlayerEmoteId() returning ushort? (null = no LocalPlayer).
//
// State: _lastObservedEmoteId tracks the most recently observed EmoteController.EmoteId.
// Transitions: 0 → X fires (X != 0); X → Y fires (X != Y, both non-zero); X → 0 resets state
// silently; same value is no-op. The state stays sticky across reset only via ResetWindowState
// (which sets _lastObservedEmoteId = 0 to re-baseline on the next non-zero observation).
// 
// CAPTURE TIMING: target is read from TargetManager.Target at the same tick as the EmoteId
// transition. Mirrors the action-effect target capture (Decision UAI11) — TargetManager.Target
// at observation time is "the target the author had when they triggered the emote." We do NOT
// cross-check EmoteController.Target (a GameObjectId, which would need ObjectTable resolution
// back to BaseId and adds noise for self-cast — same reasoning as Decision UAI11 for action).

// Field declarations — place alongside _lastObservedActionSequence
private uint _lastObservedEmoteId;   // 0 = not emoting, otherwise active Lumina Emote RowId

private void PollPlayerEmote()
{
    if (_gameProbe is null) return;

    var current = _gameProbe.GetPlayerEmoteId();
    if (current is null) return;   // no LocalPlayer
    var currentId = (uint)current.Value;

    // Same value — no transition.
    if (currentId == _lastObservedEmoteId) return;

    // Emote ended (X → 0) — reset state silently, no event.
    if (currentId == 0u)
    {
        _lastObservedEmoteId = 0u;
        return;
    }

    // Transition to a new non-zero emote (0 → X or X → Y). Fire.
    _lastObservedEmoteId = currentId;

    // Capture target. Same priority order as PollPlayerActionEffect (Decision UAI8):
    // hostile (BattleNpc) wins over interactable (EventNpc) when both are set.
    uint? targetBaseId = null;
    var hostile      = _targetProbe?.GetBattleNpcTarget();
    var interactable = _targetProbe?.GetInteractableNpcTarget();
    if (hostile is { } h)
        targetBaseId = h.BaseId;
    else if (interactable is { } i)
        targetBaseId = i.BaseId;
    // If neither — self-cast / no target, leave targetBaseId = null.

    var now   = _clock.UtcNow;
    var runId = CurrentRunId;
    WriteObservation("EmoteCompleted",
        currentId,
        new { targetBaseId = targetBaseId ?? 0u },
        runId, now);
    _aggregator?.OnEmoteCompleted(currentId, targetBaseId);
}
```

Add the call into `OnFrameworkUpdate` alongside the other every-frame pollers, **after** `PollPlayerActionEffect` (line 222):

```csharp
PollAethernetDestination();
PollTeleportAddonOpen();
PollDialogueOption();
PollSelectYesno();
PollTargetNpc();
PollPlayerActionEffect();
PollPlayerEmote();   // NEW
```

`ResetWindowState` adds (alongside the existing `_lastObservedActionSequence = null;`):
```csharp
_lastObservedEmoteId = 0u;            // re-baseline (next non-zero observation will fire)
_aggregator?.OnEmoteConsumed();       // mirrors the four existing consume calls
```

**Why `_lastObservedEmoteId = 0u` (not nullable) for the reset:** unlike the action-effect counter (where `null` means "no baseline established, treat next observation as baseline silently"), the emote field's natural baseline IS 0 (the not-emoting state). Setting `_lastObservedEmoteId = 0u` on reset means the next tick where `current != 0` is a `0 → X` transition that fires — which is the correct behavior. There is no "pre-session emote" concern equivalent to UAI8's baseline rationale, because:
- If the player is mid-emote at session start, `EmoteController.EmoteId` is non-zero from the first tick. With `_lastObservedEmoteId = 0` initial, this fires immediately (`0 → X`) — capturing the in-progress emote as an event. This is arguably the right behavior: the author opened authoring mode while the emote played; the author probably wants that emote recorded.
- If the player is idle at session start, `EmoteController.EmoteId == 0`. Initial state matches; no fire (`0 == 0`). Correct.
- The action-effect poller's "establish baseline silently on first read" exists because `ResponseGlobalSequence` is monotonically increasing and a non-zero value at session start is a stale pre-session action. Emotes are different: a non-zero `EmoteId` at session start IS a live emote, not a stale ghost.

**Why this differs from PollPlayerActionEffect's baseline pattern (UAI8):** the action-effect counter is monotonically rising and a pre-session non-zero value is stale by definition. The emote field is a momentary state — non-zero means "emoting RIGHT NOW," not "has emoted at any point in history." Different semantics → different baseline strategy. Documented in test UEI-UO_L1.

**Cancellation handling (emote interrupted before animation completes):** `EmoteController.EmoteId` only gets set when the emote actually starts playing. A queued emote that the game silently dropped (player moved mid-`/cheer`) never populates `EmoteId`, so no event fires. Same intended behaviour as the action-effect path (UAI8 cancelled-cast handling).

**Persistent emotes (sit, doze, /umbrella):** `EmoteId` stays set for the entire duration the player is in the pose. Our state machine handles this correctly:
- Tick N: `0 → X` transition fires once (correct — the author started the pose).
- Tick N+1..N+M: `X == X` → no-op (correct — same pose, no new event).
- Eventually `X → 0` when the player exits the pose (correct — silent reset, no event for the END).

This is pinned by tests UEI-UO_L2 and UEI-UO_L11 (persistent-emote sustain).

### Decision UEI10 — `IGameProbe.GetPlayerEmoteId()` is the new probe surface

Following the established `IGameProbe` shape:

```csharp
// QuestForge.Plugin.Tracing/IGameProbe.cs — append one method
public interface IGameProbe
{
    IReadOnlyList<(ushort QuestId, byte Seq, byte Flags, IReadOnlyList<byte> Variables)> GetNormalQuests();
    bool IsAetheryteUnlocked(uint rowId);
    IEnumerable<uint> GetAllAetheryteRowIds();
    IReadOnlyList<(uint ItemId, int Qty)> GetKeyItemSlots();
    (float X, float Y, float Z, int Zone)? GetPlayerPosition();
    (uint Sequence, uint FfxivActionType, uint ActionId)? GetLastActionEffect();
    ushort? GetPlayerEmoteId();   // NEW
}
```

**Why `ushort?` (not `uint?`):** the underlying field is `ushort` on `EmoteController` (per `EmoteController.cs:13`). Widening at the poller boundary (where we cast to `uint` for snapshot storage) keeps the probe surface honest about the source-of-truth type. The poller does `(uint)current.Value` and stores `uint` in the snapshot to match `UseEmoteStep.EmoteId`'s type.

**Why null when no LocalPlayer:** matches `GetPlayerPosition` (line 14) and `GetLastActionEffect` (line 15). The poller's `if (current is null) return;` handles it.

**Concrete Dalamud implementation:**

```csharp
// QuestForge.Plugin/Tracing/DalamudGameProbe.cs — append
public unsafe ushort? GetPlayerEmoteId()
{
    var player = _objectTable.LocalPlayer;
    if (player is null) return null;
    // LocalPlayer is a Character (per FFXIVClientStructs Character.cs:18 — EmoteController at 0x630).
    // Cast via Dalamud's address to access the field (mirrors GetLastActionEffect at line 76).
    var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
    if (ch is null) return null;
    return ch->EmoteController.EmoteId;
}
```

**Why we read via `player.Address` cast rather than `LocalPlayer.<wrapper>`:** the Dalamud `IPlayerCharacter` / `ICharacter` wrappers do not expose `EmoteController` fields. The cast to the raw FFXIVClientStructs struct is the same pattern `DalamudGameProbe.GetLastActionEffect` uses (line 80-83) and what `DalamudCombatProbe` uses for `CurrentHp` reads.

**Verification note (§F O1):** the FFXIVClientStructs `EmoteController.PlayEmote` doc comment says "For the local player, use AgentEmote.ExecuteEmote or EmoteManager.ExecuteEmote instead" — this concerns **triggering** emotes, not **reading** state. The `EmoteId` field should populate for the local player when they emote (the game updates the same struct regardless of who triggered the play). Smoke verifies; if reads are zero for self, fall back to polling `EmoteManager` or `AgentEmote`. Recorded as open question §F O1.

### Decision UEI11 — Trace observation event method is `"EmoteCompleted"`

The trace stream is consumed by `qf-trace extract-quest` (Phase 10) to reconstruct quest definitions. Using a distinct method name lets the extractor route the event to a `UseEmoteStep` arm directly. Mirroring the existing `WriteObservation("ActionCompleted", actionId, …)` pattern (line 650):

```csharp
WriteObservation("EmoteCompleted",
    currentId,
    new { targetBaseId = targetBaseId ?? 0u },
    runId, now);
```

- `argument` carries the EmoteId (the identifying primitive).
- `value` is a structured object with `targetBaseId` (0 = no target / self-cast).
- No `motion` field on the trace event — inference cannot observe motion (Decision UEI6).

**Why no `actionType` field analog:** unlike actions (which can be Action / GeneralAction / KeyItem), emotes are a single category — the EmoteId alone is sufficient identification.

**Why `targetBaseId ?? 0u` and not omit-when-null:** dedup logic in `TraceSession.WriteObservation` compares the serialized value; a stable shape (always-present `targetBaseId`) means two consecutive identical emote uses dedup cleanly (TraceSession suppresses unchanged values).

### Decision UEI12 — Target read is "current target at the time of the read" (TargetManager.Target via ITargetProbe), NOT EmoteController.Target

`EmoteController.Target` (offset 0x18, `GameObjectId`) carries the emote's own target. **We do NOT use it.** Same reasoning as Decision UAI11:

1. **Schema authors target NPCs by BaseId (the static template id), not by GameObjectId (the per-instance id).** Resolving `GameObjectId → BaseId` via ObjectTable scan is fragile (target may have moved out of scene by the time we read).
2. **The author's intent ("which NPC am I emoting at?") is captured by `TargetManager.Target.BaseId`** at the moment the emote starts.
3. **Self-cast handling is simpler.** If `TargetManager.Target` is null, the emote is self-directed; map to `TargetBaseId = null`. Reading `EmoteController.Target` for self-emotes returns the player's own GameObjectId, requiring a "is this me?" comparison that adds noise.
4. **Cross-check would add no value** for our use case — if `TargetManager.Target.BaseId` and `EmoteController.Target` resolve to different NPCs, that ambiguity is best surfaced by trusting the author's hard target (which they actively chose).

**What breaks if violated:** if we read `EmoteController.Target` and ObjectTable-resolve, the draft step has the wrong target on any emote where the player tab-targeted just before pressing `/cheer`.

### Decision UEI13 — Tie-breaker rule when both `EmoteCompleted` and `ActionCompleted` are set

Pinned by Decision UEI4. Emote wins (Rule 3.5e is positioned ABOVE Rule 3.5). Test scenario UEI-T4 explicitly pins this. In practice the two paths are mutually exclusive, but the snapshot fields are independent — a race window exists where both could fire on the same tick.

**Implementation:** the `return` statement inside the Rule 3.5e if-block ensures Rule 3.5 never runs when the emote signal is set. Standard short-circuit-on-first-match cascade.

### Decision UEI14 — Diagnostic log line extension

The existing `[QF-DIAG] PreviewInference:` line in `AuthoringHost.PreviewInference` (line 200) already includes `ActionCompleted={after.ActionCompleted?.ActionId}`. Append the emote signal analogously:

```csharp
_services.Log.Debug($"[QF-DIAG] PreviewInference: zone {before.Zone.Value}→{after.Zone.Value} " +
    $"AethernetTeleportCompleted={after.AethernetTeleportCompleted?.To.Value} " +
    $"TeleportCompleted={after.TeleportCompleted?.Value} " +
    $"ActionCompleted={after.ActionCompleted?.ActionId} " +
    $"EmoteCompleted={after.EmoteCompleted?.EmoteId} " +     // NEW
    $"DialogueOptionSelected={after.DialogueOptionSelected} " +
    $"DialogueNpcSource={after.DialogueNpcSource?.NpcId} " +
    $"isAethernet_before_shard={before.LastAethernetShardInteracted?.Value} " +
    $"isAethernet_before_npc={before.LastNpcInteracted?.Value}");
```

**Why only the EmoteId:** the diagnostic line is single-line; adding TargetBaseId would push it past readable width. The EmoteId is the most diagnostic single value (maps to Lumina Emote rows in `/xldata`).

### Decision UEI15 — `FakeGameProbe` extension (Plugin.Tests)

Extend the existing `FakeGameProbe` (currently in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` lines 96-156) with a scriptable `GetPlayerEmoteId`:

```csharp
public sealed class FakeGameProbe : IGameProbe
{
    // ... existing fields ...
    private ushort? _nextEmoteId;   // null = no LocalPlayer / probe returns null

    public void SetPlayerEmoteId(ushort emoteId) => _nextEmoteId = emoteId;
    public void ClearPlayerEmoteId() => _nextEmoteId = null;

    // ... existing methods ...

    public ushort? GetPlayerEmoteId() => _nextEmoteId;
}
```

`SetPlayerEmoteId` is **sticky** (mirrors `_unlockedAetherytes` semantics and `_nextActionEffect` at line 147 — the probe represents "what the game currently shows"). Tests that need to simulate "the player starts an emote" call `SetPlayerEmoteId(17)` then `framework.Tick()` — the poller observes the new id and fires. To simulate the emote ending, call `SetPlayerEmoteId(0)` then `framework.Tick()`.

**Distinction from action-effect pattern:** the `_nextActionEffect` field is a tuple representing a stable "current snapshot of the game's ActionEffect state"; the EmoteId is conceptually the same — the probe returns whatever value the test set most recently. Tests drive transitions by calling `SetPlayerEmoteId` between ticks.

The existing `FakeTargetProbe` already supports `SetBattleNpcTarget` / `SetInteractableNpcTarget` (per `UIObserverCombatForwardingTests.cs:38-65`); no extension needed.

### Decision UEI16 — No re-record cascade impact (additive observation)

Per the user's MEMORY note about trace-emission refactor cascades:
- A **new probe method** (`GetPlayerEmoteId`) called every frame by UIObserver.
- A **new observation event** (`"EmoteCompleted"`).

**Why this does NOT cascade existing fixtures:**
- The engine's `QuestEngine.Tick` is unchanged — `IEmoteExecutor` is consulted only when a `UseEmoteStep` is at the cursor. The new probe call is in the **authoring poller**, not in the engine.
- Engine replay fixtures (which capture engine reads) are unaffected.
- UIObserver-fixture replays would see new `"EmoteCompleted"` observation events, but the UIObserver test suite uses `FakeGameProbe` (the new method defaults to returning null when `SetPlayerEmoteId` is not called), so existing tests stay green.

If a future trace-replay test exists that records UIObserver output against a real game session, the new `"EmoteCompleted"` events would appear in new fixtures only — no existing fixture is re-recorded.

---

## Snapshot field summary

| Field (new or existing) | Type | Set by | Cleared by | Survives ResetDeltas? |
|---|---|---|---|---|
| `EmoteCompleted` (NEW) | `EmoteCompletedSignal?` | `OnEmoteCompleted` | `OnEmoteConsumed` | yes |
| `ActionCompleted` (existing) | `ActionCompletedSignal?` | `OnActionCompleted` | `OnActionConsumed` | yes |
| `TeleportCompleted` (existing) | `AetheryteId?` | `OnTeleportCompleted` | `OnTeleportConsumed` | yes |
| `AethernetTeleportCompleted` (existing) | `AethernetHop?` | `OnAethernetTeleportCompleted` | `OnAethernetTeleportConsumed` | yes |
| `DialogueOptionSelected` (existing) | `int?` | `OnDialogueOptionSelected` | `OnDialogueOptionConsumed` | yes |

The new `EmoteCompleted` shares the **per-window event lifecycle** of the other four: cleared in `RecordStep` (and defensively in `ResetWindowState`), survives `ResetDeltas` between the "before" capture and the "after" Preview.

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
| **3.5e (NEW)** | **after.EmoteCompleted set** | **use-emote** | **EmoteCompleted** | **High** |
| 3.5 | after.ActionCompleted set | use-action | ActionCompleted | High |
| 3   | QuestSequence advanced | talk | QuestSequenceChange | High |
| 4.0 | after.TeleportCompleted set AND zone changed | teleport | TeleportCompleted | High |
| 4   | Zone changed (aethernet / NPC dialogue / shard / catch-all) | travel | ZoneChange | High |
| 2.7 | AethernetTeleportCompleted set, same zone | travel | ZoneChange | High |
| 5   | QuestFlags changed, sequence unchanged | talk | QuestFlagChange | Medium |
| 6   | LastDialogueAnswer changed | talk | DialogueInteraction | Medium |
| 7   | LastNpcInteracted changed | talk | DialogueInteraction | Low |
| 8   | Player moved >5u, same zone | travel | MovementChange | Low |
| 9   | nothing matched | Empty | None | Low |

Rule 3.5e occupies the position **immediately before Rule 3.5** in the source file. It does NOT require any zone-change or quest-state condition — the presence of `after.EmoteCompleted` alone is sufficient.

---

## Task breakdown

### Task UEI-T1 — Engine: `EmoteCompletedSignal` record + `GameStateSnapshot.EmoteCompleted`

1. Edit `QuestForge.Engine/Authoring/GameStateSnapshot.cs`.
2. Add the `EmoteCompletedSignal` record near the top (alongside `ActionCompletedSignal` at lines 17-20):
   ```csharp
   public sealed record EmoteCompletedSignal(uint EmoteId, uint? TargetBaseId);
   ```
3. Append a non-positional property at the end of the snapshot (after `ActionCompleted` at line 146):
   ```csharp
   // Non-positional. Set when UIObserver.PollPlayerEmote observes that the player triggered an
   // emote during this recording window (LocalPlayer.EmoteController.EmoteId transitioned to a
   // new non-zero value). Cleared by OnEmoteConsumed in RecordStep so it does not bleed into the
   // next window.
   public EmoteCompletedSignal? EmoteCompleted { get; init; }
   ```
4. No other existing tests should change behaviour (additive property + additive record).

### Task UEI-T2 — Engine: `InferredFrom.EmoteCompleted`

1. Edit `QuestForge.Engine/Authoring/InferredFrom.cs`.
2. Append `EmoteCompleted,` to the enum per Decision UEI5.

### Task UEI-T3 — Engine: `SnapshotAggregator.OnEmoteCompleted` / `OnEmoteConsumed`

1. Edit `QuestForge.Engine/Authoring/SnapshotAggregator.cs`.
2. Add the backing field `private EmoteCompletedSignal? _emoteCompleted;`.
3. Add `EmoteCompleted = _emoteCompleted,` to the object-initializer in the `Current` property body (alongside `ActionCompleted = _actionCompleted`).
4. Add the setter and consumer methods per Decision UEI3.
5. Do **not** touch `_lastNpcInteracted`, `_lastAttuned`, `_lastAethernetShardInteracted`, or any other unrelated field.
6. Do **not** clear in `ResetDeltas` (survives per-window lifecycle; only `OnEmoteConsumed` clears).

### Task UEI-T4 — Engine: `StepInferenceEngine` Rule 3.5e

1. Edit `QuestForge.Engine/Authoring/StepInferenceEngine.cs`.
2. Insert the rule per Decision UEI4 immediately above the existing `// Rule 3.5 — ActionCompleted` comment (line 264).
3. Confidence: `Confidence.High`. SuggestedExpect: `null`. Notes: the canonical "author MUST write the Expect" string.
4. SuggestedStepId pattern: `$"use-emote-{EmoteId}-on-{TargetBaseId}"` when target present, `$"use-emote-{EmoteId}"` when self-cast.

### Task UEI-T5 — Engine: `StepFactory` `"use-emote"` arm

1. Edit `QuestForge.Engine/Authoring/StepFactory.cs`.
2. Add the `"use-emote"` arm per Decision UEI7 — placed in the existing `stepType switch` block alongside `"use-action"` (line 137).
3. Confirm `using QuestForge.Schema;` is already in scope (yes — line 3).

### Task UEI-T6 — Plugin: `AuthoringHost.RecordStep` clearing + diagnostic log

1. Edit `QuestForge.Plugin/Authoring/AuthoringHost.cs`.
2. At the end of `RecordStep`, after the existing four consume calls (lines 270-273), add:
   ```csharp
   _aggregator.OnEmoteConsumed();   // NEW
   ```
3. Extend the `[QF-DIAG] PreviewInference:` line per Decision UEI14 with `EmoteCompleted={after.EmoteCompleted?.EmoteId}`.

### Task UEI-T7 — Plugin.Tracing: `IGameProbe.GetPlayerEmoteId()` + `FakeGameProbe` extension

1. Edit `QuestForge.Plugin.Tracing/IGameProbe.cs` — append `ushort? GetPlayerEmoteId();`.
2. Edit `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` `FakeGameProbe` (lines 96-156) — add the `_nextEmoteId` field, the `SetPlayerEmoteId` / `ClearPlayerEmoteId` helpers, and the `GetPlayerEmoteId` accessor per Decision UEI15.

### Task UEI-T8 — Plugin.Tracing: `UIObserver.PollPlayerEmote` + ResetWindowState

1. Edit `QuestForge.Plugin.Tracing/UIObserver.cs`.
2. Add the per-window state field `private uint _lastObservedEmoteId;` alongside `_lastObservedActionSequence` (line 86).
3. Add `PollPlayerEmote()` method per Decision UEI9.
4. Wire the call into `OnFrameworkUpdate` after `PollPlayerActionEffect` (line 222).
5. Wire into `ResetWindowState`: `_lastObservedEmoteId = 0u;` and `_aggregator?.OnEmoteConsumed();`.

### Task UEI-T9 — Plugin: `DalamudGameProbe.GetPlayerEmoteId`

1. Edit `QuestForge.Plugin/Tracing/DalamudGameProbe.cs`.
2. Implement per Decision UEI10, using the `Character*` cast pattern from `GetLastActionEffect` (line 76-84).
3. The `using FFXIVClientStructs.FFXIV.Client.Game.Character;` import is already present (line 3 — pulls in `BattleChara`; `Character` lives in the same namespace).
4. Manual smoke test in-game: enter Inspect mode for any quest, trigger an emote, confirm the trace contains `"EmoteCompleted"` with the correct EmoteId.

---

## Validation rules (this plan adds none)

Validator rules for `structural/use-emote-*` are already shipped (E9, E10, W8 per `USE_EMOTE_STEP_PLAN.md` Decision UE7). The authoring path is downstream of validation: a draft containing a `UseEmoteStep { EmoteId = 0 }` (defensive fallback in Decision UEI7) will be caught when the draft is exported.

---

## Given-When-Then test scenarios

Tests are split into two files:

| File | Scenarios | Test type |
|---|---|---|
| `QuestForge.Engine.Tests/Authoring/UseEmoteInferenceTests.cs` | UEI1..UEI9 | Inference engine + aggregator + StepFactory |
| `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` | UO_L1..UO_L11 | UIObserver polling state-machine |

### Inference-engine tests (`QuestForge.Engine.Tests/Authoring/UseEmoteInferenceTests.cs`)

For all tests below, helpers `MakeSnapshot(...)` and `MakeAggregator(...)` follow the same patterns as `UseActionInferenceTests` — the Tester picks the exact factory signature.

#### UEI1 — Happy path, no target: EmoteCompleted set → infers `use-emote` step

**Given:**
- `before = MakeSnapshot()` (no quest changes, no zone change)
- `after  = before with { EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null) }` (self-cast `/cheer`)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-emote"`
- `result.SuggestedStepId == "use-emote-17"`
- `result.SuggestedExpect == null`
- `result.Confidence == Confidence.High`
- `result.InferredFrom == InferredFrom.EmoteCompleted`
- `result.Notes` is non-null and contains the substring `"Expect"`.

#### UEI2 — Happy path, NPC target: EmoteCompleted with TargetBaseId → step id includes "-on-{tid}"

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: 1000789u) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-emote"`
- `result.SuggestedStepId == "use-emote-17-on-1000789"`
- `result.SuggestedExpect == null`
- `result.InferredFrom == InferredFrom.EmoteCompleted`

#### UEI3 — Self-cast: TargetBaseId = null → step id has no "-on-" suffix

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { EmoteCompleted = new EmoteCompletedSignal(EmoteId: 7u, TargetBaseId: null) }` (salute, self-cast)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-emote"`
- `result.SuggestedStepId == "use-emote-7"` (NO `-on-` suffix)
- `result.InferredFrom == InferredFrom.EmoteCompleted`

Pins Decision UEI4's step-id pattern for self-cast.

#### UEI4 — Priority over Rule 3 (QuestSequence advanced): EmoteCompleted wins over sequence-advance

**Given:**
- `before = MakeSnapshot(questSequence: 1)`
- `after  = before with { QuestSequence = 2, EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: 1000789u) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-emote"` (NOT `"talk"`)
- `result.InferredFrom == InferredFrom.EmoteCompleted` (NOT `QuestSequenceChange`)

**Rationale:** use-emote that advances the sequence is the most common authoring case; firing Rule 3 first would draft a `talk` step.

#### UEI5 — Priority over Rule 4 (Zone changed): EmoteCompleted wins (defensive)

**Given:**
- `before = MakeSnapshot(zone: ZoneId(132))`
- `after  = before with { Zone = ZoneId(129), EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null) }`
- (defensive — production should never zone-change mid-emote, but the fields are independent)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-emote"` (NOT `"travel"`)
- `result.SuggestedStepId == "use-emote-17"`
- `result.InferredFrom == InferredFrom.EmoteCompleted`

Pins Decision UEI4's "Rule 3.5e above Rule 4" ordering (transitive via Rule 3.5e above 3 above 4).

#### UEI6 — Priority over Rule 3.5 (ActionCompleted): EmoteCompleted wins when both set (defensive)

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { ActionCompleted = new ActionCompletedSignal(QuestForge.Schema.ActionType.Action, 31u, null), EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null) }`
- (defensive — production should never set both, but the fields are independent)

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "use-emote"` (NOT `"use-action"`)
- `result.SuggestedStepId == "use-emote-17"`
- `result.InferredFrom == InferredFrom.EmoteCompleted`

Pins Decision UEI13.

#### UEI7 — Priority below Rule 1 (QuestCompleted): turn-in wins over EmoteCompleted

**Given:**
- `before = MakeSnapshot(questCompleted: false)`
- `after  = before with { QuestCompleted = true, EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "turn-in"` (Rule 1)
- `result.InferredFrom == InferredFrom.QuestCompleted`

Pins "earlier rules take precedence over Rule 3.5e."

#### UEI8 — Priority below Rule 2.3 (KeyItemsAdded): pickup-item wins over EmoteCompleted

**Given:**
- `before = MakeSnapshot()`
- `after  = before with { KeyItemsAdded = new[] { 2001u }, EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: null) }`

**When:** `engine.Infer(before, after)`

**Then:**
- `result.StepType == "pickup-item"` (Rule 2.3)
- `result.InferredFrom == InferredFrom.DialogueInteraction`

Pins placement above Rule 3 but below the specific Rule-2.x family.

#### UEI9 — Aggregator: `OnEmoteCompleted` sets the field; `Current.EmoteCompleted` returns the same signal; `OnEmoteConsumed` clears; `ResetDeltas` does NOT clear

**Given:** `var agg = new SnapshotAggregator(activeQuest: null, clock: new FakeClock(T0));`

**When (sub-A — sets the field):**
- `agg.OnEmoteCompleted(emoteId: 17u, targetBaseId: 1000789u);`
- `var snap = agg.Current;`

**Then (sub-A):**
- `snap.EmoteCompleted is not null`
- `snap.EmoteCompleted.EmoteId == 17u`
- `snap.EmoteCompleted.TargetBaseId == 1000789u`

**When (sub-B — ResetDeltas does NOT clear):**
- `agg.ResetDeltas();`
- `var snap2 = agg.Current;`

**Then (sub-B):**
- `snap2.EmoteCompleted is not null` (survives ResetDeltas; only OnEmoteConsumed clears).

**When (sub-C — OnEmoteConsumed clears and does NOT side-effect):**
- `agg.OnEmoteConsumed();`
- `var snap3 = agg.Current;`

**Then (sub-C):**
- `snap3.EmoteCompleted is null`
- `snap3.LastNpcInteracted is null` (emote did NOT bleed into NPC-interaction state — pins Decision UEI3)
- `snap3.LastAttuned is null`
- `snap3.LastAethernetShardInteracted is null`
- `snap3.ActionCompleted is null` (defensive — unrelated field untouched)

This is one `[Fact]` with three Act/Assert blocks (matching the UAI8/UAI9/UAI10 style consolidation when the Tester chooses).

#### UEI10 — `StepFactory.Build("use-emote", …)` produces a `UseEmoteStep` with snapshot fields populated AND Motion = true

**Given:**
- `after = MakeSnapshot() with { EmoteCompleted = new EmoteCompletedSignal(EmoteId: 17u, TargetBaseId: 1000789u) }`

**When:** `var step = StepFactory.Build("use-emote", "use-emote-17-on-1000789", null, after);`

**Then:**
- `step is UseEmoteStep ue`
- `ue.Id == "use-emote-17-on-1000789"`
- `ue.EmoteId == 17u`
- `ue.TargetNpcId == 1000789u`
- `ue.Motion == true` (pins Decision UEI6)
- `ue.Expect is null`

#### UEI11 — `StepFactory.Build("use-emote", …)` defensive: EmoteCompleted null → EmoteId(0), no throw

**Given:** `after = MakeSnapshot()` (EmoteCompleted is null)

**When:** `var step = StepFactory.Build("use-emote", "use-emote-X", null, after);`

**Then:**
- `step is UseEmoteStep ue`
- `ue.EmoteId == 0u` (defensive fallback; validator will catch via E9)
- `ue.TargetNpcId is null`
- `ue.Motion == true` (default still applied)
- No exception thrown.

Pins Decision UEI7's defensive behaviour.

### UIObserver tests (`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`)

These tests use the existing `BuildFixtureWithAggregatorAndTarget` helper (lines 2853-2888) and the `FakeGameProbe` extension from Task UEI-T7. Test naming follows `UO_L*` convention (`L` for "live emote"; `UO_K*` is already taken by the action-effect group).

#### UO_L1 — First non-zero observation fires immediately (NO baseline-silent behavior)

**Given:**
- `var (obs, fw, ap, gp, clock, writer, _, agg, tp) = BuildFixtureWithAggregatorAndTarget();`
- `gp.SetPlayerEmoteId(17);` (the LocalPlayer is mid-emote at session start)

**When:** `fw.Tick();` (first tick after authoring started)

**Then:**
- Exactly one `ObservationEvent` with `Method == "EmoteCompleted"` and `Argument == 17u`.
- `agg.Current.EmoteCompleted is not null`
- `agg.Current.EmoteCompleted.EmoteId == 17u`
- `agg.Current.EmoteCompleted.TargetBaseId is null` (no target probe set).

**Critical contract:** unlike PollPlayerActionEffect (which silently baselines pre-session sequences per UO_K1), PollPlayerEmote treats a non-zero first observation as a live event because `EmoteId` reflects momentary state, not historical accumulation. Pins Decision UEI9's "different baseline strategy."

#### UO_L2 — Same emoteId on second tick: no event (sustained emote)

**Given:**
- `gp.SetPlayerEmoteId(17);`
- `fw.Tick();` (first event fired)

**When:** `fw.Tick();` (probe still returns 17 — the player is mid-sit / mid-cheer animation that has not yet completed)

**Then:**
- Total `ObservationEvent` count with `Method == "EmoteCompleted"` remains 1 (no second event from the second tick).
- `agg.Current.EmoteCompleted.EmoteId == 17u` (state still set from first tick).

Pins Decision UEI9's "same value = no-op."

#### UO_L3 — Emote ends (X → 0): state resets, no event for the end

**Given:**
- `gp.SetPlayerEmoteId(17); fw.Tick();` (first event fired)

**When:**
- `gp.SetPlayerEmoteId(0);`
- `fw.Tick();`

**Then:**
- Total `ObservationEvent` count with `Method == "EmoteCompleted"` remains 1 (the END is NOT an event).
- `agg.Current.EmoteCompleted` remains set from the first tick (UIObserver did not call OnEmoteConsumed; only ResetWindowState/RecordStep does).

Then **continuation:** trigger a new emote to verify state was reset correctly:
- `gp.SetPlayerEmoteId(22);`
- `fw.Tick();`

**Then (continuation):**
- Total `ObservationEvent` count with `Method == "EmoteCompleted"` is now 2.
- The second event's `Argument == 22u`.
- `agg.Current.EmoteCompleted.EmoteId == 22u` (latest wins; the per-window state is replaced by `OnEmoteCompleted`).

Pins Decision UEI9's "X → 0 resets state silently."

#### UO_L4 — Different emote (X → Y, both non-zero): fires for Y

**Given:**
- `gp.SetPlayerEmoteId(17); fw.Tick();` (event 1: id=17)

**When:**
- `gp.SetPlayerEmoteId(22);` (player immediately switched to a different emote without idling first)
- `fw.Tick();`

**Then:**
- Total `ObservationEvent` count with `Method == "EmoteCompleted"` is 2.
- The second event's `Argument == 22u`.
- `agg.Current.EmoteCompleted.EmoteId == 22u`.

Pins Decision UEI9's `X → Y` transition handling.

#### UO_L5 — ResetWindowState clears the last-observed emote id

**Given:**
- `gp.SetPlayerEmoteId(17); fw.Tick();` (event fired; `_lastObservedEmoteId = 17`)

**When:**
- `obs.ResetWindowState();` (sets `_lastObservedEmoteId = 0u` AND calls `OnEmoteConsumed`)
- `gp.SetPlayerEmoteId(22);` (a totally different emote; would have fired anyway as X→Y, but the reset ensures it would ALSO fire even if it were the same 17)
- `fw.Tick();`

**Then:**
- The second event fires (id=22, fresh after reset).
- `agg.Current.EmoteCompleted.EmoteId == 22u`.

**Critical sub-assertion (proves ResetWindowState calls OnEmoteConsumed):** between `ResetWindowState()` and the next `fw.Tick()`, `agg.Current.EmoteCompleted is null` (consumed by reset).

Pins Decision UEI8 (ResetWindowState calls OnEmoteConsumed) AND Decision UEI9 (reset to 0 re-baselines correctly).

#### UO_L6 — Null TargetManager.Target (no target probe set): TargetBaseId is null

**Given:**
- Fixture's `FakeTargetProbe` returns null for all queries (default).
- `gp.SetPlayerEmoteId(17);`

**When:** `fw.Tick();`

**Then:**
- One `ObservationEvent` with `Method == "EmoteCompleted"`, `Argument == 17u`.
- `agg.Current.EmoteCompleted.TargetBaseId is null`.

#### UO_L7 — Interactable NPC target: TargetBaseId is the NPC BaseId

**Given:**
- `tp.SetInteractableNpcTarget((BaseId: 1000789u, X: 0f, Y: 0f, Z: 0f, Zone: 132));`
- `gp.SetPlayerEmoteId(17);`

**When:** `fw.Tick();`

**Then:**
- One `ObservationEvent` with `Method == "EmoteCompleted"`, `Argument == 17u`.
- `agg.Current.EmoteCompleted.TargetBaseId == 1000789u`.

#### UO_L8 — Null IGameProbe: no observation, no NRE

**Given:** UIObserver constructed with `gameProbe: null` (existing `UO_A2` pattern; build manually rather than via the fixture builder since the builder always injects a probe).

**When:** multiple `fw.Tick()` calls.

**Then:**
- NO `ObservationEvent` with `Method == "EmoteCompleted"` written.
- No exception thrown.

#### UO_L9 — Target probe returns BOTH a BattleNpc and an interactable: BattleNpc wins (priority)

**Given:**
- `tp.SetBattleNpcTarget((BaseId: 5005u, X: 0f, Y: 0f, Z: 0f, Zone: 132));`
- `tp.SetInteractableNpcTarget((BaseId: 1000789u, X: 0f, Y: 0f, Z: 0f, Zone: 132));`
- `gp.SetPlayerEmoteId(17);`

**When:** `fw.Tick();`

**Then:**
- `agg.Current.EmoteCompleted.TargetBaseId == 5005u` (BattleNpc wins per Decision UEI9's branch order, matching UO_K10).

#### UO_L10 — Initial state (no SetPlayerEmoteId call): no event ever fires

**Given:** fixture with no `SetPlayerEmoteId` call (probe returns null).

**When:** multiple `fw.Tick()` calls.

**Then:**
- NO `ObservationEvent` with `Method == "EmoteCompleted"` written.
- `agg.Current.EmoteCompleted is null`.

Pins the `if (current is null) return;` early-out in `PollPlayerEmote`.

#### UO_L11 — Persistent emote (sit/doze): event fires once on start, NOT on each sustaining tick

**Given:**
- `gp.SetPlayerEmoteId(50);` (e.g. /sit — a persistent pose emote)

**When:**
- `fw.Tick();` (tick 1 — event 1 fires)
- `fw.Tick();` (tick 2 — same id 50, no event)
- `fw.Tick();` (tick 3 — same id 50, no event)
- `fw.Tick();` (tick 4 — same id 50, no event)

**Then:**
- Total `ObservationEvent` count with `Method == "EmoteCompleted"` is exactly 1.
- `agg.Current.EmoteCompleted.EmoteId == 50u`.

Pins Decision UEI9's "persistent emote handled correctly — fires once on `0 → X` start, never again while sustained."

### Plan-level scenario classification

| Scenario | File | Type |
|---|---|---|
| UEI1..UEI8 | `UseEmoteInferenceTests` | inference engine (incl. priority pinning) |
| UEI9 | `UseEmoteInferenceTests` | aggregator |
| UEI10..UEI11 | `UseEmoteInferenceTests` | StepFactory |
| UO_L1..UO_L11 | `UIObserverTests` | UIObserver + FakeGameProbe + FakeTargetProbe |

**Total: 11 inference/aggregator/factory + 11 UIObserver = 22 new tests.**

---

## F. Open questions / discovery items

### O1 — Does `EmoteController.EmoteId` populate for the LOCAL player?

**Status:** unknown until in-game validation.

The FFXIVClientStructs comment on `EmoteController.PlayEmote` says "For the local player, use AgentEmote.ExecuteEmote or EmoteManager.ExecuteEmote instead." This concerns **triggering** emotes (which goes through different game-side functions for the local player vs. remote characters). The READ side (`EmoteId` field) should still populate — the game uses the same struct for animation state regardless of who started it.

**Discovery recommendation:**
1. Implement the engine + UIObserver surface (Phases A–C). Tests pass against `FakeGameProbe`.
2. Implement `DalamudGameProbe.GetPlayerEmoteId` (Phase D).
3. Manual in-game: enter Inspect mode (which polls `[QF-DIAG]` log), trigger each of {/cheer, /salute, /sit, /umbrella, /point at an NPC}. Observe whether each produces an `[QF-DIAG]` entry with the expected EmoteId.
4. **If reads always return zero for the local player:** the brief's hint is correct and the read path is different. Fallback paths in priority order:
   - `EmoteManager` — FFXIVClientStructs may expose a "currently-playing emote" accessor; verify against the version in `C:\Users\publi\RiderProjects\FFXIVClientStructs\`.
   - `AgentEmote` — agent-side state for the player's emote UI.
   - Hook `AgentEmote.ExecuteEmote` — last resort; intrusive.

**Resolution unblocks Phase D** (in-game smoke). Phases A–C (engine surface + tests) are unblocked.

### O2 — Should auto-pose / sit / loop emotes be filtered out of inference?

The brief's §K asks whether persistent emotes should be filtered. Decision UEI2 rejected an eager filter. UO_L11 verifies the state machine correctly handles sustained emotes (one event per start).

**Open question:** should the inference suppress emote ids in a known-uninteresting list (e.g. `/sit` = 50, `/doze` = 13, `/cpose` = 90, etc.)? Author's expected intent when toggling `/sit` mid-recording is almost never "make sitting a step."

**Recommendation:** defer until smoke shows real-world authoring friction. If smoke surfaces "every recording session drafts a stray `/sit` step," add a `SkipEmoteIds: HashSet<uint>` in `PollPlayerEmote` with a small hand-curated list (`{ 50, 13, 90 }`-ish). Document the threshold in the source.

**Resolution unblocks NOTHING — purely UX polish.**

### O3 — Should outbound chat hook be used to infer Motion?

The brief's §H notes: "the in-game text command (visible via outbound chat hook) WOULD reveal motion vs non-motion." This would let the inference observe `/cheer motion` literally and set `Motion = false` only when the bare command was typed.

**Out of scope for this plan.** Reasons:
- Chat-hook plumbing does not exist in QF today (would need a new ECommons hook + state field on the snapshot).
- Authoring-mode coverage for chat-hook inference would extend beyond emotes (macros, future SayChatMessage steps).
- The default-true behavior (Decision UEI6) is correct for 99% of cases; the manual override in the modal is trivial.

**Recommendation:** record as Phase 9+ follow-up alongside the broader outbound-chat-inference work.

### O4 — Race between EmoteId read and target read

The poller does:
1. Read `_gameProbe.GetPlayerEmoteId()` (returns `ushort?`).
2. Read `_targetProbe.GetBattleNpcTarget()` / `GetInteractableNpcTarget()`.

Between steps 1 and 2, the player could have retargeted (frame ~16 ms; tab-target hit). Acceptable in v1 per the same reasoning as UAI's O3 — chance of meaningful retarget mid-frame is low. If smoke surfaces a real bug, cache the `TargetManager.Target` snapshot inside `GetPlayerEmoteId` itself (probe-side capture) or use `EmoteController.Target` (rejected in Decision UEI12 but could be revisited).

Defer. Not a release blocker.

### O5 — Should `IGameProbe.GetPlayerEmoteId` be called by Inspect mode (passive trace)?

Currently `UIObserver` calls all every-frame pollers regardless of mode; the `WriteObservation` calls write to the trace per the TraceSession gate. If TraceMode is `Always` or `Authoring`, the `"EmoteCompleted"` events appear in the trace even outside Author mode. Same posture as `"ActionCompleted"`, `"AethernetTeleportCompleted"`, `"TeleportCompleted"`.

**Decision: yes, fire in Inspect mode too** (matches existing pattern). The aggregator forwarding is gated by `_aggregator is not null` — in Inspect mode there IS an aggregator (`EnterInspectModeCore` calls `SetAggregator(_aggregator, "inspect")`) so the snapshot field updates, but `RecordStep` is not invoked in Inspect mode so the field is never consumed. The Record modal in Inspect mode is read-only anyway.

---

## Implementation order

**Phase A — Engine surface (15 min, all xUnit-testable)**
1. Task UEI-T1: `EmoteCompletedSignal` record + `GameStateSnapshot.EmoteCompleted` property.
2. Task UEI-T2: `InferredFrom.EmoteCompleted`.
3. Task UEI-T3: `SnapshotAggregator.OnEmoteCompleted` / `OnEmoteConsumed` methods + `Current` initializer.
4. Tester: write UEI9 (aggregator test). Red, implement, green.

**Phase B — Inference rule + StepFactory (10 min)**
1. Task UEI-T4: insert Rule 3.5e in `StepInferenceEngine` (between Rule 2.6 and Rule 3.5).
2. Task UEI-T5: add `"use-emote"` arm to `StepFactory.Build`.
3. Tester: write UEI1..UEI8 (inference tests with priority pinning) + UEI10..UEI11 (StepFactory tests). Red, implement, green.

**Phase C — AuthoringHost clearing + diagnostic (2 min)**
1. Task UEI-T6: add `_aggregator.OnEmoteConsumed();` to `RecordStep` and extend `[QF-DIAG]` line.
2. No new test in this plan (covered structurally by UEI-T3's aggregator test; the host wiring is a one-line edit verified in-game).

**Phase D — UIObserver tests + impl (30 min)**
1. Task UEI-T7: extend `IGameProbe` and `FakeGameProbe`.
2. Tester: write UO_L1..UO_L11. Red.
3. Task UEI-T8: implement `PollPlayerEmote`, `ResetWindowState` updates. Green.

**Phase E — Dalamud probe + in-game smoke (BLOCKED on §F O1 verification)**
1. Task UEI-T9: implement `DalamudGameProbe.GetPlayerEmoteId`.
2. Manual in-game test: enter Inspect mode, trigger {/cheer, /salute, /sit, /umbrella, /point at NPC} — confirm the `[QF-DIAG]` line for each shows `EmoteCompleted` with the expected EmoteId.
3. If reads always return 0 for self (§F O1 fails): pivot to `EmoteManager` or `AgentEmote` accessor. Update Task UEI-T9 implementation; no test changes needed (the `IGameProbe.GetPlayerEmoteId` contract is unchanged).
4. Enter Author mode for any quest containing an emote requirement, trigger the emote, open Record modal — confirm the modal shows `use-emote` inference with the correct EmoteId, TargetNpcId, and Motion=true.
5. Confirm Decision UEI6 in the modal's draft preview: the JSON shows `"motion": true` regardless of whether the player typed `/cheer` or `/cheer motion`.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~UseEmoteInferenceTests` reports all 11 inference/aggregator/factory tests green.
2. `dotnet test QuestForge.Plugin.Tests --filter "FullyQualifiedName~UIObserverTests&FullyQualifiedName~UO_L"` reports all 11 UIObserver tests green.
3. No regression in existing `StepInferenceEngineTests`, `UseActionInferenceTests`, `TeleportInferenceTests`, `AethernetInferenceTests`, `AethernetStepFactoryTests`, or `UIObserverTests` (UO_A–UO_K).
4. The trace stream emitted during an emote-use contains an `ObservationEvent` with `Method == "EmoteCompleted"`, `Argument == <emoteId>`, and value object containing `targetBaseId`.
5. **In-game smoke (after Phase E):** With Author mode enabled for any quest, triggering an emote produces a draft `UseEmoteStep { EmoteId, TargetNpcId?, Motion = true }` in the recorded steps. The author edits the `Expect` field; the draft validates.
6. The `[QF-DIAG] PreviewInference:` line includes `EmoteCompleted={...}` and shows the correct EmoteId when an emote was the inferred trigger.

---

## Exclusions (what this plan does NOT include)

- **Validator rules** for `structural/use-emote-*` — already shipped (E9, E10, W8 per `USE_EMOTE_STEP_PLAN.md` Decision UE7).
- **Persistent-emote filtering** (§F O2). Defer until smoke shows real-world friction. If needed, a `SkipEmoteIds` HashSet in the poller is a 5-minute follow-up.
- **Motion-suppress vs. motion-broadcast distinction at inference time** (§F O3). Inference always sets `Motion = true`; chat-hook inference is a Phase 9+ follow-up.
- **`EmoteController.Target` (the emote's actual target)** — explicitly rejected (Decision UEI12). We use `TargetManager.Target` at the time of the read.
- **Emote-end detection** — `X → 0` is silently consumed (not an event). Only emote-start (`0 → X` or `X → Y`) triggers inference.
- **Emote loops (loops between active and idle frames)** — the state machine only fires on `0/X → Y` transitions; in-loop sustaining is invisible.
- **Inferring `Motion = false`** — always `true`. Manual override in the modal.
- **Trace-side extractor** (`qf-trace extract-quest` route for `"EmoteCompleted"` events) — Phase 10 follow-up.
- **Re-firing on the same emote** — the polling state-machine is "transition-edge fires; same value ignored." Using the same emote twice with an idle frame in between produces two events (`0 → X`, `X → 0`, `0 → X`); using it twice rapidly without idling produces one event (`0 → X`, `X == X`). The reset on idle is the implicit re-arm.
- **Custom-emote / minion / chocobo-rider-emote handling** — `EmoteController.EmoteId` is a single Lumina row; we forward whatever id the field holds. No special-casing.
- **Pet / minion summon emotes** — out of scope; not authorable in v1.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 4 scenarios (UEI1, UEI2, UEI3, UO_L1)
- Edge cases: 9 scenarios (UEI4, UEI5, UEI9, UEI10, UO_L2, UO_L4, UO_L5, UO_L7, UO_L9, UO_L11)
- Error / no-op cases: 6 scenarios (UEI11, UO_L3, UO_L6, UO_L8, UO_L10)
- Priority pinning: 4 scenarios (UEI4, UEI5, UEI6, UEI7, UEI8) — covers above/below for Rules 1, 2.3, 3, 3.5 (ActionCompleted), 4
- Expected total: ~22 tests across two files:
  - 11 in `QuestForge.Engine.Tests/Authoring/UseEmoteInferenceTests.cs`
  - 11 in `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs` (as new `UO_L*` group)

Builder is **fully unblocked for Phases A–D**. Phase E (in-game smoke) requires the `DalamudGameProbe.GetPlayerEmoteId` impl plus §F O1 verification (does `EmoteController.EmoteId` populate for the local player?).
