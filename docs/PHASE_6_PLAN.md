# Phase 6 Implementation Plan: Dalamud-Backed Adapters + Plugin Entry

**Status:** ready to implement
**Input docs:** docs/NEXT_STEPS.md §Phase 6, docs/ADAPTERS.md §4–§13, docs/SPIKE_NOTES.md (Phase 0 IPC contracts), docs/ARCHITECTURE.md (three-layer separation), docs/DESIGN.md §5–§7
**Output:** plugin builds and loads in-game; user accepts quest 66130 ("Coming to Ul'dah"); engine drives the same `EngineAction` sequence that passed Phase 4 fakes, against real Dalamud adapters, and the quest completes.
**Predecessor:** Phase 5 complete — `TraceWriter`, `RecordingGameStateProvider`, `RecordingQuestState`, six event types; 103 tests green.

---

## Goal restated

The Phase 4/5 engine drove quest 66130 end-to-end against in-memory fakes. Phase 6 swaps the fakes for production Dalamud-backed implementations. The engine code does not change. The recording proxy code does not change. What changes:

1. `QuestForge.Adapters.Dalamud` (currently an empty placeholder csproj) gets populated with one concrete implementation per adapter interface, in dependency order.
2. `QuestForge.Plugin` (also an empty placeholder) gets a Dalamud plugin entry point that constructs the adapters, wraps `IGameStateProvider`/`IQuestState` with the Phase 5 recording proxies, and bridges the Dalamud framework tick to `QuestEngine.Tick(ct)`.
3. A `/qf` slash-command harness exposes per-adapter smoke checks so each adapter can be validated in-game incrementally — the only available "test" surface, since Phase 6 adapters cannot be unit-tested without a running game.

**Replay** (Phase 7), **UI** (Phase 8), **authoring mode** (Phase 9), **death recovery / failure counters** (Phase 7+), and **step types beyond travel and talk** (Phase 10+) remain out of scope.

---

## Dependency graph

Adapters are implemented in the order in `NEXT_STEPS.md`, and each one is verified in-game before the next is started. This guards against compounding mistakes: if `IGameStateProvider` reads garbage, `IQuestState` and `INavigator` will appear broken for the wrong reason.

```
1.  QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider     ← ClientState + ObjectTable + Condition
        └── verified in-game (smoke command), then ↓
2.  QuestForge.Adapters.Dalamud/State/DalamudQuestState            ← QuestManager + Lumina Quest sheet
        └── verified, then ↓
3.  QuestForge.Adapters.Dalamud/Movement/LifestreamTeleporter      ← Lifestream IPC
        └── verified, then ↓
4.  QuestForge.Adapters.Dalamud/Movement/VnavmeshNavigator         ← vnavmesh IPC
        └── verified, then ↓
5.  QuestForge.Adapters.Dalamud/Interaction/DalamudInteractor      ← TargetSystem + Talk addon variants + JournalAccept/Result
        └── verified, then ↓
6.  QuestForge.Adapters.Dalamud/Combat/WrathComboAdapter           ← WrathCombo IPC (or BossMod — pick simpler)
7.  QuestForge.Adapters.Dalamud/Interaction/LuminaDialogueResolver ← Lumina sheet reference resolution
8.  QuestForge.Adapters.Dalamud/Timing/SeededTimingProfile         ← pure logic, lives in Adapters.Dalamud for now
9.  QuestForge.Adapters.Dalamud/Gear/DalamudGearManager            ← built-in gear functions; Stylist deferred
10. QuestForge.Adapters.Dalamud/Minigames/NullMinigameSkipper      ← returns Unsupported for every kind in v1

QuestForge.Plugin
   └── Plugin.cs                       ← Dalamud entry point, IDalamudPlugin
   └── PluginServices.cs               ← [PluginService] injection container
   └── EngineHost.cs                   ← constructs QuestEngine + recording proxies, manages lifecycle
   └── Commands/QfCommand.cs           ← /qf slash-command dispatcher (smoke tests + run controls)
```

**Build order:** csproj wiring (Dalamud SDK + ECommons) → `IGameStateProvider` impl → `/qf test gamestate` smoke verifies → next adapter, repeat. The plugin host wiring lands after `IGameStateProvider` and `IQuestState` because those two are the only adapters the recording proxy wraps; the rest can be stubbed in until their concrete implementations land.

---

## Architectural decisions (read before coding)

### 1. `QuestForge.Adapters.Dalamud` is the only Dalamud-aware project under the adapters layer

The Dalamud-backed adapter assemblies live here, not in `QuestForge.Plugin`. This preserves the testability boundary: only this project and `QuestForge.Plugin` reference Dalamud. The engine continues to depend only on `QuestForge.Adapters` (interfaces) plus `QuestForge.Schema` and `QuestForge.Predicates`.

```xml
<!-- QuestForge.Adapters.Dalamud.csproj — final shape -->
<Project Sdk="Dalamud.NET.Sdk/13.0.0">
  <PropertyGroup>
    <TargetFramework>net10.0-windows7.0</TargetFramework>
    <Platforms>x64</Platforms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- Required: Adapters.Dalamud is a library only — the plugin manifest lives in QuestForge.Plugin -->
    <NoPluginManifest>true</NoPluginManifest>
  </PropertyGroup>

  <ItemGroup>
    <!-- Only interfaces needed here; schema types belong to QuestForge.Plugin (composition root) -->
    <ProjectReference Include="..\QuestForge.Adapters\QuestForge.Adapters.csproj" />
    <!-- ECommons: MIT, brings EzIPC helpers, AtkEvent stubs, Lifestream/vnavmesh/TextAdvance IPC shims -->
    <PackageReference Include="ECommons" Version="3.*" />
  </ItemGroup>
</Project>
```

**Why this project does NOT reference `QuestForge.Engine`:** the engine is host-agnostic. Adapters do not need it. Composition happens one layer up in `QuestForge.Plugin`. Keeping the engine out of the adapter project's reference graph means a circular dependency or an accidental engine-side Dalamud dependency cannot compile.

**Why no `QuestForge.Schema` reference here:** schema types are consumed by `QuestForge.Plugin` when loading quest files. No adapter implementation touches `QuestDefinition` or step types directly. The reference belongs to the composition root, not here.

### 2. `QuestForge.Plugin` is the composition root and the only project that owns `IDalamudPlugin`

```xml
<!-- QuestForge.Plugin.csproj — final shape -->
<Project Sdk="Dalamud.NET.Sdk/13.0.0">
  <PropertyGroup>
    <TargetFramework>net10.0-windows7.0</TargetFramework>
    <Platforms>x64</Platforms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- This project produces the plugin DLL; manifest is auto-generated by the SDK from these -->
    <Description>QuestForge — FFXIV quest automation</Description>
    <Authors>QuestForge contributors</Authors>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\QuestForge.Adapters\QuestForge.Adapters.csproj" />
    <ProjectReference Include="..\QuestForge.Adapters.Dalamud\QuestForge.Adapters.Dalamud.csproj" />
    <ProjectReference Include="..\QuestForge.Adapters.Fakes\QuestForge.Adapters.Fakes.csproj" />
    <ProjectReference Include="..\QuestForge.Engine\QuestForge.Engine.csproj" />
    <ProjectReference Include="..\QuestForge.Schema\QuestForge.Schema.csproj" />
    <PackageReference Include="ECommons" Version="3.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.5" />
  </ItemGroup>
</Project>
```

**Why `QuestForge.Adapters.Fakes` is referenced here:** Phase 6 needs the recording proxies (`RecordingGameStateProvider`, `RecordingQuestState`), which Phase 5 placed in `QuestForge.Adapters.Fakes/Recording/`. The plugin wraps the Dalamud-backed providers with these proxies before handing them to the engine. The naming is awkward ("fakes" in production) and will be revisited in §A. For Phase 6 we accept the naming and move on.

### 3. The framework-thread invariant (load-bearing)

