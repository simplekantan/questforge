using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

/// <summary>
/// Emitted during authoring when the author confirms a recorded step.
/// Carries the full serialised Step JSON so qf-trace extract-quest can
/// reconstruct quest definitions from the trace without observation correlation.
/// This event is authoring-only; engine run traces do not emit it.
/// </summary>
public sealed record StepRecordedEvent : TraceEvent
{
    [JsonIgnore]
    public override string Type => "step.recorded";

    [JsonPropertyOrder(1)] public StepRecordedData Data { get; init; } = default!;

    public sealed record StepRecordedData
    {
        public string StepId { get; init; } = "";
        public int SequenceNumber { get; init; }
        public JsonElement Step { get; init; }
    }
}
