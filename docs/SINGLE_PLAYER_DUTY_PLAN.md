# Single Player Duty (SPD) Support Plan

**Status:** ready to implement
**Input docs:** Issue #138, docs/ADAPTERS.md, docs/SCHEMA.md, docs/ARCHITECTURE.md, BossMod IPCProvider.cs, Questionable SinglePlayerDuty.cs
**Output:** `DutyStep(kind: "spd")` runs end-to-end against fakes; BossMod AI delegates combat inside solo instances; retry with difficulty preference works; tooling catch-up lands in same slice.
**Phase dependencies:** Phase 11 (corpus expansion) -- builds on existing DutyStep schema, EngineHost dispatch, SelectYesnoResponder

---

## Dependency graph

```
Slice 2 — Engine + schema + adapter + validator + fakes + tests (single PR, questforge repo)
  Schema:    DutyStep already exists (kind: "spd" already valid in schema)
  Adapter:   IDutyRunner (new, in QuestForge.Adapters/Duty/)
  Fake:      FakeDutyRunner (new, in QuestForge.Adapters.Fakes/Duty/)
  Engine:    EngineAction.EnterSinglePlayerDuty + ResolveSpd pre-arm + DutyStep dispatch
  Validator: No new rules (DutyStep already validated by StructuralValidator)
  Tests:     QuestForge.Engine.Tests/Engine/SinglePlayerDutyTests.cs

Slice 3 — Dalamud impl + EngineHost dispatch + tooling catch-up (paired PRs)
  questforge repo:
    DalamudDutyRunner (BossMod IPC + chat commands)
    EngineHost: field, ctor, BeginRun, DispatchAction arm, addon handlers
    DifficultySelectHandler (new polling addon handler)
    PluginConfig.PreferredSpdDifficulty

  questforge-tools repo:
    TraceConstants.ActionEnterSinglePlayerDuty
    CapabilityInferrer (step:duty already mapped; no change needed)
    FilenameLookup / DistinguishingCapPriority (already have step:spd entries)
    FIXTURES.md update
```

---

## Architectural decisions

### SPD1: No new step type -- DutyStep(kind: "spd") is sufficient

`DutyStep` already exists in `Step.cs` with `Kind` property accepting `"regular"` and `"spd"`. No schema changes required. The engine dispatch switch inspects `Kind` to route between future dungeon delegation (kind: "regular") and SPD handling (kind: "spd").

**Rejected alternative:** A dedicated `SinglePlayerDutyStep` type. Rejected because the schema already models this via the `Kind` discriminator and the structural validator already enforces kind-specific field requirements.

### SPD2: New EngineAction.EnterSinglePlayerDuty

```csharp
public sealed record EnterSinglePlayerDuty(Step? Origin = null) : EngineAction;
```

