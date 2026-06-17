# Death Recovery Teleport

**Status:** RED -- spec complete, tests not yet written
**Input docs:** `resilient-kindling-cerf.md` (plan), `ARCHITECTURE.md`, `SCHEMA.md`
**Output:** After implementation, open-world death warps are followed by an automatic teleport back to the step's required zone. Fixture tests are unaffected (FakeGameStateProvider defaults `dead=false`).
**Phase dependencies:** None -- touches only existing `QuestEngine` fields and `AetheryteZoneMap`.

---

## 1. Scope

When a player dies in the open world, SelectYesnoResponder auto-answers "Yes" and warps the player to their home aetheryte. The engine then sees a zone mismatch but has no mechanism to teleport back. This feature adds dead-to-alive edge detection and a fallback teleport to the step's `RequiredZone` via `AetheryteZoneMap`.

Two new boolean fields. Roughly 20 lines of engine logic. Six tests.

---

## 2. Design Decisions

### DR1 -- Two boolean fields, not an enum

Add `bool _wasDeadLastTick` and `bool _deathRecoveryPending` alongside the existing state fields at ~line 78 of `QuestEngine.cs`.

**Rejected alternative:** A three-state enum (`Normal | Dead | RecoveryPending`). The two booleans are independent: `_wasDeadLastTick` tracks edge detection timing; `_deathRecoveryPending` tracks whether recovery is needed. Collapsing them into one state machine obscures the independence and adds complexity for no gain.

**Concrete surface:**
```csharp
private bool _wasDeadLastTick;
private bool _deathRecoveryPending;
```

**What breaks if violated:** If only one field is used, you cannot distinguish "player is currently dead" from "player just revived and needs recovery." The edge detector would fire every tick while alive.

**Testability:** Both fields are private. Tests observe behavior (Teleport emitted or not) rather than field state.

### DR2 -- Edge detection reads IsPlayerDead once per tick, after playerZone

Place the dead-to-alive edge detection after `playerZone` is read (~line 551) and before the quest variables read (~line 553). This ordering ensures `playerZone` is available for the later condition 4b check in the same tick.

**Concrete logic (inserted at ~line 552):**
```csharp
var deadResult = await _gameState.IsPlayerDead(ct);
var dead = deadResult is Result<bool>.Success { Value: true };
if (_wasDeadLastTick && !dead)
{
    var ikResult = await _gameState.GetCurrentInstanceKind(ct);
    var ik = ikResult is Result<InstanceKind>.Success { Value: var k } ? k : InstanceKind.None;
    if (ik == InstanceKind.None)
        _deathRecoveryPending = true;
}
_wasDeadLastTick = dead;
```

**What breaks if placed later:** If placed after the step loop, the first post-death tick would not have the flag set and the player would get a Navigate to the NPC in the wrong zone before the teleport fires on tick 2. Placing it before the step loop ensures same-tick recovery.

### DR3 -- Instance-kind gating: only InstanceKind.None sets the flag

Dungeon, trial, raid, SPD, and all other instance deaths are handled by delegated plugins (AutoDuty, BossMod). Setting the flag in instances would cause a spurious teleport after instance exit.

The `GetCurrentInstanceKind` call is only made on the dead-to-alive transition tick (not every tick), so the performance cost is negligible.

**What breaks if violated:** After a dungeon wipe + recovery, the engine would try to teleport to the step's RequiredZone aetheryte instead of letting the dungeon plugin continue. This would be a catastrophic regression for DungeonTrialStep.

### DR4 -- Clear both flags in BeginRun and on sequence change

Add `_wasDeadLastTick = false; _deathRecoveryPending = false;` to:
- `BeginRun` (~line 362-367) -- a new run starts with clean state
- Sequence change block (~line 566-577) -- a sequence advance means the quest progressed; stale death state is irrelevant

**What breaks if not cleared in BeginRun:** A stale `_deathRecoveryPending = true` from a previous run would cause a teleport on the first tick of a new run even though the player may have manually recovered.

**What breaks if not cleared on sequence change:** After the game advances the quest sequence (the player completed a step), a stale flag from an earlier death in the same run would fire a spurious teleport.

### DR5 -- Clear _deathRecoveryPending on step confirmation

