using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record ActionCompletedEvent(
    string RunId,
    string ActionType,
    string Outcome,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "action.completed";
}