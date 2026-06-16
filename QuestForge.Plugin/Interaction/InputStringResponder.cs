using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestForge.Plugin.Interaction;

public sealed class InputStringResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly PluginConfig _config;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private volatile bool _pendingSubmit;

    public InputStringResponder(
        EngineHost host, PluginConfig config,
        IAddonLifecycle addonLifecycle, IGameGui gameGui,
        IFramework framework, IPluginLog log)
    {
        _host = host;
        _config = config;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _framework = framework;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "InputString", OnEvent);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "InputString", OnEvent);
        _framework.Update += OnFrameworkUpdate;
    }

    private void OnEvent(AddonEvent type, AddonArgs args)
    {
        if (_host.IsRunActive
            && !string.IsNullOrEmpty(_config.ChocoboName)
            && PluginConfig.IsValidChocoboName(_config.ChocoboName))
        {
            _pendingSubmit = true;
        }
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!_pendingSubmit) return;
        _pendingSubmit = false;

        if (!_host.IsRunActive) return;

        var name = _config.ChocoboName;
        if (string.IsNullOrEmpty(name) || !PluginConfig.IsValidChocoboName(name)) return;

        var ptr = _gameGui.GetAddonByName("InputString");
        if (ptr.IsNull || !ptr.IsReady) return;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible) return;

        _log.Debug($"[InputStringResponder] submitting \"{name}\" (deferred)");
        var values = stackalloc AtkValue[1];
        values[0] = new AtkValue { Type = AtkValueType.String };
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* namePtr = nameBytes)
        {
            values[0].String = namePtr;
            addon->FireCallback(1, values, true);
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "InputString", OnEvent);
        _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "InputString", OnEvent);
    }
}
