# PlayerStatePanel Field Expansion Plan (issue #12)

**Status:** ready for test creation
**Input docs:** docs/AUTHORING.md §4.1 (panel mock), GitHub issue #12
**Output:** the authoring Player State panel shows Job, Level, Mount, Combat, HP, and Instance below the existing Zone/Position lines. A new pure `PlayerStateFormatter` is covered by unit tests in `QuestForge.Plugin.Tests`.
**Branch:** `feat/playerstate-panel-fields` (off `main`)
**Scope:** Plugin layer only. NO engine / `GameStateSnapshot` / `SnapshotAggregator` / trace / fixture changes.

---

## Dependency graph

```
QuestForge.Adapters            (MountState, InstanceKind, JobId, WorldPosition — Dalamud-free)
   └── QuestForge.Plugin.Tracing      ← NEW: PlayerStateFormatter (pure, net10.0)
         ├── consumed by QuestForge.Plugin.Tests   (unit tests — the only tested surface)
         └── consumed by QuestForge.Plugin          (PlayerStatePanel shell + EngineHost summary)
```

**Build order:** formatter in `QuestForge.Plugin.Tracing` first (TDD against tests) → wire panel shell + EngineHost summary in `QuestForge.Plugin`.

---

## Architectural decisions (read before coding)

### Decision 1 — Formatter lives in `QuestForge.Plugin.Tracing`, NOT `QuestForge.Plugin`

The task brief says "pure `PlayerStateFormatter` in `QuestForge.Plugin`". That is **not unit-testable** as written: `QuestForge.Plugin` targets `net10.0-windows7.0` under the Dalamud.NET.Sdk, and `QuestForge.Plugin.Tests` deliberately references only Dalamud-free assemblies — it references `QuestForge.Plugin.Tracing`, never `QuestForge.Plugin` (see the comment block in `QuestForge.Plugin.Tests.csproj`). This mirrors the existing precedent: `InteractionPanelFilter` lives in `QuestForge.Plugin.Tracing.Authoring` for exactly this reason.

**Therefore the formatter goes in `QuestForge.Plugin.Tracing/Authoring/PlayerStateFormatter.cs`, namespace `QuestForge.Plugin.Tracing.Authoring`.** It is a pure `static class`. `QuestForge.Plugin.Tracing` already references `QuestForge.Adapters`, so it sees `MountState`, `InstanceKind`, `JobId`, `WorldPosition`.

- **Alternative rejected — put it in `QuestForge.Plugin`:** would require the test project to reference the Dalamud plugin assembly, dragging in ECommons/Dalamud lifecycle + the dalamud.dev NuGet feed. The repo explicitly avoids this (the csproj comment and `InteractionPanelFilter` both establish the convention).
- **What breaks if violated:** tests fail to compile (CS0234, unresolved namespace) or the test project stops restoring on CI runners without the Dalamud feed.

### Decision 2 — Live read via the existing raw `IGameStateProvider`, sync, every frame, no new wiring class

The panel reads current values directly from the live `DalamudGameStateProvider` using the established `.GetAwaiter().GetResult()` pattern (the Dalamud impls all return `Task.FromResult`, so this never blocks across frames). This matches `EngineHost.GetGameStateSummary()` exactly.

**Wiring choice: inject `IGameStateProvider` (and `IDataManager`) into `PlayerStatePanel` directly.** `EngineHost` already exposes the raw provider as `public IGameStateProvider DebugGameState => _gameStateInner;`. `Plugin.cs` constructs the panel (`new PlayerStatePanel(_authoringHost)` at Plugin.cs:105) and has both the `EngineHost` and `IDataManager` in scope.

New panel ctor:
```csharp
public PlayerStatePanel(
    AuthoringHost host,
    IGameStateProvider gameState,   // pass EngineHost.DebugGameState
    IDataManager dataManager)       // for the ClassJob name lookup
```

- **Alternative rejected — shared `PlayerStateReader` used by both panel and `EngineHost.GetGameStateSummary` (DRY):** tempting, but `GetGameStateSummary` returns a single flat debug string and the panel needs the six values *plus* a resolved job name string and HP; a shared gather type would have to return a struct the summary then re-flattens. The duplication is six one-line live reads — not worth a new abstraction (the constraint says no speculative abstractions). We DO fix the latent bug in the summary (Decision 4) but keep the two call sites independent.
- **Alternative rejected — gather method on `AuthoringHost`:** `AuthoringHost` is built around `SnapshotAggregator`/`UIObserver` polling and does not hold the live `IGameStateProvider` (it has `_services` but reads position off `ObjectTable` directly). Adding a live-read gather there muddies its single responsibility and still would not expose HP. Injecting the provider into the panel is the minimal cut.
- **Throttle decision: read every `Draw()` (per-frame), no cache.** Six `Task.FromResult` reads per frame while the panel is open is the same cost the existing summary pattern already pays on demand; `Draw()` only runs while the window is open. A throttle/cache adds state and a clock for no measurable benefit. Noted here so it is a conscious choice, not an oversight. If profiling ever shows cost, cache behind a ~100 ms gate — out of scope now.
- **Testability implication:** zero. The panel shell is untested (ImGui + live Dalamud). All logic worth testing is funnelled into the pure formatter (Decision 5).

