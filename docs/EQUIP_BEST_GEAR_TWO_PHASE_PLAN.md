# EquipBestGear Two-Phase Dispatch Fix

**Status:** ready to implement
**Input docs:** `QuestForge.Adapters.Dalamud/Gear/DalamudBestGearEquipper.cs`, `QuestForge.Engine/QuestEngine.cs` (EquipBestGearStep dispatch arm), Questionable `EquipRecommended.cs` (reference only)
**Output:** EquipBestGear steps that use the vanilla `RecommendEquipModule` path actually change gear in-game, instead of silently no-oping.
**Phase dependencies:** None -- this is a bugfix to existing Phase 11 functionality.

---

## Root Cause

`DalamudBestGearEquipper.VanillaEquip()` calls `SetupForClassJob` and `EquipRecommendedGear` in the same synchronous method. The game's `RecommendEquipModule` sets `IsUpdating = true` after `SetupForClassJob` and needs at least one game frame to populate its recommendation list. Calling `EquipRecommendedGear` while `IsUpdating` is still true operates on an empty list, so nothing changes. The adapter returns `EquipOutcome.Equipped` (a lie), the engine's fire-once pattern self-confirms, and the step advances without any gear changing.

---

## Architectural Decisions

### EBF1 -- State machine lives in the Dalamud adapter, not the engine

**Decision:** `DalamudBestGearEquipper` tracks its own multi-tick state internally. The adapter interface (`IBestGearEquipper`) gains a new return value `EquipOutcome.Pending` to signal "I started the process but it is not done yet." The engine keeps dispatching `EquipBestGear` on subsequent ticks; the adapter returns `Pending` while `IsUpdating` is true, then actually calls `EquipRecommendedGear` once ready, and returns `Equipped`.

**Alternatives rejected:**
- **(A) Engine manages multi-tick flow:** Violates the Dalamud-free invariant -- the engine would need to know about `IsUpdating`, which is a game-side concept.
- **(B) EngineHost manages multi-tick flow in the dispatch arm:** Puts state-machine complexity in the wrong layer. EngineHost is a thin dispatch shell; per-adapter sequencing logic belongs in the adapter. Also creates a testing gap: the multi-tick behavior would only be exercisable in-game, not in unit tests against the fake.

**What breaks if violated:** If the state machine moves to the engine, `QuestForge.Engine` gains an implicit dependency on the game's frame timing model, making the engine untestable in CI without faking the timing loop.

**Testability:** `FakeBestGearEquipper` can script a sequence of `Pending` then `Equipped` returns, letting engine tests verify the retry loop purely in-process.

### EBF2 -- Add `Pending` to `EquipOutcome`

**Decision:** Add `Pending` to the existing `EquipOutcome` enum:

```csharp
public enum EquipOutcome { Equipped, NoChange, Pending, InCombat, InInstance, ItemNotFound, Failed }
```

`Pending` means "the adapter has started the equip process but it is not complete yet; dispatch again next tick." This is distinct from `NoChange` (which means "nothing needed to change") and `Failed` (which means "something went wrong").

**Alternatives rejected:**
- **(A) Separate `IBestGearEquipper.IsReady()` polling method:** Adds a second method to the interface that must be called in a specific order relative to `EquipBestGear`. The single-method approach with `Pending` return is simpler and self-documenting.
- **(B) Change the return type to a discriminated union specific to best-gear:** Over-engineered for a single additional state. `Pending` is a natural addition to the existing enum and may be useful for `EquipItem` in the future.

**What breaks if violated:** If `Pending` is not added, the engine has no way to distinguish "adapter still working" from "nothing to do" and either self-confirms prematurely (`NoChange`) or treats ongoing work as failure (`Failed`).

### EBF3 -- Revert fire-once; use `Pending`-aware dispatch loop instead

**Decision:** Remove EquipBestGearStep from the `_fireOnceDispatchedIds` pattern entirely. Instead, the dispatch arm works like other steps with implicit postconditions:

