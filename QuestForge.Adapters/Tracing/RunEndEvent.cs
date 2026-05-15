using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record RunEndEvent(
    string RunId,
    string Outcome,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "run.end";
}