using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: Global defense rule (D1-D10) from docs/GLOBAL_DEFENSE_PLAN.md §5.
///
/// Every test FAILS because <c>QuestEngine.ResolveDefenseOrNull</c> does not exist yet.
/// The engine currently emits the natural cursor action (Navigate/Interact/Engage-from-cursor)
/// rather than the defense-driven <c>Engage(Step=null, Target=attacker)</c>.
///
/// Exception: D5 and D5a are expected to be GREEN after the Engage signature change
/// because "no Engage emitted" matches current behavior — noted per test.
/// D6 is expected RED because without ResolveDefenseOrNull the engine does NOT fire
/// Engage on a non-CombatStep quest even when attacked.
/// </summary>
public sealed class GlobalDefenseTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatAction(EngineAction a) => a.GetType().Name;

    private static string FormatErrors(IEnumerable<string> msgs)
        => string.Join("; ", msgs);

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
    /// Builds a one-sequence quest with a TravelStep as the cursor and a downstream dummy
    /// CombatStep. The dummy CombatStep has an unsatisfied Expect so the cursor stays on the
    /// TravelStep. This satisfies _questHasAnyCombatStep=true for the (now-dropped) gate.
    /// </summary>
    private static QuestDefinition BuildTravelQuestWithDownstreamCombat(uint questId)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "GlobalDefense Travel Quest",
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
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "gd-travel",
                            Destination = new TravelDestination(Zone: 131, Position: new Position3(100f, 0f, 100f))
                        },
                        new CombatStep
                        {
                            Id = "gd-combat-dummy",
                            KillEnemyDataIds = [999u],
                            Spawn = CombatSpawn.OverworldEnemies,
                            Expect = new PredicateExpect { Predicate = $"questVariable({questId}, 0) >= 1" }
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// Builds a one-sequence quest with a TalkStep as the cursor and a downstream dummy
    /// CombatStep.
    /// </summary>
    private static QuestDefinition BuildTalkQuestWithDownstreamCombat(uint questId)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "GlobalDefense Talk Quest",
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
                    Steps =
                    [
                        new TalkStep
                        {
                            Id = "gd-talk",
                            Target = new NpcLocation(1000u, 0, new Position3(0f, 0f, 0f))
                        },
                        new CombatStep
                        {
                            Id = "gd-combat-dummy",
                            KillEnemyDataIds = [999u],
                            Spawn = CombatSpawn.OverworldEnemies,
                            Expect = new PredicateExpect { Predicate = $"questVariable({questId}, 0) >= 1" }
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// Builds a one-sequence quest with ONLY a TravelStep — NO CombatStep anywhere.
    /// Used for D6: defense must fire even without any CombatStep (universal, no gate).
    /// </summary>
    private static QuestDefinition BuildPureTravelQuestNoCombat(uint questId)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "GlobalDefense Pure Travel Quest",
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
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "gd-pure-travel",
                            Destination = new TravelDestination(Zone: 131, Position: new Position3(100f, 0f, 100f))
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// Builds a one-sequence quest whose cursor is on a CombatStep with the given kill set.
    /// Expect is satisfied when questVariable(questId, 0) >= 1.
    /// </summary>
    private static QuestDefinition BuildCombatCursorQuest(
        uint questId,
        uint[] killDataIds,
        bool expectSatisfied = false)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "GlobalDefense CombatCursor Quest",
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
                    Steps =
                    [
                        new CombatStep
                        {
                            Id = "gd-combat-cursor",
                            KillEnemyDataIds = killDataIds,
                            Spawn = CombatSpawn.OverworldEnemies,
                            Expect = new PredicateExpect { Predicate = $"questVariable({questId}, 0) >= 1" }
                        }
                    ]
                }
            ]
        };

    // =========================================================================
    // D1 — Player attacked mid-Travel → engine emits Engage(null, attacker)
    // RED: engine currently emits Navigate (no ResolveDefenseOrNull).
    // =========================================================================

    [Fact]
    public async Task D1_AttackedMidTravel_EmitsEngageWithNullStep()
    {
        /*
         * Given a quest with a TravelStep (cursor) and a downstream dummy CombatStep.
         * And SetInCombat(true). And AddHostileActor(id=42, dist=8, isTargetingPlayer=true).
         *
         * When Tick.
         *
         * Then action is EngineAction.Engage.
         * And engage.Step is null (defense-driven, not CombatStep-anchored).
         * And engage.Target is non-null with Id == ActorId(42).
         * And NO Navigate action was emitted.
         *
         * RED: engine currently returns Navigate because ResolveDefenseOrNull does not exist.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80001u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0); // combat expect false
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 42, dataId: 999, distance: 8, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d1");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step); // defense-driven: Step must be null
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(42), engage.Target!.Value.Id);

        // Also confirm IsPlayerInCombat was read
        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.True(reads.Any(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat)),
            $"IsPlayerInCombat must be read on defense tick. Reads: {string.Join(", ", reads.Select(r => r.Method))}");
    }

    // =========================================================================
    // D2 — Player attacked mid-Talk → engine emits Engage(null, attacker)
    // RED: engine currently emits Navigate/Interact (no ResolveDefenseOrNull).
    // =========================================================================

    [Fact]
    public async Task D2_AttackedMidTalk_EmitsEngageWithNullStep()
    {
        /*
         * Given a quest with a TalkStep (cursor) and a downstream dummy CombatStep.
         * And SetInCombat(true). And AddHostileActor(id=7, dist=6, isTargetingPlayer=true).
         *
         * When Tick.
         *
         * Then action is EngineAction.Engage with Step=null, Target.Id == ActorId(7).
         * And NO Interact action was emitted.
         *
         * RED: engine emits Navigate (to reach NPC) or Interact without defense.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80002u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 7, dataId: 500, distance: 6, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildTalkQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d2");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(7), engage.Target!.Value.Id);
    }

    // =========================================================================
    // D3 — Defense completes (in-combat=false) → engine resumes Travel
    // Phase 1 (D1 state): RED — Tick 1 fails for same reason as D1.
    // Phase 2 (after SetInCombat(false)): GREEN — Navigate is current behavior.
    // Overall test: RED because Tick 1 assertion fails.
    // =========================================================================

    [Fact]
    public async Task D3_DefenseCompletes_ResumesTravel()
    {
        /*
         * Given D1's quest and initial state. Tick 1 expected to return Engage per D1.
         * When test sets SetInCombat(false) and ClearHostileActors, ticks again.
         * Then tick 2 action is EngineAction.Navigate (the original Travel target).
         *
         * RED: Tick 1 fails because defense is not implemented (returns Navigate not Engage).
         * If the tester chooses to skip-Tick1 and only assert Tick 2, that test would be
         * trivially green — but both ticks are asserted to pin the two-phase behavior.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80003u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 42, dataId: 999, distance: 8, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d3");
        harness.GameState.Reset();

        // Tick 1 — defense fires (per D1)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var engage = Assert.IsType<EngineAction.Engage>(tick1); // RED until defense implemented
        Assert.Null(engage.Step);

        // Simulate defense completed: out of combat, no hostiles
        harness.GameState.SetInCombat(false);
        harness.GameState.ClearHostileActors();
        harness.GameState.Reset();

        // Tick 2 — cursor resumes Travel
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Navigate>(tick2);
    }

    // =========================================================================
    // D4 — Multiple attackers → engine picks per SelectAttacker tie-break
    // RED: engine does not fire defense; returns Navigate/Interact.
    // =========================================================================

    [Fact]
    public async Task D4_MultipleAttackers_SelectsHighestScoreTarget()
    {
        /*
         * Given a TalkStep quest + downstream CombatStep.
         * And SetInCombat(true).
         * And THREE hostiles:
         *   id=10, dist=5, OnEnmityList=true  (score=5)
         *   id=20, dist=8, IsTargetingPlayer=true  (score=10)
         *   id=30, dist=8, IsTargetingPlayer=true, OnEnmityList=true  (score=15, highest)
         *
         * When Tick.
         *
         * Then action is Engage with Target.Id == ActorId(30) (score 15 wins).
         *
         * RED: engine returns Navigate without defense.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80004u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 10, dataId: 100, distance: 5, isTargetingPlayer: false, onEnmityList: true));
        harness.GameState.AddHostileActor(
            MakeHostile(id: 20, dataId: 200, distance: 8, isTargetingPlayer: true, onEnmityList: false));
        harness.GameState.AddHostileActor(
            MakeHostile(id: 30, dataId: 300, distance: 8, isTargetingPlayer: true, onEnmityList: true));

        harness.Engine.StartQuest(BuildTalkQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d4");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(30), engage.Target!.Value.Id);
    }

    // =========================================================================
    // D5 — Not in combat, no hostiles → defense is no-op
    // EXPECTED: GREEN (current engine emits Navigate; defense returning null = same result).
    // This test is a regression guard: after defense lands, it must still pass.
    // =========================================================================

    [Fact]
    public async Task D5_NotInCombat_NoHostiles_EmitsNavigate()
    {
        /*
         * Given a TravelStep quest + downstream CombatStep.
         * And SetInCombat(false). No hostiles.
         *
         * When Tick.
         *
         * Then action is EngineAction.Navigate (cursor walk proceeds normally).
         * And RecordedReads contains exactly ONE IsPlayerInCombat read
         *     (defense short-circuits after reading false, before calling DecideClearAggro).
         * And RecordedReads contains ZERO GetHostileActors reads.
         *
         * Note: the Navigate assertion is GREEN today. The IsPlayerInCombat read count
         * assertion is RED until defense is implemented (currently no such read on non-combat paths).
         */
        var harness = new EngineTestHarness();
        const uint questId = 80005u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(false);

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d5");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        Assert.IsType<EngineAction.Navigate>(action);

        var reads = harness.GameState.RecordedReads.Snapshot();

        // After defense is implemented: exactly one IsPlayerInCombat read, zero GetHostileActors.
        // These assertions are RED until the feature is built.
        var inCombatReads = reads.Count(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat));
        Assert.Equal(1, inCombatReads);

        var hostileReads = reads.Count(r => r.Method == nameof(IGameStateProvider.GetHostileActors));
        Assert.Equal(0, hostileReads);
    }

    // =========================================================================
    // D5a — In combat but no attacker in scan range → defense is no-op
    // RED: read-count assertions fail (no IsPlayerInCombat read exists yet without defense).
    // The Navigate assertion might be GREEN today.
    // =========================================================================

    [Fact]
    public async Task D5a_InCombat_NoAttackerInScanRange_EmitsNavigate()
    {
        /*
         * Given a TravelStep quest + downstream CombatStep.
         * And SetInCombat(true) but hostile is at distance=200 (beyond ScanRadius=50).
         *
         * When Tick.
         *
         * Then action is EngineAction.Navigate (defense returns null; cursor walk proceeds).
         * And RecordedReads contains one IsPlayerInCombat AND one GetHostileActors
         *     (DecideClearAggro ran but found no in-range attacker).
         *
         * RED: read-count assertions fail until defense is implemented.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80051u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 99, dataId: 500, distance: 200, isTargetingPlayer: true)); // beyond ScanRadius

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d5a");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        Assert.IsType<EngineAction.Navigate>(action);

        var reads = harness.GameState.RecordedReads.Snapshot();

        // After defense: one IsPlayerInCombat read (defense enters) + one GetHostileActors
        // (DecideClearAggro called, found no in-scan attacker). RED until implemented.
        var inCombatReads = reads.Count(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat));
        Assert.Equal(1, inCombatReads);

        var hostileReads = reads.Count(r => r.Method == nameof(IGameStateProvider.GetHostileActors));
        Assert.Equal(1, hostileReads);
    }

    // =========================================================================
    // D6 — Quest with NO CombatStep + player attacked → defense STILL fires (universal)
    // RED: engine currently emits Navigate (gate was dropped but defense not yet implemented).
    // This is the regression guard for the user-pinned no-gate decision (2026-05-28).
    // =========================================================================

    [Fact]
    public async Task D6_NoCombatStepInQuest_PlayerAttacked_DefenseStillFires()
    {
        /*
         * Given a one-step Travel quest with NO CombatStep anywhere.
         * And SetInCombat(true). And AddHostileActor(id=42, dist=5, isTargetingPlayer=true).
         *
         * When Tick.
         *
         * Then action is EngineAction.Engage with Step=null, Target.Id == ActorId(42).
         *
         * User-pinned (2026-05-28): defense is universal — no quest-shape gate.
         * The fixture-cascade trade-off is documented in §3 Q1 + §7.
         *
         * RED: engine emits Navigate without defense.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80006u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 42, dataId: 777, distance: 5, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildPureTravelQuestNoCombat(questId));
        harness.Engine.BeginRun("run-d6");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(42), engage.Target!.Value.Id);

        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.True(reads.Any(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat)),
            $"IsPlayerInCombat must be read on defense tick. Reads: {string.Join(", ", reads.Select(r => r.Method))}");
        Assert.True(reads.Any(r => r.Method == nameof(IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must be read on defense tick. Reads: {string.Join(", ", reads.Select(r => r.Method))}");
    }

    // =========================================================================
    // D7 — CombatStep cursor, unsatisfied Expect, attacker NOT in kill set
    //      → defense wins, engages the attacker (not the kill-set member)
    // RED: engine currently engages the kill-set member (cursor CombatStep branch ran).
    // =========================================================================

    [Fact]
    public async Task D7_CombatCursor_AttackerNotInKillSet_DefenseWins_EngagesAttacker()
    {
        /*
         * Given cursor on CombatStep with KillEnemyDataIds=[100], Expect unsatisfied.
         * And SetInCombat(true).
         * And TWO hostiles:
         *   id=50, dataId=100, dist=20, not attacking (kill-set, idle)
         *   id=51, dataId=999, dist=10, isTargetingPlayer=true (attacker, off kill-set)
         *
         * When Tick.
         *
         * Then action is Engage with Step=null (defense pre-empted the cursor branch).
         * And Target.Id == ActorId(51) (attacker, NOT the kill-set member).
         *
         * RED: current engine runs cursor CombatStep branch, selects ActorId(50) (kill-set member),
         *      and emits Engage(Step=<step>, Target=ActorId(50)) — both Step and Target are wrong.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80007u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0); // expect false
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);

        // Kill-set member, idle (not attacking)
        harness.GameState.AddHostileActor(
            MakeHostile(id: 50, dataId: 100, distance: 20, isTargetingPlayer: false, onEnmityList: false));
        // Attacker, off kill-set
        harness.GameState.AddHostileActor(
            MakeHostile(id: 51, dataId: 999, distance: 10, isTargetingPlayer: true, onEnmityList: false));

        harness.Engine.StartQuest(BuildCombatCursorQuest(questId, killDataIds: [100u]));
        harness.Engine.BeginRun("run-d7");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step); // defense win — cursor branch did NOT run
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(51), engage.Target!.Value.Id); // attacker selected
        Assert.NotEqual(new ActorId(50), engage.Target!.Value.Id); // kill-set member NOT selected
    }

    // =========================================================================
    // D7a — CombatStep cursor, attacker IS the kill-set member → defense fires, identical target
    // RED: defense not implemented; cursor branch runs and emits Engage(Step=<step>, ...).
    //      Step should be null; it won't be until defense is built.
    // =========================================================================

    [Fact]
    public async Task D7a_CombatCursor_AttackerIsKillSetMember_DefenseFiresSameTarget()
    {
        /*
         * Given cursor on CombatStep with KillEnemyDataIds=[100], Expect unsatisfied.
         * And SetInCombat(true).
         * And ONE hostile: id=60, dataId=100, dist=10, isTargetingPlayer=true.
         *
         * When Tick.
         *
         * Then action is Engage with Step=null, Target.Id == ActorId(60).
         * (Defense pre-empted cursor; functionally the same target, but Step must be null.)
         * And RecordedReads contains exactly ONE GetHostileActors read (defense called it;
         *     the CombatStep cursor branch never ran this tick).
         *
         * RED: cursor branch runs before defense; Engage.Step will be non-null.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80071u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0); // expect false
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);

        harness.GameState.AddHostileActor(
            MakeHostile(id: 60, dataId: 100, distance: 10, isTargetingPlayer: true, onEnmityList: false));

        harness.Engine.StartQuest(BuildCombatCursorQuest(questId, killDataIds: [100u]));
        harness.Engine.BeginRun("run-d7a");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then
        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step); // defense fired; cursor branch did NOT run this tick
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(60), engage.Target!.Value.Id);

        // Exactly one GetHostileActors read (defense path only)
        var reads = harness.GameState.RecordedReads.Snapshot();
        var hostileReads = reads.Count(r => r.Method == nameof(IGameStateProvider.GetHostileActors));
        Assert.Equal(1, hostileReads);
    }

    // =========================================================================
    // D8 — CombatStep Expect satisfied + still in combat → defense engages, no mop-up
    // RED: engine currently has mop-up which fires after Expect is satisfied;
    //      after the feature, defense pre-empts and stepId should be null.
    //      The second tick (out-of-combat) assertion confirms the cursor advances.
    // =========================================================================

    [Fact]
    public async Task D8_CombatStepExpectSatisfied_InCombat_DefenseEngages_NoCursorMopUp()
    {
        /*
         * Given cursor on CombatStep, Expect satisfied (var[0]=1), SetInCombat(true).
         * And AddHostileActor(id=70, dataId=999, dist=5, isTargetingPlayer=true).
         *
         * When Tick 1.
         * Then action is Engage with Step=null, Target.Id == ActorId(70).
         * (Defense pre-empts. CombatStep cursor branch is NEVER reached this tick.)
         * This replaces ResolveMopUp — defense handles post-objective combat uniformly.
         *
         * When SetInCombat(false), ClearHostileActors, Tick 2.
         * Then action is NOT Engage. CombatStep cursor runs; Expect satisfied → confirms.
         *
         * RED: Tick 1 — current engine goes through cursor branch (Expect satisfied →
         *      ResolveMopUp) and emits Engage(Step=<step>, ...) with non-null Step.
         *      The Step=null assertion fails until defense is built.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80008u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect satisfied
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 70, dataId: 999, distance: 5, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildCombatCursorQuest(questId, killDataIds: [100u]));
        harness.Engine.BeginRun("run-d8");
        harness.GameState.Reset();

        // Tick 1 — Expect satisfied, in combat → defense should pre-empt
        var tick1 = await harness.Engine.Tick(CancellationToken.None);

        var engage = Assert.IsType<EngineAction.Engage>(tick1);
        Assert.Null(engage.Step); // RED: currently Step is non-null (mop-up path)
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(70), engage.Target!.Value.Id);

        // Tick 2 — out of combat → cursor confirms CombatStep, advances
        harness.GameState.SetInCombat(false);
        harness.GameState.ClearHostileActors();
        harness.GameState.Reset();

        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Must NOT be Engage — CombatStep confirms and cursor advances
        Assert.IsNotType<EngineAction.Engage>(tick2);
    }

    // =========================================================================
    // D9 (optional) — Race window regression: IsPlayerInCombat=false → respawn aggro
    // RED: engine does not have defense; the race scenario isn't exercised.
    // =========================================================================

    [Fact]
    public async Task D9_RaceWindow_BriefOutOfCombat_ThenRespawnAggro_DefenseRefires()
    {
        /*
         * Given cursor on CombatStep, Expect satisfied.
         * Tick 1: SetInCombat(false), no hostiles → step should confirm, action is NOT Engage.
         *
         * Simulate the quest advancing past this step (sequence increment to 1, TalkStep).
         * Before the next tick: SetInCombat(true) + add attacker.
         * Tick 2 (now on TalkStep): defense fires → Engage(null, attacker).
         *
         * This is the Quest 65847 race-window regression guard.
         * Without defense: tick 1 confirms (step is done), tick 2 emits Navigate/Interact
         *   WITHOUT checking combat → the respawn aggro is ignored.
         * With defense: tick 2 fires Engage before the TalkStep cursor runs.
         *
         * RED: tick 2 assertion fails until defense is implemented.
         */
        // Quest with two sequences: 0 = CombatStep (satisfied), 1 = TalkStep
        const uint questId = 80009u;
        var combatStep = new CombatStep
        {
            Id = "d9-combat",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.OverworldEnemies,
            Expect = new PredicateExpect { Predicate = $"questVariable({questId}, 0) >= 1" }
        };
        var talkStep = new TalkStep
        {
            Id = "d9-talk",
            Target = new NpcLocation(1000u, 0, new Position3(0f, 0f, 0f))
        };
        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "D9 Race Window Quest",
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
                new QuestSequence { Sequence = 1, Steps = [talkStep] }
            ]
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 1, 0, 0, 0, 0, 0); // expect satisfied
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(false); // no combat on tick 1

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d9");
        harness.GameState.Reset();

        // Tick 1 — out of combat, Expect satisfied → should confirm step
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsNotType<EngineAction.Engage>(tick1); // step confirms, no defense

        // Game advances to sequence 1 (TalkStep), but respawn aggro hits before next tick
        harness.QuestState.SetQuestSequence(new QuestId(questId), 1);
        harness.GameState.SetInCombat(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 77, dataId: 888, distance: 5, isTargetingPlayer: true));
        harness.GameState.Reset();

        // Tick 2 — on TalkStep, in combat → defense should fire before TalkStep cursor runs
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // RED: without defense, engine emits Navigate (toward NPC) ignoring the respawn aggro
        var engage = Assert.IsType<EngineAction.Engage>(tick2);
        Assert.Null(engage.Step);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(77), engage.Target!.Value.Id);
    }

    // =========================================================================
    // D10 (optional) — IsPlayerInCombat adapter failure → defense fails-open
    // This test is GREEN today but for the WRONG reason (no IsPlayerInCombat read exists
    // on non-combat paths). After defense is built, it will be RED until the fail-open
    // path is implemented, then GREEN again. Included as a regression guard.
    // =========================================================================

    [Fact]
    public async Task D10_IsPlayerInCombatFailure_FailsOpen_CursorWalksNormally()
    {
        /*
         * Given D1's setup (TravelStep cursor, downstream CombatStep), but
         *   harness.GameState.SetInCombatFailure("adapter offline").
         *
         * When Tick.
         *
         * Then action is EngineAction.Navigate (cursor walk — defense returned null on read failure).
         * And RecordedReads contains IsPlayerInCombat (was read, failure was observed).
         *
         * Current behavior: GREEN (Navigate emitted) but for wrong reason (no read done).
         * After defense: Navigate because fail-open reads failure → returns null from defense.
         * The IsPlayerInCombat read assertion is RED until defense is implemented.
         */
        var harness = new EngineTestHarness();
        const uint questId = 80010u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombatFailure("adapter offline"); // simulate failed read

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d10");
        harness.GameState.Reset();

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then — fail open: defense skipped → cursor walk → Navigate
        Assert.IsType<EngineAction.Navigate>(action);

        // After defense: IsPlayerInCombat must have been READ (failure observed, defense skipped).
        // RED until defense is implemented.
        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.True(reads.Any(r => r.Method == nameof(IGameStateProvider.IsPlayerInCombat)),
            $"IsPlayerInCombat must be read even when it fails. Reads: {string.Join(", ", reads.Select(r => r.Method))}");
    }

    // =========================================================================
    // D11 — mounted + still navigating → defense SKIPS (outrun the attacker).
    //
    // The whole point of mounting is to skip overworld trash mobs en route to
    // an objective. If the player is mid-flight / mid-ride and a hostile aggros,
    // the engine must NOT stop to dismount and engage — it must keep moving.
    // Engage only when at the destination (vnavmesh stopped) or already on foot.
    // =========================================================================

    [Fact]
    public async Task D11_MountedAndNavigating_DefenseSkipsToKeepMoving()
    {
        var harness = new EngineTestHarness();
        const uint questId = 80011u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.SetMountState(MountState.Mounted);
        harness.Navigator.SetIsNavigating(true);
        harness.GameState.AddHostileActor(
            MakeHostile(id: 99, dataId: 999, distance: 5, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d11");
        harness.GameState.Reset();

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Defense skipped → cursor walk emits Navigate (the TravelStep's action).
        Assert.IsType<EngineAction.Navigate>(action);
    }

    // =========================================================================
    // D12 — mounted + NOT navigating (arrived at destination) → defense FIRES.
    //
    // Once vnavmesh has stopped (we're at the destination), stopping to defend
    // is exactly the right behavior. The lazy-dismount hook in EngineHost
    // handles the actual dismount on the Engage transition.
    // =========================================================================

    [Fact]
    public async Task D12_MountedButNotNavigating_DefenseFires()
    {
        var harness = new EngineTestHarness();
        const uint questId = 80012u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.SetMountState(MountState.Mounted);
        harness.Navigator.SetIsNavigating(false); // arrived at destination
        harness.GameState.AddHostileActor(
            MakeHostile(id: 88, dataId: 999, distance: 5, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d12");
        harness.GameState.Reset();

        var action = await harness.Engine.Tick(CancellationToken.None);

        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(88), engage.Target!.Value.Id);
    }

    // =========================================================================
    // D13 — dismounted + navigating → defense FIRES (no mount = no skip).
    //
    // The "keep moving while mounted" skip only applies to mounted travel.
    // A dismounted player being attacked mid-navigation should defend
    // normally — they can't outrun on foot.
    // =========================================================================

    [Fact]
    public async Task D13_DismountedAndNavigating_DefenseStillFires()
    {
        var harness = new EngineTestHarness();
        const uint questId = 80013u;
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.QuestState.SetQuestVariables(new QuestId(questId), 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetInCombat(true);
        harness.GameState.SetMountState(MountState.Dismounted);
        harness.Navigator.SetIsNavigating(true); // still moving on foot
        harness.GameState.AddHostileActor(
            MakeHostile(id: 77, dataId: 999, distance: 5, isTargetingPlayer: true));

        harness.Engine.StartQuest(BuildTravelQuestWithDownstreamCombat(questId));
        harness.Engine.BeginRun("run-d13");
        harness.GameState.Reset();

        var action = await harness.Engine.Tick(CancellationToken.None);

        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Step);
        Assert.NotNull(engage.Target);
        Assert.Equal(new ActorId(77), engage.Target!.Value.Id);
    }
}