### Decision 3 — HP is NOT exposed by any adapter today (OPEN QUESTION — user gate)

Confirmed by inspection:
- `PlayerStateSnapshot` (IGameStateProvider.cs:58-68) carries Position, Zone, Job, JobLevel, InCombat, MountState, Diving, Casting, Dead — **no HP fields.**
- `IGameStateProvider` has no `GetHp` / `GetCurrentHp` / health accessor.
- The only HP access in the codebase is `IBattleChara.CurrentHp` read directly off Dalamud objects for hostile-actor death detection (`DalamudGameStateProvider.cs:424`), not surfaced through the interface.

So HP cannot come from the chosen live-read path without one of:

- **Option H1 (in scope, recommended):** read HP directly off the live Dalamud player object in the panel *shell* — `_dataManager`-adjacent `ObjectTable.LocalPlayer` exposes `CurrentHp`/`MaxHp` on `IPlayerCharacter`. This is Dalamud (untestable, fine — it is shell code) and the two `uint` values are passed into the pure formatter as plain ints. **No interface/adapter/engine change.** Requires the panel ctor to also receive whatever exposes `LocalPlayer` (the `IObjectTable`, or reuse `PluginServices`). This keeps the read inside the same "live Dalamud, untested shell" boundary as the cutscene/target reads elsewhere.
- **Option H2 (out of scope — flag):** add `GetPlayerHp` (or `CurrentHp`/`MaxHp` to `PlayerStateSnapshot`) on `IGameStateProvider`. This expands scope: new interface member → implement in `DalamudGameStateProvider`, `FakeGameStateProvider`, `ReplayGameStateProvider`, `RecordingGameStateProvider`, and a trace event for HP. The brief explicitly forbids adapter/engine changes, so H2 is deferred.

**OPEN QUESTION for the user's gate:** Approve **H1** (read `LocalPlayer.CurrentHp/MaxHp` directly in the panel shell, pass ints to the formatter — no adapter change)? If H1 is rejected on the grounds that the panel should only ever touch `IGameStateProvider`, then HP requires H2 (an adapter addition) and must be split into a follow-up issue; the rest of issue #12 (Job/Level/Mount/Combat/Instance) ships without HP. The formatter is designed to handle both: it always takes `hpCurrent`/`hpMax` ints, so if HP is deferred the shell passes `0, 0` and the formatter renders the "unknown" form (see GWT).

### Decision 4 — Fix `GetJobLevel(default, ct)` to pass the current job

`EngineHost.GetGameStateSummary()` (EngineHost.cs:151) calls `_gameStateInner.GetJobLevel(default, ct)` — `default` is `JobId(0)`, the wrong job. The signature is `GetJobLevel(JobId job, CancellationToken ct)`. The correct call passes the job already read on the previous line:

```csharp
var job   = _gameStateInner.GetCurrentJob(ct).GetAwaiter().GetResult().ValueOrDefault;
var level = _gameStateInner.GetJobLevel(job, ct).GetAwaiter().GetResult().ValueOrDefault;  // was: GetJobLevel(default, ct)
```

The panel uses the same `GetJobLevel(currentJob, ct)` form. Fix both the summary and write the panel correctly from the start.

