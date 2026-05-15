using QuestForge.Adapters.Fakes.Recording;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Fakes.Movement;

public sealed class FakeNavigator : INavigator
{
    private readonly FakeGameStateProvider _state;

    private NavigationOutcome? _nextResult;
    private readonly Dictionary<WorldPosition, NavigationOutcome> _keyedResults = new();
    private (string Reason, string? Detail)? _nextFailure;
    private bool _isNavigating;

    public record NavigationRequest(WorldPosition Destination, NavigationOptions Options, DateTimeOffset At)
        : AdapterCall(At);

    public record StopCall(DateTimeOffset At) : AdapterCall(At);

    public CallLog<NavigationRequest> RecordedNavigationRequests { get; } = new();
    public CallLog<StopCall> RecordedStops { get; } = new();

    public FakeNavigator(FakeGameStateProvider state)
    {
        _state = state;
    }

    // ----- Scripting -----
    public void ScriptNextResult(NavigationOutcome outcome) => _nextResult = outcome;
    public void ScriptResultFor(WorldPosition destination, NavigationOutcome outcome) =>
        _keyedResults[destination] = outcome;
    public void FailNextWith(string reason, string? detail = null) =>
        _nextFailure = (reason, detail);
    public void SetIsNavigating(bool navigating) => _isNavigating = navigating;

    // ----- Reset -----
    public void Reset()
    {
        RecordedNavigationRequests.Clear();
        RecordedStops.Clear();
        _nextResult = null;
        _keyedResults.Clear();
        _nextFailure = null;
    }

    // ----- INavigator implementation -----

    public Task<Result<NavigationOutcome>> NavigateTo(
        WorldPosition destination,
        NavigationOptions options,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedNavigationRequests.Add(new NavigationRequest(destination, options, DateTimeOffset.UtcNow));

        // Check one-shot failure
        if (_nextFailure.HasValue)
        {
            var (reason, detail) = _nextFailure.Value;
            _nextFailure = null;
            return Task.FromResult<Result<NavigationOutcome>>(Result.Fail<NavigationOutcome>(reason, detail));
        }

        // Determine outcome
        NavigationOutcome outcome;
        if (_nextResult.HasValue)
        {
            outcome = _nextResult.Value;
            _nextResult = null;
        }
        else if (_keyedResults.TryGetValue(destination, out var keyed))
        {
            outcome = keyed;
        }
        else
        {
            outcome = NavigationOutcome.Arrived;
        }

        // Update state on success
        if (outcome == NavigationOutcome.Arrived)
        {
            _state.SetPosition(destination);
            _isNavigating = false;
        }

        return Task.FromResult<Result<NavigationOutcome>>(Result.Ok(outcome));
    }

    public Task<Result<Unit>> Stop(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecordedStops.Add(new StopCall(DateTimeOffset.UtcNow));
        _isNavigating = false;
        return Task.FromResult<Result<Unit>>(Result.Ok());
    }

    public Task<Result<bool>> IsNavigating(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<bool>>(Result.Ok(_isNavigating));
    }

    public Task<Result<NavmeshInfo>> GetNavmeshInfo(ZoneId zone, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Result<NavmeshInfo>>(Result.Ok(new NavmeshInfo(NavmeshStatus.Ready, null, null)));
    }
}