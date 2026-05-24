using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported at the namespace level to avoid AetheryteId
// collisions with Adapters.Types.AetheryteId. Schema step types are fully qualified below.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for StepFactory.Build correctly populating the new RequiredZone field.
/// Issue #38 — groups B and C acceptance criteria (B1–B6, C1–C3).
///
/// ALL tests will fail to compile until Builder:
///   - Adds RequiredZone property to Step base class (QuestForge.Schema\Step.cs)
///   - Adds ZoneStr private helper to StepFactory
///   - Wires RequiredZone = ZoneStr(sourceZone) in BuildNpcDialogueTravelStep
///   - Wires RequiredZone = ZoneStr(before.Zone.Value) in BuildAethernetTravelStep
///   - Wires RequiredZone = ZoneStr(before?.Zone.Value ?? 0u) in the bare "travel" arm
///   - Wires RequiredZone = zoneStr in all non-travel arms and the default fallback
/// </summary>
public sealed class RequiredZoneStepFactoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);
    private static readonly QuestId ActiveQuest = new(2054);

    /// <summary>
    /// Shared MakeSnapshot helper — matches the shape from NpcDialogueStepFactoryTests and
    /// AethernetStepFactoryTests. Accepts all fields needed for any factory arm.
    /// </summary>
    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        AetheryteId? lastAethernetShardInteracted = null,
        AethernetId? aethernetDestinationSelected = null,
        int? dialogueOptionSelected = null,
        DateTimeOffset? capturedAt = null,
        QuestForge.Schema.NpcLocation? dialogueNpcSource = null) =>
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
            AethernetDestinationSelected = aethernetDestinationSelected,
            DialogueOptionSelected = dialogueOptionSelected,
            DialogueNpcSource = dialogueNpcSource
        };

    // =========================================================================
    // B1 — NpcDialogue travel: RequiredZone = source zone (NPC's zone), NOT after-zone
    // =========================================================================

    [Fact]
    public void B1_NpcDialogueTravel_RequiredZone_IsSourceZone_NotAfterZone()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step and wires
         * RequiredZone = ZoneStr(sourceZone) in BuildNpcDialogueTravelStep.
         *
         * CONTRACT: Given SF-ND-1 inputs — before.Zone=131, after.Zone=200,
         *           dialogueNpcSource.Zone=131, dialogue option selected,
         *           When Build("travel", ...) is called,
         *           Then step.RequiredZone == "131" (NPC source zone),
         *                NOT "200" (after-zone).
         */

        // Arrange — mirrors SF-ND-1 happy-path inputs exactly
        var before = MakeSnapshot(zone: new ZoneId(131));
        var after = MakeSnapshot(
            zone: new ZoneId(200),
            position: new WorldPosition(5f, 0f, 5f),
            lastNpcInteracted: new NpcId(7777),
            lastNpcPosition: new WorldPosition(10f, 0.5f, 20f),
            dialogueOptionSelected: 0,
            capturedAt: T1,
            dialogueNpcSource: new QuestForge.Schema.NpcLocation(
                NpcId: 7777,
                Zone: 131,
                Position: new QuestForge.Schema.Position3(10f, 0.5f, 20f)));

        // Act
        var step = StepFactory.Build("travel", "npc-dialogue-to-zone-200",
            "playerZone() == 200", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.RouteHint?.NpcDialogue);
        // RED: RequiredZone does not exist yet
        Assert.Equal("131", travel.RequiredZone);  // source zone, not after-zone
        Assert.NotEqual("200", travel.RequiredZone); // explicitly not the after-zone
    }

    // =========================================================================
    // B2 — Aethernet travel: RequiredZone = before.Zone, NOT after-zone
    // =========================================================================

    [Fact]
    public void B2_AethernetTravel_RequiredZone_IsBeforeZone_NotAfterZone()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step and wires
         * RequiredZone = ZoneStr(before.Zone.Value) in BuildAethernetTravelStep.
         *
         * CONTRACT: Given SF_NEW_1 inputs — before.Zone=131, shard==NPC==125,
         *           aethernetDestinationSelected=77, after.Zone=130,
         *           When Build("travel", ...) is called,
         *           Then step.RequiredZone == "131" (before.Zone),
         *                NOT "130" (after-zone).
         */

        // Arrange — mirrors SF_NEW_1 inputs exactly
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            lastAethernetShardInteracted: new AetheryteId(125u),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "aethernet-125-77",
            "playerZone() == 130", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.NotNull(travel.RouteHint?.Aethernet);
        // RED: RequiredZone does not exist yet
        Assert.Equal("131", travel.RequiredZone);  // before.Zone, not after-zone
        Assert.NotEqual("130", travel.RequiredZone); // explicitly not the after-zone
    }

    // =========================================================================
    // B3 — Bare/plain travel: RequiredZone = before.Zone, NOT after-zone
    // =========================================================================

    [Fact]
    public void B3_BareTravel_RequiredZone_IsBeforeZone_NotAfterZone()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step and wires
         * RequiredZone = ZoneStr(before?.Zone.Value ?? 0u) in the bare "travel" arm.
         *
         * CONTRACT: Given plain zone-gate travel — before.Zone=129, after.Zone=128,
         *           no dialogue option, no aethernet destination,
         *           When Build("travel", ...) is called,
         *           Then step.RequiredZone == "129" (before.Zone),
         *                NOT "128" (after-zone).
         *
         * Input shape mirrors the negative setup of SF-ND-4 and SF_NEW_2:
         * no dialogueOptionSelected, no shard/NPC match → falls through to bare arm.
         */

        // Arrange — plain zone-gate: no NPC dialogue, no aethernet conditions met
        var before = MakeSnapshot(zone: new ZoneId(129));
        var after = MakeSnapshot(
            zone: new ZoneId(128),
            position: new WorldPosition(50f, 0f, 50f),
            lastNpcInteracted: null,       // no NPC → npcDialogue arm won't fire
            dialogueOptionSelected: null,  // no dialogue → npcDialogue arm won't fire
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "zone-gate-129-to-128",
            "playerZone() == 128", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Null(travel.RouteHint?.NpcDialogue);
        Assert.Null(travel.RouteHint?.Aethernet);
        // RED: RequiredZone does not exist yet
        Assert.Equal("129", travel.RequiredZone);  // before.Zone, not after-zone
        Assert.NotEqual("128", travel.RequiredZone); // explicitly not the after-zone
    }

    // =========================================================================
    // B4 — Bare travel with null before → RequiredZone == null
    // =========================================================================

    [Fact]
    public void B4_BareTravel_BeforeNull_RequiredZoneIsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given Build("travel", ..., after, before: null) with after producing
         *           a plain TravelStep (no dialogue, no aethernet signals),
         *           When Build is called,
         *           Then step.RequiredZone == null.
         *
         * ZoneStr(before?.Zone.Value ?? 0u) collapses "before is null" → ZoneStr(0) → null.
         */

        // Arrange
        var after = MakeSnapshot(
            zone: new ZoneId(128),
            position: new WorldPosition(50f, 0f, 50f),
            dialogueOptionSelected: null,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "zone-gate-to-128",
            "playerZone() == 128", after, before: null);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        // RED: RequiredZone does not exist yet
        Assert.Null(travel.RequiredZone);
    }

    // =========================================================================
    // B5 — Bare travel with before.Zone = ZoneId(0) → RequiredZone == null
    // =========================================================================

    [Fact]
    public void B5_BareTravel_BeforeZoneIsZero_RequiredZoneIsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given before.Zone = ZoneId(0) (unknown/uninitialized zone),
         *           When Build("travel", ...) is called for a plain TravelStep,
         *           Then step.RequiredZone == null.
         *
         * The "zone > 0 ? ... : null" convention treats zone 0 as unknown.
         * ZoneStr(0u) must return null, not "0".
         */

        // Arrange — before zone is 0 (uninitialized); after has a real zone
        var before = MakeSnapshot(zone: new ZoneId(0));
        var after = MakeSnapshot(
            zone: new ZoneId(128),
            position: new WorldPosition(50f, 0f, 50f),
            dialogueOptionSelected: null,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "zone-gate-to-128",
            "playerZone() == 128", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        // RED: RequiredZone does not exist yet
        Assert.Null(travel.RequiredZone);
    }

    // =========================================================================
    // B6 — Zone unchanged (regression): RequiredZone does not alter the Zone field
    // =========================================================================

    [Fact]
    public void B6_AethernetTravel_ZoneStillIsAfterZone_RequiredZoneIsBeforeZone()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: For aethernet travel (SF_NEW_1 inputs), after Build:
         *           step.Zone == "130" (after-zone, unchanged)
         *           step.RequiredZone == "131" (before.Zone)
         *
         * Proves RequiredZone is purely additive — it does not alter the existing Zone field.
         */

        // Arrange — mirrors SF_NEW_1 exactly
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            lastAethernetShardInteracted: new AetheryteId(125u),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "aethernet-125-77",
            "playerZone() == 130", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal("130", travel.Zone);             // after-zone — unchanged by RequiredZone
        // RED: RequiredZone does not exist yet
        Assert.Equal("131", travel.RequiredZone);     // before.Zone — the new field
    }

    [Fact]
    public void B6b_BareTravel_ZoneStillIsAfterZone_RequiredZoneIsBeforeZone()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: For bare zone-gate travel (before.Zone=129, after.Zone=128), after Build:
         *           step.Zone == "128" (after-zone, unchanged)
         *           step.RequiredZone == "129" (before.Zone)
         */

        // Arrange
        var before = MakeSnapshot(zone: new ZoneId(129));
        var after = MakeSnapshot(
            zone: new ZoneId(128),
            position: new WorldPosition(50f, 0f, 50f),
            dialogueOptionSelected: null,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("travel", "zone-gate-129-to-128",
            "playerZone() == 128", after, before: before);

        // Assert
        var travel = Assert.IsType<QuestForge.Schema.TravelStep>(step);
        Assert.Equal("128", travel.Zone);             // after-zone — unchanged
        // RED: RequiredZone does not exist yet
        Assert.Equal("129", travel.RequiredZone);     // before.Zone — the new field
    }

    // =========================================================================
    // C1 — Non-travel step types: RequiredZone == Zone (7 types via Theory)
    // =========================================================================

    [Theory]
    [InlineData("accept")]
    [InlineData("turn-in")]
    [InlineData("talk")]
    [InlineData("hand-over-item")]
    [InlineData("attune")]
    [InlineData("pickup-item")]
    [InlineData("interact-object")]
    public void C1_NonTravelSteps_RequiredZone_EqualsZone(string stepType)
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step and wires
         * RequiredZone = zoneStr in all non-travel arms.
         *
         * CONTRACT: For each non-travel step type with after.Zone = 130,
         *           When Build(<type>, ...) is called,
         *           Then step.RequiredZone == "130"
         *                AND step.RequiredZone == step.Zone.
         *
         * For these steps the player neither moves nor changes zone, so before-zone
         * == after-zone == zoneStr. RequiredZone = zoneStr = Zone.
         */

        // Arrange
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(10f, 0f, 20f),
            lastNpcInteracted: new NpcId(1000789u),
            lastNpcPosition: new WorldPosition(12f, 0f, 22f),
            dialogueOptionSelected: null,
            lastAethernetShardInteracted: new AetheryteId(8u),  // needed for attune arm
            capturedAt: T1);

        // Act
        var step = StepFactory.Build(stepType, $"{stepType}-step",
            $"playerZone() == 130", after, before: null);

        // Assert
        // RED: RequiredZone does not exist yet
        Assert.Equal("130", step.RequiredZone);   // must equal the zone string
        Assert.Equal(step.Zone, step.RequiredZone); // must equal Zone for non-travel types
    }

    // =========================================================================
    // C2 — Default fallback arm: RequiredZone == Zone
    // =========================================================================

    [Fact]
    public void C2_DefaultFallbackArm_RequiredZone_EqualsZone()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step and wires
         * RequiredZone = zoneStr in the default fallback arm.
         *
         * CONTRACT: Given an unrecognized step type string (falls through to default TalkStep arm),
         *           with after.Zone = 130,
         *           When Build("totally-unknown", ...) is called,
         *           Then the produced step's RequiredZone == "130" (== Zone).
         */

        // Arrange
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(10f, 0f, 20f),
            lastNpcInteracted: new NpcId(1000789u),
            lastNpcPosition: new WorldPosition(12f, 0f, 22f),
            dialogueOptionSelected: null,
            capturedAt: T1);

        // Act — "totally-unknown" hits the default (_) arm → produces TalkStep
        var step = StepFactory.Build("totally-unknown", "fallback-step",
            null, after, before: null);

        // Assert
        Assert.IsType<QuestForge.Schema.TalkStep>(step); // default arm produces TalkStep
        // RED: RequiredZone does not exist yet
        Assert.Equal("130", step.RequiredZone);
        Assert.Equal(step.Zone, step.RequiredZone);
    }

    // =========================================================================
    // C3 — Non-travel step with after.Zone = ZoneId(0) → RequiredZone == null
    // =========================================================================

    [Fact]
    public void C3_TalkStep_AfterZoneIsZero_RequiredZoneIsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds RequiredZone to Step.
         *
         * CONTRACT: Given Build("talk", ...) with after.Zone = ZoneId(0) (unknown zone),
         *           When Build is called,
         *           Then step.RequiredZone == null
         *                AND step.Zone == null.
         *
         * The existing zoneStr convention: zone > 0 ? zone.ToString() : null.
         * RequiredZone = zoneStr follows the same rule; both are null when zone is 0.
         */

        // Arrange
        var after = MakeSnapshot(
            zone: new ZoneId(0),  // unknown zone
            position: new WorldPosition(10f, 0f, 20f),
            lastNpcInteracted: new NpcId(1000789u),
            lastNpcPosition: new WorldPosition(12f, 0f, 22f),
            dialogueOptionSelected: null,
            capturedAt: T1);

        // Act
        var step = StepFactory.Build("talk", "talk-unknown-zone",
            null, after, before: null);

        // Assert
        Assert.IsType<QuestForge.Schema.TalkStep>(step);
        Assert.Null(step.Zone);          // existing behaviour: zone 0 → null
        // RED: RequiredZone does not exist yet
        Assert.Null(step.RequiredZone);  // must match Zone: zone 0 → null
    }
}
