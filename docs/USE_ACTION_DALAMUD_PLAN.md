# DalamudActionExecutor Implementation Plan

**Status:** ready for test creation

**Input docs:**
- `docs/USE_ACTION_STEP_PLAN.md` (engine slice this builds on — `IActionExecutor`, `ActionStatus`, `EngineAction.UseAction` already shipped in PR #100)
- `QuestForge.Adapters/Actions/IActionExecutor.cs` (the interface to implement)
- `QuestForge.Adapters/Actions/ActionStatus.cs` (the union to return)
- `QuestForge.Schema/SharedValueTypes.cs` (Schema `ActionType` enum — the input to mapping)
- `QuestForge.Adapters.Dalamud/Movement/DalamudMount.cs` (the thin-adapter precedent we mirror)
- `QuestForge.Adapters.Dalamud/Interaction/DalamudInteractor.cs:23-44` (ObjectTable scan + `TargetManager.Target` + `TargetSystem.Instance()` pattern)
- `QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs:38-44` (the existing `ActionManager.GetActionStatus` use for `CanMount`)
- `QuestForge.Adapters.Dalamud/Combat/WrathComboAdapter.cs:69-78` (precedent for ObjectTable lookup + `Result.Fail("targetNotFound", …)` message format)
- `QuestForge.Plugin/EngineHost.cs:316-441` (`DispatchAction` switch — Purchase / Teleport / Interact arms are the templates)
- `QuestForge.Plugin/EngineHost.cs:96-112` (host constructor — where the new `DalamudActionExecutor` field is constructed)
- `QuestForge.Plugin/EngineHost.cs:205-209` (`BeginRun` — where `QuestEngine` is constructed and `actionExecutor:` must be threaded through)
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\ActionManager.cs:92, 113, 122, 137` (canonical signatures for `UseAction`, `GetActionStatus`, `GetRecastTime`, `GetRecastTimeElapsed`)
- `C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\ActionManager.cs:395-417` (canonical `ActionType` enum — **member names verified**: `Action`, `EventItem`, `GeneralAction`. There is NO `KeyItem` member — the schema `KeyItem` maps to FFXIVClientStructs `EventItem`.)
- `QuestForge.Adapters.Tests/QuestForge.Adapters.Tests.csproj` (existing test project — Microsoft.NET.Sdk, net10.0; CANNOT host FFXIVClientStructs-typed tests, see Decision DAD-3)
- `QuestForge.Plugin.Tracing/QuestForge.Plugin.Tracing.csproj` (precedent for "extract pure logic into a Dalamud-free project so net10.0 tests can target it")

**Output (CI behavior):** Adding `_actionExecutor = new DalamudActionExecutor(services)` to `EngineHost` and a `case EngineAction.UseAction:` arm to `DispatchAction` makes `UseActionStep` actually fire in-game. A new test file `QuestForge.Adapters.Tests/Dalamud/ActionExecutorLogicTests.cs` reports ~12 unit tests green covering the pure-logic helpers (enum mapping, status interpretation, error-message formatting). The Dalamud-bound shell (ObjectTable scan, `am->UseAction` native call) is validated by in-game smoke on the Marauder L5 "Axe in the Stone" quest — NOT by automated test.

---

## Dependency graph

```
QuestForge.Adapters.Dalamud
   ├── Actions/ActionExecutorLogic.cs   (pure helpers — static class, no Dalamud calls)
   │      └── consumed by ↓
   ├── Actions/DalamudActionExecutor.cs (IActionExecutor shell — calls Dalamud + helpers)
   │      └── consumed by ↓
QuestForge.Plugin
   └── EngineHost (field + ctor wiring + DispatchAction arm)

QuestForge.Adapters.Tests           (net10.0, Microsoft.NET.Sdk — Dalamud-free)
   └── Dalamud/ActionExecutorLogicTests.cs
          └── tests the PURE helpers only, via a thin port (see Decision DAD-3)
```

**Build order:**
1. Pure helpers in `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` (raw — return-type uses FFXIVClientStructs enum so it lives in the Dalamud-bound assembly).
2. A **mirror** pure helper class in `QuestForge.Adapters/Actions/ActionStatusInterpreter.cs` containing the two FFXIVClientStructs-free helpers (status interpretation + reason formatter). Tests target the mirror. The Dalamud-bound `ActionExecutorLogic` delegates to the mirror for those two. Rationale: see Decision DAD-3.
3. Tests in `QuestForge.Adapters.Tests/Dalamud/ActionStatusInterpreterTests.cs` (note: filename uses `Interpreter` not `Logic` — see Decision DAD-3).
4. `DalamudActionExecutor` shell.
5. EngineHost wiring (`_actionExecutor` field, ctor construction, `BeginRun` ctor arg, `DispatchAction` switch arm).
6. Manual in-game smoke validation.

---

## Architectural decisions (read before coding)

### Decision DAD-1 — Pure-logic extraction is mandatory

The previous attempt at this slice put everything (mapping, status math, ObjectTable scan, native call) inside `DalamudActionExecutor`. The result was that the enum mapping (`Schema.ActionType → FFXIVClientStructs.ActionType`) and the status math (`(status, recast, elapsed) → ActionStatus`) — both of which are pure functions — became un-testable because the host project they live in cannot be loaded from a net10.0 test runner without Dalamud's NuGet feed configured.

**Rule:** any logic that does not call into `_svc.ObjectTable`, `ActionManager.Instance()`, `TargetManager`, or `TargetSystem` MUST live in a static helper class that is unit-testable. The shell (`DalamudActionExecutor`) is a one-screen pass-through: scan, target, fire, interpret.

**What breaks if violated:** silent regressions in the enum mapping (e.g. someone changes `KeyItem → Mount` because the FFXIVClientStructs enum was misread) will only surface in-game, with no failing test to catch them.

### Decision DAD-2 — `ActionExecutorLogic` lives in `QuestForge.Adapters.Dalamud/Actions/`

The mapping helper returns `FFXIVClientStructs.FFXIV.Client.Game.ActionType`. That type is only available in projects that target the Dalamud SDK. The helper therefore CANNOT live in `QuestForge.Adapters` (the engine-side assembly) without breaking the engine purity invariant (engine must not reference concrete Dalamud types) — and CANNOT be referenced by `QuestForge.Adapters.Tests` (net10.0, no Dalamud feed).

**Resolution:** the helper lives in `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs`. It is `internal static`. The shell calls it. Tests for the mapping helper are intentionally NOT in this plan — see Decision DAD-3 for the workaround.

**What breaks if violated:** if `ActionExecutorLogic` is hoisted into `QuestForge.Adapters` to make tests easier, the engine assembly now transitively references `FFXIVClientStructs`, which the engine purity invariant explicitly forbids.

### Decision DAD-3 — Two-helper split to maximise testability

The pure-logic surface splits into two halves:

| Helper | Inputs | Outputs | Where it lives | Tests in |
|---|---|---|---|---|
| `ActionExecutorLogic.ToFFXIVActionType(Schema.ActionType)` | Schema enum | FFXIVClientStructs enum | `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` | NONE (Dalamud-only; validated via smoke + Decision DAD-4 indirect test) |
| `ActionStatusInterpreter.InterpretStatus(int, float, float, double)` | Raw status code + recast floats | `ActionStatus` union | `QuestForge.Adapters/Actions/ActionStatusInterpreter.cs` | `QuestForge.Adapters.Tests/Actions/ActionStatusInterpreterTests.cs` |
| `ActionStatusInterpreter.FormatTargetNotFoundReason(uint)` | BaseId | string | (same file) | (same file) |

The two-helper split is necessary because:

1. **Status math (`InterpretStatus`) and reason formatting (`FormatTargetNotFoundReason`) do NOT need any FFXIVClientStructs type.** Both operate on primitives (`int`, `float`, `uint`, `string`). They can live in `QuestForge.Adapters` (the Dalamud-free assembly that the net10.0 test project already references) and be unit-tested directly with no test-infrastructure gymnastics. This is the bulk of the logic and is where bugs are most likely (epsilon thresholds, status-vs-cooldown precedence, overflow guards).

2. **The enum mapping (`ToFFXIVActionType`) DOES need the FFXIVClientStructs enum** as a return type. Moving it to a Dalamud-free assembly would mean returning `uint` and casting at the call site, which scatters the enum-value knowledge across two files and defeats the testability goal (the cast at the call site would be the bug-prone part, and it would still be in Dalamud-only code).

   **Indirect coverage for `ToFFXIVActionType`:** instead of a direct test, the spec specifies an exhaustive switch with a `default: throw new ArgumentOutOfRangeException(...)` arm. A simple compile-time guarantee (the switch covers every Schema `ActionType` member) plus a code-review checklist item is the substitute for an xUnit test. The actual values are documented in source as comments referencing the FFXIVClientStructs enum file. If a future helper assembly with a Dalamud-bound test runner is set up (issue tracker item, not in scope here), DA1-DA4 below are ready-to-write.

3. **Why not put `InterpretStatus` in the Dalamud-bound `ActionExecutorLogic`?** Because the test project that already exists (`QuestForge.Adapters.Tests`, net10.0) cannot reference Dalamud-bound assemblies. The choice is either (a) put `InterpretStatus` in a Dalamud-free assembly and test it there, or (b) create a new `QuestForge.Adapters.Dalamud.Tests` project targeting `net10.0-windows7.0` with the Dalamud SDK. (b) requires the Dalamud dev NuGet feed to be configured in CI, which is a non-trivial setup we are not undertaking in this slice. (a) is the explicit precedent established by `QuestForge.Plugin.Tracing` (extracted from `QuestForge.Plugin` solely to enable net10.0 tests — see `QuestForge.Plugin.Tests.csproj` lines 7-22).

**Final file layout:**

```
QuestForge.Adapters/Actions/
  ActionStatusInterpreter.cs           (NEW: static class with InterpretStatus + FormatTargetNotFoundReason)
  IActionExecutor.cs                   (existing)
  ActionStatus.cs                      (existing)
  ActionType.cs                        (does not exist; lives in Schema/SharedValueTypes.cs)

QuestForge.Adapters.Tests/Actions/
  ActionStatusInterpreterTests.cs      (NEW: ~10 tests targeting the pure helpers)

QuestForge.Adapters.Dalamud/Actions/
  ActionExecutorLogic.cs               (NEW: ToFFXIVActionType only; delegates to ActionStatusInterpreter for the rest)
  DalamudActionExecutor.cs             (NEW: the IActionExecutor shell)
```

**What breaks if violated:** if `InterpretStatus` is colocated with `ToFFXIVActionType` in the Dalamud-bound assembly, the epsilon-boundary tests (DA5-DA10) cannot run in CI without a Dalamud feed, and the previous attempt's failure mode (untested epsilon math) recurs.

### Decision DAD-4 — `ToFFXIVActionType` exhaustive switch with `default` throw

```csharp
// QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs
using ClientStructsActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using SchemaActionType = QuestForge.Schema.ActionType;

namespace QuestForge.Adapters.Dalamud.Actions;

internal static class ActionExecutorLogic
{
    /// <summary>
    /// Maps the schema-side ActionType (string-discriminated enum the engine and JSON use)
    /// to the FFXIVClientStructs ActionType (the uint the game's ActionManager expects).
    ///
    /// CANONICAL MAPPING (verified against
    /// C:\Users\publi\RiderProjects\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Game\ActionManager.cs:395-417):
    ///
    ///   Schema.Action        → ClientStructs.Action         (value 1; combat abilities, weaponskills, spells)
    ///   Schema.GeneralAction → ClientStructs.GeneralAction  (value 5; mount, sprint, teleport, return)
    ///   Schema.KeyItem       → ClientStructs.EventItem      (value 3; quest key items used as actions —
    ///                                                       NOTE the rename: FFXIVClientStructs does not
    ///                                                       have a "KeyItem" member; "EventItem" is the
    ///                                                       canonical name for the quest-item-as-action bucket)
    ///
    /// If Schema.ActionType gains a new member (per USE_ACTION_STEP_PLAN.md Decision UA2's deferred list:
    /// Item, Macro, Companion, Mount, BgcArmyAction), the switch MUST be extended in this method AND in
    /// the schema-side enum. The default arm throws so a missing case is loud at runtime.
    /// </summary>
    public static ClientStructsActionType ToFFXIVActionType(SchemaActionType type) => type switch
    {
        SchemaActionType.Action        => ClientStructsActionType.Action,
        SchemaActionType.GeneralAction => ClientStructsActionType.GeneralAction,
        SchemaActionType.KeyItem       => ClientStructsActionType.EventItem,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(type), type,
                 $"Schema.ActionType.{type} has no FFXIVClientStructs mapping — extend ActionExecutorLogic.ToFFXIVActionType")
    };
}
```

**Why throw on default rather than return `EventItem` or `Action`:** silently mapping an unknown schema value to a default would mean a new schema enum member silently fires the wrong action in-game. The throw surfaces the gap loudly at first use. The validator (Decision UA7 in `USE_ACTION_STEP_PLAN.md`) also rejects unknown enum strings at JSON parse time, so this throw is genuinely unreachable in well-formed data — it exists for the future-extension case where someone adds an enum member and forgets to update the switch.

**The previous attempt's `KeyItem → EventItem` guess is now codified.** The rationale (FFXIVClientStructs has no `KeyItem` member; `EventItem` is value 3 and is the bucket the game uses for quest-given key items that are usable as actions) is pinned in the source comment so a future reviewer can verify without chasing through external repos.

### Decision DAD-5 — `InterpretStatus` precedence: status code wins over cooldown

```csharp
// QuestForge.Adapters/Actions/ActionStatusInterpreter.cs
namespace QuestForge.Adapters.Actions;

