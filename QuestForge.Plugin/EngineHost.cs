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
using QuestForge.Adapters.Dalamud.Actions;
using QuestForge.Adapters.Dalamud.Chat;
using QuestForge.Adapters.Dalamud.Duty;
using QuestForge.Adapters.Dalamud.Emotes;
using QuestForge.Adapters.Dalamud.Items;
using QuestForge.Adapters.Duty;
using QuestForge.Adapters.Dalamud.Movement;
using QuestForge.Adapters.Chat;
using QuestForge.Adapters.Emotes;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Items;
using QuestForge.Adapters.Dalamud.Timing;
using QuestForge.Adapters.Recording;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using QuestForge.Engine.Combat;
using QuestForge.Engine.Dialogue;
using QuestForge.Engine.Scheduling;
using QuestForge.Plugin.Logging;
using QuestForge.Schema;

namespace QuestForge.Plugin;

public sealed class EngineHost : IDisposable
{
    private readonly PluginServices _services;
    private readonly PluginConfig _config;

    private readonly DalamudGameStateProvider _gameStateInner;
    private readonly DalamudQuestState _questStateInner;
    private readonly VnavmeshNavigator _navigator;
    private readonly LifestreamTeleporter _teleporter;
    private readonly DalamudInteractor _interactor;
    private readonly DalamudVendor _vendor;
    private readonly DalamudMount _mount;
    private readonly DalamudActionExecutor _actionExecutor;
    private readonly DalamudEmoteExecutor _emoteExecutor;
    private readonly DalamudChatSender _chatSender;
    private readonly DalamudItemUser _itemUser;
    private readonly WrathComboAdapter _combat;
    private readonly DalamudGearEquipper _gearEquipper;
    private readonly DalamudBestGearEquipper _bestGearEquipper;
    private readonly DalamudJobChanger _jobChanger;
    private readonly DalamudGearsetManager _gearsetManager;
    private readonly DalamudCofferOpener _cofferOpener;
    private readonly DalamudObjectInteractor _objectInteractor;
    private readonly DalamudQuestBattleRunner _questBattleRunner;
    private readonly DalamudDutyRunner _dutyRunner;
    private readonly LuminaCfcResolver _cfcResolver;
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

    private Action? _onRunStart;
    private IQuestScheduler? _scheduler;
    private bool _autoMode;
    private bool _tracingEnabled;
    private DateTimeOffset _lastSchedulerPollAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromSeconds(2);

    // Aethernet throttle: prevent re-firing while zone transition / loading is in progress
    private DateTimeOffset _lastAethernetAt = DateTimeOffset.MinValue;
    // Tracks whether the previous dispatched action was a Purchase. The shop addon close
    // fires lazily on the next non-Purchase action so the in-flight buy transaction
    // (SelectYesno confirm → inventory update → engine's postcondition gate) can complete
    // before we dismiss the addon.
    private bool _lastDispatchedActionWasPurchase;
    // Tracks the active SPD step ID for StopDuty cleanup when the step advances (SPD7).
    private string? _activeSpdStepId;
    // Tracks the active dungeon/trial step ID for StopDuty cleanup when the step advances (AD9).
    private string? _activeDutyStepId;

    // Tracks whether the previous dispatched action was a Navigate so the lazy-dismount
    // hook fires on the next non-Navigate action (mirrors the close-shop pattern).
    private bool _lastDispatchedActionWasNavigate;
    private const float MountDistanceThresholdMeters = 20f;
    private static readonly TimeSpan AethernetCooldown = TimeSpan.FromSeconds(15);

    // Debounced logging: log immediately on change, then at most once per interval for repeats
    private string? _lastDebounceKey;
    private DateTimeOffset _lastDebounceAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(10);

