using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestForge.Plugin.Interaction;

public sealed class DifficultySelectResponder : IDisposable
{
    private readonly EngineHost _host;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _log;
    private readonly PluginConfig _config;

    public DifficultySelectResponder(
        EngineHost host, PluginConfig config,
        IAddonLifecycle addonLifecycle, IGameGui gameGui, IPluginLog log)
    {
        _host = host;
        _config = config;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "DifficultySelectYesNo", OnSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "DifficultySelectYesNo", OnSetup);
    }

    public void TryAnswerOpenAddon()
    {
        var p = _gameGui.GetAddonByName("DifficultySelectYesNo");
        if (!p.IsNull && p.IsReady) Answer(p.Address);
    }

    private void OnSetup(AddonEvent type, AddonArgs args) => Answer(args.Addon);

    private unsafe void Answer(nint addonAddr)
    {
        if (!_host.IsRunActive) return;

        var addon = (AtkUnitBase*)addonAddr;
        if (addon == null || !addon->IsVisible) return;

        int radioIndex = _config.PreferredSpdDifficulty switch
        {
            SpdDifficulty.Easy     => 1,
            SpdDifficulty.VeryEasy => 2,
            _                      => 0
        };

        _log.Debug($"[DifficultySelectResponder] selecting difficulty {_config.PreferredSpdDifficulty} (radio={radioIndex})");
        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.Int, Int = radioIndex }
        };
        addon->FireCallback(2, values);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "DifficultySelectYesNo", OnSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "DifficultySelectYesNo", OnSetup);
    }
}
