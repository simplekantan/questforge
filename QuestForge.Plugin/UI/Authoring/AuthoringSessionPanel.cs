using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class AuthoringSessionPanel : Window
{
    private readonly AuthoringHost _host;
    private readonly RecordStepModal _recordModal;
    private readonly StepEditModal _editModal;
    private readonly ExportDialog _exportDialog;

    // Validation result display
    private List<DraftValidationError> _lastErrors = new();
    private List<DraftValidationWarning> _lastWarnings = new();
    private bool _validationRan;
    private readonly DraftValidator _validator = new();

    public AuthoringSessionPanel(AuthoringHost host,
        RecordStepModal recordModal, StepEditModal editModal, ExportDialog exportDialog)
        : base("QuestForge — Authoring Session", ImGuiWindowFlags.None)
    {
        _host = host;
        _recordModal = recordModal;
        _editModal = editModal;
        _exportDialog = exportDialog;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(420, 300),
            MaximumSize = new System.Numerics.Vector2(800, 700)
        };
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"Mode: {_host.Mode}");
        ImGui.TextUnformatted($"Target Quest: {(_host.AuthoringTarget.HasValue ? _host.AuthoringTarget.Value.Value.ToString() : "(none)")}");
        ImGui.Separator();

        switch (_host.Mode)
        {
            case AuthoringMode.Off:
                DrawOffMode();
                break;
            case AuthoringMode.Inspect:
                DrawInspectMode();
                break;
            case AuthoringMode.Author:
                DrawAuthorMode();
                break;
        }
    }

    private void DrawOffMode()
    {
        ImGui.TextUnformatted("Not in authoring mode.");
        ImGui.TextUnformatted("Use /qf inspect or /qf author <questId> to begin.");
    }

    private void DrawInspectMode()
    {
        ImGui.TextUnformatted("Inspect mode active — panels show live game state.");
        ImGui.TextUnformatted("Use /qf author <questId> to start recording.");
        ImGui.Spacing();
        if (ImGui.Button("Exit Inspect Mode"))
            _host.ExitAuthoring();
    }

    private void DrawAuthorMode()
    {
        var target = _host.AuthoringTarget;
        if (target is null) return;

        // Controls row
        if (ImGui.Button("+ Record Next Step"))
        {
            _recordModal.Open();
            _recordModal.IsOpen = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Validate"))
        {
            var draftForValidation = _host.DraftManager.Get(target.Value, CancellationToken.None).GetAwaiter().GetResult();
            if (draftForValidation is not null)
            {
                var (errors, warnings) = _validator.Validate(draftForValidation);
                _lastErrors = errors.ToList();
                _lastWarnings = warnings.ToList();
                _validationRan = true;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Export"))
        {
            _exportDialog.OpenForTarget();
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop Authoring"))
        {
            _host.ExitAuthoring();
            return;
        }

        ImGui.Separator();

        // Validation results
        if (_validationRan)
        {
            if (_lastErrors.Count == 0 && _lastWarnings.Count == 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.2f, 1f, 0.2f, 1f));
                ImGui.TextUnformatted("Validation passed with no errors or warnings.");
                ImGui.PopStyleColor();
            }
            else
            {
                if (_lastErrors.Count > 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f));
                    ImGui.TextUnformatted($"{_lastErrors.Count} error(s):");
                    ImGui.PopStyleColor();
                    foreach (var e in _lastErrors)
                        ImGui.TextUnformatted($"  [{e.Code}] {e.Message}");
                }
                if (_lastWarnings.Count > 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f));
                    ImGui.TextUnformatted($"{_lastWarnings.Count} warning(s):");
                    ImGui.PopStyleColor();
                    foreach (var w in _lastWarnings)
                        ImGui.TextUnformatted($"  [{w.Code}] {w.Message}");
                }
            }
            ImGui.Separator();
        }

        // Steps table
        var draft = _host.DraftManager.Get(target.Value, CancellationToken.None).GetAwaiter().GetResult();
        if (draft is null)
        {
            ImGui.TextUnformatted("No draft loaded.");
            return;
        }

        var steps = draft.Steps;
        ImGui.TextUnformatted($"Steps: {steps.Count}  (Last modified: {draft.LastModifiedAt:HH:mm:ss})");
        ImGui.Separator();

        if (steps.Count == 0)
        {
            ImGui.TextUnformatted("No steps recorded yet. Click '+ Record Next Step' to begin.");
            return;
        }

        string? stepToDelete = null;
        DraftStep? stepToEdit = null;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            ImGui.PushID(i);

            ImGui.TextUnformatted($"[{i + 1}] {step.StepType} | {step.StepId} | seq={step.SequenceNumber}");

            ImGui.SameLine();
            if (ImGui.SmallButton("edit"))
                stepToEdit = step;

            ImGui.SameLine();
            if (ImGui.SmallButton("delete"))
                stepToDelete = step.StepId;

            ImGui.PopID();
        }

        // Perform deletions/edits after the loop
        if (stepToDelete is not null)
        {
            draft.RemoveStep(stepToDelete, DateTimeOffset.UtcNow);
            _host.DraftManager.MarkDirty(target.Value);
            _ = _host.DraftManager.SaveNow(target.Value, CancellationToken.None);
        }

        if (stepToEdit is not null)
            _editModal.OpenFor(stepToEdit);
    }
}
