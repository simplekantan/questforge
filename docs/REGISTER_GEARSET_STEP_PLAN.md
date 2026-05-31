# RegisterGearsetStep Implementation Plan (Issue #130)

**Status:** ready for test creation

**Slice:** 2-5 combined (schema + interface + fake + engine + validator + Dalamud impl + EngineHost + predicate + tooling catch-up + authoring inference).

**Input docs:**
- `docs/GEAR_RESEARCH.md` -- Section 2.3 (RaptureGearsetModule: CreateGearset, UpdateGearset, gearset structure)
- `docs/CHANGE_JOB_STEP_PLAN.md` -- the consumer that needs gearsets; closest analog for engine dispatch
- `QuestForge.Adapters/Gear/IJobChanger.cs` -- existing `GearsetExistsForJob` method on `IJobChanger`
- `QuestForge.Adapters.Dalamud/Gear/DalamudJobChanger.cs` -- existing gearset scan logic (reuse reference)
- `QuestForge.Schema/Step.cs:195-202` -- `ChangeJobStep` (consumer of gearsets)
- `QuestForge.Engine/QuestEngine.cs:46,93` -- `_jobChanger` field + optional ctor param patterns
- `QuestForge.Engine/Predicates/PredicateEvaluator.cs:127-133` -- existing `isPlayerJob()`, `playerJobId()` patterns
- `QuestForge.Adapters/State/IGameStateProvider.cs` -- where `GearsetExistsForJob` will go
- FFXIVClientStructs: `RaptureGearsetModule.CreateGearset()` returns int (gearset ID or 255 = fail); `UpdateGearset(int id)` overwrites existing gearset with currently equipped gear; `NumGearsets` (byte at 0xB73D) increases on creation; `GearsetEntry.ClassJob` (byte at 0x31) carries the job ID; `IsValidGearset(int)` checks slot existence + player allowance

**Output (CI behavior):** Adding `{ "type": "register-gearset", "id": "register-gearset" }` to a quest dispatches `EngineAction.RegisterGearset` from `QuestEngine`. The step has no properties -- the game auto-names gearsets after the current job/class. Authors use `skipIf: "jobGearsetExists(N)"` to avoid re-registering. A new `jobGearsetExists(N)` predicate is available for `expect`/`skipIf` usage. Authoring inference detects gearset creation by polling `RaptureGearsetModule.NumGearsets`.

---

## Dependency graph

```
QuestForge.Schema
  +-- RegisterGearsetStep : Step { } (empty sealed class, discriminator "register-gearset")
  +-- [JsonDerivedType] on Step
  +-- [JsonSerializable] in QuestForgeJsonContext
       |
QuestForge.Adapters
  +-- Gear/IGearsetManager.cs (NEW interface: RegisterGearset → Result<RegisterOutcome>)
  +-- State/IGameStateProvider.cs: GearsetExistsForJob(uint jobId, CancellationToken)
       |
QuestForge.Adapters.Fakes
  +-- Gear/FakeGearsetManager.cs (NEW fake)
  +-- State/FakeGameStateProvider: SetGearsetExistsForJob(uint jobId, bool exists)
       |
QuestForge.Engine
  +-- EngineAction.RegisterGearset (NEW record)
  +-- QuestEngine: _gearsetManager field, optional ctor param, ResolveRegisterGearset pre-arm
  +-- PredicateEvaluator: jobGearsetExists(N) function
       |
QuestForge.Engine.Tests
  +-- Engine/RegisterGearsetStepTests.cs (RG1-RG11)
  +-- Predicates/JobGearsetExistsPredicateTests.cs (RGP1-RGP4)
  +-- Schema/RoundTripTests.cs (register-gearset round-trip)
  +-- Helpers/EngineTestHarness.cs (RunToCompletion arm for RegisterGearset)
       |
QuestForge.Adapters.Dalamud
  +-- Gear/DalamudGearsetManager.cs (NEW)
       |
QuestForge.Plugin
  +-- EngineHost.cs (field + ctor + BeginRun + DispatchAction arm)
       |
QuestForge.Engine.Authoring (inference)
  +-- GameStateSnapshot: GearsetRegisteredSignal? GearsetRegistered
  +-- SnapshotAggregator: OnGearsetRegistered / OnGearsetRegisteredConsumed
  +-- InferredFrom: GearsetRegistered enum value
  +-- StepInferenceEngine: Rule 3.5k -- GearsetRegistered
  +-- StepFactory: "register-gearset" arm
       |
QuestForge.Plugin.Tracing
  +-- IGameProbe: GetGearsetCount()
  +-- UIObserver: PollGearsetCount (monotonic counter)
       |
questforge-tools (paired PR)
  +-- QuestForge.Schema (synced -- new RegisterGearsetStep)
  +-- CapabilityInferrer: StepCapabilities entry
  +-- TraceConstants: ActionRegisterGearset
  +-- FilenameLookup: new entry
  +-- DistinguishingCapPriority: new entry
  +-- FunctionRegistry: jobGearsetExists entry
```

**Build order:**
1. `RegisterGearsetStep` in `Step.cs` + JSON registrations + round-trip test.
2. `IGearsetManager` interface + `RegisterOutcome` enum.
3. `FakeGearsetManager` fake.
4. `IGameStateProvider.GearsetExistsForJob` + fake + predicate.
5. `EngineAction.RegisterGearset` record.
6. `QuestEngine`: `_gearsetManager` field, ctor param, `ResolveRegisterGearset` pre-arm, dispatch arm.
7. `EngineTestHarness`: `RunToCompletion` arm.
8. Engine tests RG1-RG11, predicate tests RGP1-RGP4.
9. `DalamudGearsetManager` impl.
10. `EngineHost`: dispatch arm.
11. Inference: snapshot signal, aggregator, inference rule, step factory, UIObserver poller, IGameProbe extension.
12. Tooling catch-up (paired PR).

---

## Architectural decisions

### RG-1: Empty schema shape -- RegisterGearsetStep has no properties

