using System.Numerics;
using QuestForge.Adapters.Dalamud.Ipc;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Movement;

public sealed class VnavmeshNavigator : INavigator
{
    private readonly VnavmeshIpc _ipc;

    public VnavmeshNavigator(PluginServices svc)
        => _ipc = new VnavmeshIpc(svc.PluginInterface);

    public Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination,
        NavigationOptions options,
        CancellationToken ct)
    {
        if (!_ipc.NavIsReady())
            return Task.FromResult<Result<NavigationOutcome>>(Result.Ok(NavigationOutcome.NavmeshUnavailable));

        var dest = new Vector3(destination.X, destination.Y, destination.Z);
        var ok = _ipc.PathfindAndMoveCloseTo(dest, options.UseFlight, options.StoppingDistance);
        if (!ok)
            return Task.FromResult<Result<NavigationOutcome>>(Result.Fail<NavigationOutcome>("pathfindRejected", "vnavmesh rejected the pathfind request"));

        // Fire-and-forget: vnavmesh accepted but is computing async. Engine checks IsNavigating next tick.
        return Task.FromResult<Result<NavigationOutcome>>(Result.Ok(NavigationOutcome.Arrived));
    }

    public Task<Result<Unit>> Stop(CancellationToken ct)
    {
        _ipc.PathStop();
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<bool>> IsNavigating(CancellationToken ct)
    {
        // PathfindInProgress covers the ~30-tick async gap after PathfindAndMoveCloseTo
        // where Path.IsRunning is false even though the player hasn't started moving yet.
        var navigating = _ipc.PathIsRunning() || _ipc.PathfindInProgress();
        return Task.FromResult<Result<bool>>(Result.Ok(navigating));
    }

    public Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct)
    {
        // vnavmesh exposes only a binary ready/not-ready signal — no generation progress percentage
        var status = _ipc.NavIsReady() ? NavmeshStatus.Ready : NavmeshStatus.Generating;
        return Task.FromResult<Result<NavmeshInfo>>(Result.Ok(new NavmeshInfo(status, null, null)));
    }
}
