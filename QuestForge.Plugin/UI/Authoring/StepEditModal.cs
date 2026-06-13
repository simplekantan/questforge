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
    private string _saveStatus = "";

    // Inline edit state — only one field editable at a time
    private string? _editingField;
    private string _editValue = "";

    // Callback for when the user wants to pick a fragment (e.g. for resumePointFragmentId)
    public Action? OnRequestFragmentPicker { get; set; }

    public StepEditModal(AuthoringHost host)
        : base("QuestForge — Edit Step", ImGuiWindowFlags.None)
    {
        _host = host;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(480, 400),
            MaximumSize = new System.Numerics.Vector2(750, 800)
        };
    }

    public void OpenFor(DraftStep step)
    {
        _editingStep = step;
        _stepId = step.StepId;
        _stepType = step.StepType;
        _sequenceNumber = step.SequenceNumber.ToString();
        _suggestedExpect = step.SuggestedExpect ?? "";
        RefreshRawJson();
        _editingField = null;
        _editValue = "";
        _saveStatus = "";
        IsOpen = true;
    }

    public override void Draw()
    {
        if (_editingStep is null) { IsOpen = false; return; }

        var isEditable = _host.Mode == AuthoringMode.Author;

        ImGui.TextUnformatted("Edit Step");
        ImGui.Separator();

        // ---- Base Step fields ----
        DrawEditableString("id", "Step ID", _stepId, isEditable,
            "Unique identifier for this step within the quest/fragment.");
        DrawReadOnly("Step Type", _stepType);
        DrawEditableString("sequence", "Sequence", _sequenceNumber, isEditable,
            "Sequence block number (e.g. 0, 1, 255). Determines which quest sequence block this step belongs to.");

        ImGui.Spacing();
        DrawPredicateField("expect", "Expect", _suggestedExpect, isEditable,
            "Postcondition predicate. Step completes when this is true.");
        DrawPredicateField("skipIf", "SkipIf", ReadRawString("skipIf") ?? "", isEditable,
            "Predicate expression. If true, this step is skipped.");
        DrawEditableString("notes", "Notes", ReadRawString("notes"), isEditable,
            "Author notes. Not used by the engine.");
        DrawEditableFloat("stopDistance", "Stop Distance", ReadRawFloat("stopDistance"), isEditable,
            "Navigation stopping distance in yalms. Null = step-type default.");
        DrawEditableString("zone", "Zone", ReadRawString("zone"), isEditable,
            "Expected zone after this step completes. Used for step grouping.");
        DrawEditableString("requiredZone", "Required Zone", ReadRawString("requiredZone"), isEditable,
            "Zone the player must be in before this step can start.\nUsed for cold-resume: if the player logs in at a different zone,\nthe engine navigates here first.");
        DrawResumePointFragmentField(isEditable);

        // ---- Per-type fields ----
        var raw = _editingStep.Raw;
        if (raw is not null)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Type-Specific Fields");
            ImGui.Separator();
            DrawPerTypeFields(raw, isEditable);
        }

        // ---- Raw JSON preview ----
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Raw JSON"))
            ImGui.TextUnformatted(_rawJsonDisplay);

        // ---- Status + buttons ----
        if (_saveStatus.Length > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.2f, 1f, 0.2f, 1f));
            ImGui.TextUnformatted(_saveStatus);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        if (isEditable && _recordModal is not null)
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

    // ---- Per-type field dispatch ----

    private void DrawPerTypeFields(Step raw, bool editable)
    {
        switch (raw)
        {
            case UseEmoteStep:
                DrawEditableUint("emoteId", "Emote ID", editable);
                DrawEditableNullableUint("targetNpcId", "Target NPC ID", editable);
                DrawEditableBool("motion", "Motion", editable, "Play the emote animation (true) or text-only (false).");
                break;
            case UseActionStep:
                DrawEditableEnum<ActionType>("actionType", "Action Type", editable);
                DrawEditableUint("actionId", "Action ID", editable);
                DrawEditableNullableUint("targetNpcId", "Target NPC ID", editable);
                break;
            case UseItemStep:
                DrawEditableEnum<ItemKind>("kind", "Item Kind", editable);
                DrawEditableUint("itemId", "Item ID", editable);
                DrawEditableNullableUint("targetNpcId", "Target NPC ID", editable);
                break;
            case ChangeJobStep:
                DrawEditableUint("jobId", "Job ID", editable);
                break;
            case InteractObjectStep:
                DrawEditableUint("interactableId", "Interactable ID", editable);
                break;
            case PickupItemStep:
                DrawEditableUint("interactableId", "Interactable ID", editable);
                break;
            case SayChatMessageStep:
                DrawEditableString("message", "Message", ReadRawString("message"), editable, null);
                DrawEditableNullableUint("targetNpcId", "Target NPC ID", editable);
                break;
            case TeleportStep:
                DrawEditableUint("aetheryteId", "Aetheryte ID", editable);
                break;
            case AethernetStep:
                DrawEditableUint("from", "From Shard ID", editable);
                DrawEditableUint("to", "To Shard ID", editable);
                break;
            case AttunementStep:
                DrawEditableUint("target", "Aetheryte/Shard ID", editable);
                break;
            case PurchaseItemStep:
                DrawEditableUint("itemId", "Item ID", editable);
                DrawEditableUint("quantity", "Quantity", editable);
                DrawEditableEnum<PurchaseCurrency>("currency", "Currency", editable);
                DrawEditableNullableInt("gcCategory", "GC Category", editable, "0=Weapons, 1=Armor, 2=Materiel, 3=Materials");
                DrawEditableNullableInt("gcRankTier", "GC Rank Tier", editable, "0=lowest, 2=highest");
                DrawEditableNullableInt("vendorCategory", "Vendor Category", editable, "SelectIconString index (0-based)");
                break;
            case WaitStep:
                DrawEditableFloat("seconds", "Seconds", ReadRawFloat("seconds"), editable, null);
                break;
            case DutyStep:
                DrawEditableStringChoice("kind", "Duty Kind", ReadRawString("kind"), editable, ["spd", "duty"]);
                DrawEditableNullableUint("contentFinderConditionId", "CFC ID", editable);
                DrawEditableNullableUint("entryTargetId", "Entry Target ID", editable);
                break;
            case CutsceneStep:
                DrawEditableString("skip", "Skip", ReadRawString("skip"), editable, "never | ifAllowed");
                break;
            case CombatStep:
                DrawEditableEnum<CombatSpawn>("spawn", "Spawn Type", editable);
                break;
            case MinigameStep:
                DrawEditableString("kind", "Kind", ReadRawString("kind"), editable, "sniping | memory | aiming | rhythm | selection | other");
                DrawEditableString("skip", "Skip", ReadRawString("skip"), editable, "never | ifAllowed | always");
                break;
            case AwaitUserStep:
                DrawEditableString("reason", "Reason", ReadRawString("reason"), editable, null);
                break;
            case TravelStep:
                DrawEditableBool("useMount", "Use Mount", editable, "Null/true = engine decides. False = no mount.");
                break;
            case UseItemOnObjectStep:
                DrawEditableUint("interactableId", "Interactable ID", editable);
                DrawEditableEnum<ItemKind>("kind", "Item Kind", editable);
                DrawEditableUint("itemId", "Item ID", editable);
                break;
            case DungeonTrialStep:
                DrawEditableUint("contentFinderConditionId", "CFC ID", editable);
                break;
            case SinglePlayerDutyStep:
                DrawEditableUint("contentFinderConditionId", "CFC ID", editable);
                DrawEditableEnum<SpdEntryKind>("entryKind", "Entry Kind", editable);
                DrawEditableNullableUint("entryTargetId", "Entry Target ID", editable);
                break;
            // TalkStep, AcceptStep, TurnInStep, HandOverItemStep, EquipGearForQuestStep,
            // FragmentStep, BranchStep, EquipBestGearStep, RegisterGearsetStep, OpenCoffersStep
            // have complex or no per-type fields — handled by Re-record or future Phase 3
        }
    }

    // ---- Field rendering helpers ----

    private void DrawReadOnly(string label, string value)
    {
        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    private void DrawPredicateField(string jsonKey, string label, string currentValue, bool editable, string? tooltip)
    {
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SetNextItemWidth(200f);
            if (ImGui.BeginCombo($"##{jsonKey}pick", "Pick predicate..."))
            {
                foreach (var entry in PredicateOptions.Build(_host.CurrentSnapshot))
                {
                    if (ImGui.Selectable(entry.Option))
                        _editValue = entry.Option == "(none)" ? "" : entry.Option;
                    if (entry.Tooltip is not null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(entry.Tooltip);
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(280f);
            ImGui.InputText($"##{jsonKey}", ref _editValue, 256);
            DrawSaveCancelButtons(jsonKey, _editValue.Length > 0 ? _editValue : null);
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(currentValue.Length > 0 ? currentValue : "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = currentValue;
                }
            }
            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawResumePointFragmentField(bool editable)
    {
        var current = ReadRawString("resumePointFragmentId");
        ImGui.TextUnformatted("Resume Fragment:");
        ImGui.SameLine();
        ImGui.TextUnformatted(current ?? "(none)");
        if (editable)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit##resumeFragment"))
                OnRequestFragmentPicker?.Invoke();
            if (current is not null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear##resumeFragment"))
                    ApplyResumePointFragment(null);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fragment to run when cold-resuming at this step.\nPick from available fragments on disk.");
    }

    public void ApplyResumePointFragment(string? fragmentId)
    {
        if (_editingStep is null) return;
        ApplyFieldChange("resumePointFragmentId", fragmentId);
        _saveStatus = fragmentId is not null
            ? $"Resume fragment set to {fragmentId}."
            : "Resume fragment cleared.";
    }

    private void DrawEditableString(string jsonKey, string label, string? currentValue, bool editable, string? tooltip)
    {
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(300f);
            ImGui.InputText($"##{jsonKey}", ref _editValue, 512);
            DrawSaveCancelButtons(jsonKey, _editValue.Length > 0 ? _editValue : null);
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(currentValue ?? "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = currentValue ?? "";
                }
            }
            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawEditableUint(string jsonKey, string label, bool editable)
    {
        var current = ReadRawUint(jsonKey);
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            ImGui.InputText($"##{jsonKey}", ref _editValue, 20);
            if (uint.TryParse(_editValue, out _))
                DrawSaveCancelButtons(jsonKey, _editValue);
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted("(invalid)");
                DrawCancelButton(jsonKey);
            }
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(current?.ToString() ?? "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = current?.ToString() ?? "";
                }
            }
        }
    }

    private void DrawEditableNullableUint(string jsonKey, string label, bool editable)
    {
        var current = ReadRawUint(jsonKey);
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            ImGui.InputText($"##{jsonKey}", ref _editValue, 20);
            if (_editValue.Length == 0)
                DrawSaveCancelButtons(jsonKey, null);
            else if (uint.TryParse(_editValue, out _))
                DrawSaveCancelButtons(jsonKey, _editValue);
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted("(invalid — clear to remove)");
                DrawCancelButton(jsonKey);
            }
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(current?.ToString() ?? "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = current?.ToString() ?? "";
                }
            }
        }
    }

    private void DrawEditableNullableInt(string jsonKey, string label, bool editable, string? tooltip)
    {
        var current = ReadRawInt(jsonKey);
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.InputText($"##{jsonKey}", ref _editValue, 20);
            if (_editValue.Length == 0)
                DrawSaveCancelButtons(jsonKey, null);
            else if (int.TryParse(_editValue, out _))
                DrawSaveCancelButtons(jsonKey, _editValue);
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted("(invalid)");
                DrawCancelButton(jsonKey);
            }
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(current?.ToString() ?? "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = current?.ToString() ?? "";
                }
            }
            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawEditableFloat(string jsonKey, string label, float? currentValue, bool editable, string? tooltip)
    {
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.InputText($"##{jsonKey}", ref _editValue, 20);
            if (_editValue.Length == 0)
                DrawSaveCancelButtons(jsonKey, null);
            else if (float.TryParse(_editValue, out _))
                DrawSaveCancelButtons(jsonKey, _editValue);
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted("(invalid)");
                DrawCancelButton(jsonKey);
            }
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(currentValue?.ToString("F1") ?? "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = currentValue?.ToString("F1") ?? "";
                }
            }
            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawEditableBool(string jsonKey, string label, bool editable, string? tooltip)
    {
        var current = ReadRawBool(jsonKey);
        if (editable)
        {
            var val = current ?? true;
            if (ImGui.Checkbox($"{label}##{jsonKey}", ref val))
                ApplyFieldChange(jsonKey, val ? "true" : "false");
            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(current?.ToString() ?? "(default)");
        }
    }

    private void DrawEditableEnum<T>(string jsonKey, string label, bool editable) where T : struct, Enum
    {
        var current = ReadRawString(jsonKey) ?? "";
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160f);
            if (ImGui.BeginCombo($"##{jsonKey}", _editValue))
            {
                foreach (var val in Enum.GetNames<T>())
                {
                    var camel = char.ToLowerInvariant(val[0]) + val[1..];
                    if (ImGui.Selectable(camel, camel == _editValue))
                    {
                        _editValue = camel;
                        ApplyFieldChange(jsonKey, camel);
                        _editingField = null;
                    }
                }
                ImGui.EndCombo();
            }
            DrawCancelButton(jsonKey);
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(current.Length > 0 ? current : "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = current;
                }
            }
        }
    }

    private void DrawEditableStringChoice(string jsonKey, string label, string? currentValue, bool editable, string[] options)
    {
        var current = currentValue ?? "";
        if (_editingField == jsonKey)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160f);
            if (ImGui.BeginCombo($"##{jsonKey}", _editValue))
            {
                foreach (var opt in options)
                {
                    if (ImGui.Selectable(opt, opt == _editValue))
                    {
                        _editValue = opt;
                        ApplyFieldChange(jsonKey, opt);
                        _editingField = null;
                    }
                }
                ImGui.EndCombo();
            }
            DrawCancelButton(jsonKey);
        }
        else
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(current.Length > 0 ? current : "(none)");
            if (editable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Edit##{jsonKey}"))
                {
                    _editingField = jsonKey;
                    _editValue = current;
                }
            }
        }
    }

    // ---- Save/Cancel buttons ----

    private void DrawSaveCancelButtons(string jsonKey, string? newValue)
    {
        ImGui.SameLine();
        if (ImGui.SmallButton($"Save##{jsonKey}"))
        {
            ApplyFieldChange(jsonKey, newValue);
            _editingField = null;
        }
        DrawCancelButton(jsonKey);
    }

    private void DrawCancelButton(string jsonKey)
    {
        ImGui.SameLine();
        if (ImGui.SmallButton($"Cancel##{jsonKey}"))
            _editingField = null;
    }

    // ---- Apply changes ----

    private void ApplyFieldChange(string jsonKey, string? newValue)
    {
        if (_editingStep is null) return;

        // Special case: Step ID change requires draft-level rename
        if (jsonKey == "id" && newValue is not null && newValue != _editingStep.StepId)
        {
            ApplyStepIdChange(newValue);
            return;
        }

        // Special case: Sequence number is a DraftStep property, not a raw JSON field
        if (jsonKey == "sequence" && newValue is not null && int.TryParse(newValue, out var newSeq))
        {
            var seqUpdated = _editingStep with { SequenceNumber = newSeq };
            SaveUpdatedStep(seqUpdated);
            _sequenceNumber = newSeq.ToString();
            _saveStatus = "Sequence updated.";
            return;
        }

        var updatedRaw = PatchRawField(_editingStep.Raw, jsonKey, newValue);
        var updatedExpect = jsonKey == "expect" ? newValue : _editingStep.SuggestedExpect;
        var updatedStep = _editingStep with { SuggestedExpect = updatedExpect, Raw = updatedRaw };

        SaveUpdatedStep(updatedStep);
        _saveStatus = $"{jsonKey} updated.";
    }

    private void ApplyStepIdChange(string newId)
    {
        if (_editingStep is null) return;

        var updatedRaw = PatchRawField(_editingStep.Raw, "id", newId);
        var updatedStep = _editingStep with { StepId = newId, Raw = updatedRaw };

        // Draft uses old StepId to find and replace
        SaveUpdatedStep(updatedStep);
        _stepId = newId;
        _saveStatus = "Step ID updated.";
    }

    private void SaveUpdatedStep(DraftStep updatedStep)
    {
        if (_editingStep is null) return;

        var isFragment = _host.IsFragmentMode;
        if (isFragment && _host.FragmentTarget is { } fragTarget)
        {
            var draft = _host.FragmentDraftManager.Get(fragTarget, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (draft is null) return;
            draft.ReplaceStep(_editingStep.StepId, updatedStep, DateTimeOffset.UtcNow);
            _host.FragmentDraftManager.MarkDirty(fragTarget);
            _ = _host.FragmentDraftManager.SaveNow(fragTarget, CancellationToken.None);
        }
        else if (_host.AuthoringTarget is { } questTarget)
        {
            var draft = _host.DraftManager.Get(questTarget, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (draft is null) return;
            draft.ReplaceStep(_editingStep.StepId, updatedStep, DateTimeOffset.UtcNow);
            _host.DraftManager.MarkDirty(questTarget);
            _ = _host.DraftManager.SaveNow(questTarget, CancellationToken.None);
        }

        _editingStep = updatedStep;
        _suggestedExpect = updatedStep.SuggestedExpect ?? "";
        RefreshRawJson();
    }

    // ---- JSON helpers ----

    private static Step? PatchRawField(Step? raw, string jsonKey, string? newValue)
    {
        if (raw is null) return null;

        var json = JsonSerializer.Serialize<Step>(raw, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json);
        if (node is null) return raw;

        if (newValue is null)
        {
            node.AsObject().Remove(jsonKey);
        }
        else if (uint.TryParse(newValue, out var uintVal) && jsonKey is not "zone" and not "requiredZone"
                 and not "id" and not "notes" and not "skipIf" and not "reason" and not "message"
                 and not "kind" and not "skip" and not "expect")
        {
            node[jsonKey] = uintVal;
        }
        else if (float.TryParse(newValue, out var floatVal) && jsonKey is "stopDistance" or "seconds")
        {
            node[jsonKey] = floatVal;
        }
        else if (newValue is "true" or "false" && jsonKey is "motion" or "useMount")
        {
            node[jsonKey] = newValue == "true";
        }
        else
        {
            node[jsonKey] = newValue;
        }

        return JsonSerializer.Deserialize<Step>(node.ToJsonString(), QuestForgeJsonContext.QuestFileOptions) ?? raw;
    }

    private string? ReadRawString(string key)
    {
        if (_editingStep?.Raw is null) return null;
        var json = JsonSerializer.Serialize<Step>(_editingStep.Raw, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json);
        return node?[key]?.GetValue<string>();
    }

    private uint? ReadRawUint(string key)
    {
        if (_editingStep?.Raw is null) return null;
        var json = JsonSerializer.Serialize<Step>(_editingStep.Raw, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json);
        return node?[key]?.GetValue<uint>();
    }

    private int? ReadRawInt(string key)
    {
        if (_editingStep?.Raw is null) return null;
        var json = JsonSerializer.Serialize<Step>(_editingStep.Raw, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json);
        return node?[key]?.GetValue<int>();
    }

    private float? ReadRawFloat(string key)
    {
        if (_editingStep?.Raw is null) return null;
        var json = JsonSerializer.Serialize<Step>(_editingStep.Raw, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json);
        try { return node?[key]?.GetValue<float>(); }
        catch { return null; }
    }

    private bool? ReadRawBool(string key)
    {
        if (_editingStep?.Raw is null) return null;
        var json = JsonSerializer.Serialize<Step>(_editingStep.Raw, QuestForgeJsonContext.QuestFileOptions);
        var node = JsonNode.Parse(json);
        try { return node?[key]?.GetValue<bool>(); }
        catch { return null; }
    }

    private void RefreshRawJson()
    {
        _rawJsonDisplay = _editingStep?.Raw is not null
            ? JsonSerializer.Serialize(_editingStep.Raw, QuestForgeJsonContext.QuestFileOptions)
            : "(none)";
    }

    public void SetRecordModal(RecordStepModal modal) => _recordModal = modal;
}
