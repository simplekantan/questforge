# UseActionStep Implementation Plan

**Status:** ready for test creation
**Input docs:**
- `docs/TELEPORT_STEP_PLAN.md` (closest analog: small step type with focused adapter, pre-flight guards, no synthesis)
- `docs/SCHEMA.md` §UseActionStep (current placeholder shape — being replaced by this plan)
- `docs/ADAPTERS.md` §ICombat (the existing `UseAction(uint, NpcId?, …)` surface is being *moved* — see Decision UA1)
- `docs/MOUNT_SUPPORT_PLAN.md` (lazy-dismount hook pinned by U8 / U9 here)
- `QuestForge.Schema/Step.cs` (current `UseActionStep` placeholder — being replaced)
- `QuestForge.Engine/QuestEngine.cs` `ResolveTeleportAction` / `ResolvePurchaseAction` (per-step async pre-arm pattern)
- `QuestForge.Engine/EngineAction.cs` (discriminated union)
- `QuestForge.Adapters/State/IGameStateProvider.cs` (`PlayerStateSnapshot.Casting`)
- `QuestForge.Adapters.Fakes/Movement/FakeTeleporter.cs` (failure-scripting reference)
- `QuestForge.Engine.Tests/Engine/TeleportStepTests.cs` (test layout template)
- `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` (`HarnessEngine` arm, `RunToCompletion` arm)
- `QuestForge.Plugin/EngineHost.cs` `DispatchAction` (pre-switch hooks, host-side arm template)
- `QuestForge.Adapters.Dalamud/Movement/DalamudMount.cs` (`ActionManager.UseAction` reference; the Dalamud impl template)
- `QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs` (`ActionManager.GetActionStatus` reference)

**Output (CI behavior):** Adding a `{ "type": "use-action", "actionType": "action", "actionId": 31, "targetNpcId": 1234567 }` step to a quest dispatches a new `EngineAction.UseAction` that the host translates to `IActionExecutor.UseAction`. Engine unit tests (xUnit, `QuestForge.Engine.Tests`) cover all dispatch arms against `FakeActionExecutor`. CI red → CI green when the new schema shape, the adapter interface + fake, the `EngineAction.UseAction` arm, and the `HarnessEngine` dispatch arm are wired up. The Marauder L5 quest "Axe in the Stone" becomes authorable end-to-end.

> **Note on the existing placeholder:** the schema currently contains a *placeholder* `UseActionStep` with fields `ActionId: uint`, `Target: ActionTarget`, `RepeatUntilExpect: bool`. **This plan replaces that shape** — none of those fields is referenced from the engine today (the engine throws `NotSupportedException("Phase 4 does not support step type UseActionStep")` for it). The replacement does **not** require a schema-version bump because no shipped quest authors the old shape. The validator and existing round-trip test (`QuestForge.Schema.Tests/RoundTripTests.cs::UseActionStep_RoundTrips`) **will be updated** as part of this plan — see Task UAT-1 and Decision UA2.

---

## Dependency graph

```
QuestForge.Schema
   └── UseActionStep (rewritten) + ActionType enum + JSON registration
        └── consumed by ↓
QuestForge.Adapters
   └── new IActionExecutor + ActionStatus union (in QuestForge.Adapters/Actions/)
        └── consumed by ↓
QuestForge.Adapters.Fakes
   └── FakeActionExecutor (Actions/FakeActionExecutor.cs)
        └── consumed by ↓
QuestForge.Engine
   └── new EngineAction.UseAction; ResolveUseAction async pre-arm in QuestEngine.cs
        └── consumed by ↓
QuestForge.Engine.Tests
   └── UseActionStepTests against FakeActionExecutor + FakeGameStateProvider
QuestForge.Plugin (out-of-scope for tests; do as follow-up PR)
   └── EngineHost.DispatchAction gains a UseAction arm
QuestForge.Adapters.Dalamud (out-of-scope for tests; follow-up PR)
   └── DalamudActionExecutor (ActionManager.UseAction + GetActionStatus + ObjectTable lookup)
```

**Build order:**
1. Schema (`UseActionStep` shape + `ActionType` enum) — round-trip test gates it.
2. `IActionExecutor` interface + `ActionStatus` union in `QuestForge.Adapters/Actions/`.
3. `FakeActionExecutor` in `QuestForge.Adapters.Fakes/Actions/`.
4. `EngineAction.UseAction` record.
5. `QuestEngine.ResolveUseAction` async pre-arm + dispatch.
6. `EngineTestHarness` wires `FakeActionExecutor`; `HarnessEngine.Tick` arm; `RunToCompletion` arm.
7. Tests U1–U13.

Plugin / Dalamud wiring is mirrored after tests are green (separate PR, mirrors §G of the Teleport plan).

---

## Architectural decisions (read before coding)

### Decision UA1 — `IActionExecutor` is a new focused adapter, separate from `ICombat`

`ICombat` today exposes `UseAction(uint, NpcId?, …)` and `UseActionOnObject(...)`, all stubbed (`Result.Ok(UseActionOutcome.Failed)`). We do **not** keep `UseAction` on `ICombat`. Reasons:

1. **Mixed concerns.** `ICombat` is *rotation-module lifecycle + targeting*. The actual "press a button" operation is a different responsibility — it has its own pre-flight gates (cooldown, casting, action usable) and its own failure modes (action not learned, target invalid). Threading it through `ICombat` forces every test that exercises rotation IPC to also stub action-button mechanics.
2. **Testability of pre-flight guards.** The engine's `ResolveUseAction` reads action status (`Ready` / `OnCooldown` / `Unusable`) before dispatching. That read belongs on a focused adapter so test setup is `FakeActionExecutor.ScriptNextStatus(...)` rather than `FakeCombat.SetActionStatus(...)` polluting the combat fake.
3. **Symmetry with `IMount`.** `IMount` is the precedent: a two-method interface (`Mount`, `Dismount`) extracted from what could have been folded into `INavigator` or `ICombat`. `IActionExecutor` follows the same shape (small, single-purpose).
4. **Migration path.** The stub methods on `ICombat` (`UseAction`, `UseActionOnObject`, `IsActionUsable`) are dead code today. **Delete them in this plan** to prevent future drift; the validator and any test referencing them will fail loudly and be removed in the same PR.

**Concrete shape:**

