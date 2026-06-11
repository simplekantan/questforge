# DutyStep Split: DungeonTrialStep + SinglePlayerDutyStep -- Architect Spec

**Status:** Draft
**Phase:** 11 (Corpus Expansion)
**Slice:** 1 of 6 (Architect Spec)
**Author:** QuestForge System Architect
**Date:** 2026-06-11

---

## 1. Header

**Input documents:**
- `docs/SCHEMA.md` -- step type taxonomy, `DutyStep` definition (section 4.9), common step fields
- `docs/ADAPTERS.md` -- adapter interfaces, `IInteractor`, `IGameStateProvider`, `ICombat`; SPD entry semantics (section 8.5); failure counters (section 15.6)
- `docs/ARCHITECTURE.md` -- three-layer architecture, engine testability boundary
- `CLAUDE.md` -- fixed slice order, TDD role separation, new step type checklist

**Output (CI behavior changes):**
- `dotnet test QuestForge.Engine.Tests` gains ~60 new tests covering DungeonTrialStep, SinglePlayerDutyStep dispatch, guards, entry kinds, retry, validator rules
- `dotnet test QuestForge.Schema.Tests` gains 4 new round-trip tests (DungeonTrialStep, SinglePlayerDutyStep x3 entry kinds)
- `dotnet test QuestForge.Engine.Tests --filter "DraftValidatorDungeonTrial"` validates E29, W14
- `dotnet test QuestForge.Engine.Tests --filter "DraftValidatorSinglePlayerDuty"` validates E30, E31, E32, E33, W15
- `dotnet test QuestForge.Engine.Tests --filter "DungeonTrialStepTests"` covers all dungeon/trial scenarios
- `dotnet test QuestForge.Engine.Tests --filter "SinglePlayerDutyStepTests"` covers all SPD scenarios including all 3 entry kinds
- **Breaking:** Old `DutyStep` JSON discriminator `"duty"` is removed. Old quest files using `"type": "duty"` fail deserialization. Migration is a find-and-replace in the data repo.

**Phase dependencies:**
- IDutyRunner, IQuestBattleRunner, ICfcResolver (implemented)
- FakeDutyRunner, FakeQuestBattleRunner, FakeCfcResolver (implemented)
- DutyStep engine resolve + host dispatch (implemented -- being replaced)
- DraftValidator E21, E22, W11 rules (implemented -- being replaced)
- TalkStep resolve pattern (for entry-kind "talk" reuse)
- InteractObjectStep resolve pattern (for entry-kind "interact" reuse)
- TravelStep resolve pattern (for entry-kind "proximity" reuse)

---

## 2. Problem Statement

The current `DutyStep` conflates two fundamentally different game mechanics behind a `Kind` string discriminator:

1. **Dungeon/Trial duties** use AutoDuty (`IDutyRunner`), resolve CFC-to-territory-type via `ICfcResolver`, have no entry fields, and AutoDuty handles the full lifecycle (entry, navigation, combat, completion).

2. **Single Player Duties (SPDs)** use BossMod (`IQuestBattleRunner`), have entry-specific fields (`EntryTargetId`, `EntryPosition`), and require the engine to handle the entry mechanism (talk to NPC, interact with object, or proximity trigger) before BossMod takes over combat.

These paths share zero engine logic -- `ResolveDuty` immediately branches into `ResolveSpd` vs `ResolveDungeonTrial`. The current design creates three concrete problems:

**Problem 1: Cannot retry failed SPDs.** When the SPD entry interaction lives in a preceding `TalkStep` or `InteractObjectStep`, the engine has already advanced past that step. After SPD failure + respawn to home aetheryte, the engine cannot re-enter because the entry step is behind the cursor. The entry mechanism must live *inside* the SPD step for retry to work.

**Problem 2: No interact mechanism on duty step.** The existing `DutyStep` has `EntryTargetId` and `EntryPosition` but no `EntryKind` discriminator. The host dispatch arm for `EnterSinglePlayerDuty` calls `_questBattleRunner.StartDuty()` and `_interactor.AdvanceDialogue()` blindly, with no awareness of whether the entry requires talking to an NPC, interacting with an EventObj, or simply walking to a location.

**Problem 3: Schema confusion.** `EntryTargetId`/`EntryPosition` only apply to SPDs. `ContentFinderConditionId` is required for dungeons but optional for SPDs. The `Kind` field is a stringly-typed discriminator that defeats the purpose of System.Text.Json's polymorphic deserialization. Conditional field semantics are confusing for quest authors.

---

## 3. Dependency Graph

```
QuestForge.Schema                (DungeonTrialStep, SinglePlayerDutyStep classes; remove DutyStep)
    |
    v
QuestForge.Engine                (new resolvers; remove ResolveDuty/ResolveSpd/ResolveDungeonTrial)
    |
    v
QuestForge.Engine.Tests          (~60 tests)
    |
QuestForge.Schema.Tests          (4 round-trip tests)
    |
QuestForge.Engine/Authoring      (DraftValidator E29-E33, W14-W15; remove E21, E22, W11)
    |
QuestForge.Adapters.Fakes        (no changes needed -- existing fakes suffice)
```

Build order: Schema -> Engine -> Tests. All in one PR per Slice 2 guidelines.

---

## 4. Architectural Decisions

### DS1: Remove DutyStep entirely; replace with two distinct step types

**Decision:** `DutyStep` is deleted from `Step.cs`. Two new classes replace it: `DungeonTrialStep` (discriminator `"dungeon-trial"`) and `SinglePlayerDutyStep` (discriminator `"single-player-duty"`).

**Alternatives considered:**
- Keeping `DutyStep` and adding a third `Kind` value. Rejected: the two paths share no logic, no fields, and no adapters. A shared base class adds nothing but confusion.
- Using an abstract `DutyStepBase` with two subclasses. Rejected: there are no shared domain fields beyond the `Step` base class. `ContentFinderConditionId` appears in both but with different semantics (required for dungeon, optional-but-recommended for SPD). Shared abstract base would force either making it nullable (weakening dungeon's contract) or duplicating it (defeating the base class).

**What breaks if violated:** If someone keeps `DutyStep`, the `Kind` string discriminator continues to be a runtime hazard. Quest authors must remember which fields apply to which kind. Validator rules remain conditional on `Kind` rather than structural. The engine's `ResolveDuty` remains a multi-path switch.

**Testability:** Two separate test files (`DungeonTrialStepTests.cs`, `SinglePlayerDutyStepTests.cs`) with zero overlap. Each step type gets its own focused scenarios.

### DS2: DungeonTrialStep schema shape

**Decision:**

```csharp
public sealed class DungeonTrialStep : Step
{
    /// <summary>
    /// ContentFinderCondition row ID (Lumina). Required -- non-nullable, non-zero.
    /// The adapter derives territory type from this ID for AutoDuty's IPC.
    /// </summary>
    public uint ContentFinderConditionId { get; init; }
}
```

**Fields:**
- `ContentFinderConditionId` (uint, required, non-zero) -- Lumina CFC row ID. AutoDuty needs territory type derived from this. Making it a non-nullable `uint` (not `uint?`) means the validator catches zero at validation time rather than null at runtime.

**Why no entry fields:** AutoDuty handles the full duty lifecycle: opening Duty Support menu, forming an NPC party, navigating, combat (via BossMod/WrathCombo), and completion. The engine does not need to know where the entry NPC is or how to interact with them. AutoDuty handles all of this internally.

**Why no `DutyFallbackPolicy` field:** This was always a plugin-level configuration, not a per-step field. The current `DutyStep.fallbackOverride` is documented in SCHEMA.md but never implemented. Omitting it from `DungeonTrialStep` keeps the step type focused.

### DS3: SinglePlayerDutyStep schema shape

**Decision:**

```csharp
public sealed class SinglePlayerDutyStep : Step
{
    /// <summary>
    /// ContentFinderCondition row ID (Lumina). Required (non-zero).
    /// Needed for BossMod activation and authoring inference.
    /// </summary>
    public uint ContentFinderConditionId { get; init; }

    /// <summary>
    /// How the SPD is entered. Discriminator for the entry mechanism.
    /// </summary>
    public SpdEntryKind EntryKind { get; init; }

    /// <summary>
    /// NPC data ID (for "talk") or EventObj data ID (for "interact").
    /// Null for "proximity" (area trigger, no interactable).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? EntryTargetId { get; init; }

    /// <summary>
    /// World-space position where the player navigates for entry.
    /// Required for all entry kinds -- the engine must know where to go.
    /// </summary>
    public Position3 EntryPosition { get; init; } = default!;
}
```

**EntryKind enum:**

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<SpdEntryKind>))]
public enum SpdEntryKind
{
    [JsonStringEnumMemberName("talk")]
    Talk,
    [JsonStringEnumMemberName("interact")]
    Interact,
    [JsonStringEnumMemberName("proximity")]
    Proximity
}
```

**Why `ContentFinderConditionId` is required (not optional):** In the old `DutyStep`, CFC was optional for SPDs because legacy quest files omitted it. In the new design, CFC is required for two reasons: (1) BossMod activation benefits from knowing which duty is active, and (2) authoring inference needs CFC to distinguish SPD entry from a plain NPC talk. Making it required at the schema level means quest authors must supply it -- no more ambiguity.

**Why `EntryPosition` is required (not nullable):** The engine must navigate to the entry location for both initial entry and retry-after-failure. If `EntryPosition` were nullable, retry would be impossible when the player respawns at a distant aetheryte. Making it structurally required (non-nullable `Position3`) catches omissions at deserialization time.

**Why `EntryTargetId` is nullable:** The `"proximity"` entry kind has no interactable -- the player just walks to a location and the game initiates the SPD via an area trigger. For `"talk"` and `"interact"`, `EntryTargetId` is required by the validator (decision DS26).

### DS4: JSON discriminators

**Decision:**
- `DungeonTrialStep` discriminator: `"dungeon-trial"`
- `SinglePlayerDutyStep` discriminator: `"single-player-duty"`

**Alternatives considered:**
- `"duty"` for DungeonTrialStep (reusing the old discriminator). Rejected: this would silently deserialize old `DutyStep` JSON as `DungeonTrialStep`, which has different fields. A clean break forces explicit migration.
- `"spd"` for SinglePlayerDutyStep. Rejected: `"spd"` is FFXIV jargon that new quest authors may not recognize. `"single-player-duty"` is self-documenting and consistent with the kebab-case multi-word pattern used throughout the schema.

**What breaks if violated:** If `"duty"` is reused, old quest files with `Kind: "spd"` would fail silently -- the JSON deserializer would try to parse SPD fields as DungeonTrialStep and either succeed with garbage data or throw a confusing error. A distinct discriminator makes the migration failure loud and clear.

### DS5: EngineAction changes -- no changes needed

**Decision:** The existing `EngineAction.EnterDuty` and `EngineAction.EnterSinglePlayerDuty` records are kept as-is. They already carry the right fields:

```csharp
// Already exists:
public sealed record EnterDuty(uint ContentFinderConditionId, Step? Origin = null) : EngineAction;

