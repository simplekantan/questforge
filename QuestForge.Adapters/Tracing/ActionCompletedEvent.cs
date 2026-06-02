using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record ActionCompletedEvent : TraceEvent
{
    [JsonIgnore]
    public override string Type => "action.completed";

    [JsonPropertyOrder(1)] public ActionCompletedData Data { get; init; } = default!;

    public sealed record ActionCompletedData
    {
        public string ActionType { get; init; } = "";
        public string Outcome { get; init; } = "";
    }
}
