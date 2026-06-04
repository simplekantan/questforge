using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;
using QuestForge.Schema;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class StepEditModal : Window
{
    private readonly AuthoringHost _host;
    private RecordStepModal? _recordModal;

    private DraftStep? _editingStep;
    private string _stepId = "";
    private string _stepType = "";
    private string _sequenceNumber = "";
    private string _suggestedExpect = "";
    private string _rawJsonDisplay = "(none)";

    private bool _editingExpect;
    private string _editExpectValue = "";
    private string _saveStatus = "";

    public StepEditModal(AuthoringHost host)
        : base("QuestForge — Edit Step", ImGuiWindowFlags.None)
    {
        _host = host;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(400, 300),
            MaximumSize = new System.Numerics.Vector2(700, 600)
        };
    }

    public void OpenFor(DraftStep step)
    {
        _editingStep = step;
        _stepId = step.StepId;
        _stepType = step.StepType;
        _sequenceNumber = step.SequenceNumber.ToString();
        _suggestedExpect = step.SuggestedExpect ?? "";
        _rawJsonDisplay = step.Raw is not null
            ? JsonSerializer.Serialize(step.Raw, QuestForgeJsonContext.QuestFileOptions)
            : "(none)";
        _editingExpect = false;
        _editExpectValue = "";
        _saveStatus = "";
        IsOpen = true;
    }

    public override void Draw()
    {
        if (_editingStep is null) { IsOpen = false; return; }

        ImGui.TextUnformatted("Edit Step");
        ImGui.Separator();

        ImGui.TextUnformatted("Step ID:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_stepId);

        ImGui.TextUnformatted("Step Type:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_stepType);

        ImGui.TextUnformatted("Sequence Number:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_sequenceNumber);

        if (_editingExpect)
            DrawExpectEditor();
        else
            DrawExpectDisplay();

        ImGui.Spacing();
        ImGui.TextUnformatted("Raw JSON:");
        ImGui.Separator();
        ImGui.TextUnformatted(_rawJsonDisplay);

        if (_saveStatus.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(_saveStatus);
        }

        ImGui.Spacing();
        if (_host.Mode == AuthoringMode.Author && _recordModal is not null)
        {
            if (ImGui.Button("Re-record"))
            {
                _recordModal.OpenForReplace(_editingStep);
                _editingStep = null;
                IsOpen = false;
            }
            ImGui.SameLine();
        }
        if (ImGui.Button("Close"))
        {
            _editingStep = null;
            IsOpen = false;
        }
    }

    private void DrawExpectDisplay()
    {
        ImGui.TextUnformatted("Expect:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_suggestedExpect.Length > 0 ? _suggestedExpect : "(none)");
        if (_host.Mode == AuthoringMode.Author)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit"))
            {
                _editingExpect = true;
                _editExpectValue = _suggestedExpect;
                _saveStatus = "";
            }
        }
    }

    private void DrawExpectEditor()
    {
        ImGui.TextUnformatted("Expect:");

        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo("##editexpectpick", "Pick predicate..."))
        {
            foreach (var option in PredicateOptions.Build(_host.CurrentSnapshot))
            {
                if (ImGui.Selectable(option))
                    _editExpectValue = option == "(none)" ? "" : option;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280f);
        ImGui.InputText("##editexpect", ref _editExpectValue, 256);

        if (ImGui.SmallButton("Save"))
        {
            ApplyExpectChange(_editExpectValue.Length > 0 ? _editExpectValue : null);
            _editingExpect = false;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
        {
            _editingExpect = false;
        }
    }

    private void ApplyExpectChange(string? newExpect)
    {
        if (_editingStep is null || _host.AuthoringTarget is null) return;

        var updatedRaw = UpdateRawStepExpect(_editingStep.Raw, newExpect);
        var updatedStep = _editingStep with { SuggestedExpect = newExpect, Raw = updatedRaw };

        var draft = _host.DraftManager.Get(_host.AuthoringTarget.Value, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (draft is null) return;

        draft.ReplaceStep(_editingStep.StepId, updatedStep, DateTimeOffset.UtcNow);
        _host.DraftManager.MarkDirty(_host.AuthoringTarget.Value);
        _ = _host.DraftManager.SaveNow(_host.AuthoringTarget.Value, CancellationToken.None);

        _editingStep = updatedStep;
        _suggestedExpect = newExpect ?? "";
        _rawJsonDisplay = updatedRaw is not null
            ? JsonSerializer.Serialize(updatedRaw, QuestForgeJsonContext.QuestFileOptions)
            : "(none)";
        _saveStatus = "Expect updated.";
    }

    private static Step? UpdateRawStepExpect(Step? raw, string? newExpect)
    {
        if (raw is null) return null;

        // Serialize as base Step so the polymorphic "type" discriminator is included
        var json = JsonSerializer.Serialize<Step>(raw, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json);
        if (node is null) return raw;

        if (newExpect is { Length: > 0 })
            node["expect"] = newExpect;
        else
            node.AsObject().Remove("expect");

        return JsonSerializer.Deserialize<Step>(node.ToJsonString(), QuestForgeJsonContext.QuestFileOptions) ?? raw;
    }

    public void SetRecordModal(RecordStepModal modal) => _recordModal = modal;
}