public sealed record EnterSinglePlayerDuty(
    uint? ContentFinderConditionId,
    uint? EntryTargetId,
    Position3? EntryPosition,
    Step? Origin = null) : EngineAction;
```

**Rationale:** The action records are the engine's output to the host. They already encode the right information. The only change is that `EnterSinglePlayerDuty.ContentFinderConditionId` becomes non-nullable in practice (because the schema now requires it), but we keep the action record's field nullable to avoid breaking the action contract. The engine will always populate it from the step's required field.

**Why keep `EnterSinglePlayerDuty.EntryTargetId` and `EntryPosition`:** The host dispatch arm needs these for retry-after-failure. When the SPD fails and the player respawns, the host re-dispatches `EnterSinglePlayerDuty` with the same fields, and the engine uses `EntryPosition` for navigation and `EntryTargetId` for the entry interaction.

### DS6: Engine resolver -- ResolveDungeonTrial (extracted, no behavior change)

**Decision:** The existing `ResolveDungeonTrial` method is renamed and moved from being a sub-method of `ResolveDuty` to a top-level async pre-arm in the step dispatch chain. Its logic is unchanged:

```csharp
// In QuestEngine.cs -- new async pre-arm
if (step is DungeonTrialStep dungeonTrialStep)
{
    var action = await ResolveDungeonTrial(dungeonTrialStep, ct);
    return (action, step.Id, playerPos);
}
```

```csharp
private async Task<EngineAction> ResolveDungeonTrial(DungeonTrialStep step, CancellationToken ct)
{
    if (_dutyRunner is null)
        return new EngineAction.AwaitUser(
            "DungeonTrialStep dispatched but no IDutyRunner configured -- " +
            "install AutoDuty or complete this duty manually");

    if (_cfcResolver is null)
        return new EngineAction.AwaitUser(
            "DungeonTrialStep dispatched but no ICfcResolver configured -- " +
            "host must supply one");

    if (step.ContentFinderConditionId == 0)
        return new EngineAction.AwaitUser(
            $"DungeonTrialStep '{step.Id}' has ContentFinderConditionId == 0 -- " +
            "quest file must specify a valid CFC ID");

    var territoryType = _cfcResolver.GetTerritoryType(step.ContentFinderConditionId);
    if (territoryType is null)
        return new EngineAction.AwaitUser(
            $"ContentFinderConditionId {step.ContentFinderConditionId} could not be " +
            "resolved to a territory type -- unknown duty");

    var availResult = await _dutyRunner.IsAvailable(ct);
    if (availResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            "AutoDuty required for dungeon/trial automation. " +
            "Install AutoDuty or complete this duty manually.");

    var pathResult = await _dutyRunner.ContentHasPath(territoryType.Value, ct);
    if (pathResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            $"AutoDuty does not have a path for territory {territoryType.Value} " +
            $"(CFC {step.ContentFinderConditionId}). Complete this duty manually " +
            "or wait for AutoDuty path support.");

    return new EngineAction.EnterDuty(step.ContentFinderConditionId, Origin: step);
}
```

**Differences from the old `ResolveDungeonTrial(DutyStep)`:**
1. Parameter type changes from `DutyStep` to `DungeonTrialStep`.
2. `step.ContentFinderConditionId` is now a non-nullable `uint`, so the `null` guard is removed. The `== 0` guard remains.
3. Error messages reference `DungeonTrialStep` instead of `DutyStep(kind:duty)`.

### DS7: Engine resolver -- ResolveSinglePlayerDuty (new, with entry kind dispatch)

**Decision:** The new `ResolveSinglePlayerDuty` method is a multi-phase resolver that handles navigation + entry mechanism + BossMod activation:

```csharp
private async Task<EngineAction> ResolveSinglePlayerDuty(
    SinglePlayerDutyStep step, WorldPosition? playerPos, CancellationToken ct)
{
    // Guard 0: IQuestBattleRunner required
    if (_questBattleRunner is null)
        return new EngineAction.AwaitUser(
            "SinglePlayerDutyStep dispatched but no IQuestBattleRunner configured -- " +
            "install BossMod or complete this duty manually");

    // Guard 1: BossMod available
    var availResult = await _questBattleRunner.IsBossModAvailable(ct);
    if (availResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            "BossMod required for Single Player Duties. " +
            "Complete manually or install BossMod.");

    // Guard 2: Navigate to entry position if player is far
    var entryWorldPos = new WorldPosition(
        step.EntryPosition.X, step.EntryPosition.Y, step.EntryPosition.Z);
    var stopDist = step.StopDistance ?? DefaultStopDistance;
    if (playerPos is not null && playerPos.Value.DistanceTo(entryWorldPos) > stopDist)
        return new EngineAction.Navigate(entryWorldPos,
            new NavigationOptions(StoppingDistance: stopDist));

    // Guard 3: Player casting -> Wait
    var stateResult = await _gameState.GetPlayerState(ct);
    if (stateResult is Result<PlayerStateSnapshot>.Success { Value.Casting: true })
        return new EngineAction.Wait("player casting; deferring single-player-duty entry",
            Origin: step);

    // Guard 4: Action cooldown -> Wait
    var cooldown = CheckActionCooldown(step);
    if (cooldown is not null) return cooldown;

    // All guards passed. Build the action and fire.
    RecordActionFired(step);
    return new EngineAction.EnterSinglePlayerDuty(
        ContentFinderConditionId: step.ContentFinderConditionId,
        EntryTargetId: step.EntryTargetId,
        EntryPosition: step.EntryPosition,
        Origin: step);
}
```

**Guard order rationale:**
1. Adapter null check first -- no point reading state if we can't dispatch.
2. BossMod availability before anything else -- if BossMod is missing, no point navigating.
3. Navigation before casting/cooldown -- if the player is far away, casting/cooldown state is irrelevant.
4. Casting before cooldown -- casting is a hard blocker.
5. Cooldown last -- only matters when close and not casting.

**Why the engine does NOT branch on `EntryKind` in the resolver:** The engine's job is to navigate to the entry position and emit `EnterSinglePlayerDuty`. The *host dispatch arm* (in EngineHost) is responsible for executing the entry mechanism based on the action's `EntryTargetId` and the step's `EntryKind`. This keeps the engine testable without Dalamud -- the engine emits the same action regardless of entry kind. The entry kind affects *how the host dispatches*, not *what the engine decides*.

**Note:** `_lastResolvedStep` is NOT set in this async pre-arm (consistent with all other async pre-arms: TeleportStep, PurchaseItemStep, UseActionStep, UseEmoteStep, UseItemOnObjectStep).

### DS8: Engine resolver -- null playerPos fail-open behavior

**Decision:** When `playerPos` is null (game state read failure), the resolver skips the navigation guard and proceeds directly to the action. This is the same fail-open behavior as `ResolveInteractOrNavigate` and `ResolveUseItemOnObject`.

**Rationale:** If we can't read the player's position, blocking forever is worse than attempting the entry. If the player happens to be at the right location, it works. If not, the entry will fail and the engine retries next tick when position may be available.

### DS9: Host dispatch arm for EnterSinglePlayerDuty -- entry kind dispatch

**Decision:** The `EngineHost.DispatchAction` arm for `EnterSinglePlayerDuty` reads the step's `EntryKind` from the `Origin` property and dispatches accordingly:

```csharp
case EngineAction.EnterSinglePlayerDuty espd:
    DebounceLog(
        $"enterspd:{espd.Origin?.Id}",
        $"[EnterSinglePlayerDuty] stepId={espd.Origin?.Id ?? "(unknown)"}" +
        $" cfcId={espd.ContentFinderConditionId}" +
        $" entryTarget={espd.EntryTargetId}" +
        $" entryKind={GetEntryKind(espd)}");
    if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
        await _navigator.Stop(ct);
    _activeSpdStepId = espd.Origin?.Id;
    TryCutsceneSkipConfirm();

    // Entry mechanism dispatch based on step's EntryKind
    var entryKind = GetEntryKind(espd);
    switch (entryKind)
    {
        case SpdEntryKind.Talk:
            if (espd.EntryTargetId is { } talkNpcId)
                await _interactor.InteractWith(new NpcId(talkNpcId), ct);
            break;
        case SpdEntryKind.Interact:
            if (espd.EntryTargetId is { } objId)
                await _objectInteractor.InteractWithObject(
                    new InteractableId(objId), ct);
            break;
        case SpdEntryKind.Proximity:
            // No interaction needed -- area trigger initiates the SPD.
            // The navigate guard in the engine already positioned the player.
            break;
    }

    await _questBattleRunner.StartDuty(ct);
    await _interactor.AdvanceDialogue(ct);
    break;
