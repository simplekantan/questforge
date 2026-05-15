using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QuestForge.Schema;

namespace QuestForge.Plugin.Commands;

internal sealed class QfCommand : IDisposable
{
    private const string Cmd = "/qf";

    private readonly EngineHost _host;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;
    private readonly IDalamudPluginInterface _pi;

    public QfCommand(
        EngineHost host,
        ICommandManager commands,
        IChatGui chat,
        IPluginLog log,
        IDalamudPluginInterface pi)
    {
        _host = host; _commands = commands; _chat = chat; _pi = pi;
        _commands.AddHandler(Cmd, new CommandInfo(OnCommand)
        {
            HelpMessage = "QuestForge: /qf run <questId> | /qf stop | /qf test <gamestate|queststate|navigate|interact>"
        });
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { PrintUsage(); return; }

        switch (parts[0])
        {
            case "run" when parts.Length >= 2:
                HandleRun(parts[1]);
                break;
            case "stop":
                HandleStop();
                break;
            case "test" when parts.Length >= 2:
                HandleTest(parts[1..]);
                break;
            default:
                PrintUsage();
                break;
        }
    }

    private void HandleRun(string questIdStr)
    {
        if (!uint.TryParse(questIdStr, out var questId))
        {
            _chat.PrintError($"QuestForge: invalid quest ID '{questIdStr}'");
            return;
        }

        var questDir = Path.Combine(_pi.GetPluginConfigDirectory(), "quests");
        var path     = Path.Combine(questDir, $"{questId}.json");

        if (!File.Exists(path))
        {
            _chat.PrintError($"QuestForge: quest file not found: {path}");
            return;
        }

        QuestDefinition? quest;
        try { quest = QuestFileLoader.Load(path); }
        catch (Exception ex)
        {
            _chat.PrintError($"QuestForge: failed to load quest {questId}: {ex.Message}");
            return;
        }

        if (quest is null)
        {
            _chat.PrintError($"QuestForge: quest file {questId} deserialized to null");
            return;
        }

        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        _host.BeginRun(quest, runId);
        _chat.Print($"QuestForge: run {runId} started for quest {questId}");
    }

    private void HandleStop()
    {
        if (!_host.IsRunActive) { _chat.Print("QuestForge: no active run"); return; }
        var runId = _host.ActiveRunId;
        _host.StopRun();
        _chat.Print($"QuestForge: run {runId} stopped");
    }

    private void HandleTest(string[] args)
    {
        switch (args[0])
        {
            case "gamestate":
                _chat.Print($"QuestForge: {_host.GetGameStateSummary()}");
                break;
            case "queststate":
                _chat.Print($"QuestForge: {_host.GetQuestStateSummary(66130)}");
                break;
            case "navigate":
                _chat.Print("QuestForge: /qf test navigate not wired in Phase 6 — use /qf run 66130");
                break;
            case "interact":
                _chat.Print("QuestForge: /qf test interact not wired in Phase 6 — use /qf run 66130");
                break;
            default:
                _chat.PrintError($"QuestForge: unknown test '{args[0]}'");
                break;
        }
    }

    private void PrintUsage()
        => _chat.Print("QuestForge: /qf run <questId> | /qf stop | /qf test <gamestate|queststate|navigate|interact>");

    public void Dispose() => _commands.RemoveHandler(Cmd);
}
