# Action Cooldown Plan: Pre-Arm Throttle for Instant Actions

**Status:** ready to implement
**Input docs:** `QuestForge.Engine/QuestEngine.cs` (4 pre-arms), `QuestForge.Plugin/PluginConfig.cs`
**Output:** instant actions (SayChatMessage, UseEmote, UseAction, UseItem) fire once, then wait a configurable cooldown before re-firing. No more 30x/sec spam.
**Branch:** `feat/pre-arm-cooldown`

---

## Dependency graph

Single repo change. No tools-repo or data-repo changes.

```
QuestForge.Plugin/PluginConfig.cs          (new field)
    |
QuestForge.Engine/QuestEngine.cs           (new ctor param + cooldown logic in 4 pre-arms)
    |
QuestForge.Engine.Tests/Engine/            (new test file)
```

Build order: schema unchanged, engine changes, plugin wiring.

---

## Architectural decisions

### AC-1: Cooldown seconds reach the engine via `double` ctor param, not `Func<double>`

**Decision:** Add `double actionCooldownSeconds = 5.0` as an optional ctor parameter on `QuestEngine`.

**Alternatives considered:**
- **(a) `Func<double> getCooldownSeconds`** — would allow live config changes mid-run. Rejected: the cooldown is a run-level setting. Changing it mid-quest is not a meaningful use case, and the `Func<>` pattern adds indirection for no benefit. If live-config becomes needed later, the ctor param can be changed to a Func in a single PR.
- **(b) Via `ITimingProfile`** — conceptually related (timing), but `ITimingProfile` is about inter-action jitter and humanization delays seeded from `runId`. The cooldown is a user-facing config knob, not a timing-profile concern. Mixing them violates SRP.
- **(c) Hardcoded constant** — rejected: users running in party content may want 0s (no cooldown) to avoid delays.

**Concrete surface:**
```csharp
public QuestEngine(
    ...,
    TimeProvider? clock = null,
    ...,
    double actionCooldownSeconds = 5.0)
```

**What breaks if violated:** if someone passes a Func instead, all tests that construct QuestEngine directly must change their call sites. The double param is backward-compatible (default value).

**Testability:** tests pass `0.0` for no-cooldown legacy behavior or specific values to assert cooldown timing.

### AC-2: Two fields track cooldown state, not one timer per pre-arm

**Decision:** Add two private fields to `QuestEngine`:

```csharp
private DateTimeOffset? _lastActionFiredAt;
private string? _lastActionFiredStepId;
```

One shared pair of fields for all 4 pre-arms. The cooldown is per-step, not per-step-type. When the engine's cursor advances to a different step (different `step.Id`), the cooldown resets implicitly because `_lastActionFiredStepId` will not match.

**Alternatives considered:**
- **(a) Dictionary<string, DateTimeOffset> per step ID** — over-engineered. The engine only ever has one active step at a time (cursor walk returns on the first unconfirmed step). A dictionary would accumulate entries that are never read again.
- **(b) One timer field per pre-arm** — 8 fields instead of 2 for the same behavior. The pre-arms are mutually exclusive (only one step type dispatches per tick).
- **(c) Reset on sequence change** — the existing `_lastKnownSequence` change-detection block already clears step-scoped state. We could add a reset there, but it is unnecessary: a sequence change means different steps with different IDs, so the `_lastActionFiredStepId` comparison fails naturally.

**What breaks if violated:** if someone adds per-step-type timers, the step-change reset logic must be duplicated 4 times instead of being implicit.

### AC-3: Cooldown check placement — AFTER guards, BEFORE emit

**Decision:** In each of the 4 pre-arms (`ResolveSayChatMessage`, `ResolveUseEmote`, `ResolveUseAction`, `ResolveUseItem`), the cooldown check goes AFTER all existing guards (null adapter check, casting check, action-status check) and BEFORE the final action emit.

**Rationale:**
- Guards that return `Wait` or `AwaitUser` (casting, unusable) should still fire immediately. The cooldown only suppresses re-emission of the action itself.
- The first time a step is reached, `_lastActionFiredStepId` will not match `step.Id` (or will be null), so the action fires immediately. On the SAME tick's return, `_lastActionFiredAt` and `_lastActionFiredStepId` are set.
- On subsequent ticks for the SAME step, if the cooldown has not elapsed, the pre-arm returns `Wait("action cooldown ...")`.

