using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudBestGearEquipper : IBestGearEquipper
{
    private readonly PluginServices _svc;
    private readonly Func<bool> _preferStylist;

    public DalamudBestGearEquipper(PluginServices svc, Func<bool> preferStylist)
    {
        _svc = svc;
        _preferStylist = preferStylist;
    }

    public Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_preferStylist())
        {
            try
            {
                _svc.PluginInterface
                    .GetIpcSubscriber<bool?, bool?, object>("Stylist.UpdateCurrentGearsetEx")
                    .InvokeAction(true, true);
                return Task.FromResult<Result<EquipOutcome>>(Result.Ok(EquipOutcome.Equipped));
            }
            catch
            {
                // Stylist not installed or not ready — fall through to vanilla path
            }
        }

        return Task.FromResult<Result<EquipOutcome>>(VanillaEquip());
    }

    public Task<Result<bool>> IsStylistAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            _svc.PluginInterface
                .GetIpcSubscriber<bool>("Stylist.IsBusy")
                .InvokeFunc();
            return Task.FromResult<Result<bool>>(Result.Ok(true));
        }
        catch
        {
            return Task.FromResult<Result<bool>>(Result.Ok(false));
        }
    }

    private static unsafe Result<EquipOutcome> VanillaEquip()
    {
        var module = RecommendEquipModule.Instance();
        if (module is null)
            return Result.Fail<EquipOutcome>("recommendEquipUnavailable",
                "RecommendEquipModule.Instance() returned null");

        var playerState = PlayerState.Instance();
        if (playerState is null)
            return Result.Fail<EquipOutcome>("playerStateUnavailable",
                "PlayerState.Instance() returned null");

        module->SetupForClassJob(playerState->CurrentClassJobId);
        module->EquipRecommendedGear();

        return Result.Ok(EquipOutcome.Equipped);
    }
}