**Decision:** `RegisterGearsetStep` is a sealed empty class inheriting from `Step`. No properties. The game auto-names gearsets from the player's current job/class. The step means "save the currently equipped gear as a new gearset."

```csharp
// QuestForge.Schema/Step.cs
public sealed class RegisterGearsetStep : Step { }
```

**Rationale:** The step fires `RaptureGearsetModule.CreateGearset()` which takes no parameters. The game derives the gearset name from `PlayerState.CurrentClassJobId` automatically. There is nothing for the author to configure.

**What breaks if violated:** Adding a `JobId` property would require the author to specify which job the gearset is for, but `CreateGearset()` always uses the current job. The property would be ignored or misleading.

**Testability:** Empty steps have the simplest round-trip test -- just `{ "type": "register-gearset", "id": "..." }`.

### RG-2: Skip via predicate, no implicit postcondition

**Decision:** `RegisterGearsetStep` has NO implicit postcondition in the engine. The step fires `RegisterGearset`, and if a gearset already exists the author's `skipIf: "jobGearsetExists(N)"` handles it. The engine does not self-confirm.

```csharp
// In ResolveRegisterGearset:
// NO implicit postcondition check. Just emit the action.
return new EngineAction.RegisterGearset(Origin: step);
```

**Rationale:**
- Unlike `ChangeJobStep` (which has a natural postcondition: "am I on job X?"), `RegisterGearsetStep` has no deterministic postcondition the engine can cheaply verify. `CreateGearset()` returns an ID, but the engine cannot check "does a gearset exist for the current job?" without a new `IGameStateProvider` read -- and that read is exactly what `jobGearsetExists(N)` provides in the predicate.
- The author writes `skipIf: "jobGearsetExists(32)"` to skip re-registration on re-runs. This is the standard pattern for idempotent steps.
- Without `skipIf`, the step fires on every tick. W1 fires (not suppressed, per RG-7) to warn the author. This is intentional -- the step is a one-shot action that SHOULD have a skip guard.

**Rejected alternative:** Add an implicit postcondition that reads `GearsetExistsForJob(currentJobId)` after firing. Rejected because:
1. `GearsetExistsForJob` would need to be on `IGameStateProvider` AND called inside the pre-arm. The pre-arm pattern (`ResolveXxx`) does not read adapter state after emitting the action -- it resolves preconditions before.
2. The step may legitimately fire even when a gearset exists (to update it). An implicit "gearset exists = done" would break the update case.
3. The skip-via-predicate pattern is already established and well-understood.

**What breaks if violated:** If an implicit postcondition were added, re-registering (updating) a gearset would self-confirm immediately because the gearset already exists, preventing `UpdateGearset` from firing.

### RG-3: IGearsetManager -- new focused interface

**Decision:** New interface `IGearsetManager` in `QuestForge.Adapters/Gear/IGearsetManager.cs`. Single method. Separate from `IJobChanger`.

```csharp
// QuestForge.Adapters/Gear/IGearsetManager.cs
namespace QuestForge.Adapters.Gear;

public interface IGearsetManager
{
    Task<Result<RegisterOutcome>> RegisterGearset(CancellationToken ct);
}

public enum RegisterOutcome
{
    Registered,
    Updated,
    MaxGearsetsReached,
    Failed
}
```

**Rationale:** Per the established pattern (`IItemUser`, `IActionExecutor`, `IEmoteExecutor`, `IChatSender`), each step type gets its own focused interface. `RegisterGearset` is logically distinct from `ChangeToJob` (which reads gearsets; this one writes them).

**What breaks if violated:** Merging into `IJobChanger` would mean every test that exercises `ChangeJobStep` must also mock `RegisterGearset`. The focused-interface pattern avoids this coupling.

**Testability:** `FakeGearsetManager` is trivial: `RecordedCalls`, `ScriptNextResult`, `Reset()`.

### RG-4: DalamudGearsetManager implementation design

**`RegisterGearset(ct)` implementation:**

1. Read `PlayerState.Instance()->CurrentClassJobId` to determine the current job.
2. Scan gearset slots 0..99 for a valid gearset with `ClassJob == currentJobId`:
   - If found: call `RaptureGearsetModule.Instance()->UpdateGearset(existingId)`. Return `RegisterOutcome.Updated`.
   - If not found: call `RaptureGearsetModule.Instance()->CreateGearset()`. Check return:
     - If return == 255: return `RegisterOutcome.MaxGearsetsReached`.
     - Otherwise: return `RegisterOutcome.Registered`.
3. If `RaptureGearsetModule.Instance()` is null: return `RegisterOutcome.Failed`.

```csharp
public sealed class DalamudGearsetManager : IGearsetManager
{
    public DalamudGearsetManager(PluginServices svc) { }

    public unsafe Task<Result<RegisterOutcome>> RegisterGearset(CancellationToken ct)
    {
        var gsm = RaptureGearsetModule.Instance();
        if (gsm == null)
            return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Failed));

        var ps = PlayerState.Instance();
        if (ps == null)
            return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Failed));

        var currentJobId = ps->CurrentClassJobId;

        // Check if a gearset already exists for this job
        for (var i = 0; i < 100; i++)
        {
            if (!gsm->IsValidGearset(i)) continue;
            var entry = gsm->GetGearset(i);
            if (entry == null) continue;
            if (entry->ClassJob == currentJobId)
            {
                gsm->UpdateGearset(i);
                return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Updated));
            }
        }

        // No existing gearset -- create a new one
        var newId = gsm->CreateGearset();
        if (newId == 255)
            return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.MaxGearsetsReached));

        return Task.FromResult<Result<RegisterOutcome>>(Result.Ok(RegisterOutcome.Registered));
    }
}
```

**Pure helper extraction:** The gearset-scan-for-job logic is trivial (loop + byte comparison, identical to `DalamudJobChanger`). No pure helper extraction needed.

### RG-5: GearsetExistsForJob on IGameStateProvider

