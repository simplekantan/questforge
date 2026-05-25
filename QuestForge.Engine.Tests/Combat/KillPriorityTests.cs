using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE (rewritten for inverted priority model, feat/combat-step-b2):
/// Kill-priority ranking — pure static KillPriority, no async.
///
/// NEW model (builder must implement):
///   DataId ∈ killIds  +1000  (quest enemy — dominant)
///   HasQuestMarker    +100
///   IsTargetingPlayer +10    (aggro add — secondary)
///   OnPlayerEnmityList +5
///
/// Overworld eligibility: kill-set OR IsTargetingPlayer OR OnPlayerEnmityList
///   (pure allies — none of the three — are excluded in OverworldEnemies mode).
///
/// Tie-breaks: higher score → nearest (DistanceToPlayer asc) → lowest ActorId.Value.
///
/// Tests FAIL against the current (old-weights) KillPriority because it assigns
/// IsTargetingPlayer=+150, OnPlayerEnmityList=+125, HasQuestMarker=+100,
/// killSet=+90 and filters Overworld to kill-set only.
///
/// Spec: docs/COMBAT_STEP_PART_B_PLAN.md §New Priority Model
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
    // CORE NEW TEST: Quest-enemy-over-add
    //
    // The headline behavior: a kill-set quest enemy (≥1000) always outranks
    // an aggro'd add (≤15), even when the add is IsTargetingPlayer.
    // OLD model expected the aggro'd actor (B) to win — now A wins.
    // =========================================================================

    [Fact]
    public void KP_QuestEnemyOverAdd_KillSetActorSelectedOverAggroAdd()
    {
        /*
         * Headline test: kill-set quest enemy beats aggro'd add.
         *
         * SelectTarget outcome is the same under old and new models (A wins either way)
         * because old code filters B out entirely, new code admits B but A outscores it.
         * The RED signal comes from pinning exact scores: old Score(A)=90, new=1000;
         * old Score(B)=150 (IsTargetingPlayer), new=10.
         *
         * RED: Score(A) will return 90 (old) not 1000, and Score(B) will return 150 not 10.
         *
         * Given OverworldEnemies, killIds={100}:
         *   A — DataId 100 (kill-set), no aggro, dist 10  → new score 1000
         *   B — DataId 200 (NOT kill-set), IsTargetingPlayer:true, dist 5 → new score 10
         *
         * Then SelectTarget returns A, and exact scores pin the new weight model.
         */
        var a = MakeActor(1, 100, distance: 10);                          // kill-set enemy
        var b = MakeActor(2, 200, distance: 5, isTargetingPlayer: true);  // aggro add, closer

        var killIds = new HashSet<uint> { 100u };
        var result = Select([a, b], killIds, CombatSpawn.OverworldEnemies);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
        Assert.Equal(100u, result!.Value.DataId);

        // Pin exact new scores — these fail against the old implementation
        Assert.Equal(1000, KillPriority.Score(a, killIds));  // old returns 90
        Assert.Equal(10,   KillPriority.Score(b, killIds));  // old returns 150
    }

    // =========================================================================
    // Add-eligible-and-chosen-when-no-quest-enemy
    //
    // After the quest enemy is down, aggro'd adds become the top eligible target.
    // =========================================================================

    [Fact]
    public void KP_AddEligible_WhenNoQuestEnemy_AggroAddSelected()
    {
        /*
         * RED: old Overworld filter drops DataId 200 (not in kill-set) entirely,
         *      returning null instead of B. Under new model B is eligible via aggro.
         *
         * Given OverworldEnemies, killIds={100} (quest enemy absent from scene),
         *   B — DataId 200 (NOT kill-set), IsTargetingPlayer:true, dist 5.
         *
         * Then SelectTarget returns B (the only eligible actor).
         * Pins that adds are cleared after the quest enemy is gone.
         */
        var b = MakeActor(2, 200, distance: 5, isTargetingPlayer: true);

        var result = Select([b], new HashSet<uint> { 100u }, CombatSpawn.OverworldEnemies);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(2), result!.Value.Id);
    }

    // =========================================================================
    // Overworld-excludes-pure-ally
    //
    // An actor that is NOT in the kill-set, NOT aggro'd, NOT on enmity list
    // must never be eligible under OverworldEnemies.
    // =========================================================================

    [Fact]
    public void KP_OverworldExcludesPureAlly_NonKillSetNonAggroActor_ReturnsNull()
    {
        /*
         * RED: old model filtered Overworld to kill-set only (DataId 999 not in set → null).
         *      New model also returns null for a pure ally, but the eligibility predicate is
         *      different: excluded because NOT (killSet OR IsTargetingPlayer OR OnPlayerEnmityList).
         *      This test pins both the null result AND the new eligibility rule.
         *
         * Given OverworldEnemies, killIds={100}:
         *   C — DataId 999, no aggro, no enmity, no quest marker, dist 1 (very close, irrelevant).
         *
         * Then SelectTarget returns null (pure ally — excluded by all three eligibility conditions).
         */
        var c = MakeActor(3, 999, distance: 1);  // pure ally — should never be targeted

        var result = Select([c], new HashSet<uint> { 100u }, CombatSpawn.OverworldEnemies);

        Assert.Null(result);
    }

    [Fact]
    public void KP_OverworldFiltersNonKillSet_OnlyKillSetActorSelected_WhenNoAggro()
    {
        /*
         * Updated form of original KP-overworld-filters-non-killset.
         *
         * Given OverworldEnemies, killIds={100}:
         *   inSet  — DataId 100, dist 5   (kill-set, eligible)
         *   outSet — DataId 200, dist 1   (NOT kill-set, NOT aggro, NOT enmity → excluded)
         *
         * Then only inSet is eligible → returns inSet even though outSet is closer.
         *
         * RED: current model returns inSet for the right reason (kill-set filter only),
         *      but this test is kept as a regression pin for the new eligibility rule.
         *      The test PASSES with both old and new models when outSet has no aggro,
         *      which means it is NOT a RED test by itself — it is a stability pin.
         *
         * NOTE: this particular scenario happens to pass with both old and new rules.
         *       It is kept here as a behavioural anchor. See KP_QuestEnemyOverAdd for the
         *       test that flips (old→new).
         */
        var inSet  = MakeActor(1, 100, distance: 5);
        var outSet = MakeActor(2, 200, distance: 1); // closer, no aggro, not in kill-set

        var result = Select([inSet, outSet], new HashSet<uint> { 100u }, CombatSpawn.OverworldEnemies);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
        Assert.Equal(100u, result!.Value.DataId);
    }

    // =========================================================================
    // Aggro tiebreak among quest enemies
    //
    // When two kill-set actors are present, the one also IsTargetingPlayer
    // wins (+1000 + 10 = 1010 vs +1000).
    // =========================================================================

    [Fact]
    public void KP_AggroTiebreakAmongQuestEnemies_AggroKillSetActorWins()
    {
        /*
         * RED: old model: both at +90 (killSet); IsTargetingPlayer gives +150 more →
         *      aggro one wins at 240. That is the SAME outcome as new (aggro wins),
         *      so the winner is unchanged — but the absolute scores differ.
         *      This test pins the new scores precisely via Score assertions below,
         *      and the SelectTarget result (aggro'd kills-set actor) remains the same.
         *
         * Given AutoOnEnterArea, killIds={100}:
         *   A — DataId 100, IsTargetingPlayer:false, dist 5  (score 1000)
         *   B — DataId 100, IsTargetingPlayer:true,  dist 8  (score 1010)
         *
         * Then SelectTarget returns B (1010 > 1000).
         */
        var a = MakeActor(1, 100, distance: 5, isTargetingPlayer: false);
        var b = MakeActor(2, 100, distance: 8, isTargetingPlayer: true);

        var result = Select([a, b], new HashSet<uint> { 100u });

        Assert.NotNull(result);
        Assert.Equal(new ActorId(2), result!.Value.Id);

        // Pin exact new scores
        var ids = new HashSet<uint> { 100u };
        Assert.Equal(1000, KillPriority.Score(a, ids));
        Assert.Equal(1010, KillPriority.Score(b, ids));
    }

    // =========================================================================
    // Kill-set over quest-marker
    //
    // Kill-set (1000) beats a marker-only mob (100).
    // OLD model: killSet=90 < marker=100, so marker won. NEW: killSet dominates.
    // =========================================================================

    [Fact]
    public void KP_KillSetOverQuestMarker_KillSetActorWins()
    {
        /*
         * RED: old model: marker(100) > killSet(90) → marker actor selected.
         *      New model: killSet(1000) > marker(100) → kill-set actor selected.
         *
         * Given AutoOnEnterArea, killIds={100}:
         *   A — DataId 100 (kill-set), no marker, dist 10  (score 1000)
         *   B — DataId 200, HasQuestMarker:true,   dist 5  (score 100)
         *
         * Then SelectTarget returns A (1000 > 100).
         */
        var a = MakeActor(1, 100, distance: 10);
        var b = MakeActor(2, 200, distance: 5, hasQuestMarker: true);

        var result = Select([a, b], new HashSet<uint> { 100u });

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
    }

    // =========================================================================
    // Marker over add (AutoOnEnterArea, empty kill-set)
    //
    // When there are no kill-set entries, HasQuestMarker(100) > IsTargetingPlayer(10).
    // OLD model: enmity(125) > marker(100). NEW: marker(100) > IsTargetingPlayer(10).
    // =========================================================================

    [Fact]
    public void KP_MarkerOverAdd_AutoEmptyKillSet_MarkerWins()
    {
        /*
         * RED: old model KP_EnmityOverMarker expected enmity(125) to beat marker(100).
         *      New model: IsTargetingPlayer(10) < marker(100) → marker wins.
         *      Also, old KP_Happy expected aggro(150) to beat marker(100) → now marker wins
         *      when there is no kill-set entry.
         *
         * Given AutoOnEnterArea, killIds={} (empty):
         *   B — HasQuestMarker:true,        dist 10  (score 100)
         *   C — IsTargetingPlayer:true,     dist 5   (score 10)
         *
         * Then SelectTarget returns B (100 > 10).
         */
        var b = MakeActor(2, 888, distance: 10, hasQuestMarker: true);
        var c = MakeActor(3, 777, distance: 5,  isTargetingPlayer: true);

        var result = Select([b, c], new HashSet<uint>());

        Assert.NotNull(result);
        Assert.Equal(new ActorId(2), result!.Value.Id);
    }

    [Fact]
    public void KP_MarkerOverEnmity_AutoEmptyKillSet_MarkerWins()
    {
        /*
         * RED: old KP_EnmityOverMarker test expected OnPlayerEnmityList(125) to beat
         *      HasQuestMarker(100). New model: enmity = +5, marker = +100 → marker wins.
         *
         * Given AutoOnEnterArea, killIds={} (empty):
         *   B — OnPlayerEnmityList:true, dist 10  (score 5)
         *   C — HasQuestMarker:true,     dist 5   (score 100)
         *
         * Then SelectTarget returns C (100 > 5).
         */
        var b = MakeActor(2, 200, distance: 10, onPlayerEnmityList: true);
        var c = MakeActor(3, 300, distance: 5,  hasQuestMarker: true);

        var result = Select([b, c], new HashSet<uint>());

        Assert.NotNull(result);
        Assert.Equal(new ActorId(3), result!.Value.Id);
    }

    // =========================================================================
    // KP-distance-tiebreak — equal score → nearest wins (unchanged semantics)
    // =========================================================================

    [Fact]
    public void KP_DistanceTiebreak_NearestWins()
    {
        /*
         * Given two actors both DataId ∈ killIds (score +1000 each), equal non-aggro flags:
         *   X — dist 10
         *   Y — dist 3
         * Then returns Y (nearest).
         *
         * Stable test: tie-break semantics unchanged, only absolute scores changed.
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
         * Given two actors, equal score and equal distance, ActorId 7 and ActorId 4.
         * Then returns ActorId 4 (lowest — final deterministic tie-break).
         *
         * Stable test: tie-break semantics unchanged.
         */
        var killId = 100u;
        var actorSeven = MakeActor(7, killId, distance: 5);
        var actorFour  = MakeActor(4, killId, distance: 5);

        var result = Select([actorSeven, actorFour], new HashSet<uint> { killId });

        Assert.NotNull(result);
        Assert.Equal(new ActorId(4), result!.Value.Id);
    }

    // =========================================================================
    // KP-filter-dead — dead actors filtered out → null (unchanged)
    // =========================================================================

    [Fact]
    public void KP_FilterDead_OnlyKillSetMatchIsDead_ReturnsNull()
    {
        /*
         * Given the only actor whose DataId ∈ killIds has IsDead:true.
         * Then returns null (no eligible target).
         */
        var killId = 100u;
        var dead = MakeActor(1, killId, distance: 5, isDead: true, isTargetable: false);

        var result = Select([dead], new HashSet<uint> { killId });

        Assert.Null(result);
    }

    // =========================================================================
    // KP-filter-untargetable — untargetable actors filtered out → null (unchanged)
    // =========================================================================

    [Fact]
    public void KP_FilterUntargetable_OnlyKillSetMatchIsUntargetable_ReturnsNull()
    {
        /*
         * Given the only actor whose DataId ∈ killIds has IsTargetable:false.
         * Then returns null (no eligible target).
         */
        var killId = 100u;
        var untargetable = MakeActor(1, killId, distance: 5, isTargetable: false);

        var result = Select([untargetable], new HashSet<uint> { killId });

        Assert.Null(result);
    }

    // =========================================================================
    // KP-auto-empty-killset — AutoOnEnterArea + empty killIds = all hostiles eligible
    //
    // Highest-score actor wins; plain (score-0) mob is eligible but lowest.
    // =========================================================================

    [Fact]
    public void KP_AutoEmptyKillSet_AllEligible_MarkerBeatsPlainActor()
    {
        /*
         * RED: old model: IsTargetingPlayer(+150) beats a plain actor (0).
         *      New model: HasQuestMarker(+100) beats a plain actor (0) too — same winner
         *      class, but this test uses a marker actor to pin the new weight.
         *
         * Given AutoOnEnterArea, killIds={} (empty), two actors:
         *   marked — HasQuestMarker:true, dist 15  (score 100)
         *   plain  — no flags,            dist 3   (score 0, closer but lower priority)
         *
         * Then returns marked (100 > 0).
         */
        var marked = MakeActor(1, 999, distance: 15, hasQuestMarker: true);
        var plain  = MakeActor(2, 888, distance: 3);

        var result = Select([marked, plain], new HashSet<uint>(), CombatSpawn.AutoOnEnterArea);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
    }

    [Fact]
    public void KP_AutoEmptyKillSet_PlainActorEligible_SelectedWhenOnlyCandidate()
    {
        /*
         * Pins that a plain (score-0) actor IS eligible under AutoOnEnterArea with
         * empty kill-set — it just has the lowest possible priority.
         *
         * Given AutoOnEnterArea, killIds={}, one plain actor. → selected (only option).
         */
        var plain = MakeActor(1, 888, distance: 5);

        var result = Select([plain], new HashSet<uint>(), CombatSpawn.AutoOnEnterArea);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
    }

    // =========================================================================
    // KP-empty — no actors → null (unchanged)
    // =========================================================================

    [Fact]
    public void KP_Empty_NoActors_ReturnsNull()
    {
        /*
         * Given an empty actor list. Then returns null.
         */
        var result = Select([], new HashSet<uint> { 100u });
        Assert.Null(result);
    }

    // =========================================================================
    // Score method — verify EXACT NEW weight values
    //
    // These directly fail against the old implementation (wrong numeric values).
    // =========================================================================

    [Fact]
    public void Score_KillSetOnly_Returns1000()
    {
        /*
         * RED: old implementation returns 90 for a kill-set match.
         *      New model: DataId ∈ killIds → +1000.
         */
        var actor = MakeActor(1, 100, distance: 5);
        var score = KillPriority.Score(actor, new HashSet<uint> { 100u });
        Assert.Equal(1000, score);
    }

    [Fact]
    public void Score_QuestMarkerOnly_Returns100()
    {
        /*
         * Stable: HasQuestMarker remains +100 in both old and new models.
         * Kept as a regression pin.
         */
        var actor = MakeActor(1, 999, distance: 5, hasQuestMarker: true);
        var score = KillPriority.Score(actor, new HashSet<uint>());
        Assert.Equal(100, score);
    }

    [Fact]
    public void Score_AggroTarget_Returns10()
    {
        /*
         * RED: old implementation returns 150 for IsTargetingPlayer.
         *      New model: IsTargetingPlayer → +10.
         */
        var actor = MakeActor(1, 999, distance: 5, isTargetingPlayer: true);
        var score = KillPriority.Score(actor, new HashSet<uint>());
        Assert.Equal(10, score);
    }

    [Fact]
    public void Score_EnmityOnly_Returns5()
    {
        /*
         * RED: old implementation returns 125 for OnPlayerEnmityList.
         *      New model: OnPlayerEnmityList → +5.
         */
        var actor = MakeActor(1, 999, distance: 5, onPlayerEnmityList: true);
        var score = KillPriority.Score(actor, new HashSet<uint>());
        Assert.Equal(5, score);
    }

    [Fact]
    public void Score_AllFlags_ReturnsSumOfAllWeights()
    {
        /*
         * RED: old total = 150+125+100+90 = 465. New total = 1000+100+10+5 = 1115.
         *
         * Actor: DataId ∈ killIds, HasQuestMarker, IsTargetingPlayer, OnPlayerEnmityList.
         * Expected new score = 1000 + 100 + 10 + 5 = 1115.
         */
        var actor = MakeActor(1, 100, distance: 5,
            isTargetingPlayer: true, onPlayerEnmityList: true, hasQuestMarker: true);
        var score = KillPriority.Score(actor, new HashSet<uint> { 100u });
        Assert.Equal(1115, score);
    }

    [Fact]
    public void Score_NoFlags_Returns0()
    {
        /*
         * Actor with no matching flags, not in kill set → score 0. (Unchanged.)
         */
        var actor = MakeActor(1, 999, distance: 5);
        var score = KillPriority.Score(actor, new HashSet<uint> { 100u }); // 999 not in set
        Assert.Equal(0, score);
    }

    // =========================================================================
    // Overworld eligibility — enmity-listed add also eligible (not just kill-set)
    // =========================================================================

    [Fact]
    public void KP_OverworldEnmityAdd_Eligible_WhenNoKillSetPresent()
    {
        /*
         * RED: old model drops DataId 200 (not kill-set) under OverworldEnemies → null.
         *      New model: OnPlayerEnmityList qualifies DataId 200 for eligibility → selected.
         *
         * Given OverworldEnemies, killIds={100} (quest enemy absent):
         *   D — DataId 200, OnPlayerEnmityList:true, dist 5.
         *
         * Then SelectTarget returns D.
         */
        var d = MakeActor(4, 200, distance: 5, onPlayerEnmityList: true);

        var result = Select([d], new HashSet<uint> { 100u }, CombatSpawn.OverworldEnemies);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(4), result!.Value.Id);
    }
}
