using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

/// <summary>
/// Emitted during authoring when the author confirms a recorded step.
/// Carries the full serialised Step JSON so qf-trace extract-quest can
/// reconstruct quest definitions from the trace without observation correlation.
/// This event is authoring-only; engine run traces do not emit it.
/// </summary>
public sealed record StepRecordedEvent(
    string  RunId,
    string  StepId,
    int     SequenceNumber,
    JsonElement Step,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "step.recorded";
}