Dalamud requires most game-state reads to occur on the framework thread. Off-thread access to `IClientState`, `IObjectTable`, `QuestManager`, and similar produces inconsistent reads at best and crashes at worst.

**Decision:** the engine is driven from the framework `Update` delegate, synchronously per tick. Adapter implementations therefore do not need to marshal back to the framework thread — they are already on it. `Task<T>` return types from adapters are still required by the interface contract, but every Dalamud-backed adapter returns `Task.FromResult(...)` after performing the read inline. They do not `await` anything that hops threads.

**Concrete rules:**

1. `Plugin.cs` subscribes to `IFramework.Update`.
2. The `Update` handler starts `_host.TickAsync(_cts.Token)` and stores it in `_inflight`. If `_inflight` is not yet completed on the next frame, the handler returns immediately (no overlapping ticks). Because all Phase 6 adapters return `Task.FromResult` synchronously, ticks complete within the same frame in practice.
3. Adapter implementations never spawn background tasks, never `await Task.Delay`, never call `Task.Run`. They do work synchronously and wrap the result in `Task.FromResult`.
4. vnavmesh and Lifestream IPC are thread-safe (SPIKE_NOTES.md confirms), but we still call them from the framework thread for symmetry — there is no reason to fan out.
5. The cancellation token threaded through `Tick(ct)` is the plugin's lifecycle token, cancelled on `Dispose`.

**Risk:** if any future code path runs the engine off the framework thread (e.g., a UI button handler in Phase 8 calling `engine.Tick(ct)` directly), the Dalamud read adapters will read stale data or crash. This must be documented as a comment on `EngineHost.RunTick()` and enforced by inspection. There is no thread assertion in v1; adding one is a Phase 8 chore when the UI introduces real risk.

### 4. `Plugin.cs` is the composition root, not a god-class

`Plugin.cs` implements `IDalamudPlugin` and does exactly four things:

1. Receives `[PluginService]` injections in its constructor.
2. Hands them to `PluginServices` (a record holding every service the plugin uses).
3. Constructs `EngineHost`, which owns the adapter graph, recording proxies, `TraceWriter`, and `QuestEngine`.
4. Registers `/qf` with `ICommandManager` and subscribes `IFramework.Update`.

The actual adapter wiring lives in `EngineHost`. The slash-command handlers live in `Commands/QfCommand.cs`. The plugin entry stays thin.

```csharp
// QuestForge.Plugin/Plugin.cs (sketch — actual implementation Phase 6)
public sealed class Plugin : IDalamudPlugin
{
    private readonly EngineHost _host;
    private readonly QfCommand _command;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly CancellationTokenSource _cts = new();

    public Plugin(
        [RequiredVersion("1.0")] IDalamudPluginInterface pi,
        IFramework framework, IClientState clientState, ICondition condition,
        IObjectTable objectTable, IDataManager dataManager, ITargetManager targetManager,
        ICommandManager commandManager, IChatGui chatGui, IGameGui gameGui,
        IPluginLog log, IGameInteropProvider hooks)
    {
        _framework = framework; _log = log;
        var services = new PluginServices(pi, framework, clientState, condition,
            objectTable, dataManager, targetManager, chatGui, gameGui, log, hooks);
        _host = new EngineHost(services);
        _command = new QfCommand(_host, commandManager, chatGui, log);
        _framework.Update += OnFrameworkUpdate;
    }

    private Task? _inflight;
    private void OnFrameworkUpdate(IFramework _)
    {
        if (_inflight is { IsCompleted: false }) return;
        _inflight = _host.TickAsync(_cts.Token);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _cts.Cancel();
        _command.Dispose();
        _host.Dispose();
        _cts.Dispose();
    }

    public string Name => "QuestForge";
}
```

### 5. `EngineHost` owns the recording-proxy wiring

The engine constructor takes interfaces. The host constructs concrete Dalamud adapters, wraps `IGameStateProvider` and `IQuestState` with the Phase 5 recording proxies, opens a `TraceWriter` for each run, and constructs the engine.

```csharp
// QuestForge.Plugin/EngineHost.cs (sketch)
public sealed class EngineHost : IDisposable
{
    private readonly PluginServices _services;
    private readonly DalamudGameStateProvider _gameStateInner;
    private readonly DalamudQuestState _questStateInner;
    private readonly VnavmeshNavigator _navigator;
    private readonly LifestreamTeleporter _teleporter;
    private readonly DalamudInteractor _interactor;
    private readonly WrathComboAdapter _combat;
    private readonly DalamudGearManager _gear;
    private readonly NullMinigameSkipper _minigames;
    private readonly LuminaDialogueResolver _dialogue;
    private readonly SeededTimingProfile _timing;

    private TraceWriter? _trace;          // recreated per run, file per run
    private QuestEngine? _engine;
    private string? _runId;

    public EngineHost(PluginServices services)
    {
        _services = services;
        _gameStateInner   = new DalamudGameStateProvider(services);
        _questStateInner  = new DalamudQuestState(services);
        _navigator        = new VnavmeshNavigator(services);
        _teleporter       = new LifestreamTeleporter(services);
        _interactor       = new DalamudInteractor(services);
        _combat           = new WrathComboAdapter(services);
        _gear             = new DalamudGearManager(services);
        _minigames        = new NullMinigameSkipper();
        _dialogue         = new LuminaDialogueResolver(services);
        _timing           = new SeededTimingProfile(seed: 0);  // reseeded per BeginRun
    }

    public void BeginRun(QuestDefinition quest, string runId)
    {
        _runId = runId;
        _trace?.Dispose();
        _trace = TraceWriter.OpenFile(BuildTraceFilePath(runId));
        _timing.Reseed(StableSeedFromRunId(runId));

        IGameStateProvider gs = new RecordingGameStateProvider(
            _gameStateInner, _trace, () => _runId, skipIfNoRunId: true);
        IQuestState qs = new RecordingQuestState(
            _questStateInner, _trace, () => _runId, skipIfNoRunId: true);

        _engine = new QuestEngine(
            gs, qs, _navigator, _teleporter, _interactor,
            _combat, _gear, _minigames, _dialogue, _timing,
            _trace, new DalamudLogger<QuestEngine>(_services.Log));
        _engine.StartQuest(quest);
        _engine.BeginRun(runId);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (_engine is null) return;  // no active run
        var action = await _engine.Tick(ct);
        await DispatchAction(action, ct);
    }

    private async Task DispatchAction(EngineAction action, CancellationToken ct)
    {
        switch (action)
        {
            case EngineAction.Navigate n: await _navigator.NavigateTo(n.Destination, n.Options, ct); break;
            case EngineAction.Interact i: await _interactor.InteractWith(i.Target, ct); break;
            case EngineAction.Wait:      break;  // no-op; engine re-ticks
            case EngineAction.AwaitUser au: _services.Log.Info($"AwaitUser: {au.Reason}"); EndRun(); break;
            case EngineAction.Done:       _services.Log.Info("Quest complete"); EndRun(); break;
        }
    }

    private void EndRun() { _engine = null; _trace?.Dispose(); _trace = null; _runId = null; }

    public void Dispose() { _trace?.Dispose(); }
}
```

**The recording proxies wrap only `IGameStateProvider` and `IQuestState`.** Phase 5 deliberately did not extend recording to the other eight interfaces. That stays true in Phase 6. Action events (Navigate, Interact) are emitted by the dispatch loop above, identical to how the Phase 4/5 `EngineTestHarness.RunToCompletion` already emits them. This mirrors decision §5 of PHASE_5_PLAN.md: the recording layer captures state reads only; action emission is the harness's (now `EngineHost`'s) job.

### 6. Adapter "smoke commands" are the Phase 6 test surface

There is no CI for the Dalamud-backed adapters in Phase 6. They cannot be unit-tested, and replay is Phase 7. The testable surface is the `/qf` slash command:

```
/qf test gamestate    — print player zone, position, job, level, in-combat, instance kind
/qf test queststate   — print current sequence, completion state for 66130
/qf test teleport <id>— teleport to the named aetheryte (e.g. /qf test teleport 9 for Ul'dah)
/qf test navigate <x> <y> <z> — navigate to a position in current zone (3yd stop range)
/qf test interact <npcId> — target NPC by DataId, call InteractWithObject
/qf run 66130         — begin a real engine run on quest 66130 (the done-criterion test)
/qf stop              — cancel run, close trace
```

Each command writes to `IChatGui` for human inspection and `IPluginLog` for postmortem. The done criterion (§6 below) is that `/qf run 66130` succeeds. Smoke commands are the rungs of the ladder.

### 7. Quest 66130 quirks are encoded in the Phase 6 done criteria, not in adapter contracts

SPIKE_NOTES.md surfaced six architectural surprises. Three are already addressed in `QuestForge.Adapters` (see ADAPTERS.md):

- The vnavmesh "stopping distance" parameter is `NavigationOptions.StoppingDistance` (decision §7 of NEXT_STEPS).
- The "seq=0 ambiguous" finding lives in `IQuestState.IsQuestComplete` (separate query).
- The "Close to Home has 3 variants" finding does not affect Phase 6 (we test only 66130).

Three remain Phase 6 implementation details:

- The internal-vs-row-ID translation (`rowId & 0xFFFF` for `QuestManager.GetQuestById`; `rowId | 0x10000u` for the reverse) lives in `DalamudQuestState`.
- The three Talk addon variants are handled inside `DalamudInteractor.AdvanceDialogue` using the `CreateAtkEvent` + `StateFlags=132` pattern.
- The `JournalAccept`-without-`Talk` flow is handled inside `DalamudInteractor.AcceptQuest` with a seq-based escape hatch.

These three are quirks of the Dalamud-backed implementation. They do not surface as new adapter contract members.

### 8. The naming awkwardness ("RecordingGameStateProvider in `Adapters.Fakes`") is accepted for Phase 6

PHASE_5_PLAN.md decision 1 acknowledged the proxies live in `QuestForge.Adapters.Fakes` for now and would be reconsidered in Phase 6. Phase 6 keeps them there. Hoisting them into `QuestForge.Adapters` proper is a tiny mechanical change that would require updating every test file's `using` statement. Phase 6 has more important things to spend churn on. The hoist is a Phase 7 chore when other Phase 7 namespace work (`Adapters.Replay`?) is happening anyway.

---

## Task 1 — `QuestForge.Adapters.Dalamud` project skeleton

### 1.1 Csproj

As shown in §1 above. The Dalamud SDK provides `IDalamudPluginInterface`, `ClientState`, `IObjectTable`, `ICondition`, `IDataManager`, `IGameGui`, `IChatGui`, `IFramework`, `IPluginLog`, `IGameInteropProvider`, `ITargetManager`, `ICommandManager`. ECommons provides `EzIPCSubscriber` for typed IPC and `AutoCutsceneSkipper` (MIT).

### 1.2 PluginServices

A record passed to every adapter constructor. Single point of injection.

```csharp
// QuestForge.Adapters.Dalamud/PluginServices.cs
public sealed record PluginServices(
    IDalamudPluginInterface PluginInterface,
    IFramework Framework,
    IClientState ClientState,
    ICondition Condition,
    IObjectTable ObjectTable,
    IDataManager DataManager,
    ITargetManager TargetManager,
    IChatGui ChatGui,
    IGameGui GameGui,
    IPluginLog Log,
    IGameInteropProvider Hooks);
```

This record lives in `QuestForge.Adapters.Dalamud` so adapter implementations don't need to import per-service `using`s; one parameter, one dependency.

### 1.3 Folder layout (mirrors `QuestForge.Adapters.Fakes` for symmetry)

```
QuestForge.Adapters.Dalamud/
  PluginServices.cs
  State/
    DalamudGameStateProvider.cs
    DalamudQuestState.cs
  Movement/
    LifestreamTeleporter.cs
    VnavmeshNavigator.cs
  Interaction/
    DalamudInteractor.cs
    LuminaDialogueResolver.cs
  Combat/
    WrathComboAdapter.cs
  Gear/
    DalamudGearManager.cs
  Timing/
    SeededTimingProfile.cs
  Minigames/
    NullMinigameSkipper.cs
  Ipc/
    VnavmeshIpc.cs           ← typed wrapper around vnavmesh gates
    LifestreamIpc.cs         ← typed wrapper around Lifestream gates
    TextAdvanceIpc.cs        ← typed wrapper around TextAdvance gates (used optionally)
    WrathComboIpc.cs         ← typed wrapper around WrathCombo gates
```

The `Ipc/` folder is non-trivial. Each file is a small class that subscribes the typed gates from SPIKE_NOTES.md §IPC contract findings once at plugin load, then exposes plain C# methods. This isolates the IPC-shape brittleness (gate names, signatures) from the adapter logic. When a dependency plugin changes its IPC contract, only one file in `Ipc/` changes.

---

## Task 2 — `IGameStateProvider` (first adapter, foundation for everything else)

### 2.1 Implementation strategy

```csharp
// QuestForge.Adapters.Dalamud/State/DalamudGameStateProvider.cs
public sealed class DalamudGameStateProvider : IGameStateProvider
{
    private readonly PluginServices _svc;
    public DalamudGameStateProvider(PluginServices svc) => _svc = svc;

    public Task<Result<ZoneId>> GetPlayerZone(CancellationToken ct)
        => Task.FromResult<Result<ZoneId>>(
            new Result<ZoneId>.Success(new ZoneId(_svc.ClientState.TerritoryType)));

    public Task<Result<WorldPosition>> GetPlayerPosition(CancellationToken ct)
    {
        var local = _svc.ObjectTable[0];  // SDK 15: LocalPlayer is gone; index 0 is the local player
        if (local is null)
            return Task.FromResult<Result<WorldPosition>>(
                Result.Fail<WorldPosition>("noLocalPlayer", "ObjectTable[0] is null"));
        var p = local.Position;
        return Task.FromResult<Result<WorldPosition>>(
            new Result<WorldPosition>.Success(new WorldPosition(p.X, p.Y, p.Z)));
    }

    public Task<Result<NpcReference?>> FindNpc(NpcId npc, CancellationToken ct)
    {
        var local = _svc.ObjectTable[0];
        foreach (var obj in _svc.ObjectTable)
        {
            if (obj is null || obj.DataId != npc.Value) continue;
            if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;
            var pos = new WorldPosition(obj.Position.X, obj.Position.Y, obj.Position.Z);
            var dist = local is null ? float.PositiveInfinity
                : Vector3.Distance(local.Position, obj.Position);
            return Task.FromResult<Result<NpcReference?>>(
                new Result<NpcReference?>.Success(new NpcReference(npc, pos, dist)));
        }
        return Task.FromResult<Result<NpcReference?>>(
            new Result<NpcReference?>.Success(null));  // not in range — null, not Failure
    }

    // ... 16 more methods, all the same shape: read inline, wrap in Task.FromResult, return Result<T>
}
```

**Critical detail from SPIKE_NOTES.md surprise 2:** the NPC raw position returned in `NpcReference.Position` is often inside geometry (Wymond, Momodi). The engine does NOT navigate to this position directly. `INavigator.NavigateTo` is called with `NavigationOptions.StoppingDistance = 3.0f`, and `vnavmesh.SimpleMove.PathfindAndMoveCloseTo` stops the player at the nearest reachable navmesh polygon within that distance. This is handled in `VnavmeshNavigator`, not here. `DalamudGameStateProvider.FindNpc` returns the raw position truthfully.

**Composite read of `PlayerStateSnapshot`:** atomic snapshot — all fields read in a single contiguous block before returning. No `await` in between. The recording proxy already records this as one observation key (`GetPlayerState`).

**`GetCurrentInstanceKind`:** combines `ClientState.TerritoryType` (cross-referenced against Lumina's `TerritoryType` sheet's `IntendedUse` and `ContentFinderCondition`) with `Condition[ConditionFlag.BoundByDuty]`. Phase 6 implements this with a Lumina table lookup at plugin start; the table maps `TerritoryId → InstanceKind` and is cached.

