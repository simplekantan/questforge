using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

[JsonSerializable(typeof(TraceEvent))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
public partial class TraceEventJsonContext : JsonSerializerContext { }