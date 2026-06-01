using QuestForge.Adapters.Fakes.Duty;
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
/// Failing tests for QuestEngine dispatch of DutyStep(kind: "duty") via AutoDuty.
/// Spec: docs/DUTY_AUTODUTY_PLAN.md -- Decisions AD1-AD15, scenarios D1-D13.
///
/// All types referenced below are NEW and do not yet exist:
///   - EngineAction.EnterDuty(uint ContentFinderConditionId, Step? Origin)     (TODO)
///   - IDutyRunner interface                                                    (TODO)
///   - FakeDutyRunner fake                                                      (TODO)
///   - ICfcResolver interface                                                   (TODO)
///   - FakeCfcResolver fake                                                     (TODO)
///   - QuestEngine ctor params dutyRunner: IDutyRunner?, cfcResolver: ICfcResolver? (TODO)
///   - EngineTestHarness.DutyRunner property                                    (TODO)
///   - EngineTestHarness.CfcResolver property                                   (TODO)
///   - EngineTestHarness RunToCompletion arm for EnterDuty                       (TODO)
///   - DutyStep.ContentFinderConditionId field                                  (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DungeonTrialDutyTests"
/// </summary>
public sealed class DungeonTrialDutyTests
{
    // =========================================================================
    // D1 -- Happy path: kind "duty", CFC ID present, all checks pass -> EnterDuty
    // =========================================================================

