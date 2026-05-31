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
/// Failing tests for QuestEngine dispatch of OpenCoffersStep.
/// Spec: docs/OPEN_COFFERS_STEP_PLAN.md -- Decisions OC-1 through OC-18,
/// scenarios OC1-OC12.
///
/// All types referenced below are NEW and do not yet exist:
///   - OpenCoffersStep (schema type)                                    (TODO)
///   - EngineAction.OpenCoffer(uint ItemId, Step? Origin)               (TODO)
///   - ICofferOpener interface                                          (TODO)
///   - FakeCofferOpener fake                                            (TODO)
///   - IGameStateProvider.HasCoffers(ct) method                         (TODO)
///   - FakeGameStateProvider.SetHasCoffers(bool) method                 (TODO)
///   - QuestEngine.ResolveOpenCoffers async pre-arm                     (TODO)
///   - EngineTestHarness.CofferOpener property                          (TODO)
///   - EngineTestHarness RunToCompletion arm for OpenCoffer              (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~OpenCoffersStepTests"
/// </summary>
public sealed class OpenCoffersStepTests
{
    // =========================================================================
    // OC1 -- Happy path, coffer in inventory -- emits OpenCoffer
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_CofferInInventory_EmitsOpenCoffer_OC1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90001), 0);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var openCoffer = Assert.IsType<EngineAction.OpenCoffer>(action);
        Assert.Equal(44001u, openCoffer.ItemId);
        Assert.NotNull(openCoffer.Origin);
    }

    // =========================================================================
    // OC2 -- No coffers in inventory -- Wait, no OpenCoffer emitted
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_NoCoffers_EmitsWait_OC2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90002), 0);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(false);
        harness.CofferOpener.SetCofferItemIds([]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90002, 0, step));

        // Expect is true (not inventoryHasCoffers() when no coffers) -> cursor walk confirms step -> Wait
        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // OC3 -- Player casting -- Wait, no OpenCoffer emitted
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_PlayerCasting_EmitsWait_OC3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90003), 0);
        harness.GameState.SetCasting(true);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // OC4 -- Inventory full -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_InventoryFull_EmitsAwaitUser_OC4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90004), 0);
        harness.GameState.SetFreeInventorySlots(0);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90004, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("inventory full", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // OC5 -- No adapter wired -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_NoAdapterWired_EmitsAwaitUser_OC5()
    {
        // Construct a QuestEngine WITHOUT cofferOpener (null default).
        // The harness always wires FakeCofferOpener, so we must construct a separate engine.
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(90005), 0);
        gameState.SetHasCoffers(true);

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
            cofferOpener: null);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        engine.StartQuest(BuildSingleStepQuest(90005, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("no ICofferOpener wired", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // OC6 -- Multiple coffers, first one dispatched
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_MultipleCoffers_DispatchesFirst_OC6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90006), 0);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u, 44002u, 44003u]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90006, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var openCoffer = Assert.IsType<EngineAction.OpenCoffer>(action);
        Assert.Equal(44001u, openCoffer.ItemId); // first in list
    }

    // =========================================================================
    // OC7 -- Multi-tick integration: open all coffers then advance
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_MultiTick_OpensAllThenAdvances_OC7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90007), 0);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u, 44002u]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90007, 0, step));

        // Tick 1: opens first coffer
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var oc1 = Assert.IsType<EngineAction.OpenCoffer>(tick1);
        Assert.Equal(44001u, oc1.ItemId);

        // Simulate: 44001 opened, removed from list
        harness.CofferOpener.SetCofferItemIds([44002u]);

        // Tick 2: opens second coffer
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var oc2 = Assert.IsType<EngineAction.OpenCoffer>(tick2);
        Assert.Equal(44002u, oc2.ItemId);

        // Simulate: all coffers opened
        harness.CofferOpener.SetCofferItemIds([]);
        harness.GameState.SetHasCoffers(false);

        // Tick 3: Expect (not inventoryHasCoffers()) is true -> step confirmed -> Wait
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick3);
    }

    // =========================================================================
    // OC8 -- Mounted + prior Navigate: lazy-dismount DOES fire (NOT exempt)
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_AfterNavigate_Mounted_DismountFires_OC8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90008), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u]);

        var quest = BuildTwoStepQuest(
            questId: 90008,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-open",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new OpenCoffersStep
            {
                Id = "open-coffers-after-navigate",
                Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: lazy-dismount fires because EngineAction.OpenCoffer is NOT exempt
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.True(harness.Mount.DismountCallCount >= 1,
            "Lazy-dismount must fire before OpenCoffer (NOT exempt per OC-6)");
    }

    // =========================================================================
    // OC9 -- Standalone + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_Standalone_Mounted_NoDismount_OC9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90009), 0);
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers-standalone",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90009, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.OpenCoffer>(action);
        // No prior Navigate -> lazy-dismount not triggered
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // OC10 -- Expect already satisfied (no coffers) -- step skipped in cursor walk
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_ExpectAlreadySatisfied_StepSkipped_OC10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90010), 0);
        harness.GameState.SetHasCoffers(false);
        harness.CofferOpener.SetCofferItemIds([]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers-skip",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90010, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits in cursor walk; step confirmed done; no more steps -> Wait
        Assert.IsType<EngineAction.Wait>(action);
        // Adapter never called
        Assert.Equal(0, harness.CofferOpener.OpenCofferCalls.Count);
    }

    // =========================================================================
    // OC11 -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_CancelledToken_ThrowsOperationCanceledException_OC11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90011), 0);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u]);

        var step = new OpenCoffersStep
        {
            Id = "open-coffers-cancel",
            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(90011, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // OC12 -- RunToCompletion integration: open two coffers then advance
    // =========================================================================

    [Fact]
    public async Task OpenCoffersStep_RunToCompletion_TwoCoffers_OC12()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90012), 0);
        harness.GameState.SetFreeInventorySlots(10);
        harness.GameState.SetHasCoffers(true);
        harness.CofferOpener.SetCofferItemIds([44001u, 44002u]);

        // AcceptStep to satisfy E4, wired to auto-satisfy on interact
        harness.Interactor.OnInteractWith(new NpcId(1000u), () =>
            harness.QuestState.SetQuestSequence(new QuestId(90012), 1));

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 90012,
            Name = "OpenCoffers RunToCompletion Test",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(1000u, 128, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new OpenCoffersStep
                        {
                            Id = "open-coffers-rtc",
                            Expect = new PredicateExpect { Predicate = "not inventoryHasCoffers()" }
                        }
                    ]
                }
            ]
        };

        harness.Engine.StartQuest(quest);

        var actions = await harness.RunToCompletion(maxTicks: 15);

        var openCofferActions = actions
            .OfType<EngineAction.OpenCoffer>()
            .ToList();

        Assert.True(openCofferActions.Count >= 2,
            $"Expected at least 2 OpenCoffer actions but got {openCofferActions.Count}. " +
            $"All actions: [{string.Join(", ", actions.Select(a => a.GetType().Name))}]");

        Assert.Equal(44001u, openCofferActions[0].ItemId);
        Assert.Equal(44002u, openCofferActions[1].ItemId);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "OpenCoffers Step Test Quest",
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
            Name = "OpenCoffers Step Two-Step Test Quest",
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
