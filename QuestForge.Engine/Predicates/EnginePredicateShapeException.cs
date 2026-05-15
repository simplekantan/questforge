namespace QuestForge.Engine.Predicates;

public sealed class EnginePredicateShapeException : Exception
{
    public EnginePredicateShapeException(string message) : base(message) { }
    public EnginePredicateShapeException(string message, Exception inner) : base(message, inner) { }
}