```

**Helper:**
```csharp
private static SpdEntryKind GetEntryKind(EngineAction.EnterSinglePlayerDuty espd) =>
    espd.Origin is SinglePlayerDutyStep spd ? spd.EntryKind : SpdEntryKind.Talk;
```

**Why read EntryKind from Origin:** The `EngineAction.EnterSinglePlayerDuty` record does not carry `EntryKind` as a field -- adding it would widen the action record for information that is only needed by the host. The host reads it from the `Origin` step (which is always a `SinglePlayerDutyStep`). The fallback to `Talk` handles the (impossible in practice) case where `Origin` is not a `SinglePlayerDutyStep`.

**Why AdvanceDialogue after StartDuty for all entry kinds:** After the entry interaction (or proximity trigger), the game may show a YesNo prompt for SPD confirmation or a SelectString for difficulty selection. `AdvanceDialogue` handles these generically. For proximity entries, the prompt appears after the area trigger fires, so calling AdvanceDialogue is still correct (it becomes a no-op if no prompt is open).

### DS10: Host dispatch arm for EnterDuty -- no changes needed

**Decision:** The existing `EngineHost.DispatchAction` arm for `EnterDuty` remains unchanged. It already correctly resolves CFC to territory type via `ICfcResolver` and calls `IDutyRunner.StartDuty`.

### DS11: SPD cleanup tracking -- no changes needed

**Decision:** The existing `_activeSpdStepId` tracking and cleanup logic in `EngineHost` remains unchanged. When the engine advances past an SPD step (emits anything other than `EnterSinglePlayerDuty` or `Wait`), the host calls `_questBattleRunner.StopDuty()`. This works identically for the new `SinglePlayerDutyStep`.

### DS12: Duty cleanup tracking -- no changes needed

**Decision:** The existing `_activeDutyStepId` tracking and cleanup logic in `EngineHost` remains unchanged.

### DS13: Lazy dismount -- both step types are subject to lazy-dismount

**Decision:** Neither `EnterSinglePlayerDuty` nor `EnterDuty` are in the lazy-dismount exemption list. The player must be dismounted to interact with NPCs, objects, or enter instanced content.

The exemption list remains:
```csharp
is not EngineAction.Navigate
and not EngineAction.Teleport
and not EngineAction.EquipGear
and not EngineAction.EquipBestGear
and not EngineAction.RegisterGearset
and not EngineAction.Jump
```

No changes needed -- `EnterSinglePlayerDuty` and `EnterDuty` are already not exempt.

### DS14: Recording proxy -- no RecordingXxx wrapper needed

**Decision:** No recording proxy wrappers are needed for either step type. Both `EnterSinglePlayerDuty` and `EnterDuty` are write-only actions. The `action.submitted` / `action.completed` events emitted by `EngineHost.DispatchAction` already capture the dispatch. This is consistent with all other write-only adapter patterns.

### DS15: EngineTestHarness -- update RunToCompletion arms

**Decision:** The existing `case EngineAction.EnterSinglePlayerDuty:` and `case EngineAction.EnterDuty:` arms in `HarnessEngine.RunToCompletion` remain unchanged. They already dispatch correctly to `FakeQuestBattleRunner.StartDuty` and `FakeDutyRunner.StartDuty` respectively.

### DS16: SPD retry after failure -- self-contained in SinglePlayerDutyStep

**Decision:** The core design improvement: `SinglePlayerDutyStep` is self-contained for the full entry-complete-retry cycle. When the SPD fails (InstanceKind transitions from SinglePlayerDuty back to None without Expect being satisfied):

1. The engine's cursor remains on the `SinglePlayerDutyStep` (Expect is not satisfied).
2. Next tick, `ResolveSinglePlayerDuty` fires again.
3. The resolver checks navigation -- if the player respawned at a distant aetheryte, it emits `Navigate` to `EntryPosition`.
4. Once close, it emits `EnterSinglePlayerDuty` again with the same entry fields.
5. The host dispatch arm executes the entry mechanism (talk/interact/proximity) and activates BossMod.
6. The SPD starts again.

This is the **stateless retry pattern** used by every other step type. No special retry logic, no cross-step state tracking, no "remember the preceding TalkStep." The entry mechanism is encoded in the step itself.

**What breaks if violated:** If someone puts the entry interaction in a preceding TalkStep and keeps the SPD step lightweight, retry-after-failure requires the engine to somehow "go back" to the TalkStep -- which violates the engine's forward-only cursor design.

### DS17: SPD failure detection -- unchanged, uses InstanceKind + Expect

**Decision:** SPD failure detection remains unchanged:
1. Engine observes `InstanceKind` transitioning from `SinglePlayerDuty` back to `None`.
2. Engine evaluates `Expect`.
3. If Expect is not satisfied, the SPD failed. The duty retry counter increments.
4. If Expect is satisfied, the SPD succeeded. The step advances.

No changes to the engine's observation or failure counter logic.

### DS18: SPD difficulty handling -- unchanged, lives in host

**Decision:** SPD difficulty selection (Normal/Easy/VeryEasy) is handled by the host's `_interactor.AdvanceDialogue()` call and the existing `SelectYesnoResponder`/`SelectStringResponder` infrastructure. No changes needed. The `PreferredDutyDifficulty` and `DutyFailurePolicy` configuration continues to apply.

### DS19: No new adapter interfaces needed

**Decision:** No new adapter interfaces. Both step types reuse existing adapters:
- `DungeonTrialStep`: `IDutyRunner`, `ICfcResolver`
- `SinglePlayerDutyStep`: `IQuestBattleRunner`, `IInteractor` (for talk entry), `IObjectInteractor` (for interact entry), `INavigator` (for all entries)

### DS20: StepFactory update -- authoring inference

**Decision:** The `StepFactory.Build` method currently maps `"duty"` to `new DutyStep { Kind = "spd" }`. This is updated to:

```csharp
"single-player-duty" => new SinglePlayerDutyStep
{
    Id = stepId,
    Expect = expectValue,
    ContentFinderConditionId = 0,  // filled by author
    EntryKind = SpdEntryKind.Talk, // default; author adjusts
    EntryPosition = ResolveTravelPosition(before, after, playerPos, false)
},
"dungeon-trial" => new DungeonTrialStep
{
    Id = stepId,
    Expect = expectValue,
    ContentFinderConditionId = 0   // filled by author
},
```

The old `"duty"` arm is removed.

### DS21: StepInferenceEngine -- no duty inference exists today

**Decision:** The `StepInferenceEngine` does not currently have any rule for inferring duty steps. This is expected -- duty entry is complex and context-dependent. Authoring inference for SPD entry (Slice 5) will need signal research (InstanceKind transitions, BoundByDuty condition flag). This spec does not defer inference -- it explicitly states that inference is out of scope for Slice 2 and required in Slice 5.

### DS22: QuestEngine constructor -- no changes needed

**Decision:** The `QuestEngine` constructor already accepts `IQuestBattleRunner?`, `IDutyRunner?`, and `ICfcResolver?` as optional parameters. No new adapter parameters needed.

### DS23: Migration strategy for existing quest files

**Decision:** Existing quest files using `"type": "duty"` must be migrated. The migration is a mechanical find-and-replace:

For `kind: "duty"`:
```json
// Before:
{ "type": "duty", "kind": "duty", "contentFinderConditionId": 2, ... }

