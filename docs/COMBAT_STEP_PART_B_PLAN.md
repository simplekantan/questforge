# Combat Step — Part B Implementation Plan, Slice 1 (Issue #6 Part B)

**Status:** ready for test creation (the Half-1 in-game items are GAME-* verified by the user; the Half-2 trace-capture items are CI-testable)
**Input docs:** `docs/COMBAT_STEP_PART_A_PLAN.md` (esp. §Exclusions — the part-B handoff list; §D2/D3/D7 — the interfaces now being implemented for real), `docs/COMBAT_STEP_PLAN.md` §2 (Questionable/WrathCombo behavioral reference; §2.1 lease IPC flow), `docs/QUEST_VARIABLES_TRACE_PLAN.md` (recording-proxy / replay-scanner / fixture-starvation patterns mirrored here), `WrathCombo.API` (`Wrapper.cs`, `IPC/Settings.cs`, `IPC/AutoRotationConfiguration.cs`, `Enum/*` — the confirmed public IPC surface), `docs/ADAPTERS.md §8`, `CLAUDE.md` (engine-purity boundary, recording-proxy pattern, Result<T>, seeded-replay determinism).
**Branch:** `feat/combat-step-part-b` (`questforge` repo, checked out).
**Output (CI behavior change):** `QuestForge.Engine.Tests` and `QuestForge.Adapters.Tests` gain green coverage for (a) the recording/replay of `GetHostileActors`, (b) a new `RecordingCombat` proxy capturing combat acts, (c) the `EngineHost` lease-lifecycle latch driven against `FakeCombat`, and (d) a combat-run round-trip into a regression fixture (`engage` transitions replay deterministically). Existing replay fixtures (`simple-linear-acceptance`, `with-attunement`) stay green with **no re-record**. The in-game Dalamud impl (WrathCombo lease IPC + `IObjectTable` scan + `TargetManager` writes) is **not** CI-testable; it ships behind GAME-* acceptance items the user verifies live.

