using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record ActionSubmittedEvent : TraceEvent
{
    [JsonIgnore]
    public override string Type => "action.submitted";

    [JsonPropertyOrder(1)] public ActionSubmittedData Data { get; init; } = default!;

    public sealed record ActionSubmittedData
    {
        public string ActionType { get; init; } = "";
        public JsonElement? Parameters { get; init; }
    }
}
