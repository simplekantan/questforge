using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

/// <summary>Captures the departure and destination shard of a completed aethernet teleport.</summary>
public sealed record AethernetHop(AetheryteId? From, AethernetId To);

/// <summary>
/// Associates a set of enemy data-ids with the quest variable (or sequence) value that was
/// reached after those kills were observed within the correlation window.
/// </summary>
public sealed record KillCorrelation(IReadOnlyList<uint> DataIds, int FinalValue);

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

    // Non-positional. The NPC that opened the SelectIconString dialog (e.g., a Lift Attendant),
    // captured from the live TargetManager when SelectIconString first appears.
    // Cleared alongside DialogueOptionSelected by OnDialogueOptionConsumed.
    // WHY: before.LastNpcInteracted is unreliable (may be a shard or prior NPC);
    //      reading TargetManager at the moment the dialog opens gives the correct NPC.
    public QuestForge.Schema.NpcLocation? DialogueNpcSource { get; init; }

    // Non-positional. Set when a quest OTHER than the active quest is accepted during this
    // recording window (e.g. a mandatory sub-quest offered by an NPC mid-sequence).
    // Cleared by ResetDeltas so it is a per-window delta signal.
    public QuestId? ForeignQuestAccepted { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // True when the local player is in combat. Set by OnInCombatChanged.
    public bool InCombat { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Kill-to-variable correlations accumulated across the current recording window.
    // Key: variable index (0-5), or SnapshotAggregator.SequenceVariableIndex (-1) for sequence advances.
    // Null or empty when no correlated kills have been observed.
    public IReadOnlyDictionary<int, KillCorrelation>? KillCorrelatedTargets { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Player position captured when the local player entered combat (false→true transition).
    // Null if no combat-start transition was observed in the current session.
    public WorldPosition? CombatStartPosition { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // Zone captured when the local player entered combat (false→true transition).
    public int CombatStartZone { get; init; }

    // Non-positional. True when the player confirmed a SelectYesno prompt during this
    // recording window. Distinct from DialogueOptionSelected (SelectIconString list choices)
    // so StepFactory emits {type:"yesno"} rather than a spurious {type:"list"} choice.
    // Cleared alongside DialogueOptionSelected by OnDialogueOptionConsumed.
    public bool SelectYesnoConfirmed { get; init; }

    // Non-positional: does not affect existing constructor call sites.
    // The six quest work bytes (V0–V5) of the ActiveQuest, captured in Author mode.
    // Null when no variables have been observed (e.g. Inspect mode, or before the first
    // heartbeat poll). Always length 6 when non-null.
    public IReadOnlyList<byte>? QuestVariables { get; init; }
}
