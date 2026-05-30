using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Combat;
using QuestForge.Engine.Tests.Engine; // ManualTimeProvider
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: Combat clear-aggro (mop-up) — CA-1..CA-9 from
/// docs/COMBAT_CLEAR_AGGRO_PLAN.md §GWT + KillPriority.SelectAttacker unit tests.
///
/// Fail modes expected:
///   COMPILE FAIL  — KillPriority.SelectAttacker and CombatController.DecideClearAggro
///                   do not exist yet. Tests in the KillPriority and CombatController
///                   sections fail at compile time.
///   ASSERTION FAIL — Engine-level tests (CA-2..CA-9) compile against the existing API
///                    but fail on assertion because the engine currently confirms +
///                    advances immediately when Expect is satisfied, ignoring combat state.
///   PASS (regression guard) — CA-1: out-of-combat advance is current behavior, so
///                    it already passes. Kept as a stability pin.
/// </summary>
public sealed class CombatClearAggroTests
{
    // =========================================================================
    // Stable epoch — shared T0 for all clock-dependent tests.
    // =========================================================================

    private static readonly DateTimeOffset T0 =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatAction(EngineAction a) => a.GetType().Name;

    private static string FormatReads(FakeGameStateProvider gs)
        => string.Join(", ", gs.RecordedReads.Snapshot().Select(r => r.Method));

    private static string FormatTargets(FakeCombat combat)
        => string.Join(", ", combat.RecordedTargets.Snapshot()
            .Select(t => t.IsClear ? "ClearTarget" : $"SetTarget({t.Target!.Value.Value})"));

