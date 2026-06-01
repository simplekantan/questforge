# Duty (Dungeon/Trial) Support via AutoDuty Delegation

**Status:** ready to implement
**Input docs:** Issue #7, docs/SINGLE_PLAYER_DUTY_PLAN.md, docs/ADAPTERS.md, docs/SCHEMA.md, AutoDuty IPCProvider.cs, Questionable AutoDutyIpc.cs, Questionable TerritoryData.cs
**Output:** `DutyStep(kind: "duty")` runs end-to-end against fakes; AutoDuty handles dungeon/trial queueing, navigation, combat, and loot in Support mode; tooling catch-up lands in the same slice.
**Phase dependencies:** Phase 11 (corpus expansion) -- builds on existing `DutyStep` schema and `IQuestBattleRunner` SPD infrastructure. SPD support (#138) must be merged first.

---

## Dependency graph

```
Slice 2 -- Engine + schema + adapter + validator + fakes + tests (single PR, questforge repo)
  Schema:    DutyStep gets ContentFinderConditionId field
  Adapter:   IDutyRunner (new, in QuestForge.Adapters/Duty/)
  Fake:      FakeDutyRunner (new, in QuestForge.Adapters.Fakes/Duty/)
  Engine:    EngineAction.EnterDuty + ResolveDungeonTrial pre-arm + DutyStep(kind:"duty") dispatch
  Validator: E-rules for ContentFinderConditionId on DutyStep(kind:"duty"); W-rule for missing Expect
  Tests:     QuestForge.Engine.Tests/Engine/DungeonTrialDutyTests.cs
             QuestForge.Engine.Tests/Authoring/DraftValidatorDutyTests.cs

Slice 3 -- Dalamud impl + EngineHost dispatch + tooling catch-up (paired PRs)
  questforge repo:
    DalamudDutyRunner (AutoDuty IPC + Lumina CFC->territory mapping)
    EngineHost: field, ctor, BeginRun, DispatchAction arm, _activeDutyStepId tracking
    /qf debug duty-info command (bonus)

  questforge-tools repo:
    TraceConstants.ActionEnterDuty
    CapabilityInferrer: DutyStep(kind:"duty") emits step:dungeon-trial
    FilenameLookup / DistinguishingCapPriority: step:dungeon-trial entries
    FIXTURES.md update
```

---

## Architectural decisions

### AD1: New `ContentFinderConditionId` field on `DutyStep`

```csharp
public class DutyStep : Step
{
    public string Kind { get; init; } = default!;   // "regular" | "spd" | "duty"
    public uint? DutyId { get; init; }
    public NpcLocation? EntryNpc { get; init; }
    public DutyTrigger? Trigger { get; init; }
    public string? FallbackOverride { get; init; }

    /// <summary>
    /// ContentFinderCondition row ID (Lumina). Required for kind "duty".
    /// The adapter derives territory type from this ID for AutoDuty's IPC.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ContentFinderConditionId { get; init; }
}
```

**Why `uint?` not `uint`:** SPD and the legacy `"regular"` kind do not use CFC IDs. Making it nullable prevents breaking existing DutyStep schemas. The validator enforces non-null for `kind: "duty"`.

**What breaks if violated:** If ContentFinderConditionId were a required `uint`, all existing SPD quest files with DutySteps would fail deserialization.

**Rejected alternative:** A separate `DungeonTrialStep` class. Rejected because `DutyStep` already exists with a `Kind` discriminator, and all three kinds share the same `Expect`-based completion model. A third kind is simpler than a third class.

### AD2: Kind value is `"duty"` (not `"regular"` or `"dungeon"`)

The existing DutyStep schema has `Kind` accepting `"regular"` and `"spd"`. The new kind value is `"duty"`.

**Why not `"regular"`:** The `"regular"` kind was designed before SPD shipped. It has its own field requirements (`dutyId`, `entryNpc`, `trigger`) that do not apply to AutoDuty delegation. Reusing `"regular"` would require backward-incompatible changes to the structural validator rules. A fresh kind value avoids this.

**Why not `"dungeon"` or `"trial"`:** Dungeons and trials use the exact same delegation mechanism (AutoDuty + Support mode). Splitting by content type adds schema complexity with zero engine benefit. If a future content type needs different handling, it gets a new kind at that point.

**Structural validator impact:** The existing `ValidateDutyStep` in `StructuralValidator.cs` checks `"regular"` and `"spd"`. A new branch for `"duty"` is added:
- `kind: "duty"` requires `ContentFinderConditionId` (non-null, non-zero).
- `kind: "duty"` must NOT have `DutyId`, `EntryNpc`, or `Trigger` set.

### AD3: New `EngineAction.EnterDuty`

```csharp
public sealed record EnterDuty(
    uint ContentFinderConditionId,
    Step? Origin = null) : EngineAction;
```

A distinct action type from `EnterSinglePlayerDuty` because:
- EngineHost dispatch is materially different (AutoDuty IPC vs BossMod IPC).
- The action carries `ContentFinderConditionId` (SPD does not).
- The cleanup semantics differ (AutoDuty.Stop vs BossMod preset clear).

**Rejected alternative:** Extend `EnterSinglePlayerDuty` with an optional CFC ID and route by its presence. Rejected because the EngineHost arms are completely different adapter calls. Merging them would add a `kind` check inside the dispatch arm, violating the pattern where each adapter gets its own action type.

**What breaks if violated:** If `EnterDuty` and `EnterSinglePlayerDuty` were merged, adding a new duty delegation target (e.g., a future AutoDuty competitor) would require yet more conditionals in a single dispatch arm rather than a clean new action type.

### AD4: `IDutyRunner` adapter interface -- focused on AutoDuty

```csharp
namespace QuestForge.Adapters.Duty;

public interface IDutyRunner
{
    /// <summary>
    /// Configure AutoDuty for Support mode and start the duty.
    /// </summary>
    /// <param name="territoryType">
    /// Territory type ID derived from ContentFinderConditionId via Lumina.
    /// AutoDuty IPC accepts territory type, not CFC ID.
    /// </param>
    Task<Result<bool>> StartDuty(uint territoryType, CancellationToken ct);

    /// <summary>
    /// Stop AutoDuty's current run. Idempotent -- safe to call when not started.
    /// </summary>
    Task<Result<bool>> StopDuty(CancellationToken ct);

    /// <summary>
    /// Check whether AutoDuty is installed and its IPC is responsive.
    /// </summary>
    Task<Result<bool>> IsAvailable(CancellationToken ct);

    /// <summary>
    /// Check whether AutoDuty has a navigation path for the given territory.
    /// </summary>
    Task<Result<bool>> ContentHasPath(uint territoryType, CancellationToken ct);
}
```

**Why `territoryType` in the interface, not `cfcId`:** AutoDuty's IPC accepts territory type exclusively. The CFC-to-territory conversion is a Lumina lookup that belongs in the Dalamud adapter (or a thin helper the adapter calls). The engine does not have Lumina access. The engine pre-arm must hand the territory type to the adapter. See AD7 for the conversion design.

**Rejected alternative A:** Put `cfcId` in the interface and let the adapter convert internally. Rejected because the engine also needs the territory type for its own pre-arm checks (`ContentHasPath`), so it would call the adapter twice for two different IDs without being able to verify the mapping is consistent.

**Rejected alternative B:** Extend `IQuestBattleRunner` with AutoDuty methods. Rejected because `IQuestBattleRunner` wraps BossMod IPC (preset management, quest battle config). AutoDuty is a completely different plugin with different IPC surface. Combining them violates SRP. The naming is clear: `IQuestBattleRunner` is for quest battles (SPDs via BossMod); `IDutyRunner` is for instanced duties (dungeons/trials via AutoDuty).

**Testability:** `FakeDutyRunner` scripts responses for each method. Tests verify the engine correctly gates on `IsAvailable`, `ContentHasPath`, and emits `EnterDuty` only when both pass.

### AD5: CFC-to-TerritoryType conversion via `ICfcResolver`

```csharp
namespace QuestForge.Adapters.Duty;

/// <summary>
/// Resolves ContentFinderCondition IDs to territory type IDs.
/// Dalamud impl uses Lumina; fake impl uses a scripted dictionary.
/// </summary>
public interface ICfcResolver
{
    /// <summary>
    /// Returns the territory type for a CFC ID, or null if the CFC ID is unknown.
    /// </summary>
    uint? GetTerritoryType(uint contentFinderConditionId);
}
```

**Why a separate interface:** The engine needs the territory type to call `IDutyRunner.ContentHasPath` and `IDutyRunner.StartDuty`. Lumina access is Dalamud-only. An interface lets the engine remain Dalamud-free while still performing the conversion.

**Dalamud implementation:**
```csharp
namespace QuestForge.Adapters.Dalamud.Duty;

public sealed class LuminaCfcResolver : ICfcResolver
{
    private readonly IDataManager _dataManager;

    public LuminaCfcResolver(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public uint? GetTerritoryType(uint contentFinderConditionId)
    {
        var cfc = _dataManager.GetExcelSheet<ContentFinderCondition>()
            .GetRowOrDefault(contentFinderConditionId);
        return cfc?.TerritoryType.RowId is > 0 ? cfc.Value.TerritoryType.RowId : null;
    }
}
```

**Fake implementation:**
```csharp
namespace QuestForge.Adapters.Fakes.Duty;

public sealed class FakeCfcResolver : ICfcResolver
{
    private readonly Dictionary<uint, uint> _map = new();

    public void Register(uint cfcId, uint territoryType) => _map[cfcId] = territoryType;

    public uint? GetTerritoryType(uint contentFinderConditionId) =>
        _map.TryGetValue(contentFinderConditionId, out var tt) ? tt : null;
}
```

**Rejected alternative:** Bake the CFC lookup into `IDutyRunner.StartDuty(cfcId)` and let the adapter convert internally. Rejected because:
1. The engine also needs territory type for `ContentHasPath`, so the conversion must happen engine-side anyway.
2. Keeping the conversion explicit makes test setup clearer (the test registers the CFC mapping rather than the fake magically knowing it).

**What breaks if violated:** If the conversion is hidden inside the adapter, tests cannot verify that the engine correctly handles an unknown CFC ID (no Lumina row) -- the fake would need to simulate Lumina internally rather than exposing a simple map.

### AD6: Optional `IDutyRunner` and `ICfcResolver` on QuestEngine constructor

```csharp
// In QuestEngine constructor:
private readonly IDutyRunner? _dutyRunner;
private readonly ICfcResolver? _cfcResolver;

public QuestEngine(
    // ... existing params ...,
    IQuestBattleRunner? questBattleRunner = null,
    IDutyRunner? dutyRunner = null,
    ICfcResolver? cfcResolver = null)
{
    _dutyRunner = dutyRunner;
    _cfcResolver = cfcResolver;
    // ...
}
```

When either `_dutyRunner` or `_cfcResolver` is null and the engine encounters a `DutyStep(kind: "duty")`, it emits `AwaitUser("IDutyRunner not configured")` or `AwaitUser("ICfcResolver not configured")`. This matches the established pattern for optional adapters.

**What breaks if violated:** Old tests that don't exercise dungeons/trials would fail if IDutyRunner or ICfcResolver were required.

### AD7: `ResolveDungeonTrial` pre-arm pattern

```csharp
private async Task<EngineAction> ResolveDungeonTrial(DutyStep step, CancellationToken ct)
{
    // Guard 1: adapter not wired
    if (_dutyRunner is null)
        return new EngineAction.AwaitUser(
            "DutyStep(kind:duty) dispatched but no IDutyRunner configured — " +
            "install AutoDuty or complete this duty manually");

    if (_cfcResolver is null)
        return new EngineAction.AwaitUser(
            "DutyStep(kind:duty) dispatched but no ICfcResolver configured — " +
            "host must supply one");

    // Guard 2: ContentFinderConditionId not set (should not happen if validator runs)
    if (step.ContentFinderConditionId is null or 0)
        return new EngineAction.AwaitUser(
            $"DutyStep '{step.Id}' has no ContentFinderConditionId — " +
            "quest file must specify it for kind 'duty'");

    // Guard 3: CFC -> territory type resolution
    var territoryType = _cfcResolver.GetTerritoryType(step.ContentFinderConditionId.Value);
    if (territoryType is null)
        return new EngineAction.AwaitUser(
            $"ContentFinderConditionId {step.ContentFinderConditionId} could not be " +
            "resolved to a territory type — unknown duty");

    // Guard 4: AutoDuty installed
    var availResult = await _dutyRunner.IsAvailable(ct);
    if (availResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            "AutoDuty required for dungeon/trial automation. " +
            "Install AutoDuty or complete this duty manually.");

    // Guard 5: AutoDuty has path for this territory
    var pathResult = await _dutyRunner.ContentHasPath(territoryType.Value, ct);
    if (pathResult is Result<bool>.Success { Value: false })
        return new EngineAction.AwaitUser(
            $"AutoDuty does not have a path for territory {territoryType.Value} " +
            $"(CFC {step.ContentFinderConditionId}). Complete this duty manually " +
            "or wait for AutoDuty path support.");

    return new EngineAction.EnterDuty(
        step.ContentFinderConditionId.Value, Origin: step);
}
```

**Key invariant:** `_lastResolvedStep` is NOT set in `ResolveDungeonTrial` (matches Teleport/Purchase/UseAction/UseEmote/SPD precedent).

**Guard ordering rationale:** Cheapest checks first (null adapter, null field), then Lumina lookup (synchronous), then async IPC calls (IsAvailable, ContentHasPath). This minimizes unnecessary IPC when the step is misconfigured.

### AD8: Engine dispatch wiring -- `ResolveDuty` kind switch

The existing `ResolveDuty` method dispatches by `step.Kind`:

```csharp
private async Task<EngineAction> ResolveDuty(DutyStep step, CancellationToken ct)
{
    return step.Kind switch
    {
        "spd" => await ResolveSpd(step, ct),
        "duty" => await ResolveDungeonTrial(step, ct),
        _ => throw new NotSupportedException(
            $"DutyStep kind '{step.Kind}' is not supported. " +
            "Only 'spd' and 'duty' are implemented.")
    };
}
```

The `"regular"` kind remains unsupported and falls through to the default `NotSupportedException`. This is the existing behavior -- no regression.

### AD9: StopDuty cleanup -- `_activeDutyStepId` tracking in EngineHost

EngineHost tracks `_activeDutyStepId` alongside the existing `_activeSpdStepId`. The cleanup pattern is identical: when the dispatched action changes away from `EnterDuty` (and is not `Wait`), EngineHost calls `_dutyRunner.StopDuty(ct)`.

```csharp
private string? _activeDutyStepId;

// In DispatchAction, alongside the existing SPD cleanup:
if (_activeDutyStepId is not null
    && action is not EngineAction.EnterDuty
    && action is not EngineAction.Wait)
{
    await _dutyRunner.StopDuty(ct);
    _activeDutyStepId = null;
}
```

Also call `StopDuty` in `EndRun()` for safety (handles `/qf stop` during a duty).

**Why a separate tracking field from SPD:** SPD and dungeon/trial use different adapters (BossMod vs AutoDuty). Mixing them in one field risks calling the wrong adapter's Stop method.

### AD10: DalamudDutyRunner -- AutoDuty IPC wrapper

```csharp
namespace QuestForge.Adapters.Dalamud.Duty;

public sealed class DalamudDutyRunner : IDutyRunner
{
    private readonly PluginServices _services;

    // Per-call IPC subscribers (same pattern as DalamudQuestBattleRunner).
    private ICallGateSubscriber<string, string, object> SetConfig
        => _services.PluginInterface.GetIpcSubscriber<string, string, object>(
            "AutoDuty.SetConfig");
    private ICallGateSubscriber<uint, int, bool, object> Run
        => _services.PluginInterface.GetIpcSubscriber<uint, int, bool, object>(
            "AutoDuty.Run");
    private ICallGateSubscriber<bool> IsStopped
        => _services.PluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
    private ICallGateSubscriber<object> Stop
        => _services.PluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop");
    private ICallGateSubscriber<uint, bool> ContentHasPathGate
        => _services.PluginInterface.GetIpcSubscriber<uint, bool>(
            "AutoDuty.ContentHasPath");

    public Task<Result<bool>> StartDuty(uint territoryType, CancellationToken ct)
    {
        try
        {
            // Configure Support mode (Duty Support / Trust NPC parties).
            // SetConfig expects (string config, object setting) where setting
            // must be a string -- AutoDuty's IPC dispatches on setting.GetType().
            SetConfig.InvokeAction("dutyModeEnum", "Support");
            // Run with 1 loop, bareMode = false (full AutoDuty navigation).
            Run.InvokeAction(territoryType, 1, false);
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("start-duty-failed", ex.Message));
        }
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        try
        {
            Stop.InvokeAction();
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
        catch
        {
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
    }

    public Task<Result<bool>> IsAvailable(CancellationToken ct)
    {
        try
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Success(ContentHasPathGate.HasFunction));
        }
        catch
        {
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(false));
        }
    }

    public Task<Result<bool>> ContentHasPath(uint territoryType, CancellationToken ct)
    {
        try
        {
            var hasPath = ContentHasPathGate.InvokeFunc(territoryType);
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(hasPath));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("content-has-path-failed", ex.Message));
        }
    }
}
```

**Key IPC observation from AutoDuty source:** `SetConfig` dispatches on `setting.GetType()` -- it must be a `string`, not an `int` or `object`. Questionable passes `"Support"` as a string, which AutoDuty's `ConfigHelper.ModifyConfig(config, s)` handles. QuestForge does the same.

**No `bareMode = true`:** Questionable uses `bareMode` based on a user setting. QuestForge always uses `bareMode = false` because bare mode skips AutoDuty's built-in navigation, which we want. The user can override this in a future config if needed.

**No recording proxy needed:** `DalamudDutyRunner` is write-only (StartDuty/StopDuty are fire-and-forget). The `action.submitted`/`action.completed` events from `EngineHost.DispatchAction` already capture the writes. Same reasoning as `DalamudQuestBattleRunner`.

### AD11: Lazy dismount exemption -- EnterDuty is NOT exempt

`EnterDuty` IS subject to lazy dismount (same as `EnterSinglePlayerDuty`, `UseAction`, `UseEmote`, `Interact`). The player should be dismounted before entering a duty queue.

In `EngineHost.DispatchAction`, the existing exemption list:
```csharp
if (_lastDispatchedActionWasNavigate && action is not EngineAction.Navigate
    and not EngineAction.Teleport
    and not EngineAction.EquipGear and not EngineAction.EquipBestGear
    and not EngineAction.RegisterGearset)
```

No change needed -- `EnterDuty` is a new type that does not match any of the `is not` clauses, so lazy dismount fires correctly.

### AD12: Completion is Expect-driven -- no `IsStopped()` polling

The engine does NOT poll `AutoDuty.IsStopped()`. Completion is determined by the step's `Expect` predicate -- typically `questSequence(N) >= M`. When the dungeon/trial completes and the player exits, the game advances the quest sequence. The engine evaluates Expect on each tick and confirms the step when true.

**Why not use IsStopped:** `IsStopped` only tells you AutoDuty stopped -- not whether the duty succeeded. The quest sequence is the canonical success signal. Polling IsStopped would add complexity without improving correctness.

**Where IsStopped IS used:** The `StopDuty` call (AD9 cleanup) fires when the engine advances past the step. This tells AutoDuty to clean up its internal state. But the engine does not read IsStopped to decide when to advance.

### AD13: Structural validator changes for `kind: "duty"`

Add a new branch to `ValidateDutyStep` in `StructuralValidator.cs`:

```csharp
else if (step.Kind == "duty")
{
    if (step.ContentFinderConditionId is null)
        errors.Add(E(ctx, "structural/duty-missing-required-field", scope.ToString(),
            $"Step '{step.Id}': duty kind 'duty' requires 'contentFinderConditionId'.",
            stepId: step.Id));
    if (step.DutyId is not null)
        errors.Add(E(ctx, "structural/duty-invalid-field-for-kind", scope.ToString(),
            $"Step '{step.Id}': 'dutyId' must not be set for duty kind 'duty'.",
            stepId: step.Id));
    if (step.EntryNpc is not null)
        errors.Add(E(ctx, "structural/duty-invalid-field-for-kind", scope.ToString(),
            $"Step '{step.Id}': 'entryNpc' must not be set for duty kind 'duty'.",
            stepId: step.Id));
    if (step.Trigger is not null)
        errors.Add(E(ctx, "structural/duty-invalid-field-for-kind", scope.ToString(),
            $"Step '{step.Id}': 'trigger' must not be set for duty kind 'duty'.",
            stepId: step.Id));
}
```

### AD14: DraftValidator changes

**E-rule for ContentFinderConditionId == 0:**
```csharp
// E13: DutyStep(kind:"duty") with ContentFinderConditionId == 0
if (step.Raw is DutyStep { Kind: "duty", ContentFinderConditionId: 0 })
{
    errors.Add(new DraftValidationError("E13",
        $"Step '{step.StepId}' has ContentFinderConditionId=0 which is invalid.",
        [i]));
}
```

**W-rule for DutyStep missing Expect:** The existing W1 suppression guard does NOT suppress warnings for DutyStep, which is correct. DutySteps without Expect spin-loop (the engine re-dispatches EnterDuty every tick). No change to W1 needed.

**New W11 for spin-loop warning:**
```csharp
// W11: DutyStep(kind:"duty") with no Expect -- engine spin-loops without one
if (step.Raw is DutyStep { Kind: "duty" } dt && dt.Expect is null)
{
    warnings.Add(new DraftValidationWarning("W11",
        $"Step '{step.StepId}' is a DutyStep(kind:duty) with no 'expect' predicate " +
        "-- without it the engine will spin-loop re-dispatching EnterDuty. " +
        "Add an expect predicate (e.g. questSequence).",
        [i]));
}
```

Note: W1 already catches DutyStep-without-Expect generically. W11 provides a more specific message for kind:"duty". The W1 suppression guard must be updated to exclude DutyStep(kind:"duty") to avoid a duplicate warning:

```csharp
// W1 suppression: add DutyStep to the excluded list
if (step.Raw.Expect is null
    && step.Raw is not UseActionStep and not UseEmoteStep
    and not SayChatMessageStep and not UseItemStep
    and not EquipGearForQuestStep and not ChangeJobStep
    and not DutyStep)  // <-- AD14: all DutyStep kinds have specific W-rules
```

### AD15: Authoring inference -- explicitly not implemented

Dungeon/trial steps are NOT auto-inferred. They are relatively rare and always explicitly authored. Detection would require monitoring `ContentFinderConditionId` transitions and distinguishing dungeons from SPDs, which is complex and unreliable.

### AD16: BONUS -- `/qf debug duty-info` command

A debug command that helps authors populate the `ContentFinderConditionId` field without zoning into the duty.

**Implementation approach:** Read the quest's associated ContentFinderCondition data from Lumina. FFXIV quests reference their duties via the `QuestParams` table with `ScriptInstruction` values like `"INSTANCEDUNGEON0"`. Questionable uses this exact mechanism in `TerritoryData.GetQuestBattles`.

```csharp
// QfCommand.cs handler for /qf debug duty-info <questId>
private void HandleDutyInfo(uint questId)
{
    var questRow = _dataManager.GetExcelSheet<Quest>().GetRowOrDefault(questId);
    if (questRow is null) { Log("Quest not found"); return; }

    foreach (var param in questRow.Value.QuestParams)
    {
        var instruction = param.ScriptInstruction.ToString();
        if (string.IsNullOrEmpty(instruction)) break;

        if (instruction.StartsWith("INSTANCEDUNGEON") || instruction.StartsWith("QUESTBATTLE"))
        {
            var contentId = param.ScriptArg;
            // Resolve to CFC ID via InstanceContent or QuestBattleResident
            uint cfcId;
            if (contentId >= 5000)
                cfcId = _dataManager.GetExcelSheet<InstanceContent>()
                    .GetRow(contentId).ContentFinderCondition.RowId;
            else
                cfcId = _dataManager.GetExcelSheet<QuestBattleResident>()
                    .GetRow(contentId).Unknown0;

            var cfc = _dataManager.GetExcelSheet<ContentFinderCondition>()
                .GetRowOrDefault(cfcId);
            if (cfc is not null)
            {
                var contentType = cfc.Value.ContentType.Value.Name.ToString();
                Log($"[{instruction}] CFC={cfcId} Territory={cfc.Value.TerritoryType.RowId} " +
                    $"Name=\"{cfc.Value.Name}\" ContentType=\"{contentType}\"");
            }
        }
    }
}
```

**Output example:**
```
[INSTANCEDUNGEON0] CFC=2 Territory=167 Name="Sastasha" ContentType="Dungeons"
```

**Why lookup from quest, not from current duty:** The user wants this information BEFORE zoning in. `GameMain.CurrentContentFinderConditionId` is only populated inside the duty. The Lumina quest-data lookup answers "what duties does this quest require?" without entering them.

**Scope:** This is a bonus/nice-to-have feature. The builder may defer it if the Lumina sheet navigation proves complex. The core engine + adapter work is the priority.

---

## FakeDutyRunner

```csharp
namespace QuestForge.Adapters.Fakes.Duty;

public sealed class FakeDutyRunner : IDutyRunner
{
    public bool AutoDutyAvailable { get; set; } = true;
    public int StartDutyCallCount { get; private set; }
    public int StopDutyCallCount { get; private set; }
    public int IsAvailableCallCount { get; private set; }
    public int ContentHasPathCallCount { get; private set; }

    /// <summary>Territory type values for which ContentHasPath returns true.</summary>
    private readonly HashSet<uint> _pathAvailable = new();

    /// <summary>Recorded territory type passed to StartDuty.</summary>
    public uint? LastStartedTerritoryType { get; private set; }

    /// <summary>When non-null, StartDuty returns this failure reason instead of success.</summary>
    public string? ScriptedStartFailure { get; set; }

    public void SetContentHasPath(uint territoryType, bool hasPath)
    {
        if (hasPath) _pathAvailable.Add(territoryType);
        else _pathAvailable.Remove(territoryType);
    }

    public void Reset()
    {
        StartDutyCallCount = 0;
        StopDutyCallCount = 0;
        IsAvailableCallCount = 0;
        ContentHasPathCallCount = 0;
        LastStartedTerritoryType = null;
        ScriptedStartFailure = null;
        _pathAvailable.Clear();
    }

    public Task<Result<bool>> StartDuty(uint territoryType, CancellationToken ct)
    {
        StartDutyCallCount++;
        LastStartedTerritoryType = territoryType;
        if (ScriptedStartFailure is not null)
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("start-duty-failed", ScriptedStartFailure));
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        StopDutyCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
    }

    public Task<Result<bool>> IsAvailable(CancellationToken ct)
    {
        IsAvailableCallCount++;
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(AutoDutyAvailable));
    }

    public Task<Result<bool>> ContentHasPath(uint territoryType, CancellationToken ct)
    {
        ContentHasPathCallCount++;
        return Task.FromResult<Result<bool>>(
            new Result<bool>.Success(_pathAvailable.Contains(territoryType)));
    }
}
```

---

## EngineTestHarness changes

```csharp
// New properties:
public FakeDutyRunner DutyRunner { get; } = new FakeDutyRunner();
public FakeCfcResolver CfcResolver { get; } = new FakeCfcResolver();

// In constructor, pass to QuestEngine:
var inner = engineRef = new QuestEngine(
    // ... existing params ...,
    questBattleRunner: QuestBattleRunner,
    dutyRunner: DutyRunner,
    cfcResolver: CfcResolver);

// In HarnessEngine constructor:
// Add FakeDutyRunner parameter and inline dispatch.

// In RunToCompletion, new case:
case EngineAction.EnterDuty ed:
    actions.Add(action);
    EmitActionSubmitted("EnterDuty",
        JsonSerializer.SerializeToElement(
            new { cfcId = ed.ContentFinderConditionId, origin = ed.Origin?.Id },
            _jsonOpts));
    // Resolve territory type for the fake adapter call
    var tt = CfcResolver.GetTerritoryType(ed.ContentFinderConditionId);
    if (tt is not null)
    {
        var dutyResult = await DutyRunner.StartDuty(tt.Value, ct);
        EmitActionCompleted("EnterDuty",
            dutyResult.IsSuccess ? "Started" : "Failed");
    }
    else
    {
        EmitActionCompleted("EnterDuty", "Failed:UnknownCFC");
    }
    break;
```

HarnessEngine inline dispatch for direct Tick() calls:
```csharp
// In HarnessEngine.Tick, alongside EnterSinglePlayerDuty:
if (action is EngineAction.EnterDuty ed)
{
    var tt = _cfcResolver.GetTerritoryType(ed.ContentFinderConditionId);
    if (tt is not null)
        await _dutyRunner.StartDuty(tt.Value, ct);
}
```

---

## Validation rule table

### Structural validator (questforge-tools)

| Rule | Code | Condition |
|---|---|---|
| `kind: "duty"` requires `contentFinderConditionId` non-null | `structural/duty-missing-required-field` | `step.ContentFinderConditionId is null` |
| `kind: "duty"` must not have `dutyId` | `structural/duty-invalid-field-for-kind` | `step.DutyId is not null` |
| `kind: "duty"` must not have `entryNpc` | `structural/duty-invalid-field-for-kind` | `step.EntryNpc is not null` |
| `kind: "duty"` must not have `trigger` | `structural/duty-invalid-field-for-kind` | `step.Trigger is not null` |

### DraftValidator (questforge engine)

| Rule | Code | Condition |
|---|---|---|
| `ContentFinderConditionId == 0` on kind "duty" | E13 | `step is DutyStep { Kind: "duty", ContentFinderConditionId: 0 }` |
| DutyStep(kind:"duty") missing Expect | W11 | `step is DutyStep { Kind: "duty" }` and `Expect is null` |

### W1 suppression update

Add `and not DutyStep` to the W1 guard so that DutyStep-without-Expect does not trigger both W1 and the kind-specific W-rule.

---

## Given-When-Then specifications

### D1: Happy path -- dungeon completes via AutoDuty

**Given:** A quest with sequence 0 containing:
- `talk-to-npc` (TalkStep, target NPC 5000 at (10,0,20) zone 132, Expect: `questSequence(80001) >= 2`)
- `enter-dungeon` (DutyStep, kind: "duty", ContentFinderConditionId: 2, Expect: `questSequence(80001) >= 3`)
- `talk-after-dungeon` (TalkStep, target NPC 5001 at (30,0,40) zone 132, Expect: `questSequence(80001) >= 4`)

Player starts at zone 132, position (10,0,20). Quest 80001 at sequence 0.
`FakeDutyRunner.AutoDutyAvailable = true`.
`FakeDutyRunner.SetContentHasPath(167, true)` (territory 167 = Sastasha).
`FakeCfcResolver.Register(2, 167)` (CFC 2 -> territory 167).
Wire callbacks: on Interact for NPC 5000 -> advance to seq 2.
On first `EnterDuty` dispatch: advance to seq 3 (simulating duty completion).
On Interact for NPC 5001 -> advance to seq 4.

**When:** `RunToCompletion(maxTicks: 20)`

**Then:**
- Actions include Interact (talk-to-npc), EnterDuty (enter-dungeon), Interact (talk-after-dungeon)
- `DutyRunner.StartDutyCallCount == 1`
- `DutyRunner.LastStartedTerritoryType == 167`
- Engine reaches Done

### D2: AutoDuty not available -- AwaitUser

**Given:** Same quest as D1. `FakeDutyRunner.AutoDutyAvailable = false`.
CFC resolver and path configured normally.

**When:** Engine ticks until reaching `enter-dungeon` step.

**Then:** Engine returns `AwaitUser` with message containing "AutoDuty required for dungeon/trial automation".

### D3: IDutyRunner not wired -- AwaitUser

**Given:** Same quest as D1. QuestEngine constructed WITHOUT passing `dutyRunner` (null).

**When:** Engine ticks until reaching `enter-dungeon` step.

**Then:** Engine returns `AwaitUser` with message containing "IDutyRunner not configured".

### D4: ICfcResolver not wired -- AwaitUser

**Given:** Same quest as D1. QuestEngine constructed WITHOUT passing `cfcResolver` (null).
`dutyRunner` IS wired.

**When:** Engine ticks until reaching `enter-dungeon` step.

**Then:** Engine returns `AwaitUser` with message containing "ICfcResolver not configured".

### D5: Unknown CFC ID -- AwaitUser

**Given:** Same quest as D1 but `ContentFinderConditionId: 9999`.
`FakeCfcResolver` does NOT have entry for CFC 9999.
`FakeDutyRunner.AutoDutyAvailable = true`.

**When:** Engine ticks until reaching `enter-dungeon` step.

**Then:** Engine returns `AwaitUser` with message containing "could not be resolved to a territory type".

### D6: ContentHasPath false -- AwaitUser

**Given:** Same quest as D1.
`FakeDutyRunner.AutoDutyAvailable = true`.
`FakeCfcResolver.Register(2, 167)`.
`FakeDutyRunner.SetContentHasPath(167, false)` -- AutoDuty has no path.

**When:** Engine ticks until reaching `enter-dungeon` step.

**Then:** Engine returns `AwaitUser` with message containing "does not have a path for territory".

### D7: ContentFinderConditionId null on kind "duty" -- AwaitUser

**Given:** A quest with a DutyStep `{ kind: "duty", contentFinderConditionId: null }`.
All adapters wired and available.

**When:** Engine ticks until reaching the step.

**Then:** Engine returns `AwaitUser` with message containing "has no ContentFinderConditionId".

### D8: ContentFinderConditionId is 0 on kind "duty" -- AwaitUser

**Given:** Same as D7 but `ContentFinderConditionId: 0`.

**When:** Engine ticks until reaching the step.

**Then:** Engine returns `AwaitUser` with message containing "has no ContentFinderConditionId".

### D9: SkipIf satisfied -- step skipped, no EnterDuty emitted

**Given:** Quest with a DutyStep(kind: "duty") with `skipIf: "questSequence(80001) >= 3"` and `expect: "questSequence(80001) >= 3"`. Quest 80001 already at sequence 3.

**When:** Engine ticks.

**Then:** The DutyStep is confirmed immediately (Expect is true). No `EnterDuty` emitted. `DutyRunner.StartDutyCallCount == 0`.

### D10: Retry -- Expect stays false after EnterDuty

**Given:** Same quest as D1. Wire callbacks so the first `EnterDuty` dispatch does NOT advance questSequence (simulating duty failure/wipe). After 3 more ticks of EnterDuty, advance questSequence to 3 (simulating success on retry).

**When:** `RunToCompletion(maxTicks: 20)`

**Then:**
- Multiple `EnterDuty` actions emitted (engine re-dispatches on each tick)
- `DutyRunner.StartDutyCallCount >= 2` (idempotent calls)
- Engine eventually reaches Done after questSequence advances

### D11: EnterDuty is subject to lazy dismount

**Given:** Quest with a TravelStep followed by a DutyStep(kind: "duty"). Player is mounted after Navigate completes (`MountState = Flying`). Navigation has stopped (`IsNavigating = false`).

**When:** Engine transitions from Navigate to EnterDuty.

**Then:** Lazy dismount fires before the action is returned. `Mount.DismountCallCount >= 1`.

### D12: DutyStep kind "regular" -- still throws NotSupportedException

**Given:** Quest with a DutyStep(kind: "regular").

**When:** Engine ticks until reaching the duty step.

**Then:** Engine throws `NotSupportedException` with message containing "not supported".

### D13: Territory type passed to StartDuty matches CFC resolver output

**Given:** Same quest as D1. `FakeCfcResolver.Register(2, 167)`.

**When:** EnterDuty is dispatched and handled in RunToCompletion or HarnessEngine.

**Then:** `DutyRunner.LastStartedTerritoryType == 167` -- the territory type from the CFC resolver is what reaches the adapter.

### D14: DraftValidator E13 -- ContentFinderConditionId is 0

**Given:** A draft with one DutyStep(kind: "duty") with `ContentFinderConditionId = 0`.

**When:** `DraftValidator.Validate` runs.

**Then:** Errors include E13 with message containing "ContentFinderConditionId=0".

### D15: DraftValidator W11 -- DutyStep(kind:"duty") missing Expect

**Given:** A draft with one DutyStep(kind: "duty") with `ContentFinderConditionId = 2` but no Expect.

**When:** `DraftValidator.Validate` runs.

**Then:** Warnings include W11 with message containing "spin-loop".

### D16: DraftValidator W1 does NOT fire for DutyStep (W11 covers it)

**Given:** Same as D15.

**When:** `DraftValidator.Validate` runs.

**Then:** Warnings include W11 but do NOT include W1 for the same step.

### D17: Structural validator -- kind "duty" missing contentFinderConditionId

**Given:** A quest JSON with `{ "type": "duty", "kind": "duty" }` (no `contentFinderConditionId` field).

**When:** `StructuralValidator.Validate` runs.

**Then:** Errors include `structural/duty-missing-required-field` with message containing "requires 'contentFinderConditionId'".

### D18: Structural validator -- kind "duty" with dutyId set

**Given:** A quest JSON with `{ "type": "duty", "kind": "duty", "contentFinderConditionId": 2, "dutyId": 99 }`.

**When:** `StructuralValidator.Validate` runs.

**Then:** Errors include `structural/duty-invalid-field-for-kind` with message containing "'dutyId' must not be set for duty kind 'duty'".

### D19: JSON round-trip for DutyStep with ContentFinderConditionId

**Given:** A `DutyStep { Kind = "duty", Id = "enter-dungeon", ContentFinderConditionId = 2, Expect = new PredicateExpect { Predicate = "questSequence(80001) >= 3" } }`.

**When:** Serialize to JSON then deserialize back.

**Then:** Round-trip produces an equal object. JSON contains `"contentFinderConditionId": 2` and `"kind": "duty"`. `ContentFinderConditionId` is absent from JSON when null (SPD case).

---

## Tooling catch-up

### TraceConstants (questforge-tools)

Add:
```csharp
internal const string ActionEnterDuty = "enterduty";
```

No behavior change (`IsTerminalAction` only uses `done`/`awaituser`). Documents what `DecisionEvent.ActionType.ToLowerInvariant()` emits for the new action.

### CapabilityInferrer (questforge-tools)

The existing `DutyStep` mapping emits `step:duty` for all DutyStep kinds. Add a new kind-specific tag for `kind: "duty"`:

```csharp
// In the DutyStep kind check (alongside the existing "spd" check):
if (step is DutyStep duty)
{
    if (duty.Kind == "spd")
        caps.Add("step:spd");
    else if (duty.Kind == "duty")
        caps.Add("step:dungeon-trial");
}
```

### FilenameLookup / DistinguishingCapPriority (questforge-tools)

Add entries:

```csharp
// FilenameLookup:
(["step:dungeon-trial"], "with-dungeon-trial.json"),

// DistinguishingCapPriority (insert above step:spd):
("step:dungeon-trial", "with-dungeon-trial.json"),
```

### FIXTURES.md (questforge repo)

Add row to `actionType` canonical strings table:
```
| `"enterduty"` | `EngineAction.EnterDuty` | `DutyStep(kind:"duty")` dispatch -- configure AutoDuty for Support mode and start dungeon/trial. |
```

---

## Implementation order

### Phase A -- Schema + adapter interfaces + fakes (0.5 day)
1. Add `ContentFinderConditionId` to `DutyStep` in both `questforge/QuestForge.Schema/Step.cs` and `questforge-tools/QuestForge.Schema/Step.cs`
2. Create `QuestForge.Adapters/Duty/IDutyRunner.cs`
3. Create `QuestForge.Adapters/Duty/ICfcResolver.cs`
4. Create `QuestForge.Adapters.Fakes/Duty/FakeDutyRunner.cs`
5. Create `QuestForge.Adapters.Fakes/Duty/FakeCfcResolver.cs`
6. Build passes.

**Done before Phase B.**

### Phase B -- Engine changes (1 day)
1. Add `EngineAction.EnterDuty` to `EngineAction.cs`
2. Add `IDutyRunner? _dutyRunner` and `ICfcResolver? _cfcResolver` as optional ctor params on `QuestEngine`
3. Add `ResolveDungeonTrial` async pre-arm in `QuestEngine.cs`
4. Update `ResolveDuty` kind switch: add `"duty" => await ResolveDungeonTrial(step, ct)` arm
5. Update `EngineTestHarness`: add `FakeDutyRunner`, `FakeCfcResolver`, pass to ctor, add dispatch arm
6. Update `HarnessEngine`: add `FakeDutyRunner`, `FakeCfcResolver` params, add inline dispatch

**Done before Phase C.**

### Phase C -- Tests (1 day)
1. Write `DungeonTrialDutyTests.cs` with scenarios D1-D13
2. Write `DraftValidatorDutyTests.cs` with scenarios D14-D16
3. Write JSON round-trip test D19 in `RoundTripTests.cs`
4. All engine tests pass (`dotnet test QuestForge.Engine.Tests`)

**Done before Phase D.**

### Phase D -- Validator changes (0.5 day)
1. Update `ValidateDutyStep` in `StructuralValidator.cs` (questforge-tools) for kind "duty" rules (AD13)
2. Add DraftValidator rules E13 and W11 (AD14)
3. Update W1 suppression guard
4. Write structural validator tests D17-D18 in `StepTypeRuleTests.cs` (questforge-tools)

**Done before Phase E.**

### Phase E -- Dalamud impl + EngineHost (1.5 days)
1. Create `QuestForge.Adapters.Dalamud/Duty/DalamudDutyRunner.cs` (AutoDuty IPC)
2. Create `QuestForge.Adapters.Dalamud/Duty/LuminaCfcResolver.cs` (Lumina CFC lookup)
3. Add `_dutyRunner` and `_cfcResolver` fields + construction to `EngineHost`
4. Pass to `QuestEngine` in `BeginRun`
5. Add `DispatchAction` arm for `EnterDuty`
6. Add `_activeDutyStepId` tracking + StopDuty cleanup in dispatch pre-switch
7. Add StopDuty call in `EndRun()`
8. Update FIXTURES.md actionType table

**Done before Phase F.**

### Phase F -- Tooling catch-up (0.5 day)
1. Add `ActionEnterDuty` to `TraceConstants.cs`
2. Update `CapabilityInferrer` for `step:dungeon-trial`
3. Update `FilenameLookup` and `DistinguishingCapPriority`
4. Add tests for the above in `QuestForge.Tools.Trace.Tests/`

### Phase G -- BONUS: `/qf debug duty-info` (0.5 day, deferrable)
1. Add `HandleDutyInfo` handler to `QfCommand.cs`
2. Lumina lookup via Quest sheet's QuestParams -> InstanceContent -> ContentFinderCondition

---

## Done criteria

1. `dotnet test QuestForge.Engine.Tests` passes with all D1-D16 scenarios green
2. `dotnet test QuestForge.Adapters.Tests` passes (no adapter test regressions)
3. A quest JSON with `{ "type": "duty", "kind": "duty", "contentFinderConditionId": 2, "expect": "questSequence(80001) >= 3" }` round-trips through serialization (D19)
4. `FakeDutyRunner.StartDutyCallCount` is asserted in D1, D10, D13
5. `FakeDutyRunner.LastStartedTerritoryType == 167` is asserted in D1, D13
6. AutoDuty unavailable -> AwaitUser with actionable message (D2)
7. IDutyRunner null -> AwaitUser with actionable message (D3)
8. ICfcResolver null -> AwaitUser with actionable message (D4)
9. Unknown CFC ID -> AwaitUser with actionable message (D5)
10. ContentHasPath false -> AwaitUser with actionable message (D6)
11. DutyStep(kind: "regular") -> NotSupportedException (D12, unchanged from SPD)
12. `TraceConstants.ActionEnterDuty == "enterduty"`
13. FIXTURES.md contains the new `"enterduty"` actionType row
14. StructuralValidator catches missing `contentFinderConditionId` on kind "duty" (D17)
15. DraftValidator E13 catches `ContentFinderConditionId == 0` (D14)
16. DraftValidator W11 warns on missing Expect with "spin-loop" in message (D15)

---

## What this plan does NOT include

- **DutyStep(kind: "regular") implementation** -- the legacy kind remains unsupported (`NotSupportedException`). All new dungeon/trial quests use `kind: "duty"`.
- **Duty Finder fallback** -- AutoDuty not available means AwaitUser, not DF queue. Player parties are not in scope.
- **`MaxDutyRetries` counter** -- retry limiting is a future enhancement; infinite stateless retry is the correct default.
- **`IsStopped()` polling for completion** -- completion is entirely Expect-driven (AD12).
- **Authoring inference for duty steps** -- explicitly deferred (AD15). Duty steps are manually authored.
- **`bareMode` configuration** -- always `false`. A future `PluginConfig` toggle may be added.
- **Unsynced mode** -- always Support mode (`"dutyModeEnum": "Support"`). Unsynced is a future config option.
- **Death recovery routing inside duties** -- AutoDuty handles death internally. The engine does not intercept.
- **Internal dungeon AI** -- QuestForge does not implement its own dungeon navigation or combat AI; AutoDuty is the delegate.
- **`/qf debug duty-info` is bonus** -- deferrable to a follow-up PR if Lumina sheet navigation is complex.
- **Validator changes in questforge-tools for `kind: "duty"`** -- must be in scope (the structural validator needs the new rules), but does NOT require a separate phase. It is bundled in Phase D alongside the DraftValidator.

---

READY FOR TEST CREATION

Tester: Write failing tests from the GWT specs in D1-D19.
- Happy paths: 4 scenarios (D1, D9, D13, D19)
- Edge cases: 4 scenarios (D10, D11, D12, D16)
- Error cases: 7 scenarios (D2, D3, D4, D5, D6, D7, D8)
- Validator scenarios: 4 scenarios (D14, D15, D17, D18)
- Expected total: ~19-21 tests across:
  - QuestForge.Engine.Tests/Engine/DungeonTrialDutyTests.cs
  - QuestForge.Engine.Tests/Authoring/DraftValidatorDutyTests.cs
  - QuestForge.Schema.Tests/RoundTripTests.cs (D19)
  - QuestForge.Tools.Validator.Tests/StepTypeRuleTests.cs (D17, D18)
