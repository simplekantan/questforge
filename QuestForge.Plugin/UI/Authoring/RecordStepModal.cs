using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;
using QuestForge.Plugin.Tracing.Authoring;
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

    // Replace mode: when re-recording a step, this holds the original step ID
    private bool _replaceMode;
    private string _replaceOriginalStepId = "";
    private int _replaceSequenceNumber;

    // Step type picker: empty = <Detect> (run inference), non-empty = author override
    private string _overrideStepType = "";
    private static readonly string[] StepTypeOptions =
    [
        "<Detect>", "travel", "talk", "accept", "turn-in", "attune",
        "hand-over-item", "pickup-item", "interact-object", "cutscene",
        "use-item", "use-emote", "use-action", "say-chat-message",
        "equip-gear-for-quest", "equip-best-gear", "change-job",
        "register-gearset", "open-coffers", "teleport", "purchase-item",
        "wait", "duty", "combat", "await-user"
    ];

    // Combat step: comma-separated DataIds entered by the author (manual override)
    private string _editKillEnemyDataIds = "";

    // Purchase step: editable item/quantity/currency fields (mirroring the combat DataIds field)
    private string _editPurchaseItemId = "";
    private string _editPurchaseQuantity = "";
    private string _editPurchaseCurrency = "gil";
    private string _editPurchaseGcCategory = "";
    private string _editPurchaseGcRankTier = "";

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
        _replaceMode = false;
        _replaceOriginalStepId = "";
        _replaceSequenceNumber = 0;
        OpenInternal();
    }

    public void OpenForReplace(DraftStep originalStep)
    {
        _replaceMode = true;
        _replaceOriginalStepId = originalStep.StepId;
        _replaceSequenceNumber = originalStep.SequenceNumber;
        OpenInternal();
        _editStepId = originalStep.StepId;
    }

    private void OpenInternal()
    {
        _before = _host.OpenRecordModal();
        _state = RecordState.WaitingForAction;
        _inference = null;
        _overrideStepType = "";
        _editStepId = "";
        _editExpect = "";
        _editNotes = "";
        _editKillEnemyDataIds = "";
        _editPurchaseItemId = "";
        _editPurchaseQuantity = "";
        _editPurchaseCurrency = "gil";
        _editPurchaseGcCategory = "";
        _editPurchaseGcRankTier = "";
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

        if (_replaceMode)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f));
            ImGui.TextUnformatted($"Re-recording step: {_replaceOriginalStepId}");
            ImGui.PopStyleColor();
            ImGui.Separator();
        }

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

            // Seed purchase fields from the detected span so the author sees pre-filled values.
            if (_inference.StepType == "purchase-item" && _after?.PurchaseDetected is { } pd && pd.ItemDeltas.Count > 0)
            {
                var primary = pd.ItemDeltas
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key)
                    .First();
                _editPurchaseItemId = primary.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _editPurchaseQuantity = primary.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _editPurchaseCurrency = pd.GilDropped > 0 && pd.SealsDropped == 0 ? "gil"
                    : pd.SealsDropped > 0 && pd.GilDropped == 0 ? "gcSeals"
                    : pd.GilDropped >= pd.SealsDropped ? "gil"
                    : "gcSeals";
                var (gcCat, gcTier) = PurchaseModalSeeder.SeedGcAxes(pd);
                _editPurchaseGcCategory = gcCat;
                _editPurchaseGcRankTier = gcTier;
            }
            else
            {
                _editPurchaseItemId = "";
                _editPurchaseQuantity = "1";
                _editPurchaseCurrency = "gil";
                _editPurchaseGcCategory = "";
                _editPurchaseGcRankTier = "";
            }
            _state = RecordState.InferenceReady;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ResetAndClose();
    }

    private static string DefaultStepIdForType(string stepType) => stepType switch
    {
        "travel"              => "travel-step",
        "talk"                => "talk-step",
        "accept"              => "accept-quest",
        "turn-in"             => "turn-in-quest",
        "attune"              => "attune-aetheryte",
        "hand-over-item"      => "hand-over-item",
        "pickup-item"         => "pickup-item",
        "interact-object"     => "interact-object",
        "cutscene"            => "cutscene",
        "combat"              => "combat-step",
        "say-chat-message"    => "say-chat-message",
        "equip-gear-for-quest"=> "equip-gear",
        "equip-best-gear"     => "equip-best-gear",
        "change-job"          => "change-job",
        "register-gearset"    => "register-gearset",
        "open-coffers"        => "open-coffers",
        "teleport"            => "teleport-step",
        "purchase-item"       => "purchase-item",
        "wait"                => "wait-step",
        "duty"                => "duty-step",
        "await-user"          => "await-user",
        _                     => "step"
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

        if (_inference!.StepType == "purchase-item")
        {
            ImGui.TextUnformatted("Item ID:");
            ImGui.SetNextItemWidth(150f);
            ImGui.InputText("##purchaseitemid", ref _editPurchaseItemId, 32);
            ImGui.TextUnformatted("Quantity:");
            ImGui.SetNextItemWidth(80f);
            ImGui.InputText("##purchasequantity", ref _editPurchaseQuantity, 16);
            ImGui.TextUnformatted("Currency (gil / gcSeals):");
            ImGui.SetNextItemWidth(120f);
            ImGui.InputText("##purchasecurrency", ref _editPurchaseCurrency, 16);
            ImGui.TextUnformatted("GC Category:");
            ImGui.SetNextItemWidth(80f);
            ImGui.InputText("##purchasegccategory", ref _editPurchaseGcCategory, 16);
            ImGui.TextUnformatted("GC Rank Tier:");
            ImGui.SetNextItemWidth(80f);
            ImGui.InputText("##purchasegcranktier", ref _editPurchaseGcRankTier, 16);
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
        if (_replaceMode)
            await _host.ReplaceRecordedStep(_replaceOriginalStepId, _replaceSequenceNumber,
                before, inference, stepId, expect, notes, rawStep, CancellationToken.None);
        else
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

        if (stepType == "purchase-item")
        {
            ExpectValue? expectValue = expect is { Length: > 0 }
                ? new PredicateExpect { Predicate = expect }
                : null;

            var zone = (int)(after?.Zone.Value ?? 0);
            var zoneStr = zone > 0 ? zone.ToString() : null;
            var npcId = after?.LastNpcInteracted?.Value ?? 0u;
            var npcPos = after?.LastNpcPosition is { } p
                ? new Position3(p.X, p.Y, p.Z)
                : new Position3(0, 0, 0);

            uint.TryParse(_editPurchaseItemId, out var itemId);
            int.TryParse(_editPurchaseQuantity, out var qty);
            if (qty <= 0) qty = 1;

            var currency = _editPurchaseCurrency.Trim() switch
            {
                "gcSeals" => PurchaseCurrency.GcSeals,
                _         => PurchaseCurrency.Gil
            };

            int? gcCategory = int.TryParse(_editPurchaseGcCategory.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var catVal) ? catVal : null;
            int? gcRankTier = int.TryParse(_editPurchaseGcRankTier.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tierVal) ? tierVal : null;

            return new PurchaseItemStep
            {
                Id = stepId,
                Expect = expectValue,
                Zone = zoneStr,
                RequiredZone = zoneStr,
                Target = new NpcLocation(NpcId: npcId, Zone: zone, Position: npcPos),
                ItemId = itemId,
                Quantity = qty,
                Currency = currency,
                GcCategory = gcCategory,
                GcRankTier = gcRankTier
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
        var npcId = snap.LastNpcInteracted?.Value ?? 0;

        var options = new List<string>
        {
            "(none)",
            // Quest state
            $"isQuestAccepted({questId})",
            $"isQuestComplete({questId})",
            $"questSequence({questId}) >= {seq}",
            $"questFlag({questId}, 0)",
            $"questFlag({questId}, 1)",
            $"questFlag({questId}, 2)",
            $"questFlag({questId}, 3)",
            $"questVariable({questId}, 0)",
            $"questVariableLow({questId}, 0) >= 3",
            $"questVariableHigh({questId}, 1) >= 3",
            // Player state
            $"playerZone() == {zone}",
            $"playerNear({{\"x\":0,\"y\":0,\"z\":0}}, 3)",
            "not inventoryHasCoffers()",
            $"playerHasItem({0})",
            $"playerHasEquipped({0})",
            $"isPlayerJob({0})",
            $"jobGearsetExists({0})",
            // Object existence
            $"objectExists({npcId})",
            $"objectExistsInRange({npcId}, 30)",
        };

        if (snap.LastAttuned.HasValue)
            options.Add($"isAttuned({snap.LastAttuned.Value.Value})");

        if (snap.LastAethernetShardInteracted.HasValue)
            options.Add($"isAetherCurrentAttuned({snap.LastAethernetShardInteracted.Value.Value})");

        if (snap.ObjectInteracted is { } oi)
            options.Add($"objectExists({oi.InteractableId})");

        if (snap.EquipmentChanged is { NewItemIds: { Count: > 0 } items })
            options.Add($"playerHasEquipped({items[0]})");

        if (snap.JobChanged is { } jc)
            options.Add($"isPlayerJob({jc.NewJobId})");

        if (snap.KeyItemsAdded is { Count: > 0 } added)
            options.Add($"playerHasItem({added[0]})");

        return options;
    }

    private void ResetAndClose()
    {
        _before = null;
        _after = null;
        _inference = null;
        _overrideStepType = "";
        _editKillEnemyDataIds = "";
        _editPurchaseItemId = "";
        _editPurchaseQuantity = "";
        _editPurchaseCurrency = "gil";
        _editPurchaseGcCategory = "";
        _editPurchaseGcRankTier = "";
        _state = RecordState.WaitingForAction;
        _saveTask = null;
        _saveError = "";
        IsOpen = false;
    }
}
