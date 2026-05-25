using Dalamud.Plugin;
using QuestForge.Adapters.Tracing;
using System.Text.Json;

namespace QuestForge.Plugin;

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