    [Fact]
    public async Task DutyStep_HappyPath_EmitsEnterDuty_D1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80001), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 20f));
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.DutyRunner.SetContentHasPath(167, true);
        harness.CfcResolver.Register(2, 167);

        // Wire: on Interact for talk-to-npc, advance quest to seq 2
        harness.Interactor.OnInteractWith(new NpcId(5000u), () =>
            harness.QuestState.SetQuestSequence(new QuestId(80001), 2));

        // Track EnterDuty dispatch count to advance quest on first EnterDuty
        var enterDutyCount = 0;

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 80001,
            Name = "Dungeon Happy Path Test",
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
                            Expect = new PredicateExpect { Predicate = "questSequence(80001) >= 2" }
                        },
                        new DutyStep
                        {
                            Id = "enter-dungeon",
                            Kind = "duty",
                            ContentFinderConditionId = 2,
                            Expect = new PredicateExpect { Predicate = "questSequence(80001) >= 3" }
                        },
                        new TalkStep
                        {
                            Id = "talk-after-dungeon",
                            Target = new NpcLocation(5001u, 132, new Position3(30f, 0f, 40f)),
                            Expect = new PredicateExpect { Predicate = "questSequence(80001) >= 4" }
                        }
                    ]
                }
            ]
        };

        harness.Engine.StartQuest(quest);

        // Tick 1: Interact for the TalkStep (talk-to-npc)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Interact>(tick1);

        // Now quest is at seq 2 (TalkStep Expect satisfied). DutyStep should fire.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        var enterDuty = Assert.IsType<EngineAction.EnterDuty>(tick2);
        Assert.Equal(2u, enterDuty.ContentFinderConditionId);
        Assert.NotNull(enterDuty.Origin);
        Assert.Equal("enter-dungeon", enterDuty.Origin!.Id);

        // Simulate duty completion: advance quest sequence
        harness.QuestState.SetQuestSequence(new QuestId(80001), 3);

        // Next tick: DutyStep confirmed, talk-after-dungeon step fires
        var tick3 = await harness.Engine.Tick(CancellationToken.None);

        // Verify the adapter was called with the correct territory type
        Assert.Equal(1, harness.DutyRunner.StartDutyCallCount);
        Assert.Equal(167u, harness.DutyRunner.LastStartedTerritoryType);
    }

    // =========================================================================
    // D2 -- AutoDuty not available -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task DutyStep_AutoDutyNotAvailable_EmitsAwaitUser_D2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80002), 0);
        harness.DutyRunner.AutoDutyAvailable = false;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DutyStep
        {
            Id = "enter-dungeon-no-autoduty",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80002) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("AutoDuty", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D3 -- IDutyRunner not wired (null) -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task DutyStep_NoDutyRunnerWired_EmitsAwaitUser_D3()
    {
        // Construct a QuestEngine WITHOUT dutyRunner (null).
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(80003), 0);

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
            questBattleRunner: new FakeQuestBattleRunner(),
            dutyRunner: null,
            cfcResolver: new FakeCfcResolver());

        var step = new DutyStep
        {
            Id = "enter-dungeon-no-adapter",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80003) >= 3" }
        };

        engine.StartQuest(BuildSingleStepQuest(80003, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("IDutyRunner", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D4 -- ICfcResolver not wired (null) -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task DutyStep_NoCfcResolverWired_EmitsAwaitUser_D4()
    {
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(80004), 0);

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
            questBattleRunner: new FakeQuestBattleRunner(),
            dutyRunner: new FakeDutyRunner(),
            cfcResolver: null);

        var step = new DutyStep
        {
            Id = "enter-dungeon-no-cfcresolver",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80004) >= 3" }
        };

        engine.StartQuest(BuildSingleStepQuest(80004, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("ICfcResolver", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D5 -- Unknown CFC ID (resolver returns null) -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task DutyStep_UnknownCfcId_EmitsAwaitUser_D5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80005), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        // Do NOT register CFC 9999 -- resolver returns null

        var step = new DutyStep
        {
            Id = "enter-dungeon-unknown-cfc",
            Kind = "duty",
            ContentFinderConditionId = 9999,
            Expect = new PredicateExpect { Predicate = "questSequence(80005) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80005, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("could not be resolved to a territory type", awaitUser.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D6 -- ContentHasPath false -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task DutyStep_ContentHasPathFalse_EmitsAwaitUser_D6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80006), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, false); // no path

        var step = new DutyStep
        {
            Id = "enter-dungeon-no-path",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80006) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80006, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("does not have a path for territory", awaitUser.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D7 -- ContentFinderConditionId null on kind "duty" -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task DutyStep_CfcIdNull_EmitsAwaitUser_D7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80007), 0);
        harness.DutyRunner.AutoDutyAvailable = true;

        var step = new DutyStep
        {
            Id = "enter-dungeon-null-cfc",
            Kind = "duty",
            ContentFinderConditionId = null,
            Expect = new PredicateExpect { Predicate = "questSequence(80007) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80007, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("has no ContentFinderConditionId", awaitUser.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D8 -- ContentFinderConditionId is 0 on kind "duty" -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task DutyStep_CfcIdZero_EmitsAwaitUser_D8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80008), 0);
        harness.DutyRunner.AutoDutyAvailable = true;

        var step = new DutyStep
        {
            Id = "enter-dungeon-zero-cfc",
            Kind = "duty",
            ContentFinderConditionId = 0,
            Expect = new PredicateExpect { Predicate = "questSequence(80008) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("has no ContentFinderConditionId", awaitUser.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // D9 -- Expect already satisfied -- step skipped, no EnterDuty emitted
    // =========================================================================

    [Fact]
    public async Task DutyStep_ExpectAlreadySatisfied_StepSkipped_D9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80009), 3);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DutyStep
        {
            Id = "enter-dungeon-skip",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80009) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80009, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect is already true -> cursor walk confirms step -> Wait (no more steps)
        Assert.IsType<EngineAction.Wait>(action);
        // DutyRunner never called
        Assert.Equal(0, harness.DutyRunner.StartDutyCallCount);
    }

    // =========================================================================
    // D10 -- Retry: Expect stays false, re-dispatches EnterDuty until satisfied
    // =========================================================================

    [Fact]
    public async Task DutyStep_Retry_ReDispatchesUntilExpectSatisfied_D10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80010), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DutyStep
        {
            Id = "enter-dungeon-retry",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80010) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80010, 0, step));

        // Tick 1-3: EnterDuty each time (duty fails / expect stays false)
        for (var i = 0; i < 3; i++)
        {
            var action = await harness.Engine.Tick(CancellationToken.None);
            Assert.IsType<EngineAction.EnterDuty>(action);
        }

        // Simulate duty completion: quest sequence advances
        harness.QuestState.SetQuestSequence(new QuestId(80010), 3);

        // Next tick: Expect satisfied, step confirmed
        var finalAction = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(finalAction);

        // StartDuty was called multiple times (idempotent calls from harness dispatch)
        Assert.True(harness.DutyRunner.StartDutyCallCount >= 3,
            $"Expected StartDutyCallCount >= 3 but got {harness.DutyRunner.StartDutyCallCount}");
    }

    // =========================================================================
    // D11 -- EnterDuty is subject to lazy dismount (NOT exempt)
    // =========================================================================

    [Fact]
    public async Task DutyStep_AfterNavigate_Mounted_DismountFires_D11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80011), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var quest = BuildTwoStepQuest(
            questId: 80011,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-duty",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new DutyStep
            {
                Id = "enter-dungeon-after-navigate",
                Kind = "duty",
                ContentFinderConditionId = 2,
                Expect = new PredicateExpect { Predicate = "questSequence(80011) >= 3" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: lazy-dismount fires because EnterDuty is NOT exempt
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but got {harness.Mount.DismountCallCount}. " +
            "EnterDuty is NOT exempt from lazy-dismount (Decision AD11).");
    }

    // =========================================================================
    // D12 -- DutyStep kind "regular" -- still throws NotSupportedException
    // =========================================================================

    [Fact]
    public async Task DutyStep_KindRegular_StillThrowsNotSupportedException_D12()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80012), 0);

        var step = new DutyStep
        {
            Id = "enter-regular-duty",
            Kind = "regular",
            Expect = new PredicateExpect { Predicate = "questSequence(80012) >= 5" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80012, 0, step));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => harness.Engine.Tick(CancellationToken.None));
    }

    // =========================================================================
    // D13 -- Territory type passed to StartDuty matches CFC resolver output
    // =========================================================================

    [Fact]
    public async Task DutyStep_TerritoryTypeMatchesCfcResolver_D13()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80013), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DutyStep
        {
            Id = "enter-dungeon-verify-tt",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80013) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80013, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EnterDuty>(action);

        // The territory type from the CFC resolver is what reaches the adapter
        Assert.Equal(167u, harness.DutyRunner.LastStartedTerritoryType);
        Assert.Equal(1, harness.DutyRunner.StartDutyCallCount);
    }

    // =========================================================================
    // D10b -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task DutyStep_CancelledToken_ThrowsOperationCanceledException_D10b()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80014), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DutyStep
        {
            Id = "enter-dungeon-cancel",
            Kind = "duty",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(80014) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(80014, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // D1-integration -- RunToCompletion integration test
    // =========================================================================

    [Fact]
    public async Task DutyStep_RunToCompletion_HappyPath_Integration()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80015), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 20f));
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        // Wire: on Interact for talk-to-npc, advance quest to seq 2
        harness.Interactor.OnInteractWith(new NpcId(5000u), () =>
            harness.QuestState.SetQuestSequence(new QuestId(80015), 2));

        // Wire: on first EnterDuty dispatch, advance quest to seq 3
        // (RunToCompletion dispatches to DutyRunner.StartDuty, so we hook that)
        var originalStartCount = harness.DutyRunner.StartDutyCallCount;
        // Use a simpler approach: advance quest after first EnterDuty tick
        // by wiring into the fake. We can do this after the first StartDuty call.

        // Wire: after talk-after-dungeon Interact, advance quest to seq 4
        harness.Interactor.OnInteractWith(new NpcId(5001u), () =>
            harness.QuestState.SetQuestSequence(new QuestId(80015), 4));

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 80015,
            Name = "Dungeon Integration Test",
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
                            Expect = new PredicateExpect { Predicate = "questSequence(80015) >= 2" }
                        },
                        new DutyStep
                        {
                            Id = "enter-dungeon",
                            Kind = "duty",
                            ContentFinderConditionId = 2,
                            Expect = new PredicateExpect { Predicate = "questSequence(80015) >= 3" }
                        }
                    ]
                }
            ]
        };

        harness.Engine.StartQuest(quest);

        // Tick through talk step
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Interact>(tick1);

        // DutyStep fires; simulate completion after first dispatch
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EnterDuty>(tick2);

        // Simulate duty completion
        harness.QuestState.SetQuestSequence(new QuestId(80015), 3);

        // Engine confirms the step
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick3);

        Assert.True(harness.DutyRunner.StartDutyCallCount >= 1);
        Assert.Equal(167u, harness.DutyRunner.LastStartedTerritoryType);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Dungeon Duty Step Test Quest",
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
            Name = "Dungeon Duty Two-Step Test Quest",
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
