using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using QuestForge.Adapters.Dalamud;
using QuestForge.Plugin.Commands;

namespace QuestForge.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private readonly EngineHost _host;
    private readonly QfCommand _command;
    private readonly IFramework _framework;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDalamudPluginInterface _pi;
    private readonly WindowSystem _windowSystem = new("QuestForge");
    private readonly UI.MainWindow _mainWindow;
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

        _host = new EngineHost(services);

        var questsDir = Path.Combine(pi.GetPluginConfigDirectory(), "quests");
        _questData = new QuestForge.Adapters.Dalamud.Scheduling.LuminaQuestDataProvider(dataManager, questsDir);
        var questData = _questData;
        _scheduler = new QuestForge.Engine.Scheduling.QuestScheduler(
            _host.QuestState,
            _host.GameState,
            questData,
            new QuestForge.Engine.Scheduling.SchedulerOptions([], config.EnableCraftGatherQuests, config.EnableSideQuests),
            new QuestForge.Plugin.Logging.DalamudLogger<QuestForge.Engine.Scheduling.QuestScheduler>(log));

        _mainWindow = new UI.MainWindow(_host, _scheduler, config, pi);
        _windowSystem.AddWindow(_mainWindow);
        pi.UiBuilder.Draw += _windowSystem.Draw;
        pi.UiBuilder.OpenMainUi += _mainWindow.Toggle;

        _command = new QfCommand(_host, _scheduler, _mainWindow, _questData, dataManager, commandManager, chatGui, log, pi, config);

        Directory.CreateDirectory(questsDir);

        // Hook the game's cutscene handler to press Escape when a run is active.
        // If another plugin (e.g. TextAdvance) already holds the hook, Init throws —
        // catch silently: IGameConfig skip-all and our SelectString confirmation still work.
        try { AutoCutsceneSkipper.Init(_ => _host.IsRunActive); }
        catch { /* Hook already owned by another plugin — IGameConfig covers ESC */ }

        _framework.Update += OnFrameworkUpdate;
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
        _cts.Dispose();
        ECommonsMain.Dispose();
    }
}
