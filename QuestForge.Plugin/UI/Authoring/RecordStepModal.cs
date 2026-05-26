using System.Linq;
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

    // Step type picker: empty = <Detect> (run inference), non-empty = author override
    private string _overrideStepType = "";
    private static readonly string[] StepTypeOptions =
    [
        "<Detect>", "travel", "talk", "accept", "turn-in", "attune",
        "hand-over-item", "pickup-item", "interact-object", "cutscene",
        "use-item", "use-emote", "use-action", "await-user", "combat"
    ];

    // Combat step: comma-separated DataIds entered by the author (manual override)
    private string _editKillEnemyDataIds = "";

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
        _overrideStepType = "";
        _editStepId = "";
        _editExpect = "";
        _editNotes = "";
        _editKillEnemyDataIds = "";
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

        // Step type override — author can specify the type upfront instead of relying on inference
        ImGui.TextUnformatted("Step type:");
        ImGui.SetNextItemWidth(180f);
        var displayType = _overrideStepType.Length > 0 ? _overrideStepType : "<Detect>";
        if (ImGui.BeginCombo("##steptypepick", displayType))
        {
            foreach (var option in StepTypeOptions)
            {
                var selected = option == displayType;
                if (ImGui.Selectable(option, selected))
                    _overrideStepType = option == "<Detect>" ? "" : option;
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Perform your in-game action, then click Record.");
        ImGui.TextUnformatted($"Before: zone={before.Zone.Value}, seq={before.QuestSequence}");
        ImGui.TextUnformatted($"Captured at: {before.CapturedAt:HH:mm:ss.fff}");
        ImGui.Spacing();

        if (ImGui.Button("Record"))
        {
            _after = _host.CurrentSnapshot;

            if (_overrideStepType.Length > 0)
            {
                // Author specified type: build a minimal inference result using the override
                _inference = new InferenceResult(
                    StepType: _overrideStepType,
                    SuggestedStepId: DefaultStepIdForType(_overrideStepType),
                    SuggestedExpect: null,
                    Confidence: QuestForge.Engine.Authoring.Confidence.High,
                    InferredFrom: InferredFrom.Manual,
                    Notes: null);
            }
            else
            {
                _inference = _host.PreviewInference(_before!);
            }

            _editStepId = _inference.SuggestedStepId;
            _editExpect = _inference.SuggestedExpect ?? "";
            _editNotes = _inference.Notes ?? "";
            // Seed the kill-set from the detected targets so an auto-detected combat step carries
            // its DataIds into KillEnemyDataIds (the field is the only source BuildRawStep reads).
            _editKillEnemyDataIds = _inference.StepType == "combat" && _after?.KillCorrelatedTargets is { } kct
                ? string.Join(",", kct.Values.SelectMany(t => t.DataIds).Distinct().OrderBy(id => id))
                : "";
            _state = RecordState.InferenceReady;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ResetAndClose();
    }

    private static string DefaultStepIdForType(string stepType) => stepType switch
    {
        "travel"         => "travel-step",
        "talk"           => "talk-step",
        "accept"         => "accept-quest",
        "turn-in"        => "turn-in-quest",
        "attune"         => "attune-aetheryte",
        "hand-over-item" => "hand-over-item",
        "pickup-item"    => "pickup-item",
        "interact-object"=> "interact-object",
        "cutscene"       => "cutscene",
        "combat"         => "combat-step",
        _                => "step"
    };

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

        if (_inference!.StepType == "combat")
        {
            ImGui.TextUnformatted("Kill Enemy DataIds (comma-separated uints):");
            ImGui.SetNextItemWidth(300f);
            ImGui.InputText("##killdataids", ref _editKillEnemyDataIds, 256);
        }

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
            var rawStep = BuildRawStep(stepId, _inference!.StepType, expectStr, _after, _before);

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

    private Step BuildRawStep(string stepId, string stepType, string? expect, GameStateSnapshot? after, GameStateSnapshot? before = null)
    {
        if (stepType == "combat")
        {
            var dataIds = _editKillEnemyDataIds
                .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .Select(s => uint.TryParse(s, out var v) ? (uint?)v : null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray();

            ExpectValue? expectValue = expect is { Length: > 0 }
                ? new PredicateExpect { Predicate = expect }
                : null;

            var zone = (int)(after?.Zone.Value ?? 0);
            var zoneStr = zone > 0 ? zone.ToString() : null;
            var combatPos = after?.CombatStartPosition is { } csp
                ? new Position3(csp.X, csp.Y, csp.Z)
                : new Position3(after?.Position.X ?? 0, after?.Position.Y ?? 0, after?.Position.Z ?? 0);
            var combatZone = after?.CombatStartZone > 0 ? after.CombatStartZone : zone;

            var stepIdResolved = dataIds.Length > 0 ? $"defeat-{dataIds[0]}" : stepId;
            if (_editStepId.Length > 0 && _editStepId != DefaultStepIdForType("combat"))
                stepIdResolved = stepId;

            return new CombatStep
            {
                Id = stepIdResolved,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = zoneStr,
                KillEnemyDataIds = dataIds,
                Spawn = CombatSpawn.OverworldEnemies,
                Location = new NpcLocation(NpcId: 0, Zone: combatZone, Position: combatPos)
            };
        }

        return QuestForge.Engine.Authoring.StepFactory.Build(stepType, stepId, expect, after, before);
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
            $"questVariable({questId}, 0)",
            $"questVariableLow({questId}, 0) >= 3",
            $"questVariableHigh({questId}, 1) >= 3",
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
        _overrideStepType = "";
        _editKillEnemyDataIds = "";
        _state = RecordState.WaitingForAction;
        _saveTask = null;
        _saveError = "";
        IsOpen = false;
    }
}
