using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestForge.Plugin.Interaction;

public sealed class CutSceneSelectStringResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IPluginLog _log;

    public CutSceneSelectStringResponder(
        EngineHost host, IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        _host = host;
        _addonLifecycle = addonLifecycle;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "CutSceneSelectString", OnSetup);
    }

    private void OnSetup(AddonEvent type, AddonArgs args) => Answer(args.Addon);

    private unsafe void Answer(nint addonAddr)
    {
        if (!_host.IsRunActive) return;

        var addon = (AtkUnitBase*)addonAddr;
        if (addon == null || !addon->IsVisible) return;

        _log.Debug("[CutSceneSelectStringResponder] selecting option 0");
        addon->FireCallbackInt(0);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "CutSceneSelectString", OnSetup);
    }
}
