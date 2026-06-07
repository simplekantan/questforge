using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Navigation;

public sealed class NavigationWatchdog
{
    private readonly TimeSpan _stallTimeout;
    private readonly float _stallDistance;
    private readonly int _maxJumps;
    private readonly TimeProvider _clock;

    private WorldPosition? _stallOrigin;
    private DateTimeOffset? _stallStartedAt;
    private int _jumpCount;

    public NavigationWatchdog(
        TimeSpan stallTimeout,
        float stallDistance,
        int maxJumps,
        TimeProvider clock)
    {
        _stallTimeout = stallTimeout;
        _stallDistance = stallDistance;
        _maxJumps = maxJumps;
        _clock = clock;
    }

    /// <summary>
    /// Called once per tick. Returns advice on how to proceed.
    /// </summary>
    /// <param name="playerPos">Current player position.</param>
    /// <param name="isNavigating">True when the engine resolved a Navigate action this tick.</param>
    public WatchdogAdvice Update(WorldPosition playerPos, bool isNavigating)
    {
        if (!isNavigating)
        {
            Reset();
            return WatchdogAdvice.Idle;
        }

        // No stall window open yet — open one
        if (_stallOrigin is null)
        {
            _stallOrigin = playerPos;
            _stallStartedAt = _clock.GetUtcNow();
            return WatchdogAdvice.Continue;
        }

        // Stall window is open
        if (playerPos.DistanceTo(_stallOrigin.Value) > _stallDistance)
        {
            // Player moved enough — full reset, re-open window
            Reset();
            _stallOrigin = playerPos;
            _stallStartedAt = _clock.GetUtcNow();
            return WatchdogAdvice.Continue;
        }

        var elapsed = _clock.GetUtcNow() - _stallStartedAt!.Value;

        if (elapsed < _stallTimeout)
            return WatchdogAdvice.Continue;

        // Stall timeout reached
        if (_jumpCount < _maxJumps)
        {
            _jumpCount++;
            _stallStartedAt = _clock.GetUtcNow(); // reset grace period
            return WatchdogAdvice.Jump;
        }

        // All jump attempts exhausted
        Reset();
        return WatchdogAdvice.CastReturn;
    }

    /// <summary>Full reset: clears stall origin, jump count, and stall window.</summary>
    public void Reset()
    {
        _stallOrigin = null;
        _stallStartedAt = null;
        _jumpCount = 0;
    }
}
