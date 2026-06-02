using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartEvent),        "run.start")]
[JsonDerivedType(typeof(RunEndEvent),          "run.end")]
[JsonDerivedType(typeof(ObservationEvent),     "observation")]
[JsonDerivedType(typeof(DecisionEvent),        "decision")]
[JsonDerivedType(typeof(ActionSubmittedEvent), "action.submitted")]
[JsonDerivedType(typeof(ActionCompletedEvent), "action.completed")]
[JsonDerivedType(typeof(StepRecordedEvent),    "step.recorded")]
public abstract record TraceEvent
{
    [JsonPropertyOrder(-3)] public int V { get; init; } = 1;
    [JsonPropertyOrder(-2)] public int Seq { get; init; }
    [JsonPropertyOrder(-1)] public long Ts { get; init; }

    [JsonPropertyOrder(0)] public string RunId { get; init; } = "";

    // Type is not serialized directly — the STJ discriminator handles "type" in JSON.
    // [JsonIgnore] prevents double-serialization (discriminator + property = duplicate "type" key).
    [JsonIgnore]
    public abstract string Type { get; }
}