public static class ActionStatusInterpreter
{
    /// <summary>
    /// Default epsilon for "is the action effectively off cooldown?" — 50 ms.
    /// Below this threshold, treat the action as Ready (the game accepts the call within
    /// a few frames of the recast completing; insisting on exactly-zero remaining produces
    /// spurious extra ticks at the cooldown boundary).
    /// </summary>
    public const double DefaultReadyEpsilonSeconds = 0.05;

    /// <summary>
    /// Interprets the raw triplet returned by FFXIVClientStructs ActionManager into the
    /// engine-facing ActionStatus union.
    ///
    /// PRECEDENCE (this order is load-bearing — see DA9):
    ///   1. statusCode != 0 → Unusable("status code {statusCode}")
    ///      Reason: the status code IS the game's "you cannot use this action right now"
    ///      verdict. It already accounts for cooldown internally (GetActionStatus's
    ///      checkRecastActive=true default), so a non-zero code means there's a structural
    ///      reason (wrong job, level too low, missing resources, out of range). Telling the
    ///      engine "Wait — it's on cooldown" would be a lie in that case.
    ///
    ///   2. remaining = recastSeconds - elapsedSeconds; if remaining > epsilon → OnCooldown(remaining)
    ///      Reason: status code 0 + non-zero remaining means the game considers the action
    ///      otherwise usable but the recast hasn't elapsed. This is the "wait" case.
    ///
    ///   3. otherwise → Ready
    ///      Reason: status code 0 + remaining at or below epsilon (including negative — see
    ///      DA10 for overflow guard) means fire it.
    ///
    /// NOTE: recastSeconds == 0 (action has no cooldown at all, e.g. Sprint at low elapsed)
    /// computes remaining = -elapsed, which is below epsilon → Ready. Confirmed correct.
    /// </summary>
    public static ActionStatus InterpretStatus(
        uint statusCode,
        float recastSeconds,
        float elapsedSeconds,
        double readyEpsilonSeconds = DefaultReadyEpsilonSeconds)
    {
        if (statusCode != 0)
            return new ActionStatus.Unusable($"status code {statusCode}");

        var remainingSeconds = (double)recastSeconds - (double)elapsedSeconds;
        if (remainingSeconds > readyEpsilonSeconds)
            return new ActionStatus.OnCooldown(TimeSpan.FromSeconds(remainingSeconds));

        return new ActionStatus.Ready();
    }

