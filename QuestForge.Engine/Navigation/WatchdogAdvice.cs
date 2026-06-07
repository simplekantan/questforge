namespace QuestForge.Engine.Navigation;

public enum WatchdogAdvice
{
    /// <summary>Not navigating; watchdog inactive.</summary>
    Idle,

    /// <summary>Navigating normally; no stall detected.</summary>
    Continue,

    /// <summary>Stall detected; attempt a jump.</summary>
    Jump,

    /// <summary>All jump attempts exhausted; escalate to Return.</summary>
    CastReturn
}
