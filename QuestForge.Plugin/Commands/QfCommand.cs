using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QuestForge.Adapters.Dalamud.Scheduling;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;
using QuestForge.Plugin.Authoring;
using QuestForge.Plugin.UI.Authoring;
using QuestForge.Schema;

namespace QuestForge.Plugin.Commands;

internal sealed class QfCommand : IDisposable
{
    private const string Cmd = "/qf";

    private readonly EngineHost _host;
    private readonly AuthoringHost _authoringHost;
    private readonly AuthoringSessionPanel _authoringSessionPanel;
    private readonly InteractionPanel _interactionPanel;
    private readonly IQuestScheduler _scheduler;
    private readonly UI.MainWindow _mainWindow;
    private readonly LuminaQuestDataProvider _questData;
    private readonly IDataManager _dataManager;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;
    private readonly IDalamudPluginInterface _pi;
    private readonly PluginConfig _config;

    public QfCommand(
        EngineHost host,
        AuthoringHost authoringHost,
        AuthoringSessionPanel authoringSessionPanel,
        InteractionPanel interactionPanel,
        IQuestScheduler scheduler,
        UI.MainWindow mainWindow,
        LuminaQuestDataProvider questData,
        IDataManager dataManager,
        ICommandManager commands,
        IChatGui chat,
        IPluginLog log,
        IDalamudPluginInterface pi,
        PluginConfig config)
    {
        _host = host;
        _authoringHost = authoringHost;
        _authoringSessionPanel = authoringSessionPanel;
        _interactionPanel = interactionPanel;
        _scheduler = scheduler;
        _mainWindow = mainWindow;
        _questData = questData;
        _dataManager = dataManager;
        _commands = commands;
        _chat = chat;
        _log = log;
        _pi = pi;
        _config = config;
        _commands.AddHandler(Cmd, new CommandInfo(OnCommand)
        {
            HelpMessage = "QuestForge: /qf run <id> | /qf start | /qf stop | /qf ui | /qf inspect | /qf author <questId> | /qf author stop | /qf quest <name> | /qf debug quest <id> | /qf config trace on|off"
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
            case "start":
                _host.StartAutoMode(_scheduler, _config.UserTracingEnabled);
                break;
            case "stop":
                HandleStop();
                break;
            case "ui":
                _mainWindow.Toggle();
                break;
            case "inspect":
                _authoringHost.EnterInspectMode();
                _authoringSessionPanel.IsOpen = true;
                _interactionPanel.IsOpen = true;
                _chat.Print("QuestForge: inspect mode active — target an NPC to see available quests");
                break;
            case "author" when parts.Length >= 2 && parts[1] == "stop":
                _authoringHost.ExitAuthoring();
                _chat.Print("QuestForge: authoring stopped.");
                break;
            case "author" when parts.Length >= 2:
                HandleAuthor(parts[1]);
                break;
            case "quest" when parts.Length >= 2:
                HandleQuestSearch(string.Join(" ", parts[1..]));
                break;
            case "config" when parts.Length >= 3:
                HandleConfig(parts[1], parts[2]);
                break;
            case "debug" when parts.Length >= 2 && parts[1] == "target":
                HandleDebugTarget();
                break;
            case "debug" when parts.Length >= 3 && parts[1] == "quest":
                HandleDebugQuest(parts[2]);
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
        _host.BeginRun(quest, runId, _config.UserTracingEnabled);
        var traceNote = _config.UserTracingEnabled ? " (tracing on)" : "";
        _chat.Print($"QuestForge: run {runId} started for quest {questId}{traceNote}");
    }

    private void HandleStop()
    {
        if (_host.IsAutoMode)
        {
            _host.StopAutoMode();
            _chat.Print("QuestForge: auto mode stopped");
            return;
        }

        if (!_host.IsRunActive) { _chat.Print("QuestForge: no active run"); return; }
        var runId = _host.ActiveRunId;
        _host.StopRun();
        _chat.Print($"QuestForge: run {runId} stopped");
    }

    private void HandleDebugTarget()
    {
        var diag = _authoringHost.GetTargetDiagnostics();
        foreach (var line in diag.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line))
                _chat.Print($"[QF] {line.TrimEnd()}");
        _log.Info($"[debug target]\n{diag}");
    }

    private void HandleDebugQuest(string questIdStr)
    {
        if (!uint.TryParse(questIdStr, out var rawId))
        {
            _chat.PrintError($"QuestForge: invalid quest ID '{questIdStr}'");
            return;
        }
        var info = _questData.GetDebugInfo(new QuestId(rawId), _dataManager);
        _chat.Print($"QuestForge: {info}");
        _log.Info($"[debug quest] {info}");
    }

    private void HandleTest(string[] args)
    {
        switch (args[0])
        {
            case "gamestate":
            {
                var summary = _host.GetGameStateSummary();
                _chat.Print($"QuestForge: {summary}");
                _log.Info($"[gamestate] {summary}");
                break;
            }
            case "queststate":
            {
                var summary = _host.GetQuestStateSummary(66130);
                _chat.Print($"QuestForge: {summary}");
                _log.Info($"[queststate] {summary}");
                break;
            }
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

    private void HandleConfig(string key, string value)
    {
        if (key == "trace")
        {
            if (value == "on")  { _config.UserTracingEnabled = true;  _config.Save(_pi); _chat.Print("QuestForge: tracing enabled — runs will write to pluginConfigs\\QuestForge\\traces\\"); }
            else if (value == "off") { _config.UserTracingEnabled = false; _config.Save(_pi); _chat.Print("QuestForge: tracing disabled"); }
            else _chat.PrintError("QuestForge: /qf config trace on|off");
        }
        else
        {
            _chat.PrintError($"QuestForge: unknown config key '{key}'. Known keys: trace");
        }
    }

    private void HandleQuestSearch(string searchTerm)
    {
        var questSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        if (questSheet == null)
        {
            _chat.PrintError("QuestForge: Lumina Quest sheet unavailable");
            return;
        }

        var ct = CancellationToken.None;
        var questState = _host.QuestState;

        // Collect all matches — exact name hits first, then partial
        var exactMatches = new List<(uint PublicId, string Name, int Level)>();
        var partialMatches = new List<(uint PublicId, string Name, int Level)>();

        foreach (var quest in questSheet)
        {
            var name = quest.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            if (name.Equals(searchTerm, StringComparison.OrdinalIgnoreCase))
                exactMatches.Add(((uint)(quest.RowId | 0x10000u), name, quest.ClassJobLevel[0]));
            else if (name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                partialMatches.Add(((uint)(quest.RowId | 0x10000u), name, quest.ClassJobLevel[0]));
        }

        var allMatches = exactMatches.Concat(partialMatches).ToList();

        if (allMatches.Count == 0)
        {
            _chat.Print($"QuestForge: no quests found matching '{searchTerm}'");
            return;
        }

        var limit = Math.Min(allMatches.Count, 10);
        for (var i = 0; i < limit; i++)
        {
            var (publicId, name, level) = allMatches[i];
            var questId = new QuestId(publicId);

            // Determine availability status
            string status;
            var completeResult = questState.IsQuestComplete(questId, ct).GetAwaiter().GetResult();
            if (completeResult is Result<bool>.Success { Value: true })
            {
                status = "complete";
            }
            else
            {
                var availResult = questState.IsQuestAvailable(questId, ct).GetAwaiter().GetResult();
                status = availResult is Result<bool>.Success { Value: true } ? "available" : "locked";
            }

            _chat.Print($"[{publicId}] {name} — Lv{level} — {status}");
        }

        if (allMatches.Count > 10)
            _chat.Print($"QuestForge: showing first 10 of {allMatches.Count} results — refine your search");
    }

    private void HandleAuthor(string questIdStr)
    {
        if (!uint.TryParse(questIdStr, out var questId))
        {
            _chat.PrintError($"QuestForge: invalid quest ID '{questIdStr}'");
            return;
        }
        _authoringHost.EnterAuthorMode(new QuestId(questId));
        _authoringSessionPanel.IsOpen = true;
        _interactionPanel.IsOpen = true;
        _chat.Print($"QuestForge: author mode active for quest {questId}");
    }

    private void PrintUsage()
        => _chat.Print("QuestForge: /qf run <id> | /qf start | /qf stop | /qf ui | /qf inspect | /qf author <questId> | /qf author stop | /qf quest <name> | /qf debug quest <id> | /qf config trace on|off");

    public void Dispose() => _commands.RemoveHandler(Cmd);
}