    /// <summary>
    /// Canonical phrasing for the "ObjectTable scan returned no match" failure case.
    /// Used by DalamudActionExecutor.UseAction when targetNpcId was supplied but the
    /// ObjectTable did not contain an object with that BaseId.
    ///
    /// Format: "no object in scene with BaseId {baseId}"
    /// Mirrors the WrathComboAdapter.SetTarget message format
    /// ("No live object for GameObjectId {target.Value}") but uses BaseId terminology
    /// because that is what UseActionStep.TargetNpcId carries (BNpcBase row id, NOT
    /// the live entity's GameObjectId — those are different identifiers).
    /// </summary>
    public static string FormatTargetNotFoundReason(uint baseId)
        => $"no object in scene with BaseId {baseId}";
}
```

**Note on `statusCode` type:** the FFXIVClientStructs signature is `uint GetActionStatus(...)`. Tests use `uint` literals (`580u`). The interface signature uses `uint`.

**Note on `remainingSeconds` precision:** `(double)recastSeconds - (double)elapsedSeconds` promotes to double to avoid float-subtraction precision loss near the boundary (e.g. `30.0f - 29.96f` in float gives `0.040001f`; in double gives `0.040000000000003...`, which is still under 0.05 so still Ready, but the double form is more robust as epsilons get tighter).

**Why epsilon is 50 ms:** the engine tick cadence is ~250 ms (per `EngineHost.cs:298` comment). At 50 ms below recast we are well within one tick of the action being truly ready, and `am->UseAction` queues an action that fires when the recast actually elapses (per the `UseActionMode` parameter — the default `None` mode does queue). Choosing a value smaller than the tick cadence avoids the "engine reports Ready, dispatches, game rejects with `actionStillOnCooldown` status code" race. Choosing it large (e.g. 1.0 s) would over-eagerly dispatch and trip the same race in the other direction.

**What breaks if violated:**
- If precedence is reversed (cooldown wins over status code): a player without the action learned would see the engine `Wait` forever (the action's cooldown is irrelevant, the action is structurally unusable — engine should AwaitUser instead).
- If epsilon is removed (strict `remaining > 0`): the engine spam-emits one extra `Wait` per recast cycle as the last 10-40 ms tick out. Cosmetic but noisy in traces.
- If overflow guard is removed (no double-promotion or no allowance for negative remaining): an action whose `elapsed > recast` (game returned a stale value, common at zone transition) gets reported as `OnCooldown(-2.4 s)` which would render meaninglessly in the engine's `Wait` reason string and confuse trace replay.

### Decision DAD-6 — Shell scans ObjectTable using `BaseId` (not `EntityId` or `GameObjectId`)

`UseActionStep.TargetNpcId` carries `BNpcBase` row id (the static template id), which `IGameObject.BaseId` exposes. The Dalamud APIs distinguish:

| Field | Type | Meaning |
|---|---|---|
| `IGameObject.BaseId` | `uint` | Static template id (BNpcBase row, or ENpcBase row for event NPCs) — matches what authors write in quest JSON |
| `IGameObject.EntityId` | `uint` | Per-instance world-unique id (changes each respawn) — used for enmity tracking |
| `IGameObject.GameObjectId` | `ulong` | Internal pointer-stable id — used as the `targetId` argument to `ActionManager.UseAction` |

The shell scans for `obj.BaseId == targetNpcId.Value` (matching `DalamudInteractor.InteractWith` at line 30), then captures the **`GameObjectId`** to pass into `am->UseAction(type, id, targetId: obj.GameObjectId)`. This mirrors the convention WrathComboAdapter uses for `SetTarget(ActorId)` where `ActorId.Value` is `GameObjectId`.

**Why not omit `targetId` and rely on `TargetManager.Target` alone?** `ActionManager.UseAction` resolves the target from its `targetId` argument first; only when `targetId == 0xE000_0000` (the sentinel meaning "use the current target") does it fall back to `TargetManager.Target`. The sentinel route works but the explicit-id route is more reliable when other plugins (BossMod, WrathCombo) may be racing the target slot. Both are set defensively: `TargetManager.Target = obj` AND `am->UseAction(..., targetId: obj.GameObjectId)`.

**Order is set-target-then-call.** Matches `DalamudInteractor.InteractWith` (lines 34-39): target assignment first, then the native call. This sequencing is required because some downstream observers (BossMod's auto-cancel, plugin chat triggers) watch `TargetManager.Target` to decide whether to interfere.

**Object kind filter:** `DalamudInteractor.InteractWith` filters to `EventNpc | BattleNpc | Aetheryte`. The same filter applies here for combat actions targeting NPCs — extending it to additional kinds would be speculative. **Filter: `EventNpc | BattleNpc`** (Aetheryte excluded because no realistic action targets an aetheryte). This is intentionally narrow; the validator will reject quest data that puts a non-NPC base id in `TargetNpcId`.

### Decision DAD-7 — `am == null` returns `Result.Fail("actionManagerUnavailable", ...)`

```csharp
// inside DalamudActionExecutor.UseAction
var am = ActionManager.Instance();
if (am is null)
    return Task.FromResult<Result<Unit>>(
        Result.Fail("actionManagerUnavailable", "ActionManager.Instance() returned null"));