When `_confirmedStepIds.Add(step.Id)` runs in the expect-evaluation block (~line 621), also set `_deathRecoveryPending = false`. A confirmed step means the quest is progressing normally -- the player successfully recovered (possibly by manual travel) and any stale death flag is no longer relevant.

**What breaks if omitted:** After dying and manually traveling back to the correct zone, the flag would persist. If the next step has a different RequiredZone, the stale flag would cause a teleport to THAT zone even though the player arrived there normally.

### DR6 -- Condition 4a/4b placement: after resume-point trigger (condition 4), before CombatStep arm (condition 5)

Insert two new conditions in the step loop between the existing condition 4 (resume-point trigger, ~line 650) and condition 5 (CombatStep arm, ~line 652):

**4a. Clear flag if zone is already satisfied:**
```csharp
if (_deathRecoveryPending && step.RequiredZone is not null
    && ZoneAlreadySatisfied(playerZone, step.RequiredZone))
    _deathRecoveryPending = false;
```

**4b. Death-recovery teleport:**
```csharp
if (_deathRecoveryPending
    && step.RequiredZone is { } deathRz
    && !ZoneAlreadySatisfied(playerZone, deathRz)
    && uint.TryParse(deathRz, out var deathZoneId)
    && AetheryteZoneMap.TryGetAetheryteByZone(deathZoneId, out var deathAeId))
{
    return (new EngineAction.Teleport(new AetheryteId(deathAeId), Origin: step),
            step.Id, playerPos);
}
```

**Why after condition 4 (resume-point):** If a step has an authored `ResumePointFragmentId`, the fragment handles zone recovery. Death-recovery teleport is a fallback for steps WITHOUT fragments. The resume-point condition returns early when it fires, so 4b is only reached for steps without active resume fragments.

**Why before condition 5 (CombatStep):** CombatStep dispatch should not fire if the player is in the wrong zone after a death warp. Teleporting first is correct.

**What breaks if placed before condition 4:** The death-recovery teleport would fire even for steps that have authored resume fragments, bypassing the more sophisticated recovery path.

### DR7 -- Steps without RequiredZone: no teleport, flag persists

If the current step has `RequiredZone == null`, neither 4a nor 4b fires. The flag remains set. This is correct: without a RequiredZone, the engine cannot determine where to teleport. The player continues with normal step dispatch until they reach a step that has a RequiredZone (which clears or uses the flag) or until the sequence advances (which clears it via DR4).

### DR8 -- Zone has no aetheryte in map: fall through to normal dispatch

If `AetheryteZoneMap.TryGetAetheryteByZone` returns false for the step's required zone, condition 4b does not fire and the step falls through to normal dispatch (Navigate). This handles zones that genuinely have no aetheryte (instanced content zones, some special areas). The flag remains set so a later step with a mapped zone can still benefit.

---

## 3. Test Scenarios

All tests use `EngineTestHarness` and live in `QuestForge.Engine.Tests/Engine/DeathRecoveryTests.cs`.

**Test zone/aetheryte mapping (from default `AetheryteZoneMap`):**
- Aetheryte 1000 maps to zone 130
- Aetheryte 8 maps to zone 129
- Zone 128 has NO aetheryte in the default test map

**Shared helper:**
```csharp
private static QuestDefinition BuildQuest(uint questId, params Step[] steps) =>
    new()
    {
        SchemaVersion = "1.0.0",
        Id = questId,
        Name = "Death Recovery Test Quest",
        Expansion = "arr",
        Category = "side",
        Enabled = true,
        SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
        LastVerifiedPatch = "7.4",
        Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
        AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
        Sequences =
        [
            new QuestSequence { Sequence = 0, Steps = steps }
        ]
    };

private static TalkStep TalkInZone(string id, string requiredZone) => new()
{
    Id = id,
    Target = new NpcLocation(12345u, (int)uint.Parse(requiredZone), new Position3(999f, 0f, 999f)),
    RequiredZone = requiredZone,
    Expect = new PredicateExpect { Predicate = "0 == 1" }  // never auto-confirms
};
```

### D1 -- Open-world death warp: engine teleports back to step's required zone

**Given:**
- A TalkStep with `RequiredZone = "130"` (zone 130 maps to aetheryte 1000 in the default test map)
- Player starts in zone 130, position (0,0,0)
- Quest sequence 0 active