**Open questions deferred to Task 2 implementation:** `DutyAvailability`, `NewGamePlusState`, `GetAttunedAetherytes`, `TravelCapability` — all return reasonable values from Lumina + game-state reads, but the implementation details are non-trivial. Phase 6 implements them with "good enough" defaults; quest 66130 only needs `GetPlayerZone`, `GetPlayerPosition`, `FindNpc`, `GetUiState`, `IsPlayerInCombat`, `GetCurrentInstanceKind`. The rest may return safe placeholder values (e.g., `NewGamePlusState(false, null, false)`) and be filled in when a quest needs them.

### 2.2 Smoke verification

```
/qf test gamestate
> zone: 182 (Ul'dah Steps of Nald — new player instance)
> position: (35.56, 4.0, -151.18)
> job: 1 (Gladiator)
> jobLevel: 1
> inCombat: false
> instanceKind: None
> uiState: { dialogueOpen=false, cutscenePlaying=false, ... }
```

Stand in a known location, run the command, verify the printed values match `/xldata`. This is the minimum bar before `IQuestState` work begins.

---

## Task 3 — `IQuestState`

### 3.1 The internal-vs-row-ID translation

**Read from SPIKE_NOTES.md §Quest state.** FFXIV row IDs in the schema (`uint`, range starts at 0x10000) are translated to in-game IDs (`ushort`) by `rowId & 0xFFFF`. Both `QuestManager.GetQuestById` and `QuestManager.IsQuestComplete` expect the in-game ID.

```csharp
// QuestForge.Adapters.Dalamud/State/DalamudQuestState.cs
public sealed class DalamudQuestState : IQuestState
{
    private readonly PluginServices _svc;
    private readonly Lazy<ExcelSheet<Quest>> _questSheet;
    public DalamudQuestState(PluginServices svc)
    {
        _svc = svc;
        _questSheet = new(() => svc.DataManager.GetExcelSheet<Quest>()
            ?? throw new InvalidOperationException("Lumina Quest sheet unavailable"));
    }

    private static ushort ToInternal(QuestId q) => (ushort)(q.Value & 0xFFFF);

    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            var q = qm->GetQuestById(ToInternal(quest));
            // q == null → quest not in journal (could mean "never accepted" OR "completed")
            // The engine separately calls IsQuestComplete to disambiguate
            var seq = q == null ? 0 : q->Sequence;
            return Task.FromResult<Result<int>>(new Result<int>.Success(seq));
        }
    }

    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Success(QuestManager.IsQuestComplete(ToInternal(quest))));
        }
    }

    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
    {
        unsafe
        {
            var qm = QuestManager.Instance();
            for (var i = 0; i < 30; i++)
            {
                if (qm->NormalQuests[i].QuestId == ToInternal(quest))
                    return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
            }
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(false));
        }
    }

    // GetQuestStatus composes GetQuestSequence + IsQuestComplete + IsQuestAccepted
    // GetQuestFlags reads from QuestWork in QuestManager (16-bit bitfield per active quest)
    // IsQuestAvailable + WhyUnavailable read Quest sheet's PreviousQuest, ClassJobRequired, etc.
    // GetAvailableQuestRewards reads JournalResult addon when quest is at seq=255 + reading the Quest sheet
}
```

**Engine path for quest 66130:**
- Before accept: `GetQuestSequence(66130) → 0`, `IsQuestComplete(66130) → false` → engine emits `Interact(Wymond)` to start the quest.
- After accept (skipping 0→255 directly, per SPIKE_NOTES.md): `GetQuestSequence → 255`.
- After turn-in: `GetQuestSequence → 0`, `IsQuestComplete → true` → engine emits `Done`.

### 3.2 Smoke verification

```
/qf test queststate
> 66130: status=Available, seq=0, complete=false, accepted=false
> (after manual /qf run 66130 acceptance)
> 66130: status=Accepted, seq=255, complete=false, accepted=true
> (after manual completion)
> 66130: status=Complete, seq=0, complete=true, accepted=false
```

---

## Task 4 — `ITeleporter` (Lifestream)

### 4.1 Lifestream IPC

Per SPIKE_NOTES.md §Lifestream:

```csharp
// QuestForge.Adapters.Dalamud/Ipc/LifestreamIpc.cs
public sealed class LifestreamIpc
{
    private readonly Func<uint, byte, bool> _teleport;   // Lifestream.Teleport
    private readonly Func<bool> _isBusy;                  // Lifestream.IsBusy

    public LifestreamIpc(IDalamudPluginInterface pi)
    {
        _teleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport").InvokeFunc;
        _isBusy   = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc;
    }

    public bool Teleport(uint aetheryteId, byte subIndex = 0) => _teleport(aetheryteId, subIndex);
    public bool IsBusy() => _isBusy();
}
```

### 4.2 Teleporter adapter

```csharp
public sealed class LifestreamTeleporter : ITeleporter
{
    private readonly PluginServices _svc;
    private readonly LifestreamIpc _ipc;

    public LifestreamTeleporter(PluginServices svc)
    {
        _svc = svc;
        _ipc = new LifestreamIpc(svc.PluginInterface);
    }

    public Task<Result<TeleportOutcome>> TeleportToAetheryte(AetheryteId destination, CancellationToken ct)
    {
        // Pre-check: attuned? (Lifestream returns false on attunement/cooldown without distinction)
        if (!IsAetheryteAttunedInline(destination))
            return Task.FromResult<Result<TeleportOutcome>>(
                Result.Fail<TeleportOutcome>("notAttuned", $"aetheryte {destination.Value}"));
        if (_ipc.IsBusy())
            return Task.FromResult<Result<TeleportOutcome>>(
                new Result<TeleportOutcome>.Success(TeleportOutcome.OnCooldown));

        var ok = _ipc.Teleport(destination.Value, 0);
        if (!ok)
            return Task.FromResult<Result<TeleportOutcome>>(
                Result.Fail<TeleportOutcome>("lifestream-rejected"));

        // Fire-and-forget: Lifestream accepted the request; return Arrived immediately.
        // The engine re-observes GetPlayerZone() on the next tick to detect actual arrival.
        // Do NOT poll here — that would violate the framework-thread invariant (§3):
        // awaiting Task.Yield() inside an Update-frame delegate parks the tick indefinitely.
        return Task.FromResult<Result<TeleportOutcome>>(
            new Result<TeleportOutcome>.Success(TeleportOutcome.Arrived));
    }
    // ... other methods follow the same shape
}
```

### 4.3 Smoke verification

```
/qf test teleport 9
> Lifestream returned true
> ... (wait 5 seconds) ...
> Arrived at zone 130 (Ul'dah Steps of Nald veteran)
```

Quest 66130 starts in zone 182 with no aetherytes attuned — teleport is not exercised by the done-criterion quest. The smoke command still verifies the IPC wires up.

---

## Task 5 — `INavigator` (vnavmesh)

### 5.1 Vnavmesh IPC

Per SPIKE_NOTES.md §vnavmesh (confirmed gate names):