1. Engine calls `ResolveEquipBestGear` every tick (unchanged -- returns `Wait` if casting/combat, `EquipBestGear` otherwise).
2. EngineHost dispatches the action to the adapter.
3. Adapter returns `Pending` (still working) or `Equipped`/`NoChange` (done).
4. **New:** After `EquipBestGear` dispatch, if the adapter result is `Equipped` or `NoChange`, the engine self-confirms the step. If `Pending` or `Failed`, the engine does NOT self-confirm and will re-dispatch next tick.

**Implementation detail -- where the outcome flows:** The adapter outcome is not visible to `QuestEngine.Tick()` today because the dispatch happens in EngineHost (or the test harness). The engine only sees the `EngineAction` it emitted. Two options for the self-confirm signal:

- **(Chosen) EngineHost calls a new `QuestEngine.ConfirmStep(stepId)` method after successful dispatch.** This is the same pattern as navigate completion: the host observes the outcome and tells the engine. The engine's EquipBestGearStep arm does NOT self-confirm; it emits `EquipBestGear` every tick until `ConfirmStep` is called.
- *(Rejected) Adapter outcome embedded in a callback or engine state:* Would require threading the adapter result back through the engine action loop, coupling the engine to adapter return types.

Wait -- `ConfirmStep` would be a new concept and potentially over-engineered. Let me reconsider.

**Revised approach (simpler):** Keep the fire-once pattern but move the "dispatched" flag to AFTER the host confirms success. The engine already has `_fireOnceDispatchedIds`. Currently it adds to this set immediately after resolving the action (before dispatch). Instead:

- Engine emits `EquipBestGear` every tick (does NOT add to `_fireOnceDispatchedIds` in the resolve arm).
- EngineHost dispatches to the adapter.
- If adapter returns `Equipped` or `NoChange`, the harness/host calls back to confirm.

This still requires a callback. Too complex.

**Final approach (simplest, chosen):** The fire-once pattern stays but the adapter is now multi-tick internally. The key insight: the engine dispatches `EquipBestGear` once, the host calls the adapter, and the adapter blocks (within reason) until the equip is done or times out. The adapter is `async` and can await across frames.

But wait -- the adapter runs on the game's framework thread. `await` does not span game frames in Dalamud; the adapter call completes within a single framework update. So the adapter cannot internally await multiple frames.

**Actual final approach (chosen after considering all constraints):**

The adapter cannot span frames. The host CAN span frames because it controls the dispatch loop. The solution:

1. **Adapter interface unchanged** except adding `Pending` to `EquipOutcome`.
2. **`DalamudBestGearEquipper` becomes a two-phase state machine:**
   - First call: `SetupForClassJob` + return `Pending`.
   - Subsequent calls: check `IsUpdating`; if still true, return `Pending`; if false, call `EquipRecommendedGear` + return `Equipped`. Reset internal state.
3. **Engine removes EquipBestGearStep from fire-once.** Instead, the dispatch arm re-emits `EquipBestGear` every tick until the adapter returns something other than `Pending`. To detect "not Pending", the engine needs the adapter result. But the engine does not call the adapter directly.

Therefore the simplest correct approach is:

1. **Engine removes fire-once for EquipBestGearStep.** The engine emits `EquipBestGear` every tick (like a stateless retry) as long as the step has no `Expect` or its `Expect` is not yet satisfied.
2. **Adapter becomes a two-phase state machine** (first call = setup, subsequent = poll/equip).
3. **EngineHost and test harness simply dispatch** `EquipBestGear` to the adapter every tick. The adapter handles idempotency.
4. **Self-confirm for no-Expect steps:** When `Expect` is null, the engine needs some signal that the step is done. The adapter returning `Equipped` or `NoChange` is that signal, but the engine does not see it. **Solution: add an `EquipOutcome` property to `EngineAction.EquipBestGear` that the host populates after dispatch.** No -- the action is emitted before dispatch.

**OK, final final design (I will stop deliberating):**

The engine uses a **two-phase handshake** modeled on how EngineHost already works for other multi-tick operations:

1. **Remove EquipBestGearStep from `_fireOnceDispatchedIds`.**
2. **Add a new `_pendingEquipBestGearIds` HashSet** in QuestEngine.
3. Engine dispatch arm for EquipBestGearStep:
   - If step.Id is in `_pendingEquipBestGearIds`: emit `EngineAction.EquipBestGear` (re-dispatch, adapter will poll).
   - If step.Id is NOT in `_pendingEquipBestGearIds`: emit `EngineAction.EquipBestGear` and add to `_pendingEquipBestGearIds`.
