using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Navigation;
using QuestForge.Schema;
using AdapterAetheryteId = QuestForge.Adapters.Types.AetheryteId;
using Xunit;

namespace QuestForge.Engine.Tests.Navigation;

/// <summary>
/// RED PHASE: Engine integration tests for navigation stuck detection (NE1--NE8, NE5b, NE5c, NE7b).
///
/// All tests fail to compile until Builder creates:
///   - NavigationWatchdog + WatchdogAdvice (Phase A)
///   - EngineAction.Jump + EngineAction.UseReturn (Phase B)
///   - QuestEngine watchdog consultation in Tick() (Phase B)
///   - MapRecoverAction UseReturn/UseTeleport arms (Phase B)
///   - QuestEngine new optional params: navStallTimeoutSeconds, navStallDistanceThreshold, navMaxJumpAttempts
///
/// These tests use Engine.Tick() directly (not RunToCompletion) because stuck detection
/// requires controlling player position between ticks without the harness dispatching
/// Navigate (which would move the player via FakeNavigator).
///
/// Spec: docs/NAV_STUCK_DETECTION_SPEC.md section 3.2
/// </summary>
public sealed class NavStuckEngineTests
{
    // Stable epoch for all tests.
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // ManualTimeProvider for deterministic time control
    // -------------------------------------------------------------------------

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTimeOffset startTime) => _now = startTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    // -------------------------------------------------------------------------
    // Engine construction helper -- constructs QuestEngine directly with clock
    // and nav stuck parameters. Returns all fakes needed for test control.
    // -------------------------------------------------------------------------

    private sealed class EngineKit
    {
        public QuestEngine Engine { get; init; } = null!;
        public FakeGameStateProvider GameState { get; init; } = null!;
        public FakeQuestState QuestState { get; init; } = null!;
        public FakeNavigator Navigator { get; init; } = null!;
        public FakeTeleporter Teleporter { get; init; } = null!;
        public FakeCombat Combat { get; init; } = null!;
        public ManualTimeProvider Clock { get; init; } = null!;
    }

    /// <summary>
    /// Builds a QuestEngine with FakeTimeProvider and configurable stuck detection params.
    /// RED: navStallTimeoutSeconds, navStallDistanceThreshold, navMaxJumpAttempts do not
    /// exist on QuestEngine constructor until Builder adds them.
    /// </summary>
    private static EngineKit BuildEngine(
        double navStallTimeoutSeconds = 5.0,
        float navStallDistanceThreshold = 2.0f,
        int navMaxJumpAttempts = 3)
    {
        var clock = new ManualTimeProvider(T0);
        var gameState = new FakeGameStateProvider();
        var questState = new FakeQuestState();
        var navigator = new FakeNavigator(gameState);
        var teleporter = new FakeTeleporter(gameState);
        var interactor = new FakeInteractor(gameState, questState);
        var combat = new FakeCombat();
        var minigames = new FakeMinigameSkipper();
        var dialogue = new FakeDialogueResolver();
        var timing = new FakeTimingProfile();
        var trace = new NullTraceWriter();
        var logger = NullLogger<QuestEngine>.Instance;

        var engine = new QuestEngine(
            gameState, questState, navigator, teleporter, interactor,
            combat, minigames, dialogue, timing, trace, logger,
            clock,
            actionCooldownSeconds: 0,
            navStallTimeoutSeconds: navStallTimeoutSeconds,
            navStallDistanceThreshold: navStallDistanceThreshold,
            navMaxJumpAttempts: navMaxJumpAttempts);

        return new EngineKit
        {
            Engine = engine,
            GameState = gameState,
            QuestState = questState,
            Navigator = navigator,
            Teleporter = teleporter,
            Combat = combat,
            Clock = clock
        };
    }

    private static QuestDefinition BuildQuest(uint questId, int sequence, params Step[] steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "NavStuck Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = sequence, Steps = steps }
            ]
        };

    /// <summary>
    /// Ticks the engine for the specified number of ticks, advancing the clock between each.
    /// Does NOT dispatch actions (does not call Navigator/Teleporter). This simulates a
    /// stuck player whose position does not change between ticks.
    /// Returns all emitted actions.
    /// </summary>
    private static async Task<List<EngineAction>> TickWithoutDispatch(
        QuestEngine engine, ManualTimeProvider clock, int tickCount, TimeSpan tickInterval)
    {
        var actions = new List<EngineAction>();
        for (int i = 0; i < tickCount; i++)
        {
            var action = await engine.Tick(CancellationToken.None);
            actions.Add(action);
            clock.Advance(tickInterval);
        }
        return actions;
    }

    // =========================================================================
    // NE1 -- TravelStep emits Navigate, player stuck 5s, engine emits Jump
    // =========================================================================

    [Fact]
    public async Task NE1_TravelStep_PlayerStuck5s_EmitsJump()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0);
        var questId = new QuestId(70001u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TravelStep
        {
            Id = "travel-far",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70001, 3)" }
        };
        var quest = BuildQuest(70001u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick for slightly over 5s at 1s intervals (player stays at origin)
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 7, TimeSpan.FromSeconds(1));

        // At least one action should be Jump (after cumulative time >= 5s)
        Assert.Contains(actions, a => a is EngineAction.Jump);

        // Verify earlier actions were Navigate (before stuck fires)
        var firstAction = actions[0];
        Assert.IsType<EngineAction.Navigate>(firstAction);
    }

    // =========================================================================
    // NE2 -- Player stuck, 3 jumps exhausted, engine emits UseReturn
    // =========================================================================

    [Fact]
    public async Task NE2_ThreeJumpsExhausted_EmitsUseReturn()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0, navMaxJumpAttempts: 3);
        var questId = new QuestId(70002u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TravelStep
        {
            Id = "travel-stuck",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70002, 3)" }
        };
        var quest = BuildQuest(70002u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick for ~25s (enough for 3 jumps + final escalation): 25 ticks at 1s
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 25, TimeSpan.FromSeconds(1));

        // Should have Jump actions followed by UseReturn
        var jumpCount = actions.Count(a => a is EngineAction.Jump);
        Assert.Equal(3, jumpCount);
        Assert.Contains(actions, a => a is EngineAction.UseReturn);
    }

    // =========================================================================
    // NE3 -- Jump succeeds (player moves), engine resumes Navigate
    // =========================================================================

    [Fact]
    public async Task NE3_JumpSucceeds_PlayerMoves_ResumesNavigate()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0);
        var questId = new QuestId(70003u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TravelStep
        {
            Id = "travel-unstick",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70003, 3)" }
        };
        var quest = BuildQuest(70003u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick until Jump is emitted
        EngineAction? jumpAction = null;
        for (int i = 0; i < 10; i++)
        {
            var action = await kit.Engine.Tick(CancellationToken.None);
            kit.Clock.Advance(TimeSpan.FromSeconds(1));
            if (action is EngineAction.Jump)
            {
                jumpAction = action;
                break;
            }
        }
        Assert.NotNull(jumpAction);

        // Simulate jump success: move player 10m
        kit.GameState.SetPosition(new WorldPosition(10f, 0f, 0f));
        kit.Clock.Advance(TimeSpan.FromSeconds(1));

        // Next tick: engine should resume Navigate (not Jump)
        var afterJump = await kit.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(afterJump);
    }

    // =========================================================================
    // NE4 -- Step confirms (Expect met) while stuck, watchdog resets
    // =========================================================================

    [Fact]
    public async Task NE4_StepConfirmsWhileStuck_WatchdogResets_NoJump()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0);
        var questId = new QuestId(70004u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TravelStep
        {
            Id = "travel-confirm",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70004, 3)" }
        };
        var quest = BuildQuest(70004u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick for 3s (building stall, not yet triggered)
        for (int i = 0; i < 3; i++)
        {
            var action = await kit.Engine.Tick(CancellationToken.None);
            Assert.IsNotType<EngineAction.Jump>(action);
            kit.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Set quest flag so Expect is satisfied
        kit.QuestState.SetQuestFlagBit(questId, 3, true);

        // Next tick: step confirms, engine moves on (Wait or Done)
        var confirmedAction = await kit.Engine.Tick(CancellationToken.None);
        Assert.IsNotType<EngineAction.Jump>(confirmedAction);
        Assert.IsNotType<EngineAction.UseReturn>(confirmedAction);
    }

    // =========================================================================
    // NE5 -- Step has Recover.OnObstacle = awaitUser("stuck"), engine emits AwaitUser
    // =========================================================================

    [Fact]
    public async Task NE5_OnObstacleAwaitUser_EmitsAwaitUser()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0, navMaxJumpAttempts: 3);
        var questId = new QuestId(70005u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TravelStep
        {
            Id = "travel-obstacle-await",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70005, 3)" },
            Recover = new RecoverConfig
            {
                OnObstacle = new AwaitUserRecoverAction { Reason = "stuck" }
            }
        };
        var quest = BuildQuest(70005u, 0, step);
        kit.Engine.StartQuest(quest);

        // Exhaust 3 jumps, then expect AwaitUser (not UseReturn)
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 25, TimeSpan.FromSeconds(1));

        var jumpCount = actions.Count(a => a is EngineAction.Jump);
        Assert.Equal(3, jumpCount);

        // After jumps exhausted, should emit AwaitUser with the configured reason
        var awaitUser = actions.OfType<EngineAction.AwaitUser>().FirstOrDefault();
        Assert.NotNull(awaitUser);
        Assert.Contains("stuck", awaitUser!.Reason);
    }

    // =========================================================================
    // NE5b -- Step has Recover.OnObstacle = useReturn, engine emits UseReturn
    // =========================================================================

    [Fact]
    public async Task NE5b_OnObstacleUseReturn_EmitsUseReturn()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0, navMaxJumpAttempts: 3);
        var questId = new QuestId(70006u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TravelStep
        {
            Id = "travel-obstacle-return",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70006, 3)" },
            Recover = new RecoverConfig
            {
                OnObstacle = new UseReturnRecoverAction()
            }
        };
        var quest = BuildQuest(70006u, 0, step);
        kit.Engine.StartQuest(quest);

        // Exhaust 3 jumps
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 25, TimeSpan.FromSeconds(1));

        Assert.Contains(actions, a => a is EngineAction.UseReturn);
    }

    // =========================================================================
    // NE5c -- Step has Recover.OnObstacle = useTeleport(aetheryteId: 8), engine emits Teleport
    // =========================================================================

    [Fact]
    public async Task NE5c_OnObstacleUseTeleport_EmitsTeleport()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0, navMaxJumpAttempts: 3);
        var questId = new QuestId(70007u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TravelStep
        {
            Id = "travel-obstacle-teleport",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70007, 3)" },
            Recover = new RecoverConfig
            {
                OnObstacle = new UseTeleportRecoverAction { AetheryteId = 8 }
            }
        };
        var quest = BuildQuest(70007u, 0, step);
        kit.Engine.StartQuest(quest);

        // Exhaust 3 jumps
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 25, TimeSpan.FromSeconds(1));

        var teleport = actions.OfType<EngineAction.Teleport>().FirstOrDefault();
        Assert.NotNull(teleport);
        Assert.Equal(new AdapterAetheryteId(8), teleport!.Destination);
    }

    // =========================================================================
    // NE6 -- CombatStep fallback-navigate-to-Location, player stuck
    // =========================================================================

    [Fact]
    public async Task NE6_CombatStepFallbackNavigate_PlayerStuck_EmitsJump()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0);
        var questId = new QuestId(70008u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        // No hostile actors -- forces fallback to navigate-to-Location
        // (CombatController.Decide returns null target, engine falls back to Location navigate)

        var step = new CombatStep
        {
            Id = "combat-fallback-nav",
            KillEnemyDataIds = [100u],
            Spawn = CombatSpawn.AutoOnEnterArea,
            Location = new NpcLocation(0u, 1, new Position3(50f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70008, 3)" }
        };
        var quest = BuildQuest(70008u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick for over 5s -- watchdog should fire on the fallback Navigate
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 7, TimeSpan.FromSeconds(1));

        Assert.Contains(actions, a => a is EngineAction.Jump);
        // Should NOT be Engage (there are no hostiles, the fallback is Navigate)
    }

    // =========================================================================
    // NE7 -- Non-navigate step (talk), watchdog stays idle
    // =========================================================================

    [Fact]
    public async Task NE7_NonNavigateStep_WatchdogStaysIdle_NeverEmitsJump()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0);
        var questId = new QuestId(70009u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new TalkStep
        {
            Id = "talk-npc",
            Target = new NpcLocation(1000u, 1, new Position3(0f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70009, 3)" }
        };
        var quest = BuildQuest(70009u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick for 10s (well beyond stuck timeout) -- player at (0,0,0) entire time
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 10, TimeSpan.FromSeconds(1));

        // Never emits Jump or UseReturn -- watchdog should stay idle on non-Navigate
        Assert.DoesNotContain(actions, a => a is EngineAction.Jump);
        Assert.DoesNotContain(actions, a => a is EngineAction.UseReturn);
    }

    // =========================================================================
    // NE7b -- Null player position, watchdog skipped (fail-open)
    // =========================================================================

    [Fact]
    public async Task NE7b_NullPlayerPosition_WatchdogSkipped_EmitsNavigate()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0);
        var questId = new QuestId(70010u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        // Simulate position read failure -- SetPositionFailure makes GetPlayerPosition
        // return a failure Result
        kit.GameState.SetPositionFailure("position-read-failed", "unit test");

        var step = new TravelStep
        {
            Id = "travel-no-pos",
            Destination = new TravelDestination(Zone: 1, Position: new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70010, 3)" }
        };
        var quest = BuildQuest(70010u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick several times -- even though "stuck" time has passed, watchdog should not fire
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 7, TimeSpan.FromSeconds(1));

        // Fail-open: engine should emit Navigate (not Jump), watchdog was not consulted
        Assert.DoesNotContain(actions, a => a is EngineAction.Jump);
        Assert.DoesNotContain(actions, a => a is EngineAction.UseReturn);
    }

    // =========================================================================
    // NE8 -- Implied navigation (ResolveInteractOrNavigate emits Navigate)
    // =========================================================================

    [Fact]
    public async Task NE8_ImpliedNavigation_PlayerStuck_EmitsJump()
    {
        var kit = BuildEngine(navStallTimeoutSeconds: 5.0);
        var questId = new QuestId(70011u);
        kit.QuestState.SetQuestSequence(questId, 0);
        kit.GameState.SetZone(new ZoneId(1));
        kit.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        // TalkStep with NPC far away -- ResolveInteractOrNavigate emits Navigate
        var step = new TalkStep
        {
            Id = "talk-far",
            Target = new NpcLocation(1000u, 1, new Position3(100f, 0f, 0f)),
            Expect = new PredicateExpect { Predicate = "questFlag(70011, 3)" }
        };
        var quest = BuildQuest(70011u, 0, step);
        kit.Engine.StartQuest(quest);

        // Tick for 5+s without moving player -- watchdog should fire on implied Navigate
        var actions = await TickWithoutDispatch(kit.Engine, kit.Clock, 7, TimeSpan.FromSeconds(1));

        // Should emit Jump (watchdog fires on Navigate from implied navigation)
        Assert.Contains(actions, a => a is EngineAction.Jump);
    }
}
