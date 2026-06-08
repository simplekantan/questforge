# AethernetStep Implementation Plan

**Status:** ready for test creation
**Input docs:** docs/SCHEMA.md (step taxonomy), docs/ADAPTERS.md, docs/TELEPORT_STEP_PLAN.md (closest analog), existing TravelStep aethernet handling in QuestEngine.cs
**Output (CI behavior):** Adding a `{ "type": "aethernet", "from": 8, "to": 48, "fromPosition": {...} }` step to a quest dispatches `EngineAction.UseAethernet` cleanly via a dedicated dispatch arm. Engine unit tests (xUnit, `QuestForge.Engine.Tests`) cover all dispatch arms against fakes. CI red when tests are written; CI green when implementation lands.
**Phase dependencies:** None. Existing `EngineAction.UseAethernet`, `AethernetId` (adapter type), `FakeTeleporter.TeleportToAethernet`, and `EngineTestHarness` UseAethernet arm are already wired from the TravelStep aethernet work.

---

## Motivation

Intra-zone aethernet travel is currently expressed as a `TravelStep` with `routeHint.aethernet`:

```json
{
  "type": "travel",
  "destination": { "zone": 128, "position": { "x": -218.9, "y": 16.0, "z": 51.4 } },
  "routeHint": { "aethernet": { "from": 8, "to": 48 } },
  "expect": "playerZone() == 128"
}
```

This creates three problems:

1. **TravelStep dispatch is overloaded.** `ResolveActionForStep` has three `TravelStep` arms (NpcDialogue, Aethernet, plain Navigate), each with different routing logic. Adding guard logic (e.g., in-combat refusal) requires touching every arm.
2. **The "hint" is the primary mechanic.** The destination position is actually the source shard's location, not a navigation goal. The semantic inversion confuses authors.
3. **QuestDataDrivenState has three TravelStep sub-cases.** The replay fixture state machine pattern-matches on `RouteHint.Aethernet is not null` to decide whether Navigate means "move to shard" or "move to final destination."

A dedicated `AethernetStep` makes the intent explicit and the dispatch straightforward.

---

## Dependency graph

```
QuestForge.Schema
   +-- AethernetStep added; registered in QuestForgeJsonContext
        +-- consumed by:
QuestForge.Engine
   +-- new dispatch arm in ResolveActionForStep for AethernetStep
   +-- reuses existing EngineAction.UseAethernet (no new action record)
        +-- consumed by:
QuestForge.Engine.Tests
   +-- AethernetStepTests against existing FakeTeleporter
QuestForge.Engine/Authoring/DraftValidator.cs
   +-- E25: from == 0, E26: to == 0, W12: missing Expect
        +-- consumed by:
QuestForge.Engine.Tests/Authoring/DraftValidatorAethernetTests.cs
```

**Build order:** Schema type -> JSON context registration -> engine dispatch arm -> validator rules -> tests. No new EngineAction, no new fake, no new adapter interface.

---

## Architectural decisions (read before coding)

### Decision AN1 -- AethernetStep is a first-class step type, not a TravelStep variant

The TravelStep aethernet arm will remain for backward compatibility, but new quests should use `AethernetStep`. Three concrete benefits:

1. **Dispatch clarity.** One `AethernetStep` arm in `ResolveActionForStep` replaces the pattern-matched `TravelStep when travel.RouteHint?.Aethernet is { To: > 0 }` guard. The step type is the discriminator, not a nested property.
2. **Field semantics are obvious.** `from` is the source shard ID. `to` is the destination shard ID. `fromPosition` is the world position of the source shard. No confusion about what `Destination.Position` means.
3. **QuestDataDrivenState gains a clean arm.** `AethernetStep => action is EngineAction.UseAethernet` replaces the `TravelStep { RouteHint.Aethernet: not null }` pattern match in `IsPrimaryActionForStep`.

**Rejected alternative:** Migrating all existing TravelStep aethernet usage to AethernetStep and removing the TravelStep arm. This would break existing quest data and trace fixtures. The old arm stays; we add deprecation guidance.