**Clean-room note.** Questionable (`../Questionable`) and WrathCombo (`../WrathCombo`) are behavioral references only. No source is copied or ported (specifically not Questionable's `WrathComboModule.cs` / `CombatController.cs`). We *call* WrathCombo's **published** IPC API (`WrathCombo.API` — `WrathIPCWrapper`), which is normal plugin interop; the adapter, latch, lease state machine, and ClientStructs scan below are independently derived for QuestForge. Every IPC signature cited is confirmed by reading `WrathCombo.API` (see §A1).

**Out of this slice (deferred):** death recovery / `InstanceKind` routing (issue #63). The combat controller and host lease-latch do **not** handle death; `IsPlayerDead` integration, the open-world return-to-aetheryte ladder, dungeon delegation, and SPD wait are a later slice that depends on #63. See §Exclusions.

---

## 0. The two halves (keep them separated)

This slice is deliberately split so the test boundary is unambiguous:

- **Half 1 — In-game Dalamud impl (GAME-* — not CI-testable).** `WrathComboAdapter` real lease IPC; `DalamudGameStateProvider.GetHostileActors` real `IObjectTable`/ClientStructs scan; the live `TargetManager` writes. These touch concrete Dalamud + WrathCombo IPC, so they run only against a game client and are verified by the user. The plan specifies them precisely (signatures, ClientStructs reads, lease state machine) so the Builder can write them once and the user verifies in one in-game pass.
- **Half 2 — Combat trace capture (CI-testable).** Flip `GetHostileActors` recording/replay from no-record/no-scan (the part-A no-cascade choice) to record/scan; add a `RecordingCombat` proxy; define how an `Engage` decision appears in a fixture and replays deterministically; pin that existing fixtures do not starve. All of this is exercised against fakes / recorded traces with **no game**.
- **The CI-testable seam in Half 1.** The lease-lifecycle *latch* (when to `StartRotation`/`StopRotation`, and where the "are we currently holding a rotation lease" boolean lives) is host-side orchestration that can be tested against `FakeCombat` without a game (the fake records `StartRotation`/`StopRotation`). Only the IPC the latch *calls into* is in-game. §C splits this precisely.

---

## Dependency graph

Two repos; this slice's code is all in `questforge`. A **paired questforge-tools change is NOT required** (the existing qf-trace pipeline already handles `step:combat` capability and a generic `engage` `DecisionEvent` — see §F). Strict build order within `questforge`:

```
1. questforge (this repo)
   ├── QuestForge.Adapters
   │     ├── Recording/RecordingGameStateProvider  ← flip GetHostileActors no-record → record (Half 2)
   │     └── Recording/RecordingCombat (NEW)        ← combat-act capture proxy (Half 2)
   ├── QuestForge.Adapters.Fakes
   │     └── Replay/ReplayGameStateProvider         ← flip GetHostileActors no-scan → scan (Half 2)
   ├── QuestForge.Adapters.Dalamud
   │     ├── Ipc/WrathComboIpc (FILL IN)            ← typed lease IPC wrapper (Half 1, GAME-*)
   │     ├── Combat/WrathComboAdapter (REAL IMPL)   ← lease lifecycle + TargetManager writes (Half 1, GAME-*)
   │     └── State/DalamudGameStateProvider          ← real GetHostileActors scan (Half 1, GAME-*)
   ├── QuestForge.Plugin
   │     └── EngineHost.DispatchAction               ← Engage arm + lease latch (Half 1 orchestration; latch is CI-testable)
   ├── QuestForge.Engine.Tests                       ← recording/replay flip tests, lease-latch tests, fixture round-trip
   └── QuestForge.Adapters.Tests                     ← RecordingCombat contract tests

2. questforge-tools — NO CHANGE REQUIRED (§F confirms qf-trace already extracts combat fixtures).
```

**Build order:** Half-2 recording/replay flips + `RecordingCombat` first (they are pure and gate the fixture round-trip tests) → host lease-latch (CI seam) → the Dalamud real impls last (compile-checked but GAME-* verified). The Dalamud work is independent of the CI work and can land in the same PR with its GAME-* criteria pending.

**Prerequisites already landed (part A — confirmed by reading):** `ICombat` reshaped to the rotation-module + targeting surface (`QuestForge.Adapters/Combat/ICombat.cs`); `IGameStateProvider.GetHostileActors(radius)` + `HostileActor` exist; `ActorId` exists; `EngineAction.Engage(CombatStep, KillTarget?)` exists and the engine emits it (`QuestEngine.cs:433-450`); `CombatController` + `KillPriority` exist and call `GetHostileActors`/`SetTarget`/`ClearTarget` via interfaces; `FakeCombat` records `RecordedTargets`/`RecordedRotation`; `FakeGameStateProvider.GetHostileActors` is scriptable; `WrathComboAdapter` and `DalamudGameStateProvider.GetHostileActors` are compiling stubs; the recording/replay proxies' `GetHostileActors` are no-record/no-scan.

---

## Architectural decisions (read before coding)

### A1 — WrathCombo public IPC surface (confirmed from `WrathCombo.API`)

Confirmed by reading `WrathCombo.API/IPC/Settings.cs`, `IPC/AutoRotationConfiguration.cs`, `Enum/SetResult.cs`, `Enum/AutoRotationConfigOption.cs`, `Enum/DPSRotationMode.cs`, `Enum/CancellationReason.cs`, and `Wrapper.cs`. The published static facade is `WrathCombo.API.WrathIPCWrapper`. The methods our adapter calls:

```csharp
// WrathCombo.API.WrathIPCWrapper — confirmed public static signatures:
static void   WrathIPCWrapper.Test();                                   // throws if IPC not wired
static bool   WrathIPCWrapper.IPCReady();                               // availability gate
static Guid?  WrathIPCWrapper.RegisterForLeaseWithCallback(             // acquire lease + register revocation callback
                  string internalPluginName, string pluginName, string? ipcPrefixForCallback);
static SetResult WrathIPCWrapper.SetAutoRotationState(Guid lease, bool enabled = true);
static SetResult WrathIPCWrapper.SetCurrentJobAutoRotationReady(Guid lease);
static SetResult WrathIPCWrapper.SetAutoRotationConfigState(            // typed overload for the DPS mode enum
                  Guid lease, AutoRotationConfigOption passedOption, DPSRotationMode value);
static SetResult WrathIPCWrapper.SetAutoRotationConfigState(           // bool overload (IncludeNPCs, OnlyAttackInCombat, …)
                  Guid lease, AutoRotationConfigOption passedOption, bool value);
static void   WrathIPCWrapper.ReleaseControl(Guid lease);              // release lease (stop)

// Confirmed enums:
enum SetResult { IGNORED=-1, Okay=0, OkayWorking=1, IPCDisabled=10, InvalidLease=11,
                 BlacklistedLease=12, Duplicate=13, PlayerNotAvailable=14,
                 InvalidConfiguration=15, InvalidValue=16 }
enum AutoRotationConfigOption { InCombatOnly=0, DPSRotationMode=1, HealerRotationMode=2, …,
                                IncludeNPCs=12, OnlyAttackInCombat=13, … }
enum DPSRotationMode { Manual=0, Highest_Max=1, … }

// Lease-revocation callback (confirmed by the behavioral reference; normal interop):
//   leasee registers an IPC PROVIDER at "{ipcPrefixForCallback}.WrathComboCallback"
//   with the shape (int cancellationReason, string additionalInfo) -> void
//   reason maps to WrathCombo.API.Enum.CancellationReason.
```

**Decision: call `WrathIPCWrapper` directly via a thin `WrathComboIpc` wrapper, not raw `GetIpcSubscriber`.** `WrathIPCWrapper` already wraps every raw `ICallGateSubscriber` with `SafeInvokeRawMethod` (swallows `IPCException`/`APIBehindException` per its `Init`/`SuppressedErrorTypes` policy) and exposes typed `SetAutoRotationConfigState` overloads. We add a `WrathComboIpc` (fill in the existing `QuestForge.Adapters.Dalamud/Ipc/WrathComboIpc.cs` stub) that (a) calls `WrathIPCWrapper.Init(pluginInterface)` once, and (b) registers the **revocation callback provider** (a raw `pi.GetIpcProvider<int, string, object>($"{Prefix}.WrathComboCallback").RegisterAction(...)`), because `WrathIPCWrapper` provides the *consumer* side of the lease but the leasee owns the *provider* side of the callback. **Rejected:** re-implementing all the raw `GetIpcSubscriber<…>` gates ourselves — that duplicates `WrathIPCWrapper`'s exception handling and `[Obsolete]`-shielded raw layer and is exactly the kind of port the clean-room policy forbids when a published facade exists.

```csharp
// QuestForge.Adapters.Dalamud/Ipc/WrathComboIpc.cs  (FILL IN — GAME-* verified)
internal sealed class WrathComboIpc : IDisposable
{
    private const string InternalName = "QuestForge";
    private const string DisplayName  = "QuestForge";
    private const string CallbackPrefix = "QuestForge";   // → "QuestForge.WrathComboCallback"

    private readonly ICallGateProvider<int, string, object> _callback;
    private Action<CancellationReason>? _onRevoked;        // set by the adapter

    public WrathComboIpc(IDalamudPluginInterface pi)
    {
        WrathIPCWrapper.Init(pi);                          // confirmed: idempotent, ECommons-optional
        _callback = pi.GetIpcProvider<int, string, object>($"{CallbackPrefix}.WrathComboCallback");
        _callback.RegisterAction(OnCallback);
    }

    public bool   IsReady()        => SafeBool(() => WrathIPCWrapper.IPCReady());
    public Guid?  RegisterLease()  => WrathIPCWrapper.RegisterForLeaseWithCallback(InternalName, DisplayName, CallbackPrefix);
    public SetResult SetAutoRotation(Guid lease, bool on)        => WrathIPCWrapper.SetAutoRotationState(lease, on);
    public SetResult SetJobReady(Guid lease)                     => WrathIPCWrapper.SetCurrentJobAutoRotationReady(lease);
    public SetResult SetDpsMode(Guid lease, DPSRotationMode m)   => WrathIPCWrapper.SetAutoRotationConfigState(lease, AutoRotationConfigOption.DPSRotationMode, m);
    public SetResult SetBool(Guid lease, AutoRotationConfigOption o, bool v) => WrathIPCWrapper.SetAutoRotationConfigState(lease, o, v);
    public void   Release(Guid lease) => WrathIPCWrapper.ReleaseControl(lease);

    public void SetRevocationHandler(Action<CancellationReason>? h) => _onRevoked = h;
    private void OnCallback(int reason, string info) => _onRevoked?.Invoke((CancellationReason)reason);
    public void Dispose() { /* unregister callback provider */ }
}
```

### A2 — `WrathComboAdapter` real impl: lease state machine for `StartRotation`/`StopRotation`/`SetTarget`/`ClearTarget` (GAME-*)

The adapter implements the part-A `ICombat` surface for real. The lease lifecycle mirrors `COMBAT_STEP_PLAN §2.1` (independently derived from the confirmed IPC):

- **`IsRotationModuleAvailable(ct)`** → `Result.Ok(_ipc.IsReady())`. (Replaces the part-A `false` stub.)
- **`GetRotationModule(ct)`** → `Result.Ok(new RotationModuleInfo("WrathCombo", <version-or-"ipc">, LeaseHeld: _lease is not null))`.
- **`StartRotation(ct)`** — acquire-and-configure, idempotent (no-op if `_lease` already held):
  1. `if (!_ipc.IsReady()) return Result.Fail("rotationModuleUnavailable", …);`
  2. `var lease = _ipc.RegisterLease();` `if (lease is not { } g) return Result.Fail("leaseDenied", …);`
  3. `_ipc.SetAutoRotation(g, on: true);`
  4. `_ipc.SetJobReady(g);`
  5. `_ipc.SetDpsMode(g, DPSRotationMode.Manual);`  ← **critical:** WrathCombo attacks the *manually-set* target; it owns no targeting.
  6. `_ipc.SetBool(g, AutoRotationConfigOption.IncludeNPCs, true);` and `_ipc.SetBool(g, AutoRotationConfigOption.OnlyAttackInCombat, false);` (so we can engage pre-pull).
  7. Store `_lease = g`; treat `SetResult.Okay`/`OkayWorking`/`Duplicate` as success, other codes as a logged soft-fail (still `Result.Ok` — the lease exists; config drift is non-fatal).
- **`StopRotation(ct)`** — release-and-clear, idempotent: `if (_lease is { } g) { _ipc.Release(g); _lease = null; }` → `Result.Ok`.
- **Revocation callback** — `_ipc.SetRevocationHandler(reason => _lease = null)`. When WrathCombo revokes (user cancel, job change, plugin disabled — `CancellationReason`), the adapter drops its lease so the next `StartRotation` re-acquires. The host latch (§A4) detects the dropped lease via the next `IsRotationModuleAvailable`/re-`StartRotation`. The adapter never throws on revocation.
- **`SetTarget(ActorId target, ct)`** — resolve `ActorId` (a `GameObjectId`) → live `IGameObject` and write `TargetManager.Target`:
  ```csharp
  var obj = _svc.ObjectTable.SearchById(target.Value);   // GameObjectId → IGameObject?
  if (obj is null) return Result.Fail<Unit>("targetNotFound", $"no live object for {target.Value}");
  _svc.TargetManager.Target = obj;
  return Result.Ok();
  ```
- **`ClearTarget(ct)`** — `_svc.TargetManager.Target = null;` → `Result.Ok`.
- `UseAction*`/`IsActionUsable` keep their part-A behavior (out of scope).

**Why store the lease on the adapter, not the host?** The lease `Guid` is a WrathCombo-IPC concept; the adapter is the only layer that knows it. The host's latch (§A4) is a higher-level "should a rotation be running for this step" boolean and must not leak the `Guid`. `Result<Unit>` keeps lease-denied / IPC-unavailable as routine failures (per CLAUDE.md), so the host can idle without an exception.

### A3 — `DalamudGameStateProvider.GetHostileActors(radius)` real impl: the ClientStructs scan (GAME-*)

Replaces the part-A `Array.Empty` stub. Scan `IObjectTable`, filter to `ObjectKind.BattleNpc`, populate each `HostileActor`. Confirmed field sources (mark GAME-* — verified live by the user):

| `HostileActor` field | Source | Notes |
|---|---|---|
| `Id` (`ActorId`) | `new ActorId(obj.GameObjectId)` | the live identity `SetTarget` writes back. |
| `DataId` (uint base id) | `obj.DataId` | BNpc base data-id; matches `KillEnemyDataIds`. (The Dalamud `IGameObject.DataId` is the base id; the existing `GetNearbyNpcs` uses `obj.BaseId` — confirm which property yields the BNpc base; use the one that matches the `KillEnemyDataIds` authoring convention.) |
| `Position` | `new WorldPosition(obj.Position.X, …Y, …Z)` | — |
| `DistanceToPlayer` | `Vector3.Distance(local.Position, obj.Position)` | `local = _svc.ObjectTable.LocalPlayer`; if null → `float.PositiveInfinity` (mirrors `GetNearbyNpcs`). |
| `IsTargetable` | ClientStructs `Character.GameObject.GetIsTargetable()` (cast `obj.Address`→`FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*`) | filters untargetable/dead-but-present. |
| `IsDead` | `(obj as IBattleChara)?.CurrentHp == 0` or ClientStructs `BattleChara.Health == 0` | dead actors are filtered by `KillPriority`. |
| `IsTargetingPlayer` | `(obj as IBattleChara)?.TargetObjectId == local.GameObjectId` | aggro-on-us (+150 in `KillPriority`). |
| `OnPlayerEnmityList` | ClientStructs enmity/Hater list: `UIState.Instance()->Hater` entries contain the actor's `EntityId` | scan the Hater list for `obj.GameObjectId`; on null/empty → `false`. |
| `HasQuestMarker` | ClientStructs `Character.NamePlateIconId != 0` (cast to `FFXIVClientStructs…Character*`) | nameplate quest icon present (+100). |

Filter: only include actors within `radius`. Return `Result.Ok(list)`; on `LocalPlayer == null` still return `Result.Ok` with `PositiveInfinity` distances (the radius filter drops them) — fail-open, never throw. **Builder note:** every ClientStructs cast is `unsafe`; the precise pointer chains (`GameObject*`, `BattleChara*`, `Character*`, `UIState->Hater`) are GAME-* — the user verifies the populated fields against a live enemy.

**Why richer than `GetNearbyNpcs`?** `KillPriority` (part A, pure) consumes all six priority fields; `GetNearbyNpcs` returns only `(Id, Position, Distance)` and serves dialogue, not combat. They stay separate (part-A D3).

### A4 — `EngineHost` `Engage` arm + the rotation-lease latch (CI-testable seam)

The engine emits `EngineAction.Engage(CombatStep, KillTarget?)`; the controller already issued `SetTarget`/`ClearTarget` via the adapter **inside `CombatController.Decide`** during the engine's `Decide` (confirmed `CombatController.cs:51-63`). So the host's `Engage` arm does **not** re-target — it only drives the **rotation lease lifecycle** and records the action in the trace.

**Decision: a single host-side latch field `private bool _inRotationLease;` on `EngineHost`** is the authoritative "we currently hold a running rotation for the active combat step". Lifecycle:

- **On the first `Engage`** of a combat step (`!_inRotationLease`): `await _combat.StartRotation(ct);` then `_inRotationLease = true`. Subsequent `Engage` ticks are no-ops on the latch (idempotent — `StartRotation` is itself idempotent at the adapter, but the latch avoids per-tick IPC churn).
- **On leaving the combat step** — any dispatched action that is **not** `Engage` while `_inRotationLease` is true → `await _combat.StopRotation(ct); _inRotationLease = false;`. This covers: the step completes (next action is `Navigate`/`Interact`/`Wait` for the following step), the run terminates (`Done`/`AwaitUser`), or the user stops the run (`StopRun`/`EndRun`).
- **`Engage(step, null)`** (controller found nothing to attack this tick) keeps the latch as-is — we still hold the lease, the rotation simply has no target (WrathCombo idles).
- **`EndRun()`** must release: `if (_inRotationLease) { _combat.StopRotation(CancellationToken.None)…; _inRotationLease = false; }` before clearing `_engine`. Interruption safety: a run that ends mid-combat (stop, error, auto-mode halt) must not leak the WrathCombo lease.

**The CI-testable seam:** the latch transitions — "first Engage → StartRotation once", "non-Engage after Engage → StopRotation", "EndRun while latched → StopRotation", "Engage(null) holds the latch" — are pure host orchestration that drive `ICombat` only. They are tested against `FakeCombat` (which records `RecordedRotation` Start/Stop calls) by exercising the latch logic directly. **Decision: extract the latch transition into a small pure helper so it is unit-testable without a live `EngineHost`/Dalamud:**

```csharp
// QuestForge.Plugin/Combat/RotationLeaseLatch.cs  (NEW — pure, CI-testable)
public sealed class RotationLeaseLatch
{
    private bool _inLease;
    public bool InLease => _inLease;

    /// Drives Start/Stop on the given ICombat based on the dispatched action.
    /// Returns the act taken (for assertions). Engage → ensure Start; anything else → ensure Stop.
    public async Task<LeaseAct> OnAction(EngineAction action, ICombat combat, CancellationToken ct)
    {
        if (action is EngineAction.Engage)
        {
            if (_inLease) return LeaseAct.None;
            await combat.StartRotation(ct); _inLease = true; return LeaseAct.Started;
        }
        if (_inLease) { await combat.StopRotation(ct); _inLease = false; return LeaseAct.Stopped; }
        return LeaseAct.None;
    }

    /// Forced release on run teardown / interruption.
    public async Task<LeaseAct> Release(ICombat combat, CancellationToken ct)
    {
        if (!_inLease) return LeaseAct.None;
        await combat.StopRotation(ct); _inLease = false; return LeaseAct.Stopped;
    }
}
public enum LeaseAct { None, Started, Stopped }
```

`EngineHost` owns a `RotationLeaseLatch` and calls `OnAction` at the top of `DispatchAction` (before the per-action switch) and `Release` in `EndRun`. **Why a separate latch class rather than inline `bool` on `EngineHost`?** `EngineHost` is Dalamud-coupled and untestable in CI; `RotationLeaseLatch` depends only on `ICombat` + `EngineAction` (both pure-side types), so the transition rules are exercised against `FakeCombat` with no game. This mirrors part-A's decision to keep `CombatController` in the engine assembly for the same testability reason. **Rejected:** putting Start/Stop directly in the `Engage` switch arm — then "stop on the *next, non-Engage* action" has no place to live (the switch only sees one action at a time), and the teardown/interruption release would be duplicated and untested.

**Where the `Engage` switch arm goes:** a new `case EngineAction.Engage engage:` in `DispatchAction` that (a) the latch already handled Start/Stop for, (b) records the combat action in the trace (§A6), and (c) advances dialogue / cutscene-skips like the other arms (combat can be interrupted by cutscenes). It does **not** call `SetTarget` (the controller did).

### A5 — Flip `GetHostileActors` recording/replay from no-record/no-scan to record/scan (Half 2)

Part A made `RecordingGameStateProvider.GetHostileActors` delegate-through (no `Record`) and `ReplayGameStateProvider.GetHostileActors` return empty without scanning (the no-cascade choice). Part B flips both to the standard pattern every other `IGameStateProvider` read already uses:

- **`RecordingGameStateProvider.GetHostileActors`** → call inner, then `Record(nameof(GetHostileActors), radius, result);` (exactly like `GetNearbyNpcs` at `RecordingGameStateProvider.cs:170-175`). The argument is `radius` (a float); the value is the `IReadOnlyList<HostileActor>` (serialized as a JSON array of objects). The existing `Record<T>` dedup-on-value-change applies unchanged.
- **`ReplayGameStateProvider.GetHostileActors`** → `var obs = ScanNext(nameof(GetHostileActors), radius); return Materialize<IReadOnlyList<HostileActor>>(obs.Value);` (exactly like `GetNearbyNpcs` at `ReplayGameStateProvider.cs:122-126`). Both the legacy and segmented scanner paths are covered by `ScanNext`.

**The argument is `radius` (the controller's `ScanRadius`, currently `30f`).** Record-side and replay-side must serialize `radius` identically so the `(method, arg)` dedup key and scanner match key align. `30f` serializes as `30` via the camelCase `_jsonOpts`; `ReplayGameStateProvider` passes the same `float radius` to `ScanNext`, which serializes it the same way. **Decision: keep `ScanRadius` a compile-time constant in `CombatController`** so record-time and replay-time radii are byte-identical (a future author-configurable radius would have to be recorded as part of the step and is out of scope). Pin this with a round-trip test (§E group RH).

**`HostileActor` serialization round-trip (the subtle case, mirroring QUEST_VARIABLES §2.5).** `HostileActor` is a `record` of `(ActorId, uint, WorldPosition, float, bool, bool, bool, bool, bool)`. `ActorId` is `readonly record struct ActorId(ulong Value)`; STJ serializes it as `{"value":<ulong>}` via the camelCase options on both record (`_jsonOpts`) and replay (`ReplayJsonOptions.Default`) sides — the same options already round-trip `ZoneId`/`NpcId`/`JobId` (all single-`Value` record structs) through `GetNearbyNpcs`/`FindNpc`. **Decision: keep `HostileActor` and `ActorId` as plain records with no custom converters**, so the existing materializer round-trips them with zero new serialization code. Pin a record↔replay round-trip test asserting a populated `HostileActor` survives byte-identically (§E group RH).

### A6 — `RecordingCombat` proxy: capture combat *acts* (Half 2)

Part A shipped no `RecordingCombat`. Part B adds it so the combat actions the controller/host perform land in the trace for debugging and human readability. **Decision: combat acts are recorded as `ObservationEvent`s with a `method`/`argument`/`value` shape (reusing the existing event type), NOT a new event kind**, and they are **debugging-only — replay does NOT consume them.** Rationale and consequences:

- **Why reuse `ObservationEvent`, not a new `TraceEvent` subtype?** A new event type forces edits to `TraceEvent`'s `[JsonDerivedType]` table, `TraceEventJsonContext`, the qf-trace `TraceEventParser` discriminator switch, and `TraceReader` — a cross-cutting change for data that replay never reads. Reusing `ObservationEvent` (the same vehicle the `UseAethernet` *action* already uses in `EngineHost.DispatchAction:240`) is precedented and zero-schema-churn. **Rejected:** a `CombatActEvent` subtype — unnecessary surface for non-replayed data.
- **Why not consumed by replay?** Under replay there is no game: `SetTarget`/`ClearTarget`/`StartRotation`/`StopRotation` are side-effects on a live `TargetManager`/WrathCombo that do not exist. Completion derives from the recorded `expect` (quest vars/flags/sequence — already replayed), and the controller's target *selection* derives from the **recorded `GetHostileActors` observations** (§A5) so `KillPriority` resolves identically. Therefore the combat acts are pure side-effects/no-ops under replay and need not be scanned. The `RecordingCombat` records them only so a human (or a future debugger) can see "we told the game to target actor 9, started the rotation, …" in the trace.

```csharp
// QuestForge.Adapters/Recording/RecordingCombat.cs  (NEW — wraps ICombat)
public sealed class RecordingCombat : ICombat
{
    private readonly ICombat _inner;
    private readonly ITraceWriter _trace;
    private readonly Func<string?> _runIdAccessor;
    private readonly TimeProvider _clock;
    private readonly bool _skipIfNoRunId;
    // NO dedup map — combat acts are an ordered side-effect log, not a deduped observation stream.

    public async Task<Result<Unit>> SetTarget(ActorId target, CancellationToken ct)
    {
        var r = await _inner.SetTarget(target, ct);
        RecordAct("SetTarget", new { actorId = target.Value }, r);   // ObservationEvent, debug-only
        return r;
    }
    // ClearTarget / StartRotation / StopRotation: same shape (StartRotation/StopRotation arg = null).
    // IsRotationModuleAvailable / GetRotationModule / UseAction* : delegate-through, NOT recorded
    //   (reads with no side-effect; not needed for debugging the engage loop).
}
```

- **No dedup.** Unlike `RecordingGameStateProvider.Record` (deduped on value-change), combat acts are an ordered command log — recording two consecutive `SetTarget(9)` would be unusual (the controller suppresses redundant targets, part-A CC-no-redundant-settarget) but if it happens both should appear. Use a non-deduping write.
- **`method` values:** `"SetTarget"`, `"ClearTarget"`, `"StartRotation"`, `"StopRotation"` (distinct from the `GetHostileActors` read method so they never collide with the scanner's read keys).
- **Wiring:** `EngineHost.BeginRun` wraps `_combat` in `RecordingCombat` exactly as it wraps `_gameStateInner`/`_questStateInner` in their recording proxies (`EngineHost.cs:165-169`). Since combat acts are not replayed, this wrapping is **for recording only** — the engine and controller still see `ICombat` and behave identically.

**Fixture-safety of `RecordingCombat`:** because `RecordingCombat`'s recorded methods (`SetTarget`/`StartRotation`/…) are **distinct method names** from any `IGameStateProvider`/`IQuestState` read, and because the replay scanner never asks for them (replay uses `FakeCombat`, not `ReplayCombat` — there is no replay combat proxy), adding `RecordingCombat` cannot starve any fixture. A combat act in a trace is simply ignored by the replay scanner (it only `ScanNext`s for reads the engine performs).

### A7 — Fixture-starvation analysis for the `GetHostileActors` record/scan flip (the central safety claim)

**Claim: flipping `RecordingGameStateProvider.GetHostileActors` to record and `ReplayGameStateProvider.GetHostileActors` to scan does NOT starve any existing fixture, and requires NO re-record.** This is the opposite of the quest-variables change (which added a read to the *common* per-tick path and forced a `with-attunement` re-record).

**Why:** `GetHostileActors` is **step-gated** (part-A D6, confirmed at `QuestEngine.cs:431-450`). The only caller is `CombatController.Decide`, which the engine calls **only** from the `CombatStep` async arm of the step loop — never in the per-tick prelude (`GetUiState`/`GetPlayerPosition`/`GetPlayerZone`/`GetQuestVariables`). Therefore:

- During replay of a fixture whose quest has **no `combat` step**, the active step is never a `CombatStep`, so `CombatController.Decide` is never called, so `ReplayGameStateProvider.GetHostileActors` is never called, so the segmented scanner is never asked for a `(GetHostileActors, 30)` observation. The pair never enters the read pattern → **no `ReplayObservationStarvationException`.**
- The two existing fixtures qualify: `simple-linear-acceptance` (scripted state, quest 66130 — `step:talk`/`step:travel`) and `with-attunement` (generic scanner-backed, quest 65644 — `step:attune`/`step:travel`) contain **no `combat` step** (confirmed via the qf-trace capability mappings and the existing fixture inventory). Both stay green; **no re-record.**
- A *future* combat fixture records `GetHostileActors` from creation (its active step *is* a `CombatStep`), so its trace carries the `(GetHostileActors, 30)` pair and replays without starvation. That is the round-trip §A8 enables.

**This is the direct payoff of part-A step-gating.** Pin it with a test that runs the two existing fixtures green AND asserts a non-combat step tick records no `GetHostileActors` (§E group NS).

### A8 — `EngineAction.Engage` in the fixture / transition format, and replay determinism

**How an `Engage` decision appears in a recorded fixture (confirmed by reading the pipeline):**

1. **Engine writes the `DecisionEvent`.** `QuestEngine.Tick` writes `new DecisionEvent(runId, stepId, ActionType: action.GetType().Name, …)` for non-terminal actions (`QuestEngine.cs:276-281`). `Engage` is non-terminal → its `ActionType` is `"Engage"` (PascalCase, the `GetType().Name`). The `StepId` is the combat step's id (the engine returns `(Engage, step.Id)` at `QuestEngine.cs:449`).
2. **TraceSession decision-debounce** (`TraceSession.cs:142-147`) suppresses consecutive identical `(runId, stepId, "Engage")` decisions — so a combat step that emits `Engage` for N ticks writes **one** `Engage` decision (until the step changes or completes), exactly like a multi-tick `Navigate`. Good: the fixture gets one `engage` transition per combat step, not N.
3. **qf-trace `extract-fixture`** (`TraceToFixtureExtractor.Extract`, confirmed) builds transitions from `DecisionEvent.ActionType.ToLowerInvariant()` → `"engage"`, skips terminal actions (`engage` is not terminal — `TraceConstants.IsTerminalAction` only matches `done`/`awaituser`), and dedups consecutive identical pairs. So the committed fixture's `expectedTransitions` entry is `{ "stepId": "<combat-step-id>", "actionType": "engage" }`.

**How the replay harness compares it (confirmed by reading `EngineFixtureTests`):** the harness drives the engine and maps each emitted action to a string via `ActionTypeString(action)` (`EngineFixtureTests.cs:264-272`). That switch has **no explicit `Engage` arm**, so it falls through to `action.GetType().Name.ToLowerInvariant()` → `"engage"`. This already matches the extractor's `"engage"`. **Decision: add an explicit `EngineAction.Engage _ => "engage"` arm to `ActionTypeString` anyway** (so the mapping is intentional and documented, not an accident of the default fall-through). This is a one-line, behavior-preserving change that pins the contract. (Equivalent default-fallthrough behavior is also why no qf-trace change is needed — §F.)

**Replay determinism (spell it out):** a committed combat fixture replays deterministically because:
- The controller's target selection (`KillPriority.SelectTarget`) is a pure, deterministic function (part-A D4) over the **recorded** `GetHostileActors` observation for the active segment. Same recorded actors → same `KillTarget` → same `Engage(step, target)` every replay. No clock, no RNG (part-A tie-breaks are score → distance → lowest `ActorId.Value`).
- `SetTarget`/`StartRotation`/`StopRotation` are no-ops under replay (the harness wires `FakeCombat`, which records but does not touch a game; `TraceReplayFixtureState.Combat` is a `FakeCombat`, confirmed `TraceReplayFixtureState.cs:36`). They have no effect on the emitted decision sequence.
- Completion is the recorded `expect` (quest vars/flags/sequence): when the recorded `IsQuestComplete`/`questVariable`/`questFlag`/`questSequence` observation for the terminal segment satisfies the step's `expect`, the engine confirms-and-skips the combat step and advances — exactly as part-A EX-* established, now driven by recorded observations under the segmented scanner.
- The combat acts recorded by `RecordingCombat` (§A6) are ignored by replay (no `ReplayCombat`), so they cannot perturb determinism.

**Segment alignment note (Builder):** the segmented scanner advances one segment per new `(stepId, actionType)` transition (`TraceReplayFixtureState.OnTransitionRecorded` → `_scanner.AdvanceSegment`, confirmed). An `engage` transition is one segment; the `GetHostileActors` observation(s) for that segment must be recorded *within* that segment in the trace (they are, because recording is in-order with the engine's reads). The terminal segment carries the `expect`-satisfying quest-state observation. No special-casing — `engage` is just another transition to the scanner.

---

## Task breakdown

### Task 1 — Half 2: flip `GetHostileActors` record/scan (`QuestForge.Adapters`, `QuestForge.Adapters.Fakes`)

**1.1** `RecordingGameStateProvider.GetHostileActors` (`QuestForge.Adapters/Recording/RecordingGameStateProvider.cs:185-189`): replace the delegate-through with the record pattern — `var result = await _inner.GetHostileActors(radius, ct); Record(nameof(GetHostileActors), radius, result); return result;`. Update the XML doc to state it now records (combat-only, step-gated, no starvation per §A7).
**1.2** `ReplayGameStateProvider.GetHostileActors` (`QuestForge.Adapters.Fakes/Replay/ReplayGameStateProvider.cs:208-210`): replace the empty-return with `var obs = ScanNext(nameof(GetHostileActors), radius); return Task.FromResult(Materialize<IReadOnlyList<HostileActor>>(obs.Value));`. Update the XML doc.

### Task 2 — Half 2: `RecordingCombat` proxy (`QuestForge.Adapters`)

**2.1** Add `QuestForge.Adapters/Recording/RecordingCombat.cs` (A6): wraps `ICombat`; records `SetTarget`/`ClearTarget`/`StartRotation`/`StopRotation` as non-deduped `ObservationEvent`s with distinct `method` names; delegates `IsRotationModuleAvailable`/`GetRotationModule`/`UseAction*` through unrecorded. Constructor mirrors `RecordingGameStateProvider` (`inner, trace, runIdAccessor, clock?, skipIfNoRunId`).

### Task 3 — Half 1 (orchestration, CI-testable seam): host lease latch (`QuestForge.Plugin`)

**3.1** Add `QuestForge.Plugin/Combat/RotationLeaseLatch.cs` (A4): pure `OnAction(action, combat, ct)` + `Release(combat, ct)` + `InLease`.
**3.2** `EngineHost`: add `private readonly RotationLeaseLatch _leaseLatch = new();`; call `await _leaseLatch.OnAction(action, _combat, ct)` at the top of `DispatchAction`; add a `case EngineAction.Engage engage:` arm (records via the recording-combat path is already covered by wrapping; this arm advances dialogue / cutscene-skip and is otherwise a no-op since targeting + lease are handled elsewhere); call `await _leaseLatch.Release(_combat, CancellationToken.None)` in `EndRun` before clearing `_engine`.
**3.3** `EngineHost.BeginRun`: wrap `_combat` in `RecordingCombat` (mirroring the gamestate/queststate recording wrap at `EngineHost.cs:165-169`) and pass the wrapped instance to the `QuestEngine` ctor and to the latch. *(The engine ctor takes `ICombat`; the wrapped instance satisfies it.)*

### Task 4 — Half 1 (GAME-*): Dalamud real impls (`QuestForge.Adapters.Dalamud`)

**4.1** `Ipc/WrathComboIpc.cs` (A1): fill in the stub — `WrathIPCWrapper.Init`, lease/auto-rotation/config/release wrappers, callback provider registration + revocation handler hook.
**4.2** `Combat/WrathComboAdapter.cs` (A2): real `IsRotationModuleAvailable`/`GetRotationModule`/`StartRotation`/`StopRotation`/`SetTarget`/`ClearTarget`; `_lease` field; revocation handler clears `_lease`. Construct `WrathComboIpc` from `PluginServices.PluginInterface`. *(The adapter ctor already takes `PluginServices`.)*
**4.3** `State/DalamudGameStateProvider.GetHostileActors` (A3): real `IObjectTable`/ClientStructs scan populating all `HostileActor` fields. Replace the `Array.Empty` stub.

### Task 5 — `ActionTypeString` Engage arm (`QuestForge.Engine.Tests`)

**5.1** Add `EngineAction.Engage _ => "engage"` to `EngineFixtureTests.ActionTypeString` (A8) — intentional contract, behavior-preserving.

### Task 6 — Tests (`QuestForge.Engine.Tests`, `QuestForge.Adapters.Tests`)

Per §Given-When-Then. Recording/replay flip + round-trip + fixture round-trip + non-starvation in `QuestForge.Engine.Tests`; `RecordingCombat` contract + `RotationLeaseLatch` in the appropriate test project (`RecordingCombat` in `QuestForge.Adapters.Tests`; `RotationLeaseLatch` in `QuestForge.Plugin.Tests` or `QuestForge.Engine.Tests` depending on assembly references — Builder picks; it depends only on `ICombat`+`EngineAction`).

---

## Given-When-Then specifications

### G-RC — `RecordingCombat` proxy (`QuestForge.Adapters.Tests`)

- **RC-settarget-records:** Given `RecordingCombat` over a `FakeCombat`, a non-empty runId, gate-open trace writer. When `SetTarget(new ActorId(9))`. Then exactly one `ObservationEvent` with `Method == "SetTarget"` is written, its `Argument` encodes `actorId == 9`, AND the inner `FakeCombat.RecordedTargets` has one `SetTarget(9)` (proves delegate-through).
- **RC-clear-start-stop-record:** Given the proxy. When `ClearTarget`, `StartRotation`, `StopRotation` are each called once. Then three `ObservationEvent`s with `Method` `"ClearTarget"`, `"StartRotation"`, `"StopRotation"` are written in order, and the inner `FakeCombat` logs each.
- **RC-no-dedup:** Given the proxy. When `SetTarget(9)` is called **twice** with no change. Then **two** `SetTarget` observations are written (combat acts are an ordered log, not deduped — contrast `GetHostileActors` which dedups). Pins A6's no-dedup decision.
- **RC-reads-not-recorded:** Given the proxy. When `IsRotationModuleAvailable` and `GetRotationModule` are called. Then **no** `ObservationEvent` is written for them (reads/availability are delegate-through unrecorded), and the inner result is returned unchanged.
- **RC-skip-if-no-runid:** Given the proxy constructed `skipIfNoRunId: true` with the runId accessor returning null. When `SetTarget(9)`. Then **no** observation is written, but the inner `FakeCombat` still records the call (delegate-through always happens; only the trace write is gated). Mirrors `RecordingGameStateProvider` skip behavior.

### G-RH — `GetHostileActors` record/scan flip + round-trip (`QuestForge.Engine.Tests`)

- **RH-record-emits:** Given `RecordingGameStateProvider` over a `FakeGameStateProvider` with one scripted `HostileActor` (Id 9, DataId 100, dist 5, targetable, alive), runId set. When `GetHostileActors(30)`. Then exactly one `ObservationEvent` with `Method == "GetHostileActors"` is written, its `Argument` is `30`, and its `Value` is a JSON **array** of one object whose `id.value == 9`, `dataId == 100`, `isTargetable == true`. (Pins the flip from no-record → record.)
- **RH-record-dedup:** Given the same proxy. When `GetHostileActors(30)` is called twice with the actor list unchanged. Then only **one** `GetHostileActors` observation is written (the existing `Record<T>` value-change dedup applies). When the actor list then changes (actor moves / dies), a second observation is written. Mirrors the `GetNearbyNpcs`/quest-variables dedup contract.
- **RH-replay-scans:** Given a trace with one `GetHostileActors` observation (arg 30, value = the one-actor array). When a `ReplayGameStateProvider` (legacy or segmented) `GetHostileActors(30)` is called. Then `Result.Ok` with one `HostileActor` equal field-for-field to the recorded one. (Pins the flip from no-scan → scan.)
- **RH-roundtrip-byte-identical (the subtle case):** Record a `GetHostileActors` observation for a populated `HostileActor` (all nine fields non-default — Id 7, DataId 200, Position (1,2,3), dist 4.5, every bool distinct), then build a `ReplayGameStateProvider` from that one observation and read it back. Then every field round-trips identically — especially `ActorId.Value` (ulong) as `{"value":7}` not base64/string, and the four bools in correct order. Pins A5's no-custom-converter decision.
- **RH-replay-radius-mismatch-starves:** Given a trace recorded at arg 30. When replay calls `GetHostileActors(99)` (a different radius). Then the scanner cannot find a `(GetHostileActors, 99)` pair → `ReplayObservationStarvationException`. (Pins the contract that `ScanRadius` must be a constant — record and replay must use the same arg; A5.)

### G-NS — fixture non-starvation (`QuestForge.Engine.Tests`)

- **NS-existing-fixtures-green:** Run the parametric `EngineFixtureTests.EngineProducesExpectedTransitions` for `simple-linear-acceptance` and `with-attunement` (skip if questforge-data absent). Then **both pass unchanged** after the `GetHostileActors` record/scan flip — neither has a `combat` step, so `GetHostileActors` is never called during replay, so no `(GetHostileActors, *)` starvation. **No re-record.** Pins A7.
- **NS-noncombat-tick-no-hostile-read:** Given a `talk`/`travel`-only quest wired with `FakeGameStateProvider`, when `Tick` runs once, then `FakeGameStateProvider.RecordedReads` contains **no** `GetHostileActors` entry. Pins that the combat read stays step-gated and never leaked into the per-tick prelude even after the flip.

### G-LL — rotation-lease latch (`RotationLeaseLatch` vs `FakeCombat`)

- **LL-first-engage-starts:** Given a fresh `RotationLeaseLatch` and a `FakeCombat`. When `OnAction(Engage(step, target), combat)`. Then it returns `LeaseAct.Started`, `InLease == true`, and `FakeCombat.RecordedRotation` has exactly one `StartRotation`.
- **LL-repeat-engage-noop:** Given the latch already in lease. When `OnAction(Engage(...))` again. Then returns `LeaseAct.None`, `InLease` stays true, and `RecordedRotation` still has only the one `StartRotation` (no per-tick churn).
- **LL-engage-null-holds:** Given the latch in lease. When `OnAction(Engage(step, null))` (controller found no target). Then returns `LeaseAct.None`, `InLease` stays true, no new Start/Stop (we keep the lease; the rotation idles).
- **LL-nonengage-after-engage-stops:** Given the latch in lease. When `OnAction(Navigate(...))` (next step). Then returns `LeaseAct.Stopped`, `InLease == false`, and `RecordedRotation` has one `StartRotation` then one `StopRotation`.
- **LL-nonengage-while-unlatched-noop:** Given a fresh latch (not in lease). When `OnAction(Wait(...))`. Then returns `LeaseAct.None`, no Start/Stop recorded. Pins that we never stop a rotation we never started.
- **LL-release-while-latched-stops:** Given the latch in lease (e.g. run torn down mid-combat). When `Release(combat)`. Then returns `LeaseAct.Stopped`, `InLease == false`, `RecordedRotation` ends with a `StopRotation`. Pins interruption safety (EndRun must not leak the lease).
- **LL-release-while-unlatched-noop:** Given a fresh latch. When `Release(combat)`. Then `LeaseAct.None`, no `StopRotation`.
- **LL-terminal-stops:** Given the latch in lease. When `OnAction(Done())` (or `AwaitUser`). Then `LeaseAct.Stopped` (terminal actions are non-Engage → the latch releases). Pins lease release on run completion.

### G-FX — combat fixture round-trip + replay determinism (`QuestForge.Engine.Tests`)

- **FX-engage-actiontype:** Assert `EngineFixtureTests.ActionTypeString(new EngineAction.Engage(step, null)) == "engage"` (the new explicit arm; A8). Also assert the engine writes `DecisionEvent.ActionType == "Engage"` (PascalCase `GetType().Name`) for an `Engage` decision via a `CapturingTraceWriter` over a small combat quest. Pins the case-mapping contract end-to-end.
- **FX-engage-debounced-single-transition:** Given a combat quest whose `expect` stays unmet for several ticks (eligible hostile present, quest var below threshold), driven by a `CapturingTraceWriter` + `FakeGameStateProvider`/`FakeQuestState`/`FakeCombat`. When ticked N>1 times. Then exactly **one** `engage` `DecisionEvent` is captured for the combat step (TraceSession decision-debounce collapses repeats — `TraceSession.cs:142`). Pins one `engage` transition per combat step in the eventual fixture.
- **FX-synthetic-fixture-replays (the round-trip proof):** Build a synthetic JSONL trace for a 1-sequence combat quest: `run.start`; segment-0 observations (`GetQuestSequence=0`, `IsQuestComplete=false`, `GetUiState`, `GetPlayerPosition` in-range, `GetPlayerZone`, `GetQuestVariables=[0,…]`, `GetHostileActors(30)=[one eligible actor]`); a `decision (stepId=combat, "Engage")`; terminal-tail observations (`IsQuestComplete=true` / the `expect`-satisfying quest-state); `run.end "done"`. Wire `TraceReplayFixtureState.FromTraceFile` + the engine, drive the harness loop. Then the actual transitions are `[(combat, "engage")]` and the terminal action is `Done` — i.e. a recorded combat run replays deterministically to the expected single `engage` transition + `done`. Pins A8 determinism end-to-end against the segmented scanner.
- **FX-replay-target-deterministic:** Given the FX-synthetic trace where `GetHostileActors` recorded **two** eligible actors (one higher kill-priority). When replayed twice. Then both replays emit `Engage` with the **same** `Target` (the higher-priority actor by `KillPriority`), proving target selection reads the recorded observation and is deterministic (no game, no clock). 

### G-GAME — in-game-only acceptance (verified live by the user; NOT CI)

These are listed here so the Tester does **not** write CI tests for them (they require a game + WrathCombo + live enemies). They are the GAME-* acceptance criteria.

- **GAME-lease-acquire:** With WrathCombo installed, starting a combat step acquires a lease (`RegisterForLeaseWithCallback` returns a `Guid`), sets auto-rotation on, sets `DPSRotationMode.Manual`, and the character begins attacking the engine-set target.
- **GAME-lease-release:** Leaving the combat step (or `/qf stop` mid-combat) releases the lease (`ReleaseControl`); auto-rotation stops; no lease leak across runs.
- **GAME-lease-revocation:** Manually revoking QuestForge's lease in WrathCombo (or changing job) fires the `WrathComboCallback`; the adapter drops `_lease`; the next combat step re-acquires without error.
- **GAME-settarget:** `SetTarget(actorId)` sets `TargetManager.Target` to the correct live enemy; `ClearTarget` clears it.
- **GAME-scan-fields:** `GetHostileActors` populates `DataId`/`IsTargetable`/`IsDead`/`IsTargetingPlayer`/`OnPlayerEnmityList`/`HasQuestMarker` correctly against a known live enemy (e.g. a quest mob with a marker, an aggro'd add, a dead corpse).
- **GAME-end-to-end-kill:** A real `combat` quest step navigates to the arena, engages, the rotation kills the kill-set enemies, the `expect` (`questVariable`/`questFlag`) flips, and the step completes — recorded into a trace that `qf-trace extract-fixture` turns into a committable combat fixture.

---

## Implementation order

**Phase A — Half 2 recording/replay flip + `RecordingCombat`, 0.5 day.** Tasks 1, 2. Write RC-* (G-RC) and RH-* (G-RH); make green. Pure proxy work, no engine/host wiring. **Done before B.**

**Phase B — fixture round-trip + non-starvation + Engage arm, 0.5 day.** Tasks 5, 6 (FX-*, NS-*). Write FX-* (G-FX) and NS-* (G-NS); make green. Verify NS-existing-fixtures-green requires **no** re-record. **Done before C.**

**Phase C — host lease latch (CI seam), 0.5 day.** Task 3.1, 3.2, 3.3. Write LL-* (G-LL) against `FakeCombat`; make green. The `EngineHost` wiring compiles; the latch logic is fully unit-tested via `RotationLeaseLatch`. **Done before D.**

**Phase D — Half 1 Dalamud real impls (GAME-*), parallel-safe, in-game-verified.** Task 4. Builder writes `WrathComboIpc`/`WrathComboAdapter`/`DalamudGameStateProvider.GetHostileActors`; confirm-compile only in CI; the user verifies GAME-* live. Independent of A-C and can land in the same PR with GAME-* pending. *(Build to confirm the WrathCombo IPC signatures compile — the net10 prefix per CLAUDE.md notes — but reading `WrathCombo.API` is usually enough; see §A1.)*

---

## Done criteria

1. `RecordingGameStateProvider.GetHostileActors` records a `GetHostileActors` `ObservationEvent` (arg = radius, value = the actor array), deduped on value-change; `ReplayGameStateProvider.GetHostileActors` scans the recorded observation (legacy + segmented). Both verified in `QuestForge.Engine.Tests` with no game (RH-record-emits, RH-record-dedup, RH-replay-scans).
2. A populated `HostileActor` (incl. `ActorId` ulong and all four bools) round-trips byte-identically through record → replay with no custom converters; a radius mismatch starves (RH-roundtrip-byte-identical, RH-replay-radius-mismatch-starves).
3. The two existing replay fixtures (`simple-linear-acceptance`, `with-attunement`) stay green after the flip with **no re-record**, and a non-combat tick performs no `GetHostileActors` read — the step-gating payoff (NS-existing-fixtures-green, NS-noncombat-tick-no-hostile-read).
4. A new `RecordingCombat` proxy captures `SetTarget`/`ClearTarget`/`StartRotation`/`StopRotation` as non-deduped `ObservationEvent`s (debug-only, not replayed), delegating reads through unrecorded and honoring `skipIfNoRunId` (RC-*).
5. The `RotationLeaseLatch` starts the rotation on the first `Engage`, holds across `Engage(null)`, stops on the first non-`Engage` action and on `Release`, and never stops a rotation it never started — all against `FakeCombat`, no game (LL-*). `EngineHost` owns the latch, calls it in `DispatchAction`, and releases it in `EndRun` (interruption-safe).
6. An `Engage` decision serializes as `DecisionEvent.ActionType == "Engage"`, extracts to fixture `actionType "engage"`, debounces to one transition per combat step, and a synthetic combat trace replays deterministically to `[(combat, "engage")]` + `done` with target selection reading the recorded `GetHostileActors` observation (FX-*). No paired questforge-tools change is needed (§F).
7. (GAME-*) The user verifies live: WrathCombo lease acquire/release/revocation, `SetTarget`/`ClearTarget` against `TargetManager`, `GetHostileActors` field population, and an end-to-end combat-step kill recorded into a committable fixture (GAME-*). These are not CI-asserted.

---

## §F — questforge-tools (qf-trace) impact: NO paired change required

Confirmed by reading the pipeline:

- **`TraceEventParser`** dispatches only on the existing `type` discriminators; combat acts ride on `ObservationEvent` (type `"observation"`) so they parse unchanged. The `Engage` decision is a normal `DecisionEvent` (type `"decision"`). No new discriminator → no parser change.
- **`TraceToFixtureExtractor.Extract`** builds transitions from `DecisionEvent.ActionType.ToLowerInvariant()` (→ `"engage"`), skips only `done`/`awaituser` (`TraceConstants.IsTerminalAction`), and dedups consecutive identical pairs. An `engage` decision flows through with zero changes.
- **`CapabilityInferrer`** already maps `CombatStep → "step:combat"` (confirmed `CapabilityInferrer.cs:20`). So a combat quest's fixture gets the right capability tag automatically.
- **`SuggestFilename`** has no exact-match entry for a combat capability set (e.g. `["step:combat", "step:travel"]`), so it falls back to `simple-linear-acceptance.json`. This is **cosmetic** (a default suggested name) and not a blocker — the author renames the fixture. *Optional, non-blocking follow-up (NOT in this slice): add `(["step:combat", "step:travel"], "with-combat.json")` to `FilenameLookup` and `("step:combat", "with-combat.json")` to `DistinguishingCapPriority`.* Flagged for the corpus pass, not required for this slice's done criteria.

**Conclusion:** qf-trace already extracts a combat fixture correctly; this slice needs no questforge-tools PR.

---

## Exclusions (later slices — do NOT design or build here)

- **Death recovery / `InstanceKind` routing (#63).** Open-world return-to-aetheryte + re-plan, dungeon/trial/raid delegation (no counter increment), SPD note-death-and-wait. The latch and controller do not handle death; `IsPlayerDead`/recovery-ladder integration is a later slice gated on #63 (the live `ClassifyInstanceKind` still returns `Other`, confirmed `DalamudGameStateProvider.cs:110`).
- **A `ReplayCombat` proxy.** Replay uses `FakeCombat`; combat acts are not replayed (A6). No replay-side combat scanner.
- **RSR / BossMod rotation modules.** Additional `ICombat` impls behind the same interface and lease shape.
- **Triggered spawn types** (`AfterInteraction`/`AfterItemUse`/`AfterAction`/`AfterEmote`, FATE) and `ComplexCombatData` multi-stage fights — v1 ships `autoOnEnterArea`/`overworldEnemies` only.
- **Per-target movement during the fight** (melee gap-close, line-of-sight). The controller selects + targets; `Location` is coarse arena nav (part A). In-fight repositioning is a later concern.
- **Author-configurable scan radius.** `ScanRadius` stays a `CombatController` constant so record/replay args align (A5). Making it a `CombatStep` field is a schema change out of scope.
- **The optional `with-combat.json` filename mapping** in qf-trace `SuggestFilename` (§F) — cosmetic, corpus-pass follow-up.
- **`Engage` action-completed / action-submitted trace events.** This slice records combat acts as observations only; the richer `ActionSubmittedEvent`/`ActionCompletedEvent` modeling for combat is not added.

---

## Sub-split recommendation

This slice is already two clearly-separable halves and is the right size for one PR **if** the GAME-* items land with their criteria pending in-game verification. If the user prefers strictly-green-CI PRs, sub-split along the Half boundary:

- **PR B1 (CI-only, fully green):** Half 2 (Tasks 1, 2, 5) + the CI seam of Half 1 (Task 3 — `RotationLeaseLatch` + `EngineHost` wiring) + all CI tests (G-RC, G-RH, G-NS, G-LL, G-FX). No game required; ships the trace-capture + latch with full coverage.
- **PR B2 (in-game-verified):** Half 1 Dalamud real impls (Task 4 — `WrathComboIpc`, `WrathComboAdapter`, `DalamudGameStateProvider.GetHostileActors`) with the GAME-* acceptance items. Depends on B1 (the latch + recording proxy must exist first).

Recommendation: **sub-split into B1/B2.** B1 is a clean, fully-CI-green deliverable; B2 is the in-game integration the user verifies in one pass. This keeps CI green-only and isolates the un-CI-able Dalamud work.

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §Given-When-Then specifications (the CI-testable groups only — G-RC, G-RH, G-NS, G-LL, G-FX). The G-GAME group is in-game-only and gets NO CI tests.
- Happy paths: 7 scenarios (RC-settarget-records, RH-record-emits, RH-replay-scans, NS-existing-fixtures-green, LL-first-engage-starts, FX-synthetic-fixture-replays, FX-engage-actiontype)
- Edge cases: 14 scenarios (RC-clear-start-stop-record, RC-no-dedup, RC-reads-not-recorded, RH-record-dedup, NS-noncombat-tick-no-hostile-read, LL-repeat-engage-noop, LL-engage-null-holds, LL-nonengage-after-engage-stops, LL-nonengage-while-unlatched-noop, LL-release-while-latched-stops, LL-release-while-unlatched-noop, LL-terminal-stops, FX-engage-debounced-single-transition, FX-replay-target-deterministic)
- Error / boundary cases: 3 scenarios (RC-skip-if-no-runid, RH-roundtrip-byte-identical, RH-replay-radius-mismatch-starves)
- Expected total: ~24 CI tests — ~5 in `QuestForge.Adapters.Tests` (RecordingCombat, group RC), ~5 in `QuestForge.Engine.Tests` (record/scan flip + round-trip, group RH), ~2 in `QuestForge.Engine.Tests` (non-starvation, group NS), ~8 in `QuestForge.Plugin.Tests` or `QuestForge.Engine.Tests` (RotationLeaseLatch, group LL), ~4 in `QuestForge.Engine.Tests` (fixture round-trip + determinism, group FX) — plus the 6 GAME-* items verified in-game by the user (not CI).
