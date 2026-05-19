using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

// WHY: QuestForge.Schema is imported here; its AetheryteId and NpcLocation types
// are needed when inspecting built steps. Adapters.Types is also imported for snapshot
// construction (ZoneId, WorldPosition, NpcId, QuestId).

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for StepFactory.Build — population of TalkStep.DialogueChoices
/// from GameStateSnapshot.DialogueOptionSelected, and the dialogueChoicesOverride parameter.
///
/// ALL tests will fail to compile until Builder:
///   1. Adds GameStateSnapshot.DialogueOptionSelected (int? non-positional property)
///   2. Adds optional parameter: IReadOnlyList&lt;int&gt;? dialogueChoicesOverride = null
///      to StepFactory.Build
///   3. Wires DialogueOptionSelected and dialogueChoicesOverride into TalkStep.DialogueChoices
///      (new DialogueChoice(Type: "list", Answer: index.ToString()))
/// </summary>
public sealed class DialogueOptionStepFactoryTests
{
    private static readonly QuestId ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GameStateSnapshot SnapshotWithDialogueOption(int? option) =>
        new(
            CapturedAt: T0,
            Zone: new ZoneId(131),
            Position: new WorldPosition(10, 0, 20),
            ActiveQuest: ActiveQuest,
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: false,
            QuestCompleted: false,
            LastNpcInteracted: new NpcId(1002001u),
            LastNpcPosition: new WorldPosition(12, 0, 22),
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null)
        {
            // RED: DialogueOptionSelected does not exist yet
            DialogueOptionSelected = option
        };

    // SF-DLG-1
    [Fact]
    public void SF_DLG_1_DialogueOptionSelected2_BuildTalk_ProducesListChoiceWithAnswer2()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property
         *      and wires it into TalkStep.DialogueChoices in StepFactory.Build.
         *
         * CONTRACT: Given after.DialogueOptionSelected == 2,
         *           When Build("talk", ...) is called,
         *           Then result is TalkStep with DialogueChoices[0].Type == "list"
         *            and DialogueChoices[0].Answer == "2".
         *
         * BUILDER GUIDANCE: In the "talk" switch arm (and default arm), after building the
         * TalkStep, set DialogueChoices from the snapshot:
         *   if (after?.DialogueOptionSelected is { } idx)
         *       dialogueChoices = [new DialogueChoice(Type: "list", Answer: idx.ToString())]
         */

        // Arrange
        var after = SnapshotWithDialogueOption(2);

