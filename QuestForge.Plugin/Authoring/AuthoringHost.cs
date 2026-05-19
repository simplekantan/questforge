using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Authoring;
using QuestForge.Adapters.State;
using QuestForge.Adapters;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;

namespace QuestForge.Plugin.Authoring;

/// <summary>Lumina-backed quest info for a single NPC target, cached by AuthoringHost.</summary>
public sealed record NpcQuestInfo(uint QuestId, string QuestName, bool IsAvailable, bool IsComplete);

/// <summary>
/// Plugin-side authoring coordinator. Subscribes to Dalamud events, maintains
/// a fresh GameStateSnapshot, and exposes the recording workflow to the UI panels.
/// </summary>
public sealed class AuthoringHost : IDisposable
{
    private readonly PluginServices _services;
    private readonly IPluginLog _log;
    private readonly DraftManager _draftManager;
    private readonly StepInferenceEngine _inferenceEngine = new();
    private readonly IQuestState _questState;
    private readonly TraceSession _traceSession;
    private SnapshotAggregator _aggregator;

    // Logical run-id label for events emitted during this authoring session.
    // Not a write gate — TraceSession owns that. Null when not in Author mode.
    private string? _authoringRunId;

    // NPC quest cache — refreshed only when target.BaseId changes
    private uint _lastQuestQueryNpcBaseId;
    private IReadOnlyList<NpcQuestInfo> _cachedNpcQuests = [];
    private PluginConfig _config;

