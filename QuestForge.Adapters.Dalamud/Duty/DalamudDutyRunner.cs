using Dalamud.Plugin.Ipc;
using QuestForge.Adapters.Duty;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Duty;

/// <summary>
/// Delegates dungeon/trial runs to AutoDuty via IPC.
/// StartDuty: configures Support mode then calls AutoDuty.Run.
/// StopDuty: calls AutoDuty.Stop. Idempotent.
/// IsAvailable: probes the AutoDuty.ContentHasPath IPC gate.
/// ContentHasPath: queries AutoDuty for whether a path exists for the territory.
/// </summary>
public sealed class DalamudDutyRunner : IDutyRunner
{
    private readonly PluginServices _svc;

    private ICallGateSubscriber<string, string, object> SetConfig
        => _svc.PluginInterface.GetIpcSubscriber<string, string, object>("AutoDuty.SetConfig");

    private ICallGateSubscriber<uint, int, bool, object> Run
        => _svc.PluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");

    private ICallGateSubscriber<object> Stop
        => _svc.PluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop");

    private ICallGateSubscriber<uint, bool> ContentHasPathGate
        => _svc.PluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");

    public DalamudDutyRunner(PluginServices svc)
    {
        _svc = svc;
    }

    public Task<Result<bool>> StartDuty(uint territoryType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            SetConfig.InvokeAction("dutyModeEnum", "Support");
            Run.InvokeAction(territoryType, 1, true);
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("start-duty-failed", ex.Message));
        }
    }

    public Task<Result<bool>> StopDuty(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Stop.InvokeAction();
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
        catch
        {
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(true));
        }
    }

    public Task<Result<bool>> IsAvailable(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Success(ContentHasPathGate.HasFunction));
        }
        catch
        {
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(false));
        }
    }

    public Task<Result<bool>> ContentHasPath(uint territoryType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var hasPath = ContentHasPathGate.InvokeFunc(territoryType);
            return Task.FromResult<Result<bool>>(new Result<bool>.Success(hasPath));
        }
        catch (Exception ex)
        {
            return Task.FromResult<Result<bool>>(
                new Result<bool>.Failure("content-has-path-failed", ex.Message));
        }
    }
}
