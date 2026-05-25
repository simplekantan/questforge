using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Replay;

/// <summary>
/// Inert no-op INavigator for trace-replay fixtures. Never mutates any state.
/// Returns benign success so the engine dispatch path does not derail during replay.
/// </summary>
public sealed class InertNavigator : INavigator
{
    public Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination,
        NavigationOptions options,
        CancellationToken ct)
        => Task.FromResult<Result<NavigationOutcome>>(Result.Ok(NavigationOutcome.Arrived));

    public Task<Result<Unit>> Stop(CancellationToken ct)
        => Task.FromResult<Result<Unit>>(Result.Ok());

    public Task<Result<bool>> IsNavigating(CancellationToken ct)
        => Task.FromResult<Result<bool>>(Result.Ok(false));

    public Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct)
        => Task.FromResult<Result<NavmeshInfo>>(Result.Ok(new NavmeshInfo(NavmeshStatus.Ready, null, null)));
}