// After:
{ "type": "dungeon-trial", "contentFinderConditionId": 2, ... }
```

For `kind: "spd"`:
```json
// Before:
{ "type": "duty", "kind": "spd", "entryTargetId": 1045123, "entryPosition": {...}, ... }

// After:
{ "type": "single-player-duty", "contentFinderConditionId": 830,
  "entryKind": "talk", "entryTargetId": 1045123, "entryPosition": {...}, ... }
```

**Migration notes:**
- `kind` field is removed entirely (the type discriminator replaces it).
- `contentFinderConditionId` becomes required for both types. Quest authors must supply a CFC ID for SPDs that previously omitted it.
- `entryKind` must be added to all SPD steps. Quest authors determine the entry mechanism from the quest's gameplay.
- `entryPosition` becomes required for SPDs. Quest authors must supply coordinates if previously omitted.

**Migration timing:** The migration PR in `questforge-data` is paired with the Slice 2 PR in `questforge`. Both must merge together.

### DS24: Tools-repo catch-up

**Decision:** The `questforge-tools` repo must be updated in Slice 3 (per project invariant -- tooling catch-up MUST land in the same slice as Dalamud impl):

1. `CapabilityInferrer.cs`: Replace `[typeof(DutyStep)] = "step:duty"` with two entries:
   - `[typeof(DungeonTrialStep)] = "step:dungeon-trial"`
   - `[typeof(SinglePlayerDutyStep)] = "step:single-player-duty"`
   Remove the special-case `DutyStep` kind branching that emits `step:spd` / `step:dungeon-trial`.

2. `TraceToFixtureExtractor.cs`: Update `FilenameLookup` and `DistinguishingCapPriority`:
   - `(["step:dungeon-trial"], "with-dungeon.json")` replaces `(["step:duty"], "with-dungeon.json")`
   - `(["step:single-player-duty"], "with-spd.json")` is added
   - `"step:dungeon-trial"` priority replaces `"step:duty"` in fallback

3. `TraceConstants.cs`: No changes needed -- `ActionEnterSinglePlayerDuty` and `ActionEnterDuty` are already correct.

4. `StructuralValidator.cs`: `ValidateDutyStep` is replaced with `ValidateDungeonTrialStep` and `ValidateSinglePlayerDutyStep`.

5. `Step.cs` in tools-repo Schema: mirrors the main repo schema changes.

### DS25: InCombat guard for SPD entry

**Decision:** No `IsPlayerInCombat` guard in `ResolveSinglePlayerDuty`. Consistent with the existing `ResolveSpd` which has no combat guard.

**Rationale:** The player should not be in combat when approaching an SPD entry point under normal circumstances. If they are (due to proximity aggro), the casting guard will catch any active casts, and the entry interaction itself will fail and be retried next tick after combat resolves.

### DS26: Validator rules -- DungeonTrialStep

| Code | Level | Step Type | Condition | Message |
|------|-------|-----------|-----------|---------|
| E29 | Error | DungeonTrialStep | `ContentFinderConditionId == 0` | "Step '{stepId}' is a DungeonTrialStep with ContentFinderConditionId == 0." |
| W14 | Warning | DungeonTrialStep | `Expect is null` | "Step '{stepId}' is a DungeonTrialStep with no 'expect' predicate -- without it the engine will spin-loop re-dispatching EnterDuty. Add an expect predicate." |

**W1 suppression:** `DungeonTrialStep` is added to the W1 suppression guard (replaces the old `DutyStep { Kind: "duty" }` suppression):
```csharp
&& step.Raw is not DungeonTrialStep
```

### DS27: Validator rules -- SinglePlayerDutyStep

| Code | Level | Step Type | Condition | Message |
|------|-------|-----------|-----------|---------|
| E30 | Error | SinglePlayerDutyStep | `ContentFinderConditionId == 0` | "Step '{stepId}' is a SinglePlayerDutyStep with ContentFinderConditionId == 0." |
| E31 | Error | SinglePlayerDutyStep | `EntryKind is Talk or Interact` AND `EntryTargetId is null` | "Step '{stepId}' has EntryKind '{kind}' but EntryTargetId is null. Talk and Interact entry kinds require an EntryTargetId." |
| E32 | Error | SinglePlayerDutyStep | `EntryKind is Talk or Interact` AND `EntryTargetId == 0` | "Step '{stepId}' has EntryKind '{kind}' but EntryTargetId == 0. Provide a valid NPC/EventObj DataId." |
| E33 | Error | SinglePlayerDutyStep | `EntryKind is Proximity` AND `EntryTargetId is not null` | "Step '{stepId}' has EntryKind 'proximity' but EntryTargetId is set. Proximity entries use area triggers, not interactable targets." |
| W15 | Warning | SinglePlayerDutyStep | `Expect is null` | "Step '{stepId}' is a SinglePlayerDutyStep with no 'expect' predicate -- without it the engine will spin-loop re-entering the duty. Add an expect predicate." |

**W1 suppression:** `SinglePlayerDutyStep` is added to the W1 suppression guard:
```csharp
&& step.Raw is not SinglePlayerDutyStep
```

### DS28: Remove old DutyStep validator rules

**Decision:** E21, E22, and W11 are removed along with the `DutyStep` class. The old W1 suppression for `DutyStep { Kind: "duty" }` is replaced with the new `DungeonTrialStep` suppression.

### DS29: DungeonTrialStep is subject to action cooldown

**Decision:** No. The `ResolveDungeonTrial` method does NOT check action cooldown. This is consistent with the existing behavior -- `ResolveDungeonTrial` never called `CheckActionCooldown`. Dungeon entry is a one-shot action that AutoDuty handles idempotently.

### DS30: SinglePlayerDutyStep IS subject to action cooldown

**Decision:** Yes. The `ResolveSinglePlayerDuty` method checks action cooldown (Guard 4 in DS7). This prevents rapid re-entry attempts when the SPD fails and the engine retries. Without the cooldown, the engine would spam entry interactions every 250ms (the tick interval).

### DS31: SinglePlayerDutyStep casting guard

**Decision:** Yes. The `ResolveSinglePlayerDuty` method checks if the player is casting (Guard 3 in DS7). This prevents the engine from attempting SPD entry while the player is mid-cast (e.g., after a Return teleport animation).

### DS32: DungeonTrialStep does NOT check casting

**Decision:** No casting guard in `ResolveDungeonTrial`. Consistent with the existing behavior. AutoDuty handles its own entry timing.

### DS33: SPD entry kind "talk" reuse pattern

**Decision:** The `"talk"` entry kind reuses the TalkStep pattern at the host level: `_interactor.InteractWith(new NpcId(entryTargetId))`. The engine does not call `ResolveInteractOrNavigate` for the talk -- navigation is handled by the standalone Guard 2 (navigate to `EntryPosition`). The host's dispatch arm handles the interaction.

**Why not reuse the TalkStep engine resolver:** The TalkStep resolver operates on a `TalkStep` object with `Target.NpcId` and `Target.Position`. The SPD step has a different shape (`EntryTargetId` and `EntryPosition`). Trying to construct a temporary TalkStep and pass it through the resolver would be fragile and unclear. Instead, the engine handles navigation uniformly via Guard 2, and the host handles the entry interaction based on `EntryKind`.

### DS34: SPD entry kind "interact" reuse pattern

**Decision:** The `"interact"` entry kind uses `_objectInteractor.InteractWithObject(new InteractableId(entryTargetId))` at the host level. Same pattern as "talk" but with the object interactor.

### DS35: SPD entry kind "proximity" -- no interaction

**Decision:** The `"proximity"` entry kind performs no interaction at the host level. The engine navigates to `EntryPosition` (Guard 2), and the game's area trigger initiates the SPD automatically. The host calls `_questBattleRunner.StartDuty()` and `_interactor.AdvanceDialogue()` to handle any confirmation prompts.

### DS36: IObjectInteractor null guard for interact entry kind

**Decision:** When `EntryKind` is `Interact` and `_objectInteractor` is null in the host, the host logs a warning and falls through to `_questBattleRunner.StartDuty()`. This is a host-level concern, not an engine-level guard. The engine does not check `_objectInteractor` for SPD steps because the null-check logic depends on `EntryKind`, which is a host dispatch concern.

**Why not guard in the engine:** Adding an `IObjectInteractor?` null check in the engine resolver would require the engine to branch on `EntryKind` -- which violates DS7's design of keeping entry-kind dispatch in the host. The engine's contract is "emit the action; the host dispatches it." If the host can't dispatch, it fails gracefully.

### DS37: SPD entry -- host calls AdvanceDialogue after entry for all kinds

**Decision:** After executing the entry mechanism (talk/interact/nothing for proximity), the host always calls `_interactor.AdvanceDialogue()`. This handles:
- YesNo prompts for first-time Normal difficulty confirmation
- SelectString prompts for difficulty selection after previous failures
- Any quest dialogue that appears between entry and instance transition

This is a single `AdvanceDialogue` call -- not a loop. The engine's stateless retry handles cases where the prompt requires multiple advances across ticks.

### DS38: Removing `ResolveDuty` switch

**Decision:** The existing `ResolveDuty(DutyStep)` method and its routing switch are removed entirely. The step dispatch chain now has two separate arms:

```csharp
if (step is DungeonTrialStep dungeonTrialStep)
{
    var action = await ResolveDungeonTrial(dungeonTrialStep, ct);
    return (action, step.Id, playerPos);
}

