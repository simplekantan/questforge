using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace QuestForge.Adapters.Dalamud.Ipc;

/// <summary>
/// Raw Dalamud IPC wrapper for WrathCombo — no reference to WrathCombo.API.
/// Gate names and enum int-values are defined in <see cref="WrathComboIpcContract"/>.
/// </summary>
internal sealed class WrathComboIpc : IDisposable
{
    private const string InternalName   = "QuestForge";
    private const string DisplayName    = "QuestForge";
    private const string CallbackPrefix = "QuestForge";

    // Subscribers — captured in ctor; InvokeFunc/InvokeAction called on demand.
    private readonly Func<bool>                         _ipcReady;
    private readonly Func<string, string, string?, Guid?> _registerWithCallback;
    private readonly Func<Guid, bool, int>              _setAutoRotationState;
    private readonly Func<Guid, int>                    _setCurrentJobReady;
    private readonly Func<Guid, object, object, int>    _setAutoRotationConfigState;
    private readonly Action<Guid>                       _releaseControl;

    // Callback provider — WrathCombo calls back into this when revoking our lease.
    private readonly ICallGateProvider<int, string, object> _callbackProvider;
    private Action<int>? _onRevoked;

    public WrathComboIpc(IDalamudPluginInterface pi)
    {
        _ipcReady = pi
            .GetIpcSubscriber<bool>(WrathComboIpcContract.GateIPCReady)
            .InvokeFunc;

        _registerWithCallback = pi
            .GetIpcSubscriber<string, string, string?, Guid?>(
                WrathComboIpcContract.GateRegisterForLeaseWithCallback)
            .InvokeFunc;

        _setAutoRotationState = pi
            .GetIpcSubscriber<Guid, bool, int>(
                WrathComboIpcContract.GateSetAutoRotationState)
            .InvokeFunc;

        _setCurrentJobReady = pi
            .GetIpcSubscriber<Guid, int>(
                WrathComboIpcContract.GateSetCurrentJobAutoRotationReady)
            .InvokeFunc;

        // SetAutoRotationConfigState(Guid lease, object option, object value) -> int
        // Both the AutoRotationConfigOption enum and the value are passed as object
        // on the wire; WrathCombo unboxes them internally.
        _setAutoRotationConfigState = pi
            .GetIpcSubscriber<Guid, object, object, int>(
                WrathComboIpcContract.GateSetAutoRotationConfigState)
            .InvokeFunc;

        // ReleaseControl(Guid) -> void; the gate is registered as Action<Guid>
        // but Dalamud IPC requires at least one type parameter for InvokeAction,
        // so we use ICallGateSubscriber<Guid, object> and InvokeAction.
        _releaseControl = pi
            .GetIpcSubscriber<Guid, object>(
                WrathComboIpcContract.GateReleaseControl)
            .InvokeAction;

        _callbackProvider = pi.GetIpcProvider<int, string, object>(
            $"{CallbackPrefix}.WrathComboCallback");
        _callbackProvider.RegisterAction(OnCallback);
    }

    public bool IsReady()
    {
        try   { return _ipcReady(); }
        catch { return false; }
    }

    public Guid? RegisterLease()
    {
        try   { return _registerWithCallback(InternalName, DisplayName, CallbackPrefix); }
        catch { return null; }
    }

    /// <returns>The raw SetResult int; use <see cref="WrathComboIpcContract.IsSetResultOk"/>.</returns>
    public int SetAutoRotation(Guid lease, bool on)
    {
        try   { return _setAutoRotationState(lease, on); }
        catch { return -1; }
    }

    public int SetJobReady(Guid lease)
    {
        try   { return _setCurrentJobReady(lease); }
        catch { return -1; }
    }

    /// <summary>
    /// Sets DPSRotationMode on the lease.
    /// Pass <see cref="WrathComboIpcContract.DPSRotationModeManual"/> as <paramref name="modeInt"/>.
    /// </summary>
    public int SetDpsMode(Guid lease, int modeInt)
        => SetConfigInt(lease, WrathComboIpcContract.ConfigDPSRotationMode, modeInt);

    public int SetConfigBool(Guid lease, int optionInt, bool value)
        => SetConfig(lease, optionInt, (object)value);

    private int SetConfigInt(Guid lease, int optionInt, int value)
        => SetConfig(lease, optionInt, (object)value);

    private int SetConfig(Guid lease, int optionInt, object value)
    {
        try   { return _setAutoRotationConfigState(lease, (object)optionInt, value); }
        catch { return -1; }
    }

    public void Release(Guid lease)
    {
        try   { _releaseControl(lease); }
        catch { /* best-effort: plugin may be unloading */ }
    }

    public void SetRevocationHandler(Action<int>? handler)
        => _onRevoked = handler;

    private void OnCallback(int reason, string info)
        => _onRevoked?.Invoke(reason);

    public void Dispose()
    {
        try { _callbackProvider.UnregisterAction(); }
        catch { /* best-effort: plugin may already be unloading */ }
    }
}