```csharp
// QuestForge.Adapters/Actions/IActionExecutor.cs (new file, new namespace)
namespace QuestForge.Adapters.Actions;

using QuestForge.Adapters.Types;

/// <summary>
/// Fires a single game action (combat ability, general action, key item) on an optional target.
/// The Dalamud implementation owns target acquisition: given a non-null targetNpcId, it searches
/// ObjectTable for an object with matching BaseId, sets TargetManager.Target, then invokes
/// ActionManager.UseAction(type, actionId). Null targetNpcId → self-cast (no target write).
///
/// Both methods are best-effort: the game may silently reject the action (wrong job, out of range,
/// no resources). Callers verify outcome via subsequent reads of PlayerStateSnapshot.Casting,
/// quest flags, or the authored Expect predicate.
/// </summary>
public interface IActionExecutor
{
    /// <summary>
    /// Fires the action. Returns Result.Failure if target lookup fails (targetNpcId provided
    /// but no matching object in ObjectTable) or if the underlying ActionManager call returns false.
    /// Success means "ActionManager.UseAction returned true" — NOT "the action's effect landed".
    /// </summary>
    Task<Result<Unit>> UseAction(
        ActionType type,
        uint actionId,
        NpcId? targetNpcId,
        CancellationToken ct);

    /// <summary>
    /// Returns the current status of an action: Ready (cooldown == 0 and learned), OnCooldown,
    /// or Unusable (wrong job, level too low, missing resources). The engine polls this each
    /// tick to decide between dispatching UseAction, emitting Wait, or emitting AwaitUser.
    ///
    /// The Dalamud impl wraps ActionManager.GetActionStatus(type, actionId): return value 0
    /// means Ready; non-zero status codes map to Unusable with the raw code in the reason
    /// string (cooldown remaining is read separately via ActionManager.GetRecastTime).
    /// </summary>
    Task<Result<ActionStatus>> GetActionStatus(
        ActionType type,
        uint actionId,
        CancellationToken ct);
}

public abstract record ActionStatus
{
    public sealed record Ready : ActionStatus;
    public sealed record OnCooldown(TimeSpan Remaining) : ActionStatus;
    public sealed record Unusable(string Reason) : ActionStatus;
}
```

**What breaks if violated:** if `UseAction` is added back to `ICombat`, every test using `FakeCombat` must script action behaviour even when it does not exercise actions. If `ActionStatus` becomes a flag enum (e.g. `Ready | OnCooldown | Unusable`), the remaining-cooldown payload disappears and the engine cannot communicate why it is waiting.

### Decision UA2 — `ActionType` is a schema-side string-discriminated enum, mapped to FFXIVClientStructs in the Dalamud adapter

The engine MUST NOT reference `FFXIVClientStructs.FFXIV.Client.Game.ActionType` directly (testability boundary). The engine carries an `ActionType` of its own, defined in `QuestForge.Adapters.Actions`. The Dalamud adapter translates.

**Concrete shape:**

```csharp
// QuestForge.Adapters/Actions/ActionType.cs
namespace QuestForge.Adapters.Actions;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ActionType>))]
public enum ActionType
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("action")]
    Action,              // combat abilities, weaponskills, spells (FFXIVClientStructs ActionType.Action == 1)
    [System.Text.Json.Serialization.JsonStringEnumMemberName("generalAction")]
    GeneralAction,       // mount, sprint, teleport (FFXIVClientStructs ActionType.GeneralAction == 5)
    [System.Text.Json.Serialization.JsonStringEnumMemberName("keyItem")]
    KeyItem              // quest key items used as actions (FFXIVClientStructs ActionType.KeyItem == 6)
}
```

**Mapping in the Dalamud adapter:**
```csharp
// QuestForge.Adapters.Dalamud/Actions/DalamudActionExecutor.cs (follow-up PR)
internal static FFXIVClientStructs.FFXIV.Client.Game.ActionType ToClientStructs(
    QuestForge.Adapters.Actions.ActionType t) => t switch
{
    QuestForge.Adapters.Actions.ActionType.Action        => FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action,
    QuestForge.Adapters.Actions.ActionType.GeneralAction => FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction,
    QuestForge.Adapters.Actions.ActionType.KeyItem       => FFXIVClientStructs.FFXIV.Client.Game.ActionType.KeyItem,
    _ => throw new ArgumentOutOfRangeException(nameof(t))
};
```

**Initial enum members** (rationale):
- **`Action`** — primary need: the Marauder L5 quest fires a job ability (Heavy Swing) on quest-specific rocks. Also covers later "use this AoE buff before entering" quests.
- **`GeneralAction`** — needed for "interact with this quest-mode menu via Mount/Dismount/Sprint sequencing" cases that come up later; trivial to enable alongside `Action`.
- **`KeyItem`** — needed for FFXIV's "Astrolabe of the Astrologians" / "Onion Doublet of the Soulless" style quests that hand the player a "key item" usable as an action. We already encounter this in MSQ.

**Deferred** (do not add without a real authoring need):
- `Item` — adding `Item` here overlaps with the existing `UseItemStep`, which has the quantity/inventory semantics already wired through `IInteractor.UseItem`. Deferring keeps the two step types orthogonal (Decision UA8).
- `Macro`, `Companion`, `Mount`, `BgcArmyAction` — no authored quest exercises these; speculative inclusion would bloat the validator/serializer surface.

When a future quest needs one of the deferred values, add it as a one-line schema change plus a Dalamud mapping line. Round-trip test gates the addition.

### Decision UA3 — `UseActionStep` schema is rewritten; old fields removed

The existing placeholder (`ActionId`, `Target: ActionTarget`, `RepeatUntilExpect: bool`) is **deleted** and replaced with:

```csharp
// QuestForge.Schema/Step.cs (replaces existing UseActionStep)
public class UseActionStep : Step
{
    /// <summary>
    /// Which ActionManager bucket the action lives in (combat ability, general action, key item).
    /// Maps to FFXIVClientStructs ActionType in the Dalamud adapter only.
    /// </summary>
    public ActionType ActionType { get; init; }

    /// <summary>
    /// The action's RowId in the relevant Lumina sheet (Action for combat abilities,
    /// GeneralAction for general actions, EventItem for key items). MUST be non-zero —
    /// the validator rejects 0 (Decision UA7).
    /// </summary>
    public uint ActionId { get; init; }

    /// <summary>
    /// Optional NPC/object target for the action. Null means self-cast (no target write).
    /// The Dalamud adapter resolves this BaseId via ObjectTable and writes TargetManager.Target
    /// before invoking ActionManager.UseAction. NpcId here is the BNpcBase data-id, matching
    /// the convention used by InteractStep/TalkStep targets.
    /// </summary>
    public NpcId? TargetNpcId { get; init; }
}
```

**`NpcId` is `QuestForge.Schema.NpcId`** — but currently there is no schema-side `NpcId` wrapper; the schema uses `uint NpcId` on `NpcLocation`. **Use `uint? TargetNpcId { get; init; }`** to avoid introducing a new value type when the existing ones use raw `uint`. The engine converts to `Adapters.Types.NpcId` at dispatch time. This matches the `TalkStep.Target.NpcId` pattern (uint) and avoids polluting the schema with a redundant wrapper.

**Final schema shape:**
```csharp
public class UseActionStep : Step
{
    public ActionType ActionType { get; init; }
    public uint ActionId { get; init; }
    public uint? TargetNpcId { get; init; }
}
```

