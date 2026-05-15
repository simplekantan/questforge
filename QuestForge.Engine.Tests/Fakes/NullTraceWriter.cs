using QuestForge.Adapters;
using QuestForge.Adapters.Tracing;

namespace QuestForge.Engine.Tests.Fakes;

public sealed class NullTraceWriter : ITraceWriter
{
    public void Write(TraceEvent evt) { }
}