```

Same posture for `GetActionStatus`:

```csharp
var am = ActionManager.Instance();
if (am is null)
    return Task.FromResult<Result<ActionStatus>>(
        Result.Fail<ActionStatus>("actionManagerUnavailable", "ActionManager.Instance() returned null"));
```

**Why explicit failure, not silent ok:** `DalamudMount.Mount` (the precedent) does `if (am != null) am->UseAction(...)` then returns `Task.CompletedTask` — a silent no-op on null. That's defensible for fire-and-forget mount/dismount (the engine re-reads `MountState` next tick anyway). For `IActionExecutor.UseAction` the engine consumes the `Result<Unit>` to decide whether to log a dispatch error, and for `GetActionStatus` the engine fail-opens on read failure (per `USE_ACTION_STEP_PLAN.md` Decision UA5). Returning `Result.Fail` makes both behaviors correct without changing the engine. Returning `Result.Ok` from a `null` ActionManager would falsely tell the engine "the button was pressed" and the engine would mark the step as having dispatched, which is a lie.

**This is rarely-if-ever observed in production** — `ActionManager.Instance()` is null only during game init / very early plugin load. But the guard costs one line and the trace shows the failure if it does happen.

### Decision DAD-8 — `am->UseAction` return value is discarded (best-effort, matches `DalamudMount`)

```csharp
// inside DalamudActionExecutor.UseAction, after target setup
am->UseAction(ToFFXIVActionType(type), actionId, targetId: targetGameObjectId);
return Task.FromResult<Result<Unit>>(Result.Ok());
```

`am->UseAction` returns `bool` — `false` means the game rejected the dispatch (e.g. cast bar already active, action queued behind another animation). `DalamudMount` discards this value with the rationale: "the engine re-reads `MountState` on the next tick to determine outcome" (per `DalamudMount.cs:13-17`).

The same rationale applies here: the engine uses `Step.Expect` to verify the action's effect landed; it does NOT use the `UseAction` return value as a success signal (per `USE_ACTION_STEP_PLAN.md` Decision UA1's docstring: "Success means 'ActionManager.UseAction returned true' — NOT 'the action's effect landed'"). Returning `Result.Ok` on dispatch + relying on `Expect` to catch failure is the established posture.

**What breaks if violated:** if we check the return and `Result.Fail` on `false`, every transient game-side rejection (e.g. animation lock between two consecutive ticks) becomes a logged dispatch error in chat, spamming the user. The engine's stateless retry handles the re-attempt cleanly without the noise.

### Decision DAD-9 — EngineHost dispatch arm: stop navigation first + debounced log

Position the arm between `EngineAction.Purchase` (line 427) and `EngineAction.Wait` (line 443). Mirror the Purchase arm pattern:

```csharp
case EngineAction.UseAction ua:
    DebounceLog(
        $"useaction:{ua.Type}:{ua.ActionId}:{ua.TargetNpcId?.Value}",
        $"[UseAction] type={ua.Type} id={ua.ActionId}" +
        (ua.TargetNpcId is { } id ? $" target={id.Value}" : " (self)"));
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _actionExecutor.UseAction(ua.Type, ua.ActionId, ua.TargetNpcId, ct);
    break;
```

**Why stop navigation first:** mirrors the Interact and Purchase arms. The lazy-dismount hook (lines 276-314) already fires before the switch enters this arm (since `UseAction` is not in the exemption list per `USE_ACTION_STEP_PLAN.md` Decision UA6). But vnavmesh's pathfinding loop can still be ticking when we arrive at the arm — calling `_navigator.Stop` is cheap and prevents the player from continuing to walk while the action animation plays. The `IsNavigating` guard avoids a no-op `Stop` call in the common case.

**Why `TryCutsceneSkipConfirm`:** quest-related actions (e.g. ARR job-action-quests) sometimes fire mid-cutscene; the engine emits `UseAction` while the cutscene is still rolling. Skipping the cutscene confirm dialog lets the player return to control faster. Mirrors every other dispatch arm.

**Why debounced (not always-on) log:** the engine re-emits `UseAction` every tick while `Expect` is unmet (the stateless retry — `USE_ACTION_STEP_PLAN.md` U6). Without debounce, a 10-second action-then-wait-for-expect cycle dumps ~40 `[UseAction]` lines into Dalamud's log. The `DebounceLog` helper logs once per 10 seconds per dedup key (`useaction:{Type}:{ActionId}:{TargetNpcId}`) — see `EngineHost.cs:91-94` for the existing interval definition. Identical to Purchase and Navigate arms.

**Why no `_lastDispatchedActionWasUseAction` tracking:** unlike `Purchase` (which needs a deferred shop-close), `UseAction` has no follow-up cleanup. The shell is fire-and-forget.

### Decision DAD-10 — `BeginRun` constructs `DalamudActionExecutor` once at host construction time, NOT per-run

```csharp
// EngineHost field, near _mount / _combat declarations (line 42-47)
private readonly DalamudActionExecutor _actionExecutor;

// EngineHost constructor body, near line 106
_actionExecutor = new DalamudActionExecutor(services);

// EngineHost.BeginRun, line 205-209, modified ctor call:
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor,
    actionExecutor: _actionExecutor);
