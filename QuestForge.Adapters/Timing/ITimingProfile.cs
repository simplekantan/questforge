namespace QuestForge.Adapters.Timing;

public interface ITimingProfile
{
    TimeSpan ReactionDelay(StimulusType stimulus);
    TimeSpan DecisionDelay(int choiceCount);
    TimeSpan InterActionGap();
    bool ShouldTakeBreak(SessionContext context);
    TimeSpan BreakDuration();
}

public enum StimulusType
{
    Dialogue,
    NewWindow,
    SceneTransition,
    QuestStart,
    CombatStart,
    Generic
}

public record SessionContext(
    TimeSpan TotalSessionDuration,
    int ActionsSinceLastBreak,
    TimeSpan TimeSinceLastBreak
);