# TeleportStep Implementation Plan

**Status:** ready for test creation
**Input docs:** docs/SCHEMA.md (step taxonomy), docs/ADAPTERS.md §ITeleporter, docs/PHASE_11B_PLAN.md §AttunementStep (closest pattern reference)
**Output (CI behavior):** Adding a `{ "type": "teleport", "aetheryteId": N }` step to a quest dispatches a new `EngineAction.Teleport` that the host translates to `ITeleporter.TeleportToAetheryte`. Engine unit tests (xUnit, `QuestForge.Engine.Tests`) cover all dispatch arms against `FakeTeleporter`. CI red → CI green when the dispatch arm is wired up.

---

## Dependency graph

```
QuestForge.Schema
   └── TeleportStep added; registered in QuestForgeJsonContext
        └── consumed by ↓
QuestForge.Engine
   └── new EngineAction.Teleport; dispatch arm in ResolveActionForStep; AetheryteZoneMap (private)
        └── consumed by ↓
QuestForge.Engine.Tests
   └── TeleportStepTests against FakeTeleporter (this plan’s test scenarios)
QuestForge.Plugin (out-of-scope this plan)
   └── EngineHost gains an EngineAction.Teleport arm calling ITeleporter.TeleportToAetheryte
```

**Build order:** Schema type → JSON context registration → EngineAction → engine dispatch arm → tests. The plugin dispatch arm (`EngineHost`) is mirrored later — the engine emits `EngineAction.Teleport` regardless of host.

---

## Architectural decisions (read before coding)

### Decision T1 — TeleportStep ships as a first-class step, not as a TravelStep variant

`TravelStep` already has `TravelDestination.AetheryteId` and a `null Position` arm that throws `NotSupportedException("Phase 4 does not support aetheryte-only travel steps")`. We could route teleport through that arm. We do **not**, for three reasons:

1. `TravelStep` is overloaded today (in-zone Navigate, NPC-mediated travel, Aethernet hop). Layering a teleport arm under it forces every TravelStep dispatch to read aetheryte data even when irrelevant.
2. The teleport postcondition is "player is in expected zone." Encoding that as a `TravelStep.Expect` requires the author to hand-write `playerZone() == N` every time — duplication across every teleport step.
3. Quest JSON readability: `{"type":"teleport","aetheryteId":70}` is obviously a teleport; `{"type":"travel","destination":{"zone":129,"aetheryteId":70}}` reads like an in-zone navigation with a hint.

**Concrete shape:**
```csharp
public class TeleportStep : Step
{
    /// <summary>
    /// Aetheryte to teleport to. The schema-side AetheryteId is a thin wrapper
    /// around uint; the engine converts to Adapters.Types.AetheryteId at dispatch.
    /// </summary>
    public AetheryteId AetheryteId { get; init; }
}
```

`Step.Expect` may still be authored explicitly (rarely useful: the engine synthesises a default `playerZone() == <expectedZone>` from `AetheryteZoneMap` if Expect is null — see T5).

### Decision T2 — New `EngineAction.Teleport`, not reuse of `UseAethernet`

`UseAethernet` is for **intra-region** shard hops; the IPC plumbing in `LifestreamTeleporter.TeleportToAethernet` is shard-keyed. Cross-zone teleport uses `TeleportToAetheryte` and is debounced/cost-checked differently in the host.

```csharp
public sealed record Teleport(AetheryteId Destination, Step? Origin = null) : EngineAction;
```

`Origin` parallels `Interact`/`Purchase`/`Wait` so the trace and dispatcher can identify the originating step.

### Decision T3 — `AetheryteZoneMap` is an engine-private static `IReadOnlyDictionary<uint,uint>`, seed-tested

The map (aetheryteId → expected zoneId) lives in `QuestForge.Engine.Travel.AetheryteZoneMap`. It is **not** authored into quest JSON. Rejected alternatives:

- *Schema-side `expectedZoneId` on TeleportStep:* duplicates a constant across every authored step. If we ever get the mapping wrong we have to re-edit every quest.
- *Lumina lookup at runtime:* engine cannot depend on Dalamud. Lumina-driven generation is acceptable as a build-time script that emits the engine-side dictionary, but the runtime lookup itself must be a plain `IReadOnlyDictionary`.

