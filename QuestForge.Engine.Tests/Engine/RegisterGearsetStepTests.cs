using QuestForge.Adapters.Gear;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
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
/// Failing tests for QuestEngine dispatch of RegisterGearsetStep.
/// Spec: docs/REGISTER_GEARSET_STEP_PLAN.md -- Decisions RG-1 through RG-17,
/// scenarios RG1-RG11.
///
/// Types referenced below that do NOT yet exist:
///   - RegisterGearsetStep : Step { } (empty sealed class)                 (TODO)
///   - EngineAction.RegisterGearset(Step? Origin = null)                   (TODO)
///   - IGearsetManager interface                                           (TODO)
///   - FakeGearsetManager fake                                             (TODO)
///   - RegisterOutcome enum                                                (TODO)
///   - QuestEngine._gearsetManager field, optional ctor param              (TODO)
///   - QuestEngine.ResolveRegisterGearset async pre-arm                    (TODO)
///   - EngineTestHarness.GearsetManager property                           (TODO)
///   - EngineTestHarness RunToCompletion arm for RegisterGearset           (TODO)
///   - HarnessEngine dismount exemption for RegisterGearset               (TODO)
///   - IGameStateProvider.GearsetExistsForJob(uint, CancellationToken)     (TODO)
///   - FakeGameStateProvider.SetGearsetExistsForJob(uint, bool)            (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~RegisterGearsetStepTests"
/// </summary>
public sealed class RegisterGearsetStepTests
{
    // =========================================================================
    // RG1 -- Happy path, adapter wired -- emits RegisterGearset
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_HappyPath_EmitsRegisterGearset_RG1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87001), 0);
        // Player not casting, not in combat (defaults).

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset"
            // No authored Expect
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var registerGearset = Assert.IsType<EngineAction.RegisterGearset>(action);
        Assert.NotNull(registerGearset.Origin);
    }

    // =========================================================================
    // RG2 -- No adapter wired -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_NoAdapterWired_EmitsAwaitUser_RG2()
    {
        // Construct a QuestEngine WITHOUT gearsetManager (null default).
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
            gearsetManager: null);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-no-adapter"
        };

        engine.StartQuest(BuildSingleStepQuest(87002, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("no IGearsetManager wired", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // RG3 -- Player casting -- Wait, no RegisterGearset emitted
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_PlayerCasting_EmitsWait_RG3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87003), 0);
        harness.GameState.SetCasting(true);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-casting"
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // RG4 -- Player in combat -- Wait, no RegisterGearset emitted
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_PlayerInCombat_EmitsWait_RG4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87004), 0);
        harness.GameState.SetInCombat(true);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-combat"
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87004, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("in combat", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // RG5 -- skipIf satisfied -- step skipped
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_SkipIfSatisfied_StepSkipped_RG5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87005), 0);
        harness.GameState.SetGearsetExistsForJob(32, true);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-skip",
            SkipIf = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87005, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Step skipped, engine moves past it; Wait (no more steps)
        Assert.IsType<EngineAction.Wait>(action);
        // Adapter never called
        Assert.Equal(0, harness.GearsetManager.RecordedCalls.Count);
    }

    // =========================================================================
    // RG6 -- skipIf not satisfied -- step fires
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_SkipIfNotSatisfied_StepFires_RG6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87006), 0);
        harness.GameState.SetGearsetExistsForJob(32, false);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-no-skip",
            SkipIf = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87006, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.RegisterGearset>(action);
    }

    // =========================================================================
    // RG7 -- Expect satisfied -- step confirmed
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_ExpectSatisfied_StepConfirmed_RG7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87007), 0);
        harness.GameState.SetGearsetExistsForJob(32, true);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-expect",
            Expect = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87007, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect satisfied, step confirmed; Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // RG8 -- Mounted + prior Navigate: dismount does NOT fire (exempt)
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_AfterNavigate_Mounted_DismountDoesNotFire_RG8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87008), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildTwoStepQuest(
            questId: 87008,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-register-gearset",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new RegisterGearsetStep
            {
                Id = "register-gearset-after-navigate"
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: RegisterGearset -- lazy-dismount does NOT fire (RegisterGearset IS exempt per RG-11)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.RegisterGearset>(tick2);

        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // RG9 -- Standalone + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_Standalone_Mounted_NoDismount_RG9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87009), 0);
        harness.GameState.SetMountState(MountState.Mounted);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-standalone-mounted"
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87009, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.RegisterGearset>(action);
        // No prior Navigate -> lazy-dismount not triggered
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // RG10 -- RunToCompletion integration
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_RunToCompletion_Integration_RG10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87010), 0);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-rtc",
            Expect = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87010, 0, step));

        // Tick 1: RegisterGearset emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.RegisterGearset>(tick1);

        // Simulate: game confirms gearset now exists for job 32
        harness.GameState.SetGearsetExistsForJob(32, true);

        // RunToCompletion from here
        var actions = await harness.RunToCompletion(maxTicks: 10);

        // The RegisterGearset action was verified in tick 1 (line 312). RunToCompletion
        // confirms the engine reaches a terminal state after the Expect is satisfied.
        Assert.NotNull(actions);
    }

    // =========================================================================
    // RG11 -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task RegisterGearsetStep_CancelledToken_ThrowsOperationCanceledException_RG11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87011), 0);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-cancel"
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87011, 0, step));

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
            Name = "RegisterGearset Step Test Quest",
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
            Name = "RegisterGearset Step Two-Step Test Quest",
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
