using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Component.GUI;
using QuestForge.Adapters;
using Microsoft.Extensions.Logging;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Combat;
using QuestForge.Adapters.Dalamud.State;
using QuestForge.Adapters.Dalamud.Gear;
using QuestForge.Adapters.Dalamud.Interaction;
using QuestForge.Adapters.Dalamud.Minigames;
using QuestForge.Adapters.Dalamud.Movement;
using QuestForge.Adapters.Dalamud.Timing;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using QuestForge.Engine.Combat;
using QuestForge.Engine.Scheduling;
using QuestForge.Plugin.Logging;
using QuestForge.Schema;

namespace QuestForge.Plugin;

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

    private readonly TraceSession _traceSession;

    // Per-step dialogue choice progress: reset when the source step changes across ticks.
    private string? _lastInteractStepId;
    private int _dialogueChoiceProgress;
    private readonly RotationLeaseLatch _leaseLatch = new();
    // The RecordingCombat-wrapped combat for the active run, so the lease latch's
    // StartRotation/StopRotation acts are captured in the trace alongside SetTarget/ClearTarget.
    private ICombat? _recordingCombat;
    private QuestEngine? _engine;
    private RecordingQuestState? _recordingQs;
    private string? _runId;
    private QuestId _currentQuestId;

    // True once the engine has emitted its own terminal run.end ("done"/"awaitUser") for the
    // current run, so EndRun must not append a redundant "ended" run.end. Reset per BeginRun.
    private bool _engineEmittedRunEnd;

    // Saved cutscene skip settings — null means not saved (no run active or settings not changed)
    private uint? _savedCutsceneSkipContents;
    private uint? _savedCutsceneSkipShip;

    private IQuestScheduler? _scheduler;
    private bool _autoMode;
    private bool _tracingEnabled;
    private DateTimeOffset _lastSchedulerPollAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromSeconds(2);

    // Aethernet throttle: prevent re-firing while zone transition / loading is in progress
    private DateTimeOffset _lastAethernetAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan AethernetCooldown = TimeSpan.FromSeconds(15);

    // Debounced logging: log immediately on change, then at most once per interval for repeats
    private string? _lastDebounceKey;
    private DateTimeOffset _lastDebounceAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(10);

    public EngineHost(PluginServices services, TraceSession traceSession)
    {
        _services = services;
        _traceSession = traceSession;
        _gameStateInner = new DalamudGameStateProvider(services);
        _questStateInner = new DalamudQuestState(services);
        _navigator       = new VnavmeshNavigator(services);
        _teleporter      = new LifestreamTeleporter(services);
        _interactor      = new DalamudInteractor(services);
        _combat          = new WrathComboAdapter(services);
        _gear            = new DalamudGearManager(services);
        _minigames       = new NullMinigameSkipper();
        _dialogue        = new LuminaDialogueResolver(services);
        _timing          = new SeededTimingProfile(seed: 0);
    }

    public bool IsRunActive => _engine is not null;
    public string? ActiveRunId => _runId;

    public bool IsAutoMode => _autoMode;
    public QuestId? CurrentQuestId => _engine is not null ? _currentQuestId : null;
    public SchedulerStatus? SchedulerStatus => _scheduler?.CurrentStatus;

    // Exposed for QuestScheduler construction in Plugin.cs
    internal IQuestState QuestState => _questStateInner;
    internal IGameStateProvider GameState => _gameStateInner;

    // Called by /qf stop — safe to call mid-tick because all Phase 6 adapters complete
    // synchronously (Task.FromResult), so DispatchAction never parks across frames.
    public void StopRun() => EndRun();

    public void StartAutoMode(IQuestScheduler scheduler, bool enableTracing)
    {
        StopAutoMode();
        _scheduler = scheduler;
        _autoMode = true;
        _tracingEnabled = enableTracing;
        _lastSchedulerPollAt = DateTimeOffset.MinValue; // fire immediately on first tick
        _services.Log.Info("QuestForge: auto mode started");
        _services.ChatGui.Print("QuestForge: auto mode started — will run all available quests");
    }

    public void StopAutoMode()
    {
        _autoMode = false;
        _scheduler = null;
        StopRun();
    }

    public void UpdateSchedulerOptions(SchedulerOptions options)
        => _scheduler?.UpdateOptions(options);

    public string GetGameStateSummary()
    {
        var ct = CancellationToken.None;
        var zone = _gameStateInner.GetPlayerZone(ct).GetAwaiter().GetResult().ValueOrDefault;
        var pos = _gameStateInner.GetPlayerPosition(ct).GetAwaiter().GetResult().ValueOrDefault;
        var job = _gameStateInner.GetCurrentJob(ct).GetAwaiter().GetResult().ValueOrDefault;
        var level = _gameStateInner.GetJobLevel(default, ct).GetAwaiter().GetResult().ValueOrDefault;
        var combat = _gameStateInner.IsPlayerInCombat(ct).GetAwaiter().GetResult().ValueOrDefault;
        var kind = _gameStateInner.GetCurrentInstanceKind(ct).GetAwaiter().GetResult().ValueOrDefault;
        return $"zone={zone.Value} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) " +
               $"job={job.Value} lv={level} combat={combat} instance={kind}";
    }

    public string GetQuestStateSummary(uint questRowId)
    {
        var ct = CancellationToken.None;
        var qid = new QuestId(questRowId);
        var seq = _questStateInner.GetQuestSequence(qid, ct).GetAwaiter().GetResult().ValueOrDefault;
        var complete = _questStateInner.IsQuestComplete(qid, ct).GetAwaiter().GetResult().ValueOrDefault;
        var accepted = _questStateInner.IsQuestAccepted(qid, ct).GetAwaiter().GetResult().ValueOrDefault;
        return $"quest={questRowId} seq={seq} complete={complete} accepted={accepted}";
    }

    public void BeginRun(QuestDefinition quest, string runId, bool enableTracing)
    {
        EndRun(); // clean up any previous run (also restores cutscene settings)

        EnableCutsceneSkip();
        _runId          = runId;
        _engineEmittedRunEnd = false;
        _currentQuestId = new QuestId(quest.Id);
        _timing.Reseed(StableHash(runId));
        _traceSession.OnQuestRunStart(quest.Id);
        _traceSession.Write(new RunStartEvent(runId, quest.Id, quest.Id, DateTimeOffset.UtcNow));

        IGameStateProvider gs = new RecordingGameStateProvider(
            _gameStateInner, _traceSession, () => _runId, skipIfNoRunId: true);
        var recordingQs = new RecordingQuestState(
            _questStateInner, _traceSession, () => _runId, skipIfNoRunId: true);
        IQuestState qs = recordingQs;
        // Always hold the reference — TraceSession gate controls whether observations reach disk.
        _recordingQs = recordingQs;
        _recordingCombat = new RecordingCombat(
            _combat, _traceSession, () => _runId, skipIfNoRunId: true);

        _engine = new QuestEngine(
            gs, qs, _navigator, _teleporter, _interactor,
            _recordingCombat, _gear, _minigames, _dialogue, _timing,
            _traceSession, new DalamudLogger<QuestEngine>(_services.Log));
        _engine.StartQuest(quest, LoadFragments());
        _engine.BeginRun(runId);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (_engine is null)
        {
            if (_autoMode && _scheduler is { } scheduler
                && DateTimeOffset.UtcNow - _lastSchedulerPollAt > SchedulerPollInterval)
            {
                _lastSchedulerPollAt = DateTimeOffset.UtcNow;
                await TryStartNextQuestAsync(scheduler, ct);
            }
            return;
        }

        EngineAction action;
        try { action = await _engine.Tick(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _services.Log.Error(ex, "QuestForge: engine tick threw");
            _services.ChatGui.PrintError($"QuestForge: tick error — {ex.Message}");
            return;
        }

        try { await DispatchAction(action, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _services.Log.Error(ex, $"QuestForge: dispatch error for {action.GetType().Name}");
            _services.ChatGui.PrintError($"QuestForge: dispatch error — {ex.Message}");
        }

        // Ambient quest flag polling: emit a trace observation whenever the active quest's
        // flags byte changes. The dedup layer in RecordingQuestState suppresses unchanged frames.
        // _recordingQs is null when tracing is off (DispatchAction.Done also clears it via EndRun).
        if (_recordingQs is not null)
            await _recordingQs.GetQuestFlags(_currentQuestId, ct);
    }

    private async Task DispatchAction(EngineAction action, CancellationToken ct)
    {
        await _leaseLatch.OnAction(action, _recordingCombat ?? _combat, ct);

        switch (action)
        {
            case EngineAction.Engage:
                // Targeting and lease lifecycle are handled by CombatController and the latch above.
                // Advance dialogue / skip cutscenes in case a cutscene fires during combat.
                TryCutsceneSkipConfirm();
                await _interactor.AdvanceDialogue(ct);
                break;

            case EngineAction.UseAethernet ua:
                // Throttle: the engine fires UseAethernet every tick while playerZone() fails
                // (loading screen takes several ticks). Without this, Lifestream receives
                // duplicate requests and opens the aethernet menu after landing in the
                // destination zone.
                if (DateTimeOffset.UtcNow - _lastAethernetAt < AethernetCooldown)
                    break;
                _lastAethernetAt = DateTimeOffset.UtcNow;
                DebounceLog(
                    $"aethernet:{ua.Destination.Value}",
                    $"[UseAethernet] shard={ua.Destination.Value}" +
                    (ua.SourcePosition.HasValue
                        ? $" from ({ua.SourcePosition.Value.X:F1},{ua.SourcePosition.Value.Y:F1},{ua.SourcePosition.Value.Z:F1})"
                        : ""));
                if (_runId is not null)
                    try
                    {
                        _traceSession.Write(new QuestForge.Adapters.Tracing.ObservationEvent(
                            _runId, "UseAethernet",
                            System.Text.Json.JsonSerializer.SerializeToElement(new
                            {
                                destinationShardId = ua.Destination.Value,
                                sourcePosX = ua.SourcePosition?.X,
                                sourcePosY = ua.SourcePosition?.Y,
                                sourcePosZ = ua.SourcePosition?.Z
                            }),
                            null, DateTimeOffset.UtcNow));
                    }
                    catch { /* trace failures must not block dispatch */ }
                await _teleporter.TeleportToAethernet(ua.Destination, ct);
                break;

            case EngineAction.Navigate n:
                DebounceLog(
                    $"nav:{n.Destination.X:F0},{n.Destination.Z:F0}",
                    $"[Navigate] → ({n.Destination.X:F1},{n.Destination.Y:F1},{n.Destination.Z:F1}) stop={n.Options.StoppingDistance}");
                await _navigator.NavigateTo(n.Destination, n.Options, ct);
                // Skip cutscene and advance dialogue that may be open while navigating.
                // Attuning to the main aetheryte triggers a cutscene; isAttuned(N) becomes
                // true before it ends so the engine transitions to Navigate while the cutscene
                // and its follow-up Talk dialog are still blocking the player.
                TryCutsceneSkipConfirm();
                await _interactor.AdvanceDialogue(ct);
                break;

            case EngineAction.Interact i:
                DebounceLog($"interact:{i.Target.Value}", $"[Interact] npc={i.Target.Value}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _interactor.InteractWith(i.Target, ct);
                // Reset dialogue choice progress when the source step changes.
                if (i.Origin?.Id != _lastInteractStepId)
                {
                    _lastInteractStepId = i.Origin?.Id;
                    _dialogueChoiceProgress = 0;
                }
                // Drive SelectIconString/SelectString choices from the step's DialogueChoices.
                // If a choice was dispatched, skip AdvanceDialogue this tick so the selection
                // can register before the SelectYesno confirmation opens.
                var sip = _services.GameGui.GetAddonByName("SelectIconString");
                var ssp = _services.GameGui.GetAddonByName("SelectString");
                var syn = _services.GameGui.GetAddonByName("SelectYesno");
                var choiceDispatched = DialogueChoiceDispatcher.TryDispatch(
                    i.Origin,
                    !sip.IsNull && sip.IsReady,
                    !ssp.IsNull && ssp.IsReady,
                    ref _dialogueChoiceProgress,
                    _interactor, ct,
                    selectYesnoOpen: !syn.IsNull && syn.IsReady);
                if (!choiceDispatched)
                    await _interactor.AdvanceDialogue(ct);
                await _interactor.AcceptQuest(_currentQuestId, ct);
                await _interactor.CompleteQuest(_currentQuestId, ct);
                break;

            case EngineAction.HandOver h:
                DebounceLog($"handover:{h.Target.Value}:{string.Join(",", h.Items.Select(i => i.Value))}",
                    $"[HandOver] npc={h.Target.Value} items=[{string.Join(",", h.Items.Select(i => i.Value))}]");
                TryCutsceneSkipConfirm();
                await _interactor.InteractWith(h.Target, ct);
                await _interactor.AdvanceDialogue(ct);
                // Places items in Request addon slots then clicks Hand Over button.
                // Returns NoDialog if the addon is not yet open — engine will retry next tick.
                await _interactor.HandOverItem(h.Items, h.Target, ct);
                break;

            case EngineAction.Wait:
                // Engine is satisfied with step state but waiting for the game to advance
                // sequence (e.g. Talk addon still open after interact). Keep clicking through.
                TryCutsceneSkipConfirm();
                await _interactor.AdvanceDialogue(ct);
                await _interactor.AcceptQuest(_currentQuestId, ct);
                await _interactor.CompleteQuest(_currentQuestId, ct);
                break;

            case EngineAction.AwaitUser au:
                _services.Log.Warning($"QuestForge run {_runId} paused: {au.Reason}");
                _services.ChatGui.PrintError($"QuestForge: run paused — {au.Reason}");
                _engineEmittedRunEnd = true;  // engine already wrote the "awaitUser" run.end
                if (_autoMode)
                {
                    StopAutoMode();
                    _services.ChatGui.PrintError("QuestForge: auto mode stopped — manual intervention required");
                }
                else
                {
                    EndRun();
                }
                break;

            case EngineAction.Done:
                _services.Log.Info($"QuestForge run {_runId} complete");
                _services.ChatGui.Print("QuestForge: quest complete!");
                _engineEmittedRunEnd = true;  // engine already wrote the "done" run.end
                EndRun();
                break;
        }
    }

    private async Task TryStartNextQuestAsync(IQuestScheduler scheduler, CancellationToken ct)
    {
        Result<QuestId?> result;
        try { result = await scheduler.NextQuestToRun(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _services.Log.Error(ex, "QuestForge: scheduler threw");
            StopAutoMode();
            return;
        }

        if (result is Result<QuestId?>.Failure f)
        {
            _services.Log.Warning($"QuestForge: scheduler returned failure {f.Reason}: {f.Detail}");
            return;
        }

        if (result is not Result<QuestId?>.Success { Value: { } questId })
        {
            // Success(null) → Idle or AwaitingUser. Surface the blocker once via debounced log+chat.
            if (scheduler.CurrentStatus is SchedulerStatus.AwaitingUser au)
                DebounceLog(
                    $"awaiting:{au.BlockedQuest.Value}",
                    $"QuestForge: auto mode blocked — quest {au.BlockedQuest.Value}: {FormatAwaitReason(au.Reason)}");
            return;
        }

        var quest = TryLoadQuest(questId);
        if (quest is null)
        {
            _services.Log.Warning($"QuestForge: scheduler selected quest {questId.Value} but no quest file found");
            return;
        }

        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        BeginRun(quest, runId, _tracingEnabled);
        _services.ChatGui.Print($"QuestForge: starting quest {questId.Value}");
    }

    private static string FormatAwaitReason(QuestUnlockReason r)
    {
        if (r.LevelTooLow)            return $"level too low (need {r.RequiredLevel})";
        if (r.WrongJob)               return "wrong job";
        if (r.PrerequisiteIncomplete) return "missing prerequisites";
        if (r.AlreadyCompleted)       return "already completed (data inconsistency)";
        return r.Detail ?? "unavailable";
    }

    private IReadOnlyDictionary<string, FragmentDefinition> LoadFragments()
    {
        var dir = Path.Combine(
            _services.PluginInterface.GetPluginConfigDirectory(),
            "fragments");
        if (!Directory.Exists(dir))
            return new Dictionary<string, FragmentDefinition>();

        var result = new Dictionary<string, FragmentDefinition>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var def = QuestFileLoader.LoadFragment(file);
                if (def is not null && !string.IsNullOrEmpty(def.FragmentId))
                    result[def.FragmentId] = def;
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[LoadFragments] skipping {file}: {ex.Message}");
            }
        }
        _services.Log.Debug($"[LoadFragments] loaded {result.Count} fragment(s)");
        return result;
    }

    private QuestDefinition? TryLoadQuest(QuestId questId)
    {
        var path = Path.Combine(
            _services.PluginInterface.GetPluginConfigDirectory(),
            "quests",
            $"{questId.Value}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var def = QuestFileLoader.Load(path);
            if (def is not null)
                foreach (var seq in def.Sequences)
                    foreach (var step in seq.Steps)
                        _services.Log.Debug($"[LoadQuest] step={step.Id} type={step.GetType().Name} stopDist={step.StopDistance?.ToString() ?? "null"}");
            return def;
        }
        catch { return null; }
    }

    private void EndRun()
    {
        _leaseLatch.Release(_recordingCombat ?? _combat, CancellationToken.None).GetAwaiter().GetResult();
        if (_runId is not null && !_engineEmittedRunEnd)
            _traceSession.Write(new RunEndEvent(_runId, "ended", DateTimeOffset.UtcNow));
        _engine      = null;
        _recordingQs = null;
        _recordingCombat = null;
        _runId       = null;
        _traceSession.OnQuestRunEnd();
        RestoreCutsceneSkip();
        // TraceSession file lifecycle for non-QuestRun modes is managed by Plugin.cs.
    }

    public void Dispose()
    {
        EndRun();
        _combat.Dispose();
    }

    // When a skippable cutscene is active, AutoCutsceneSkipper presses Escape which opens
    // a SelectString confirmation dialog. Click the first entry (Yes/skip) to confirm.
    // OccupiedInCutSceneEvent is the reliable flag for skippable cutscenes; WatchingCutscene78
    // marks non-skippable ones that must be waited out — do not attempt to click those.
    private unsafe void TryCutsceneSkipConfirm()
    {
        if (!_services.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return;

        var addonPtr = _services.GameGui.GetAddonByName("SelectString");
        if (addonPtr.IsNull || !addonPtr.IsReady) return;

        ((AtkUnitBase*)addonPtr.Address)->FireCallbackInt(0);
    }

    private void EnableCutsceneSkip()
    {
        // Save current settings and set to maximum (skip all). Uses props.Maximum so we don't
        // hardcode a value — the game defines what "skip all" means per option.
        if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsContents, out uint cur1))
        {
            _savedCutsceneSkipContents = cur1;
            if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsContents, out UIntConfigProperties? props) && props is not null)
                _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsContents, props.Maximum);
        }
        if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsShip, out uint cur2))
        {
            _savedCutsceneSkipShip = cur2;
            if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsShip, out UIntConfigProperties? props) && props is not null)
                _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsShip, props.Maximum);
        }
    }

    private void RestoreCutsceneSkip()
    {
        if (_savedCutsceneSkipContents is { } v1)
        {
            _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsContents, v1);
            _savedCutsceneSkipContents = null;
        }
        if (_savedCutsceneSkipShip is { } v2)
        {
            _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsShip, v2);
            _savedCutsceneSkipShip = null;
        }
    }

    // Log immediately when key changes; suppress repeats beyond once per DebounceInterval.
    private void DebounceLog(string key, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (key == _lastDebounceKey && now - _lastDebounceAt < DebounceInterval) return;
        _services.Log.Debug(message);
        _lastDebounceKey = key;
        _lastDebounceAt  = now;
    }

    // Deterministic seed from runId — same runId → same timing sequence (Phase 7 replay)
    private static int StableHash(string s)
    {
        var hash = 17;
        foreach (var c in s) hash = hash * 31 + c;
        return hash;
    }

}
