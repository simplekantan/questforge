using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

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
    AetheryteId? LastAttuned);   // Phase 11B — appended last to minimise call-site churn
