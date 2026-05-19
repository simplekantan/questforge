using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

/// <summary>Captures the departure and destination shard of a completed aethernet teleport.</summary>
public sealed record AethernetHop(AetheryteId? From, AethernetId To);

public sealed record GameStateSnapshot(
    DateTimeOffset CapturedAt,
    ZoneId Zone,
    WorldPosition Position,
    QuestId? ActiveQuest,
    int QuestSequence,
    uint QuestFlags,
    bool QuestAccepted,
    bool QuestCompleted,
    NpcId? LastNpcInteracted,
    WorldPosition? LastNpcPosition,
    string? LastDialoguePrompt,
    string? LastDialogueAnswer,
    uint InventoryHash,
    // WHY: appended last — positional record; inserting mid-record would force churn-only edits
    // across all existing constructor call sites with no semantic benefit.
    AetheryteId? LastAttuned)
{
    // Non-positional: does not affect existing constructor call sites.
    // Full key item map snapshot (authoring mode only — null in production/hash-only mode).
    public IReadOnlyDictionary<uint, int>? KeyItems { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Holds item IDs newly detected in the key items container since the last snapshot.
    public IReadOnlyList<uint>? KeyItemsAdded { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Holds item IDs removed from the key items container since the last snapshot.
    public IReadOnlyList<uint>? KeyItemsRemoved { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // The last aethernet shard (sub-aetheryte) the player targeted/interacted with.
    // Used by StepInferenceEngine to detect aethernet travel hops.
    public AetheryteId? LastAethernetShardInteracted { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // The aethernet destination shard selected by the player in the aethernet menu.
    // Preferred over LastAethernetShardInteracted for "to" in StepInferenceEngine Rule 4.
    public AethernetId? AethernetDestinationSelected { get; init; }

    // Non-positional: set when an aethernet teleport completes this recording window
    // (TelepotTown menu closed after a selection was made). Cleared by OnAethernetTeleportConsumed
    // in RecordStep so it does not bleed into the next inference window.
    // This is the primary signal for aethernet inference — replaces the fragile snapshot-diff approach.
    public AethernetHop? AethernetTeleportCompleted { get; init; }

    // Non-positional. Set when the player selects an option from SelectIconString/SelectString
    // during an authoring session. Cleared by OnDialogueOptionConsumed after RecordStep.
    // Survives ResetDeltas.
    public int? DialogueOptionSelected { get; init; }
}
