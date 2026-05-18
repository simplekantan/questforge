using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using QuestForge.Schema;
using Xunit;
using AdaptersAetheryteId = QuestForge.Adapters.Types.AetheryteId;

// WHY: Both QuestForge.Schema and QuestForge.Adapters.Types define AetheryteId.
// GameStateSnapshot uses Adapters.Types.AetheryteId for LastAethernetShardInteracted.
// The alias AdaptersAetheryteId disambiguates the two types.
// AethernetId (shard type) is from Adapters.Types exclusively.
// AethernetRouteHint is in QuestForge.Schema (new type added by Builder).

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for the updated StepFactory.Build that constructs
/// RouteHint with the new AethernetRouteHint type instead of uint[] (Issue #25, Component 7).
///
/// ALL tests will fail to compile or at runtime until Builder:
///   1. Adds AethernetRouteHint record to QuestForge.Schema
///   2. Changes RouteHint.Aethernet from uint[]? to AethernetRouteHint?
///   3. Updates StepFactory.BuildAethernetTravelStep to use the new type
/// </summary>
public sealed class AethernetStepFactoryTests
{
    private static readonly QuestId ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        AdaptersAetheryteId? lastAethernetShardInteracted = null,
        AethernetId? aethernetDestinationSelected = null,
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
            // RED: Both properties do not exist yet
            LastAethernetShardInteracted = lastAethernetShardInteracted,
            AethernetDestinationSelected = aethernetDestinationSelected
        };

    // =========================================================================
    // SF_NEW_1 — AethernetDestinationSelected → RouteHint.Aethernet uses new type
    // =========================================================================

    [Fact]
    public void SF_NEW_1_AethernetDestinationSelected_BuildsRouteHintWithNewType()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint and
         * changes RouteHint.Aethernet from uint[]? to AethernetRouteHint?.
         *
         * CONTRACT: Given before with LastNpcInteracted=125, LastAethernetShardInteracted=125,
         *           LastNpcPosition=(10,0,20), AethernetDestinationSelected=AethernetId(77),
         *           after with zone=130, player position=(100,5,-50),
         *           When Build("travel", ..., after, before: before) is called,
         *           Then TravelStep.RouteHint.Aethernet is AethernetRouteHint,
         *                RouteHint.Aethernet.To == 77 (from AethernetDestinationSelected),
         *                RouteHint.Aethernet.From == 125 (from before.LastAethernetShardInteracted).
         *
         * BUILDER GUIDANCE: In BuildAethernetTravelStep, change:
         *   new RouteHint(Aethernet: new[] { destShard!.Value.Value })
         * to:
         *   new RouteHint(Aethernet: new AethernetRouteHint(
         *       From: sourceShardId?.Value,
         *       To: destShardValue))
         * Where destShardValue comes from AethernetDestinationSelected first,
         * then after.LastAethernetShardInteracted as fallback.
         */

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            lastAethernetShardInteracted: new AdaptersAetheryteId(125u),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            capturedAt: T1);

        var step = StepFactory.Build("travel", "aethernet-125-77", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<TravelStep>(step);
        Assert.NotNull(travel.RouteHint);
        // RED: RouteHint.Aethernet is still uint[]? — compile error expected when
        // asserting AethernetRouteHint properties
        Assert.NotNull(travel.RouteHint!.Aethernet);
        // After Builder changes type: Aethernet should be AethernetRouteHint, not uint[]
        var aethernetHint = travel.RouteHint!.Aethernet;
        Assert.Equal(77u, aethernetHint!.To);
        Assert.Equal(125u, aethernetHint.From);
    }

    // =========================================================================
    // SF_NEW_2 — From is null when source shard doesn't match LastNpcInteracted
    // =========================================================================

    [Fact]
    public void SF_NEW_2_FromIsNull_WhenNpcDoesNotMatchShard()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given before with LastNpcInteracted=NpcId(7) (different from shard 125),
         *           AethernetDestinationSelected=AethernetId(77),
         *           but shard conditions NOT met (7 != 125),
         *           When Build("travel", ...) is called without aethernet path,
         *           Then RouteHint is null (not an aethernet travel step).
         *
         * BUILDER GUIDANCE: The aethernet travel path requires NpcId to match shard ID.
         * When they differ, StepFactory treats it as a regular cross-zone travel.
         */

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            position: new WorldPosition(60, 4, -80),
            lastNpcInteracted: new NpcId(7u),   // does NOT match shard 125
            lastNpcPosition: new WorldPosition(10, 0, 20),
            lastAethernetShardInteracted: new AdaptersAetheryteId(125u),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            capturedAt: T1);

        var step = StepFactory.Build("travel", "travel-130", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<TravelStep>(step);
        Assert.Null(travel.RouteHint);  // Not an aethernet step — shard conditions not met
    }

    // =========================================================================
    // SF_NEW_3 — To comes from after.LastAethernetShardInteracted when AethernetDestinationSelected is null
    // =========================================================================

    [Fact]
    public void SF_NEW_3_To_FallsBackTo_AfterLastShard_WhenDestinationSelectedNull()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given before with NpcId(125) matching shard 125, AethernetDestinationSelected=null,
         *           after.LastAethernetShardInteracted=AetheryteId(33),
         *           When Build("travel", ...) is called,
         *           Then RouteHint.Aethernet.To == 33 (from after.LastAethernetShardInteracted).
         *
         * BUILDER GUIDANCE: When AethernetDestinationSelected is null, fall back to
         * after.LastAethernetShardInteracted?.Value.Value for the To field.
         */

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            lastAethernetShardInteracted: new AdaptersAetheryteId(125u),
            aethernetDestinationSelected: null);
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            lastAethernetShardInteracted: new AdaptersAetheryteId(33u),
            capturedAt: T1);

        var step = StepFactory.Build("travel", "aethernet-125-33", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<TravelStep>(step);
        Assert.NotNull(travel.RouteHint);
        Assert.NotNull(travel.RouteHint!.Aethernet);
        Assert.Equal(33u, travel.RouteHint!.Aethernet!.To);
        Assert.Equal(125u, travel.RouteHint.Aethernet.From);
    }

    // =========================================================================
    // SF_NEW_4 — No destination info → RouteHint is null (unconfirmed aethernet)
    // =========================================================================

    [Fact]
    public void SF_NEW_4_NoDestinationInfo_RouteHintIsNull()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given before conditions met (NPC=125, shard=125) but both
         *           AethernetDestinationSelected=null and after.LastAethernetShardInteracted=null,
         *           When Build("travel", ...) is called,
         *           Then RouteHint is null (no confirmed destination shard).
         *
         * BUILDER GUIDANCE: Only populate RouteHint.Aethernet when a concrete To value
         * is available. When destShardValue == 0 or cannot be determined, emit null.
         */

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            lastAethernetShardInteracted: new AdaptersAetheryteId(125u),
            aethernetDestinationSelected: null);
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            lastAethernetShardInteracted: null,
            capturedAt: T1);

        var step = StepFactory.Build("travel", "travel-130", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<TravelStep>(step);
        Assert.Null(travel.RouteHint);
    }

    // =========================================================================
    // SF_NEW_5 — SourcePosition preserved in Destination.Position
    // =========================================================================

    [Fact]
    public void SF_NEW_5_SourceShardPosition_UsedAs_DestinationPosition()
    {
        /*
         * RED: Will fail to compile until Builder adds AethernetRouteHint.
         *
         * CONTRACT: Given before.LastNpcPosition=(10,0,20) (source shard world coords),
         *           When aethernet step is built,
         *           Then TravelStep.Destination.Position == (10,0,20).
         *           This is unchanged from the existing behaviour — the test validates
         *           the position is preserved after the RouteHint type change.
         *
         * BUILDER GUIDANCE: Only RouteHint.Aethernet changes type; the Destination.Position
         * logic (using source shard's world position) is unchanged.
         */

        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            lastAethernetShardInteracted: new AdaptersAetheryteId(125u),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            position: new WorldPosition(100, 5, -50),
            capturedAt: T1);

        var step = StepFactory.Build("travel", "aethernet-125-77", "playerZone() == 130", after, before: before);

        var travel = Assert.IsType<TravelStep>(step);
        Assert.NotNull(travel.Destination.Position);
        Assert.Equal(10f, travel.Destination.Position!.X, precision: 3);
        Assert.Equal(0f,  travel.Destination.Position!.Y, precision: 3);
        Assert.Equal(20f, travel.Destination.Position!.Z, precision: 3);
    }
}
