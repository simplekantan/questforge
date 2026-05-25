using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Inert no-op ITeleporter for trace-replay fixtures. Never mutates any state.
/// Returns benign success for all calls.
/// </summary>
public sealed class InertTeleporter : ITeleporter
{
    public Task<Result<TeleportOutcome>> TeleportToAetheryte(AetheryteId destination, CancellationToken ct)
        => Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.Arrived));

    public Task<Result<TeleportOutcome>> TeleportToAethernet(AethernetId destination, CancellationToken ct)
        => Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.Arrived));

    public Task<Result<TeleportOutcome>> UseReturn(CancellationToken ct)
        => Task.FromResult<Result<TeleportOutcome>>(Result.Ok(TeleportOutcome.Arrived));

    public Task<Result<AetheryteId?>> GetHomeAetheryte(CancellationToken ct)
        => Task.FromResult<Result<AetheryteId?>>(Result.Ok((AetheryteId?)null));

    public Task<Result<TimeSpan>> GetReturnCooldown(CancellationToken ct)
        => Task.FromResult<Result<TimeSpan>>(Result.Ok(TimeSpan.Zero));

    public Task<Result<TimeSpan>> GetTeleportCooldown(CancellationToken ct)
        => Task.FromResult<Result<TimeSpan>>(Result.Ok(TimeSpan.Zero));

    public Task<Result<long>> EstimateTeleportCost(AetheryteId destination, CancellationToken ct)
        => Task.FromResult<Result<long>>(Result.Ok(0L));

    public Task<Result<bool>> IsTeleportAvailable(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(true));
}