**Concrete shape:**
```csharp
internal static class AetheryteZoneMap
{
    // Seeded with the aetherytes needed by the existing test corpus + the
    // canonical first-tier set: Limsa Lower Decks (8 → 129), Gridania Old Quarter
    // (2 → 132), Ul'dah Steps of Nald (9 → 130), Mor Dhona (24 → 156), etc.
    // New entries land in this dictionary as quest data demands them.
    private static readonly IReadOnlyDictionary<uint, uint> _map =
        new Dictionary<uint, uint>
        {
            { 8u,  129u },  // Limsa Lominsa Lower Decks
            { 2u,  132u },  // New Gridania (Old Gridania → 132 per Lumina)
            { 9u,  130u },  // Ul'dah - Steps of Nald
            // … etc; the Builder seeds only the IDs the tests reference
        };

    public static bool TryGetZone(uint aetheryteId, out uint zoneId)
        => _map.TryGetValue(aetheryteId, out zoneId);

    public static IReadOnlyDictionary<uint, uint> All => _map;
}
```

**What breaks if violated:** if an author requests an aetheryte ID not in the map, the engine cannot verify the postcondition. Decision T4 covers the failure mode.

### Decision T4 — Unknown aetheryte ID → `AwaitUser`, never silent success

A `TeleportStep` with an `AetheryteId` not in `AetheryteZoneMap` is treated as an authoring error. The engine emits `EngineAction.AwaitUser("teleport target aetheryte 12345 not in zone map — author must extend AetheryteZoneMap")`. Rejected alternatives:

- *Throw `NotSupportedException`:* an exception thrown from a per-tick dispatch arm tears down the host loop. We use exceptions only for genuine engine bugs, not authoring errors.
- *Emit Teleport with no zone verification:* makes the postcondition unfalsifiable; the engine would loop forever if the teleport silently fails in-game.

This is also where `qf-validate` will eventually emit `structural/teleport-aetheryte-not-in-map` (Phase 1+ rule). The runtime guard is the safety net.

### Decision T5 — Engine synthesises a default `Expect` when none is authored

Mirroring the `PurchaseItemStep` `SynthesizePurchaseExpect` pattern (`QuestEngine.SynthesizePurchaseExpect`), the engine wraps a `TeleportStep` with no `Expect` at `StartQuest` expansion time:

```csharp
private static Step SynthesizeTeleportExpect(Step step)
{
    if (step is not TeleportStep ts || ts.Expect is not null) return step;
    if (!AetheryteZoneMap.TryGetZone(ts.AetheryteId.Value, out var zoneId))
        return step; // unknown — leave Expect null; dispatch arm emits AwaitUser
    var synthesized = new PredicateExpect
    {
        Predicate = $"playerZone() == {zoneId}"
    };
    return CloneStepWith(ts, ts.Id, synthesized);
}
```

This synthesis is what allows the regular Expect-first cursor walk to confirm the step after the player arrives. Without it the engine would loop emitting `Teleport` even after arrival.

### Decision T6 — Pre-flight guards live in the engine dispatch arm, not in the host

Three pre-flight guards run inside `ResolveActionForStep` for `TeleportStep`:

| Guard | Source of truth | Behavior on violation |
|---|---|---|
| Unknown aetheryte | `AetheryteZoneMap.TryGetZone` | `EngineAction.AwaitUser` (Decision T4) |
| Already in target zone | `playerZone == expectedZone` | Step is skipped by the per-step `Expect` walk *before* dispatch (synthesised expect, Decision T5) — never reaches the arm |
| In combat | `IGameStateProvider.IsPlayerInCombat` | `EngineAction.AwaitUser("cannot teleport while in combat")` |

The InCombat guard is **not** mirrored from the global defense rule. Global defense fires for *engagement*; the teleport step’s InCombat refusal is a *step-local* refusal. The two are independent — global defense may have already engaged on a prior tick; by the time the cursor walks to TeleportStep, we double-check.

Note: the InCombat read is **step-gated** (only fires when the cursor lands on a TeleportStep) so non-teleport ticks pay no `IsPlayerInCombat` cost beyond the global-defense read already in `ResolveDefenseOrNull`. The two reads in the same tick are harmless (FakeGameStateProvider records both; the recording proxy dedups identical observations).

