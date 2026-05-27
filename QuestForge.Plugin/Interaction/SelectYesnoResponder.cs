using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using QuestForge.Engine.Dialogue;

namespace QuestForge.Plugin.Interaction;

public sealed class SelectYesnoResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _log;

    public SelectYesnoResponder(EngineHost host, IAddonLifecycle addonLifecycle,
        IGameGui gameGui, IPluginLog log)
    {
        _host = host;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "SelectYesno", OnSetup);
    }

    public void TryAnswerOpenPopup()
    {
        var p = _gameGui.GetAddonByName("SelectYesno");
        if (!p.IsNull && p.IsReady) Answer(p.Address);
    }

    private void OnSetup(AddonEvent type, AddonArgs args) => Answer(args.Addon);

    private unsafe void Answer(nint addonAddr)
    {
        var decision = SelectYesnoDecider.Decide(
            new YesNoContext(_host.IsRunActive, _host.CurrentYesNoAnswer));
        if (decision is not { } answer) return;

        var addon = (AtkUnitBase*)addonAddr;
        if (addon == null || !addon->IsVisible) return;

        _log.Debug($"[SelectYesnoResponder] answering {answer}");
        addon->FireCallbackInt(answer == YesNoAnswer.Yes ? 0 : 1);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "SelectYesno", OnSetup);
    }
}