**Decision:** Add `Task<Result<bool>> GearsetExistsForJob(uint jobId, CancellationToken ct)` to `IGameStateProvider`. This enables the `jobGearsetExists(N)` predicate for `skipIf`/`Expect` usage.

```csharp
// IGameStateProvider.cs -- append to interface
Task<Result<bool>> GearsetExistsForJob(uint jobId, CancellationToken ct);
```

**Why on IGameStateProvider (not kept only on IJobChanger):**
- Predicates evaluate against `IGameStateProvider` and `IQuestState`. They do not have access to focused adapters like `IJobChanger`.
- `GearsetExistsForJob` is a read (game state query), not a write (action). Reads belong on `IGameStateProvider`.
- `IJobChanger` already has this method. The `IGameStateProvider` version uses the same logic but is accessible to the predicate evaluator. The Dalamud implementation can share code or duplicate the trivial scan.

**Dalamud implementation:** Same gearset scan as `DalamudJobChanger.GearsetExistsForJob` -- iterate `IsValidGearset(i)` + `GetGearset(i)->ClassJob == jobId`.

**Fake implementation:** `FakeGameStateProvider.SetGearsetExistsForJob(uint jobId, bool exists)` -- a `Dictionary<uint, bool>` keyed by job ID.

**Recording/Replay:** Standard passthrough pattern. `RecordingGameStateProvider` wraps and dedup-records. `ReplayGameStateProvider` reads from the observation map.

**What breaks if violated:** If `GearsetExistsForJob` stayed only on `IJobChanger`, the predicate evaluator would need a reference to `IJobChanger`, breaking the established pattern where predicates only read from `IGameStateProvider` and `IQuestState`.

### RG-6: `jobGearsetExists(N)` predicate

**Decision:** Add a new predicate function `jobGearsetExists(N)` to both `PredicateEvaluator` and `FunctionRegistry`. Returns `true` when a gearset exists for the given ClassJob row ID.

**PredicateEvaluator addition:**
```csharp
// In the EvaluateFunction switch, after "isPlayerJob":
"jobGearsetExists" => (await _gameState.GearsetExistsForJob((uint)(long)args[0], ct)).ValueOrThrow,
```

**FunctionRegistry addition** (in `questforge-tools/QuestForge.Predicates/FunctionRegistry.cs`):
```csharp
new("jobGearsetExists", new Fixed(1), [Int], Bool),
```

**Why a new predicate (vs reusing `gearsetExists`):** The existing `gearsetExists` takes a string (gearset name) and returns bool. `jobGearsetExists` takes a numeric job ID and checks by ClassJob byte, not by name. Gearset names are locale-sensitive; job IDs are stable. Per `feedback_locale_stable_quest_identifiers.md`, prefer numeric IDs.

**Usage in quest JSON:**
```json
{
  "type": "register-gearset",
  "id": "register-gearset-drk",
  "skipIf": "jobGearsetExists(32)"
}
```

### RG-7: W1 NOT suppressed for RegisterGearsetStep

**Decision:** W1 fires for `RegisterGearsetStep` with no `Expect`. The W1 suppression guard is NOT extended to include `RegisterGearsetStep`.

**Rationale:** Unlike `ChangeJobStep` (which has an implicit postcondition and can self-confirm), `RegisterGearsetStep` has no implicit postcondition (per RG-2). Without `Expect` or `skipIf`, the engine will re-fire `RegisterGearset` every tick. W1 correctly warns the author about this spin-loop risk.

**The expected authoring pattern is:**
- `skipIf: "jobGearsetExists(32)"` -- skip if gearset already exists.
- OR `Expect: "jobGearsetExists(32)"` -- wait until gearset is confirmed.

Without either, the step is a spin-loop. W1 is the correct warning.

**What breaks if violated:** If W1 were suppressed, authors could accidentally write `RegisterGearsetStep` without `skipIf`/`Expect` and get a spin-loop with no warning.

### RG-8: EngineAction shape -- no payload

**Decision:** `EngineAction.RegisterGearset` carries no payload (the adapter reads the current job from `PlayerState`). Only `Step? Origin`.

```csharp
// QuestForge.Engine/EngineAction.cs (append after ChangeJob)
public sealed record RegisterGearset(Step? Origin = null) : EngineAction;
```

**Rationale:** `CreateGearset()` takes no parameters. The adapter reads the current job from `PlayerState.CurrentClassJobId` internally. The engine has no information to pass.

**What breaks if violated:** Adding a `JobId` payload would create a mismatch -- the adapter ignores it because `CreateGearset()` uses the current job. The payload would be misleading.

### RG-9: Pre-flight guards in `ResolveRegisterGearset`

**Decision:** Three guards in priority order:

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. No adapter wired | `_gearsetManager is null` | `AwaitUser("RegisterGearsetStep dispatched but no IGearsetManager wired")` |
| 2. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `Wait("player casting; deferring register-gearset")` |
| 3. Player in combat | `IsPlayerInCombat` | `Wait("player in combat; deferring register-gearset")` |

After guards pass, emit `RegisterGearset(Origin: step)`. No implicit postcondition check (per RG-2).

```csharp
private async Task<EngineAction> ResolveRegisterGearset(RegisterGearsetStep step, CancellationToken ct)
{
    if (_gearsetManager is null)
        return new EngineAction.AwaitUser(
            "RegisterGearsetStep dispatched but no IGearsetManager wired -- host must supply one");

    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring register-gearset", Origin: step);

    var inCombatResult = await _gameState.IsPlayerInCombat(ct);
    if (inCombatResult is Result<bool>.Success { Value: true })
        return new EngineAction.Wait("player in combat; deferring register-gearset", Origin: step);

    return new EngineAction.RegisterGearset(Origin: step);
}
```

**Why no gearset-exists pre-flight (unlike ChangeJobStep):** `ChangeJobStep` pre-flights `GearsetExistsForJob` because the operation is impossible without a gearset. `RegisterGearsetStep` can always succeed (it creates or updates). The only failure case is `MaxGearsetsReached` (100 gearsets), which is rare enough not to warrant a pre-flight.

