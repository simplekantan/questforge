# DalamudBestGearEquipper Implementation Plan

**Status:** ready for implementation
**Scope:** Flesh out the `DalamudBestGearEquipper` stub with Stylist-preferred / vanilla-fallback dual-path logic. No engine changes, no schema changes, no new tests beyond what already exists.
**Parent decisions:** `docs/EQUIP_BEST_GEAR_STEP_PLAN.md` EB-8 through EB-11
**Branch:** `feat/dalamud-best-gear-equipper`

---

## Files changed

| File | Change |
|---|---|
| `QuestForge.Adapters.Dalamud/Gear/DalamudBestGearEquipper.cs` | Flesh out from stub |
| `QuestForge.Plugin/PluginConfig.cs` | Add `PreferStylist` property |
| `QuestForge.Plugin/EngineHost.cs` | Accept `PluginConfig`, pass `Func<bool>` to equipper ctor |
| `QuestForge.Plugin/Plugin.cs` | Pass `config` to `EngineHost` ctor |

---

## Decisions

### DBG-1: Constructor signature -- `Func<bool>` for config, not `PluginConfig`

The adapter layer (`QuestForge.Adapters.Dalamud`) must not reference `QuestForge.Plugin` (lower layer cannot depend on upper). The equipper receives a `Func<bool> preferStylist` closure, matching EB-8.

```csharp
public DalamudBestGearEquipper(PluginServices svc, Func<bool> preferStylist)
```

**What breaks if violated:** Circular assembly reference (`Plugin` -> `Adapters.Dalamud` -> `Plugin`). Build fails.

### DBG-2: Stylist IPC gate names and call shapes

From `Stylist/Services/IpcProvider.cs` (verified against source):

| Gate | ECommons subscriber type | Parameters |
|---|---|---|
| `Stylist.UpdateCurrentGearsetEx` | `ICallGateSubscriber<bool?, bool?, object>` | `(true, true)` -- pull from inventory, always equip |
| `Stylist.IsBusy` | `ICallGateSubscriber<bool>` | none |

ECommons `[EzIPC]` convention: prefix is the plugin's `InternalName` (`"Stylist"` from `Stylist.json`), gate name is `Prefix.MethodName`.

The `Action<bool?, bool?>` IPC signature maps to `ICallGateSubscriber<bool?, bool?, object>` on the subscriber side (ECommons convention: void returns use `object` as the last type parameter for `InvokeAction`).

### DBG-3: `EquipBestGear` implementation -- try-Stylist, catch-fallback

```
1. if _preferStylist():
   a. try IsStylistAvailable(ct)
   b. if available:
      - get IPC subscriber for "Stylist.UpdateCurrentGearsetEx"
      - call InvokeAction(true, true)
      - return Result.Ok(EquipOutcome.Equipped)
      - (any IPC exception -> fall through to vanilla)
2. vanilla path:
   a. PlayerState.Instance() -> null guard
   b. RecommendEquipModule.Instance() -> null guard
   c. SetupForClassJob(PlayerState->CurrentClassJobId)
   d. EquipRecommendedGear()
   e. return Result.Ok(EquipOutcome.Equipped)
3. if both null guards fail: return Result.Fail("recommendEquipUnavailable", ...)
```

**Fire-and-forget:** No `IsBusy` polling, no `IsUpdating` polling. If gear has not yet changed when the engine next ticks, the step re-fires (harmless no-op per EB-2).

### DBG-4: `IsStylistAvailable` implementation

```
1. try:
   - get IPC subscriber for "Stylist.IsBusy"
   - call InvokeFunc() (don't care about the bool result)
   - return Result.Ok(true)
2. catch (Exception):
   - return Result.Ok(false)
```

