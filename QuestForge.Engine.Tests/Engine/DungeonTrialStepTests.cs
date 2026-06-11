using QuestForge.Adapters.Fakes.Duty;
using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED tests for DungeonTrialStep engine dispatch.
/// Spec: docs/DUTY_STEP_SPLIT_PLAN.md -- Section 7, scenarios DT_T1 through DT_T11.
///
/// All tests reference DungeonTrialStep which does NOT yet exist (RED).
/// These tests will FAIL TO COMPILE until the builder implements:
///   - DungeonTrialStep sealed class in Step.cs
///   - [JsonDerivedType(typeof(DungeonTrialStep), "dungeon-trial")] on Step
///   - ResolveDungeonTrial(DungeonTrialStep, CancellationToken) in QuestEngine
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~DungeonTrialStepTests"
/// </summary>
public sealed class DungeonTrialStepTests
{
    // =========================================================================
    // DT_T1 -- Happy path: CFC ID present, all checks pass, emits EnterDuty
    // =========================================================================

    /// <summary>
    /// DT_T1: Given DungeonTrialStep with CFC=2, DutyRunner available, CFC resolves,
    /// path exists; When Engine.Tick(); Then EnterDuty with CFC=2.
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_HappyPath_EmitsEnterDuty_DT_T1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90001), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 20f));
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon",
            ContentFinderConditionId = 2,                              // RED: property may differ
            Expect = new PredicateExpect { Predicate = "questSequence(90001) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var enterDuty = Assert.IsType<EngineAction.EnterDuty>(action);
        Assert.Equal(2u, enterDuty.ContentFinderConditionId);
        Assert.NotNull(enterDuty.Origin);
        Assert.Equal("enter-dungeon", enterDuty.Origin!.Id);

        Assert.Equal(1, harness.DutyRunner.StartDutyCallCount);
        Assert.Equal(167u, harness.DutyRunner.LastStartedTerritoryType);
    }

    // =========================================================================
    // DT_T2 -- AutoDuty not available -- AwaitUser
    // =========================================================================

    /// <summary>
    /// DT_T2: Given DungeonTrialStep with CFC=2, DutyRunner.AutoDutyAvailable=false;
    /// When Engine.Tick(); Then AwaitUser containing "AutoDuty".
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_AutoDutyNotAvailable_EmitsAwaitUser_DT_T2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90002), 0);
        harness.DutyRunner.AutoDutyAvailable = false;

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-unavail",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(90002) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("AutoDuty", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // DT_T3 -- IDutyRunner not wired (null) -- AwaitUser
    // =========================================================================

    /// <summary>
    /// DT_T3: Given QuestEngine with dutyRunner: null, DungeonTrialStep with CFC=2;
    /// When Engine.Tick(); Then AwaitUser containing "IDutyRunner".
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_NoDutyRunnerWired_EmitsAwaitUser_DT_T3()
    {
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(90003), 0);

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
            dutyRunner: null,                                          // null -- no IDutyRunner
            cfcResolver: new FakeCfcResolver());

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-no-adapter",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(90003) >= 3" }
        };

        engine.StartQuest(BuildSingleStepQuest(90003, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("IDutyRunner", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // DT_T4 -- ICfcResolver not wired (null) -- AwaitUser
    // =========================================================================

    /// <summary>
    /// DT_T4: Given QuestEngine with cfcResolver: null, DungeonTrialStep with CFC=2,
    /// DutyRunner available; When Engine.Tick(); Then AwaitUser containing "ICfcResolver".
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_NoCfcResolverWired_EmitsAwaitUser_DT_T4()
    {
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(90004), 0);

        var dutyRunner = new FakeDutyRunner();
        dutyRunner.AutoDutyAvailable = true;

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
            dutyRunner: dutyRunner,
            cfcResolver: null);                                        // null -- no ICfcResolver

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-no-cfcresolver",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(90004) >= 3" }
        };

        engine.StartQuest(BuildSingleStepQuest(90004, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("ICfcResolver", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // DT_T5 -- Unknown CFC ID (resolver returns null) -- AwaitUser
    // =========================================================================

    /// <summary>
    /// DT_T5: Given DungeonTrialStep with CFC=9999 not registered, DutyRunner available;
    /// When Engine.Tick(); Then AwaitUser containing "could not be resolved to a territory type".
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_UnknownCfcId_EmitsAwaitUser_DT_T5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90005), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        // Do NOT register CFC 9999

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-unknown-cfc",
            ContentFinderConditionId = 9999,
            Expect = new PredicateExpect { Predicate = "questSequence(90005) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90005, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("could not be resolved to a territory type", awaitUser.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // DT_T6 -- ContentHasPath false -- AwaitUser
    // =========================================================================

    /// <summary>
    /// DT_T6: Given DungeonTrialStep with CFC=2, CFC resolves to 167,
    /// but DutyRunner.SetContentHasPath(167, false); When Engine.Tick();
    /// Then AwaitUser containing "does not have a path for territory".
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_ContentHasPathFalse_EmitsAwaitUser_DT_T6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90006), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, false);

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-no-path",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(90006) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90006, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("does not have a path for territory", awaitUser.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // DT_T7 -- ContentFinderConditionId == 0 -- AwaitUser (runtime guard)
    // =========================================================================

    /// <summary>
    /// DT_T7: Given DungeonTrialStep with CFC=0, DutyRunner available;
    /// When Engine.Tick(); Then AwaitUser containing "ContentFinderConditionId == 0".
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_CfcIdZero_EmitsAwaitUser_DT_T7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90007), 0);
        harness.DutyRunner.AutoDutyAvailable = true;

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-zero-cfc",
            ContentFinderConditionId = 0,
            Expect = new PredicateExpect { Predicate = "questSequence(90007) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90007, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("ContentFinderConditionId == 0", awaitUser.Reason);
    }

    // =========================================================================
    // DT_T8 -- Expect already satisfied -- step skipped
    // =========================================================================

    /// <summary>
    /// DT_T8: Given QuestSequence for quest 90008 is already 3 (Expect true),
    /// DungeonTrialStep with Expect="questSequence(90008) >= 3";
    /// When Engine.Tick(); Then step is skipped, DutyRunner.StartDutyCallCount is 0.
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_ExpectAlreadySatisfied_StepSkipped_DT_T8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90008), 3);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-skip",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(90008) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect is already true -> cursor walk confirms step -> Wait (no more steps)
        Assert.IsNotType<EngineAction.EnterDuty>(action);
        Assert.Equal(0, harness.DutyRunner.StartDutyCallCount);
    }

    // =========================================================================
    // DT_T9 -- Retry: re-dispatches EnterDuty until Expect satisfied
    // =========================================================================

    /// <summary>
    /// DT_T9: Given DungeonTrialStep with Expect="questSequence(90009) >= 3",
    /// all duty prerequisites met; When tick 3 times (Expect stays false),
    /// then set QuestSequence to 3, then tick again; Then first 3 ticks: EnterDuty,
    /// after sequence = 3: next tick is NOT EnterDuty, StartDutyCallCount >= 3.
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_Retry_ReDispatchesUntilExpectSatisfied_DT_T9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90009), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-retry",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(90009) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90009, 0, step));

        // Tick 1-3: EnterDuty each time
        for (var i = 0; i < 3; i++)
        {
            var action = await harness.Engine.Tick(CancellationToken.None);
            Assert.IsType<EngineAction.EnterDuty>(action);
        }

        // Simulate duty completion
        harness.QuestState.SetQuestSequence(new QuestId(90009), 3);

        // Next tick: Expect satisfied, step confirmed
        var finalAction = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsNotType<EngineAction.EnterDuty>(finalAction);

        Assert.True(harness.DutyRunner.StartDutyCallCount >= 3,
            $"Expected StartDutyCallCount >= 3 but got {harness.DutyRunner.StartDutyCallCount}");
    }

    // =========================================================================
    // DT_T10 -- Lazy dismount fires (EnterDuty is NOT exempt)
    // =========================================================================

    /// <summary>
    /// DT_T10: Given player is mounted, TravelStep (Expect satisfied) precedes
    /// DungeonTrialStep, last action was Navigate; When Engine.Tick() returns EnterDuty;
    /// Then Mount.DismountCallCount >= 1.
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_AfterNavigate_Mounted_DismountFires_DT_T10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90010), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var quest = BuildTwoStepQuest(
            questId: 90010,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-duty",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new DungeonTrialStep                                // RED: type does not exist
            {
                Id = "enter-dungeon-after-navigate",
                ContentFinderConditionId = 2,
                Expect = new PredicateExpect { Predicate = "questSequence(90010) >= 3" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: lazy-dismount fires because EnterDuty is NOT exempt
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but got {harness.Mount.DismountCallCount}. " +
            "EnterDuty is NOT exempt from lazy-dismount (Decision DS13).");
    }

    // =========================================================================
    // DT_T11 -- Cancellation propagates
    // =========================================================================

    /// <summary>
    /// DT_T11: Given DungeonTrialStep with all prerequisites met,
    /// CancellationToken is already cancelled; When Engine.Tick(cancelledToken);
    /// Then OperationCanceledException is thrown.
    /// </summary>
    [Fact]
    public async Task DungeonTrialStep_CancelledToken_ThrowsOperationCanceledException_DT_T11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90011), 0);
        harness.DutyRunner.AutoDutyAvailable = true;
        harness.CfcResolver.Register(2, 167);
        harness.DutyRunner.SetContentHasPath(167, true);

        var step = new DungeonTrialStep                                // RED: type does not exist
        {
            Id = "enter-dungeon-cancel",
            ContentFinderConditionId = 2,
            Expect = new PredicateExpect { Predicate = "questSequence(90011) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90011, 0, step));

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
            Name = "DungeonTrialStep Test Quest",
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
            Name = "DungeonTrialStep Two-Step Test Quest",
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
