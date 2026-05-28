using QuestForge.Adapters.Movement;

namespace QuestForge.Adapters.Fakes.Movement;

/// <summary>
/// In-memory fake for IMount. Counts calls so tests can assert mount/dismount behaviour.
/// Mirrors FakeVendor.CloseCallCount convention.
/// </summary>
public sealed class FakeMount : IMount
{
    public int MountCallCount { get; private set; }
    public int DismountCallCount { get; private set; }

    public Task Mount(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MountCallCount++;
        return Task.CompletedTask;
    }

    public Task Dismount(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DismountCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>Resets both counters to zero — use between sub-tests in a single harness instance.</summary>
    public void Reset()
    {
        MountCallCount = 0;
        DismountCallCount = 0;
    }
}
