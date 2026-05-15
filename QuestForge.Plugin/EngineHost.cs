using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Dalamud;
using QuestForge.Adapters.Dalamud.Combat;
using QuestForge.Adapters.Dalamud.State;
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

    // Saved cutscene skip settings — null means not saved (no run active or settings not changed)
    private uint? _savedCutsceneSkipContents;
    private uint? _savedCutsceneSkipShip;

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
        return $"zone={zone.Value} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) " +
               $"job={job.Value} lv={level} combat={combat} instance={kind}";
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
        EndRun(); // clean up any previous run (also restores cutscene settings)

        EnableCutsceneSkip();
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
        catch (Exception ex)
        {
            _services.Log.Error(ex, "QuestForge: engine tick threw");
            _services.ChatGui.PrintError($"QuestForge: tick error — {ex.Message}");
            return;
        }

        try { await DispatchAction(action, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _services.Log.Error(ex, $"QuestForge: dispatch error for {action.GetType().Name}");
            _services.ChatGui.PrintError($"QuestForge: dispatch error — {ex.Message}");
        }
    }

    private async Task DispatchAction(EngineAction action, CancellationToken ct)
    {
        switch (action)
        {
            case EngineAction.Navigate n:
                _services.Log.Debug($"[Navigate] → ({n.Destination.X:F1},{n.Destination.Y:F1},{n.Destination.Z:F1}) stop={n.Options.StoppingDistance}");
                await _navigator.NavigateTo(n.Destination, n.Options, ct);
                break;

            case EngineAction.Interact i:
                _services.Log.Debug($"[Interact] npc={i.Target.Value}");
                TryCutsceneSkipConfirm();
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
                TryCutsceneSkipConfirm();
                await _interactor.AdvanceDialogue(ct);
                await _interactor.AcceptQuest(_currentQuestId, ct);
                await _interactor.CompleteQuest(_currentQuestId, ct);
                break;

            case EngineAction.AwaitUser au:
                _services.Log.Warning($"QuestForge run {_runId} paused: {au.Reason}");
                _services.ChatGui.PrintError($"QuestForge: run paused — {au.Reason}");
                EndRun();
                break;

            case EngineAction.Done:
                _services.Log.Info($"QuestForge run {_runId} complete");
                _services.ChatGui.Print("QuestForge: quest complete!");
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
        RestoreCutsceneSkip();
    }

    public void Dispose() => EndRun();

    // When a skippable cutscene is active, AutoCutsceneSkipper presses Escape which opens
    // a SelectString confirmation dialog. Click the first entry (Yes/skip) to confirm.
    // OccupiedInCutSceneEvent is the reliable flag for skippable cutscenes; WatchingCutscene78
    // marks non-skippable ones that must be waited out — do not attempt to click those.
    private unsafe void TryCutsceneSkipConfirm()
    {
        if (!_services.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return;

        var addonPtr = _services.GameGui.GetAddonByName("SelectString");
        if (addonPtr.IsNull || !addonPtr.IsVisible) return;

        ((AtkUnitBase*)addonPtr.Address)->FireCallbackInt(0);
    }

    private void EnableCutsceneSkip()
    {
        // Save current settings and set to maximum (skip all). Uses props.Maximum so we don't
        // hardcode a value — the game defines what "skip all" means per option.
        if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsContents, out uint cur1))
        {
            _savedCutsceneSkipContents = cur1;
            if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsContents, out UIntConfigProperties? props) && props is not null)
                _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsContents, props.Maximum);
        }
        if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsShip, out uint cur2))
        {
            _savedCutsceneSkipShip = cur2;
            if (_services.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsShip, out UIntConfigProperties? props) && props is not null)
                _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsShip, props.Maximum);
        }
    }

    private void RestoreCutsceneSkip()
    {
        if (_savedCutsceneSkipContents is { } v1)
        {
            _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsContents, v1);
            _savedCutsceneSkipContents = null;
        }
        if (_savedCutsceneSkipShip is { } v2)
        {
            _services.GameConfig.Set(UiConfigOption.CutsceneSkipIsShip, v2);
            _savedCutsceneSkipShip = null;
        }
    }

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