```

**Why construction is once-per-host, not once-per-run:** `DalamudActionExecutor` is stateless (no per-run timers, no per-run lease management). Every `Mount` / `Navigator` / `Interactor` / `Combat` field is also constructed once at host time (see `EngineHost.cs:100-111`). Following the same pattern.

**No `RecordingActionExecutor`** in this slice. The combat-act trace pattern (`RecordingCombat` in `QuestForge.Adapters/Recording/`) records `SetTarget` / `ClearTarget` / `StartRotation` / `StopRotation` as observation events for debugging. We could wrap `IActionExecutor.UseAction` calls the same way, but:

1. The engine already emits a `DecisionEvent` with `ActionType == "UseAction"` (per the harness `EmitActionSubmitted("UseAction", …)` arm in `EngineTestHarness`, which is mirrored by the real engine in `QuestEngine.Tick`). The dispatch is therefore already visible in the trace via the engine-side decision event, before the adapter is even called.
2. The shell does not introduce any non-deterministic behavior that replay would need to re-derive (target lookup is from ObjectTable observations, which `RecordingGameStateProvider` already captures).

If a future debugging need calls for adapter-act capture (e.g. "did we actually call into ActionManager?" mid-incident), add `RecordingActionExecutor` then. Not in v1 scope.

### Decision DAD-11 — No `IPlayerCharacter` self-target sentinel handling in the shell

When `targetNpcId is null` (self-cast), the shell does NOT scan ObjectTable. It calls `am->UseAction(type, id)` with the default `targetId` (`0xE000_0000` — the "current target / self" sentinel) and does NOT touch `TargetManager.Target`. This mirrors `DalamudMount.Mount` which also calls `UseAction(GeneralAction, 9)` with no target argument and lets the game pick.

**Why not write `TargetManager.Target = null` for self-cast:** clobbering the user's existing target on every self-cast `UseAction` would be a poor experience (the user may have a target acquired manually for situational awareness). The sentinel routing in `am->UseAction` is the canonical way to say "self".

**What this means for tests:** the shell's self-cast path is one line. The Dalamud-bound logic is not testable in this plan; smoke-test only.

---

## File layout (summary)

| File | Status | Purpose |
|---|---|---|
| `QuestForge.Adapters/Actions/ActionStatusInterpreter.cs` | NEW | Pure helpers `InterpretStatus`, `FormatTargetNotFoundReason` |
| `QuestForge.Adapters.Tests/Actions/ActionStatusInterpreterTests.cs` | NEW | ~10 xUnit tests for the pure helpers |
| `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` | NEW | `ToFFXIVActionType` only (Dalamud-bound; no test) |
| `QuestForge.Adapters.Dalamud/Actions/DalamudActionExecutor.cs` | NEW | The `IActionExecutor` shell |
| `QuestForge.Plugin/EngineHost.cs` | MODIFY | Field + ctor construct + `BeginRun` arg + `DispatchAction` case |

Note the test folder structure: `QuestForge.Adapters.Tests/Actions/` (not `/Dalamud/`). The "Dalamud" path the user mentioned in the brief is a misnomer — the tests target a Dalamud-free helper in the Dalamud-free `QuestForge.Adapters` assembly (per Decision DAD-3). The folder name reflects what the tests cover, not where the production code lives.

---

## A. `ActionStatusInterpreter` — pure-logic surface

### A.1 `InterpretStatus(uint statusCode, float recastSeconds, float elapsedSeconds, double readyEpsilonSeconds = 0.05)`

**Signature:**

```csharp
public static ActionStatus InterpretStatus(
    uint statusCode,
    float recastSeconds,
    float elapsedSeconds,
    double readyEpsilonSeconds = DefaultReadyEpsilonSeconds);
```

**Branch table:**

| Inputs | Output | Test |
|---|---|---|
| `statusCode != 0` | `Unusable($"status code {statusCode}")` | DA8, DA9 |
| `statusCode == 0`, `recast - elapsed > epsilon` | `OnCooldown(TimeSpan.FromSeconds(recast - elapsed))` | DA6 |
| `statusCode == 0`, `recast - elapsed <= epsilon` | `Ready` | DA5, DA7, DA10 |

**Test scenarios:** DA5, DA6, DA7, DA8, DA9, DA10.

### A.2 `FormatTargetNotFoundReason(uint baseId)`

**Signature:**

```csharp
public static string FormatTargetNotFoundReason(uint baseId);
```

**Behavior:** returns `$"no object in scene with BaseId {baseId}"`. Trivial; tested for the exact format so that downstream string-matching consumers (validator log scraping, error-message regex tests) have a contract.

**Test scenario:** DA11.

---

## B. `ActionExecutorLogic` — Dalamud-bound mapping helper

### B.1 `ToFFXIVActionType(Schema.ActionType type)`

**Signature:**

```csharp
internal static FFXIVClientStructs.FFXIV.Client.Game.ActionType ToFFXIVActionType(
    QuestForge.Schema.ActionType type);
```

**Mapping table (verified against canonical source):**

| Schema | FFXIVClientStructs | uint | Rationale |
|---|---|---|---|
| `Action` | `Action` | 1 | Combat abilities, weaponskills, spells |
| `GeneralAction` | `GeneralAction` | 5 | Mount, sprint, teleport, return |
| `KeyItem` | `EventItem` | 3 | Quest key items used as actions (rename: FFXIVClientStructs has no `KeyItem`) |
| (any other) | (throws `ArgumentOutOfRangeException`) | — | Default arm — Decision DAD-4 |

**No direct test.** Indirect coverage:
- Compile-time: exhaustive switch on a closed enum (compiler warning if a new member is added without updating the switch).
- Smoke test: Marauder L5 "Axe in the Stone" exercises the `Action` arm; future quests using key-item or general-action will exercise the other two.

If a Dalamud-targeting test project is set up in a future ticket, the test scenarios DA1-DA4 (defined below) are ready to copy in.

---

## C. `DalamudActionExecutor` — `IActionExecutor` shell

### C.1 Constructor

```csharp
public sealed class DalamudActionExecutor : IActionExecutor
{
    private readonly PluginServices _svc;

    public DalamudActionExecutor(PluginServices svc) => _svc = svc;

