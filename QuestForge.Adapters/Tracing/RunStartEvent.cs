using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record RunStartEvent(
    string RunId,
    uint QuestId,
    uint QuestSchemaId,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "run.start";
}