**JSON sample (Marauder L5 "Axe in the Stone"):**
```json
{
  "type": "use-action",
  "id": "axe-the-rock",
  "actionType": "action",
  "actionId": 31,
  "targetNpcId": 2001234,
  "expect": { "predicate": "questSequence(65849) >= 2" }
}
```

Self-cast variant (no target):
```json
{
  "type": "use-action",
  "id": "sprint-to-keep-up",
  "actionType": "generalAction",
  "actionId": 4,
  "expect": { "predicate": "playerHasBuff(50)" }
}
```

**What breaks if violated:** if `RepeatUntilExpect` were kept, every dispatch arm would need to inspect it, and the engine's normal Expect-first cursor walk would have to special-case it — duplicating the "stateless retry until Expect satisfies" behaviour the engine already provides for free.

### Decision UA4 — Author-required `Expect`; engine performs NO synthesis

The Teleport / Purchase steps synthesise default `Expect` values when the author omits them (`playerZone() == N`, `playerHasItem(item, qty)`). **UseActionStep does not.** Reasoning:

1. **No universal postcondition exists.** Different actions complete in different ways: some flip a quest flag, some advance a quest sequence, some grant a buff, some spawn a follow-up dialogue. There is no `actionUsed(id)` predicate that means "the action's effect has landed" — `UseAction` returning success means *the button was pressed*, which the engine cannot use as a completion signal without re-emitting forever.
2. **TalkStep / CombatStep precedent.** Both also require author-written `Expect` for the same reason: there is no schema-derivable postcondition.
3. **Stateless retry is the loss mode.** If the author forgets `Expect`, the engine re-emits `UseAction` every tick. This is annoying but recoverable (the user notices in the trace); silent synthesis would be worse — the engine would mark the step done on the first dispatch even if nothing happened in-game.

The validator MUST warn (not error) when `UseActionStep.Expect` is null (Decision UA7 — `structural/use-action-missing-expect`, Severity.Warning). Errors would block authoring while iterating; the runtime symptom (engine loops re-emitting) is loud enough.

### Decision UA5 — Pre-flight guards live in `ResolveUseAction`, in this priority order

Mirroring `ResolveTeleportAction`'s structure. Order matters — earlier guards win.

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| 1. Player casting | `PlayerStateSnapshot.Casting` via `GetPlayerState` | `EngineAction.Wait("player casting; deferring use-action")` |
| 2. Action status `Unusable` | `IActionExecutor.GetActionStatus` | `EngineAction.AwaitUser("action {id} unusable: {reason}")` |
| 3. Action status `OnCooldown` | same | `EngineAction.Wait("action {id} on cooldown: {remaining}s")` |
| 4. (none — proceed to emit) | — | `EngineAction.UseAction(type, id, target, Origin: step)` |

**WHY casting before action-status:** if the player is mid-cast, the action-status read is meaningless (it would report whatever-was-true-before-the-cast-began). Reading casting first and short-circuiting on Wait is the same posture `EngineHost.DispatchAction` already uses for mount fire (`!ps.Casting` is a guard there too).

**Why `Unusable` is AwaitUser, not Wait:** `OnCooldown` is transient (will become Ready in seconds). `Unusable` is structural (wrong job, not learned, missing resources) — the engine cannot resolve it by waiting. AwaitUser is the correct surface for "user must intervene" the same way `TeleportStep` uses it for "unknown aetheryte."

**No `IsPlayerInCombat` guard.** Most combat actions exist *to be used in combat* (the Marauder dummy-practice quest is the motivating example). Adding an in-combat refusal would defeat the use case. Pinned by U13.

**No engine-side cooldown timer.** The engine does not track time; it polls `GetActionStatus` each tick and re-emits `Wait` until the adapter says `Ready`. This keeps `ResolveUseAction` stateless and deterministic across replay.

**Concrete shape:**
```csharp
private async Task<EngineAction> ResolveUseAction(UseActionStep step, CancellationToken ct)
{
    // Guard 1: casting → Wait (defer until cast finishes)
    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring use-action", Origin: step);

    // Guard 2/3: action status — Unusable → AwaitUser; OnCooldown → Wait
    var statusResult = await _actionExecutor.GetActionStatus(step.ActionType, step.ActionId, ct);
    if (statusResult is Result<ActionStatus>.Success { Value: var status })
    {
        switch (status)
        {
            case ActionStatus.Unusable u:
                return new EngineAction.AwaitUser(
                    $"action {step.ActionId} unusable: {u.Reason}");
            case ActionStatus.OnCooldown oc:
                return new EngineAction.Wait(
                    $"action {step.ActionId} on cooldown: {oc.Remaining.TotalSeconds:F1}s",
                    Origin: step);
            case ActionStatus.Ready:
                break; // fall through to emit
        }
    }
    // Fail-open: status read failure → proceed to emit (mirrors ResolveTeleportAction's
    // IsPlayerInCombat fail-open posture). If the action genuinely can't fire, the
    // adapter's UseAction returns Result.Failure and the next tick stateless-retries.

    var target = step.TargetNpcId is { } id ? new NpcId(id) : (NpcId?)null;
    return new EngineAction.UseAction(step.ActionType, step.ActionId, target, Origin: step);
}
```

### Decision UA6 — Mount handling: lazy-dismount applies; UseAction is **not** in the dismount-exemption list

Most combat actions cannot be used while mounted. The existing lazy-dismount hook in `EngineHost.DispatchAction` (and `HarnessEngine.Tick`) fires before any non-`Navigate` action after a prior `Navigate`:

```csharp
// EngineHost.cs lines 276-314 (current)
if (_lastDispatchedActionWasNavigate && action is not EngineAction.Navigate and not EngineAction.Teleport)
{
    // … dismount logic …
}
```

`Teleport` is exempt because Lifestream dismounts on arrival. **`UseAction` is NOT exempt** — the engine emits `UseAction` while the player is still mounted; without the lazy-dismount hook firing first, `ActionManager.UseAction(Action, 31)` is silently rejected by the game and the engine spin-loops re-emitting.

**Action required in this plan:** the `HarnessEngine.Tick` arm and the `EngineHost.DispatchAction` arm **do not need code changes** — the existing pattern already dismounts before any non-`Navigate` action that is not `Teleport`. We only need a regression test (U8) pinning that `UseAction` triggers dismount. The negative variant (U9) pins that a standalone `UseAction` on a mounted player does *not* dismount (matches the same `lazy-dismount-bound-to-prior-Navigate` contract as Teleport T4b).

**Why we are not adding `UseAction` to the exemption list:** unlike Teleport (game auto-dismounts on arrival), there is no game-side auto-dismount for actions. Adding `UseAction` to the exemption list would cause permanent silent rejection.

### Decision UA7 — Validator rules (deferred to validator PR, documented here)

The validator (`QuestForge.Tools.Validator`) gains these rules. Implementation is out of scope for this plan; the table is documented so the validator PR knows what to add.