if (step is SinglePlayerDutyStep spdStep)
{
    var action = await ResolveSinglePlayerDuty(spdStep, playerPos, ct);
    return (action, step.Id, playerPos);
}
```

The `ResolveSpd(DutyStep)` method is also removed. The `ResolveInteractOrNavigate`-based approach in `ResolveSinglePlayerDuty` replaces it with a purpose-built resolver.

### DS39: Remove DutyStep from the synchronous step dispatch switch

**Decision:** The existing `ResolveActionForStep` switch expression does not handle `DutyStep` (duty steps are handled in the async pre-arm block above). No changes needed to the switch expression.

### DS40: SpdEntryKind JSON serialization

**Decision:** `SpdEntryKind` uses `JsonStringEnumConverter<SpdEntryKind>` with explicit `[JsonStringEnumMemberName]` attributes for kebab-case values: `"talk"`, `"interact"`, `"proximity"`. This matches the pattern used by `PurchaseCurrency`, `CombatSpawn`, and `ItemKind`.

### DS41: EntryPosition structural enforcement

**Decision:** `EntryPosition` on `SinglePlayerDutyStep` is a non-nullable `Position3` with `= default!`. If omitted from JSON, deserialization fails with a missing-property error. No validator E-rule is needed for missing `EntryPosition` -- the schema enforces it structurally.

### DS42: Ordering in the async pre-arm chain

**Decision:** The two new arms are placed where the old `DutyStep` arm was (after `OpenCoffersStep`, before `TeleportStep`):

```csharp
// 6b0a. DungeonTrialStep async arm
if (step is DungeonTrialStep dungeonTrialStep) { ... }

// 6b0b. SinglePlayerDutyStep async arm
if (step is SinglePlayerDutyStep spdStep) { ... }

// 6b. TeleportStep async arm
if (step is TeleportStep teleportStep) { ... }
```

### DS43: Schema version bump

**Decision:** This is a **major schema change** (removing a step type, adding two new ones). Per SCHEMA.md section 9, removing a step type requires a major version bump. However, since no stable release has been published yet (the project is in pre-1.0 development), the schema version stays at `1.0.0` with an explicit note in the migration commit that this is a breaking change.

---

## 5. Validation Rule Table

### New Rules

| Code | Level | Step Type | Condition | Suppress | Message |
|------|-------|-----------|-----------|----------|---------|
| E29 | Error | DungeonTrialStep | `ContentFinderConditionId == 0` | -- | "Step '{stepId}' is a DungeonTrialStep with ContentFinderConditionId == 0." |
| E30 | Error | SinglePlayerDutyStep | `ContentFinderConditionId == 0` | -- | "Step '{stepId}' is a SinglePlayerDutyStep with ContentFinderConditionId == 0." |
| E31 | Error | SinglePlayerDutyStep | `EntryKind in (Talk, Interact)` AND `EntryTargetId is null` | -- | "Step '{stepId}' has EntryKind '{kind}' but EntryTargetId is null. Talk and Interact entry kinds require an EntryTargetId." |
| E32 | Error | SinglePlayerDutyStep | `EntryKind in (Talk, Interact)` AND `EntryTargetId == 0` | -- | "Step '{stepId}' has EntryKind '{kind}' but EntryTargetId == 0. Provide a valid NPC/EventObj DataId." |
| E33 | Error | SinglePlayerDutyStep | `EntryKind is Proximity` AND `EntryTargetId is not null` | -- | "Step '{stepId}' has EntryKind 'proximity' but EntryTargetId is set. Proximity entries use area triggers, not interactable targets." |
| W14 | Warning | DungeonTrialStep | `Expect is null` | Suppresses W1 | "Step '{stepId}' is a DungeonTrialStep with no 'expect' predicate -- without it the engine will spin-loop re-dispatching EnterDuty. Add an expect predicate." |
| W15 | Warning | SinglePlayerDutyStep | `Expect is null` | Suppresses W1 | "Step '{stepId}' is a SinglePlayerDutyStep with no 'expect' predicate -- without it the engine will spin-loop re-entering the duty. Add an expect predicate." |

### Removed Rules

| Code | Reason |
|------|--------|
| E21 | Replaced by E29 (DungeonTrialStep) |
| E22 | Replaced by E31/E32 (SinglePlayerDutyStep entry target validation is now context-aware by EntryKind) |
| W11 | Replaced by W14 (DungeonTrialStep) |

---

## 6. Task Breakdown

### Task 1: Schema (QuestForge.Schema)

**Deliverables:**

1. Add `SpdEntryKind` enum to a new file or to `Step.cs`:
```csharp
[JsonConverter(typeof(JsonStringEnumConverter<SpdEntryKind>))]
public enum SpdEntryKind
{
    [JsonStringEnumMemberName("talk")]
    Talk,
    [JsonStringEnumMemberName("interact")]
    Interact,
    [JsonStringEnumMemberName("proximity")]
    Proximity
}
```

2. Add `DungeonTrialStep` sealed class to `Step.cs`:
```csharp
public sealed class DungeonTrialStep : Step
{
    public uint ContentFinderConditionId { get; init; }
}
```

3. Add `SinglePlayerDutyStep` sealed class to `Step.cs`:
```csharp
public sealed class SinglePlayerDutyStep : Step
{
    public uint ContentFinderConditionId { get; init; }
    public SpdEntryKind EntryKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? EntryTargetId { get; init; }
    public Position3 EntryPosition { get; init; } = default!;
}
```

4. Add `[JsonDerivedType]` attributes to `Step`:
```csharp
[JsonDerivedType(typeof(DungeonTrialStep),      "dungeon-trial")]
[JsonDerivedType(typeof(SinglePlayerDutyStep),   "single-player-duty")]
```

5. Remove `[JsonDerivedType(typeof(DutyStep), "duty")]` and the `DutyStep` class.

6. Add `[JsonSerializable(typeof(DungeonTrialStep))]` and `[JsonSerializable(typeof(SinglePlayerDutyStep))]` to `QuestForgeJsonContext.cs`. Remove `[JsonSerializable(typeof(DutyStep))]`.

7. Add 4 round-trip tests in `QuestForge.Schema.Tests/RoundTripTests.cs` (see GWT section).

### Task 2: Engine (QuestForge.Engine)

**Deliverables:**

1. Remove `ResolveDuty`, `ResolveSpd` methods from `QuestEngine.cs`.
2. Add `ResolveDungeonTrial(DungeonTrialStep, CancellationToken)` method (adapted from old `ResolveDungeonTrial(DutyStep, CancellationToken)`).
3. Add `ResolveSinglePlayerDuty(SinglePlayerDutyStep, WorldPosition?, CancellationToken)` method (new).
4. Replace the `if (step is DutyStep dutyStep)` arm with two separate arms for `DungeonTrialStep` and `SinglePlayerDutyStep`.
5. Update `EngineTestHarness` if needed (existing arms should work since `EnterDuty` and `EnterSinglePlayerDuty` action types are unchanged).

### Task 3: DraftValidator (QuestForge.Engine/Authoring/DraftValidator.cs)

**Deliverables:**

1. Remove E21 rule (`DutyStep { Kind: "duty", ContentFinderConditionId: 0 }`).
2. Remove E22 rule (`DutyStep { EntryTargetId: 0 }`).
3. Remove W11 rule (`DutyStep { Kind: "duty" }` with null Expect).
4. Add E29 rule: `DungeonTrialStep.ContentFinderConditionId == 0`.
5. Add E30 rule: `SinglePlayerDutyStep.ContentFinderConditionId == 0`.
6. Add E31 rule: `SinglePlayerDutyStep.EntryKind in (Talk, Interact)` AND `EntryTargetId is null`.
7. Add E32 rule: `SinglePlayerDutyStep.EntryKind in (Talk, Interact)` AND `EntryTargetId == 0`.
8. Add E33 rule: `SinglePlayerDutyStep.EntryKind is Proximity` AND `EntryTargetId is not null`.
9. Add W14 rule: `DungeonTrialStep.Expect is null` (message contains "spin-loop").
10. Add W15 rule: `SinglePlayerDutyStep.Expect is null` (message contains "spin-loop").
11. Update W1 suppression guard: replace `DutyStep { Kind: "duty" }` with `DungeonTrialStep` and add `SinglePlayerDutyStep`.

### Task 4: Update existing tests

**Deliverables:**

1. Rewrite `DungeonTrialDutyTests.cs` to use `DungeonTrialStep` instead of `DutyStep { Kind = "duty" }`.
2. Rewrite `SpdRetryTests.cs` to use `SinglePlayerDutyStep` instead of `DutyStep { Kind = "spd" }`.
3. Update round-trip tests that reference `DutyStep`.

### Task 5: New tests

See Section 7 (Given-When-Then Specs) for all new test scenarios.

---

## 7. Given-When-Then Specs

### DungeonTrialStep Engine Tests: `DungeonTrialStepTests.cs`

---

#### DT_T1: Happy path -- CFC ID present, all checks pass, emits EnterDuty

**Given:**
- QuestSequence for quest 90001 is 0
- DungeonTrialStep with ContentFinderConditionId=2, Expect="questSequence(90001) >= 3"
- DutyRunner.AutoDutyAvailable = true
- CfcResolver.Register(2, 167)
- DutyRunner.SetContentHasPath(167, true)

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.EnterDuty`
- `ContentFinderConditionId` is 2
- `Origin.Id` is the step's ID
- DutyRunner.StartDutyCallCount is 1
- DutyRunner.LastStartedTerritoryType is 167

