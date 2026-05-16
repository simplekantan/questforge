using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for QuestDraft mutation — scenarios 14-18 from PHASE_9_PLAN.md §5.2.
/// All tests will fail until Builder implements QuestDraft.AddStep/RemoveStep/ReplaceStep/GetStep.
/// </summary>
public sealed class QuestDraftTests
{
    private static readonly QuestId ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(60);
    private static readonly DateTimeOffset T2 = T1.AddSeconds(60);

    private static GameStateSnapshot MakeSnapshot(DateTimeOffset? at = null) => new(
        CapturedAt: at ?? T0,
        Zone: new ZoneId(100),
        Position: new WorldPosition(0, 0, 0),
        ActiveQuest: ActiveQuest,
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

    private static DraftStep MakeDraftStep(string stepId, int seqNum = 0) =>
        new(
            StepId: stepId,
            StepType: "talk",
            SequenceNumber: seqNum,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: $"questSequence(2054) >= {seqNum}",
            Notes: null,
            Raw: new TalkStep { Id = stepId });

    // =========================================================================
    // Scenario 14 — DraftStep_AddAndRetrieveInOrder
    // =========================================================================
    [Fact]
    public void DraftStep_AddAndRetrieveInOrder()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.AddStep
         *
         * CONTRACT: Given a draft,
         *           When three steps A, B, C are added in that order,
         *           Then draft.Steps returns them in insertion order [A, B, C]
         *
         * BUILDER GUIDANCE: _steps is a List<DraftStep>; AddStep appends to end.
         */

        // Arrange
        var draft = new QuestDraft(ActiveQuest, T0);
        var stepA = MakeDraftStep("step-a");
        var stepB = MakeDraftStep("step-b");
        var stepC = MakeDraftStep("step-c");

        // Act
        draft.AddStep(stepA, T0);
        draft.AddStep(stepB, T0);
        draft.AddStep(stepC, T0);

        // Assert
        Assert.Equal(3, draft.Steps.Count);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-b", draft.Steps[1].StepId);
        Assert.Equal("step-c", draft.Steps[2].StepId);
    }

    // =========================================================================
    // Scenario 15 — DraftStep_Remove_RemovesByStepId
    // =========================================================================
    [Fact]
    public void DraftStep_Remove_RemovesByStepId()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.RemoveStep
         *
         * CONTRACT: Given steps A, B, C in draft,
         *           When B is removed,
         *           Then draft.Steps returns [A, C] in that order
         *
         * BUILDER GUIDANCE: RemoveStep finds by StepId and removes from _steps list.
         */

        // Arrange
        var draft = new QuestDraft(ActiveQuest, T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);
        draft.AddStep(MakeDraftStep("step-c"), T0);

        // Act
        var removed = draft.RemoveStep("step-b", T1);

        // Assert
        Assert.True(removed);
        Assert.Equal(2, draft.Steps.Count);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-c", draft.Steps[1].StepId);
    }

    // =========================================================================
    // Scenario 16 — DraftStep_Remove_ReturnsFalse_WhenStepIdMissing
    // =========================================================================
    [Fact]
    public void DraftStep_Remove_ReturnsFalse_WhenStepIdMissing()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.RemoveStep
         *
         * CONTRACT: Given draft with step A,
         *           When "nonexistent" is removed,
         *           Then returns false and draft.Steps is unchanged [A]
         *
         * BUILDER GUIDANCE: RemoveStep returns false when step ID not found.
         */

        // Arrange
        var draft = new QuestDraft(ActiveQuest, T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);

        // Act
        var removed = draft.RemoveStep("nonexistent", T1);

        // Assert
        Assert.False(removed);
        var onlyStep = Assert.Single(draft.Steps);
        Assert.Equal("step-a", onlyStep.StepId);
    }

    // =========================================================================
    // Scenario 17 — DraftStep_Replace_PreservesOrderAndUpdatesLastModified
    // =========================================================================
    [Fact]
    public void DraftStep_Replace_PreservesOrderAndUpdatesLastModified()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.ReplaceStep
         *
         * CONTRACT: Given steps A, B, C added at T0,
         *           When B is replaced with B' at T1 (T1 > T0),
         *           Then order remains [A, B', C] and draft.LastModifiedAt == T1
         *
         * BUILDER GUIDANCE: ReplaceStep replaces at existing index, updates LastModifiedAt.
         */

        // Arrange
        var draft = new QuestDraft(ActiveQuest, T0);
        draft.AddStep(MakeDraftStep("step-a"), T0);
        draft.AddStep(MakeDraftStep("step-b"), T0);
        draft.AddStep(MakeDraftStep("step-c"), T0);
        var stepBPrime = MakeDraftStep("step-b") with { Notes = "replaced" };

        // Act
        var replaced = draft.ReplaceStep("step-b", stepBPrime, T1);

        // Assert
        Assert.True(replaced);
        Assert.Equal(3, draft.Steps.Count);
        Assert.Equal("step-a", draft.Steps[0].StepId);
        Assert.Equal("step-b", draft.Steps[1].StepId);
        Assert.Equal("replaced", draft.Steps[1].Notes);
        Assert.Equal("step-c", draft.Steps[2].StepId);
        Assert.Equal(T1, draft.LastModifiedAt);
    }

    // =========================================================================
    // Scenario 18 — DraftStep_Add_RejectsDuplicateStepId
    // =========================================================================
    [Fact]
    public void DraftStep_Add_RejectsDuplicateStepId()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.AddStep
         *
         * CONTRACT: Given a draft with step id="X",
         *           When another step with id="X" is added,
         *           Then AddStep throws InvalidOperationException
         *                and draft.Steps still contains exactly one step with id="X"
         *
         * BUILDER GUIDANCE: AddStep checks _steps for duplicate StepId before appending.
         */

        // Arrange
        var draft = new QuestDraft(ActiveQuest, T0);
        draft.AddStep(MakeDraftStep("X"), T0);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => draft.AddStep(MakeDraftStep("X"), T1));

        var onlyStep = Assert.Single(draft.Steps);
        Assert.Equal("X", onlyStep.StepId);
        Assert.NotNull(ex);
    }
}
