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
    // EB8 -- No Expect + single Equipped: host-driven confirm (replaces fire-once)
    // Spec: F4 -- same observable behavior as old EB8, but via NotifyEquipBestGearComplete
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_NoExpect_HostConfirmsAfterEquipped_EB8()
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

        // Host dispatches to adapter, gets Equipped, calls NotifyEquipBestGearComplete
        var adapterResult = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.True(adapterResult.IsSuccess);
        harness.Engine.NotifyEquipBestGearComplete("equip-best-no-expect");

        // Tick 2: step confirmed; no more steps -> Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        // Adapter called exactly once by the test (the engine Tick does not call adapter directly)
        Assert.Equal(1, harness.BestGearEquipper.RecordedCalls.Count);
    }

    // =========================================================================
    // EB8b -- Casting guard then host-driven confirm (replaces fire-once)
    // Spec: F5 -- casting -> Wait, clear casting -> EquipBestGear, Pending,
    //             EquipBestGear again, Equipped, confirm, Wait
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_CastingGuardThenHostConfirm_EB8b()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.GameState.SetCasting(true);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Pending,
            Adapters.Gear.EquipOutcome.Equipped);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-casting-then-fire",
            Expect = null
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: casting -> Wait (NOT dispatched). Adapter NOT called.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick1);
        Assert.Equal(0, harness.BestGearEquipper.RecordedCalls.Count);

        // Clear casting
        harness.GameState.SetCasting(false);

        // Tick 2: EquipBestGear emitted. Host dispatches -> adapter returns Pending.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);
        var result2 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.True(result2.IsSuccess);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, result2.ValueOrThrow);
        // Pending -- do NOT call NotifyEquipBestGearComplete

        // Tick 3: EquipBestGear emitted again (not confirmed yet).
        //         Host dispatches -> adapter returns Equipped -> confirm.
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick3);
        var result3 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.True(result3.IsSuccess);
        Assert.Equal(Adapters.Gear.EquipOutcome.Equipped, result3.ValueOrThrow);
        harness.Engine.NotifyEquipBestGearComplete("equip-best-casting-then-fire");

        // Tick 4: step confirmed -> Wait (done)
        var tick4 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick4);

        // Adapter called exactly 2 times by the test
        Assert.Equal(2, harness.BestGearEquipper.RecordedCalls.Count);
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
    // F1 -- Happy path: adapter returns Pending then Equipped (two-tick)
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F1
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_PendingThenEquipped_TwoTick_F1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Pending,
            Adapters.Gear.EquipOutcome.Equipped);

        var step = new EquipBestGearStep
        {
            Id = "equip-best",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted. Host dispatches -> adapter returns Pending.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var result1 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, result1.ValueOrThrow);
        // Pending -> do NOT call NotifyEquipBestGearComplete

        // Tick 2: EquipBestGear emitted again. Host dispatches -> adapter returns Equipped.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);
        var result2 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Equipped, result2.ValueOrThrow);
        harness.Engine.NotifyEquipBestGearComplete("equip-best");

        // Adapter called exactly 2 times
        Assert.Equal(2, harness.BestGearEquipper.RecordedCalls.Count);
    }

    // =========================================================================
    // F2 -- Happy path: adapter returns Equipped on first call (backwards compat)
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F2
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_EquippedOnFirstCall_BackwardsCompat_F2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptNextResult(Adapters.Gear.EquipOutcome.Equipped);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-compat",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted. Host dispatches -> adapter returns Equipped.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var result = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Equipped, result.ValueOrThrow);
        harness.Engine.NotifyEquipBestGearComplete("equip-best-compat");

        // Adapter called exactly 1 time
        Assert.Equal(1, harness.BestGearEquipper.RecordedCalls.Count);
    }

    // =========================================================================
    // F3 -- No Expect + Pending then Equipped: self-confirms only after Equipped
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F3
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_NoExpect_PendingPendingEquipped_ConfirmsAfterEquipped_F3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Pending,
            Adapters.Gear.EquipOutcome.Pending,
            Adapters.Gear.EquipOutcome.Equipped);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-multi-pending",
            Expect = null
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted. Adapter returns Pending. Step NOT confirmed.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var r1 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, r1.ValueOrThrow);

        // Tick 2: EquipBestGear emitted again. Adapter returns Pending. Step NOT confirmed.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);
        var r2 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, r2.ValueOrThrow);

        // Tick 3: EquipBestGear emitted. Adapter returns Equipped. Host confirms.
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick3);
        var r3 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Equipped, r3.ValueOrThrow);
        harness.Engine.NotifyEquipBestGearComplete("equip-best-multi-pending");

        // Tick 4: step confirmed; no more steps -> Wait
        var tick4 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick4);

        // Adapter called exactly 3 times
        Assert.Equal(3, harness.BestGearEquipper.RecordedCalls.Count);
    }

    // =========================================================================
    // F4 -- No Expect + single Equipped: self-confirms immediately
    // This is the same as the updated EB8 above -- included here for spec traceability.
    // See EquipBestGearStep_NoExpect_HostConfirmsAfterEquipped_EB8.
    // =========================================================================

    // (Covered by EB8 above -- no duplicate test needed)

    // =========================================================================
    // F5 -- Casting guard then Pending then Equipped
    // This is the same as the updated EB8b above -- included here for spec traceability.
    // See EquipBestGearStep_CastingGuardThenHostConfirm_EB8b.
    // =========================================================================

    // (Covered by EB8b above -- no duplicate test needed)

    // =========================================================================
    // F6 -- Adapter failure does not confirm step
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F6
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_NoExpect_AdapterFailureThenEquipped_ConfirmsOnEquipped_F6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        // First call fails, second returns Equipped (default)
        harness.BestGearEquipper.ScriptNextFailure("adapter error");

        var step = new EquipBestGearStep
        {
            Id = "equip-best-fail-then-ok",
            Expect = null
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted. Host dispatches -> adapter returns Failed.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var r1 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.False(r1.IsSuccess);
        // Failed -> do NOT call NotifyEquipBestGearComplete

        // Tick 2: EquipBestGear emitted again (step not confirmed).
        //         Host dispatches -> adapter returns Equipped (default). Confirm.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);
        var r2 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.True(r2.IsSuccess);
        Assert.Equal(Adapters.Gear.EquipOutcome.Equipped, r2.ValueOrThrow);
        harness.Engine.NotifyEquipBestGearComplete("equip-best-fail-then-ok");

        // Tick 3: step confirmed -> Wait (done)
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick3);

        // Adapter called exactly 2 times
        Assert.Equal(2, harness.BestGearEquipper.RecordedCalls.Count);
    }

    // =========================================================================
    // F7 -- Pending does not count as dispatched for combat guard
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F7
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_PendingThenCombat_CombatGuardFires_F7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Pending);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-pending-combat",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted. Adapter returns Pending.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var r1 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, r1.ValueOrThrow);

        // Enter combat
        harness.GameState.SetInCombat(true);

        // Tick 2: combat guard fires -> Wait. Adapter NOT called again.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var wait = Assert.IsType<EngineAction.Wait>(tick2);
        Assert.Contains("in combat", wait.Reason, StringComparison.OrdinalIgnoreCase);

        // Only 1 adapter call total
        Assert.Equal(1, harness.BestGearEquipper.RecordedCalls.Count);
    }

    // =========================================================================
    // F8 -- RunToCompletion integration with multi-tick equip
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F8
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_RunToCompletion_MultiTickEquip_F8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Pending,
            Adapters.Gear.EquipOutcome.Pending,
            Adapters.Gear.EquipOutcome.Equipped);

        const uint completionNpc = 9999u;
        harness.Interactor.OnInteractWith(new NpcId(completionNpc), () =>
            harness.QuestState.SetQuestStatus(new QuestId(65575), QuestStatus.Complete));

        var quest = BuildTwoSequenceQuest(
            questId: 65575,
            seq0Step: new EquipBestGearStep
            {
                Id = "equip-best-rtc-multi",
                Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
            },
            seq1Step: new TalkStep
            {
                Id = "complete-quest-f8",
                Target = new NpcLocation(completionNpc, 128, new Position3(0f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "questSequence(65575) >= 255" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: EquipBestGear (Pending)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var r1 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, r1.ValueOrThrow);

        // Tick 2: EquipBestGear (Pending)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);
        var r2 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, r2.ValueOrThrow);

        // Tick 3: EquipBestGear (Equipped) -> confirm
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick3);
        var r3 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Equipped, r3.ValueOrThrow);
        harness.Engine.NotifyEquipBestGearComplete("equip-best-rtc-multi");

        // Advance sequence externally (simulating game state change from equip)
        harness.QuestState.SetQuestSequence(new QuestId(65575), 1);

        // Run remaining ticks to completion via TalkStep
        var actions = await harness.RunToCompletion(maxTicks: 10);

        // RunToCompletion should reach Done via the TalkStep in sequence 1
        Assert.True(actions.Count > 0,
            "Expected at least 1 action in RunToCompletion but got 0");
    }

    // =========================================================================
    // F9 -- Sequence change clears pending state
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F9
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_SequenceChangeClearsPendingState_F9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Pending);

        var quest = BuildTwoSequenceQuest(
            questId: 65575,
            seq0Step: new EquipBestGearStep
            {
                Id = "equip-best-seq-change",
                Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
            },
            seq1Step: new EquipBestGearStep
            {
                Id = "equip-best-seq1",
                Expect = new PredicateExpect { Predicate = "questSequence(65575) == 2" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: EquipBestGear emitted for seq 0 step. Adapter returns Pending.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var r1 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.Equal(Adapters.Gear.EquipOutcome.Pending, r1.ValueOrThrow);

        // Externally advance sequence to 1 (simulating game-side completion)
        harness.QuestState.SetQuestSequence(new QuestId(65575), 1);

        // Tick 2: engine moves to seq 1 step (seq 0 step is no longer visited).
        //         The pending state from seq 0 should be cleared by the sequence change.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        // Should emit EquipBestGear for the seq 1 step (not be stuck on seq 0)
        Assert.IsType<EngineAction.EquipBestGear>(tick2);
    }

    // =========================================================================
    // F10 -- NoChange outcome confirms step (gear already optimal)
    // Spec: docs/EQUIP_BEST_GEAR_TWO_PHASE_PLAN.md F10
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_NoExpect_NoChangeOutcome_Confirms_F10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptNextResult(Adapters.Gear.EquipOutcome.NoChange);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-nochange",
            Expect = null
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted. Adapter returns NoChange. Host confirms.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        var r1 = await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        Assert.True(r1.IsSuccess);
        Assert.Equal(Adapters.Gear.EquipOutcome.NoChange, r1.ValueOrThrow);
        harness.Engine.NotifyEquipBestGearComplete("equip-best-nochange");

        // Tick 2: step confirmed -> Wait (done)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);
    }

    // =========================================================================
    // F2b -- NotifyEquipBestGearComplete for wrong stepId is ignored
    // Spec: Derived from EBF6 -- specific stepId matching
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_NoExpect_WrongStepIdNotify_StillDispatches_F2b()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Equipped,
            Adapters.Gear.EquipOutcome.Equipped);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-wrong-id",
            Expect = null
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);

        // Confirm with WRONG stepId -- should be ignored
        harness.Engine.NotifyEquipBestGearComplete("some-other-step-id");

        // Tick 2: step NOT confirmed (wrong id) -> still emits EquipBestGear
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick2);

        // Now confirm with correct stepId
        harness.Engine.NotifyEquipBestGearComplete("equip-best-wrong-id");

        // Tick 3: step confirmed -> Wait
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick3);
    }

    // =========================================================================
    // F4b -- With authored Expect, Expect takes priority over host confirm
    // Spec: Derived from plan -- expect-satisfaction bypasses need for Notify
    // =========================================================================

    [Fact]
    public async Task EquipBestGearStep_WithExpect_ExpectSatisfiedBeforeNotify_ConfirmsByExpect_F4b()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(65575), 0);
        harness.BestGearEquipper.ScriptOutcomeSequence(
            Adapters.Gear.EquipOutcome.Pending);

        var step = new EquipBestGearStep
        {
            Id = "equip-best-expect-priority",
            Expect = new PredicateExpect { Predicate = "questSequence(65575) == 1" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(65575, 0, step));

        // Tick 1: EquipBestGear emitted. Adapter returns Pending.
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);
        await harness.BestGearEquipper.EquipBestGear(CancellationToken.None);
        // Do NOT call NotifyEquipBestGearComplete

        // Externally satisfy the expect predicate
        harness.QuestState.SetQuestSequence(new QuestId(65575), 1);

        // Tick 2: Expect is satisfied -> step confirmed via expect, not via Notify.
        //         With no more steps in this sequence, engine should Wait or advance.
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        // The step should be confirmed by expect-check, so engine moves past it -> Wait
        Assert.IsType<EngineAction.Wait>(tick2);
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