---

#### DT_T2: AutoDuty not available -- AwaitUser

**Given:**
- DungeonTrialStep with ContentFinderConditionId=2
- DutyRunner.AutoDutyAvailable = false

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "AutoDuty" (case-insensitive)

---

#### DT_T3: IDutyRunner not wired (null) -- AwaitUser

**Given:**
- QuestEngine constructed with dutyRunner: null
- DungeonTrialStep with ContentFinderConditionId=2

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "IDutyRunner" (case-insensitive)

---

#### DT_T4: ICfcResolver not wired (null) -- AwaitUser

**Given:**
- QuestEngine constructed with cfcResolver: null
- DungeonTrialStep with ContentFinderConditionId=2
- DutyRunner.AutoDutyAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "ICfcResolver" (case-insensitive)

---

#### DT_T5: Unknown CFC ID (resolver returns null) -- AwaitUser

**Given:**
- DungeonTrialStep with ContentFinderConditionId=9999
- DutyRunner.AutoDutyAvailable = true
- CFC 9999 is NOT registered in CfcResolver

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "could not be resolved to a territory type" (case-insensitive)

---

#### DT_T6: ContentHasPath false -- AwaitUser

**Given:**
- DungeonTrialStep with ContentFinderConditionId=2
- DutyRunner.AutoDutyAvailable = true
- CfcResolver.Register(2, 167)
- DutyRunner.SetContentHasPath(167, false)

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "does not have a path for territory" (case-insensitive)

---

#### DT_T7: ContentFinderConditionId == 0 -- AwaitUser (runtime guard)

**Given:**
- DungeonTrialStep with ContentFinderConditionId=0
- DutyRunner.AutoDutyAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "ContentFinderConditionId == 0"

---

#### DT_T8: Expect already satisfied -- step skipped

**Given:**
- QuestSequence for quest 90008 is 3 (Expect already true)
- DungeonTrialStep with ContentFinderConditionId=2, Expect="questSequence(90008) >= 3"

**When:** Engine.Tick()

**Then:**
- Action is NOT `EngineAction.EnterDuty`
- DutyRunner.StartDutyCallCount is 0

---

#### DT_T9: Retry -- re-dispatches EnterDuty until Expect satisfied

**Given:**
- QuestSequence for quest 90009 is 0
- DungeonTrialStep with Expect="questSequence(90009) >= 3"
- All duty prerequisites met

**When:** Tick 3 times (Expect stays false), then set QuestSequence to 3, then tick again

**Then:**
- First 3 ticks: `EngineAction.EnterDuty`
- After QuestSequence = 3: next tick is NOT `EnterDuty` (step advances)
- DutyRunner.StartDutyCallCount >= 3

---

#### DT_T10: Lazy dismount fires (EnterDuty is NOT exempt)

**Given:**
- Player is mounted
- TravelStep (Expect satisfied) precedes DungeonTrialStep
- Last action was Navigate

**When:** Engine.Tick() returns EnterDuty

**Then:**
- Mount.DismountCallCount >= 1

---

#### DT_T11: Cancellation propagates

**Given:**
- DungeonTrialStep with all prerequisites met
- CancellationToken is already cancelled

**When:** Engine.Tick(cancelledToken)

**Then:**
- `OperationCanceledException` is thrown

---

### SinglePlayerDutyStep Engine Tests: `SinglePlayerDutyStepTests.cs`

---

#### SPD_T1: Happy path -- entry kind "talk", player close, emits EnterSinglePlayerDuty

**Given:**
- Player position is (10, 0, 10) in zone 132
- QuestSequence for quest 91001 is 0
- SinglePlayerDutyStep with:
  - ContentFinderConditionId=830
  - EntryKind=Talk
  - EntryTargetId=1045123
  - EntryPosition=(10, 0, 10)
  - Expect="questSequence(91001) >= 3"
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.EnterSinglePlayerDuty`
- ContentFinderConditionId is 830
- EntryTargetId is 1045123
- EntryPosition matches (10, 0, 10)
- Origin.Id is the step's ID

---

#### SPD_T2: Happy path -- entry kind "interact", player close, emits EnterSinglePlayerDuty

**Given:**
- Player position is (10, 0, 10) in zone 132
- SinglePlayerDutyStep with:
  - ContentFinderConditionId=831
  - EntryKind=Interact
  - EntryTargetId=2001234
  - EntryPosition=(10, 0, 10)
  - Expect="questSequence(91002) >= 3"
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.EnterSinglePlayerDuty`
- ContentFinderConditionId is 831
- EntryTargetId is 2001234

---

#### SPD_T3: Happy path -- entry kind "proximity", no EntryTargetId, emits EnterSinglePlayerDuty

**Given:**
- Player position is (10, 0, 10) in zone 132
- SinglePlayerDutyStep with:
  - ContentFinderConditionId=832
  - EntryKind=Proximity
  - EntryTargetId=null
  - EntryPosition=(10, 0, 10)
  - Expect="questSequence(91003) >= 3"
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.EnterSinglePlayerDuty`
- ContentFinderConditionId is 832
- EntryTargetId is null

---

#### SPD_T4: Player far from entry position -- Navigate emitted first

**Given:**
- Player position is (0, 0, 0) in zone 132
- SinglePlayerDutyStep with EntryPosition=(100, 0, 100), EntryKind=Talk
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Navigate`
- Destination is approximately (100, 0, 100)

---

#### SPD_T5: Player close with custom StopDistance honored

**Given:**
- Player position is (10, 0, 10) in zone 132
- SinglePlayerDutyStep with EntryPosition=(14, 0, 10), StopDistance=5.0
- Distance is 4.0 (within StopDistance)
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.EnterSinglePlayerDuty` (NOT Navigate)

---

#### SPD_T6: Player casting -- Wait

**Given:**
- Player position is (10, 0, 10) in zone 132 (close to entry)
- Player is casting (GameState.Casting = true)
- SinglePlayerDutyStep with EntryPosition=(10, 0, 10)
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Wait`
- Reason contains "casting"

---

#### SPD_T7: Action cooldown active -- Wait

**Given:**
- Player position is (10, 0, 10) (close to entry)
- Player is NOT casting
- SinglePlayerDutyStep with EntryPosition=(10, 0, 10)
- QuestBattleRunner.BossModAvailable = true
- Action cooldown has not elapsed

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Wait`
- Reason contains "cooldown"

---

#### SPD_T8: BossMod not available -- AwaitUser

**Given:**
- SinglePlayerDutyStep with all fields populated
- QuestBattleRunner.BossModAvailable = false

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "BossMod" (case-insensitive)

---

#### SPD_T9: IQuestBattleRunner not wired (null) -- AwaitUser

**Given:**
- QuestEngine constructed with questBattleRunner: null
- SinglePlayerDutyStep with all fields populated

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.AwaitUser`
- Reason contains "IQuestBattleRunner" (case-insensitive)

---