**What breaks if violated:** If someone adds a new quest using TravelStep with routeHint.aethernet, the behavior is identical -- but the quest JSON is harder to read and the QuestDataDrivenState dispatch remains complex.

### Decision AN2 -- Schema shape: three dedicated fields, no nesting

```csharp
public sealed class AethernetStep : Step
{
    /// <summary>Source aethernet shard ID. The player navigates to this shard first.</summary>
    public uint From { get; init; }

    /// <summary>Destination aethernet shard ID. Passed to Lifestream via UseAethernet.</summary>
    public uint To { get; init; }

    /// <summary>
    /// World position of the source shard. The engine uses implied navigation:
    /// if the player is beyond StopDistance, it emits Navigate to this position first.
    /// Required -- without it the engine cannot navigate to the shard.
    /// </summary>
    public Position3 FromPosition { get; init; } = default!;
}
```

JSON shape:
```json
{
  "type": "aethernet",
  "id": "aethernet-to-marauder-guild",
  "from": 8,
  "to": 48,
  "fromPosition": { "x": -218.9, "y": 16.0, "z": 51.4 },
  "expect": "playerZone() == 128"
}
```

**Why `uint` instead of a schema-side `AethernetId` wrapper:** The existing `AethernetRouteHint` record uses plain `uint` for `From` and `To`. The adapter-side `AethernetId` (in `QuestForge.Adapters.Types`) is a `readonly record struct`. The engine dispatch converts `uint` to `AethernetId` at the dispatch boundary (`new AethernetId(step.To)`), same as the existing TravelStep aethernet arm. Adding a schema-side wrapper would require a custom JSON converter (like `AetheryteIdConverter`) for no additional type safety -- shard IDs are opaque game constants, not typed domain objects that benefit from nominal typing at the schema layer.

**Rejected alternative:** Wrapping `from`/`to` in a nested `AethernetRoute` record (e.g., `public AethernetRoute Route { get; init; }`). This adds indirection with no benefit -- the step has exactly three fields beyond the base `Step`, all directly related to the aethernet hop.

**Rejected alternative:** Omitting `fromPosition` and requiring a preceding TravelStep. This breaks the "one step = one action" pattern that makes AethernetStep self-contained. The existing TravelStep arm uses `Destination.Position` as the navigate-to target for the same reason -- the shard position must be known for implied navigation.

**What breaks if violated:** If `FromPosition` is omitted (made nullable), the engine must either always emit `UseAethernet` without navigation (player might be far away) or require a separate TravelStep -- which defeats the purpose of a dedicated step type.

### Decision AN3 -- Reuse existing `EngineAction.UseAethernet`, do not create a new action

The existing action:
```csharp
public sealed record UseAethernet(AethernetId Destination, WorldPosition? SourcePosition = null) : EngineAction;
```

This already carries everything the host needs: the destination shard ID and the source position (for trace logging). The `SourcePosition` is populated from `FromPosition` at dispatch time.

**What breaks if violated:** Creating a new `EngineAction.Aethernet` would require a second dispatch arm in `EngineHost.DispatchAction`, duplicating the Lifestream IPC call and debounce logic. The host would need to handle two action types for the same underlying operation.

### Decision AN4 -- Engine dispatch arm: navigate to fromPosition if far, UseAethernet if near

The dispatch arm in `ResolveActionForStep` uses the existing `ResolveInteractOrNavigate` helper:

```csharp
AethernetStep aethernet =>
    ResolveInteractOrNavigate(
        step, aethernet.FromPosition, playerPos,
        new EngineAction.UseAethernet(
            new AethernetId(aethernet.To),
            new WorldPosition(aethernet.FromPosition.X, aethernet.FromPosition.Y, aethernet.FromPosition.Z))),
```

This mirrors the existing TravelStep aethernet arm exactly. `ResolveInteractOrNavigate` handles the distance check and emits `Navigate` if the player is beyond `StopDistance`, otherwise returns the provided action.

**No async pre-arm needed.** Unlike TeleportStep (which needs InCombat guard and AetheryteZoneMap lookup), AethernetStep has no pre-flight guards beyond the implied navigation. The UseAethernet action is dispatched every tick until the Expect is satisfied -- the EngineHost debounces duplicate calls via `AethernetCooldown`.

