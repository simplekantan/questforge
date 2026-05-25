using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;

namespace QuestForge.Adapters.Dalamud.Ipc;

internal sealed class VnavmeshIpc
{
    private readonly Func<bool> _navIsReady;
    private readonly Func<Vector3, bool, float, bool> _pathfindAndMoveCloseTo;
    private readonly Func<bool> _pathfindInProgress;
    private readonly Func<bool> _pathIsRunning;
    private readonly Action _pathStop;   // vnavmesh registers Path.Stop as an Action, not a Func
    private readonly Func<Vector3, float, float, Vector3?> _nearestPointReachable;

    public VnavmeshIpc(IDalamudPluginInterface pi)
    {
        _navIsReady             = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc;
        _pathfindAndMoveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo").InvokeFunc;
        _pathfindInProgress     = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc;
        _pathIsRunning          = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc;
        _pathStop               = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction;
        _nearestPointReachable  = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable").InvokeFunc;
    }

    // vnavmesh's IPC funcs are briefly unregistered while it (re)loads — notably during zone
    // transitions, where a call can throw IpcNotReadyError ("...was not registered yet"). Treat that
    // as "not ready" and degrade to a safe default instead of letting it surface as a dispatch error;
    // the engine then simply waits for vnavmesh rather than logging a spurious failure.
    private static T Safe<T>(Func<T> call, T fallback)
    {
        try { return call(); }
        catch (IpcNotReadyError) { return fallback; }
    }

    public bool NavIsReady() => Safe(_navIsReady, false);
    public bool PathfindAndMoveCloseTo(Vector3 dest, bool fly, float range) => Safe(() => _pathfindAndMoveCloseTo(dest, fly, range), false);
    public bool PathfindInProgress() => Safe(_pathfindInProgress, false);
    public bool PathIsRunning() => Safe(_pathIsRunning, false);

    /// <summary>Stops vnavmesh path-following. No-op (returns false) when vnavmesh IPC is not ready.</summary>
    public bool PathStop()
    {
        try { _pathStop(); return true; }
        catch (IpcNotReadyError) { return false; }
    }

    public Vector3? NearestPointReachable(Vector3 pos, float yMin, float yMax) => Safe(() => _nearestPointReachable(pos, yMin, yMax), null);
}
