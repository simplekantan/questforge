using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;
using QuestForge.Schema;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class RecordStepModal : Window
{
    private readonly AuthoringHost _host;

    // State machine
    private enum RecordState { WaitingForAction, InferenceReady }
    private RecordState _state = RecordState.WaitingForAction;

    private GameStateSnapshot? _before;
    private GameStateSnapshot? _after;
    private InferenceResult? _inference;

    // Editable fields shown after inference
    private string _editStepId = "";
    private string _editExpect = "";
    private string _editNotes = "";

    // Async save tracking
    private Task? _saveTask;
    private string _saveError = "";

    public RecordStepModal(AuthoringHost host)
        : base("QuestForge — Record Step", ImGuiWindowFlags.None)
    {
        _host = host;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(380, 260),
            MaximumSize = new System.Numerics.Vector2(600, 500)
        };
    }

    public void Open()
    {
        _before = _host.OpenRecordModal();
        _state = RecordState.WaitingForAction;
        _inference = null;
        _editStepId = "";
        _editExpect = "";
        _editNotes = "";
        _saveError = "";
        IsOpen = true;
    }

    public override void Draw()
    {
        if (_before is null) { IsOpen = false; return; }

        switch (_state)
        {
            case RecordState.WaitingForAction:
                DrawWaitingState();
                break;
            case RecordState.InferenceReady:
                DrawInferenceState();
                break;
        }
    }

    private void DrawWaitingState()
    {
        var before = _before!;
        ImGui.TextUnformatted("Perform your in-game action, then click Record.");
        ImGui.Separator();
        ImGui.TextUnformatted($"Before: zone={before.Zone.Value}, seq={before.QuestSequence}");
        ImGui.TextUnformatted($"Captured at: {before.CapturedAt:HH:mm:ss.fff}");
        ImGui.Spacing();

        if (ImGui.Button("Record"))
        {
            _after = _host.CurrentSnapshot;
            _inference = _host.PreviewInference(_before!);
            _editStepId = _inference.SuggestedStepId;
            _editExpect = _inference.SuggestedExpect ?? "";
            _editNotes = _inference.Notes ?? "";
            _state = RecordState.InferenceReady;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ResetAndClose();
    }

    private void DrawInferenceState()
    {
        if (_inference is null) { ResetAndClose(); return; }

        ImGui.TextUnformatted("Inferred step — review and confirm.");
        ImGui.Separator();

        ImGui.TextUnformatted($"Step Type: {(_inference.StepType.Length > 0 ? _inference.StepType : "(none — override below)")}");
        ImGui.TextUnformatted($"Confidence: {_inference.Confidence}");
        ImGui.TextUnformatted($"Inferred From: {_inference.InferredFrom}");

        if (_inference.Notes is { Length: > 0 } notes)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f));
            ImGui.TextUnformatted($"Note: {notes}");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Step ID:");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("##stepid", ref _editStepId, 128);

        ImGui.TextUnformatted("Expect (predicate):");
        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo("##predicatepick", "Pick predicate..."))
        {
            foreach (var option in BuildPredicateOptions())
            {
                if (ImGui.Selectable(option))
                    _editExpect = option == "(none)" ? "" : option;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280f);
        ImGui.InputText("##expect", ref _editExpect, 256);

        ImGui.TextUnformatted("Notes:");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("##notes", ref _editNotes, 512);

        ImGui.Spacing();

        // Wait for any in-progress save
        if (_saveTask is { IsCompleted: false })
        {
            ImGui.TextUnformatted("Saving...");
            return;
        }

        if (_saveError.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.2f, 0.2f, 1f));
            ImGui.TextUnformatted($"Error: {_saveError}");
            ImGui.PopStyleColor();
        }

        if (ImGui.Button("Confirm"))
        {
            var stepId = _editStepId.Length > 0 ? _editStepId : "step-unknown";
            var expectStr = _editExpect.Length > 0 ? _editExpect : null;
            var notesStr = _editNotes.Length > 0 ? _editNotes : null;

            // Build a minimal raw Step for confirmation — use the inferred type
            // For Phase 9 we build a TalkStep for "talk", TravelStep for "travel", etc.
            var rawStep = BuildRawStep(stepId, _inference!.StepType, expectStr, _after);

            _saveError = "";
            _saveTask = SaveAsync(_before!, _inference!, stepId, expectStr, notesStr, rawStep);
        }
        ImGui.SameLine();
        if (ImGui.Button("Back"))
        {
            _state = RecordState.WaitingForAction;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ResetAndClose();

        // Check if save completed
        if (_saveTask?.IsCompletedSuccessfully == true)
        {
            _saveTask = null;
            ResetAndClose();
        }
        else if (_saveTask?.IsFaulted == true)
        {
            _saveError = _saveTask.Exception?.InnerException?.Message ?? "Unknown error";
            _saveTask = null;
        }
    }

    private async Task SaveAsync(
        GameStateSnapshot before,
        InferenceResult inference,
        string stepId,
        string? expect,
        string? notes,
        Step rawStep)
    {
        await _host.RecordStep(before, inference, stepId, expect, notes, rawStep, CancellationToken.None);
    }

    private static Step BuildRawStep(string stepId, string stepType, string? expect, GameStateSnapshot? after)
    {
        QuestForge.Schema.ExpectValue? expectValue = expect is { Length: > 0 }
            ? new PredicateExpect { Predicate = expect }
            : null;

        // Extract position data from snapshot
        var zone = (int)(after?.Zone.Value ?? 0);
        var npcId = after?.LastNpcInteracted?.Value ?? 0u;
        var pos = after?.LastNpcPosition is { } p
            ? new Position3(p.X, p.Y, p.Z)
            : new Position3(0, 0, 0);
        var npcLoc = new NpcLocation(NpcId: npcId, Zone: zone, Position: pos);

        return stepType switch
        {
            "travel" => new TravelStep
            {
                Id = stepId,
                Expect = expectValue,
                Destination = new TravelDestination(Zone: zone, Position: pos)
            },
            "accept" => new AcceptStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = npcLoc
            },
            "turn-in" => new TurnInStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = npcLoc
            },
            "talk" => new TalkStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = npcLoc
            },
            "hand-over-item" => new HandOverItemStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = npcLoc,
                Item = after?.KeyItemsRemoved is { Count: > 0 } removed ? removed[0] : 0u
            },
            "attune" => new AttunementStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = new QuestForge.Schema.AetheryteId(after?.LastAttuned?.Value ?? 0u),
                Location = npcLoc
            },
            "pickup-item" => new PickupItemStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = new InteractableTarget(
                    InteractableId: npcId,
                    Zone: zone,
                    Position: pos)
            },
            "interact-object" => new InteractObjectStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = new InteractableTarget(
                    InteractableId: npcId,
                    Zone: zone,
                    Position: pos)
            },
            _ => new TalkStep
            {
                Id = stepId,
                Expect = expectValue,
                Target = npcLoc
            }
        };
    }

    private List<string> BuildPredicateOptions()
    {
        var snap = _host.CurrentSnapshot;
        var questId = snap.ActiveQuest?.Value ?? 0;
        var seq = snap.QuestSequence;
        var zone = snap.Zone.Value;

        var options = new List<string>
        {
            "(none)",
            $"isQuestAccepted({questId})",
            $"isQuestComplete({questId})",
            $"questSequence({questId}) >= {seq}",
            $"questFlag({questId}, 0)",
            $"questFlag({questId}, 1)",
            $"questFlag({questId}, 2)",
            $"questFlag({questId}, 3)",
            $"playerZone() == {zone}",
        };

        if (snap.LastAttuned.HasValue)
            options.Add($"isAttuned({snap.LastAttuned.Value.Value})");

        return options;
    }

    private void ResetAndClose()
    {
        _before = null;
        _after = null;
        _inference = null;
        _state = RecordState.WaitingForAction;
        _saveTask = null;
        _saveError = "";
        IsOpen = false;
    }
}
