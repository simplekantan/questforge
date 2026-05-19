using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported at the namespace level to avoid AetheryteId
// collisions with Adapters.Types.AetheryteId. Schema step types are fully qualified below.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for StepFactory.Build producing TravelStep with RouteHint.NpcDialogue.
///
/// ALL tests will fail to compile or runtime-fail until Builder:
///   - Adds NpcDialogue sub-case to StepFactory.Build "travel" arm
///   - NpcDialogue conditions: after.DialogueOptionSelected.HasValue
///                             AND after.LastNpcInteracted.HasValue
///                             AND after.LastNpcPosition.HasValue
///   - NpcDialogue sub-case must come BEFORE aethernet sub-case
///   - NpcDialogue.Target.Position = after.LastNpcPosition (NPC position, NOT player position)
///   - NpcDialogue.Target.Zone = after.Zone (source zone where NPC is)
///   - Destination.Position = after.Position (player landing position)
///   - Destination.Zone = after.Zone
///
/// Test IDs: SF-ND-1 through SF-ND-12
/// </summary>
public sealed class NpcDialogueStepFactoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);
    private static readonly QuestId ActiveQuest = new(2054);

    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        AetheryteId? lastAethernetShardInteracted = null,
        int? dialogueOptionSelected = null,
        DateTimeOffset? capturedAt = null) =>
        new(
            CapturedAt: capturedAt ?? T0,
            Zone: zone ?? new ZoneId(131),
            Position: position ?? new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: 0,
            QuestFlags: 0,
            QuestAccepted: false,
            QuestCompleted: false,
            LastNpcInteracted: lastNpcInteracted,
            LastNpcPosition: lastNpcPosition,
            LastDialoguePrompt: null,
            LastDialogueAnswer: null,
            InventoryHash: 0,
            LastAttuned: null)
        {
            LastAethernetShardInteracted = lastAethernetShardInteracted,
            DialogueOptionSelected = dialogueOptionSelected
        };

    // SF-ND-1
    [Fact]
    public void SFND1_HappyPath_AllFieldsPopulatedCorrectly()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case to StepFactory "travel" arm.
         *
         * CONTRACT: Given after.Zone=200, after.LastNpcInteracted=NpcId(7777),
         *           after.LastNpcPosition=(10,0.5f,20), after.Position=(5,0,5),
         *           after.DialogueOptionSelected=0,
         *           before.Zone=131,
         *           When Build("travel", ...) is called,
         *           Then TravelStep.Destination.Zone=200,
         *                TravelStep.RouteHint.NpcDialogue is not null,
         *                NpcDialogue.Target.NpcId=7777,
         *                NpcDialogue.Target.Zone=131 (source zone from before.Zone),
         *                NpcDialogue.Target.Position=(10,0.5f,20) (NPC position),
         *                NpcDialogue.DialogueChoices[0].Answer=="0",
         *                RouteHint.Aethernet==null.
         *
         * BUILDER GUIDANCE: Source zone for NpcDialogue.Target.Zone = before.Zone
         * (where the NPC resides before the zone transition).
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            position: new WorldPosition(5f, 0f, 5f),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0.5f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal(200, travel.Destination.Zone);
        Assert.NotNull(travel.RouteHint);
        Assert.NotNull(travel.RouteHint!.NpcDialogue);
        Assert.Null(travel.RouteHint.Aethernet);

        var npc = travel.RouteHint.NpcDialogue!;
        Assert.Equal(7777u, npc.Target.NpcId);
        Assert.Equal(131, npc.Target.Zone);
        Assert.Equal(10f,   npc.Target.Position.X, precision: 3);
        Assert.Equal(0.5f,  npc.Target.Position.Y, precision: 3);
        Assert.Equal(20f,   npc.Target.Position.Z, precision: 3);

        Assert.Single(npc.DialogueChoices);
        Assert.Equal("0", npc.DialogueChoices[0].Answer);
        Assert.Equal("list", npc.DialogueChoices[0].Type);
    }

    // SF-ND-2
    [Fact]
    public void SFND2_NpcDialogueTakesPriorityOverAethernet()
    {
        /*
         * RED: Will fail until Builder places NpcDialogue sub-case before aethernet sub-case.
         *
         * CONTRACT: Given NpcDialogue conditions met AND aethernet conditions also met
         *           (shard==NPC, both match),
         *           When Build is called,
         *           Then RouteHint.NpcDialogue is set, RouteHint.Aethernet is null.
         */

        // Arrange
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            lastAethernetShardInteracted: new AetheryteId(7777)); // matches NPC
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            position: new WorldPosition(5f, 0f, 5f),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            lastAethernetShardInteracted: new AetheryteId(33),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.RouteHint?.NpcDialogue);
        Assert.Null(travel.RouteHint!.Aethernet);
    }

    // SF-ND-3
    [Fact]
    public void SFND3_SourceZoneFallsBackToAfterZoneWhenBeforeIsNull()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case with fallback zone logic.
         *
         * CONTRACT: Given Build called with before=null, after.Zone=200,
         *           When NpcDialogue conditions met (DialogueOptionSelected set, LastNpcInteracted set),
         *           Then NpcDialogue.Target.Zone = after.Zone (200) as fallback.
         *
         * BUILDER GUIDANCE: When before is null, use after.Zone as the source zone.
         */

        // Arrange
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            position: new WorldPosition(5f, 0f, 5f),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: null);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.RouteHint?.NpcDialogue);
        // Source zone falls back to after.Zone
        Assert.Equal(200, travel.RouteHint!.NpcDialogue!.Target.Zone);
    }

    // SF-ND-4
    [Fact]
    public void SFND4_NoBuildWhenLastNpcInteractedIsNull()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case with null guards.
         *
         * CONTRACT: Given DialogueOptionSelected is set but LastNpcInteracted == null,
         *           When Build("travel", ...) is called,
         *           Then RouteHint.NpcDialogue is null (falls through to regular travel).
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            lastNpcInteracted: null, // no NPC
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "travel-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.True(
            travel.RouteHint?.NpcDialogue is null,
            "NpcDialogue must be null when LastNpcInteracted is null");
    }

    // SF-ND-5
    [Fact]
    public void SFND5_NoBuildWhenLastNpcPositionIsNull()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case with null guards.
         *
         * CONTRACT: Given DialogueOptionSelected is set, LastNpcInteracted is set,
         *           but LastNpcPosition == null,
         *           When Build("travel", ...) is called,
         *           Then RouteHint.NpcDialogue is null (no position = cannot build NpcLocation).
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: null, // no position
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "travel-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.True(
            travel.RouteHint?.NpcDialogue is null,
            "NpcDialogue must be null when LastNpcPosition is null");
    }

    // SF-ND-6
    [Fact]
    public void SFND6_NoBuildWhenDialogueOptionSelectedIsNull()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case with null guards.
         *
         * CONTRACT: Given LastNpcInteracted and LastNpcPosition are both set,
         *           but DialogueOptionSelected == null,
         *           When Build("travel", ...) is called,
         *           Then RouteHint.NpcDialogue is null.
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: null, // not set
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "travel-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.True(
            travel.RouteHint?.NpcDialogue is null,
            "NpcDialogue must be null when DialogueOptionSelected is null");
    }

    // SF-ND-7
    [Fact]
    public void SFND7_AnswerUsesInvariantCulture_Index0Gives0()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case with InvariantCulture formatting.
         *
         * CONTRACT: Given DialogueOptionSelected=0,
         *           When NpcDialogue is built,
         *           Then DialogueChoices[0].Answer == "0" (InvariantCulture; not locale-dependent).
         *
         * BUILDER GUIDANCE: Use idx.ToString(CultureInfo.InvariantCulture) — same pattern as
         * the existing TalkStep DialogueChoices path in StepFactory.
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.RouteHint?.NpcDialogue);
        Assert.Equal("0", travel.RouteHint!.NpcDialogue!.DialogueChoices[0].Answer);
    }

    // SF-ND-8
    [Fact]
    public void SFND8_DialogueChoicesOverrideTakesPrecedence()
    {
        /*
         * RED: Will fail until Builder wires dialogueChoicesOverride into NpcDialogue path.
         *
         * CONTRACT: Given dialogueChoicesOverride=[3,1] and DialogueOptionSelected=0,
         *           When Build is called,
         *           Then NpcDialogue.DialogueChoices[0].Answer=="3" (override wins).
         *
         * BUILDER GUIDANCE: The existing dialogueChoicesOverride→dialogueChoices logic in
         * StepFactory should also feed into NpcDialogue.DialogueChoices, not just TalkStep.
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: before,
            dialogueChoicesOverride: [3, 1]);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.RouteHint?.NpcDialogue);
        var choices = travel.RouteHint!.NpcDialogue!.DialogueChoices;
        Assert.Equal(2, choices.Length);
        Assert.Equal("3", choices[0].Answer);
        Assert.Equal("1", choices[1].Answer);
    }

    // SF-ND-9
    [Fact]
    public void SFND9_AethernetPathPreservedWhenNpcDialogueConditionsNotMet()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case (aethernet regression guard).
         *
         * CONTRACT: Given aethernet conditions met, DialogueOptionSelected==null,
         *           When Build is called,
         *           Then RouteHint.Aethernet is set, RouteHint.NpcDialogue is null.
         */

        // Arrange
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125),
            lastNpcPosition: new WorldPosition(10f, 0f, 20f),
            lastAethernetShardInteracted: new AetheryteId(125));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(5f, 0f, 5f),
            lastAethernetShardInteracted: new AetheryteId(33),
            dialogueOptionSelected: null, // NpcDialogue sub-case must NOT fire
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "aethernet-to-zone-130",
            "playerZone() == 130", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.RouteHint?.Aethernet);
        Assert.Null(travel.RouteHint!.NpcDialogue);
    }

    // SF-ND-10
    [Fact]
    public void SFND10_TalkStepStillGetsDialogueChoicesFromSnapshot_Regression()
    {
        /*
         * RED: Regression guard — TalkStep path must be unaffected by NpcDialogue changes.
         *
         * CONTRACT: Given Build("talk", ...) with DialogueOptionSelected=2,
         *           When Build executes,
         *           Then TalkStep.DialogueChoices[0].Answer=="2" (existing behaviour preserved).
         */

        // Arrange
        var after = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(9),
            lastNpcPosition: new WorldPosition(5f, 0f, 5f),
            dialogueOptionSelected: 2);

        // Act
        var step = StepFactory.Build("talk", "talk-npc", null, after);

        // Assert
        var talk = Assert.IsType<QuestForge.Schema.TalkStep>(step);
        Assert.Single(talk.DialogueChoices);
        Assert.Equal("2", talk.DialogueChoices[0].Answer);
    }

    // SF-ND-11
    [Fact]
    public void SFND11_Destination_Position_Is_AfterPlayerPosition()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case.
         *
         * CONTRACT: Given after.Position=(5,1,7) (player landing position),
         *           When Build builds NpcDialogue TravelStep,
         *           Then Destination.Position == (5,1,7) (player landing position, NOT NPC position).
         *
         * BUILDER GUIDANCE: Destination is where the player ends up.
         * NpcDialogue.Target is where the NPC is (source zone).
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            position: new WorldPosition(5f, 1f, 7f),   // player landing position
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0.5f, 20f), // NPC position (different)
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.Destination.Position);
        Assert.Equal(5f, travel.Destination.Position!.X, precision: 3);
        Assert.Equal(1f, travel.Destination.Position.Y, precision: 3);
        Assert.Equal(7f, travel.Destination.Position.Z, precision: 3);
    }

    // SF-ND-12
    [Fact]
    public void SFND12_NpcDialogue_Target_Position_Is_AfterLastNpcPosition_Not_PlayerPosition()
    {
        /*
         * RED: Will fail until Builder adds NpcDialogue sub-case.
         *
         * CONTRACT: Given after.Position=(5,1,7) (player) and after.LastNpcPosition=(10,0.5f,20) (NPC),
         *           When NpcDialogue.Target.Position is read,
         *           Then it equals (10,0.5f,20), NOT (5,1,7).
         *
         * BUILDER GUIDANCE: NpcDialogue.Target.Position = after.LastNpcPosition (NPC's world position).
         * This is distinct from Destination.Position which is the player's landing position (SF-ND-11).
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            position: new WorldPosition(5f, 1f, 7f),             // player position
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0.5f, 20f),  // NPC position
            dialogueOptionSelected: 0,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        var npcPos = travel.RouteHint!.NpcDialogue!.Target.Position;
        Assert.Equal(10f,   npcPos.X, precision: 3);
        Assert.Equal(0.5f,  npcPos.Y, precision: 3);
        Assert.Equal(20f,   npcPos.Z, precision: 3);
        // Verify it is NOT the player position
        Assert.NotEqual(5f, npcPos.X);
    }
}