**Concrete pattern (same in all 4 pre-arms):**
```csharp
// --- existing guards above (casting, adapter null, action status) ---

// Cooldown: suppress re-fire within the configured window.
if (_actionCooldownSeconds > 0
    && _lastActionFiredStepId == step.Id
    && _lastActionFiredAt is not null)
{
    var elapsed = _clock.GetUtcNow() - _lastActionFiredAt.Value;
    if (elapsed.TotalSeconds < _actionCooldownSeconds)
        return new EngineAction.Wait(
            $"action cooldown ({elapsed.TotalSeconds:F1}s / {_actionCooldownSeconds}s)",
            Origin: step);
}

_lastActionFiredAt = _clock.GetUtcNow();
_lastActionFiredStepId = step.Id;
return new EngineAction.<TheAction>(...);
```

**What breaks if violated:** if the cooldown check is placed BEFORE the casting guard, a casting player would see "action cooldown" instead of "player casting" — confusing diagnostics.

### AC-4: Cooldown of 0 disables throttling entirely

**Decision:** When `actionCooldownSeconds` is `0.0` (or negative), the cooldown check is skipped entirely via the `_actionCooldownSeconds > 0` guard. This preserves backward compatibility: all existing tests pass without modification because the default is `5.0` but existing tests never tick the same instant-action step twice (they advance Expect between ticks).

**What breaks if violated:** if 0.0 still throttled, every existing multi-tick test for these 4 step types would need clock injection or would intermittently fail.

### AC-5: Expect satisfaction during cooldown still confirms the step immediately

**Decision:** No change needed. The cursor walk's Expect check (step 2 in `ResolveAction`) runs BEFORE the pre-arm (step 6a3 etc.). If Expect satisfies during cooldown, the step is confirmed and the pre-arm is never reached. This is the existing behavior and requires zero code changes.

**Testability:** a test sets Expect true during cooldown and verifies the step confirms without waiting.

### AC-6: PluginConfig field

**Decision:**
```csharp
/// <summary>
/// Seconds to wait after firing an instant action (SayChatMessage, UseEmote, UseAction, UseItem)
/// before allowing re-fire on the same step. Prevents visible spam for actions with no cast bar.
/// Set to 0 to disable throttling.
/// </summary>
public double ActionCooldownSeconds { get; set; } = 5.0;
```

Passed to `QuestEngine` ctor by `EngineHost` in `BeginRun`:
```csharp
var engine = new QuestEngine(
    ...,
    actionCooldownSeconds: _config.ActionCooldownSeconds);
```

### AC-7: Which pre-arms are affected and which are NOT

**Affected (4):**
| Pre-arm | Why |
|---|---|
| `ResolveSayChatMessage` | Instant, visible to other players |
| `ResolveUseEmote` | Instant, visible to other players |
| `ResolveUseAction` | Some actions have no cast bar |
| `ResolveUseItem` | Items have internal game cooldown but engine would still spam |

**NOT affected:**
| Pre-arm | Why excluded |
|---|---|
| `ResolveEquipGear` | Implicit postcondition (self-confirms when all items equipped) |
| `ResolveEquipBestGear` | Fires once per tick; harmless |
| `ResolveChangeJob` | Implicit postcondition (self-confirms on correct job) |
| `ResolveRegisterGearset` | Fires once; no re-dispatch concern |
| `ResolveOpenCoffers` | Has casting guard + coffer-count guard |
| `ResolveInteractOrNavigate` | Interaction is already throttled by the adapter (1/sec) |
| `ResolveTeleportAction` | Teleport has in-combat guard + zone-change postcondition |
| `ResolvePurchaseAction` | Has affordability pre-check + vendor flow |
| `ResolveSpd`/`ResolveDungeonTrial` | Duty entry is gated by BossMod/AutoDuty availability |

---

## Task 1 — Engine changes

### 1.1 New ctor parameter and fields

