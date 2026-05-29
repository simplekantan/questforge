using QuestForge.Adapters.Actions;

namespace QuestForge.Adapters.Tests.Actions;

public class ActionStatusInterpreterTests
{
    // DA5 — Recast fully elapsed, status clean → Ready
    [Fact]
    public void InterpretStatus_RecastFullyElapsed_ReturnsReady()
    {
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 30.0f);

        Assert.IsType<ActionStatus.Ready>(status);
    }

    // DA6 — Mid-cooldown → OnCooldown with correct remaining duration
    [Fact]
    public void InterpretStatus_FiveSecondsRemaining_ReturnsOnCooldownWithRemaining()
    {
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 25.0f);

        var onCooldown = Assert.IsType<ActionStatus.OnCooldown>(status);
        Assert.Equal(5.0, onCooldown.Remaining.TotalSeconds, precision: 3);
    }

    // DA7 — 40 ms remaining (below 50 ms default epsilon) → Ready
    [Fact]
    public void InterpretStatus_FortyMillisecondsRemaining_ReturnsReady_BelowDefaultEpsilon()
    {
        // recast - elapsed = 30.0 - 29.96 = 0.04 s; default epsilon = 0.05 s → Ready.
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 29.96f);

        Assert.IsType<ActionStatus.Ready>(status);
    }

    // DA8 — Non-zero status code → Unusable whose Reason contains the code
    [Fact]
    public void InterpretStatus_NonZeroStatusCode_ReturnsUnusableWithCodeInReason()
    {
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 580u, recastSeconds: 30.0f, elapsedSeconds: 0.0f);

        var unusable = Assert.IsType<ActionStatus.Unusable>(status);
        Assert.Contains("580", unusable.Reason);
    }

    // DA9 — Non-zero status code AND mid-cooldown → status code wins (Unusable, not OnCooldown)
    [Fact]
    public void InterpretStatus_StatusCodeAndCooldown_StatusCodeWins()
    {
        // Decision DAD-5: the status code is the game's authoritative verdict.
        // It supersedes the cooldown read; reporting OnCooldown would be a lie.
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 580u, recastSeconds: 30.0f, elapsedSeconds: 25.0f);

        Assert.IsType<ActionStatus.Unusable>(status);
    }

    // DA10 — elapsed > recast (zone-transition artifact) → Ready, not OnCooldown(-5s)
    [Fact]
    public void InterpretStatus_NegativeRemaining_ReturnsReady_OverflowGuard()
    {
        // remaining = 30 - 35 = -5. Must not crash; must not return OnCooldown(-5 s).
        // Negative remaining is below any reasonable epsilon → falls through to Ready.
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 35.0f);

        Assert.IsType<ActionStatus.Ready>(status);
    }

    // DA10b — Custom epsilon override: 200 ms remaining with 500 ms epsilon → Ready
    [Fact]
    public void InterpretStatus_CustomEpsilon_RespectsOverride()
    {
        // Default epsilon (0.05 s) would report OnCooldown for 0.2 s remaining.
        // Overriding to 0.5 s makes 0.2 s fall inside the epsilon → Ready.
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 0u, recastSeconds: 30.0f, elapsedSeconds: 29.8f,
            readyEpsilonSeconds: 0.5);

        Assert.IsType<ActionStatus.Ready>(status);
    }

    // DA10c — Action with no cooldown (recast = 0, elapsed = 0) → Ready
    [Fact]
    public void InterpretStatus_ZeroRecast_ReturnsReady()
    {
        // remaining = 0 - 0 = 0; 0 <= epsilon → Ready.
        var status = ActionStatusInterpreter.InterpretStatus(
            statusCode: 0u, recastSeconds: 0.0f, elapsedSeconds: 0.0f);

        Assert.IsType<ActionStatus.Ready>(status);
    }

    // DA11 — FormatTargetNotFoundReason produces the exact canonical phrase
    [Fact]
    public void FormatTargetNotFoundReason_IncludesBaseIdInExactPhrase()
    {
        var reason = ActionStatusInterpreter.FormatTargetNotFoundReason(2001234u);

        Assert.Equal("no object in scene with BaseId 2001234", reason);
    }

    // DA11b — Zero BaseId still formats cleanly (defensive; validator rejects in quest data)
    [Fact]
    public void FormatTargetNotFoundReason_ZeroBaseId_FormatsWithZero()
    {
        var reason = ActionStatusInterpreter.FormatTargetNotFoundReason(0u);

        Assert.Equal("no object in scene with BaseId 0", reason);
    }
}