### Decision T7 — Dismount is handled by the EngineHost mount/dismount pre-switch hook, mirrored in `HarnessEngine`

The existing pre-switch dismount logic in `EngineHost.DispatchAction` (and its mirror in `HarnessEngine.Tick`) dismounts after a `Navigate` action when the next action is non-Navigate. `Teleport` is non-Navigate, so when the previous tick’s action was `Navigate` and the current tick emits `Teleport`, the harness dismounts before returning the action — *no new code is needed in the engine*.

The Teleport step itself does **not** read `MountState`. Rejected alternative: emitting an explicit `EngineAction.Dismount` before Teleport. That would double-up with the existing lazy-dismount pathway and require new tracking state. The lazy-dismount pre-switch hook is the established pattern (Mounted → Aethernet works the same way today).

**Test implication:** the harness test for "mounted before teleport" asserts `FakeMount.DismountCallCount` increments **only after a prior Navigate**. A standalone TeleportStep that fires while the player is mounted (no prior Navigate this run) will **not** trigger dismount — the player must manually dismount in-game first, or the teleport must be authored after a Navigate. This is the same constraint that applies to Aethernet/UseReturn today.

### Decision T8 — Zone-verification failure is the engine’s problem, not the adapter’s

`ITeleporter.TeleportToAetheryte` returns `Result<TeleportOutcome>`. `TeleportOutcome.Arrived` means *Lifestream reports done*, not *Lifestream confirms the destination zone*. The synthesised `Expect = "playerZone() == N"` is the actual postcondition. If the adapter reports `Arrived` but the player ends up in zone `M ≠ N` (e.g., Lifestream completes onto a sub-zone or the game cancels the teleport), the engine simply does not see `Expect` go true and re-emits `Teleport` on the next tick. Counter logic (consecutive failure tracking) is out of scope for this plan — same posture as AttunementStep.

### Decision T9 — `FakeTeleporter` already exists; no new fake type required

`QuestForge.Adapters.Fakes.Movement.FakeTeleporter` already exposes:
- `RecordedTeleports` (CallLog of `TeleportCall(AetheryteId, At)`)
- `ScriptNextTeleportResult(TeleportOutcome)` for outcome injection
- `RegisterAetheryte(AetheryteId, ZoneId, WorldPosition)` for zone-on-arrival simulation
- `TeleportToAetheryte` which calls `_state.SetZone(info.Zone)` on `Arrived` if registered

This is sufficient. Tests use the existing surface. One small addition is needed for outcome failure injection (`Result.Fail`) — see Decision T10.

### Decision T10 — `FakeTeleporter` gains a `ScriptNextTeleportFailure(string, string?)` helper

Currently `FakeTeleporter.TeleportToAetheryte` always returns `Result.Ok(outcome)`. Test scenario T4 ("ITeleporter returns failure → step fails") needs the fake to be able to return `Result<TeleportOutcome>.Failure`. Add:

```csharp
// New scriptable state alongside _nextTeleportResult
private (string Reason, string? Detail)? _nextTeleportFailure;

public void ScriptNextTeleportFailure(string reason, string? detail = null)
    => _nextTeleportFailure = (reason, detail);
```

`TeleportToAetheryte` consumes `_nextTeleportFailure` first (if set) and returns `Result.Fail<TeleportOutcome>(...)` without invoking the registered-aetheryte arrival simulation. The recorded call is still appended (the call *happened*, it just failed).

### Decision T11 — `EngineTestHarness.RunToCompletion` gains a `Teleport` arm

Mirrors the existing `UseAethernet` arm:

```csharp
case EngineAction.Teleport tp:
    actions.Add(action);
    EmitActionSubmitted("Teleport", JsonSerializer.SerializeToElement(tp.Destination, _jsonOpts));
    var tpResult = await Teleporter.TeleportToAetheryte(tp.Destination, ct);
    EmitActionCompleted("Teleport", tpResult.IsSuccess ? tpResult.ValueOrThrow.ToString() : "Failed");
    break;
```

