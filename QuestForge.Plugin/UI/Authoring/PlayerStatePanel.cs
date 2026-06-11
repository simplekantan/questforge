using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Plugin.Authoring;
using QuestForge.Plugin.Tracing.Authoring;

namespace QuestForge.Plugin.UI.Authoring;

public sealed class PlayerStatePanel : Window
{
    private readonly AuthoringHost _host;
    private readonly IGameStateProvider _gameState;
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;

    public PlayerStatePanel(
        AuthoringHost host,
        IGameStateProvider gameState,
        IDataManager dataManager,
        IObjectTable objectTable)
        : base("QuestForge — Player State", ImGuiWindowFlags.None)
    {
        _host = host;
        _gameState = gameState;
        _dataManager = dataManager;
        _objectTable = objectTable;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(280, 220),
            MaximumSize = new System.Numerics.Vector2(500, 500)
        };
    }

    public override void Draw()
    {
        var ct = CancellationToken.None;

        var snapshot = _host.CurrentSnapshot;
        var state = _gameState.GetPlayerState(ct).GetAwaiter().GetResult().ValueOrDefault;
        var instanceKind = _gameState.GetCurrentInstanceKind(ct).GetAwaiter().GetResult().ValueOrDefault;

        var zone = state?.Zone ?? snapshot.Zone;
        var pos = state?.Position ?? snapshot.Position;
        var job = state?.Job ?? default;
        var level = state?.JobLevel ?? 0;
        var mount = state?.MountState ?? MountState.Dismounted;
        var inCombat = state?.InCombat ?? false;

        string? jobName = null;
        if (job.Value != 0)
        {
            var sheet = _dataManager.GetExcelSheet<ClassJob>();
            var row = sheet?.GetRow(job.Value);
            if (row is { } r)
            {
                var n = r.Name.ToString();
                if (!string.IsNullOrEmpty(n))
                    jobName = n;
            }
        }

        int hpCurrent = 0;
        int hpMax = 0;
        if (_objectTable.LocalPlayer is { } player)
        {
            hpCurrent = (int)player.CurrentHp;
            hpMax = (int)player.MaxHp;
        }

        ImGui.TextUnformatted($"Mode: {_host.Mode}");
        ImGui.Separator();

        ushort cfcId;
        unsafe { cfcId = GameMain.Instance()->CurrentContentFinderConditionId; }

        ImGui.TextUnformatted(PlayerStateFormatter.FormatZone(zone.Value));
        ImGui.SameLine();
        ImGui.TextUnformatted(PlayerStateFormatter.FormatCfc(cfcId));
        ImGui.TextUnformatted(PlayerStateFormatter.FormatPosition(pos));

        ImGui.SameLine();
        if (ImGui.Button("Copy Position"))
        {
            ImGui.SetClipboardText(PlayerStateFormatter.FormatPositionJson(pos));
        }

        ImGui.TextUnformatted(PlayerStateFormatter.FormatJob(job, jobName, level));
        ImGui.TextUnformatted(PlayerStateFormatter.FormatMount(mount));
        ImGui.TextUnformatted(PlayerStateFormatter.FormatCombat(inCombat));
        ImGui.TextUnformatted(PlayerStateFormatter.FormatHp(hpCurrent, hpMax));
        ImGui.TextUnformatted(PlayerStateFormatter.FormatInstance(instanceKind));

        var target = _objectTable.LocalPlayer?.TargetObject;
        if (target is not null)
        {
            ImGui.TextUnformatted($"Target: {target.Name} (BaseId: {target.BaseId})");
            var targetPos = new WorldPosition(target.Position.X, target.Position.Y, target.Position.Z);
            ImGui.TextUnformatted(PlayerStateFormatter.FormatPosition(targetPos).Replace("Position:", "Target Pos:"));
            ImGui.SameLine();
            if (ImGui.Button("Copy Target Pos"))
            {
                ImGui.SetClipboardText(PlayerStateFormatter.FormatPositionJson(targetPos));
            }
        }
        else
        {
            ImGui.TextUnformatted("Target: (none)");
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Captured at: {snapshot.CapturedAt:HH:mm:ss.fff}");
    }
}
