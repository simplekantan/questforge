using QuestForge.Adapters.Tracing;

namespace QuestForge.Adapters.Fakes;

public sealed class FakeTraceWriter : ITraceWriter
{
    private readonly List<TraceEvent> _events = new();
    private readonly object _lock = new();

    public void Write(TraceEvent evt) { lock (_lock) _events.Add(evt); }
    public IReadOnlyList<TraceEvent> RecordedEvents { get { lock (_lock) return _events.ToArray(); } }
    public int Count { get { lock (_lock) return _events.Count; } }
    public void Reset() { lock (_lock) _events.Clear(); }
}