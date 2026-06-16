using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestForge.Plugin.Interaction;

public sealed class ContentsTutorialResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private volatile bool _pendingDismiss;

    public ContentsTutorialResponder(
        EngineHost host, IAddonLifecycle addonLifecycle,
        IGameGui gameGui, IFramework framework, IPluginLog log)
    {
        _host = host;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _framework = framework;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsTutorial", OnEvent);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "ContentsTutorial", OnEvent);
        _framework.Update += OnFrameworkUpdate;
    }

    private void OnEvent(AddonEvent type, AddonArgs args)
    {
        if (_host.IsRunActive)
            _pendingDismiss = true;
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!_pendingDismiss) return;
        _pendingDismiss = false;

        if (!_host.IsRunActive) return;

        var ptr = _gameGui.GetAddonByName("ContentsTutorial");
        if (ptr.IsNull || !ptr.IsReady) return;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible) return;

        _log.Debug("[ContentsTutorialResponder] dismissing ContentsTutorial (deferred)");
        addon->FireCallbackInt(13);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ContentsTutorial", OnEvent);
        _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "ContentsTutorial", OnEvent);
    }
}
