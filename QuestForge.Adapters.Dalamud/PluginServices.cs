using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace QuestForge.Adapters.Dalamud;

public sealed record PluginServices(
    IDalamudPluginInterface PluginInterface,
    IFramework Framework,
    IClientState ClientState,
    ICondition Condition,
    IObjectTable ObjectTable,
    IDataManager DataManager,
    ITargetManager TargetManager,
    IChatGui ChatGui,
    IGameGui GameGui,
    IPluginLog Log,
    IGameInteropProvider Hooks);