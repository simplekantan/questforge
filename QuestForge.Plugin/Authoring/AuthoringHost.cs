using System.Text.Json;
using Dalamud.Plugin.Services;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Authoring;
using QuestForge.Adapters.State;
using QuestForge.Adapters;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Tracing;
using QuestForge.Schema;

namespace QuestForge.Plugin.Authoring;

/// <summary>Lumina-backed quest info for a single NPC target, cached by AuthoringHost.</summary>
public sealed record NpcQuestInfo(uint QuestId, string QuestName, bool IsAvailable, bool IsComplete);

/// <summary>
/// Plugin-side authoring coordinator. Delegates all polling to UIObserver, maintains
/// a fresh GameStateSnapshot via SnapshotAggregator, and exposes the recording workflow
/// to the UI panels.
/// </summary>
public sealed class AuthoringHost : IDisposable
{
    private readonly PluginServices _services;
    private readonly IPluginLog _log;
    private readonly DraftManager _draftManager;
    private readonly StepInferenceEngine _inferenceEngine = new();
    private readonly IQuestState _questState;
    private readonly TraceSession _traceSession;
    private readonly UIObserver _uiObserver;
    private SnapshotAggregator _aggregator;

    // Logical run-id label for events emitted during this authoring session.
    // Not a write gate — TraceSession owns that. Null when not in Author mode.
    private string? _authoringRunId;

    // NPC quest cache — refreshed only when target.BaseId changes
    private uint _lastQuestQueryNpcBaseId;
    private IReadOnlyList<NpcQuestInfo> _cachedNpcQuests = [];
    private PluginConfig _config;

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

        _uiObserver = new UIObserver(
            new DalamudFrameworkDispatch(services.Framework),
            traceSession,
            passiveRunId: $"passive-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            new DalamudAddonProbe(services.GameGui, services.DataManager),
            new DalamudGameProbe(services.DataManager, services.ObjectTable, services.ClientState),
            targetProbe: new DalamudTargetProbe(services.TargetManager, services.ClientState));

        _services.ClientState.TerritoryChanged += OnTerritoryChanged;
        _services.Framework.Update += OnFrameworkUpdate;
    }

    // --- Mode management ---

    // WHY: mode changes called from the chat-command thread (not Framework thread).
    // RunOnFrameworkThread ensures _aggregator and Mode are only mutated from the
    // same thread that reads them in OnFrameworkUpdate.
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
        // Preload the draft into cache so RecordStep calls are synchronous (cache hit)
        _ = _draftManager.GetOrCreate(target, CancellationToken.None);

        _authoringRunId = $"author-{target.Value}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        _traceSession.OnEnterAuthorMode(target.Value);
        _traceSession.Write(new RunStartEvent(_authoringRunId, target.Value, target.Value, DateTimeOffset.UtcNow));

        _uiObserver.ResetHeartbeatState();
        _uiObserver.SetAggregator(_aggregator, _authoringRunId);

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
        _uiObserver.SetAggregator(null, null);
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
        // Reset all per-window signals. ResetWindowState covers ResetDeltas, consume calls,
        // and in-progress menu tracking so the first open of each menu in this window
        // is treated as a fresh transition.
        _aggregator.ResetDeltas();
        _uiObserver.ResetWindowState();
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
        var after = _aggregator.Current;
        _services.Log.Debug($"[QF-DIAG] PreviewInference: zone {before.Zone.Value}→{after.Zone.Value} AethernetTeleportCompleted={after.AethernetTeleportCompleted?.To.Value} DialogueOptionSelected={after.DialogueOptionSelected} DialogueNpcSource={after.DialogueNpcSource?.NpcId} isAethernet_before_shard={before.LastAethernetShardInteracted?.Value} isAethernet_before_npc={before.LastNpcInteracted?.Value}");
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
            // Accept steps use before.QuestSequence: the main quest accept happens at seq 0
            // (before.QuestSequence = 0), and mandatory sub-quest accepts happen at whatever
            // sequence the parent quest is at when the sub-quest is offered (e.g. seq 1).
            // Hardcoding 0 for all accepts was wrong for foreign-quest accepts mid-sequence.
            // Steps that advance the sequence (e.g. talk-to-guild: 1→255) belong in the
            // BEFORE block, not the AFTER block. The beforeSeq > 0 guard handles the
            // timing window where the heartbeat hasn't run yet (before would be 0 stale).
            SequenceNumber: inference.StepType == "accept" ? before.QuestSequence
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

            // Serialize the step to JSON using the quest file options and emit step.recorded
            // so qf-trace extract-quest can reconstruct quest definitions from the trace.
            var stepJson = JsonSerializer.SerializeToElement(
                rawStep,
                QuestForge.Schema.QuestForgeJsonContext.QuestFileOptions);
            _traceSession.Write(new StepRecordedEvent(
                RunId:          _authoringRunId,
                StepId:         finalStepId,
                SequenceNumber: draftStep.SequenceNumber,
                Step:           stepJson,
                At:             DateTimeOffset.UtcNow));
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

    private void OnFrameworkUpdate(IFramework _)
    {
        // UIObserver owns all polling. AuthoringHost only updates the NPC quest
        // cache used by the authoring UI panel, triggered by target changes.
        if (Mode == AuthoringMode.Off) return;
        var target = _services.TargetManager.Target;
        if (target is not null && target.BaseId != _lastQuestQueryNpcBaseId
            && target.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                                 or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
        {
            UpdateNpcQuestCache(target.BaseId);
        }
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

    private static readonly JsonSerializerOptions _jsonOpts = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Dispose()
    {
        _services.ClientState.TerritoryChanged -= OnTerritoryChanged;
        _services.Framework.Update -= OnFrameworkUpdate;
        _uiObserver.Dispose();
        if (_authoringRunId is not null)
            _traceSession.Write(new RunEndEvent(_authoringRunId, "disposed", DateTimeOffset.UtcNow));
        // TraceSession lifecycle (OnExitAuthoring / Dispose) is managed by Plugin.cs.
    }
}
