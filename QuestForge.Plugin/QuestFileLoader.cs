using System.Text.Json;
using QuestForge.Schema;

namespace QuestForge.Plugin;

internal static class QuestFileLoader
{
    internal static QuestDefinition? Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions);
    }

    internal static FragmentDefinition? LoadFragment(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FragmentDefinition>(json, QuestForgeJsonContext.QuestFileOptions);
    }
}