| Rule | Code | Severity | Suppressed when |
|---|---|---|---|
| `actionId` non-zero | `structural/use-action-id-missing` | Error | — |
| `actionType` is a known enum value | `structural/use-action-type-invalid` | Error | (handled by enum parser; rule is defensive) |
| `expect` is authored | `structural/use-action-missing-expect` | Warning | — (warning only; runtime works without it, just spin-loops) |
| `targetNpcId` is non-zero when present | `structural/use-action-target-npc-id-zero` | Error | (null is allowed; only an explicit 0 is rejected) |

The engine performs **no** validation of `actionId == 0` at runtime — the adapter will return `Unusable` or fail naturally. The validator is the front line.

### Decision UA8 — `UseActionStep` and `UseItemStep` remain separate; `ActionType.Item` is **not** added

`UseItemStep` already exists with `ItemId: uint` and `Target: UseItemTarget` (Kind discriminator: `npc` / `object` / `position`). It is wired to `IInteractor.UseItem` (inventory item, with quantity / cooldown / charge semantics — different from `ActionManager.UseAction(Item, …)`). Routing inventory-item use through `UseActionStep` would create two codepaths for the same authoring intent.

**Rule:** `UseActionStep.ActionType` never carries `Item`. Inventory items go through `UseItemStep`. The validator does NOT need to enforce this — the enum simply doesn't have the value (Decision UA2's initial list excludes `Item`).

If a future use case needs `ActionManager.UseAction(Item, eventItemRowId, …)` for an event item (a "this item is a single-use action" key-item), it falls under `ActionType.KeyItem` (which is what FFXIVClientStructs uses for event items).

### Decision UA9 — `FakeActionExecutor` exposes ScriptNextStatus / ScriptNextResult / ScriptNextFailure / RecordedCalls

Mirrors `FakeTeleporter`'s shape (Decision T10 + the call log pattern from `FakeMount.MountCallCount`). Concrete shape:

```csharp
// QuestForge.Adapters.Fakes/Actions/FakeActionExecutor.cs
namespace QuestForge.Adapters.Fakes.Actions;

using QuestForge.Adapters.Actions;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Types;

public sealed class FakeActionExecutor : IActionExecutor
{
    // ---- recording ----
    public record UseActionCall(
        ActionType Type,
        uint ActionId,
        NpcId? TargetNpcId,
        DateTimeOffset At) : AdapterCall(At);

    public CallLog<UseActionCall> RecordedCalls { get; } = new();

    // ---- scripting ----
    private ActionStatus _nextStatus = new ActionStatus.Ready();
    private (string Reason, string? Detail)? _nextFailure;
    private (string Reason, string? Detail)? _nextStatusFailure;

    /// <summary>Sets the status to return on the next GetActionStatus call. Persists across calls
    /// (status is "current" state, not per-call). Use ScriptNextStatusFailure to inject a read error.</summary>
    public void ScriptNextStatus(ActionStatus status) => _nextStatus = status;

    /// <summary>Forces UseAction to return Result.Failure on the next call only (then resets).</summary>
    public void ScriptNextFailure(string reason, string? detail = null)
        => _nextFailure = (reason, detail);

    /// <summary>Forces GetActionStatus to return Result.Failure on the next call only (then resets).</summary>
    public void ScriptNextStatusFailure(string reason, string? detail = null)
        => _nextStatusFailure = (reason, detail);

    public void Reset()
    {
        RecordedCalls.Clear();
        _nextStatus = new ActionStatus.Ready();
        _nextFailure = null;
        _nextStatusFailure = null;
    }

    // ---- IActionExecutor ----

    public Task<Result<Unit>> UseAction(
        ActionType type, uint actionId, NpcId? targetNpcId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedCalls.Add(new UseActionCall(type, actionId, targetNpcId, DateTimeOffset.UtcNow));

        if (_nextFailure is { } f)
        {
            _nextFailure = null;
            return Task.FromResult<Result<Unit>>(Result.Fail(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<ActionStatus>> GetActionStatus(
        ActionType type, uint actionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_nextStatusFailure is { } f)
        {
            _nextStatusFailure = null;
            return Task.FromResult<Result<ActionStatus>>(Result.Fail<ActionStatus>(f.Reason, f.Detail));
        }
        return Task.FromResult<Result<ActionStatus>>(Result.Ok(_nextStatus));
    }
}
```

Note: `_nextStatus` is **sticky** (not consumed on read). This matches reality — the status is current state, queried each tick. Tests that need a one-shot status change do `ScriptNextStatus(OnCooldown(…))`, tick, `ScriptNextStatus(Ready())`, tick.

### Decision UA10 — `EngineAction.UseAction` carries the schema-side `ActionType` (not FFXIVClientStructs)

```csharp
// QuestForge.Engine/EngineAction.cs (append)
public sealed record UseAction(
    QuestForge.Adapters.Actions.ActionType Type,
    uint ActionId,
    QuestForge.Adapters.Types.NpcId? TargetNpcId,
    Step? Origin = null) : EngineAction;
```

The engine and the dispatch arm never see FFXIVClientStructs types. The Dalamud adapter is the only place that translates.

### Decision UA11 — `EngineTestHarness` constructs `FakeActionExecutor` and `HarnessEngine.Tick` does not need a new arm

`HarnessEngine.Tick` already returns the action after applying pre-switch hooks; the dispatch (calling `IActionExecutor.UseAction`) happens in `RunToCompletion`. Mirroring the Teleport arm (line 169-174 of `EngineTestHarness.cs`):

```csharp
// In RunToCompletion's switch:
case EngineAction.UseAction ua:
    actions.Add(action);
    EmitActionSubmitted("UseAction", JsonSerializer.SerializeToElement(
        new { type = ua.Type.ToString(), actionId = ua.ActionId, targetNpcId = ua.TargetNpcId?.Value },
        _jsonOpts));
    var uaResult = await ActionExecutor.UseAction(ua.Type, ua.ActionId, ua.TargetNpcId, ct);
    EmitActionCompleted("UseAction", uaResult.IsSuccess ? "Done" : "Failed");
    break;
```

`EngineTestHarness` gains:
```csharp
public FakeActionExecutor ActionExecutor { get; } = new FakeActionExecutor();
```
…and passes `ActionExecutor` to the `QuestEngine` constructor (a new parameter — see Decision UA12).

### Decision UA12 — `QuestEngine` constructor gains an `IActionExecutor` parameter (required, not optional)

`IActionExecutor` is required because the engine has a dispatch arm that always needs the adapter. Optional would force every test setup to either pass a stub or accept that `UseActionStep` throws. Constructor parameter is added **after** `IVendor?` (which is already optional) and **before** the `IMinigameSkipper` parameter — placement that minimizes diff churn in `EngineHost.cs`.

Actually — to keep ordering invariant for trace-replay and to avoid disturbing the existing constructor parameter sequence (which downstream test helpers depend on), append the new parameter **at the end**, after `IVendor?` and `TimeProvider?`:

```csharp
public QuestEngine(
    IGameStateProvider gameState,
    IQuestState questState,
    INavigator navigator,
    ITeleporter teleporter,
    IInteractor interactor,
    ICombat combat,
    IGearManager gear,
    IMinigameSkipper minigames,
    IDialogueResolver dialogue,
    ITimingProfile timing,
    ITraceWriter trace,
    ILogger<QuestEngine> logger,
    TimeProvider? clock = null,
    IVendor? vendor = null,
    IActionExecutor? actionExecutor = null)   // NEW, optional with null fallback (see below)
```

`actionExecutor` is **optional** for backward-compatibility (existing test sites that don't use UseActionStep continue to compile). If null and a `UseActionStep` is encountered, `ResolveUseAction` returns `EngineAction.AwaitUser("UseActionStep dispatched but no IActionExecutor wired — host must supply one")`. This makes the misconfiguration loud rather than crashing.

`EngineHost.BeginRun` MUST always pass a non-null executor. `EngineTestHarness` MUST always pass `ActionExecutor`. The optional-with-AwaitUser pattern mirrors the IVendor handling (engine treats absence as configuration error, not crash).

### Decision UA13 — `_lastResolvedStep` is updated by the `UseAction` arm

`QuestEngine._lastResolvedStep` is used by `CurrentYesNoAnswer` extraction (lines 281-297 of `QuestEngine.cs`). `UseActionStep` does not have `DialogueChoices`, so `ExtractYesNo` returns null for it — but `_lastResolvedStep` should still be set so trace events carry the correct step context. Mirror the Teleport / Purchase pattern: set `_lastResolvedStep = step;` immediately before returning from `ResolveUseAction`, before the action is built. Actually — both Teleport and Purchase do *not* set `_lastResolvedStep` in their async pre-arms (only `ResolveActionForStep` sets it). Follow that convention: do NOT set `_lastResolvedStep` in `ResolveUseAction`. The `Origin: step` field on `EngineAction.UseAction` carries the same information for downstream consumers.

### Decision UA14 — Dispatch arm goes between PurchaseItemStep (line 566) and TeleportStep (line 575)

Insertion site in `QuestEngine.ResolveAction`:

```csharp
// 6. PurchaseItemStep async arm
if (step is PurchaseItemStep purchaseStep) { … }

// 6a. UseActionStep async arm — NEW; step-gated so IsPlayerCasting / GetActionStatus
//     are only read when the cursor is on a UseActionStep.
if (step is UseActionStep useActionStep)
{
    var useAction = await ResolveUseAction(useActionStep, ct);
    return (useAction, step.Id);
}

// 6b. TeleportStep async arm
if (step is TeleportStep teleportStep) { … }
```

This preserves the existing comment numbering convention and keeps all per-step async arms grouped together before the WaitStep arm (7).

---

## Task breakdown

### Task UAT-1 — Schema: replace `UseActionStep`, add `ActionType`, update round-trip test

1. **Add** `QuestForge.Adapters/Actions/ActionType.cs` with the enum + JSON converter per Decision UA2.
   (Lives in `QuestForge.Adapters` not `QuestForge.Schema` so the engine and adapter both reference it from one place. The schema project already depends on `QuestForge.Adapters`? **Check:** if not, the type lives in `QuestForge.Schema` and is re-exported. Builder verifies dependency direction.)

   **Resolution:** `QuestForge.Schema` is a *leaf* — it deliberately has no dependency on `QuestForge.Adapters` (see `SharedValueTypes.cs` comment line 10-12: "Schema-side AetheryteId alias. Lives here (not in Adapters) to keep Schema as a leaf"). Therefore:
   - **`ActionType` enum lives in `QuestForge.Schema/SharedValueTypes.cs`** (alongside `AetheryteId`).
   - `QuestForge.Adapters/Actions/IActionExecutor.cs` does `using QuestForge.Schema;` to consume it. **OR** — to keep `QuestForge.Adapters` from depending on `QuestForge.Schema` (verify the actual project reference graph), introduce a *parallel* `QuestForge.Adapters.Actions.ActionType` that duplicates the enum values, with an internal conversion. The Teleport/AetheryteId precedent is to keep the duplication (see line 18 of `SharedValueTypes.cs`).

   **Builder verifies the project reference graph and picks one:** schema-only or adapter-only. The test surface is identical either way (string serialization is the contract). For this plan we assume **schema-only** (lives in `SharedValueTypes.cs`) — the adapter's `IActionExecutor` does `using QuestForge.Schema;`.

   **Re-resolution after re-reading:** `QuestForge.Adapters` does already reference enum-style types from `QuestForge.Schema` (`PurchaseCurrency` flows through `EngineAction.Purchase`). The precedent supports schema-side ownership. **Final:** `ActionType` lives in `QuestForge.Schema/SharedValueTypes.cs`.

2. **Replace** `UseActionStep` in `QuestForge.Schema/Step.cs` per Decision UA3 (delete `ActionId/Target/RepeatUntilExpect`; add `ActionType/ActionId/TargetNpcId`).

3. **`QuestForgeJsonContext`** — no new line required (the `[JsonSerializable(typeof(UseActionStep))]` line already exists). Add `[JsonSerializable(typeof(ActionType))]` for the new enum.

4. **Update** `QuestForge.Schema.Tests/RoundTripTests.cs::UseActionStep_RoundTrips` to assert the new fields:
   ```csharp
   [Fact]
   public void UseActionStep_RoundTrips()
   {
       var step = new UseActionStep
       {
           Id = "axe-the-rock",
           ActionType = ActionType.Action,
           ActionId = 31,
           TargetNpcId = 2001234u,
           Expect = new PredicateExpect { Predicate = "questFlag(65849, 3)" }
       };

       var result = RoundTrip(step);
       Assert.Equal(ActionType.Action, result.ActionType);
       Assert.Equal(31u, result.ActionId);
       Assert.Equal(2001234u, result.TargetNpcId);
   }
   ```
   This is **U12** in the test list below — written by the Tester, but the existing test must be updated in the same commit to avoid a red build between the schema swap and the new round-trip test.

5. **`QuestForge.Plugin/UI/Authoring/ExportDialog.cs` line 208** already maps `UseActionStep => "use-action"`. No change required.

### Task UAT-2 — Adapter: `IActionExecutor` + `ActionStatus`

1. Create `QuestForge.Adapters/Actions/IActionExecutor.cs` with the interface per Decision UA1.
2. Create `QuestForge.Adapters/Actions/ActionStatus.cs` (or include in the same file) with the abstract record + three sealed subtypes (`Ready`, `OnCooldown(TimeSpan)`, `Unusable(string Reason)`).
3. **Delete** `UseAction`, `UseActionOnObject`, `IsActionUsable` from `ICombat` (per Decision UA1). Delete their stub implementations in `WrathComboAdapter` and `FakeCombat`. Any test referencing them (search: `IsActionUsable`, `UseActionOnObject` — both appear to be unreferenced today) compiles or is removed.

### Task UAT-3 — Fake: `FakeActionExecutor`

1. Create `QuestForge.Adapters.Fakes/Actions/FakeActionExecutor.cs` per Decision UA9.

### Task UAT-4 — Engine: `EngineAction.UseAction` + `ResolveUseAction`

1. Append `UseAction` record to `EngineAction.cs` per Decision UA10.
2. Add `_actionExecutor` field + constructor parameter to `QuestEngine` per Decision UA12.
3. Insert the async pre-arm in `ResolveAction` per Decision UA14.
4. Implement `ResolveUseAction` per Decision UA5.

### Task UAT-5 — Test harness wiring

1. `EngineTestHarness` gains `public FakeActionExecutor ActionExecutor { get; } = new();` and passes it through to the `QuestEngine` constructor.
2. `RunToCompletion` gains the `EngineAction.UseAction` arm per Decision UA11.

### Task UAT-6 — Tests (this plan's deliverable for the Tester)

Write U1–U13 below in `QuestForge.Engine.Tests/Engine/UseActionStepTests.cs`. Round-trip test U12 lives in `QuestForge.Schema.Tests/RoundTripTests.cs` (replacing the existing `UseActionStep_RoundTrips`).

### Task UAT-7 — Plugin / Dalamud wiring (out of scope for tests; follow-up PR)

1. `EngineHost.DispatchAction` gains a `case EngineAction.UseAction:` arm calling `_actionExecutor.UseAction(…)`.
2. `EngineHost.BeginRun` constructs `DalamudActionExecutor` and passes it to `new QuestEngine(…)`.
3. `DalamudActionExecutor` (new file `QuestForge.Adapters.Dalamud/Actions/DalamudActionExecutor.cs`):
   - `UseAction(type, actionId, targetNpcId, ct)`: if `targetNpcId is { } id`, search `ObjectTable` for an object whose `DataId == id.Value` (or the appropriate equivalent — Builder confirms). Set `TargetManager.Target = obj`. Then call `ActionManager.Instance()->UseAction(ToClientStructs(type), actionId)`. Return `Result.Ok` if `UseAction` returns true; `Result.Fail("actionRejected", …)` otherwise. Return `Result.Fail("targetNotFound", …)` if `targetNpcId` was provided but no matching object was found.
   - `GetActionStatus(type, actionId, ct)`: call `ActionManager.Instance()->GetActionStatus(ToClientStructs(type), actionId)`. Status 0 → check `GetRecastTime`; if remaining > 0 → `OnCooldown(TimeSpan.FromSeconds(remaining))`; else `Ready`. Non-zero status → `Unusable($"status={status}")` (raw code in the message; the Dalamud impl can later add a code → human-readable map if useful).

---

## Validation rules (Phase 1+ follow-up — not implemented by this plan)

| Rule | Code | Severity |
|---|---|---|
| `actionId` non-zero | `structural/use-action-id-missing` | Error |
| `actionType` is a known enum value | `structural/use-action-type-invalid` | Error (defensive) |
| `expect` authored | `structural/use-action-missing-expect` | Warning |
| `targetNpcId` non-zero when present | `structural/use-action-target-npc-id-zero` | Error |

---

## Given-When-Then test scenarios

All tests live in `QuestForge.Engine.Tests/Engine/UseActionStepTests.cs` unless noted. Layout mirrors `TeleportStepTests`: one `[Fact]` per scenario, factory helpers at the bottom.

For every scenario:
- `harness.QuestState.SetQuestSequence(new QuestId(<questId>), 0)` is called.
- The quest contains exactly one UseActionStep in sequence 0 (unless noted).
- The step has an authored `Expect` (PredicateExpect using a predicate that does NOT auto-satisfy from default fake state, unless the test specifically tests satisfaction).

### U1 — Happy path, no target: action Ready → emits UseAction(Action, id, null)

**Given:**
- Player not casting (default).
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());`
- UseActionStep `{ ActionType = ActionType.Action, ActionId = 31, TargetNpcId = null, Expect = PredicateExpect("questFlag(81001, 3)") }` (predicate false).

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.UseAction(Type: ActionType.Action, ActionId: 31u, TargetNpcId: null, Origin: <step>)`.
- `harness.ActionExecutor.RecordedCalls.Count == 0` (engine returns the action; harness's `RunToCompletion` arm would call the adapter, but this test ticks once).

### U2 — Happy path, NPC target: action Ready → emits UseAction with TargetNpcId set

**Given:**
- Same as U1 but `TargetNpcId = 2001234u`.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.UseAction(Type: ActionType.Action, ActionId: 31u, TargetNpcId: new NpcId(2001234), Origin: <step>)`.

### U3 — Player casting → Wait, no UseAction emitted

**Given:**
- `harness.GameState.SetCasting(true);`
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());` (irrelevant — guard 1 short-circuits)
- UseActionStep as U1.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` with `Reason` containing the substring `"player casting"`.
- `harness.ActionExecutor.RecordedCalls.Count == 0`.

### U4 — Action on cooldown → Wait, no UseAction emitted

**Given:**
- Player not casting.
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.OnCooldown(TimeSpan.FromSeconds(12.5)));`
- UseActionStep as U1.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` with `Reason` containing the substring `"on cooldown"` AND `"12.5"`.
- `harness.ActionExecutor.RecordedCalls.Count == 0`.

### U5 — Action Unusable → AwaitUser, no UseAction emitted

**Given:**
- Player not casting.
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Unusable("wrong job"));`
- UseActionStep as U1.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.AwaitUser` with `Reason` containing the substrings `"action 31"` AND `"wrong job"`.
- `harness.ActionExecutor.RecordedCalls.Count == 0`.

### U6 — Adapter UseAction returns Result.Failure → stateless retry on next tick

**Given:**
- UseActionStep as U1 (no target, Expect false).
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());` (sticky)
- `harness.ActionExecutor.ScriptNextFailure("adapter-error", "ActionManager.UseAction returned false");`

**When:**
1. Tick 1 → returns `EngineAction.UseAction(…)`.
2. Manually call `await harness.ActionExecutor.UseAction(ActionType.Action, 31, null, ct);` (consumes the scripted failure; returns `Result.Failure`; recorded call appended).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.UseAction(…)` again — stateless retry (same posture as TeleportStep T5).
- `harness.ActionExecutor.RecordedCalls.Count == 1` (only the manual call; engine emits the action but does not invoke the adapter).

### U7 — Cancellation propagates from dispatch arm

**Given:**
- UseActionStep as U1.
- `var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates (mirrors TeleportStep T10).

### U8 — Mounted + prior Navigate: lazy-dismount fires before UseAction

**Given:**
- Two-step quest in sequence 0:
  1. TravelStep navigating to position `(200, 0, 0)` in zone `130` with `Expect = "playerZone() == 130"`.
  2. UseActionStep `{ ActionType = ActionType.Action, ActionId = 31, TargetNpcId = null, Expect = PredicateExpect("questFlag(81001, 3)") }` (predicate false).
- Player starts in zone 128 at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`).
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());`

**When:**
1. Tick 1 → `EngineAction.Navigate`. After this tick `_lastDispatchedActionWasNavigate = true` in `HarnessEngine`.
2. Advance state: `harness.GameState.SetZone(new ZoneId(130));` (TravelStep Expect now satisfies).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.UseAction(…)`.
- `harness.Mount.DismountCallCount >= 1` (the lazy-dismount hook fired before returning the UseAction action — UseAction is NOT in the exemption list).

Pins Decision UA6.

### U9 — Standalone UseAction + mounted, no prior Navigate: dismount does NOT fire

**Given:**
- One-step quest: UseActionStep as U1.
- Player mounted (`SetMountState(MountState.Mounted)`).
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());`

**When:** Tick once.

**Then:**
- Returns `EngineAction.UseAction(…)`.
- `harness.Mount.DismountCallCount == 0` (lazy-dismount is bound to *prior Navigate*, not to step-entry; player must dismount manually or the quest must have a Navigate first).

Pins the same "lazy-dismount bound to prior Navigate" contract as TeleportStep T4b.

### U10 — Authored Expect already satisfied → step skipped → no UseAction emitted

**Given:**
- UseActionStep `{ Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(new AdaptersAetheryteId(8), true);` so the predicate is true *before* the step runs.
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());` (irrelevant; should not be consulted)

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` (Expect short-circuits the dispatch).
- `harness.ActionExecutor.RecordedCalls.Count == 0`.
- The adapter's `GetActionStatus` was **not** called (assert via the fake's optional `RecordedStatusReads` counter — Builder adds this counter if useful, or use the `FakeGameStateProvider.RecordedReads` to assert `IsPlayerCasting` was not recorded).

  Optional implementation detail: `FakeActionExecutor` can also record `GetActionStatus` calls. Builder discretion — if added, assert `harness.ActionExecutor.RecordedStatusCalls.Count == 0` here.