Tests that don’t use `RunToCompletion` (i.e., they `Tick()` directly) don’t need this — they call `Teleporter.TeleportToAetheryte` manually to simulate the dispatch effect, identical to the `UseAethernet` I1 pattern.

---

## Task breakdown

### T-1: `QuestForge.Schema` — add `TeleportStep`

1. Add to `Step.cs`:
   ```csharp
   public class TeleportStep : Step
   {
       public AetheryteId AetheryteId { get; init; }
   }
   ```
2. Add `[JsonDerivedType(typeof(TeleportStep), "teleport")]` to the `Step` polymorphic attribute list.
3. Register in `QuestForgeJsonContext`: `[JsonSerializable(typeof(TeleportStep))]`.
4. Write a round-trip serialization test (`QuestForge.Schema.Tests` if it exists; otherwise the test belongs in the engine tests project per existing convention).

### T-2: `QuestForge.Engine` — add `EngineAction.Teleport`

1. Add to `EngineAction.cs`:
   ```csharp
   public sealed record Teleport(AetheryteId Destination, Step? Origin = null) : EngineAction;
   ```
   `AetheryteId` here is `QuestForge.Adapters.Types.AetheryteId` (the engine-side ID type used by `ITeleporter`).

### T-3: `QuestForge.Engine.Travel.AetheryteZoneMap`

New file `QuestForge.Engine/Travel/AetheryteZoneMap.cs`. Internal static class with `TryGetZone(uint, out uint)` and `All` accessor. Seed with at least:
- `1000u → 130u` (the test-corpus aetheryte used in existing AttunementStepTests)
- `8u → 129u` (Limsa Lower Decks, the canonical "first" aetheryte)

Builder may add others as needed by quest data; the tests in this plan reference only `1000u` and `8u`.

### T-4: `QuestEngine` — synthesis + dispatch arm

1. Add `SynthesizeTeleportExpect` (mirrors `SynthesizePurchaseExpect`); call it from `ExpandSteps` alongside the purchase synthesis.
2. Add an async pre-arm in `ResolveAction` (mirrors the `PurchaseItemStep` arm) **before** the synchronous `ResolveActionForStep` dispatch:

   ```csharp
   if (step is TeleportStep teleportStep)
   {
       var teleportAction = await ResolveTeleportAction(teleportStep, ct);
       return (teleportAction, step.Id);
   }
   ```
3. Implement `ResolveTeleportAction`:
   - Check `AetheryteZoneMap.TryGetZone(step.AetheryteId.Value, out _)`. On miss → return `AwaitUser("teleport target aetheryte {id} not in zone map — author must extend AetheryteZoneMap")`.
   - Read `IsPlayerInCombat`. On `Result.Success(true)` → return `AwaitUser("cannot teleport while in combat")`. On read failure → fail-open (proceed to Teleport), mirroring how `ResolveDefenseOrNull` fail-opens.
   - Otherwise return `new EngineAction.Teleport(new AdaptersAetheryteId(step.AetheryteId.Value), Origin: step)`.

### T-5: `FakeTeleporter` — failure-scripting

Add `ScriptNextTeleportFailure(string, string?)` and the consumption logic per Decision T10.

### T-6: `EngineTestHarness.RunToCompletion` — Teleport dispatch arm

Add the arm from Decision T11.

---

## Validation rules (Phase-1+ follow-up, not required for tests)

These rules belong in `QuestForge.Tools.Validator` eventually; this plan does **not** implement them but documents them here so the validator phase knows what to add.

| Rule | Code | Suppressed when |
|---|---|---|
| `aetheryteId` non-zero | `structural/teleport-aetheryte-id-missing` | — |
| `aetheryteId` is in engine `AetheryteZoneMap` (warning, not error: engine handles runtime guard) | `structural/teleport-aetheryte-not-in-map` (Severity.Warning) | — |

The validator does not need to know which zones map to which aetherytes for Phase 1 — the engine guard suffices.

---

## Given-When-Then test scenarios

All tests live in `QuestForge.Engine.Tests/Engine/TeleportStepTests.cs`. Test class shape mirrors `AttunementStepTests`: a single `BuildSingleStepQuest` factory at the bottom, one `[Fact]` per scenario.