**`_lastResolvedStep` is NOT set in any pre-arm** -- but since this step does not use an async pre-arm (it dispatches from the synchronous `ResolveActionForStep` switch), this invariant is automatically maintained. The `_lastResolvedStep` assignment happens in the caller at line 806 of QuestEngine.cs, after `ResolveActionForStep` returns.

### Decision AN5 -- No new adapter interface, no new fake

The AethernetStep dispatches `EngineAction.UseAethernet`, which is already handled by:
- `EngineTestHarness.RunToCompletion` (calls `Teleporter.TeleportToAethernet`)
- `EngineHost.DispatchAction` (calls `_teleporter.TeleportToAethernet` with debounce)
- `FakeTeleporter.TeleportToAethernet` (records calls, simulates zone change)

No new adapter, executor, or fake is needed. This is a schema + engine dispatch change only.

### Decision AN6 -- Backward compatibility: TravelStep aethernet arm stays, deprecation comment added

The existing TravelStep arm in `ResolveActionForStep`:
```csharp
TravelStep travel when travel.RouteHint?.Aethernet is { To: > 0 } hop => ...
```

remains unchanged. A doc comment is added above it:

```csharp
// DEPRECATED: prefer AethernetStep for new quests. This arm handles legacy
// TravelStep quests that encode aethernet hops via routeHint.aethernet.
```

Existing quest data files are not migrated. New quests authored after this lands should use `AethernetStep`.

### Decision AN7 -- QuestDataDrivenState gains an AethernetStep arm

In `QuestDataDrivenState.IsPrimaryActionForStep`:
```csharp
AethernetStep => action is EngineAction.UseAethernet,
```

And in the Navigate dispatch block, AethernetStep Navigate means "move to source shard position":
```csharp
else if (currentPlan.Step is AethernetStep aethernetStep)
{
    var shardPos = aethernetStep.FromPosition;
    _gameState.SetPosition(new WorldPosition(shardPos.X, shardPos.Y, shardPos.Z));
}
```

This mirrors the existing TravelStep aethernet Navigate handling but is explicit about the step type.

### Decision AN8 -- Validator rules: E25 (from == 0), E26 (to == 0), W12 (missing Expect)

Following the established pattern (E6/E7/E9 for impossible-zero fields, W7/W8/W9/W10/W11 for missing Expect with spin-loop warning):

**E25:** `AethernetStep.From == 0` is an error. Shard ID 0 is not a valid aethernet shard.
```csharp
// E25: AethernetStep with From == 0
if (steps[i].Raw is AethernetStep aes && aes.From == 0)
    errors.Add(new DraftValidationError("E25",
        $"Step '{steps[i].StepId}' is an AethernetStep with From == 0.", [i]));
```

**E26:** `AethernetStep.To == 0` is an error. Same reasoning.
```csharp
// E26: AethernetStep with To == 0
if (steps[i].Raw is AethernetStep aes && aes.To == 0)
    errors.Add(new DraftValidationError("E26",
        $"Step '{steps[i].StepId}' is an AethernetStep with To == 0.", [i]));
```

**W12:** AethernetStep with no Expect. The engine fires UseAethernet every tick until Expect is satisfied. Without an Expect, this is an infinite spin-loop.
```csharp
// W12: AethernetStep with no Expect -- engine spin-loops without one
if (steps[i].Raw is AethernetStep aes && aes.Expect is null)
    warnings.Add(new DraftValidationWarning("W12",
        $"Step '{steps[i].StepId}' is an AethernetStep with no 'expect' predicate " +
        "-- without it the engine will spin-loop re-emitting UseAethernet. Add an expect predicate.",
        [i]));
```

**W1 suppression guard update:** The W1 catch-all must exclude `AethernetStep` (same pattern as UseActionStep, UseEmoteStep, etc.):
```csharp
&& step.Raw is not UseActionStep
    and not UseEmoteStep
    and not SayChatMessageStep
    and not UseItemStep
    and not EquipGearForQuestStep
    and not ChangeJobStep
    and not AethernetStep  // <-- new exclusion
```

