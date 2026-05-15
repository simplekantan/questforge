using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartEvent),        "run.start")]
[JsonDerivedType(typeof(RunEndEvent),          "run.end")]
[JsonDerivedType(typeof(ObservationEvent),     "observation")]
[JsonDerivedType(typeof(DecisionEvent),        "decision")]
[JsonDerivedType(typeof(ActionSubmittedEvent), "action.submitted")]
[JsonDerivedType(typeof(ActionCompletedEvent), "action.completed")]
public abstract record TraceEvent(DateTimeOffset At)
{
    // Type is not a positional parameter — the STJ discriminator handles "type" in JSON.
    // [JsonIgnore] prevents double-serialization (discriminator + property = duplicate "type" key).
    [JsonIgnore]
    public abstract string Type { get; }
}