using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using QuestForge.Adapters.Gear;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Gear;

public sealed class DalamudBestGearEquipper : IBestGearEquipper
{
    private enum VanillaEquipPhase { Idle, WaitingForRecommendation }

    private readonly PluginServices _svc;
    private readonly Func<bool> _preferStylist;
    private VanillaEquipPhase _vanillaPhase = VanillaEquipPhase.Idle;
    private readonly Stopwatch _phaseTimer = new();
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(5);

    public DalamudBestGearEquipper(PluginServices svc, Func<bool> preferStylist)
    {
        _svc = svc;
        _preferStylist = preferStylist;
    }

    public Task<Result<EquipOutcome>> EquipBestGear(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_preferStylist() && HasValidGearset())
        {
            try
            {
                _svc.PluginInterface
                    .GetIpcSubscriber<bool?, bool?, object>("Stylist.UpdateCurrentGearsetEx")
                    .InvokeAction(true, true);
                _vanillaPhase = VanillaEquipPhase.Idle;
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

    private static unsafe bool HasValidGearset()
    {
        var module = RaptureGearsetModule.Instance();
        if (module is null) return false;
        var index = module->CurrentGearsetIndex;
        return index != 255 && module->IsValidGearset(index);
    }

    private unsafe Result<EquipOutcome> VanillaEquip()
    {
        var module = RecommendEquipModule.Instance();
        if (module is null)
            return Result.Fail<EquipOutcome>("recommendEquipUnavailable",
                "RecommendEquipModule.Instance() returned null");

        switch (_vanillaPhase)
        {
            case VanillaEquipPhase.Idle:
            {
                var playerState = PlayerState.Instance();
                if (playerState is null)
                    return Result.Fail<EquipOutcome>("playerStateUnavailable",
                        "PlayerState.Instance() returned null");

                module->SetupForClassJob(playerState->CurrentClassJobId);
                _vanillaPhase = VanillaEquipPhase.WaitingForRecommendation;
                _phaseTimer.Restart();
                return Result.Ok(EquipOutcome.Pending);
            }

            case VanillaEquipPhase.WaitingForRecommendation:
            {
                if (_phaseTimer.Elapsed > PhaseTimeout)
                {
                    _vanillaPhase = VanillaEquipPhase.Idle;
                    _phaseTimer.Reset();
                    return Result.Fail<EquipOutcome>("recommendEquipTimeout",
                        "RecommendEquipModule did not finish updating within 5 seconds");
                }

                if (module->IsUpdating)
                    return Result.Ok(EquipOutcome.Pending);

                module->EquipRecommendedGear();
                _vanillaPhase = VanillaEquipPhase.Idle;
                _phaseTimer.Reset();
                return Result.Ok(EquipOutcome.Equipped);
            }

            default:
                _vanillaPhase = VanillaEquipPhase.Idle;
                return Result.Fail<EquipOutcome>("unexpectedPhase",
                    $"Unexpected vanilla equip phase: {_vanillaPhase}");
        }
    }
}
