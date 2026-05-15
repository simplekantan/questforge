using Microsoft.Extensions.Logging;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Combat;
using QuestForge.Adapters.Dalamud.Gear;
using QuestForge.Adapters.Dalamud.Interaction;
using QuestForge.Adapters.Dalamud.Minigames;
using QuestForge.Adapters.Dalamud.Movement;
using QuestForge.Adapters.Dalamud.Timing;
using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Interaction;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Timing;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine;
using QuestForge.Engine.Tracing;
using QuestForge.Plugin.Logging;
using QuestForge.Schema;

namespace QuestForge.Plugin;

public sealed class EngineHost : IDisposable
{
    private readonly PluginServices _services;

    private readonly DalamudGameStateProvider _gameStateInner;
    private readonly DalamudQuestState _questStateInner;
    private readonly VnavmeshNavigator _navigator;
    private readonly LifestreamTeleporter _teleporter;
    private readonly DalamudInteractor _interactor;
    private readonly WrathComboAdapter _combat;
    private readonly DalamudGearManager _gear;
    private readonly NullMinigameSkipper _minigames;
    private readonly LuminaDialogueResolver _dialogue;
    private readonly SeededTimingProfile _timing;

    private TraceWriter? _trace;
    private QuestEngine? _engine;
    private string? _runId;
    private QuestId _currentQuestId;

    public EngineHost(PluginServices services)
    {
        _services = services;
        _gameStateInner = new DalamudGameStateProvider(services);
        _questStateInner = new DalamudQuestState(services);
        _navigator       = new VnavmeshNavigator(services);
        _teleporter      = new LifestreamTeleporter(services);
        _interactor      = new DalamudInteractor(services);
        _combat          = new WrathComboAdapter(services);
        _gear            = new DalamudGearManager(services);
        _minigames       = new NullMinigameSkipper();
        _dialogue        = new LuminaDialogueResolver(services);
        _timing          = new SeededTimingProfile(seed: 0);
    }

    public bool IsRunActive => _engine is not null;
    public string? ActiveRunId => _runId;

    // Called by /qf stop — safe to call mid-tick because all Phase 6 adapters complete
    // synchronously (Task.FromResult), so DispatchAction never parks across frames.
    public void StopRun() => EndRun();

    public string GetGameStateSummary()
    {
        var ct = CancellationToken.None;
        var zone = _gameStateInner.GetPlayerZone(ct).GetAwaiter().GetResult().ValueOrDefault;
        var pos = _gameStateInner.GetPlayerPosition(ct).GetAwaiter().GetResult().ValueOrDefault;
        var job = _gameStateInner.GetCurrentJob(ct).GetAwaiter().GetResult().ValueOrDefault;
        var level = _gameStateInner.GetJobLevel(default, ct).GetAwaiter().GetResult().ValueOrDefault;
        var combat = _gameStateInner.IsPlayerInCombat(ct).GetAwaiter().GetResult().ValueOrDefault;
        var kind = _gameStateInner.GetCurrentInstanceKind(ct).GetAwaiter().GetResult().ValueOrDefault;
        return $"zone={zone?.Value} pos=({pos?.X:F1},{pos?.Y:F1},{pos?.Z:F1}) " +
               $"job={job?.Value} lv={level} combat={combat} instance={kind}";
    }

    public string GetQuestStateSummary(uint questRowId)
    {
        var ct = CancellationToken.None;
        var qid = new QuestId(questRowId);
        var seq = _questStateInner.GetQuestSequence(qid, ct).GetAwaiter().GetResult().ValueOrDefault;
        var complete = _questStateInner.IsQuestComplete(qid, ct).GetAwaiter().GetResult().ValueOrDefault;
        var accepted = _questStateInner.IsQuestAccepted(qid, ct).GetAwaiter().GetResult().ValueOrDefault;
        return $"quest={questRowId} seq={seq} complete={complete} accepted={accepted}";
    }

    public void BeginRun(QuestDefinition quest, string runId)
    {
        EndRun(); // clean up any previous run

        _runId          = runId;
        _currentQuestId = new QuestId(quest.Id);
        _trace          = TraceWriter.OpenFile(BuildTracePath(runId));
        _timing.Reseed(StableHash(runId));

        IGameStateProvider gs = new RecordingGameStateProvider(
            _gameStateInner, _trace, () => _runId, skipIfNoRunId: true);
        IQuestState qs = new RecordingQuestState(
            _questStateInner, _trace, () => _runId, skipIfNoRunId: true);

        _engine = new QuestEngine(
            gs, qs, _navigator, _teleporter, _interactor,
            _combat, _gear, _minigames, _dialogue, _timing,
            _trace, new DalamudLogger<QuestEngine>(_services.Log));
        _engine.StartQuest(quest);
        _engine.BeginRun(runId);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (_engine is null) return;

        EngineAction action;
        try { action = await _engine.Tick(ct); }
        catch (OperationCanceledException) { return; }

        await DispatchAction(action, ct);
    }

    private async Task DispatchAction(EngineAction action, CancellationToken ct)
    {
        switch (action)
        {
            case EngineAction.Navigate n:
                await _navigator.NavigateTo(n.Destination, n.Options, ct);
                break;

            case EngineAction.Interact i:
                await _interactor.InteractWith(i.Target, ct);
                // Advance any open dialogue and attempt journal buttons — returns Fail
                // immediately if the respective addon is not visible.
                await _interactor.AdvanceDialogue(ct);
                await _interactor.AcceptQuest(_currentQuestId, ct);
                await _interactor.CompleteQuest(_currentQuestId, ct);
                break;

            case EngineAction.Wait:
                // Engine is satisfied with step state but waiting for the game to advance
                // sequence (e.g. Talk addon still open after interact). Keep clicking through.
                await _interactor.AdvanceDialogue(ct);
                await _interactor.AcceptQuest(_currentQuestId, ct);
                await _interactor.CompleteQuest(_currentQuestId, ct);
                break;

            case EngineAction.AwaitUser au:
                _services.Log.Warning($"QuestForge run {_runId} paused: {au.Reason}");
                EndRun();
                break;

            case EngineAction.Done:
                _services.Log.Info($"QuestForge run {_runId} complete");
                EndRun();
                break;
        }
    }

    private void EndRun()
    {
        _engine = null;
        _trace?.Dispose();
        _trace  = null;
        _runId  = null;
    }

    public void Dispose() => EndRun();

    // Deterministic seed from runId — same runId → same timing sequence (Phase 7 replay)
    private static int StableHash(string s)
    {
        var hash = 17;
        foreach (var c in s) hash = hash * 31 + c;
        return hash;
    }

    private string BuildTracePath(string runId)
    {
        var dir = Path.Combine(_services.PluginInterface.GetPluginConfigDirectory(), "traces");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{runId}.jsonl");
    }
}
