using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for SnapshotAggregator — scenarios 41-45 from PHASE_9_PLAN.md §5.5.
/// All tests will fail until Builder implements SnapshotAggregator.
/// </summary>
public sealed class SnapshotAggregatorTests
{
    private static readonly QuestId Quest2054 = new(2054);
    private static readonly QuestId OtherQuest = new(9999);

    // =========================================================================
    // Scenario 41 — SnapshotAggregator_OnZoneChanged_UpdatesZoneAndPosition
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnZoneChanged_UpdatesZoneAndPosition()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator initialized at zone 100,
         *           When OnZoneChanged(zone=200, pos=(50,0,50)) is called,
         *           Then Current.Zone == 200 and Current.Position == (50,0,50)
         *
         * BUILDER GUIDANCE: OnZoneChanged updates both Zone and Position fields in Current.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnZoneChanged(new ZoneId(200), new WorldPosition(50, 0, 50));

        // Assert
        Assert.Equal(new ZoneId(200), aggregator.Current.Zone);
        Assert.Equal(new WorldPosition(50, 0, 50), aggregator.Current.Position);
    }

    // =========================================================================
    // Scenario 42 — SnapshotAggregator_OnQuestSequenceChanged_UpdatesSequence
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnQuestSequenceChanged_UpdatesSequence()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator with QuestId=2054, QuestSequence=0,
         *           When OnQuestSequenceChanged(2054, 2) is called,
         *           Then Current.QuestSequence == 2
         *
         * BUILDER GUIDANCE: OnQuestSequenceChanged checks quest matches activeQuest, then updates.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnQuestSequenceChanged(Quest2054, 2);

        // Assert
        Assert.Equal(2, aggregator.Current.QuestSequence);
    }

    // =========================================================================
    // Scenario 43 — SnapshotAggregator_OnQuestSequenceChanged_Ignored_WhenDifferentQuest
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnQuestSequenceChanged_Ignored_WhenDifferentQuest()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator authoring quest 2054 with QuestSequence=0,
         *           When OnQuestSequenceChanged(9999, 5) is called (different quest),
         *           Then Current.QuestSequence remains 0 (unchanged)
         *
         * BUILDER GUIDANCE: Silently ignore sequence/flag changes for non-active quests.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act — send event for a different quest
        aggregator.OnQuestSequenceChanged(OtherQuest, 5);

        // Assert
        Assert.Equal(0, aggregator.Current.QuestSequence);
    }

    // =========================================================================
    // Scenario 44 — SnapshotAggregator_OnDialogueChoice_UpdatesLastPromptAndAnswer
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnDialogueChoice_UpdatesLastPromptAndAnswer()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given empty aggregator,
         *           When OnDialogueChoice("TEXT_Q1", "TEXT_A1") is called,
         *           Then Current.LastDialoguePrompt == "TEXT_Q1"
         *                and Current.LastDialogueAnswer == "TEXT_A1"
         *
         * BUILDER GUIDANCE: OnDialogueChoice updates both prompt and answer fields.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnDialogueChoice("TEXT_Q1", "TEXT_A1");

        // Assert
        Assert.Equal("TEXT_Q1", aggregator.Current.LastDialoguePrompt);
        Assert.Equal("TEXT_A1", aggregator.Current.LastDialogueAnswer);
    }

    // =========================================================================
    // Scenario 45 — SnapshotAggregator_OnQuestAccepted_SetsAcceptedFlag
    // =========================================================================
    [Fact]
    public void SnapshotAggregator_OnQuestAccepted_SetsAcceptedFlag()
    {
        /*
         * RED: Will fail until Builder implements SnapshotAggregator
         *
         * CONTRACT: Given aggregator with QuestAccepted=false,
         *           When OnQuestAccepted(2054) is called,
         *           Then Current.QuestAccepted == true
         *
         * BUILDER GUIDANCE: OnQuestAccepted sets QuestAccepted = true when questId matches activeQuest.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnQuestAccepted(Quest2054);

        // Assert
        Assert.True(aggregator.Current.QuestAccepted);
    }
}