### RG-10: `_lastResolvedStep` is NOT set in `ResolveRegisterGearset`

Mirrors established precedent: EG-4, EB-4, UA13, UE12, UI14, CJ-4. `RegisterGearsetStep` does not carry `DialogueChoices`, so `ExtractYesNo` returns null. The `Origin: step` field on `EngineAction.RegisterGearset` carries context for trace consumers.

### RG-11: Dismount exemption -- RegisterGearset IS exempt

**Decision:** `RegisterGearset` IS in the lazy-dismount exemption list. Gearset registration is a pure data operation (writing to `GearsetEntry` memory, no character animation or model change). The game allows it while mounted.

**Code change:** Extend the exemption check:
```csharp
// EngineHost.cs (in lazy-dismount logic):
// OLD: is not EngineAction.Navigate and not EngineAction.Teleport
// NEW: is not EngineAction.Navigate and not EngineAction.Teleport and not EngineAction.RegisterGearset
```

**Rationale:** `CreateGearset()` and `UpdateGearset()` are pure memory writes to the `GearsetEntry` array. They do not trigger character animations, weapon model changes, or equipment swaps. The player can create a gearset while mounted/flying without issue. This is different from `ChangeJob` (which calls `EquipGearset` and changes the weapon model) and `EquipGear` (which swaps equipment).

**Pinned by tests:** RG8 (mounted + prior Navigate: dismount does NOT fire before RegisterGearset -- exempt) and RG9 (standalone + mounted: dismount does NOT fire).

### RG-12: Recording-proxy decision -- no `RecordingGearsetManager` needed

Per CLAUDE.md Slice 3 contract: "Write-only adapters don't need a `RecordingXxxExecutor` wrapper." `IGearsetManager.RegisterGearset` is write-only with no interesting reads. `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` capture the write.

### RG-13: EngineHost dispatch arm for RegisterGearset

```csharp
case EngineAction.RegisterGearset rg:
    DebounceLog(
        "registergearset",
        "[RegisterGearset] firing");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _gearsetManager.RegisterGearset(ct);
    break;
```

Placement: after the `ChangeJob` arm, before the `Wait` arm.

### RG-14: Authoring inference -- gearset registration detection

**Detection signal:** Poll `RaptureGearsetModule.NumGearsets` (byte at 0xB73D) via `IGameProbe.GetGearsetCount()`. On increase, emit `GearsetRegisteredSignal`. On no change (update case), the signal does not fire -- this is acceptable because the update case is indistinguishable from "author changed gear within the same gearset" and should be explicitly authored.

**Signal record:**
```csharp
// GameStateSnapshot.cs
public sealed record GearsetRegisteredSignal(byte OldCount, byte NewCount);
```

**SnapshotAggregator methods:**
```csharp
public void OnGearsetRegistered(byte oldCount, byte newCount)
    => _gearsetRegistered = new GearsetRegisteredSignal(oldCount, newCount);

public void OnGearsetRegisteredConsumed() => _gearsetRegistered = null;
```

Survives `ResetDeltas` (like all action-category signals). Cleared by `OnGearsetRegisteredConsumed` (called from `AuthoringHost.RecordStep`).

**GameStateSnapshot property:**
```csharp
// Non-positional. Set when UIObserver.PollGearsetCount detects that
// RaptureGearsetModule.NumGearsets increased during this recording window.
// Cleared by OnGearsetRegisteredConsumed (called from AuthoringHost.RecordStep
// and UIObserver.ResetWindowState) so it does not bleed into the next window.
// Baseline is NOT reset in ResetWindowState (gearset count persists across windows).
public GearsetRegisteredSignal? GearsetRegistered { get; init; }
```

**IGameProbe extension:**
```csharp
/// <summary>
/// Returns RaptureGearsetModule.NumGearsets (byte), or null when RaptureGearsetModule is unavailable.
/// </summary>
byte? GetGearsetCount();
```

**Polling approach (monotonic counter pattern -- like action effect polling):**
```
UIObserver.PollGearsetCount():
  - First observation (baseline): store value, no fire.
  - Subsequent observations: compare against stored value.
    - If increased: fire OnGearsetRegistered(old, new). Update stored value.
    - If same or decreased (deletion?): update stored value silently, no fire.
  - ResetWindowState: call OnGearsetRegisteredConsumed(); do NOT reset baseline
    (gearset count persists across recording windows).
```

**Limitation:** This signal only fires for NEW gearset creation (`CreateGearset`), not for gearset updates (`UpdateGearset`). This is acceptable because:
1. The primary use case is brand-new characters who have never created a gearset for a newly-acquired job. `CreateGearset` is the expected path.
2. If the author manually updates an existing gearset, the count does not change, and the step must be explicitly authored (like `EquipBestGearStep`).
3. The W1 warning (RG-7) reminds authors to add `skipIf` regardless of how the step was created.

### RG-15: Inference rule placement -- Rule 3.5k

**Decision:** Place the `GearsetRegistered` inference rule at Rule 3.5k -- BELOW `JobChanged` (Rule 3.5j) and ABOVE `EquipmentChanged` (Rule 3.5g).