    public EngineHost(PluginServices services, TraceSession traceSession, PluginConfig config)
    {
        _services = services;
        _traceSession = traceSession;
        _config = config;
        _gameStateInner = new DalamudGameStateProvider(services);
        _questStateInner = new DalamudQuestState(services);
        _navigator       = new VnavmeshNavigator(services);
        _teleporter      = new LifestreamTeleporter(services);
        _interactor      = new DalamudInteractor(services);
        _vendor          = new DalamudVendor(services);
        _mount           = new DalamudMount(services);
        _actionExecutor  = new DalamudActionExecutor(services);
        _emoteExecutor   = new DalamudEmoteExecutor(services);
        _chatSender      = new DalamudChatSender(services);
        _itemUser        = new DalamudItemUser(services);
        _combat          = new WrathComboAdapter(services);
        _gearEquipper    = new DalamudGearEquipper(services);
        _bestGearEquipper = new DalamudBestGearEquipper(services, () => config.PreferStylist);
        _jobChanger      = new DalamudJobChanger(services);
        _gearsetManager  = new DalamudGearsetManager(services);
        _cofferOpener    = new DalamudCofferOpener(services);
        _objectInteractor = new DalamudObjectInteractor(_interactor);
        _questBattleRunner = new DalamudQuestBattleRunner(services);
        _dutyRunner      = new DalamudDutyRunner(services);
        _cfcResolver     = new LuminaCfcResolver(services);
        _minigames       = new NullMinigameSkipper();
        _dialogue        = new LuminaDialogueResolver(services);
        _timing          = new SeededTimingProfile(seed: 0);
    }

    public bool IsRunActive => _engine is not null;
    public string? ActiveRunId => _runId;
    public YesNoAnswer? CurrentYesNoAnswer => _engine?.CurrentYesNoAnswer;

    public void SetRunStartCallback(Action callback) => _onRunStart = callback;

    public bool IsAutoMode => _autoMode;
    public QuestId? CurrentQuestId => _engine is not null ? _currentQuestId : null;
    public SchedulerStatus? SchedulerStatus => _scheduler?.CurrentStatus;

    // Exposed for QuestScheduler construction in Plugin.cs
    internal IQuestState QuestState => _questStateInner;
    internal IGameStateProvider GameState => _gameStateInner;