    // … methods below
}
```

Identical pattern to `DalamudMount(PluginServices svc)` and `DalamudInteractor(PluginServices svc)`.

### C.2 `UseAction(ActionType type, uint actionId, NpcId? targetNpcId, CancellationToken ct)`

```csharp
public unsafe Task<Result<Unit>> UseAction(
    ActionType type,
    uint actionId,
    NpcId? targetNpcId,
    CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    var am = ActionManager.Instance();
    if (am is null)
        return Task.FromResult<Result<Unit>>(
            Result.Fail("actionManagerUnavailable", "ActionManager.Instance() returned null"));

    ulong targetGameObjectId = 0xE000_0000UL; // sentinel: self / current target

    if (targetNpcId is { } id)
    {
        IGameObject? found = null;
        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null || obj.BaseId != id.Value) continue;
            if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;
            found = obj;
            break;
        }

        if (found is null)
            return Task.FromResult<Result<Unit>>(
                Result.Fail("targetNotFound", ActionStatusInterpreter.FormatTargetNotFoundReason(id.Value)));

        _svc.TargetManager.Target = found;
        targetGameObjectId = found.GameObjectId;
    }

    am->UseAction(ActionExecutorLogic.ToFFXIVActionType(type), actionId, targetId: targetGameObjectId);
    return Task.FromResult<Result<Unit>>(Result.Ok());
}
```

**Return-value contract:**
- `Result.Ok()` — the call was dispatched (target found, native call invoked; whether the game accepted is not asserted here, per Decision DAD-8).
- `Result.Fail("actionManagerUnavailable", …)` — `ActionManager.Instance()` returned null (Decision DAD-7).
- `Result.Fail("targetNotFound", …)` — `targetNpcId` was supplied but no matching `EventNpc`/`BattleNpc` with that BaseId in ObjectTable (Decision DAD-6).

### C.3 `GetActionStatus(ActionType type, uint actionId, CancellationToken ct)`

```csharp
public unsafe Task<Result<ActionStatus>> GetActionStatus(
    ActionType type,
    uint actionId,
    CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    var am = ActionManager.Instance();
    if (am is null)
        return Task.FromResult<Result<ActionStatus>>(
            Result.Fail<ActionStatus>("actionManagerUnavailable", "ActionManager.Instance() returned null"));

    var clientStructsType = ActionExecutorLogic.ToFFXIVActionType(type);
    var statusCode = am->GetActionStatus(clientStructsType, actionId);
    var recastSeconds = am->GetRecastTime(clientStructsType, actionId);
    var elapsedSeconds = am->GetRecastTimeElapsed(clientStructsType, actionId);

    return Task.FromResult<Result<ActionStatus>>(
        Result.Ok(ActionStatusInterpreter.InterpretStatus(statusCode, recastSeconds, elapsedSeconds)));
}
```

**Call-order rationale:** read `GetActionStatus` first (cheap, exits fast on non-zero), then the two recast floats (only meaningful when status is 0). Reading all three unconditionally is the simpler shape and the cost difference is unmeasurable. If a profile shows the recast reads dominating, gate them on `statusCode == 0` later — premature optimisation otherwise.

**No retry-on-zero-elapsed:** at zone transition `am->GetRecastTimeElapsed` can briefly return 0 for any action regardless of true cooldown state. The interpreter handles this gracefully (`recast - 0 = recast`, reported as `OnCooldown(recast)`), and the engine's stateless re-read next tick gets the corrected value. No special handling needed.

---

## D. EngineHost wiring

### D.1 Field declaration

In the field block around `EngineHost.cs:36-47`, add:

```csharp
private readonly DalamudActionExecutor _actionExecutor;
```

(Position: after `_mount`, before `_combat` — alphabetical-ish grouping with the other interaction adapters.)

### D.2 Constructor

In `EngineHost(PluginServices, TraceSession)` around `EngineHost.cs:96-112`, after the `_mount = new DalamudMount(services);` line, add:

```csharp
_actionExecutor = new DalamudActionExecutor(services);
```

### D.3 `BeginRun` engine construction

Modify `EngineHost.cs:205-209` from:

```csharp
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor);
```

to:

```csharp
_engine = new QuestEngine(
    gs, qs, _navigator, _teleporter, _interactor,
    _recordingCombat, _gear, _minigames, _dialogue, _timing,
    _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
    vendor: _vendor,
    actionExecutor: _actionExecutor);
```

After this wiring, the engine's `ResolveUseAction` (already shipped) stops returning `AwaitUser("UseActionStep dispatched but no IActionExecutor wired …")` and starts actually planning UseAction dispatches.

### D.4 `DispatchAction` switch arm

Insert the case from Decision DAD-9 between the existing `EngineAction.Purchase` case (ends at `EngineHost.cs:441`) and the `EngineAction.Wait` case (starts at `EngineHost.cs:443`):

```csharp
case EngineAction.UseAction ua:
    DebounceLog(
        $"useaction:{ua.Type}:{ua.ActionId}:{ua.TargetNpcId?.Value}",
        $"[UseAction] type={ua.Type} id={ua.ActionId}" +
        (ua.TargetNpcId is { } id ? $" target={id.Value}" : " (self)"));
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    TryCutsceneSkipConfirm();
    await _actionExecutor.UseAction(ua.Type, ua.ActionId, ua.TargetNpcId, ct);
    break;
