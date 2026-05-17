using System.Text.Json.Serialization;

namespace QuestForge.Adapters.Tracing;

public sealed record InventoryChangedEvent(
    string? RunId,
    IReadOnlyList<KeyItemDelta> Gained,
    IReadOnlyList<KeyItemDelta> Lost,
    uint NewHash,
    DateTimeOffset At
) : TraceEvent(At)
{
    [JsonIgnore]
    public override string Type => "inventory.changed";
}
