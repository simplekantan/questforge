using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Scheduling;

namespace QuestForge.Plugin.UI;

public sealed class ConfigWindow : Window
{
    private readonly PluginConfig _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly TraceSession _traceSession;
    private readonly EngineHost _host;

    public ConfigWindow(PluginConfig config, IDalamudPluginInterface pi, TraceSession traceSession, EngineHost host)
        : base("QuestForge — Settings", ImGuiWindowFlags.None)
    {
        _config = config;
        _pi = pi;
        _traceSession = traceSession;
        _host = host;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(380, 340),
            MaximumSize = new System.Numerics.Vector2(600, 600)
        };
    }

    public override void Draw()
    {
        DrawEngineSettings();
        ImGui.Spacing();
        ImGui.Separator();
        DrawAuthoringSettings();
    }

    private void DrawEngineSettings()
    {
        ImGui.TextUnformatted("Engine");
        ImGui.Separator();

        var sideQuests = _config.EnableSideQuests;
        if (ImGui.Checkbox("Side quests", ref sideQuests))
        {
            _config.EnableSideQuests = sideQuests;
            Save();
            _host.UpdateSchedulerOptions(BuildOptions());
        }

        var craftGather = _config.EnableCraftGatherQuests;
        if (ImGui.Checkbox("Crafting / gathering quests", ref craftGather))
        {
            _config.EnableCraftGatherQuests = craftGather;
            Save();
            _host.UpdateSchedulerOptions(BuildOptions());
        }

        var blueQuests = _config.EnableBlueQuests;
        if (ImGui.Checkbox("Feature unlock quests (blue)", ref blueQuests))
        {
            _config.EnableBlueQuests = blueQuests;
            Save();
            _host.UpdateSchedulerOptions(BuildOptions());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Run blue quests (Gold Saucer, Hildebrand, etc.).\n\"blue-urgent\" quests (e.g. chocobo companion) always run regardless.");

        ImGui.Spacing();

        var preferStylist = _config.PreferStylist;
        if (ImGui.Checkbox("Prefer Stylist for equip-best-gear", ref preferStylist))
        {
            _config.PreferStylist = preferStylist;
            Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, EquipBestGearStep uses the Stylist plugin.\nFalls back to native Recommended Gear if Stylist is unavailable.");

        ImGui.Spacing();

        ImGui.SetNextItemWidth(100f);
        var difficultyNames = new[] { "Normal", "Easy", "Very Easy" };
        var diffIdx = (int)_config.PreferredSpdDifficulty;
        if (ImGui.Combo("SPD difficulty", ref diffIdx, difficultyNames, difficultyNames.Length))
        {
            _config.PreferredSpdDifficulty = (SpdDifficulty)diffIdx;
            Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Difficulty selected when retrying a failed Single Player Duty.");

        ImGui.Spacing();

        ImGui.SetNextItemWidth(80f);
        var cooldown = (float)_config.ActionCooldownSeconds;
        if (ImGui.InputFloat("Action cooldown (sec)", ref cooldown, 0.5f, 1.0f, "%.1f"))
        {
            if (cooldown < 0f) cooldown = 0f;
            _config.ActionCooldownSeconds = cooldown;
            Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Seconds to wait after firing an instant action\n(say, emote, action, item) before re-firing. 0 = no delay.");
    }

    private void DrawAuthoringSettings()
    {
        ImGui.TextUnformatted("Authoring");
        ImGui.Separator();

        var showCompleted = _config.ShowCompletedQuestsInAuthorPanel;
        if (ImGui.Checkbox("Show completed quests in Interaction panel", ref showCompleted))
        {
            _config.ShowCompletedQuestsInAuthorPanel = showCompleted;
            Save();
        }

        var showUnacceptable = _config.ShowUnacceptableQuestsInAuthorPanel;
        if (ImGui.Checkbox("Show locked/future quests in Interaction panel", ref showUnacceptable))
        {
            _config.ShowUnacceptableQuestsInAuthorPanel = showUnacceptable;
            Save();
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(110f);
        var traceModeNames = new[] { "Off", "Always", "Authoring", "Recording" };
        var traceModeIdx = (int)_config.TraceMode;
        if (ImGui.Combo("Trace mode", ref traceModeIdx, traceModeNames, traceModeNames.Length))
        {
            var newMode = (TraceMode)traceModeIdx;
            _config.TraceMode = newMode;
            Save();
            _traceSession.ChangeMode(newMode);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Off — no trace files\n" +
                "Always — one file per plugin session\n" +
                "Authoring — one file per author session\n" +
                "Recording — one file per recording window");

        ImGui.Spacing();

        ImGui.SetNextItemWidth(100f);
        var heartbeat = _config.AuthoringHeartbeatMs;
        if (ImGui.InputInt("Authoring heartbeat (ms)", ref heartbeat, 50, 100))
        {
            if (heartbeat < 50) heartbeat = 50;
            if (heartbeat > 2000) heartbeat = 2000;
            _config.AuthoringHeartbeatMs = heartbeat;
            Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How often the authoring system polls game state (ms).\nLower = more responsive, higher = less CPU. Default: 250.");
    }

    private void Save() => _config.Save(_pi);

    private SchedulerOptions BuildOptions() => new(
        ManualChain: [],
        EnableCraftGatherQuests: _config.EnableCraftGatherQuests,
        EnableSideQuests: _config.EnableSideQuests,
        EnableBlueQuests: _config.EnableBlueQuests);
}