For every scenario:
- `harness.QuestState.SetQuestSequence(new QuestId(questId), 0)` is called.
- The quest contains exactly one TeleportStep in sequence 0 (unless noted).
- `AetheryteZoneMap` must contain the relevant aetheryte (Builder seeds `1000u → 130u` and `8u → 129u`).

### T1 — Happy path: not yet in target zone → emits Teleport

**Given:**
- Player is in zone `129` (not the target).
- TeleportStep `{ AetheryteId = AetheryteId(1000) }` (no authored Expect; engine synthesises `playerZone() == 130`).
- `AetheryteZoneMap` maps `1000 → 130`.

**When:** `Engine.Tick()`.

**Then:** Returns `EngineAction.Teleport(Destination: AdaptersAetheryteId(1000), Origin: <the step>)`.

**No call counts asserted yet (the harness dispatch is the I-integration test).**

### T2 — Happy path completion: I-integration tick sequence

**Given:** Same as T1, plus `FakeTeleporter.RegisterAetheryte(AetheryteId(1000), ZoneId(130), <any pos>)` so `TeleportToAetheryte` flips the fake zone on `Arrived`.

**When:**
1. Tick 1 → assert `EngineAction.Teleport`.
2. Manually `await harness.Teleporter.TeleportToAetheryte(AetheryteId(1000), CancellationToken.None);` (this is what `RunToCompletion` does; the test mimics it).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.Wait` (synthesised Expect `playerZone() == 130` now true → step confirmed → no more steps).
- `harness.Teleporter.RecordedTeleports.Count == 1`.
- `harness.Teleporter.RecordedTeleports[0].Destination.Value == 1000u`.

### T3 — InCombat refusal: emits AwaitUser, no teleport call

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(1000) }`.
- `harness.GameState.SetInCombat(true)`.
- Player zone `129` (not at target).

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.AwaitUser` with `Reason` containing the substring `"cannot teleport while in combat"`.
- `harness.Teleporter.RecordedTeleports.Count == 0`.

### T4 — Mounted before teleport: dismount fires when preceded by Navigate

**Given:**
- Two-step sequence in sequence 0:
  1. TravelStep with `Destination.Position = (10,0,20)` (Navigate fires).
  2. TeleportStep `{ AetheryteId = AetheryteId(1000) }`.
- TravelStep `Expect = "playerZone() == 130"` so the player must teleport to satisfy.
  *(Actually for T4 we use a simpler shape: a TravelStep with Expect `"playerPos.Equals(target)"` is overkill. Instead use a TravelStep that becomes satisfied between Tick 1 and Tick 2 by manually advancing the fake position.)*
- Player initially at `(0,0,0)`, mounted (`SetMountState(MountState.Mounted)`), zone `129`.

**Setup detail:** the cleanest way to exercise the dismount pre-switch is to use `RunToCompletion`. The test:
1. Adds aetheryte registration (`RegisterAetheryte(AetheryteId(1000), ZoneId(130), ...)`) so the teleport flips the zone on arrival.
2. Wires a callback or pre-sets state so that after the first Navigate dispatch, the position equals the destination (`harness.GameState.SetPosition(new WorldPosition(10,0,20))` in the FakeNavigator’s NavigateTo callback — or just set it before the second tick by hand using direct `Tick()` calls).

**When:** Tick the engine repeatedly (`RunToCompletion` or two manual ticks with state advancement between).

**Then:**
- The first dispatched action is `Navigate`.
- Before the second action (`Teleport`) is returned to the caller, `harness.Mount.DismountCallCount >= 1`.
- The second dispatched action is `Teleport`.

**Negative variant T4b — TeleportStep alone, mounted, no prior Navigate:**
- One-step quest: just TeleportStep, player mounted, zone 129.
- Tick once.
- `Engine.Tick` returns `EngineAction.Teleport`.
- `harness.Mount.DismountCallCount == 0` (no prior Navigate means the lazy-dismount hook does not fire; mirrors current Aethernet behaviour).
- This pins the “lazy dismount is bound to prior Navigate, not to teleport-step entry” contract.

### T5 — ITeleporter returns failure → engine re-emits Teleport (no counter in this phase)

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(1000) }`, player in zone `129`.
- `harness.Teleporter.ScriptNextTeleportFailure("adapter-error", "lifestream rejected")`.
- `RegisterAetheryte(AetheryteId(1000), ZoneId(130), ...)` is **omitted** so that even if the fake didn’t fail, no zone change would occur — defensive setup.

