using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record ActionSubmittedEvent(
    string RunId,
    string ActionType,
    JsonElement? Parameters,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "action.submitted";
}