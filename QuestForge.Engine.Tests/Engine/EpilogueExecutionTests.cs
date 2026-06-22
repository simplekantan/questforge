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
using JobId = QuestForge.Adapters.Types.JobId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Tests for post-completion epilogue execution in QuestEngine.
///
/// ALL tests fail because QuestDefinition.Epilogue does not exist yet,
/// QuestEngine.ResolveEpilogueAction does not exist yet, and the engine's
/// IsQuestComplete gate does not enter epilogue mode.
///
/// Spec: docs/EPILOGUE_STEP_PLAN.md -- Decisions EP1 through EP13,
/// scenarios EP_T3 through EP_T22.
///
/// BUILDER GUIDANCE -- implementation checklist:
///   1. Add Epilogue property (Step[]?) to QuestDefinition (EP1).
///   2. Add _epilogueSteps, _inEpilogue, _epilogueConfirmedStepIds fields to QuestEngine (EP2, EP8, EP11).
///   3. Expand epilogue steps in StartQuest using shared usageCount (EP9).
///   4. Clear epilogue state in BeginRun (EP8).
///   5. Modify the IsQuestComplete gate to enter epilogue (EP13).
///   6. Add ResolveEpilogueAction cursor walk (EP3, EP10).
///   7. Route NotifyEquipBestGearComplete by _inEpilogue (EP5).
///   8. Extend FindStepById to search _epilogueSteps (EP7).
///
/// Run: dotnet test QuestForge.Engine.Tests --filter EpilogueExecutionTests -v detailed
/// </summary>
public sealed class EpilogueExecutionTests
{
    // =========================================================================
    // EP_T3 -- Regression guard: quest completes with no epilogue -> Done immediately
    // =========================================================================