    // Exposed for /qf debug combat subcommands — raw adapters (not recording-proxy wrappers)
    public IGameStateProvider DebugGameState => _gameStateInner;
    public ICombat            DebugCombat    => _combat;
    public INavigator         DebugNavigator => _navigator;
    public IVendor            DebugVendor    => _vendor;
    public IMount             DebugMount     => _mount;
    public IEmoteExecutor     DebugEmoteExecutor => _emoteExecutor;
    public IChatSender        DebugChatSender    => _chatSender;
    public IItemUser          DebugItemUser      => _itemUser;
    public IGearEquipper      DebugGearEquipper  => _gearEquipper;
    public IBestGearEquipper  DebugBestGearEquipper => _bestGearEquipper;
    public IJobChanger        DebugJobChanger    => _jobChanger;
    public IGearsetManager    DebugGearsetManager => _gearsetManager;
    public ICofferOpener      DebugCofferOpener   => _cofferOpener;
    public IObjectInteractor  DebugObjectInteractor => _objectInteractor;
    public IQuestBattleRunner DebugQuestBattleRunner => _questBattleRunner;
    public IDutyRunner DebugDutyRunner => _dutyRunner;

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
        var level = _gameStateInner.GetJobLevel(job, ct).GetAwaiter().GetResult().ValueOrDefault;
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
        _traceSession.Write(new RunStartEvent
        {
            RunId = runId,
            Data = new RunStartEvent.RunStartData
            {
                QuestId      = quest.Id,
                SchemaVer    = "1.0",
                WallClockUtc = DateTimeOffset.UtcNow
            }
        });

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
            _recordingCombat, _minigames, _dialogue, _timing,
            _traceSession, new DalamudLogger<QuestEngine>(_services.Log),
            vendor: _vendor,
            actionExecutor: _actionExecutor,
            emoteExecutor: _emoteExecutor,
            chatSender: _chatSender,
            itemUser: _itemUser,
            gearEquipper: _gearEquipper,
            bestGearEquipper: _bestGearEquipper,
            jobChanger: _jobChanger,
            gearsetManager: _gearsetManager,
            cofferOpener: _cofferOpener,
            objectInteractor: _objectInteractor,
            questBattleRunner: _questBattleRunner,
            dutyRunner: _dutyRunner,
            cfcResolver: _cfcResolver,
            actionCooldownSeconds: _config.ActionCooldownSeconds);
        _engine.StartQuest(quest, LoadFragments());
        _engine.BeginRun(runId);
        _onRunStart?.Invoke();
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

        // Lazy shop close: if the previous dispatch was a Purchase and the engine has now
        // moved past it (postcondition met → emitted a non-Purchase action), dismiss any
        // open Shop / GrandCompanyExchange addon BEFORE handling the new action. This lets
        // SelectYesnoResponder finish the buy confirm and the inventory update land before
        // we close, while still freeing the player to move before the next step runs.
        if (_lastDispatchedActionWasPurchase && action is not EngineAction.Purchase)
        {
            await _vendor.Close(ct);
            _lastDispatchedActionWasPurchase = false;
        }

        // SPD cleanup (SPD7): when the engine advances past an SPD step (emits anything other
        // than EnterSinglePlayerDuty or Wait), disable BossMod AI and clear the tracking flag.
        if (_activeSpdStepId is not null
            && action is not EngineAction.EnterSinglePlayerDuty
            && action is not EngineAction.Wait)
        {
            await _questBattleRunner.StopDuty(ct);
            _activeSpdStepId = null;
        }

        // Duty cleanup (AD9): when the engine advances past a dungeon/trial step (emits anything
        // other than EnterDuty or Wait), tell AutoDuty to stop and clear the tracking flag.
        if (_activeDutyStepId is not null
            && action is not EngineAction.EnterDuty
            && action is not EngineAction.Wait)
        {
            await _dutyRunner.StopDuty(ct);
            _activeDutyStepId = null;
        }

        // Lazy dismount: if the previous dispatch was a Navigate and the engine has now
        // moved past it (postcondition met → emitted a non-Navigate action), dismount BEFORE
        // handling the new action. Flying-dismount causes fall damage if the navigate ended
        // in the air — accept this v1 limitation; vnavmesh usually grounds at stop-distance.
        // NOTE (Q4): mount-decision reads are on the host, not QuestEngine, so replay fixtures
        // against QuestEngine.Tick() are unaffected (see MOUNT_SUPPORT_PLAN.md §3 Q4).
        // Teleport is exempt: the game dismounts the player automatically on arrival if the
        // destination zone prohibits mounts (e.g. cities). No pre-dismount needed.
        if (_lastDispatchedActionWasNavigate && action is not EngineAction.Navigate and not EngineAction.Teleport and not EngineAction.EquipGear and not EngineAction.EquipBestGear and not EngineAction.RegisterGearset)
        {
            // Don't dismount while vnavmesh is still moving the player. Some non-Navigate
            // actions fire early — notably Engage, which the engine emits as soon as a combat
            // target enters scan range (often 30m+ from the player), while the CombatController
            // is internally navigating the player toward the target. Dismounting on that early
            // Engage transition drops a flying player out of the sky mid-approach. The fix:
            // only consider dismount when navigation has actually stopped.
            var navResult = await _navigator.IsNavigating(ct);
            var stillNavigating = navResult is Result<bool>.Success { Value: true };

            if (!stillNavigating)
            {
                var stateResult = await _gameStateInner.GetPlayerState(ct);
                if (stateResult is Result<PlayerStateSnapshot>.Success { Value: var dsPs })
                {
                    if (dsPs.MountState != MountState.Dismounted)
                    {
                        // Flying dismount is a two-step game action: first call lands the player,
                        // second call (after the landing animation) actually dismounts. We can't
                        // fire both UseAction calls in the same tick — the second is rejected
                        // mid-animation. Keep the navigate-flag set and re-fire each tick until
                        // MountState observes Dismounted; the engine's ~250ms tick gap gives the
                        // landing animation time to complete between calls.
                        await _mount.Dismount(ct);
                    }
                    else
                    {
                        _lastDispatchedActionWasNavigate = false;
                    }
                }
                else
                {
                    // State read failed — don't loop forever; give up.
                    _lastDispatchedActionWasNavigate = false;
                }
            }
            // If still navigating: keep the flag set; re-check next tick.
        }

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
                        _traceSession.Write(new QuestForge.Adapters.Tracing.ObservationEvent
                        {
                            RunId = _runId,
                            Data = new QuestForge.Adapters.Tracing.ObservationEvent.ObservationData
                            {
                                Method = "UseAethernet",
                                Argument = System.Text.Json.JsonSerializer.SerializeToElement(new
                                {
                                    destinationShardId = ua.Destination.Value,
                                    sourcePosX = ua.SourcePosition?.X,
                                    sourcePosY = ua.SourcePosition?.Y,
                                    sourcePosZ = ua.SourcePosition?.Z
                                }),
                                Value = null
                            }
                        });
                    }
                    catch { /* trace failures must not block dispatch */ }
                await _teleporter.TeleportToAethernet(ua.Destination, ct);
                break;

            case EngineAction.Navigate n:
                DebounceLog(
                    $"nav:{n.Destination.X:F0},{n.Destination.Z:F0}",
                    $"[Navigate] → ({n.Destination.X:F1},{n.Destination.Y:F1},{n.Destination.Z:F1}) stop={n.Options.StoppingDistance}");
                // Mount predicate: fire Mount Roulette when preconditions hold.
                // Casting: true covers mid-mount-animation (~2s gap) — suppresses re-fire spam.
                // Silent best-effort: UseAction rejection (indoor, no mount) degrades to on-foot.
                if (n.Options.UseMount)
                {
                    var mountResult = await _gameStateInner.GetPlayerState(ct);
                    if (mountResult is Result<PlayerStateSnapshot>.Success { Value: var mPs }
                        && mPs.MountState == MountState.Dismounted
                        && !mPs.InCombat
                        && mPs.CanMount
                        && !mPs.Casting
                        && mPs.Position.DistanceTo(n.Destination) >= MountDistanceThresholdMeters)
                    {
                        await _mount.Mount(ct);
                    }
                }
                _lastDispatchedActionWasNavigate = true;
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
                // can register before the next prompt opens.
                var sip = _services.GameGui.GetAddonByName("SelectIconString");
                var ssp = _services.GameGui.GetAddonByName("SelectString");
                var choiceDispatched = DialogueChoiceDispatcher.TryDispatch(
                    i.Origin,
                    !sip.IsNull && sip.IsReady,
                    !ssp.IsNull && ssp.IsReady,
                    ref _dialogueChoiceProgress,
                    _interactor, ct);
                if (!choiceDispatched)
                    await _interactor.AdvanceDialogue(ct);
                await _interactor.TryFillRequestAddon(ct);
                await _interactor.AcceptQuest(_currentQuestId, ct);
                await _interactor.CompleteQuest(_currentQuestId, ct);
                break;

            case EngineAction.InteractObject io:
                DebounceLog($"interactobject:{io.Target.Value}",
                    $"[InteractObject] interactableId={io.Target.Value}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _objectInteractor.InteractWithObject(io.Target, ct);
                await _interactor.AdvanceDialogue(ct);
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

            case EngineAction.Purchase p:
                DebounceLog(
                    $"purchase:{p.Vendor.Value}:{p.Item.Value}",
                    $"[Purchase] vendor={p.Vendor.Value} item={p.Item.Value} qty={p.Quantity} cur={p.Currency}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                // InteractWith opens the shop; DalamudVendor then drives the buy addon.
                // SelectYesno confirm ("buy N for Y?") is dismissed by the existing SelectYesnoResponder.
                await _interactor.InteractWith(p.Vendor, ct);
                await _vendor.Purchase(p.Vendor, p.Item, p.Quantity, p.Currency, ct, p.GcCategory, p.GcRankTier);
                // Mark this dispatch as a Purchase so the next non-Purchase action will
                // close the shop addon first (see the pre-switch close hook above).
                _lastDispatchedActionWasPurchase = true;
                break;

            case EngineAction.UseAction ua:
                DebounceLog(
                    $"useaction:{ua.Type}:{ua.ActionId}:{ua.TargetNpcId?.Value}",
                    $"[UseAction] type={ua.Type} id={ua.ActionId}" +
                    (ua.TargetNpcId is { } uaId ? $" target={uaId.Value}" : " (self)"));
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _actionExecutor.UseAction(ua.Type, ua.ActionId, ua.TargetNpcId, ct);
                break;

            case EngineAction.UseEmote ue:
                DebounceLog(
                    $"useemote:{ue.EmoteId}:{ue.TargetNpcId?.Value}:{ue.Motion}",
                    $"[UseEmote] id={ue.EmoteId}" +
                    (ue.TargetNpcId is { } ueTargetId ? $" target={ueTargetId.Value}" : " (self)") +
                    $" motion={ue.Motion}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _emoteExecutor.UseEmote(ue.EmoteId, ue.TargetNpcId, ue.Motion, ct);
                break;

            case EngineAction.SayChatMessage sc:
                DebounceLog(
                    $"saychat:{sc.Message.Length}:{sc.TargetNpcId?.Value}",
                    $"[SayChatMessage] message=\"{sc.Message}\" target={sc.TargetNpcId?.Value.ToString() ?? "broadcast"}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                await _chatSender.Send(sc.Message, sc.TargetNpcId, ct);
                break;

            case EngineAction.UseItem ui:
                DebounceLog(
                    $"useitem:{ui.Kind}:{ui.ItemId}:{ui.TargetNpcId?.Value}",
                    $"[UseItem] kind={ui.Kind} id={ui.ItemId}" +
                    (ui.TargetNpcId is { } uiNpcId ? $" target={uiNpcId.Value}" : "") +
                    (ui.TargetPosition is { } uiPos ? $" pos=({uiPos.X},{uiPos.Y},{uiPos.Z})" : ""));
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _itemUser.UseItem(ui.Kind, ui.ItemId, ui.TargetNpcId, ui.TargetPosition, ct);
                break;

            case EngineAction.EquipGear eg:
                DebounceLog(
                    $"equipgear:{eg.ItemId}",
                    $"[EquipGear] itemId={eg.ItemId}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _gearEquipper.EquipItem(eg.ItemId, ct);
                break;

            case EngineAction.EquipBestGear:
                DebounceLog(
                    "equipbestgear",
                    "[EquipBestGear] firing");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _bestGearEquipper.EquipBestGear(ct);
                break;

            case EngineAction.ChangeJob cj:
                DebounceLog(
                    $"changejob:{cj.Job.Value}",
                    $"[ChangeJob] jobId={cj.Job.Value}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _jobChanger.ChangeToJob(cj.Job, ct);
                break;

            case EngineAction.RegisterGearset:
                DebounceLog(
                    "registergearset",
                    "[RegisterGearset] firing");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _gearsetManager.RegisterGearset(ct);
                break;

            case EngineAction.OpenCoffer oc:
                DebounceLog(
                    $"opencoffer:{oc.ItemId}",
                    $"[OpenCoffer] itemId={oc.ItemId}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                TryCutsceneSkipConfirm();
                await _cofferOpener.OpenCoffer(oc.ItemId, ct);
                break;

            case EngineAction.EnterSinglePlayerDuty espd:
                DebounceLog(
                    $"enterspd:{espd.Origin?.Id}",
                    $"[EnterSinglePlayerDuty] stepId={espd.Origin?.Id ?? "(unknown)"}" +
                    $" cfcId={espd.ContentFinderConditionId}" +
                    $" entryTarget={espd.EntryTargetId}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                _activeSpdStepId = espd.Origin?.Id;
                TryCutsceneSkipConfirm();
                await _questBattleRunner.StartDuty(ct);
                await _interactor.AdvanceDialogue(ct);
                break;

            case EngineAction.EnterDuty ed:
                DebounceLog(
                    $"enterduty:{ed.Origin?.Id}",
                    $"[EnterDuty] stepId={ed.Origin?.Id ?? "(unknown)"} cfcId={ed.ContentFinderConditionId}");
                if ((await _navigator.IsNavigating(ct)).ValueOrDefault)
                    await _navigator.Stop(ct);
                _activeDutyStepId = ed.Origin?.Id;
                var tt = _cfcResolver.GetTerritoryType(ed.ContentFinderConditionId);
                if (tt is not null)
                    await _dutyRunner.StartDuty(tt.Value, ct);
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
        // Stop BossMod AI if an SPD was active when the run ended (e.g. via /qf stop).
        if (_activeSpdStepId is not null)
        {
            _questBattleRunner.StopDuty(CancellationToken.None).GetAwaiter().GetResult();
            _activeSpdStepId = null;
        }
        // Stop AutoDuty if a dungeon/trial was active when the run ended (e.g. via /qf stop).
        if (_activeDutyStepId is not null)
        {
            _dutyRunner.StopDuty(CancellationToken.None).GetAwaiter().GetResult();
            _activeDutyStepId = null;
        }
        if (_runId is not null && !_engineEmittedRunEnd)
            _traceSession.Write(new RunEndEvent { RunId = _runId, Data = new RunEndEvent.RunEndData { Outcome = "ended" } });
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

    // When the DifficultySelectYesNo addon appears (SPD retry), select the configured
    // difficulty radio button and click Proceed. The exact FireCallback signatures must be
    // verified in-game via /qf debug addon DifficultySelectYesNo before this code goes live.
    // NodeIDs: 5=Normal, 6=Easy, 7=VeryEasy; Proceed button NodeID=13 (provisional).
    private unsafe void TryHandleDifficultySelect()
    {
        var addonPtr = _services.GameGui.GetAddonByName("DifficultySelectYesNo");
        if (addonPtr.IsNull || !addonPtr.IsReady) return;

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon == null || !addon->IsVisible) return;

        int radioIndex = _config.PreferredSpdDifficulty switch
        {
            SpdDifficulty.Easy     => 1,
            SpdDifficulty.VeryEasy => 2,
            _                      => 0
        };
        addon->FireCallbackInt(radioIndex);
        addon->FireCallbackInt(3); // Proceed
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
