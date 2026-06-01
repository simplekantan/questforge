using System.Collections;

namespace QuestForge.Adapters.Fakes.Recording;

public sealed class CallLog<T> : IEnumerable<T> where T : AdapterCall
{
    private readonly List<T> _calls = new();
    private readonly object _lock = new();

    public void Add(T call) { lock (_lock) _calls.Add(call); }
    public IReadOnlyList<T> Snapshot() { lock (_lock) return _calls.ToArray(); }
    public int Count { get { lock (_lock) return _calls.Count; } }
    public T this[int index] { get { lock (_lock) return _calls[index]; } }
    public void Clear() { lock (_lock) _calls.Clear(); }

    public IEnumerator<T> GetEnumerator()
    {
        T[] snapshot;
        lock (_lock) snapshot = _calls.ToArray();
        return ((IEnumerable<T>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
