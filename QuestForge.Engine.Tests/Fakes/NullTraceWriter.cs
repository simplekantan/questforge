using QuestForge.Adapters;

namespace QuestForge.Engine.Tests.Fakes;

public sealed class NullTraceWriter : ITraceWriter
{
    public void Write(object evt) { }
}