4. **New: `QuestEngine.NotifyEquipBestGearComplete(string stepId)`** -- called by EngineHost/harness after adapter returns `Equipped` or `NoChange`. Removes from `_pendingEquipBestGearIds` and adds to `_confirmedStepIds`.
5. If `Expect` is authored, the normal expect-check handles completion (and `_pendingEquipBestGearIds` gets cleared on sequence change anyway).
6. If `Expect` is null, `NotifyEquipBestGearComplete` is what self-confirms the step.

Wait, this is over-engineered. The step already emits every tick because we are removing fire-once. The host calls the adapter every tick. The adapter internally tracks its state. The only missing piece is: when does the engine stop emitting `EquipBestGear` for a step with no `Expect`?

**Truly final design:**

Keep it simple. Two changes:

1. **Adapter becomes stateful (two-phase).** `DalamudBestGearEquipper` tracks whether setup has been called. First call = `SetupForClassJob` + return `Pending`. Subsequent calls = poll `IsUpdating`; if done, `EquipRecommendedGear` + return `Equipped`; if still updating, return `Pending`. Reset state after returning `Equipped`/`NoChange`/`Failed`.

2. **Engine: replace fire-once with outcome-aware confirm.** Add `QuestEngine.NotifyStepOutcome(string stepId, bool success)`. EngineHost calls this after dispatching `EquipBestGear`:
   - Adapter returns `Equipped`/`NoChange` --> `NotifyStepOutcome(stepId, true)` --> engine adds to `_confirmedStepIds` (for no-Expect steps) or lets expect-check handle it.
   - Adapter returns `Pending` --> no notification, engine re-dispatches next tick.
   - Adapter returns `Failed` --> `NotifyStepOutcome(stepId, false)` --> engine does not confirm, re-dispatches next tick.

   The engine's EquipBestGearStep arm emits `EquipBestGear` every tick unless the step is in `_confirmedStepIds`. This is identical to how stateless-retry steps work, except the confirm comes from the host instead of from the engine's own expect-check.

**Concrete surface area:**

```csharp
// QuestEngine -- new public method
public void NotifyEquipBestGearComplete(string stepId)
{
    _confirmedStepIds.Add(stepId);
}

// EquipBestGearStep dispatch arm (replaces fire-once):
if (step is EquipBestGearStep bestGearStep)
{
    var bestGearAction = await ResolveEquipBestGear(bestGearStep, ct);
    return (bestGearAction, step.Id, playerPos);
}
// No _fireOnceDispatchedIds involvement.

// EngineHost dispatch arm (updated):
case EngineAction.EquipBestGear ebg:
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    var equipResult = await _bestGearEquipper.EquipBestGear(ct);
    _services.Log.Debug($"[QuestForge] [EquipBestGear] result={equipResult}");
    if (equipResult is Result<EquipOutcome>.Success { Value: EquipOutcome.Equipped or EquipOutcome.NoChange })
        _engine.NotifyEquipBestGearComplete(lastStepId);
    break;

// EngineTestHarness dispatch arm (updated):
case EngineAction.EquipBestGear:
    actions.Add(action);
    EmitActionSubmitted("EquipBestGear", default);
    var ebgResult = await BestGearEquipper.EquipBestGear(ct);
    EmitActionCompleted("EquipBestGear",
        ebgResult.IsSuccess ? ebgResult.ValueOrThrow.ToString() : "Failed");
    if (ebgResult is Result<EquipOutcome>.Success { Value: EquipOutcome.Equipped or EquipOutcome.NoChange })
        Engine.NotifyEquipBestGearComplete(lastStepId);
    break;
```

**What breaks if violated:** If fire-once is kept as-is, the engine self-confirms after the first dispatch before the adapter has finished the multi-tick equip. Gear does not change.

**Testability:** `FakeBestGearEquipper` can script `Pending, Pending, Equipped` sequences. Engine tests verify that the step is NOT confirmed until `Equipped` is returned, and that the engine keeps emitting `EquipBestGear` until then.