Add to `QuestEngine`:

```csharp
private readonly double _actionCooldownSeconds;
private DateTimeOffset? _lastActionFiredAt;
private string? _lastActionFiredStepId;
```

Constructor gains:
```csharp
double actionCooldownSeconds = 5.0
```

Store: `_actionCooldownSeconds = actionCooldownSeconds;`

### 1.2 Cooldown helper method

Extract the shared cooldown logic into a private method to avoid duplicating the pattern 4 times:

```csharp
/// <summary>
/// Returns a Wait action if the cooldown for the given step has not elapsed.
/// Returns null if the action should fire (first dispatch or cooldown expired).
/// DOES NOT update the timestamp — caller must set _lastActionFiredAt and _lastActionFiredStepId
/// after emitting the action.
/// </summary>
private EngineAction.Wait? CheckActionCooldown(Step step)
{
    if (_actionCooldownSeconds <= 0) return null;
    if (_lastActionFiredStepId != step.Id || _lastActionFiredAt is null) return null;

    var elapsed = _clock.GetUtcNow() - _lastActionFiredAt.Value;
    if (elapsed.TotalSeconds >= _actionCooldownSeconds) return null;

    return new EngineAction.Wait(
        $"action cooldown ({elapsed.TotalSeconds:F1}s / {_actionCooldownSeconds}s)",
        Origin: step);
}

private void RecordActionFired(Step step)
{
    _lastActionFiredAt = _clock.GetUtcNow();
    _lastActionFiredStepId = step.Id;
}
```

### 1.3 Modify 4 pre-arms

Each pre-arm gains two lines after existing guards, before the final return:

```csharp
var cooldown = CheckActionCooldown(step);
if (cooldown is not null) return cooldown;

// ... build and return the action ...
RecordActionFired(step);
return new EngineAction.<TheAction>(...);
```

### 1.4 PluginConfig field

```csharp
public double ActionCooldownSeconds { get; set; } = 5.0;
```

### 1.5 EngineHost wiring

In `EngineHost`, when constructing `QuestEngine` in `BeginRun`, pass `actionCooldownSeconds: _config.ActionCooldownSeconds`.

### 1.6 EngineTestHarness update

The `EngineTestHarness` constructs `QuestEngine` without a `TimeProvider` (uses `TimeProvider.System`). For cooldown tests, this is fine because:
- Existing tests never tick the same instant-action step twice without advancing Expect between ticks. The cooldown window (5s) is irrelevant when Expect confirms the step.
- New cooldown-specific tests construct `QuestEngine` directly with a `ManualTimeProvider` (same pattern as `WaitStepTests`).

No changes to `EngineTestHarness` are required.

---

## Task 2 — Given-When-Then specifications

Test file: `QuestForge.Engine.Tests/Engine/ActionCooldownTests.cs`

Tests construct `QuestEngine` directly with a `ManualTimeProvider` and `actionCooldownSeconds` parameter, same pattern as `WaitStepTests.cs`. Each test verifies the cooldown behavior for one of the 4 affected pre-arms, plus cross-cutting scenarios.

### CD-1: SayChatMessage first tick fires immediately

**Given:** A quest with a single `SayChatMessageStep` (Id="say-hello", Expect="questFlag(90001, 3)"). `QuestEngine` constructed with `actionCooldownSeconds: 5.0` and a `ManualTimeProvider` at T0.

**When:** Engine.Tick() is called.

**Then:** Returns `EngineAction.SayChatMessage` with Message="Open Sesame". NOT a Wait.

### CD-2: SayChatMessage second tick within cooldown returns Wait

**Given:** Same quest as CD-1. Tick 1 returned `SayChatMessage`. Clock advanced by 2.0 seconds (within the 5.0s cooldown).

**When:** Engine.Tick() is called again (Expect still false).

**Then:** Returns `EngineAction.Wait` with Reason containing "action cooldown" and "2.0s / 5.0s".

### CD-3: SayChatMessage tick after cooldown expires fires again

**Given:** Same quest as CD-1. Tick 1 returned `SayChatMessage`. Clock advanced by 5.0 seconds (exactly at cooldown boundary, `>=` semantics).

