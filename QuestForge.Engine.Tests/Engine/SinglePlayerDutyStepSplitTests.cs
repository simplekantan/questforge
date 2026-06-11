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
/// RED tests for SinglePlayerDutyStep engine dispatch (the new split step type).
/// Spec: docs/DUTY_STEP_SPLIT_PLAN.md -- Section 7, scenarios SPD_T1 through SPD_T18.
///
/// All tests reference SinglePlayerDutyStep and SpdEntryKind which do NOT yet exist (RED).
/// These tests will FAIL TO COMPILE until the builder implements:
///   - SinglePlayerDutyStep sealed class in Step.cs
///   - SpdEntryKind enum in Step.cs
///   - [JsonDerivedType(typeof(SinglePlayerDutyStep), "single-player-duty")] on Step
///   - ResolveSinglePlayerDuty(SinglePlayerDutyStep, WorldPosition?, CancellationToken) in QuestEngine
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~SinglePlayerDutyStepSplitTests"
/// </summary>
public sealed class SinglePlayerDutyStepSplitTests
{
    // =========================================================================
    // SPD_T1 -- Happy path: entry kind "talk", player close, emits EnterSinglePlayerDuty
    // =========================================================================

    /// <summary>
    /// SPD_T1: Given player at (10,0,10), SinglePlayerDutyStep with EntryKind=Talk,
    /// EntryTargetId=1045123, EntryPosition=(10,0,10), CFC=830, BossModAvailable;
    /// When Engine.Tick(); Then EnterSinglePlayerDuty with CFC=830, EntryTargetId=1045123.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_TalkEntry_HappyPath_EmitsEnterSpd_SPD_T1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91001), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-talk",
            ContentFinderConditionId = 830,                            // RED: property shape
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91001) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Equal(830u, espd.ContentFinderConditionId);
        Assert.Equal(1045123u, espd.EntryTargetId);
        Assert.NotNull(espd.EntryPosition);
        Assert.Equal(10f, espd.EntryPosition!.X, precision: 3);
        Assert.Equal(0f, espd.EntryPosition!.Y, precision: 3);
        Assert.Equal(10f, espd.EntryPosition!.Z, precision: 3);
        Assert.NotNull(espd.Origin);
        Assert.Equal("enter-spd-talk", espd.Origin!.Id);
    }

    // =========================================================================
    // SPD_T2 -- Happy path: entry kind "interact", player close
    // =========================================================================

    /// <summary>
    /// SPD_T2: Given player at (10,0,10), SinglePlayerDutyStep with EntryKind=Interact,
    /// EntryTargetId=2001234, CFC=831, BossModAvailable;
    /// When Engine.Tick(); Then EnterSinglePlayerDuty with CFC=831, EntryTargetId=2001234.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_InteractEntry_HappyPath_EmitsEnterSpd_SPD_T2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91002), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-interact",
            ContentFinderConditionId = 831,
            EntryKind = SpdEntryKind.Interact,                         // RED: enum does not exist
            EntryTargetId = 2001234u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91002) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Equal(831u, espd.ContentFinderConditionId);
        Assert.Equal(2001234u, espd.EntryTargetId);
    }

    // =========================================================================
    // SPD_T3 -- Happy path: entry kind "proximity", null EntryTargetId
    // =========================================================================

    /// <summary>
    /// SPD_T3: Given player at (10,0,10), SinglePlayerDutyStep with EntryKind=Proximity,
    /// EntryTargetId=null, CFC=832, BossModAvailable;
    /// When Engine.Tick(); Then EnterSinglePlayerDuty with CFC=832, EntryTargetId=null.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_ProximityEntry_HappyPath_EmitsEnterSpd_SPD_T3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91003), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-proximity",
            ContentFinderConditionId = 832,
            EntryKind = SpdEntryKind.Proximity,                        // RED: enum does not exist
            EntryTargetId = null,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91003) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Equal(832u, espd.ContentFinderConditionId);
        Assert.Null(espd.EntryTargetId);
    }

    // =========================================================================
    // SPD_T4 -- Player far from entry position -- Navigate emitted first
    // =========================================================================

    /// <summary>
    /// SPD_T4: Given player at (0,0,0), SinglePlayerDutyStep with EntryPosition=(100,0,100),
    /// BossModAvailable; When Engine.Tick(); Then Navigate to ~(100,0,100).
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_PlayerFar_EmitsNavigate_SPD_T4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91004), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-far",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(100f, 0f, 100f),
            Expect = new PredicateExpect { Predicate = "questSequence(91004) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91004, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var nav = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(100f, nav.Destination.X, precision: 1);
        Assert.Equal(0f, nav.Destination.Y, precision: 1);
        Assert.Equal(100f, nav.Destination.Z, precision: 1);
    }

    // =========================================================================
    // SPD_T5 -- Player close with custom StopDistance honored
    // =========================================================================

    /// <summary>
    /// SPD_T5: Given player at (10,0,10), SinglePlayerDutyStep with EntryPosition=(14,0,10),
    /// StopDistance=5.0 (distance is 4.0, within StopDistance), BossModAvailable;
    /// When Engine.Tick(); Then EnterSinglePlayerDuty (NOT Navigate).
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_CustomStopDistance_Honored_SPD_T5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91005), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-close",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(14f, 0f, 10f),
            StopDistance = 5.0f,
            Expect = new PredicateExpect { Predicate = "questSequence(91005) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91005, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
    }

    // =========================================================================
    // SPD_T6 -- Player casting -- Wait
    // =========================================================================

    /// <summary>
    /// SPD_T6: Given player at (10,0,10) (close to entry), player is casting,
    /// BossModAvailable; When Engine.Tick(); Then Wait containing "casting".
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_PlayerCasting_EmitsWait_SPD_T6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91006), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.GameState.SetCasting(true);
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-casting",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91006) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91006, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // SPD_T7 -- Action cooldown active -- Wait
    // =========================================================================

    /// <summary>
    /// SPD_T7: Given player close to entry, not casting, action cooldown not elapsed,
    /// BossModAvailable; When Engine.Tick(); Then Wait containing "cooldown".
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_CooldownActive_EmitsWait_SPD_T7()
    {
        // Create a harness with a non-zero cooldown to trigger the guard.
        // The default harness uses actionCooldownSeconds: 0, so we need a custom engine.
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(91007), 0);
        gameState.SetZone(new ZoneId(132));
        gameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var questBattleRunner = new FakeQuestBattleRunner();
        questBattleRunner.BossModAvailable = true;

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
            questBattleRunner: questBattleRunner,
            actionCooldownSeconds: 60);  // 60s cooldown -- will always be active

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-cooldown",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91007) >= 3" }
        };

        engine.StartQuest(BuildSingleStepQuest(91007, 0, step));

        // First tick fires the action (cooldown starts)
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EnterSinglePlayerDuty>(tick1);

        // Second tick should hit cooldown
        var tick2 = await engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.Contains("cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // SPD_T8 -- BossMod not available -- AwaitUser
    // =========================================================================

    /// <summary>
    /// SPD_T8: Given SinglePlayerDutyStep with all fields populated,
    /// QuestBattleRunner.BossModAvailable=false;
    /// When Engine.Tick(); Then AwaitUser containing "BossMod".
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_BossModNotAvailable_EmitsAwaitUser_SPD_T8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91008), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = false;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-no-bossmod",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91008) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("BossMod", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // SPD_T9 -- IQuestBattleRunner not wired (null) -- AwaitUser
    // =========================================================================

    /// <summary>
    /// SPD_T9: Given QuestEngine with questBattleRunner: null,
    /// SinglePlayerDutyStep with all fields populated;
    /// When Engine.Tick(); Then AwaitUser containing "IQuestBattleRunner".
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_NoQuestBattleRunnerWired_EmitsAwaitUser_SPD_T9()
    {
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(91009), 0);
        gameState.SetZone(new ZoneId(132));
        gameState.SetPosition(new WorldPosition(10f, 0f, 10f));

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
            questBattleRunner: null);                                  // null -- no IQuestBattleRunner

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-no-adapter",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91009) >= 3" }
        };

        engine.StartQuest(BuildSingleStepQuest(91009, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("IQuestBattleRunner", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // SPD_T10 -- Expect already satisfied -- step skipped
    // =========================================================================

    /// <summary>
    /// SPD_T10: Given QuestSequence for quest 91010 is 3 (Expect already true),
    /// SinglePlayerDutyStep with Expect="questSequence(91010) >= 3";
    /// When Engine.Tick(); Then action is NOT EnterSinglePlayerDuty.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_ExpectAlreadySatisfied_StepSkipped_SPD_T10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91010), 3);
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-skip",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91010) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91010, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsNotType<EngineAction.EnterSinglePlayerDuty>(action);
    }

    // =========================================================================
    // SPD_T11 -- Two-tick integration: SPD fires, Expect satisfies, step completes
    // =========================================================================

    /// <summary>
    /// SPD_T11: Given player close to entry, SinglePlayerDutyStep with
    /// Expect="questSequence(91011) >= 3", BossModAvailable;
    /// When tick 1 emits EnterSinglePlayerDuty, between ticks set QuestSequence to 3,
    /// tick 2 Expect satisfied; Then tick 1 returns EnterSinglePlayerDuty,
    /// tick 2 returns action for next step (not EnterSinglePlayerDuty).
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_TwoTickIntegration_SPD_T11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91011), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-two-tick",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91011) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91011, 0, step));

        // Tick 1: EnterSinglePlayerDuty
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EnterSinglePlayerDuty>(tick1);

        // Simulate duty completion
        harness.QuestState.SetQuestSequence(new QuestId(91011), 3);

        // Tick 2: Expect satisfied, step completes
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsNotType<EngineAction.EnterSinglePlayerDuty>(tick2);
    }

    // =========================================================================
    // SPD_T12 -- Retry: re-dispatches with same entry fields across multiple ticks
    // =========================================================================

    /// <summary>
    /// SPD_T12: Given player close to entry, SinglePlayerDutyStep with EntryKind=Talk,
    /// EntryTargetId=5000, EntryPosition=(10,0,20), BossModAvailable;
    /// When tick 3 times (Expect stays false); Then all 3 ticks return
    /// EnterSinglePlayerDuty with identical CFC, EntryTargetId, EntryPosition.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_Retry_SameEntryFields_SPD_T12()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91012), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 20f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-retry",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 5000u,
            EntryPosition = new Position3(10f, 0f, 20f),
            Expect = new PredicateExpect { Predicate = "questSequence(91012) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91012, 0, step));

        var retryActions = new List<EngineAction.EnterSinglePlayerDuty>();
        for (var i = 0; i < 3; i++)
        {
            var action = await harness.Engine.Tick(CancellationToken.None);
            retryActions.Add(Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action));
        }

        // All 3 retries carry the same entry fields
        foreach (var espd in retryActions)
        {
            Assert.Equal(830u, espd.ContentFinderConditionId);
            Assert.Equal(5000u, espd.EntryTargetId);
            Assert.NotNull(espd.EntryPosition);
            Assert.Equal(10f, espd.EntryPosition!.X, precision: 3);
            Assert.Equal(0f, espd.EntryPosition!.Y, precision: 3);
            Assert.Equal(20f, espd.EntryPosition!.Z, precision: 3);
        }
    }

    // =========================================================================
    // SPD_T13 -- Retry after respawn: Navigate emitted to EntryPosition
    // =========================================================================

    /// <summary>
    /// SPD_T13: Given player at (200,0,200) (far from entry -- simulating respawn),
    /// SinglePlayerDutyStep with EntryPosition=(10,0,20), BossModAvailable,
    /// Expect NOT satisfied; When Engine.Tick(); Then Navigate to ~(10,0,20).
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_RetryAfterRespawn_NavigatesToEntry_SPD_T13()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91013), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(200f, 0f, 200f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-respawn",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 20f),
            Expect = new PredicateExpect { Predicate = "questSequence(91013) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91013, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var nav = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(10f, nav.Destination.X, precision: 1);
        Assert.Equal(0f, nav.Destination.Y, precision: 1);
        Assert.Equal(20f, nav.Destination.Z, precision: 1);
    }

    // =========================================================================
    // SPD_T14 -- Null playerPos -- fail-open, emits EnterSinglePlayerDuty
    // =========================================================================

    /// <summary>
    /// SPD_T14: Given player position is null (game state read failure),
    /// SinglePlayerDutyStep with all fields populated, BossModAvailable;
    /// When Engine.Tick(); Then EnterSinglePlayerDuty (not Navigate).
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_NullPlayerPos_FailOpen_EmitsEnterSpd_SPD_T14()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91014), 0);
        harness.GameState.SetZone(new ZoneId(132));
        // Force position read to fail -- simulates game state read failure
        harness.GameState.SetPositionFailure("test", "simulated position read failure");
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-null-pos",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(100f, 0f, 100f),
            Expect = new PredicateExpect { Predicate = "questSequence(91014) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91014, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Fail-open: skip navigation guard, emit action directly
        Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
    }

    // =========================================================================
    // SPD_T15 -- Cancellation propagates
    // =========================================================================

    /// <summary>
    /// SPD_T15: Given SinglePlayerDutyStep with all prerequisites met,
    /// CancellationToken is already cancelled; When Engine.Tick(cancelledToken);
    /// Then OperationCanceledException is thrown.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_CancelledToken_ThrowsOperationCanceledException_SPD_T15()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91015), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-cancel",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91015) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91015, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // SPD_T16 -- Lazy dismount fires after Navigate
    // =========================================================================

    /// <summary>
    /// SPD_T16: Given player is mounted, TravelStep precedes SinglePlayerDutyStep,
    /// player far from entry (first tick: Navigate from TravelStep), TravelStep Expect
    /// satisfies, then SinglePlayerDutyStep fires; When engine transitions from
    /// Navigate to EnterSinglePlayerDuty; Then Mount.DismountCallCount >= 1.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_AfterNavigate_Mounted_DismountFires_SPD_T16()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91016), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.QuestBattleRunner.BossModAvailable = true;

        var quest = BuildTwoStepQuest(
            questId: 91016,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-spd",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new SinglePlayerDutyStep                            // RED: type does not exist
            {
                Id = "enter-spd-after-navigate",
                ContentFinderConditionId = 830,
                EntryKind = SpdEntryKind.Talk,                         // RED: enum does not exist
                EntryTargetId = 1045123u,
                EntryPosition = new Position3(0f, 0f, 0f),
                Expect = new PredicateExpect { Predicate = "questSequence(91016) >= 3" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: lazy-dismount fires because EnterSinglePlayerDuty is NOT exempt
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but got {harness.Mount.DismountCallCount}. " +
            "EnterSinglePlayerDuty is NOT exempt from lazy-dismount (Decision DS13).");
    }

    // =========================================================================
    // SPD_T17 -- ContentFinderConditionId value carried through to action
    // =========================================================================

    /// <summary>
    /// SPD_T17: Given SinglePlayerDutyStep with CFC=999, BossModAvailable,
    /// player close to entry; When Engine.Tick();
    /// Then EnterSinglePlayerDuty.ContentFinderConditionId is 999.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_CfcCarriedThrough_SPD_T17()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91017), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-cfc-pass",
            ContentFinderConditionId = 999,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 10f),
            Expect = new PredicateExpect { Predicate = "questSequence(91017) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91017, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Equal(999u, espd.ContentFinderConditionId);
    }

    // =========================================================================
    // SPD_T18 -- Navigate uses default StopDistance (3.0) when not overridden
    // =========================================================================

    /// <summary>
    /// SPD_T18: Given player at (0,0,0), SinglePlayerDutyStep with EntryPosition=(10,0,0),
    /// StopDistance=null (not set), distance=10 (> default 3.0), BossModAvailable;
    /// When Engine.Tick(); Then Navigate, Options.StoppingDistance is 3.0f.
    /// </summary>
    [Fact]
    public async Task SinglePlayerDutyStep_DefaultStopDistance_SPD_T18()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(91018), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new SinglePlayerDutyStep                            // RED: type does not exist
        {
            Id = "enter-spd-default-stop",
            ContentFinderConditionId = 830,
            EntryKind = SpdEntryKind.Talk,                             // RED: enum does not exist
            EntryTargetId = 1045123u,
            EntryPosition = new Position3(10f, 0f, 0f),
            // StopDistance intentionally NOT set (null / default)
            Expect = new PredicateExpect { Predicate = "questSequence(91018) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(91018, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var nav = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(3.0f, nav.Options.StoppingDistance, precision: 2);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "SinglePlayerDutyStep Test Quest",
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
            Name = "SinglePlayerDutyStep Two-Step Test Quest",
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
