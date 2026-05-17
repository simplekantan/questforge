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
    private readonly IGameGui _gameGui;
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
        IGameGui gameGui,
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
        _gameGui = gameGui;
        _commands = commands;
        _chat = chat;
        _log = log;
        _pi = pi;
        _config = config;
        _commands.AddHandler(Cmd, new CommandInfo(OnCommand)
        {
            HelpMessage = "QuestForge: /qf run <id> | /qf start | /qf stop | /qf ui | /qf inspect | /qf author <questId> | /qf author stop | /qf quest <name> | /qf debug offered-quest | /qf debug quest <id> | /qf config trace on|off"
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
            case "debug" when parts.Length >= 3 && parts[1] == "addon":
                HandleDebugAddon(parts[2]);
                break;
            case "debug" when parts.Length >= 2 && parts[1] == "offered-quest":
                HandleDebugOfferedQuest();
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

    private unsafe void HandleDebugAddon(string addonName)
    {
        var addonPtr = _gameGui.GetAddonByName(addonName);
        if (addonPtr.IsNull) { _chat.Print($"Addon '{addonName}' not found (not open?)"); return; }

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtr.Address;
        if (!addon->IsVisible) { _chat.Print($"Addon '{addonName}' exists but is not visible"); return; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Addon '{addonName}' — {addon->AtkValuesCount} AtkValues:");

        int count = Math.Min((int)addon->AtkValuesCount, 30);
        for (var i = 0; i < count; i++)
        {
            var val = addon->AtkValues[i];
            var valStr = val.Type switch
            {
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int    => val.Int.ToString(),
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt   => val.UInt.ToString(),
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool   => val.Byte.ToString(),
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String =>
                    val.String.Value == null ? "(null)" : val.String.ToString(),
                _ => $"({val.Type})"
            };
            var line = $"  [{i}] {val.Type,-8} = {valStr}";
            sb.AppendLine(line);
            _chat.Print(line);
        }

        if (addon->AtkValuesCount > 30)
            _chat.Print($"  ... (showing first 30 of {addon->AtkValuesCount})");

        _log.Info($"[debug addon]\n{sb}");
    }

    // JournalAccept AtkValue[261] holds the raw Lumina Quest RowId (without 0x10000 flag).
    // Confirmed by inspecting the addon with /qf debug addon JournalAccept + /xldata.
    private const int JournalAcceptQuestIdAtkIndex = 261;

    private unsafe void HandleDebugOfferedQuest()
    {
        var addonPtr = _gameGui.GetAddonByName("JournalAccept");
        if (addonPtr.IsNull) { _chat.Print("JournalAccept addon not open — talk to an NPC offering a quest first"); return; }

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtr.Address;
        if (!addon->IsVisible || addon->AtkValuesCount <= JournalAcceptQuestIdAtkIndex)
        {
            _chat.Print("JournalAccept addon not visible or AtkValues too short");
            return;
        }

        var rawRowId = addon->AtkValues[JournalAcceptQuestIdAtkIndex].UInt;
        var publicId = rawRowId | 0x10000u;  // Lumina RowId = publicId; AtkValue stores without 0x10000 flag

        var questSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        var name = questSheet?.GetRow(publicId).Name.ToString() ?? "(unknown)";

        var msg = $"Offered quest: [{publicId}] {name} (rawRowId={rawRowId})";
        _chat.Print(msg);
        _log.Info($"[debug offered-quest] {msg}");
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

        // Raw Lumina lookup — works for any quest ID, not just corpus quests
        var questSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        var classJobCatSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJobCategory>();
        var genreSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.JournalGenre>();
        if (questSheet == null) { _chat.PrintError("Quest sheet unavailable"); return; }

        var row = questSheet.GetRow(rawId);
        var name = row.Name.ToString();
        var classJobCatId = row.ClassJobCategory0.RowId;
        var level = row.ClassJobLevel[0];
        var genreId = row.JournalGenre.RowId;
        var journalCatId = genreId != 0 && genreSheet != null ? genreSheet.GetRow(genreId).JournalCategory.RowId : 0u;
        var prereqs = string.Join(",", new[] { row.PreviousQuest[0].RowId, row.PreviousQuest[1].RowId, row.PreviousQuest[2].RowId }.Where(p => p != 0));

        _chat.Print($"[{rawId}] {name}");
        _chat.Print($"  classJobCat={classJobCatId}  level={level}  genre={genreId}  journalCat={journalCatId}");
        _chat.Print($"  prereqs=[{prereqs}]  issuer={row.IssuerStart.RowId}");

        // Show which jobs the ClassJobCategory covers
        if (classJobCatId != 0 && classJobCatSheet != null)
        {
            var cat = classJobCatSheet.GetRow(classJobCatId);
            var jobs = new List<string>();
            if (cat.ADV) jobs.Add("ADV");
            if (cat.GLA) jobs.Add("GLA"); if (cat.PLD) jobs.Add("PLD");
            if (cat.MRD) jobs.Add("MRD"); if (cat.WAR) jobs.Add("WAR");
            if (cat.DRK) jobs.Add("DRK"); if (cat.GNB) jobs.Add("GNB");
            if (cat.CNJ) jobs.Add("CNJ"); if (cat.WHM) jobs.Add("WHM");
            if (cat.SCH) jobs.Add("SCH"); if (cat.AST) jobs.Add("AST"); if (cat.SGE) jobs.Add("SGE");
            if (cat.PGL) jobs.Add("PGL"); if (cat.MNK) jobs.Add("MNK");
            if (cat.LNC) jobs.Add("LNC"); if (cat.DRG) jobs.Add("DRG");
            if (cat.ROG) jobs.Add("ROG"); if (cat.NIN) jobs.Add("NIN");
            if (cat.ARC) jobs.Add("ARC"); if (cat.BRD) jobs.Add("BRD");
            if (cat.THM) jobs.Add("THM"); if (cat.BLM) jobs.Add("BLM");
            if (cat.ACN) jobs.Add("ACN"); if (cat.SMN) jobs.Add("SMN");
            if (cat.SAM) jobs.Add("SAM"); if (cat.RDM) jobs.Add("RDM");
            if (cat.BLU) jobs.Add("BLU"); if (cat.MCH) jobs.Add("MCH");
            if (cat.DNC) jobs.Add("DNC"); if (cat.RPR) jobs.Add("RPR");
            if (cat.VPR) jobs.Add("VPR"); if (cat.PCT) jobs.Add("PCT");
            if (cat.CRP) jobs.Add("CRP"); if (cat.BSM) jobs.Add("BSM"); if (cat.ARM) jobs.Add("ARM");
            if (cat.GSM) jobs.Add("GSM"); if (cat.LTW) jobs.Add("LTW"); if (cat.WVR) jobs.Add("WVR");
            if (cat.ALC) jobs.Add("ALC"); if (cat.CUL) jobs.Add("CUL");
            if (cat.MIN) jobs.Add("MIN"); if (cat.BTN) jobs.Add("BTN"); if (cat.FSH) jobs.Add("FSH");
            _chat.Print($"  jobs=[{string.Join(",", jobs)}]");
        }
        else
        {
            _chat.Print($"  jobs=[no restriction — classJobCat=0]");
        }

        // Also show corpus info if available
        var corpusInfo = _questData.GetDebugInfo(new QuestId(rawId), _dataManager);
        if (!corpusInfo.Contains("not in corpus"))
            _chat.Print($"  corpus: {corpusInfo}");

        var jobsLine = classJobCatId != 0 && classJobCatSheet != null
            ? $"jobs=[{string.Join(",", (classJobCatSheet.GetRow(classJobCatId) is var c2 ? new List<string>()
                .Concat(c2.ADV ? ["ADV"] : []).Concat(c2.GLA ? ["GLA"] : []).Concat(c2.PLD ? ["PLD"] : [])
                .Concat(c2.MRD ? ["MRD"] : []).Concat(c2.WAR ? ["WAR"] : []).Concat(c2.DRK ? ["DRK"] : []).Concat(c2.GNB ? ["GNB"] : [])
                .Concat(c2.CNJ ? ["CNJ"] : []).Concat(c2.WHM ? ["WHM"] : []).Concat(c2.SCH ? ["SCH"] : []).Concat(c2.AST ? ["AST"] : []).Concat(c2.SGE ? ["SGE"] : [])
                .Concat(c2.PGL ? ["PGL"] : []).Concat(c2.MNK ? ["MNK"] : []).Concat(c2.LNC ? ["LNC"] : []).Concat(c2.DRG ? ["DRG"] : [])
                .Concat(c2.ROG ? ["ROG"] : []).Concat(c2.NIN ? ["NIN"] : []).Concat(c2.ARC ? ["ARC"] : []).Concat(c2.BRD ? ["BRD"] : [])
                .Concat(c2.THM ? ["THM"] : []).Concat(c2.BLM ? ["BLM"] : []).Concat(c2.ACN ? ["ACN"] : []).Concat(c2.SMN ? ["SMN"] : [])
                .Concat(c2.SAM ? ["SAM"] : []).Concat(c2.RDM ? ["RDM"] : []).Concat(c2.BLU ? ["BLU"] : []).Concat(c2.MCH ? ["MCH"] : [])
                .Concat(c2.DNC ? ["DNC"] : []).Concat(c2.RPR ? ["RPR"] : []).Concat(c2.VPR ? ["VPR"] : []).Concat(c2.PCT ? ["PCT"] : [])
                .Concat(c2.CRP ? ["CRP"] : []).Concat(c2.BSM ? ["BSM"] : []).Concat(c2.ARM ? ["ARM"] : []).Concat(c2.GSM ? ["GSM"] : [])
                .Concat(c2.LTW ? ["LTW"] : []).Concat(c2.WVR ? ["WVR"] : []).Concat(c2.ALC ? ["ALC"] : []).Concat(c2.CUL ? ["CUL"] : [])
                .Concat(c2.MIN ? ["MIN"] : []).Concat(c2.BTN ? ["BTN"] : []).Concat(c2.FSH ? ["FSH"] : []) : []))}]"
            : "jobs=[no restriction]";
        _log.Info($"[debug quest] [{rawId}] {name}\n  classJobCat={classJobCatId}  level={level}  genre={genreId}  journalCat={journalCatId}\n  prereqs=[{prereqs}]  issuer={row.IssuerStart.RowId}\n  {jobsLine}");
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
        => _chat.Print("QuestForge: /qf run <id> | /qf start | /qf stop | /qf ui | /qf inspect | /qf author <questId> | /qf author stop | /qf quest <name> | /qf debug offered-quest | /qf debug quest <id> | /qf config trace on|off");

    public void Dispose() => _commands.RemoveHandler(Cmd);
}