```

### D.5 Optional debug accessor

Mirroring `DebugCombat`, `DebugVendor`, `DebugMount` (lines 129-133) — useful for a future `/qf debug action <type> <id> [<targetId>]` subcommand. Not required by this slice, but a one-line add at low cost:

```csharp
public IActionExecutor DebugActionExecutor => _actionExecutor;
```

Builder may include or defer at discretion.

---

## E. Test scenarios

All tests live in `QuestForge.Adapters.Tests/Actions/ActionStatusInterpreterTests.cs` (single file). xUnit `[Fact]` per scenario; no `[Theory]` data churn — the inputs are small and named.

### Tests of `InterpretStatus`

#### DA5 — Recast complete, status clean → Ready

```csharp
[Fact]
public void InterpretStatus_RecastFullyElapsed_ReturnsReady()
{
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 30.0f);

    Assert.IsType<ActionStatus.Ready>(status);
}
```

**Given:** action's recast is fully elapsed (`elapsed == recast`), status code is 0 (no error). **When:** interpret. **Then:** `Ready`.

#### DA6 — Mid-cooldown → OnCooldown with correct remaining

```csharp
[Fact]
public void InterpretStatus_FiveSecondsRemaining_ReturnsOnCooldownWithRemaining()
{
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 25.0f);

    var onCooldown = Assert.IsType<ActionStatus.OnCooldown>(status);
    Assert.Equal(5.0, onCooldown.Remaining.TotalSeconds, precision: 3);
}
```

**Given:** 5 seconds remaining on a 30 s recast, no error. **When:** interpret. **Then:** `OnCooldown(5 s)`.

#### DA7 — Within epsilon → Ready (boundary test)

```csharp
[Fact]
public void InterpretStatus_FortyMillisecondsRemaining_ReturnsReady_BelowDefaultEpsilon()
{
    // recast - elapsed = 0.04 s; default epsilon = 0.05 s. 0.04 < 0.05 → Ready.
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 29.96f);

    Assert.IsType<ActionStatus.Ready>(status);
}
```

**Given:** 40 ms remaining (under the 50 ms default epsilon). **When:** interpret with default epsilon. **Then:** `Ready` (not `OnCooldown`).

#### DA8 — Non-zero status code → Unusable with code in reason

```csharp
[Fact]
public void InterpretStatus_NonZeroStatusCode_ReturnsUnusableWithCodeInReason()
{
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 580u, recastSeconds: 30.0f, elapsedSeconds: 0.0f);

    var unusable = Assert.IsType<ActionStatus.Unusable>(status);
    Assert.Contains("580", unusable.Reason);
}
```

**Given:** game returned status code 580 (no specific game-side semantic — picked as a representative non-zero error). **When:** interpret. **Then:** `Unusable` whose `Reason` contains the substring `"580"` (exact phrasing per Decision DAD-5: `"status code 580"`).

#### DA9 — Status code AND cooldown both present → status code wins

```csharp
[Fact]
public void InterpretStatus_StatusCodeAndCooldown_StatusCodeWins()
{
    // Both error code AND mid-cooldown. The interpreter must report Unusable
    // (Decision DAD-5 precedence): the status code is the game's authoritative
    // "you cannot use this" verdict and supersedes the cooldown read.
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 580u, recastSeconds: 30.0f, elapsedSeconds: 25.0f);

    Assert.IsType<ActionStatus.Unusable>(status);
}
```

**Given:** status code is non-zero AND recast is mid-elapse. **When:** interpret. **Then:** `Unusable` (NOT `OnCooldown`). Pins Decision DAD-5.

#### DA10 — Negative remaining (overflow guard) → Ready

```csharp
[Fact]
public void InterpretStatus_NegativeRemaining_ReturnsReady_OverflowGuard()
{
    // Possible at zone transition: elapsed reports stale value greater than recast.
    // remaining = recast - elapsed = 30 - 35 = -5. Interpreter must not crash;
    // must not return OnCooldown(-5s); must report Ready.
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 35.0f);

    Assert.IsType<ActionStatus.Ready>(status);
}
```

**Given:** elapsed > recast (a real-world artifact of zone transition / cache lag). **When:** interpret. **Then:** `Ready` (negative remaining is below epsilon, falls through to the Ready arm). Pins Decision DAD-5's overflow comment.

#### DA10b (optional) — Explicit epsilon override → Ready/OnCooldown boundary respects the override

```csharp
[Fact]
public void InterpretStatus_CustomEpsilon_RespectsOverride()
{
    // 0.2 s remaining; default epsilon (0.05 s) would say OnCooldown.
    // Override epsilon to 0.5 s; should say Ready.
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 29.8f,
        readyEpsilonSeconds: 0.5);

    Assert.IsType<ActionStatus.Ready>(status);
}
```

**Given:** 200 ms remaining, caller supplies `readyEpsilonSeconds: 0.5`. **When:** interpret. **Then:** `Ready` (override changes the boundary).

This is a defensive test that documents the API contract for callers that might want a custom epsilon (e.g. a future stress test that wants stricter readiness semantics).

#### DA10c (optional) — Zero recast (action with no cooldown) → Ready

```csharp
[Fact]
public void InterpretStatus_ZeroRecast_ReturnsReady()
{
    // Some general actions (sprint outside combat) report recast = 0.
    // remaining = 0 - 0 = 0; 0 <= epsilon → Ready.
    var status = ActionStatusInterpreter.InterpretStatus(
        statusCode: 0u, recastSeconds: 0.0f, elapsedSeconds: 0.0f);

    Assert.IsType<ActionStatus.Ready>(status);
}
```

**Given:** action has no cooldown, fresh. **When:** interpret. **Then:** `Ready`.

### Tests of `FormatTargetNotFoundReason`

#### DA11 — BaseId formatted into the canonical phrase

```csharp
[Fact]
public void FormatTargetNotFoundReason_IncludesBaseIdInExactPhrase()
{
    var reason = ActionStatusInterpreter.FormatTargetNotFoundReason(2001234u);

    Assert.Equal("no object in scene with BaseId 2001234", reason);
}
```

**Given:** BaseId 2001234. **When:** format. **Then:** exact phrase `"no object in scene with BaseId 2001234"`.

#### DA11b (optional) — Zero BaseId still formats cleanly

```csharp
[Fact]
public void FormatTargetNotFoundReason_ZeroBaseId_FormatsWithZero()
{
    var reason = ActionStatusInterpreter.FormatTargetNotFoundReason(0u);

    Assert.Equal("no object in scene with BaseId 0", reason);
}
```

**Given:** BaseId 0 (defensive — the validator should reject this in quest data, but the helper must not crash). **When:** format. **Then:** `"no object in scene with BaseId 0"`.

### Tests of `ToFFXIVActionType` (DEFINED HERE, not implemented unless a Dalamud test project is set up)

These are the tests that **would be written** if `QuestForge.Adapters.Tests` could reference `QuestForge.Adapters.Dalamud`. Per Decision DAD-3 they are not implemented in this slice. They are recorded here so that a future ticket to add a Dalamud-bound test project can pick them up verbatim.

#### DA1 — `Schema.Action` → `ClientStructs.Action`

```csharp
[Fact]
public void ToFFXIVActionType_Action_MapsToAction()
{
    var result = ActionExecutorLogic.ToFFXIVActionType(QuestForge.Schema.ActionType.Action);
    Assert.Equal(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, result);
}
```

#### DA2 — `Schema.GeneralAction` → `ClientStructs.GeneralAction`

```csharp
[Fact]
public void ToFFXIVActionType_GeneralAction_MapsToGeneralAction()
{
    var result = ActionExecutorLogic.ToFFXIVActionType(QuestForge.Schema.ActionType.GeneralAction);
    Assert.Equal(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, result);
}
```

#### DA3 — `Schema.KeyItem` → `ClientStructs.EventItem` (the rename verification)

```csharp
[Fact]
public void ToFFXIVActionType_KeyItem_MapsToEventItem_NotKeyItem()
{
    // Critical: FFXIVClientStructs has no "KeyItem" member. The canonical name
    // for quest-item-as-action is EventItem (value 3). The previous attempt at
    // this slice mapped via this same value; this test pins the choice so it
    // cannot silently regress to e.g. ClientStructs.Item (value 2).
    var result = ActionExecutorLogic.ToFFXIVActionType(QuestForge.Schema.ActionType.KeyItem);
    Assert.Equal(FFXIVClientStructs.FFXIV.Client.Game.ActionType.EventItem, result);
}
```

#### DA4 — Invalid enum value → `ArgumentOutOfRangeException`

```csharp
[Fact]
public void ToFFXIVActionType_InvalidEnumValue_Throws()
{
    // Cast a bogus int into the enum to bypass the closed-enum compile check.
    var bogus = (QuestForge.Schema.ActionType)999;

    Assert.Throws<ArgumentOutOfRangeException>(
        () => ActionExecutorLogic.ToFFXIVActionType(bogus));
}
```

**Note:** the throw text contains `"extend ActionExecutorLogic.ToFFXIVActionType"` so future contributors who hit this in development get a self-explanatory message.

---

## F. Implementation order

**Phase A — Pure helpers + their tests (15 min, TDD)**

1. Create `QuestForge.Adapters/Actions/ActionStatusInterpreter.cs` with the two helper signatures, bodies stubbed to throw `NotImplementedException`.
2. Tester writes DA5-DA11 (+ optional DA10b, DA10c, DA11b). All red.
3. Implement `InterpretStatus` per Decision DAD-5 and `FormatTargetNotFoundReason` per Decision DAD-5. All green.

**Phase B — Dalamud-bound mapping helper (5 min, no test in this slice)**

1. Create `QuestForge.Adapters.Dalamud/Actions/ActionExecutorLogic.cs` with `ToFFXIVActionType` per Decision DAD-4.
2. Verify it compiles (relies on FFXIVClientStructs being available — `QuestForge.Adapters.Dalamud.csproj` already targets Dalamud SDK).

**Phase C — `DalamudActionExecutor` shell (15 min)**

1. Create `QuestForge.Adapters.Dalamud/Actions/DalamudActionExecutor.cs` per §C.
2. Verify it compiles.

**Phase D — EngineHost wiring (10 min)**

1. Add field, ctor construct, `BeginRun` arg per §D.1-D.3.
2. Add `DispatchAction` switch arm per §D.4.
3. Verify it compiles and all existing tests still pass (no engine-side test changes — the engine wiring shipped in PR #100 and is already exercised by `UseActionStepTests`).

**Phase E — In-game smoke (manual, 20-30 min)**

1. Author the Marauder L5 "Axe in the Stone" quest data (separate PR or local-only fixture).
2. Run on character.
3. Verify the action fires on the rocks; verify status reads produce sensible engine `Wait` reasons when on cooldown.
4. If smoke passes: merge. If smoke fails: bug report, do NOT fall back to non-TDD edits.

Total dev time: ~45 min code + ~30 min smoke ≈ 1.5 hours.

---

## G. Done criteria

1. `dotnet test QuestForge.Adapters.Tests --filter FullyQualifiedName~ActionStatusInterpreterTests` reports all ~10 tests green.
2. `dotnet build` succeeds (no warnings as errors regression in `QuestForge.Adapters.Dalamud` or `QuestForge.Plugin`).
3. `QuestForge.Plugin/EngineHost.cs:205-209` constructs `QuestEngine` with `actionExecutor: _actionExecutor` (no longer null).
4. The `case EngineAction.UseAction:` arm exists in `EngineHost.cs:DispatchAction` between the Purchase and Wait cases.
5. Running an authored quest containing a `use-action` step (e.g. Marauder L5 "Axe in the Stone") on a character causes the action to fire in-game on the targeted object. Manual verification; observable in `/xllog`.
6. The engine's `ResolveUseAction` no longer returns the `AwaitUser("UseActionStep dispatched but no IActionExecutor wired …")` fallback (per `QuestEngine.cs:829-831`) when `EngineHost` is the host.
7. No regression in existing engine, schema, or plugin tests (`dotnet test`).

---

## H. Exclusions (what this plan does NOT include)

- **`RecordingActionExecutor`** (recording proxy for `IActionExecutor`). Deferred until a debugging case demands per-call adapter-act capture. The engine already emits decision events for `EngineAction.UseAction` via the existing trace machinery; the dispatch is visible without an adapter-level wrap.
- **Engine-side tests for the wiring (E2E EngineHost → DalamudActionExecutor)**. The engine's `ResolveUseAction` is already covered by `UseActionStepTests` (PR #100). The Dalamud-bound `DalamudActionExecutor` shell is validated by smoke per §E. No additional engine test is justified.
- **Auto-face target** (rotate player toward target before firing). The Marauder L5 quest doesn't need it. If a future action requires positionals and the auto-face becomes a real-world authoring pain, add `LocalPlayer.Rotation = atan2(target - player)` in the shell before the `am->UseAction` call. Easy follow-up.
- **Cooldown-aware tick scheduling** (engine skips ticks when `OnCooldown.Remaining > tickInterval`). Out of scope; engine polls per-tick and re-reads. Future optimisation if profiling shows the GetActionStatus reads are hot.
- **Dalamud-bound test project for `ToFFXIVActionType`** (`QuestForge.Adapters.Dalamud.Tests` with Dalamud SDK + dev NuGet feed). Tracked as a future ticket. DA1-DA4 in §E are ready to copy in when that project exists.
- **A `/qf debug useaction <type> <id> [<targetId>]` subcommand**. Optional one-line `DebugActionExecutor` accessor in §D.5 makes it trivial to add later but the subcommand itself is not in scope.
- **Validator rules** for `use-action-*` (still pending from `USE_ACTION_STEP_PLAN.md` Decision UA7 — a separate validator-side PR).
- **Marauder L5 quest data file** (`questforge-data` PR). Authored by the data team; this plan only proves the engine + shell + helper surface.
- **NG+ behavior verification** for use-action steps. NG+ does not change the action dispatch semantics, so no special handling is expected; if NG+ exposes a quirk (e.g. job-locked actions reporting different status codes during a replay), that's a follow-up bug ticket.
- **Multi-target / area-targeted actions** (`am->UseActionLocation` with a `Vector3*` ground target). The `UseActionLocation` API exists in FFXIVClientStructs but no authored quest needs ground targeting. Schema-side extension required first (`UseActionStep.GroundTarget: Position3?`).

---

## I. Open questions / decisions to call out

| Question | Recommendation | Rationale | Decision |
|---|---|---|---|
| `am == null`: fail or no-op? | **Fail with `Result.Fail("actionManagerUnavailable", …)`** | Engine consumes the Result to decide retry vs AwaitUser; silent ok would lie. | DAD-7 |
| Discard or check `am->UseAction` return bool? | **Discard** | Mirrors `DalamudMount`; engine uses `Expect` to verify outcome. | DAD-8 |
| Set target before or after `UseAction`? | **Before** | Mirrors `DalamudInteractor.InteractWith`; other plugins observe target slot. | DAD-6 |
| Debounced or always-on log in dispatch arm? | **Debounced** | Engine stateless-retries `UseAction` every tick while Expect unmet; always-on spams chat. | DAD-9 |
| Stop navigation first in dispatch arm? | **Yes** (cheap `IsNavigating` guard) | Mirrors Interact/Purchase; vnavmesh may still be ticking. | DAD-9 |
| Test the enum mapping directly? | **No in this slice** (DA1-DA4 deferred); use exhaustive switch + smoke | Dalamud test project not yet set up; not blocking | DAD-3 |
| Wrap `IActionExecutor` in a `RecordingActionExecutor`? | **No in v1** | Decision event from engine already covers trace need | §H exclusion |
| `KeyItem → EventItem` mapping (the previous attempt's guess) | **Pin with comment + smoke + future direct test (DA3)** | Verified against canonical ActionManager.cs source; documented in mapping comment | DAD-4 |
| Object kind filter for ObjectTable scan | **`EventNpc \| BattleNpc`** (exclude Aetheryte) | No realistic action targets an aetheryte; matches DalamudInteractor for NPC interactions | DAD-6 |
| Pass `targetId:` to `am->UseAction` or rely on sentinel + `TargetManager.Target`? | **Both — set target slot AND pass explicit `GameObjectId`** | Other plugins may race the target slot; explicit `targetId` is authoritative | DAD-6 |
| Self-cast: clobber `TargetManager.Target` to null? | **No — leave it alone, use sentinel (`0xE000_0000`)** | Don't disturb user's manually-acquired target | DAD-11 |

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §E.
- Happy paths: 4 scenarios (DA5, DA6, DA7, DA11) covering Ready, OnCooldown, epsilon boundary, error-message format
- Edge cases: 4 scenarios (DA9, DA10, DA10b optional, DA10c optional, DA11b optional) — precedence, overflow, custom epsilon, zero-recast, zero-baseId
- Error cases: 2 scenarios (DA8, DA9) — non-zero status code, status-vs-cooldown precedence
- DA1-DA4 (enum mapping): documented but **not implemented in this slice** (see Decision DAD-3); ready to copy in when a Dalamud-bound test project exists
- Expected total: ~7 required tests + ~3 optional tests = up to 10 tests in `QuestForge.Adapters.Tests/Actions/ActionStatusInterpreterTests.cs`