### Decision AN9 -- Dismount handling: NOT exempt from lazy dismount

AethernetStep is NOT added to the dismount exemption list. The lazy-dismount pre-switch hook in EngineHost fires when the previous action was Navigate and the current action is non-Navigate. Since UseAethernet is non-Navigate, dismount fires naturally when a Navigate precedes the UseAethernet dispatch.

This matches the existing TravelStep aethernet behavior -- no change needed. The UseAethernet arm is already non-exempt in the existing `is not EngineAction.Navigate and not EngineAction.Teleport` check (UseAethernet is neither Navigate nor Teleport, so it IS subject to dismount).

---

## Task breakdown

### Task AN-1: `QuestForge.Schema` -- add `AethernetStep`

1. Add to `Step.cs`:
   ```csharp
   public sealed class AethernetStep : Step
   {
       public uint From { get; init; }
       public uint To { get; init; }
       public Position3 FromPosition { get; init; } = default!;
   }
   ```
2. Add `[JsonDerivedType(typeof(AethernetStep), "aethernet")]` to the Step polymorphic attribute list.
3. Register in `QuestForgeJsonContext.cs`: `[JsonSerializable(typeof(AethernetStep))]`.
4. Write round-trip serialization test (mandatory gate per Phase 1 plan).

### Task AN-2: `QuestForge.Engine` -- add dispatch arm in `ResolveActionForStep`

Add to the step-dispatch switch in `ResolveActionForStep`, after the AttunementStep arm and before the catch-all:

```csharp
AethernetStep aethernet =>
    ResolveInteractOrNavigate(
        step, aethernet.FromPosition, playerPos,
        new EngineAction.UseAethernet(
            new AethernetId(aethernet.To),
            new WorldPosition(aethernet.FromPosition.X, aethernet.FromPosition.Y, aethernet.FromPosition.Z))),
```

No async pre-arm. No `_lastResolvedStep` concerns (handled by the caller).

### Task AN-3: `QuestForge.Engine/Authoring/DraftValidator.cs` -- add E25, E26, W12

1. Add E25 (From == 0) in the step validation loop alongside E6-E24.
2. Add E26 (To == 0) in the step validation loop alongside E25.
3. Add W12 (missing Expect) in the warning section alongside W7-W11.
4. Update W1 suppression guard to exclude `AethernetStep`.

### Task AN-4: `QuestForge.Engine.Tests/Replay/States/QuestDataDrivenState.cs` -- add AethernetStep arm

1. Add `AethernetStep` case to `IsPrimaryActionForStep`.
2. Add Navigate handling for AethernetStep (set position to source shard position).
3. Add UseAethernet handling for AethernetStep (apply mutations + start loading zone simulation), mirroring the existing TravelStep aethernet UseAethernet case.

### Task AN-5: Add deprecation comment to existing TravelStep aethernet arm

Add comment above the `TravelStep when travel.RouteHint?.Aethernet` arm in `ResolveActionForStep`.

---

## Validation rule table

| Rule | Code | Condition | Severity | Suppressed when |
|------|------|-----------|----------|-----------------|
| E25 | `E25` | `AethernetStep.From == 0` | Error | -- |
| E26 | `E26` | `AethernetStep.To == 0` | Error | -- |
| W12 | `W12` | `AethernetStep.Expect is null` | Warning | -- |
| W1 update | `W1` | Exclude `AethernetStep` from generic missing-Expect warning | Warning | When step is `AethernetStep` (W12 fires instead) |

---

## Given-When-Then test scenarios

All engine tests live in `QuestForge.Engine.Tests/Engine/AethernetStepTests.cs`. Test class shape mirrors `UseAethernetStepTests` and `TeleportStepTests`: a `BuildSingleStepQuest` factory, one `[Fact]` per scenario.

For every scenario unless noted:
- `harness.QuestState.SetQuestSequence(new QuestId(questId), 0)` is called.
- The quest contains exactly one AethernetStep in sequence 0.
- Player zone is set to the destination zone in the Expect (so zone-check-based Expect is pre-satisfied), OR explicitly set to a different zone to test navigation.