Catching broad `Exception` is intentional: ECommons throws various exception types for IPC failures (`IpcNotReadyError` is internal to Dalamud's IPC layer; not always the exact type surfaced through ECommons). The established ecosystem pattern (Questionable, AutoDuty) is to catch broadly on availability probes.

### DBG-5: IPC subscriber acquisition -- per-call, not cached

Each call to `EquipBestGear` / `IsStylistAvailable` creates a fresh `ICallGateSubscriber` via `_svc.PluginInterface.GetIpcSubscriber<...>(gateName)`. No caching.

**Rationale:** Stylist may be installed/uninstalled mid-session. A cached subscriber would go stale. The subscriber creation is cheap (dictionary lookup in Dalamud's IPC registry). This matches `DalamudActionExecutor`'s pattern for its IPC calls.

**Rejected alternative:** Cache subscribers in the constructor. Rejected because hot-reload of Stylist during a play session would leave us holding a dead reference.

### DBG-6: Vanilla path -- `SetupForClassJob` is synchronous in practice

`RecommendEquipModule.SetupForClassJob(byte classJobId)` returns `bool` and updates internal state. The `IsUpdating` field transitions to `true` briefly during internal processing, but `EquipRecommendedGear()` can be called immediately after `SetupForClassJob` -- the game handles the sequencing internally when called on the framework thread (which all Dalamud adapters run on).

Questionable polls `IsUpdating` as a safety margin. We skip this because:
1. Our fire-and-forget design means re-firing on the next tick handles any race.
2. Both calls execute on the framework thread, so `SetupForClassJob`'s internal work completes before `EquipRecommendedGear` runs.

### DBG-7: `PluginConfig.PreferStylist` -- defaults to `true`

```csharp
// QuestForge.Plugin/PluginConfig.cs
/// <summary>
/// When true, EquipBestGearStep prefers Stylist over the native Recommended Gear module.
/// Falls back to native if Stylist is not installed. Default: true.
/// </summary>
public bool PreferStylist { get; set; } = true;
```

Stylist produces better gear choices (stat weighting, inventory pulling). Users without Stylist are unaffected: the adapter silently falls back to vanilla.

### DBG-8: `EngineHost` constructor change

Add `PluginConfig config` as a third parameter:

```csharp
public EngineHost(PluginServices services, TraceSession traceSession, PluginConfig config)
```

Change the `_bestGearEquipper` construction line from:
```csharp
_bestGearEquipper = new DalamudBestGearEquipper(services);
```
to:
```csharp
_bestGearEquipper = new DalamudBestGearEquipper(services, () => config.PreferStylist);
```

The caller (`Plugin.cs`) already has `config` in scope -- pass it through.

### DBG-9: No pure logic to extract -- no unit tests for this adapter

Both paths (`EquipBestGear`, `IsStylistAvailable`) are pure Dalamud/IPC shell code with no branching logic that can be tested without a running game. There is no enum mapping, no status interpretation, no command formatting to extract.

The only "logic" is the `if (_preferStylist())` branch, which is a trivial boolean read -- not worth a test class.

Per `feedback_tdd_even_for_adapters.md`: "extract pure logic for unit tests." There is no pure logic here. Documented explicitly so the reviewer does not request tests for untestable IPC calls.

### DBG-10: Error codes

| Error code | When | Path |
|---|---|---|
| `recommendEquipUnavailable` | `RecommendEquipModule.Instance()` returns null | Vanilla |
| `playerStateUnavailable` | `PlayerState.Instance()` returns null | Vanilla |

Stylist IPC failures do not produce error codes -- they silently fall through to vanilla. If vanilla also fails, the `Result.Fail` propagates to the engine, which increments the step-failure counter per normal flow.

---

## Scope guard -- what this plan does NOT include

- **No engine changes.** `EngineAction.EquipBestGear`, the dispatch arm in `QuestEngine`, and the `EngineTestHarness` arm are all covered by the parent plan (EB-1 through EB-7) and tracked separately.
- **No new tests.** There is no extractable pure logic (DBG-9). The in-game smoke test (Slice 4 in the step-type protocol) is the verification mechanism.
- **No `IsBusy` / `IsUpdating` polling.** Fire-and-forget by design (EB-2, DBG-3, DBG-6).
- **No `RecordingBestGearEquipper`.** Write-only adapter; `action.submitted`/`action.completed` events suffice (EB-12).
- **No UI for `PreferStylist`.** Settings UI is tracked separately; the config property is settable via JSON config file or future UI.
- **No tooling catch-up.** TraceConstants / CapabilityInferrer / FilenameLookup changes are tracked in EB-14 (separate paired PR).
