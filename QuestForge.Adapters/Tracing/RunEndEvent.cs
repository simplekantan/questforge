using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record RunEndEvent : TraceEvent
{
    [JsonIgnore]
    public override string Type => "run.end";

    [JsonPropertyOrder(1)] public RunEndData Data { get; init; } = default!;

    public sealed record RunEndData
    {
        public string Outcome { get; init; } = "";
    }
}
