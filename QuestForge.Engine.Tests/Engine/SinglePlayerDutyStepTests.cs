using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;
using NpcId = QuestForge.Adapters.Types.NpcId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Failing tests for QuestEngine dispatch of DutyStep(kind: "spd").
/// Spec: docs/SINGLE_PLAYER_DUTY_PLAN.md -- Decisions SPD1-SPD14,
/// scenarios S1-S10.
///
/// All types referenced below are NEW and do not yet exist:
///   - EngineAction.EnterSinglePlayerDuty(Step? Origin)               (TODO)
///   - IDutyRunner interface                                           (TODO)
///   - FakeDutyRunner fake                                             (TODO)
///   - QuestEngine ctor param dutyRunner: IDutyRunner?                 (TODO)
///   - QuestEngine.ResolveSpd async pre-arm                            (TODO)
///   - EngineTestHarness.DutyRunner property                           (TODO)
///   - EngineTestHarness RunToCompletion arm for EnterSinglePlayerDuty (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~SinglePlayerDutyStepTests"
/// </summary>
public sealed class SinglePlayerDutyStepTests
{
    // =========================================================================
    // S1 -- Happy path: DutyStep(kind: "spd"), BossMod available, duty completes
    // =========================================================================

    [Fact]
    public async Task SpdStep_HappyPath_EmitsEnterSinglePlayerDuty_S1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70001), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 20f));
        harness.DutyRunner.BossModAvailable = true;

        // Wire: on first Interact for talk-to-npc, advance quest to seq 2
        harness.Interactor.OnInteractWith(new NpcId(5000u), () =>
            harness.QuestState.SetQuestSequence(new QuestId(70001), 2));

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 70001,
            Name = "SPD Happy Path Test",
            Expansion = "arr",
            Category = "msq",
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
                            Id = "talk-to-npc",
                            Target = new NpcLocation(5000u, 132, new Position3(10f, 0f, 20f)),
                            Expect = new PredicateExpect { Predicate = "questSequence(70001) >= 2" }
                        },
                        new DutyStep
                        {
                            Id = "enter-spd",
                            Kind = "spd",
                            Expect = new PredicateExpect { Predicate = "questSequence(70001) >= 3" }
                        }
                    ]
                }
            ]
        };

        harness.Engine.StartQuest(quest);

        // Tick until we get past the TalkStep
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        // Expect: Interact for the TalkStep (talk-to-npc)
        Assert.IsType<EngineAction.Interact>(tick1);

        // Now quest is at seq 2 (TalkStep Expect satisfied). DutyStep should fire.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        var enterSpd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(tick2);
        Assert.NotNull(enterSpd.Origin);

        // Verify BossMod was checked
        Assert.True(harness.DutyRunner.IsBossModAvailableCallCount >= 1);
    }

    // =========================================================================
    // S2 -- BossMod not available -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task SpdStep_BossModNotAvailable_EmitsAwaitUser_S2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70002), 0);
        harness.DutyRunner.BossModAvailable = false;

        var step = new DutyStep
        {
            Id = "enter-spd-no-bossmod",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questSequence(70002) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("BossMod", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // S3 -- IDutyRunner null (no adapter wired) -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task SpdStep_NoDutyRunnerWired_EmitsAwaitUser_S3()
    {
        // Construct a QuestEngine WITHOUT dutyRunner (null).
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(70003), 0);

        var engine = new QuestEngine(
            gameState,
            questState,
            new Adapters.Fakes.Movement.FakeNavigator(gameState),
            new Adapters.Fakes.Movement.FakeTeleporter(gameState),
            new Adapters.Fakes.Interaction.FakeInteractor(gameState, questState),
            new Adapters.Fakes.Combat.FakeCombat(),
            new Adapters.Fakes.Minigames.FakeMinigameSkipper(),
            new Adapters.Fakes.Interaction.FakeDialogueResolver(),
            new Adapters.Fakes.Timing.FakeTimingProfile(),
            new Adapters.Fakes.FakeTraceWriter(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QuestEngine>.Instance,
            dutyRunner: null);

        var step = new DutyStep
        {
            Id = "enter-spd-no-adapter",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questSequence(70003) >= 3" }
        };

        engine.StartQuest(BuildSingleStepQuest(70003, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("IDutyRunner", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // S4 -- No Expect -- fires every tick (stateless spin-loop)
    // =========================================================================

    [Fact]
    public async Task SpdStep_NoExpect_FiresEveryTick_S4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70004), 0);
        harness.DutyRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "enter-spd-no-expect",
            Kind = "spd",
            Expect = null  // no Expect -- spin-loop
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70004, 0, step));

        // All 5 ticks should return EnterSinglePlayerDuty (engine never self-confirms)
        for (var i = 0; i < 5; i++)
        {
            var action = await harness.Engine.Tick(CancellationToken.None);
            Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        }
    }

    // =========================================================================
    // S5 -- Retry: Expect stays false, re-dispatches until satisfied
    // =========================================================================

    [Fact]
    public async Task SpdStep_Retry_ReDispatchesUntilExpectSatisfied_S5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70005), 0);
        harness.DutyRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "enter-spd-retry",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questSequence(70005) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70005, 0, step));

        // Tick 1-3: EnterSinglePlayerDuty each time (duty fails / expect stays false)
        for (var i = 0; i < 3; i++)
        {
            var action = await harness.Engine.Tick(CancellationToken.None);
            Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        }

        // Simulate duty completion: quest sequence advances
        harness.QuestState.SetQuestSequence(new QuestId(70005), 3);

        // Next tick: Expect satisfied, step confirmed, engine moves on -> Wait (no more steps)
        var finalAction = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(finalAction);

        // StartDuty was called multiple times (idempotent calls from harness dispatch)
        Assert.True(harness.DutyRunner.StartDutyCallCount >= 3,
            $"Expected StartDutyCallCount >= 3 but got {harness.DutyRunner.StartDutyCallCount}");
    }

    // =========================================================================
    // S6 -- SkipIf satisfied -- step skipped, no EnterSinglePlayerDuty
    // =========================================================================

    [Fact]
    public async Task SpdStep_ExpectAlreadySatisfied_StepSkipped_S6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70006), 3);
        harness.DutyRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "enter-spd-skip",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questSequence(70006) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70006, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect is already true -> cursor walk confirms step -> Wait (no more steps)
        Assert.IsType<EngineAction.Wait>(action);
        // DutyRunner never called
        Assert.Equal(0, harness.DutyRunner.StartDutyCallCount);
        Assert.Equal(0, harness.DutyRunner.IsBossModAvailableCallCount);
    }

    // =========================================================================
    // S7 -- DutyStep kind "regular" -- unsupported, NotSupportedException
    // =========================================================================

    [Fact]
    public async Task DutyStep_KindRegular_ThrowsNotSupportedException_S7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70007), 0);

        var step = new DutyStep
        {
            Id = "enter-regular-duty",
            Kind = "regular",
            DutyId = 56,
            EntryNpc = new NpcLocation(9000u, 128, new Position3(0f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questSequence(70007) >= 5" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70007, 0, step));

        // DutyStep kind "regular" is not implemented yet -- should throw
        await Assert.ThrowsAsync<NotSupportedException>(
            () => harness.Engine.Tick(CancellationToken.None));
    }

    // =========================================================================
    // S8 -- Mounted + prior Navigate: lazy-dismount DOES fire (NOT exempt)
    // =========================================================================

    [Fact]
    public async Task SpdStep_AfterNavigate_Mounted_DismountFires_S8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70008), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.DutyRunner.BossModAvailable = true;

        var quest = BuildTwoStepQuest(
            questId: 70008,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-spd",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new DutyStep
            {
                Id = "enter-spd-after-navigate",
                Kind = "spd",
                Expect = new PredicateExpect { Predicate = "questSequence(70008) >= 3" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: lazy-dismount fires because EnterSinglePlayerDuty is NOT exempt
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but got {harness.Mount.DismountCallCount}. " +
            "EnterSinglePlayerDuty is NOT exempt from lazy-dismount (Decision SPD11).");
    }

    // =========================================================================
    // S9 -- Standalone + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task SpdStep_Standalone_Mounted_NoDismount_S9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70009), 0);
        harness.GameState.SetMountState(MountState.Mounted);
        harness.DutyRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "enter-spd-standalone-mounted",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questSequence(70009) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70009, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        // No prior Navigate -> lazy-dismount not triggered
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // S10 -- Multiple SPD steps in different sequences both complete
    // =========================================================================

    [Fact]
    public async Task SpdStep_MultipleSpdStepsInQuest_BothComplete_S10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70010), 0);
        harness.DutyRunner.BossModAvailable = true;

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 70010,
            Name = "Multiple SPD Test",
            Expansion = "arr",
            Category = "msq",
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
                        new DutyStep
                        {
                            Id = "spd-1",
                            Kind = "spd",
                            Expect = new PredicateExpect { Predicate = "questSequence(70010) >= 1" }
                        }
                    ]
                },
                new QuestSequence
                {
                    Sequence = 1,
                    Steps =
                    [
                        new DutyStep
                        {
                            Id = "spd-2",
                            Kind = "spd",
                            Expect = new PredicateExpect { Predicate = "questSequence(70010) >= 2" }
                        }
                    ]
                }
            ]
        };

        harness.Engine.StartQuest(quest);

        // Tick 1: first SPD
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EnterSinglePlayerDuty>(tick1);

        // Simulate first duty completion
        harness.QuestState.SetQuestSequence(new QuestId(70010), 1);

        // Tick 2-3: engine advances to sequence 1, second SPD fires
        EngineAction? secondSpd = null;
        for (var i = 0; i < 5; i++)
        {
            var action = await harness.Engine.Tick(CancellationToken.None);
            if (action is EngineAction.EnterSinglePlayerDuty espd && espd.Origin?.Id == "spd-2")
            {
                secondSpd = action;
                break;
            }
        }

        Assert.NotNull(secondSpd);
        Assert.IsType<EngineAction.EnterSinglePlayerDuty>(secondSpd);

        // Simulate second duty completion
        harness.QuestState.SetQuestSequence(new QuestId(70010), 2);

        // StartDuty was called for both SPDs
        Assert.True(harness.DutyRunner.StartDutyCallCount >= 2,
            $"Expected StartDutyCallCount >= 2 but got {harness.DutyRunner.StartDutyCallCount}");
    }

    // =========================================================================
    // S10b -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task SpdStep_CancelledToken_ThrowsOperationCanceledException_S10b()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70011), 0);
        harness.DutyRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "enter-spd-cancel",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questSequence(70011) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70011, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "SPD Step Test Quest",
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
            Name = "SPD Step Two-Step Test Quest",
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
}
