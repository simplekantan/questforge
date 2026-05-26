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
            // One row per variable (transposed) so cells never truncate. Dec is the plain
            // value; High/Low are the nibbles — quests often pack per-objective sub-counts
            // into the high/low nibble of a single variable byte.
            if (ImGui.BeginTable("qf-quest-variables", 5,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Var");
                ImGui.TableSetupColumn("Dec");
                ImGui.TableSetupColumn("Hex");
                ImGui.TableSetupColumn("High");
                ImGui.TableSetupColumn("Low");
                ImGui.TableHeadersRow();

                for (var i = 0; i < 6; i++)
                {
                    var b = vars[i];
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted($"V{i}");
                    ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(b.ToString());
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted($"0x{b:X2}");
                    ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted((b >> 4).ToString());
                    ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted((b & 0x0F).ToString());
                }
                ImGui.EndTable();
            }
        }
    }
}
