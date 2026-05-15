using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record ObservationEvent(
    string? RunId,
    string Method,
    JsonElement? Argument,
    JsonElement? Value,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "observation";
}