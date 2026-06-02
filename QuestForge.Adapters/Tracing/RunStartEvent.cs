using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record RunStartEvent : TraceEvent
{
    [JsonIgnore]
    public override string Type => "run.start";

    [JsonPropertyOrder(1)] public RunStartData Data { get; init; } = default!;

    public sealed record RunStartData
    {
        public uint QuestId { get; init; }
        public string SchemaVer { get; init; } = "1.0";
        public string? PluginVer { get; init; }
        public string? PatchVer { get; init; }
        public DateTimeOffset? WallClockUtc { get; init; }
        public object? EngineConfig { get; init; }
        public string? PrecedingRunId { get; init; }
        public object? NewGamePlus { get; init; }
    }
}
