using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;

namespace QuestForge.Engine.Tests.Authoring;

internal static class DraftValidatorTestData
{
    public static readonly QuestId Quest2054 = new(2054);
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset T1 = T0.AddSeconds(30);
    public static readonly NpcLocation ValidNpcLoc =
        new(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));

    public static GameStateSnapshot MakeSnapshot(DateTimeOffset? at = null) => new(
        CapturedAt: at ?? T0,
        Zone: new ZoneId(128),
        Position: new WorldPosition(9.5f, 40f, 14.2f),
        ActiveQuest: Quest2054,
        QuestSequence: 0,
        QuestFlags: 0,
        QuestAccepted: false,
        QuestCompleted: false,
        LastNpcInteracted: null,
        LastNpcPosition: null,
        LastDialoguePrompt: null,
        LastDialogueAnswer: null,
        InventoryHash: 0,
        LastAttuned: null);

    public static DraftStep MakeDraftStep(
        string stepId,
        int seqNum,
        Step raw,
        InferredFrom inferredFrom = InferredFrom.QuestSequenceChange,
        string? notes = null) =>
        new(
            StepId: stepId,
            StepType: raw.GetType().Name.Replace("Step", "").ToLowerInvariant(),
            SequenceNumber: seqNum,
            InferredFrom: inferredFrom,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: notes,
            Raw: raw);

    /// <summary>
    /// Baseline: accept + turn-in, both with valid expect, both with notes, QuestName set.
    /// Produces zero errors, zero warnings — every new test should start here and mutate exactly one thing.
    /// </summary>
    public static QuestDraft ValidBaseline(string questName = "Test Quest")
    {
        var draft = new QuestDraft(Quest2054, T0) { QuestName = questName };
        draft.AddStep(MakeDraftStep("accept-quest", 0,
            new AcceptStep
            {
                Id = "accept-quest",
                Target = ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(2054)" }
            },
            notes: "accept"), T0);
        draft.AddStep(MakeDraftStep("turn-in-quest", 1,
            new TurnInStep
            {
                Id = "turn-in-quest",
                Target = ValidNpcLoc,
                Expect = new PredicateExpect { Predicate = "isQuestComplete(2054)" }
            },
            notes: "turn-in"), T0);
        return draft;
    }
}
