using QuestForge.Adapters.Tracing;

namespace QuestForge.Adapters;

/// <summary>
/// No-op ITraceWriter used when tracing is disabled (the default for normal users).
/// Authoring mode and the /qf config trace on command substitute a real TraceWriter.
/// </summary>
public sealed class NullTraceWriter : ITraceWriter
{
    public static readonly NullTraceWriter Instance = new();
    public void Write(TraceEvent evt) { }
}
