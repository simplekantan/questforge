using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace QuestForge.Spike;

// ---------------------------------------------------------------------------
// SPIKE CONSTANTS — fill these in before the first run.
// Use Lumina (DataManager.GetExcelSheet<Quest>()) or XIVAPI to look up IDs.
// ---------------------------------------------------------------------------
internal static class QuestData
{
    // "Coming to Ul'dah" — confirmed via DumpLookupData()
    public const uint QuestId = 66130;

    // Sequence numbers in FFXIV quest flow:
    //   0   = not in journal (null from QuestManager — either not accepted or already turned in)
    //   1…n = intermediate objectives (quest-specific, not all quests have these)
    //   255 = all objectives done, waiting for turn-in NPC interaction
    // After the player talks to the turn-in NPC, the quest leaves the journal entirely → QuestManager returns null → GetQuestSequence() returns 0.
    //
    // SPIKE FINDING: "Coming to Ul'dah" goes 0 → 255 directly — there is no intermediate
    // sequence. The quest is accepted and immediately at turn-in stage in the same moment.
    // SeqAccepted is only relevant for quests with intermediate objectives between
    // acceptance and turn-in. For this quest, WaitingForDialogue1 just polls for >= 255.
    // SPIKE FINDING: "Coming to Ul'dah" had no intermediate seq (went 0→255 directly).
    // "Close to Home" has seq=1 on acceptance — intermediate objectives exist.
    public const byte SeqAccepted       = 1;   // confirmed: active quest dump showed seq=1
    public const byte SeqReadyToTurnIn  = 255; // standard turn-in value — verify for this quest

    // Ul'dah - Steps of Nald — new-player instance confirmed via ClientState.TerritoryType in-game.
    // SPIKE FINDING: new characters land in 182, not the open-world 130.
    // The real engine must resolve zone variants (PlaceName lookup isn't sufficient —
    // multiple TerritoryType rows share the same PlaceName). It will need to know
    // which variant to expect based on player progression state.
    public const uint TargetZone = 182;

    // Ul'dah aetheryte — confirmed via DumpLookupData() (IsAetheryte=True)
    public const uint AetheryteId = 9;

    // NPC 1: Wymond — confirmed via /xldata > Target
    // ApproachPos is a confirmed walkable position adjacent to Wymond from which
    // the player can interact. Raw NPC position (33.37, 4.1, -151.99) is inside geometry.
    public const uint Npc1DataId = 1003987;
    public static readonly Vector3 Npc1ApproachPos = new(35.55988f, 4f, -151.17778f);

    // NPC 2: Momodi — confirmed via /xldata > Target
    // SPIKE FINDING: NPC raw positions are often inside geometry and unreachable by vnavmesh.
    // ApproachPos only needs to be close enough to load the NPC into the ObjectTable —
    // the re-nav loop takes over from there using the NPC's live ObjectTable position.
    public const uint Npc2DataId = 1003988;
    public static readonly Vector3 Npc2ApproachPos = new(21.835424f, 6.999995f, -81.1309f);

    // How close we need to be before attempting interaction.
    // SPIKE FINDING: 10 yalms triggers "too far to interact" — FFXIV's actual interact
    // radius is ~6 yalms. Nav approach position must place the player within that range.
    public const float InteractRange = 2.5f;
}

