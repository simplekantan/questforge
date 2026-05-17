using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;
using QuestForge.Schema;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class ExportDialog : Window
{
    private readonly AuthoringHost _host;
    private readonly IDalamudPluginInterface _pi;
    private readonly IDataManager _dataManager;

    private string _exportPath = "";
    private bool _validateBeforeExport = true;
    private string _statusMessage = "";
    private bool _statusIsError;
    private List<DraftValidationError> _validationErrors = new();
    private List<DraftValidationWarning> _validationWarnings = new();
    private bool _showExportAnyway;

    private readonly DraftValidator _validator = new();

    public ExportDialog(AuthoringHost host, IDalamudPluginInterface pi, IDataManager dataManager)
        : base("QuestForge — Export Draft", ImGuiWindowFlags.None)
    {
        _host = host;
        _pi = pi;
        _dataManager = dataManager;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(400, 280),
            MaximumSize = new System.Numerics.Vector2(700, 500)
        };
    }

    public void OpenForTarget()
    {
        var target = _host.AuthoringTarget;
        _exportPath = target.HasValue
            ? Path.Combine(_pi.GetPluginConfigDirectory(), "quests", $"{target.Value.Value}.json")
            : Path.Combine(_pi.GetPluginConfigDirectory(), "quests", "unknown.json");
        _statusMessage = "";
        _statusIsError = false;
        _validationErrors.Clear();
        _validationWarnings.Clear();
        _showExportAnyway = false;
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Export Draft to Quest File");
        ImGui.Separator();

        ImGui.TextUnformatted("Export path:");
        ImGui.SetNextItemWidth(500f);
        ImGui.InputText("##exportpath", ref _exportPath, 512);

        ImGui.Checkbox("Validate before export", ref _validateBeforeExport);

        ImGui.Spacing();

        if (ImGui.Button("Export"))
            DoExport(forceExport: false);

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            IsOpen = false;
            return;
        }

        if (_statusMessage.Length > 0)
        {
            ImGui.Spacing();
            if (_statusIsError)
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f));
            else
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.2f, 1f, 0.2f, 1f));
            ImGui.TextUnformatted(_statusMessage);
            ImGui.PopStyleColor();
        }

        if (_validationErrors.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Validation Errors:");
            foreach (var e in _validationErrors)
                ImGui.TextUnformatted($"  [{e.Code}] {e.Message}");
        }

        if (_validationWarnings.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Validation Warnings:");
            foreach (var w in _validationWarnings)
                ImGui.TextUnformatted($"  [{w.Code}] {w.Message}");
        }

        if (_showExportAnyway)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Export despite validation errors?");
            if (ImGui.Button("Export Anyway"))
                DoExport(forceExport: true);
        }
    }

    private void DoExport(bool forceExport)
    {
        _statusMessage = "";
        _statusIsError = false;
        _validationErrors.Clear();
        _validationWarnings.Clear();
        _showExportAnyway = false;

        var target = _host.AuthoringTarget;
        if (target is null)
        {
            _statusMessage = "No authoring target set.";
            _statusIsError = true;
            return;
        }

        // Synchronous get — in practice the draft is already in cache
        var draft = _host.DraftManager.Get(target.Value, CancellationToken.None).GetAwaiter().GetResult();
        if (draft is null)
        {
            _statusMessage = "No draft found for this quest.";
            _statusIsError = true;
            return;
        }

        if (_validateBeforeExport && !forceExport)
        {
            var (errors, warnings) = _validator.Validate(draft);
            _validationErrors = errors.ToList();
            _validationWarnings = warnings.ToList();

            if (errors.Count > 0)
            {
                _statusMessage = $"Validation failed with {errors.Count} error(s). Fix them or export anyway.";
                _statusIsError = true;
                _showExportAnyway = true;
                return;
            }
        }

        try
        {
            // Rebuild each step's Raw from its snapshot data so that the exported file
            // reflects current authoring logic rather than stale rawJson from old sessions.
            var now = DateTimeOffset.UtcNow;
            foreach (var step in draft.Steps.ToList())
            {
                var rebuilt = StepFactory.Build(step.StepType, step.StepId, step.SuggestedExpect, step.ObservedAfter);
                draft.ReplaceStep(step.StepId, step with { Raw = rebuilt }, now);
            }

            var definition = draft.ToQuestDefinition();
            definition = PatchFromLumina(definition);
            var json = JsonSerializer.Serialize(definition, QuestForgeJsonContext.QuestFileOptions);
            var dir = Path.GetDirectoryName(_exportPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(_exportPath, json);
            _statusMessage = $"Exported successfully to: {_exportPath}";
            _statusIsError = false;
        }
        catch (DraftSerializationException ex)
        {
            _statusMessage = $"Serialization error: {ex.Message}";
            _statusIsError = true;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Export failed: {ex.Message}";
            _statusIsError = true;
        }
    }

    private QuestDefinition PatchFromLumina(QuestDefinition def)
    {
        var questSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        if (questSheet is null) return def;

        var row = questSheet.GetRow(def.Id);
        var name = row.Name.ToString();
        if (string.IsNullOrEmpty(name)) return def;

        var minLevel = (int)row.ClassJobLevel[0];
        var classJobCatId = row.ClassJobCategory0.RowId;

        var reqs = def.Requirements ?? new Requirements { Prereqs = [] };
        return def with
        {
            Name = name,
            Requirements = reqs with { MinLevel = minLevel > 0 ? minLevel : reqs.MinLevel }
        };
    }
}
