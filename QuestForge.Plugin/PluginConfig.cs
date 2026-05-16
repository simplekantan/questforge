using Dalamud.Plugin;
using System.Text.Json;

namespace QuestForge.Plugin;

public sealed class PluginConfig
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Whether to write trace files during runs. Off by default — traces consume disk space.
    /// Enable with /qf config trace on, or automatically when Authoring mode is active.
    /// </summary>
    public bool UserTracingEnabled { get; set; } = false;

    public bool EnableSideQuests { get; set; } = false;
    public bool EnableCraftGatherQuests { get; set; } = false;

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