**When:** Engine.Tick() is called again (Expect still false).

**Then:** Returns `EngineAction.SayChatMessage` (action re-fires).

### CD-4: UseEmote first tick fires, second tick within cooldown returns Wait

**Given:** A quest with a single `UseEmoteStep` (Id="emote-dance", EmoteId=7, Expect="questFlag(90002, 3)"). `actionCooldownSeconds: 3.0`. Clock at T0.

**When:** Tick 1 returns `UseEmote`. Clock advance 1.5s. Tick 2.

**Then:** Tick 2 returns `EngineAction.Wait` with Reason containing "action cooldown".

### CD-5: UseAction first tick fires, second tick within cooldown returns Wait

**Given:** A quest with a single `UseActionStep` (Id="use-sprint", ActionType=Ability, ActionId=4, Expect="questFlag(90003, 3)"). `actionCooldownSeconds: 5.0`. ActionExecutor returns `ActionStatus.Ready`. Clock at T0.

**When:** Tick 1 returns `UseAction`. Clock advance 2.0s. Tick 2.

**Then:** Tick 2 returns `EngineAction.Wait` with Reason containing "action cooldown".

### CD-6: UseItem first tick fires, second tick within cooldown returns Wait

**Given:** A quest with a single `UseItemStep` (Id="use-potion", Kind=Normal, ItemId=4551, Expect="questFlag(90004, 3)"). `actionCooldownSeconds: 5.0`. Clock at T0.

**When:** Tick 1 returns `UseItem`. Clock advance 1.0s. Tick 2.

**Then:** Tick 2 returns `EngineAction.Wait` with Reason containing "action cooldown".

### CD-7: Different step resets cooldown — fires immediately

**Given:** A quest with two steps: `SayChatMessageStep` (Id="say-first") and `SayChatMessageStep` (Id="say-second"). Both with different Expect predicates. `actionCooldownSeconds: 5.0`. Clock at T0.

**When:** Tick 1 returns `SayChatMessage` for "say-first". Satisfy "say-first" Expect. Clock advance 1.0s (within cooldown). Tick 2.

**Then:** Tick 2 returns `SayChatMessage` for "say-second" — NOT Wait. The cooldown does not carry across steps because `_lastActionFiredStepId` does not match.

### CD-8: Expect satisfies during cooldown — step confirms immediately

**Given:** A quest with a `SayChatMessageStep` (Id="say-expect", Expect="questFlag(90005, 3)"). `actionCooldownSeconds: 10.0`. Clock at T0.

**When:** Tick 1 returns `SayChatMessage`. Clock advance 1.0s. Set questFlag(90005, 3)=true. Tick 2.

**Then:** Tick 2 does NOT return Wait("action cooldown"). The Expect check in the cursor walk confirms the step before the pre-arm is reached. Returns `Wait("all steps in current sequence satisfied")`.

### CD-9: Cooldown of 0 disables throttling

**Given:** A quest with a `SayChatMessageStep` (Id="say-no-cd", Expect="questFlag(90006, 3)"). `actionCooldownSeconds: 0.0`. Clock at T0.

**When:** Tick 1 returns `SayChatMessage`. Clock does NOT advance. Tick 2.

**Then:** Tick 2 returns `SayChatMessage` (no cooldown; fires every tick).

### CD-10: Casting guard takes priority over cooldown

**Given:** A quest with a `UseActionStep`. `actionCooldownSeconds: 5.0`. Player is casting. ActionExecutor returns `ActionStatus.Ready`. Clock at T0.

**When:** Tick 1 is called.

**Then:** Returns `EngineAction.Wait` with Reason containing "player casting" — NOT "action cooldown". The casting guard fires before the cooldown check.

### CD-11: UseAction action-status OnCooldown takes priority over engine cooldown

**Given:** A quest with a `UseActionStep`. `actionCooldownSeconds: 5.0`. Player is NOT casting. ActionExecutor returns `ActionStatus.OnCooldown(remaining: 2.5s)`. Clock at T0.

**When:** Tick 1 is called.

