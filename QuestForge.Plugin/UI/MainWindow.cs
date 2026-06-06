using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Plugin.UI;

public sealed class MainWindow : Window
{
    private readonly EngineHost _host;
    private readonly IQuestScheduler _scheduler;
    private readonly PluginConfig _config;
    private ConfigWindow? _configWindow;
    private PlaylistWindow? _playlistWindow;

    public MainWindow(EngineHost host, IQuestScheduler scheduler, PluginConfig config)
        : base("QuestForge", ImGuiWindowFlags.None)
    {
        _host = host;
        _scheduler = scheduler;
        _config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(320, 180),
            MaximumSize = new System.Numerics.Vector2(600, 400)
        };
    }

    public void SetConfigWindow(ConfigWindow configWindow) => _configWindow = configWindow;
    internal void SetPlaylistWindow(PlaylistWindow playlistWindow) => _playlistWindow = playlistWindow;

    public override void Draw()
    {
        DrawStatus();
        if (_host.IsRunActive)
            DrawRunDetails();
        DrawPlaylistProgress();
        ImGui.Separator();
        DrawControls();
    }

    private void DrawStatus()
    {
        var statusText = BuildStatusText();
        ImGui.TextUnformatted($"Status: {statusText}");
    }

    private void DrawRunDetails()
    {
        var questName = _host.CurrentQuestName;
        var questId = _host.CurrentQuestId;
        if (questName is not null && questId is not null)
            ImGui.TextUnformatted($"Quest: {questName} ({questId.Value.Value})");

        var stepId = _host.CurrentStepId;
        if (stepId is not null)
        {
            var progress = _host.TotalStepCount > 0
                ? $" ({_host.CurrentStepIndex}/{_host.TotalStepCount})"
                : "";
            ImGui.TextUnformatted($"Step: {stepId}{progress}");
        }

        var action = _host.LastActionName;
        if (action is not null)
            ImGui.TextUnformatted($"Action: {action}");
    }

    private void DrawPlaylistProgress()
    {
        var playlist = _config.QuestPlaylist;
        if (playlist.Count == 0) return;
        var completed = playlist.Count(id =>
            _host.QuestState.IsQuestComplete(
                new QuestForge.Adapters.Types.QuestId(id),
                CancellationToken.None).GetAwaiter().GetResult().ValueOrDefault);
        ImGui.TextUnformatted($"Playlist: {completed}/{playlist.Count}");
    }

    private string BuildStatusText()
    {
        if (_host.IsAutoMode)
        {
            return _scheduler.CurrentStatus switch
            {
                SchedulerStatus.Running r      => $"Running quest {r.CurrentQuest.Value}",
                SchedulerStatus.SelectingNext  => "Selecting next quest...",
                SchedulerStatus.Idle           => "Idle — no available quests in corpus",
                SchedulerStatus.AwaitingUser a => FormatAwaitingUser(a),
                SchedulerStatus.Paused         => "Paused",
                _                              => "Auto mode active"
            };
        }

        if (_host.IsRunActive)
            return $"Running quest {_host.CurrentQuestId?.Value} (manual)";

        return "Stopped";
    }

    private void DrawControls()
    {
        if (!_host.IsAutoMode)
        {
            if (ImGui.Button("Start All Questing"))
                _host.StartAutoMode(_scheduler, _config.UserTracingEnabled);
        }
        else
        {
            if (ImGui.Button("Stop"))
                _host.StopAutoMode();
        }

        ImGui.SameLine();
        if (ImGui.Button("Playlist"))
            _playlistWindow?.Toggle();
        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            _configWindow?.Toggle();
    }

    private static string FormatAwaitingUser(SchedulerStatus.AwaitingUser s)
    {
        var r = s.Reason;
        if (r.LevelTooLow)            return $"Quest {s.BlockedQuest.Value}: level too low (need {r.RequiredLevel})";
        if (r.WrongJob)               return $"Quest {s.BlockedQuest.Value}: wrong job";
        if (r.PrerequisiteIncomplete) return $"Quest {s.BlockedQuest.Value}: missing prerequisites";
        if (r.AlreadyCompleted)       return $"Quest {s.BlockedQuest.Value}: already completed (data inconsistency)";
        return $"Quest {s.BlockedQuest.Value}: {r.Detail ?? "unavailable"}";
    }
}
