using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Travel;
using QuestForge.Plugin.Authoring;
using QuestForge.Plugin.Commands;
using QuestForge.Plugin.Interaction;
using QuestForge.Adapters.Types;
using QuestForge.Plugin.DataDelivery;
using QuestForge.Plugin.UI.Authoring;

namespace QuestForge.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private readonly TraceSession _traceSession;
    private readonly EngineHost _host;
    private readonly SelectYesnoResponder _responder;
    private readonly DifficultySelectResponder _difficultyResponder;
    private readonly ContentsTutorialResponder _tutorialResponder;
    private readonly CutSceneSelectStringResponder _cutsceneSelectResponder;
    private readonly AuthoringHost _authoringHost;
    private readonly QfCommand _command;
    private readonly IFramework _framework;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDalamudPluginInterface _pi;
    private readonly WindowSystem _windowSystem = new("QuestForge");
    private readonly UI.MainWindow _mainWindow;
    private readonly UI.ConfigWindow _configWindow;
    private readonly AuthoringSessionPanel _authoringSessionPanel;
    private readonly InteractionPanel _interactionPanel;
    private readonly PlayerStatePanel _playerStatePanel;
    private readonly QuestStatePanel _questStatePanel;
    private readonly QuestForge.Engine.Scheduling.QuestScheduler _scheduler;
    private readonly QuestForge.Adapters.Dalamud.Scheduling.LuminaQuestDataProvider _questData;
    private readonly DataUpdateService _dataUpdate;

    public Plugin(
        IDalamudPluginInterface pi,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        IDataManager dataManager,
        ITargetManager targetManager,
        ICommandManager commandManager,
        IChatGui chatGui,
        IGameGui gameGui,
        IPluginLog log,
        IGameInteropProvider hooks,
        IGameConfig gameConfig,
        IAddonLifecycle addonLifecycle)
    {
        _framework = framework;
        _pi = pi;

        // ECommons must be initialized before AutoCutsceneSkipper
        ECommonsMain.Init(pi, this);

        var services = new PluginServices(
            pi, framework, clientState, condition,
            objectTable, dataManager, targetManager,
            chatGui, gameGui, log, hooks, gameConfig, addonLifecycle);

        var config = PluginConfig.Load(pi);

        var tracesDir = Path.Combine(pi.GetPluginConfigDirectory(), "traces");
        Directory.CreateDirectory(tracesDir);
        _traceSession = new TraceSession(
            config.TraceMode,
            tracesDir,
            onOpenError: ex => log.Error(ex, "QuestForge: failed to open trace file"));
        _traceSession.OnPluginStart();

        PopulateAetheryteZoneMap(dataManager, log);

        _host = new EngineHost(services, _traceSession, config);
        _responder = new SelectYesnoResponder(_host, addonLifecycle, gameGui, log);
        _difficultyResponder = new DifficultySelectResponder(_host, config, addonLifecycle, gameGui, log);
        _tutorialResponder = new ContentsTutorialResponder(_host, addonLifecycle, log);
        _cutsceneSelectResponder = new CutSceneSelectStringResponder(_host, addonLifecycle, log);
        _host.SetRunStartCallback(_responder.TryAnswerOpenPopup);

        var questsDir = Path.Combine(pi.GetPluginConfigDirectory(), "quests");
        var questIndex = QuestFileIndex.Build(questsDir, log);
        _host.SetQuestFileIndex(questIndex);
        _questData = new QuestForge.Adapters.Dalamud.Scheduling.LuminaQuestDataProvider(dataManager, questIndex.Metadata);
        var questData = _questData;
        var skipIfEvaluator = new QuestForge.Engine.Predicates.ExpectEvaluator(
            new QuestForge.Engine.Predicates.PredicateEvaluator(_host.GameState, _host.QuestState));
        _scheduler = new QuestForge.Engine.Scheduling.QuestScheduler(
            _host.QuestState,
            _host.GameState,
            questData,
            new QuestForge.Engine.Scheduling.SchedulerOptions(
                config.QuestPlaylist.Select(id => new QuestForge.Adapters.Types.QuestId(id)).ToList(),
                config.EnableCraftGatherQuests, config.EnableSideQuests, config.EnableBlueQuests),
            new QuestForge.Plugin.Logging.DalamudLogger<QuestForge.Engine.Scheduling.QuestScheduler>(log),
            evaluateSkipIf: skipIfEvaluator.Evaluate);

        _configWindow = new UI.ConfigWindow(config, pi, _traceSession, _host);
        var playlistWindow = new UI.PlaylistWindow(config, pi, dataManager, _host, questIndex);
        _mainWindow = new UI.MainWindow(_host, _scheduler, config);
        _mainWindow.SetConfigWindow(_configWindow);
        _mainWindow.SetPlaylistWindow(playlistWindow);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(playlistWindow);

        // Authoring infrastructure
        var draftsDir = Path.Combine(pi.GetPluginConfigDirectory(), "drafts");
        Directory.CreateDirectory(draftsDir);
        var draftStorage = new FileDraftStorage(draftsDir, log);
        var fragmentDraftStorage = new FileFragmentDraftStorage(draftsDir, log);
        _authoringHost = new AuthoringHost(services, draftStorage, fragmentDraftStorage, log, config, _host.QuestState, _traceSession);

        var recordModal = new RecordStepModal(_authoringHost);
        var editModal = new StepEditModal(_authoringHost);
        editModal.SetRecordModal(recordModal);
        var exportDialog = new ExportDialog(_authoringHost, pi, dataManager);
        _authoringSessionPanel = new AuthoringSessionPanel(_authoringHost, recordModal, editModal, exportDialog, pi);

        _windowSystem.AddWindow(_authoringSessionPanel);
        _windowSystem.AddWindow(recordModal);
        _windowSystem.AddWindow(editModal);
        _windowSystem.AddWindow(exportDialog);
        _playerStatePanel = new PlayerStatePanel(_authoringHost, _host.DebugGameState, dataManager, objectTable);
        _questStatePanel  = new QuestStatePanel(_authoringHost);
        _windowSystem.AddWindow(_playerStatePanel);
        _windowSystem.AddWindow(_questStatePanel);
        _interactionPanel = new InteractionPanel(_authoringHost, config, pi);
        _windowSystem.AddWindow(_interactionPanel);

        pi.UiBuilder.Draw += _windowSystem.Draw;
        pi.UiBuilder.OpenMainUi += _mainWindow.Toggle;
        pi.UiBuilder.OpenConfigUi += _configWindow.Toggle;

        _command = new QfCommand(_host, _authoringHost, _authoringSessionPanel, _interactionPanel, _playerStatePanel, _questStatePanel, _scheduler, _mainWindow, _questData, dataManager, gameGui, commandManager, chatGui, log, pi, config, objectTable, framework, hooks);

        Directory.CreateDirectory(questsDir);

        // Hook the game's cutscene handler to press Escape when a run is active.
        // If another plugin (e.g. TextAdvance) already holds the hook, Init throws —
        // catch silently: IGameConfig skip-all and our SelectString confirmation still work.
        try { AutoCutsceneSkipper.Init(_ => _host.IsRunActive); }
        catch { /* Hook already owned by another plugin — IGameConfig covers ESC */ }

        _dataUpdate = new DataUpdateService(pi, log, onDataUpdated: () =>
        {
            var newIndex = QuestFileIndex.Build(questsDir, log);
            _host.SetQuestFileIndex(newIndex);
            _questData.ReloadMetadata(newIndex.Metadata);
            log.Info("[DataUpdate] quest file index rebuilt after data update");
        });
        _ = _dataUpdate.CheckAndUpdateAsync(_cts.Token);

        _framework.Update += OnFrameworkUpdate;
    }

    private static void PopulateAetheryteZoneMap(IDataManager dataManager, IPluginLog log)
    {
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
        if (sheet == null)
        {
            log.Warning("QuestForge: Aetheryte sheet unavailable — AetheryteZoneMap will use defaults only");
            return;
        }

        var map = new Dictionary<uint, uint>();
        foreach (var row in sheet)
        {
            if (!row.IsAetheryte) continue;         // skip Aethernet shards
            var zoneId = row.Territory.RowId;
            if (zoneId == 0) continue;              // skip unrooted entries
            map[row.RowId] = zoneId;
        }

        AetheryteZoneMap.Populate(map);
        log.Debug($"QuestForge: AetheryteZoneMap populated with {map.Count} entries from Lumina");
    }


    private Task? _inflight;

    private void OnFrameworkUpdate(IFramework framework)
    {
        // If a tick is still in flight, skip this frame — adapters are synchronous so this
        // should never actually happen in Phase 6, but the guard prevents overlapping ticks
        // if an await somehow parks across frames in a future phase.
        if (_inflight is { IsCompleted: false }) return;
        _inflight = _host.TickAsync(_cts.Token);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _cts.Cancel();
        _pi.UiBuilder.Draw -= _windowSystem.Draw;
        _pi.UiBuilder.OpenMainUi -= _mainWindow.Toggle;
        _pi.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        _command.Dispose();
        _responder.Dispose();
        _difficultyResponder.Dispose();
        _tutorialResponder.Dispose();
        _cutsceneSelectResponder.Dispose();
        _host.Dispose();
        _authoringHost.Dispose();
        _traceSession.OnPluginStop();
        _traceSession.Dispose();
        _dataUpdate.Dispose();
        _cts.Dispose();
        ECommonsMain.Dispose();
    }
}
