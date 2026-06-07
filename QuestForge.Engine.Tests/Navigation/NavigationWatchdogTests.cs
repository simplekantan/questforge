using QuestForge.Adapters.Types;
using QuestForge.Engine.Navigation;
using Xunit;

namespace QuestForge.Engine.Tests.Navigation;

/// <summary>
/// RED PHASE: NavigationWatchdog unit tests (NW1--NW12).
///
/// All tests fail to compile until Builder creates:
///   - QuestForge.Engine/Navigation/WatchdogAdvice.cs (enum)
///   - QuestForge.Engine/Navigation/NavigationWatchdog.cs (class)
///
/// These tests exercise the watchdog in isolation (no engine, no harness).
/// Time is controlled via ManualTimeProvider (hand-rolled TimeProvider subclass).
///
/// Spec: docs/NAV_STUCK_DETECTION_SPEC.md section 3.1
/// </summary>
public sealed class NavigationWatchdogTests
{
    // Stable epoch for all tests.
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // Minimal hand-rolled TimeProvider for deterministic time control.
    // Mirrors the ManualTimeProvider in WaitStepTests.
    // -------------------------------------------------------------------------

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTimeOffset startTime) => _now = startTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a watchdog with the specified thresholds (defaulting to the production defaults).
    /// </summary>
    private static (NavigationWatchdog watchdog, ManualTimeProvider clock) CreateWatchdog(
        double stallTimeoutSeconds = 5.0,
        float stallDistance = 2.0f,
        int maxJumps = 3)
    {
        var clock = new ManualTimeProvider(T0);
        var watchdog = new NavigationWatchdog(
            TimeSpan.FromSeconds(stallTimeoutSeconds),
            stallDistance,
            maxJumps,
            clock);
        return (watchdog, clock);
    }

    // =========================================================================
    // NW1 -- Normal navigation, position changes every tick
    // =========================================================================

    [Fact]
    public void NW1_NormalNavigation_AlwaysContinue()
    {
        // Setup: defaults (5s timeout, 2m distance, 3 max jumps)
        var (watchdog, clock) = CreateWatchdog();

        // Simulate 20 ticks at 250ms each, moving 3m along X each tick
        for (int i = 0; i < 20; i++)
        {
            var pos = new WorldPosition(i * 3f, 0f, 0f);
            var advice = watchdog.Update(pos, isNavigating: true);
            Assert.Equal(WatchdogAdvice.Continue, advice);
            clock.Advance(TimeSpan.FromMilliseconds(250));
        }
    }

    // =========================================================================
    // NW2 -- Stall detected at 5 seconds
    // =========================================================================

    [Fact]
    public void NW2_StallDetectedAt5Seconds_ReturnsJump()
    {
        var (watchdog, clock) = CreateWatchdog();
        var pos = new WorldPosition(0f, 0f, 0f);

        // Step 1: open stall window
        var advice1 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, advice1);

        // Step 2: advance 4.9s -- still under threshold
        clock.Advance(TimeSpan.FromSeconds(4.9));
        var advice2 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, advice2);

        // Step 3: advance 0.2s more (total 5.1s) -- over threshold
        clock.Advance(TimeSpan.FromSeconds(0.2));
        var advice3 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, advice3);
    }

    // =========================================================================
    // NW3 -- Grace period after Jump: no re-detection for 5s
    // =========================================================================

    [Fact]
    public void NW3_GracePeriodAfterJump_NoRedetectionFor5s()
    {
        var (watchdog, clock) = CreateWatchdog();
        var pos = new WorldPosition(0f, 0f, 0f);

        // Drive to first Jump at 5s
        watchdog.Update(pos, isNavigating: true);
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var firstJump = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, firstJump);

        // Within grace period (4.9s after jump): should Continue
        clock.Advance(TimeSpan.FromSeconds(4.9));
        var duringGrace = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, duringGrace);

        // Past grace period (5.1s after jump): should Jump again
        clock.Advance(TimeSpan.FromSeconds(0.2));
        var afterGrace = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, afterGrace);
    }

    // =========================================================================
    // NW4 -- Three jumps exhausted, then CastReturn
    // =========================================================================

    [Fact]
    public void NW4_ThreeJumpsExhausted_ReturnsCastReturn()
    {
        var (watchdog, clock) = CreateWatchdog();
        var pos = new WorldPosition(0f, 0f, 0f);

        // Jump 1: open window, stall for 5s
        watchdog.Update(pos, isNavigating: true);
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var jump1 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, jump1);

        // Jump 2: stall another 5s
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var jump2 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, jump2);

        // Jump 3: stall another 5s
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var jump3 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, jump3);

        // All 3 jumps exhausted -- next timeout should CastReturn
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var castReturn = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.CastReturn, castReturn);
    }

    // =========================================================================
    // NW5 -- Jump succeeds (position changes > 2m), counter resets
    // =========================================================================

    [Fact]
    public void NW5_JumpSucceeds_PositionChanges_CounterResets()
    {
        var (watchdog, clock) = CreateWatchdog();
        var pos = new WorldPosition(0f, 0f, 0f);

        // Drive to first Jump
        watchdog.Update(pos, isNavigating: true);
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var jump1 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, jump1);

        // Player moves 5m (> 2m threshold) -- reset occurs
        clock.Advance(TimeSpan.FromSeconds(1));
        var movedPos = new WorldPosition(5f, 0f, 0f);
        var afterMove = watchdog.Update(movedPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, afterMove);

        // Stall again for 5s at new position -- should be FIRST jump (counter was reset)
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var secondStallJump = watchdog.Update(movedPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, secondStallJump);
    }

    // =========================================================================
    // NW6 -- Non-Navigate action causes full reset
    // =========================================================================

    [Fact]
    public void NW6_NonNavigateAction_FullReset()
    {
        var (watchdog, clock) = CreateWatchdog();
        var pos = new WorldPosition(0f, 0f, 0f);

        // Navigate for 3s (building toward stall but not triggered)
        watchdog.Update(pos, isNavigating: true);
        clock.Advance(TimeSpan.FromSeconds(3));
        watchdog.Update(pos, isNavigating: true);

        // Non-navigate action
        var idle = watchdog.Update(pos, isNavigating: false);
        Assert.Equal(WatchdogAdvice.Idle, idle);

        // Resume navigating -- opens a fresh window
        var freshWindow = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, freshWindow);

        // Stall for 5s from the NEW window (not carried over from step 1)
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var jumpAfterReset = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, jumpAfterReset);
    }

    // =========================================================================
    // NW7 -- Not navigating returns Idle
    // =========================================================================

    [Fact]
    public void NW7_NotNavigating_ReturnsIdle()
    {
        var (watchdog, _) = CreateWatchdog();
        var advice = watchdog.Update(new WorldPosition(10f, 20f, 30f), isNavigating: false);
        Assert.Equal(WatchdogAdvice.Idle, advice);
    }

    // =========================================================================
    // NW9 -- Custom thresholds respected
    // =========================================================================

    [Fact]
    public void NW9_CustomThresholds_Respected()
    {
        // 10s timeout, 5m distance, 5 max jumps
        var (watchdog, clock) = CreateWatchdog(
            stallTimeoutSeconds: 10.0,
            stallDistance: 5.0f,
            maxJumps: 5);

        var pos = new WorldPosition(0f, 0f, 0f);

        // 9.9s of navigation at (0,0,0) -- still Continue
        watchdog.Update(pos, isNavigating: true);
        clock.Advance(TimeSpan.FromSeconds(9.9));
        var beforeTimeout = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, beforeTimeout);

        // Past 10s -- Jump fires
        clock.Advance(TimeSpan.FromSeconds(0.2));
        var jump1 = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, jump1);

        // Move 4m (below 5m threshold) -- still considered stalled
        clock.Advance(TimeSpan.FromSeconds(10.1));
        var nearPos = new WorldPosition(4f, 0f, 0f);
        var jump2 = watchdog.Update(nearPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, jump2);

        // Move to 10m from (4,0,0) = (10,0,0) -- 6m > 5m threshold, reset
        var farPos = new WorldPosition(10f, 0f, 0f);
        var afterBigMove = watchdog.Update(farPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, afterBigMove);

        // Exhaust 5 jumps (5 x 10s cycles at stationary position)
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10.1));
            var j = watchdog.Update(farPos, isNavigating: true);
            Assert.Equal(WatchdogAdvice.Jump, j);
        }

        // 6th timeout -- CastReturn
        clock.Advance(TimeSpan.FromSeconds(10.1));
        var castReturn = watchdog.Update(farPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.CastReturn, castReturn);
    }

    // =========================================================================
    // NW10 -- Stall threshold is strict boundary (>= comparison)
    // =========================================================================

    [Fact]
    public void NW10_StallThresholdStrictBoundary()
    {
        var (watchdog, clock) = CreateWatchdog(stallTimeoutSeconds: 5.0, stallDistance: 2.0f);
        var pos = new WorldPosition(0f, 0f, 0f);

        // Open window at T=0
        watchdog.Update(pos, isNavigating: true);

        // At T=4.999s -- just under, should Continue
        clock.Advance(TimeSpan.FromSeconds(4.999));
        var justUnder = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, justUnder);

        // At T=5.0s (exactly at) -- should Jump (>= comparison)
        clock.Advance(TimeSpan.FromSeconds(0.001));
        var exactlyAt = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, exactlyAt);
    }

    // =========================================================================
    // NW11 -- Distance threshold is strict boundary
    // =========================================================================

    [Fact]
    public void NW11_DistanceThresholdStrictBoundary()
    {
        var (watchdog, clock) = CreateWatchdog(stallTimeoutSeconds: 5.0, stallDistance: 2.0f);
        var origin = new WorldPosition(0f, 0f, 0f);

        // Open window
        watchdog.Update(origin, isNavigating: true);

        // Stall for 5.1s, then check with 1.9m movement (< 2m, still stalled)
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var nearPos = new WorldPosition(1.9f, 0f, 0f);
        var stillStalled = watchdog.Update(nearPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, stillStalled);

        // After jump grace, check with 2.1m from stall origin (> 2m, reset)
        var farPos = new WorldPosition(2.1f, 0f, 0f);
        var reset = watchdog.Update(farPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, reset);
    }

    // =========================================================================
    // NW12 -- CastReturn causes full reset, fresh window on next navigate
    // =========================================================================

    [Fact]
    public void NW12_CastReturnCausesFullReset_FreshWindowOnNextNavigate()
    {
        var (watchdog, clock) = CreateWatchdog();
        var pos = new WorldPosition(0f, 0f, 0f);

        // Exhaust 3 jumps and get CastReturn
        watchdog.Update(pos, isNavigating: true);
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5.1));
            watchdog.Update(pos, isNavigating: true); // Jump
        }
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var castReturn = watchdog.Update(pos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.CastReturn, castReturn);

        // After Return, new position (100,0,0) -- fresh window
        var newPos = new WorldPosition(100f, 0f, 0f);
        var freshStart = watchdog.Update(newPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Continue, freshStart);

        // Stall for 5s at new position -- first jump of new sequence
        clock.Advance(TimeSpan.FromSeconds(5.1));
        var firstJumpNewSequence = watchdog.Update(newPos, isNavigating: true);
        Assert.Equal(WatchdogAdvice.Jump, firstJumpNewSequence);
    }
}
