using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Gear;
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
        var gear        = new FakeGearManager();
        var minigames   = new FakeMinigameSkipper();
        var dialogue    = new FakeDialogueResolver();
        var timing      = new FakeTimingProfile();
        var trace       = new NullTraceWriter();
        var logger      = NullLogger<QuestEngine>.Instance;

        var engine = new QuestEngine(
            gameState, questState, navigator, teleporter, interactor,
            combat, gear, minigames, dialogue, timing, trace, logger,
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
    // No attackers → Engage(null), no confirm, no crash.
    // ASSERTION FAIL: engine currently confirms immediately.
    // =========================================================================

    [Fact]
    public async Task CA_4_MopUp_NoAttackerInScan_ReturnsEngageNull_NoConfirm()
    {
        /*
         * Given Expect satisfied, SetInCombat(true), but NO attacker in scan range
         *       (only a non-attacking kill-set mob present).
         * When tick.
         * Then action is Engage with Target == null (forward decision, no stall, no crash).
         *      Step is NOT confirmed.
         *
         * ASSERTION FAIL: current engine confirms + returns Wait immediately when Expect is true,
         * ignoring in-combat state.
         */
        // Given
        var harness = new EngineTestHarness();
        const uint questId = 70004u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        harness.GameState.SetInCombat(true);
        // Non-attacking kill-set mob only — no attackers
        harness.GameState.AddHostileActor(
            MakeHostile(5, 100, distance: 5, isTargetingPlayer: false, onEnmityList: false));

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca4");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then — Engage(null), NOT confirmed, NOT AwaitUser
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Target);
    }

    // =========================================================================
    // CA-5 (timeout)
    // Stuck in combat past MopUpTimeout → AwaitUser, AND boundary tick.
    // Uses ManualTimeProvider. ASSERTION FAIL: no mop-up timeout in current engine.
    // =========================================================================

    [Fact]
    public async Task CA_5_MopUpTimeout_ExpiredAfter15s_ReturnsAwaitUser()
    {
        /*
         * Given a directly-constructed engine with ManualTimeProvider at T0,
         *       Expect satisfied, SetInCombat(true) held across ticks,
         *       an attacker present.
         * When tick 1 (arms mop-up timer) → Engage.
         * Advance clock by 16s (> MopUpTimeout of 15s).
         * When tick 2.
         * Then action is EngineAction.AwaitUser with reason containing "could not leave combat".
         *      AND ClearTarget is recorded (timeout path calls ResetAsync).
         *
         * ASSERTION FAIL: current engine has no timeout — it will keep returning Wait/nothing.
         */
        // Given
        var clock = new ManualTimeProvider(T0);
        var (engine, gameState, questState, combat, _) = BuildEngineWithClock(clock);

        const uint questId = 70005u;
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        gameState.SetInCombat(true);
        gameState.AddHostileActor(
            MakeHostile(9, 777, distance: 5, isTargetingPlayer: true));
        // Provide job so controller can resolve attack range
        gameState.SetJob(new JobId(2), 80); // Paladin level 80

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        engine.StartQuest(BuildCombatQuest(questId, step));
        engine.BeginRun("run-ca5");

        // Tick 1 — arms the mop-up timer; should return Engage
        gameState.Reset();
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1); // RED: fails until feature built

        // Advance past MopUpTimeout (15s)
        clock.Advance(TimeSpan.FromSeconds(16));

        // Tick 2 — timeout should fire → AwaitUser
        gameState.Reset();
        var tick2 = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(tick2);
        Assert.Contains("could not leave combat", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);

        // ClearTarget recorded (ResetAsync called on timeout path)
        var targets = combat.RecordedTargets.Snapshot();
        Assert.True(targets.Any(t => t.IsClear),
            $"Expected ClearTarget on timeout. Targets: {FormatTargets(combat)}");
    }

    [Fact]
    public async Task CA_5b_MopUpTimeout_JustUnder15s_StillEngages()
    {
        /*
         * Boundary: elapsed == MopUpTimeout - 1ms → still mopping (not yet expired).
         * When tick after advancing by 14 999 ms.
         * Then action is Engage (timer not yet tripped).
         *
         * ASSERTION FAIL: no timeout in current engine.
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

        // Tick 1 — arms timer
        await engine.Tick(CancellationToken.None);

        // Advance to just under 15s
        clock.Advance(TimeSpan.FromMilliseconds(14_999));

        // Tick 2 — just under timeout → still Engage
        var tick2 = await engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Engage>(tick2);
    }

    // =========================================================================
    // CA-6 (reset on confirm)
    // Confirming a combat step clears mop-up timer; next combat step starts fresh.
    // ASSERTION FAIL: no timer in current engine.
    // =========================================================================

    [Fact]
    public async Task CA_6_ConfirmClearsMopUpTimer_NextCombatStepStartsFresh()
    {
        /*
         * Given engine at T0, first combat step: Expect satisfied, in-combat, attacker present.
         * Tick 1 arms mop-up (at T0). Advance clock 5s (inside timeout window).
         * SetInCombat(false). Tick 2 → step confirms, mop-up timer cleared.
         *
         * Start a second pass of the same step (simulated by rewinding via BeginRun + seq reset).
         * Advance clock by 1 more second (total 6s elapsed since T0, but mop-up timer should have
         * reset to T0+6 when the step was re-entered, NOT carried the 5s debt from the first pass).
         * Tick 3 (new mop-up, in combat, attacker present): should return Engage (not AwaitUser).
         *
         * ASSERTION FAIL: timer not implemented.
         */
        var clock = new ManualTimeProvider(T0);
        var (engine, gameState, questState, combat, _) = BuildEngineWithClock(clock);

        const uint questId = 70006u;
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect true
        gameState.SetInCombat(true);
        gameState.AddHostileActor(MakeHostile(9, 777, distance: 5, isTargetingPlayer: true));
        gameState.SetJob(new JobId(2), 80);

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        engine.StartQuest(BuildCombatQuest(questId, step));
        engine.BeginRun("run-ca6");

        // Tick 1 — arms mop-up at T0
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1); // RED

        // Advance 5s (still within 15s window)
        clock.Advance(TimeSpan.FromSeconds(5));

        // Confirm — out of combat
        gameState.SetInCombat(false);
        var tick2 = await engine.Tick(CancellationToken.None);
        Assert.IsNotType<EngineAction.Engage>(tick2); // confirms

        // Simulate re-entering the same combat step by starting a fresh run
        engine.BeginRun("run-ca6-second");
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // still expect true
        gameState.SetInCombat(true); // in combat again
        gameState.ClearHostileActors();
        gameState.AddHostileActor(MakeHostile(9, 777, distance: 5, isTargetingPlayer: true));

        // Advance 1 more second — total 6s from T0, but new mop-up started at T0+5 on second pass.
        // Elapsed since new arm = 1s → well under 15s → must NOT timeout.
        clock.Advance(TimeSpan.FromSeconds(1));

        var tick3 = await engine.Tick(CancellationToken.None);

        // Should be Engage (mop-up active, timer freshly armed, not expired)
        // NOT AwaitUser (which would mean the stale timer was carried over)
        Assert.IsType<EngineAction.Engage>(tick3);
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

        // Tick 1 — arms mop-up timer
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1); // RED: engine currently confirms

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

        // Verify timer cleared: re-enter combat step without timeout
        questState.SetQuestSequence(new QuestId(questId), 0);
        questState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0);
        gameState.SetInCombat(true);

        // Advance clock by 14s total — if timer carried forward from seq 0, we'd timeout.
        // It should NOT time out; a fresh arm at re-entry means 14s < 15s is still Engage.
        clock.Advance(TimeSpan.FromSeconds(14));

        var tick3 = await engine.Tick(CancellationToken.None);
        // NOT AwaitUser (which would indicate a stale timer from seq 0)
        Assert.IsNotType<EngineAction.AwaitUser>(tick3);
    }

    // =========================================================================
    // CA-8 (D6 regression — read is combat-completion-gated)
    // Non-combat step and mid-fight combat step must NOT read IsPlayerInCombat.
    // ASSERTION FAIL (case b): current engine does not read IsPlayerInCombat at all —
    //   so case (b) already "passes" today. After the feature, the read only happens on the
    //   Expect-satisfied CombatStep tick, so case (b) must still be zero reads.
    // Case (a) (talk step) should already pass today too.
    // =========================================================================

    [Fact]
    public async Task CA_8a_TalkStep_NoIsPlayerInCombatRead()
    {
        /*
         * Given a quest with only a TalkStep (no CombatStep).
         * When Tick.
         * Then RecordedReads contains NO IsPlayerInCombat call.
         *
         * REGRESSION GUARD: should pass both before and after feature (no change to talk path).
         */
        var harness = new EngineTestHarness();
        const uint questId = 70008u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

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
        Assert.False(reads.Any(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat)),
            $"TalkStep tick must NOT read IsPlayerInCombat. Reads: {FormatReads(harness.GameState)}");
    }

    [Fact]
    public async Task CA_8b_CombatStep_ExpectNotYetTrue_NoIsPlayerInCombatRead()
    {
        /*
         * Given a combat step whose Expect is NOT yet satisfied (var[0]=0).
         * When Tick (engine goes to Decide/Engage path).
         * Then RecordedReads contains NO IsPlayerInCombat call.
         *      (The read only happens when Expect IS satisfied on a CombatStep tick.)
         *
         * REGRESSION GUARD: should pass today AND after the feature (the read is gated on Expect).
         */
        var harness = new EngineTestHarness();
        const uint questId = 70009u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0); // expect FALSE
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(MakeHostile(1, 100, distance: 5));
        harness.GameState.SetJob(new JobId(2), 80);

        var step = MakeCombatStep(questId, killDataIds: [100u]);
        harness.Engine.StartQuest(BuildCombatQuest(questId, step));
        harness.Engine.BeginRun("run-ca8b");
        harness.GameState.Reset();

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Should be Engage (fighting, expect false)
        Assert.IsType<EngineAction.Engage>(action);

        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.False(reads.Any(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat)),
            $"Mid-fight CombatStep tick must NOT read IsPlayerInCombat. Reads: {FormatReads(harness.GameState)}");
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
