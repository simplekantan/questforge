using QuestForge.Adapters;
using QuestForge.Adapters.Tracing;

namespace QuestForge.Engine.Authoring;

/// <summary>
/// Polls key item inventory slots, diffs against the previous map, and writes
/// an InventoryChangedEvent to the trace stream when a change is detected.
/// </summary>
public sealed class KeyItemInventoryPoller
{
    private readonly ITraceWriter _traceWriter;
    private readonly string? _runId;

    public KeyItemInventoryPoller(ITraceWriter traceWriter, string? runId)
    {
        _traceWriter = traceWriter ?? throw new ArgumentNullException(nameof(traceWriter));
        _runId = runId;
    }

    /// <summary>
    /// Diffs <paramref name="previous"/> against <paramref name="currentSlots"/>.
    /// If changed and runId is non-null, writes an InventoryChangedEvent.
    /// Returns the new map.
    /// </summary>
    public Dictionary<uint, int> Poll(
        Dictionary<uint, int> previous,
        IEnumerable<(uint id, int qty)> currentSlots)
    {
        var result = KeyItemPollDiff.Diff(previous, currentSlots);

        if (result.Changed && _runId is not null)
        {
            var evt = new InventoryChangedEvent(
                RunId:   _runId,
                Gained:  result.Gained,
                Lost:    result.Lost,
                NewHash: result.NewHash,
                At:      DateTimeOffset.UtcNow);
            _traceWriter.Write(evt);
        }

        return result.NewMap;
    }
}