#### SPD_T10: Expect already satisfied -- step skipped

**Given:**
- QuestSequence for quest 91010 is 3 (Expect already true)
- SinglePlayerDutyStep with Expect="questSequence(91010) >= 3"

**When:** Engine.Tick()

**Then:**
- Action is NOT `EngineAction.EnterSinglePlayerDuty`

---

#### SPD_T11: Two-tick integration: SPD fires, Expect satisfies, step completes

**Given:**
- Player position close to entry
- SinglePlayerDutyStep with Expect="questSequence(91011) >= 3"
- QuestBattleRunner.BossModAvailable = true

**When:**
- Tick 1: emits EnterSinglePlayerDuty
- Between ticks: set QuestSequence to 3
- Tick 2: Expect satisfied

**Then:**
- Tick 1 returns `EngineAction.EnterSinglePlayerDuty`
- Tick 2 returns an action for the NEXT step (not EnterSinglePlayerDuty again)

---

#### SPD_T12: Retry -- re-dispatches with same entry fields across multiple ticks

**Given:**
- Player position close to entry
- SinglePlayerDutyStep with EntryKind=Talk, EntryTargetId=5000, EntryPosition=(10, 0, 20)
- QuestBattleRunner.BossModAvailable = true

**When:** Tick 3 times (Expect stays false)

**Then:**
- All 3 ticks return `EngineAction.EnterSinglePlayerDuty`
- All 3 actions have identical ContentFinderConditionId, EntryTargetId, EntryPosition

---

#### SPD_T13: Retry after respawn -- Navigate emitted to EntryPosition

**Given:**
- Player position is (200, 0, 200) (far from entry -- simulating respawn)
- SinglePlayerDutyStep with EntryPosition=(10, 0, 20)
- QuestBattleRunner.BossModAvailable = true
- Expect is NOT satisfied (SPD failed)

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Navigate`
- Destination is approximately (10, 0, 20)

---

#### SPD_T14: Null playerPos -- fail-open, emits EnterSinglePlayerDuty

**Given:**
- Player position is null (game state read failure)
- SinglePlayerDutyStep with all fields populated
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.EnterSinglePlayerDuty` (not Navigate)

---

#### SPD_T15: Cancellation propagates

**Given:**
- SinglePlayerDutyStep with all prerequisites met
- CancellationToken is already cancelled

**When:** Engine.Tick(cancelledToken)

**Then:**
- `OperationCanceledException` is thrown

---

#### SPD_T16: Lazy dismount fires after Navigate

**Given:**
- Player is mounted
- TravelStep precedes SinglePlayerDutyStep
- Player far from entry (first tick: Navigate from TravelStep)
- TravelStep Expect satisfies, then SinglePlayerDutyStep fires

**When:** Engine transitions from Navigate to EnterSinglePlayerDuty

**Then:**
- Mount.DismountCallCount >= 1

---

#### SPD_T17: ContentFinderConditionId value carried through to action

**Given:**
- SinglePlayerDutyStep with ContentFinderConditionId=999
- QuestBattleRunner.BossModAvailable = true
- Player close to entry

**When:** Engine.Tick()

**Then:**
- `EnterSinglePlayerDuty.ContentFinderConditionId` is 999

---

#### SPD_T18: Navigate uses default StopDistance (3.0) when not overridden

**Given:**
- Player position is (0, 0, 0)
- SinglePlayerDutyStep with EntryPosition=(10, 0, 0), StopDistance=null
- Distance is 10.0 (> default 3.0)
- QuestBattleRunner.BossModAvailable = true

**When:** Engine.Tick()

**Then:**
- Action is `EngineAction.Navigate`
- `Options.StoppingDistance` is 3.0f

---

---

### DungeonTrialStep Validator Tests: `DraftValidatorDungeonTrialTests.cs`

---

#### DTV_T1: E29 -- ContentFinderConditionId == 0

**Given:**
- QuestDraft with a single DungeonTrialStep where ContentFinderConditionId = 0

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E29"
- Message contains "ContentFinderConditionId == 0"

---

#### DTV_T2: W14 -- no Expect, spin-loop warning

**Given:**
- QuestDraft with a single DungeonTrialStep where Expect is null

**When:** DraftValidator.Validate(draft)

**Then:**
- Warnings contains exactly one entry with Code == "W14"
- Message contains "spin-loop"
- W1 is NOT present in warnings

---

#### DTV_T3: Valid DungeonTrialStep -- no errors or warnings

**Given:**
- QuestDraft with a DungeonTrialStep where ContentFinderConditionId = 2, Expect = "questSequence(90001) >= 3"

**When:** DraftValidator.Validate(draft)

**Then:**
- No errors with code E29
- No warnings with code W14
- No W1 warning

---

### SinglePlayerDutyStep Validator Tests: `DraftValidatorSinglePlayerDutyTests.cs`

---

#### SPDV_T1: E30 -- ContentFinderConditionId == 0

**Given:**
- QuestDraft with a SinglePlayerDutyStep where ContentFinderConditionId = 0

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E30"
- Message contains "ContentFinderConditionId == 0"

---

#### SPDV_T2: E31 -- EntryKind is Talk, EntryTargetId is null

**Given:**
- QuestDraft with a SinglePlayerDutyStep where EntryKind = Talk, EntryTargetId = null

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E31"
- Message contains "EntryTargetId is null"

---

#### SPDV_T3: E31 -- EntryKind is Interact, EntryTargetId is null

**Given:**
- QuestDraft with a SinglePlayerDutyStep where EntryKind = Interact, EntryTargetId = null

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E31"
- Message contains "EntryTargetId is null"

---

#### SPDV_T4: E32 -- EntryKind is Talk, EntryTargetId == 0

**Given:**
- QuestDraft with a SinglePlayerDutyStep where EntryKind = Talk, EntryTargetId = 0

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E32"
- Message contains "EntryTargetId == 0"

---

#### SPDV_T5: E32 -- EntryKind is Interact, EntryTargetId == 0

**Given:**
- QuestDraft with a SinglePlayerDutyStep where EntryKind = Interact, EntryTargetId = 0

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E32"
- Message contains "EntryTargetId == 0"

---

#### SPDV_T6: E33 -- EntryKind is Proximity, EntryTargetId is set

**Given:**
- QuestDraft with a SinglePlayerDutyStep where EntryKind = Proximity, EntryTargetId = 5000

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contains exactly one entry with Code == "E33"
- Message contains "Proximity" and "EntryTargetId is set"

---

#### SPDV_T7: W15 -- no Expect, spin-loop warning

**Given:**
- QuestDraft with a SinglePlayerDutyStep where Expect is null

**When:** DraftValidator.Validate(draft)

**Then:**
- Warnings contains exactly one entry with Code == "W15"
- Message contains "spin-loop"
- W1 is NOT present in warnings

---

#### SPDV_T8: Valid SinglePlayerDutyStep (talk) -- no errors or warnings

**Given:**
- QuestDraft with a SinglePlayerDutyStep where:
  - ContentFinderConditionId = 830
  - EntryKind = Talk
  - EntryTargetId = 1045123
  - Expect = "questSequence(91001) >= 3"

**When:** DraftValidator.Validate(draft)

**Then:**
- No errors with codes E30-E33
- No warnings with code W15

---

#### SPDV_T9: Valid SinglePlayerDutyStep (proximity, null target) -- no errors

**Given:**
- QuestDraft with a SinglePlayerDutyStep where:
  - ContentFinderConditionId = 832
  - EntryKind = Proximity
  - EntryTargetId = null
  - Expect = "questSequence(91009) >= 3"

**When:** DraftValidator.Validate(draft)

**Then:**
- No errors with codes E30-E33

---

#### SPDV_T10: Multiple errors -- E30 and E31 can fire simultaneously

**Given:**
- QuestDraft with a SinglePlayerDutyStep where:
  - ContentFinderConditionId = 0
  - EntryKind = Talk
  - EntryTargetId = null

**When:** DraftValidator.Validate(draft)

**Then:**
- Errors contain both E30 (CFC == 0) and E31 (EntryTargetId null)

---

### Schema Round-Trip Tests: `RoundTripTests.cs`

---

#### RT_DT1: DungeonTrialStep round-trips with all fields

**Given:**
- DungeonTrialStep with Id="enter-dungeon", ContentFinderConditionId=2, Expect="questSequence(90001) >= 3"

**When:** Serialize to JSON as Step, then deserialize back

**Then:**
- Result is `DungeonTrialStep`
- ContentFinderConditionId is 2
- JSON contains `"type":"dungeon-trial"`

---

#### RT_SPD1: SinglePlayerDutyStep round-trips -- talk entry kind

**Given:**
- SinglePlayerDutyStep with:
  - Id="enter-spd-talk"
  - ContentFinderConditionId=830
  - EntryKind=Talk
  - EntryTargetId=1045123
  - EntryPosition=(100, 20, -50)
  - Expect="questSequence(91001) >= 3"