**When:**
1. Tick 1 → `EngineAction.Teleport`.
2. Manually call `await harness.Teleporter.TeleportToAetheryte(AetheryteId(1000), ct)` (returns `Result.Failure`; the fake records the call).
3. Tick 2.

**Then:**
- Tick 2 also returns `EngineAction.Teleport(Destination: AdaptersAetheryteId(1000), …)` — stateless retry, same arm as AttunementStep B4.
- `harness.Teleporter.RecordedTeleports.Count == 2`.

(The failure-counter / recovery ladder is out of scope here, exactly matching AttunementStep B4’s posture.)

### T6 — Adapter-reported Arrived but wrong zone → engine re-emits Teleport

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(1000) }`, player in zone `129`.
- `RegisterAetheryte(AetheryteId(1000), ZoneId(999), …)` — deliberately *wrong* destination zone (mismatches map’s 130).
- The synthesised Expect is `playerZone() == 130`.

**When:**
1. Tick 1 → `EngineAction.Teleport`.
2. Manually call `await harness.Teleporter.TeleportToAetheryte(AetheryteId(1000), ct)` → `Result.Ok(Arrived)`, zone flips to `999`.
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.Teleport` again (Expect still false because `playerZone() == 999 ≠ 130`).
- `harness.Teleporter.RecordedTeleports.Count == 2`.

### T7 — Unknown aetheryteId → AwaitUser before calling ITeleporter

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(424242) }` — guaranteed not in `AetheryteZoneMap`.
- Player zone `129`, not in combat.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.AwaitUser` with `Reason` containing `"424242"` and `"not in zone map"`.
- `harness.Teleporter.RecordedTeleports.Count == 0`.

### T8 — Already in target zone → step skipped via synthesised Expect, no teleport call

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(1000) }`.
- `harness.GameState.SetZone(new ZoneId(130))` — already at the destination.
- No authored Expect (engine synthesises `playerZone() == 130`).

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` (per-step Expect short-circuit fires before dispatch; mirrors UseAethernet B5).
- `harness.Teleporter.RecordedTeleports.Count == 0`.

### T9 — Authored Expect overrides synthesis

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(1000), Expect = PredicateExpect("isAttuned(8)") }`.
- `harness.GameState.SetAetheryteAttuned(AdaptersAetheryteId(8), true)` — author’s predicate is true *before* teleport.
- Player zone `129` (so the *would-be* synthesised predicate `playerZone() == 130` is false, but synthesis must not run because Expect is authored).

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.Wait` (authored Expect is true; step is skipped without dispatching Teleport).
- `harness.Teleporter.RecordedTeleports.Count == 0`.

This pins Decision T5’s "synthesis is suppressed when Expect is authored" contract.

### T10 — Cancellation propagates from dispatch arm

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(1000) }`, player zone `129`.
- `var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates (the engine’s `IsPlayerInCombat` adapter read honours `ct.ThrowIfCancellationRequested()`; the fake does the same — same pattern as every other async arm).

### T11 — InCombat adapter read failure → fail-open, Teleport still emitted

**Given:**
- TeleportStep `{ AetheryteId = AetheryteId(1000) }`, player zone `129`.
- `harness.GameState.SetInCombatFailure("adapter-error")`.

**When:** `Engine.Tick()`.

**Then:**
- Returns `EngineAction.Teleport(Destination: AdaptersAetheryteId(1000), …)`.
- `harness.Teleporter.RecordedTeleports.Count == 0` (the dispatch is *returned*, not yet executed against the fake — that happens in `RunToCompletion`).

This pins Decision T6’s "InCombat read failure fail-opens, mirroring global defense."

### T12 — Round-trip JSON serialization of TeleportStep

**Given:** A `TeleportStep { Id = "teleport-to-limsa", AetheryteId = new AetheryteId(8) }`.

**When:** Serialize via `QuestForgeJsonContext.QuestFileOptions`, then deserialize as `Step`.

**Then:**
- The serialized JSON contains `"type": "teleport"` and `"aetheryteId": 8`.
- The deserialized value is a `TeleportStep` with `AetheryteId.Value == 8`.

