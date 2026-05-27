using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE — S2: Engine runs a single replayed quest off live signals.
///
/// Tests S2.1–S2.11 (docs/NEW_GAME_PLUS_PLAN.md §S2 GWT specs).
///
/// EXPECTED STATE after writing:
///   S2.1, S2.2 — PASS (regression guards; existing engine behaviour already correct for normal play).
///   S2.3, S2.4, S2.5, S2.6, S2.7, S2.8, S2.9, S2.10, S2.11 — FAIL by assertion
///     because QuestEngine.ResolveAction does not yet read GetNewGamePlusState; it fires the
///     IsQuestComplete → Done gate unconditionally, so tests expecting a step action
///     (or Wait("ng+ replay suspended")) instead receive Done.
///
/// Fake seam added (S2.8): FakeGameStateProvider.SetNewGamePlusStateFailure(reason) makes
/// GetNewGamePlusState return Result.Failure — mirroring the existing SetHostileActorsFailure pattern.
/// </summary>
public sealed class NewGamePlusS2EngineTests
{
    // =========================================================================
    // Shared constants
    // =========================================================================

    private const uint QuestId_Q = 65847u;
    private static readonly QuestId Q = new(QuestId_Q);

    // A position ON TOP of the travel step destination (XZ distance = 0 → Interact, not Navigate).
    // Using a neutral position: the step action type being Navigate or Interact does not matter
    // for most tests — we just need the engine to return ANY non-Done, non-AwaitUser action
    // that indicates it is running the step rather than short-circuiting via the bitmap gate.
    private static readonly WorldPosition AtStepPos = new(100f, 0f, 100f);
    private static readonly Position3 StepPos3 = new(100f, 0f, 100f);

    // =========================================================================
    // Quest builders
    // =========================================================================

