using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

[JsonSerializable(typeof(TraceEvent))]
[JsonSerializable(typeof(StepRecordedEvent))]
[JsonSerializable(typeof(RunStartEvent.RunStartData))]
[JsonSerializable(typeof(RunEndEvent.RunEndData))]
[JsonSerializable(typeof(ObservationEvent.ObservationData))]
[JsonSerializable(typeof(DecisionEvent.DecisionData))]
[JsonSerializable(typeof(ActionSubmittedEvent.ActionSubmittedData))]
[JsonSerializable(typeof(ActionCompletedEvent.ActionCompletedData))]
[JsonSerializable(typeof(StepRecordedEvent.StepRecordedData))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
public partial class TraceEventJsonContext : JsonSerializerContext { }
