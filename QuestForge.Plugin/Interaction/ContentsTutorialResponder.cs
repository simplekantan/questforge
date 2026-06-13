using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestForge.Plugin.Interaction;

public sealed class ContentsTutorialResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IPluginLog _log;

    public ContentsTutorialResponder(
        EngineHost host, IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        _host = host;
        _addonLifecycle = addonLifecycle;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsTutorial", OnSetup);
    }

    private void OnSetup(AddonEvent type, AddonArgs args) => Dismiss(args.Addon);

    private unsafe void Dismiss(nint addonAddr)
    {
        if (!_host.IsRunActive) return;

        var addon = (AtkUnitBase*)addonAddr;
        if (addon == null || !addon->IsVisible) return;

        _log.Debug("[ContentsTutorialResponder] dismissing ContentsTutorial");
        addon->FireCallbackInt(13);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ContentsTutorial", OnSetup);
    }
}