**Then:** Returns `EngineAction.Wait` with Reason containing "on cooldown: 2.5s" (the game's own cooldown). NOT "action cooldown" (the engine's throttle). The action-status guard fires before the engine cooldown check.

### CD-12: Sequence change resets cooldown implicitly

**Given:** A quest with sequence 0 containing `SayChatMessageStep` (Id="say-seq0") and sequence 1 containing `SayChatMessageStep` (Id="say-seq1"). `actionCooldownSeconds: 5.0`. Clock at T0.

**When:** Tick 1 on seq 0 returns `SayChatMessage` for "say-seq0". Game advances sequence to 1. Clock advance 0.5s. Tick 2.

**Then:** Tick 2 returns `SayChatMessage` for "say-seq1" — NOT Wait. Even if we used the same step ID (which we do not here), the sequence change clears `_confirmedStepIds` and the step is fresh.

### CD-13: Cooldown Wait message includes Origin for trace context

**Given:** A quest with a `UseEmoteStep` (Id="emote-cd-origin"). `actionCooldownSeconds: 5.0`. Clock at T0.

**When:** Tick 1 returns `UseEmote`. Clock advance 2.0s. Tick 2 returns Wait.

**Then:** The returned `EngineAction.Wait` has `Origin` set to the `UseEmoteStep` instance (not null). This ensures the trace's `DecisionEvent` can attribute the cooldown wait to the correct step.

---

## Implementation order

### Phase A — Engine (1-2 hours)

1. Add `double actionCooldownSeconds = 5.0` ctor param to `QuestEngine`
2. Add `_actionCooldownSeconds`, `_lastActionFiredAt`, `_lastActionFiredStepId` fields
3. Add `CheckActionCooldown` and `RecordActionFired` helper methods
4. Modify `ResolveSayChatMessage` — add cooldown check + record
5. Modify `ResolveUseEmote` — add cooldown check + record
6. Modify `ResolveUseAction` — add cooldown check + record
7. Modify `ResolveUseItem` — add cooldown check + record

Done-before-next: all 4 pre-arms modified, project compiles.

### Phase B — Tests (1-2 hours)

1. Create `QuestForge.Engine.Tests/Engine/ActionCooldownTests.cs`
2. Implement CD-1 through CD-13 using `ManualTimeProvider` + direct `QuestEngine` construction
3. All tests green

Done-before-next: all 13 tests pass.

### Phase C — Plugin wiring (15 minutes)

1. Add `ActionCooldownSeconds` to `PluginConfig`
2. Pass to `QuestEngine` ctor in `EngineHost.BeginRun`

Done-before-next: plugin compiles, config field persisted.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests` passes — all existing tests unaffected (backward-compatible default of 5.0s; existing tests advance Expect between ticks so the cooldown window is never entered).
2. `ActionCooldownTests` contains 13 tests, all green.
3. SayChatMessage, UseEmote, UseAction, UseItem pre-arms each contain the cooldown check AFTER existing guards.
4. `PluginConfig.ActionCooldownSeconds` defaults to `5.0` and is passed to the engine.
5. Cooldown of `0.0` disables throttling entirely (CD-9).
6. `dotnet build` succeeds with TreatWarningsAsErrors for `QuestForge.Engine`.

---

## What this plan does NOT include

- UI exposure of `ActionCooldownSeconds` in the settings window (future PR)
- Per-step-type cooldown durations (not needed; the 5s default is a good universal value)
- Cooldown for non-instant actions (Navigate, Interact, Teleport, etc.) — these have their own throttling or postcondition gates
- Tooling changes (no new step type, no new trace event, no validator rules)
- Changes to `EngineTestHarness` — cooldown tests use direct engine construction

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Task 2.
- Happy paths: 4 scenarios (CD-1, CD-4 first tick, CD-5 first tick, CD-6 first tick)
- Edge cases: 5 scenarios (CD-7 step reset, CD-8 Expect during cooldown, CD-9 zero cooldown, CD-12 sequence change, CD-13 Origin on Wait)
- Error/guard cases: 4 scenarios (CD-2 within cooldown, CD-3 at boundary, CD-10 casting priority, CD-11 action-status priority)
- Expected total: ~13 tests in QuestForge.Engine.Tests