    /// <summary>
    /// Creates a one-sequence quest containing a single CombatStep.
    /// The expect predicate is "questVariable({questId}, 0) >= 1" — false when var[0]=0, true when var[0]>=1.
    /// </summary>
    private static QuestDefinition BuildCombatQuest(
        uint questId,
        CombatStep step)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "ClearAggro Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = [step] }
            ]
        };

    /// <summary>
    /// Two-sequence quest: seq-0 = combat step, seq-1 = a talk step.
    /// </summary>
    private static QuestDefinition BuildTwoSequenceCombatQuest(
        uint questId,
        CombatStep combatStep,
        Step seqOneStep)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "ClearAggro Two-Sequence Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = [combatStep] },
                new QuestSequence { Sequence = 1, Steps = [seqOneStep] }
            ]
        };

    /// <summary>
    /// A CombatStep with Expect "questVariable({questId}, 0) >= 1" and the given kill-set / spawn.
    /// </summary>
    private static CombatStep MakeCombatStep(
        uint questId,
        uint[]? killDataIds = null,
        CombatSpawn spawn = CombatSpawn.OverworldEnemies)
        => new CombatStep
        {
            Id = "ca-combat",
            KillEnemyDataIds = killDataIds ?? [100u],
            Spawn = spawn,
            Expect = new PredicateExpect { Predicate = $"questVariable({questId}, 0) >= 1" }
        };

    /// <summary>
    /// Makes a hostile actor. Defaults: alive, targetable, not attacking, not on enmity list.
    /// </summary>
    private static HostileActor MakeHostile(
        ulong id,
        uint dataId,
        float distance,
        bool isTargetingPlayer = false,
        bool onEnmityList = false)
        => new HostileActor(
            new ActorId(id),
            dataId,
            new WorldPosition(0f, 0f, 0f),
            distance,
            IsTargetable: true,
            IsDead: false,
            IsTargetingPlayer: isTargetingPlayer,
            OnPlayerEnmityList: onEnmityList,
            HasQuestMarker: false);

    /// <summary>
    /// Constructs a QuestEngine with all fakes and a controllable clock.
    /// Returns the engine plus the raw fakes for scripting/assertion.
    /// </summary>
    private static (
        QuestEngine engine,
        FakeGameStateProvider gameState,
        FakeQuestState questState,
        FakeCombat combat,
        FakeNavigator navigator)
        BuildEngineWithClock(ManualTimeProvider clock)
    {
        var gameState   = new FakeGameStateProvider();
        var questState  = new FakeQuestState();
        var navigator   = new FakeNavigator(gameState);
        var teleporter  = new FakeTeleporter(gameState);
        var interactor  = new FakeInteractor(gameState, questState);
        var combat      = new FakeCombat();
        var minigames   = new FakeMinigameSkipper();
        var dialogue    = new FakeDialogueResolver();
        var timing      = new FakeTimingProfile();
        var trace       = new NullTraceWriter();
        var logger      = NullLogger<QuestEngine>.Instance;

        var engine = new QuestEngine(
            gameState, questState, navigator, teleporter, interactor,
            combat, minigames, dialogue, timing, trace, logger,
            clock);

        return (engine, gameState, questState, combat, navigator);
    }

    // =========================================================================
    // KillPriority.SelectAttacker — pure unit tests
    // (COMPILE FAIL until Builder adds KillPriority.SelectAttacker)
    // =========================================================================

    [Fact]
    public void SA_AttackerEligible_IsTargetingPlayer_Selected()
    {
        /*
         * Given one actor that IsTargetingPlayer=true, alive, targetable.
         * When SelectAttacker.
         * Then returns that actor.
         *
         * COMPILE FAIL: KillPriority.SelectAttacker does not exist yet.
         */
        var attacker = MakeHostile(1, 200, distance: 10, isTargetingPlayer: true);

        // RED — compile error: SelectAttacker does not exist
        var result = KillPriority.SelectAttacker([attacker]);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
    }

    [Fact]
    public void SA_AttackerEligible_OnEnmityList_Selected()
    {
        /*
         * Given one actor that OnPlayerEnmityList=true (but not IsTargetingPlayer), alive, targetable.
         * When SelectAttacker.
         * Then returns that actor.
         *
         * COMPILE FAIL: KillPriority.SelectAttacker does not exist yet.
         */
        var enmityActor = MakeHostile(2, 200, distance: 10, onEnmityList: true);

        // RED — compile error
        var result = KillPriority.SelectAttacker([enmityActor]);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(2), result!.Value.Id);
    }

    [Fact]
    public void SA_NonAttackingKillSetMob_NotSelected()
    {
        /*
         * Given one actor in no kill-set (DataId 100 could be in a kill-set),
         * IsTargetingPlayer=false, OnEnmityList=false — a non-attacking mob.
         * When SelectAttacker.
         * Then returns null (not an attacker — ineligible regardless of DataId).
         *
         * This pins the key invariant: SelectAttacker ignores kill-set membership.
         *
         * COMPILE FAIL: KillPriority.SelectAttacker does not exist yet.
         */
        var nonAttacker = MakeHostile(3, 100, distance: 3,
            isTargetingPlayer: false, onEnmityList: false);

        // RED — compile error
        var result = KillPriority.SelectAttacker([nonAttacker]);

        Assert.Null(result);
    }

    [Fact]
    public void SA_DeadActorExcluded()
    {
        /*
         * Given one actor that is an attacker (IsTargetingPlayer=true) but IsDead=true.
         * When SelectAttacker.
         * Then returns null (dead actors excluded).
         *
         * COMPILE FAIL: KillPriority.SelectAttacker does not exist yet.
         */
        var deadAttacker = new HostileActor(
            new ActorId(4), 200, new WorldPosition(0f, 0f, 0f), DistanceToPlayer: 5,
            IsTargetable: false, IsDead: true,
            IsTargetingPlayer: true, OnPlayerEnmityList: false, HasQuestMarker: false);

        // RED — compile error
        var result = KillPriority.SelectAttacker([deadAttacker]);

        Assert.Null(result);
    }

    [Fact]
    public void SA_TargetingPlayerScoresHigherThanEnmityOnly_NearestTiebreak()
    {
        /*
         * Given two attackers at the same distance:
         *   A — IsTargetingPlayer=true, OnEnmityList=false → score +10
         *   B — IsTargetingPlayer=false, OnEnmityList=true → score +5
         * When SelectAttacker.
         * Then returns A (higher score wins, then nearest, then lowest ActorId).
         *
         * COMPILE FAIL: KillPriority.SelectAttacker does not exist yet.
         */
        var a = MakeHostile(1, 999, distance: 10, isTargetingPlayer: true, onEnmityList: false);
        var b = MakeHostile(2, 888, distance: 5,  isTargetingPlayer: false, onEnmityList: true);

        // RED — compile error
        var result = KillPriority.SelectAttacker([a, b]);

        // A has score 10, B has score 5 → A wins despite B being closer
        Assert.NotNull(result);
        Assert.Equal(new ActorId(1), result!.Value.Id);
    }

    [Fact]
    public void SA_TiebreakerNearest_WhenSameScore()
    {
        /*
         * Given two attackers both IsTargetingPlayer=true (score +10 each):
         *   A — dist 20
         *   B — dist 5
         * When SelectAttacker.
         * Then returns B (same score → nearest wins).
         *
         * COMPILE FAIL: KillPriority.SelectAttacker does not exist yet.
         */
        var a = MakeHostile(1, 999, distance: 20, isTargetingPlayer: true);
        var b = MakeHostile(2, 888, distance: 5,  isTargetingPlayer: true);

        // RED — compile error
        var result = KillPriority.SelectAttacker([a, b]);

        Assert.NotNull(result);
        Assert.Equal(new ActorId(2), result!.Value.Id);
    }

    // =========================================================================
    // CA-1 (regression guard / happy path)
    // Objective met + already out of combat → step confirms, engine advances.
    // Expected: PASS with current engine (out-of-combat is existing behavior today).
    // After the feature lands, this also pins that IsPlayerInCombat was read exactly once.
    // =========================================================================

    [Fact]
    public async Task CA_1_ObjectiveMet_OutOfCombat_ConfirmsAndAdvances()
    {
        /*
         * Given a one-combat-step quest, Expect satisfied (var[0]=1), IsPlayerInCombat=false.
         * When Tick.
         * Then action is NOT Engage (step confirms → Wait "all steps satisfied"),
         *      AND FakeCombat.RecordedTargets contains a ClearTarget (from ResetAsync).
         *
         * REGRESSION GUARD: current engine confirms immediately when Expect is true,
         * so this PASSES today. After the feature, the new IsPlayerInCombat read must not
         * break this path (out-of-combat → still confirms). The IsPlayerInCombat-read
         * assertion only passes after the feature is built.
         */
        // Given
        var harness = new EngineTestHarness();
        const uint questId = 70001u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        harness.GameState.SetInCombat(false);

        var step = MakeCombatStep(questId);
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca1");

        // When
        harness.GameState.Reset(); // clear setup reads
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then — must NOT be Engage
        Assert.IsNotType<EngineAction.Engage>(action);

        // ClearTarget from ResetAsync on confirm
        var targets = harness.Combat.RecordedTargets.Snapshot();
        Assert.True(targets.Any(t => t.IsClear),
            $"Expected ClearTarget on confirm. Targets: {FormatTargets(harness.Combat)}");

        // After feature: IsPlayerInCombat was read exactly once on the Expect-satisfied tick.
        // This assertion fails until the feature is implemented — that is expected RED behavior.
        var reads = harness.GameState.RecordedReads.Snapshot();
        var inCombatReads = reads.Count(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat));
        Assert.Equal(1, inCombatReads);
    }

    // =========================================================================
    // CA-2 (core behavior)
    // Objective met + in combat → mop-up (Engage); then out-of-combat → confirms.
    // ASSERTION FAIL: current engine confirms immediately on Expect-satisfied tick.
    // =========================================================================

    [Fact]
    public async Task CA_2_ObjectiveMet_InCombat_MopsUp_ThenConfirms()
    {
        /*
         * Given one combat step, Expect satisfied, SetInCombat(true),
         *       attacker (ActorId=9, DataId=999 not in kill-set, dist=5, IsTargetingPlayer=true).
         * When tick 1.
         * Then action is Engage with Target.Id == ActorId(9) — mop-up the attacker.
         *      IsPlayerInCombat was read this tick.
         *
         * When SetInCombat(false), tick 2.
         * Then action is NOT Engage (confirms).
         *      ClearTarget is recorded.
         *
         * ASSERTION FAIL: current engine returns Wait("all steps satisfied") on tick 1
         * (no mop-up), making tick 1 wrong and the tick-2 check irrelevant.
         */
        // Given
        var harness = new EngineTestHarness();
        const uint questId = 70002u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(9, 999, distance: 5, isTargetingPlayer: true)); // attacker, not kill-set

        var step = MakeCombatStep(questId, killDataIds: [100u]); // kill-set={100}, attacker is DataId=999
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca2");
        harness.GameState.Reset();

        // Tick 1 — Expect true, in combat → should mop-up
        var tick1 = await harness.Engine.Tick(CancellationToken.None);

        // RED assertion: engine currently does NOT mop up — it confirms immediately.
        // This Assert.IsType will fail until the feature is built.
        var engage = Assert.IsType<EngineAction.Engage>(tick1);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(9), engage.Target!.Value.Id);

        // IsPlayerInCombat was read on tick 1
        var reads1 = harness.GameState.RecordedReads.Snapshot();
        Assert.True(reads1.Any(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat)),
            $"IsPlayerInCombat must be read on Expect-satisfied tick. Reads: {FormatReads(harness.GameState)}");

        // Tick 2 — now out of combat → confirms
        harness.GameState.SetInCombat(false);
        harness.GameState.Reset();
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsNotType<EngineAction.Engage>(tick2);
        var targets = harness.Combat.RecordedTargets.Snapshot();
        Assert.True(targets.Any(t => t.IsClear),
            $"Expected ClearTarget on confirm. Targets: {FormatTargets(harness.Combat)}");
    }

    // =========================================================================
    // CA-3 (CRITICAL — attackers-only)
    // Mop-up selects the ATTACKER, NOT the non-attacking kill-set respawn.
    // Tests both spawn types as Theory rows.
    // COMPILE FAIL (KillPriority.SelectAttacker + CombatController.DecideClearAggro missing)
    // AND ASSERTION FAIL (engine mop-up not implemented).
    // =========================================================================

    [Theory]
    [InlineData(CombatSpawn.AutoOnEnterArea)]
    [InlineData(CombatSpawn.OverworldEnemies)]
    public async Task CA_3_MopUp_SelectsAttacker_NotNonAttackingKillSetRespawn(CombatSpawn spawn)
    {
        /*
         * Given one combat step with KillEnemyDataIds=[100], Spawn=<spawn>, Expect satisfied,
         *       SetInCombat(true). Two hostiles:
         *   A — ActorId=1, DataId=100 (kill-set), dist=3, IsTargetingPlayer=false → non-attacking respawn
         *   B — ActorId=2, DataId=777 (not kill-set), dist=20, IsTargetingPlayer=true → attacker
         * When tick.
         * Then Engage with Target.Id == ActorId(2) (attacker B), NOT ActorId(1).
         *
         * Under AutoOnEnterArea: old SelectTarget would pick A (DataId 100 in kill-set → +1000 score).
         * Under OverworldEnemies: old SelectTarget picks A (kill-set), skipping B if kill-set has members.
         * Both spawn types must select B (the attacker) in mop-up — SelectAttacker is spawn-type-agnostic.
         *
         * COMPILE FAIL: SelectAttacker / DecideClearAggro do not exist.
         * ASSERTION FAIL (if compile somehow succeeds): engine uses Decide, not DecideClearAggro.
         */
        // Given
        var harness = new EngineTestHarness();
        const uint questId = 70003u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        harness.GameState.SetInCombat(true);

        // A: non-attacking kill-set respawn, closer
        harness.GameState.AddHostileActor(
            MakeHostile(1, 100, distance: 3, isTargetingPlayer: false, onEnmityList: false));
        // B: attacker, farther
        harness.GameState.AddHostileActor(
            MakeHostile(2, 777, distance: 20, isTargetingPlayer: true, onEnmityList: false));

        var step = new CombatStep
        {
            Id = "ca-combat",
            KillEnemyDataIds = [100u],
            Spawn = spawn,
            Expect = new PredicateExpect { Predicate = $"questVariable({questId}, 0) >= 1" }
        };
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca3");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then — attacker B selected, NOT the closer kill-set respawn A
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(2), engage.Target!.Value.Id);
        Assert.NotEqual(new ActorId(1), engage.Target!.Value.Id);
    }

    // =========================================================================
    // CA-4 (edge — no reachable attacker)
    // Expect satisfied + in combat + no attacker in scan range
    // → defense returns null (no attacker), cursor confirms step.
    // Global defense subsumes mop-up: without an attacking target, the step
    // simply confirms rather than looping on Engage(null).
    // =========================================================================

    [Fact]
    public async Task CA_4_MopUp_NoAttackerInScan_ConfirmsStep()
    {
        /*
         * Given Expect satisfied, SetInCombat(true), but NO attacker in scan range
         *       (only a non-attacking kill-set mob present).
         * When tick.
         * Then defense returns null (DecideClearAggro found no attacker).
         *      Cursor branch runs: CombatStep Expect satisfied → confirms.
         *      Action is NOT Engage (Wait "all steps satisfied").
         */
        // Given
        var harness = new EngineTestHarness();
        const uint questId = 70004u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        harness.GameState.SetInCombat(true);
        // Non-attacking kill-set mob only — no attackers (not IsTargetingPlayer, not OnEnmityList)
        harness.GameState.AddHostileActor(
            MakeHostile(5, 100, distance: 5, isTargetingPlayer: false, onEnmityList: false));

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca4");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then — defense found no attacker → cursor confirms → NOT Engage
        Assert.IsNotType<EngineAction.Engage>(action);
    }

    // =========================================================================
    // CA-5 (no timeout in v1)
    // Attacker present → defense fires Engage every tick indefinitely.
    // v1 has no MopUpTimeout. The engine never emits AwaitUser for stuck-in-combat.
    // =========================================================================

    [Fact]
    public async Task CA_5_AttackerPresent_DefenseEngagesEveryTick_NoTimeout()
    {
        /*
         * Given Expect satisfied, SetInCombat(true), attacker present across multiple ticks.
         * When tick 1 and tick 2 (with time advanced).
         * Then both ticks return Engage (defense active each tick).
         *      No AwaitUser is ever emitted — v1 has no defense timeout.
         *
         * This replaces the old MopUpTimeout test: ResolveMopUp and its 15-second
         * timeout are deleted. Global defense fires uniformly every tick.
         */
        // Given
        var clock = new ManualTimeProvider(T0);
        var (engine, gameState, questState, combat, _) = BuildEngineWithClock(clock);

        const uint questId = 70005u;
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        gameState.SetInCombat(true);
        gameState.AddHostileActor(MakeHostile(9, 777, distance: 5, isTargetingPlayer: true));
        gameState.SetJob(new JobId(2), 80);

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        engine.StartQuest(BuildCombatQuest(questId, step));
        engine.BeginRun("run-ca5");

        // Tick 1 — defense fires, returns Engage
        gameState.Reset();
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1);

        // Advance well past the old 15-second timeout
        clock.Advance(TimeSpan.FromSeconds(30));

        // Tick 2 — still in combat, still an attacker → defense fires again (no timeout)
        gameState.Reset();
        var tick2 = await engine.Tick(CancellationToken.None);

        // Must still be Engage (no AwaitUser — v1 has no timeout)
        Assert.IsType<EngineAction.Engage>(tick2);
        Assert.IsNotType<EngineAction.AwaitUser>(tick2);
    }

    [Fact]
    public async Task CA_5b_AttackerPresent_MultipleTicksAllEngage()
    {
        /*
         * Regression guard: defense fires on every tick as long as in combat + attacker present.
         * Three consecutive ticks all return Engage — no state that would cause a transition
         * to Wait or AwaitUser after a fixed number of ticks.
         */
        // Given
        var clock = new ManualTimeProvider(T0);
        var (engine, gameState, questState, _, _) = BuildEngineWithClock(clock);

        const uint questId = 70051u;
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0);
        gameState.SetInCombat(true);
        gameState.AddHostileActor(MakeHostile(9, 777, distance: 5, isTargetingPlayer: true));
        gameState.SetJob(new JobId(2), 80);

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        engine.StartQuest(BuildCombatQuest(questId, step));
        engine.BeginRun("run-ca5b");

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            var tick = await engine.Tick(CancellationToken.None);
            Assert.IsType<EngineAction.Engage>(tick);
        }
    }

    // =========================================================================
    // CA-6 (confirm clears combat state; fresh run starts clean)
    // After confirming a CombatStep, ResetAsync is called. A fresh BeginRun
    // on the same step sees clean controller state (no stale targets).
    // =========================================================================

    [Fact]
    public async Task CA_6_ConfirmClearsCombatState_NextRunStartsFresh()
    {
        /*
         * Given Expect satisfied, out of combat.
         * Tick 1 → defense returns null (not in combat), cursor confirms step.
         *          ResetAsync is called → ClearTarget recorded.
         *
         * Start a second run. In combat, attacker present.
         * Tick 2 → defense fires → Engage (fresh state, no stale latches).
         *
         * This replaces the old mop-up-timer-reset test: the timer concept is gone (v1
         * has no defense timeout). This test pins that ResetAsync on confirm clears
         * controller state so subsequent combat encounters start fresh.
         */
        var clock = new ManualTimeProvider(T0);
        var (engine, gameState, questState, combat, _) = BuildEngineWithClock(clock);

        const uint questId = 70006u;
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        gameState.SetInCombat(false); // out of combat on first run

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        engine.StartQuest(BuildCombatQuest(questId, step));
        engine.BeginRun("run-ca6");

        // Tick 1 — Expect satisfied, not in combat → confirms
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsNotType<EngineAction.Engage>(tick1);

        // ClearTarget from ResetAsync on confirm
        var targets1 = combat.RecordedTargets.Snapshot();
        Assert.True(targets1.Any(t => t.IsClear),
            $"Expected ClearTarget on confirm. Targets: {FormatTargets(combat)}");

        // Second run — in combat, attacker present
        engine.BeginRun("run-ca6-second");
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0);
        gameState.SetInCombat(true);
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 777, distance: 5, isTargetingPlayer: true));

        var tick2 = await engine.Tick(CancellationToken.None);

        // Defense fires → Engage (clean state, no stale latches from first run)
        Assert.IsType<EngineAction.Engage>(tick2);
    }

    // =========================================================================
    // CA-7 (reset on sequence change)
    // Sequence change clears mop-up state.
    // ASSERTION FAIL: no mop-up timer in current engine.
    // =========================================================================

    [Fact]
    public async Task CA_7_SequenceChange_ClearsMopUpTimer_TalkStepFollows()
    {
        /*
         * Given seq 0 (combat, mop-up armed), then game advances to seq 1 (talk step).
         * When tick on seq 1.
         * Then action is NOT Engage/AwaitUser from mop-up (engine is on the talk step).
         *      ClearTarget is recorded (sequence-change ResetAsync).
         *      Mop-up timer cleared: if we re-enter the combat step later, it does NOT time out.
         *
         * ASSERTION FAIL: the ClearTarget-after-sequence-change assertion is about ResetAsync
         * being called (this already works today via the sequence-change branch). But the
         * timer-clear assertion (no spurious timeout on re-entry) is the new RED behavior.
         */
        var clock = new ManualTimeProvider(T0);
        var (engine, gameState, questState, combat, _) = BuildEngineWithClock(clock);

        const uint questId = 70007u;
        var combatStep = new CombatStep
        {
            Id = "ca7-combat",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.OverworldEnemies,
            Expect = new PredicateExpect { Predicate = $"questVariable({questId}, 0) >= 1" }
        };
        var talkStep = new TalkStep
        {
            Id = "ca7-talk",
            Target = new NpcLocation(1000u, 0, new Position3(0f, 0f, 0f))
        };
        var quest = BuildTwoSequenceCombatQuest(questId, combatStep, talkStep);
        engine.StartQuest(quest);
        engine.BeginRun("run-ca7");

        // Seq 0: Expect satisfied, in combat → mop-up
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        gameState.SetInCombat(true);
        gameState.AddHostileActor(MakeHostile(9, 777, distance: 5, isTargetingPlayer: true));
        gameState.SetJob(new JobId(2), 80);

        // Tick 1 — defense fires (in combat, attacker present)
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1);

        // Game advances to seq 1 (talk step)
        questState.SetQuestSequence(new QuestId(questId), 1);
        gameState.SetInCombat(false); // out of combat after sequence change

        // Tick 2 — seq 1, talk step
        gameState.Reset();
        combat.Reset();
        var tick2 = await engine.Tick(CancellationToken.None);

        // Should be Interact or Navigate (talk step), NOT Engage or AwaitUser
        Assert.IsNotType<EngineAction.Engage>(tick2);
        Assert.IsNotType<EngineAction.AwaitUser>(tick2);

        // ClearTarget from sequence-change ResetAsync
        var targets = combat.RecordedTargets.Snapshot();
        Assert.True(targets.Any(t => t.IsClear),
            $"Expected ClearTarget on sequence change. Targets: {FormatTargets(combat)}");

        // Verify no stale state from seq 0: re-enter combat step, attacker present → Engage
        // v1 has no timeout, so this always fires Engage when in combat with an attacker.
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0);
        gameState.SetInCombat(true);
        clock.Advance(TimeSpan.FromSeconds(14));

        var tick3 = await engine.Tick(CancellationToken.None);
        // NOT AwaitUser (no timeout in v1)
        Assert.IsNotType<EngineAction.AwaitUser>(tick3);
    }

    // =========================================================================
    // CA-8 (universal defense reads IsPlayerInCombat on every tick)
    // Global defense fires before the cursor walk on EVERY tick. IsPlayerInCombat
    // is read once per tick (regardless of step type) as the defense gate.
    // =========================================================================

    [Fact]
    public async Task CA_8a_TalkStep_DefenseReadsIsPlayerInCombatOnce()
    {
        /*
         * Given a quest with only a TalkStep (no CombatStep).
         * And SetInCombat(false). No hostiles.
         * When Tick.
         * Then RecordedReads contains EXACTLY ONE IsPlayerInCombat call (from defense).
         * Defense returns null (not in combat), cursor walk runs → Navigate/Interact.
         *
         * Updated for universal defense (2026-05-28): defense reads IsPlayerInCombat
         * on every tick, including non-combat quests. Prior assertion (zero reads) is
         * replaced with exactly-one-read.
         */
        var harness = new EngineTestHarness();
        const uint questId = 70008u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(false);

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Non-Combat Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps = [new TalkStep { Id = "talk", Target = new NpcLocation(1u, 0, new Position3(0, 0, 0)) }]
                }
            ]
        };
        harness.Engine.StartQuest(quest);
        harness.GameState.Reset();

        await harness.Engine.Tick(CancellationToken.None);

        var reads = harness.GameState.RecordedReads.Snapshot();
        var inCombatReads = reads.Count(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat));
        Assert.Equal(1, inCombatReads);
    }

    [Fact]
    public async Task CA_8b_CombatStep_ExpectNotYetTrue_DefenseReadsIsPlayerInCombat()
    {
        /*
         * Given a combat step whose Expect is NOT yet satisfied (var[0]=0).
         * And SetInCombat(true), attacker NOT present (non-attacking kill-set mob only).
         * When Tick (defense runs, finds no attacker → cursor branch runs → Decide → Engage).
         * Then RecordedReads contains EXACTLY ONE IsPlayerInCombat call (from defense).
         *
         * Updated for universal defense (2026-05-28): defense reads IsPlayerInCombat
         * on every tick. The Expect-satisfied branch no longer reads it separately —
         * defense handles that check universally before the cursor walk.
         */
        var harness = new EngineTestHarness();
        const uint questId = 70009u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0); // expect FALSE
        harness.GameState.SetInCombat(true);
        // Non-attacking mob: defense finds no attacker → cursor runs Decide
        harness.GameState.AddHostileActor(MakeHostile(1, 100, distance: 5,
            isTargetingPlayer: false, onEnmityList: false));
        harness.GameState.SetJob(new JobId(2), 80);

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca8b");
        harness.GameState.Reset();

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Should be Engage (fighting, expect false — cursor branch ran Decide)
        Assert.IsType<EngineAction.Engage>(action);

        var reads = harness.GameState.RecordedReads.Snapshot();
        var inCombatReads = reads.Count(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat));
        Assert.Equal(1, inCombatReads); // exactly one: from defense gate
    }

    // =========================================================================
    // CA-9 (fail-open)
    // IsPlayerInCombat read returns Result.Failure → engine confirms (fail open).
    // ASSERTION FAIL: current engine doesn't read it at all, so this tests the
    // fail-open semantics of the NEW code path.
    // =========================================================================

    [Fact]
    public async Task CA_9_IsPlayerInCombatFailure_FailOpen_Confirms()
    {
        /*
         * Given Expect satisfied, and IsPlayerInCombat scripted to return Result.Failure.
         * When Tick.
         * Then engine treats the read failure as "not in combat" → confirms + advances.
         *      Action is NOT Engage (confirms, mop-up not entered on failure).
         *
         * Pins: an unknowable combat state never traps a satisfied objective in mop-up.
         *
         * Uses FakeGameStateProvider.SetInCombatFailure — the new fake setter added to
         * support this test (test infra only, not production code).
         *
         * ASSERTION FAIL: current engine confirms immediately regardless (no read done),
         * so this actually "passes" today for the wrong reason. After the feature, the
         * engine reads IsPlayerInCombat, gets a failure, and still confirms (fail-open).
         * The assertion also checks that IsPlayerInCombat WAS read (to pin the code path).
         */
        // Given
        var harness = new EngineTestHarness();
        const uint questId = 70091u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        harness.GameState.SetInCombatFailure("fake-adapter-error");

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca9");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then — fail open: treats failure as out-of-combat → confirms
        Assert.IsNotType<EngineAction.Engage>(action);

        // After the feature, IsPlayerInCombat must have been READ (and the failure handled).
        // This assertion is the new RED behavior — it will fail until the feature reads it.
        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.True(reads.Any(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat)),
            $"IsPlayerInCombat must be read even when it fails. Reads: {FormatReads(harness.GameState)}");
    }
}
