using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestForge.Engine.Authoring;
using QuestForge.Plugin.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class QuestStatePanel : Window
{
    private readonly AuthoringHost _host;

    public QuestStatePanel(AuthoringHost host)
        : base("QuestForge — Quest State", ImGuiWindowFlags.None)
    {
        _host = host;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(280, 160),
            MaximumSize = new System.Numerics.Vector2(500, 460)
        };
    }

    public override void Draw()
    {
        var snapshot = _host.CurrentSnapshot;

        // Recent change highlight at the top
        if (_host.RecentChange is { } change)
        {
            var elapsed = DateTimeOffset.UtcNow - change.When;
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 1f, 0f, 1f));
            ImGui.TextUnformatted($"Recent change {elapsed.TotalSeconds:F1}s ago: {change.Description}");
            ImGui.PopStyleColor();
            ImGui.Separator();
        }

        if (_host.Mode == AuthoringMode.Off || _host.Mode == AuthoringMode.Inspect)
        {
            ImGui.TextUnformatted("No quest actively tracked. Use /qf author <questId> to start recording.");
            ImGui.Separator();
        }

        ImGui.TextUnformatted($"Active Quest: {(snapshot.ActiveQuest.HasValue ? snapshot.ActiveQuest.Value.Value.ToString() : "(none)")}");
        ImGui.TextUnformatted($"Quest Sequence: {snapshot.QuestSequence}");
        ImGui.TextUnformatted($"Quest Flags: 0x{snapshot.QuestFlags:X8}");
        ImGui.TextUnformatted($"Accepted: {snapshot.QuestAccepted}");
        ImGui.TextUnformatted($"Completed: {snapshot.QuestCompleted}");

        if (snapshot.QuestVariables is { Count: 6 } vars)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Variables (V0–V5):");
            if (ImGui.BeginTable("qf-quest-variables", 6,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                for (var i = 0; i < 6; i++)
                    ImGui.TableSetupColumn($"V{i}");
                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                for (var i = 0; i < 6; i++)
                {
                    ImGui.TableSetColumnIndex(i);
                    var b = vars[i];
                    if (b == 0)
                        ImGui.TextUnformatted("0");
                    else
                        ImGui.TextUnformatted($"0x{b:X2} H:{b >> 4} L:{b & 0x0F}");
                }
                ImGui.EndTable();
            }
        }
    }
}
