using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Plugin.UI;

public sealed class MainWindow : Window
{
    private readonly EngineHost _host;
    private readonly IQuestScheduler _scheduler;
    private readonly PluginConfig _config;
    private ConfigWindow? _configWindow;

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

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawControls();
    }

    private void DrawStatus()
    {
        var statusText = BuildStatusText();
        ImGui.TextUnformatted($"Status: {statusText}");
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
