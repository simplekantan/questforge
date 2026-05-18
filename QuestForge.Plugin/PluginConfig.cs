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
    /// Controls when trace files are written. Defaults to Off (no traces) for normal users.
    ///
    /// NOTE: TraceMode is fixed at plugin load and cannot be changed while the plugin is running.
    /// Changing this setting takes effect on the next plugin reload (game restart or /xlplugins
    /// reinstall). This is intentional — TraceSession is constructed once at startup and the
    /// mode cannot safely change mid-session without closing/reopening the active file.
    ///
    /// For quest development, Authoring is the recommended mode. Once the quest corpus is
    /// stable, this can remain Off.
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
