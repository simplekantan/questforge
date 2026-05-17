using QuestForge.Adapters.Dalamud.Ipc;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Dalamud.Movement;

public sealed class LifestreamTeleporter : ITeleporter
{
    private readonly LifestreamIpc _ipc;

    public LifestreamTeleporter(PluginServices svc)
        => _ipc = new LifestreamIpc(svc.PluginInterface);

    public Task<Result<TeleportOutcome>> TeleportToAetheryte(AetheryteId destination, CancellationToken ct)
    {
        if (_ipc.IsBusy())
            return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.OnCooldown));

        var ok = _ipc.Teleport(destination.Value, subIndex: 0);
        if (!ok)
            // Lifestream returns false for: not attuned, cooldown active, or busy — no distinction
            return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.NotAttuned));

        // Fire-and-forget: Lifestream accepted; engine re-reads GetPlayerZone() next tick to detect arrival.
        // Polling here would violate the framework-thread invariant — no awaiting across ticks.
        return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.Arrived));
    }

    public Task<Result<TeleportOutcome>> TeleportToAethernet(AethernetId destination, CancellationToken ct)
    {
        if (_ipc.IsBusy())
            return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.OnCooldown));

        var ok = _ipc.AethernetTeleportById(destination.Value);
        if (!ok)
            // Lifestream returns false for: not attuned, cooldown active, or busy — no distinction
            return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.NotAttuned));

        // Fire-and-forget: Lifestream accepted; engine re-reads GetPlayerZone() next tick to detect arrival.
        return Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.Arrived));
    }

    public Task<Result<TeleportOutcome>> UseReturn(CancellationToken ct)
        => Task.FromResult<Result<TeleportOutcome>>(Result.Fail<TeleportOutcome>("notImplemented", "Phase 6 placeholder"));

    public Task<Result<AetheryteId?>> GetHomeAetheryte(CancellationToken ct)
        => Task.FromResult<Result<AetheryteId?>>(Result.Ok<AetheryteId?>(null));

    public Task<Result<TimeSpan>> GetReturnCooldown(CancellationToken ct)
        => Task.FromResult<Result<TimeSpan>>(Result.Ok(TimeSpan.Zero));

    public Task<Result<TimeSpan>> GetTeleportCooldown(CancellationToken ct)
        => Task.FromResult<Result<TimeSpan>>(Result.Ok(TimeSpan.Zero));

    public Task<Result<long>> EstimateTeleportCost(AetheryteId destination, CancellationToken ct)
        => Task.FromResult<Result<long>>(Result.Ok(0L));

    public Task<Result<bool>> IsTeleportAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(true));
}
