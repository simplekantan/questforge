using QuestForge.Adapters.Combat;
using QuestForge.Adapters.Dalamud.Ipc;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Combat;

public sealed class WrathComboAdapter : ICombat, IDisposable
{
    private readonly PluginServices _svc;
    private readonly WrathComboIpc  _ipc;
    private Guid? _lease;

    public WrathComboAdapter(PluginServices svc)
    {
        _svc = svc;
        _ipc = new WrathComboIpc(svc.PluginInterface);
        _ipc.SetRevocationHandler(OnRevoked);
    }

    // ---- rotation module ----

    public Task<Result<bool>> IsRotationModuleAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(_ipc.IsReady()));

    public Task<Result<RotationModuleInfo>> GetRotationModule(CancellationToken ct)
        => Task.FromResult<Result<RotationModuleInfo>>(
            Result.Ok(new RotationModuleInfo("WrathCombo", "ipc", LeaseHeld: _lease is not null)));

    public Task<Result<Unit>> StartRotation(CancellationToken ct)
    {
        // Idempotent: if we already hold a lease, no-op.
        if (_lease is not null)
            return Task.FromResult<Result<Unit>>(Result.Ok());

        if (!_ipc.IsReady())
            return Task.FromResult<Result<Unit>>(
                Result.Fail<Unit>("rotationModuleUnavailable", "WrathCombo IPC is not ready"));

        var lease = _ipc.RegisterLease();
        if (lease is not { } g)
            return Task.FromResult<Result<Unit>>(
                Result.Fail<Unit>("leaseDenied", "WrathCombo RegisterForLeaseWithCallback returned null"));

        // Configure the rotation: auto-rotation on, manual DPS targeting (we own targeting),
        // include NPCs so non-player enemies are attacked, attack even when not yet in combat.
        LogSoftFail("SetAutoRotation",    _ipc.SetAutoRotation(g, on: true));
        LogSoftFail("SetJobReady",        _ipc.SetJobReady(g));
        LogSoftFail("SetDpsMode",         _ipc.SetDpsMode(g, WrathComboIpcContract.DPSRotationModeManual));
        LogSoftFail("IncludeNPCs",        _ipc.SetConfigBool(g, WrathComboIpcContract.ConfigIncludeNPCs, true));
        LogSoftFail("InCombatOnly",       _ipc.SetConfigBool(g, WrathComboIpcContract.ConfigInCombatOnly, false));
        LogSoftFail("OnlyAttackInCombat", _ipc.SetConfigBool(g, WrathComboIpcContract.ConfigOnlyAttackInCombat, false));

        _lease = g;
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> StopRotation(CancellationToken ct)
    {
        if (_lease is { } g)
        {
            _ipc.Release(g);
            _lease = null;
        }
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    // ---- targeting ----

    public Task<Result<Unit>> SetTarget(ActorId target, CancellationToken ct)
    {
        var obj = _svc.ObjectTable.SearchById(target.Value);
        if (obj is null)
            return Task.FromResult<Result<Unit>>(
                Result.Fail<Unit>("targetNotFound", $"No live object for GameObjectId {target.Value}"));

        _svc.TargetManager.Target = obj;
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<Unit>> ClearTarget(CancellationToken ct)
    {
        _svc.TargetManager.Target = null;
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    // ---- direct action use (use-action step type — unchanged from part A) ----

    public Task<Result<UseActionOutcome>> UseAction(uint actionId, NpcId? target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    public Task<Result<UseActionOutcome>> UseActionOnObject(uint actionId, InteractableId target, CancellationToken ct)
        => Task.FromResult<Result<UseActionOutcome>>(Result.Ok(UseActionOutcome.Failed));

    public Task<Result<bool>> IsActionUsable(uint actionId, CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    // ---- revocation ----

    private void OnRevoked(int reason)
    {
        // WrathCombo revoked our lease (user cancel, job change, plugin disabled).
        // Drop the stored lease so the next StartRotation re-acquires.
        _lease = null;
    }

    // ---- helpers ----

    private void LogSoftFail(string step, int result)
    {
        if (WrathComboIpcContract.IsSetResultOk(result))
            return;
        // Non-fatal: the lease exists; log but do not fail the call.
        _svc.Log.Warning($"[WrathComboAdapter] {step} returned SetResult={result} (soft-fail; lease is held)");
    }

    public void Dispose()
    {
        if (_lease is { } g)
        {
            _ipc.Release(g);
            _lease = null;
        }
        _ipc.Dispose();
    }
}
