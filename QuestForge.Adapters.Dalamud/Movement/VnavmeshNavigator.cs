using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using QuestForge.Adapters.Dalamud.Ipc;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Movement;

public sealed class VnavmeshNavigator : INavigator
{
    private readonly PluginServices _svc;
    private readonly VnavmeshIpc _ipc;
    private bool _lastPathWasFly;

    public VnavmeshNavigator(PluginServices svc)
    {
        _svc = svc;
        _ipc = new VnavmeshIpc(svc.PluginInterface);
    }

    public Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination,
        NavigationOptions options,
        CancellationToken ct)
    {
        if (!_ipc.NavIsReady())
            return Task.FromResult<Result<NavigationOutcome>>(Result.Ok(NavigationOutcome.NavmeshUnavailable));

        if (_ipc.PathIsRunning() || _ipc.PathfindInProgress())
        {
            // If we're navigating on ground but flying is now available, stop and re-pathfind.
            bool canFlyNow = options.UseFlight && CanFlyInCurrentZone();
            if (canFlyNow && !_lastPathWasFly)
            {
                _ipc.PathStop();
                // Fall through to pathfind below with fly=true
            }
            else
            {
                return Task.FromResult<Result<NavigationOutcome>>(Result.Ok(NavigationOutcome.Arrived));
            }
        }

        var dest = new Vector3(destination.X, destination.Y, destination.Z);
        bool fly = options.UseFlight && CanFlyInCurrentZone();
        _lastPathWasFly = fly;
        var ok = _ipc.PathfindAndMoveCloseTo(dest, fly, options.StoppingDistance);
        if (!ok)
            return Task.FromResult<Result<NavigationOutcome>>(Result.Fail<NavigationOutcome>("pathfindRejected", "vnavmesh rejected the pathfind request"));

        return Task.FromResult<Result<NavigationOutcome>>(Result.Ok(NavigationOutcome.Arrived));
    }

    public Task<Result<Unit>> Stop(CancellationToken ct)
    {
        _ipc.PathStop();
        _lastPathWasFly = false;
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public unsafe Task<Result<Unit>> Jump(CancellationToken ct)
    {
        var am = ActionManager.Instance();
        if (am != null) am->UseAction(ActionType.GeneralAction, 2);
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<bool>> IsNavigating(CancellationToken ct)
    {
        var navigating = _ipc.PathIsRunning() || _ipc.PathfindInProgress();
        return Task.FromResult<Result<bool>>(Result.Ok(navigating));
    }

    public Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct)
    {
        var status = _ipc.NavIsReady() ? NavmeshStatus.Ready : NavmeshStatus.Generating;
        return Task.FromResult<Result<NavmeshInfo>>(Result.Ok(new NavmeshInfo(status, null, null)));
    }

    private static unsafe bool CanFlyInCurrentZone()
    {
        var ps = PlayerState.Instance();
        return ps != null && ps->CanFly;
    }
}