### EBF4 -- `DalamudBestGearEquipper` internal state machine

**Decision:** The Dalamud adapter tracks a simple enum state:

```csharp
private enum VanillaEquipPhase { Idle, WaitingForRecommendation }
private VanillaEquipPhase _vanillaPhase = VanillaEquipPhase.Idle;
```

- `Idle` + `EquipBestGear()` called --> call `SetupForClassJob`, set `_vanillaPhase = WaitingForRecommendation`, return `Pending`.
- `WaitingForRecommendation` + `EquipBestGear()` called --> check `IsUpdating`:
  - `true` --> return `Pending`.
  - `false` --> call `EquipRecommendedGear()`, set `_vanillaPhase = Idle`, return `Equipped`.

**Timeout guard:** If the adapter has been in `WaitingForRecommendation` for more than 5 seconds (tracked via `Stopwatch`), reset to `Idle` and return `Failed` with reason `"recommendEquipTimeout"`. This prevents infinite loops if `RecommendEquipModule` gets stuck.

**Stylist path:** Stylist IPC (`UpdateCurrentGearsetEx`) is fire-and-forget and handles its own timing. The Stylist path continues to return `Equipped` immediately -- no state machine needed. If future testing reveals a similar issue, the same pattern can be applied, but we do not speculatively fix what is not broken.

```csharp
public sealed class DalamudBestGearEquipper : IBestGearEquipper
{
    private enum VanillaEquipPhase { Idle, WaitingForRecommendation }

    private VanillaEquipPhase _vanillaPhase = VanillaEquipPhase.Idle;
    private readonly Stopwatch _phaseTimer = new();
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(5);

    // ... ctor, fields unchanged ...

    public Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_preferStylist())
        {
            try
            {
                _svc.PluginInterface
                    .GetIpcSubscriber<bool?, bool?, object>("Stylist.UpdateCurrentGearsetEx")
                    .InvokeAction(true, true);
                return Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Equipped));
            }
            catch
            {
                // Stylist not installed or not ready -- fall through to vanilla path
            }
        }

        return Task.FromResult<Result<EquipOutcome>>(VanillaEquip());
    }

    private unsafe Result<EquipOutcome> VanillaEquip()
    {
        var module = RecommendEquipModule.Instance();
        if (module is null)
            return Result.Fail<EquipOutcome>("recommendEquipUnavailable",
                "RecommendEquipModule.Instance() returned null");

        switch (_vanillaPhase)
        {
            case VanillaEquipPhase.Idle:
            {
                var playerState = PlayerState.Instance();
                if (playerState is null)
                    return Result.Fail<EquipOutcome>("playerStateUnavailable",
                        "PlayerState.Instance() returned null");

                module->SetupForClassJob(playerState->CurrentClassJobId);
                _vanillaPhase = VanillaEquipPhase.WaitingForRecommendation;
                _phaseTimer.Restart();
                return Result.Ok(EquipOutcome.Pending);
            }

            case VanillaEquipPhase.WaitingForRecommendation:
            {
                if (_phaseTimer.Elapsed > PhaseTimeout)
                {
                    _vanillaPhase = VanillaEquipPhase.Idle;
                    _phaseTimer.Reset();
                    return Result.Fail<EquipOutcome>("recommendEquipTimeout",
                        "RecommendEquipModule did not finish updating within 5 seconds");
                }

                if (module->IsUpdating)
                    return Result.Ok(EquipOutcome.Pending);

                module->EquipRecommendedGear();
                _vanillaPhase = VanillaEquipPhase.Idle;
                _phaseTimer.Reset();
                return Result.Ok(EquipOutcome.Equipped);
            }

            default:
                _vanillaPhase = VanillaEquipPhase.Idle;
                return Result.Fail<EquipOutcome>("unexpectedPhase",
                    $"Unexpected vanilla equip phase: {_vanillaPhase}");
        }
    }
}
```

### EBF5 -- `FakeBestGearEquipper` supports scripted sequences

**Decision:** `FakeBestGearEquipper` gains a `ScriptOutcomeSequence(params EquipOutcome[] outcomes)` method that scripts multiple consecutive returns. Each call to `EquipBestGear` pops the next outcome from the queue. When the queue is empty, it falls back to the default `_nextOutcome ?? EquipOutcome.Equipped` behavior.