### A1 -- Happy path: player far from source shard, emits Navigate to fromPosition

**Given:**
- Player at position (0, 0, 0) in zone 128.
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 }, Expect = "playerZone() == 128" }`.

**When:** `Engine.Tick()`.

**Then:** Returns `EngineAction.Navigate` with `Destination` approximately (-218.9, 16.0, 51.4).

### A2 -- Happy path: player near source shard, emits UseAethernet

**Given:**
- Player at position (-218.0, 16.0, 51.0) in zone 129 (near the source shard).
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 }, Expect = "playerZone() == 128" }`.

**When:** `Engine.Tick()`.

**Then:** Returns `EngineAction.UseAethernet` with `Destination.Value == 48` and `SourcePosition` approximately (-218.9, 16.0, 51.4).

### A3 -- Expect already satisfied: step skipped, no action emitted

**Given:**
- Player in zone 128 (the destination zone from Expect).
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 }, Expect = "playerZone() == 128" }`.

**When:** `Engine.Tick()`.

**Then:** Returns `EngineAction.Wait` (Expect is already true; step is skipped before dispatch). `harness.Teleporter.RecordedAethernetTeleports.Count == 0`.

### A4 -- Two-tick completion: Navigate then UseAethernet then Expect satisfied

**Given:**
- Player at position (0, 0, 0) in zone 129.
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 }, Expect = "playerZone() == 128" }`.
- `FakeTeleporter` configured with `RegisterAethernet(AethernetId(48), ZoneId(128))` so TeleportToAethernet flips zone on arrival.

**When:**
1. Tick 1 -> assert `EngineAction.Navigate`.
2. Set player position to (-218.9, 16.0, 51.4) via `harness.GameState.SetPosition(...)`.
3. Tick 2 -> assert `EngineAction.UseAethernet(Destination: AethernetId(48))`.
4. Manually `await harness.Teleporter.TeleportToAethernet(AethernetId(48), ct)`.
5. Tick 3.

**Then:**
- Tick 3 returns `EngineAction.Wait` (Expect satisfied after zone change).
- `harness.Teleporter.RecordedAethernetTeleports.Count == 1`.
- Recorded call's `Destination.Value == 48`.

### A5 -- Custom StopDistance: player within custom range emits UseAethernet

**Given:**
- Player at position (-210.0, 16.0, 51.0) in zone 129 (about 9 units from source shard).
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 }, StopDistance = 10.0f, Expect = "playerZone() == 128" }`.

**When:** `Engine.Tick()`.

**Then:** Returns `EngineAction.UseAethernet` (player is within 10-unit custom StopDistance, not the default 3.0).

### A6 -- Default StopDistance: player at 4 units emits Navigate (just outside default 3.0)

**Given:**
- Player at position (-215.0, 16.0, 51.4) in zone 129 (about 3.9 units from source shard).
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 }, Expect = "playerZone() == 128" }`.

**When:** `Engine.Tick()`.

**Then:** Returns `EngineAction.Navigate` (player is beyond default 3.0 StopDistance).

### A7 -- Adapter-reported Arrived but wrong zone: engine re-emits UseAethernet

