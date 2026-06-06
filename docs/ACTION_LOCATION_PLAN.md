# ACTION_LOCATION_PLAN.md -- Add NpcLocation to UseActionStep and UseEmoteStep

**Status:** Draft
**Author:** Architect (TDD)
**Date:** 2026-06-06

## Problem

UseActionStep and UseEmoteStep have `TargetNpcId` (the target entity's BaseId) but no position. The engine cannot navigate to the target before firing the action -- it just fires and hopes the player is in range. This causes actions to fail silently or miss when the target is far away.

CombatStep already solves this: when `NpcLocation? Location` is present, the engine calls `ResolveInteractOrNavigate(step, location.Position, playerPos, actionToEmit)` to navigate if far, or emit the action if close. The Navigate dispatch in EngineHost handles mounting.

## Solution

Add `NpcLocation? Location` to UseActionStep and UseEmoteStep (same pattern as CombatStep). When present, the engine uses `ResolveInteractOrNavigate` before emitting the action. When absent, the action fires immediately (backward compatible).

The recording system (StepFactory) captures the target's position into `Location` when building use-action and use-emote steps. This requires extending `ActionCompletedSignal` and `EmoteCompletedSignal` with target position fields, populated from `ITargetProbe` data that already returns `(BaseId, X, Y, Z, Zone)`.

## Input Documents

- `QuestForge.Schema/Step.cs` -- UseActionStep (line ~201), UseEmoteStep (line ~183), CombatStep (line ~104)
- `QuestForge.Engine/QuestEngine.cs` -- `ResolveUseAction` (~1008), `ResolveUseEmote` (~1046), `ResolveInteractOrNavigate` (~866), CombatStep resolution (~608)
- `QuestForge.Engine/Authoring/StepFactory.cs` -- `"use-action"` case (~137), `"use-emote"` case (~145)
- `QuestForge.Engine/Authoring/GameStateSnapshot.cs` -- `ActionCompletedSignal`, `EmoteCompletedSignal`
- `QuestForge.Engine/Authoring/SnapshotAggregator.cs` -- `OnActionCompleted` (~518), `OnEmoteCompleted` (~538)
- `QuestForge.Plugin.Tracing/UIObserver.cs` -- `PollPlayerActionEffect` (~700), `PollPlayerEmote` (~753), `CaptureTargetBaseId` (~739)
- `QuestForge.Plugin.Tracing/ITargetProbe.cs` -- already returns `(BaseId, X, Y, Z, Zone)`
- `QuestForge.Plugin/EngineHost.cs` -- UseAction dispatch (~587), UseEmote dispatch (~598)

## Output

- UseActionStep and UseEmoteStep gain `NpcLocation? Location` (both repos)
- Engine navigates to target when Location is present and player is far
- StepFactory builds Location from enriched signal data
- Existing quest files without Location continue to work unchanged

---

## Architectural Decisions

### AL1 -- Same field shape as CombatStep

```csharp
// UseActionStep (addition)
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public NpcLocation? Location { get; init; }

// UseEmoteStep (addition)
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public NpcLocation? Location { get; init; }
```

**Rationale:** `NpcLocation` is `record NpcLocation(uint NpcId, int Zone, Position3 Position)` -- it bundles the target id, zone, and world position in the same structure CombatStep and TalkStep use. Reusing this type means `ResolveInteractOrNavigate` works without any adapter or engine changes to the navigation path.

**Rejected alternative:** Adding just `Position3? Position` (like InteractObjectStep). This loses the zone, which the engine could use for zone-mismatch detection. CombatStep precedent is authoritative.

**What breaks if violated:** If we use a different field shape, `ResolveInteractOrNavigate` cannot be reused and we duplicate navigation logic.

### AL2 -- Backward compatibility via null Location

When `Location` is null (all existing quest files), `ResolveUseAction` and `ResolveUseEmote` skip navigation and emit the action directly -- identical to current behavior. No migration needed.

**What breaks if violated:** Every existing quest file would need a schema migration.

### AL3 -- Navigation before action, not inline

When `Location` is present and the player is beyond `StopDistance`, the engine emits `EngineAction.Navigate` instead of the action. On the next tick, once the player is within range, the engine re-evaluates and emits the action. This is the standard `ResolveInteractOrNavigate` two-phase pattern.

**Concrete change to `ResolveUseAction`:**

```csharp
private async Task<EngineAction> ResolveUseAction(UseActionStep step, WorldPosition? playerPos, CancellationToken ct)
{
    // NEW: navigate to target if Location is present and player is far
    if (step.Location is { } loc)
    {
        var navOrAction = ResolveInteractOrNavigate(
            step, loc.Position, playerPos,
            interactAction: null!); // sentinel -- replaced below if close enough
        if (navOrAction is EngineAction.Navigate)
            return navOrAction;
    }

    // ... existing guards (null executor, cooldown, casting, action status) ...
    // ... emit UseAction ...
}
```

Actually, the cleaner pattern (matching CombatStep exactly) is: call `ResolveInteractOrNavigate` with the actual action as the `interactAction` parameter, and return whatever it returns. But `ResolveUseAction` has async guards (casting, action status) that should NOT run while navigating. So the navigation check must come FIRST, before the async guards.

**Refined approach:**

```csharp
private async Task<EngineAction> ResolveUseAction(UseActionStep step, WorldPosition? playerPos, CancellationToken ct)
{
    // Guard 0 (NEW): navigate to target if Location is present and player is far
    if (step.Location is { } loc)
    {
        var navCheck = ResolveInteractOrNavigate(step, loc.Position, playerPos,
            new EngineAction.Wait("navigate-sentinel")); // placeholder -- not returned
        if (navCheck is EngineAction.Navigate nav)
            return nav;
    }

    // Guard 0.5: no executor wired
    if (_actionExecutor is null)
        return new EngineAction.AwaitUser(
            "UseActionStep dispatched but no IActionExecutor wired -- host must supply one");

    // ... existing guards (cooldown, casting, action status) unchanged ...
    // ... emit UseAction ...
}
```

**Why navigate-sentinel:** `ResolveInteractOrNavigate` takes an `interactAction` parameter and returns it when the player IS close enough. We pass a dummy sentinel and only act on the Navigate branch. When the player is close, we fall through to the existing guards. This avoids running GetPlayerState/GetActionStatus while the player is still walking.

**Rejected alternative:** Running all guards first, then navigating. This wastes adapter reads (GetPlayerState, GetActionStatus) every tick during navigation for no benefit.

**Rejected alternative:** A single `ResolveInteractOrNavigate` call with the UseAction as `interactAction`. This skips the casting/cooldown/status guards when the player is close, which would be a regression.

**What breaks if violated:** If guards run during navigation, we waste adapter reads and may hit spurious "action on cooldown" waits while the player is walking.

### AL4 -- playerPos must be threaded to ResolveUseAction and ResolveUseEmote

Currently `ResolveUseAction(UseActionStep step, CancellationToken ct)` does not receive `playerPos`. The caller (`ResolveActionForStep` or the step-dispatch switch in `Tick`) has `playerPos` available. We add `WorldPosition? playerPos` as a parameter.

**Concrete signature changes:**

```csharp
// Before:
private async Task<EngineAction> ResolveUseAction(UseActionStep step, CancellationToken ct)
private async Task<EngineAction> ResolveUseEmote(UseEmoteStep step, CancellationToken ct)

// After:
private async Task<EngineAction> ResolveUseAction(UseActionStep step, WorldPosition? playerPos, CancellationToken ct)
private async Task<EngineAction> ResolveUseEmote(UseEmoteStep step, WorldPosition? playerPos, CancellationToken ct)
```

The call sites in the step-dispatch switch already have `playerPos` in scope (it is read at the top of the `Tick` method).

### AL5 -- Signal enrichment: add target position to ActionCompletedSignal and EmoteCompletedSignal

```csharp
// Before:
public sealed record ActionCompletedSignal(
    QuestForge.Schema.ActionType ActionType,
    uint ActionId,
    uint? TargetBaseId);

// After:
public sealed record ActionCompletedSignal(
    QuestForge.Schema.ActionType ActionType,
    uint ActionId,
    uint? TargetBaseId,
    float? TargetX = null,
    float? TargetY = null,
    float? TargetZ = null,
    int? TargetZone = null);

// Before:
public sealed record EmoteCompletedSignal(
    uint EmoteId,
    uint? TargetBaseId);

// After:
public sealed record EmoteCompletedSignal(
    uint EmoteId,
    uint? TargetBaseId,
    float? TargetX = null,
    float? TargetY = null,
    float? TargetZ = null,
    int? TargetZone = null);
```

**Rationale:** The `ITargetProbe` methods already return `(BaseId, X, Y, Z, Zone)`. We just need to thread the position through. Optional (defaulted) parameters maintain backward compat with all existing call sites (tests, SnapshotAggregator).

**Rejected alternative:** A separate `TargetPosition` signal. Adds unnecessary complexity when the position is always captured at the same moment as the BaseId.

**What breaks if violated:** StepFactory cannot build `NpcLocation` for use-action/use-emote steps.

### AL6 -- UIObserver: CaptureTargetBaseId becomes CaptureTargetInfo returning position

```csharp
// Before:
private uint? CaptureTargetBaseId()

// After:
private (uint BaseId, float X, float Y, float Z, int Zone)? CaptureTargetInfo()
```

All existing call sites that only need BaseId destructure to `.BaseId`. The PollPlayerEmote inline capture also gains position. This is a plugin-side-only change (no engine/test impact -- UIObserver tests use FakeTargetProbe which already returns the full tuple).

### AL7 -- SnapshotAggregator: OnActionCompleted and OnEmoteCompleted gain position parameters

```csharp
// Before:
public void OnActionCompleted(ActionType actionType, uint actionId, uint? targetBaseId)
public void OnEmoteCompleted(uint emoteId, uint? targetBaseId)

// After:
public void OnActionCompleted(ActionType actionType, uint actionId, uint? targetBaseId,
    float? targetX = null, float? targetY = null, float? targetZ = null, int? targetZone = null)
public void OnEmoteCompleted(uint emoteId, uint? targetBaseId,
    float? targetX = null, float? targetY = null, float? targetZ = null, int? targetZone = null)
```

Optional parameters with defaults = zero churn on existing test call sites.

### AL8 -- StepFactory builds Location from enriched signal

```csharp
// In StepFactory.Build, "use-action" case:
"use-action" => new UseActionStep
{
    Id = stepId,
    Expect = expectValue,
    ActionType = after?.ActionCompleted?.ActionType ?? ActionType.Action,
    ActionId = after?.ActionCompleted?.ActionId ?? 0u,
    TargetNpcId = after?.ActionCompleted?.TargetBaseId,
    Location = BuildLocationFromSignal(
        after?.ActionCompleted?.TargetBaseId,
        after?.ActionCompleted?.TargetX,
        after?.ActionCompleted?.TargetY,
        after?.ActionCompleted?.TargetZ,
        after?.ActionCompleted?.TargetZone),
},

// Helper:
private static NpcLocation? BuildLocationFromSignal(
    uint? baseId, float? x, float? y, float? z, int? zone)
{
    if (baseId is null || x is null || y is null || z is null || zone is null)
        return null;
    return new NpcLocation(
        NpcId: baseId.Value,
        Zone: zone.Value,
        Position: new Position3(x.Value, y.Value, z.Value));
}
```

**Why null-guard all fields:** If any position component is missing (e.g. self-cast with no target), Location is null and the step falls back to fire-immediately behavior.

### AL9 -- EngineHost UseAction/UseEmote dispatch unchanged

The EngineHost dispatch for UseAction and UseEmote already:
1. Stops navigation if navigating
2. Targets the entity via object table scan (implicit in `_actionExecutor.UseAction`)
3. Fires the action

No changes needed. The engine handles navigation via `EngineAction.Navigate` before ever reaching `EngineAction.UseAction`.

### AL10 -- Tools repo schema sync

The `questforge-tools/QuestForge.Schema/Step.cs` file must gain the same `NpcLocation? Location` property on both `UseActionStep` and `UseEmoteStep`. The tools repo already has `NpcLocation` defined (used by TalkStep, CombatStep, etc.).

### AL11 -- No new validator rules

The existing E8/E10 rules reject explicit `TargetNpcId == 0`. Location is entirely optional (null is valid). No new error/warning rules are needed:
- `Location.NpcId == 0` is not independently validated because Location is built from signals (StepFactory) or hand-authored; a zero NpcId in Location would be caught by the existing `TargetNpcId` validation if someone also sets TargetNpcId to 0.
- Location without TargetNpcId is a valid combination (navigate to a position, then self-cast).

---

## Task Breakdown

### Task AL-T1 -- Schema: add Location to UseActionStep and UseEmoteStep

**Both repos:** `questforge/QuestForge.Schema/Step.cs` and `questforge-tools/QuestForge.Schema/Step.cs`.

Add to `UseActionStep`:
```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public NpcLocation? Location { get; init; }
```

Add to `UseEmoteStep`:
```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public NpcLocation? Location { get; init; }
```

**Deliverables:**
- Property added to both step types in both repos
- JSON round-trip test in `QuestForge.Schema.Tests/RoundTripTests.cs` (UseActionStep with Location, UseEmoteStep with Location, and both without Location for backward compat)

### Task AL-T2 -- Engine: ResolveUseAction and ResolveUseEmote gain navigation

**File:** `QuestForge.Engine/QuestEngine.cs`

1. Change signatures to accept `WorldPosition? playerPos`
2. Add Guard 0 (navigation check) before existing guards
3. Update call sites in step-dispatch switch to pass `playerPos`

### Task AL-T3 -- Signal enrichment: ActionCompletedSignal and EmoteCompletedSignal

**File:** `QuestForge.Engine/Authoring/GameStateSnapshot.cs`

Add optional positional fields to both signal records (see AL5).

### Task AL-T4 -- SnapshotAggregator: thread position through

**File:** `QuestForge.Engine/Authoring/SnapshotAggregator.cs`

Update `OnActionCompleted` and `OnEmoteCompleted` signatures (see AL7).

### Task AL-T5 -- StepFactory: build Location from signal

**File:** `QuestForge.Engine/Authoring/StepFactory.cs`

Add `BuildLocationFromSignal` helper and use it in `"use-action"` and `"use-emote"` cases (see AL8).

### Task AL-T6 -- UIObserver: CaptureTargetInfo returns position

**File:** `QuestForge.Plugin.Tracing/UIObserver.cs`

Refactor `CaptureTargetBaseId` to `CaptureTargetInfo` returning the full tuple. Update `PollPlayerActionEffect` and `PollPlayerEmote` to thread position to aggregator calls.

### Task AL-T7 -- Engine tests

**File:** `QuestForge.Engine.Tests/Engine/UseActionStepTests.cs` (append), `QuestForge.Engine.Tests/Engine/UseEmoteStepTests.cs` (append)

New test scenarios per the GWT specs below.

### Task AL-T8 -- StepFactory tests

**File:** `QuestForge.Engine.Tests/Authoring/StepFactoryLocationTests.cs` (new)

Test that StepFactory builds Location correctly from enriched signals.

---

## Given-When-Then Specs

### Engine: UseActionStep with Location

#### A1 -- UseAction with Location, player far: emits Navigate

**Given:** A quest with one UseActionStep:
```json
{
  "type": "use-action",
  "id": "action-far",
  "actionType": "action",
  "actionId": 31,
  "targetNpcId": 2001234,
  "location": { "npcId": 2001234, "zone": 130, "position": { "x": 100, "y": 0, "z": 200 } },
  "expect": "questFlag(81001, 3)"
}
```
Player position is `(0, 0, 0)` (distance > DefaultStopDistance).
`FakeActionExecutor.ScriptNextStatus(Ready)`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.Navigate` with target `(100, 0, 200)` and `StoppingDistance == DefaultStopDistance (3.0)`.

#### A2 -- UseAction with Location, player close: emits UseAction (existing guards still run)

**Given:** Same step as A1. Player position is `(100, 0, 201)` (distance ~1.0, within DefaultStopDistance).
`FakeActionExecutor.ScriptNextStatus(Ready)`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.UseAction(Action, 31, NpcId(2001234))`.

#### A3 -- UseAction with Location, player close but action on cooldown: emits Wait

**Given:** Same step as A1. Player position is `(100, 0, 201)` (close).
`FakeActionExecutor.ScriptNextStatus(new ActionStatus.OnCooldown(TimeSpan.FromSeconds(2.5)))`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.Wait` with message containing "on cooldown".

#### A4 -- UseAction with null Location (backward compat): emits UseAction directly

**Given:** A UseActionStep with `Location = null`, `TargetNpcId = 2001234`.
`FakeActionExecutor.ScriptNextStatus(Ready)`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.UseAction(Action, 31, NpcId(2001234))`. No Navigate emitted. Identical to pre-change behavior.

#### A5 -- UseAction with Location and custom StopDistance

**Given:** A UseActionStep with Location at `(100, 0, 200)`, `StopDistance = 10.0f`.
Player position is `(95, 0, 200)` (distance 5.0, within custom StopDistance).
`FakeActionExecutor.ScriptNextStatus(Ready)`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.UseAction` (player is within custom stop distance).

#### A6 -- UseAction with Location, player far, no position available: fires action (fail-open)

**Given:** A UseActionStep with Location set. `playerPos` is null (position read failed).
`FakeActionExecutor.ScriptNextStatus(Ready)`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.UseAction` (fail-open, matching `ResolveInteractOrNavigate` behavior when `playerPos is null`).

### Engine: UseEmoteStep with Location

#### A7 -- UseEmote with Location, player far: emits Navigate

**Given:** A UseEmoteStep with `EmoteId = 17`, `TargetNpcId = 1000789`, `Location = { npcId: 1000789, zone: 130, position: (100, 0, 200) }`.
Player position is `(0, 0, 0)`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.Navigate` with target `(100, 0, 200)`.

#### A8 -- UseEmote with Location, player close: emits UseEmote

**Given:** Same step as A7. Player position is `(100, 0, 201)`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.UseEmote(17, NpcId(1000789), true)`.

#### A9 -- UseEmote with null Location (backward compat): emits UseEmote directly

**Given:** A UseEmoteStep with `Location = null`.

**When:** Engine ticks.

**Then:** Engine emits `EngineAction.UseEmote` directly. Identical to pre-change behavior.

### StepFactory: Location from signals

#### A10 -- StepFactory builds UseActionStep with Location from enriched ActionCompletedSignal

**Given:** A `GameStateSnapshot` where `ActionCompleted` is:
```csharp
new ActionCompletedSignal(ActionType.Action, 31u, 2001234u,
    TargetX: 100f, TargetY: 5f, TargetZ: 200f, TargetZone: 130)
```

**When:** `StepFactory.Build("use-action", ...)` is called.

**Then:** The resulting `UseActionStep` has:
- `TargetNpcId == 2001234`
- `Location` is not null
- `Location.NpcId == 2001234`
- `Location.Zone == 130`
- `Location.Position == new Position3(100f, 5f, 200f)`

#### A11 -- StepFactory builds UseEmoteStep with Location from enriched EmoteCompletedSignal

**Given:** A `GameStateSnapshot` where `EmoteCompleted` is:
```csharp
new EmoteCompletedSignal(17u, 1000789u,
    TargetX: 50f, TargetY: 0f, TargetZ: 80f, TargetZone: 130)
```

**When:** `StepFactory.Build("use-emote", ...)` is called.

**Then:** The resulting `UseEmoteStep` has:
- `TargetNpcId == 1000789`
- `Location` is not null
- `Location.NpcId == 1000789`
- `Location.Zone == 130`
- `Location.Position == new Position3(50f, 0f, 80f)`

#### A12 -- StepFactory builds UseActionStep without Location when signal has no position

**Given:** A `GameStateSnapshot` where `ActionCompleted` is:
```csharp
new ActionCompletedSignal(ActionType.Action, 31u, null) // self-cast, no target
```

**When:** `StepFactory.Build("use-action", ...)` is called.

**Then:** The resulting `UseActionStep` has `Location == null` and `TargetNpcId == null`.

#### A13 -- StepFactory builds UseActionStep without Location when signal has TargetBaseId but no position

**Given:** A `GameStateSnapshot` where `ActionCompleted` is:
```csharp
new ActionCompletedSignal(ActionType.Action, 31u, 2001234u)
// position fields are null (defaulted)
```

**When:** `StepFactory.Build("use-action", ...)` is called.

**Then:** The resulting `UseActionStep` has `TargetNpcId == 2001234` but `Location == null` (all position fields must be present to build Location).

### Schema round-trip

#### A14 -- UseActionStep with Location round-trips through JSON

**Given:** A UseActionStep with Location set.

**When:** Serialized to JSON and deserialized back.

**Then:** All fields including Location are preserved. The JSON includes `"location": { "npcId": ..., "zone": ..., "position": { ... } }`.

#### A15 -- UseActionStep without Location round-trips (no "location" key in JSON)

**Given:** A UseActionStep with `Location = null`.

**When:** Serialized to JSON.

**Then:** The JSON does NOT contain a `"location"` key (due to `JsonIgnoreCondition.WhenWritingNull`).

#### A16 -- UseEmoteStep with Location round-trips through JSON

Same as A14 but for UseEmoteStep.

### Signal enrichment

#### A17 -- SnapshotAggregator.OnActionCompleted with position sets all fields

**Given:** A fresh SnapshotAggregator.

**When:** `OnActionCompleted(ActionType.Action, 31u, 2001234u, 100f, 5f, 200f, 130)` is called.

**Then:** `agg.Current.ActionCompleted` has `TargetBaseId == 2001234`, `TargetX == 100f`, `TargetY == 5f`, `TargetZ == 200f`, `TargetZone == 130`.

#### A18 -- SnapshotAggregator.OnActionCompleted without position (backward compat)

**Given:** A fresh SnapshotAggregator.

**When:** `OnActionCompleted(ActionType.Action, 31u, 2001234u)` is called (no position args).

**Then:** `agg.Current.ActionCompleted` has `TargetBaseId == 2001234`, `TargetX == null`, `TargetY == null`, `TargetZ == null`, `TargetZone == null`.

#### A19 -- SnapshotAggregator.OnEmoteCompleted with position sets all fields

Same pattern as A17 but for `OnEmoteCompleted`.

---

## Implementation Order

### Phase A -- Schema (Task AL-T1)

Add `NpcLocation? Location` to UseActionStep and UseEmoteStep in both repos. Add round-trip tests. This is the foundation; everything else depends on it.

**Done before Phase B.**

### Phase B -- Signal enrichment (Tasks AL-T3, AL-T4, AL-T5, AL-T8)

Enrich `ActionCompletedSignal` and `EmoteCompletedSignal` with position fields. Update `SnapshotAggregator`. Update `StepFactory`. Write StepFactory tests.

These are engine-side (pure C#, testable in CI). No Dalamud dependency.

**Done before Phase C.**

### Phase C -- Engine navigation (Tasks AL-T2, AL-T7)

Add navigation guard to `ResolveUseAction` and `ResolveUseEmote`. Thread `playerPos` to both methods. Write engine tests.

**Done before Phase D.**

### Phase D -- UIObserver (Task AL-T6)

Refactor `CaptureTargetBaseId` to `CaptureTargetInfo`. Thread position through `PollPlayerActionEffect` and `PollPlayerEmote` to aggregator calls. UIObserver tests.

**Estimated duration:** 1-2 days total across all phases.

---

## Done Criteria

1. `UseActionStep` and `UseEmoteStep` in both repos have `NpcLocation? Location` with `[JsonIgnore(WhenWritingNull)]`.
2. JSON round-trip tests pass for steps with and without Location.
3. Engine emits `EngineAction.Navigate` when Location is present and player is beyond StopDistance.
4. Engine emits the action directly when Location is null (backward compat) or player is close.
5. `ResolveInteractOrNavigate` fail-open behavior preserved: null playerPos fires the action.
6. `ActionCompletedSignal` and `EmoteCompletedSignal` carry optional target position fields.
7. `SnapshotAggregator.OnActionCompleted`/`OnEmoteCompleted` accept position; existing call sites (no position args) continue to compile.
8. `StepFactory.Build("use-action", ...)` and `Build("use-emote", ...)` produce `Location` when signal has position, null when it does not.
9. `CaptureTargetInfo` in UIObserver returns position alongside BaseId; position is threaded to aggregator.
10. All existing UseAction/UseEmote tests continue to pass without modification (backward compat via optional params).

## Exclusions

- **No new validator rules.** Location is optional; null is always valid.
- **No EngineHost changes.** Navigation is handled by the engine emitting Navigate; EngineHost dispatch for UseAction/UseEmote is unchanged.
- **No tools-repo trace/fixture changes.** The schema change in tools is sufficient; no new capabilities or fixture shapes are needed.
- **No changes to the lazy-dismount exemption list.** UseAction and UseEmote are NOT exempt from dismount (they require the player to be on foot). This is unchanged.

---

## Ready for Test Creation

Tester: Write failing tests from the GWT specs above.
- Happy paths: 6 scenarios (A1, A2, A7, A8, A10, A11)
- Edge cases: 7 scenarios (A4, A5, A6, A9, A12, A13, A18)
- Error cases: 1 scenario (A3)
- Round-trip: 3 scenarios (A14, A15, A16)
- Signal: 2 scenarios (A17, A19)
- Expected total: ~19 tests across QuestForge.Engine.Tests, QuestForge.Schema.Tests
