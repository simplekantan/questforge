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
/// Failing tests for QuestEngine dispatch of EquipGearForQuestStep.
/// Spec: docs/EQUIP_GEAR_FOR_QUEST_STEP_PLAN.md -- Decisions EG-1 through EG-12,
/// scenarios EG1-EG14.
///
/// All types referenced below are NEW and do not yet exist:
///   - EngineAction.EquipGear(uint ItemId, Step? Origin)             (TODO)
///   - QuestEngine.ResolveEquipGear async pre-arm                    (TODO)
///   - EngineTestHarness RunToCompletion arm for EquipGear            (TODO)
///   - HarnessEngine dismount exemption for EquipGear                 (TODO)
///   - W1 suppression for EquipGearForQuestStep                       (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~EquipGearForQuestStepTests"
/// </summary>
public sealed class EquipGearForQuestStepTests
{
    // =========================================================================
    // EG1 -- Happy path, single item, not equipped -- emits EquipGear
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_SingleItem_NotEquipped_EmitsEquipGear_EG1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85001), 0);
        harness.GearEquipper.SetItemEquipped(12345u, false);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-quest-armor",
            ItemIds = [12345u]
            // Expect is null -- implicit postcondition handles completion
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var equipGear = Assert.IsType<EngineAction.EquipGear>(action);
        Assert.Equal(12345u, equipGear.ItemId);
        Assert.NotNull(equipGear.Origin);
    }

    // =========================================================================
    // EG2 -- Happy path, single item, already equipped -- step self-confirms
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_SingleItem_AlreadyEquipped_StepSelfConfirms_EG2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85002), 0);
        harness.GearEquipper.SetItemEquipped(12345u, true);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-quest-armor-done",
            ItemIds = [12345u]
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // All items equipped -> implicit postcondition met -> step self-confirms -> Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // EG3 -- Multi-item, first unequipped -- emits EquipGear for first unequipped
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_MultiItem_FirstUnequipped_EmitsForFirstUnequipped_EG3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85003), 0);
        harness.GearEquipper.SetItemEquipped(11111u, true);   // first item already equipped
        harness.GearEquipper.SetItemEquipped(22222u, false);  // second item not equipped

        var step = new EquipGearForQuestStep
        {
            Id = "equip-multi-item",
            ItemIds = [11111u, 22222u, 33333u]
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var equipGear = Assert.IsType<EngineAction.EquipGear>(action);
        Assert.Equal(22222u, equipGear.ItemId); // skipped 11111 (equipped), emits first unequipped
    }

    // =========================================================================
    // EG4 -- Multi-item integration: equip all items across multiple ticks
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_MultiItem_EquipsAcrossMultipleTicks_EG4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85004), 0);
        harness.GearEquipper.SetItemEquipped(11111u, false);
        harness.GearEquipper.SetItemEquipped(22222u, false);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-two-items",
            ItemIds = [11111u, 22222u]
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85004, 0, step));

        // Tick 1: emits EquipGear(11111u)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var eg1 = Assert.IsType<EngineAction.EquipGear>(tick1);
        Assert.Equal(11111u, eg1.ItemId);

        // Simulate game confirming first equip
        harness.GearEquipper.SetItemEquipped(11111u, true);

        // Tick 2: emits EquipGear(22222u)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var eg2 = Assert.IsType<EngineAction.EquipGear>(tick2);
        Assert.Equal(22222u, eg2.ItemId);

        // Simulate game confirming second equip
        harness.GearEquipper.SetItemEquipped(22222u, true);

        // Tick 3: all items equipped -> step self-confirms -> Wait
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick3);
    }

    // =========================================================================
    // EG5 -- Player casting -- Wait, no EquipGear emitted
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_PlayerCasting_EmitsWait_EG5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85005), 0);
        harness.GameState.SetCasting(true);
        harness.GearEquipper.SetItemEquipped(12345u, false);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-while-casting",
            ItemIds = [12345u]
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85005, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // EG6 -- Player in combat -- Wait, no EquipGear emitted
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_PlayerInCombat_EmitsWait_EG6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85006), 0);
        harness.GameState.SetInCombat(true);
        harness.GearEquipper.SetItemEquipped(12345u, false);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-in-combat",
            ItemIds = [12345u]
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85006, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("in combat", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // EG7 -- Mounted + prior Navigate: lazy-dismount does NOT fire (exempt)
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_AfterNavigate_Mounted_DismountDoesNotFire_EG7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85007), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GearEquipper.SetItemEquipped(12345u, false);

        var quest = BuildTwoStepQuest(
            questId: 85007,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-equip",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new EquipGearForQuestStep
            {
                Id = "equip-after-navigate",
                ItemIds = [12345u]
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: EquipGear -- lazy-dismount does NOT fire (EquipGear is exempt per EG-5)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipGear>(tick2);

        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // EG8 -- Standalone EquipGear + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_Standalone_Mounted_NoDismount_EG8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85008), 0);
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GearEquipper.SetItemEquipped(12345u, false);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-standalone-mounted",
            ItemIds = [12345u]
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.EquipGear>(action);
        // No prior Navigate -> lazy-dismount bound to prior Navigate
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // EG9 -- No adapter wired -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_NoAdapterWired_EmitsAwaitUser_EG9()
    {
        // Construct a QuestEngine WITHOUT gearEquipper (null default).
        // We cannot use EngineTestHarness directly because it always wires GearEquipper.
        // Instead, use the harness but assert the AwaitUser message content.
        // NOTE: This test requires the builder to make gearEquipper truly null.
        // For now, we construct the engine without the gear equipper.

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85009), 0);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-no-adapter",
            ItemIds = [12345u]
        };

        // Build a quest engine without gearEquipper by using the inner engine directly.
        // The harness always wires FakeGearEquipper, so we must construct a separate engine.
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
            gearEquipper: null);

        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(85009), 0);

        engine.StartQuest(BuildSingleStepQuest(85009, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("no IGearEquipper wired", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // EG10 -- Authored Expect already satisfied + all items equipped -- step skipped
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_ExpectAlreadySatisfied_AllEquipped_StepSkipped_EG10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85010), 0);
        harness.GameState.SetAetheryteAttuned(new AetheryteId(8), true); // predicate true
        harness.GearEquipper.SetItemEquipped(12345u, true);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-expect-skip",
            ItemIds = [12345u],
            Expect = new PredicateExpect { Predicate = "isAttuned(8)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85010, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits in cursor walk; step confirmed done; Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // EG11 -- Authored Expect NOT satisfied but all items equipped -- step confirmed
    //         via implicit postcondition in dispatch arm
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_ExpectNotSatisfied_AllEquipped_StepConfirmedByImplicit_EG11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85011), 0);
        harness.GearEquipper.SetItemEquipped(12345u, true); // item equipped
        // Quest flag 3 NOT set -> predicate false

        var step = new EquipGearForQuestStep
        {
            Id = "equip-expect-false-but-equipped",
            ItemIds = [12345u],
            Expect = new PredicateExpect { Predicate = "questFlag(82111, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85011, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect was false in cursor walk -> dispatch arm runs -> ResolveEquipGear returns null
        // (all equipped) -> dispatch arm self-confirms -> continues to next step -> Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // EG12 -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_CancelledToken_ThrowsOperationCanceledException_EG12()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85012), 0);
        harness.GearEquipper.SetItemEquipped(12345u, false);

        var step = new EquipGearForQuestStep
        {
            Id = "equip-cancel",
            ItemIds = [12345u]
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(85012, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // EG13 -- Full RunToCompletion with two items
    // =========================================================================

    [Fact]
    public async Task EquipGearStep_RunToCompletion_TwoItems_EG13()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(85013), 0);
        harness.GearEquipper.SetItemEquipped(11111u, false);
        harness.GearEquipper.SetItemEquipped(22222u, false);

        // Wire a TalkStep after the EquipGear step that completes the quest
        // when interacted with, allowing RunToCompletion to reach Done.
        const uint completionNpc = 9999u;
        harness.Interactor.OnInteractWith(new NpcId(completionNpc), () =>
            harness.QuestState.SetQuestStatus(new QuestId(85013), QuestStatus.Complete));

        var quest = BuildTwoStepQuest(
            questId: 85013,
            sequence: 0,
            step0: new EquipGearForQuestStep
            {
                Id = "equip-two-items-rtc",
                ItemIds = [11111u, 22222u]
            },
            step1: new TalkStep
            {
                Id = "complete-quest",
                Target = new NpcLocation(completionNpc, 128, new Position3(0f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "questSequence(85013) >= 255" }
            });

        harness.Engine.StartQuest(quest);

        var actions = await harness.RunToCompletion(maxTicks: 15);

        var equipActions = actions
            .OfType<EngineAction.EquipGear>()
            .ToList();

        Assert.True(equipActions.Count >= 2,
            $"Expected at least 2 EquipGear actions but got {equipActions.Count}. " +
            $"All actions: [{string.Join(", ", actions.Select(a => a.GetType().Name))}]");

        Assert.Equal(11111u, equipActions[0].ItemId);
        Assert.Equal(22222u, equipActions[1].ItemId);
    }

    // =========================================================================
    // EG14 -- W1 suppressed for EquipGearForQuestStep without Expect
    //         (per Decision EG-9: implicit postcondition makes Expect optional)
    // =========================================================================

    [Fact]
    public void Validate_EquipGearForQuest_NoExpect_W1Suppressed_EG14()
    {
        var draft = Authoring.DraftValidatorTestData.ValidBaseline();
        draft.AddStep(Authoring.DraftValidatorTestData.MakeDraftStep("equip-no-expect-eg14", 2,
            new EquipGearForQuestStep
            {
                Id = "equip-no-expect-eg14",
                ItemIds = [12345u],
                Expect = null  // missing Expect -- but W1 is suppressed per EG-9
            },
            notes: "x"), Authoring.DraftValidatorTestData.T0);

        var result = new QuestForge.Engine.Authoring.DraftValidator().Validate(draft);

        // W1 must NOT fire for EquipGearForQuestStep (implicit postcondition makes it non-spin-loop)
        Assert.DoesNotContain(result.Warnings, w => w.Code == "W1");
        // No errors for well-formed ItemIds
        Assert.DoesNotContain(result.Errors, e => e.Code == "E16");
        Assert.DoesNotContain(result.Errors, e => e.Code == "E17");
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "EquipGear Step Test Quest",
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
            Name = "EquipGear Step Two-Step Test Quest",
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