        // Act
        var step = StepFactory.Build("talk", "step-1", null, after);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Single(talkStep.DialogueChoices);
        Assert.Equal("list", talkStep.DialogueChoices[0].Type);
        Assert.Equal("2", talkStep.DialogueChoices[0].Answer);
    }

    // SF-DLG-2
    [Fact]
    public void SF_DLG_2_DialogueOptionSelected0_Answer_IsZeroString()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given after.DialogueOptionSelected == 0,
         *           When Build("talk", ...) is called,
         *           Then DialogueChoices[0].Answer == "0" (not omitted or null).
         *
         * BUILDER GUIDANCE: Do not special-case zero; it is a valid first-item selection.
         */

        // Arrange
        var after = SnapshotWithDialogueOption(0);

        // Act
        var step = StepFactory.Build("talk", "step-2", null, after);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Single(talkStep.DialogueChoices);
        Assert.Equal("0", talkStep.DialogueChoices[0].Answer);
    }

    // SF-DLG-3
    [Fact]
    public void SF_DLG_3_DialogueOptionSelectedNull_DialogueChoicesEmpty()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given after.DialogueOptionSelected == null,
         *           When Build("talk", ...) is called,
         *           Then TalkStep.DialogueChoices.Length == 0.
         *
         * BUILDER GUIDANCE: No selection recorded → no choices in the step.
         */

        // Arrange
        var after = SnapshotWithDialogueOption(null);

        // Act
        var step = StepFactory.Build("talk", "step-3", null, after);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Empty(talkStep.DialogueChoices);
    }

    // SF-DLG-4
    [Fact]
    public void SF_DLG_4_AfterNull_NoNullReferenceException_DialogueChoicesEmpty()
    {
        /*
         * RED: Will fail to compile until the Builder adds the property (compile-time);
         *      the runtime behaviour (no NRE) is also verified here.
         *
         * CONTRACT: Given after == null,
         *           When Build("talk", ...) is called,
         *           Then no NullReferenceException is thrown
         *            and TalkStep.DialogueChoices.Length == 0.
         *
         * BUILDER GUIDANCE: Use null-safe access (after?.DialogueOptionSelected).
         */

        // Act
        var step = StepFactory.Build("talk", "step-4", null, after: null);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Empty(talkStep.DialogueChoices);
    }

    // SF-DLG-5
    [Fact]
    public void SF_DLG_5_DialogueChoicesOverride_ProducesThreeEntries()
    {
        /*
         * RED: Will fail to compile until Builder adds the dialogueChoicesOverride parameter
         *      to StepFactory.Build.
         *
         * CONTRACT: Given dialogueChoicesOverride = [1, 0, 3],
         *           When Build("talk", ...) is called,
         *           Then TalkStep.DialogueChoices has 3 entries with Answers "1", "0", "3".
         *
         * BUILDER GUIDANCE: Add optional parameter:
         *   IReadOnlyList<int>? dialogueChoicesOverride = null
         * after itemsOverride. When non-null, use override list instead of snapshot field:
         *   dialogueChoices = override.Select(i => new DialogueChoice("list", Answer: i.ToString())).ToArray()
         */

        // Arrange
        var after = SnapshotWithDialogueOption(null);
        IReadOnlyList<int> overrideList = [1, 0, 3];

        // Act
        // RED: dialogueChoicesOverride parameter does not exist yet
        var step = StepFactory.Build("talk", "step-5", null, after, dialogueChoicesOverride: overrideList);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Equal(3, talkStep.DialogueChoices.Length);
        Assert.Equal("1", talkStep.DialogueChoices[0].Answer);
        Assert.Equal("0", talkStep.DialogueChoices[1].Answer);
        Assert.Equal("3", talkStep.DialogueChoices[2].Answer);
        Assert.All(talkStep.DialogueChoices, c => Assert.Equal("list", c.Type));
    }

    // SF-DLG-6
    [Fact]
    public void SF_DLG_6_OverrideAndSnapshotBothSet_OverrideWins()
    {
        /*
         * RED: Will fail to compile until Builder adds the dialogueChoicesOverride parameter.
         *
         * CONTRACT: Given after.DialogueOptionSelected == 7 AND dialogueChoicesOverride = [2, 4],
         *           When Build("talk", ...) is called,
         *           Then TalkStep.DialogueChoices.Length == 2 (override wins, snapshot ignored).
         *
         * BUILDER GUIDANCE: Same precedence as itemsOverride: override != null → use override only.
         */

        // Arrange
        var after = SnapshotWithDialogueOption(7);
        IReadOnlyList<int> overrideList = [2, 4];

        // Act
        // RED: dialogueChoicesOverride parameter does not exist yet
        var step = StepFactory.Build("talk", "step-6", null, after, dialogueChoicesOverride: overrideList);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Equal(2, talkStep.DialogueChoices.Length);
        Assert.Equal("2", talkStep.DialogueChoices[0].Answer);
        Assert.Equal("4", talkStep.DialogueChoices[1].Answer);
    }

    // SF-DLG-7
    [Fact]
    public void SF_DLG_7_NonTalkStepTypes_WithDialogueOption_NoDialogueChoicesField()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property
         *      (the test needs to compile, even though non-talk steps don't use the field).
         *
         * CONTRACT: Given after.DialogueOptionSelected == 2,
         *           When Build is called with step types "travel", "accept", "turn-in", "attune",
         *           Then the returned step instances do NOT have DialogueChoices populated
         *            (those types either have no such field or it stays empty).
         *
         * BUILDER GUIDANCE: Only TalkStep (and possibly TurnInStep if desired) should receive
         * DialogueChoices from the snapshot. travel/accept/attune/pickup-item do not have the
         * field on the C# class — the compiler enforces this.
         */

        // Arrange
        var after = SnapshotWithDialogueOption(2);

        // Act
        var travelStep = StepFactory.Build("travel", "s-travel", null, after);
        var acceptStep = StepFactory.Build("accept", "s-accept", null, after);
        var attuneStep = StepFactory.Build("attune", "s-attune", null, after);
        var pickupStep = StepFactory.Build("pickup-item", "s-pickup", null, after);

        // Assert — these types do not have DialogueChoices; just verify type and no exception
        Assert.IsType<TravelStep>(travelStep);
        Assert.IsType<AcceptStep>(acceptStep);
        Assert.IsType<AttunementStep>(attuneStep);
        Assert.IsType<PickupItemStep>(pickupStep);

        // TurnInStep does have DialogueChoices but it's an independent field; verify it stays empty
        var turnInStep = StepFactory.Build("turn-in", "s-turnin", null, after);
        var ti = Assert.IsType<TurnInStep>(turnInStep);
        Assert.Empty(ti.DialogueChoices);
    }

    // SF-DLG-8
    [Fact]
    public void SF_DLG_8_UnknownStepType_WithDialogueOption_FallsBackToTalkStepWithChoice()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given step type "unknown-xyz" and after.DialogueOptionSelected == 2,
         *           When Build is called,
         *           Then the default arm produces a TalkStep with DialogueChoices[0].Answer == "2".
         *
         * BUILDER GUIDANCE: The default switch arm returns a TalkStep; apply the same
         * DialogueChoices population logic there as in the explicit "talk" arm.
         */

        // Arrange
        var after = SnapshotWithDialogueOption(2);

        // Act
        var step = StepFactory.Build("unknown-xyz", "step-8", null, after);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Single(talkStep.DialogueChoices);
        Assert.Equal("2", talkStep.DialogueChoices[0].Answer);
    }

    // SF-DLG-9
    [Fact]
    public void SF_DLG_9_OtherTalkStepFields_UnaffectedWhenDialogueChoicesSet()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected property.
         *
         * CONTRACT: Given after with NpcId=1002001, Zone=131, and DialogueOptionSelected=3,
         *           When Build("talk", ...) is called,
         *           Then Target.NpcId, Zone, and DialogueChoices[0].Answer are all correct.
         *
         * BUILDER GUIDANCE: Setting DialogueChoices must not disturb Target or Zone fields.
         */

        // Arrange
        var after = SnapshotWithDialogueOption(3);

        // Act
        var step = StepFactory.Build("talk", "step-9", null, after);

        // Assert
        var talkStep = Assert.IsType<TalkStep>(step);
        Assert.Equal(1002001u, talkStep.Target!.NpcId);
        Assert.Equal("131", talkStep.Zone);
        Assert.Single(talkStep.DialogueChoices);
        Assert.Equal("3", talkStep.DialogueChoices[0].Answer);
    }
}
