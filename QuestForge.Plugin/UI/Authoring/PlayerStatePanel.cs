using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestForge.Plugin.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class PlayerStatePanel : Window
{
    private readonly AuthoringHost _host;

    public PlayerStatePanel(AuthoringHost host)
        : base("QuestForge — Player State", ImGuiWindowFlags.None)
    {
        _host = host;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(280, 160),
            MaximumSize = new System.Numerics.Vector2(500, 400)
        };
    }

    public override void Draw()
    {
        var snapshot = _host.CurrentSnapshot;

        ImGui.TextUnformatted($"Mode: {_host.Mode}");
        ImGui.Separator();

        ImGui.TextUnformatted($"Zone: {snapshot.Zone.Value}");

        var pos = snapshot.Position;
        ImGui.TextUnformatted($"Position: ({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})");

        ImGui.SameLine();
        if (ImGui.Button("Copy Position"))
        {
            ImGui.SetClipboardText(FormatPositionJson(pos));
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Captured at: {snapshot.CapturedAt:HH:mm:ss.fff}");
    }

    private static string FormatPositionJson(QuestForge.Adapters.Types.WorldPosition pos)
        => $"{{\"x\": {pos.X:F2}, \"y\": {pos.Y:F2}, \"z\": {pos.Z:F2}}}";
}
