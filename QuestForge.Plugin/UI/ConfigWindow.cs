using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Rewards;
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

        var equipOnStart = _config.EquipBestGearOnStart;
        if (ImGui.Checkbox("Equip best gear on Start All Questing", ref equipOnStart))
        {
            _config.EquipBestGearOnStart = equipOnStart;
            Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically equip best available gear before the first quest starts.");

        ImGui.Spacing();

        ImGui.SetNextItemWidth(130f);
        var gcNames = new[] { "User Decides", "Serpents", "Flames", "Maelstrom", "Random" };
        var gcIdx = (int)_config.ChosenGrandCompany;
        if (ImGui.Combo("Grand Company", ref gcIdx, gcNames, gcNames.Length))
        {
            _config.ChosenGrandCompany = (GrandCompanyChoice)gcIdx;
            Save();
            _host.UpdateSchedulerOptions(BuildOptions());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which Grand Company to join at the MSQ branch.\nUser Decides halts automation so you can pick manually.");

        ImGui.Spacing();

        ImGui.SetNextItemWidth(160f);
        var chocoboName = _config.ChocoboName;
        if (ImGui.InputText("Chocobo name", ref chocoboName, 21))
        {
            if (PluginConfig.IsValidChocoboName(chocoboName))
            {
                _config.ChocoboName = chocoboName;
                Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Name auto-filled when the game asks you to name your chocobo.\n2-20 alphabetic characters. Leave blank to name it yourself.");

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

        ImGui.Spacing();
        DrawRewardPriority();
    }

    private static readonly Dictionary<RewardPriority, string> PriorityLabels = new()
    {
        [RewardPriority.BiggestUpgrade]   = "Biggest Upgrade",
        [RewardPriority.HighestGilValue]  = "Highest Gil Value",
        [RewardPriority.GearCoffer]       = "Gear Coffer",
        [RewardPriority.GilSack]          = "Gil Sack (Allagan Piece)",
        [RewardPriority.EquippableGear]   = "Equippable Gear",
        [RewardPriority.UnequippableGear] = "Unequippable Gear",
        [RewardPriority.AnythingElse]     = "Anything Else",
    };

    private void DrawRewardPriority()
    {
        ImGui.TextUnformatted("Reward priority");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When a quest offers optional rewards, tiers are evaluated\ntop-to-bottom. The first tier that matches a reward wins.");

        var list = _config.RewardPriorityOrder;
        int? swap = null;
        for (var i = 0; i < list.Count; i++)
        {
            ImGui.PushID(i);
            var canUp = i > 0;
            var canDown = i < list.Count - 1;

            if (canUp)
            {
                if (ImGui.SmallButton("▲")) swap = i - 1;
            }
            else
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton("▲");
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
            if (canDown)
            {
                if (ImGui.SmallButton("▼")) swap ??= i;
            }
            else
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton("▼");
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. {PriorityLabels.GetValueOrDefault(list[i], list[i].ToString())}");
            ImGui.PopID();
        }

        if (swap is { } s)
        {
            (list[s], list[s + 1]) = (list[s + 1], list[s]);
            Save();
        }

        if (ImGui.SmallButton("Reset to default"))
        {
            _config.RewardPriorityOrder = new(RewardPrioritizer.DefaultPriorityOrder);
            Save();
        }
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
        var traceModeNames = new[] { "Off", "Always", "Authoring", "Recording", "Quest Run" };
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

    internal SchedulerOptions BuildOptions() => new(
        ManualChain: _config.QuestPlaylist.Select(id => new QuestForge.Adapters.Types.QuestId(id)).ToList(),
        EnableCraftGatherQuests: _config.EnableCraftGatherQuests,
        EnableSideQuests: _config.EnableSideQuests,
        EnableBlueQuests: _config.EnableBlueQuests,
        GrandCompanyChoice: MapGrandCompanyChoice(_config.ChosenGrandCompany));

    internal static GrandCompanyPreference MapGrandCompanyChoice(GrandCompanyChoice choice) => choice switch
    {
        GrandCompanyChoice.UserDecides => GrandCompanyPreference.UserDecides,
        GrandCompanyChoice.Serpents    => GrandCompanyPreference.TwinAdder,
        GrandCompanyChoice.Flames      => GrandCompanyPreference.ImmortalFlames,
        GrandCompanyChoice.Maelstrom   => GrandCompanyPreference.Maelstrom,
        GrandCompanyChoice.Random      => GrandCompanyPreference.Random,
        _ => GrandCompanyPreference.UserDecides,
    };
}
