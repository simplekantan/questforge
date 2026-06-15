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
    private readonly IPluginLog _log;

    public InputStringResponder(
        EngineHost host, PluginConfig config,
        IAddonLifecycle addonLifecycle, IGameGui gameGui, IPluginLog log)
    {
        _host = host;
        _config = config;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "InputString", OnSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "InputString", OnSetup);
    }

    private void OnSetup(AddonEvent type, AddonArgs args) => TryAnswer(args.Addon);

    private unsafe void TryAnswer(nint addonAddr)
    {
        if (!_host.IsRunActive) return;

        var name = _config.ChocoboName;
        if (string.IsNullOrEmpty(name)) return;
        if (!PluginConfig.IsValidChocoboName(name)) return;

        var addon = (AtkUnitBase*)addonAddr;
        if (addon == null || !addon->IsVisible) return;

        var textInputNode = addon->GetNodeById(9);
        if (textInputNode == null) return;

        var componentNode = (AtkComponentNode*)textInputNode;
        if (componentNode->Component == null) return;

        var textInput = (AtkComponentTextInput*)componentNode->Component;
        textInput->SetText(name);

        _log.Debug($"[InputStringResponder] set text to \"{name}\", firing OK callback");
        addon->FireCallbackInt(0);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "InputString", OnSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "InputString", OnSetup);
    }
}