```csharp
private readonly Queue<EquipOutcome> _outcomeSequence = new();

public void ScriptOutcomeSequence(params EquipOutcome[] outcomes)
{
    foreach (var o in outcomes) _outcomeSequence.Enqueue(o);
}

public Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    RecordedCalls.Add(new EquipBestCall(DateTimeOffset.UtcNow));

    if (_nextFailure is { } f)
    {
        _nextFailure = null;
        return Task.FromResult<Result<EquipOutcome>>(Result.Fail<EquipOutcome>(f.Reason, f.Detail));
    }

    if (_outcomeSequence.TryDequeue(out var scripted))
        return Task.FromResult<Result<EquipOutcome>>(Result.Ok(scripted));

    var outcome = _nextOutcome ?? EquipOutcome.Equipped;
    _nextOutcome = null;
    return Task.FromResult<Result<EquipOutcome>>(Result.Ok(outcome));
}
```

**Testability:** This lets tests script `Pending, Pending, Equipped` to verify the engine re-dispatches across ticks and only self-confirms after `Equipped`.

### EBF6 -- `NotifyEquipBestGearComplete` is specific, not generic

**Decision:** The method is `NotifyEquipBestGearComplete(string stepId)`, not a generic `NotifyStepOutcome`. Rationale: other fire-once steps (`RegisterGearsetStep`) do not have this problem today, and a generic mechanism would require threading outcome information through the entire host dispatch path for all action types. If `RegisterGearsetStep` develops the same issue, it gets its own `NotifyRegisterGearsetComplete` method. YAGNI.

```csharp
// QuestEngine.cs
public void NotifyEquipBestGearComplete(string stepId)
{
    _confirmedStepIds.Add(stepId);
}
```

**What breaks if violated:** A generic `NotifyStepOutcome` would require changes to every host dispatch arm and every test harness dispatch arm, for no benefit to steps that work correctly today.

### EBF7 -- `_fireOnceDispatchedIds` usage for EquipBestGearStep only is removed; RegisterGearsetStep keeps it

**Decision:** Only EquipBestGearStep is removed from the fire-once pattern. RegisterGearsetStep retains fire-once because its adapter (`RaptureGearsetModule.UpdateGearset`) is synchronous and completes within one frame.

**What breaks if violated:** If fire-once is removed globally, RegisterGearsetStep would re-dispatch every tick indefinitely (no expect, no adapter-driven confirm).

### EBF8 -- Existing tests EB1-EB13 are updated, not deleted

**Decision:** Tests EB1-EB7, EB9-EB13 require minimal changes (the engine behavior for those scenarios is unchanged: emit `EquipBestGear` when conditions are met, `Wait` when guarded). EB8 and EB8b change semantics: without fire-once, "no Expect" steps need the host/harness to call `NotifyEquipBestGearComplete` to self-confirm. The test harness already calls the adapter and can check the result.

Updated harness dispatch arm handles this automatically, so EB8 and EB8b continue to pass without test-body changes -- the harness now calls `NotifyEquipBestGearComplete` when the fake returns `Equipped`.

---

## Task Breakdown

### Task 1 -- Add `Pending` to `EquipOutcome` enum

**File:** `QuestForge.Adapters/Gear/IGearEquipper.cs`

Add `Pending` between `NoChange` and `InCombat`:

```csharp
public enum EquipOutcome { Equipped, NoChange, Pending, InCombat, InInstance, ItemNotFound, Failed }
```

No other file changes. All existing code that pattern-matches on `EquipOutcome` will get compiler warnings if non-exhaustive (good -- we want to find them).

### Task 2 -- Update `FakeBestGearEquipper`

**File:** `QuestForge.Adapters.Fakes/Gear/FakeBestGearEquipper.cs`

- Add `_outcomeSequence` queue and `ScriptOutcomeSequence` method (per EBF5).
- Update `EquipBestGear` to dequeue from the sequence before falling back to `_nextOutcome`.
- Add `_outcomeSequence.Clear()` to `Reset()`.

### Task 3 -- Add `NotifyEquipBestGearComplete` to `QuestEngine`

