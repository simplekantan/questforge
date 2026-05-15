using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record DecisionEvent(
    string RunId,
    string? StepId,
    string ActionType,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "decision";
}