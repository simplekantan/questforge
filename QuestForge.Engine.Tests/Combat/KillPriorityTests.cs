using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: Kill-priority ranking — pure static KillPriority, no async.
/// All tests throw NotImplementedException until Builder implements
/// KillPriority.SelectTarget and KillPriority.Score.
///
/// Spec: docs/COMBAT_STEP_PART_A_PLAN.md §G1
/// </summary>
public sealed class KillPriorityTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static HostileActor MakeActor(
        ulong id, uint dataId, float distance,
        bool isTargetingPlayer = false,
        bool onPlayerEnmityList = false,
        bool hasQuestMarker = false,
        bool isTargetable = true,
        bool isDead = false)
        => new HostileActor(
            new ActorId(id),
            dataId,
            new WorldPosition(0f, 0f, 0f),
            distance,
            isTargetable,
            isDead,
            isTargetingPlayer,
            onPlayerEnmityList,
            hasQuestMarker);

    private static KillTarget? Select(
        IReadOnlyList<HostileActor> actors,
        IReadOnlySet<uint>? killIds = null,
        CombatSpawn spawn = CombatSpawn.AutoOnEnterArea)
        => KillPriority.SelectTarget(actors, killIds ?? new HashSet<uint>(), spawn);

    // =========================================================================
    // KP-happy — aggro (+150) beats quest-marker (+100) and kill-set (+90)
    //            regardless of distance
    // =========================================================================

    [Fact]
    public void KP_Happy_AggroBeatsQuestMarkerAndKillSet_ReturnsAggroTarget()
    {
        /*
         * RED: KillPriority.SelectTarget throws NotImplementedException.
         *
         * Given three hostiles:
         *   A — IsTargetingPlayer:true (+150), dist 20
         *   B — HasQuestMarker:true (+100), dist 5
         *   C — DataId ∈ killIds (+90), dist 2
         * killIds = {C.DataId}, Spawn = autoOnEnterArea
         *
         * Then SelectTarget returns A (score 150 > 100 > 90).
         */
        var a = MakeActor(1, 999, distance: 20, isTargetingPlayer: true);
        var b = MakeActor(2, 888, distance: 5, hasQuestMarker: true);
        var c = MakeActor(3, 100, distance: 2);

        var killIds = new HashSet<uint> { 100u };
        var result = Select([a, b, c], killIds);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
    }

    // =========================================================================
    // KP-enmity-over-marker — enmity (+125) beats quest-marker (+100)
    // =========================================================================

    [Fact]
    public void KP_EnmityOverMarker_OnPlayerEnmityListWins()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given:
         *   B — OnPlayerEnmityList:true (+125), dist 10
         *   C — HasQuestMarker:true (+100), dist 5
         * No IsTargetingPlayer. Then returns B.
         */
        var b = MakeActor(2, 200, distance: 10, onPlayerEnmityList: true);
        var c = MakeActor(3, 300, distance: 5, hasQuestMarker: true);

        var result = Select([b, c]);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(2), result!.Value.Id);
    }

    // =========================================================================
    // KP-distance-tiebreak — equal score → nearest wins
    // =========================================================================

    [Fact]
    public void KP_DistanceTiebreak_NearestWins()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given two actors both with DataId ∈ killIds (score +90 each):
         *   X — dist 10
         *   Y — dist 3
         * Then returns Y (nearest).
         */
        var killId = 100u;
        var x = MakeActor(1, killId, distance: 10);
        var y = MakeActor(2, killId, distance: 3);

        var result = Select([x, y], new HashSet<uint> { killId });

        Assert.NotNull(result);
        Assert.Equal(new ActorId(2), result!.Value.Id);
    }

    // =========================================================================
    // KP-actorid-tiebreak — equal score AND equal distance → lowest ActorId wins
    // =========================================================================

    [Fact]
    public void KP_ActorIdTiebreak_LowestActorIdWins()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given two actors, equal score and equal distance, ActorId 7 and ActorId 4.
         * Then returns ActorId 4 (lowest — final deterministic tie-break).
         */
        var killId = 100u;
        var actorSeven = MakeActor(7, killId, distance: 5);
        var actorFour  = MakeActor(4, killId, distance: 5);

        var result = Select([actorSeven, actorFour], new HashSet<uint> { killId });

        Assert.NotNull(result);
        Assert.Equal(new ActorId(4), result!.Value.Id);
    }

    // =========================================================================
    // KP-filter-dead — dead actors filtered out → null
    // =========================================================================

    [Fact]
    public void KP_FilterDead_OnlyKillSetMatchIsDead_ReturnsNull()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given the only actor whose DataId ∈ killIds has IsDead:true.
         * Then returns null (no eligible target).
         */
        var killId = 100u;
        var dead = MakeActor(1, killId, distance: 5, isDead: true, isTargetable: false);

        var result = Select([dead], new HashSet<uint> { killId });

        Assert.Null(result);
    }

    // =========================================================================
    // KP-filter-untargetable — untargetable actors filtered out → null
    // =========================================================================

    [Fact]
    public void KP_FilterUntargetable_OnlyKillSetMatchIsUntargetable_ReturnsNull()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given the only actor whose DataId ∈ killIds has IsTargetable:false.
         * Then returns null (no eligible target).
         */
        var killId = 100u;
        var untargetable = MakeActor(1, killId, distance: 5, isTargetable: false);

        var result = Select([untargetable], new HashSet<uint> { killId });

        Assert.Null(result);
    }

    // =========================================================================
    // KP-overworld-filters-non-killset — Spawn=OverworldEnemies only kills listed DataIds
    // =========================================================================

    [Fact]
    public void KP_OverworldFiltersNonKillSet_OnlyKillSetActorSelected()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given Spawn=OverworldEnemies, killIds={100}, actors with DataIds {100, 200}.
         * Then only the DataId=100 actor is eligible (returns it).
         * The DataId=200 actor must never be selected.
         */
        var inSet  = MakeActor(1, 100, distance: 5);
        var outSet = MakeActor(2, 200, distance: 1); // closer, but not in kill set

        var result = Select([inSet, outSet], new HashSet<uint> { 100u }, CombatSpawn.OverworldEnemies);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
        Assert.Equal(100u, result!.Value.DataId);
    }

    // =========================================================================
    // KP-auto-empty-killset-all-eligible — AutoOnEnterArea + empty killIds = all hostiles eligible
    // =========================================================================

    [Fact]
    public void KP_AutoEmptyKillSet_AllEligible_ReturnsHigherPriorityActor()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given Spawn=AutoOnEnterArea, killIds={} (empty), two targetable hostiles.
         * All hostiles are eligible ("clear the room" case).
         * Returns the one with IsTargetingPlayer:true (higher priority).
         */
        var aggro  = MakeActor(1, 999, distance: 15, isTargetingPlayer: true);
        var random = MakeActor(2, 888, distance: 3);  // closer but no aggro

        var result = Select([aggro, random], new HashSet<uint>(), CombatSpawn.AutoOnEnterArea);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
    }

    // =========================================================================
    // KP-empty — no actors → null
    // =========================================================================

    [Fact]
    public void KP_Empty_NoActors_ReturnsNull()
    {
        /*
         * RED: throws NotImplementedException.
         *
         * Given an empty actor list. Then returns null.
         */
        var result = Select([], new HashSet<uint> { 100u });
        Assert.Null(result);
    }

    // =========================================================================
    // Score method — verify exact weight values
    // =========================================================================

    [Fact]
    public void Score_AggroTarget_Returns150()
    {
        /*
         * RED: KillPriority.Score throws NotImplementedException.
         * Pins the weight for IsTargetingPlayer.
         */
        var actor = MakeActor(1, 999, distance: 5, isTargetingPlayer: true);
        var score = KillPriority.Score(actor, new HashSet<uint>());
        Assert.Equal(150, score);
    }

    [Fact]
    public void Score_EnmityOnly_Returns125()
    {
        /*
         * RED: throws NotImplementedException.
         * Pins the weight for OnPlayerEnmityList without aggro.
         */
        var actor = MakeActor(1, 999, distance: 5, onPlayerEnmityList: true);
        var score = KillPriority.Score(actor, new HashSet<uint>());
        Assert.Equal(125, score);
    }

    [Fact]
    public void Score_QuestMarkerOnly_Returns100()
    {
        /*
         * RED: throws NotImplementedException.
         * Pins the weight for HasQuestMarker without other flags.
         */
        var actor = MakeActor(1, 999, distance: 5, hasQuestMarker: true);
        var score = KillPriority.Score(actor, new HashSet<uint>());
        Assert.Equal(100, score);
    }

    [Fact]
    public void Score_KillSetOnly_Returns90()
    {
        /*
         * RED: throws NotImplementedException.
         * Pins the weight for DataId ∈ killIds with no other flags.
         */
        var actor = MakeActor(1, 100, distance: 5);
        var score = KillPriority.Score(actor, new HashSet<uint> { 100u });
        Assert.Equal(90, score);
    }

    [Fact]
    public void Score_NoFlags_Returns0()
    {
        /*
         * RED: throws NotImplementedException.
         * Actor with no matching flags, not in kill set → score 0.
         */
        var actor = MakeActor(1, 999, distance: 5);
        var score = KillPriority.Score(actor, new HashSet<uint> { 100u }); // 999 not in set
        Assert.Equal(0, score);
    }
}
