using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QuestForge.Adapters.Dalamud;
using QuestForge.Plugin.Commands;

namespace QuestForge.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private readonly EngineHost _host;
    private readonly QfCommand _command;
    private readonly IFramework _framework;
    private readonly CancellationTokenSource _cts = new();

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
        IGameInteropProvider hooks)
    {
        _framework = framework;

        var services = new PluginServices(
            pi, framework, clientState, condition,
            objectTable, dataManager, targetManager,
            chatGui, gameGui, log, hooks);

        _host    = new EngineHost(services);
        _command = new QfCommand(_host, commandManager, chatGui, log, pi);

        Directory.CreateDirectory(Path.Combine(pi.GetPluginConfigDirectory(), "quests"));

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
        _command.Dispose();
        _host.Dispose();
        _cts.Dispose();
    }
}
