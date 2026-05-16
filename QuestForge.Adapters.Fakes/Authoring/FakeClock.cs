using QuestForge.Engine.Authoring;

namespace QuestForge.Adapters.Fakes.Authoring;

/// <summary>
/// A deterministic clock for testing. Time advances only when <see cref="Advance"/> is called.
/// </summary>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset startTime)
    {
        _now = startTime;
    }

    public FakeClock() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan amount)
    {
        _now += amount;
    }

    public void SetTime(DateTimeOffset time)
    {
        _now = time;
    }
}