This is the mandatory gate (per Phase 1 plan §1.2.1) for adding any step type.

---

## Implementation order

**Phase A — Schema (10 min)**
1. Add `TeleportStep` to `Step.cs`.
2. Add `[JsonDerivedType(typeof(TeleportStep), "teleport")]`.
3. Add `[JsonSerializable(typeof(TeleportStep))]` to `QuestForgeJsonContext`.
4. Write T12 round-trip test, green it.

**Phase B — Engine action + map (10 min)**
1. Add `EngineAction.Teleport`.
2. Add `QuestForge.Engine/Travel/AetheryteZoneMap.cs` with the two seed entries.

**Phase C — Engine synthesis + dispatch (30-60 min, TDD)**
1. Write T7, T8, T9 (the cheapest tests — they exercise synthesis and the dispatch guards without needing a full tick chain).
2. Implement `SynthesizeTeleportExpect` and the per-step async pre-arm `ResolveTeleportAction`.
3. Make T7/T8/T9 green.
4. Write T1, T3, T11 (single-tick dispatch shape).
5. Make them green.
6. Write T10 (cancellation).
7. Write T5, T6 (manual two-tick stateless retry).
8. Make them green.

**Phase D — Fakes + harness wiring (20 min)**
1. Add `ScriptNextTeleportFailure` to `FakeTeleporter`.
2. Add `EngineAction.Teleport` arm to `EngineTestHarness.RunToCompletion`.
3. Write T2 (integration via two manual ticks with explicit `await Teleporter.TeleportToAetheryte`).
4. Write T4 (mounted-with-prior-Navigate) and T4b (no-prior-Navigate).
5. Make them green.

**Phase E — Plugin wiring (out of scope for tests; do as follow-up PR)**
1. Mirror T11 (Teleport arm) into `EngineHost.DispatchAction` calling `_teleporter.TeleportToAetheryte`.
2. Add a debounce comparable to `AethernetCooldown` if observed re-fire occurs in-game.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~TeleportStepTests` reports all twelve tests green.
2. A quest JSON file with `{ "type": "teleport", "aetheryteId": 8 }` round-trips through `QuestForgeJsonContext.QuestFileOptions` losslessly.
3. `qf-validate` continues to pass on existing quest data (no Phase-1 validator changes required by this plan).
4. The trace emitted on a Teleport tick contains a `DecisionEvent` with `ActionType == "Teleport"`.
5. No regression in `AttunementStepTests`, `UseAethernetStepTests`, or `TravelNpcDialogueEngineTests` (the new arm is additive; existing TravelStep arms must remain unchanged).

---

## Exclusions (what this plan does NOT include)

- Failure-counter increments / recovery ladder for repeated teleport failures (deferred — same posture as AttunementStep).
- `qf-validate` rules `structural/teleport-aetheryte-id-missing` and `structural/teleport-aetheryte-not-in-map` (deferred to a validator-side PR).
- Dalamud-side `EngineHost.DispatchAction` arm (follow-up PR; the engine arm and tests land first so the schema/engine surface is locked).
- Cost / cooldown checks before dispatching (`ITeleporter.EstimateTeleportCost` / `GetTeleportCooldown`). Lifestream handles cooldown internally; the engine treats the call as best-effort like `UseAethernet`.
- A `UseTeleport` recovery action wired to `RecoverConfig` (the recovery type exists in schema as `UseTeleportRecoverAction`, but wiring it to the new TeleportStep dispatch is a separate task).
- A `useTeleport(N)` predicate function in the predicate language.
- Authoring-mode inference of TeleportStep from observed Lifestream calls (Phase 9 follow-up).

---

✅ READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in §"Given-When-Then test scenarios".
- Happy paths: 2 scenarios (T1, T2)
- Edge cases: 5 scenarios (T4, T4b, T8, T9, T11)
- Error cases: 5 scenarios (T3, T5, T6, T7, T10)
- Serialization gate: 1 scenario (T12)
- Expected total: ~13 tests in `QuestForge.Engine.Tests/Engine/TeleportStepTests.cs` (plus T12 either in the same file or in a schema round-trip test file, depending on existing layout).
