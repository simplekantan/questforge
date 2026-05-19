using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for SnapshotAggregator.OnDialogueOptionSelected and OnDialogueOptionConsumed.
///
/// ALL tests will fail to compile until Builder adds:
///   - SnapshotAggregator.OnDialogueOptionSelected(int index)
///   - SnapshotAggregator.OnDialogueOptionConsumed()
///   - GameStateSnapshot.DialogueOptionSelected (int? non-positional property)
/// </summary>
public sealed class DialogueOptionAggregatorTests
{
    private static readonly QuestId ActiveQuest = new(2054);

    // AGG-DLG-1
    [Fact]
    public void AGG_DLG_1_FreshAggregator_DialogueOptionSelected_IsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds DialogueOptionSelected to snapshot.
         *
         * CONTRACT: Given a freshly constructed SnapshotAggregator,
         *           When Current is read,
         *           Then Current.DialogueOptionSelected is null.
         *
         * BUILDER GUIDANCE: _dialogueOptionSelected private field starts as null.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act & Assert
        // RED: DialogueOptionSelected does not exist yet
        Assert.Null(aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-2
    [Fact]
    public void AGG_DLG_2_OnDialogueOptionSelected_SetsField()
    {
        /*
         * RED: Will fail to compile until Builder adds OnDialogueOptionSelected method.
         *
         * CONTRACT: Given a SnapshotAggregator,
         *           When OnDialogueOptionSelected(2) is called,
         *           Then Current.DialogueOptionSelected == 2.
         *
         * BUILDER GUIDANCE: Add method:
         *   public void OnDialogueOptionSelected(int index) => _dialogueOptionSelected = index;
         * Wire the field into Current via:
         *   DialogueOptionSelected = _dialogueOptionSelected
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        // RED: Method does not exist yet
        aggregator.OnDialogueOptionSelected(2);

        // Assert
        Assert.Equal(2, aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-3
    [Fact]
    public void AGG_DLG_3_OnDialogueOptionSelected_Zero_StoresZeroNotNull()
    {
        /*
         * RED: Will fail to compile until Builder adds OnDialogueOptionSelected method.
         *
         * CONTRACT: Given OnDialogueOptionSelected(0) is called,
         *           When Current is read,
         *           Then Current.DialogueOptionSelected == 0 (not null).
         *
         * BUILDER GUIDANCE: 0 is a valid list index; must not be coerced to null.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        // RED: Method does not exist yet
        aggregator.OnDialogueOptionSelected(0);

        // Assert
        Assert.NotNull(aggregator.Current.DialogueOptionSelected);
        Assert.Equal(0, aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-4
    [Fact]
    public void AGG_DLG_4_TwoCalls_LastWriteWins()
    {
        /*
         * RED: Will fail to compile until Builder adds OnDialogueOptionSelected method.
         *
         * CONTRACT: Given OnDialogueOptionSelected(3) then OnDialogueOptionSelected(7),
         *           When Current is read,
         *           Then Current.DialogueOptionSelected == 7.
         *
         * BUILDER GUIDANCE: Simple field assignment; last write wins automatically.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        // RED: Method does not exist yet
        aggregator.OnDialogueOptionSelected(3);
        aggregator.OnDialogueOptionSelected(7);

        // Assert
        Assert.Equal(7, aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-5
    [Fact]
    public void AGG_DLG_5_SetThenResetDeltas_FieldSurvives()
    {
        /*
         * RED: Will fail to compile until Builder adds OnDialogueOptionSelected method
         *      and wires DialogueOptionSelected into Current.
         *
         * CONTRACT: Given DialogueOptionSelected is set to 4,
         *           When ResetDeltas() is called,
         *           Then Current.DialogueOptionSelected is still 4.
         *
         * BUILDER GUIDANCE: ResetDeltas only clears KeyItemsAdded and KeyItemsRemoved.
         * Do NOT add _dialogueOptionSelected to ResetDeltas — it is cleared only by
         * OnDialogueOptionConsumed (same pattern as AethernetTeleportCompleted).
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        // RED: Method does not exist yet
        aggregator.OnDialogueOptionSelected(4);

        // Act
        aggregator.ResetDeltas();

        // Assert
        Assert.Equal(4, aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-6
    [Fact]
    public void AGG_DLG_6_SetThenConsumed_FieldCleared()
    {
        /*
         * RED: Will fail to compile until Builder adds OnDialogueOptionSelected and
         *      OnDialogueOptionConsumed methods.
         *
         * CONTRACT: Given DialogueOptionSelected is set to 2,
         *           When OnDialogueOptionConsumed() is called,
         *           Then Current.DialogueOptionSelected is null.
         *
         * BUILDER GUIDANCE: Add method:
         *   public void OnDialogueOptionConsumed() => _dialogueOptionSelected = null;
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);
        // RED: Methods do not exist yet
        aggregator.OnDialogueOptionSelected(2);

        // Act
        aggregator.OnDialogueOptionConsumed();

        // Assert
        Assert.Null(aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-7
    [Fact]
    public void AGG_DLG_7_ConsumedOnFresh_NoExceptionStillNull()
    {
        /*
         * RED: Will fail to compile until Builder adds OnDialogueOptionConsumed method.
         *
         * CONTRACT: Given a fresh aggregator (DialogueOptionSelected is null),
         *           When OnDialogueOptionConsumed() is called,
         *           Then no exception is thrown and DialogueOptionSelected remains null.
         *
         * BUILDER GUIDANCE: Setting a nullable field to null when already null is a no-op.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act & Assert — must not throw
        // RED: Method does not exist yet
        aggregator.OnDialogueOptionConsumed();

        Assert.Null(aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-8
    [Fact]
    public void AGG_DLG_8_SetConsumeSetAgain_SecondValueStored()
    {
        /*
         * RED: Will fail to compile until Builder adds both methods.
         *
         * CONTRACT: Given set(1) → consume → set(5),
         *           When Current is read,
         *           Then Current.DialogueOptionSelected == 5.
         *
         * BUILDER GUIDANCE: Consume clears the field; subsequent set stores the new value.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        // RED: Methods do not exist yet
        aggregator.OnDialogueOptionSelected(1);
        aggregator.OnDialogueOptionConsumed();
        aggregator.OnDialogueOptionSelected(5);

        // Assert
        Assert.Equal(5, aggregator.Current.DialogueOptionSelected);
    }

    // AGG-DLG-9
    [Fact]
    public void AGG_DLG_9_DialogueAndAethernetDestination_BothSetIndependently()
    {
        /*
         * RED: Will fail to compile until Builder adds OnDialogueOptionSelected method
         *      and DialogueOptionSelected property.
         *
         * CONTRACT: Given OnDialogueOptionSelected(3) and OnAethernetDestinationSelected(AethernetId(77)),
         *           When Current is read,
         *           Then both DialogueOptionSelected == 3 AND AethernetDestinationSelected == AethernetId(77).
         *
         * BUILDER GUIDANCE: The two fields are independent; setting one must not affect the other.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: ActiveQuest);

        // Act
        // RED: OnDialogueOptionSelected does not exist yet
        aggregator.OnDialogueOptionSelected(3);
        aggregator.OnAethernetDestinationSelected(new AethernetId(77u));

        // Assert
        Assert.Equal(3, aggregator.Current.DialogueOptionSelected);
        Assert.Equal(new AethernetId(77u), aggregator.Current.AethernetDestinationSelected!.Value);
    }
}
