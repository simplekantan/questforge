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

    private static readonly string[] CategoryOptions =
        ["msq", "class", "job", "role", "blue-urgent", "blue", "side"];

    private string _exportPath = "";
    private int _categoryIndex;
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
        if (_host.IsFragmentMode)
        {
            var fragmentId = _host.FragmentTarget ?? "unknown";
            var fragmentRelPath = fragmentId.Replace('/', Path.DirectorySeparatorChar);
            _exportPath = Path.Combine(_pi.GetPluginConfigDirectory(), "fragments", $"{fragmentRelPath}.json");
        }
        else
        {
            var target = _host.AuthoringTarget;
            _exportPath = target.HasValue
                ? Path.Combine(_pi.GetPluginConfigDirectory(), "quests", $"{target.Value.Value}.json")
                : Path.Combine(_pi.GetPluginConfigDirectory(), "quests", "unknown.json");
        }
        _statusMessage = "";
        _statusIsError = false;
        _validationErrors.Clear();
        _validationWarnings.Clear();
        _showExportAnyway = false;

        if (!_host.IsFragmentMode && _host.AuthoringTarget is { } qt)
        {
            var draft = _host.DraftManager.Get(qt, CancellationToken.None).GetAwaiter().GetResult();
            var cat = draft?.Category ?? "msq";
            _categoryIndex = Array.IndexOf(CategoryOptions, cat);
            if (_categoryIndex < 0) _categoryIndex = 0;
        }

        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Export Draft to Quest File");
        ImGui.Separator();

        ImGui.TextUnformatted("Export path:");
        ImGui.SetNextItemWidth(500f);
        ImGui.InputText("##exportpath", ref _exportPath, 512);

        if (!_host.IsFragmentMode)
        {
            ImGui.SetNextItemWidth(160f);
            if (ImGui.Combo("Category", ref _categoryIndex, CategoryOptions, CategoryOptions.Length))
            {
                var target = _host.AuthoringTarget;
                if (target is not null)
                {
                    var draft = _host.DraftManager.Get(target.Value, CancellationToken.None).GetAwaiter().GetResult();
                    if (draft is not null)
                    {
                        draft.Category = CategoryOptions[_categoryIndex];
                        _host.DraftManager.MarkDirty(target.Value);
                        _ = _host.DraftManager.SaveNow(target.Value, CancellationToken.None);
                    }
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("msq = Main Scenario\nclass/job/role = Class/Job quests (Tier 1, job-filtered)\nblue-urgent = Critical unlocks (Tier 1, always runs)\nblue = Feature unlocks (Tier 4, opt-in)\nside = Side quests (Tier 5, opt-in)");
        }

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
        if (_host.IsFragmentMode)
            DoExportFragment(forceExport);
        else
            DoExportQuest(forceExport);
    }

    private void DoExportQuest(bool forceExport)
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
            // Only rebuild step.Raw from snapshot when the stored type is stale (wrong type).
            // If step.Raw already has the correct type it was built with a live snapshot that
            // included all fields (LastAttuned, KeyItemsRemoved, LastAethernetShardInteracted);
            // rebuilding from the deserialized draft snapshot would lose those fields because
            // the draft format does not serialize them.
            var now = DateTimeOffset.UtcNow;
            foreach (var step in draft.Steps.ToList())
            {
                var rawTypeDiscriminator = GetTypeDiscriminator(step.Raw);
                if (rawTypeDiscriminator == step.StepType) continue; // Raw is correct — use as-is
                // Raw type is stale (e.g. "talk" stored when stepType is "attune") — rebuild
                var rebuilt = StepFactory.Build(step.StepType, step.StepId, step.SuggestedExpect, step.ObservedAfter, step.ObservedBefore);
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

    private void DoExportFragment(bool forceExport)
    {
        _statusMessage = "";
        _statusIsError = false;
        _validationErrors.Clear();
        _validationWarnings.Clear();
        _showExportAnyway = false;

        var fragmentTarget = _host.FragmentTarget;
        if (fragmentTarget is null)
        {
            _statusMessage = "No fragment target set.";
            _statusIsError = true;
            return;
        }

        var draft = _host.FragmentDraftManager.Get(fragmentTarget, CancellationToken.None).GetAwaiter().GetResult();
        if (draft is null)
        {
            _statusMessage = "No draft found for this fragment.";
            _statusIsError = true;
            return;
        }

        if (_validateBeforeExport && !forceExport)
        {
            var (errors, warnings) = _validator.ValidateFragment(draft);
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
            var now = DateTimeOffset.UtcNow;
            foreach (var step in draft.Steps.ToList())
            {
                var rawTypeDiscriminator = GetTypeDiscriminator(step.Raw);
                if (rawTypeDiscriminator == step.StepType) continue;
                var rebuilt = StepFactory.Build(step.StepType, step.StepId, step.SuggestedExpect, step.ObservedAfter, step.ObservedBefore);
                draft.ReplaceStep(step.StepId, step with { Raw = rebuilt }, now);
            }

            var definition = draft.ToFragmentDefinition();
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

    private static string GetTypeDiscriminator(QuestForge.Schema.Step? step) => step switch
    {
        QuestForge.Schema.TravelStep             => "travel",
        QuestForge.Schema.TalkStep               => "talk",
        QuestForge.Schema.AcceptStep             => "accept",
        QuestForge.Schema.TurnInStep             => "turn-in",
        QuestForge.Schema.AttunementStep         => "attune",
        QuestForge.Schema.HandOverItemStep       => "hand-over-item",
        QuestForge.Schema.PickupItemStep         => "pickup-item",
        QuestForge.Schema.InteractObjectStep     => "interact-object",
        QuestForge.Schema.CutsceneStep           => "cutscene",
        QuestForge.Schema.DutyStep               => "duty",
        QuestForge.Schema.CombatStep             => "combat",
        QuestForge.Schema.UseItemStep            => "use-item",
        QuestForge.Schema.UseEmoteStep           => "use-emote",
        QuestForge.Schema.UseActionStep          => "use-action",
        QuestForge.Schema.SayChatMessageStep     => "say-chat-message",
        QuestForge.Schema.EquipGearForQuestStep  => "equip-gear-for-quest",
        QuestForge.Schema.EquipBestGearStep      => "equip-best-gear",
        QuestForge.Schema.ChangeJobStep          => "change-job",
        QuestForge.Schema.RegisterGearsetStep    => "register-gearset",
        QuestForge.Schema.OpenCoffersStep        => "open-coffers",
        QuestForge.Schema.TeleportStep           => "teleport",
        QuestForge.Schema.PurchaseItemStep       => "purchase-item",
        QuestForge.Schema.WaitStep               => "wait",
        QuestForge.Schema.MinigameStep           => "minigame",
        QuestForge.Schema.BranchStep             => "branch",
        QuestForge.Schema.FragmentStep           => "fragment",
        QuestForge.Schema.AwaitUserStep          => "await-user",
        null                                     => "",
        _                                        => ""
    };

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