**Given:**
- Player near source shard in zone 129.
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 }, Expect = "playerZone() == 128" }`.
- `RegisterAethernet(AethernetId(48), ZoneId(999))` -- deliberately wrong destination zone.

**When:**
1. Tick 1 -> `EngineAction.UseAethernet`.
2. Manually `await harness.Teleporter.TeleportToAethernet(AethernetId(48), ct)` (zone flips to 999).
3. Tick 2.

**Then:**
- Tick 2 returns `EngineAction.UseAethernet` again (Expect still false because `playerZone() == 999 != 128`).
- `harness.Teleporter.RecordedAethernetTeleports.Count == 2`.

### A8 -- Missing Expect (null): engine emits UseAethernet every tick (spin-loop)

**Given:**
- Player near source shard in zone 129.
- AethernetStep `{ From = 8, To = 48, FromPosition = { X = -218.9, Y = 16.0, Z = 51.4 } }` (no Expect).

**When:** Tick twice (no state changes between ticks).

**Then:** Both ticks return `EngineAction.UseAethernet`. This demonstrates the spin-loop that W12 warns about. The engine has no way to know the step is complete without an Expect.

### A9 -- Round-trip JSON serialization

**Given:** An `AethernetStep { Id = "aethernet-to-guild", From = 8, To = 48, FromPosition = new Position3(-218.9f, 16.0f, 51.4f) }`.

**When:** Serialize via `QuestForgeJsonContext.QuestFileOptions`, then deserialize as `Step`.

**Then:**
- The serialized JSON contains `"type": "aethernet"`, `"from": 8`, `"to": 48`, and `"fromPosition"`.
- The deserialized value is an `AethernetStep` with `From == 8`, `To == 48`, `FromPosition.X` approximately -218.9.

### A10 -- Cancellation propagates

**Given:**
- AethernetStep with valid fields. Player far from source shard.
- `var cts = new CancellationTokenSource(); cts.Cancel();`

**When:** `await harness.Engine.Tick(cts.Token)`.

**Then:** `OperationCanceledException` propagates.

---

### Validator test scenarios

All tests live in `QuestForge.Engine.Tests/Authoring/DraftValidatorAethernetTests.cs`.

### AV1 -- E25: From == 0 is an error

**Given:** A draft with one AethernetStep where `From = 0`, `To = 48`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** `errors` contains exactly one error with Code `"E25"`.

### AV2 -- E26: To == 0 is an error

**Given:** A draft with one AethernetStep where `From = 8`, `To = 0`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** `errors` contains exactly one error with Code `"E26"`.

### AV3 -- E25 + E26: both From and To == 0

**Given:** A draft with one AethernetStep where `From = 0`, `To = 0`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** `errors` contains errors with codes `"E25"` and `"E26"` (both fire independently).

### AV4 -- W12: missing Expect emits spin-loop warning

**Given:** A draft with one AethernetStep where `From = 8`, `To = 48`, `Expect = null`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** `warnings` contains a warning with Code `"W12"` and message containing `"spin-loop"`.

### AV5 -- W12 not emitted when Expect is present

**Given:** A draft with one AethernetStep where `From = 8`, `To = 48`, `Expect = PredicateExpect("playerZone() == 128")`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** `warnings` does NOT contain a warning with Code `"W12"`.

### AV6 -- W1 suppression: AethernetStep without Expect does NOT trigger W1

**Given:** A draft with one AethernetStep where `From = 8`, `To = 48`, `Expect = null`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** `warnings` does NOT contain a warning with Code `"W1"`. (W12 fires instead.)

### AV7 -- Valid AethernetStep produces no errors

**Given:** A draft with one AethernetStep where `From = 8`, `To = 48`, `Expect = PredicateExpect("playerZone() == 128")`.

**When:** `DraftValidator.Validate(draft)`.

**Then:** No errors related to AethernetStep. No W12 warning.

---

## Implementation order

**Phase A -- Schema (15 min)**
1. Add `AethernetStep` to `Step.cs`.
2. Add `[JsonDerivedType(typeof(AethernetStep), "aethernet")]`.
3. Add `[JsonSerializable(typeof(AethernetStep))]` to `QuestForgeJsonContext`.
4. Write A9 round-trip test, green it.

**Phase B -- Engine dispatch (20 min, TDD)**
1. Write A1, A2, A3 (single-tick dispatch shape tests).
2. Implement the `AethernetStep` arm in `ResolveActionForStep`.
3. Make A1/A2/A3 green.
4. Write A5, A6 (StopDistance boundary tests).
5. Make them green.
6. Write A8 (spin-loop), A10 (cancellation).
7. Make them green.

**Phase C -- Multi-tick scenarios (20 min)**
1. Write A4 (Navigate -> UseAethernet -> completion).
2. Write A7 (wrong-zone re-emit).
3. Make them green. (Existing harness UseAethernet arm handles dispatch.)

**Phase D -- Validator (15 min, TDD)**
1. Write AV1-AV7 tests.
2. Implement E25, E26, W12 in DraftValidator.
3. Update W1 suppression guard.
4. Make all green.

**Phase E -- QuestDataDrivenState + deprecation (15 min)**
1. Add AethernetStep arm to `IsPrimaryActionForStep`.
2. Add AethernetStep Navigate handling.
3. Add AethernetStep UseAethernet handling.
4. Add deprecation comment to TravelStep aethernet arm.

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~AethernetStepTests` reports all engine tests green.
2. `dotnet test QuestForge.Engine.Tests --filter FullyQualifiedName~DraftValidatorAethernetTests` reports all validator tests green.
3. A quest JSON file with `{ "type": "aethernet", "from": 8, "to": 48, "fromPosition": {...} }` round-trips through `QuestForgeJsonContext.QuestFileOptions` losslessly.
4. `qf-validate` continues to pass on existing quest data (TravelStep aethernet quests are unaffected).
5. No regression in `UseAethernetStepTests` or `TravelNpcDialogueEngineTests` (the new arm is additive; existing TravelStep arms remain unchanged).
6. `DraftValidator.Validate` on an AethernetStep with `From == 0` produces error E25.
7. `DraftValidator.Validate` on an AethernetStep with null Expect produces warning W12 (not W1).

