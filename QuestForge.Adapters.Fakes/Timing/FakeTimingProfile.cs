using QuestForge.Adapters.Timing;

namespace QuestForge.Adapters.Fakes.Timing;

public sealed class FakeTimingProfile : ITimingProfile
{
    private readonly Dictionary<StimulusType, TimeSpan> _delays = new();
    private TimeSpan _decisionDelay = TimeSpan.Zero;
    private TimeSpan _interActionGap = TimeSpan.FromMilliseconds(50);
    private bool _shouldTakeBreak;
    private TimeSpan _breakDuration = TimeSpan.FromMinutes(2);

    // ----- Scripting -----
    public void SetDelayFor(StimulusType stimulus, TimeSpan delay) => _delays[stimulus] = delay;
    public void SetDecisionDelay(TimeSpan delay) => _decisionDelay = delay;
    public void SetInterActionGap(TimeSpan gap) => _interActionGap = gap;
    public void SetShouldTakeBreak(bool value) => _shouldTakeBreak = value;
    public void SetBreakDuration(TimeSpan duration) => _breakDuration = duration;

    // ----- Reset -----
    public void Reset()
    {
        _delays.Clear();
        _decisionDelay = TimeSpan.Zero;
        _interActionGap = TimeSpan.FromMilliseconds(50);
        _shouldTakeBreak = false;
        _breakDuration = TimeSpan.FromMinutes(2);
    }

    // ----- ITimingProfile implementation -----

    public TimeSpan ReactionDelay(StimulusType stimulus) =>
        _delays.TryGetValue(stimulus, out var delay) ? delay : TimeSpan.Zero;

    public TimeSpan DecisionDelay(int choiceCount) => _decisionDelay;

    public TimeSpan InterActionGap() => _interActionGap;

    public bool ShouldTakeBreak(SessionContext context) => _shouldTakeBreak;

    public TimeSpan BreakDuration() => _breakDuration;
}