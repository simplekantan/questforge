using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Movement;

public interface INavigator
{
    Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination,
        NavigationOptions options,
        CancellationToken ct);

    Task<Result<Unit>> Stop(CancellationToken ct);
    Task<Result<Unit>> Jump(CancellationToken ct);
    Task<Result<bool>> IsNavigating(CancellationToken ct);
    Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct);
}

public record NavigationOptions(
    float StoppingDistance = 3.0f,
    bool UseMount = true,
    bool UseFlight = true,
    TimeSpan? Timeout = null
);

public enum NavigationOutcome
{
    Arrived,
    StoppedByObstacle,
    NavmeshUnavailable,
    Interrupted,
    TimedOut,
    PlayerDied
}

public record NavmeshInfo(
    NavmeshStatus Status,
    float? GenerationProgress,
    TimeSpan? EstimatedTimeRemaining
);

public enum NavmeshStatus
{
    Ready,
    Generating,
    NotStarted,
    Failed,
    Unsupported
}