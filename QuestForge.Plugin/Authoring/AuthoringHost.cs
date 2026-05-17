using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Authoring;
using QuestForge.Adapters.State;
using QuestForge.Adapters;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Engine.Tracing;
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
    private readonly string _tracesDir;
    private SnapshotAggregator _aggregator;

    // Authoring trace — written alongside the draft for qf-trace compatibility
    private ITraceWriter _authoringTrace = NullTraceWriter.Instance;
    private string? _authoringRunId;

    // NPC quest cache — refreshed only when target.BaseId changes
    private uint _lastQuestQueryNpcBaseId;
    private IReadOnlyList<NpcQuestInfo> _cachedNpcQuests = [];
    private PluginConfig _config;

    // Quest-state polling: track last known (seq, flags) per quest to detect changes
    private readonly Dictionary<ushort, (byte Seq, byte Flags)> _lastKnownQuestState = new();
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);

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

    public AuthoringHost(PluginServices services, FileDraftStorage storage, IPluginLog log, PluginConfig config, IQuestState questState, string tracesDir)
    {
        _services = services;
        _log = log;
        _config = config;
        _questState = questState;
        _tracesDir = tracesDir;
        _draftManager = new DraftManager(storage, SystemClock.Instance, TimeSpan.FromSeconds(60));
        _aggregator = new SnapshotAggregator(null, SystemClock.Instance);

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
        _lastKnownQuestState.Clear();
        // Preload the draft into cache so RecordStep calls are synchronous (cache hit)
        _ = _draftManager.GetOrCreate(target, CancellationToken.None);

        // Start an authoring trace alongside the draft so qf-trace can process this session
        Directory.CreateDirectory(_tracesDir);
        _authoringRunId = $"author-{target.Value}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        _authoringTrace = TraceWriter.OpenFile(Path.Combine(_tracesDir, $"{_authoringRunId}.jsonl"));
        _authoringTrace.Write(new RunStartEvent(_authoringRunId, target.Value, target.Value, DateTimeOffset.UtcNow));

        _log.Info($"QuestForge Authoring: entered Author mode for quest {target.Value}, trace: {_authoringRunId}");
    }

    public void ExitAuthoring() =>
        _services.Framework.RunOnFrameworkThread(ExitAuthoringCore);

    private void ExitAuthoringCore()
    {
        if (_authoringRunId is not null)
        {
            _authoringTrace.Write(new RunEndEvent(_authoringRunId, "authored", DateTimeOffset.UtcNow));
            (_authoringTrace as IDisposable)?.Dispose();
            _authoringTrace = NullTraceWriter.Instance;
            _authoringRunId = null;
        }
        Mode = AuthoringMode.Off;
        AuthoringTarget = null;
        _log.Info("QuestForge Authoring: exited authoring mode");
    }

    // --- Record workflow ---

    /// <summary>Captures the current snapshot as the "before" for the next Record.</summary>
    public GameStateSnapshot OpenRecordModal() => _aggregator.Current;

    /// <summary>
    /// Captures "after" snapshot, calls inference, returns the suggestion for the modal to display.
    /// </summary>
    public InferenceResult PreviewInference(GameStateSnapshot before)
    {
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
            SequenceNumber: _aggregator.Current.QuestSequence,
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
            _authoringTrace.Write(new ActionSubmittedEvent(_authoringRunId, inference.StepType, stepParams, DateTimeOffset.UtcNow));
            _authoringTrace.Write(new ActionCompletedEvent(_authoringRunId, inference.StepType, "recorded", DateTimeOffset.UtcNow));
        }

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

        var now = DateTimeOffset.UtcNow;
        if (now - _lastHeartbeatAt < HeartbeatInterval) return;
        _lastHeartbeatAt = now;

        PollPlayerPosition();
        PollQuestState();
        PollTargetNpc();
    }

    private void PollPlayerPosition()
    {
        var local = _services.ObjectTable.LocalPlayer;
        if (local is null) return;

        var p = local.Position;
        _aggregator.OnPlayerMoved(new WorldPosition(p.X, p.Y, p.Z));
    }

    private unsafe void PollQuestState()
    {
        var qm = QuestManager.Instance();
        if (qm == null) return;

        var quests = qm->NormalQuests;
        for (var i = 0; i < quests.Length; i++)
        {
            var id = quests[i].QuestId;
            if (id == 0) continue;

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
                    WriteObservation("GetQuestSequence", publicId.Value, (int)seq);
                }
                else if (flags != last.Flags)
                {
                    var publicId = ToPublicQuestId(id);
                    _aggregator.OnQuestFlagsChanged(publicId, flags);
                    RecentChange = (publicId, $"Flags changed to 0x{flags:X2}", DateTimeOffset.UtcNow);
                    _lastKnownQuestState[id] = (seq, flags);
                    WriteObservation("GetQuestFlags", publicId.Value, (int)flags);
                }
            }
            else
            {
                _lastKnownQuestState[id] = (seq, flags);
                var publicId = ToPublicQuestId(id);
                _aggregator.OnQuestSequenceChanged(publicId, seq);
                _aggregator.OnQuestFlagsChanged(publicId, flags);
            }
        }
    }

    private void PollTargetNpc()
    {
        var target = _services.TargetManager.Target;
        if (target is null || (target.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc &&
                               target.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc))
        {
            // No valid NPC target — clear the quest cache
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
        _aggregator.OnInteraction(npcId, npcPos);

        UpdateNpcQuestCache(target.BaseId);
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

    private static readonly JsonSerializerOptions _jsonOpts = new() { IncludeFields = true };

    private void WriteObservation(string method, uint questId, int value)
    {
        if (_authoringRunId is null) return;
        try
        {
            var arg = JsonSerializer.SerializeToElement(questId, _jsonOpts);
            var val = JsonSerializer.SerializeToElement(value, _jsonOpts);
            _authoringTrace.Write(new ObservationEvent(_authoringRunId, method, arg, val, DateTimeOffset.UtcNow));
        }
        catch { /* trace write failure must not affect authoring */ }
    }

    public void Dispose()
    {
        _services.ClientState.TerritoryChanged -= OnTerritoryChanged;
        _services.Framework.Update -= OnFrameworkUpdate;
        if (_authoringRunId is not null)
        {
            _authoringTrace.Write(new RunEndEvent(_authoringRunId, "disposed", DateTimeOffset.UtcNow));
            (_authoringTrace as IDisposable)?.Dispose();
        }
    }
}