**File:** `QuestForge.Engine/QuestEngine.cs`

- Add public method `NotifyEquipBestGearComplete(string stepId)` that adds to `_confirmedStepIds`.
- Remove EquipBestGearStep from the `_fireOnceDispatchedIds` block. Replace with:

```csharp
// 6a6. EquipBestGearStep async arm -- re-dispatches every tick until confirmed by host.
if (step is EquipBestGearStep bestGearStep)
{
    var bestGearAction = await ResolveEquipBestGear(bestGearStep, ct);
    return (bestGearAction, step.Id, playerPos);
}
```

No changes to `ResolveEquipBestGear` -- it already handles casting/combat guards and returns `EquipBestGear` otherwise.

### Task 4 -- Update `EngineTestHarness` dispatch arm

**File:** `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs`

Update the `EngineAction.EquipBestGear` case to call `NotifyEquipBestGearComplete` when the adapter returns a terminal outcome:

```csharp
case EngineAction.EquipBestGear:
    actions.Add(action);
    EmitActionSubmitted("EquipBestGear", default);
    var ebgResult = await BestGearEquipper.EquipBestGear(ct);
    EmitActionCompleted("EquipBestGear",
        ebgResult.IsSuccess ? ebgResult.ValueOrThrow.ToString() : "Failed");
    if (ebgResult.IsSuccess && ebgResult.ValueOrThrow is EquipOutcome.Equipped or EquipOutcome.NoChange)
        Engine.NotifyEquipBestGearComplete(lastStepId);
    break;
```

Where `lastStepId` is the step ID from the most recent tick. The harness already tracks this (it is the second element of the tick return tuple).

### Task 5 -- Update `EngineHost` dispatch arm

**File:** `QuestForge.Plugin/EngineHost.cs`

Same pattern as Task 4:

```csharp
case EngineAction.EquipBestGear:
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    var equipResult = await _bestGearEquipper.EquipBestGear(ct);
    _services.Log.Debug($"[QuestForge] [EquipBestGear] result={equipResult}");
    if (equipResult is Result<EquipOutcome>.Success { Value: EquipOutcome.Equipped or EquipOutcome.NoChange })
        _engine.NotifyEquipBestGearComplete(_lastStepId);
    break;
```

Where `_lastStepId` is already tracked by EngineHost from the tick result.

### Task 6 -- Rewrite `DalamudBestGearEquipper.VanillaEquip`

**File:** `QuestForge.Adapters.Dalamud/Gear/DalamudBestGearEquipper.cs`

Implement the two-phase state machine per EBF4. Add `_vanillaPhase` enum, `_phaseTimer` stopwatch, and the `switch`-based `VanillaEquip` method.

### Task 7 -- Update or add tests

**File:** `QuestForge.Engine.Tests/Engine/EquipBestGearStepTests.cs`

See Given-When-Then specs below.

---

## Given-When-Then Specifications

### F1 -- Happy path: adapter returns Pending then Equipped (two-tick)

**Given:** A quest with a single EquipBestGearStep (id `"equip-best"`, Expect = `questSequence(65575) == 1`). Fake adapter scripted with `ScriptOutcomeSequence(Pending, Equipped)`.
**When:** Engine ticks twice.
**Then:**
- Tick 1: emits `EngineAction.EquipBestGear`. Harness dispatches, adapter returns `Pending`, harness does NOT call `NotifyEquipBestGearComplete`.
- Tick 2: emits `EngineAction.EquipBestGear` again. Harness dispatches, adapter returns `Equipped`, harness calls `NotifyEquipBestGearComplete`.
- Adapter called exactly 2 times.

### F2 -- Happy path: adapter returns Equipped on first call (single-tick, backwards compat)

**Given:** A quest with a single EquipBestGearStep (Expect = `questSequence(65575) == 1`). Fake adapter scripted with `ScriptNextResult(Equipped)`.
**When:** Engine ticks once.
**Then:** Emits `EngineAction.EquipBestGear`. Harness dispatches, adapter returns `Equipped`, harness calls `NotifyEquipBestGearComplete`. Adapter called exactly 1 time.

This verifies backwards compatibility -- the fix does not break the single-frame case.

### F3 -- No Expect + Pending then Equipped: self-confirms only after Equipped

