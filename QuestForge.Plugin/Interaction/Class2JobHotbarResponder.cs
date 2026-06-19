using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestForge.Plugin.Interaction;

public sealed class Class2JobHotbarResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private bool _dismissed;

    public Class2JobHotbarResponder(
        EngineHost host, IAddonLifecycle addonLifecycle,
        IGameGui gameGui, IFramework framework, IPluginLog log)
    {
        _host = host;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _framework = framework;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Class2JobHotbar", OnEvent);
        _framework.Update += OnFrameworkUpdate;
    }

    private void OnEvent(AddonEvent type, AddonArgs args)
    {
        _dismissed = false;
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (_dismissed) return;
        if (!_host.IsRunActive && !_host.IsAutoMode) return;

        var ptr = _gameGui.GetAddonByName("Class2JobHotbar");
        if (ptr.IsNull || !ptr.IsReady) return;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible) return;

        _log.Debug("[Class2JobHotbarResponder] dismissing Class2JobHotbar");
        addon->Close(true);
        _dismissed = true;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Class2JobHotbar", OnEvent);
    }
}