```csharp
// QuestForge.Adapters.Dalamud/Ipc/VnavmeshIpc.cs
public sealed class VnavmeshIpc
{
    private readonly Func<bool> _navIsReady;
    private readonly Func<Vector3, bool, float, bool> _pathfindAndMoveCloseTo;
    private readonly Func<bool> _pathfindInProgress;
    private readonly Func<bool> _pathIsRunning;
    private readonly Func<bool> _pathStop;
    private readonly Func<Vector3, float, float, Vector3?> _nearestPointReachable;

    public VnavmeshIpc(IDalamudPluginInterface pi)
    {
        _navIsReady              = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc;
        _pathfindAndMoveCloseTo  = pi.GetIpcSubscriber<Vector3, bool, float, bool>(
                                    "vnavmesh.SimpleMove.PathfindAndMoveCloseTo").InvokeFunc;
        _pathfindInProgress      = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc;
        _pathIsRunning           = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc;
        _pathStop                = pi.GetIpcSubscriber<bool>("vnavmesh.Path.Stop").InvokeFunc;
        _nearestPointReachable   = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>(
                                    "vnavmesh.Query.Mesh.NearestPointReachable").InvokeFunc;
    }

    public bool NavIsReady() => _navIsReady();
    public bool PathfindAndMoveCloseTo(Vector3 dest, bool fly, float range) => _pathfindAndMoveCloseTo(dest, fly, range);
    public bool PathfindInProgress() => _pathfindInProgress();
    public bool PathIsRunning() => _pathIsRunning();
    public bool PathStop() => _pathStop();
    public Vector3? NearestPointReachable(Vector3 p, float yMin, float yMax) => _nearestPointReachable(p, yMin, yMax);
}
```

### 5.2 Navigator adapter

```csharp
public sealed class VnavmeshNavigator : INavigator
{
    private readonly PluginServices _svc;
    private readonly VnavmeshIpc _ipc;

    public VnavmeshNavigator(PluginServices svc)
    {
        _svc = svc;
        _ipc = new VnavmeshIpc(svc.PluginInterface);
    }

    public Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination, NavigationOptions options, CancellationToken ct)
    {
        if (!_ipc.NavIsReady())
            return Task.FromResult<Result<NavigationOutcome>>(
                new Result<NavigationOutcome>.Success(NavigationOutcome.NavmeshUnavailable));

        var dest = new Vector3(destination.X, destination.Y, destination.Z);
        var ok = _ipc.PathfindAndMoveCloseTo(dest, options.UseFlight, options.StoppingDistance);
        if (!ok)
            return Task.FromResult<Result<NavigationOutcome>>(
                Result.Fail<NavigationOutcome>("pathfindRejected"));

        // Fire-and-forget: vnavmesh has accepted the request but is computing pathing async.
        // The engine observes IsNavigating next tick; postconditions decide arrival.
        return Task.FromResult<Result<NavigationOutcome>>(
            new Result<NavigationOutcome>.Success(NavigationOutcome.Arrived));
    }

    public Task<Result<bool>> IsNavigating(CancellationToken ct)
    {
        // SPIKE_NOTES.md async timing gap: Path.IsRunning returns false for ~30 ticks
        // after PathfindAndMoveCloseTo while pathfinding computes.
        // Treat "pathfinding in progress" as "still navigating" for the engine's purposes.
        var navigating = _ipc.PathIsRunning() || _ipc.PathfindInProgress();
        return Task.FromResult<Result<bool>>(new Result<bool>.Success(navigating));
    }

    public Task<Result<Unit>> Stop(CancellationToken ct)
    {
        _ipc.PathStop();
        return Task.FromResult<Result<Unit>>(new Result<Unit>.Success(Unit.Value));
    }

    public Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct)
    {
        // vnavmesh doesn't expose a generation-progress IPC. NavIsReady is the only signal.
        var status = _ipc.NavIsReady() ? NavmeshStatus.Ready : NavmeshStatus.Generating;
        return Task.FromResult<Result<NavmeshInfo>>(
            new Result<NavmeshInfo>.Success(new NavmeshInfo(status, null, null)));
    }
}
```

**Critical contract issue: `NavigateTo` returns `Arrived` immediately on request acceptance.** The engine's `EngineAction.Navigate` handler dispatches this once per tick. If the player is still moving on the next tick, `IsNavigating` returns true and the engine emits `Wait` (waiting for postcondition). This works because `QuestEngine.Tick` is idempotent on tick boundaries — emitting `Navigate` again with the same destination is harmless to vnavmesh (the new request lands on the same path).

For more precise semantics, post-Phase 6 work may refactor `NavigationOutcome` into a state-machine where `Arrived` is only emitted on actual arrival. Phase 6 accepts the looser contract because the engine already handles it via postcondition re-evaluation.

### 5.3 Smoke verification

```
/qf test navigate 35.56 4.0 -151.18
> NavIsReady: true
> PathfindAndMoveCloseTo (3yd range): true
> ... player walks ...
> Arrived (PathIsRunning false, distance to dest < 3yd)
```

Run this in zone 182 starting from the new-player spawn. Expect the player to walk to Wymond's approach position.

---

## Task 6 — `IInteractor` (TargetSystem + Talk addon + JournalAccept/Result)

### 6.1 Interaction primitives

Per SPIKE_NOTES.md §NPC interaction:

```csharp
public Task<Result<InteractOutcome>> InteractWith(NpcId npc, CancellationToken ct)
{
    foreach (var obj in _svc.ObjectTable)
    {
        if (obj is null || obj.DataId != npc.Value) continue;
        _svc.TargetManager.Target = obj;
        unsafe
        {
            var ts = TargetSystem.Instance();
            if (ts->Target != null)
                ts->InteractWithObject(ts->Target, false);
        }
        return Task.FromResult<Result<InteractOutcome>>(
            new Result<InteractOutcome>.Success(InteractOutcome.DialogueOpened));
    }
    return Task.FromResult<Result<InteractOutcome>>(
        new Result<InteractOutcome>.Success(InteractOutcome.NpcNotFound));
}
```

**Range check:** the engine doesn't pre-check distance because `INavigator.NavigateTo` was responsible for getting close. If the NPC is out of range, FFXIV will respond with "too far to interact"; the engine observes that the Talk addon never opens, and the step's recovery applies. Phase 6 hardcodes a re-attempt every 5 seconds inside the engine's tick loop (PR-able later as `RetryConfig`).

### 6.2 Talk addon advancement (three variants)

Per SPIKE_NOTES.md §Talk addon. The implementation lives in `DalamudInteractor.AdvanceDialogue` and uses the `CreateAtkEvent`-with-`StateFlags=132` pattern for all three variants. Throttle to once every ~15 ticks.

```csharp
public Task<Result<DialogueOutcome>> AdvanceDialogue(CancellationToken ct)
{
    var addon = (AtkUnitBase*)_svc.GameGui.GetAddonByName("Talk");
    if (addon is null || !addon->IsVisible)
        return Task.FromResult<Result<DialogueOutcome>>(
            new Result<DialogueOutcome>.Success(DialogueOutcome.NoActiveDialogue));

    // Variant 1: node 132 present (normal NPC dialogue)
    // Variant 2: WindowHeaderCollisionNode non-null (system Talk)
    // Variant 3: WindowHeaderCollisionNode null, CollisionNodeList has RespondToMouse node (NPC quest event)
    // All three use the same AtkEvent with Listener+Target+StateFlags=132.
    var evt = stackalloc AtkEvent[1];
    evt[0].Listener = (AtkEventListener*)addon;
    evt[0].Target   = &AtkStage.Instance()->AtkEventTarget;
    evt[0].State    = new AtkEventState { StateFlags = (AtkEventStateFlags)132 };
    var data = stackalloc AtkEventData[1];
    addon->ReceiveEvent(AtkEventType.MouseDown,  0, evt, data);
    addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
    addon->ReceiveEvent(AtkEventType.MouseUp,    0, evt, data);

    return Task.FromResult<Result<DialogueOutcome>>(
        new Result<DialogueOutcome>.Success(DialogueOutcome.Advanced));
}
```

**The throttle is the adapter's responsibility, not the engine's.** The adapter tracks the last `AdvanceDialogue` call's `DateTime` and returns `DialogueOutcome.Advanced` without doing work if called within 15 ticks. This means the engine can call `AdvanceDialogue` on every tick safely; only every 15th tick produces a click.

### 6.3 AcceptQuest (JournalAccept addon)

Per SPIKE_NOTES.md §JournalAccept. Button id 44; can appear without a preceding Talk.

