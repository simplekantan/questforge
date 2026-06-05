using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Plugin.DataDelivery;

internal sealed class QuestFileIndex
{
    private readonly Dictionary<QuestId, string> _pathById = new();
    private readonly Dictionary<QuestId, string> _categoryById = new();

    internal IReadOnlyDictionary<QuestId, string> Categories => _categoryById;

    internal static QuestFileIndex Build(string questsDir, Dalamud.Plugin.Services.IPluginLog log)
    {
        var index = new QuestFileIndex();
        if (!Directory.Exists(questsDir)) return index;

        // Pass 1: scan subdirectories (downloaded data)
        foreach (var file in Directory.EnumerateFiles(questsDir, "*.json", SearchOption.AllDirectories))
        {
            if (IsRootFile(questsDir, file)) continue;
            index.TryIndex(file, log);
        }

        // Pass 2: scan root (locally authored) — overwrites downloaded entries on conflict
        foreach (var file in Directory.EnumerateFiles(questsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            index.TryIndex(file, log);
        }

        log.Debug($"[QuestFileIndex] indexed {index._pathById.Count} quest(s)");
        return index;
    }

    internal string? GetPath(QuestId questId)
        => _pathById.GetValueOrDefault(questId);

    private void TryIndex(string file, Dalamud.Plugin.Services.IPluginLog log)
    {
        try
        {
            var def = QuestFileLoader.Load(file);
            if (def is null || def.Id == 0) return;
            var qid = new QuestId(def.Id);
            _pathById[qid] = file;
            if (def.Category is { } cat)
                _categoryById[qid] = cat;
        }
        catch (Exception ex)
        {
            log.Warning($"[QuestFileIndex] skipping {file}: {ex.Message}");
        }
    }

    private static bool IsRootFile(string root, string file)
        => string.Equals(
            Path.GetDirectoryName(file),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
