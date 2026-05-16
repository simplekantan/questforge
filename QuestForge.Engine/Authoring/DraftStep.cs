using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public sealed record DraftStep(
    string StepId,
    string StepType,
    int SequenceNumber,
    InferredFrom InferredFrom,
    GameStateSnapshot ObservedBefore,
    GameStateSnapshot ObservedAfter,
    string? SuggestedExpect,
    string? Notes,
    Step? Raw);
