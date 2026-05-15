using QuestForge.Adapters.Timing;

namespace QuestForge.Adapters.Dalamud.Timing;

// Pure logic — no Dalamud services. Reseed per run via EngineHost.BeginRun.
public sealed class SeededTimingProfile : ITimingProfile
{
    public SeededTimingProfile(int seed) { }

    public void Reseed(int seed) { }

    // ITimingProfile

    public TimeSpan ReactionDelay(StimulusType stimulus)
        => throw new NotImplementedException();

    public TimeSpan DecisionDelay(int choiceCount)
        => throw new NotImplementedException();

    public TimeSpan InterActionGap()
        => throw new NotImplementedException();

    public bool ShouldTakeBreak(SessionContext context)
        => throw new NotImplementedException();

    public TimeSpan BreakDuration()
        => throw new NotImplementedException();
}