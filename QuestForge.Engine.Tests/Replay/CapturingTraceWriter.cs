using QuestForge.Adapters;
using QuestForge.Adapters.Tracing;

namespace QuestForge.Engine.Tests.Replay;

/// <summary>
/// Test helper: buffers every TraceEvent written to it in memory.
/// Used by replay tests to inspect DecisionEvents emitted by the engine during Tick()
/// so the test can extract (stepId, actionType) pairs after each tick without the engine
/// needing to expose those fields on the returned EngineAction.
/// </summary>
internal sealed class CapturingTraceWriter : ITraceWriter
{
    public List<TraceEvent> Events { get; } = new();
    public void Write(TraceEvent evt) => Events.Add(evt);
}