**Priority rationale:** Gearset registration typically happens right after a job change (the player equips a starting weapon, then saves the gearset). If both `GearsetRegistered` and `JobChanged` fire in the same window, `JobChanged` must win (it's the more specific authoring intent -- the job change caused the gearset registration, not the other way around). If `GearsetRegistered` fires alone (no job change), it should win over `EquipmentChanged` (which might fire because the gearset save involves the current equipment state).

**Rule placement in priority order (relevant section):**
```
3.5s  SayChatMessageSent     (highest among 3.5x)
3.5i  ItemUsed
3.5j  JobChanged
3.5k  GearsetRegistered      (NEW -- above equipment, below job)
3.5g  EquipmentChanged
3.5e  EmoteCompleted
3.5   ActionCompleted         (lowest among 3.5x)
```

```csharp
// StepInferenceEngine, Rule 3.5k (insert between 3.5j and 3.5g)
if (after.GearsetRegistered is { } gsSignal)
{
    return new InferenceResult(
        StepType:        "register-gearset",
        SuggestedStepId: "register-gearset",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.GearsetRegistered,
        Notes:           $"Gearset count changed: {gsSignal.OldCount} -> {gsSignal.NewCount}. " +
                         "Author MUST add skipIf with jobGearsetExists(N) to prevent re-registration.");
}
```

### RG-16: StepFactory `"register-gearset"` arm

```csharp
"register-gearset" => new RegisterGearsetStep
{
    Id = stepId,
    Expect = expectValue,
},
```

No properties to populate from snapshot data -- the step is empty.

### RG-17: Debug accessor

Add `IGearsetManager DebugGearsetManager => _gearsetManager;` to EngineHost alongside the existing debug accessors.

---

## Schema shape

```csharp
// Step.cs -- add to [JsonDerivedType] list on Step:
[JsonDerivedType(typeof(RegisterGearsetStep), "register-gearset")]

// Step.cs -- add sealed class:
public sealed class RegisterGearsetStep : Step { }

// QuestForgeJsonContext.cs -- add:
[JsonSerializable(typeof(RegisterGearsetStep))]
```

**JSON example:**
```json
{
  "type": "register-gearset",
  "id": "register-gearset-drk",
  "skipIf": "jobGearsetExists(32)"
}
```

---

## Validation rule table

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| W1 (missing Expect) | W1 | Warning | `step.Expect is null` | NOT suppressed for RegisterGearsetStep (per RG-7) |

No new E-rules needed. The step has no required properties beyond the inherited `Id`.

---

## Given-When-Then test scenarios

### Schema tests (`QuestForge.Schema.Tests/RoundTripTests.cs`)

#### RG-RT1 -- Round-trip serialization for RegisterGearsetStep

**Given:**
- A `RegisterGearsetStep { Id = "register-gearset", Expect = new PredicateExpect { Predicate = "jobGearsetExists(32)" } }`.

**When:** Serialize to JSON then deserialize back.

**Then:**
- Deserialized step is `RegisterGearsetStep` with `Id == "register-gearset"`.
- `Expect` is `PredicateExpect` with `Predicate == "jobGearsetExists(32)"`.

### Engine tests (`QuestForge.Engine.Tests/Engine/RegisterGearsetStepTests.cs`)

All tests follow the established pattern. Quest with one RegisterGearsetStep in sequence 0. AcceptStep present to satisfy E4.

#### RG1 -- Happy path, adapter wired -- emits RegisterGearset

**Given:**
- Player not casting, not in combat.
- `RegisterGearsetStep { Id = "register-gearset" }`. No authored `Expect`.
- `harness.GearsetManager` is wired (non-null).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.RegisterGearset` with `Origin != null`.

#### RG2 -- No adapter wired -- AwaitUser

**Given:**
- A `QuestEngine` constructed WITHOUT `gearsetManager` (null default).
- `RegisterGearsetStep { Id = "register-gearset" }`.

**When:** `engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.AwaitUser` whose `Reason` contains `"no IGearsetManager wired"`.

#### RG3 -- Player casting -- Wait, no RegisterGearset emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- `RegisterGearsetStep { Id = "register-gearset" }`. Adapter wired.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"player casting"`.

#### RG4 -- Player in combat -- Wait, no RegisterGearset emitted

**Given:**
- `harness.GameState.SetInCombat(true)`.
- `RegisterGearsetStep { Id = "register-gearset" }`. Adapter wired.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"in combat"`.

#### RG5 -- skipIf satisfied -- step skipped

**Given:**
- `RegisterGearsetStep { Id = "register-gearset", SkipIf = PredicateExpect("jobGearsetExists(32)") }`.
- `harness.GameState.SetGearsetExistsForJob(32, true)`.
- Adapter wired.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Step is skipped (SkipIf evaluated true). Engine moves past it.
- `harness.GearsetManager.RecordedCalls.Count == 0` (adapter never called).

#### RG6 -- skipIf not satisfied -- step fires

**Given:**
- `RegisterGearsetStep { Id = "register-gearset", SkipIf = PredicateExpect("jobGearsetExists(32)") }`.
- `harness.GameState.SetGearsetExistsForJob(32, false)`.
- Adapter wired.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.RegisterGearset`.
- Step is NOT skipped.

#### RG7 -- Expect satisfied -- step confirmed

**Given:**
- `RegisterGearsetStep { Id = "register-gearset", Expect = PredicateExpect("jobGearsetExists(32)") }`.
- `harness.GameState.SetGearsetExistsForJob(32, true)`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (Expect satisfied, step confirmed).

#### RG8 -- Mounted + prior Navigate: dismount does NOT fire (exempt)

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep to `(200, 0, 0)` with `Expect = "playerZone() == 130"`.
  2. RegisterGearsetStep. Adapter wired.
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).

**When:**
1. Tick 1 --> `EngineAction.Navigate`. `_lastDispatchedWasNavigate = true`.
2. `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect satisfied).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.RegisterGearset`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount did NOT fire -- RegisterGearset IS exempt).

Pins Decision RG-11.

#### RG9 -- Standalone + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: RegisterGearsetStep. Adapter wired.
- Player mounted (`SetMountState(MountState.Mounted)`).

**When:** Tick once.

**Then:**
- Returns `EngineAction.RegisterGearset`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount only fires after prior Navigate, and RegisterGearset is exempt anyway).

#### RG10 -- RunToCompletion integration

**Given:**
- Quest: AcceptStep (auto-satisfies) + `RegisterGearsetStep { Expect = PredicateExpect("jobGearsetExists(32)") }`.
- Adapter wired.
- In the RunToCompletion loop, after `RegisterGearset` is dispatched, script `harness.GameState.SetGearsetExistsForJob(32, true)` to simulate the game confirming.

