using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// RED PHASE: Engine CombatStep resolution (EC-*, EX-*, NS-*).
/// Uses EngineTestHarness for full engine against fakes.
///
/// EC-* tests fail RED because CombatController.Decide throws NotImplementedException.
/// EX-* tests for completed steps may fail on assertion (expect-path bypasses the controller).
/// NS-* non-starvation tests must PASS (they assert the absence of GetHostileActors
/// on non-combat ticks — no controller is ever invoked).
///
/// Spec: docs/COMBAT_STEP_PART_A_PLAN.md §G3, §G4, §G7
/// </summary>
public sealed class CombatEngineTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatActions(IEnumerable<EngineAction> actions)
        => string.Join(", ", actions.Select(a => a.GetType().Name));

    private static string FormatReads(FakeGameStateProvider gs)
        => string.Join(", ", gs.RecordedReads.Snapshot().Select(r => r.Method));

    private static QuestDefinition BuildCombatQuest(
        uint questId, int sequence, CombatStep combatStep)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "CombatEngine Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = sequence, Steps = [combatStep] }
            ]
        };

    private static QuestDefinition BuildTwoSequenceQuest(
        uint questId, CombatStep combat, Step nextStep)
        => new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "CombatEngine Two-Sequence Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = [combat] },
                new QuestSequence { Sequence = 1, Steps = [nextStep] }
            ]
        };

    private static HostileActor MakeHostile(ulong id, uint dataId, float distance = 5)
        => new HostileActor(
            new ActorId(id),
            dataId,
            new WorldPosition(0f, 0f, 0f),
            distance,
            IsTargetable: true,
            IsDead: false,
            IsTargetingPlayer: false,
            OnPlayerEnmityList: false,
            HasQuestMarker: false);

    // =========================================================================
    // EC-navigate-first — combat step with Location navigates first, no GetHostileActors
    // =========================================================================

    [Fact]
    public async Task EC_NavigateFirst_CombatStepWithLocation_PlayerOutOfRange_EmitsNavigate()
    {
        /*
         * RED (partially): CombatController.Decide is never reached because the navigate
         * leg returns first. However the test may fail because:
         *   - The engine's CombatStep arm uses ResolveInteractOrNavigate; if that
         *     incorrectly falls through to the sync switch default, it throws NotSupportedException.
         *   - Alternatively it correctly returns Navigate — this test verifies that.
         * The pinned assertion is: action is Navigate to (100,0,100), NOT GetHostileActors.
         *
         * CONTRACT: Given a quest with one combat step having Location at (100,0,100),
         *           player at (0,0,0), StopDistance default (3f).
         * When Tick, Then action is Navigate to (100,0,100),
         *            AND FakeGameStateProvider.RecordedReads contains NO 'GetHostileActors'.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66200u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new CombatStep
        {
            Id = "combat-arena",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Location = new NpcLocation(0u, 0, new Position3(100f, 0f, 100f)),
            Expect = new PredicateExpect { Predicate = "questSequence(66200) >= 1" }
        };
        var quest = BuildCombatQuest(66200u, 0, step);
        harness.Engine.StartQuest(quest);

        harness.GameState.Reset(); // clear reads from StartQuest
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert: Navigate (not Engage)
        var nav = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(100f, nav.Destination.X, precision: 0);
        Assert.Equal(100f, nav.Destination.Z, precision: 0);

        // Assert: no GetHostileActors read this tick (step-gating)
        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.False(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must not be called on a navigate-first tick. Reads: {FormatReads(harness.GameState)}");
    }

    // =========================================================================
    // EC-engage-when-in-range — player in range emits Engage and reads GetHostileActors
    // =========================================================================

    [Fact]
    public async Task EC_EngageWhenInRange_PlayerAtLocation_EmitsEngageWithTarget()
    {
        /*
         * RED: CombatController.Decide throws NotImplementedException.
         *
         * Given same step, player already at (100,0,100) (within StopDistance),
         *       one eligible hostile DataId=100 scripted.
         * When Tick, Then action is Engage with Target.DataId == 100,
         *            AND RecordedReads contains 'GetHostileActors'.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66200u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(100f, 0f, 100f));
        harness.GameState.AddHostileActor(MakeHostile(9, 100, distance: 5));

        var step = new CombatStep
        {
            Id = "combat-arena",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Location = new NpcLocation(0u, 0, new Position3(100f, 0f, 100f)),
            Expect = new PredicateExpect { Predicate = "questSequence(66200) >= 1" }
        };
        var quest = BuildCombatQuest(66200u, 0, step);
        harness.Engine.StartQuest(quest);

        harness.GameState.Reset();
        var action = await harness.Engine.Tick(CancellationToken.None);

        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.NotNull(engage.Target);
        Assert.Equal(100u, engage.Target!.Value.DataId);

        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.True(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must be called on engage tick. Reads: {FormatReads(harness.GameState)}");
    }

    // =========================================================================
    // EC-engage-null-target — no eligible hostile → Engage(step, null), no exception
    // =========================================================================

    [Fact]
    public async Task EC_EngageNullTarget_NoEligibleHostile_EmitsEngageWithNullTarget()
    {
        /*
         * RED: CombatController.Decide throws NotImplementedException.
         *
         * Given player in range but no eligible hostile scripted, expect unmet.
         * When Tick, Then action is Engage(step, null) — engine still selects combat step;
         *            controller found nothing. No exception.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66201u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        // No hostiles added, no expect met

        var step = new CombatStep
        {
            Id = "combat-no-enemies",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questSequence(66201) >= 1" }
        };
        var quest = BuildCombatQuest(66201u, 0, step);
        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var engage = Assert.IsType<EngineAction.Engage>(action);
        Assert.Null(engage.Target);
    }

    // =========================================================================
    // EC-not-supported-removed — combat step no longer throws NotSupportedException
    // =========================================================================

    [Fact]
    public async Task EC_NotSupportedRemoved_CombatStep_DoesNotThrowNotSupportedException()
    {
        /*
         * This test asserts the ABSENCE of a specific exception type.
         * If the engine still has CombatStep in the default throw arm, the tick throws
         * NotSupportedException and this test fails.
         * If it throws NotImplementedException (from controller stub), that's correct RED behavior.
         * If it returns an action, that's also acceptable (verifies routing).
         *
         * The intent: CombatStep is no longer unhandled in the engine.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66202u);
        harness.QuestState.SetQuestSequence(questId, 0);

        var step = new CombatStep
        {
            Id = "combat-routed",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questSequence(66202) >= 1" }
        };
        var quest = BuildCombatQuest(66202u, 0, step);
        harness.Engine.StartQuest(quest);

        // Must NOT throw NotSupportedException
        // May throw NotImplementedException (from controller stub — correct RED)
        var ex = await Record.ExceptionAsync(() => harness.Engine.Tick(CancellationToken.None));
        Assert.IsNotType<NotSupportedException>(ex);
    }

    // =========================================================================
    // EX-questvariable-gates-completion — expect evaluates questVariable, gates done
    // =========================================================================

    [Fact]
    public async Task EX_QuestVariableGatesCompletion_VariableFlipConfirmsStep()
    {
        /*
         * RED: CombatController.Decide throws NotImplementedException on tick 1.
         *
         * Given a combat step expect: "questVariable(66104, 0) >= 3"
         *       player in range, eligible hostile present.
         * Tick 1: variables = [0,0,0,0,0,0] → Engage (expect false).
         * Set variables to [3,0,0,0,0,0].
         * Tick 2 → step confirmed-and-skipped → engine emits Wait/Done (no further Engage).
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66104u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.QuestState.SetQuestVariables(questId, 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.AddHostileActor(new HostileActor(
            new ActorId(9), 100, new WorldPosition(0,0,0), 5,
            IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false));

        var step = new CombatStep
        {
            Id = "kill-three-enemies",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questVariable(66104, 0) >= 3" }
        };
        var quest = BuildCombatQuest(66104u, 0, step);
        harness.Engine.StartQuest(quest);

        // Tick 1: variables[0] == 0 → expect false → Engage
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1);

        // Flip: variables[0] = 3 → expect becomes true
        harness.QuestState.SetQuestVariables(questId, 3, 0, 0, 0, 0, 0);

        // Tick 2: expect true → step confirmed → Wait (no more steps in sequence 0)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        // Tick 3: same → still Wait (step confirmed, no further Engage)
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsNotType<EngineAction.Engage>(tick3);
    }

    // =========================================================================
    // EX-questflag-completion — expect evaluates questFlag, gates done
    // =========================================================================

    [Fact]
    public async Task EX_QuestFlagCompletion_FlagFlipConfirmsStep()
    {
        /*
         * RED: CombatController.Decide throws NotImplementedException on tick 1.
         *
         * Same shape as EX-questvariable but with questFlag(66104, 3).
         * Flipping bit 3 confirms the step.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66104u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.QuestState.SetQuestFlags(questId, 0u);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.AddHostileActor(new HostileActor(
            new ActorId(9), 100, new WorldPosition(0,0,0), 5,
            IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false));

        var step = new CombatStep
        {
            Id = "combat-flag",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questFlag(66104, 3)" }
        };
        var quest = BuildCombatQuest(66104u, 0, step);
        harness.Engine.StartQuest(quest);

        // Tick 1: flag bit 3 unset → expect false → Engage
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1);

        // Flip bit 3
        harness.QuestState.SetQuestFlagBit(questId, 3, true);

        // Tick 2: expect true → step confirmed → Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);
    }

    // =========================================================================
    // EX-completed-no-engage — when expect already true, never emits Engage
    // =========================================================================

    [Fact]
    public async Task EX_CompletedNoEngage_ExpectAlreadyTrue_NeverEmitsEngageOrReadsHostiles()
    {
        /*
         * Given the combat step's expect already true on tick 1.
         * Then engine never emits Engage and never calls GetHostileActors.
         * Pins step-gating at the confirmed boundary (confirm-and-skip path short-circuits).
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66203u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.QuestState.SetQuestVariables(questId, 5, 0, 0, 0, 0, 0); // already >= 3

        var step = new CombatStep
        {
            Id = "combat-already-done",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questVariable(66203, 0) >= 3" }
        };
        var quest = BuildCombatQuest(66203u, 0, step);
        harness.Engine.StartQuest(quest);

        harness.GameState.Reset();
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Never Engage
        Assert.IsNotType<EngineAction.Engage>(action);

        // Never read GetHostileActors (step-gated at confirm path)
        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.False(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must NOT be called when step is already confirmed. Reads: {FormatReads(harness.GameState)}");
    }

    // =========================================================================
    // EX-reset-on-advance — controller.Reset() is called on sequence advance
    // =========================================================================

    [Fact]
    public async Task EX_ResetOnAdvance_SequenceAdvances_ControllerResetProducesNewSetTarget()
    {
        /*
         * RED: CombatController.Decide and Reset both throw NotImplementedException.
         *
         * Given a two-sequence quest: seq 0 has a combat step, seq 1 has a talk step.
         * After the combat step's expect becomes true (seq advances to 1),
         * the controller is Reset(). Observable: if we artificially go back to seq 0
         * (re-enter same combat), a fresh SetTarget is issued (reset state).
         *
         * Simpler assertion: after sequence advance, FakeCombat.RecordedTargets.Count
         * reflects only calls before Reset, and a subsequent Decide (by re-entering seq 0)
         * produces a new SetTarget.
         *
         * Implementation note: this tests the seam that the engine calls _combatController.Reset()
         * when _lastKnownSequence changes. We observe it by counting SetTarget calls.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(66204u);

        // Start in sequence 0 (combat step, variables[0]=0)
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.QuestState.SetQuestVariables(questId, 0, 0, 0, 0, 0, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.AddHostileActor(new HostileActor(
            new ActorId(9), 100, new WorldPosition(0,0,0), 5,
            IsTargetable: true, IsDead: false,
            IsTargetingPlayer: false, OnPlayerEnmityList: false, HasQuestMarker: false));

        var combatStep = new CombatStep
        {
            Id = "reset-test-combat",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Expect = new PredicateExpect { Predicate = "questVariable(66204, 0) >= 1" }
        };
        var talkStep = new TalkStep
        {
            Id = "post-combat-talk",
            Target = new NpcLocation(1u, 0, new Position3(0,0,0))
        };
        var quest = BuildTwoSequenceQuest(66204u, combatStep, talkStep);
        harness.Engine.StartQuest(quest);

        // Tick 1: seq 0, variables[0]=0 → Engage (controller sets target)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick1);
        var targetsAfterTick1 = harness.Combat.RecordedTargets.Count; // should be 1

        // Advance to seq 1: variables[0]=1 (triggers confirm-and-skip), then game advances seq
        harness.QuestState.SetQuestVariables(questId, 1, 0, 0, 0, 0, 0);
        harness.QuestState.SetQuestSequence(questId, 1);

        // Tick 2: seq 1 → talk step → engine resets controller (observable if we go back)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        // The engine is now on seq 1 (talk step) — no Engage
        Assert.IsNotType<EngineAction.Engage>(tick2);

        // Go back to seq 0 to verify reset: controller should issue a fresh SetTarget
        harness.QuestState.SetQuestVariables(questId, 0, 0, 0, 0, 0, 0);
        harness.QuestState.SetQuestSequence(questId, 0);

        // Tick 3: seq 0 again → Engage → controller should re-issue SetTarget (state was reset)
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Engage>(tick3);

        // After tick 3, RecordedTargets should have 2 entries (one from tick 1, one from tick 3)
        // because Reset() cleared the "current target" state
        Assert.Equal(2, harness.Combat.RecordedTargets.Count);
    }

    // =========================================================================
    // NS-no-common-path-read — non-combat step tick never reads GetHostileActors
    // =========================================================================

    [Fact]
    public async Task NS_NoCommonPathRead_TalkStepTick_NoGetHostileActorsRead()
    {
        /*
         * This test must PASS immediately (no NotImplementedException path hit).
         *
         * Given a quest with only a TalkStep (no CombatStep).
         * When Tick, Then FakeGameStateProvider.RecordedReads contains NO 'GetHostileActors'.
         * Pins that the combat read never leaked into the common per-tick prelude.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(99001u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId.Value,
            Name = "Non-Combat Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0,0,0)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new TalkStep
                        {
                            Id = "talk-npc",
                            Target = new NpcLocation(1000u, 0, new Position3(0,0,0))
                        }
                    ]
                }
            ]
        };
        harness.Engine.StartQuest(quest);

        harness.GameState.Reset();
        await harness.Engine.Tick(CancellationToken.None);

        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.False(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must NEVER appear in non-combat tick reads. Reads: {FormatReads(harness.GameState)}");
    }
}
