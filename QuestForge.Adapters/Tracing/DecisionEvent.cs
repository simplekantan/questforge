using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record DecisionEvent : TraceEvent
{
    [JsonIgnore]
    public override string Type => "decision";

    [JsonPropertyOrder(1)] public DecisionData Data { get; init; } = default!;

    public sealed record DecisionData
    {
        public string? StepId { get; init; }
        public string ActionType { get; init; } = "";
    }
}
