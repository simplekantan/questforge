using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;
using Xunit;

// WHY: QuestForge.Schema is NOT imported here because Schema.AetheryteId and
// Adapters.Types.AetheryteId would collide. Schema step types are referenced with
// full qualification when needed.
// AethernetId (shard IDs) and AetheryteId (master crystal IDs) are DISTINCT types
// in Adapters.Types — this file exclusively uses AethernetId for shard values.

namespace QuestForge.Engine.Tests.Authoring;

/// <summary>
/// RED PHASE: Tests for the updated StepInferenceEngine aethernet rules using the new
/// AethernetDestinationSelected snapshot field (Issue #25, Component 5).
///
/// Design change: inference now uses:
///   - to: prefer before.AethernetDestinationSelected, fallback to after.LastAethernetShardInteracted
///   - from: before.LastAethernetShardInteracted when LastNpcInteracted == LastAethernetShardInteracted
///
/// ALL tests will fail until Builder:
///   1. Adds AethernetDestinationSelected to GameStateSnapshot
///   2. Updates the aethernet inference sub-case in StepInferenceEngine Rule 4
/// </summary>
public sealed class AethernetInferenceV2Tests
{
    private static readonly QuestId ActiveQuest = new(2054);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(30);

    private static GameStateSnapshot MakeSnapshot(
        ZoneId? zone = null,
        WorldPosition? position = null,
        NpcId? lastNpcInteracted = null,
        WorldPosition? lastNpcPosition = null,
        AetheryteId? lastAethernetShardInteracted = null,
        AethernetId? aethernetDestinationSelected = null,
        bool questCompleted = false,
        bool questAccepted = false,
        int questSequence = 0,
        DateTimeOffset? capturedAt = null) =>
        new(
            CapturedAt: capturedAt ?? T0,
            Zone: zone ?? new ZoneId(131),
            Position: position ?? new WorldPosition(0, 0, 0),
            ActiveQuest: ActiveQuest,
            QuestSequence: questSequence,
            QuestFlags: 0,
            QuestAccepted: questAccepted,
            QuestCompleted: questCompleted,
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
    // INF2_1 — AethernetDestinationSelected preferred over LastAethernetShardInteracted for "to"
    // =========================================================================

    [Fact]
    public void INF2_1_AethernetDestinationSelected_UsedAs_To_WhenPresent()
    {
        /*
         * RED: Will fail until Builder updates the inference rule to prefer
         * AethernetDestinationSelected for the "to" field.
         *
         * CONTRACT: Given zone 131→130, before.LastAethernetShardInteracted=AetheryteId(125),
         *           before.LastNpcInteracted=NpcId(125) (shard match),
         *           before.AethernetDestinationSelected=AethernetId(77) (explicit selection),
         *           after.LastAethernetShardInteracted=AetheryteId(33) (different from destination),
         *           When Infer is called,
         *           Then Notes contains "77" (from AethernetDestinationSelected, not after.LastShard).
         *           Confidence == High.
         *
         * BUILDER GUIDANCE:
         *   In Rule 4 aethernet sub-case, compute destId as:
         *     before.AethernetDestinationSelected?.Value
         *     ?? after.LastAethernetShardInteracted?.Value.Value
         *   When AethernetDestinationSelected is set, it takes priority over the
         *   after-snapshot's LastAethernetShardInteracted.
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125u),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastAethernetShardInteracted: new AetheryteId(33u),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.NotNull(result.Notes);
        // Notes should reference 77 (AethernetDestinationSelected), not 33 (after.LastShard)
        Assert.Contains("77", result.Notes!, StringComparison.Ordinal);
        Assert.Contains("125", result.Notes!, StringComparison.Ordinal);
    }

    // =========================================================================
    // INF2_2 — Falls back to after.LastAethernetShardInteracted when AethernetDestinationSelected is null
    // =========================================================================

    [Fact]
    public void INF2_2_FallsBackTo_AfterLastAethernetShard_WhenDestinationSelectedNull()
    {
        /*
         * RED: Will fail until Builder implements the fallback logic.
         *
         * CONTRACT: Given zone 131→130, shard conditions met,
         *           before.AethernetDestinationSelected = null,
         *           after.LastAethernetShardInteracted = AetheryteId(33),
         *           When Infer is called,
         *           Then Notes contains "33" (fallback to after.LastShard).
         *           Confidence == High (destination shard known).
         *
         * BUILDER GUIDANCE: When AethernetDestinationSelected is null, fall back to
         * after.LastAethernetShardInteracted?.Value.Value as the destination.
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125u),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            aethernetDestinationSelected: null);
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastAethernetShardInteracted: new AetheryteId(33u),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.NotNull(result.Notes);
        Assert.Contains("33", result.Notes!, StringComparison.Ordinal);
    }

    // =========================================================================
    // INF2_3 — AethernetDestinationSelected provides "from" via before.LastAethernetShardInteracted
    // =========================================================================

    [Fact]
    public void INF2_3_From_ComesFrom_BeforeLastAethernetShard_WhenNpcMatches()
    {
        /*
         * RED: Will fail until Builder implements updated inference.
         *
         * CONTRACT: Given zone 131→130, before.LastAethernetShardInteracted=AetheryteId(125),
         *           before.LastNpcInteracted=NpcId(125) (matches shard),
         *           before.AethernetDestinationSelected=AethernetId(77),
         *           When Infer is called,
         *           Then Notes contains both "125" (from/source shard) and "77" (to/dest),
         *                Confidence == High.
         *
         * BUILDER GUIDANCE: The "from" field corresponds to before.LastAethernetShardInteracted
         * when LastNpcInteracted matches it. Include both in the notes string for
         * StepFactory to read and populate AethernetRouteHint.
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125u),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Contains("125", result.Notes!, StringComparison.Ordinal);
        Assert.Contains("77", result.Notes!, StringComparison.Ordinal);
    }

    // =========================================================================
    // INF2_4 — No destination info (both null) → Medium confidence
    // =========================================================================

    [Fact]
    public void INF2_4_NoDestinationInfo_MediumConfidence()
    {
        /*
         * RED: Will fail until Builder implements updated inference.
         *
         * CONTRACT: Given zone change, shard conditions met (NPC matches shard 125),
         *           before.AethernetDestinationSelected = null,
         *           after.LastAethernetShardInteracted = null,
         *           When Infer is called,
         *           Then Confidence == Medium (no destination shard captured).
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125u),
            lastNpcInteracted: new NpcId(125u),
            lastNpcPosition: new WorldPosition(10, 0, 20),
            aethernetDestinationSelected: null);
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            lastAethernetShardInteracted: null,
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("travel", result.StepType);
        Assert.Equal(Confidence.Medium, result.Confidence);
    }

    // =========================================================================
    // INF2_5 — AethernetDestinationSelected without zone change does not affect Rule 4
    // =========================================================================

    [Fact]
    public void INF2_5_AethernetDestinationSelected_NoZoneChange_Rule4NotFired()
    {
        /*
         * RED: Will fail until Builder implements updated inference.
         *
         * CONTRACT: Given zone stays the same (131→131),
         *           AethernetDestinationSelected is set,
         *           When Infer is called,
         *           Then Rule 4 does not fire (zone change required).
         *           Result is InferenceResult.Empty.
         *
         * BUILDER GUIDANCE: AethernetDestinationSelected does not affect Rule 2.5
         * (attune detection). The destination-selected field is only consumed in Rule 4's
         * aethernet sub-case, which requires a zone change.
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125u),
            lastNpcInteracted: new NpcId(125u),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(131),   // same zone
            aethernetDestinationSelected: new AethernetId(77u),
            capturedAt: T1);

        var result = engine.Infer(before, after);

        // Rule 4 requires zone change; no other rule fires with these inputs
        Assert.NotEqual("travel", result.StepType);
    }

    // =========================================================================
    // INF2_6 — AethernetDestinationSelected + QuestCompleted: Rule 1 wins
    // =========================================================================

    [Fact]
    public void INF2_6_AethernetDestinationSelected_QuestCompleted_Rule1Wins()
    {
        /*
         * CONTRACT: Given zone change + AethernetDestinationSelected set + QuestCompleted,
         *           When Infer is called,
         *           Then StepType == "turn-in" (Rule 1 evaluates before Rule 4).
         */

        var engine = new StepInferenceEngine();
        var before = MakeSnapshot(
            zone: new ZoneId(131),
            lastAethernetShardInteracted: new AetheryteId(125u),
            lastNpcInteracted: new NpcId(125u),
            aethernetDestinationSelected: new AethernetId(77u));
        var after = MakeSnapshot(
            zone: new ZoneId(130),
            questCompleted: true,
            capturedAt: T1);

        var result = engine.Infer(before, after);

        Assert.Equal("turn-in", result.StepType);
        Assert.Equal(InferredFrom.QuestCompleted, result.InferredFrom);
    }
}
