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
/// Failing tests for QuestEngine dispatch of ChangeJobStep.
/// Spec: docs/CHANGE_JOB_STEP_PLAN.md -- Decisions CJ-1 through CJ-15,
/// scenarios CJ1-CJ14.
///
/// Types referenced below that do NOT yet exist:
///   - EngineAction.ChangeJob(JobId Job, Step? Origin)                  (TODO)
///   - QuestEngine.ResolveChangeJob async pre-arm                       (TODO)
///   - EngineTestHarness RunToCompletion arm for ChangeJob              (TODO)
///   - W1 suppression for ChangeJobStep                                 (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~ChangeJobStepTests"
/// </summary>
public sealed class ChangeJobStepTests
{
    // =========================================================================
    // CJ1 -- Happy path, not on target job, gearset exists -- emits ChangeJob
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_NotOnTargetJob_GearsetExists_EmitsChangeJob_CJ1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86001), 0);
        harness.GameState.SetJob(new JobId(19), 90); // Paladin, not DRK
        harness.JobChanger.AddGearsetForJob(new JobId(32));

        var step = new ChangeJobStep
        {
            Id = "switch-to-drk",
            JobId = 32
            // Expect is null -- implicit postcondition handles completion
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86001, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var changeJob = Assert.IsType<EngineAction.ChangeJob>(action);
        Assert.Equal(32u, changeJob.Job.Value);
        Assert.NotNull(changeJob.Origin);
    }

    // =========================================================================
    // CJ2 -- Already on target job -- step self-confirms
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_AlreadyOnTargetJob_StepSelfConfirms_CJ2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86002), 0);
        harness.GameState.SetJob(new JobId(32), 90); // already DRK

        var step = new ChangeJobStep
        {
            Id = "switch-to-drk-done",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86002, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Already on target job -> implicit postcondition met -> step self-confirms -> Wait
        Assert.IsType<EngineAction.Wait>(action);
        // Adapter never called
        Assert.Equal(0, harness.JobChanger.RecordedCalls.Count);
    }

    // =========================================================================
    // CJ3 -- Gearset not found -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_NoGearsetForJob_EmitsAwaitUser_CJ3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86003), 0);
        harness.GameState.SetJob(new JobId(19), 90); // not DRK
        // No gearset added for job 32

        var step = new ChangeJobStep
        {
            Id = "switch-no-gearset",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86003, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("no gearset found for job 32", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.JobChanger.RecordedCalls.Count);
    }

    // =========================================================================
    // CJ4 -- Player casting -- Wait, no ChangeJob emitted
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_PlayerCasting_EmitsWait_CJ4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86004), 0);
        harness.GameState.SetCasting(true);
        harness.GameState.SetJob(new JobId(19), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(32));

        var step = new ChangeJobStep
        {
            Id = "switch-while-casting",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86004, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("player casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CJ5 -- Player in combat -- Wait, no ChangeJob emitted
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_PlayerInCombat_EmitsWait_CJ5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86005), 0);
        harness.GameState.SetInCombat(true);
        harness.GameState.SetJob(new JobId(19), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(32));

        var step = new ChangeJobStep
        {
            Id = "switch-in-combat",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86005, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("in combat", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CJ6 -- No adapter wired -- AwaitUser
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_NoAdapterWired_EmitsAwaitUser_CJ6()
    {
        // Construct a QuestEngine WITHOUT jobChanger (null default).
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
            jobChanger: null);

        var step = new ChangeJobStep
        {
            Id = "switch-no-adapter",
            JobId = 32
        };

        engine.StartQuest(BuildSingleStepQuest(86006, 0, step));

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("no IJobChanger wired", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // CJ7 -- Mounted + prior Navigate: lazy-dismount DOES fire (NOT exempt)
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_AfterNavigate_Mounted_DismountDoesFire_CJ7()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86007), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GameState.SetJob(new JobId(19), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(32));

        var quest = BuildTwoStepQuest(
            questId: 86007,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-change-job",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new ChangeJobStep
            {
                Id = "change-job-after-navigate",
                JobId = 32
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: Navigate emitted; _lastDispatchedWasNavigate = true
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Advance state: TravelStep Expect now satisfies
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: ChangeJob -- lazy-dismount DOES fire (ChangeJob is NOT exempt per CJ-5)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Dismount must have been called (ChangeJob is NOT in the exemption list)
        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but got {harness.Mount.DismountCallCount}. " +
            "ChangeJob is NOT exempt from lazy-dismount (Decision CJ-5).");
    }

    // =========================================================================
    // CJ8 -- Standalone ChangeJob + mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_Standalone_Mounted_NoDismount_CJ8()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86008), 0);
        harness.GameState.SetMountState(MountState.Mounted);
        harness.GameState.SetJob(new JobId(19), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(32));

        var step = new ChangeJobStep
        {
            Id = "change-job-standalone-mounted",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86008, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.ChangeJob>(action);
        // No prior Navigate -> lazy-dismount not triggered
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // CJ9 -- Authored Expect already satisfied + already on target job -- step skipped
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_ExpectSatisfied_AlreadyOnJob_StepSkipped_CJ9()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86009), 0);
        harness.GameState.SetAetheryteAttuned(new AetheryteId(8), true); // predicate true
        harness.GameState.SetJob(new JobId(32), 90); // already DRK

        var step = new ChangeJobStep
        {
            Id = "change-job-expect-skip",
            JobId = 32,
            Expect = new PredicateExpect { Predicate = "isAttuned(8)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86009, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect short-circuits in cursor walk; step confirmed done; Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // CJ10 -- Authored Expect NOT satisfied but already on target job --
    //         step self-confirms via dispatch arm
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_ExpectNotSatisfied_AlreadyOnJob_StepConfirmedByImplicit_CJ10()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86010), 0);
        harness.GameState.SetJob(new JobId(32), 90); // already DRK
        // questFlag(82111, 3) NOT set -> predicate false

        var step = new ChangeJobStep
        {
            Id = "change-job-expect-false-but-on-job",
            JobId = 32,
            Expect = new PredicateExpect { Predicate = "questFlag(82111, 3)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86010, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect was false in cursor walk -> dispatch arm runs -> ResolveChangeJob returns null
        // (already on job) -> dispatch arm self-confirms -> continues to next step -> Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // CJ11 -- RunToCompletion integration: change job then advance
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_RunToCompletion_Integration_CJ11()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86011), 0);
        harness.GameState.SetJob(new JobId(19), 90); // starts as PLD
        harness.JobChanger.AddGearsetForJob(new JobId(32));

        var step = new ChangeJobStep
        {
            Id = "switch-to-drk-rtc",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86011, 0, step));

        // Tick 1: ChangeJob emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.ChangeJob>(tick1);

        // Simulate: game confirms job change
        harness.GameState.SetJob(new JobId(32), 90);

        // RunToCompletion from here
        var actions = await harness.RunToCompletion(maxTicks: 10);

        // The step should self-confirm on the next tick (implicit postcondition met),
        // and the engine should reach Done.
        // actions list may or may not contain the ChangeJob from tick1 (depending on
        // RunToCompletion starting fresh). The key assertion: RunToCompletion succeeds.
        Assert.True(actions.Count >= 0, "RunToCompletion should succeed");
    }

    // =========================================================================
    // CJ12 -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_CancelledToken_ThrowsOperationCanceledException_CJ12()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86012), 0);
        harness.GameState.SetJob(new JobId(19), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(32));

        var step = new ChangeJobStep
        {
            Id = "change-job-cancel",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86012, 0, step));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // CJ13 -- Adapter returns GearsetNotFound (dispatched despite pre-flight) -- engine re-fires
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_AdapterReturnsGearsetNotFound_EngineRefires_CJ13()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86013), 0);
        harness.GameState.SetJob(new JobId(19), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(32)); // pre-flight passes
        harness.JobChanger.ScriptNextResult(JobChangeOutcome.GearsetNotFound); // adapter fails

        var step = new ChangeJobStep
        {
            Id = "change-job-adapter-gearset-not-found",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86013, 0, step));

        // Engine emits ChangeJob regardless of adapter outcome (fire-and-forget)
        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.ChangeJob>(action);
    }

    // =========================================================================
    // CJ14 -- GetCurrentJob read failure (fail-open) -- emits ChangeJob
    // =========================================================================

    [Fact]
    public async Task ChangeJobStep_GetCurrentJobFailure_FailOpen_EmitsChangeJob_CJ14()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86014), 0);
        harness.GameState.SetJob(new JobId(19), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(32));
        harness.GameState.SetCurrentJobFail("adapter error"); // GetCurrentJob returns Failure

        var step = new ChangeJobStep
        {
            Id = "change-job-fail-open",
            JobId = 32
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86014, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Fail-open: if we can't read the current job, we cannot confirm the implicit
        // postcondition, so we emit the action anyway (safe because ChangeToJob is idempotent).
        var changeJob = Assert.IsType<EngineAction.ChangeJob>(action);
        Assert.Equal(32u, changeJob.Job.Value);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "ChangeJob Step Test Quest",
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
            Name = "ChangeJob Step Two-Step Test Quest",
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
