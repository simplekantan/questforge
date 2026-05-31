using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;
using NpcId = QuestForge.Adapters.Types.NpcId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;
using AetheryteId = QuestForge.Adapters.Types.AetheryteId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Failing tests for QuestEngine dispatch of EquipBestGearStep.
/// Spec: docs/EQUIP_BEST_GEAR_STEP_PLAN.md -- Decisions EB-1 through EB-14,
/// scenarios EB1-EB13.
///
/// Types referenced below that do NOT yet exist:
///   - EngineAction.EquipBestGear(Step? Origin = null)               (TODO)
///   - QuestEngine.ResolveEquipBestGear async pre-arm                (TODO)
///   - EngineTestHarness RunToCompletion arm for EquipBestGear       (TODO)
///   - HarnessEngine dismount exemption for EquipBestGear            (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~EquipBestGearStepTests"
/// </summary>
public sealed class EquipBestGearStepTests
{
    // =========================================================================
    // EB1 -- Happy path -- emits EquipBestGear
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_HappyPath_EmitsEquipBestGear_EB1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptNextResult(Adapters.Gear.EquipOutcome.Equipped);

        var step = new EquipBestGearStep
        {
            Id = "equip-best",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var equipBest = Assert.IsType<EngineAction.EquipBestGear>(action);
        Assert.NotNull(equipBest.Origin);
    }

    // =========================================================================
    // EB2 -- Expect already satisfied -- step skipped (cursor walk short-circuits)
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_ExpectAlreadySatisfied_StepSkipped_EB2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.GameState.SetAetheryteAttuned(new AetheryteId(8), true);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-skip",
            Expect = new PredicateExpect { Predicate = "isAttuned(8)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits; step confirmed done; no more steps -> Wait
        Assert.IsType<EngineAction.Wait>(action);
        // Adapter never called
        Assert.Equal(0, harness.BestGearEquipper.RecordedCalls.Count);
    }

    // =========================================================================
    // EB3 -- Player casting -- Wait, no EquipBestGear emitted
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_PlayerCasting_EmitsWait_EB3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.GameState.SetCasting(true);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-casting",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // EB4 -- Player in combat -- Wait, no EquipBestGear emitted
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_PlayerInCombat_EmitsWait_EB4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.GameState.SetInCombat(true);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-combat",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("in combat", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // EB5 -- No adapter wired -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_NoAdapterWired_EmitsAwaitUser_EB5()
    {
        // Construct a QuestEngine WITHOUT bestGearEquipper (null default).
        // The harness always wires FakeBestGearEquipper, so we must construct a separate engine.
        var engine = new QuestEngine(
            new Adapters.Fakes.State.FakeGameStateProvider(),
            new Adapters.Fakes.State.FakeQuestState(),
            new Adapters.Fakes.Movement.FakeNavigator(new Adapters.Fakes.State.FakeGameStateProvider()),
            new Adapters.Fakes.Movement.FakeTeleporter(new Adapters.Fakes.State.FakeGameStateProvider()),
            new Adapters.Fakes.Interaction.FakeInteractor(
                new Adapters.Fakes.State.FakeGameStateProvider(),
                new Adapters.Fakes.State.FakeQuestState()),
            new Adapters.Fakes.Combat.FakeCombat(),
            new Adapters.Fakes.Minigames.FakeMinigameSkipper(),
            new Adapters.Fakes.Interaction.FakeDialogueResolver(),
            new Adapters.Fakes.Timing.FakeTimingProfile(),
            new Adapters.Fakes.FakeTraceWriter(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QuestEngine>.Instance,
            bestGearEquipper: null);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-no-adapter",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("no IBestGearEquipper wired", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // EB6 -- Mounted + prior Navigate: lazy-dismount does NOT fire (exempt)
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_AfterNavigate_Mounted_DismountDoesNotFire_EB6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildTwoStepQuest(
            questId: 65575,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-equip-best",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new EquipBestGearStep
            {
                Id = "equip-best-after-navigate",
                Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: EquipBestGear -- lazy-dismount does NOT fire (exempt per EB-5)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);

        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // EB7 -- Standalone EquipBestGear + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_Standalone_Mounted_NoDismount_EB7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.GameState.SetMountState(MountState.Mounted);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-standalone-mounted",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.EquipBestGear>(action);
        // No prior Navigate -> lazy-dismount not triggered
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // EB8 -- No Expect -- engine re-fires every tick (stateless retry)
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_NoExpect_RefiresEveryTick_EB8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptNextResult(Adapters.Gear.EquipOutcome.Equipped);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-no-expect",
            Expect = null  // no authored Expect
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);

        // Tick 2: same action again (no Expect to advance past it)
        harness.BestGearEquipper.ScriptNextResult(Adapters.Gear.EquipOutcome.Equipped);
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);
    }

