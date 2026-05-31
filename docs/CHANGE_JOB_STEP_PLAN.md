# ChangeJobStep Implementation Plan (Issue #119)

**Status:** ready for test creation

**Slice:** Combined 2-5 (schema+validator already done in #122; interface+fake+stub already done in #121; this plan covers engine dispatch, Dalamud impl, EngineHost wiring, `isPlayerJob` predicate, tooling catch-up, and authoring inference).

**Input docs:**
- `docs/GEAR_RESEARCH.md` -- Section 2.4 (job change via gearset), Section 5.3 (job change detection signal)
- `docs/EQUIP_GEAR_FOR_QUEST_STEP_PLAN.md` -- closest analog (implicit postcondition, self-confirm pattern, inference)
- `docs/EQUIP_BEST_GEAR_STEP_PLAN.md` -- secondary analog (simpler dispatch)
- `QuestForge.Schema/Step.cs:195-202` -- `ChangeJobStep { uint JobId }` (already landed in #122)
- `QuestForge.Adapters/Gear/IJobChanger.cs` -- 2-method interface (`ChangeToJob`, `GearsetExistsForJob`) (already landed in #121)
- `QuestForge.Adapters.Fakes/Gear/FakeJobChanger.cs` -- fake with `AddGearsetForJob`/`RemoveGearsetForJob` (already landed in #121)
- `QuestForge.Adapters.Dalamud/Gear/DalamudJobChanger.cs` -- stub returning `Failed` (already landed in #121)
- `QuestForge.Engine/QuestEngine.cs:46,93` -- `_jobChanger` field + optional ctor param (already wired)
- `QuestForge.Engine/Predicates/PredicateEvaluator.cs:127-131` -- existing `playerJobId()`, `isDiscipleOfWar()`, `isDiscipleOfMagic()`
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` -- existing signals (EquipmentChangedSignal pattern)
- `QuestForge.Plugin.Tracing/IGameProbe.cs` -- existing interface (no `GetCurrentClassJobId` yet)
- `QuestForge.Plugin/EngineHost.cs:57,127,240` -- `_jobChanger` field + construction + BeginRun passthrough (already wired)
- `QuestForge.Engine/Authoring/DraftValidator.cs:205-213` -- E18 (ChangeJobStep.JobId == 0) already landed
- FFXIVClientStructs: `PlayerState.CurrentClassJobId` (byte at offset 0x7E), `RaptureGearsetModule.EquipGearset`, `GearsetEntry.ClassJob`

**Output (CI behavior):** Adding `{ "type": "change-job", "id": "switch-to-drk", "jobId": 32 }` to a quest dispatches `EngineAction.ChangeJob` from `QuestEngine`. The engine checks `GetCurrentJob() == targetJobId` as an implicit postcondition before emitting the action (already on the right job = self-confirm). When the adapter reports `GearsetNotFound`, the engine returns `AwaitUser`. A new `isPlayerJob(N)` predicate is available for `expect`/`skipIf` usage. Authoring inference detects `PlayerState.CurrentClassJobId` changes via `UIObserver` polling.

---

## Dependency graph

```
QuestForge.Engine
  +-- EngineAction.ChangeJob (NEW record)
  +-- QuestEngine: ResolveChangeJob async pre-arm + dispatch arm (6a7)
  +-- QuestEngine: implicit postcondition (GetCurrentJob == targetJobId) before Expect
  +-- PredicateEvaluator: isPlayerJob(N) function
       |
QuestForge.Engine.Tests
  +-- Engine/ChangeJobStepTests.cs (CJ1-CJ14)
  +-- Predicates/IsPlayerJobPredicateTests.cs (CJP1-CJP4)
  +-- Helpers/EngineTestHarness.cs (RunToCompletion arm for ChangeJob)
       |
QuestForge.Adapters.Dalamud
  +-- Gear/DalamudJobChanger.cs (fleshed out from stub)
       |
QuestForge.Plugin
  +-- EngineHost.cs (DispatchAction arm for ChangeJob)
       |
QuestForge.Engine.Authoring (inference)
  +-- GameStateSnapshot: JobChangedSignal? JobChanged
  +-- SnapshotAggregator: OnJobChanged / OnJobChangedConsumed
  +-- InferredFrom: JobChanged enum value
  +-- StepInferenceEngine: Rule 3.5j -- JobChanged
  +-- StepFactory: "change-job" arm
       |
QuestForge.Plugin.Tracing
  +-- IGameProbe: GetCurrentClassJobId()
  +-- UIObserver: PollJobChange (single byte poll)
       |
questforge-tools (paired PR)
  +-- TraceConstants: ActionChangeJob
  +-- CapabilityInferrer: already has entry (no change)
  +-- FilenameLookup: new entry
  +-- DistinguishingCapPriority: new entry
```

**Build order:**
1. `EngineAction.ChangeJob` record in `EngineAction.cs`.
2. `isPlayerJob` predicate in `PredicateEvaluator.cs` + `FunctionRegistry`.
3. `QuestEngine`: `ResolveChangeJob` pre-arm + dispatch arm (6a7).
4. `EngineTestHarness`: `RunToCompletion` arm for `ChangeJob`.
5. Engine tests CJ1-CJ14, predicate tests CJP1-CJP4.
6. W1 suppression in `DraftValidator.cs`.
7. `DalamudJobChanger`: flesh out from stub.
8. `EngineHost`: dispatch arm.
9. Inference: snapshot signal, aggregator, inference rule, step factory, UIObserver poller, IGameProbe extension.
10. Tooling catch-up (paired PR).

---

## Architectural decisions

### CJ-1: EngineAction shape -- carries JobId

**Decision:** `EngineAction.ChangeJob` carries a `JobId` (the adapter-layer strong type) and `Step? Origin`.

```csharp
// QuestForge.Engine/EngineAction.cs (append after EquipBestGear)
public sealed record ChangeJob(JobId Job, Step? Origin = null) : EngineAction;
```

**Rationale:** The engine must tell the adapter which job to change to. `JobId` is the adapter-layer strong type already used by `IJobChanger.ChangeToJob`. The schema uses raw `uint JobId`, but the engine wraps it as `JobId` at the dispatch boundary (matching the pattern of `ItemId`, `NpcId`, etc.).

**What breaks if violated:** If the action carries raw `uint`, the EngineTestHarness must wrap it before calling `FakeJobChanger.ChangeToJob`. Using the strong type keeps the harness clean.

**Testability:** Tests call `harness.JobChanger.AddGearsetForJob(new JobId(32))` to script gearset availability, then assert the emitted action has `Job.Value == 32`.

### CJ-2: Implicit postcondition -- GetCurrentJob == targetJobId gates advancement (before Expect)

**Decision:** `ResolveChangeJob` checks `GetCurrentJob()` before emitting the action. If the player is already on the target job, the pre-arm returns `null`, and the dispatch arm self-confirms the step (adds to `_confirmedStepIds` and continues). This matches the `EquipGearForQuestStep` pattern (Decision EG-2).

**Implementation detail:** The implicit postcondition reads `GetCurrentJob` (already on `IGameStateProvider`). No new adapter method needed.

```csharp
// After guards pass:
var jobResult = await _gameState.GetCurrentJob(ct);
if (jobResult is Result<JobId>.Success { Value: var currentJob } && currentJob.Value == step.JobId)
    return null; // Already on the right job -- self-confirm.
```

**Rationale:**
- Unlike `EquipBestGearStep` (which has no deterministic postcondition), `ChangeJobStep` has a natural, deterministic check: "is the player on job X?"
- Requiring authors to write `"expect": "playerJobId() == 32"` when the step already carries `JobId = 32` is redundant.
- The Expect field remains available for additional postconditions (e.g., quest sequence advance after job change).

**What breaks if violated:** Without the implicit postcondition, every `ChangeJobStep` without authored `Expect` becomes a spin-loop (engine changes job, doesn't know it succeeded, re-fires). W1 warns, but the real fix is the implicit check.

### CJ-3: Pre-flight guards in `ResolveChangeJob`

**Decision:** Four guards in priority order:

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. No adapter wired | `_jobChanger is null` | `AwaitUser("ChangeJobStep dispatched but no IJobChanger wired")` |
| 2. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `Wait("player casting; deferring change-job")` |
| 3. Player in combat | `IsPlayerInCombat` | `Wait("player in combat; deferring change-job")` |
| 4. Gearset not found | `GearsetExistsForJob` returns false | `AwaitUser("no gearset found for job {jobId}")` |

After guards pass, check implicit postcondition (CJ-2). If already on the right job, return `null`. Otherwise emit `ChangeJob(new JobId(step.JobId), Origin: step)`.

```csharp
private async Task<EngineAction?> ResolveChangeJob(ChangeJobStep step, CancellationToken ct)
{
    if (_jobChanger is null)
        return new EngineAction.AwaitUser(
            "ChangeJobStep dispatched but no IJobChanger wired -- host must supply one");

    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring change-job", Origin: step);

    var inCombatResult = await _gameState.IsPlayerInCombat(ct);
    if (inCombatResult is Result<bool>.Success { Value: true })
        return new EngineAction.Wait("player in combat; deferring change-job", Origin: step);

    // Implicit postcondition: already on the right job?
    var jobResult = await _gameState.GetCurrentJob(ct);
    if (jobResult is Result<JobId>.Success { Value: var currentJob } && currentJob.Value == step.JobId)
        return null; // Self-confirm.

    // Pre-flight: gearset must exist.
    var gearsetResult = await _jobChanger.GearsetExistsForJob(new JobId(step.JobId), ct);
    if (gearsetResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            $"no gearset found for job {step.JobId} -- create one via /gearset before running this quest");

    return new EngineAction.ChangeJob(new JobId(step.JobId), Origin: step);
}
```

**Why InCombat guard:** GEAR_RESEARCH.md Section 1.4 confirms `RaptureGearsetModule.EquipGearset()` fails in combat. `ChangeToJob` calls `EquipGearset` internally. The game silently rejects gearset changes during combat.

**Why GearsetExistsForJob as a pre-flight:** The adapter's `ChangeToJob` returns `GearsetNotFound`, which the engine could handle as a retry-then-AwaitUser. But checking upfront avoids the round-trip and produces a clearer user-facing message. The gearset check is cheap (reads gearset memory, no IPC).

**Rejected alternative:** Omit the gearset pre-flight and let the adapter return `GearsetNotFound`. Rejected because the engine would retry on each tick until `MaxConsecutiveStepFailures`, wasting ticks on a permanently-failing operation. AwaitUser on the first tick is more honest.

### CJ-4: `_lastResolvedStep` is NOT set in `ResolveChangeJob`

Mirrors EG-4 / EB-4 / UA13 / UE12 / UI14. `ChangeJobStep` does not carry `DialogueChoices`, so `ExtractYesNo` returns null. The `Origin: step` field on `EngineAction.ChangeJob` carries context for trace consumers.

### CJ-5: Dismount exemption -- ChangeJob is NOT exempt

**Decision:** `ChangeJob` is NOT in the lazy-dismount exemption list. Changing jobs via `EquipGearset` changes the equipped weapon, which the game may reject while mounted (the mount animation system expects a stable weapon model). Additionally, switching from a flying-capable job to one that cannot fly in the current zone could cause undefined behavior mid-flight.

**No code change needed:** The existing exemption check `is not EngineAction.Navigate and not EngineAction.Teleport and not EngineAction.EquipGear and not EngineAction.EquipBestGear` already does NOT include `ChangeJob`. Lazy-dismount fires normally before ChangeJob.

**Pinned by tests:** CJ7 (mounted + prior Navigate: dismount DOES fire before ChangeJob) and CJ8 (standalone + mounted: dismount does NOT fire -- lazy-dismount is only armed by prior Navigate).

**Research basis:** GEAR_RESEARCH.md does not explicitly cover mounted gearset equip, and Questionable's `SwitchClassJob` does not address it either. Since gearset equip changes the weapon model (which affects mount animations), the safe default is to dismount first. If future testing proves gearset changes work while mounted, this decision can be revised without breaking tests (just add `ChangeJob` to the exemption list and flip CJ7's assertion).

### CJ-6: Recording-proxy decision -- no `RecordingJobChanger` needed

Per CLAUDE.md Slice 3 contract: "Write-only adapters don't need a `RecordingXxxExecutor` wrapper." `IJobChanger.ChangeToJob` is write-only. `GearsetExistsForJob` is a capability probe, not an observation worth recording. `action.submitted` / `action.completed` events from `EngineHost.DispatchAction` capture the write.

### CJ-7: DalamudJobChanger implementation design

**`ChangeToJob(JobId job, ct)` implementation:**

1. Read `PlayerState.Instance()->CurrentClassJobId`. If already == `job.Value`, return `Result.Ok(JobChangeOutcome.Changed)` (idempotent).
2. Iterate gearset slots 0..99:
   - Call `RaptureGearsetModule.Instance()->IsValidGearset(i)`.
   - If valid, read `RaptureGearsetModule.Instance()->GetGearset(i)->ClassJob`.
   - If `ClassJob == job.Value`, call `RaptureGearsetModule.Instance()->EquipGearset(i)`.
   - Return `Result.Ok(JobChangeOutcome.Changed)`.
3. If no matching gearset found: return `Result.Ok(JobChangeOutcome.GearsetNotFound)`.

**`GearsetExistsForJob(JobId job, ct)` implementation:**

Same scan as above, but returns `Result.Ok(true)` on first match, `Result.Ok(false)` if no match. Does not call `EquipGearset`.

**Pure helper extraction:** The gearset-scan-for-job logic is trivial (loop + byte comparison). No pure helper extraction needed -- unlike `EquipSlotResolver` (which maps EquipSlotCategory to slot indices with edge cases), the gearset scan has no interesting logic to unit-test independently.

### CJ-8: EngineHost dispatch arm for ChangeJob

```csharp
case EngineAction.ChangeJob cj:
    DebounceLog(
        $"changejob:{cj.Job.Value}",
        $"[ChangeJob] jobId={cj.Job.Value}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _jobChanger.ChangeToJob(cj.Job, ct);
    break;
```

Placement: after the `EquipBestGear` arm (EngineHost.cs line ~536), before the `Wait` arm.

### CJ-9: W1 suppression for ChangeJobStep

**Decision:** DO extend the W1 suppression guard to include `ChangeJobStep`.

**Rationale:** Decision CJ-2 introduces an implicit postcondition (`GetCurrentJob() == targetJobId`) that makes `Expect` truly optional. Without suppression, every `ChangeJobStep` with no `Expect` triggers W1, which is misleading because the engine will NOT spin-loop (it checks `GetCurrentJob` internally and self-confirms).

**New W1 guard:**
```csharp
if (step.Raw.Expect is null
    && step.Raw is not UseActionStep
    and not UseEmoteStep
    and not SayChatMessageStep
    and not UseItemStep
    and not EquipGearForQuestStep
    and not ChangeJobStep)  // NEW
```

### CJ-10: `isPlayerJob(jobId)` predicate function

**Decision:** Add a new predicate function `isPlayerJob(N)` to both `PredicateEvaluator` and `FunctionRegistry`. Returns `true` when the player's current job matches the given ClassJob row ID.

**PredicateEvaluator addition:**
```csharp
// In the EvaluateFunction switch, after "isDiscipleOfMagic":
"isPlayerJob" => (await _gameState.GetCurrentJob(ct)).ValueOrThrow.Value == (uint)(long)args[0],
```

**FunctionRegistry addition** (in `questforge-tools/QuestForge.Predicates/FunctionRegistry.cs`):
```csharp
new("isPlayerJob",  new Fixed(1), [Int], Bool),
```

**Why a new predicate (vs reusing `playerJobId() == N`):**
- `isPlayerJob(32)` is more readable than `playerJobId() == 32` in quest JSON.
- Mirrors existing boolean predicates (`isQuestComplete`, `isAttuned`, `isQuestAccepted`).
- No new `IGameStateProvider` method needed -- `GetCurrentJob` already exists and is used by `playerJobId()`.

**What breaks if violated:** Nothing breaks functionally. But quest authors would need `"expect": "playerJobId() == 32"` instead of the cleaner `"expect": "isPlayerJob(32)"`. The predicate is a convenience, not a necessity.

### CJ-11: Authoring inference -- job change detection

**Detection signal:** Poll `PlayerState.CurrentClassJobId` (single byte) via `IGameProbe.GetCurrentClassJobId()`. On change, emit `JobChangedSignal(OldJobId, NewJobId)`.

**Signal record:**
```csharp
// GameStateSnapshot.cs
public sealed record JobChangedSignal(uint OldJobId, uint NewJobId);
```

**SnapshotAggregator methods:**
```csharp
public void OnJobChanged(uint oldJobId, uint newJobId)
    => _jobChanged = new JobChangedSignal(oldJobId, newJobId);

public void OnJobChangedConsumed() => _jobChanged = null;
```

Survives `ResetDeltas` (like all action-category signals). Cleared by `OnJobChangedConsumed` (called from `AuthoringHost.RecordStep`).

**GameStateSnapshot property:**
```csharp
// Non-positional. Set when UIObserver.PollJobChange detects that
// PlayerState.CurrentClassJobId changed during this recording window.
// Cleared by OnJobChangedConsumed (called from AuthoringHost.RecordStep
// and UIObserver.ResetWindowState) so it does not bleed into the next window.
public JobChangedSignal? JobChanged { get; init; }
```

**IGameProbe extension:**
```csharp
/// <summary>
/// Returns PlayerState.CurrentClassJobId (byte), or null when PlayerState is unavailable.
/// </summary>
byte? GetCurrentClassJobId();
```

**Polling approach (monotonic-value pattern -- like equipment polling):**

```
UIObserver.PollJobChange():
  - First observation (baseline): store value, no fire.
  - Subsequent observations: compare against stored value.
    - If changed: fire OnJobChanged(old, new). Update stored value.
    - If same: no fire.
  - ResetWindowState: call OnJobChangedConsumed(); do NOT reset baseline
    (job state persists across recording windows).
```

### CJ-12: Inference rule placement -- Rule 3.5j

**Decision:** Place the `JobChanged` inference rule at Rule 3.5j -- ABOVE `EquipmentChanged` (Rule 3.5g) and BELOW `ItemUsed` (Rule 3.5i).

**Priority rationale:** A job change always causes an equipment change (the gearset equip swaps all gear). If both `JobChanged` and `EquipmentChanged` fire in the same window, `JobChanged` must win because it is the more specific signal. The `EquipmentChanged` signal is a side effect of the gearset equip, not the author's intent.

**Rule placement in priority order (relevant section):**
```
3.5s  SayChatMessageSent     (highest among 3.5x)
3.5i  ItemUsed
3.5j  JobChanged             (NEW -- above equipment, below item)
3.5g  EquipmentChanged
3.5e  EmoteCompleted
3.5   ActionCompleted         (lowest among 3.5x)
```

```csharp
// StepInferenceEngine, Rule 3.5j (insert between 3.5i and 3.5g)
if (after.JobChanged is { } jobSignal)
{
    return new InferenceResult(
        StepType:        "change-job",
        SuggestedStepId: $"change-job-{jobSignal.NewJobId}",
        SuggestedExpect: null,
        Confidence:      Confidence.High,
        InferredFrom:    InferredFrom.JobChanged,
        Notes:           $"Job changed: {jobSignal.OldJobId} -> {jobSignal.NewJobId}. " +
                         "Implicit postcondition handles self-confirmation.");
}
```

### CJ-13: StepFactory `"change-job"` arm

```csharp
"change-job" => new ChangeJobStep
{
    Id = stepId,
    Expect = expectValue,
    JobId = after?.JobChanged?.NewJobId ?? 0u
},
```

### CJ-14: Tooling catch-up scope

**CapabilityInferrer:** Already has `[typeof(ChangeJobStep)] = "step:change-job"` at line 29. No change.

**TraceConstants:** Add `ActionChangeJob = "changejob"` (from `EngineAction.ChangeJob.GetType().Name.ToLowerInvariant()`). No behavior change (`IsTerminalAction` only uses `done`/`awaituser`).

**FilenameLookup:** Add exact-shape entry:
```csharp
(["step:change-job", "step:talk", "step:travel"], "with-change-job.json"),
```

**DistinguishingCapPriority:** Add entry after `step:equip-best-gear` (or after `step:equip-gear-for-quest`), before `step:say-chat-message`:
```csharp
("step:change-job", "with-change-job.json"),
```

Priority rationale: Job change is as distinguishing as gear equip steps (both affect player state directly). Ranks equal to or just above `equip-gear-for-quest` since changing job implies a full gearset swap.

### CJ-15: Debug accessor

Add `IJobChanger DebugJobChanger => _jobChanger;` to EngineHost alongside the existing debug accessors.

---

## Validation rule table

All rules already landed in #122. Reproduced for reference:

| Rule | Code | Severity | Check | Suppressed when |
|---|---|---|---|---|
| ChangeJobStep.JobId == 0 | E18 | Error | `ChangeJobStep.JobId == 0` | -- |
| W1 (missing Expect) | W1 | Warning | `step.Expect is null` | **Extended**: `not (... or ChangeJobStep)` per CJ-9 |

---

## Given-When-Then test scenarios

### Engine tests (`QuestForge.Engine.Tests/Engine/ChangeJobStepTests.cs`)

All tests follow the established pattern. Quest with one ChangeJobStep in sequence 0. AcceptStep present to satisfy E4.

#### CJ1 -- Happy path, not on target job, gearset exists -- emits ChangeJob

**Given:**
- Player not casting, not in combat.
- `ChangeJobStep { JobId = 32 }` (Dark Knight). No authored `Expect`.
- `harness.GameState.SetCurrentJob(new JobId(19))` (player is Paladin, not DRK).
- `harness.JobChanger.AddGearsetForJob(new JobId(32))`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.ChangeJob` with `Job.Value == 32`, `Origin != null`.

#### CJ2 -- Already on target job -- step self-confirms

**Given:**
- Player not casting, not in combat.
- `ChangeJobStep { JobId = 32 }`. No authored `Expect`.
- `harness.GameState.SetCurrentJob(new JobId(32))` (already on DRK).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (step confirmed, no more steps, waiting for sequence advance).
- The step is confirmed (confirmed set contains the step ID).
- `harness.JobChanger.RecordedCalls.Count == 0` (adapter never called).

#### CJ3 -- Gearset not found -- AwaitUser

**Given:**
- Player not casting, not in combat.
- `ChangeJobStep { JobId = 32 }`. No authored `Expect`.
- `harness.GameState.SetCurrentJob(new JobId(19))` (not on DRK).
- No gearset added for job 32 (default: `GearsetExistsForJob` returns false).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.AwaitUser` whose `Reason` contains `"no gearset found for job 32"`.
- `harness.JobChanger.RecordedCalls.Count == 0` (adapter's ChangeToJob never called).

#### CJ4 -- Player casting -- Wait, no ChangeJob emitted

**Given:**
- `harness.GameState.SetCasting(true)`.
- `ChangeJobStep { JobId = 32 }`. Player not on DRK. Gearset exists.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"player casting"`.

#### CJ5 -- Player in combat -- Wait, no ChangeJob emitted

**Given:**
- `harness.GameState.SetInCombat(true)`.
- `ChangeJobStep { JobId = 32 }`. Player not on DRK. Gearset exists.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` whose `Reason` contains `"in combat"`.

#### CJ6 -- No adapter wired -- AwaitUser

**Given:**
- A `QuestEngine` constructed WITHOUT `jobChanger` (null default).
- `ChangeJobStep { JobId = 32 }`.

**When:** `engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.AwaitUser` whose `Reason` contains `"no IJobChanger wired"`.

#### CJ7 -- Mounted + prior Navigate: lazy-dismount DOES fire before ChangeJob (NOT exempt)

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep to `(200, 0, 0)` with `Expect = "playerZone() == 130"`.
  2. ChangeJobStep `{ JobId = 32 }`. Player not on DRK. Gearset exists.
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).

**When:**
1. Tick 1 --> `EngineAction.Navigate`. `_lastDispatchedWasNavigate = true`.
2. `harness.GameState.SetZone(new ZoneId(130))` (TravelStep Expect satisfied).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.ChangeJob(32)` -- BUT the test harness RunToCompletion must model dismount. The key assertion: `harness.Mount.DismountCallCount >= 1` (lazy-dismount DID fire -- ChangeJob is NOT exempt).

**Note:** This test requires the EngineTestHarness to model the lazy-dismount logic for the `_lastDispatchedActionWasNavigate` flag. If the harness does not model this, the test verifies the Engine emits `ChangeJob` (not `Dismount`) and a code-review-level assertion on the EngineHost exemption list suffices.

Pins Decision CJ-5.

#### CJ8 -- Standalone ChangeJob + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: ChangeJobStep `{ JobId = 32 }`. Player not on DRK. Gearset exists.
- Player mounted (`SetMountState(MountState.Mounted)`).

**When:** Tick once.

**Then:**
- Returns `EngineAction.ChangeJob(32)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount only fires after prior Navigate).

#### CJ9 -- Authored Expect already satisfied + already on target job -- step skipped

**Given:**
- `ChangeJobStep { JobId = 32, Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(new AetheryteId(8), true)` (predicate true).
- `harness.GameState.SetCurrentJob(new JobId(32))` (already on DRK).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits in cursor walk; step confirmed).

#### CJ10 -- Authored Expect NOT satisfied but already on target job -- step self-confirms via dispatch arm

**Given:**
- `ChangeJobStep { JobId = 32, Expect = PredicateExpect("questFlag(82111, 3)") }`.
- `harness.GameState.SetCurrentJob(new JobId(32))` (already on DRK).
- Quest flag 3 NOT set (predicate false).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- The dispatch arm runs (Expect was false in the cursor walk). `ResolveChangeJob` checks `GetCurrentJob`, finds already on target job, returns null. Dispatch arm self-confirms.
- Returns `EngineAction.Wait` (no more steps).

#### CJ11 -- RunToCompletion integration: change job then advance

**Given:**
- Quest: AcceptStep (auto-satisfies) + `ChangeJobStep { JobId = 32 }`. No authored Expect.
- Player starts as job 19. Gearset exists for 32.
- In the RunToCompletion loop, after `ChangeJob(32)` is dispatched, script `harness.GameState.SetCurrentJob(new JobId(32))` to simulate the game confirming.

**When:** `harness.RunToCompletion(maxTicks: 10)`.

**Then:**
- `actions` contains at least one `ChangeJob` entry with `Job.Value == 32`.
- RunToCompletion succeeds (Done returned).

#### CJ12 -- Cancellation propagates

**Given:**
- ChangeJobStep as CJ1.
- `using var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

#### CJ13 -- Adapter returns GearsetNotFound (dispatched despite pre-flight) -- engine re-fires

**Given:**
- `ChangeJobStep { JobId = 32 }`. Player not on DRK.
- `harness.JobChanger.AddGearsetForJob(new JobId(32))` (pre-flight passes).
- `harness.JobChanger.ScriptNextResult(JobChangeOutcome.GearsetNotFound)` (adapter fails).

**When:** Tick once (RunToCompletion dispatches the action, adapter returns GearsetNotFound).

**Then:**
- Returns `EngineAction.ChangeJob(32)` (the engine does not inspect the adapter result -- fire-and-forget like EquipBestGear).
- The engine will re-fire on the next tick because the implicit postcondition (GetCurrentJob) is still unmet.

This test verifies that adapter failure at the dispatch level does not crash the engine.

#### CJ14 -- GetCurrentJob read failure (fail-open) -- emits ChangeJob

**Given:**
- `ChangeJobStep { JobId = 32 }`.
- `harness.GameState.SetCurrentJob(new JobId(19))` (not on DRK).
- `harness.JobChanger.AddGearsetForJob(new JobId(32))`.
- `harness.GameState.SetCurrentJobFailure("adapter error")` (GetCurrentJob returns Failure).

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.ChangeJob(32)` (fail-open: if we can't read the current job, we cannot confirm the implicit postcondition, so we emit the action anyway).
- This is safe because `ChangeToJob` is idempotent (already-on-job is a no-op inside the adapter).

### Predicate tests (`QuestForge.Engine.Tests/Predicates/IsPlayerJobPredicateTests.cs`)

#### CJP1 -- isPlayerJob(32) returns true when player is on job 32

**Given:**
- `harness.GameState.SetCurrentJob(new JobId(32))`.

**When:** Evaluate predicate `"isPlayerJob(32)"`.

**Then:** Returns `true`.

#### CJP2 -- isPlayerJob(32) returns false when player is on job 19

**Given:**
- `harness.GameState.SetCurrentJob(new JobId(19))`.

**When:** Evaluate predicate `"isPlayerJob(32)"`.

**Then:** Returns `false`.

#### CJP3 -- isPlayerJob used in expect with ChangeJobStep -- step confirmed when true

**Given:**
- `ChangeJobStep { JobId = 32, Expect = PredicateExpect("isPlayerJob(32)") }`.
- `harness.GameState.SetCurrentJob(new JobId(32))`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Returns `EngineAction.Wait` (Expect satisfied, step confirmed).

#### CJP4 -- isPlayerJob used in skipIf -- step skipped when true

**Given:**
- `ChangeJobStep { JobId = 32, SkipIf = PredicateExpect("isPlayerJob(32)") }`.
- `harness.GameState.SetCurrentJob(new JobId(32))`.
- No authored `Expect`.

**When:** `harness.Engine.Tick(ct)`.

**Then:**
- Step is skipped (SkipIf evaluated true). Engine moves past it.
- `harness.JobChanger.RecordedCalls.Count == 0` (adapter never called).

### Inference tests (`QuestForge.Engine.Tests/Authoring/ChangeJobInferenceTests.cs`)

#### CJI1 -- JobChanged signal fires inference rule 3.5j

**Given:**
- `before` snapshot with no JobChanged.
- `after` snapshot with `JobChanged = new JobChangedSignal(19, 32)`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "change-job"`.
- `result.InferredFrom == InferredFrom.JobChanged`.
- `result.Confidence == Confidence.High`.
- `result.SuggestedStepId == "change-job-32"`.

#### CJI2 -- JobChanged has higher priority than EquipmentChanged

**Given:**
- `after` snapshot with BOTH `JobChanged = new JobChangedSignal(19, 32)` AND `EquipmentChanged = new EquipmentChangedSignal([12345u])`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "change-job"` (JobChanged at Rule 3.5j wins over EquipmentChanged at Rule 3.5g).

#### CJI3 -- StepFactory builds ChangeJobStep from inference result

**Given:**
- `InferenceResult` with `StepType = "change-job"`, `SuggestedExpect = null`.
- `after` snapshot with `JobChanged = new JobChangedSignal(19, 32)`.

**When:** `StepFactory.Build(result, after)`.

**Then:**
- Result is `ChangeJobStep` with `JobId == 32`.

#### CJI4 -- ItemUsed has higher priority than JobChanged

**Given:**
- `after` snapshot with BOTH `ItemUsed = new ItemUsedSignal(...)` AND `JobChanged = new JobChangedSignal(19, 32)`.

**When:** `StepInferenceEngine.Infer(before, after)`.

**Then:**
- `result.StepType == "use-item"` (ItemUsed at Rule 3.5i wins over JobChanged at Rule 3.5j).

### UIObserver polling tests (`QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`)

#### UO_J1 -- First job observation sets baseline, no fire

**Given:**
- `FakeGameProbe.SetCurrentClassJobId(19)` (Paladin).
- UIObserver created, no prior poll.

**When:** `OnFrameworkUpdate` fires once.

**Then:**
- `_aggregator.OnJobChanged` was NOT called (first observation is silent baseline).

#### UO_J2 -- Subsequent observation with changed job fires OnJobChanged

**Given:**
- Baseline established: job = 19.
- `FakeGameProbe.SetCurrentClassJobId(32)` (now Dark Knight).

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnJobChanged` called with `oldJobId = 19, newJobId = 32`.

#### UO_J3 -- Same job across ticks does not re-fire

**Given:**
- Baseline established with job = 32.
- Same value returned on next poll.

**When:** `OnFrameworkUpdate` fires.

**Then:**
- `_aggregator.OnJobChanged` NOT called.

#### UO_J4 -- ResetWindowState calls OnJobChangedConsumed but does NOT reset baseline

**Given:**
- Job changed from 19 to 32 and OnJobChanged fired.

**When:** `ResetWindowState()` called, then another `OnFrameworkUpdate` with job still 32.

**Then:**
- `_aggregator.OnJobChangedConsumed` was called.
- `_aggregator.OnJobChanged` NOT called on the subsequent tick (baseline was NOT reset; same job = no change).

### DraftValidator tests

#### CJ15 -- W1 suppressed for ChangeJobStep without Expect

**Given:**
- `QuestDraft` with `ChangeJobStep { JobId = 32, Expect = null }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `warnings` does NOT contain any entry with `Code == "W1"` for the change-job step.
- `errors` does NOT contain E18 (JobId is non-zero).

#### CJ16 -- E18 fires for ChangeJobStep with JobId == 0

**Given:**
- `QuestDraft` with `ChangeJobStep { JobId = 0 }`. AcceptStep present.

**When:** `DraftValidator.Validate(draft)`.

**Then:**
- `errors` contains an entry with `Code == "E18"` for the change-job step.

### FunctionRegistry predicate test

#### CJP5 -- isPlayerJob is registered in FunctionRegistry

**Given:** The `FunctionRegistry.All` list.

**When:** Search for function named `"isPlayerJob"`.

**Then:**
- Found with arity `Fixed(1)`, parameter types `[Int]`, return type `Bool`.

This test lives in `QuestForge.Predicates.Tests` (the tools-repo predicate tests).

---

## Implementation order

### Phase A -- EngineAction + Predicate + Engine dispatch (1.5 hours)

1. Append `EngineAction.ChangeJob(JobId Job, Step? Origin = null)` to `EngineAction.cs` (after `EquipBestGear`).
2. Add `"isPlayerJob"` to `PredicateEvaluator.EvaluateFunction` switch (after `isDiscipleOfMagic`).
3. Add `"isPlayerJob"` entry to `FunctionRegistry.All` (in `questforge-tools/QuestForge.Predicates/FunctionRegistry.cs`).
4. Add `ResolveChangeJob` method to `QuestEngine.cs` per Decision CJ-3.
5. Add dispatch arm (6a7) to the step-dispatch switch in `ResolveAction`, after the `EquipBestGearStep` arm. Self-confirms when `ResolveChangeJob` returns null.
6. Extend W1 suppression in `DraftValidator.cs` to include `ChangeJobStep` per Decision CJ-9.
7. **Tester writes CJ1-CJ6, CJ9, CJ10, CJ12, CJ13, CJ14, CJP1-CJP4, CJ15, CJ16** (single-tick dispatch, predicate, and validator tests). Red until builder implements steps 1-6.

**Done before B:** Engine tests green. `dotnet test QuestForge.Engine.Tests` passes.

### Phase B -- EngineTestHarness wiring + mount/integration tests (30 min)

1. Add `case EngineAction.ChangeJob cj:` arm to `RunToCompletion` in `EngineTestHarness.cs`:
   ```csharp
   case EngineAction.ChangeJob cj:
       actions.Add(action);
       EmitActionSubmitted("ChangeJob",
           JsonSerializer.SerializeToElement(new { jobId = cj.Job.Value }, _jsonOpts));
       var cjResult = await JobChanger.ChangeToJob(cj.Job, ct);
       if (cjResult.IsSuccess && cjResult.ValueOrThrow == JobChangeOutcome.Changed)
           GameState.SetCurrentJob(cj.Job);
       EmitActionCompleted("ChangeJob",
           cjResult.IsSuccess ? cjResult.ValueOrThrow.ToString() : "Failed");
       break;
   ```
2. **Tester writes CJ7, CJ8, CJ11** (mount exemption and RunToCompletion integration).

**Done before C:** All engine tests green.

### Phase C -- DalamudJobChanger (30 min)

1. Flesh out `DalamudJobChanger.ChangeToJob` per Decision CJ-7.
2. Flesh out `DalamudJobChanger.GearsetExistsForJob`.
3. `dotnet build QuestForge.Adapters.Dalamud` succeeds.

**Done before D:** Plugin compiles.

### Phase D -- EngineHost dispatch arm (15 min)

1. Add `case EngineAction.ChangeJob cj:` arm to `EngineHost.DispatchAction` per Decision CJ-8.
2. Add `IJobChanger DebugJobChanger => _jobChanger;` per Decision CJ-15.
3. `dotnet build QuestForge.Plugin` succeeds.

**Done before E:** Plugin compiles.

### Phase E -- Authoring inference (1.5 hours)

1. Add `JobChangedSignal` record to `GameStateSnapshot.cs`.
2. Add `JobChanged` property to `GameStateSnapshot` (non-positional init-only).
3. Add `InferredFrom.JobChanged` enum value.
4. Add `OnJobChanged` / `OnJobChangedConsumed` to `SnapshotAggregator`.
5. Add Rule 3.5j to `StepInferenceEngine` (between Rule 3.5i ItemUsed and Rule 3.5g EquipmentChanged).
6. Add `"change-job"` arm to `StepFactory`.
7. Add `GetCurrentClassJobId()` to `IGameProbe` + `FakeGameProbe`.
8. Add `PollJobChange` to `UIObserver`.
9. Wire `PollJobChange` into `OnFrameworkUpdate`.
10. Wire `OnJobChangedConsumed` into `ResetWindowState`.
11. Wire `OnJobChangedConsumed` into `AuthoringHost.RecordStep` consume sequence.
12. Wire `PreviewInference` diagnostic log extension.
13. Add `DalamudGameProbe.GetCurrentClassJobId` implementation (reads `PlayerState.Instance()->CurrentClassJobId`).
14. **Tester writes CJI1-CJI4, UO_J1-UO_J4** (inference + polling tests).

**Done before F:** Inference tests green.

### Phase F -- Tooling catch-up (paired PR, 20 min)

1. Add `ActionChangeJob = "changejob"` to `TraceConstants.cs`.
2. Add FilenameLookup entry per Decision CJ-14.
3. Add DistinguishingCapPriority entry per Decision CJ-14.
4. Add `isPlayerJob` to `FunctionRegistry` per Decision CJ-10 (already listed in Phase A step 3 -- verify it landed).
5. Write tests for the new entries in `QuestForge.Tools.Trace.Tests/`.
6. Write CJP5 (FunctionRegistry test) in `QuestForge.Predicates.Tests/`.
7. `dotnet test` in tools repo green.

**Total estimated time: ~4.5 hours across engine, predicate, Dalamud, inference, and tooling.**

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~ChangeJobStepTests` reports all 14 engine tests green (CJ1-CJ14).
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~IsPlayerJobPredicate` reports all 4 predicate tests green (CJP1-CJP4).
3. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~DraftValidator` reports CJ15 and CJ16 green.
4. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~ChangeJobInference` reports all 4 inference tests green (CJI1-CJI4).
5. `dotnet test QuestForge.Plugin.Tests --filter FullyQualifiedName~UO_J` reports all 4 polling tests green (UO_J1-UO_J4).
6. A quest with `{ "type": "change-job", "id": "switch-to-drk", "jobId": 32 }` dispatches `EngineAction.ChangeJob(32)` when the player is not on job 32.
7. When the player is already on the target job (via `GetCurrentJob`), the step self-confirms without needing an authored `Expect`.
8. When `GearsetExistsForJob` returns false, the engine returns `AwaitUser` with a clear message about the missing gearset.
9. `isPlayerJob(32)` predicate evaluates correctly and is registered in both `PredicateEvaluator` and `FunctionRegistry`.
10. `DalamudJobChanger.ChangeToJob` scans gearsets and calls `EquipGearset` for the first matching gearset.
11. `EngineHost.DispatchAction` has a `case EngineAction.ChangeJob` arm that calls `_jobChanger.ChangeToJob`.
12. W1 does NOT fire for `ChangeJobStep` with `Expect = null`.
13. `StepInferenceEngine` infers `"change-job"` when `JobChanged` signal is present.
14. `StepFactory` builds `ChangeJobStep` with correct `JobId` from the `JobChanged` signal.
15. `UIObserver.PollJobChange` detects `CurrentClassJobId` changes via `IGameProbe.GetCurrentClassJobId()`.
16. `dotnet build` succeeds in both `questforge` and `questforge-tools` repos with no `TreatWarningsAsErrors` regressions.
17. `questforge-tools` TraceConstants has `ActionChangeJob = "changejob"`.
18. No regression in EquipGearForQuestStepTests, EquipBestGearStepTests, UseItemStepTests, UseEmoteStepTests, UseActionStepTests, SayChatMessageStepTests, TeleportStepTests, PurchaseItemStepTests, AttunementStepTests, or any existing test.

---

## Exclusions

- **`RegisterGearsetStep`.** Tracked in issue #130. ChangeJobStep requires a gearset to already exist; it does not create one.
- **Weapon/crystal equip fallback.** Per user decision: gearset-only, no fallback. If no gearset exists, AwaitUser.
- **Schema changes.** Already landed in #122. `ChangeJobStep { uint JobId }` is final.
- **Interface split.** Already landed in #121. `IJobChanger` with 2 methods is final.
- **DraftValidator E18 rule.** Already landed in #122.
- **Fake (`FakeJobChanger`) and Dalamud stub.** Already landed in #121.
- **`EquipGearForQuestStep` engine dispatch.** Already implemented (issue #117).
- **`EquipBestGearStep` engine dispatch.** Already implemented (issue #118).
- **Repair interfaces.** Tracked in issue #120.
- **Multiple gearsets per job.** The adapter picks the first matching gearset. No schema-level control for "use gearset #3 specifically."
- **In-game smoke test.** Manual Slice 4 verification (not part of this CI-focused plan).

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 4 scenarios (CJ1, CJ2, CJ9, CJ11)
- Edge cases: 5 scenarios (CJ7, CJ8, CJ10, CJ13, CJ14)
- Error / wait cases: 3 scenarios (CJ3, CJ4, CJ5, CJ6)
- Cancellation: 1 scenario (CJ12)
- Predicate: 4 scenarios (CJP1-CJP4)
- Validator: 2 scenarios (CJ15, CJ16)
- Inference: 4 scenarios (CJI1-CJI4)
- Polling: 4 scenarios (UO_J1-UO_J4)
- Expected total:
  - `QuestForge.Engine.Tests/Engine/ChangeJobStepTests.cs`: 14 tests (CJ1-CJ14)
  - `QuestForge.Engine.Tests/Predicates/IsPlayerJobPredicateTests.cs`: 4 tests (CJP1-CJP4)
  - `QuestForge.Engine.Tests/Authoring/ChangeJobInferenceTests.cs`: 4 tests (CJI1-CJI4)
  - `QuestForge.Plugin.Tests/Tracing/UIObserverTests.cs`: 4 tests (UO_J1-UO_J4, appended to existing file)
  - `QuestForge.Engine.Tests/Authoring/DraftValidatorChangeJobTests.cs`: 2 tests (CJ15, CJ16)
  - `QuestForge.Predicates.Tests/`: 1 test (CJP5 -- FunctionRegistry)
  - Grand total: ~29 tests
