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
    // Phase 11B — SnapshotAggregator attunement tests (B1-B3 from §3.6)
    // =========================================================================

    [Fact]
    public void SnapshotAggregator_FreshInstance_LastAttuned_IsNull()
    {
        /*
         * RED: Will fail until Builder implements OnAttunementChanged
         *      (the _lastAttuned field must be initialized to null and included in Current).
         *
         * CONTRACT: Given a fresh SnapshotAggregator,
         *           When Current is read,
         *           Then LastAttuned == null.
         *
         * BUILDER GUIDANCE: _lastAttuned = null is the field initializer (already added as a stub).
         *   Current must include LastAttuned: _lastAttuned.
         *   This test will PASS once the stub compiles correctly — but it's included to document
         *   the contract and guard against regressions.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        var snapshot = aggregator.Current;

        // Assert
        Assert.Null(snapshot.LastAttuned);
    }

    [Fact]
    public void SnapshotAggregator_OnAttunementChanged_SetsLastAttuned()
    {
        /*
         * RED: Will fail until Builder implements OnAttunementChanged.
         *
         * CONTRACT: Given aggregator.OnAttunementChanged(new AetheryteId(500)),
         *           When Current is read,
         *           Then LastAttuned == new AetheryteId(500).
         *
         * BUILDER GUIDANCE: OnAttunementChanged sets _lastAttuned = aetheryte.
         *   The NotImplementedException stub must be replaced with: _lastAttuned = aetheryte;
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnAttunementChanged(new AetheryteId(500));

        // Assert
        Assert.Equal(new AetheryteId(500), aggregator.Current.LastAttuned);
    }

    [Fact]
    public void SnapshotAggregator_OnAttunementChanged_Overwrite_TracksLatest()
    {
        /*
         * RED: Will fail until Builder implements OnAttunementChanged.
         *
         * CONTRACT: Given OnAttunementChanged(AetheryteId(500)) then OnAttunementChanged(AetheryteId(501)),
         *           When Current is read,
         *           Then LastAttuned == new AetheryteId(501).
         *           (Aggregator tracks most-recent attunement event — same semantics as LastNpcInteracted.)
         *
         * BUILDER GUIDANCE: _lastAttuned is overwritten on each call; no history is retained.
         */

        // Arrange
        var aggregator = new SnapshotAggregator(activeQuest: Quest2054);

        // Act
        aggregator.OnAttunementChanged(new AetheryteId(500));
        aggregator.OnAttunementChanged(new AetheryteId(501));

        // Assert
        Assert.Equal(new AetheryteId(501), aggregator.Current.LastAttuned);
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