- **Note:** `DalamudGameStateProvider.GetJobLevel` reads `LocalPlayer.Level` regardless of the `job` argument (level is the current job's level), so the bug is currently latent — but the call is wrong on its face and must be corrected to avoid propagating the pattern.

### Decision 5 — Job name resolution is Dalamud-side; the resolved name enters the formatter as a plain string

Resolving `JobId.Value` → "Dark Knight" requires the Lumina `ClassJob` sheet via `IDataManager` — Dalamud, untestable. This stays in the panel shell:

```csharp
// shell, untested:
var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
string? jobName = job.Value == 0 ? null
    : sheet?.GetRow(job.Value).Name.ToString() is { Length: > 0 } n ? n : null;
```

The resolved `string?` is passed into the formatter. The formatter never touches Lumina; it formats whatever name string (or null) it is given. Zero / unresolved job id → `null` name → formatter renders the id-only fallback (Decision: see GWT `Job` cases). This keeps the only Dalamud dependency (sheet lookup) in the untested shell and the formatting decision (how to render a missing name) in the tested pure code.

---

## Task 1 — `PlayerStateFormatter` (pure, the unit-tested surface)

**Location:** `QuestForge.Plugin.Tracing/Authoring/PlayerStateFormatter.cs`
**Namespace:** `QuestForge.Plugin.Tracing.Authoring`
**Type:** `public static class PlayerStateFormatter`

It produces the individual display strings shown in AUTHORING.md §4.1. Each is a separate static method so tests pin each line independently and the shell calls them line-by-line for ImGui `TextUnformatted`.

```csharp
using QuestForge.Adapters.State;   // MountState, InstanceKind
using QuestForge.Adapters.Types;   // JobId, WorldPosition

namespace QuestForge.Plugin.Tracing.Authoring;

public static class PlayerStateFormatter
{
    // "Zone: 132"
    public static string FormatZone(uint zoneId);

    // "Position: (157.94, -19.48, 53.02)"  — 2dp, invariant culture
    public static string FormatPosition(WorldPosition pos);

    // Clipboard JSON: {"x": 157.94, "y": -19.48, "z": 53.02}  (moved from the panel; pure, testable)
    public static string FormatPositionJson(WorldPosition pos);

    // jobName non-null/non-empty:  "Job: Dark Knight (32), Lv 30"
    // jobName null/empty, id != 0: "Job: (32), Lv 30"
    // id == 0 (no job loaded):     "Job: (none)"
    public static string FormatJob(JobId job, string? jobName, int level);

    // "Mount: Dismounted" | "Mount: Mounted" | "Mount: Flying"  (enum name verbatim)
    public static string FormatMount(MountState mount);

    // "Combat: Yes" | "Combat: No"
    public static string FormatCombat(bool inCombat);

    // hpMax > 0:  "HP: 11420 / 11420"
    // hpMax == 0: "HP: (unknown)"   ← used when HP source is unavailable (Decision 3 H2 deferral)
    public static string FormatHp(int hpCurrent, int hpMax);

    // "Instance: None" | "Instance: Dungeon" | ...  (enum name verbatim)
    public static string FormatInstance(InstanceKind kind);
}
```

**Formatting rules (pin in tests):**
- Position uses `F2` and `CultureInfo.InvariantCulture` (so a comma-decimal locale cannot break the output or the JSON).
- Mount/Instance use `enum.ToString()` verbatim — the enum names already read as the spec labels (`Dismounted`, `None`, etc.). No mapping table; if the spec ever wants different casing, add it then.
- `FormatJob` always shows `(id)`; the *name* is the optional part. This makes the id-only fallback unambiguous and keeps the id visible per the user's chosen `"Name (id)"` format.
- `FormatHp` treats `hpMax == 0` as "unknown" rather than printing `0 / 0`, so the deferred-HP path (Decision 3) renders gracefully.

---

## Task 2 — Panel shell wiring (untested)

**File:** `QuestForge.Plugin/UI/Authoring/PlayerStatePanel.cs`

1. Extend the ctor (Decision 2): `PlayerStatePanel(AuthoringHost host, IGameStateProvider gameState, IDataManager dataManager)`. If H1 (Decision 3) is approved, also pass whatever exposes `LocalPlayer` (reuse the existing `ObjectTable` from services, or accept `IObjectTable`).
2. In `Draw()`:
   - Keep `Mode:` line, `Separator`, and `Captured at:` from the existing panel.
   - Read the six live values with `.GetAwaiter().GetResult().ValueOrDefault` (zone, position, job, level via `GetJobLevel(job, ct)`, combat, mount, instance kind). Use `GetPlayerState` once if preferred (it returns all of these except HP in a single `PlayerStateSnapshot`) — fewer calls, same data. Either is acceptable; `GetPlayerState` is cleaner (one read).
   - Resolve job name via Lumina (Decision 5).
   - Read HP per Decision 3 (H1: `LocalPlayer.CurrentHp`/`MaxHp`; if H2 deferred: pass `0, 0`).
   - Render each line via `ImGui.TextUnformatted(PlayerStateFormatter.Format*(...))`.
   - Keep the "Copy Position" button calling `PlayerStateFormatter.FormatPositionJson(pos)` (now sourced from the formatter, not a private method).
3. Update `Plugin.cs:105` construction to pass the new ctor args (`_engineHost.DebugGameState`, the `IDataManager`).

**EngineHost.cs:151** — apply the `GetJobLevel(job, ct)` fix (Decision 4).

No tests for this task; it is ImGui + live Dalamud shell.

---

## Task 3 — Given-When-Then specifications (the tested surface)

All target `PlayerStateFormatter` in `QuestForge.Plugin.Tracing.Authoring`, tested from `QuestForge.Plugin.Tests/Authoring/PlayerStateFormatterTests.cs`.

### Job (`FormatJob`)
- **Happy:** `JobId(32)`, name `"Dark Knight"`, level `30` → `"Job: Dark Knight (32), Lv 30"`.
- **Edge — name resolved empty string:** `JobId(32)`, name `""`, level `30` → `"Job: (32), Lv 30"` (empty treated as unresolved).
- **Edge — name null:** `JobId(32)`, name `null`, level `30` → `"Job: (32), Lv 30"`.
- **Edge — no job loaded:** `JobId(0)`, name `null`, level `0` → `"Job: (none)"`.

### Position (`FormatPosition` / `FormatPositionJson`)
- **Happy:** `(157.94, -19.48, 53.02)` → `"Position: (157.94, -19.48, 53.02)"`.
- **Happy JSON:** same pos → `{"x": 157.94, "y": -19.48, "z": 53.02}`.
- **Edge — rounding:** `(0.005f, 0f, 0f)` rounds to 2dp deterministically (pin the exact `F2` output).
- **Edge — invariant culture:** the test asserts a `.` decimal separator (guards against locale-dependent formatting).

### Zone (`FormatZone`)
- **Happy:** `132` → `"Zone: 132"`.
- **Edge — zero:** `0` → `"Zone: 0"` (no special-casing; zone 0 is "not loaded" but we show the raw value, matching the existing panel).

### Mount (`FormatMount`)
- **Theory:** `Dismounted` → `"Mount: Dismounted"`; `Mounted` → `"Mount: Mounted"`; `Flying` → `"Mount: Flying"`.

### Combat (`FormatCombat`)
- `true` → `"Combat: Yes"`; `false` → `"Combat: No"`.

### HP (`FormatHp`)
- **Happy:** `11420, 11420` → `"HP: 11420 / 11420"`.
- **Happy — partial:** `8000, 11420` → `"HP: 8000 / 11420"`.
- **Edge — unknown (deferred-HP / pre-login):** `0, 0` → `"HP: (unknown)"`.
- **Edge — zero current, alive max:** `0, 11420` → `"HP: 0 / 11420"` (dead but loaded; max>0 means known).

### Instance (`FormatInstance`)
- **Theory across the enum:** `None` → `"Instance: None"`; `Dungeon` → `"Instance: Dungeon"`; `SinglePlayerDuty` → `"Instance: SinglePlayerDuty"`; `Trial`, `Raid`, etc. verbatim.

### Counts
- Happy paths: ~8
- Edge cases: ~9
- Error/degenerate cases: ~3 (no-job, HP-unknown, zone-zero)

---

## Implementation order

**Phase A — formatter (TDD)**
1. Add `PlayerStateFormatter` skeleton (methods throwing `NotImplementedException`) to `QuestForge.Plugin.Tracing/Authoring/`.
2. Write `PlayerStateFormatterTests` from Task 3 (RED).
3. Implement each method until green. Done before Phase B.

**Phase B — shell wiring (no tests)**
1. Resolve the HP gate (Decision 3) — H1 or H2-deferred — per the user's answer.
2. Extend `PlayerStatePanel` ctor + `Draw()`; move position-JSON to the formatter.
3. Update `Plugin.cs` construction.
4. Fix `EngineHost.GetGameStateSummary()` `GetJobLevel(job, ct)`.

---

## Done criteria

1. `PlayerStateFormatter` exists in `QuestForge.Plugin.Tracing.Authoring` and all Task 3 GWT tests pass in `QuestForge.Plugin.Tests` (`dotnet test QuestForge.Plugin.Tests`).
2. The authoring Player State panel renders Job/Level, Mount, Combat, HP, and Instance lines below Zone/Position, matching AUTHORING.md §4.1 layout.
3. `EngineHost.GetGameStateSummary()` calls `GetJobLevel(currentJob, ct)` (not `default`).
4. "Copy Position" produces the same JSON as before, now via `PlayerStateFormatter.FormatPositionJson`.
5. Solution builds; `QuestForge.Plugin.Tests` requires no Dalamud feed (formatter stays Dalamud-free).

---

## What this change does NOT include

- No `IGameStateProvider` / `PlayerStateSnapshot` change (HP via H2 is explicitly deferred unless the user chooses it).
- No engine, `GameStateSnapshot`, `SnapshotAggregator`, trace, or fixture changes.
- No throttle/cache layer on the live read (per-frame read accepted).
- No new tests for the ImGui shell (untested by policy).
- No mount/instance label remapping (enum names used verbatim).
