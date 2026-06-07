using Dalamud.Plugin;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Tracing;
using QuestForge.Engine.Rewards;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Plugin;

/// <summary>
/// Preferred difficulty when retrying a Single Player Duty (DutyStep kind="spd").
/// Read by EngineHost.TryHandleDifficultySelect when the DifficultySelectYesNo addon appears.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpdDifficulty>))]
public enum SpdDifficulty
{
    Normal   = 0,
    Easy     = 1,
    VeryEasy = 2
}

public sealed class PluginConfig
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Whether to write trace files during engine runs. Off by default — traces consume disk space.
    /// Enable with /qf config trace on. Authoring mode always writes its own trace automatically
    /// (separate from this setting) to pluginConfigs/QuestForge/traces/author-{questId}-{timestamp}.jsonl.
    /// </summary>
    public bool UserTracingEnabled { get; set; } = false;

    public bool EnableSideQuests { get; set; } = false;
    public bool EnableCraftGatherQuests { get; set; } = false;
    public bool EnableBlueQuests { get; set; } = false;

    public List<uint> QuestPlaylist { get; set; } = [];

    public int AuthoringHeartbeatMs { get; set; } = 250;

    public bool ShowCompletedQuestsInAuthorPanel { get; set; } = false;

    /// <summary>
    /// When true, the Interaction panel reveals locked/future quests that are not acceptable right now
    /// (e.g. wrong GC rank). When false (default), only quests the player can accept immediately are shown.
    /// </summary>
    public bool ShowUnacceptableQuestsInAuthorPanel { get; set; } = false;

    /// <summary>
    /// Controls when trace files are written. Defaults to Off (no traces) for normal users.
    /// Can be changed live via the settings UI — no plugin reload required.
    /// For quest development, Authoring is the recommended mode.
    /// </summary>
    public TraceMode TraceMode { get; set; } = TraceMode.Off;

    /// <summary>
    /// When true, EquipBestGearStep prefers Stylist over the native Recommended Gear module.
    /// Falls back to native if Stylist is not installed. Default: true.
    /// </summary>
    public bool PreferStylist { get; set; } = true;

    /// <summary>
    /// Preferred difficulty when retrying a Single Player Duty.
    /// Selected when the DifficultySelectYesNo addon appears after an SPD failure.
    /// Default: Normal.
    /// </summary>
    public SpdDifficulty PreferredSpdDifficulty { get; set; } = SpdDifficulty.Normal;

    /// <summary>
    /// Seconds to wait after firing an instant action (SayChatMessage, UseEmote, UseAction, UseItem)
    /// before allowing re-fire on the same step. Prevents visible spam for actions with no cast bar.
    /// Set to 0 to disable throttling.
    /// </summary>
    public double ActionCooldownSeconds { get; set; } = 5.0;

    /// <summary>Seconds of no progress before stuck detection fires.</summary>
    public double NavStallTimeoutSeconds { get; set; } = 5.0;

    /// <summary>Minimum movement in metres to count as progress during navigation.</summary>
    public float NavStallDistanceThreshold { get; set; } = 2.0f;

    /// <summary>Number of jump attempts before escalating to Return.</summary>
    public int NavMaxJumpAttempts { get; set; } = 3;

    /// <summary>
    /// Priority order for selecting quest rewards. Evaluated top-to-bottom; first tier
    /// that produces a winner is used. Quest-level RewardOverride takes precedence over this list.
    /// </summary>
    public List<RewardPriority> RewardPriorityOrder { get; set; } = new(RewardPrioritizer.DefaultPriorityOrder);

    public static PluginConfig Load(IDalamudPluginInterface pi)
    {
        try
        {
            var path = ConfigPath(pi);
            if (!File.Exists(path)) return new PluginConfig();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PluginConfig>(json) ?? new PluginConfig();
        }
        catch { return new PluginConfig(); }
    }

    public void Save(IDalamudPluginInterface pi)
    {
        try { File.WriteAllText(ConfigPath(pi), JsonSerializer.Serialize(this, _jsonOpts)); }
        catch { }
    }

    private static string ConfigPath(IDalamudPluginInterface pi)
        => Path.Combine(pi.GetPluginConfigDirectory(), "config.json");
}
