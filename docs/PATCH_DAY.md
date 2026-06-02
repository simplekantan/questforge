# Patch Day Checklist

Steps to verify QuestForge works after a new FFXIV patch.

---

## 1. Wait for upstream updates

QuestForge cannot load until Dalamud and its dependencies update for the new game version.

- [ ] **Dalamud** — updated for new game version (plugin won't load without this)
- [ ] **FFXIVClientStructs** — NuGet package updated (struct offsets may shift)
- [ ] **Lumina** — NuGet package updated (Excel sheet schemas may change)

## 2. Update dependency plugins

Check that each dependency plugin has a 7.x-compatible release:

- [ ] **vnavmesh** — in-zone navigation
- [ ] **Lifestream** — aetheryte teleport
- [ ] **TextAdvance** — dialogue skipping
- [ ] **BossMod / WrathCombo / RSR** — combat rotation (at least one)
- [ ] **AutoDuty** — dungeon pathing
- [ ] **Stylist** — gear management (optional, falls back to native)

## 3. Build and test

```bash
dotnet build
dotnet test QuestForge.Engine.Tests
dotnet test QuestForge.Adapters.Tests
```

- [ ] Build succeeds against updated Dalamud/ClientStructs references
- [ ] Engine tests pass (these are game-independent and should always pass)
- [ ] Adapter tests pass

## 4. Check FFXIVClientStructs fields we depend on

Our `DalamudGameProbe` reads raw struct fields for authoring inference. Offset shifts break silently at runtime (no compile error — just wrong data or crashes). Verify these fields still exist and have the same semantics:

| Field | Used by | What to check |
|-------|---------|---------------|
| `BattleChara.CastInfo.ResponseGlobalSequence` | Action inference | Counter still increments on action use |
| `Character.EmoteController.EmoteId` | Emote inference | Still populated during emote animation |
| `Character.CharacterData.ClassJob` | Job change inference | Still returns correct ClassJob ID |

How to verify: enter Author mode, perform each action, check `[QF-DIAG] PreviewInference` log lines show non-zero signal values.

## 5. In-game smoke test

- [ ] Plugin loads without errors in `dalamud.log`
- [ ] `/qf inspect` — debug panels show live game state (position, zone, target)
- [ ] `/qf debug target` — returns correct NPC ID and position
- [ ] `/qf debug quest <id>` — returns sequence/flags for a known quest
- [ ] Settings window opens via gear icon in plugin list

## 6. Authoring smoke test

- [ ] `/qf author <questId>` — enters author mode
- [ ] Record a talk step — NPC ID and position detected correctly
- [ ] Record a travel step — position change detected
- [ ] `[QF-DIAG] PreviewInference` log shows correct signal values
- [ ] Export produces valid JSON

## 7. Engine run smoke test

- [ ] `/qf run <questId>` on a known-good quest file
- [ ] Navigation works (vnavmesh pathfinding)
- [ ] Teleport works (Lifestream)
- [ ] NPC interaction works (TextAdvance dialogue skipping)
- [ ] Quest completes successfully

## 8. Quest data check

- [ ] Existing quest files still reference valid IDs (quest IDs, NPC IDs, item IDs are generally stable across patches)
- [ ] `lastVerifiedPatch` field in quest files flagged as outdated (informational only — no runtime effect)
- [ ] Spot-check one quest: run `/qf debug quest <id>` and verify sequence values match what the quest file expects

---

## Risk assessment by patch type

| Patch type | Risk level | Typical impact |
|------------|-----------|----------------|
| Hotfix (7.x1) | Low | Server-side fixes, no client changes |
| Minor (7.5x) | Low-Medium | New quests/items, Excel sheet additions, rare struct changes |
| Major (7.x) | Medium-High | New systems, struct layout changes, possible IPC breaks |
| Expansion (8.0) | High | Everything changes — full revalidation needed |

## What breaks silently vs loudly

| Failure mode | Symptom | How to detect |
|-------------|---------|---------------|
| Dalamud not updated | Plugin won't load | Obvious — Dalamud says so |
| ClientStructs offset shift | Wrong data or access violation | `[QF-DIAG]` signals show 0 or garbage; crash in `dalamud.log` |
| Lumina sheet change | Missing rows or wrong data | `AetheryteZoneMap` logs warning; quest lookup returns null |
| Dependency plugin IPC change | Adapter calls fail | Engine logs error and emits `AwaitUser` |
| Quest ID renumbered | Quest file targets wrong quest | `/qf debug quest` shows unexpected state (extremely rare) |
