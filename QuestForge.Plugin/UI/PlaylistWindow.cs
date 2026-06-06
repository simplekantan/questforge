using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using QuestForge.Plugin.DataDelivery;

namespace QuestForge.Plugin.UI;

internal sealed class PlaylistWindow : Window
{
    private readonly PluginConfig _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly IDataManager _dataManager;
    private readonly EngineHost _host;
    private readonly QuestFileIndex _questFileIndex;

    private string _searchText = "";
    private List<(uint Id, string Name, bool HasFile)>? _searchResults;

    public PlaylistWindow(
        PluginConfig config,
        IDalamudPluginInterface pi,
        IDataManager dataManager,
        EngineHost host,
        QuestFileIndex questFileIndex)
        : base("QuestForge — Quest Playlist", ImGuiWindowFlags.None)
    {
        _config = config;
        _pi = pi;
        _dataManager = dataManager;
        _host = host;
        _questFileIndex = questFileIndex;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(400, 300),
            MaximumSize = new System.Numerics.Vector2(600, 600)
        };
    }

    public override void Draw()
    {
        DrawSearch();
        ImGui.Separator();
        DrawPlaylist();
    }

    private void DrawSearch()
    {
        ImGui.TextUnformatted("Add quest to playlist:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##search", ref _searchText, 128))
            UpdateSearchResults();

        if (_searchResults is { Count: > 0 })
        {
            var maxVisible = Math.Min(_searchResults.Count, 8);
            var height = maxVisible * ImGui.GetTextLineHeightWithSpacing();
            if (ImGui.BeginChild("##searchResults", new System.Numerics.Vector2(-1, height), true))
            {
                foreach (var (id, name, hasFile) in _searchResults)
                {
                    if (!hasFile) ImGui.BeginDisabled();

                    var label = $"[{id}] {name}";
                    if (hasFile && _config.QuestPlaylist.Contains(id))
                        label += " (in playlist)";

                    if (ImGui.Selectable(label, false) && hasFile && !_config.QuestPlaylist.Contains(id))
                    {
                        _config.QuestPlaylist.Add(id);
                        Save();
                        _searchText = "";
                        _searchResults = null;
                    }

                    if (!hasFile) ImGui.EndDisabled();
                }
            }
            ImGui.EndChild();
        }
    }

    private void UpdateSearchResults()
    {
        if (string.IsNullOrWhiteSpace(_searchText) || _searchText.Length < 2)
        {
            _searchResults = null;
            return;
        }

        var sheet = _dataManager.GetExcelSheet<Quest>();
        if (sheet is null) { _searchResults = null; return; }

        var query = _searchText.Trim();
        var results = new List<(uint Id, string Name, bool HasFile)>();

        if (uint.TryParse(query, out var exactId))
        {
            if (sheet.TryGetRow(exactId, out var row))
            {
                var name = row.Name.ExtractText();
                if (!string.IsNullOrEmpty(name))
                {
                    var hasFile = _questFileIndex.GetPath(new QuestForge.Adapters.Types.QuestId(exactId)) is not null;
                    results.Add((exactId, name, hasFile));
                }
            }
        }

        foreach (var row in sheet)
        {
            if (results.Count >= 20) break;
            var name = row.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (results.Any(r => r.Id == row.RowId)) continue;
            var hasFile = _questFileIndex.GetPath(new QuestForge.Adapters.Types.QuestId(row.RowId)) is not null;
            results.Add((row.RowId, name, hasFile));
        }

        _searchResults = results;
    }

    private void DrawPlaylist()
    {
        var list = _config.QuestPlaylist;
        ImGui.TextUnformatted($"Playlist ({list.Count} quests):");
        if (list.Count == 0)
        {
            ImGui.TextDisabled("Empty. Search above to add quests.");
            return;
        }

        int? swap = null;
        int? removeAt = null;

        for (var i = 0; i < list.Count; i++)
        {
            ImGui.PushID(i);
            var questId = list[i];
            var name = LookupQuestName(questId);

            var canUp = i > 0;
            var canDown = i < list.Count - 1;

            if (canUp) { if (ImGui.SmallButton("▲")) swap = i - 1; }
            else { ImGui.BeginDisabled(); ImGui.SmallButton("▲"); ImGui.EndDisabled(); }

            ImGui.SameLine();
            if (canDown) { if (ImGui.SmallButton("▼")) swap ??= i; }
            else { ImGui.BeginDisabled(); ImGui.SmallButton("▼"); ImGui.EndDisabled(); }

            ImGui.SameLine();
            if (ImGui.SmallButton("X")) removeAt = i;

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. [{questId}] {name}");
            ImGui.PopID();
        }

        if (swap is { } s)
        {
            (list[s], list[s + 1]) = (list[s + 1], list[s]);
            Save();
        }

        if (removeAt is { } r)
        {
            list.RemoveAt(r);
            Save();
        }

        ImGui.Spacing();
        if (ImGui.SmallButton("Clear All"))
        {
            list.Clear();
            Save();
        }
    }

    private string LookupQuestName(uint questId)
    {
        var sheet = _dataManager.GetExcelSheet<Quest>();
        if (sheet is not null && sheet.TryGetRow(questId, out var row))
        {
            var name = row.Name.ExtractText();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return "(unknown)";
    }

    private void Save()
    {
        _config.Save(_pi);
        _host.UpdateSchedulerOptions(
            new QuestForge.Engine.Scheduling.SchedulerOptions(
                _config.QuestPlaylist.Select(id => new QuestForge.Adapters.Types.QuestId(id)).ToList(),
                _config.EnableCraftGatherQuests,
                _config.EnableSideQuests,
                _config.EnableBlueQuests));
    }
}
