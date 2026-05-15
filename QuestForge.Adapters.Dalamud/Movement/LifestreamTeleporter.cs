using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Movement;

public sealed class LifestreamTeleporter : ITeleporter
{
    private readonly PluginServices _svc;

    public LifestreamTeleporter(PluginServices svc) => _svc = svc;

    public Task<Result<TeleportOutcome>> TeleportToAetheryte(AetheryteId destination, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<TeleportOutcome>> TeleportToAethernet(AethernetId destination, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<TeleportOutcome>> UseReturn(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<AetheryteId?>> GetHomeAetheryte(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<TimeSpan>> GetReturnCooldown(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<TimeSpan>> GetTeleportCooldown(CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<long>> EstimateTeleportCost(AetheryteId destination, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<Result<bool>> IsTeleportAvailable(CancellationToken ct)
        => throw new NotImplementedException();
}