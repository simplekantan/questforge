using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestForge.Plugin.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class InteractionPanel : Window
{
    private readonly AuthoringHost _host;

    public InteractionPanel(AuthoringHost host)
        : base("QuestForge — Interaction", ImGuiWindowFlags.None)
    {
        _host = host;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(280, 180),
            MaximumSize = new System.Numerics.Vector2(500, 400)
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
