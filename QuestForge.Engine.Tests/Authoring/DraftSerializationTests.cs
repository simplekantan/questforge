using System.Text.Json;
using QuestForge.Adapters.Fakes.Authoring;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for QuestDraft serialization and FakeDraftStorage round-trips —
/// scenarios 26-33 from PHASE_9_PLAN.md §5.3.
/// All tests will fail until Builder implements QuestDraft.ToQuestDefinition and the
/// draft mutation methods AddStep/RemoveStep/ReplaceStep.
/// </summary>
public sealed class DraftSerializationTests
{
    private static readonly QuestId Quest2054 = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static GameStateSnapshot MakeSnapshot(DateTimeOffset? at = null) => new(
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
        InventoryHash: 0);

    private static DraftStep MakeDraftStep(string stepId, int seqNum, Step raw) =>
        new(
            StepId: stepId,
            StepType: raw.GetType().Name.Replace("Step", "").ToLowerInvariant(),
            SequenceNumber: seqNum,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: null,
            Raw: raw);

    // =========================================================================
    // Scenario 26 — ToQuestDefinition_GroupsStepsBySequenceNumber
    // =========================================================================
    [Fact]
    public void ToQuestDefinition_GroupsStepsBySequenceNumber()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.ToQuestDefinition
         *
         * CONTRACT: Given steps with sequenceNumbers [0, 0, 1, 1, 2],
         *           When ToQuestDefinition() is called,
         *           Then QuestDefinition.Sequences has 3 entries:
         *                seq 0 has 2 steps, seq 1 has 2 steps, seq 2 has 1 step
         *
         * BUILDER GUIDANCE: GroupBy SequenceNumber, then OrderBy ascending.
         */

        // Arrange
        var draft = new QuestDraft(Quest2054, T0) { QuestName = "Test Quest" };