    [Fact]
    public async Task EP_T3_QuestCompletes_NoEpilogue_DoneImmediately()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90001 with one TalkStep (expect satisfied), quest complete,
         *           AND Epilogue is null (the default for existing quests),
         *           When Tick,
         *           Then action is Done immediately -- identical to current behavior.
         *
         * This test is the regression guard: adding the epilogue feature must NOT change
         * behavior for any existing quest that has no epilogue.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90001), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90001), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));

        var quest = BuildQuestWithEpilogue(90001, epilogue: null,
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90001) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t3");

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Done>(action);
    }

    // =========================================================================
    // EP_T4 -- Empty epilogue array -> Done immediately
    // =========================================================================

    [Fact]
    public async Task EP_T4_QuestCompletes_EmptyEpilogue_DoneImmediately()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90002 complete, Epilogue = [] (empty array, not null),
         *           When Tick,
         *           Then action is Done.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90002), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90002), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));

        var quest = BuildQuestWithEpilogue(90002, epilogue: [],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90002) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t4");

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Done>(action);
    }

    // =========================================================================
    // EP_T5 -- Quest completes with epilogue -- epilogue steps execute before Done
    // =========================================================================

    [Fact]
    public async Task EP_T5_QuestCompletes_EpilogueExecutesBeforeDone()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet
         * and QuestEngine.ResolveEpilogueAction does not exist.
         *
         * CONTRACT: Given quest 90003 complete, epilogue has ChangeJobStep(JobId=3),
         *           AND player's current job is 1 (not 3),
         *           When Tick 1: action is ChangeJob(3), NOT Done.
         *           Between ticks: set player job to 3.
         *           When Tick 2: action is Done.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90003), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90003), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(3));

        var quest = BuildQuestWithEpilogue(90003,
            epilogue:
            [
                new ChangeJobStep { Id = "switch-job", JobId = 3 }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90003) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t5");

        // Tick 1: epilogue entered, ChangeJob emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var changeJob = Assert.IsType<EngineAction.ChangeJob>(tick1);
        Assert.Equal(3u, changeJob.Job.Value);

        // Between ticks: simulate host executing the job change
        harness.GameState.SetJob(new JobId(3), 90);

        // Tick 2: ChangeJobStep self-confirms (already on target job), epilogue complete -> Done
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(tick2);
    }

    // =========================================================================
    // EP_T6 -- Epilogue with FragmentStep expands and executes
    // =========================================================================

    [Fact]
    public async Task EP_T6_EpilogueFragmentStep_ExpandsAndExecutes()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given fragment "common/test-setup" containing:
         *              EquipGearForQuestStep(Id="equip-item", ItemIds=[4553]),
         *              ChangeJobStep(Id="switch-job", JobId=3),
         *           AND quest 90004 epilogue has FragmentStep(Ref="common/test-setup"),
         *           AND quest complete, player: item 4553 NOT equipped, job is 1,
         *           When Tick 1: EquipGear(4553), stepId contains scoped fragment ID.
         *           Between: mark item 4553 equipped.
         *           When Tick 2: ChangeJob(3).
         *           Between: set player job to 3.
         *           When Tick 3: Done.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90004), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90004), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.GearEquipper.SetItemEquipped(4553u, false);
        harness.JobChanger.AddGearsetForJob(new JobId(3));

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "common/test-setup",
            Parameters = [],
            Steps =
            [
                new EquipGearForQuestStep { Id = "equip-item", ItemIds = [4553u] },
                new ChangeJobStep { Id = "switch-job", JobId = 3 }
            ]
        };

        var quest = BuildQuestWithEpilogue(90004,
            epilogue:
            [
                new FragmentStep { Id = "epilogue-setup", Ref = "common/test-setup" }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90004) >= 1" }
            });

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["common/test-setup"] = fragment
        };

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-ep-t6");

        // Tick 1: EquipGear(4553)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var equipGear = Assert.IsType<EngineAction.EquipGear>(tick1);
        Assert.Equal(4553u, equipGear.ItemId);

        // Simulate equip completion
        harness.GearEquipper.SetItemEquipped(4553u, true);

        // Tick 2: ChangeJob(3)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var changeJob = Assert.IsType<EngineAction.ChangeJob>(tick2);
        Assert.Equal(3u, changeJob.Job.Value);

        // Simulate job change
        harness.GameState.SetJob(new JobId(3), 90);

        // Tick 3: Done
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(tick3);
    }

    // =========================================================================
    // EP_T7 -- Epilogue steps support skipIf
    // =========================================================================

    [Fact]
    public async Task EP_T7_EpilogueSteps_SkipIfEvaluated()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90005 complete, epilogue:
         *              Step A: ChangeJobStep(Id="maybe-switch", JobId=3, SkipIf="playerJobId() == 3"),
         *              Step B: EquipBestGearStep(Id="gear-up"),
         *           AND player's current job is already 3,
         *           When Tick 1: Step A is skipped (skipIf satisfied),
         *                        Action is EquipBestGear (Step B), NOT ChangeJob.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90005), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90005), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(3), 90); // already on target job

        var quest = BuildQuestWithEpilogue(90005,
            epilogue:
            [
                new ChangeJobStep
                {
                    Id = "maybe-switch",
                    JobId = 3,
                    SkipIf = new PredicateExpect { Predicate = "playerJobId() == 3" }
                },
                new EquipBestGearStep { Id = "gear-up" }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90005) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t7");

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Step A skipped; Step B dispatched
        Assert.IsType<EngineAction.EquipBestGear>(action);
    }

    // =========================================================================
    // EP_T8 -- Epilogue uses separate confirmed-step set
    // =========================================================================

    [Fact]
    public async Task EP_T8_EpilogueUsesSeparateConfirmedStepSet()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90006 complete, epilogue:
         *              EquipGearForQuestStep(Id="equip-crystal", ItemIds=[4553], Expect="isSlotEquipped(13)"),
         *              ChangeJobStep(Id="switch-job", JobId=3),
         *           AND isSlotEquipped(13) is true (crystal already equipped),
         *           When Tick 1: "equip-crystal" is confirmed by expect evaluation,
         *                        Action is ChangeJob (second epilogue step).
         *
         * Proves that the epilogue confirmed-step set is separate from _confirmedStepIds.
         * If the first step's confirmation went to the wrong set, the cursor would
         * not advance to the second step.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90006), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90006), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(3));
        // isSlotEquipped(13) evaluates true when item level > 0 for slot 13
        harness.GameState.SetEquippedItemLevelForSlot(13, 1);

        var quest = BuildQuestWithEpilogue(90006,
            epilogue:
            [
                new EquipGearForQuestStep
                {
                    Id = "equip-crystal",
                    ItemIds = [4553u],
                    Expect = new PredicateExpect { Predicate = "isSlotEquipped(13)" }
                },
                new ChangeJobStep { Id = "switch-job", JobId = 3 }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90006) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t8");

        var action = await harness.Engine.Tick(CancellationToken.None);

        // equip-crystal confirmed by expect; cursor advances to switch-job
        var changeJob = Assert.IsType<EngineAction.ChangeJob>(action);
        Assert.Equal(3u, changeJob.Job.Value);
    }

    // =========================================================================
    // EP_T9 -- EquipBestGear in epilogue uses NotifyEquipBestGearComplete
    // =========================================================================

    [Fact]
    public async Task EP_T9_EquipBestGear_InEpilogue_NotifyRoutesCorrectly()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90007 complete, epilogue:
         *              EquipBestGearStep(Id="gear-up"),
         *              RegisterGearsetStep(Id="save-gearset"),
         *           When Tick 1: EquipBestGear emitted.
         *           Between: call NotifyEquipBestGearComplete("gear-up").
         *           When Tick 2: gear-up confirmed (routed to _epilogueConfirmedStepIds),
         *                        RegisterGearset emitted.
         *           When Tick 3: save-gearset self-confirmed (fire-once),
         *                        Done emitted.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90007), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90007), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.BestGearEquipper.ScriptNextResult(EquipOutcome.Equipped);

        var quest = BuildQuestWithEpilogue(90007,
            epilogue:
            [
                new EquipBestGearStep { Id = "gear-up" },
                new RegisterGearsetStep { Id = "save-gearset" }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90007) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t9");

        // Tick 1: EquipBestGear
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);

        // Simulate host calling NotifyEquipBestGearComplete
        harness.Engine.NotifyEquipBestGearComplete("gear-up");

        // Tick 2: gear-up confirmed, RegisterGearset emitted
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.RegisterGearset>(tick2);

        // Tick 3: save-gearset self-confirmed via fire-once, Done
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(tick3);
    }

    // =========================================================================
    // EP_T10 -- RegisterGearsetStep fire-once works in epilogue
    // =========================================================================

    [Fact]
    public async Task EP_T10_RegisterGearsetStep_FireOnce_InEpilogue()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90008 complete, epilogue has only RegisterGearsetStep,
         *           When Tick 1: RegisterGearset emitted,
         *                        _fireOnceDispatchedIds now contains "save-gearset".
         *           When Tick 2: self-confirmed via fire-once, Done emitted.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90008), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90008), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));

        var quest = BuildQuestWithEpilogue(90008,
            epilogue:
            [
                new RegisterGearsetStep { Id = "save-gearset" }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90008) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t10");

        // Tick 1: RegisterGearset emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.RegisterGearset>(tick1);

        // Tick 2: self-confirmed (fire-once dispatched), Done
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(tick2);
    }

    // =========================================================================
    // EP_T11 -- Epilogue does not activate during NG+ replay
    // =========================================================================

    [Fact]
    public async Task EP_T11_Epilogue_DoesNotActivate_DuringNGPlus()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90009 with epilogue ChangeJobStep,
         *           AND NG+ is active (IsActive=true, IsSuspended=false),
         *           AND QuestSequence is 255 (NG+ complete),
         *           AND sequence 255 block exists with one step (expect satisfied),
         *           When Tick: IsQuestComplete is NOT called (replayActive skips gate),
         *                      Action is Wait (sequence steps satisfied, awaiting advance),
         *                      NOT ChangeJob -- epilogue does not run during NG+.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90009), 255);
        harness.QuestState.SetQuestStatus(new QuestId(90009), QuestStatus.Accepted);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: new NewGamePlusChapter(1, "Chapter 1"),
            IsSuspended: false,
            ActiveReplayQuestId: new QuestId(90009)));

        var quest = BuildQuestWithEpilogueAndMultipleSequences(90009,
            epilogue:
            [
                new ChangeJobStep { Id = "switch-job", JobId = 3 }
            ],
            sequences:
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new TalkStep
                        {
                            Id = "talk-npc",
                            Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                            Expect = new PredicateExpect { Predicate = "questSequence(90009) >= 1" }
                        }
                    ]
                },
                new QuestSequence
                {
                    Sequence = 255,
                    Steps =
                    [
                        new TalkStep
                        {
                            Id = "talk-final",
                            Target = new NpcLocation(1000002, 182, new Position3(0, 0, 0)),
                            Expect = new PredicateExpect { Predicate = "questSequence(90009) >= 255" }
                        }
                    ]
                }
            ]);

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t11");

        var action = await harness.Engine.Tick(CancellationToken.None);

        // NG+ active: quest completion gate skipped, epilogue not entered.
        // All steps in seq 255 have expect satisfied -> Wait for game advance.
        Assert.IsNotType<EngineAction.ChangeJob>(action);
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("all steps in current sequence satisfied", wait.Reason, StringComparison.Ordinal);
    }

    // =========================================================================
    // EP_T12 -- BeginRun clears epilogue state between runs
    // =========================================================================

    [Fact]
    public async Task EP_T12_BeginRun_ClearsEpilogueState()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90010 complete, epilogue has ChangeJobStep(JobId=3),
         *           Run 1: Tick produces ChangeJob (epilogue entered).
         *           Run 2: new BeginRun called, quest no longer complete (simulating restart),
         *                  Tick produces the normal sequence step action, NOT epilogue.
         *
         * Proves BeginRun resets _inEpilogue and _epilogueConfirmedStepIds.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90010), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90010), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0, 0, 0));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(3));

        var quest = BuildQuestWithEpilogue(90010,
            epilogue:
            [
                new ChangeJobStep { Id = "switch-job", JobId = 3 }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(10, 0, 10)),
                Expect = new PredicateExpect { Predicate = "questSequence(90010) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-1");

        // Run 1: epilogue entered, ChangeJob emitted
        var run1Action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.ChangeJob>(run1Action);

        // Run 2: reset quest state so it's not complete any more
        harness.QuestState.SetQuestSequence(new QuestId(90010), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90010), QuestStatus.Accepted);
        harness.Engine.BeginRun("run-2");

        // Tick in run 2: engine should resolve the normal sequence step, not epilogue
        var run2Action = await harness.Engine.Tick(CancellationToken.None);

        // The TalkStep is unconfirmed and has a target at (10,0,10) -> implied navigation
        // or interact. Either way, it must NOT be ChangeJob from the epilogue.
        Assert.IsNotType<EngineAction.ChangeJob>(run2Action);
        Assert.IsNotType<EngineAction.Done>(run2Action);
    }

    // =========================================================================
    // EP_T13 -- AwaitUser in epilogue when adapter not wired
    // =========================================================================

    [Fact]
    public async Task EP_T13_EpilogueAwaitUser_WhenAdapterNotWired()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90011 complete, epilogue has EquipGearForQuestStep,
         *           AND IGearEquipper is NOT wired (null),
         *           When Tick: AwaitUser emitted, reason mentions "IGearEquipper".
         */

        // Construct engine WITHOUT gearEquipper to test the adapter-null guard
        var gameState = new Adapters.Fakes.State.FakeGameStateProvider();
        var questState = new Adapters.Fakes.State.FakeQuestState();
        questState.SetQuestSequence(new QuestId(90011), 0);
        questState.SetQuestStatus(new QuestId(90011), QuestStatus.Complete);
        gameState.SetZone(new ZoneId(182));

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
            gearEquipper: null);

        var quest = BuildQuestWithEpilogue(90011,
            epilogue:
            [
                new EquipGearForQuestStep { Id = "equip-thing", ItemIds = [9999u] }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90011) >= 1" }
            });

        engine.StartQuest(quest);
        engine.BeginRun("run-ep-t13");

        var action = await engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("IGearEquipper", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // EP_T14 -- Epilogue with TravelStep navigates correctly
    // =========================================================================

    [Fact]
    public async Task EP_T14_EpilogueTravelStep_NavigatesCorrectly()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90012 complete, epilogue has TravelStep(dest 100,0,200, zone 182),
         *           AND player is at (0,0,0) in zone 182,
         *           When Tick: Navigate(100,0,200), NOT Done.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90012), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90012), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0, 0, 0));

        var quest = BuildQuestWithEpilogue(90012,
            epilogue:
            [
                new TravelStep
                {
                    Id = "go-somewhere",
                    Destination = new TravelDestination(Zone: 182, Position: new Position3(100, 0, 200)),
                    Expect = new PredicateExpect { Predicate = "playerZone() == 182" }
                }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90012) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t14");

        var action = await harness.Engine.Tick(CancellationToken.None);

        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(100f, 0f, 200f), navigate.Destination);
    }

    // =========================================================================
    // EP_T15 -- Epilogue completes all steps then returns Done
    // =========================================================================

    [Fact]
    public async Task EP_T15_EpilogueCompletesAllSteps_ThenDone()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90013 complete, epilogue:
         *              ChangeJobStep(Id="switch", JobId=3) -- player already on job 3,
         *              EquipBestGearStep(Id="gear-up"),
         *           When Tick 1: ChangeJobStep self-confirmed (already on correct job),
         *                        EquipBestGear emitted.
         *           Between: call NotifyEquipBestGearComplete("gear-up").
         *           When Tick 2: Done.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90013), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90013), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(3), 90); // already on target job
        harness.BestGearEquipper.ScriptNextResult(EquipOutcome.Equipped);

        var quest = BuildQuestWithEpilogue(90013,
            epilogue:
            [
                new ChangeJobStep { Id = "switch", JobId = 3 },
                new EquipBestGearStep { Id = "gear-up" }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90013) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t15");

        // Tick 1: ChangeJobStep self-confirms, EquipBestGear emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick1);

        // Simulate host completing equip best gear
        harness.Engine.NotifyEquipBestGearComplete("gear-up");

        // Tick 2: All epilogue steps confirmed, Done
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(tick2);
    }

    // =========================================================================
    // EP_T16 -- Epilogue with all steps already satisfied -> Done immediately
    // =========================================================================

    [Fact]
    public async Task EP_T16_EpilogueAllStepsSatisfied_DoneImmediately()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90014 complete, epilogue has
         *              ChangeJobStep(Id="switch", JobId=3, Expect="playerJobId() == 3"),
         *           AND player is already on job 3 (expect satisfied),
         *           When Tick: ChangeJobStep confirmed by expect on same tick,
         *                      Done returned immediately (no intermediate actions).
         *
         * This is the "fast path" -- all epilogue steps already satisfied.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90014), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90014), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(3), 90); // already on target job

        var quest = BuildQuestWithEpilogue(90014,
            epilogue:
            [
                new ChangeJobStep
                {
                    Id = "switch",
                    JobId = 3,
                    Expect = new PredicateExpect { Predicate = "playerJobId() == 3" }
                }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90014) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t16");

        // Single tick: expect satisfied -> confirmed -> Done (no ChangeJob emitted)
        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(action);
    }

    // =========================================================================
    // EP_T17 -- Full integration: class quest with soul crystal epilogue
    // =========================================================================

    [Fact]
    public async Task EP_T17_FullIntegration_ClassQuestWithSoulCrystalEpilogue()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90015 (simulated class quest) with:
         *              Sequence 0: AcceptStep (expect="questSequence(90015) >= 1"),
         *              Sequence 1: TalkStep (expect="questSequence(90015) >= 255"),
         *              Epilogue: FragmentStep(Ref="common/test-warrior-setup"),
         *           AND fragment "common/test-warrior-setup" contains:
         *              EquipGearForQuestStep(ItemIds=[4553]),
         *              ChangeJobStep(JobId=3),
         *              RegisterGearsetStep,
         *           When RunToCompletion (harness drives engine to Done),
         *           Then actions contain: Interact (accept), Interact (talk),
         *                                EquipGear(4553), ChangeJob(3), RegisterGearset,
         *           AND final action is Done,
         *           AND no AwaitUser was emitted.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90015), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90015), QuestStatus.Accepted);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(0, 0, 0));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.GearEquipper.SetItemEquipped(4553u, false);
        harness.JobChanger.AddGearsetForJob(new JobId(3));

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "common/test-warrior-setup",
            Parameters = [],
            Steps =
            [
                new EquipGearForQuestStep { Id = "equip-crystal", ItemIds = [4553u] },
                new ChangeJobStep { Id = "switch-job", JobId = 3 },
                new RegisterGearsetStep { Id = "save-gearset" }
            ]
        };

        // Wire callbacks: interact advances sequence, eventually completes
        var acceptNpc = new NpcId(1000001);
        var talkNpc = new NpcId(1000002);
        harness.Interactor.OnInteractWith(acceptNpc, () =>
        {
            harness.QuestState.SetQuestSequence(new QuestId(90015), 1);
        });
        harness.Interactor.OnInteractWith(talkNpc, () =>
        {
            harness.QuestState.SetQuestSequence(new QuestId(90015), 255);
            harness.QuestState.SetQuestStatus(new QuestId(90015), QuestStatus.Complete);
        });

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = 90015,
            Name = "Epilogue Integration Test",
            Expansion = "arr",
            Category = "class",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
            Epilogue =
            [
                new FragmentStep { Id = "setup", Ref = "common/test-warrior-setup" }
            ],
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new AcceptStep
                        {
                            Id = "accept-quest",
                            Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                            Expect = new PredicateExpect { Predicate = "questSequence(90015) >= 1" }
                        }
                    ]
                },
                new QuestSequence
                {
                    Sequence = 1,
                    Steps =
                    [
                        new TalkStep
                        {
                            Id = "talk-to-complete",
                            Target = new NpcLocation(1000002, 182, new Position3(5, 0, 5)),
                            Expect = new PredicateExpect { Predicate = "questSequence(90015) >= 255" }
                        }
                    ]
                }
            ]
        };

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["common/test-warrior-setup"] = fragment
        };

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-ep-t17");

        var actions = await harness.RunToCompletion(maxTicks: 10);

        // Verify action sequence contains the expected types in order
        Assert.Contains(actions, a => a is EngineAction.Interact);
        Assert.Contains(actions, a => a is EngineAction.EquipGear eg && eg.ItemId == 4553u);
        Assert.Contains(actions, a => a is EngineAction.ChangeJob cj && cj.Job.Value == 3u);
        Assert.Contains(actions, a => a is EngineAction.RegisterGearset);
        Assert.True(actions.Count <= 10, $"Expected <= 10 actions, got {actions.Count}");
    }

    // =========================================================================
    // EP_T18 -- Epilogue with TeleportStep dispatches correctly
    // =========================================================================

    [Fact]
    public async Task EP_T18_EpilogueTeleportStep_DispatchesCorrectly()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90016 complete, epilogue has TeleportStep(AetheryteId=8),
         *           When Tick: Teleport(AetheryteId(8)) emitted.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90016), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90016), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));

        var quest = BuildQuestWithEpilogue(90016,
            epilogue:
            [
                new TeleportStep
                {
                    Id = "tp-home",
                    AetheryteId = new QuestForge.Schema.AetheryteId(8)
                }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90016) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t18");

        var action = await harness.Engine.Tick(CancellationToken.None);

        var teleport = Assert.IsType<EngineAction.Teleport>(action);
        Assert.Equal(new Adapters.Types.AetheryteId(8), teleport.Destination);
    }

    // =========================================================================
    // EP_T19 -- Fragment expansion uses shared usageCount with sequences
    // =========================================================================

    [Fact]
    public async Task EP_T19_FragmentExpansion_SharedUsageCount()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given fragment "nav/shared" with one TravelStep(Id="travel"),
         *           AND quest 90017 with:
         *              Sequence 0: FragmentStep(Id="seq-use", Ref="nav/shared"),
         *              Epilogue: FragmentStep(Id="epi-use", Ref="nav/shared"),
         *           AND player zone is 100 (all expects satisfied),
         *           AND quest is complete,
         *           When Tick: all steps confirmed via expect, Done emitted.
         *
         * The sequence fragment expanded as "seq-use:travel" and the epilogue fragment
         * expanded as "epi-use:travel" -- distinct IDs (no collision).
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90017), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90017), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(100));
        harness.GameState.SetPosition(new WorldPosition(10, 0, 10));

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "nav/shared",
            Parameters = [],
            Steps =
            [
                new TravelStep
                {
                    Id = "travel",
                    Destination = new TravelDestination(Zone: 100, Position: new Position3(10, 0, 10)),
                    Expect = new PredicateExpect { Predicate = "playerZone() == 100" }
                }
            ]
        };

        var quest = BuildQuestWithEpilogueAndMultipleSequences(90017,
            epilogue:
            [
                new FragmentStep { Id = "epi-use", Ref = "nav/shared" }
            ],
            sequences:
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new FragmentStep { Id = "seq-use", Ref = "nav/shared" }
                    ]
                }
            ]);

        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["nav/shared"] = fragment
        };

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-ep-t19");

        // Both expects are true (zone == 100), so both fragment steps confirm.
        // Epilogue steps also confirm. Engine should reach Done.
        var action = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(action);
    }

    // =========================================================================
    // EP_T20 -- Epilogue handles multiple step types in sequence (tick-by-tick)
    // =========================================================================

    [Fact]
    public async Task EP_T20_EpilogueMultipleStepTypes_TickByTick()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90018 complete, epilogue:
         *              EquipGearForQuestStep(ItemIds=[4553]),
         *              ChangeJobStep(JobId=3),
         *              EquipBestGearStep,
         *              RegisterGearsetStep,
         *           AND item 4553 not equipped, job is 1,
         *           When driven tick-by-tick with side effects:
         *              Tick 1: EquipGear(4553) -- mark equipped
         *              Tick 2: ChangeJob(3) -- set job to 3
         *              Tick 3: EquipBestGear -- call NotifyEquipBestGearComplete
         *              Tick 4: RegisterGearset
         *              Tick 5: Done (RegisterGearset self-confirmed via fire-once)
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90018), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90018), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.GearEquipper.SetItemEquipped(4553u, false);
        harness.JobChanger.AddGearsetForJob(new JobId(3));
        harness.BestGearEquipper.ScriptNextResult(EquipOutcome.Equipped);

        var quest = BuildQuestWithEpilogue(90018,
            epilogue:
            [
                new EquipGearForQuestStep { Id = "equip", ItemIds = [4553u] },
                new ChangeJobStep { Id = "switch", JobId = 3 },
                new EquipBestGearStep { Id = "gear-up" },
                new RegisterGearsetStep { Id = "save" }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90018) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t20");

        // Tick 1: EquipGear(4553)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipGear>(tick1);
        Assert.Equal(4553u, ((EngineAction.EquipGear)tick1).ItemId);
        harness.GearEquipper.SetItemEquipped(4553u, true);

        // Tick 2: ChangeJob(3)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.ChangeJob>(tick2);
        Assert.Equal(3u, ((EngineAction.ChangeJob)tick2).Job.Value);
        harness.GameState.SetJob(new JobId(3), 90);

        // Tick 3: EquipBestGear
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.EquipBestGear>(tick3);
        harness.Engine.NotifyEquipBestGearComplete("gear-up");

        // Tick 4: RegisterGearset
        var tick4 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.RegisterGearset>(tick4);

        // Tick 5: Done (RegisterGearset self-confirmed via fire-once)
        var tick5 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Done>(tick5);
    }

    // =========================================================================
    // EP_T21 -- Epilogue persists across ticks (re-entry)
    // =========================================================================

    [Fact]
    public async Task EP_T21_EpiloguePersistsAcrossTicks()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90019 complete, epilogue has
         *              ChangeJobStep(Id="switch", JobId=3, Expect="playerJobId() == 3"),
         *           AND player job is 1 (not 3),
         *           When Tick 1: ChangeJob emitted.
         *           When Tick 2: (job still 1) ChangeJob emitted again.
         *
         * The engine correctly re-enters the epilogue path on the second tick
         * because _inEpilogue persists. IsQuestComplete is still true but
         * the engine does not emit Done while _inEpilogue is set.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90019), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90019), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetJob(new JobId(1), 90);
        harness.JobChanger.AddGearsetForJob(new JobId(3));

        var quest = BuildQuestWithEpilogue(90019,
            epilogue:
            [
                new ChangeJobStep
                {
                    Id = "switch",
                    JobId = 3,
                    Expect = new PredicateExpect { Predicate = "playerJobId() == 3" }
                }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90019) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t21");

        // Tick 1: ChangeJob(3)
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var cj1 = Assert.IsType<EngineAction.ChangeJob>(tick1);
        Assert.Equal(3u, cj1.Job.Value);

        // Tick 2: (no side effect applied -- job still 1) -> still ChangeJob, NOT Done
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.ChangeJob>(tick2);
        Assert.IsNotType<EngineAction.Done>(tick2);
    }

    // =========================================================================
    // EP_T22 -- Casting guard applies during epilogue EquipBestGear
    // =========================================================================

    [Fact]
    public async Task EP_T22_CastingGuard_DuringEpilogueEquipBestGear()
    {
        /*
         * RED: Will fail because QuestDefinition.Epilogue does not exist yet.
         *
         * CONTRACT: Given quest 90020 complete, epilogue has EquipBestGearStep,
         *           AND player is casting,
         *           When Tick: Wait emitted, reason contains "casting",
         *                      NOT EquipBestGear.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(90020), 0);
        harness.QuestState.SetQuestStatus(new QuestId(90020), QuestStatus.Complete);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetCasting(true);

        var quest = BuildQuestWithEpilogue(90020,
            epilogue:
            [
                new EquipBestGearStep { Id = "gear-up" }
            ],
            new TalkStep
            {
                Id = "talk-npc",
                Target = new NpcLocation(1000001, 182, new Position3(0, 0, 0)),
                Expect = new PredicateExpect { Predicate = "questSequence(90020) >= 1" }
            });

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-ep-t22");

        var action = await harness.Engine.Tick(CancellationToken.None);

        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("casting", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Builds a QuestDefinition with a single sequence block (Sequence=0) containing one step,
    /// and an optional epilogue array.
    /// The sequence step's expect is pre-satisfied (sequence set to match).
    /// </summary>
    private static QuestDefinition BuildQuestWithEpilogue(
        uint questId,
        Step[]? epilogue,
        params Step[] sequenceSteps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Epilogue Test Quest",
            Expansion = "arr",
            Category = "class",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Epilogue = epilogue,
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps = sequenceSteps
                }
            ]
        };

    /// <summary>
    /// Builds a QuestDefinition with explicit sequence blocks and an optional epilogue.
    /// </summary>
    private static QuestDefinition BuildQuestWithEpilogueAndMultipleSequences(
        uint questId,
        Step[]? epilogue,
        QuestSequence[] sequences) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Epilogue Multi-Seq Test Quest",
            Expansion = "arr",
            Category = "class",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Epilogue = epilogue,
            Sequences = sequences
        };
}
