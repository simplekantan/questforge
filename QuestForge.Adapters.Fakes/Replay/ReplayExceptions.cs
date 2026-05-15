namespace QuestForge.Adapters.Fakes.Replay;

public sealed class ReplayObservationStarvationException : Exception
{
    public ReplayObservationStarvationException(string message) : base(message) { }
}

public sealed class ReplayDecisionMismatchException : Exception
{
    public ReplayDecisionMismatchException(string message) : base(message) { }
}
