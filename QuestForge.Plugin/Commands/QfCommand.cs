using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QuestForge.Adapters.Dalamud.Scheduling;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
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
    private readonly PlayerStatePanel _playerStatePanel;
    private readonly QuestStatePanel _questStatePanel;
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
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;

    private CombatController? _debugCombatController;
    private DateTime _debugCombatLastTick = DateTime.MinValue;
    private bool _debugCombatLoopActive;

    public QfCommand(
        EngineHost host,
        AuthoringHost authoringHost,
        AuthoringSessionPanel authoringSessionPanel,
        InteractionPanel interactionPanel,
        PlayerStatePanel playerStatePanel,
        QuestStatePanel questStatePanel,
        IQuestScheduler scheduler,
        UI.MainWindow mainWindow,
        LuminaQuestDataProvider questData,
        IDataManager dataManager,
        IGameGui gameGui,
        ICommandManager commands,
        IChatGui chat,
        IPluginLog log,
        IDalamudPluginInterface pi,
        PluginConfig config,
        IObjectTable objectTable,
        IFramework framework)
    {
        _host = host;
        _authoringHost = authoringHost;
        _authoringSessionPanel = authoringSessionPanel;
        _interactionPanel = interactionPanel;
        _playerStatePanel = playerStatePanel;
        _questStatePanel = questStatePanel;
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
        _objectTable = objectTable;
        _framework = framework;
        _commands.AddHandler(Cmd, new CommandInfo(OnCommand)
        {
            HelpMessage = "QuestForge: /qf run <id> | /qf start | /qf stop | /qf ui | /qf inspect | /qf author [questId] | /qf author stop | /qf quest <name> | /qf debug offered-quest | /qf debug quest <id> | /qf debug hostiles [radius] | /qf debug rotation start|stop | /qf config trace on|off"
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
                OpenAllAuthoringPanels();
                _chat.Print("QuestForge: inspect mode active — target an NPC to see available quests");
                break;
            case "author" when parts.Length >= 2 && parts[1] == "stop":
                _authoringHost.ExitAuthoring();
                _chat.Print("QuestForge: authoring stopped.");
                break;
            case "author" when parts.Length >= 2:
                HandleAuthor(parts[1]);
                break;
            case "author":
                OpenAllAuthoringPanels();
                _chat.Print("QuestForge: authoring panels opened — use /qf author <questId> to start recording");
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
            case "debug" when parts.Length >= 2 && parts[1] == "todolist":
                HandleDebugToDoList();
                break;
            case "debug" when parts.Length >= 2 && parts[1] == "aetheryte":
                HandleDebugAetheryte(parts.Length >= 3 ? parts[2] : null);
                break;
            case "debug" when parts.Length >= 3 && parts[1] == "quest":
                HandleDebugQuest(parts[2]);
                break;
            case "debug" when parts.Length >= 2 && parts[1] == "hostiles":
                HandleDebugHostiles(parts.Length >= 3 ? parts[2] : null);
                break;
            case "debug" when parts.Length >= 3 && parts[1] == "rotation":
                HandleDebugRotation(parts[2]);
                break;
            case "debug" when parts.Length >= 2 && parts[1] == "ngplus":
                HandleDebugNgPlus();
                break;
            case "debug" when parts.Length >= 2 && parts[1] == "yesno":
                // Diagnostic: fire our exact SelectYesno callback on demand (mirrors
                // DalamudInteractor.ConfirmYesNoPrompt). Lets us test whether FireCallback
                // takes effect during a non-skippable cutscene. /qf debug yesno [yes|no]
                HandleDebugYesno(parts.Length >= 3 && parts[2] == "no");
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

        foreach (var seq in quest.Sequences)
            foreach (var step in seq.Steps)
                _log.Debug($"[LoadedStep] seq={seq.Sequence} id={step.Id} type={step.GetType().Name} stopDist={step.StopDistance?.ToString() ?? "null"}");

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

    private unsafe void HandleDebugYesno(bool no)
    {
        var ptr = _gameGui.GetAddonByName("SelectYesno");
        if (ptr.IsNull) { _chat.Print("[QF] SelectYesno not open"); return; }
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;
        var ready = addon->IsReady;
        var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[1];
        values[0] = new FFXIVClientStructs.FFXIV.Component.GUI.AtkValue
        {
            Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int,
            Int  = no ? 1 : 0
        };
        addon->FireCallback(1, values);
        _chat.Print($"[QF] fired SelectYesno {(no ? "No(1)" : "Yes(0)")} (IsReady={ready}) — did it close?");
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

    private unsafe void HandleDebugAetheryte(string? idArg)
    {
        var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (uiState == null) { _chat.Print("UIState unavailable"); return; }

        var sb = new System.Text.StringBuilder();

        // If a specific ID was given, dump full diagnostic for that ID
        if (idArg is not null && uint.TryParse(idArg, out var checkId))
        {
            var uiUnlocked = uiState->IsAetheryteUnlocked(checkId);

            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            var row   = sheet?.GetRow(checkId);
            var isAetheryte   = row.HasValue ? row.Value.IsAetheryte : (bool?)null;
            var aethernetName = row.HasValue ? row.Value.AethernetName.ValueNullable?.Name.ExtractText() : null;

            var telepo    = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            bool inTelepo = false;
            int  telepoCount = 0;
            if (telepo != null)
            {
                telepoCount = (int)(telepo->TeleportList.Last - telepo->TeleportList.First);
                for (var ptr = telepo->TeleportList.First; ptr < telepo->TeleportList.Last; ptr++)
                    if (ptr->AetheryteId == checkId) { inTelepo = true; break; }
            }

            _chat.Print($"--- Aetheryte {checkId} ---");
            _chat.Print($"  UIState.IsAetheryteUnlocked = {uiUnlocked}");
            _chat.Print($"  Lumina row exists           = {row.HasValue}");
            _chat.Print($"  Lumina IsAetheryte          = {isAetheryte}");
            _chat.Print($"  Lumina AethernetName        = {aethernetName ?? "(null)"}");
            _chat.Print($"  Telepo instance exists      = {telepo != null}");
            _chat.Print($"  Telepo list count           = {telepoCount}");
            _chat.Print($"  In Telepo.TeleportList      = {inTelepo}");
            _log.Info($"[debug aetheryte] id={checkId} uiUnlocked={uiUnlocked} lumina.exists={row.HasValue} lumina.IsAetheryte={isAetheryte} lumina.Name={aethernetName} telepoCount={telepoCount} inTelepo={inTelepo}");
            return;
        }

        // Otherwise: scan nearby ObjectTable for Aetheryte objects and report BaseId vs unlock status
        var local = _objectTable.LocalPlayer;
        if (local is null) { _chat.Print("No local player"); return; }
        var playerPos = local.Position;

        _chat.Print("Nearby aetherytes (BaseId | unlocked | distance):");
        sb.AppendLine("Nearby aetherytes:");
        foreach (var obj in _objectTable)
        {
            if (obj is null) continue;
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte) continue;
            var dx = obj.Position.X - playerPos.X;
            var dz = obj.Position.Z - playerPos.Z;
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist > 100f) continue;

            var baseId = obj.BaseId;
            var unlocked = uiState->IsAetheryteUnlocked(baseId);
            var line = $"  BaseId={baseId} unlocked={unlocked} dist={dist:F1} pos=({obj.Position.X:F1},{obj.Position.Y:F1},{obj.Position.Z:F1})";
            _chat.Print(line);
            sb.AppendLine(line);
        }

        _log.Info($"[debug aetheryte]\n{sb}");
    }

    private unsafe void HandleDebugToDoList()
    {
        var addonPtr = _gameGui.GetAddonByName("_ToDoList");
        if (addonPtr.IsNull) { _chat.Print("_ToDoList addon not found (open the journal/todo list first)"); return; }

        var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonToDoList*)addonPtr.Address;
        var count = addon->ActionDataCount;
        var actionData = addon->ActionData; // Span<int>, FixedSizeArray128

        var header = $"_ToDoList ActionDataCount={count}, XPosition={addon->XPosition}";
        _chat.Print(header);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);

        // Print all active entries
        for (var i = 0; i < (int)Math.Min(count, (uint)actionData.Length); i++)
        {
            var val = actionData[i];
            // Decode: FFXIV typically packs quest data as two 16-bit halves
            var hi = (ushort)((uint)val >> 16);
            var lo = (ushort)((uint)val & 0xFFFF);
            var line = $"  ActionData[{i}] = 0x{val:X8} ({val}) | hi=0x{hi:X4} ({hi}) lo=0x{lo:X4} ({lo})";
            _chat.Print(line);
            sb.AppendLine(line);
        }

        // Also scan the rest of the array for any non-zero values
        var extras = 0;
        for (var i = (int)count; i < actionData.Length; i++)
        {
            if (actionData[i] != 0)
            {
                var val = actionData[i];
                var line = $"  ActionData[{i}] (beyond count) = 0x{val:X8} ({val})";
                _chat.Print(line);
                sb.AppendLine(line);
                if (++extras >= 10) break; // cap overflow scan
            }
        }

        _log.Info($"[debug todolist]\n{sb}");
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

        var ct = CancellationToken.None;
        var varsResult = _host.QuestState.GetQuestVariables(new QuestId(rawId), ct).GetAwaiter().GetResult();
        if (varsResult is Result<IReadOnlyList<byte>>.Success { Value: var vars } && vars.Count == 6)
        {
            var parts = new string[6];
            for (var i = 0; i < 6; i++)
            {
                var b = vars[i];
                parts[i] = b == 0 ? $"V{i}=0" : $"V{i}=0x{b:X2}(H:{b >> 4} L:{b & 0x0F})";
            }
            var line = $"  variables: {string.Join("  ", parts)}";
            _chat.Print(line);
            _log.Info($"[debug quest] {line}");
        }
        else
        {
            _chat.Print("  variables: (quest not accepted — no work bytes)");
        }

        var acceptableResult = _host.QuestState.IsAcceptableNow(new QuestId(rawId), ct).GetAwaiter().GetResult();
        var acceptableLine = acceptableResult switch
        {
            Result<bool>.Success { Value: var v } => $"  acceptableNow: {v.ToString().ToLowerInvariant()}",
            Result<bool>.Failure { Reason: var r } => $"  acceptableNow: (failure: {r})",
            _ => "  acceptableNow: (unknown)"
        };
        _chat.Print(acceptableLine);
        _log.Info($"[debug quest] {acceptableLine}");
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
        OpenAllAuthoringPanels();
        _chat.Print($"QuestForge: author mode active for quest {questId}");
    }

    private void OpenAllAuthoringPanels()
    {
        _authoringSessionPanel.IsOpen = true;
        _interactionPanel.IsOpen = true;
        _playerStatePanel.IsOpen = true;
        _questStatePanel.IsOpen = true;
    }

    private void HandleDebugHostiles(string? radiusArg)
    {
        var radius = 30f;
        if (radiusArg is not null && float.TryParse(radiusArg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            radius = parsed;

        try
        {
            var result = _host.DebugGameState.GetHostileActors(radius, CancellationToken.None).GetAwaiter().GetResult();
            if (result is Result<System.Collections.Generic.IReadOnlyList<HostileActor>>.Failure f)
            {
                _log.Warning($"[debug hostiles] failure: {f.Reason} — {f.Detail}");
                _chat.Print($"[QF] hostiles: failure — {f.Reason}");
                return;
            }

            var actors = ((Result<System.Collections.Generic.IReadOnlyList<HostileActor>>.Success)result).Value;
            var header = $"[debug hostiles] radius={radius:F0} count={actors.Count}";
            _log.Info(header);
            _chat.Print($"[QF] {header}");

            foreach (var a in actors)
            {
                var line = $"  ActorId={a.Id.Value} DataId(BaseId)={a.DataId} dist={a.DistanceToPlayer:F1}" +
                           $" targetable={a.IsTargetable} dead={a.IsDead}" +
                           $" aggroed={a.IsTargetingPlayer} enmity={a.OnPlayerEnmityList} questMark={a.HasQuestMarker}";
                _log.Info(line);
            }

            var summary = actors.Count == 0
                ? "(none in range)"
                : $"nearest DataId={actors[0].DataId} dist={actors[0].DistanceToPlayer:F1}";
            _chat.Print($"[QF] {summary} — see /xllog for full list");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[debug hostiles] unexpected exception");
            _chat.PrintError($"QuestForge: debug hostiles error — {ex.Message}");
        }
    }

    private void HandleDebugNgPlus()
    {
        try
        {
            var result = _host.DebugGameState.GetNewGamePlusState(CancellationToken.None).GetAwaiter().GetResult();
            if (result is Result<QuestForge.Adapters.State.NewGamePlusState>.Failure f)
            {
                _chat.PrintError($"[QF] ngplus: failure — {f.Reason}");
                return;
            }

            var s = ((Result<QuestForge.Adapters.State.NewGamePlusState>.Success)result).Value;
            var chapter = s.CurrentChapter is { } c ? $"{c.ChapterId}:{c.Name}" : "null";
            var activeQuest = s.ActiveReplayQuestId is { } q ? q.Value.ToString() : "null";
            var line = $"[QF] NG+ IsActive={s.IsActive} chapter={chapter} suspended={s.IsSuspended} activeReplayQuestId={activeQuest}";
            _log.Info(line);
            _chat.Print(line);
            _chat.Print("[QF] raw reads logged as '[QuestForge NG+ probe]' — see /xllog");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[debug ngplus] unexpected exception");
            _chat.PrintError($"QuestForge: debug ngplus error — {ex.Message}");
        }
    }

    private void HandleDebugRotation(string subCmd)
    {
        switch (subCmd)
        {
            case "start":
            {
                try
                {
                    if (_debugCombatLoopActive)
                        StopDebugCombatLoop();

                    var r = _host.DebugCombat.StartRotation(CancellationToken.None).GetAwaiter().GetResult();
                    if (r is Result<Unit>.Failure f)
                    {
                        _log.Warning($"[debug rotation start] failure: {f.Reason} — {f.Detail}");
                        _chat.Print($"[QF] rotation start failed: {f.Reason}");
                        return;
                    }

                    _debugCombatController = new CombatController(_host.DebugGameState, _host.DebugCombat, _host.DebugNavigator);
                    _framework.Update += OnDebugCombatTick;
                    _debugCombatLoopActive = true;

                    _log.Info("[debug rotation start] continuous loop started");
                    _chat.Print("[QF] continuous rotation started — retargets every ~250ms; run /qf debug rotation stop to end.");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[debug rotation start] unexpected exception");
                    _chat.PrintError($"QuestForge: debug rotation start error — {ex.Message}");
                }
                break;
            }
            case "stop":
            {
                StopDebugCombatLoop();
                break;
            }
            default:
                _chat.PrintError("QuestForge: /qf debug rotation start|stop");
                break;
        }
    }

    private void OnDebugCombatTick(IFramework framework)
    {
        if (!_debugCombatLoopActive || _debugCombatController is null) return;
        var now = DateTime.UtcNow;
        if (now - _debugCombatLastTick < TimeSpan.FromMilliseconds(250)) return;
        _debugCombatLastTick = now;
        try
        {
            var step = new CombatStep
            {
                Id = "debug-combat",
                KillEnemyDataIds = System.Array.Empty<uint>(),
                Spawn = CombatSpawn.AutoOnEnterArea,
            };
            _ = _debugCombatController.Decide(step, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[debug rotation tick] exception — stopping loop");
            StopDebugCombatLoop();
        }
    }

    private void StopDebugCombatLoop()
    {
        _framework.Update -= OnDebugCombatTick;
        _debugCombatLoopActive = false;
        _debugCombatController = null;

        try
        {
            var r = _host.DebugCombat.StopRotation(CancellationToken.None).GetAwaiter().GetResult();
            if (r is Result<Unit>.Failure f)
                _log.Warning($"[debug rotation stop] StopRotation failure: {f.Reason} — {f.Detail}");

            _host.DebugCombat.ClearTarget(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[debug rotation stop] exception during cleanup");
        }

        _log.Info("[debug rotation stop] continuous loop stopped — lease released");
        _chat.Print("[QF] continuous rotation stopped — lease released.");
    }

    private void PrintUsage()
        => _chat.Print("QuestForge: /qf run <id> | /qf start | /qf stop | /qf ui | /qf inspect | /qf author <questId> | /qf author stop | /qf quest <name> | /qf debug offered-quest | /qf debug quest <id> | /qf debug hostiles [radius] | /qf debug rotation start|stop | /qf config trace on|off");

    public void Dispose()
    {
        if (_debugCombatLoopActive)
            StopDebugCombatLoop();
        _commands.RemoveHandler(Cmd);
    }
}