```csharp
public Task<Result<Unit>> AcceptQuest(QuestId quest, CancellationToken ct)
{
    var addon = (AtkUnitBase*)_svc.GameGui.GetAddonByName("JournalAccept");
    if (addon is null || !addon->IsVisible)
        return Task.FromResult<Result<Unit>>(Result.Fail("noAcceptDialog"));
    var btn = addon->GetComponentButtonById(44);
    if (btn is null)
        return Task.FromResult<Result<Unit>>(Result.Fail("acceptButtonMissing"));
    var btnResNode = btn->AtkComponentBase.OwnerNode->AtkResNode;
    var evt = btnResNode.AtkEventManager.Event;
    addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt);
    return Task.FromResult<Result<Unit>>(new Result<Unit>.Success(Unit.Value));
}
```

`CompleteQuest` is analogous, targeting `JournalResult` and button id 37.

### 6.4 Quest 66130 flow

The engine, given the schema for quest 66130, emits this sequence:

1. Initial state: zone=182, seq=0, complete=false → engine finds Wymond is the AcceptFrom NPC → `EngineAction.Interact(Wymond/1003987)`.
2. `DalamudInteractor.InteractWith(Wymond)` targets him and triggers interaction.
3. Talk addon opens with Wymond's dialogue (Variant 3 per SPIKE_NOTES.md).
4. Engine ticks; on each tick, the adapter `AdvanceDialogue` clicks (throttled to every 15 ticks).
5. After several advances, Wymond's dialogue closes and **`JournalAccept` opens** (without preceding Talk, per SPIKE_NOTES.md surprise).
6. Engine's interact step has a postcondition `questAccepted(66130)`. As long as that's false, engine keeps emitting `Interact(Wymond)`, which the adapter no-ops (no NPC to find) but the AdvanceDialogue advances the Talk/JournalAccept that's already open. Phase 6 quest 66130 schema places an `AcceptStep` after the initial `TalkStep`; the engine emits `AcceptQuest(66130)` which clicks the JournalAccept button.
7. Quest accepted → seq goes 0→255.
8. Engine sees seq=255 → navigates to Momodi (35.56→21.84, etc.) → interacts → Talk advances → quest completes (CompleteQuest clicks JournalResult).
9. `IsQuestComplete(66130) → true` → `EngineAction.Done`.

### 6.5 Smoke verification

```
/qf test interact 1003987
> Found Wymond at (35.56, 4.0, -151.18)
> Targeted, interacted
> Talk addon opened (variant 3)
```

---

## Task 7 — Remaining adapters (stubs for Phase 6 done-criteria; full impl deferred)

Quest 66130 exercises `IGameStateProvider`, `IQuestState`, `INavigator`, `IInteractor`, `ITimingProfile`. The other five adapters (`ITeleporter` partially, `ICombat`, `IGearManager`, `IDialogueResolver`, `IMinigameSkipper`) are not exercised by the done-criteria quest.

**Decision for Phase 6:** ship them as **minimal-but-real** implementations:

| Adapter | Phase 6 implementation |
|---|---|
| `ICombat` (WrathComboAdapter) | IPC subscriber for the chosen plugin; all methods route through it. Quest 66130 does not enter combat, so this is exercised only by the smoke command `/qf test combat`. |
| `IGearManager` (DalamudGearManager) | Wraps the game's `/equiprecommended` command; `IsStylistAvailable` returns false in v1; `EquipBestGearViaStylist` returns `Failed` until Phase 9. |
| `IDialogueResolver` (LuminaDialogueResolver) | `ResolveText` reads the requested sheet by reference. `FindOptionByText` walks the open dialogue addon's option list. Not exercised by 66130 (no dialogue choices). |
| `IMinigameSkipper` (NullMinigameSkipper) | Every method returns `SkipOutcome.Unsupported`. Phase 10+ replaces this. |
| `ITimingProfile` (SeededTimingProfile) | Log-normal sampling per ADAPTERS.md §10.4. `Reseed(long seed)` is the entry point; `Plugin.cs` reseeds at `BeginRun`. |

The point is to have **all 10 adapter constructors green** so the engine's DI graph compiles and runs. Their methods can return placeholder failures for unimplemented paths; the engine never calls them during quest 66130.

---

## Task 7B — Engine extension: `accept` and `turn-in` step types

The Phase 4 engine only handles `TravelStep` and `TalkStep` in `ResolveActionForStep`. Quest 66130 in Phase 6 uses `AcceptStep` (to accept via `JournalAccept`) and a `TurnInStep`-shaped interaction (turn-in via `JournalResult`). The engine must be extended or the quest file must use only `talk` steps.

**Decision for Phase 6:** extend `QuestEngine.ResolveActionForStep` with two new cases:

```csharp
AcceptStep accept =>
    new EngineAction.AcceptQuest(new QuestId(accept.Target.NpcId)),

TurnInStep turnIn =>
    new EngineAction.TurnIn(new NpcId(turnIn.Target.NpcId)),
```

Add two new `EngineAction` subtypes:
```csharp
public sealed record AcceptQuest(QuestId Quest) : EngineAction;
public sealed record TurnIn(NpcId Target) : EngineAction;
```

`EngineHost.DispatchAction` handles them:
```csharp
case EngineAction.AcceptQuest aq: await _interactor.AcceptQuest(aq.Quest, ct); break;
case EngineAction.TurnIn ti:      await _interactor.CompleteQuest(_currentQuestId!, ct); break;
```

**Alternative:** keep the quest file using `talk` steps (as the Phase 4 fixture does) and defer `accept`/`turn-in` to Phase 10. In that case, the `JournalAccept` button-click and `JournalResult` button-click are handled by the interactor's `AdvanceDialogue` / `AcceptQuest` methods triggered by the engine continuing to emit `Interact` until the postcondition (`isQuestAccepted`, `isQuestComplete`) is satisfied.

**Phase 6 picks the alternative:** use `talk` steps throughout, matching the Phase 4 fixture. The `AcceptStep`/`TurnInStep` engine extension is Phase 10. The quest file (`66130-coming-to-uldah.json`) already uses `talk` everywhere — this works because the interactor's `AcceptQuest`/`CompleteQuest` calls are triggered by `questAccepted` / `isQuestComplete` postcondition evaluation, not by separate step types. The engine keeps emitting `Interact(Wymond)` until the postcondition clears.

This is documented explicitly so Phase 10 knows to add the engine extension. Quest files that use `accept`/`turn-in` step types will throw `NotSupportedException` until then.

---

## Task 8 — Plugin entry point

### 8.1 File structure

```
QuestForge.Plugin/
  Plugin.cs                   ← IDalamudPlugin, framework.Update subscription
  EngineHost.cs               ← engine + recording proxy + trace writer lifecycle
  Commands/
    QfCommand.cs              ← /qf dispatcher
    QfTestGameState.cs
    QfTestQuestState.cs
    QfTestNavigate.cs
    QfTestInteract.cs
    QfRunQuest.cs             ← /qf run 66130 — the done-criteria entry point
  QuestFileLoader.cs          ← loads quest JSON from on-disk fixtures (Phase 6 reads
                              ←   from a hardcoded path, no networking)
  Logging/
    DalamudLogger.cs          ← Microsoft.Extensions.Logging.ILogger<T> over IPluginLog
                              ←   (one-line bridge: ILogger<T>.Log → IPluginLog.Debug/Info/Warning/Error)
```

### 8.2 Trace file location

`<pluginConfigDir>/traces/<runId>.jsonl`. `IDalamudPluginInterface.GetPluginConfigDirectory()` returns the right directory. `runId` is generated as `DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + new Guid().ToString("N").Substring(0, 8)` — sortable + unique.

### 8.3 `/qf run 66130` flow