**When:**
- Tick 1: normal dispatch (Navigate toward NPC at 999,0,999 -- player is in the correct zone)
- Set `GameState.SetDead(true)` -- player dies
- Tick 2: engine reads dead=true; `_wasDeadLastTick` becomes true; engine returns Wait (player is dead, engine should not dispatch actions while dead -- or Navigate; the exact action while dead is not the point of this test)
- Set `GameState.SetDead(false)` and `GameState.SetZone(new ZoneId(129))` -- player revives at home aetheryte in zone 129 (wrong zone)
- Tick 3: dead-to-alive transition fires; `InstanceKind.None` (default) -> `_deathRecoveryPending = true`; step has `RequiredZone = "130"`, player is in zone 129, zone 130 maps to aetheryte 1000 -> condition 4b fires

**Then:**
- Tick 3 returns `EngineAction.Teleport` with `Destination.Value == 1000` (aetheryte for zone 130)

### D2 -- Zone has no aetheryte: falls through to normal Navigate

**Given:**
- A TalkStep with `RequiredZone = "128"` (zone 128 has NO aetheryte in the default test map)
- Player starts in zone 128, position (0,0,0)
- Quest sequence 0 active

**When:**
- Tick 1: normal dispatch (Navigate toward NPC)
- Set dead=true
- Tick 2: engine reads dead=true
- Set dead=false, zone=129 (wrong zone, simulating death warp)
- Tick 3: dead-to-alive transition -> `_deathRecoveryPending = true`; step RequiredZone = "128" but `TryGetAetheryteByZone(128)` returns false

**Then:**
- Tick 3 returns `EngineAction.Navigate` (falls through to normal dispatch), NOT `EngineAction.Teleport`
- The flag remains set (not cleared by 4a since zone is wrong, not consumed by 4b since no aetheryte)

### D3 -- Never died: wrong zone does NOT trigger death teleport

**Given:**
- A TalkStep with `RequiredZone = "130"` (has aetheryte 1000)
- Player starts in zone 129 (wrong zone), never dies
- Quest sequence 0 active

**When:**
- Tick 1: player is in wrong zone but `_deathRecoveryPending` is false (no death occurred)

**Then:**
- Tick 1 returns `EngineAction.Navigate` (normal step dispatch toward the NPC at 999,0,999), NOT `EngineAction.Teleport`
- This confirms that zone mismatch alone is insufficient; the death flag must be set

### D4 -- Death in instance (Dungeon): flag NOT set, no teleport

**Given:**
- A TalkStep with `RequiredZone = "130"` (has aetheryte 1000)
- Player starts in zone 130
- `GameState.SetInstanceKind(InstanceKind.Dungeon)` -- player is in a dungeon
- Quest sequence 0 active

**When:**
- Tick 1: normal dispatch
- Set dead=true (player dies in dungeon)
- Tick 2: engine reads dead=true
- Set dead=false, zone=129 (simulating post-dungeon zone change)
- Set `GameState.SetInstanceKind(InstanceKind.None)` -- back in open world
- Tick 3: dead-to-alive transition fires, but `GetCurrentInstanceKind` was `InstanceKind.Dungeon` at the moment of the transition (the instance kind at the time of revival)

**Clarification on timing:** The instance kind check happens at the moment of the dead-to-alive transition. If the player revives inside the dungeon (InstanceKind.Dungeon), the flag is NOT set. The test must set `InstanceKind.Dungeon` BEFORE setting dead=false, then change to `InstanceKind.None` after.

**Revised When:**
- Set dead=true, instanceKind=Dungeon
- Tick 2: engine reads dead=true, _wasDeadLastTick=true
- Set dead=false (player revives inside dungeon -- instanceKind still Dungeon)
- Tick 3: dead-to-alive transition; GetCurrentInstanceKind returns Dungeon -> flag NOT set
- Set zone=129, instanceKind=None (player exits dungeon to open world, wrong zone)
- Tick 4: no dead-to-alive transition (was alive last tick too) -> flag still not set

**Then:**
- Tick 4 returns `EngineAction.Navigate` (normal dispatch), NOT `EngineAction.Teleport`

### D5 -- Flag clears when zone becomes satisfied