    /// <summary>
    /// Builds a quest with a single sequence block (sequence=0) containing one TravelStep.
    /// The step has no Expect — it is never auto-confirmed, forcing the engine to emit Navigate.
    /// </summary>
    private static QuestDefinition BuildSingleStepQuest(
        uint questId = QuestId_Q,
        int sequence = 0,
        string stepId = "step-a") =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "NG+ S2 Test Quest",
            Expansion = "arr",
            Category = "main",
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
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = stepId,
                            Destination = new TravelDestination(Zone: 182, Position: StepPos3)
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// Builds a quest with two sequence blocks (0 and 1), each with one TravelStep.
    /// Used by S2.4 to verify live questSequence selects the correct block.
    /// </summary>
    private static QuestDefinition BuildTwoSequenceQuest() =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = QuestId_Q,
            Name = "NG+ S2 Two-Sequence Quest",
            Expansion = "arr",
            Category = "main",
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
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "step-seq0",
                            Destination = new TravelDestination(Zone: 182, Position: new Position3(1f, 0f, 1f))
                        }
                    ]
                },
                new QuestSequence
                {
                    Sequence = 1,
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "step-seq1",
                            Destination = new TravelDestination(Zone: 182, Position: StepPos3)
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// Builds a quest whose single step has an Expect predicate that evaluates to true
    /// when questSequence(Q) >= 255 (a sentinel no real sequence will reach in tests unless
    /// the test explicitly sets it — used for S2.5 "all steps confirmed" scenarios).
    ///
    /// For S2.5 we need the step's Expect to be satisfied so it confirms and the engine
    /// falls through to the Wait("all steps in current sequence satisfied…") return.
    /// We use isQuestAccepted(Q) as the Expect and call AddAcceptedQuest before the tick.
    /// </summary>
    private static QuestDefinition BuildQuestWithConfirmableStep() =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = QuestId_Q,
            Name = "NG+ S2 Confirmable Quest",
            Expansion = "arr",
            Category = "main",
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
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "step-confirm",
                            Destination = new TravelDestination(Zone: 182, Position: StepPos3),
                            // Expect = isQuestAccepted(Q): satisfied when AddAcceptedQuest is called.
                            Expect = new PredicateExpect { Predicate = $"isQuestAccepted({QuestId_Q})" }
                        }
                    ]
                }
            ]
        };

    /// <summary>
    /// Builds a quest whose sequence 0 is: [AcceptStep(Expect=isQuestAccepted(Q)), TravelStep].
    /// Used by S2.9 and S2.10.
    /// </summary>
    private static QuestDefinition BuildAcceptStepQuest() =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = QuestId_Q,
            Name = "NG+ S2 Accept-Step Quest",
            Expansion = "arr",
            Category = "main",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(NpcId: 1003987u, Zone: 182, Position: StepPos3),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new AcceptStep
                        {
                            Id = "accept-step",
                            Target = new NpcLocation(NpcId: 1003987u, Zone: 182, Position: StepPos3),
                            Expect = new PredicateExpect { Predicate = $"isQuestAccepted({QuestId_Q})" }
                        },
                        new TravelStep
                        {
                            Id = "step-after-accept",
                            Destination = new TravelDestination(Zone: 182, Position: StepPos3)
                        }
                    ]
                }
            ]
        };

    // =========================================================================
    // Helper
    // =========================================================================

    private static string FormatAction(EngineAction action) => action switch
    {
        EngineAction.Done => "Done",
        EngineAction.Wait w => $"Wait(\"{w.Reason}\")",
        EngineAction.AwaitUser au => $"AwaitUser(\"{au.Reason}\")",
        EngineAction.Navigate nav => $"Navigate({nav.Destination})",
        EngineAction.Interact i => $"Interact({i.Target})",
        _ => action.GetType().Name
    };

    // =========================================================================
    // S2.1 — Normal play: IsQuestComplete=true → Done (regression guard)
    // =========================================================================

    /// <summary>
    /// S2.1 (normal-play regression):
    /// Given IsActive == false and IsQuestComplete(Q) == true (QuestStatus.Complete);
    /// When StartQuest(Q); BeginRun; Tick;
    /// Then EngineAction.Done (existing gate is UNCHANGED for normal play).
    ///
    /// EXPECTED: PASS — this is the existing behaviour; no engine change required for S2.1.
    /// </summary>
    [Fact]
    public async Task S2_1_NormalPlay_QuestComplete_ReturnsDone()
    {
        // Given
        var harness = new EngineTestHarness();

        // Normal play: IsActive == false (default FakeGameStateProvider state)
        // Bitmap reads complete
        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete);
        harness.QuestState.SetQuestSequence(Q, 0);

        var quest = BuildSingleStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-1-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: existing IsQuestComplete → Done gate fires
        Assert.IsType<EngineAction.Done>(action);
    }

    // =========================================================================
    // S2.2 — Normal play: not complete → runs the sequence step (regression guard)
    // =========================================================================

    /// <summary>
    /// S2.2 (normal-play regression — runs sequence):
    /// Given IsActive == false, IsQuestComplete(Q) == false, sequence 0 active,
    /// and a TravelStep with no Expect (never auto-confirmed, player far from destination);
    /// When Tick;
    /// Then a step action (Navigate) is returned — NOT Done.
    ///
    /// EXPECTED: PASS — existing behaviour.
    /// </summary>
    [Fact]
    public async Task S2_2_NormalPlay_NotComplete_RunsStep()
    {
        // Given
        var harness = new EngineTestHarness();

        // Normal play: IsActive == false (default)
        // Quest NOT complete
        harness.QuestState.SetQuestSequence(Q, 0);
        // Player far from the step destination so Navigate (not Interact) is emitted
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-2-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: step action produced, NOT Done
        Assert.IsNotType<EngineAction.Done>(action);
        Assert.IsNotType<EngineAction.AwaitUser>(action);
        Assert.IsType<EngineAction.Navigate>(action);
    }

    // =========================================================================
    // S2.3 — Replay active: bitmap lies true → NOT Done, runs the step (core fix)
    // =========================================================================

    /// <summary>
    /// S2.3 (replay active, bitmap lies true — NOT Done):
    /// Given IsActive == true, IsSuspended == false, AND IsQuestComplete(Q) == true (the lie);
    /// sequence 0 active with an unconfirmed TravelStep;
    /// When Tick;
    /// Then the engine produces a step action (Navigate), NOT Done.
    ///
    /// EXPECTED: FAIL by assertion — engine currently returns Done from the bitmap gate.
    /// Builder: add GetNewGamePlusState read to ResolveAction; bypass the IsQuestComplete
    /// gate when replayActive == true.
    /// </summary>
    [Fact]
    public async Task S2_3_ReplayActive_BitmapLiesTrue_NotDone_RunsStep()
    {
        // Given
        var harness = new EngineTestHarness();

        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: false));

        // The lying bitmap: quest reads Complete even though the replay is in progress
        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete);
        harness.QuestState.SetQuestSequence(Q, 0);
        // Player far from destination → Navigate
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-3-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: bitmap suppressed by IsActive; step action returned
        Assert.IsNotType<EngineAction.Done>(action);
        Assert.IsNotType<EngineAction.AwaitUser>(action);
        Assert.IsType<EngineAction.Navigate>(action);
    }

    // =========================================================================
    // S2.4 — Replay drives via live questSequence
    // =========================================================================

    /// <summary>
    /// S2.4 (replay drives via live questSequence):
    /// Given IsActive == true, IsSuspended == false, IsQuestComplete(Q) == true (lie),
    /// GetQuestSequence(Q) == 1, and the quest has a sequence-1 block with one unconfirmed step;
    /// When Tick;
    /// Then the engine dispatches the sequence-1 step's action (Navigate),
    /// proving live questSequence selects the block while the bitmap is ignored.
    ///
    /// EXPECTED: FAIL by assertion — engine currently hits Done before reaching sequence selection.
    /// </summary>
    [Fact]
    public async Task S2_4_ReplayActive_LiveQuestSequenceSelectsBlock()
    {
        // Given
        var harness = new EngineTestHarness();

        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: false));

        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete); // the lie
        harness.QuestState.SetQuestSequence(Q, 1);                  // LIVE: we are on sequence 1
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildTwoSequenceQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-4-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: sequence-1 step dispatched — Navigate to StepPos3 (100, 0, 100)
        Assert.IsNotType<EngineAction.Done>(action);
        Assert.IsNotType<EngineAction.AwaitUser>(action);
        var nav = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(100f, 0f, 100f), nav.Destination);
    }

    // =========================================================================
    // S2.5 — Replay active, all steps confirmed → Wait (not Done)
    // =========================================================================

    /// <summary>
    /// S2.5 (replay active, all steps confirmed → Wait, not Done):
    /// Given IsActive == true, IsSuspended == false, IsQuestComplete(Q) == true (lie),
    /// and the matching sequence block's only step is already confirmed (its Expect is satisfied);
    /// When Tick;
    /// Then EngineAction.Wait whose reason mentions "awaiting game sequence advance",
    /// NOT Done (D2a — v1 idles, no fragile completion heuristic).
    ///
    /// EXPECTED: FAIL by assertion — engine currently returns Done from the bitmap gate
    /// before reaching the step loop.
    /// </summary>
    [Fact]
    public async Task S2_5_ReplayActive_AllStepsConfirmed_ReturnsWaitNotDone()
    {
        // Given
        var harness = new EngineTestHarness();

        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: false));

        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete); // the lie
        harness.QuestState.SetQuestSequence(Q, 0);
        // Satisfy the step's Expect: isQuestAccepted(Q) → true
        harness.QuestState.AddAcceptedQuest(Q);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        // Quest with one step whose Expect = isQuestAccepted(Q) — satisfied above
        var quest = BuildQuestWithConfirmableStep();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-5-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: all steps confirmed → existing Wait return at QuestEngine.cs:466
        Assert.IsNotType<EngineAction.Done>(action);
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("awaiting game sequence advance", wait.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // S2.6 — Replay suspended → Wait (not Done, not AwaitUser)
    // =========================================================================

    /// <summary>
    /// S2.6 (replay suspended → Wait):
    /// Given IsActive == true, IsSuspended == true, IsQuestComplete(Q) == true;
    /// When Tick;
    /// Then EngineAction.Wait with reason mentioning "suspended";
    /// NOT Done, NOT AwaitUser.
    ///
    /// EXPECTED: FAIL by assertion — engine currently returns Done from the bitmap gate.
    /// Builder: check IsSuspended before the bitmap gate; return Wait("ng+ replay suspended").
    /// </summary>
    [Fact]
    public async Task S2_6_ReplaySuspended_ReturnsWaitNotDone()
    {
        // Given
        var harness = new EngineTestHarness();

        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: true));   // suspended

        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete); // would fire Done in normal play
        harness.QuestState.SetQuestSequence(Q, 0);

        var quest = BuildSingleStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-6-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: suspended — transient Wait, never Done or AwaitUser
        Assert.IsNotType<EngineAction.Done>(action);
        Assert.IsNotType<EngineAction.AwaitUser>(action);
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Contains("suspended", wait.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // S2.7 — Suspended then resumed continues (cursor not lost)
    // =========================================================================

    /// <summary>
    /// S2.7 (suspended then resumed continues):
    /// Tick 1: IsSuspended == true → Wait("ng+ replay suspended").
    /// Tick 2: IsSuspended == false → the step action (Navigate).
    /// The confirmed-step cursor is not cleared by a suspend tick.
    ///
    /// EXPECTED: FAIL by assertion — tick 1 currently returns Done; tick 2 never reached.
    /// </summary>
    [Fact]
    public async Task S2_7_SuspendedThenResumed_ContinuesStep()
    {
        // Given
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete); // lying bitmap
        harness.QuestState.SetQuestSequence(Q, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-7-run");

        // Tick 1: suspended
        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: true));

        var tick1 = await harness.Engine.Tick(CancellationToken.None);

        // Assert tick 1: Wait with "suspended"
        Assert.IsNotType<EngineAction.Done>(tick1);
        var wait1 = Assert.IsType<EngineAction.Wait>(tick1);
        Assert.Contains("suspended", wait1.Reason, StringComparison.OrdinalIgnoreCase);

        // Tick 2: resumed (IsSuspended = false)
        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: false));

        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert tick 2: step action — suspension is transient, step resumes
        Assert.IsNotType<EngineAction.Done>(tick2);
        Assert.IsNotType<EngineAction.AwaitUser>(tick2);
        Assert.IsType<EngineAction.Navigate>(tick2);
    }

    // =========================================================================
    // S2.8 — GetNewGamePlusState failure → fail-open to normal play
    // =========================================================================

    /// <summary>
    /// S2.8 (NG+ read failure → fail-open to normal play):
    /// Given FakeGameStateProvider configured to fail GetNewGamePlusState (adapter failure)
    /// AND IsQuestComplete(Q) == true;
    /// When Tick;
    /// Then EngineAction.Done (fail-open: the NG+ read failure must never suppress the gate).
    ///
    /// Seam used: FakeGameStateProvider.SetNewGamePlusStateFailure(reason) — minimal addition
    /// mirroring the existing SetHostileActorsFailure pattern.
    ///
    /// EXPECTED: FAIL by assertion — the engine does not yet read GetNewGamePlusState at all,
    /// so the gate fires regardless (Done). After the builder adds the read, this test pinning
    /// "fail → normal play → Done" must stay green.
    ///
    /// NOTE: This test currently PASSES because the engine fires the bitmap gate without
    /// reading NG+ state. After the builder's change adds the NG+ read with fail-open
    /// semantics, it must still return Done — so this test passes both before AND after
    /// the implementation. It is included as a specification pin.
    /// </summary>
    [Fact]
    public async Task S2_8_NgpReadFailure_FailsOpenToNormalPlay_ReturnsDone()
    {
        // Given
        var harness = new EngineTestHarness();

        // Adapter failure for GetNewGamePlusState (fail-open seam)
        harness.GameState.SetNewGamePlusStateFailure("adapter-read-fault");

        // Quest reads complete (bitmap)
        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete);
        harness.QuestState.SetQuestSequence(Q, 0);

        var quest = BuildSingleStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-8-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: fail-open — treat as IsActive=false, normal play, bitmap gate fires
        Assert.IsType<EngineAction.Done>(action);
    }

    // =========================================================================
    // S2.9 — Accept step runs as authored under replay
    // =========================================================================

    /// <summary>
    /// S2.9 (accept step runs as authored under replay):
    /// Given a quest whose first step is an AcceptStep with Expect = isQuestAccepted(Q);
    /// IsActive == true, IsSuspended == false, IsQuestComplete(Q) == true (lie),
    /// AND IsQuestAccepted(Q) == true (live signal says accepted);
    /// When Tick;
    /// Then the accept step is confirmed (skipped) and the engine proceeds to the next step
    /// (Navigate for the following TravelStep) — the engine does NOT special-case the accept step.
    ///
    /// EXPECTED: FAIL by assertion — engine currently returns Done from the bitmap gate.
    /// </summary>
    [Fact]
    public async Task S2_9_ReplayActive_AcceptStepRunsAsAuthored_IsAcceptedConfirms()
    {
        // Given
        var harness = new EngineTestHarness();

        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: false));

        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete); // the lie
        harness.QuestState.SetQuestSequence(Q, 0);
        harness.QuestState.AddAcceptedQuest(Q);          // isQuestAccepted(Q) = true (LIVE)
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f)); // far → TravelStep → Navigate

        var quest = BuildAcceptStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-9-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: accept step Expect fires (isQuestAccepted = true) → confirmed and skipped;
        //       engine falls through to the next step → Navigate
        Assert.IsNotType<EngineAction.Done>(action);
        Assert.IsNotType<EngineAction.AwaitUser>(action);
        Assert.IsType<EngineAction.Navigate>(action);
    }

    // =========================================================================
    // S2.10 — Accept step NOT prematurely completed by the lying bitmap
    // =========================================================================

    /// <summary>
    /// S2.10 (accept step NOT prematurely skipped by the lying bitmap):
    /// Given the same accept-step quest; IsActive == true, IsSuspended == false,
    /// IsQuestComplete(Q) == true (lie), BUT IsQuestAccepted(Q) == false;
    /// When Tick;
    /// Then the engine issues the accept step's action (Interact with the NPC),
    /// NOT Done, NOT a skip-via-bitmap.
    ///
    /// EXPECTED: FAIL by assertion — engine currently returns Done from the bitmap gate.
    /// </summary>
    [Fact]
    public async Task S2_10_ReplayActive_AcceptStepNotShortcutByBitmap_WhenNotAccepted()
    {
        // Given
        var harness = new EngineTestHarness();

        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: false));

        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete); // the lie
        harness.QuestState.SetQuestSequence(Q, 0);
        // isQuestAccepted(Q) == false — NOT accepted yet; accept step's Expect not yet satisfied
        // Player at the NPC position → Interact (not Navigate) for the accept step
        harness.GameState.SetPosition(AtStepPos);

        var quest = BuildAcceptStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-10-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: accept step dispatched — Interact with the accept NPC (1003987)
        Assert.IsNotType<EngineAction.Done>(action);
        Assert.IsNotType<EngineAction.AwaitUser>(action);
        Assert.IsType<EngineAction.Interact>(action);
    }

    // =========================================================================
    // S2.11 — ActiveReplayQuestId null does not affect S2 behaviour
    // =========================================================================

    /// <summary>
    /// S2.11 (ActiveReplayQuestId null does not affect S2):
    /// Given IsActive == true, IsSuspended == false, ActiveReplayQuestId == null
    /// (as the current Dalamud impl returns), IsQuestComplete(Q) == true (lie),
    /// sequence 0 unconfirmed step;
    /// When Tick;
    /// Then the step action (Navigate) — S2 does NOT read ActiveReplayQuestId,
    /// so null does not block replay gating.
    ///
    /// EXPECTED: FAIL by assertion — engine currently returns Done from the bitmap gate.
    /// </summary>
    [Fact]
    public async Task S2_11_ActiveReplayQuestIdNull_DoesNotAffectS2Behaviour()
    {
        // Given
        var harness = new EngineTestHarness();

        // ActiveReplayQuestId explicitly null (mirroring the live Dalamud impl)
        harness.GameState.SetNewGamePlusState(new NewGamePlusState(
            IsActive: true,
            CurrentChapter: null,
            IsSuspended: false,
            ActiveReplayQuestId: null));

        harness.QuestState.SetQuestStatus(Q, QuestStatus.Complete); // the lie
        harness.QuestState.SetQuestSequence(Q, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("s2-11-run");

        // When
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Then: ActiveReplayQuestId == null has no effect; replay gating on IsActive alone
        Assert.IsNotType<EngineAction.Done>(action);
        Assert.IsNotType<EngineAction.AwaitUser>(action);
        Assert.IsType<EngineAction.Navigate>(action);
    }
}
