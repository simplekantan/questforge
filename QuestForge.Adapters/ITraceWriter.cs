using QuestForge.Adapters.Tracing;

namespace QuestForge.Adapters;

public interface ITraceWriter
{
    void Write(TraceEvent evt);
}