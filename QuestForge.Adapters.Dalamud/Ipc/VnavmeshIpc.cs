using System.Numerics;
using Dalamud.Plugin;

namespace QuestForge.Adapters.Dalamud.Ipc;

internal sealed class VnavmeshIpc
{
    private readonly Func<bool> _navIsReady;
    private readonly Func<Vector3, bool, float, bool> _pathfindAndMoveCloseTo;
    private readonly Func<bool> _pathfindInProgress;
    private readonly Func<bool> _pathIsRunning;
    private readonly Func<bool> _pathStop;
    private readonly Func<Vector3, float, float, Vector3?> _nearestPointReachable;

    public VnavmeshIpc(IDalamudPluginInterface pi)
    {
        _navIsReady             = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc;
        _pathfindAndMoveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo").InvokeFunc;
        _pathfindInProgress     = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc;
        _pathIsRunning          = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc;
        _pathStop               = pi.GetIpcSubscriber<bool>("vnavmesh.Path.Stop").InvokeFunc;
        _nearestPointReachable  = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable").InvokeFunc;
    }

    public bool NavIsReady() => _navIsReady();
    public bool PathfindAndMoveCloseTo(Vector3 dest, bool fly, float range) => _pathfindAndMoveCloseTo(dest, fly, range);
    public bool PathfindInProgress() => _pathfindInProgress();
    public bool PathIsRunning() => _pathIsRunning();
    public bool PathStop() => _pathStop();
    public Vector3? NearestPointReachable(Vector3 pos, float yMin, float yMax) => _nearestPointReachable(pos, yMin, yMax);
}
