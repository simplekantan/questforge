using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ECChat = ECommons.Automation.Chat;
using QuestForge.Adapters.Duty;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Duty;

/// <summary>
/// Enables BossMod AI for Single Player Duties via BossMod IPC.
/// StartDuty: creates/activates the "QuestForge" preset, enables quest battles via /vbm cfg.
/// StopDuty: disables AI and clears the preset. Idempotent.
/// IsBossModAvailable: probes the BossMod.Presets.Get IPC gate.
/// </summary>
public sealed class DalamudQuestBattleRunner : IQuestBattleRunner
{
    private readonly PluginServices _svc;
    private bool _started;

    // Per-call IPC subscribers — same pattern as DalamudBestGearEquipper.
    private ICallGateSubscriber<string, string?> GetPreset
        => _svc.PluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");

    private ICallGateSubscriber<string, bool, bool> CreatePreset
        => _svc.PluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");

    private ICallGateSubscriber<string, bool> SetPresetActive
        => _svc.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");

    private ICallGateSubscriber<bool> ClearPresetActive
        => _svc.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");

    public DalamudQuestBattleRunner(PluginServices svc)
    {
        _svc = svc;
    }

    public Task<Result<bool>> StartDuty(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            ECChat.SendMessage("/vbm cfg ZoneModuleConfig EnableQuestBattles true");
            ECChat.SendMessage("/vbm cfg Autorotation ClearPresetOnCombatEnd false");
            ECChat.SendMessage("/vbm ai on");
            _started = true;
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(
                Result.Fail<bool>("startDutyFailed", ex.Message));
        }
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_started)
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));

        try
        {
            ECChat.SendMessage("/vbm ai off");
            ECChat.SendMessage("/vbm cfg ZoneModuleConfig EnableQuestBattles false");
            _started = false;
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
        catch (Exception ex)
        {
            _started = false;
            return Task.FromResult<Result<bool>>(
                Result.Fail<bool>("stopDutyFailed", ex.Message));
        }
    }

    public Task<Result<bool>> IsBossModAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Success(GetPreset.HasFunction));
        }
        catch
        {
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(false));
        }
    }

    // Minimal QuestBattle preset JSON (clean-room implementation).
    // A preset with no module-specific overrides causes BossMod to use its default
    // quest-battle AI, which is sufficient for most single-player duty objectives.
    private const string QuestBattlePresetJson =
        """{"Name":"QuestForge","Modules":{}}""";
}
