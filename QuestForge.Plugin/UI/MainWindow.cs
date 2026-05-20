using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Plugin.UI;

public sealed class MainWindow : Window
{
    private readonly EngineHost _host;
    private readonly IQuestScheduler _scheduler;
    private readonly PluginConfig _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly TraceSession _traceSession;

    public MainWindow(EngineHost host, IQuestScheduler scheduler, PluginConfig config, IDalamudPluginInterface pi, TraceSession traceSession)
        : base("QuestForge", ImGuiWindowFlags.None)
    {
        _host = host;
        _scheduler = scheduler;
        _config = config;
        _pi = pi;
        _traceSession = traceSession;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(320, 180),
            MaximumSize = new System.Numerics.Vector2(600, 400)
        };
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawControls();
        ImGui.Separator();
        DrawSettings();
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
    }

    private void DrawSettings()
    {
        ImGui.TextUnformatted("Settings");

        var sideQuests = _config.EnableSideQuests;
        if (ImGui.Checkbox("Side Quests (off by default)", ref sideQuests))
        {
            _config.EnableSideQuests = sideQuests;
            _config.Save(_pi);
            _host.UpdateSchedulerOptions(BuildOptions());
        }

        var craftGather = _config.EnableCraftGatherQuests;
        if (ImGui.Checkbox("Crafting / Gathering Quests (off by default)", ref craftGather))
        {
            _config.EnableCraftGatherQuests = craftGather;
            _config.Save(_pi);
            _host.UpdateSchedulerOptions(BuildOptions());
        }

        var blueQuests = _config.EnableBlueQuests;
        if (ImGui.Checkbox("Feature Unlock Quests (blue)", ref blueQuests))
        {
            _config.EnableBlueQuests = blueQuests;
            _config.Save(_pi);
            _host.UpdateSchedulerOptions(BuildOptions());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Run blue quests (Gold Saucer, Hildebrand, etc.).\n\"blue-urgent\" quests (e.g. chocobo companion) always run regardless of this setting.");

        ImGui.Spacing();

        var tracing = _config.UserTracingEnabled;
        if (ImGui.Checkbox("Trace engine runs", ref tracing))
        {
            _config.UserTracingEnabled = tracing;
            _config.Save(_pi);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes detailed run logs to pluginConfigs\\QuestForge\\traces\\");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        var traceModeNames = new[] { "Off", "Always", "Authoring", "Recording", "Quest Run" };
        var traceModeIdx = (int)_config.TraceMode;
        if (ImGui.Combo("Trace mode", ref traceModeIdx, traceModeNames, traceModeNames.Length))
        {
            var newMode = (TraceMode)traceModeIdx;
            _config.TraceMode = newMode;
            _config.Save(_pi);
            _traceSession.ChangeMode(newMode);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Off — no trace files\n" +
                "Always — one file per plugin session (engine + authoring)\n" +
                "Authoring — one file per author session\n" +
                "Recording — one file per recording window");
    }

    private SchedulerOptions BuildOptions() => new(
        ManualChain: [],
        EnableCraftGatherQuests: _config.EnableCraftGatherQuests,
        EnableSideQuests: _config.EnableSideQuests,
        EnableBlueQuests: _config.EnableBlueQuests);

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
