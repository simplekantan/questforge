# Mount Support — Architecture Plan

**Status:** ARCHITECT spec. No code yet. Branch `feat/mount-support` off main `be5ddd6`.
**Scope:** Mount Roulette on Navigate, lazy dismount before non-Navigate actions.
**Cap:** ~500 lines.
**Audience:** Tester (lifts failing tests from §5) → Implementer (lifts production wiring from §3/§6).

---

## §1 Goals and non-goals

### Goals (v1)
- Engine fires **Mount Roulette** (`ActionManager.UseAction(GeneralAction, 9)`) when a `Navigate` action is dispatched and all preconditions hold: dismounted, not in combat, distance ≥ 20m to destination.
- Engine fires **Dismount** (`ActionManager.UseAction(GeneralAction, 23)`) lazily before any non-`Navigate` action when the player is currently `Mounted` or `Flying`.
- Both calls are silent best-effort: mount failures (indoor zone, no mount unlocked, casting, in cutscene) do not block navigation — the player proceeds on foot.
- vnavmesh's existing `fly: true` argument continues to drive flight pathing. We do not add a separate `Fly` engine action.
- `/qf debug mount` and `/qf debug dismount` for in-game smoke verification.

### Non-goals (v1)
- No per-mount selection. Mount Roulette gives the player whatever their roulette is configured for (in-game UI).
- No fall-damage protection on dismount-while-flying. If the user dismounts the player at altitude, the player falls.
- No mount-while-in-flight transition handling — we treat `Mounted` and `Flying` as "mounted enough" for skip-mount decisions.
- No schema field. (See §3 Q3 — opt-out deferred.)
- No async wait for the mount animation to complete inside one dispatch. The engine fires Mount and returns; the next tick re-reads `MountState` and decides again.
- No engine-level mount throttle that survives across step transitions. Per-tick state reads are the sole arbitration.

---

## §2 User-pinned decisions (verbatim)

These were resolved via AskUserQuestion before this spec was drafted. They are **not subject to architect-debate**; they constrain everything below.