        var npcLoc = new NpcLocation(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));
        draft.AddStep(MakeDraftStep("accept-quest", 0, new AcceptStep { Id = "accept-quest", Target = npcLoc }), T0);
        draft.AddStep(MakeDraftStep("talk-1", 0, new TalkStep { Id = "talk-1" }), T0);
        draft.AddStep(MakeDraftStep("talk-2", 1, new TalkStep { Id = "talk-2" }), T0);
        draft.AddStep(MakeDraftStep("talk-3", 1, new TalkStep { Id = "talk-3" }), T0);
        draft.AddStep(MakeDraftStep("turn-in-quest", 2, new TurnInStep { Id = "turn-in-quest", Target = npcLoc }), T0);

        // Act
        var definition = draft.ToQuestDefinition();

        // Assert
        Assert.Equal(3, definition.Sequences.Length);
        Assert.Equal(0, definition.Sequences[0].Sequence);
        Assert.Equal(2, definition.Sequences[0].Steps.Length);
        Assert.Equal(1, definition.Sequences[1].Sequence);
        Assert.Equal(2, definition.Sequences[1].Steps.Length);
        Assert.Equal(2, definition.Sequences[2].Sequence);
        Assert.Single(definition.Sequences[2].Steps);
    }

    // =========================================================================
    // Scenario 27 — ToQuestDefinition_OrdersSequencesAscending
    // =========================================================================
    [Fact]
    public void ToQuestDefinition_OrdersSequencesAscending()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.ToQuestDefinition
         *
         * CONTRACT: Given steps added with sequenceNumber=2 before sequenceNumber=0,
         *           When ToQuestDefinition() is called,
         *           Then Sequences are in ascending order [0, 2]
         *
         * BUILDER GUIDANCE: GroupBy then OrderBy(g => g.Key) ascending.
         */

        // Arrange
        var draft = new QuestDraft(Quest2054, T0);
        var npcLoc = new NpcLocation(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));
        // Add seq=2 first (out of order)
        draft.AddStep(MakeDraftStep("turn-in-quest", 2, new TurnInStep { Id = "turn-in-quest", Target = npcLoc }), T0);
        // Add seq=0 second
        draft.AddStep(MakeDraftStep("accept-quest", 0, new AcceptStep { Id = "accept-quest", Target = npcLoc }), T0);

        // Act
        var definition = draft.ToQuestDefinition();

        // Assert
        Assert.Equal(2, definition.Sequences.Length);
        Assert.Equal(0, definition.Sequences[0].Sequence); // seq 0 comes first
        Assert.Equal(2, definition.Sequences[1].Sequence); // seq 2 comes second
    }

    // =========================================================================
    // Scenario 28 — ToQuestDefinition_ThrowsWhenAnyRawIsNull
    // =========================================================================
    [Fact]
    public void ToQuestDefinition_ThrowsWhenAnyRawIsNull()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.ToQuestDefinition
         *
         * CONTRACT: Given a draft contains a step with Raw=null,
         *           When ToQuestDefinition() is called,
         *           Then DraftSerializationException is thrown
         *
         * BUILDER GUIDANCE: Check all steps for Raw == null before grouping.
         */

        // Arrange
        var draft = new QuestDraft(Quest2054, T0);
        var npcLoc = new NpcLocation(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));
        draft.AddStep(MakeDraftStep("accept-quest", 0, new AcceptStep { Id = "accept-quest", Target = npcLoc }), T0);
        // Step with Raw=null (modal abandoned mid-record)
        var nullRawStep = new DraftStep(
            StepId: "unconfirmed-step",
            StepType: "talk",
            SequenceNumber: 1,
            InferredFrom: InferredFrom.QuestSequenceChange,
            ObservedBefore: MakeSnapshot(),
            ObservedAfter: MakeSnapshot(T1),
            SuggestedExpect: null,
            Notes: null,
            Raw: null);
        draft.AddStep(nullRawStep, T0);

        // Act & Assert
        Assert.Throws<DraftSerializationException>(() => draft.ToQuestDefinition());
    }

    // =========================================================================
    // Scenario 29 — ToQuestDefinition_InfersAcceptFromFromFirstAcceptStep
    // =========================================================================
    [Fact]
    public void ToQuestDefinition_InfersAcceptFromFromFirstAcceptStep()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.ToQuestDefinition
         *
         * CONTRACT: Given draft contains an AcceptStep with Target = NpcLocation(1000789, 128, (9.5,40,14.2)),
         *           When ToQuestDefinition() is called,
         *           Then QuestDefinition.AcceptFrom equals that NpcLocation
         *
         * BUILDER GUIDANCE: InferAcceptFrom scans _steps for first AcceptStep and copies its Target.
         */

        // Arrange
        var draft = new QuestDraft(Quest2054, T0);
        var npcLoc = new NpcLocation(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));
        var acceptStep = new AcceptStep { Id = "accept-quest", Target = npcLoc };
        draft.AddStep(MakeDraftStep("accept-quest", 0, acceptStep), T0);

        var npcLoc2 = new NpcLocation(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));
        var turnInStep = new TurnInStep { Id = "turn-in-quest", Target = npcLoc2 };
        draft.AddStep(MakeDraftStep("turn-in-quest", 1, turnInStep), T0);

        // Act
        var definition = draft.ToQuestDefinition();

        // Assert
        Assert.NotNull(definition.AcceptFrom);
        Assert.Equal(1000789u, definition.AcceptFrom.NpcId);
        Assert.Equal(128, definition.AcceptFrom.Zone);
        Assert.Equal(9.5f, definition.AcceptFrom.Position.X);
        Assert.Equal(40f, definition.AcceptFrom.Position.Y);
        Assert.Equal(14.2f, definition.AcceptFrom.Position.Z);
    }

    // =========================================================================
    // Scenario 30 — ToQuestDefinition_ProducesValidSchema_FullRoundTrip
    // =========================================================================
    [Fact]
    public void ToQuestDefinition_ProducesValidSchema_FullRoundTrip()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.ToQuestDefinition
         *
         * CONTRACT: Given draft with accept, travel, talk, turn-in steps,
         *           When ToQuestDefinition() is called and the result is serialized
         *                then deserialized via QuestForgeJsonContext,
         *           Then the deserialized QuestDefinition equals the original
         *
         * BUILDER GUIDANCE: ToQuestDefinition must produce a QuestDefinition that round-trips
         *                   through QuestForgeJsonContext.QuestFileOptions.
         */

        // Arrange
        var draft = new QuestDraft(Quest2054, T0)
        {
            QuestName = "Ishgardian Justice",
            Category = "msq",
            Expansion = "heavensward",
            LastVerifiedPatch = "7.4"
        };
        var npcLoc = new NpcLocation(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));

        draft.AddStep(MakeDraftStep("accept-quest", 0, new AcceptStep { Id = "accept-quest", Target = npcLoc }), T0);
        draft.AddStep(MakeDraftStep("travel-to-zone-200", 1, new TravelStep
        {
            Id = "travel-to-zone-200",
            Destination = new TravelDestination(Zone: 200)
        }), T0);
        draft.AddStep(MakeDraftStep("talk-to-npc", 2, new TalkStep
        {
            Id = "talk-to-npc",
            Target = npcLoc,
            Expect = new PredicateExpect { Predicate = "questSequence(2054) >= 1" }
        }), T0);
        draft.AddStep(MakeDraftStep("turn-in-quest", 3, new TurnInStep { Id = "turn-in-quest", Target = npcLoc }), T0);

        // Act
        var definition = draft.ToQuestDefinition();

        // Serialize then deserialize
        var json = JsonSerializer.Serialize(definition, QuestForgeJsonContext.QuestFileOptions);
        var deserialized = JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(definition.Id, deserialized.Id);
        Assert.Equal(definition.Name, deserialized.Name);
        Assert.Equal(definition.Expansion, deserialized.Expansion);
        Assert.Equal(definition.Sequences.Length, deserialized.Sequences.Length);
        Assert.Equal(4, deserialized.Sequences.Length);
    }

    // =========================================================================
    // Scenario 31 — FakeDraftStorage_SaveThenLoad_RoundTrips
    // =========================================================================
    [Fact]
    public async Task FakeDraftStorage_SaveThenLoad_RoundTrips()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft.AddStep (FakeDraftStorage is fully implemented)
         *
         * CONTRACT: Given a draft with 3 steps,
         *           When Save then Load is called,
         *           Then loaded draft has 3 steps with identical IDs, types, sequence numbers
         *
         * BUILDER GUIDANCE: FakeDraftStorage stores by reference; steps must be readable via Steps.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var draft = new QuestDraft(Quest2054, T0);
        var npcLoc = new NpcLocation(NpcId: 1000789, Zone: 128, Position: new Position3(9.5f, 40f, 14.2f));

        draft.AddStep(MakeDraftStep("accept-quest", 0, new AcceptStep { Id = "accept-quest", Target = npcLoc }), T0);
        draft.AddStep(MakeDraftStep("talk-step-1", 1, new TalkStep { Id = "talk-step-1" }), T0);
        draft.AddStep(MakeDraftStep("turn-in-quest", 2, new TurnInStep { Id = "turn-in-quest", Target = npcLoc }), T0);

        // Act
        await storage.Save(Quest2054, draft, CancellationToken.None);
        var loadResult = await storage.Load(Quest2054, CancellationToken.None);

        // Assert
        Assert.True(loadResult.IsSuccess);
        var loaded = loadResult.ValueOrThrow;
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Steps.Count);
        Assert.Equal("accept-quest", loaded.Steps[0].StepId);
        Assert.Equal("talk-step-1", loaded.Steps[1].StepId);
        Assert.Equal("turn-in-quest", loaded.Steps[2].StepId);
        Assert.Equal(0, loaded.Steps[0].SequenceNumber);
        Assert.Equal(1, loaded.Steps[1].SequenceNumber);
        Assert.Equal(2, loaded.Steps[2].SequenceNumber);
    }

    // =========================================================================
    // Scenario 32 — FakeDraftStorage_ListDrafts_ReturnsAllSavedQuestIds
    // =========================================================================
    [Fact]
    public async Task FakeDraftStorage_ListDrafts_ReturnsAllSavedQuestIds()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft constructor
         *      (FakeDraftStorage.ListDrafts is fully implemented)
         *
         * CONTRACT: Given drafts saved for quests 100, 200, 300,
         *           When ListDrafts() is called,
         *           Then result contains exactly those 3 quest IDs (order unspecified)
         *
         * BUILDER GUIDANCE: FakeDraftStorage.ListDrafts returns _drafts.Keys.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var q100 = new QuestId(100);
        var q200 = new QuestId(200);
        var q300 = new QuestId(300);

        await storage.Save(q100, new QuestDraft(q100, T0), CancellationToken.None);
        await storage.Save(q200, new QuestDraft(q200, T0), CancellationToken.None);
        await storage.Save(q300, new QuestDraft(q300, T0), CancellationToken.None);

        // Act
        var listResult = await storage.ListDrafts(CancellationToken.None);

        // Assert
        Assert.True(listResult.IsSuccess);
        var ids = listResult.ValueOrThrow;
        Assert.Contains(q100, ids);
        Assert.Contains(q200, ids);
        Assert.Contains(q300, ids);
        Assert.Equal(3, ids.Count);
    }

    // =========================================================================
    // Scenario 33 — FakeDraftStorage_Delete_RemovesDraftAndAllBackups
    // =========================================================================
    [Fact]
    public async Task FakeDraftStorage_Delete_RemovesDraftAndAllBackups()
    {
        /*
         * RED: Will fail until Builder implements QuestDraft constructor
         *      (FakeDraftStorage.Delete and CreateBackup are fully implemented)
         *
         * CONTRACT: Given draft 2054 saved with 2 backups,
         *           When Delete(2054) is called,
         *           Then Load returns null, ListDrafts excludes 2054, BackupCount == 0
         *
         * BUILDER GUIDANCE: FakeDraftStorage.Delete removes from both _drafts and _backups.
         */

        // Arrange
        var storage = new FakeDraftStorage();
        var draft = new QuestDraft(Quest2054, T0);
        await storage.Save(Quest2054, draft, CancellationToken.None);
        await storage.CreateBackup(Quest2054, CancellationToken.None);
        await storage.CreateBackup(Quest2054, CancellationToken.None);
        Assert.Equal(2, storage.BackupCount(Quest2054)); // precondition

        // Act
        var deleteResult = await storage.Delete(Quest2054, CancellationToken.None);

        // Assert
        Assert.True(deleteResult.IsSuccess);
        Assert.True(deleteResult.ValueOrThrow);

        var loadResult = await storage.Load(Quest2054, CancellationToken.None);
        Assert.True(loadResult.IsSuccess);
        Assert.Null(loadResult.ValueOrThrow);

        var listResult = await storage.ListDrafts(CancellationToken.None);
        Assert.DoesNotContain(Quest2054, listResult.ValueOrThrow);

        Assert.Equal(0, storage.BackupCount(Quest2054));
    }
}