### U11 — Integration two-tick sequence: UseAction fires, Expect satisfies, step completes

**Given:**
- UseActionStep `{ ActionType = ActionType.Action, ActionId = 31, TargetNpcId = 2001234, Expect = PredicateExpect("questFlag(81001, 3)") }`.
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());`
- Predicate false initially.

**When:**
1. Tick 1 → `EngineAction.UseAction(…)`.
2. Mimic the harness dispatch:
   - `await harness.ActionExecutor.UseAction(ActionType.Action, 31, new NpcId(2001234), ct);`
   - Simulate game effect: `harness.QuestState.SetQuestFlag(new QuestId(81001), 3, true);` (or however `questFlag(...)` is satisfied — Tester picks the right setter; see existing AttunementStep / PurchaseItemStep tests for the established pattern).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.Wait` (Expect now satisfies, step confirmed, no more steps).
- `harness.ActionExecutor.RecordedCalls.Count == 1`.
- The recorded call's fields: `Type == ActionType.Action`, `ActionId == 31u`, `TargetNpcId == new NpcId(2001234)`.

### U12 — Round-trip JSON serialization of UseActionStep (replaces existing test)

**Located in** `QuestForge.Schema.Tests/RoundTripTests.cs`.

**Given:** A `UseActionStep { Id = "axe-the-rock", ActionType = ActionType.Action, ActionId = 31, TargetNpcId = 2001234u, Expect = PredicateExpect("questFlag(65849, 3)") }`.

