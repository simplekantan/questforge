using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Movement;

public interface ITeleporter
{
    Task<Result<TeleportOutcome>> TeleportToAetheryte(AetheryteId destination, CancellationToken ct);
    Task<Result<TeleportOutcome>> TeleportToAethernet(AethernetId destination, CancellationToken ct);
    Task<Result<TeleportOutcome>> UseReturn(CancellationToken ct);

    Task<Result<AetheryteId?>> GetHomeAetheryte(CancellationToken ct);
    Task<Result<TimeSpan>> GetReturnCooldown(CancellationToken ct);
    Task<Result<TimeSpan>> GetTeleportCooldown(CancellationToken ct);
    Task<Result<long>> EstimateTeleportCost(AetheryteId destination, CancellationToken ct);
    Task<Result<bool>> IsTeleportAvailable(CancellationToken ct);
}

public enum TeleportOutcome
{
    Arrived,
    InsufficientGil,
    NotAttuned,
    OnCooldown,
    Interrupted,
    InCombat,
    InInstance,
    Failed
}