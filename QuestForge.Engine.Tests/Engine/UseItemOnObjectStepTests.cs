using Microsoft.Extensions.Logging.Abstractions;
using QuestForge.Adapters;
using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Actions;
using QuestForge.Adapters.Fakes.Chat;
using QuestForge.Adapters.Fakes.Combat;
using QuestForge.Adapters.Fakes.Emotes;
using QuestForge.Adapters.Fakes.Gear;
using QuestForge.Adapters.Fakes.Interaction;
using QuestForge.Adapters.Fakes.Items;
using QuestForge.Adapters.Fakes.Minigames;
using QuestForge.Adapters.Fakes.Movement;
using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Fakes.Timing;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of UseItemOnObjectStep.
/// Spec: docs/USE_ITEM_ON_OBJECT_STEP_PLAN.md -- Decisions UIO1-UIO12, scenarios UIO_T1-UIO_T13.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~UseItemOnObjectStepTests"
/// </summary>
public sealed class UseItemOnObjectStepTests
{
    // =========================================================================
    // UIO_T1 -- Happy path: player close, emits UseItemOnObject with correct fields
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_PlayerClose_EmitsUseItemOnObject_WithCorrectFields_UIO_T1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95001), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-on-device",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95001, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useItemOnObj = Assert.IsType<EngineAction.UseItemOnObject>(action);
        Assert.Equal(new InteractableId(99001), useItemOnObj.Target);
        Assert.Equal(ItemKind.KeyItem, useItemOnObj.Kind);
        Assert.Equal(2002001u, useItemOnObj.ItemId);
        Assert.NotNull(useItemOnObj.Origin);
        Assert.IsType<UseItemOnObjectStep>(useItemOnObj.Origin);
    }

    // =========================================================================
    // UIO_T2 -- Player far from object, emits Navigate first
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_PlayerFar_EmitsNavigate_UIO_T2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95002), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-far",
            InteractableId = 99001u,
            Position = new Position3(100f, 0f, 100f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95002, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        // Default StopDistance for interaction is 3.0
        Assert.Equal(3.0f, navigate.Options.StoppingDistance, 1);
    }

    // =========================================================================
    // UIO_T3 -- Player close with custom StopDistance honored
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_CustomStopDistance_PlayerWithin_EmitsUseItemOnObject_UIO_T3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95003), 0);
        harness.GameState.SetZone(new ZoneId(132));
        // Player at (14, 0, 10), object at (10, 0, 10) -> distance = 4.0, within StopDistance of 5.0
        harness.GameState.SetPosition(new WorldPosition(14f, 0f, 10f));

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-custom-stop",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            StopDistance = 5.0f,
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95003, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Player within custom StopDistance -> UseItemOnObject, NOT Navigate
        Assert.IsType<EngineAction.UseItemOnObject>(action);
    }

    // =========================================================================
    // UIO_T4 -- Player casting -> Wait
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_PlayerCasting_EmitsWait_UIO_T4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95004), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.GameState.SetCasting(true);

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-casting",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95004, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95004, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // UIO_T5 -- Action cooldown active -> Wait
    // Requires direct engine construction with non-zero actionCooldownSeconds
    // and ManualTimeProvider (same pattern as ActionCooldownTests).
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_ActionCooldown_EmitsWait_UIO_T5()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var gameState = new FakeGameStateProvider();
        var questState = new FakeQuestState();
        var navigator = new FakeNavigator(gameState);
        var teleporter = new FakeTeleporter(gameState);
        var interactor = new FakeInteractor(gameState, questState);
        var objectInteractor = new FakeObjectInteractor();

        questState.SetQuestSequence(new QuestId(95005), 0);
        gameState.SetZone(new ZoneId(132));
        gameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var engine = new QuestEngine(
            gameState, questState, navigator, teleporter, interactor,
            new FakeCombat(), new FakeMinigameSkipper(),
            new FakeDialogueResolver(), new FakeTimingProfile(),
            NullTraceWriter.Instance,
            NullLogger<QuestEngine>.Instance,
            clock,
            objectInteractor: objectInteractor,
            actionCooldownSeconds: 5.0);

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-cooldown",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95005, 3)" }
        };

        engine.StartQuest(BuildSingleStepQuest(95005, 0, step));

        // Tick 1: emits UseItemOnObject (sets _lastActionFiredAt)
        var tick1 = await engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseItemOnObject>(tick1);

        // Advance only 2s -- well within the 5s cooldown window
        clock.Advance(TimeSpan.FromSeconds(2));

        // Tick 2: cooldown not elapsed -> Wait with "cooldown" in reason
        var tick2 = await engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.Contains("cooldown", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // UIO_T6 -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_CancelledToken_ThrowsOperationCanceledException_UIO_T6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95006), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-cancel",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95006, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95006, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // UIO_T7 -- Mounted + prior Navigate: lazy-dismount fires
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_AfterNavigate_MountedPlayer_LazyDismountFires_UIO_T7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95007), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(100f, 0f, 100f));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildTwoStepQuest(
            questId: 95007,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-use-item-on-obj",
                Destination = new TravelDestination(Zone: 132, Position: new Position3(10f, 0f, 10f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 132" }
            },
            step1: new UseItemOnObjectStep
            {
                Id = "use-key-after-navigate",
                InteractableId = 99001u,
                Position = new Position3(10f, 0f, 10f),
                Kind = ItemKind.KeyItem,
                ItemId = 2002001u,
                Expect = new PredicateExpect { Predicate = "questFlag(95007, 3)" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies; move player close
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        // Tick 2: UseItemOnObject -- lazy-dismount fires (not in exemption list per UIO7)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseItemOnObject>(tick2);

        // Pins Decision UIO7: UseItemOnObject triggers lazy-dismount
        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but was {harness.Mount.DismountCallCount}");
    }

    // =========================================================================
    // UIO_T8 -- Standalone + mounted: no dismount before UseItemOnObject emission
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_Standalone_MountedPlayer_NoDismount_UIO_T8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95008), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));
        harness.GameState.SetMountState(MountState.Mounted);

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-standalone-mounted",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95008, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.UseItemOnObject>(action);
        // No prior Navigate -> lazy-dismount does NOT fire
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // UIO_T9 -- Expect already satisfied -> step skipped
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_ExpectAlreadySatisfied_StepSkipped_UIO_T9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95009), 0);
        // Make the Expect predicate true before the step runs
        harness.QuestState.SetQuestFlagBit(new QuestId(95009), 3, true);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var step0 = new UseItemOnObjectStep
        {
            Id = "use-key-expect-skip",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95009, 3)" }
        };

        // Second step so engine has somewhere to go after skipping step0
        var step1 = new TalkStep
        {
            Id = "talk-after-skip",
            Target = new NpcLocation(1000789u, 132, new Position3(10f, 0f, 10f)),
            Expect = new PredicateExpect { Predicate = "questSequence(95009) >= 1" }
        };

        harness.Engine.StartQuest(BuildTwoStepQuest(95009, 0, step0, step1));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Engine skips UseItemOnObjectStep; action corresponds to the NEXT step
        Assert.IsNotType<EngineAction.UseItemOnObject>(action);
    }

    // =========================================================================
    // UIO_T10 -- Two-tick integration: UseItemOnObject fires, Expect satisfies, step completes
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_TwoTickIntegration_ItemFires_ExpectSatisfies_StepCompletes_UIO_T10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95010), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var step0 = new UseItemOnObjectStep
        {
            Id = "use-key-integration",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95010, 3)" }
        };

        // Second step so engine advances past step0
        var step1 = new TalkStep
        {
            Id = "talk-after-use-item-on-obj",
            Target = new NpcLocation(1000789u, 132, new Position3(10f, 0f, 10f)),
            Expect = new PredicateExpect { Predicate = "questSequence(95010) >= 1" }
        };

        harness.Engine.StartQuest(BuildTwoStepQuest(95010, 0, step0, step1));

        // Tick 1: engine emits UseItemOnObject
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var useItemOnObj = Assert.IsType<EngineAction.UseItemOnObject>(tick1);
        Assert.Equal(ItemKind.KeyItem, useItemOnObj.Kind);
        Assert.Equal(2002001u, useItemOnObj.ItemId);

        // Between ticks: simulate the game advancing (quest flag 3 now true)
        harness.QuestState.SetQuestFlagBit(new QuestId(95010), 3, true);

        // Tick 2: Expect now satisfied -> step complete, engine moves to next step
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        // The action should correspond to the NEXT step (TalkStep), not UseItemOnObject again
        Assert.IsNotType<EngineAction.UseItemOnObject>(tick2);
    }

    // =========================================================================
    // UIO_T11 -- IObjectInteractor not wired -> AwaitUser
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_NoObjectInteractor_EmitsAwaitUser_UIO_T11()
    {
        var gameState = new FakeGameStateProvider();
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(95011), 0);
        gameState.SetZone(new ZoneId(132));
        gameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var engine = new QuestEngine(
            gameState,
            questState,
            new FakeNavigator(gameState),
            new FakeTeleporter(gameState),
            new FakeInteractor(gameState, questState),
            new FakeCombat(),
            new FakeMinigameSkipper(),
            new FakeDialogueResolver(),
            new FakeTimingProfile(),
            NullTraceWriter.Instance,
            NullLogger<QuestEngine>.Instance,
            objectInteractor: null);

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-no-adapter",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95011, 3)" }
        };

        engine.StartQuest(BuildSingleStepQuest(95011, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("IObjectInteractor", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // UIO_T12 -- Null playerPos -> fail-open, emits UseItemOnObject
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_NullPlayerPos_FailOpen_EmitsUseItemOnObject_UIO_T12()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95012), 0);
        harness.GameState.SetZone(new ZoneId(132));
        // Force position read to fail -- engine sees playerPos == null
        harness.GameState.SetPositionFailure("adapter-error", "position unavailable");

        var step = new UseItemOnObjectStep
        {
            Id = "use-key-null-pos",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.KeyItem,
            ItemId = 2002001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95012, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95012, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Fail-open: null playerPos -> emit UseItemOnObject (not Navigate)
        Assert.IsType<EngineAction.UseItemOnObject>(action);
    }

    // =========================================================================
    // UIO_T13 -- InventoryItem kind emits correct Kind field
    // =========================================================================

    [Fact]
    public async Task UseItemOnObjectStep_InventoryItemKind_EmitsCorrectKindField_UIO_T13()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(95013), 0);
        harness.GameState.SetZone(new ZoneId(132));
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 10f));

        var step = new UseItemOnObjectStep
        {
            Id = "use-inventory-item-on-obj",
            InteractableId = 99001u,
            Position = new Position3(10f, 0f, 10f),
            Kind = ItemKind.InventoryItem,
            ItemId = 5001u,
            Expect = new PredicateExpect { Predicate = "questFlag(95013, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(95013, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useItemOnObj = Assert.IsType<EngineAction.UseItemOnObject>(action);
        Assert.Equal(ItemKind.InventoryItem, useItemOnObj.Kind);
        Assert.Equal(5001u, useItemOnObj.ItemId);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "UseItemOnObject Step Test Quest",
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
            Name = "UseItemOnObject Step Two-Step Test Quest",
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

/// <summary>
/// File-scoped ManualTimeProvider for action cooldown test (UIO_T5).
/// Same pattern used in ActionCooldownTests, WaitStepTests, etc.
/// </summary>
file sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset startTime) => _now = startTime;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