---

## Exclusions (what this plan does NOT include)

- **Migration of existing TravelStep aethernet quests to AethernetStep.** Existing quest data files remain as-is. Migration is a data-repo task, not an engine task.
- **Removal of the TravelStep aethernet dispatch arm.** The arm stays for backward compatibility. Removal is gated on all quest data being migrated.
- **Authoring inference for AethernetStep.** This is Slice 5 per CLAUDE.md. Signal research (detecting Lifestream aethernet calls) is deferred.
- **Tooling catch-up (questforge-tools).** CapabilityInferrer, TraceToFixtureExtractor, and TraceConstants entries for `step:aethernet` are Slice 3 per CLAUDE.md. The needed changes:
  - `CapabilityInferrer.StepCapabilities`: `[typeof(AethernetStep)] = "step:aethernet"`
  - `TraceToFixtureExtractor.FilenameLookup`: add `"step:aethernet"` entry
  - `TraceConstants.cs`: `ActionUseAethernet = "useaethernet"` (already may exist from TravelStep aethernet; verify)
- **In-combat guard for AethernetStep.** Unlike TeleportStep (cross-zone), aethernet is intra-zone and the game itself prevents the menu from opening in combat. The engine does not need a pre-flight InCombat guard.
- **Expect synthesis.** Unlike TeleportStep (which has AetheryteZoneMap to derive the expected zone), there is no reliable mapping from shard IDs to destination zones. Authors must provide Expect explicitly. W12 warns when they don't.
- **Failure-counter / recovery ladder for repeated aethernet failures.** Same posture as the existing TravelStep aethernet arm.

---

## Notes for tooling catch-up (Slice 3)

When the Slice 3 paired PR lands:

1. `CapabilityInferrer.StepCapabilities` dict: `[typeof(AethernetStep)] = "step:aethernet"`.
2. `TraceToFixtureExtractor.FilenameLookup`: add `["step:aethernet"], "with-aethernet.json"` (or fold into existing aethernet fixture entries if they exist).
3. `TraceToFixtureExtractor.DistinguishingCapPriority`: aethernet is less shape-defining than action/emote but more than teleport. Place after `step:teleport`.
4. `TraceConstants.cs`: verify whether `ActionUseAethernet` already exists from TravelStep work. If not, add it.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs above.
- Happy paths: 4 scenarios (A1, A2, A4, A9)
- Edge cases: 4 scenarios (A3, A5, A6, A8)
- Error cases: 2 scenarios (A7, A10)
- Validator scenarios: 7 scenarios (AV1-AV7)
- Expected total: ~17 tests across `QuestForge.Engine.Tests/Engine/AethernetStepTests.cs` and `QuestForge.Engine.Tests/Authoring/DraftValidatorAethernetTests.cs`