    // =========================================================================
    // EB9 -- Adapter returns Failed -- engine re-fires (stateless retry)
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_AdapterFailure_EngineRefires_EB9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptNextFailure("adapter error");

        var step = new EquipBestGearStep
        {
            Id = "equip-best-adapter-fail",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Engine emits EquipBestGear regardless of adapter outcome (fire-and-forget)
        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(action);
    }

    // =========================================================================
    // EB10 -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_CancelledToken_ThrowsOperationCanceledException_EB10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-cancel",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // EB11 -- RunToCompletion integration: equip best gear then advance
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_RunToCompletion_Integration_EB11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptNextResult(Adapters.Gear.EquipOutcome.Equipped);

        // Wire a TalkStep that completes the quest when interacted with.
        const uint completionNpc = 9999u;
        harness.Interactor.OnInteractWith(new NpcId(completionNpc), () =>
            harness.QuestState.SetQuestStatus(new QuestId(65575), QuestStatus.Complete));

        // Two separate sequence blocks so the engine finds a matching block after
        // the game sequence advances from 0 to 1 (mirrors real quest structure).
        var quest = BuildTwoSequenceQuest(
            questId: 65575,
            seq0Step: new EquipBestGearStep
            {
                Id = "equip-best-rtc",
                Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
            },
            seq1Step: new TalkStep
            {
                Id = "complete-quest",
                Target = new NpcLocation(completionNpc, 128, new Position3(0f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "questSequence(65575) >= 255" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: EquipBestGear emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);

        // Simulate: adapter call + game state change (sequence advances to 1)
        await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        harness.QuestState.SetQuestSequence(new QuestId(65575), 1);

        // Now run remaining ticks to completion
        var actions = await harness.RunToCompletion(maxTicks: 10);

        // RunToCompletion should reach Done via the TalkStep in sequence 1.
        Assert.True(actions.Count > 0,
            $"Expected at least 1 action in RunToCompletion but got 0");
    }

    // =========================================================================
    // EB12 -- W1 fires for EquipBestGearStep without Expect (NOT suppressed)
    // =========================================================================

    [Fact]
    public void Validate_EquipBestGear_NoExpect_W1Fires_EB12()
    {
        var draft = Authoring.DraftValidatorTestData.ValidBaseline();
        draft.AddStep(Authoring.DraftValidatorTestData.MakeDraftStep("equip-best-no-expect-w1", 2,
            new EquipBestGearStep
            {
                Id = "equip-best-no-expect-w1",
                Expect = null  // missing Expect -- W1 fires (NOT suppressed per EB-7)
            },
            notes: "x"), Authoring.DraftValidatorTestData.T0);

        var result = new QuestForge.Engine.Authoring.DraftValidator().Validate(draft);

        // W1 must fire for EquipBestGearStep (no implicit postcondition)
        Authoring.DraftValidatorAssertions.AssertSingleWarning(result, "W1");
    }

    // =========================================================================
    // EB13 -- W1 does NOT fire for EquipBestGearStep with Expect
    // =========================================================================

    [Fact]
    public void Validate_EquipBestGear_WithExpect_NoW1_EB13()
    {
        var draft = Authoring.DraftValidatorTestData.ValidBaseline();
        draft.AddStep(Authoring.DraftValidatorTestData.MakeDraftStep("equip-best-with-expect", 2,
            new EquipBestGearStep
            {
                Id = "equip-best-with-expect",
                Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
            },
            notes: "x"), Authoring.DraftValidatorTestData.T0);

        var result = new QuestForge.Engine.Authoring.DraftValidator().Validate(draft);

        // W1 must NOT fire when Expect is present
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "EquipBestGear Step Test Quest",
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
                    Sequence = sequence,
                    Steps = [step]
                }
            ]
        };

    private static QuestDefinition BuildTwoStepQuest(
        uint questId,
        int sequence,
        Step step0,
        Step step1) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "EquipBestGear Step Two-Step Test Quest",
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
                    Sequence = sequence,
                    Steps = [step0, step1]
                }
            ]
        };

    private static QuestDefinition BuildTwoSequenceQuest(
        uint questId,
        Step seq0Step,
        Step seq1Step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "EquipBestGear Step Two-Sequence Test Quest",
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
                    Steps = [seq0Step]
                },
                new QuestSequence
                {
                    Sequence = 1,
                    Steps = [seq1Step]
                }
            ]
        };
}