```csharp
public void RunQuest(uint questRowId)
{
    var path = Path.Combine(QuestDataDirectory, "arr", "msq",
                            $"{questRowId}-coming-to-uldah.json");
    if (!File.Exists(path)) { _chat.PrintError($"quest file not found: {path}"); return; }

    QuestDefinition? quest;
    try
    {
        var json = File.ReadAllText(path);
        quest = JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions);
    }
    catch (Exception ex) { _chat.PrintError($"Failed to load quest: {ex.Message}"); return; }
    if (quest is null) { _chat.PrintError("Quest file deserialized to null"); return; }
    // Note: structural validation (duplicate IDs, predicate syntax) is handled by qf-validate
    // in questforge-data CI. The plugin trusts that committed quest files are valid.

    var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}".Substring(0, 24);
    _host.BeginRun(quest, runId);
    _chat.Print($"QuestForge: run {runId} started for quest {questRowId}");
}
```

The framework `Update` then drives `engine.Tick` automatically. On `EngineAction.Done`, `EngineHost.EndRun` closes the trace and resets state. On `EngineAction.AwaitUser`, the run also ends (Phase 6 has no UI to surface awaitUser; it's logged and the user retries via `/qf run 66130` after manual intervention).

---

## Task 9 — Given-When-Then specifications

### 9.1 `IGameStateProvider.GetPlayerZone`

**Happy path:**
Given: player is standing in zone 182 (Ul'dah Steps of Nald, new-player instance).
When: `GetPlayerZone(ct)` is called.
Then: returns `Result<ZoneId>.Success(new ZoneId(182))`.

**Edge case — character not logged in:**
Given: `_svc.ClientState.LocalContentId == 0` (no character loaded).
When: `GetPlayerZone(ct)` is called.
Then: returns `Result<ZoneId>.Success(new ZoneId(0))`. Engine treats zone 0 as "not in game" and waits.

### 9.2 `IGameStateProvider.FindNpc`

**Happy path — NPC in range:**
Given: player is in zone 182, Wymond (DataId 1003987) is in the object table.
When: `FindNpc(new NpcId(1003987), ct)`.
Then: returns `Result<NpcReference?>.Success(new NpcReference(Id=1003987, Position=(35.56, 4.0, -151.18), DistanceToPlayer=<euclidean to player>))`.

**Edge case — NPC not in range or wrong zone:**
Given: player is in zone 130, Wymond is not loaded in the ObjectTable.
When: `FindNpc(new NpcId(1003987), ct)`.
Then: returns `Result<NpcReference?>.Success(null)`. Not a `Failure` — "not in range" is a valid observation.

**Edge case — DataId matches non-NPC entity:**
Given: an object with DataId 1003987 exists but has `ObjectKind == ObjectKind.Player`.
When: `FindNpc(new NpcId(1003987), ct)`.
Then: returns `Result<NpcReference?>.Success(null)`. NPC-kind filter rejected the match.

### 9.3 `IQuestState.IsQuestComplete` — internal-ID translation

**Happy path — quest completed:**
Given: row ID 66130, the player has completed "Coming to Ul'dah" previously.
When: `IsQuestComplete(new QuestId(66130), ct)`.
Then: `DalamudQuestState` calls `QuestManager.IsQuestComplete((ushort)(66130 & 0xFFFF)) == IsQuestComplete(610)` which returns true.
Returns `Result<bool>.Success(true)`.

**Happy path — quest never accepted:**
Given: a fresh character on a new server has never seen 66130.
When: `IsQuestComplete(new QuestId(66130), ct)`.
Then: `QuestManager.IsQuestComplete(610) == false`. Engine separately checks `IsQuestAccepted` to disambiguate seq=0.
Returns `Result<bool>.Success(false)`.

**Edge case — quest currently in progress:**
Given: quest is accepted, sequence is 255 (ready to turn in).
When: `IsQuestComplete(new QuestId(66130), ct)`.
Then: returns `Result<bool>.Success(false)`. Quest is not complete until turn-in.

### 9.4 `INavigator.NavigateTo`

**Happy path:**
Given: player is in zone 182 at (0, 0, 0), navmesh is ready (`NavIsReady → true`).
When: `NavigateTo(new WorldPosition(35.56f, 4.0f, -151.18f), new NavigationOptions(StoppingDistance: 3.0f), ct)`.
Then: `VnavmeshNavigator` calls `vnavmesh.SimpleMove.PathfindAndMoveCloseTo((35.56, 4.0, -151.18), fly: true, range: 3.0f)`, which returns true.
Returns `Result<NavigationOutcome>.Success(NavigationOutcome.Arrived)` immediately (fire-and-forget contract).

**Edge case — navmesh not ready:**
Given: just zone-changed; `NavIsReady → false`.
When: `NavigateTo(...)`.
Then: returns `Result<NavigationOutcome>.Success(NavigationOutcome.NavmeshUnavailable)`. Engine retries next tick.

**Edge case — pathfind rejected:**
Given: destination is outside the navmesh (across a zone boundary).
When: `NavigateTo(...)`.
Then: `PathfindAndMoveCloseTo` returns false.
Returns `Result.Fail<NavigationOutcome>("pathfindRejected")`.

**Postcondition observability:**
Given: `NavigateTo` returned `Arrived`.
When: the next engine tick calls `IsNavigating(ct)`.
Then: returns true while `PathIsRunning || PathfindInProgress` — engine emits `Wait` until both become false.

### 9.5 `IInteractor.InteractWith` and `AcceptQuest`

**Happy path — interact:**
Given: player is within 2.5 yalms of Wymond; `_svc.ObjectTable` contains a `BattleNpc`/`EventNpc` with DataId 1003987.
When: `InteractWith(new NpcId(1003987), ct)`.
Then: `TargetManager.Target` is set; `TargetSystem.Instance()->InteractWithObject(...)` is called. The Talk addon opens within ~5 ticks.
Returns `Result<InteractOutcome>.Success(InteractOutcome.DialogueOpened)`.

**Edge case — out of range:**
Given: player is 10 yalms from Wymond.
When: `InteractWith(new NpcId(1003987), ct)`.
Then: the game's interact handler rejects ("too far to interact"); Talk addon never opens.
The adapter still returns `DialogueOpened` (it can't detect the in-game rejection synchronously); the engine observes that the Talk addon is not visible after 15 ticks and re-emits `Interact` (or applies recovery).

**Happy path — accept quest:**
Given: the JournalAccept addon is open for quest 66130 (via `IGameGui.GetAddonByName("JournalAccept")`).
When: `AcceptQuest(new QuestId(66130), ct)`.
Then: button id 44 is clicked via `addon->ReceiveEvent(AtkEventType.ButtonClick, ...)`. The addon closes. `IQuestState.IsQuestAccepted(66130)` returns true on the next engine tick.
Returns `Result<Unit>.Success(Unit.Value)`.

**Edge case — JournalAccept not open:**
Given: no JournalAccept addon is visible.
When: `AcceptQuest(new QuestId(66130), ct)`.
Then: returns `Result.Fail<Unit>("noAcceptDialog")`. Engine retries next tick (the dialog may not have opened yet after the preceding `Interact`).

### 9.6 End-to-end (the done criterion)

**Happy path — full quest 66130:**

Given:
- A fresh Gladiator character standing in zone 182 near the new-player spawn.
- All 10 Dalamud-backed adapters are constructed and the recording proxies wrap `IGameStateProvider` and `IQuestState`.
- The quest file `66130-coming-to-uldah.json` parses cleanly.
- vnavmesh, Lifestream, TextAdvance are all loaded; WrathCombo or BossMod is configured.

When: the user runs `/qf run 66130` and waits.

Then, the engine's tick loop produces this sequence over ~41 seconds (matching SPIKE_NOTES.md):

1. Tick 1: `Tick` returns `EngineAction.Interact(NpcId(1003987))` (Wymond as AcceptFrom).
2. Plugin `EngineHost.DispatchAction` calls `_interactor.InteractWith(...)`. Target set, interaction triggered. Talk addon opens.
3. Ticks 2–N: engine continues to return `Interact(Wymond)` while Talk is open; `DalamudInteractor.AdvanceDialogue` throttled-clicks the addon every 15 ticks.
4. Eventually Talk closes and **JournalAccept** opens. Engine schema advances to `AcceptStep`, emits `AcceptQuest`. Plugin calls `_interactor.AcceptQuest`. Button 44 clicked.
5. `IsQuestAccepted(66130) → true`, `GetQuestSequence(66130) → 255`. Engine moves to sequence 255 steps.
6. Engine emits `Navigate((21.84, 7.0, -81.13), StoppingDistance=3)` toward Momodi's approach position.
7. Plugin calls `_navigator.NavigateTo`. vnavmesh moves the player. Ticks emit `Wait` while `IsNavigating`.
8. On arrival, engine emits `Interact(NpcId(1003988))` (Momodi).
9. Plugin interacts. Talk addon opens.
10. Engine advances dialogue; eventually JournalResult opens; engine emits `CompleteQuest`; button 37 clicked.
11. `IsQuestComplete(66130) → true`.
12. Engine emits `EngineAction.Done`. `EngineHost.EndRun` closes the trace.

The trace file at `<pluginConfigDir>/traces/<runId>.jsonl` contains:
- One `run.start` event with questId=66130.
- 30–100 `observation` events (every adapter read from the recording proxies).
- 5–10 `decision` events (one per `EngineAction` emitted).
- 5–10 `action.submitted` + `action.completed` pairs (emitted by `EngineHost.DispatchAction`).
- One `run.end` event with outcome="done".

The full trace is the deliverable; replay tests in Phase 7 will consume this exact file to regression-test the engine.

---

## Task 10 — Done criteria

The plugin satisfies Phase 6 when **all five** of the following hold:

1. `dotnet build` succeeds in Release; the resulting plugin DLL loads in Dalamud without errors.
2. `/qf test gamestate` prints plausible values for zone, position, job, instance kind in zone 182 immediately after character login.
3. `/qf test queststate` correctly distinguishes "not accepted" from "completed" for quest 66130 across the three states (never accepted, accepted/in-progress, completed).
4. `/qf test navigate <wymond-approach-pos>` walks the player to within 3 yalms of Wymond's approach position in under 10 seconds, starting from a known spawn.
5. `/qf run 66130` executes end-to-end: a fresh Gladiator character accepts the quest, walks to Momodi, turns it in, and the engine emits `EngineAction.Done` within 60 seconds. The trace file is written to disk and is valid JSONL.

All five must be demonstrated with a screen recording or a series of in-game screenshots in the PR description. The 103 Phase 5 fake-based tests continue to pass in CI (no regression).

---

## Implementation order

**Phase A — Project skeletons (1-2 days)**

1. Update `QuestForge.Adapters.Dalamud.csproj` with Dalamud SDK + ECommons (decision §1).
2. Update `QuestForge.Plugin.csproj` similarly (decision §2).
3. Create `PluginServices` record.
4. Create empty stubs for all 10 Dalamud adapters, each throwing `NotImplementedException`.
5. Create `Plugin.cs` skeleton with `[PluginService]` constructor, framework subscription, command registration.
6. Verify `dotnet build` succeeds with all references resolved.

**Phase B — Foundation adapters (1 week)**

1. Implement `DalamudGameStateProvider` (Task 2). Verify via `/qf test gamestate`.
2. Implement `DalamudQuestState` (Task 3). Verify via `/qf test queststate` across all three quest-state cases.

**Phase C — Movement adapters (1 week)**

1. Implement `LifestreamTeleporter` (Task 4). Verify via `/qf test teleport`.
2. Implement `VnavmeshNavigator` (Task 5). Verify via `/qf test navigate`.

**Phase D — Interaction adapter (1 week, the hardest one)**

1. Implement `DalamudInteractor.InteractWith` and `InteractWithObject` (Task 6.1).
2. Implement `AdvanceDialogue` for all three Talk variants (Task 6.2).
3. Implement `AcceptQuest` (Task 6.3) and `CompleteQuest`.
4. Verify via `/qf test interact 1003987`.

**Phase E — Engine host + remaining adapters (3-4 days)**

1. Implement `EngineHost` with recording proxy wiring.
2. Implement minimal stubs for `ICombat`, `IGearManager`, `IDialogueResolver`, `IMinigameSkipper`, `ITimingProfile` (Task 7).
3. Implement `/qf run` command and `QuestFileLoader`.

**Phase F — End-to-end test (1-3 days)**

1. Place `66130-coming-to-uldah.json` in the plugin's data folder.
2. Run `/qf run 66130` on a fresh Gladiator. Iterate until the engine reaches `EngineAction.Done`.
3. Capture the trace file. Save it as `66130-canonical-trace-phase6.jsonl` in the plugin repo for Phase 7's replay harness to consume.
4. Demo and PR.

**Total estimate: 3-4 weeks** (matches NEXT_STEPS.md §Phase 6 budget).

---

## What Phase 6 does NOT include

- `IMinigameSkipper` real implementations beyond `Unsupported` (Phase 10+).
- `IGearManager.EquipBestGearViaStylist` (Phase 9, when Stylist IPC is wired).
- Replay harness consuming the captured trace (Phase 7).
- UI windows beyond chat output and the slash command (Phase 8).
- `AcceptStep` and `TurnInStep` engine support (Phase 10+; see Task 7B — Phase 6 uses `talk` steps throughout, with `AcceptQuest`/`CompleteQuest` interactor calls triggered by postcondition evaluation).
- All other step types beyond `travel` and `talk` (Phase 10+).
- Death recovery, failure counters, retry config in the engine — these stay at the Phase 4/5 baseline (Phase 7+).
- Network fetching of quest data (Phase 10+; Phase 6 reads from a local checkout).
- A second worked quest. Quest 66130 is the only one. "Close to Home" (66104) is Phase 7.
- Multi-character / login-handling robustness. If the player logs out mid-run, behavior is undefined (likely the engine crashes when `ObjectTable[0]` is null). This is acceptable for Phase 6.

---

## Appendix A — Naming question deferred from Phase 5

The recording proxies (`RecordingGameStateProvider`, `RecordingQuestState`) live in `QuestForge.Adapters.Fakes/Recording/`. In production (Phase 6) they wrap real Dalamud adapters, so the "Fakes" namespace is misleading.

**Phase 6 decision:** leave them where they are. Hoisting requires updating every test's `using` statement (low value churn) and the right home is unclear (`QuestForge.Adapters` would expose the proxies to the engine — fine in theory, but the engine shouldn't construct them either). The natural home is a future `QuestForge.Adapters.Tracing` project that owns both the recording side and the Phase 7 replay side. Defer to Phase 7 when both sides exist.

This is a TODO comment in the Phase 6 code, not a build-breaking issue.

---

## Appendix B — Risks and mitigations

| Risk | Mitigation |
|---|---|
| Dalamud SDK version drift breaks the build mid-phase | Pin SDK version in csproj; update only at phase boundaries |
| vnavmesh / Lifestream IPC contract change between SPIKE_NOTES.md and now | Re-run the smoke commands as the first thing every implementation session; if a gate fails, update the wrapper in `Ipc/` |
| `ObjectTable[0]` is null mid-run (zone change, logout) | Adapter returns `Result.Fail("noLocalPlayer")` instead of crashing; engine surfaces via `AwaitUser` |
| AtkEvent crash pattern (SPIKE_NOTES.md surprise) reappears | The shared helper `AtkEventBuilder.For(addon, stateFlags: 132)` in `DalamudInteractor` is the only construction path; centralized to make crashes recur in one place |
| Quest 66130 schema diverges from the hand-rolled spike's hardcoded values | Phase 1's validator runs against the quest file in CI (already wired); Phase 6 adds a one-time smoke-check that the schema's AcceptFrom matches `NpcId(1003987)` |
| Engine race conditions due to framework-thread-only invariant violation (decision §3) | No mitigation in v1; documented as a known constraint. Phase 8 UI will need explicit checks. |
| Trace file grows unboundedly | Phase 5's `TraceWriter` has a 4096-byte-per-event cap; Phase 6 also rotates trace files per-run (one file per `runId`). Total disk footprint per run: ~100KB for quest 66130. |