**Given:** A quest with a single EquipBestGearStep (Expect = null). Fake adapter scripted with `ScriptOutcomeSequence(Pending, Pending, Equipped)`.
**When:** Engine ticks four times.
**Then:**
- Tick 1: `EquipBestGear` emitted. Adapter returns `Pending`. Step NOT confirmed.
- Tick 2: `EquipBestGear` emitted. Adapter returns `Pending`. Step NOT confirmed.
- Tick 3: `EquipBestGear` emitted. Adapter returns `Equipped`. Harness calls `NotifyEquipBestGearComplete`. Step confirmed.
- Tick 4: `Wait` (no more steps). Adapter NOT called again.
- Adapter called exactly 3 times.

### F4 -- No Expect + single Equipped: self-confirms immediately (replaces EB8)

**Given:** A quest with a single EquipBestGearStep (Expect = null). Fake adapter returns `Equipped` (default).
**When:** Engine ticks twice.
**Then:**
- Tick 1: `EquipBestGear` emitted. Adapter returns `Equipped`. Step confirmed.
- Tick 2: `Wait` (no more steps).
- Adapter called exactly 1 time.

This is the updated EB8 -- same observable behavior, different mechanism (host-driven confirm instead of fire-once).

### F5 -- Casting guard then Pending then Equipped (replaces EB8b)

**Given:** A quest with EquipBestGearStep (Expect = null). Player is casting. Fake adapter scripted with `ScriptOutcomeSequence(Pending, Equipped)`.
**When:** Tick 1 (casting), clear casting, Tick 2, Tick 3, Tick 4.
**Then:**
- Tick 1: `Wait` (casting guard). Adapter NOT called.
- Tick 2: `EquipBestGear` emitted. Adapter returns `Pending`.
- Tick 3: `EquipBestGear` emitted. Adapter returns `Equipped`. Step confirmed.
- Tick 4: `Wait` (done).
- Adapter called exactly 2 times.

### F6 -- Adapter failure does not confirm step

**Given:** A quest with EquipBestGearStep (Expect = null). Fake adapter scripted with `ScriptNextFailure("adapter error")` then default `Equipped`.
**When:** Engine ticks three times.
**Then:**
- Tick 1: `EquipBestGear` emitted. Adapter returns `Failed`. Step NOT confirmed.
- Tick 2: `EquipBestGear` emitted. Adapter returns `Equipped`. Step confirmed.
- Tick 3: `Wait` (done).
- Adapter called exactly 2 times.

### F7 -- Pending does not count as dispatched for combat guard

**Given:** A quest with EquipBestGearStep (Expect = `questSequence(65575) == 1`). Fake adapter scripted with `ScriptOutcomeSequence(Pending)`. After tick 1, set player in combat.
**When:** Tick 1, set in combat, Tick 2.
**Then:**
- Tick 1: `EquipBestGear` emitted. Adapter returns `Pending`.
- Tick 2: `Wait` (combat guard). Adapter NOT called.
- The combat guard fires correctly even though the adapter previously returned `Pending`.

### F8 -- RunToCompletion integration with multi-tick equip

**Given:** A two-sequence quest: seq 0 has EquipBestGearStep (Expect = `questSequence(65575) == 1`), seq 1 has TalkStep. Fake adapter scripted with `ScriptOutcomeSequence(Pending, Pending, Equipped)`. Sequence advances to 1 after the equip completes (simulated by the test, same as EB11).
**When:** Run ticks manually: 3 ticks for equip (Pending, Pending, Equipped), advance sequence, then `RunToCompletion`.
**Then:**
- First 3 ticks all emit `EquipBestGear`.
- After sequence advance, engine moves to seq 1 TalkStep.
- `RunToCompletion` completes via the TalkStep.

### F9 -- Sequence change clears pending state

**Given:** A quest with EquipBestGearStep (Expect = `questSequence(65575) == 1`) in seq 0. Fake adapter scripted with `ScriptOutcomeSequence(Pending)`. After tick 1, externally advance sequence to 1 (simulating game-side completion).
**When:** Tick 1, advance sequence, Tick 2.
**Then:**
- Tick 1: `EquipBestGear` emitted, adapter returns `Pending`.
- Sequence change clears `_confirmedStepIds` (existing behavior).
- Tick 2: engine moves to seq 1 (EquipBestGearStep in seq 0 is no longer visited).