**When:** `harness.RunToCompletion(maxTicks: 10)`.

**Then:**
- `actions` contains at least one `RegisterGearset` entry.
- RunToCompletion succeeds (Done returned).

#### RG11 -- Cancellation propagates

**Given:**
- RegisterGearsetStep as RG1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

### Predicate tests (`QuestForge.Engine.Tests/Predicates/JobGearsetExistsPredicateTests.cs`)

#### RGP1 -- jobGearsetExists(32) returns true when gearset exists

**Given:**
- `harness.GameState.SetGearsetExistsForJob(32, true)`.

**When:** Evaluate predicate `"jobGearsetExists(32)"`.

**Then:** Returns `true`.

#### RGP2 -- jobGearsetExists(32) returns false when no gearset exists

**Given:**
- `harness.GameState.SetGearsetExistsForJob(32, false)` (or default -- no setup).

**When:** Evaluate predicate `"jobGearsetExists(32)"`.

**Then:** Returns `false`.

#### RGP3 -- jobGearsetExists used in skipIf with RegisterGearsetStep -- step skipped when true

**Given:**
- `RegisterGearsetStep { SkipIf = PredicateExpect("jobGearsetExists(32)") }`.
- `harness.GameState.SetGearsetExistsForJob(32, true)`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Step is skipped.
- `harness.GearsetManager.RecordedCalls.Count == 0`.

#### RGP4 -- jobGearsetExists used in expect -- step confirmed when true

**Given:**
- `RegisterGearsetStep { Expect = PredicateExpect("jobGearsetExists(32)") }`.
- `harness.GameState.SetGearsetExistsForJob(32, true)`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Step is confirmed (Expect satisfied). Returns `EngineAction.Wait`.

### Inference tests (`QuestForge.Engine.Tests/Authoring/RegisterGearsetInferenceTests.cs`)

#### RGI1 -- GearsetRegistered signal fires inference rule 3.5k

**Given:**
- `before` snapshot with no GearsetRegistered.
- `after` snapshot with `GearsetRegistered = new GearsetRegisteredSignal(5, 6)`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "register-gearset"`.
- `result.InferredFrom == InferredFrom.GearsetRegistered`.
- `result.Confidence == Confidence.High`.
- `result.SuggestedStepId == "register-gearset"`.
- `result.SuggestedExpect` is null.

#### RGI2 -- JobChanged has higher priority than GearsetRegistered

**Given:**
- `after` snapshot with BOTH `JobChanged = new JobChangedSignal(19, 32)` AND `GearsetRegistered = new GearsetRegisteredSignal(5, 6)`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "change-job"` (JobChanged at Rule 3.5j wins over GearsetRegistered at Rule 3.5k).

#### RGI3 -- GearsetRegistered has higher priority than EquipmentChanged

**Given:**
- `after` snapshot with BOTH `GearsetRegistered = new GearsetRegisteredSignal(5, 6)` AND `EquipmentChanged = new EquipmentChangedSignal([12345u])`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "register-gearset"` (GearsetRegistered at Rule 3.5k wins over EquipmentChanged at Rule 3.5g).

#### RGI4 -- StepFactory builds RegisterGearsetStep from inference result

**Given:**
- `InferenceResult` with `StepType = "register-gearset"`, `SuggestedExpect = null`.
- `after` snapshot with `GearsetRegistered = new GearsetRegisteredSignal(5, 6)`.

**When:** `StepFactory.Build(result, after)`.

**Then:**
- Result is `RegisterGearsetStep` with `Id == "register-gearset"`.

### UIObserver polling tests (`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`)

#### UO_GS1 -- First gearset count observation sets baseline, no fire

**Given:**
- `FakeGameProbe.SetGearsetCount(5)`.
- UIObserver created, no prior poll.

**When:** `OnFrameworkUpdate` fires once.

**Then:**
- `_aggregator.OnGearsetRegistered` was NOT called (first observation is silent baseline).

#### UO_GS2 -- Subsequent observation with increased count fires OnGearsetRegistered

**Given:**
- Baseline established: count = 5.
- `FakeGameProbe.SetGearsetCount(6)` (new gearset created).

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnGearsetRegistered` called with `oldCount = 5, newCount = 6`.

#### UO_GS3 -- Same count across ticks does not re-fire

**Given:**
- Baseline established with count = 6.
- Same value returned on next poll.

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnGearsetRegistered` NOT called.

#### UO_GS4 -- Decreased count (deletion) updates baseline silently, no fire

**Given:**
- Baseline established with count = 6.
- `FakeGameProbe.SetGearsetCount(5)` (gearset deleted).

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnGearsetRegistered` NOT called (only increases fire).
- Internal baseline updated to 5 (subsequent increase from 5 to 6 would fire).

#### UO_GS5 -- ResetWindowState calls OnGearsetRegisteredConsumed but does NOT reset baseline

**Given:**
- Gearset count increased from 5 to 6 and OnGearsetRegistered fired.

**When:** `ResetWindowState()` called, then another `OnFrameworkUpdate` with count still 6.

**Then:**
- `_aggregator.OnGearsetRegisteredConsumed` was called.
- `_aggregator.OnGearsetRegistered` NOT called on the subsequent tick (baseline was NOT reset; same count = no change).

### DraftValidator tests

#### RG12 -- W1 fires for RegisterGearsetStep without Expect

