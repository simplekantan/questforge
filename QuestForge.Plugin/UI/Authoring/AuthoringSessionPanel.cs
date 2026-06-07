using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

public sealed partial class AuthoringSessionPanel : Window
{
    private readonly AuthoringHost _host;
    private readonly RecordStepModal _recordModal;
    private readonly StepEditModal _editModal;
    private readonly ExportDialog _exportDialog;
    private readonly IDalamudPluginInterface _pi;

    // Validation result display
    private List<DraftValidationError> _lastErrors = new();
    private List<DraftValidationWarning> _lastWarnings = new();
    private bool _validationRan;
    private readonly DraftValidator _validator = new();

    // Fragment authoring modal state
    private bool _fragmentModalOpen;
    private string _fragmentIdInput = "";
    private string? _fragmentModalWarning;
    private string? _fragmentModalError;

    public AuthoringSessionPanel(AuthoringHost host,
        RecordStepModal recordModal, StepEditModal editModal, ExportDialog exportDialog,
        IDalamudPluginInterface pi)
        : base("QuestForge — Authoring Session", ImGuiWindowFlags.None)
    {
        _host = host;
        _recordModal = recordModal;
        _editModal = editModal;
        _exportDialog = exportDialog;
        _pi = pi;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(420, 300),
            MaximumSize = new System.Numerics.Vector2(800, 700)
        };
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"Mode: {_host.Mode}");
        if (_host.IsFragmentMode)
            ImGui.TextUnformatted($"Fragment: {_host.FragmentTarget ?? "(none)"}");
        else
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
                if (_host.IsFragmentMode)
                    DrawFragmentAuthorMode();
                else
                    DrawAuthorMode();
                break;
        }

    }

    private static readonly Regex FragmentIdRegex = new(@"^[a-z][a-z0-9/\-]*[a-z0-9]$", RegexOptions.Compiled);

    private void DrawOffMode()
    {
        ImGui.TextUnformatted("Not in authoring mode.");
        ImGui.TextUnformatted("Use /qf inspect or /qf author <questId> or /qf author-fragment <id> to begin.");
        ImGui.Spacing();
        if (ImGui.Button("Author Fragment"))
        {
            _fragmentModalOpen = true;
            _fragmentIdInput = "";
            _fragmentModalWarning = null;
            _fragmentModalError = null;
        }
        DrawFragmentIdModal();
    }

    private void DrawFragmentIdModal()
    {
        if (!_fragmentModalOpen) return;

        ImGui.OpenPopup("Author Fragment");
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));

        if (ImGui.BeginPopupModal("Author Fragment", ref _fragmentModalOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Enter a unique fragment ID:");
            ImGui.TextUnformatted("(e.g., travel/teleport-to-gridania)");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(300f);
            ImGui.InputText("##fragmentId", ref _fragmentIdInput, 128);

            var id = _fragmentIdInput.Trim();
            var validFormat = id.Length >= 3
                && FragmentIdRegex.IsMatch(id)
                && !id.Contains("//");

            // Check for existing fragment file
            _fragmentModalWarning = null;
            _fragmentModalError = null;
            if (validFormat)
            {
                var existingPath = Path.Combine(_pi.GetPluginConfigDirectory(), "fragments", $"{id.Replace('/', Path.DirectorySeparatorChar)}.json");
                if (File.Exists(existingPath))
                    _fragmentModalWarning = $"Fragment '{id}' already exists. Exporting will overwrite.";

                var existingDraft = _host.FragmentDraftManager.Get(id, CancellationToken.None).GetAwaiter().GetResult();
                if (existingDraft is not null)
                    _fragmentModalWarning = $"Draft for '{id}' exists ({existingDraft.Steps.Count} steps). Will resume.";
            }
            else if (id.Length > 0)
            {
                _fragmentModalError = "Invalid format: lowercase letters, digits, hyphens, slashes. Min 3 chars.";
            }

            if (_fragmentModalError is not null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f));
                ImGui.TextUnformatted(_fragmentModalError);
                ImGui.PopStyleColor();
            }
            if (_fragmentModalWarning is not null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f));
                ImGui.TextUnformatted(_fragmentModalWarning);
                ImGui.PopStyleColor();
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(!validFormat);
            if (ImGui.Button("Start", new System.Numerics.Vector2(120, 0)))
            {
                _host.EnterFragmentAuthorMode(id);
                _fragmentModalOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new System.Numerics.Vector2(120, 0)))
            {
                _fragmentModalOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
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
        string? stepToMoveUp = null;
        string? stepToMoveDown = null;
        DraftStep? stepToEdit = null;
        DraftStep? stepToDuplicate = null;

        var sequences = steps
            .Select((s, i) => (Step: s, GlobalIndex: i))
            .GroupBy(x => x.Step.SequenceNumber)
            .OrderBy(g => g.Key);

        foreach (var seqGroup in sequences)
        {
            if (ImGui.CollapsingHeader($"Sequence {seqGroup.Key} ({seqGroup.Count()} steps)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var stepsInSeq = seqGroup.ToList();
                for (var local = 0; local < stepsInSeq.Count; local++)
                {
                    var (step, globalIndex) = stepsInSeq[local];
                    ImGui.PushID(globalIndex);

                    ImGui.TextUnformatted($"  [{local + 1}] {step.StepType} | {step.StepId}");

                    ImGui.SameLine();
                    ImGui.BeginDisabled(local == 0);
                    if (ImGui.SmallButton("▲"))
                        stepToMoveUp = step.StepId;
                    ImGui.EndDisabled();

                    ImGui.SameLine();
                    ImGui.BeginDisabled(local == stepsInSeq.Count - 1);
                    if (ImGui.SmallButton("▼"))
                        stepToMoveDown = step.StepId;
                    ImGui.EndDisabled();

                    ImGui.SameLine();
                    if (ImGui.SmallButton("edit"))
                        stepToEdit = step;

                    ImGui.SameLine();
                    if (ImGui.SmallButton("duplicate"))
                        stepToDuplicate = step;

                    ImGui.SameLine();
                    if (ImGui.SmallButton("delete"))
                        stepToDelete = step.StepId;

                    ImGui.PopID();
                }
            }
        }

        var dirty = false;

        if (stepToMoveUp is not null)
            dirty = draft.MoveStepUp(stepToMoveUp, DateTimeOffset.UtcNow);

        if (stepToMoveDown is not null)
            dirty = draft.MoveStepDown(stepToMoveDown, DateTimeOffset.UtcNow);

        if (stepToDuplicate is not null)
        {
            var newId = stepToDuplicate.StepId + "-copy";
            var dupe = stepToDuplicate with { StepId = newId };
            draft.InsertStepAfter(stepToDuplicate.StepId, dupe, DateTimeOffset.UtcNow);
            dirty = true;
        }

        if (stepToDelete is not null)
        {
            draft.RemoveStep(stepToDelete, DateTimeOffset.UtcNow);
            dirty = true;
        }

        if (dirty)
        {
            _host.DraftManager.MarkDirty(target.Value);
            _ = _host.DraftManager.SaveNow(target.Value, CancellationToken.None);
        }

        if (stepToEdit is not null)
            _editModal.OpenFor(stepToEdit);
    }

    private void DrawFragmentAuthorMode()
    {
        var fragmentTarget = _host.FragmentTarget;
        if (fragmentTarget is null) return;

        // Controls row
        if (ImGui.Button("+ Record Next Step"))
        {
            _recordModal.Open();
            _recordModal.IsOpen = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Validate"))
        {
            var fragmentDraft = _host.FragmentDraftManager.Get(fragmentTarget, CancellationToken.None).GetAwaiter().GetResult();
            if (fragmentDraft is not null)
            {
                var (errors, warnings) = _validator.ValidateFragment(fragmentDraft);
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

        // Flat steps list (no sequence grouping for fragments)
        var draft = _host.FragmentDraftManager.Get(fragmentTarget, CancellationToken.None).GetAwaiter().GetResult();
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
        string? stepToMoveUp = null;
        string? stepToMoveDown = null;
        DraftStep? stepToEdit = null;
        DraftStep? stepToDuplicate = null;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            ImGui.PushID(i);

            ImGui.TextUnformatted($"  [{i + 1}] {step.StepType} | {step.StepId}");

            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            if (ImGui.SmallButton("▲"))
                stepToMoveUp = step.StepId;
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(i == steps.Count - 1);
            if (ImGui.SmallButton("▼"))
                stepToMoveDown = step.StepId;
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.SmallButton("edit"))
                stepToEdit = step;

            ImGui.SameLine();
            if (ImGui.SmallButton("duplicate"))
                stepToDuplicate = step;

            ImGui.SameLine();
            if (ImGui.SmallButton("delete"))
                stepToDelete = step.StepId;

            ImGui.PopID();
        }

        var dirty = false;

        if (stepToMoveUp is not null)
            dirty = draft.MoveStepUp(stepToMoveUp, DateTimeOffset.UtcNow);

        if (stepToMoveDown is not null)
            dirty = draft.MoveStepDown(stepToMoveDown, DateTimeOffset.UtcNow);

        if (stepToDuplicate is not null)
        {
            var newId = stepToDuplicate.StepId + "-copy";
            var dupe = stepToDuplicate with { StepId = newId };
            draft.InsertStepAfter(stepToDuplicate.StepId, dupe, DateTimeOffset.UtcNow);
            dirty = true;
        }

        if (stepToDelete is not null)
        {
            draft.RemoveStep(stepToDelete, DateTimeOffset.UtcNow);
            dirty = true;
        }

        if (dirty)
        {
            _host.FragmentDraftManager.MarkDirty(fragmentTarget);
            _ = _host.FragmentDraftManager.SaveNow(fragmentTarget, CancellationToken.None);
        }

        if (stepToEdit is not null)
            _editModal.OpenFor(stepToEdit);
    }
}