A distinct action type is needed because the EngineHost dispatch arm has materially different behavior from `Interact`:
- Calls `IDutyRunner.StartDuty` (BossMod AI enablement)
- Does NOT call `_interactor.InteractWith` (the NPC interaction that triggers entry is handled by a preceding TalkStep/InteractObjectStep; the SPD step's job is only BossMod enablement + wait)
- Needs cleanup tracking (`_activeSpdStepId`) for `StopDuty` on step completion

**Rejected alternative:** Reuse `EngineAction.Interact`. Rejected because the EngineHost needs to know this is an SPD entry (for BossMod start/stop lifecycle), and piggybacking on Interact conflates two different dispatch concerns.

**What breaks if violated:** If someone emits an Interact instead, BossMod AI is never enabled, and the player sits inside the duty with no combat automation.

### SPD3: IDutyRunner adapter -- new focused interface

```csharp
namespace QuestForge.Adapters.Duty;

public interface IDutyRunner
{
    /// <summary>
    /// Enable BossMod AI for a Single Player Duty.
    /// Creates a "QuestForge" preset, sets it active, enables quest battles.
    /// </summary>
    Task<Result<bool>> StartDuty(CancellationToken ct);

    /// <summary>
    /// Disable BossMod AI after duty completion or engine stop.
    /// Clears preset, disables quest battles, disables AI.
    /// Idempotent -- safe to call when not started.
    /// </summary>
    Task<Result<bool>> StopDuty(CancellationToken ct);

    /// <summary>
    /// Check whether BossMod is installed and its IPC is responsive.
    /// </summary>
    Task<Result<bool>> IsBossModAvailable(CancellationToken ct);
}
```

**Placement:** `QuestForge.Adapters/Duty/IDutyRunner.cs`. New directory mirrors the `Combat/`, `Gear/`, `Items/` convention.

**Why not extend ICombat:** ICombat is for WrathCombo-style rotation management (SetTarget, StartRotation, StopRotation). BossMod SPD delegation is a completely different IPC surface (preset management, quest battle config). Combining them violates SRP and makes testing harder.

**Testability:** `FakeDutyRunner` scripts responses for each method. Tests verify the engine correctly gates on `IsBossModAvailable` and emits `EnterSinglePlayerDuty` only when BossMod is present.

### SPD4: Optional IDutyRunner on QuestEngine constructor

```csharp
// In QuestEngine constructor:
private readonly IDutyRunner? _dutyRunner;

public QuestEngine(
    // ... existing params ...,
    IDutyRunner? dutyRunner = null)
{
    _dutyRunner = dutyRunner;
    // ...
}
```

When `_dutyRunner` is null and the engine encounters a `DutyStep(kind: "spd")`, it emits `AwaitUser("IDutyRunner not configured")`. This matches the pattern of `_actionExecutor`, `_emoteExecutor`, etc.

**What breaks if violated:** Old tests that don't exercise SPDs would fail if IDutyRunner were required.

### SPD5: ResolveSpd pre-arm pattern

```csharp
private async Task<EngineAction> ResolveSpd(DutyStep step, CancellationToken ct)
{
    if (_dutyRunner is null)
        return new EngineAction.AwaitUser(
            "DutyStep(kind:spd) dispatched but no IDutyRunner wired — host must supply one");

    var availResult = await _dutyRunner.IsBossModAvailable(ct);
    if (availResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            "BossMod required for Single Player Duties. Complete manually or install BossMod.");

    // No casting/combat guards -- the NPC interaction that triggers entry
    // is a preceding step (talk/interact-object). This step only starts BossMod.
    return new EngineAction.EnterSinglePlayerDuty(Origin: step);
}
```

**Key invariant:** `_lastResolvedStep` is NOT set in `ResolveSpd` (matches Teleport/Purchase/UseAction/UseEmote precedent).

### SPD6: Addon handling lives in EngineHost, not in the engine

The engine is a pure-C# tick loop. Addon interactions (SelectYesno for entry, DifficultySelectYesNo for retry) are Dalamud-specific and belong in EngineHost's dispatch arm or tick-level hooks.

**SelectYesno for SPD entry:** Already handled by `SelectYesnoResponder`. When the engine is running (`IsRunActive`), the responder clicks Yes on any SelectYesno popup. The SPD entry confirmation is just another SelectYesno. No new code needed for this addon.

**DifficultySelectYesNo for retry:** New addon handler in EngineHost. This addon only appears after failing an SPD. The handler:
1. Checks if `DifficultySelectYesNo` addon is visible
2. Selects the radio button matching `PluginConfig.PreferredSpdDifficulty`
3. Clicks Proceed (NodeID 13)

```csharp
// In EngineHost — called from DispatchAction's EnterSinglePlayerDuty arm
// and from the Wait arm (while inside duty, engine ticks Wait)
private unsafe void TryHandleDifficultySelect()
{
    var addonPtr = _services.GameGui.GetAddonByName("DifficultySelectYesNo");
    if (addonPtr.IsNull || !addonPtr.IsReady) return;

    var addon = (AtkUnitBase*)addonPtr.Address;
    if (addon == null || !addon->IsVisible) return;

    // Select difficulty radio button.
    // NodeIDs 5=Normal, 6=Easy, 7=VeryEasy
    int radioIndex = _config.PreferredSpdDifficulty switch
    {
        SpdDifficulty.Easy => 1,
        SpdDifficulty.VeryEasy => 2,
        _ => 0 // Normal
    };
    addon->FireCallbackInt(radioIndex); // select radio
    addon->FireCallbackInt(3);          // click Proceed
    // NOTE: exact FireCallback signatures must be verified in-game.
    // The radio+proceed may be a single compound callback or two separate calls.
    // The builder must research the actual AtkValue layout via /qf debug addon DifficultySelectYesNo.
}
```

**Rejected alternative:** Handle addons in the engine via adapter interfaces (e.g., `IAddonHandler.TryHandleDifficultySelect`). Rejected because addon handling is inherently Dalamud-specific (AtkUnitBase pointers, FireCallback) and cannot be tested without a game client. The engine's concern is the decision (emit `EnterSinglePlayerDuty`), not the GUI mechanics.

### SPD7: StopDuty cleanup strategy -- step-change tracking in EngineHost

EngineHost tracks `_activeSpdStepId`. When the dispatched action changes away from `EnterSinglePlayerDuty` (meaning the step's Expect was satisfied and the engine advanced), EngineHost calls `_dutyRunner.StopDuty(ct)`.

```csharp
private string? _activeSpdStepId;

// In DispatchAction, before the switch:
if (_activeSpdStepId is not null && action is not EngineAction.EnterSinglePlayerDuty
    && action is not EngineAction.Wait)
{
    await _dutyRunner.StopDuty(ct);
    _activeSpdStepId = null;
}

// In the EnterSinglePlayerDuty dispatch arm:
case EngineAction.EnterSinglePlayerDuty espd:
    _activeSpdStepId = espd.Origin?.Id;
    // ... start duty ...
    break;
```

Also call `StopDuty` in `EndRun()` for safety (handles manual `/qf stop` during an SPD).

**Rejected alternative A:** Engine emits a cleanup action (`StopSinglePlayerDuty`). Rejected because it adds a new action type for pure lifecycle management that only the host cares about. The engine doesn't know or care about BossMod.

**Rejected alternative B:** Call `StopDuty` on every step transition. Rejected because `StopDuty` disables BossMod AI, and if called spuriously during a Wait tick inside the duty, it would disable combat mid-fight.

**Why Wait is exempt:** While inside the duty, the engine emits Wait (because Expect is not yet satisfied). StopDuty must NOT fire on Wait ticks. Only on genuine step advancement (non-Wait, non-EnterSinglePlayerDuty actions).

### SPD8: Engine flow -- the complete lifecycle

1. Engine cursor reaches `DutyStep(kind: "spd")` with `Expect = "questSequence(N) >= M"`
2. `ResolveSpd` pre-arm:
   - Checks `_dutyRunner is null` -> AwaitUser
   - Checks `IsBossModAvailable` -> AwaitUser if false
   - Emits `EnterSinglePlayerDuty(Origin: step)`
3. EngineHost `DispatchAction` receives `EnterSinglePlayerDuty`:
   - Sets `_activeSpdStepId = step.Id`
   - Calls `_dutyRunner.StartDuty(ct)` (enables BossMod AI)
   - Stops navigation if active
   - Calls `TryCutsceneSkipConfirm()` (entry cutscene)
   - Calls `_interactor.AdvanceDialogue(ct)` (flush any pending dialogue)
4. Engine ticks. Expect (`questSequence(N) >= M`) is false -> cursor stays on same step -> emits `EnterSinglePlayerDuty` again
5. EngineHost dispatch: `_activeSpdStepId` is already set -> no re-start needed (StartDuty is idempotent, but we can guard with the tracking flag)
6. Meanwhile, per-tick addon handlers run:
   - `SelectYesnoResponder` handles entry confirmation (already existing)
   - `TryHandleDifficultySelect()` handles retry difficulty selection (new)
7. BossMod handles combat inside the duty
8. On success: quest sequence advances -> Expect true -> step confirmed -> engine advances to next step -> EngineHost sees non-SPD action -> calls `_dutyRunner.StopDuty(ct)` -> `_activeSpdStepId = null`
9. On failure: player ejected to overworld -> Expect still false -> engine re-dispatches `EnterSinglePlayerDuty` -> stateless retry naturally occurs (engine walks cursor back to same step)
10. On retry: preceding TalkStep/InteractObjectStep navigates to entry NPC -> engine re-enters SPD step -> `DifficultySelectYesNo` appears -> handler selects difficulty -> back to step 3

### SPD9: PluginConfig.PreferredSpdDifficulty

```csharp
// In PluginConfig.cs:
public SpdDifficulty PreferredSpdDifficulty { get; set; } = SpdDifficulty.Normal;

// In QuestForge.Plugin (or QuestForge.Adapters.Types):
[JsonConverter(typeof(JsonStringEnumConverter<SpdDifficulty>))]
public enum SpdDifficulty
{
    Normal = 0,
    Easy = 1,
    VeryEasy = 2
}
```

**Placement:** The `SpdDifficulty` enum lives in `QuestForge.Plugin` (not Adapters) because it is purely a UI/config concern. The engine never sees it. Only `EngineHost.TryHandleDifficultySelect` reads it from config.

**Rejected alternative:** Put the enum in `QuestForge.Adapters.Types`. Rejected because the adapter interface `IDutyRunner` does not need to know about difficulty -- that's an addon/UI concern, not an adapter concern. The adapter just starts/stops BossMod AI.

### SPD10: DalamudDutyRunner -- BossMod IPC wrapper

```csharp
namespace QuestForge.Adapters.Dalamud.Duty;

public sealed class DalamudDutyRunner : IDutyRunner
{
    private readonly PluginServices _services;
    private bool _started;

    // Per-call IPC subscribers (same pattern as DalamudBestGearEquipper)
    private ICallGateSubscriber<string, string?> GetPreset
        => _services.PluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
    private ICallGateSubscriber<string, bool, bool> CreatePreset
        => _services.PluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
    private ICallGateSubscriber<string, bool> SetPresetActive
        => _services.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
    private ICallGateSubscriber<bool> ClearPresetActive
        => _services.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");

    public Task<Result<bool>> StartDuty(CancellationToken ct)
    {
        // 1. Enable quest battles via chat command
        _services.CommandManager.ProcessCommand("/vbm cfg ZoneModuleConfig EnableQuestBattles true");
        _services.CommandManager.ProcessCommand("/vbm cfg Autorotation ClearPresetOnCombatEnd false");
        // 2. Create or overwrite the QuestForge preset, then set it active
        //    Preset content: embedded resource (same approach as Questionable)
        if (GetPreset.InvokeFunc("QuestForge") == null)
            CreatePreset.InvokeFunc(QuestBattlePresetContent, true);
        SetPresetActive.InvokeFunc("QuestForge");
        _started = true;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        if (!_started) return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        _services.CommandManager.ProcessCommand("/vbmai off");
        _services.CommandManager.ProcessCommand("/vbm cfg ZoneModuleConfig EnableQuestBattles false");
        ClearPresetActive.InvokeFunc();
        _started = false;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> IsBossModAvailable(CancellationToken ct)
    {
        try
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Success(GetPreset.HasFunction));
        }
        catch
        {
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(false));
        }
    }
}
```

**Preset content:** The QuestBattle preset JSON will be an embedded resource in the `QuestForge.Adapters.Dalamud` assembly. Its content is derived from observing BossMod's Quest Battle preset structure (NOT copied from Questionable's embedded preset -- clean-room implementation). The builder must create a minimal QuestBattle preset via BossMod's in-game UI, export it, and embed it.

**No recording proxy needed:** `DalamudDutyRunner` is write-only (StartDuty/StopDuty are fire-and-forget). The `action.submitted`/`action.completed` events from `EngineHost.DispatchAction` already capture the write. No `RecordingDutyRunner` wrapper needed (same reasoning as `DalamudActionExecutor`, `DalamudEmoteExecutor`, etc.).

### SPD11: Lazy dismount exemption -- EnterSinglePlayerDuty is NOT exempt

`EnterSinglePlayerDuty` IS subject to lazy dismount (same as UseAction, UseEmote, Interact). The player should be dismounted before entering a duty. Teleport is exempt because the game auto-dismounts on arrival; SPD entry does not auto-dismount.

In `EngineHost.DispatchAction`:
```csharp
// Current exemption:
if (_lastDispatchedActionWasNavigate && action is not EngineAction.Navigate
    and not EngineAction.Teleport
    and not EngineAction.EquipGear and not EngineAction.EquipBestGear
    and not EngineAction.RegisterGearset)
```
No change needed -- `EnterSinglePlayerDuty` is already NOT in the exemption list (it's a new type that doesn't match any of the `is not` clauses).

### SPD12: Retry is naturally stateless -- no retry counter in this phase

The engine's cursor-walk architecture handles retry naturally:
- SPD fails -> player ejected to overworld -> Expect still false -> engine re-dispatches
- The preceding steps (navigate to NPC, interact) re-execute naturally
- `SelectYesnoResponder` clicks through the re-entry confirmation
- `TryHandleDifficultySelect` handles difficulty selection on retry

`MaxDutyRetries` from DESIGN.md is a future concern. This phase does NOT implement a retry counter. The engine retries indefinitely (which is the correct default -- BossMod can usually clear SPDs, and the player can manually stop via `/qf stop`).

**What breaks if violated:** If someone adds a retry counter prematurely, SPDs that BossMod can handle but takes 2-3 attempts would be abandoned unnecessarily.

### SPD13: DutyStep(kind: "spd") does NOT use Trigger or EntryNpc fields

The existing `DutyStep` schema has `Trigger` and `EntryNpc` fields, but for SPDs these are NOT used. The entry mechanism is a preceding TalkStep or InteractObjectStep that talks to the NPC and triggers the SPD. The DutyStep's only job is to enable BossMod and wait for Expect.

The structural validator already handles this: `kind: "spd"` does NOT require `dutyId`, `entryNpc`, or `trigger` (validated by `structural/duty-missing-required-field` which only fires for `kind: "regular"`). The existing rule `structural/duty-invalid-field-for-kind` rejects `dutyId` on SPD steps.

### SPD14: Authoring inference -- explicitly deferred

SPD steps are NOT auto-inferred. SPDs are quest-specific and the quest author knows when one occurs. Detection would require zone-change-to-duty-zone heuristics that are complex and unreliable. The author manually adds a DutyStep(kind: "spd") to the quest draft.

This is acceptable because:
- SPDs are relatively rare (one per quest chapter at most)
- The step is simple to author (just `type: "duty"`, `kind: "spd"`, `expect: "questSequence(N) >= M"`)
- Detection signals (BoundByDuty + ContentFinderConditionId) are complex to poll reliably

---

## FakeDutyRunner

```csharp
namespace QuestForge.Adapters.Fakes.Duty;

public sealed class FakeDutyRunner : IDutyRunner
{
    public bool BossModAvailable { get; set; } = true;
    public int StartDutyCallCount { get; private set; }
    public int StopDutyCallCount { get; private set; }
    public int IsBossModAvailableCallCount { get; private set; }

    /// <summary>
    /// When non-null, StartDuty returns this failure reason instead of success.
    /// </summary>
    public string? ScriptedStartFailure { get; set; }

    public void Reset()
    {
        StartDutyCallCount = 0;
        StopDutyCallCount = 0;
        IsBossModAvailableCallCount = 0;
        ScriptedStartFailure = null;
    }

    public Task<Result<bool>> StartDuty(CancellationToken ct)
    {
        StartDutyCallCount++;
        if (ScriptedStartFailure is not null)
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("start-duty-failed", ScriptedStartFailure));
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        StopDutyCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> IsBossModAvailable(CancellationToken ct)
    {
        IsBossModAvailableCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(BossModAvailable));
    }
}
```

---

## EngineTestHarness changes

```csharp
// New property:
public FakeDutyRunner DutyRunner { get; } = new FakeDutyRunner();

// In constructor, pass to QuestEngine:
var inner = engineRef = new QuestEngine(
    // ... existing params ...,
    dutyRunner: DutyRunner);

// In RunToCompletion, new case:
case EngineAction.EnterSinglePlayerDuty espd:
    actions.Add(action);
    EmitActionSubmitted("EnterSinglePlayerDuty",
        JsonSerializer.SerializeToElement(new { origin = espd.Origin?.Id }, _jsonOpts));
    var spdResult = await DutyRunner.StartDuty(ct);
    EmitActionCompleted("EnterSinglePlayerDuty",
        spdResult.IsSuccess ? "Started" : "Failed");
    break;
```

---

## Validation rules -- no new rules needed

The existing `StructuralValidator` already handles `DutyStep`:
- `structural/duty-missing-required-field`: `kind: "regular"` requires `dutyId` and `entryNpc`
- `structural/duty-invalid-field-for-kind`: `kind: "spd"` with `dutyId` set is rejected
- No DraftValidator rules needed because DutyStep has no zero-value fields that need checking (no NpcId, no ActionId, etc.)

The W1 suppression guard for missing Expect does NOT need updating. DutyStep is not in the suppression list (`is not UseActionStep and not UseEmoteStep and not SayChatMessageStep and not UseItemStep and not EquipGearForQuestStep and not ChangeJobStep`), so W1 correctly warns if a DutyStep lacks an Expect. This is appropriate -- SPD steps without Expect would spin-loop.

---

## Given-When-Then specifications

### S1: Happy path -- SPD completes successfully

**Given:** A quest with sequence 0 containing:
- `travel-to-npc` (TravelStep, Expect: `questSequence(Q) >= 1`)
- `talk-to-npc` (TalkStep, Expect: `questSequence(Q) >= 2`)
- `enter-spd` (DutyStep, kind: "spd", Expect: `questSequence(Q) >= 3`)
- `talk-after-spd` (TalkStep, Expect: `questSequence(Q) >= 4`)

Player starts at zone 132, position (10,0,20). Quest Q at sequence 0.
FakeDutyRunner.BossModAvailable = true.
Wire callbacks: on Interact for NPC -> advance questSequence.
On `EnterSinglePlayerDuty` dispatch: after 1 tick, advance questSequence to 3 (simulating duty completion).

**When:** RunToCompletion(maxTicks: 20)

**Then:**
- Actions include Navigate, Interact (talk-to-npc), EnterSinglePlayerDuty, Interact (talk-after-spd)
- DutyRunner.StartDutyCallCount == 1
- Engine reaches Done

### S2: BossMod not available -- AwaitUser

**Given:** Same quest as S1. FakeDutyRunner.BossModAvailable = false.

**When:** Engine ticks until reaching `enter-spd` step.

**Then:** Engine returns `AwaitUser` with message containing "BossMod required for Single Player Duties".

### S3: IDutyRunner not wired -- AwaitUser

**Given:** Same quest as S1. QuestEngine constructed WITHOUT passing dutyRunner (null).

**When:** Engine ticks until reaching `enter-spd` step.

**Then:** Engine returns `AwaitUser` with message containing "IDutyRunner not configured".

### S4: SPD step with no Expect -- fires every tick (stateless)

**Given:** A quest with a DutyStep(kind: "spd") with NO Expect predicate.
FakeDutyRunner.BossModAvailable = true.

**When:** Engine ticks 5 times on this step.

**Then:** All 5 ticks return `EnterSinglePlayerDuty`. The engine never self-confirms (no Expect to satisfy). This is correct behavior -- the step relies on external sequence advancement.

### S5: Retry -- Expect stays false after EnterSinglePlayerDuty

**Given:** Same quest as S1. Wire callbacks so the first `EnterSinglePlayerDuty` dispatch does NOT advance questSequence (simulating duty failure). After 3 more ticks of EnterSinglePlayerDuty, advance questSequence (simulating success on retry).

**When:** RunToCompletion(maxTicks: 20)

**Then:**
- Multiple `EnterSinglePlayerDuty` actions emitted (engine re-dispatches on each tick)
- StartDutyCallCount >= 2 (idempotent calls)
- Engine eventually reaches Done after questSequence advances

### S6: SkipIf on DutyStep -- step skipped when predicate is true

**Given:** Quest with a DutyStep(kind: "spd") with `skipIf: "questSequence(Q) >= 3"` and `expect: "questSequence(Q) >= 3"`. Quest Q already at sequence 3.

**When:** Engine ticks.

**Then:** The DutyStep is confirmed immediately (Expect is true). No `EnterSinglePlayerDuty` emitted. DutyRunner.StartDutyCallCount == 0.

### S7: DutyStep kind "regular" -- unsupported, throws/awaits

**Given:** Quest with a DutyStep(kind: "regular").

**When:** Engine ticks until reaching the duty step.

**Then:** Engine returns `AwaitUser` or throws `NotSupportedException` (consistent with how unimplemented step types are handled). The exact behavior should match the `_ => throw new NotSupportedException(...)` fallback in `ResolveActionForStep`.

### S8: EnterSinglePlayerDuty is subject to lazy dismount

**Given:** Quest with a TravelStep followed by a DutyStep(kind: "spd"). Player is mounted after Navigate completes (MountState = Flying). Navigation has stopped (IsNavigating = false).

**When:** Engine transitions from Navigate to EnterSinglePlayerDuty.

**Then:** In the test harness (HarnessEngine), the lazy dismount fires before returning the action. Mount.DismountCallCount >= 1.

### S9: Interleaved Wait ticks -- engine emits Wait while inside duty

**Given:** Quest with a DutyStep(kind: "spd") with Expect. Engine has emitted EnterSinglePlayerDuty. Quest sequence has NOT advanced. All other steps in the sequence are confirmed.

**When:** The DutyStep's Expect is false but the step is not self-confirming.

**Then:** Engine emits `EnterSinglePlayerDuty` on each tick (the step cursor stays on the DutyStep because Expect is not satisfied). This is the expected behavior -- the engine re-dispatches the same action until Expect becomes true.

Note: The engine does NOT emit Wait for the DutyStep (that would require a special case). The standard cursor-walk logic applies: Expect is false -> step is not confirmed -> ResolveSpd pre-arm fires -> EnterSinglePlayerDuty is emitted.

### S10: Multiple SPD steps in same quest

**Given:** Quest with two DutyStep(kind: "spd") steps in different sequences. FakeDutyRunner.BossModAvailable = true.

**When:** RunToCompletion. Wire callbacks to advance sequence after each SPD.

**Then:** DutyRunner.StartDutyCallCount == 2. Both SPDs complete. Engine reaches Done.

---

## Tooling catch-up

### TraceConstants (questforge-tools)

Add:
```csharp
internal const string ActionEnterSinglePlayerDuty = "entersingleplayerduty";
```

No behavior change (`IsTerminalAction` only uses `done`/`awaituser`). Documents what `DecisionEvent.ActionType.ToLowerInvariant()` emits for the new action.

### CapabilityInferrer (questforge-tools)

`step:duty` is already mapped via `[typeof(DutyStep)] = "step:duty"`. No change needed. The `step:spd` capability in FilenameLookup/DistinguishingCapPriority is a fixture-naming concern, not a capability tag -- it is NOT emitted by CapabilityInferrer. If we want `step:spd` as a capability, CapabilityInferrer would need to inspect `DutyStep.Kind`, which is a future enhancement.

### FilenameLookup / DistinguishingCapPriority (questforge-tools)

Already have entries for `step:spd` and `step:duty`. No changes needed.

### FIXTURES.md (questforge repo)

Add row to `actionType` canonical strings table:
```
| `entersingleplayerduty` | `EngineAction.EnterSinglePlayerDuty` | BossMod AI start for SPD |
```

---

## Implementation order

### Phase A -- Adapter interface + fake (0.5 day)
1. Create `QuestForge.Adapters/Duty/IDutyRunner.cs`
2. Create `QuestForge.Adapters.Fakes/Duty/FakeDutyRunner.cs`
3. Build passes.

**Done before Phase B.**

### Phase B -- Engine changes (1 day)
1. Add `EngineAction.EnterSinglePlayerDuty` to `EngineAction.cs`
2. Add `IDutyRunner? _dutyRunner` as optional ctor param on `QuestEngine`
3. Add `ResolveSpd` async pre-arm in `QuestEngine.cs`
4. Wire `DutyStep(kind: "spd")` into the step-dispatch switch (between existing pre-arm checks)
5. Add `case DutyStep { Kind: "regular" }` -> throw NotSupportedException (guard)
6. Update `EngineTestHarness`: add `FakeDutyRunner`, pass to ctor, add dispatch arm
7. Update `HarnessEngine`: no changes needed (lazy dismount already applies to unknown action types)

**Done before Phase C.**

### Phase C -- Tests (1 day)
1. Write `SinglePlayerDutyTests.cs` with scenarios S1-S10
2. All engine tests pass (`dotnet test QuestForge.Engine.Tests`)

**Done before Phase D.**

### Phase D -- Dalamud impl + EngineHost (1.5 days)
1. Create `QuestForge.Adapters.Dalamud/Duty/DalamudDutyRunner.cs`
2. Create embedded QuestBattle preset resource
3. Add `SpdDifficulty` enum to Plugin project
4. Add `PreferredSpdDifficulty` to `PluginConfig`
5. Add `_dutyRunner` field + construction to `EngineHost`
6. Pass to `QuestEngine` in `BeginRun`
7. Add `DispatchAction` arm for `EnterSinglePlayerDuty`
8. Add `TryHandleDifficultySelect()` to EngineHost tick loop
9. Add `_activeSpdStepId` tracking + StopDuty cleanup in dispatch pre-switch
10. Add StopDuty call in `EndRun()`
11. Add `IDutyRunner DebugDutyRunner => _dutyRunner` accessor
12. Update FIXTURES.md actionType table

**Done before Phase E.**

### Phase E -- Tooling catch-up (0.5 day)
1. Add `ActionEnterSinglePlayerDuty` to `TraceConstants.cs`
2. Add tests for the new constant in `QuestForge.Tools.Trace.Tests`
3. Verify CapabilityInferrer / FilenameLookup already handle step:duty and step:spd

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests` passes with all S1-S10 scenarios green
2. `dotnet test QuestForge.Adapters.Tests` passes (no adapter test regressions)
3. A quest JSON with `{ "type": "duty", "kind": "spd", "expect": "questSequence(65000) >= 5" }` round-trips through serialization
4. `FakeDutyRunner.StartDutyCallCount` is asserted in S1 and S5
5. `EngineAction.EnterSinglePlayerDuty` appears in trace events emitted during S1
6. BossMod unavailable -> AwaitUser with actionable message (S2)
7. IDutyRunner null -> AwaitUser with actionable message (S3)
8. DutyStep(kind: "regular") -> NotSupportedException (S7)
9. `PluginConfig.PreferredSpdDifficulty` serializes to/from JSON
10. `TraceConstants.ActionEnterSinglePlayerDuty == "entersingleplayerduty"`
11. FIXTURES.md contains the new actionType row

---

## What this plan does NOT include

- **DutyStep(kind: "regular") implementation** -- dungeon/trial delegation via AutoDuty is a separate issue (#7)
- **MaxDutyRetries counter** -- retry limiting is a future enhancement; infinite stateless retry is the correct default for now
- **ContentFinderConditionId polling** -- the engine does not need to monitor duty state; Expect handles it
- **BoundByDuty condition flag monitoring** -- same as above; not needed
- **Internal SPD AI** -- QuestForge does not implement its own combat AI; BossMod is the delegate
- **Authoring inference for SPD steps** -- explicitly deferred (SPD14); SPD steps are manually authored
- **Death recovery routing** -- the engine's existing stateless retry handles SPD death naturally; context-routed death recovery (from DESIGN.md) is a separate future concern
- **DifficultySelectYesNo AtkValue research** -- the builder must research the exact FireCallback signature in-game via `/qf debug addon DifficultySelectYesNo` before implementing TryHandleDifficultySelect
- **QuestBattle preset content** -- the builder must create or export a minimal BossMod preset in-game; this plan specifies the interface, not the preset JSON content
- **step:spd as a distinct CapabilityInferrer tag** -- currently step:duty covers all DutyStep variants; splitting by Kind is a future enhancement

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in S1-S10.
- Happy paths: 3 scenarios (S1, S6, S10)
- Edge cases: 4 scenarios (S4, S5, S8, S9)
- Error cases: 3 scenarios (S2, S3, S7)
- Expected total: ~10-12 tests in QuestForge.Engine.Tests/Engine/SinglePlayerDutyTests.cs