**Given:**
- `QuestDraft` with `RegisterGearsetStep { Expect = null }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `warnings` contains an entry with `Code == "W1"` for the register-gearset step.

### FunctionRegistry predicate test

#### RGP5 -- jobGearsetExists is registered in FunctionRegistry

**Given:** The `FunctionRegistry.All` list.

**When:** Search for function named `"jobGearsetExists"`.

**Then:**
- Found with arity `Fixed(1)`, parameter types `[Int]`, return type `Bool`.

This test lives in `QuestForge.Predicates.Tests` (the tools-repo predicate tests).

---

## Tooling catch-up (in paired PR -- NOT deferred)

### CapabilityInferrer

Add `[typeof(RegisterGearsetStep)] = "step:register-gearset"` to `StepCapabilities` dict.

### TraceConstants

Add `ActionRegisterGearset = "registergearset"` (from `EngineAction.RegisterGearset.GetType().Name.ToLowerInvariant()`). No behavior change (`IsTerminalAction` only uses `done`/`awaituser`).

### FilenameLookup

Add exact-shape entry:
```csharp
(["step:register-gearset", "step:talk", "step:travel"], "with-register-gearset.json"),
```

### DistinguishingCapPriority

Add entry after `step:change-job`, before `step:teleport`:
```csharp
("step:register-gearset", "with-register-gearset.json"),
```

Priority rationale: Gearset registration is a companion to job change and ranks equally in fixture identity. Below `step:change-job` because a quest that has both should be named after the change-job shape (the higher-level operation).

### FunctionRegistry

Add `jobGearsetExists` entry (per RG-6):
```csharp
new("jobGearsetExists", new Fixed(1), [Int], Bool),
```

### FIXTURES.md

Add the following rows:

**Capabilities table:**
| `step:register-gearset` | `RegisterGearsetStep` |

**`actionType` canonical strings table:**
| `"registergearset"` | `EngineAction.RegisterGearset` | `RegisterGearsetStep` dispatch -- save current gear as new gearset. |

---

## Implementation order

### Phase A -- Schema + Interface + Fake (30 min)

1. Add `RegisterGearsetStep` to `Step.cs` (sealed empty class, `[JsonDerivedType]`).
2. Add `[JsonSerializable(typeof(RegisterGearsetStep))]` to `QuestForgeJsonContext.cs`.
3. Add round-trip test RG-RT1 to `RoundTripTests.cs`.
4. Create `IGearsetManager.cs` in `QuestForge.Adapters/Gear/` with `RegisterOutcome` enum.
5. Create `FakeGearsetManager.cs` in `QuestForge.Adapters.Fakes/Gear/` (mirror `FakeJobChanger` shape).
6. Add `GearsetExistsForJob(uint jobId, CancellationToken ct)` to `IGameStateProvider`.
7. Add `SetGearsetExistsForJob(uint jobId, bool exists)` to `FakeGameStateProvider`.
8. Implement `GearsetExistsForJob` on all `IGameStateProvider` implementations (Fake, Recording, Replay).

**Done before B:** Schema round-trip test green. Adapters compile.

### Phase B -- Predicate + EngineAction + Engine dispatch (1 hour)

1. Append `EngineAction.RegisterGearset(Step? Origin = null)` to `EngineAction.cs`.
2. Add `"jobGearsetExists"` to `PredicateEvaluator.EvaluateFunction` switch.
3. Add `"jobGearsetExists"` entry to `FunctionRegistry.All`.
4. Add `_gearsetManager` field + optional ctor param to `QuestEngine`.
5. Add `ResolveRegisterGearset` method per Decision RG-9.
6. Add dispatch arm (6a8) to the step-dispatch switch, after the `ChangeJobStep` arm.
7. **Tester writes RG1-RG7, RG11, RGP1-RGP4, RG12** (single-tick dispatch, predicate, and validator tests). Red until builder implements steps 1-6.

**Done before C:** Engine tests green. `dotnet test QuestForge.Engine.Tests` passes.

### Phase C -- EngineTestHarness wiring + mount/integration tests (30 min)

1. Add `FakeGearsetManager` property to `EngineTestHarness`.
2. Pass to `QuestEngine` ctor as `gearsetManager:`.
3. Add `case EngineAction.RegisterGearset rg:` arm to `RunToCompletion`:
   ```csharp
   case EngineAction.RegisterGearset rg:
       actions.Add(action);
       EmitActionSubmitted("RegisterGearset", default);
       var rgResult = await GearsetManager.RegisterGearset(ct);
       EmitActionCompleted("RegisterGearset",
           rgResult.IsSuccess ? rgResult.ValueOrThrow.ToString() : "Failed");
       break;
   ```
4. **Tester writes RG8, RG9, RG10** (mount exemption and RunToCompletion integration).

**Done before D:** All engine tests green.

### Phase D -- DalamudGearsetManager + EngineHost (30 min)

1. Create `DalamudGearsetManager` in `QuestForge.Adapters.Dalamud/Gear/` per Decision RG-4.
2. Add `DalamudGearsetManager` field to `EngineHost`.
3. Construct in ctor.
4. Pass to `QuestEngine` in `BeginRun` as `gearsetManager: _gearsetManager`.
5. Add dispatch arm in `DispatchAction` per Decision RG-13.
6. Add `RegisterGearset` to the lazy-dismount exemption list per Decision RG-11.
7. Add `IGearsetManager DebugGearsetManager => _gearsetManager;` per Decision RG-17.
8. Implement `GearsetExistsForJob` on `DalamudGameStateProvider` (same scan as `DalamudJobChanger.GearsetExistsForJob`).
9. `dotnet build QuestForge.Plugin` succeeds.

**Done before E:** Plugin compiles.

### Phase E -- Authoring inference (1.5 hours)

1. Add `GearsetRegisteredSignal` record to `GameStateSnapshot.cs`.
2. Add `GearsetRegistered` property to `GameStateSnapshot` (non-positional init-only).
3. Add `InferredFrom.GearsetRegistered` enum value.
4. Add `OnGearsetRegistered` / `OnGearsetRegisteredConsumed` to `SnapshotAggregator`.
5. Add Rule 3.5k to `StepInferenceEngine` (between Rule 3.5j JobChanged and Rule 3.5g EquipmentChanged).
6. Add `"register-gearset"` arm to `StepFactory`.
7. Add `GetGearsetCount()` to `IGameProbe` + `FakeGameProbe`.
8. Add `PollGearsetCount` to `UIObserver`.
9. Wire `PollGearsetCount` into `OnFrameworkUpdate`.
10. Wire `OnGearsetRegisteredConsumed` into `ResetWindowState`.
11. Wire `OnGearsetRegisteredConsumed` into `AuthoringHost.RecordStep` consume sequence.
12. Wire `PreviewInference` diagnostic log extension.
13. Add `DalamudGameProbe.GetGearsetCount` implementation (reads `RaptureGearsetModule.Instance()->NumGearsets`).
14. **Tester writes RGI1-RGI4, UO_GS1-UO_GS5** (inference + polling tests).

**Done before F:** Inference tests green.

### Phase F -- Tooling catch-up (paired PR, 20 min)

1. Sync `RegisterGearsetStep` to tools-repo `QuestForge.Schema`.
2. Add `[typeof(RegisterGearsetStep)] = "step:register-gearset"` to `CapabilityInferrer.cs`.
3. Add `ActionRegisterGearset = "registergearset"` to `TraceConstants.cs`.
4. Add FilenameLookup entry.
5. Add DistinguishingCapPriority entry.
6. Add `jobGearsetExists` to `FunctionRegistry` (already listed in Phase B step 3 -- verify it landed).
7. Write tests for the new entries in `QuestForge.Tools.Trace.Tests/`.
8. Write RGP5 (FunctionRegistry test) in `QuestForge.Predicates.Tests/`.
9. Update `docs/FIXTURES.md` with the two new table rows.
10. `dotnet test` in tools repo green.

**Total estimated time: ~4.5 hours across schema, engine, predicate, Dalamud, inference, and tooling.**

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~RegisterGearsetStepTests` reports all 11 engine tests green (RG1-RG11).
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~JobGearsetExistsPredicate` reports all 4 predicate tests green (RGP1-RGP4).
3. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~DraftValidator` reports RG12 green.
4. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~RegisterGearsetInference` reports all 4 inference tests green (RGI1-RGI4).
5. `dotnet test QuestForge.Plugin.Tests --filter FullyQualifiedName~UO_GS` reports all 5 polling tests green (UO_GS1-UO_GS5).
6. `dotnet test QuestForge.Schema.Tests --filter FullyQualifiedName~RoundTrip` includes RegisterGearsetStep (RG-RT1).
7. A quest with `{ "type": "register-gearset", "id": "register-gearset" }` dispatches `EngineAction.RegisterGearset` from `QuestEngine`.
8. `skipIf: "jobGearsetExists(32)"` correctly skips the step when a gearset exists for job 32.
9. `jobGearsetExists(32)` predicate evaluates correctly and is registered in both `PredicateEvaluator` and `FunctionRegistry`.
10. `DalamudGearsetManager.RegisterGearset` creates a new gearset or updates an existing one.
11. `EngineHost.DispatchAction` has a `case EngineAction.RegisterGearset` arm that calls `_gearsetManager.RegisterGearset`.
12. W1 DOES fire for `RegisterGearsetStep` with `Expect = null` (not suppressed).
13. `StepInferenceEngine` infers `"register-gearset"` when `GearsetRegistered` signal is present.
14. `StepFactory` builds `RegisterGearsetStep` from inference result.
15. `UIObserver.PollGearsetCount` detects `NumGearsets` increases via `IGameProbe.GetGearsetCount()`.
16. `dotnet build` succeeds in both `questforge` and `questforge-tools` repos with no `TreatWarningsAsErrors` regressions.
17. `questforge-tools` TraceConstants has `ActionRegisterGearset = "registergearset"`.
18. Lazy-dismount does NOT fire before `RegisterGearset` (exempt from dismount).
19. No regression in ChangeJobStepTests, EquipGearForQuestStepTests, EquipBestGearStepTests, or any existing test.

---

## Exclusions

- **`ChangeJobStep` changes.** Already implemented (issue #119). `RegisterGearsetStep` is the companion that makes gearsets available for `ChangeJobStep`.
- **Gearset naming.** The game auto-names gearsets. No schema support for custom gearset names.
- **Gearset deletion.** Not a quest automation concern. If a player has too many gearsets, that is a manual management issue.
- **Multiple gearsets per job.** `RegisterGearset` creates or updates exactly one gearset per job. If the player has multiple gearsets for the same job, the adapter updates the first match.
- **Gearset ordering/priority.** No schema support for "use gearset #3 specifically."
- **In-game smoke test.** Manual Slice 4 verification (not part of this CI-focused plan).
- **RecordingGearsetManager wrapper.** Not needed (write-only adapter, per RG-12).

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 3 scenarios (RG1, RG7, RG10)
- Edge cases: 4 scenarios (RG5, RG6, RG8, RG9)
- Error / wait cases: 3 scenarios (RG2, RG3, RG4)
- Cancellation: 1 scenario (RG11)
- Predicate: 4 scenarios (RGP1-RGP4)
- Validator: 1 scenario (RG12)
- Schema: 1 scenario (RG-RT1)
- Inference: 4 scenarios (RGI1-RGI4)
- Polling: 5 scenarios (UO_GS1-UO_GS5)
- Expected total:
  - `QuestForge.Schema.Tests/RoundTripTests.cs`: 1 test (RG-RT1, appended)
  - `QuestForge.Engine.Tests/Engine/RegisterGearsetStepTests.cs`: 11 tests (RG1-RG11)
  - `QuestForge.Engine.Tests/Predicates/JobGearsetExistsPredicateTests.cs`: 4 tests (RGP1-RGP4)
  - `QuestForge.Engine.Tests/Authoring/RegisterGearsetInferenceTests.cs`: 4 tests (RGI1-RGI4)
  - `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`: 5 tests (UO_GS1-UO_GS5, appended to existing file)
  - `QuestForge.Engine.Tests/Authoring/DraftValidatorRegisterGearsetTests.cs`: 1 test (RG12)
  - `QuestForge.Predicates.Tests/`: 1 test (RGP5 -- FunctionRegistry)
  - Grand total: ~27 tests
