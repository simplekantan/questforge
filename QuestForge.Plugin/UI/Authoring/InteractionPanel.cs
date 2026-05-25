using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class InteractionPanel : Window
{
    private readonly AuthoringHost _host;
    private readonly PluginConfig _config;
    private readonly IDalamudPluginInterface _pi;

    public InteractionPanel(AuthoringHost host, PluginConfig config, IDalamudPluginInterface pi)
        : base("QuestForge — Interaction", ImGuiWindowFlags.None)
    {
        _host = host;
        _config = config;
        _pi = pi;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(280, 180),
            MaximumSize = new System.Numerics.Vector2(560, 480)
        };
    }

    public override void Draw()
    {
        var snapshot = _host.CurrentSnapshot;

        ImGui.TextUnformatted("Last NPC Interaction");
        ImGui.Separator();

        ImGui.TextUnformatted($"NPC ID: {(snapshot.LastNpcInteracted.HasValue ? snapshot.LastNpcInteracted.Value.Value.ToString() : "(none)")}");

        if (snapshot.LastNpcPosition is { } npcPos)
        {
            ImGui.TextUnformatted($"NPC Position: ({npcPos.X:F2}, {npcPos.Y:F2}, {npcPos.Z:F2})");
            ImGui.SameLine();
            if (ImGui.Button("Copy NPC Pos"))
                ImGui.SetClipboardText($"{{\"x\": {npcPos.X:F2}, \"y\": {npcPos.Y:F2}, \"z\": {npcPos.Z:F2}}}");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Available quests from this NPC:");
        ImGui.Separator();

        // Warning when actively authoring a different quest
        if (_host.Mode == AuthoringMode.Author && _host.AuthoringTarget.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.75f, 0.1f, 1f));
            ImGui.TextUnformatted($"Currently authoring quest {_host.AuthoringTarget.Value.Value}.");
            ImGui.TextUnformatted("To stop: /qf author stop, or click 'Stop Authoring' in the Authoring panel.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        var quests = _host.NpcQuests;
        var displayed = 0;

        foreach (var qi in quests)
        {
            if (displayed >= 5) break;

            // Build label
            string label;
            if (qi.IsComplete)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1f));
                label = $"[done] \"{qi.QuestName}\" ({qi.QuestId})";
                ImGui.TextUnformatted(label);
                ImGui.PopStyleColor();
            }
            else
            {
                label = $"\"{qi.QuestName}\" ({qi.QuestId})";
                ImGui.TextUnformatted(label);
            }

            ImGui.SameLine();
            ImGui.PushID((int)qi.QuestId);
            if (ImGui.SmallButton("Start Authoring"))
                _host.EnterAuthorMode(new QuestId(qi.QuestId));
            ImGui.PopID();

            displayed++;
        }

        if (displayed == 0)
        {
            ImGui.TextUnformatted("(No quests available from this NPC.)");
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f));
            if (!_config.ShowUnacceptableQuestsInAuthorPanel)
                ImGui.TextWrapped("Enable 'Show locked/future quests' below to reveal quests you cannot accept yet.");
            if (!_config.ShowCompletedQuestsInAuthorPanel)
                ImGui.TextWrapped("Enable 'Show completed quests' below to see all quests from this NPC.");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Settings toggles
        var showCompleted = _config.ShowCompletedQuestsInAuthorPanel;
        if (ImGui.Checkbox("Show completed quests", ref showCompleted))
        {
            _config.ShowCompletedQuestsInAuthorPanel = showCompleted;
            _config.Save(_pi);
            _host.InvalidateNpcCache(); // refresh list immediately on next heartbeat
        }

        var showUnacceptable = _config.ShowUnacceptableQuestsInAuthorPanel;
        if (ImGui.Checkbox("Show locked/future quests", ref showUnacceptable))
        {
            _config.ShowUnacceptableQuestsInAuthorPanel = showUnacceptable;
            _config.Save(_pi);
            _host.InvalidateNpcCache();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Last Dialogue");
        ImGui.Separator();

        ImGui.TextUnformatted($"Prompt: {snapshot.LastDialoguePrompt ?? "(none)"}");
        if (snapshot.LastDialoguePrompt is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy Prompt"))
                ImGui.SetClipboardText(snapshot.LastDialoguePrompt);
        }

        ImGui.TextUnformatted($"Answer: {snapshot.LastDialogueAnswer ?? "(none)"}");
        if (snapshot.LastDialogueAnswer is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy Answer"))
                ImGui.SetClipboardText(snapshot.LastDialogueAnswer);
        }
    }
}