// ---------------------------------------------------------------------------
// State machine
// ---------------------------------------------------------------------------
internal enum SpikeState
{
    Idle,
    Teleporting,
    WaitingForZone,
    NavigatingToNpc1,
    InteractingWithNpc1,
    WaitingForDialogue1,
    NavigatingToNpc2,
    InteractingWithNpc2,
    WaitingForDialogue2,
    Done,
    Failed,
}

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] internal static IDataManager           DataManager     { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;
    [PluginService] internal static ITargetManager         TargetManager   { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui         { get; private set; } = null!;
    [PluginService] internal static ICondition             Condition       { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInterop     { get; private set; } = null!;
    [PluginService] internal static ISigScanner            SigScanner      { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui         { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log             { get; private set; } = null!;

    private const string Command = "/qfspike";

    // --- vnavmesh IPC --- confirmed against IPCProvider.cs in ffxiv_navmesh source
    private readonly ICallGateSubscriber<bool>                 _navIsReady;
    private readonly ICallGateSubscriber<bool>                 _pathIsRunning;          // Path.IsRunning
    private readonly ICallGateSubscriber<Vector3, bool, bool>  _navPathfindAndMoveTo;   // SimpleMove.PathfindAndMoveTo
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _navPathfindAndMoveCloseTo; // SimpleMove.PathfindAndMoveCloseTo
    private readonly ICallGateSubscriber<bool>                 _navStop;                // Path.Stop

    // --- Lifestream IPC ---
    // SPIKE NOTE: confirm gate names against Lifestream source.
    // Source: look for IPCProvider or IPC.cs in Lifestream repo
    private readonly ICallGateSubscriber<uint, byte, bool> _lifestreamTeleport;
    private readonly ICallGateSubscriber<bool>             _lifestreamIsBusy;

    // --- TextAdvance IPC ---
    // SPIKE NOTE: TextAdvance may auto-handle dialogue when enabled — we may not need
    // explicit IPC calls at all. Verify whether we need to call anything or just wait.
    // If explicit skipping is needed, find the correct gate name in TextAdvance source.
    private readonly ICallGateSubscriber<bool> _textAdvanceIsEnabled;

    private SpikeState _state = SpikeState.Idle;
    private int        _ticksInState;
    private bool       _sawTalkAddon; // tracks whether Talk addon appeared during TickInteract
    private const int  TimeoutTicks = 54000; // ~15 minutes at 60fps — worst-case unmounted travel time

    // Cutscene skip hook — replicates TextAdvance ECommons AutoCutsceneSkipper.
    // TODO: moves to the concrete IInteractor implementation in Phase 6.
    private delegate byte CutsceneHandleInputDelegate(nint a1, float a2);
    private Hook<CutsceneHandleInputDelegate>? _cutsceneHook;
    private nint _cutsceneSkipPatchAddr;
    // Signatures sourced from TextAdvance/ECommons AutoCutsceneSkipper.cs
    private const string CutsceneHandleInputSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 40 " +
        "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 80 79 29 00";
    private const string CutsceneSkipConditionSig =
        "75 11 BA ?? ?? ?? ?? 48 8B CF E8 ?? ?? ?? ?? 84 C0 74 4C";

    public Plugin()
    {
        _navIsReady                 = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _pathIsRunning              = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _navPathfindAndMoveTo       = PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        _navPathfindAndMoveCloseTo  = PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _navStop                    = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.Stop");

        _lifestreamTeleport = PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        _lifestreamIsBusy   = PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");

        _textAdvanceIsEnabled = PluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsEnabled");

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Start the Coming-to-Ul'dah spike automation. /qfspike lookup — dump IDs to log.",
        });

        Framework.Update += OnUpdate;
        InitCutsceneSkip();
        DumpLookupData();
    }

    // Logs the IDs we need to fill in QuestData constants.
    // Run once after loading the plugin — read output in /xldev > Log.
    private static void DumpLookupData()
    {
        Log.Information("[Spike] === LOOKUP DUMP ===");

        // Active quests in the player's journal right now
        unsafe
        {
            var qm = QuestManager.Instance();
            var questSheet = DataManager.GetExcelSheet<Quest>();
            for (var i = 0; i < 30; i++)
            {
                var q = qm->NormalQuests[i];
                if (q.QuestId == 0) continue;
                // QuestManager stores the lower 16 bits; Lumina row IDs have a category prefix
                // Try to find the matching Lumina row by checking both common prefixes
                var rowId = q.QuestId | 0x10000u;
                var name = questSheet.TryGetRow(rowId, out var row) ? row.Name.ToString() : $"(unknown)";
                Log.Information("[Spike] Active quest: '{Name}' gameId={GId} rowId={RId} seq={Seq}",
                    name, q.QuestId, rowId, q.Sequence);
            }
        }

        // Quests we care about (for ID lookup)
        foreach (var row in DataManager.GetExcelSheet<Quest>())
        {
            var name = row.Name.ToString();
            if (name.Contains("Coming to Ul", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Close to Home", StringComparison.OrdinalIgnoreCase))
                Log.Information("[Spike] Quest '{Name}' → RowId={Id}", name, row.RowId);
        }

        // TerritoryType: Ul'dah - Steps of Nald
        // TerritoryIntendedUse.RowId == 1 filters to open-world zones only (excludes duty instances).
        foreach (var row in DataManager.GetExcelSheet<TerritoryType>())
        {
            var name = row.PlaceName.Value.Name.ToString();
            if (name.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase) && row.TerritoryIntendedUse.RowId == 1)
                Log.Information("[Spike] TerritoryType (open-world) '{Name}' → RowId={Id}", name, row.RowId);
        }

        // Aetheryte: Ul'dah
        foreach (var row in DataManager.GetExcelSheet<Aetheryte>())
        {
            var name = row.PlaceName.Value.Name.ToString();
            if (name.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase))
                Log.Information("[Spike] Aetheryte '{Name}' → RowId={Id}, IsAetheryte={IsAetheryte}", name, row.RowId, row.IsAetheryte);
        }

        Log.Information("[Spike] === END LOOKUP DUMP ===");
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        CommandManager.RemoveHandler(Command);
        TryStopNav();
        _cutsceneHook?.Dispose();
    }

    private void InitCutsceneSkip()
    {
        try
        {
            var fnAddr = SigScanner.ScanText(CutsceneHandleInputSig);
            _cutsceneHook = GameInterop.HookFromAddress<CutsceneHandleInputDelegate>(
                fnAddr, CutsceneHandleInputDetour);
            _cutsceneHook.Enable();

            _cutsceneSkipPatchAddr = SigScanner.ScanText(CutsceneSkipConditionSig);
            Log.Information("[Spike] Cutscene skip hook initialized at 0x{Addr:X}.", fnAddr);
        }
        catch (Exception ex)
        {
            Log.Warning("[Spike] Cutscene skip hook init failed: {Ex}. Cutscene skip disabled.", ex.Message);
        }
    }

    private unsafe byte CutsceneHandleInputDetour(nint a1, float a2)
    {
        if (!Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent])
            return _cutsceneHook!.Original(a1, a2);

        // Check skippability via game struct offset 0x38 (56 bytes), sourced from TextAdvance.
        var skippable = *(nint*)(a1 + 56) != nint.Zero;
        if (!skippable || _cutsceneSkipPatchAddr == nint.Zero)
            return _cutsceneHook!.Original(a1, a2);

        // Patch: change conditional JNZ (0x75) → unconditional JMP (0xEB) so the game
        // processes ESC as a skip trigger. Restored immediately after the call.
        var originalByte = Marshal.ReadByte(_cutsceneSkipPatchAddr);
        VirtualProtect(_cutsceneSkipPatchAddr, 1, 0x40, out var oldProtect);
        Marshal.WriteByte(_cutsceneSkipPatchAddr, 0xEB);
        var result = _cutsceneHook!.Original(a1, a2);
        Marshal.WriteByte(_cutsceneSkipPatchAddr, originalByte);
        VirtualProtect(_cutsceneSkipPatchAddr, 1, oldProtect, out _);
        return result;
    }

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();

        if (arg.StartsWith("seq", StringComparison.OrdinalIgnoreCase))
        {
            // /qfspike seq          → uses QuestData.QuestId
            // /qfspike seq <id>     → checks arbitrary quest ID
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            uint questId = QuestData.QuestId;
            if (parts.Length > 1 && uint.TryParse(parts[1], out var parsed))
                questId = parsed;

            var gameId = (ushort)(questId & 0xFFFF);
            unsafe
            {
                var quest = QuestManager.Instance()->GetQuestById(gameId);
                var seq = quest == null ? (byte)0 : quest->Sequence;
                var complete = QuestManager.IsQuestComplete(gameId);
                var status = seq > 0 ? "in-progress" : complete ? "completed" : "not accepted";
                Log.Information("[Spike] Quest {Id} (game id {GId}) seq={Seq} [{Status}]", questId, gameId, seq, status);
                ChatGui.Print($"[QFSpike] Quest {questId} seq={seq} [{status}]");
            }
            return;
        }

        if (arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            TransitionTo(SpikeState.Idle);
            TryStopNav();
            return;
        }

        if (_state != SpikeState.Idle && _state != SpikeState.Done && _state != SpikeState.Failed)
        {
            Log.Information("[Spike] Already running (state={State}). Use /qfspike stop to cancel.", _state);
            return;
        }

        var startState = DetermineStartState();
        Log.Information("[Spike] Starting automation for 'Coming to Ul'dah' from {State}.", startState);
        TransitionTo(startState);
    }

    // Determine where in the state machine to begin based on current zone + quest sequence.
    // This handles: already in zone (no teleport needed), quest already at turn-in stage, etc.
    private static SpikeState DetermineStartState()
    {
        var seq = GetQuestSequence();

        // seq=0 is ambiguous — could be "not yet accepted" or "already completed".
        if (seq == 0 && IsQuestComplete())
        {
            Log.Warning("[Spike] Quest {Id} is already complete on this character. Nothing to do.", QuestData.QuestId);
            return SpikeState.Done;
        }

        if (seq >= QuestData.SeqReadyToTurnIn)
        {
            if (ClientState.TerritoryType == QuestData.TargetZone)
                return SpikeState.NavigatingToNpc2;
            return SpikeState.Teleporting;
        }

        // seq=0 (not accepted) or seq=1..n (in-progress, NPC1 objective pending)
        if (ClientState.TerritoryType == QuestData.TargetZone)
            return SpikeState.NavigatingToNpc1;

        return SpikeState.Teleporting;
    }

    // ---------------------------------------------------------------------------
    // Main tick — runs every framework frame
    // ---------------------------------------------------------------------------
    private void OnUpdate(IFramework fw)
    {
        if (_state is SpikeState.Idle) return;

        // Advance dialogue addons every ~15 ticks — fast enough to feel responsive,
        // slow enough not to hammer ReceiveEvent every frame.
        if (_uiHandlerTick % 15 == 0)
        {
            TryAdvanceTalkAddon();
            TryAdvanceBattleTalkAddon();
        }

        if (_state is SpikeState.Done or SpikeState.Failed) return;

        TryHandleBlockingUi();

        _ticksInState++;
        if (_ticksInState > TimeoutTicks)
        {
            Fail($"Timed out in state {_state} after {TimeoutTicks} ticks.");
            return;
        }

        switch (_state)
        {
            case SpikeState.Teleporting:       TickTeleporting();       break;
            case SpikeState.WaitingForZone:    TickWaitingForZone();    break;
            case SpikeState.NavigatingToNpc1:  TickNavigating(QuestData.Npc1ApproachPos, QuestData.Npc1DataId, SpikeState.InteractingWithNpc1); break;
            case SpikeState.InteractingWithNpc1: TickInteract(QuestData.Npc1DataId, SpikeState.WaitingForDialogue1); break;
            case SpikeState.WaitingForDialogue1: TickWaitForQuestProgress(QuestData.SeqReadyToTurnIn, SpikeState.NavigatingToNpc2); break;
            case SpikeState.NavigatingToNpc2:  TickNavigating(QuestData.Npc2ApproachPos, QuestData.Npc2DataId, SpikeState.InteractingWithNpc2); break;
            case SpikeState.InteractingWithNpc2: TickInteract(QuestData.Npc2DataId, SpikeState.WaitingForDialogue2); break;
            case SpikeState.WaitingForDialogue2: TickWaitForQuestTurnedIn(); break;
        }
    }

    // ---------------------------------------------------------------------------
    // State handlers
    // ---------------------------------------------------------------------------

    private void TickTeleporting()
    {
        if (_ticksInState > 1) return; // only call teleport once

        bool ok;
        try { ok = _lifestreamTeleport.InvokeFunc(QuestData.AetheryteId, 0); }
        catch (Exception ex) { Fail($"Lifestream IPC failed: {ex.Message}"); return; }

        if (!ok)
        {
            // SPIKE NOTE (confirmed): Lifestream.Teleport returns false when the aetheryte
            // is not attuned OR player has insufficient gil. It does NOT throw — it returns false.
            // For the real engine, IAdapterTeleporter must check attunement state before calling
            // and surface a distinct "not attuned" result rather than a generic failure.
            Log.Warning("[Spike] Lifestream.Teleport returned false (not attuned or no gil).");
            Log.Warning("[Spike] Travel to Ul'dah manually, then run /qfspike again.");
            ChatGui.Print("[QFSpike] Can't teleport — not attuned or no gil. Travel to Ul'dah manually then /qfspike again.");
            TransitionTo(SpikeState.Idle);
            return;
        }

        Log.Information("[Spike] Teleport requested — waiting for zone change.");
        TransitionTo(SpikeState.WaitingForZone);
    }

    private void TickWaitingForZone()
    {
        if (ClientState.TerritoryType != QuestData.TargetZone) return;

        // SPIKE NOTE: vnavmesh may still be initialising its navmesh after a zone change.
        // If IsReady() returns false here, we need to wait longer. Document whether
        // polling IsReady is sufficient or if there's a lifecycle event to listen to.
        bool ready;
        try { ready = _navIsReady.InvokeFunc(); }
        catch (Exception ex) { Fail($"vnavmesh.Nav.IsReady IPC failed: {ex.Message}"); return; }

        if (!ready) return;

        Log.Information("[Spike] In zone and navmesh ready — navigating to NPC 1.");
        TransitionTo(SpikeState.NavigatingToNpc1);
    }

    // Re-issue PathfindAndMoveCloseTo toward NPC's live position every N ticks once visible.
    private const int ReNavIntervalTicks = 120; // ~2 seconds at 60fps

    private void TickNavigating(Vector3 destination, uint npcDataId, SpikeState nextState)
    {
        if (_ticksInState == 1)
        {
            // Use PathfindAndMoveCloseTo so vnavmesh stops automatically within InteractRange.
            // ApproachPos gets us into the area; vnavmesh handles the final stop.
            try { _navPathfindAndMoveCloseTo.InvokeFunc(destination, false, QuestData.InteractRange); }
            catch (Exception ex) { Fail($"vnavmesh.SimpleMove.PathfindAndMoveCloseTo IPC failed: {ex.Message}"); return; }
        }

        // Once NPC is visible, home in on their live position with the same tolerance.
        // vnavmesh will stop when within InteractRange — no manual distance check needed.
        var npc = FindNearestNpc(npcDataId);
        if (npc != null && _ticksInState % ReNavIntervalTicks == 0)
        {
            try { _navPathfindAndMoveCloseTo.InvokeFunc(npc.Position, false, QuestData.InteractRange); }
            catch (Exception ex) { Fail($"vnavmesh re-nav failed: {ex.Message}"); }
        }

        // Wait at least 30 ticks (~0.5s) for vnavmesh to start path following before polling.
        // Path.IsRunning returns false before the async pathfind computation completes.
        if (_ticksInState < 30) return;

        bool running;
        try { running = _pathIsRunning.InvokeFunc(); }
        catch (Exception ex) { Fail($"vnavmesh.Path.IsRunning IPC failed: {ex.Message}"); return; }

        if (!running)
        {
            Log.Information("[Spike] vnavmesh arrived within {Range} yalms of NPC {Id}. Next: {Next}.", QuestData.InteractRange, npcDataId, nextState);
            TransitionTo(nextState);
        }
    }

    private void TickInteract(uint npcDataId, SpikeState nextState)
    {
        if (_ticksInState == 1)
        {
            _sawTalkAddon = false;

            var npc = FindNearestNpc(npcDataId);
            if (npc == null) { Fail($"NPC {npcDataId} not found in ObjectTable."); return; }

            TargetManager.Target = npc;

            // Trigger interaction via TargetSystem.InteractWithObject.
            // SPIKE NOTE: verify this is the correct FFXIVClientStructs method name and
            // signature in the current game version — check against Questionable source
            // or FFXIVClientStructs repo if this produces an error or does nothing.
            unsafe
            {
                var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
                if (ts->Target != null)
                    ts->InteractWithObject(ts->Target, false);
            }

            Log.Information("[Spike] Targeted and interacted with NPC {Id} — waiting for Talk addon.", npcDataId);
            return;
        }

        // Wait for the Talk addon to appear (confirms interaction registered),
        // then disappear (TextAdvance advanced all dialogue).
        // SPIKE NOTE: for a turn-in with a cutscene or SelectString prompt the addon
        // name may differ — check /xldata > Addon list if this never triggers.
        // Quest sequence escape hatch: JournalAccept can be handled by TryHandleBlockingUi
        // without Talk ever appearing, leaving _sawTalkAddon false permanently.
        // If the quest already advanced to turn-in stage, skip the Talk wait entirely.
        if (GetQuestSequence() >= QuestData.SeqReadyToTurnIn)
        {
            Log.Information("[Spike] Quest at seq=255 — skipping Talk wait. Next: {Next}.", nextState);
            TransitionTo(nextState);
            return;
        }

        var talkVisible = GameGui.GetAddonByName("Talk") != nint.Zero;

        if (talkVisible)
        {
            _sawTalkAddon = true;
        }
        else if (_sawTalkAddon)
        {
            Log.Information("[Spike] Talk addon closed — dialogue complete. Next: {Next}.", nextState);
            TransitionTo(nextState);
            return;
        }

        // Interaction didn't open dialogue within 5 seconds — retry interact.
        // SPIKE NOTE: document whether this ever fires and what caused it.
        if (!_sawTalkAddon && _ticksInState % 300 == 0)
        {
            Log.Warning("[Spike] Talk addon not seen after {Ticks} ticks — retrying interact.", _ticksInState);
            unsafe
            {
                var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
                if (ts->Target != null)
                    ts->InteractWithObject(ts->Target, false);
            }
        }
    }

    private void TickWaitForQuestProgress(byte expectedSequence, SpikeState nextState)
    {
        // SPIKE NOTE: does the sequence update on the same frame as dialogue close,
        // or is there a one-frame lag? Document after testing.
        if (GetQuestSequence() >= expectedSequence)
        {
            Log.Information("[Spike] Quest sequence reached {Seq}. Advancing to {Next}.", expectedSequence, nextState);
            TransitionTo(nextState);
        }
    }

    private void TickWaitForQuestTurnedIn()
    {
        // Turn-in is complete when the quest leaves the journal entirely.
        // QuestManager returns null → GetQuestSequence() returns 0.
        // SPIKE NOTE: is there a one-frame lag here too? Does the sequence briefly
        // show 255 on the same frame as NPC dialogue closes, then drop to 0 next frame?
        if (GetQuestSequence() == 0)
        {
            Log.Information("[Spike] Quest left journal — turn-in confirmed. Automation complete.");
            TransitionTo(SpikeState.Done);
        }
    }

    // ---------------------------------------------------------------------------
    // Background UI handling — runs every tick while automation is active
    // ---------------------------------------------------------------------------

    private int _uiHandlerTick;
    private const int UiHandlerIntervalTicks = 5; // ~80ms — fast enough to catch short-lived addons

    private void TryHandleBlockingUi()
    {
        if (++_uiHandlerTick < UiHandlerIntervalTicks) return;
        _uiHandlerTick = 0;
        TrySkipCutsceneDialog();
        TryAdvanceTalkAddon();
        TryAcceptQuestWindow();
        TryCompleteQuestWindow();
    }

    private static unsafe void TryAdvanceBattleTalkAddon()
    {
        // _BattleTalk is the NPC speech bubble shown during cutscenes and battle events.
        // Appears instead of (or alongside) Talk when dialogue plays during cinematic sequences.
        // TODO: becomes IInteractor.AdvanceDialogue() alongside TryAdvanceTalkAddon in Phase 3.
        var ptr = (nint)GameGui.GetAddonByName("_BattleTalk");
        if (ptr == nint.Zero) return;
        var addon = (AtkUnitBase*)ptr;
        if (!addon->IsVisible) return;

        // _BattleTalk uses the same WindowHeaderCollisionNode advance pattern as system Talk dialogs.
        // SPIKE NOTE: verify this advances _BattleTalk — if not, inspect node structure
        // via /xldata > Addon > _BattleTalk to find the correct click target.
        var collNode = addon->WindowHeaderCollisionNode;
        if (collNode == null) return;

        var evt = collNode->AtkResNode.AtkEventManager.Event;
        if (evt == null) return;

        addon->ReceiveEvent(AtkEventType.MouseClick, (int)evt->Param, evt);
    }

    private static unsafe void TrySkipCutsceneDialog()
    {
        // TODO: becomes IInteractor.SkipCutscene() in Phase 3.
        // Only fire during actual cutscenes to avoid clicking unrelated SelectString prompts.
        // Pattern sourced from TextAdvance ExecConfirmCutsceneSkip + AutoCutsceneSkipper.
        if (!Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent] &&
            !Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene78])
            return;

        // SPIKE NOTE: TextAdvance uses "SelectString" (not "CutSceneSelectString") for the
        // skip confirmation dialog. It also checks the entry text for the skip string —
        // we guard via condition flags instead. Verify index 0 = "Skip" not "Watch again".
        var ptr = (nint)GameGui.GetAddonByName("SelectString");
        if (ptr == nint.Zero) return;
        var addon = (AtkUnitBase*)ptr;
        if (!addon->IsVisible) return;

        Log.Information("[Spike] SelectString visible during cutscene — firing skip (index 0).");
        // SPIKE FINDING: enum is AtkValueType in SDK 15, not ValueType.
        var value = stackalloc AtkValue[1];
        value[0].Type = AtkValueType.Int;
        value[0].Int = 0; // index 0 = first entry (Skip)
        addon->FireCallback(1, value, true); // updateState=true required for SelectString
    }

    private static unsafe void TryAcceptQuestWindow()
    {
        // TODO: becomes IInteractor.ConfirmQuestAccept() in Phase 3.
        // Button ID 44 = Accept button, sourced from TextAdvance ExecQuestAccept.cs.
        var ptr = (nint)GameGui.GetAddonByName("JournalAccept");
        if (ptr == nint.Zero) return;
        var addon = (AtkUnitBase*)ptr;
        if (!addon->IsVisible) return;

        var button = addon->GetComponentButtonById(44);
        if (button == null || !button->IsEnabled) return;

        var btnResNode = button->AtkComponentBase.OwnerNode->AtkResNode;
        var evt = btnResNode.AtkEventManager.Event;
        if (evt == null) return;

        Log.Information("[Spike] JournalAccept visible — clicking Accept (button 44).");
        addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt);
    }

    private static unsafe void TryCompleteQuestWindow()
    {
        // TODO: becomes IInteractor.ConfirmQuestComplete() in Phase 3.
        // Button ID 37 = Complete button, sourced from TextAdvance ExecQuestComplete.cs.
        var ptr = (nint)GameGui.GetAddonByName("JournalResult");
        if (ptr == nint.Zero) return;
        var addon = (AtkUnitBase*)ptr;
        if (!addon->IsVisible) return;

        var button = addon->GetComponentButtonById(37);
        if (button == null || !button->IsEnabled) return;

        // Pattern from ECommons ClickAddonButton (ClickHelper.cs lines 129-135):
        // Use the button's stored event from AtkEventManager — the EventType and Param
        // come from the event already attached to the button, not from a new AtkEvent.
        // This avoids both null-node crashes and wrong-event-type misses.
        var btnResNode = button->AtkComponentBase.OwnerNode->AtkResNode;
        var evt = btnResNode.AtkEventManager.Event; // AtkEvent* — no cast needed in SDK 15
        if (evt == null) return;

        // SPIKE NOTE: ECommons uses evt->State.EventType — field name changed in SDK 15.
        // Using AtkEventType.ButtonClick directly; verify this is correct for JournalResult.
        Log.Information("[Spike] JournalResult — clicking Complete (button 37, param={P}).", evt->Param);
        addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt);
    }

    private static unsafe void TryAdvanceTalkAddon()
    {
        // TODO: becomes IInteractor.AdvanceDialogue() in Phase 3.
        // SPIKE FINDING: CreateAtkEvent(132) in ECommons sets State.StateFlags=132,
        // NOT Node to node-id-132. 132 is an AtkEventStateFlags value. Listener and
        // Target must also be set. This unified approach works for ALL Talk variants —
        // no node lookup required.
        var ptr = (nint)GameGui.GetAddonByName("Talk");
        if (ptr == nint.Zero) return;
        var addon = (AtkUnitBase*)ptr;
        if (!addon->IsVisible) return;

        var evt  = stackalloc AtkEvent[1];
        evt[0].Listener = (AtkEventListener*)addon;
        evt[0].Target   = &AtkStage.Instance()->AtkEventTarget;
        evt[0].State    = new AtkEventState { StateFlags = (AtkEventStateFlags)132 };
        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(AtkEventType.MouseDown,  0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseUp,    0, evt, data);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static unsafe bool IsQuestComplete()
    {
        // QuestManager tracks completed quests in a separate bitfield — distinct from
        // the active journal. Returns true whether completed today or years ago.
        var questId = (ushort)(QuestData.QuestId & 0xFFFF);
        return QuestManager.IsQuestComplete(questId);
    }

    private static unsafe byte GetQuestSequence()
    {
        // QuestManager stores quests by their in-game ID, which is the lower 16 bits
        // of the Lumina row ID. For QuestId=66130: 66130 & 0xFFFF = 594.
        // SPIKE NOTE: verify this masking assumption holds — document in SPIKE_NOTES.md.
        var questId = (ushort)(QuestData.QuestId & 0xFFFF);
        var quest = QuestManager.Instance()->GetQuestById(questId);

        // null means quest not in journal — either never accepted or already completed.
        // Use IsQuestComplete() to distinguish the two cases.
        return quest == null ? (byte)0 : quest->Sequence;
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearestNpc(uint dataId)
    {
        // SPIKE NOTE: IClientState.LocalPlayer appears to be absent in SDK 15.
        // Using ObjectTable scan without distance sort for now — assumes the NPC
        // we want is uniquely identified by BaseId in the current zone.
        foreach (var obj in ObjectTable)
        {
            if (obj.BaseId == dataId) return obj;
        }
        return null;
    }

    private void TryStopNav()
    {
        try { _navStop.InvokeFunc(); } // vnavmesh.Path.Stop
        catch { /* nav may not be running — ignore */ }
    }

    private void TransitionTo(SpikeState next)
    {
        Log.Information("[Spike] {From} → {To}", _state, next);
        _state = next;
        _ticksInState = 0;
    }

    private void Fail(string reason)
    {
        Log.Error("[Spike] FAILED: {Reason}", reason);
        ChatGui.PrintError($"[QFSpike] Failed: {reason}");
        TryStopNav();
        _state = SpikeState.Failed;
    }
}