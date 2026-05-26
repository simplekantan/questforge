using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Authoring;
using QuestForge.Adapters.Tracing;
using QuestForge.Plugin.Authoring;
using QuestForge.Plugin.Commands;
using QuestForge.Plugin.UI.Authoring;

namespace QuestForge.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private readonly TraceSession _traceSession;
    private readonly EngineHost _host;
    private readonly AuthoringHost _authoringHost;
    private readonly QfCommand _command;
    private readonly IFramework _framework;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDalamudPluginInterface _pi;
    private readonly WindowSystem _windowSystem = new("QuestForge");
    private readonly UI.MainWindow _mainWindow;
    private readonly AuthoringSessionPanel _authoringSessionPanel;
    private readonly InteractionPanel _interactionPanel;
    private readonly PlayerStatePanel _playerStatePanel;
    private readonly QuestStatePanel _questStatePanel;
    private readonly QuestForge.Engine.Scheduling.QuestScheduler _scheduler;
    private readonly QuestForge.Adapters.Dalamud.Scheduling.LuminaQuestDataProvider _questData;

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
        IGameConfig gameConfig)
    {
        _framework = framework;
        _pi = pi;

        // ECommons must be initialized before AutoCutsceneSkipper
        ECommonsMain.Init(pi, this);

        var services = new PluginServices(
            pi, framework, clientState, condition,
            objectTable, dataManager, targetManager,
            chatGui, gameGui, log, hooks, gameConfig);

        var config = PluginConfig.Load(pi);

        var tracesDir = Path.Combine(pi.GetPluginConfigDirectory(), "traces");
        Directory.CreateDirectory(tracesDir);
        _traceSession = new TraceSession(
            config.TraceMode,
            tracesDir,
            onOpenError: ex => log.Error(ex, "QuestForge: failed to open trace file"));
        _traceSession.OnPluginStart();

        _host = new EngineHost(services, _traceSession);

        var questsDir = Path.Combine(pi.GetPluginConfigDirectory(), "quests");
        var questCategories = BuildQuestCategories(questsDir);
        _questData = new QuestForge.Adapters.Dalamud.Scheduling.LuminaQuestDataProvider(dataManager, questCategories);
        var questData = _questData;
        _scheduler = new QuestForge.Engine.Scheduling.QuestScheduler(
            _host.QuestState,
            _host.GameState,
            questData,
            new QuestForge.Engine.Scheduling.SchedulerOptions([], config.EnableCraftGatherQuests, config.EnableSideQuests, config.EnableBlueQuests),
            new QuestForge.Plugin.Logging.DalamudLogger<QuestForge.Engine.Scheduling.QuestScheduler>(log));

        _mainWindow = new UI.MainWindow(_host, _scheduler, config, pi, _traceSession);
        _windowSystem.AddWindow(_mainWindow);

        // Authoring infrastructure
        var draftsDir = Path.Combine(pi.GetPluginConfigDirectory(), "drafts");
        Directory.CreateDirectory(draftsDir);
        var draftStorage = new FileDraftStorage(draftsDir, log);
        _authoringHost = new AuthoringHost(services, draftStorage, log, config, _host.QuestState, _traceSession);

        var recordModal = new RecordStepModal(_authoringHost);
        var editModal = new StepEditModal(_authoringHost);
        var exportDialog = new ExportDialog(_authoringHost, pi, dataManager);
        _authoringSessionPanel = new AuthoringSessionPanel(_authoringHost, recordModal, editModal, exportDialog, config, pi);

        _windowSystem.AddWindow(_authoringSessionPanel);
        _windowSystem.AddWindow(recordModal);
        _windowSystem.AddWindow(editModal);
        _windowSystem.AddWindow(exportDialog);
        _playerStatePanel = new PlayerStatePanel(_authoringHost);
        _questStatePanel  = new QuestStatePanel(_authoringHost);
        _windowSystem.AddWindow(_playerStatePanel);
        _windowSystem.AddWindow(_questStatePanel);
        _interactionPanel = new InteractionPanel(_authoringHost, config, pi);
        _windowSystem.AddWindow(_interactionPanel);

        pi.UiBuilder.Draw += _windowSystem.Draw;
        pi.UiBuilder.OpenMainUi += _mainWindow.Toggle;

        _command = new QfCommand(_host, _authoringHost, _authoringSessionPanel, _interactionPanel, _playerStatePanel, _questStatePanel, _scheduler, _mainWindow, _questData, dataManager, gameGui, commandManager, chatGui, log, pi, config, objectTable, framework);

        Directory.CreateDirectory(questsDir);

        // Hook the game's cutscene handler to press Escape when a run is active.
        // If another plugin (e.g. TextAdvance) already holds the hook, Init throws —
        // catch silently: IGameConfig skip-all and our SelectString confirmation still work.
        try { AutoCutsceneSkipper.Init(_ => _host.IsRunActive); }
        catch { /* Hook already owned by another plugin — IGameConfig covers ESC */ }

        _framework.Update += OnFrameworkUpdate;
    }

    private static Dictionary<QuestForge.Adapters.Types.QuestId, string> BuildQuestCategories(string questsDir)
    {
        var result = new Dictionary<QuestForge.Adapters.Types.QuestId, string>();
        if (!Directory.Exists(questsDir)) return result;
        foreach (var file in Directory.EnumerateFiles(questsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), out var rawId)) continue;
            try
            {
                var def = QuestFileLoader.Load(file);
                if (def?.Category is { } cat)
                    result[new QuestForge.Adapters.Types.QuestId(rawId)] = cat;
            }
            catch { /* malformed file — skip */ }
        }
        return result;
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
        _command.Dispose();
        _host.Dispose();
        _authoringHost.Dispose();
        _traceSession.OnPluginStop();
        _traceSession.Dispose();
        _cts.Dispose();
        ECommonsMain.Dispose();
    }
}
