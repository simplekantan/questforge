# Phase 0 Spike Notes — Coming to Ul'dah

**Status:** in progress
**Quest:** Coming to Ul'dah (breadcrumb → Close to Home, Ul'dah)
**Date started:** <!-- fill in -->

Fill this in as you discover things. The notes are the deliverable — more valuable than the code.

---

## Hardcoded values to fill in before first run

| Constant | Where to find it | Value found |
|----------|-----------------|-------------|
| `QuestData.QuestId` | Lumina: `Quest` sheet, filter `Name = "Coming to Ul'dah"` | |
| `QuestData.TargetZone` | Lumina: `TerritoryType` sheet, or read `ClientState.TerritoryType` in-zone | |
| `QuestData.AetheryteId` | Lumina: `Aetheryte` sheet, or Lifestream's own aetheryte list | |
| `QuestData.Npc1DataId` | Walk up to NPC in-game, read `TargetManager.Target.DataId` in /xldev | |
| `QuestData.Npc1Pos` | Stand near NPC, read `ClientState.LocalPlayer.Position` in /xldev | |
| `QuestData.Npc2DataId` | Same as above for turn-in NPC | |
| `QuestData.Npc2Pos` | Same as above | |
| `QuestData.SeqAccepted` | Check `QuestManager` after accepting quest — what sequence number appears? | |

---

## IPC contract findings

### vnavmesh

**Gate names used:**
- `vnavmesh.Nav.IsReady` → `Func<bool>`
- `vnavmesh.Nav.IsRunning` → `Func<bool>`
- `vnavmesh.SimpleMove.PathfindAndMoveTo` → `Func<Vector3, bool, bool>`
- `vnavmesh.Nav.Stop` → `Func<bool>`

**Questions to answer:**
- [ ] Do these gate names exactly match what vnavmesh exposes? Check `IPCProvider.cs` in vnavmesh source.
- [ ] Does `IsReady` return `false` immediately after a zone change? How long until it returns `true`?
- [ ] Does `SimpleMove.PathfindAndMoveTo` block until arrived, or is it fire-and-forget?
- [ ] Does `Nav.IsRunning` flip to `false` on arrival, or does it need explicit `Stop()`?
- [ ] What happens if called while navmesh isn't ready yet?
- [ ] Does the `fly: false` parameter correctly force ground pathing?

**Findings:**
<!-- fill in after testing -->

---

### Lifestream

**Gate names used:**
- `Lifestream.Teleport` → `Func<uint, byte, bool>` (aetheryteId, subIndex)
- `Lifestream.IsBusy` → `Func<bool>`

**Questions to answer:**
- [ ] Are these gate names correct? Check Lifestream source for IPC declarations.
- [ ] What does `Teleport` return `false` for? (not attuned? already in zone? plugin busy?)
- [ ] Is `IsBusy` the right way to detect "teleport in flight"? Or should we just poll `TerritoryType`?
- [ ] What is `subIndex` used for? (ward sub-destinations? always 0 for standard aetherytes?)
- [ ] Does Lifestream handle the "you need to attune first" case, or do we need to check separately?

**Findings:**
<!-- fill in after testing -->

---

### TextAdvance

**Gate names used:**
- `TextAdvance.IsEnabled` → `Func<bool>`

**Questions to answer:**
- [ ] Is the gate name correct?
- [ ] Does TextAdvance auto-advance dialogue without any explicit IPC call from us?
- [ ] Does it handle dialogue option selection (selecting a response) automatically?
- [ ] Do we need to call anything to "enable" it per-session, or is it always on when the plugin is loaded?
- [ ] Are there any dialogue types it does NOT handle that we'd need to handle ourselves?

**Findings:**
<!-- fill in after testing -->

---

### Quest state / QuestManager

**Questions to answer:**
- [ ] What is the correct API to read quest sequence numbers from Dalamud?
  - Unsafe: `QuestManager.Instance()->GetQuestById(questId)->Sequence`?
  - Is there a safe Dalamud service equivalent?
- [ ] Does the sequence number update on the same frame dialogue closes, or is there a lag?
- [ ] What value does an un-accepted quest have? (0? null? absent from the manager?)
- [ ] What is the "quest complete" sequence value? (255? something else?)

**Findings:**
<!-- fill in after testing -->

---

### NPC interaction

**Questions to answer:**
- [ ] Dalamud doesn't expose a direct "interact with target" API. What approach works?
  - Option A: `TargetManager.Target = npc` then send VKey.NUMPAD0 (confirm key)?
  - Option B: Is there a framework method or IGameGui method for this?
  - Option C: Does TextAdvance / some other plugin expose an "interact" IPC?
- [ ] Does interaction require being within a specific range, and if so what is it exactly?
- [ ] Does targeting + interact work if there's another player already in dialogue with the NPC?

**Findings:**
<!-- fill in after testing -->

---

## Architecture assumptions that need revisiting

<!-- Add at least 3 after completing the spike. Example format: -->
<!--
### 1. [Assumption from design docs]
**What we assumed:** ...
**What we found:** ...
**Impact on architecture:** ...
-->

---

## Other surprises / quirks

<!-- Anything that didn't fit the above categories -->

---

## Verdict

- [ ] Spike completed (one quest automated end-to-end)
- [ ] At least 3 architectural assumptions documented above
- [ ] SPIKE_NOTES.md shared / committed
- [ ] Code deleted or clearly marked as throwaway

**Ready to start Phase 1?** <!-- yes / no / needs more investigation -->