This verifies that a stale pending state does not cause issues.

### F10 -- NoChange outcome confirms step (e.g., gear already optimal)

**Given:** A quest with EquipBestGearStep (Expect = null). Fake adapter returns `NoChange`.
**When:** Engine ticks twice.
**Then:**
- Tick 1: `EquipBestGear` emitted. Adapter returns `NoChange`. Step confirmed.
- Tick 2: `Wait` (done).

---

## Implementation Order

### Phase A -- Enum + Fake (30 min)

1. Add `Pending` to `EquipOutcome` (Task 1).
2. Update `FakeBestGearEquipper` with `ScriptOutcomeSequence` (Task 2).

**Done gate:** `dotnet build` succeeds. No test changes yet.

### Phase B -- Engine changes (1 hour)

1. Add `NotifyEquipBestGearComplete` to `QuestEngine` (Task 3).
2. Remove EquipBestGearStep from fire-once in `QuestEngine.cs` (Task 3).
3. Update `EngineTestHarness` dispatch arm (Task 4).

**Done gate:** Existing tests EB1-EB13 pass (EB8/EB8b pass because the updated harness now calls `NotifyEquipBestGearComplete` on `Equipped`).

### Phase C -- New tests (1 hour)

1. Write tests F1-F10 (Task 7).

**Done gate:** All F1-F10 tests pass. All EB1-EB13 tests still pass.

### Phase D -- Dalamud adapter (30 min)

1. Rewrite `VanillaEquip` with two-phase state machine (Task 6).
2. Update `EngineHost` dispatch arm (Task 5).

**Done gate:** `dotnet build` succeeds for all projects. Manual in-game test confirms gear actually changes.

### Phase E -- In-game smoke test (15 min)

1. Load quest 65999 ("Dressed to Call") or any quest with an equip-best-gear step.
2. Verify gear changes visually.
3. Verify `[QuestForge] [EquipBestGear] result=...` log shows `Pending` then `Equipped` across frames.

---

## Done Criteria

1. `dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~EquipBestGearStepTests"` passes with all existing EB tests and all new F tests.
2. `dotnet build` succeeds for all projects in the solution.
3. In-game: EquipBestGearStep with vanilla path causes visible gear changes (not a silent no-op).
4. In-game: `dalamud.log` shows `result=Success { Value = Pending }` on the first frame, then `result=Success { Value = Equipped }` on a subsequent frame.
5. Existing quest data does not require changes -- `EquipBestGearStep` with or without `Expect` continues to work.

---

## Exclusions

- **Stylist path timing fix:** Not addressed. Stylist IPC is fire-and-forget and handles its own timing internally. If a similar issue is found, the same two-phase pattern can be applied later.
- **Generic `NotifyStepOutcome` mechanism:** YAGNI. Only EquipBestGearStep needs host-driven confirmation today.
- **`IsAllRecommendedGearEquipped` check:** Questionable checks this before calling `EquipRecommendedGear`. We skip this optimization in v1 -- calling `EquipRecommendedGear` when gear is already optimal is harmless (it equips the same items). Can be added later if needed.
- **Post-equip wait (Questionable's 1-second delay):** Not included. The engine's expect-check or `NotifyEquipBestGearComplete` handles step completion. If in-game testing reveals that gear changes need a settling delay, it can be added to the adapter's state machine as a third phase.
- **Adapter unit tests for `DalamudBestGearEquipper`:** The state machine logic is simple enough that the engine-level F1-F10 tests (via `FakeBestGearEquipper`) provide sufficient coverage. The Dalamud adapter itself is tested via in-game smoke (Phase E).

---

## READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in F1-F10.
- Happy paths: 4 scenarios (F1, F2, F4, F10)
- Edge cases: 4 scenarios (F3, F5, F7, F9)
- Error cases: 1 scenario (F6)
- Integration: 1 scenario (F8)
- Expected total: ~10 new tests in `QuestForge.Engine.Tests`, plus updates to EB8 and EB8b if their mechanics change
