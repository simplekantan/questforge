using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record ObservationEvent : TraceEvent
{
    [JsonIgnore]
    public override string Type => "observation";

    [JsonPropertyOrder(1)] public ObservationData Data { get; init; } = default!;

    public sealed record ObservationData
    {
        public string Method { get; init; } = "";
        public JsonElement? Argument { get; init; }
        public JsonElement? Value { get; init; }
    }
}