**When:** Serialize to JSON as Step, then deserialize back

**Then:**
- Result is `SinglePlayerDutyStep`
- All field values preserved
- JSON contains `"type":"single-player-duty"`
- JSON contains `"entryKind":"talk"`

---

#### RT_SPD2: SinglePlayerDutyStep round-trips -- interact entry kind

**Given:**
- SinglePlayerDutyStep with EntryKind=Interact, EntryTargetId=2001234

**When:** Round-trip

**Then:**
- `EntryKind` is `SpdEntryKind.Interact`
- JSON contains `"entryKind":"interact"`

---

#### RT_SPD3: SinglePlayerDutyStep round-trips -- proximity entry kind, null target

**Given:**
- SinglePlayerDutyStep with EntryKind=Proximity, EntryTargetId=null

**When:** Round-trip

**Then:**
- `EntryKind` is `SpdEntryKind.Proximity`
- `EntryTargetId` is null
- JSON does NOT contain `"entryTargetId"` (due to `WhenWritingNull`)

---

#### RT_DT2: Old DutyStep discriminator "duty" fails deserialization

**Given:**
- JSON string `{"type":"duty","id":"old-step","kind":"spd"}`

**When:** Deserialize as Step

**Then:**
- Throws `JsonException` or returns null/error (the `"duty"` discriminator is no longer registered)

---

---

## 8. Implementation Order

### Phase A: Schema changes (est. 1 hour)

1. Add `SpdEntryKind` enum
2. Add `DungeonTrialStep` class
3. Add `SinglePlayerDutyStep` class
4. Add `[JsonDerivedType]` attributes
5. Remove `DutyStep` class and its `[JsonDerivedType]`
6. Update `QuestForgeJsonContext.cs`
7. Add 4 round-trip tests (RT_DT1, RT_SPD1, RT_SPD2, RT_SPD3) + regression test RT_DT2

**Done before Phase B:** `dotnet test QuestForge.Schema.Tests` passes with the new round-trip tests. Build may fail for Engine.Tests due to DutyStep references -- that is expected and fixed in Phase B.

### Phase B: Engine resolvers (est. 2 hours)

1. Replace `ResolveDuty` / `ResolveSpd` / `ResolveDungeonTrial(DutyStep)` with `ResolveDungeonTrial(DungeonTrialStep)` and `ResolveSinglePlayerDuty(SinglePlayerDutyStep)`
2. Update the async pre-arm chain
3. Fix all compilation errors from DutyStep removal (test files, harness)

**Done before Phase C:** `dotnet build QuestForge.Engine` and `dotnet build QuestForge.Engine.Tests` succeed.

### Phase C: Update existing tests (est. 1.5 hours)

1. Rewrite `DungeonTrialDutyTests.cs` to use `DungeonTrialStep`
2. Rewrite `SpdRetryTests.cs` to use `SinglePlayerDutyStep`
3. Fix any other test references to `DutyStep`
4. All existing tests pass

**Done before Phase D:** `dotnet test QuestForge.Engine.Tests` -- all existing tests green.

### Phase D: New engine tests (est. 2.5 hours)

1. Create `DungeonTrialStepTests.cs` with DT_T1 through DT_T11
2. Create `SinglePlayerDutyStepTests.cs` with SPD_T1 through SPD_T18
3. All new engine tests pass

**Done before Phase E:** `dotnet test QuestForge.Engine.Tests --filter "DungeonTrialStepTests|SinglePlayerDutyStepTests"` -- all green.

### Phase E: Validator rules + tests (est. 1.5 hours)

1. Remove E21, E22, W11 rules
2. Add E29, E30, E31, E32, E33, W14, W15 rules
3. Update W1 suppression guard
4. Create `DraftValidatorDungeonTrialTests.cs` with DTV_T1 through DTV_T3
5. Create `DraftValidatorSinglePlayerDutyTests.cs` with SPDV_T1 through SPDV_T10
6. All validator tests pass

**Done before complete:** `dotnet test QuestForge.Engine.Tests --filter "DraftValidatorDungeonTrial|DraftValidatorSinglePlayerDuty"` -- all green.

---

## 9. Done Criteria

1. `dotnet build` succeeds for all projects (Schema, Engine, Engine.Tests, Schema.Tests, Adapters, Adapters.Fakes).
2. `DutyStep` class does not exist in `QuestForge.Schema/Step.cs`.
3. `DungeonTrialStep` and `SinglePlayerDutyStep` classes exist with correct field types and `[JsonDerivedType]` registrations.
4. `SpdEntryKind` enum exists with `Talk`, `Interact`, `Proximity` values and JSON serialization attributes.
5. `dotnet test QuestForge.Schema.Tests` passes -- all round-trip tests green, including DungeonTrialStep, SinglePlayerDutyStep x3, and DutyStep-rejection regression test.
6. `dotnet test QuestForge.Engine.Tests --filter "DungeonTrialStepTests"` passes -- 11 engine tests green.
7. `dotnet test QuestForge.Engine.Tests --filter "SinglePlayerDutyStepTests"` passes -- 18 engine tests green.
8. `dotnet test QuestForge.Engine.Tests --filter "DraftValidatorDungeonTrial"` passes -- 3 validator tests green.
9. `dotnet test QuestForge.Engine.Tests --filter "DraftValidatorSinglePlayerDuty"` passes -- 10 validator tests green.
10. `dotnet test QuestForge.Engine.Tests` passes -- all existing tests still pass (no regressions).
11. E29 fires on DungeonTrialStep.ContentFinderConditionId == 0.
12. E30 fires on SinglePlayerDutyStep.ContentFinderConditionId == 0.
13. E31 fires on (Talk or Interact) with null EntryTargetId.
14. E32 fires on (Talk or Interact) with EntryTargetId == 0.
15. E33 fires on Proximity with non-null EntryTargetId.
16. W14 fires on DungeonTrialStep with null Expect, message contains "spin-loop"; W1 does NOT fire.
17. W15 fires on SinglePlayerDutyStep with null Expect, message contains "spin-loop"; W1 does NOT fire.
18. JSON serialized DungeonTrialStep contains `"type":"dungeon-trial"`.
19. JSON serialized SinglePlayerDutyStep contains `"type":"single-player-duty"` and `"entryKind":"talk"` (or `"interact"` / `"proximity"`).
20. Old `"type":"duty"` JSON fails deserialization.

---

## 10. Exclusions

This spec explicitly does NOT include:

1. **EngineHost dispatch arm changes** -- Slice 3 (Dalamud impl). The engine emits the same `EnterDuty` and `EnterSinglePlayerDuty` actions; the host dispatch arm changes for entry-kind dispatch are a Slice 3 deliverable.
2. **Dalamud adapter implementation changes** -- Slice 3.
3. **Authoring inference for duty entry** -- Slice 5. Signal research for InstanceKind transitions and BoundByDuty condition flags is deferred.
4. **Tools-repo catch-up** -- Slice 3 (must land in the same slice as Dalamud impl per project invariant).
5. **In-game smoke test** -- Slice 4.
6. **Quest data migration** -- paired with Slice 2 PR in questforge-data, but the data-repo PR is a separate deliverable.
7. **DutyFallbackPolicy per-step field** -- not implemented, not carried forward. Plugin-level configuration only.
8. **SPD difficulty selection per-step override** -- not in v1. Plugin-level `PreferredDutyDifficulty` applies globally.
9. **Multi-SPD-in-one-step** -- each SPD is one `SinglePlayerDutyStep`. No batching.
10. **Proximity entry type auto-detection in authoring** -- proximity vs talk/interact is determined by the quest author based on gameplay knowledge. No auto-inference in v1.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in Section 7.
- Happy paths: 14 scenarios (DT_T1, DT_T8, DT_T9, SPD_T1, SPD_T2, SPD_T3, SPD_T5, SPD_T11, SPD_T17, DTV_T3, SPDV_T8, SPDV_T9, RT_DT1, RT_SPD1)
- Edge cases: 13 scenarios (DT_T10, SPD_T4, SPD_T12, SPD_T13, SPD_T14, SPD_T16, SPD_T18, RT_SPD2, RT_SPD3, RT_DT2, SPDV_T6, SPDV_T10, SPD_T10)
- Error cases: 15 scenarios (DT_T2, DT_T3, DT_T4, DT_T5, DT_T6, DT_T7, DT_T11, SPD_T6, SPD_T7, SPD_T8, SPD_T9, SPD_T15, DTV_T1, SPDV_T1, SPDV_T4)
- Warning cases: 5 scenarios (DTV_T2, SPDV_T2, SPDV_T3, SPDV_T5, SPDV_T7)
- Expected total: ~47 tests in QuestForge.Engine.Tests + QuestForge.Schema.Tests