**When:** Serialize via `QuestForgeJsonContext.QuestFileOptions`, deserialize as `Step`.

**Then:**
- The serialized JSON contains `"type": "use-action"`, `"actionType": "action"`, `"actionId": 31`, `"targetNpcId": 2001234`.
- The deserialized value is a `UseActionStep`.
- `result.ActionType == ActionType.Action`.
- `result.ActionId == 31u`.
- `result.TargetNpcId == 2001234u`.

**Additionally**, a sibling test pins the null-target case:
- Build a step with `TargetNpcId = null`.
- Serialize.
- Assert the JSON does NOT contain `"targetNpcId"` (or contains `"targetNpcId": null` — match the existing convention for other nullable fields in the schema; Builder picks one).
- Round-trip preserves null.

### U13 — InCombat is NOT a guard: UseAction fires while player is in combat

**Given:**
- `harness.GameState.SetInCombat(true);`
- Player not casting.
- `harness.ActionExecutor.ScriptNextStatus(new ActionStatus.Ready());`
- UseActionStep as U1 (no target).
- No CombatStep in the quest (so the global defense rule does not trigger an Engage that would short-circuit cursor walk).

  **Critical setup:** the global defense rule reads `IsPlayerInCombat` and, if true + an attacker is in scan range, emits `Engage` *before* the cursor walk. For this test to exercise the UseAction arm:
  - Either ensure no hostile actors exist (default fake state; `harness.GameState.ClearHostileActors()` defensively), OR
  - Set `harness.GameState.SetMountState(MountState.Mounted);` AND wire `harness.Navigator.SetNavigating(true);` to trigger the "mounted + still navigating → skip defense" branch.

  The first option is simpler; use it.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.UseAction(…)`. (Pins Decision UA5 — no InCombat guard for UseActionStep.)

### U14 (optional, defensive) — Status read failure → fail-open, UseAction still emitted

**Given:**
- Player not casting.
- `harness.ActionExecutor.ScriptNextStatusFailure("adapter-error", "ActionManager.GetActionStatus threw");`
- UseActionStep as U1.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.UseAction(…)` (fail-open, mirroring `ResolveTeleportAction`'s InCombat fail-open posture).

Pins Decision UA5's fail-open comment.

---

## Implementation order

**Phase A — Schema (10 min)**
1. Add `ActionType` enum to `QuestForge.Schema/SharedValueTypes.cs` (Task UAT-1.1).
2. Replace `UseActionStep` in `Step.cs` (Task UAT-1.2).
3. Add `[JsonSerializable(typeof(ActionType))]` to `QuestForgeJsonContext`.
4. Update `RoundTripTests.UseActionStep_RoundTrips` to the new shape (Task UAT-1.4) and add the null-target sibling test.
5. Run round-trip test — must be green before proceeding.

**Phase B — Adapter surface (15 min)**
1. Create `QuestForge.Adapters/Actions/IActionExecutor.cs` (interface + `ActionStatus` union).
2. Delete `UseAction` / `UseActionOnObject` / `IsActionUsable` from `ICombat`, `WrathComboAdapter`, `FakeCombat`. Compile.

**Phase C — Fake (5 min)**
1. Create `QuestForge.Adapters.Fakes/Actions/FakeActionExecutor.cs`.

**Phase D — Engine (30-45 min, TDD)**
1. Append `EngineAction.UseAction` record.
2. Add `_actionExecutor` field + constructor param to `QuestEngine`.
3. **Tester writes U1, U3, U4, U5, U7, U13** (single-tick dispatch shape; cheapest). Red.
4. Insert async pre-arm in `ResolveAction` (Decision UA14) + implement `ResolveUseAction` (Decision UA5). Green.
5. Tester writes U10 (Expect short-circuit). Green (no engine change required).
6. Tester writes U2 (target NPC variant). Green.
7. Tester writes U14 (status fail-open). Green (fail-open is in the initial implementation per Decision UA5).

**Phase E — Harness wiring (15 min)**
1. `EngineTestHarness` gains `ActionExecutor` property + constructor passthrough.
2. `RunToCompletion` gains the `UseAction` arm.
3. Tester writes U6 (stateless retry via manual two-tick).
4. Tester writes U11 (integration two-tick).
5. Tester writes U8 (lazy-dismount with prior Navigate) and U9 (standalone, no dismount).
6. Make them green.

**Phase F — Plugin wiring (out of scope for tests; follow-up PR)**
1. `EngineHost.BeginRun` constructs `DalamudActionExecutor` and passes to `QuestEngine`.
2. `EngineHost.DispatchAction` adds `case EngineAction.UseAction:`.
3. `DalamudActionExecutor` implementation (per Task UAT-7).
4. Manual in-game smoke test: Marauder L5 "Axe in the Stone" — author the quest, run to the use-action step, confirm the action fires on the rocks.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~UseActionStepTests` reports all ~13 tests green.
2. `dotnet test QuestForge.Schema.Tests --filter FullyQualifiedName~UseActionStep` reports the round-trip test green with the new schema shape.
3. A quest JSON file with `{ "type": "use-action", "actionType": "action", "actionId": 31, "targetNpcId": 2001234 }` round-trips through `QuestForgeJsonContext.QuestFileOptions` losslessly.
4. `qf-validate` continues to pass on existing quest data (no validator rules added by this plan; the validator-side warnings are Phase 1+).
5. The trace emitted on a UseAction tick contains a `DecisionEvent` with `ActionType == "UseAction"`.
6. No regression in `TeleportStepTests`, `PurchaseItemStepTests`, `AttunementStepTests`, or any test that previously referenced `ICombat.UseAction` / `IsActionUsable` (those reference sites are deleted or migrated in the same PR).
7. The deleted `ICombat.UseAction` / `UseActionOnObject` / `IsActionUsable` methods are gone from `WrathComboAdapter.cs` and `FakeCombat.cs` — `dotnet build` succeeds without warnings.

---

## Exclusions (what this plan does NOT include)

- **Validator rules** for `structural/use-action-*` (deferred to a validator-side PR; the table in §UA7 documents them).
- **Plugin-side dispatch arm** (`EngineHost.DispatchAction` case for `EngineAction.UseAction`) — follow-up PR.
- **Dalamud-side adapter implementation** (`DalamudActionExecutor`) — follow-up PR; the engine arm and tests land first to lock the schema and interface contract.
- **`ActionType.Item`** — explicitly excluded (Decision UA8). `UseItemStep` already covers inventory items.
- **`ActionType.Macro`, `Companion`, `Mount`, `BgcArmyAction`** — not added until a real authored quest needs one. Each is a one-line addition (enum value + Dalamud mapping line).
- **Repeated use (use action N times)** — deferred; the engine's stateless retry + postcondition gating handles "fire until Expect satisfies" implicitly.
- **Status-effect preconditions** ("player must have buff X before using") — deferred; can be authored via `Expect` or via a `SkipIf` predicate. A future `playerHasBuff(N)` predicate is a separate addition to the predicate language.
- **Positional checks** (rear / flank) — deferred; the game silently fails the action if positional is wrong, and the engine's stateless retry will spam UseAction. If this becomes a real authoring problem, add a `playerIsRearOf(npc)` / `playerIsFlankOf(npc)` predicate.
- **Auto-face target** — deferred. Many actions auto-face; for those that don't, the player needs to be facing the target. The Dalamud adapter MAY add auto-face as a follow-up (write `LocalPlayer.Rotation = atan2(target - player)` before `UseAction`) but it is not required for the Marauder L5 case (the dummies don't care about facing).
- **Cooldown-aware scheduling** (don't even tick this step until cooldown elapses) — deferred. The current design polls each tick. A future optimisation can buffer `OnCooldown.Remaining` and skip ticks, but it requires engine-side time tracking that breaks the stateless-replay invariant.
- **Authoring-mode inference** of UseActionStep from observed `ActionManager.UseAction` calls — Phase 9 follow-up (parallel to the Teleport inference plan).
- **Engine-side `actionId == 0` rejection** — left to the validator; the runtime treats 0 as "the adapter will return Unusable or just fail," which the engine handles via the existing AwaitUser / retry paths.
- **NpcId wrapper in schema** — kept as `uint? TargetNpcId` to match the existing `NpcLocation.NpcId: uint` convention. A future schema cleanup may introduce a `QuestForge.Schema.NpcId` wrapper across all step types; that is a cross-cutting refactor outside this plan.
- **Marauder quest data file** — the JSON for "Axe in the Stone" is authored as a separate PR consuming this engine surface. This plan only proves the surface exists and works against fakes.

---

## Open questions / future extensions (recap)

| Question | Recommendation in this plan | Defer to |
|---|---|---|
| Should InCombat block UseAction? | NO — combat actions exist to be used in combat (Decision UA5; pinned by U13). | — |
| Should `actionId == 0` be a runtime error? | NO — validator's job (Decision UA7). | Validator PR |
| Should `ActionType.Item` be included? | NO — overlaps with `UseItemStep` (Decision UA8). | Reconsider when a real authored case demands it. |
| Where does `ActionType` live (Schema vs Adapters)? | `QuestForge.Schema/SharedValueTypes.cs` (matches `AetheryteId` precedent). | — |
| Should `IActionExecutor` be folded into `ICombat`? | NO — focused adapter, matches `IMount` precedent (Decision UA1). | — |
| Should the Dalamud adapter auto-face the target? | NOT in v1 — out of scope for Marauder use case. | Follow-up if non-auto-face actions become common. |

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 3 scenarios (U1, U2, U11)
- Edge cases: 4 scenarios (U8, U9, U10, U13)
- Error / wait cases: 5 scenarios (U3, U4, U5, U6, U7, U14)
- Serialization gate: 1 scenario (U12 — replaces existing `UseActionStep_RoundTrips` in `RoundTripTests.cs`, plus the null-target sibling)
- Expected total: ~13 tests in `QuestForge.Engine.Tests/Engine/UseActionStepTests.cs`, plus 1 (or 2) updated tests in `QuestForge.Schema.Tests/RoundTripTests.cs`.