    // Quest-state polling: track last known (seq, flags) per quest to detect changes
    private readonly Dictionary<ushort, (byte Seq, byte Flags)> _lastKnownQuestState = new();
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);

    // Attunement polling: track aetherytes we've already seen as attuned
    private readonly HashSet<uint> _attunedAetheryteIds = new();

    // Key item polling: track previous key item map (itemId → qty) for diff
    private Dictionary<uint, int> _previousKeyItemsMap = new();

    // (dedup is now owned by TraceSession)

    public AuthoringMode Mode { get; private set; } = AuthoringMode.Off;
    public QuestId? AuthoringTarget { get; private set; }

    public GameStateSnapshot CurrentSnapshot => _aggregator.Current;
    public DraftManager DraftManager => _draftManager;

    // Recent change tracking for QuestStatePanel highlight
    public (QuestId QuestId, string Description, DateTimeOffset When)? RecentChange { get; private set; }

    public IReadOnlyList<NpcQuestInfo> NpcQuests => _cachedNpcQuests;

    /// <summary>Force a cache refresh on the next heartbeat tick. Call when settings change.</summary>
    public void InvalidateNpcCache() => _lastQuestQueryNpcBaseId = 0;

    /// <summary>Diagnostic: dump the current target's ObjectKind, BaseId, and index state to a string.</summary>
    public string GetTargetDiagnostics()
    {
        var target = _services.TargetManager.Target;
        if (target is null) return "No target.";

        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"Target: {target.Name} | ObjectKind: {target.ObjectKind} | BaseId: {target.BaseId}");

        var passesFilter = target.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                        || target.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc;
        lines.AppendLine($"Passes ObjectKind filter (EventNpc|BattleNpc): {passesFilter}");

        var index = GetOrBuildNpcQuestIndex();
        lines.AppendLine($"Quest index size: {index.Count} NPCs");
        lines.AppendLine($"Index has entry for BaseId {target.BaseId}: {index.ContainsKey(target.BaseId)}");

        if (index.TryGetValue(target.BaseId, out var questRowIds))
            lines.AppendLine($"Quest RowIds in index for this NPC: [{string.Join(", ", questRowIds)}]");

        lines.AppendLine($"Cached NPC quests: {_cachedNpcQuests.Count}");
        lines.AppendLine($"Last queried BaseId: {_lastQuestQueryNpcBaseId}");
        return lines.ToString().TrimEnd();
    }

    public AuthoringHost(PluginServices services, FileDraftStorage storage, IPluginLog log, PluginConfig config, IQuestState questState, TraceSession traceSession)
    {
        _services = services;
        _log = log;
        _config = config;
        _questState = questState;
        _traceSession = traceSession;
        _draftManager = new DraftManager(storage, SystemClock.Instance, TimeSpan.FromSeconds(60));
        _aggregator = new SnapshotAggregator(null, SystemClock.Instance);
        _dialoguePoller = new QuestForge.Engine.Authoring.DialogueMenuPoller(_aggregator);

        _services.ClientState.TerritoryChanged += OnTerritoryChanged;
        _services.Framework.Update += OnFrameworkUpdate;
    }

    // --- Mode management ---

    // WHY: mode changes called from the chat-command thread (not Framework thread).
    // RunOnFrameworkThread ensures _aggregator, _lastKnownQuestState, and Mode are
    // only mutated from the same thread that reads them in OnFrameworkUpdate.
    public void EnterInspectMode() =>
        _services.Framework.RunOnFrameworkThread(EnterInspectModeCore);

    private void EnterInspectModeCore()
    {
        Mode = AuthoringMode.Inspect;
        _log.Info("QuestForge Authoring: entered Inspect mode");
    }

    public void EnterAuthorMode(QuestId target) =>
        _services.Framework.RunOnFrameworkThread(() => EnterAuthorModeCore(target));

    private void EnterAuthorModeCore(QuestId target)
    {
        Mode = AuthoringMode.Author;
        AuthoringTarget = target;
        _aggregator = new SnapshotAggregator(target, SystemClock.Instance);
        _dialoguePoller = new QuestForge.Engine.Authoring.DialogueMenuPoller(_aggregator);
        _lastKnownQuestState.Clear();
        // Preload the draft into cache so RecordStep calls are synchronous (cache hit)
        _ = _draftManager.GetOrCreate(target, CancellationToken.None);

        _authoringRunId = $"author-{target.Value}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        _traceSession.OnEnterAuthorMode(target.Value);
        _traceSession.Write(new RunStartEvent(_authoringRunId, target.Value, target.Value, DateTimeOffset.UtcNow));

        _log.Info($"QuestForge Authoring: entered Author mode for quest {target.Value}, runId: {_authoringRunId}");
    }

    public void ExitAuthoring() =>
        _services.Framework.RunOnFrameworkThread(ExitAuthoringCore);

    private void ExitAuthoringCore()
    {
        if (_authoringRunId is not null)
        {
            _traceSession.Write(new RunEndEvent(_authoringRunId, "authored", DateTimeOffset.UtcNow));
            _authoringRunId = null;
        }
        _traceSession.OnExitAuthoring();
        Mode = AuthoringMode.Off;
        AuthoringTarget = null;
        _log.Info("QuestForge Authoring: exited authoring mode");
    }

    // --- Record workflow ---

    /// <summary>Captures the current snapshot as the "before" for the next Record.</summary>
    public GameStateSnapshot OpenRecordModal()
    {
        if (AuthoringTarget is { } target)
            _traceSession.OnOpenRecordModal(target.Value);
        // Reset per-action delta signals so stale KeyItemsAdded/Removed from a previous
        // step don't bleed into the new inference window.
        _aggregator.ResetDeltas();
        return _aggregator.Current;
    }

    /// <summary>
    /// Captures "after" snapshot, calls inference, returns the suggestion for the modal to display.
    /// WHY the extra polls: the heartbeat runs every 250 ms; if the author clicks Record in the
    /// same frame a quest state change occurred, the aggregator may not reflect it yet. Forcing
    /// a fresh poll ensures inference always sees the current game state.
    /// </summary>
    public InferenceResult PreviewInference(GameStateSnapshot before)
    {
        // Flush the most time-sensitive pollers so accept/complete/sequence are always current.
        PollQuestState();
        PollTargetNpc();
        var after = _aggregator.Current;
        return _inferenceEngine.Infer(before, after);
    }

    /// <summary>
    /// Author confirmed the modal. Build a DraftStep from the (possibly user-edited) inference
    /// and append it to the active draft. Triggers a SaveNow.
    /// </summary>
    // WHY: RecordStep is called from ImGui.Draw (Framework thread). The draft is
    // preloaded into cache by EnterAuthorModeCore, so GetAwaiter().GetResult() is a
    // synchronous cache-hit with no I/O. Mutations (AddStep, MarkDirty) stay on the
    // Framework thread. SaveNow (file write) is fire-and-forget to avoid blocking the UI.
    public Task RecordStep(
        GameStateSnapshot before,
        InferenceResult inference,
        string finalStepId,
        string? finalExpect,
        string? notes,
        Step rawStep,
        CancellationToken ct)
    {
        if (AuthoringTarget is null || Mode != AuthoringMode.Author) return Task.CompletedTask;

        var draft = _draftManager.GetOrCreate(AuthoringTarget.Value, ct).GetAwaiter().GetResult();
        var draftStep = new DraftStep(
            StepId: finalStepId,
            StepType: inference.StepType,
            // Accept always belongs in sequence 0 (before the quest enters NormalQuests).
            // Steps that advance the sequence (e.g. talk-to-guild: 1→255) belong in the
            // BEFORE block, not the AFTER block. The beforeSeq > 0 guard handles the
            // timing window where the heartbeat hasn't run yet (before would be 0 stale).
            SequenceNumber: inference.StepType == "accept" ? 0
                : _aggregator.Current.QuestSequence > before.QuestSequence && before.QuestSequence > 0
                    ? before.QuestSequence
                    : _aggregator.Current.QuestSequence,
            InferredFrom: inference.InferredFrom,
            ObservedBefore: before,
            ObservedAfter: _aggregator.Current,
            SuggestedExpect: finalExpect,
            Notes: notes,
            Raw: rawStep);

        draft.AddStep(draftStep, DateTimeOffset.UtcNow);
        _draftManager.MarkDirty(AuthoringTarget.Value);
        _ = _draftManager.SaveNow(AuthoringTarget.Value, CancellationToken.None); // fire-and-forget file write

        if (_authoringRunId is not null)
        {
            var stepParams = JsonSerializer.SerializeToElement(new { stepId = finalStepId, stepType = inference.StepType }, _jsonOpts);
            _traceSession.Write(new ActionSubmittedEvent(_authoringRunId, inference.StepType, stepParams, DateTimeOffset.UtcNow));
            _traceSession.Write(new ActionCompletedEvent(_authoringRunId, inference.StepType, "recorded", DateTimeOffset.UtcNow));
        }
        _traceSession.OnConfirmRecordStep();

        // Consume per-step events so they don't bleed into the next recording window.
        _aggregator.OnAethernetTeleportConsumed();
        _aggregator.OnDialogueOptionConsumed();

        return Task.CompletedTask;
    }

    // --- Dalamud event handlers ---

    private void OnTerritoryChanged(uint territoryId)
    {
        var pos = GetPlayerPosition();
        _aggregator.OnZoneChanged(new ZoneId(territoryId), pos);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (Mode == AuthoringMode.Off) return;

        // WHY every frame: menus (TelepotTown, SelectIconString) can open and close within one
        // 250 ms heartbeat. Running every frame ensures we never miss open→close transitions.
        PollAethernetDestination();
        PollDialogueOption();

        var now = DateTimeOffset.UtcNow;
        if (now - _lastHeartbeatAt < HeartbeatInterval) return;
        _lastHeartbeatAt = now;

        PollPlayerPosition();
        PollQuestState();
        PollAttunement();
        PollKeyItems();
        PollTargetNpc();
    }

    private void PollPlayerPosition()
    {
        var local = _services.ObjectTable.LocalPlayer;
        if (local is null) return;

        var p = local.Position;
        _aggregator.OnPlayerMoved(new WorldPosition(p.X, p.Y, p.Z));
        WriteObservationDeduped("GetPlayerPosition", 0, new { x = p.X, y = p.Y, z = p.Z });
    }

    private unsafe void PollQuestState()
    {
        var qm = QuestManager.Instance();
        if (qm == null) return;

        var quests = qm->NormalQuests;

        // Track which quest IDs are present this tick
        var seenIds = new HashSet<ushort>();

        for (var i = 0; i < quests.Length; i++)
        {
            var id = quests[i].QuestId;
            if (id == 0) continue;
            seenIds.Add(id);

            var seq = quests[i].Sequence;
            var flags = quests[i].Flags;

            if (_lastKnownQuestState.TryGetValue(id, out var last))
            {
                if (seq != last.Seq)
                {
                    var publicId = ToPublicQuestId(id);
                    _aggregator.OnQuestSequenceChanged(publicId, seq);
                    RecentChange = (publicId, $"Sequence changed to {seq}", DateTimeOffset.UtcNow);
                    _lastKnownQuestState[id] = (seq, flags);
                }
                else if (flags != last.Flags)
                {
                    var publicId = ToPublicQuestId(id);
                    _aggregator.OnQuestFlagsChanged(publicId, flags);
                    RecentChange = (publicId, $"Flags changed to 0x{flags:X2}", DateTimeOffset.UtcNow);
                    _lastKnownQuestState[id] = (seq, flags);
                }
            }
            else
            {
                _lastKnownQuestState[id] = (seq, flags);
                var publicId = ToPublicQuestId(id);
                _aggregator.OnQuestAccepted(publicId);
                _aggregator.OnQuestSequenceChanged(publicId, seq);
                _aggregator.OnQuestFlagsChanged(publicId, flags);
                WriteObservationDeduped("IsQuestAccepted", publicId, true);
            }

            // Passive trace — dedup suppresses redundant JSONL writes
            {
                var publicId = ToPublicQuestId(id);
                WriteObservationDeduped("GetQuestSequence", publicId, (int)seq);
                WriteObservationDeduped("GetQuestFlags", publicId, (int)flags);
            }
        }

        // Detect quests that disappeared (turned in / abandoned)
        var removedIds = _lastKnownQuestState.Keys.Where(id => !seenIds.Contains(id)).ToList();
        foreach (var id in removedIds)
        {
            var publicId = ToPublicQuestId(id);
            _aggregator.OnQuestCompleted(publicId);
            RecentChange = (publicId, "Quest completed (left NormalQuests)", DateTimeOffset.UtcNow);
            WriteObservationDeduped("IsQuestComplete", publicId, true);
            _lastKnownQuestState.Remove(id);
            _log.Info($"QuestForge Authoring: quest {publicId.Value} removed from NormalQuests (completed or abandoned)");
        }
    }

    private unsafe void PollAttunement()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return;
        var aetheryteSheet = _services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
        if (aetheryteSheet == null) return;
        foreach (var row in aetheryteSheet)
        {
            if (row.RowId == 0) continue;
            if (_attunedAetheryteIds.Contains(row.RowId)) continue;
            if (uiState->IsAetheryteUnlocked(row.RowId))
            {
                _attunedAetheryteIds.Add(row.RowId);
                _aggregator.OnAttunementChanged(new QuestForge.Adapters.Types.AetheryteId(row.RowId));
                WriteObservationDeduped("IsAetheryteAttuned", row.RowId, 1);
            }
        }
    }

    private unsafe void PollKeyItems()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return;
        var container = mgr->GetInventoryContainer(InventoryType.KeyItems);
        if (container == null) return;

        var currentSlots = new List<(uint id, int qty)>(container->Size);
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            currentSlots.Add((slot->ItemId, (int)slot->Quantity));
        }

        var result = KeyItemPollDiff.Diff(_previousKeyItemsMap, currentSlots);
        _previousKeyItemsMap = result.NewMap;

        if (result.Changed)
        {
            _aggregator.OnKeyItemsSnapshot(result.NewMap, result.NewHash);

            if (result.AddedIds.Count > 0)
                _aggregator.OnKeyItemsChanged(result.AddedIds);
            if (result.RemovedIds.Count > 0)
                _aggregator.OnKeyItemsRemoved(result.RemovedIds);

            if (_authoringRunId is not null)
            {
                _traceSession.Write(new InventoryChangedEvent(
                    RunId:   _authoringRunId,
                    Gained:  result.Gained,
                    Lost:    result.Lost,
                    NewHash: result.NewHash,
                    At:      DateTimeOffset.UtcNow));
            }
        }
        else
        {
            _aggregator.OnInventoryHashChanged(result.NewHash);
        }
    }

    private void PollTargetNpc()
    {
        var target = _services.TargetManager.Target;
        var kind = target?.ObjectKind;
        var isInteractable = kind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                                  or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                                  or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte;

        if (target is null || !isInteractable)
        {
            // No valid target — clear the quest cache
            if (_lastQuestQueryNpcBaseId != 0)
            {
                _cachedNpcQuests = [];
                _lastQuestQueryNpcBaseId = 0;
            }
            return;
        }

        var npcId = new NpcId(target.BaseId);
        var p = target.Position;
        var npcPos = new WorldPosition(p.X, p.Y, p.Z);
        // WHY: OnInteraction fires for aetherytes too, keeping LastNpcInteracted == shard.BaseId.
        // This is the staleness-guard invariant for StepInferenceEngine Rule 4: aethernet is
        // detected only when LastNpcInteracted.Value == LastAethernetShardInteracted.Value —
        // meaning the most recent interaction was the shard, not a stale value from earlier.
        _aggregator.OnInteraction(npcId, npcPos);
        WriteObservationDeduped("GetTarget", 0, target.BaseId);

        // For aetheryte/aethernet shard targets: record the shard for aethernet hop inference
        if (kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte)
        {
            _aggregator.OnAethernetShardTargeted(new QuestForge.Adapters.Types.AetheryteId(target.BaseId));
            WriteObservationDeduped("AethernetShardTargeted", target.BaseId, 0);
        }

        // Only update NPC quest cache for EventNpc/BattleNpc — aetherytes don't have quests
        if (kind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                 or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
            UpdateNpcQuestCache(target.BaseId);
    }

    private unsafe void PollAethernetDestination()
    {
        var ptr = _services.GameGui.GetAddonByName("TelepotTown");
        bool menuIsOpen = !ptr.IsNull && ptr.IsReady;

        if (!menuIsOpen)
        {
            if (_aethernetMenuWasOpen && _pendingAethernetTo.HasValue)
                // Menu just closed after a selection — fire the teleport-completed event.
                _aggregator.OnAethernetTeleportCompleted(_pendingAethernetFrom, _pendingAethernetTo.Value);
            _aethernetMenuWasOpen = false;
            _pendingAethernetFrom = null;
            _pendingAethernetTo   = null;
            return;
        }

        if (!_aethernetMenuWasOpen)
        {
            // Menu just opened — capture departure shard.
            // WHY read TargetManager directly: PollTargetNpc is throttled to 250 ms so the
            // aggregator state may lag. Reading the live target bypasses that lag and captures
            // the shard the player just interacted with to open the menu.
            _aethernetMenuWasOpen = true;
            var liveTarget = _services.TargetManager.Target;
            if (liveTarget?.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte)
            {
                _pendingAethernetFrom = new QuestForge.Adapters.Types.AetheryteId(liveTarget.BaseId);
            }
            else
            {
                // Fall back to aggregator if the target was cleared when the menu opened.
                var cur = _aggregator.Current;
                _pendingAethernetFrom =
                    cur.LastAethernetShardInteracted.HasValue
                    && cur.LastNpcInteracted.HasValue
                    && cur.LastNpcInteracted.Value.Value == cur.LastAethernetShardInteracted.Value.Value
                        ? cur.LastAethernetShardInteracted
                        : null;
            }
        }

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;

        // List (AtkComponentTreeList*) at offset 0x238 in AddonTeleportTown.
        // SelectedItemIndex at offset 0x134 within AtkComponentTreeList.
        var listPtr = *(nint*)(ptr.Address + 0x238);
        if (listPtr == 0) return;

        var selectedIdx = *(int*)(listPtr + 0x134);
        if (selectedIdx < 0) return;

        // Destination names start at AtkValues[262].
        const int NamesBase = 262;
        if (addon->AtkValuesCount <= NamesBase + selectedIdx) return;

        var nameVal = addon->AtkValues[NamesBase + selectedIdx];
        if (nameVal.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String
            || nameVal.String.Value == null) return;

        var destName = Marshal.PtrToStringUTF8((nint)nameVal.String.Value);
        if (string.IsNullOrEmpty(destName)) return;

        if (!GetAethernetNameMap().TryGetValue(destName, out var rowId))
        {
            _services.Log.Warning($"[TelepotTown] No Aetheryte sheet match for '{destName}' (idx={selectedIdx})");
            return;
        }

        _pendingAethernetTo = new AethernetId(rowId);
    }

    // TelepotTown tracking: latch departure + destination when menu opens/closes.
    private bool _aethernetMenuWasOpen;
    private QuestForge.Adapters.Types.AetheryteId? _pendingAethernetFrom;
    private QuestForge.Adapters.Types.AethernetId? _pendingAethernetTo;

    // SelectIconString/SelectString tracking: captures which list option the author chose.
    // Re-created alongside _aggregator so it always targets the active aggregator instance.
    private QuestForge.Engine.Authoring.DialogueMenuPoller _dialoguePoller;

    private unsafe void PollDialogueOption()
    {
        // Try SelectIconString first (destination pickers, lift attendants), then SelectString.
        var ptr = _services.GameGui.GetAddonByName("SelectIconString");
        if (ptr.IsNull || !ptr.IsReady)
            ptr = _services.GameGui.GetAddonByName("SelectString");

        bool menuIsOpen = !ptr.IsNull && ptr.IsReady;
        int? selectedIdx = null;

        if (menuIsOpen)
        {
            // List component pointer at addon offset 0x238; SelectedItemIndex at list offset 0x134.
            // NOTE: 0x238 is confirmed for AddonTeleportTown; SelectIconString/SelectString may differ.
            // If selection is never captured in-game, inspect the SelectIconString struct with /xldata
            // to find the correct list-component offset and update here.
            var listPtr = *(nint*)(ptr.Address + 0x238);
            if (listPtr != 0)
            {
                var raw = *(int*)(listPtr + 0x134);
                if (raw >= 0)
                    selectedIdx = raw;
            }
        }

        _dialoguePoller.Tick(menuIsOpen, selectedIdx);
    }

    // Built once on first TelepotTown open; maps AethernetName display string → Aetheryte sheet RowId.
    private Dictionary<string, uint>? _aethernetNameToId;

    private Dictionary<string, uint> GetAethernetNameMap()
    {
        if (_aethernetNameToId != null) return _aethernetNameToId;
        _aethernetNameToId = new Dictionary<string, uint>(StringComparer.Ordinal);
        var sheet = _services.DataManager.GetExcelSheet<Aetheryte>();
        if (sheet == null) return _aethernetNameToId;
        foreach (var row in sheet)
        {
            if (row.AethernetGroup == 0 || row.IsAetheryte) continue;
            var name = row.AethernetName.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrEmpty(name))
                _aethernetNameToId.TryAdd(name, row.RowId);
        }
        return _aethernetNameToId;
    }

    // WHY: Lumina Quest sheet has 5000+ rows. A linear scan per NPC retarget would
    // run on the Framework thread and could stutter. Build the index once and look up in O(1).
    private Dictionary<uint, List<uint>>? _npcQuestIndex; // npcBaseId -> list of quest RowIds

    private Dictionary<uint, List<uint>> GetOrBuildNpcQuestIndex()
    {
        if (_npcQuestIndex != null) return _npcQuestIndex;
        var questSheet = _services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        var index = new Dictionary<uint, List<uint>>();
        if (questSheet != null)
        {
            foreach (var quest in questSheet)
            {
                var issuerId = quest.IssuerStart.RowId;
                if (issuerId == 0 || string.IsNullOrEmpty(quest.Name.ToString())) continue;
                if (!index.TryGetValue(issuerId, out var list))
                    index[issuerId] = list = new List<uint>();
                list.Add(quest.RowId);
            }
        }
        _npcQuestIndex = index;
        return _npcQuestIndex;
    }

    private void UpdateNpcQuestCache(uint npcBaseId)
    {
        if (npcBaseId == _lastQuestQueryNpcBaseId) return;
        _lastQuestQueryNpcBaseId = npcBaseId;

        var index = GetOrBuildNpcQuestIndex();
        if (!index.TryGetValue(npcBaseId, out var questRowIds)) { _cachedNpcQuests = []; return; }

        var questSheet = _services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        if (questSheet == null) { _cachedNpcQuests = []; return; }

        var results = new List<NpcQuestInfo>();
        var ct = CancellationToken.None;

        foreach (var rowId in questRowIds)
        {
            var quest = questSheet.GetRow(rowId);
            var name = quest.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            var publicId = new QuestId((uint)(quest.RowId | 0x10000u));

            var availableResult = _questState.IsQuestAvailable(publicId, ct).GetAwaiter().GetResult();
            var isAvailable = availableResult is Result<bool>.Success { Value: true };

            var completeResult = _questState.IsQuestComplete(publicId, ct).GetAwaiter().GetResult();
            var isComplete = completeResult is Result<bool>.Success { Value: true };

            // Skip completed quests unless the setting is enabled
            if (isComplete && !_config.ShowCompletedQuestsInAuthorPanel) continue;

            results.Add(new NpcQuestInfo(publicId.Value, name, isAvailable, isComplete));
            if (results.Count >= 10) break;
        }

        _cachedNpcQuests = results;
    }

    private WorldPosition GetPlayerPosition()
    {
        var local = _services.ObjectTable.LocalPlayer;
        if (local is null) return new WorldPosition(0, 0, 0);
        var p = local.Position;
        return new WorldPosition(p.X, p.Y, p.Z);
    }

    private static QuestId ToPublicQuestId(ushort id) => new((uint)id | 0x10000u);

    private static readonly JsonSerializerOptions _jsonOpts = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void WriteObservationDeduped(string method, object argument, object value)
    {
        if (_authoringRunId is null) return;
        try
        {
            var argEl = JsonSerializer.SerializeToElement(argument, _jsonOpts);
            var valEl = JsonSerializer.SerializeToElement(value, _jsonOpts);
            _traceSession.WriteObservation(method, argEl, valEl, _authoringRunId, DateTimeOffset.UtcNow);
        }
        catch { /* trace write failure must not affect authoring */ }
    }

    public void Dispose()
    {
        _services.ClientState.TerritoryChanged -= OnTerritoryChanged;
        _services.Framework.Update -= OnFrameworkUpdate;
        if (_authoringRunId is not null)
            _traceSession.Write(new RunEndEvent(_authoringRunId, "disposed", DateTimeOffset.UtcNow));
        // TraceSession lifecycle (OnExitAuthoring / Dispose) is managed by Plugin.cs.
    }
}
