namespace QuestForge.Adapters.Actions;

public abstract record ActionStatus
{
    public sealed record Ready : ActionStatus;
    public sealed record OnCooldown(TimeSpan Remaining) : ActionStatus;
    public sealed record Unusable(string Reason) : ActionStatus;
}
