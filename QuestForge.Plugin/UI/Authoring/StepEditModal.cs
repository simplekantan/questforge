using System.Text.Json;
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
        IsOpen = true;
    }

    public override void Draw()
    {
        if (_editingStep is null) { IsOpen = false; return; }

        ImGui.TextUnformatted("Edit Step (display only — Raw JSON is read-only in Phase 9)");
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

        ImGui.TextUnformatted("Suggested Expect:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_suggestedExpect.Length > 0 ? _suggestedExpect : "(none)");

        ImGui.Spacing();
        ImGui.TextUnformatted("Raw JSON (read-only):");
        ImGui.Separator();
        ImGui.TextUnformatted(_rawJsonDisplay);

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

    public void SetRecordModal(RecordStepModal modal) => _recordModal = modal;
}