**Given:**
- A TalkStep with `RequiredZone = "130"` (has aetheryte 1000)
- Player starts in zone 130
- Quest sequence 0 active

**When:**
- Tick 1: normal dispatch
- Set dead=true
- Tick 2: engine reads dead=true
- Set dead=false, zone=129 (death warp)
- Tick 3: transition fires -> `_deathRecoveryPending = true`; wrong zone -> Teleport returned
- Set zone=130 (player arrives via teleport)
- Tick 4: condition 4a fires (zone satisfied) -> `_deathRecoveryPending = false`

**Then:**
- Tick 4 returns `EngineAction.Navigate` (normal dispatch toward NPC), NOT `EngineAction.Teleport`
- Confirms the flag is cleared and does not cause repeated teleports

### D6 -- BeginRun clears death recovery state

**Given:**
- A TalkStep with `RequiredZone = "130"` (has aetheryte 1000)
- Player starts in zone 130
- Quest sequence 0 active

**When:**
- Tick 1: normal dispatch
- Set dead=true
- Tick 2: engine reads dead=true (sets `_wasDeadLastTick = true`)
- Call `BeginRun("run-2")` -- new run starts
- Set dead=false, zone=129 (death warp conditions, but flags were cleared by BeginRun)
- Tick 3 (new run): `_wasDeadLastTick` was cleared to false by BeginRun, so no dead-to-alive transition is detected even though the player is now alive in zone 129

**Then:**
- Tick 3 returns `EngineAction.Navigate` (normal dispatch), NOT `EngineAction.Teleport`
- Confirms BeginRun clears both `_wasDeadLastTick` and `_deathRecoveryPending`

---

## 4. Implementation Order

### Phase A -- Engine fields and edge detection (~15 min)
1. Add `_wasDeadLastTick` and `_deathRecoveryPending` fields
2. Clear both in `BeginRun` and sequence-change block
3. Add edge detection logic after playerZone read
4. Add `_deathRecoveryPending = false` in step confirmation block
5. Add conditions 4a and 4b in step loop

### Phase B -- Tests (~20 min)
Write `DeathRecoveryTests.cs` with D1-D6.

### Phase C -- Verify fixtures unaffected (~5 min)
Run full `dotnet test QuestForge.Engine.Tests` and confirm all existing tests still pass.

---

## 5. Done Criteria

1. `dotnet test QuestForge.Engine.Tests` passes with all existing tests plus 6 new tests in `DeathRecoveryTests.cs`
2. D1 confirms open-world death -> teleport back to step's required zone aetheryte
3. D2 confirms no teleport when zone has no aetheryte in the map
4. D3 confirms zone mismatch alone (no death) does NOT trigger teleport
5. D4 confirms instance death does NOT set the recovery flag
6. D5 confirms flag clears when zone becomes satisfied (no repeated teleports)
7. D6 confirms BeginRun clears stale death recovery state
8. All existing fixture tests pass unchanged (FakeGameStateProvider defaults `dead=false` so the flag is never set)

---

## 6. Exclusions

- **Death while dead:** The engine's behavior while `IsPlayerDead` returns true is not specified here. The engine may return Wait, Navigate, or any other action -- the death recovery logic only cares about the dead-to-alive transition.
- **Multiple deaths in one sequence:** The flag is set on every dead-to-alive transition in the open world. If the player dies, recovers via teleport, then dies again, the cycle repeats naturally. No special handling needed.
- **SPD (SinglePlayerDuty) death:** SPD deaths are gated by `InstanceKind.SinglePlayerDuty != InstanceKind.None`, so the flag is not set. SPD death recovery is handled by the SPD runner.
- **ReplayGameStateProvider starvation safety:** The plan mentions adding try/catch to `IsPlayerDead` in `ReplayGameStateProvider`. This is tracked separately -- the replay system is not under test here.
- **Resume-point interaction:** Steps with `ResumePointFragmentId` set will have their recovery handled by the resume fragment (condition 4 fires first). Death-recovery teleport is only a fallback for steps without resume fragments. No special interaction logic is needed.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in D1-D6.
- Happy paths: 2 scenarios (D1, D5)
- Edge cases: 2 scenarios (D2, D3)
- Error cases: 2 scenarios (D4, D6)
- Expected total: ~6 tests in QuestForge.Engine.Tests
