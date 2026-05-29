namespace QuestForge.Adapters.Actions;

public static class ActionStatusInterpreter
{
    /// <summary>
    /// Default epsilon for "is the action effectively off cooldown?" — 50 ms.
    /// Below this threshold, treat the action as Ready (the game accepts the call within
    /// a few frames of the recast completing; insisting on exactly-zero remaining produces
    /// spurious extra ticks at the cooldown boundary).
    /// </summary>
    public const double DefaultReadyEpsilonSeconds = 0.05;

    /// <summary>
    /// Interprets the raw triplet returned by FFXIVClientStructs ActionManager into the
    /// engine-facing ActionStatus union.
    ///
    /// PRECEDENCE (this order is load-bearing — see DAD-5):
    ///   1. statusCode != 0 → Unusable("status code {statusCode}")
    ///   2. remaining = recastSeconds - elapsedSeconds; if remaining > epsilon → OnCooldown(remaining)
    ///   3. otherwise → Ready (negative remaining handled gracefully — overflow guard)
    /// </summary>
    public static ActionStatus InterpretStatus(
        uint statusCode,
        float recastSeconds,
        float elapsedSeconds,
        double readyEpsilonSeconds = DefaultReadyEpsilonSeconds)
    {
        if (statusCode != 0)
            return new ActionStatus.Unusable($"status code {statusCode}");

        var remainingSeconds = (double)recastSeconds - (double)elapsedSeconds;
        if (remainingSeconds > readyEpsilonSeconds)
            return new ActionStatus.OnCooldown(TimeSpan.FromSeconds(remainingSeconds));

        return new ActionStatus.Ready();
    }

    /// <summary>
    /// Canonical phrasing for the "ObjectTable scan returned no match" failure case.
    /// Format: "no object in scene with BaseId {baseId}"
    /// </summary>
    public static string FormatTargetNotFoundReason(uint baseId)
        => $"no object in scene with BaseId {baseId}";
}
