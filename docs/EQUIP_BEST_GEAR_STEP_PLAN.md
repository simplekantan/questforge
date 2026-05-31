# EquipBestGearStep Implementation Plan (Issue #118)

**Status:** ready for test creation

**Slice:** Combined 2-3 (schema already done in #122; interface+fake+stub already done in #121; this plan covers engine dispatch, Dalamud impl, EngineHost wiring, and tooling catch-up). No authoring inference (per user decision -- no polling signal for "recommended gear was applied").

**Input docs:**
- `docs/GEAR_RESEARCH.md` -- Section 2.2 (native `RecommendEquipModule`), Section 1 (Stylist IPC)
- `docs/EQUIP_GEAR_FOR_QUEST_STEP_PLAN.md` -- closest analog (gear dispatch pattern)
- `QuestForge.Schema/Step.cs:193` -- `EquipBestGearStep` (empty sealed class, no properties)
- `QuestForge.Adapters/Gear/IBestGearEquipper.cs` -- 2-method interface (`EquipBestGear`, `IsStylistAvailable`)
- `QuestForge.Adapters.Fakes/Gear/FakeBestGearEquipper.cs` -- fake with recording + scripting
- `QuestForge.Adapters.Dalamud/Gear/DalamudBestGearEquipper.cs` -- stub returning `Failed`
- `QuestForge.Engine/QuestEngine.cs:45,93` -- `_bestGearEquipper` field + optional ctor param (already wired)
- `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs:47,138` -- `BestGearEquipper` property + ctor passthrough (already wired)
- `QuestForge.Plugin/EngineHost.cs:56,126,238` -- `_bestGearEquipper` field + construction + BeginRun passthrough (already wired)

**Output (CI behavior):** Adding `{ "type": "equip-best-gear", "id": "equip-best" }` to a quest dispatches `EngineAction.EquipBestGear` from `QuestEngine`. The adapter fires the equip (Stylist or vanilla per user config), returns `Equipped`. Since there is no implicit postcondition, the step relies entirely on authored `Expect` for advancement. W1 fires if no Expect is authored (by design -- authors should write one). Engine unit tests in `QuestForge.Engine.Tests/Engine/EquipBestGearStepTests.cs` cover all dispatch arms.

---

## Dependency graph

```
QuestForge.Engine
  ├── EngineAction.EquipBestGear (NEW record)
  ├── QuestEngine: ResolveEquipBestGear async pre-arm + dispatch arm
  └── (no implicit postcondition — relies on authored Expect)
       ↓
QuestForge.Engine.Tests
  ├── Engine/EquipBestGearStepTests.cs (EB1-EB11)
  └── Helpers/EngineTestHarness.cs (RunToCompletion arm for EquipBestGear)
       ↓
QuestForge.Adapters.Dalamud
  └── Gear/DalamudBestGearEquipper.cs (fleshed out from stub)
       ↓
QuestForge.Plugin
  ├── EngineHost.cs (DispatchAction arm for EquipBestGear)
  └── PluginConfig.cs (PreferStylist config property)
       ↓
questforge-tools (paired PR)
  ├── TraceConstants: ActionEquipBestGear
  ├── CapabilityInferrer: already has entry (no change)
  ├── FilenameLookup: new entry
  └── DistinguishingCapPriority: new entry
```

**Build order:**
1. `EngineAction.EquipBestGear` record in `EngineAction.cs`.
2. `QuestEngine`: `ResolveEquipBestGear` pre-arm + dispatch arm.
3. `EngineTestHarness`: `RunToCompletion` arm for `EquipBestGear`.
4. Engine tests EB1-EB11.
5. `PluginConfig.PreferStylist` property.
6. `DalamudBestGearEquipper`: flesh out from stub.
7. `EngineHost`: dispatch arm.
8. Tooling catch-up (paired PR).

---

## Architectural decisions

### EB-1: EngineAction shape -- no payload

**Decision:** `EngineAction.EquipBestGear` carries only `Step? Origin`. The step has no properties (no item IDs, no job ID, no constraints). The action is a simple command: "equip the best gear you have."

```csharp
// QuestForge.Engine/EngineAction.cs (append after EquipGear)
public sealed record EquipBestGear(Step? Origin = null) : EngineAction;
```

**Rationale:** The schema type `EquipBestGearStep` is an empty sealed class. There is nothing to parameterize the action with. The adapter internally decides Stylist vs vanilla based on user config -- that is not an engine concern.

**What breaks if violated:** If the action carries a `bool PreferStylist` flag, the engine becomes aware of adapter strategy -- violating the testability boundary. The engine must not know or care how the adapter implements "equip best gear."

**Testability:** Tests call `harness.BestGearEquipper.ScriptNextResult(EquipOutcome.Equipped)` to control outcomes. No payload to assert beyond the action type itself.

### EB-2: No implicit postcondition -- Expect-only advancement

**Decision:** Unlike `EquipGearForQuestStep` (which has an implicit postcondition via `IsItemEquipped` checks), `EquipBestGearStep` has NO implicit postcondition. The engine cannot verify "best gear is equipped" because there is no expected item list to check against. The step relies entirely on authored `Expect` for advancement.

Consequence: if the step has no `Expect`, the engine will re-fire `EquipBestGear` every tick (fire-and-forget, harmless -- equipping already-equipped gear is a no-op) and increment the step-failure counter until `MaxConsecutiveStepFailures` triggers `AwaitUser`. This is intentional: W1 warns the author.

**Rejected alternative:** Self-confirm after first successful `EquipBestGear` dispatch (treat the adapter returning `Equipped` as postcondition). Rejected because `Equipped` means "the command was issued," not "the gear is now optimal." The adapter's fire-and-forget design (Decision 2 in the issue) means the result does not prove the game state changed.

**What breaks if violated:** If the step self-confirms on `Equipped`, the engine moves past the step before the game has processed the gear change. A subsequent step that depends on the new gear (e.g., a duty with an ilvl gate) may fail.

### EB-3: Pre-flight guards in `ResolveEquipBestGear`

**Decision:** Three guards in priority order, identical to `ResolveEquipGear`:

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. No adapter wired | `_bestGearEquipper is null` | `AwaitUser("EquipBestGearStep dispatched but no IBestGearEquipper wired")` |
| 2. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `Wait("player casting; deferring equip-best-gear")` |
| 3. Player in combat | `IsPlayerInCombat` | `Wait("player in combat; deferring equip-best-gear")` |

After guards pass, emit `EngineAction.EquipBestGear(Origin: step)`.

```csharp
private async Task<EngineAction> ResolveEquipBestGear(EquipBestGearStep step, CancellationToken ct)
{
    if (_bestGearEquipper is null)
        return new EngineAction.AwaitUser(
            "EquipBestGearStep dispatched but no IBestGearEquipper wired — host must supply one");

    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring equip-best-gear", Origin: step);

    var inCombatResult = await _gameState.IsPlayerInCombat(ct);
    if (inCombatResult is Result<bool>.Success { Value: true })
        return new EngineAction.Wait("player in combat; deferring equip-best-gear", Origin: step);

    return new EngineAction.EquipBestGear(Origin: step);
}
```

**Key difference from `ResolveEquipGear`:** The return type is `Task<EngineAction>` (non-nullable), not `Task<EngineAction?>`. There is no implicit postcondition to check, so the method always returns an action. No `null` return, no self-confirm in the dispatch arm.

**Why InCombat guard:** Same reasoning as EG-3. GEAR_RESEARCH.md Section 1.4 confirms `RaptureGearsetModule.EquipGearset()` fails in combat. Both the Stylist path (which calls `EquipGearset` internally) and the vanilla path (which calls `EquipRecommendedGear`) fail during combat.

### EB-4: `_lastResolvedStep` is NOT set in `ResolveEquipBestGear`

Mirrors EG-4 / UA13 / UE12 / UI14. `EquipBestGearStep` has no properties and no `DialogueChoices`. The `Origin: step` field on `EngineAction.EquipBestGear` carries context for trace consumers.

### EB-5: Dismount exemption -- EquipBestGear IS exempt

**Decision:** `EquipBestGear` IS in the lazy-dismount exemption list. Same reasoning as EG-5: the game allows equipment changes while mounted. Forcing a dismount before equipping best gear would be unnecessary.

**Code change required:** extend the exemption check from `is not EngineAction.Navigate and not EngineAction.Teleport and not EngineAction.EquipGear` to include `and not EngineAction.EquipBestGear`.

Pinned by tests EB6 (mounted + prior Navigate: dismount does NOT fire before EquipBestGear) and EB7 (standalone + mounted: dismount does NOT fire).

### EB-6: Dispatch arm -- standard, no self-confirm

**Decision:** The dispatch arm in the step-dispatch switch is simpler than `EquipGearForQuestStep` because there is no implicit postcondition.

```csharp
// In the step-dispatch switch, after EquipGearForQuestStep arm:
if (step is EquipBestGearStep bestGearStep)
{
    var bestGearAction = await ResolveEquipBestGear(bestGearStep, ct);
    return (bestGearAction, step.Id);
}
```

No `null` check, no self-confirm. The action is always returned to the caller. Advancement happens when the authored `Expect` evaluates true on a subsequent tick.

### EB-7: W1 is NOT suppressed for EquipBestGearStep

**Decision:** W1 DOES fire for `EquipBestGearStep` with no `Expect`. This is by design (per user decision 4): authors should always write an Expect for this step type because there is no implicit postcondition.

**No code change:** The existing W1 guard already fires for step types not in the suppression list. `EquipBestGearStep` is not in the list, so W1 fires automatically.

**No step-specific W11+ warning needed.** W1's generic message ("Consider adding one for reliability") is sufficient. Unlike `UseActionStep`/`UseEmoteStep` which have W7/W8 with "spin-loop" messaging, `EquipBestGearStep`'s spin-loop is harmless (fire-and-forget equip of already-equipped gear is a no-op). The step-failure counter will eventually trigger `AwaitUser` if Expect is never met.

### EB-8: DalamudBestGearEquipper implementation design

**Decision:** The adapter takes a `Func<bool>` for the Stylist preference, injected from the Plugin layer. This avoids `QuestForge.Adapters.Dalamud` depending on `PluginConfig` (which is in `QuestForge.Plugin`).

```csharp
public sealed class DalamudBestGearEquipper : IBestGearEquipper
{
    private readonly PluginServices _svc;
    private readonly Func<bool> _preferStylist;

    public DalamudBestGearEquipper(PluginServices svc, Func<bool> preferStylist)
    {
        _svc = svc;
        _preferStylist = preferStylist;
    }
}
```

**`EquipBestGear(ct)` implementation:**

1. Check `_preferStylist()`.
2. **Stylist path** (when preferred and available):
   - Call `IsStylistAvailable(ct)`. If not available, fall back to vanilla path.
   - Call Stylist IPC: `Stylist.UpdateCurrentGearsetEx(true, true)`.
     - First `true`: `moveItemsFromInventory` -- pull best items from player inventory.
     - Second `true`: `shouldEquip` -- always equip the updated gearset.
   - Return `Result.Ok(EquipOutcome.Equipped)`.
3. **Vanilla path** (fallback or when Stylist not preferred):
   - Read `PlayerState.Instance()->CurrentClassJobId`.
   - Call `RecommendEquipModule.Instance()->SetupForClassJob(currentClassJobId)`.
   - Call `RecommendEquipModule.Instance()->EquipRecommendedGear()`.
   - Return `Result.Ok(EquipOutcome.Equipped)`.
4. **Fire-and-forget:** No polling of `IsBusy` (Stylist) or `IsUpdating` (vanilla). If the gear change is not yet reflected when the engine ticks, it will re-fire (harmless no-op).

**`IsStylistAvailable(ct)` implementation:**
- Try calling `Stylist.IsBusy` IPC gate.
- If it succeeds (returns `true` or `false`): Stylist is installed. Return `Result.Ok(true)`.
- If it throws `IpcError`/`IpcNotReadyError`: Stylist is not installed. Return `Result.Ok(false)`.

**Rejected alternative:** Pass `PluginConfig` directly to the adapter. Rejected because:
1. `QuestForge.Adapters.Dalamud` does not reference `QuestForge.Plugin` (and must not -- it's a lower-layer assembly).
2. `Func<bool>` is the minimal contract: the adapter only needs one bit of information.

**Rejected alternative:** Add `PreferStylist` to `PluginServices`. Rejected because `PluginServices` is a Dalamud-services bag, not a config container. Mixing config into it would blur the boundary.

**What breaks if violated:** If the adapter imports `PluginConfig`, the assembly dependency graph has a cycle (`Plugin` -> `Adapters.Dalamud` -> `Plugin`). The build fails.

### EB-9: PluginConfig.PreferStylist property

**Decision:** Add a `bool PreferStylist` property to `PluginConfig` with a default of `true`. Stylist produces better results than the vanilla `RecommendEquipModule` (stat weighting, inventory pulling). Users who do not have Stylist installed are unaffected: the adapter falls back to vanilla when `IsStylistAvailable` returns false.

```csharp
// QuestForge.Plugin/PluginConfig.cs
/// <summary>
/// When true, EquipBestGearStep prefers Stylist over the native Recommended Gear module.
/// Falls back to native if Stylist is not installed. Default: true.
/// </summary>
public bool PreferStylist { get; set; } = true;
```

### EB-10: EngineHost dispatch arm for EquipBestGear

```csharp
case EngineAction.EquipBestGear ebg:
    DebounceLog(
        "equipbestgear",
        "[EquipBestGear] firing");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _bestGearEquipper.EquipBestGear(ct);
    break;
```

Placement: after the `EquipGear` arm, before the `Wait` arm.

The debounce key is a fixed string `"equipbestgear"` (no payload to differentiate). Consecutive ticks that re-fire the same step will be deduplicated in the log.

### EB-11: EngineHost construction -- inject config lambda

**Decision:** Update `DalamudBestGearEquipper` construction in `EngineHost` to pass the config lambda. This requires `EngineHost` to receive the `PluginConfig` (or just the relevant `Func<bool>`).

Currently `EngineHost` receives only `PluginServices` and `TraceSession`. The simplest change: accept `PluginConfig` as a third constructor parameter.

```csharp
// EngineHost constructor signature change:
public EngineHost(PluginServices services, TraceSession traceSession, PluginConfig config)

// Construction:
_bestGearEquipper = new DalamudBestGearEquipper(services, () => config.PreferStylist);
```

**Caller update:** `Plugin.cs` already loads `PluginConfig` and passes it to `AuthoringHost`. Add the same `config` argument to the `EngineHost` constructor call.

### EB-12: Recording-proxy decision -- no `RecordingBestGearEquipper` needed

Per CLAUDE.md Slice 3 contract: "Write-only adapters don't need a `RecordingXxxExecutor` wrapper." `IBestGearEquipper.EquipBestGear` is write-only. `IsStylistAvailable` is a capability probe, not an observation worth recording. `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` capture the write.

### EB-13: Debug accessor

Add `IBestGearEquipper DebugBestGearEquipper => _bestGearEquipper;` to EngineHost alongside the existing debug accessors.

### EB-14: Tooling catch-up scope

**CapabilityInferrer:** Already has `[typeof(EquipBestGearStep)] = "step:equip-best-gear"` at line 28. No change.

**TraceConstants:** Add `ActionEquipBestGear = "equipbestgear"` (from `EngineAction.EquipBestGear.GetType().Name.ToLowerInvariant()`). No behavior change (`IsTerminalAction` only uses `done`/`awaituser`).

**FilenameLookup:** Add exact-shape entry:
```csharp
(["step:equip-best-gear", "step:talk", "step:travel"], "with-equip-best-gear.json"),
```

**DistinguishingCapPriority:** Add entry after `step:equip-gear-for-quest` (if present) or after `step:use-item`, before `step:say-chat-message`:
```csharp
("step:equip-best-gear", "with-equip-best-gear.json"),
```

Priority rationale: Gear equip steps are more distinguishing than chat messages and emotes. `equip-best-gear` ranks equal to or just below `equip-gear-for-quest` (both are gear operations).

---

## Validation rule table

No new validation rules. `EquipBestGearStep` has no properties to validate (no `ItemIds`, no `JobId`, no constraints).

| Rule | Code | Severity | Check | Applies to EquipBestGearStep? |
|---|---|---|---|---|
| W1 (missing Expect) | W1 | Warning | `step.Expect is null` | **YES** -- NOT suppressed (per EB-7) |

---

## Given-When-Then test scenarios

### Engine tests (`QuestForge.Engine.Tests/Engine/EquipBestGearStepTests.cs`)

All tests follow the established pattern. Quest with one `EquipBestGearStep` in sequence 0. AcceptStep present to satisfy E4.

#### EB1 -- Happy path -- emits EquipBestGear

**Given:**
- Player not casting, not in combat.
- `EquipBestGearStep { }` with `Expect = PredicateExpect("questSequence(65575) == 1")`.
- `harness.BestGearEquipper.ScriptNextResult(EquipOutcome.Equipped)`.
- Quest sequence 65575 currently at 0 (Expect is false).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.EquipBestGear` with `Origin != null`.

#### EB2 -- Expect already satisfied -- step skipped (cursor walk short-circuits)

**Given:**
- Player not casting, not in combat.
- `EquipBestGearStep { Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(new AetheryteId(8), true)` (predicate true before step runs).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits; step confirmed; no more steps in sequence; waiting for sequence advance).
- The step is confirmed (confirmed set contains the step ID).
- `harness.BestGearEquipper.RecordedCalls.Count == 0` (adapter never called).

#### EB3 -- Player casting -- Wait, no EquipBestGear emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- `EquipBestGearStep { Expect = PredicateExpect("questSequence(65575) == 1") }`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"player casting"`.

#### EB4 -- Player in combat -- Wait, no EquipBestGear emitted

**Given:**
- `harness.GameState.SetInCombat(true)`.
- `EquipBestGearStep { Expect = PredicateExpect("questSequence(65575) == 1") }`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"in combat"`.

#### EB5 -- No adapter wired -- AwaitUser

**Given:**
- A `QuestEngine` constructed WITHOUT `bestGearEquipper` (null default).
- `EquipBestGearStep { Expect = PredicateExpect("questSequence(65575) == 1") }`.

**When:** `engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.AwaitUser` whose `Reason` contains `"no IBestGearEquipper wired"`.

#### EB6 -- Mounted + prior Navigate: lazy-dismount does NOT fire before EquipBestGear (exempt)

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep to `(200, 0, 0)` with `Expect = "playerZone() == 130"`.
  2. EquipBestGearStep with `Expect = "questSequence(65575) == 1"`.
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).

**When:**
1. Tick 1 --> `EngineAction.Navigate`. `_lastDispatchedWasNavigate = true` (set in `EngineHost` or test-equivalent).
2. `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect satisfied).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.EquipBestGear`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount did NOT fire -- EquipBestGear is exempt).

Pins Decision EB-5. **Note:** This test exercises the EngineHost lazy-dismount exemption. If the test harness does not model the EngineHost lazy-dismount logic, this test should be implemented as an EngineHost-level integration test or as a unit test that directly asserts the exemption condition. Alternatively, the Tester may verify via the existing `RunToCompletion` helper if it models the dismount flag.

#### EB7 -- Standalone EquipBestGear + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: `EquipBestGearStep` with `Expect = "questSequence(65575) == 1"`.
- Player mounted (`SetMountState(MountState.Mounted)`).

**When:** Tick once.

**Then:**
- Returns `EngineAction.EquipBestGear`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount only triggers after Navigate).

#### EB8 -- No Expect -- engine re-fires every tick (stateless retry)

**Given:**
- `EquipBestGearStep { Expect = null }` (no authored Expect).
- Player not casting, not in combat.
- `harness.BestGearEquipper.ScriptNextResult(EquipOutcome.Equipped)` (always succeeds).

**When:** Tick twice.

**Then:**
- Tick 1: returns `EngineAction.EquipBestGear`.
- Tick 2: returns `EngineAction.EquipBestGear` again (same step, no Expect to advance past it).
- `harness.BestGearEquipper.RecordedCalls.Count` is 0 at the engine level (engine emits the action; the RunToCompletion harness calls the adapter). The Tester verifies both ticks emit the same action type.

This test documents the spin-loop behavior when Expect is missing. The step-failure counter will eventually trigger AwaitUser (not tested here -- that is a general engine behavior).

#### EB9 -- Adapter returns Failed -- engine re-fires (stateless retry)

**Given:**
- `EquipBestGearStep { Expect = PredicateExpect("questSequence(65575) == 1") }`.
- `harness.BestGearEquipper.ScriptNextFailure("adapter error")`.

**When:** Tick once. (RunToCompletion dispatches the action, adapter returns failure.)

**Then:**
- Returns `EngineAction.EquipBestGear` (the engine does not inspect the adapter result -- fire-and-forget).
- The engine will re-fire on the next tick because Expect is still false.

This test verifies that adapter failure does not crash the engine or change dispatch behavior.

#### EB10 -- Cancellation propagates

**Given:**
- EquipBestGearStep as EB1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

#### EB11 -- RunToCompletion integration: equip best gear then advance

**Given:**
- Quest: AcceptStep (auto-satisfies) + `EquipBestGearStep { Expect = "questSequence(65575) == 1" }`.
- `harness.BestGearEquipper.ScriptNextResult(EquipOutcome.Equipped)`.
- In the RunToCompletion loop, after `EquipBestGear` is dispatched, script `harness.QuestState.SetSequence(65575, 1)` to satisfy the Expect.

**When:** `harness.RunToCompletion(maxTicks: 10)`.

**Then:**
- `actions` contains at least one `EquipBestGear` entry.
- RunToCompletion succeeds (Done returned).

### DraftValidator W1 test

#### EB12 -- W1 fires for EquipBestGearStep without Expect

**Given:**
- `QuestDraft` with `EquipBestGearStep { Expect = null }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `warnings` contains an entry with `Code == "W1"` for the equip-best-gear step.

#### EB13 -- W1 does NOT fire for EquipBestGearStep with Expect

**Given:**
- `QuestDraft` with `EquipBestGearStep { Expect = PredicateExpect("questSequence(65575) == 1") }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `warnings` does NOT contain any entry with `Code == "W1"` for the equip-best-gear step.

---

## Implementation order

### Phase A -- EngineAction + Engine dispatch (45 min)

1. Append `EngineAction.EquipBestGear(Step? Origin = null)` to `EngineAction.cs` (after `EquipGear`).
2. Add `ResolveEquipBestGear` method to `QuestEngine.cs` per Decision EB-3.
3. Add dispatch arm to the step-dispatch switch in `ResolveAction`, after the `EquipGearForQuestStep` arm. No self-confirm (always returns the action).
4. **Tester writes EB1-EB5, EB8, EB10** (single-tick dispatch tests). Red until builder implements steps 1-3.

**Done before B:** Engine tests green. `dotnet test QuestForge.Engine.Tests` passes.

### Phase B -- EngineTestHarness wiring + mount/integration tests (30 min)

1. Add `case EngineAction.EquipBestGear ebg:` arm to `RunToCompletion` in `EngineTestHarness.cs`:
   ```csharp
   case EngineAction.EquipBestGear ebg:
       actions.Add(action);
       EmitActionSubmitted("EquipBestGear", default);
       var ebgResult = await BestGearEquipper.EquipBestGear(ct);
       EmitActionCompleted("EquipBestGear",
           ebgResult.IsSuccess ? ebgResult.ValueOrThrow.ToString() : "Failed");
       break;
   ```
2. Extend lazy-dismount exemption list in `EngineHost.cs` to include `and not EngineAction.EquipBestGear` per Decision EB-5.
3. **Tester writes EB6, EB7, EB9, EB11** (mount exemption, adapter failure, RunToCompletion integration).
4. **Tester writes EB12, EB13** (W1 validator tests). Green immediately (no code change needed -- W1 already fires for unlisted step types).

**Done before C:** All engine + validator tests green.

### Phase C -- DalamudBestGearEquipper + PluginConfig (45 min)

1. Add `bool PreferStylist { get; set; } = true` to `PluginConfig.cs` per Decision EB-9.
2. Update `DalamudBestGearEquipper` constructor to accept `Func<bool> preferStylist` per Decision EB-8.
3. Flesh out `EquipBestGear(ct)` per Decision EB-8 (Stylist path + vanilla fallback).
4. Flesh out `IsStylistAvailable(ct)` per Decision EB-8.
5. Update `EngineHost` constructor to accept `PluginConfig` and pass `() => config.PreferStylist` to `DalamudBestGearEquipper` per Decision EB-11.
6. Update `Plugin.cs` to pass `config` to `EngineHost`.

**Done before D:** `dotnet build QuestForge.Plugin` succeeds.

### Phase D -- EngineHost dispatch arm (15 min)

1. Add `case EngineAction.EquipBestGear ebg:` arm to `EngineHost.DispatchAction` per Decision EB-10.
2. Add `IBestGearEquipper DebugBestGearEquipper => _bestGearEquipper;` per Decision EB-13.

**Done before E:** Plugin compiles. `dotnet build` green.

### Phase E -- Tooling catch-up (paired PR, 20 min)

1. Add `ActionEquipBestGear = "equipbestgear"` to `TraceConstants.cs`.
2. Add FilenameLookup entry per Decision EB-14.
3. Add DistinguishingCapPriority entry per Decision EB-14.
4. Write tests for the new entries in `QuestForge.Tools.Trace.Tests/`.
5. `dotnet test` in tools repo green.

**Total estimated time: ~2.5 hours across engine, Dalamud, and tooling.**

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~EquipBestGearStepTests` reports all 11 engine tests green (EB1-EB11).
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~DraftValidator` reports EB12 and EB13 green (W1 behavior).
3. A quest with `{ "type": "equip-best-gear", "id": "equip-best", "expect": "questSequence(65575) == 1" }` dispatches `EngineAction.EquipBestGear` from `QuestEngine`.
4. The step advances only when the authored `Expect` evaluates true. Without `Expect`, the engine re-fires every tick (W1 warns the author).
5. `DalamudBestGearEquipper.EquipBestGear` checks `PreferStylist` config, tries Stylist IPC when preferred and available, falls back to native `RecommendEquipModule`.
6. `EngineHost.DispatchAction` has a `case EngineAction.EquipBestGear` arm that calls `_bestGearEquipper.EquipBestGear`.
7. Lazy-dismount exemption includes `EquipBestGear` (gear changes work while mounted).
8. `dotnet build` succeeds in both `questforge` and `questforge-tools` repos with no `TreatWarningsAsErrors` regressions.
9. `questforge-tools` TraceConstants has `ActionEquipBestGear = "equipbestgear"`.
10. `PluginConfig.PreferStylist` defaults to `true` and is persisted to `config.json`.
11. No regression in existing tests (EquipGearForQuestStepTests, UseItemStepTests, UseEmoteStepTests, UseActionStepTests, etc.).

---

## Exclusions

- **Authoring inference.** Per user decision: no easy polling signal for "recommended gear was applied." This step is always explicitly authored.
- **Implicit postcondition.** No expected item list to verify. Relies on authored `Expect`.
- **`IsUpdating`/`IsBusy` polling.** Fire-and-forget by design. The engine re-fires if Expect is unmet.
- **Stylist version detection.** The adapter uses `UpdateCurrentGearsetEx` (the non-deprecated variant). If a very old Stylist version is installed that lacks the `Ex` suffix, the IPC call will fail and the adapter falls back to vanilla. No explicit version gate.
- **`EquipGearForQuestStep` engine dispatch.** Already implemented (separate PR, issue #117).
- **`ChangeJobStep` engine dispatch.** Tracked in issue #119.
- **Schema changes.** Already landed in #122. `EquipBestGearStep` is an empty sealed class, final.
- **Interface split.** Already landed in #121. `IBestGearEquipper` with 2 methods is final.
- **Fake (`FakeBestGearEquipper`) and Dalamud stub.** Already landed in #121.
- **In-game smoke test.** Manual Slice 4 verification (not part of this CI-focused plan).

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in the scenarios section.
- Happy paths: 3 scenarios (EB1, EB2, EB11)
- Edge cases: 4 scenarios (EB6, EB7, EB8, EB9)
- Error / wait cases: 3 scenarios (EB3, EB4, EB5)
- Cancellation: 1 scenario (EB10)
- Validator: 2 scenarios (EB12, EB13)
- Expected total: ~13 tests in `QuestForge.Engine.Tests`
