using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using QuestForge.Adapters.Dalamud.Ipc;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Movement;

public sealed class VnavmeshNavigator : INavigator
{
    private readonly PluginServices _svc;
    private readonly VnavmeshIpc _ipc;

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

        // Don't re-queue pathfinding every tick while already navigating — vnavmesh would
        // re-pathfind from the current position each call, flooding the log and wasting CPU.
        if (_ipc.PathIsRunning() || _ipc.PathfindInProgress())
            return Task.FromResult<Result<NavigationOutcome>>(Result.Ok(NavigationOutcome.Arrived));

        var dest = new Vector3(destination.X, destination.Y, destination.Z);
        // Respect the quest schema's UseFlight preference, but override to ground
        // if PlayerState.CanFly is false — the zone hasn't granted flying (no air navmesh built).
        // Default to ground (false) if PlayerState is unavailable.
        bool fly = options.UseFlight && CanFlyInCurrentZone();
        var ok = _ipc.PathfindAndMoveCloseTo(dest, fly, options.StoppingDistance);
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

    public Task<Result<Unit>> Jump(CancellationToken ct)
    {
        // Stub: Dalamud implementation is a Slice 3 deliverable.
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

    // PlayerState.CanFly is set by the game on zone load and accounts for Aether Current unlocks.
    // Returns false if PlayerState is unavailable — fail safe over fail open.
    private static unsafe bool CanFlyInCurrentZone()
    {
        var ps = PlayerState.Instance();
        return ps != null && ps->CanFly;
    }
}