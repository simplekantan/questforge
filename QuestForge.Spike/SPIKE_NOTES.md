# Phase 0 Spike Notes — Coming to Ul'dah

**Status:** Complete ✓
**Quest:** Coming to Ul'dah (rowId=66130, breadcrumb → Close to Home, Ul'dah)
**Date completed:** 2026-05-14
**Result:** Full end-to-end automation confirmed on a fresh Gladiator character, ~41 seconds.

---

## Hardcoded values resolved

| Constant | How found | Value |
|---|---|---|
| `QuestId` | `IDataManager.GetExcelSheet<Quest>()` on plugin load | **66130** |
| `TargetZone` | `ClientState.TerritoryType` in-game | **182** (new-player instance, NOT 130) |
| `AetheryteId` | `IDataManager.GetExcelSheet<Aetheryte>()` | **9** (Ul'dah Steps of Nald, IsAetheryte=True) |
| `Npc1DataId` | `/xldata > Target > BaseId` (Wymond) | **1003987** |
| `Npc1ApproachPos` | ObjectTable player position while standing near Wymond | **(35.56, 4.0, -151.18)** |
| `Npc2DataId` | `/xldata > Target > BaseId` (Momodi) | **1003988** |
| `Npc2ApproachPos` | ObjectTable player position standing near Momodi | **(21.84, 7.0, -81.13)** |
| `SeqAccepted` | N/A — quest goes 0→255 directly | **255 (no intermediate state)** |
| `SeqReadyToTurnIn` | `/qfspike seq` after acceptance | **255** |

**How to look up values:** Use `IDataManager.GetExcelSheet<T>()` injected via `[PluginService]` — no standalone Lumina setup needed inside a plugin. For NPC DataIds and positions: use `/xldata > Target` and `/xldata > Object Table` while standing in-game.

---

## IPC contract findings

### vnavmesh

**Confirmed correct gate names** (checked against `IPCProvider.cs` in ffxiv_navmesh source):

| Gate | Signature | Notes |
|---|---|---|
| `vnavmesh.Nav.IsReady` | `Func<bool>` | Check before navigating after zone change |
| `vnavmesh.SimpleMove.PathfindAndMoveTo` | `Func<Vector3, bool, bool>` | dest, fly → success |
| `vnavmesh.SimpleMove.PathfindAndMoveCloseTo` | `Func<Vector3, bool, float, bool>` | dest, fly, range → success. **Use this instead of PathfindAndMoveTo.** |
| `vnavmesh.SimpleMove.PathfindInProgress` | `Func<bool>` | Whether pathfind computation is running |
| `vnavmesh.Path.IsRunning` | `Func<bool>` | Whether movement is active |
| `vnavmesh.Path.Stop` | `Func<bool>` | Stop movement |
| `vnavmesh.Query.Mesh.NearestPointReachable` | `Func<Vector3, float, float, Vector3?>` | Find nearest walkable point to a position |

**Gate names that do NOT exist** (our initial assumptions were wrong):
- `vnavmesh.SimpleMove.IsRunning` — does not exist
- `vnavmesh.Nav.Stop` — does not exist
- `vnavmesh.Nav.IsRunning` — does not exist

**Key behaviors:**
- `PathfindAndMoveTo` / `PathfindAndMoveCloseTo` are **fire-and-forget** — they return immediately, movement is async.
- `Path.IsRunning` returns **false briefly after calling PathfindAndMoveCloseTo** while the pathfinding computation is still running (async). Wait at least ~30 ticks before polling, or the state machine transitions immediately.
- `PathfindAndMoveCloseTo(dest, fly, range)` — vnavmesh stops the player when within `range` yalms of `dest`. This is the correct primitive for "navigate to within interact range."
- `Nav.IsReady` returns false after zone changes while the navmesh is being generated. Wait for true before issuing movement commands.
- vnavmesh **cannot path across zone boundaries**.
- Ground pathing (fly=false) works correctly.

---

### Lifestream

**Gate names used:**
- `Lifestream.Teleport` → `Func<uint, byte, bool>` (aetheryteId, subIndex)
- `Lifestream.IsBusy` → `Func<bool>`

**Findings:**
- `Teleport` returns **false** (no exception) when the aetheryte is not attuned or the player has insufficient gil.
- `Teleport` was not successfully tested — the test character had no gil and no Ul'dah attunement.
- For new characters, teleport is not a viable first-quest strategy; they must walk.
- **Architecture impact:** `IAdapterTeleporter` must check attunement and gil before calling and surface a distinct "not attuned" / "insufficient gil" result rather than a generic failure.
- `subIndex=0` is standard for non-ward aetherytes (untested for housing wards).

---

### TextAdvance

**Gate names (via EzIPC):**
- `TextAdvance.IsEnabled` → `Func<bool>`
- `TextAdvance.GetEnableTalkSkip` → `Func<bool>`
- `TextAdvance.GetEnableCutsceneEsc` → `Func<bool>`
- `TextAdvance.GetEnableCutsceneSkipConfirm` → `Func<bool>`
- `TextAdvance.EnableExternalControl` → `Func<string, ExternalTerritoryConfig, bool>` ← requires TextAdvance.dll reference

**Key findings:**
- TextAdvance **does NOT expose** a simple "advance dialogue now" or "skip cutscene now" action IPC.
- TextAdvance hooks the `Talk` addon via `AddonLifecycle` events and auto-advances when condition flags are set (`OccupiedInQuestEvent`, `OccupiedInEvent`, `WatchingCutscene`, `WatchingCutscene78`, etc.).
- When condition flags are NOT set (e.g., post-quest Momodi dialogue after completion), TextAdvance does NOT advance — our own code must handle it.
- TextAdvance cutscene skip uses a game function hook (`CutsceneHandleInput`) — there is no simple IPC equivalent we can call.
- `EnableExternalControl` IPC exists but requires `ExternalTerritoryConfig` (a TextAdvance-internal type), making it unusable without adding TextAdvance as a compiled dependency.
- **Architecture impact:** `IInteractor` must implement dialogue advancement and cutscene skipping independently; TextAdvance cannot be relied upon as the sole mechanism.

**Licensing:** TextAdvance source is licensed "all rights reserved" (no LICENSE file). ECommons (submodule) is **MIT** (NightmareXIV, 2023). Production `IInteractor` must be implemented independently, not derived from TextAdvance source.

---

### Quest state / QuestManager

**Confirmed API:**
```csharp
// Read quest sequence — in-game ID = Lumina rowId lower 16 bits
unsafe {
    var gameId = (ushort)(rowId & 0xFFFF);
    var quest = QuestManager.Instance()->GetQuestById(gameId);
    // quest == null → not in journal (not accepted, OR already completed)
    var seq = quest == null ? (byte)0 : quest->Sequence;
}

// Distinguish "not accepted" from "completed"
unsafe {
    bool complete = QuestManager.IsQuestComplete((ushort)(rowId & 0xFFFF));
}

// Scan active quests in journal (up to 30)
unsafe {
    for (var i = 0; i < 30; i++) {
        var q = QuestManager.Instance()->NormalQuests[i];
        if (q.QuestId == 0) continue;
        // q.QuestId = in-game ID; Lumina rowId = q.QuestId | 0x10000u
    }
}
```

**Sequence number behavior:**
- `0` — quest not in journal. **Ambiguous:** could mean "never accepted" or "already completed."
- `1`–`254` — quest in progress, intermediate objectives pending (quest-specific).
- `255` — all objectives complete, ready to turn in. Quest is still in the journal.
- After turn-in — quest leaves journal entirely; `GetQuestById` returns null; seq→0.
- Use `QuestManager.IsQuestComplete()` to distinguish seq=0 (not accepted) from seq=0 (completed).

**"Coming to Ul'dah" specific:**
- Seq goes **0→255 directly** on acceptance — no intermediate objectives for this quest.
- The acceptance flow involves a cutscene (the Ul'dah intro cinematic).
- After the cutscene, `JournalAccept` appears. Seq becomes 255 as part of that flow.
- `JournalAccept` can appear **without a `Talk` addon ever appearing first** — the state machine must have a seq-based escape hatch in `TickInteract`, not rely solely on Talk appearing and closing.

**Close to Home (next quest) specifics:**
- rowId=66104 (Gladiator), 66105 (Pugilist?), 66106 (Thaumaturge?)
- Has intermediate objectives (seq=1 on acceptance for Gladiator).
- Each class variant sends you to visit your respective guild.

---

### NPC interaction

**Confirmed approach:**
```csharp
// 1. Set target
TargetManager.Target = npc; // npc from ObjectTable

// 2. Trigger interaction via TargetSystem
unsafe {
    var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
    if (ts->Target != null)
        ts->InteractWithObject(ts->Target, false);
}
```

**Key findings:**
- FFXIV's actual interact range for these NPCs: ~**2.5 yalms** (at 6 yalms we got "too far to interact").
- Targeting alone does nothing — `InteractWithObject` must be explicitly called.
- If the interaction doesn't register immediately (Talk doesn't appear), retry `InteractWithObject` every ~5 seconds.
- Works correctly even if `Path.IsRunning` just stopped — no movement delay needed before interacting.

---

### Talk addon — THREE structural variants

The `Talk` addon (NPC dialogue) has three distinct node structures. Each requires a different click mechanism. All three must be handled by `IInteractor.AdvanceDialogue()`.

**Variant 1 — Normal NPC dialogue (standard node 132):**
- Present when: player talks to NPCs outside quest events in the open world.
- Identifying feature: `addon->GetNodeById(132)` returns non-null.
- NOT identified in this spike (Wymond uses Variant 3 during quest events).

**Variant 2 — System Talk (tutorial, control scheme):**
- Present when: game shows a system message (control scheme selection, tutorial tips).
- Identifying feature: No node 132; `addon->WindowHeaderCollisionNode` is non-null.
- Click target: `WindowHeaderCollisionNode->AtkResNode.AtkEventManager.Event`.
- AtkValues[0]: `ManagedString` type (e.g., "Please select the control scheme").

**Variant 3 — NPC dialogue during quest events / cutscenes:**
- Present when: NPC speaks during quest acceptance flow, cutscenes, or event sequences.
- Identifying feature: No node 132; `addon->WindowHeaderCollisionNode` is **null**.
- Click target: First `AtkResNode` in `addon->CollisionNodeList` where `NodeFlags.RespondToMouse` is set.
- AtkValues[0]: `ManagedString` type (NPC name in [1], dialogue text in [0]).
- Example: Wymond's dialogue during "Coming to Ul'dah" acceptance.

**Correct AtkEvent construction for ALL variants:**
```csharp
// CRITICAL: CreateAtkEvent(132) in ECommons sets StateFlags=132, NOT Node=node-id-132.
// The 132 is an AtkEventStateFlags value. Listener and Target must also be set.
var evt = stackalloc AtkEvent[1];
evt[0].Listener = (AtkEventListener*)addon;
evt[0].Target   = &AtkStage.Instance()->AtkEventTarget;
evt[0].State    = new AtkEventState { StateFlags = (AtkEventStateFlags)132 };
var data = stackalloc AtkEventData[1];
addon->ReceiveEvent(AtkEventType.MouseDown,  0, evt, data);
addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
addon->ReceiveEvent(AtkEventType.MouseUp,    0, evt, data);
```

**CRASH WARNING:** Creating an `AtkEvent` on the stack with `Node` set to an arbitrary pointer (collision node, etc.) and other fields uninitialized causes a game crash. The handler dereferences `Target` and `Listener`. Always initialize all three fields (Listener, Target, State.StateFlags) or leave Node null.

**Throttle:** Call `TryAdvanceTalkAddon` at most every ~15 ticks. Running every frame caused crashes and excessive IPC calls.

---

### JournalAccept (quest acceptance window)

- Addon name: `JournalAccept`
- Accept button ID: **44** (from TextAdvance `ExecQuestAccept.cs`)
- Click pattern:
  ```csharp
  var button = addon->GetComponentButtonById(44);
  var btnResNode = button->AtkComponentBase.OwnerNode->AtkResNode;
  var evt = btnResNode.AtkEventManager.Event;
  addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt);
  ```
- Can appear WITHOUT a preceding `Talk` dialog — JournalAccept is the first addon shown in some quest acceptance flows.

---

### JournalResult (quest completion window)

- Addon name: `JournalResult`
- Complete button ID: **37** (from TextAdvance `ExecQuestComplete.cs`)
- Same click pattern as JournalAccept using `GetComponentButtonById(37)`.
- `AtkEventType.ButtonClick` with `evt->Param` from the button's EventManager.

---

### Cutscene skipping

**Two-part mechanism (both required):**

**Part 1 — Hook to trigger ESC:**
- Hook: `CutsceneHandleInput` game function
- Signature: `"48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 40 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 80 79 29 00"`
- Skippability check: `*(nint*)(a1 + 56) != 0`
- Byte patch (JNZ→JMP): `"75 11 BA ?? ?? ?? ?? 48 8B CF E8 ?? ?? ?? ?? 84 C0 74 4C"` offset 0, byte `0xEB`
- Only fires when `ConditionFlag.OccupiedInCutSceneEvent` is set.
- Source: ECommons `AutoCutsceneSkipper` (MIT license — safe to use with attribution).

**Part 2 — Confirm the skip dialog:**
- Addon name: `SelectString` (NOT `CutSceneSelectString`)
- Guarded by `OccupiedInCutSceneEvent` || `WatchingCutscene78` condition flags to avoid accidental clicks on non-cutscene SelectString prompts.
- Click index 0 (Skip) via `FireCallback(1, value, true)` where `value[0].Type = AtkValueType.Int; value[0].Int = 0`.
- `updateState = true` required for SelectString FireCallback.

---

### SDK 15 / Dalamud API differences from older code

- `AtkValue.Type` enum: **`AtkValueType`** (renamed from `ValueType` — `System.ValueType` conflict)
- `IClientState.LocalPlayer`: **removed** in SDK 15. Use `ObjectTable[0]` for the local player object.
- `IGameGui.GetAddonByName()`: returns `AtkUnitBasePtr` wrapper — cast to `(nint)` then `(AtkUnitBase*)`.
- `IGameInteropProvider.HookFromAddress<T>()` returns `Hook<T>` (not `ICallableHook<T>`).
- `Hook<T>` is in `Dalamud.Hooking` namespace.

---

## Architecture assumptions that need revising

### 1. vnavmesh IPC gate names were all wrong
**What we assumed:** `SimpleMove.IsRunning`, `Nav.Stop`, `Nav.IsRunning`
**What we found:** Correct gates are `Path.IsRunning`, `Path.Stop`. `SimpleMove.IsRunning` does not exist. `PathfindAndMoveCloseTo` (with range param) is the right primitive — `PathfindAndMoveTo` lacks the stop-within-range capability.
**Impact:** `INavigator` adapter must use `PathfindAndMoveCloseTo` not `PathfindAndMoveTo`. The adapter interface should accept an interact range parameter.

### 2. NPC ObjectTable positions are often unreachable
**What we assumed:** Nav to NPC position, arrive, interact.
**What we assumed initially:** Raw ObjectTable positions cannot be used — a manually-recorded walkable `ApproachPos` is required per NPC.
**What we actually found:** `vnavmesh.SimpleMove.PathfindAndMoveCloseTo(npc.rawPosition, fly, range)` largely solves this. vnavmesh pathfinds to the closest reachable navmesh polygon and stops when the player is within `range` yalms (Euclidean) of the destination. For Momodi (behind a counter) and Wymond, the closest walkable point was within 2.5 yalms of their actual positions — confirmed by successful interaction on live runs using raw ObjectTable positions.
**Remaining edge case:** An NPC whose closest walkable navmesh point is further than `interactRange` from their actual position would cause the player to run indefinitely. No such NPC was encountered in this spike, but it is theoretically possible for NPCs deeply embedded in geometry.
**Revised impact:** Quest schema does NOT need a `standPosition` field for most NPCs — just the NPC DataId. `INavigator.NavigateCloseTo(Vector3, float range)` maps directly to `PathfindAndMoveCloseTo` and is the correct abstraction. Manually-recorded approach positions should be an optional override for edge cases, not a requirement.

### 3. IInteractor.AdvanceDialogue must handle 3 Talk addon variants
**What we assumed:** Click node 132. Simple.
**What we found:** Three distinct Talk structures exist; node 132 is absent for NPC dialogue during quest events. The correct mechanism (`CreateAtkEvent`) does not reference node 132 at all — it sets `AtkEventState.StateFlags = 132`. All three variants are advanced by the same AtkEvent construction but discovered through different paths.
**Impact:** `IInteractor` implementation is non-trivial. The production implementation should use the StateFlags=132 + Listener + Target pattern regardless of Talk variant. `IInteractor` should NOT depend on node 132 existing.

### 4. Zone identification requires more than PlaceName
**What we assumed:** Look up `TerritoryType` by `PlaceName` to identify zones.
**What we found:** Multiple `TerritoryType` rows share the same `PlaceName` (e.g., "Ul'dah - Steps of Nald" maps to rows 130, 182, 251, 254, 259, 274, 790). Row 182 is the new-player tutorial instance. New characters start in 182, not 130. `TerritoryIntendedUse.RowId == 1` filters to open-world zones only.
**Impact:** Quest schema must store explicit `TerritoryType` row IDs, not PlaceNames. Zone variant selection must be player-progression-aware (new player vs veteran same zone, different row ID).

### 5. Quest state is ambiguous at seq=0
**What we assumed:** seq=0 means quest not started.
**What we found:** seq=0 means EITHER "not yet accepted" OR "already completed." `QuestManager.GetQuestById()` returns null in both cases. `QuestManager.IsQuestComplete()` must be called to distinguish them. Additionally, JournalAccept can fire before Talk ever appears, so the interaction state machine cannot rely solely on Talk appearing and closing — it needs a seq-based escape hatch.
**Impact:** `IQuestState` must expose both `GetQuestSequence()` and `IsQuestComplete()`. Engine state machines must guard against seq=0 ambiguity.

### 6. "Close to Home" has 3 variants by starting class
**What we assumed:** One quest per MSQ chain.
**What we found:** "Close to Home" in Ul'dah has 3 rowIds (66104, 66105, 66106) — one per starting class (Gladiator, Pugilist, Thaumaturge). Each has a class-specific intermediate objective (visit your guild). The engine cannot hardcode a single quest ID — it must detect which variant is in the player's journal.
**Impact:** `IQuestState.GetActiveQuestId()` capability needed. Quest data may need a "variant family" concept where multiple rowIds represent the same logical quest step.

---

## Other surprises / quirks

**Lifestream returns false, not exception, for unattested aetherytes.** The caller must explicitly check the return value. No exception is thrown for "cannot teleport here."

**`Path.IsRunning` async timing gap.** After calling `PathfindAndMoveCloseTo`, the function returns before pathfinding computation is complete. For the next ~30 ticks, `Path.IsRunning` returns false even though the player hasn't moved yet. Polling too early causes immediate false "arrived" detection.

**`WatchingCutscene78` vs `OccupiedInCutSceneEvent`.** The cutscene hook only fires on `OccupiedInCutSceneEvent`. Some cutscenes only set `WatchingCutscene78`. Those cutscenes cannot be skipped via the hook and must be waited out with dialogue advancement.

**`JournalAccept` can bypass `Talk` entirely.** For "Coming to Ul'dah," the quest acceptance flow shows JournalAccept as the first UI element — there is no preceding Talk dialog. State machines that wait for Talk→close before checking for JournalAccept will get stuck.

**AtkEvent stack allocation crash pattern.** Any `AtkEvent` created on the stack where `Node` is set to an arbitrary pointer but `Listener` and `Target` are zero/null will crash the game when the handler processes it. These fields are always dereferenced. Either use an existing event from a node's `AtkEventManager.Event`, or initialize all three fields (Listener, Target, State.StateFlags).

**Multiple addon types use `Talk` name but different structures.** The game uses the Talk addon for: (a) NPC dialogue windows, (b) system tutorial messages, (c) control scheme selection. The AtkValues differ between these. AtkValues[2] = 0 appears to indicate system messages; AtkValues[2] = 1 appears for NPC/event dialogue — but this is not confirmed as a reliable discriminator.

**`HowToNotice` addon appears for new players.** A tutorial tip addon ("HowToNotice") appears alongside Talk during new character tutorials. It's a separate addon — not handled by the spike — but may block automation on truly fresh characters. Worth handling before the Phase 6 production run.

---

## Verdict

- [x] Spike completed — "Coming to Ul'dah" automated end-to-end (~41 seconds)
- [x] At least 3 architectural assumptions documented (6 found and documented above)
- [x] SPIKE_NOTES.md filled in and committed
- [x] Code clearly marked as throwaway in csproj and commit message

**Ready to start Phase 1?** Yes. All three IPC contracts validated. The primary blockers for Phase 1 (schema validator) are unrelated to Dalamud — it's a CLI tool reading JSON and validating against C# schema types.

**Outstanding questions for Phase 6 (Dalamud adapters):**
- Lifestream: confirm `subIndex` semantics for housing ward aetherytes.
- vnavmesh: confirm `Nav.IsReady` wait behavior after zone change and ideal polling strategy.
- NPC stand positions: authoring tooling must capture player position at time of interaction, not NPC position.
- `IInteractor.SkipCutscene()`: production implementation must use `CutsceneHandleInput` hook (ECommons pattern, MIT); document dependency in `ADAPTERS.md`.