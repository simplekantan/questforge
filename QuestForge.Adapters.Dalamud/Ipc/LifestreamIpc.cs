using Dalamud.Plugin;

namespace QuestForge.Adapters.Dalamud.Ipc;

internal sealed class LifestreamIpc
{
    private readonly Func<uint, byte, bool> _teleport;
    private readonly Func<bool> _isBusy;
    private readonly Func<uint, bool> _aethernetTeleportById;

    public LifestreamIpc(IDalamudPluginInterface pi)
    {
        _teleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport").InvokeFunc;
        _isBusy   = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc;
        _aethernetTeleportById = pi.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById").InvokeFunc;
    }

    public bool Teleport(uint aetheryteId, byte subIndex = 0) => _teleport(aetheryteId, subIndex);
    public bool IsBusy() => _isBusy();
    public bool AethernetTeleportById(uint shardId) => _aethernetTeleportById(shardId);
}