1. **Mount mechanism**: Mount Roulette via `ActionManager.UseAction(GeneralAction, 9)`. Dismount via `(GeneralAction, 23)`.
2. **Mount policy**: Always on `Navigate` when `NavigationOptions.UseMount && !InCombat && distance >= 20m && MountState == Dismounted`. Failures (no mount unlocked, indoor zone, etc.) are silent — navigate on foot.
3. **Dismount policy**: Lazy-dismount before any non-`Navigate` action when mounted (mirrors the close-shop pattern from PR #94).
4. **Flight**: vnavmesh already accepts `fly: true` to `PathfindAndMoveCloseTo` (it handles its own takeoff). Mount Roulette gives the player whatever mount they configured. No separate `Fly` action call needed for v1.

---

## §3 Architectural decisions

### Q1 — Adapter placement and shape

**Options considered:**
- (a) New `IMount` in `QuestForge.Adapters/Movement/IMount.cs` alongside `INavigator` / `ITeleporter`.
- (b) Extend `INavigator` with `Mount()` / `Dismount()` methods.
- (c) Extend `ICombat.UseAction(uint actionId, …)` — Mount Roulette is technically a `GeneralAction` UseAction call.

**Rejected (b)** — `INavigator` is "in-zone pathing"; mounting is a player-state mutation orthogonal to pathfinding. Coupling them blocks future "mount but don't navigate" use cases (e.g. mount for FATE travel before combat) and bloats the navigator fake.

**Rejected (c)** — `WrathComboAdapter.UseAction` returns `UseActionOutcome.Failed` as a placeholder (`QuestForge.Adapters.Dalamud\Combat\WrathComboAdapter.cs:88-89`). The `ICombat.UseAction` surface is reserved for combat actions targeting an NPC; reusing it for `GeneralAction` would conflate two ActionManager kinds and complicate the test surface.

**Shape — `Result<Unit>` vs `Task`:**
- `INavigator.Stop` returns `Task<Result<Unit>>` (`QuestForge.Adapters/Movement/INavigator.cs:12`).
- `IVendor.Close` returns `Task` (`QuestForge.Adapters/Interaction/IVendor.cs:21`) — the PR #94 precedent for "fire-and-forget lazy hook".
- Mount/dismount fits the `IVendor.Close` shape: silent best-effort, success is verified by the next-tick `MountState` read, not by a return value. A `Result<Unit>` adds noise — the engine has no meaningful handling for `Result.Fail("noMountUnlocked")` because the state-read tells us the same thing one tick later, with no risk of stale information.

**Pinned:** New `IMount` interface in `QuestForge.Adapters/Movement/IMount.cs`. Methods: `Task Mount(CancellationToken ct)` and `Task Dismount(CancellationToken ct)`. Both return non-generic `Task` (mirror of `IVendor.Close`). The engine and host both observe outcomes via `IGameStateProvider.GetMountState` on the next tick. Concrete: `DalamudMount` in `QuestForge.Adapters.Dalamud/Movement/DalamudMount.cs`; fake: `FakeMount` in `QuestForge.Adapters.Fakes/Movement/FakeMount.cs` (mirrors `FakeVendor` with `MountCallCount` and `DismountCallCount` counters).

---

### Q2 — Failure handling and animation gap

**Failure modes (silent, no error path):**
- Indoor zone → game rejects `UseAction(9)`. No effect; next-tick `MountState == Dismounted`; engine navigates on foot.
- No mount unlocked → same as indoor.
- Mid-cast / cutscene → `UseAction` is rejected by the game. Next tick will re-evaluate; if still casting, mount won't fire (see throttle below).
- Mounting succeeded but animation in progress (~2s) → `Condition[ConditionFlag.Mounted]` flips when the animation completes. During the gap the player is in a "mounting" intermediate state; `MountState` stays `Dismounted` until the flag flips.

**Animation-gap question — re-fire vs throttle:**
- (a) **Re-fire each tick**: simplest, idempotent at the game layer (`UseAction` while mounting is a no-op), trivially testable. Risk: spams `UseAction` calls.
- (b) **Time-based throttle** like the aethernet `AethernetCooldown = 15s` pattern (`QuestForge.Plugin/EngineHost.cs:84`). Adds state.
- (c) **State-based throttle**: refire-on-state-change only — track "last mount attempt" tick and don't refire until state moves.

**Rejected (b)** for v1 — a 15s cooldown is too long if mount actually fails (engine waits 15s on foot before attempting again). 2s is too short to be useful as a debounce.
**Rejected (c)** — adds state to `EngineHost` for a problem that may not materialize. The aethernet throttle exists because Lifestream is genuinely re-entrant-unsafe (opens the menu twice). `UseAction(GeneralAction, 9)` is idempotent.

**Pinned (a) with one safeguard:** Re-fire Mount each `Navigate` tick where the preconditions hold. Mid-mount is fine because the game ignores duplicate calls. The single safeguard: if `GetPlayerState` returns `Casting: true`, skip the mount attempt this tick (a casting player includes the mount-animation cast — this naturally suppresses the re-fire during the 2s gap). The casting check is also a cheap defense against firing during pre-existing cast actions (e.g. teleport channel).

---

### Q3 — Per-step opt-out (`noMount`)

**Arguments for adding `noMount: true` schema field:**
- Indoor zones (Mor Dhona cathedral, Vesper Bay quayside) have known mount-prohibited subareas. An author who has tested a step can flag it explicitly.
- Cheap to add (one optional `bool?` on `BaseStep`).
- Self-documenting for future authors reading the quest file.

**Arguments against:**
- Q1.2 user decision is "always on Navigate" — adding an opt-out re-opens that debate.
- The cost of attempting mount in an indoor zone is **zero**: `UseAction` is rejected silently, `MountState` stays `Dismounted`, navigation proceeds on foot. No retry storm (Q2 safeguard suppresses re-fire during cast; non-cast indoor zones simply fail-and-proceed each tick).
- Adding the field triggers a schema-cascade: questforge-tools validator, JSON Schema regen, sample quests, fragment composition rules. The cost is several files for a feature with no observable benefit in the failure case.
- Authors can already gate Navigate by setting `UseMount = false` on `NavigationOptions` if a quest sequence requires it — but the schema currently does not expose `UseMount` per-step either, which is a separate (also deferred) issue.

**Pinned:** **No schema change in v1.** If indoor failures turn out to be noisy in the trace (e.g. emit a stream of failed `UseAction` observations that pollute fixtures), we add `noMount` in a follow-up. Until then, the silent-fail model is the correct default. **(Answer to user's hand-back question: NO for v1.)**

---

### Q4 — Fixture-cascade risk

**The risk:** adding new `IGameStateProvider` reads to engine-relevant code paths can starve existing replay fixtures (per the `MEMORY.md` entry on trace-emission refactor).

**Walk:**
- The mount-decision logic lives in `EngineHost.DispatchAction` (not in `QuestEngine.Tick`). The new `GetPlayerState` read at the top of the `Navigate` switch arm is on the **host**, not the engine.
- `Quest66130ReplayTests` constructs `QuestEngine` directly and calls `engine.Tick(ct)` (`QuestForge.Engine.Tests/Replay/Quest66130ReplayTests.cs:128, 247`). It does **not** run `EngineHost` or `EngineTestHarness.RunToCompletion`. So mount-decision reads do not enter the engine's observation stream and do not appear in any replay fixture.
- `EngineTestHarness.RunToCompletion` (`QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs:118-200`) **does** mirror EngineHost's pre-switch hooks (the close-shop hook is at line 131). Adding a `Navigate`-arm mount hook here will issue `GetPlayerState` calls against the recording proxy, which **will** emit observations to `TraceWriter`. However, no current fixture asserts on the absence of `GetPlayerState` observations during Navigate — fixture replay matches on action transitions, not observation counts.
- `CloseShopAfterPurchaseTests` exercises `EngineTestHarness.RunToCompletion`. The new Navigate-arm `GetPlayerState` read will fire during that test's Navigate phase, but the assertions there look at `Vendor.CloseCallCount`, not at GameState read counts. Safe.

**Pinned:** Fixture cascade risk is **zero** for existing replay fixtures. The mount logic gates on a host-level read that does not propagate into the recording trace of any current `QuestEngine`-driven test. **No mitigation needed.** Document this in the implementer-facing comment on the new hook so a future contributor doesn't try to "move the mount logic into the engine for testability" without re-reading this section.

---

### Q5 — Distance threshold

**Options:**
- (a) `const float MountDistanceThreshold = 20f` on `EngineHost`.
- (b) New property on `NavigationOptions { float MountThresholdMeters = 20f }`.
- (c) New engine-level constant in `QuestForge.Engine.Defaults`.

**Rejected (b)** — `NavigationOptions` is a runtime hint to the navigator; it has no current public schema surface, and adding it implies authors-can-tune-this when (a) authors don't have a good intuition for the right value and (b) we don't actually want them tuning it.
**Rejected (c)** — there is no `QuestForge.Engine.Defaults` class. Introducing one for a single constant is over-architecture.

**Pinned (a):** Define `private const float MountDistanceThresholdMeters = 20f;` on `EngineHost` (and mirror the same constant on `EngineTestHarness` next to its lazy-close hook). If a future contributor wants to make it tunable, the call-site grep is one constant in two files — easy. Distance is computed Euclidean-3D from `PlayerStateSnapshot.Position` to `EngineAction.Navigate.Destination` (existing types).

---

### Q6 — Mount-while-Flying and other edge states

**Cases:**
- **`MountState == Flying` + non-Navigate action arrives**: we must dismount. The in-game `Dismount` action drops a flying player to the ground (gravity takes over — possible fall damage). v1 does not protect against this; an author who needs a safe dismount altitude must structure the quest so the non-Navigate step starts at ground-level coordinates. Note in the implementer comment.
- **`MountState == Mounted` + Combat action**: the game auto-dismounts on aggro. The lazy-dismount hook fires for the `Engage` switch arm too (since `Engage` is not `Navigate`); the extra `UseAction(23)` is harmless — a dismounted call on an already-dismounted player is a no-op.
- **`RidingPillion`**: `DalamudGameStateProvider` reports this as `MountState.Mounted` (`QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs:32, 64`). The Dismount action on the rider drops them off the host's mount, which is the correct behavior — they need to be on their own feet to interact with quest NPCs. v1 treats pillion identically to mounted.

**Pinned:** Lazy-dismount fires whenever `MountState ∈ {Mounted, Flying}` and the next action is not `Navigate`. No altitude check, no pillion special-case. The decision predicate is **one expression**: `currentMountState != MountState.Dismounted && action is not EngineAction.Navigate`. Document the flying-dismount fall-damage caveat in the implementer-facing comment.

---

### Q7 — Test surface

**Where tests land:** `QuestForge.Engine.Tests/Engine/MountSupportTests.cs` — direct mirror of `CloseShopAfterPurchaseTests.cs` (`QuestForge.Engine.Tests/Engine/CloseShopAfterPurchaseTests.cs:39`). Same fixture-helper pattern. Same `EngineTestHarness` integration plus direct hook-conditional probes.

**Why this file location:** the mount/dismount conditional is a pre-switch hook in `EngineHost.DispatchAction` and its mirror in `EngineTestHarness.RunToCompletion`. `EngineHost` cannot be unit-tested without Dalamud, so all coverage runs against the harness (integration) plus direct hook-conditional checks (unit).

**`FakeGameStateProvider` setter audit:**
- `SetInCombat(bool v)` — **already exists** (`FakeGameStateProvider.cs:61`).
- `SetMountState(MountState v)` — **already exists** (`FakeGameStateProvider.cs:64`).
- `SetPosition(WorldPosition position)` — **already exists** (`FakeGameStateProvider.cs:55`).
- `GetPlayerState` snapshot already includes Position, InCombat, MountState as a single read (`FakeGameStateProvider.cs:133-144`). One read covers the entire mount-decision predicate. No new scaffolding needed.

**`FakeMount` surface (new):**
```csharp
public sealed class FakeMount : IMount
{
    public int MountCallCount { get; private set; }
    public int DismountCallCount { get; private set; }
    public Task Mount(CancellationToken ct = default)    { MountCallCount++;    return Task.CompletedTask; }
    public Task Dismount(CancellationToken ct = default) { DismountCallCount++; return Task.CompletedTask; }
    public void Reset() { MountCallCount = 0; DismountCallCount = 0; }
}
```
Wire into `EngineTestHarness` as a public property `FakeMount Mount { get; }` alongside `FakeVendor Vendor { get; }`. Pass to the engine constructor and to the new `RunToCompletion` pre-switch hooks.

**Matrix M1–M7 — see §5 for full GWT.**

**Pinned:** All seven matrix cases plus three counter sanity tests = 10 test methods in `MountSupportTests.cs`. Plus the `FakeMount` adds zero tests of its own — it's exercised entirely through the harness, same as `FakeVendor.CloseCallCount` is.

---

### Q8 — Debug commands

**Pattern:** `/qf debug close-shop` (`QuestForge.Plugin/Commands/QfCommand.cs:1057-1075`) is a one-line handler that calls into `_host.DebugVendor.Close(CancellationToken.None)`. Two new symmetric handlers.

**Pinned:** Add `/qf debug mount` and `/qf debug dismount`. Each is ~10 lines mirroring `HandleDebugCloseShop`. Expose `DebugMount` on `EngineHost` (parallel to `DebugVendor`). Update the `PrintUsage` string. The handlers exist for in-game probing of the FFXIVClientStructs `ActionManager.UseAction` shape before the engine-level wiring is exercised end-to-end.

---

## §4 Schema impact

**None.** §3 Q3 deferred per-step `noMount` indefinitely. The quest file format is unchanged. The validator (`questforge-tools`) gets no new rules. No JSON Schema regen needed. No existing quest files need migration.

---

## §5 Test plan

All tests live in `QuestForge.Engine.Tests/Engine/MountSupportTests.cs`. Run with:
```
& "C:\Users\publi\.dotnet\dotnet.exe" test QuestForge.Engine.Tests --filter "FullyQualifiedName~MountSupport"
```

Each test uses `EngineTestHarness` (mirrors `CloseShopAfterPurchaseTests` setup) plus a new `FakeMount` field on the harness. Quest fixture is a one-sequence quest with a single `TravelStep` (destination 40m away) followed by a `TalkStep` at the destination, so the engine emits `Navigate → Interact → Done`.

### Matrix M1 — happy path: mount fires
- **Given** harness with `MountState = Dismounted`, `InCombat = false`, player at (0,0,0), Navigate target at (40,0,0) (distance ≈ 40m, ≥ 20m threshold).
- **When** the harness ticks and dispatches `Navigate`.
- **Then** `harness.Mount.MountCallCount == 1` after that tick. `Navigator.NavigateTo` was also called.

### Matrix M1b — UseMount=false opt-out: no mount
- **Given** as M1 (distance ≥ 20m, dismounted, not in combat) but the `TravelStep`'s `NavigationOptions.UseMount = false`.
- **When** the harness ticks and dispatches `Navigate`.
- **Then** `harness.Mount.MountCallCount == 0`. `Navigator.NavigateTo` was called.
- **Why this matters:** the existing `NavigationOptions.UseMount` field becomes a per-step opt-out (resolved O3, 2026-05-28). Authors can pin `UseMount: false` on any step where the engine must not attempt to mount (e.g. a navigate inside a known mount-prohibited subarea where the silent-fail still produces noisy trace observations).

### Matrix M2 — distance below threshold: no mount
- **Given** as M1 but Navigate target at (10,0,0) (distance ≈ 10m, < 20m).
- **When** the harness ticks and dispatches `Navigate`.
- **Then** `harness.Mount.MountCallCount == 0`. `Navigator.NavigateTo` was called.

### Matrix M3 — in combat: no mount
- **Given** as M1 but `harness.GameState.SetInCombat(true)`.
- **When** the harness ticks and dispatches `Navigate`.
- **Then** `harness.Mount.MountCallCount == 0`. `Navigator.NavigateTo` was called.

### Matrix M4 — already mounted: no remount
- **Given** as M1 but `harness.GameState.SetMountState(MountState.Mounted)`.
- **When** the harness ticks and dispatches `Navigate`.
- **Then** `harness.Mount.MountCallCount == 0`. `harness.Mount.DismountCallCount == 0` (Navigate does not dismount; dismount is only for non-Navigate next actions). `Navigator.NavigateTo` was called.

### Matrix M5 — non-Navigate after Navigate while Mounted: dismount fires
- **Given** harness with `MountState = Mounted` (player arrived at destination still mounted). Quest layout: `Navigate` step has completed; next action is `Interact`.
- **When** the harness ticks and dispatches the `Interact` action (engine satisfied with arrival, transitioning to interact).
- **Then** before `Interactor.InteractWith` is called: `harness.Mount.DismountCallCount == 1`. Order assertion (use call-time records on `FakeMount` if needed, but the simpler check is: after the tick, `DismountCallCount == 1` AND the interact happened).

### Matrix M6 — non-Navigate after Navigate while Dismounted: no dismount
- **Given** harness with `MountState = Dismounted` throughout (e.g. M2 scenario where mount never fired). Quest transitions Navigate → Interact normally.
- **When** the harness ticks through Navigate and then Interact.
- **Then** `harness.Mount.DismountCallCount == 0`.

### Matrix M7 — Navigate after Navigate while Mounted: no intermediate dismount
- **Given** quest with two consecutive `TravelStep`s (e.g. Navigate to (40,0,0) then Navigate to (80,0,0)). `MountState = Mounted` after first arrival.
- **When** the harness ticks: Navigate-1 dispatched → arrival → Navigate-2 dispatched.
- **Then** `harness.Mount.DismountCallCount == 0` across the entire run. Both `Navigate` dispatches occurred.

### Sanity S1 — `FakeMount.Reset()` clears counters
- **Given** a `FakeMount` with `Mount` called once and `Dismount` called twice.
- **When** `Reset()` is invoked.
- **Then** both counters are zero.

### Sanity S2 — `Mount`/`Dismount` are idempotent counters
- **Given** a `FakeMount`.
- **When** `Mount` is called 3 times back-to-back.
- **Then** `MountCallCount == 3` (counter increments, no throttle inside the fake).

### Sanity S3 — Cancellation propagates
- **Given** a cancelled `CancellationToken`.
- **When** `FakeMount.Mount(cts.Token)` is called.
- **Then** `OperationCanceledException` is thrown (mirrors `FakeVendor.Close` cancellation contract at `FakeVendor.cs:71`).

### Hook-direct probe H1 — mount predicate evaluated on cast-true does not fire
- **Given** harness with `MountState = Dismounted`, `InCombat = false`, distance ≥ 20m, but `harness.GameState.SetCasting(true)` (simulates mid-cast or mid-mount-animation).
- **When** the Navigate dispatch hook is exercised.
- **Then** `harness.Mount.MountCallCount == 0`. This proves the Q2 safeguard.

**Coverage summary:** 7 matrix + 3 fake sanity + 1 cast-safeguard probe = **11 test methods**. Expected file size: ~250 lines (mirrors `CloseShopAfterPurchaseTests.cs` at 180 lines plus extra matrix coverage).

---

## §6 Sub-slicing

**Single PR.** Total touched files (10 production + 1 test):

**Production — adapters layer:**
1. `QuestForge.Adapters/Movement/IMount.cs` (new, ~10 lines)
2. `QuestForge.Adapters.Fakes/Movement/FakeMount.cs` (new, ~25 lines)
3. `QuestForge.Adapters.Dalamud/Movement/DalamudMount.cs` (new, ~30 lines — `ActionManager.UseAction((uint)ActionType.GeneralAction, 9)` and `(…, 23)`)

**Production — engine wiring:**
4. `QuestForge.Plugin/EngineHost.cs` — add `_mount` field, ctor wiring, pre-switch lazy-dismount hook (mirror of close-shop hook at `EngineHost.cs:255-259`), inside-Navigate-arm mount predicate, `MountDistanceThresholdMeters` const, `DebugMount` property. ~40 lines added.
5. `QuestForge.Engine.Tests/Helpers/EngineTestHarness.cs` — add `FakeMount Mount` property, pass to harness wiring, mirror the host's two new hooks in `RunToCompletion`. ~25 lines added.

**Production — debug commands:**
6. `QuestForge.Plugin/Commands/QfCommand.cs` — `HandleDebugMount` and `HandleDebugDismount` (mirror of `HandleDebugCloseShop` at line 1057). Wire into the switch at line ~193. Update `PrintUsage` at line 1078. ~25 lines added.

**Tests:**
7. `QuestForge.Engine.Tests/Engine/MountSupportTests.cs` (new, ~250 lines per §5).

**Ordering for the implementer (after tests are red):**
1. Write `IMount` and `FakeMount` first (files 1, 2). Tests should compile but fail on missing harness wiring.
2. Wire `FakeMount` into `EngineTestHarness` (file 5). Tests should compile and fail on the assertion (counters stay zero).
3. Wire the mount predicate and lazy-dismount hook in `RunToCompletion`. Most matrix tests should pass.
4. Mirror to `EngineHost` (file 4). No tests exercise this directly, but the production code now has parity.
5. `DalamudMount` (file 3). No CI tests; verified by `/qf debug mount` in-game.
6. Debug commands (file 6). Smoke-only.

**Why a single PR:** the engine wiring is meaningless without the adapter interface, the harness mirror is required for tests to pass, and the debug commands are how the implementer validates the Dalamud adapter actually fires the right `UseAction` in-game. Splitting would leave intermediate commits in a state where either the tests are dark or the in-game probe doesn't exist.

---

## §7 Risks and open questions

### Resolved-but-flagged
- **R1 (Q3 deferral)**: If indoor zones produce noisy mount-failure observations in the trace, we'll need `noMount` after all. Mitigation: monitor the first quest that crosses an indoor zone after this lands; if the trace shows >5 failed mount attempts per indoor traversal, file a follow-up to add the schema field.
- **R2 (flying dismount)**: A player mid-flight who dismounts will fall. If a quest has a Navigate-to-Talk transition where the navigate ends in the air (rare — vnavmesh usually grounds at the stop-distance), the dismount fires and the player takes fall damage. No current quest hits this, but as the corpus grows, watch for it.
- **R3 (pillion edge)**: A party member dismounting their pillion rider via our hook may surprise the host player. The party-quest case is not in the v1 corpus.

### Genuinely open
- **O1 — `ActionManager.UseAction` return value**: the Dalamud call returns `bool` (true = accepted, false = rejected). We discard it in v1 (silent). Future enhancement: if the call returns false AND we're not casting AND we're not in combat, log a single-line warning so the user knows their indoor zone or missing-mount is the cause. Not in v1 scope, but worth a `TODO` comment in `DalamudMount`.
- **O2 — Animation-completion observable**: there is no "mount animation done" condition flag distinct from `ConditionFlag.Mounted`. The 2s gap is naturally absorbed by the per-tick re-fire model (Q2). Confirmed no surprise.
- **O3 — `NavigationOptions.UseMount = false` (RESOLVED 2026-05-28)**: User agreed to respect `UseMount=false` as a per-step opt-out. The mount predicate now reads `n.Options.UseMount && !InCombat && distance >= 20m && MountState == Dismounted && !Casting`. Test M1b added to §5 to lock this in.

### Did not find a problem with
- The `EngineTestHarness.RunToCompletion` mirror is a known accepted divergence pattern (already mirrors close-shop). No new debt incurred.
- `DalamudGameStateProvider.GetPlayerState` already does the composite read in one call, so the host's new mount-decision read does not add a second IPC round-trip.

---

## End of plan
