namespace QuestForge.Engine.Predicates;

public sealed class UnknownStateFunctionException : Exception
{
    public string FunctionName { get; }

    public UnknownStateFunctionException(string functionName)
        : base($"Unknown state function: '{functionName}'")
    {
        FunctionName = functionName;
    }

    public UnknownStateFunctionException(string functionName, string message)
        : base(message)
    {
        FunctionName = functionName;
    }
}