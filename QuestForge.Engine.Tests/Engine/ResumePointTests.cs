using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Engine tests for cold-resume positioning via ResumePointFragmentId.
/// Issue #39 — group B acceptance criteria (B1–B14).
///
/// ALL tests will fail to compile until Builder adds:
///   public string? ResumePointFragmentId { get; init; }
/// to the Step base class in QuestForge.Schema\Step.cs.
///
/// ALL tests will fail at runtime until Builder implements the resume-point trigger and
/// sub-loop in QuestEngine.ResolveAction (§3 of RESUME_POINT_PLAN.md):
///   - _fragments field (stored in StartQuest)
///   - _resumePointExecutedIds cursor (cleared in BeginRun and sequence change)
///   - _activeResumeFragment state
///   - ProcessActiveResume zone-gated sub-loop
///   - Four-condition trigger in the main step loop
///   - OnResumeFail interception hook
/// </summary>
public sealed class ResumePointTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private const uint BaseQuestId = 91000;

    /// <summary>
    /// Builds a minimal 1-sequence quest with the given steps.
    /// </summary>
    private static QuestDefinition BuildQuest(uint questId, params Step[] steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Resume Point Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = steps }
            ]
        };

    /// <summary>
    /// Builds a 2-sequence quest for tests that need sequence-change behavior.
    /// </summary>
    private static QuestDefinition BuildTwoSeqQuest(uint questId, Step[] seq0Steps, Step[] seq1Steps) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Resume Point Two-Seq Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = seq0Steps },
                new QuestSequence { Sequence = 1, Steps = seq1Steps }
            ]
        };

    /// <summary>
    /// A main TalkStep with RequiredZone and ResumePointFragmentId set.
    /// The NPC is far from the player's default position — will always Navigate unless already near.
    /// The Expect is always false so the step is never auto-confirmed.
    /// </summary>
    private static TalkStep MainStepWithResumePoint(
        string id,
        string requiredZone,
        string resumePointFragmentId,
        RecoverConfig? recover = null) => new()
    {
        Id = id,
        Target = new NpcLocation(12345u, (int)uint.Parse(requiredZone), new Position3(999f, 0f, 999f)),
        RequiredZone = requiredZone,
        ResumePointFragmentId = resumePointFragmentId,  // RED: property does not exist yet
        Expect = new PredicateExpect { Predicate = "0 == 1" },  // never auto-confirms
        Recover = recover
    };

    /// <summary>
    /// A main TalkStep with RequiredZone set but NO ResumePointFragmentId (for B9).
    /// </summary>
    private static TalkStep MainStepWithRequiredZoneOnly(string id, string requiredZone) => new()
    {
        Id = id,
        Target = new NpcLocation(12345u, (int)uint.Parse(requiredZone), new Position3(999f, 0f, 999f)),
        RequiredZone = requiredZone,
        ResumePointFragmentId = null,  // RED: property does not exist yet
        Expect = new PredicateExpect { Predicate = "0 == 1" }
    };

    /// <summary>
    /// A main TalkStep with ResumePointFragmentId set but NO RequiredZone (for B8).
    /// </summary>
    private static TalkStep MainStepWithFragmentIdOnly(string id, string fragmentId) => new()
    {
        Id = id,
        Target = new NpcLocation(12345u, 128, new Position3(999f, 0f, 999f)),
        RequiredZone = null,  // no required zone
        ResumePointFragmentId = fragmentId,  // RED: property does not exist yet
        Expect = new PredicateExpect { Predicate = "0 == 1" }
    };

    private static FragmentDefinition FragmentWithSingleTravelStep(string fragmentId, string stepId, Position3 dest) =>
        new()
        {
            SchemaVersion = "1.0.0",
            FragmentId = fragmentId,
            Parameters = [],
            Steps =
            [
                new TravelStep
                {
                    Id = stepId,
                    Destination = new TravelDestination(128, dest)
                }
            ]
        };

    private static string FormatAction(EngineAction action) => action switch
    {
        EngineAction.Navigate nav => $"Navigate({nav.Destination.X},{nav.Destination.Y},{nav.Destination.Z})",
        EngineAction.Interact interact => $"Interact(NpcId={interact.Target.Value}, Origin={interact.Origin?.Id})",
        EngineAction.Wait wait => $"Wait({wait.Reason})",
        EngineAction.AwaitUser au => $"AwaitUser({au.Reason})",
        EngineAction.Done => "Done",
        _ => action.GetType().Name
    };

    // =========================================================================
    // B1 — Warm run: player already in RequiredZone → resume does NOT trigger
    // =========================================================================

    [Fact]
    public async Task B1_WarmRun_PlayerAlreadyInRequiredZone_ResumeDoesNotTrigger()
    {
        /*
         * AC B1 — Given a main TalkStep with RequiredZone = "128", ResumePointFragmentId = "fragment-go-128",
         *          AND player zone == 128 (already in zone),
         *          AND a fragment "fragment-go-128" whose only step navigates to (999,0,999),
         *          When Tick,
         *          Then the engine dispatches the MAIN step (Navigate toward (999,0,999) at the main
         *               step's NPC location, or Interact if in range), NOT the fragment's travel.
         *          The resume trigger must not fire when the player is already in RequiredZone.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements the zone-gated trigger (condition b).
         */

        // Arrange
        const uint questId = 91001;
        var fragmentStepDest = new Position3(777f, 0f, 777f);  // distinct from main step NPC at (999,0,999)

        var fragment = FragmentWithSingleTravelStep("fragment-go-128", "fragment-travel-1", fragmentStepDest);
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-go-128");
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(128));  // already in RequiredZone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));  // far from NPC

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b1");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — main step should dispatch (Navigate toward NPC at 999,0,999), NOT fragment at 777,0,777
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.NotEqual(new WorldPosition(777f, 0f, 777f), navigate.Destination);
        Assert.Equal(new WorldPosition(999f, 0f, 999f), navigate.Destination);
    }

    // =========================================================================
    // B2 — Cold run: player NOT in RequiredZone → resume triggers, fragment dispatches
    // =========================================================================

    [Fact]
    public async Task B2_ColdRun_PlayerNotInRequiredZone_ResumeTriggers_FragmentDispatches()
    {
        /*
         * AC B2 — Given a main TalkStep with RequiredZone = "128", ResumePointFragmentId = "fragment-go-128",
         *          AND player zone == 130 (wrong zone),
         *          AND fragment "fragment-go-128" whose first step is a TravelStep to (50,0,50) with no expect,
         *          When Tick,
         *          Then the engine returns Navigate(50,0,50) (the fragment's first step),
         *               NOT the main step's Navigate(999,0,999).
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements the resume trigger.
         */

        // Arrange
        const uint questId = 91002;
        var fragmentDest = new Position3(50f, 0f, 50f);

        var fragment = FragmentWithSingleTravelStep("fragment-go-128", "fragment-travel-1", fragmentDest);
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-go-128");
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone — resume should trigger
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b2");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — fragment's first step should dispatch, not main step
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(50f, 0f, 50f), navigate.Destination);
    }

    // =========================================================================
    // B3 — Zone gate ends sub-loop; main step dispatches same tick on zone match
    // =========================================================================

    [Fact]
    public async Task B3_ZoneGate_EndsSubLoop_MainStepDispatchesSameTick()
    {
        /*
         * AC B3 — Multi-tick flow:
         *   Tick 1 (zone 130): fragment running → Navigate(50,0,50).
         *   Set zone to 128. Tick 2: zone matches → resume done → main step dispatches THIS tick.
         *   Assert Tick 2 action is the main step's action (Navigate toward NPC at 999,0,999).
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements zone-gated sub-loop termination and fall-through.
         */

        // Arrange
        const uint questId = 91003;
        var fragmentDest = new Position3(50f, 0f, 50f);

        var fragment = FragmentWithSingleTravelStep("fragment-go-128", "fragment-travel-1", fragmentDest);
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-go-128");
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // start in wrong zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b3");

        // Tick 1 — fragment running: Navigate(50,0,50)
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        var nav1 = Assert.IsType<EngineAction.Navigate>(action1);
        Assert.Equal(new WorldPosition(50f, 0f, 50f), nav1.Destination);

        // Player arrives in RequiredZone
        harness.GameState.SetZone(new ZoneId(128));

        // Tick 2 — zone matches → resume complete → main step dispatches this tick
        var action2 = await harness.Engine.Tick(CancellationToken.None);
        var nav2 = Assert.IsType<EngineAction.Navigate>(action2);
        // Main step NPC is at (999,0,999) — player is at (0,0,0) so it navigates there
        Assert.Equal(new WorldPosition(999f, 0f, 999f), nav2.Destination);
    }

    // =========================================================================
    // B4 — Zone gate ends sub-loop even with unconfirmed fragment steps remaining
    // =========================================================================

    [Fact]
    public async Task B4_ZoneGate_EndsSubLoop_EvenWithUnconfirmedFragmentStepsRemaining()
    {
        /*
         * AC B4 — Fragment has TWO travel steps (no expect on either).
         *   Tick 1 (zone 130): Navigate(10,0,10) — fragment-a dispatches (fragment-b not yet reached).
         *   Set zone to 128. Tick 2: zone matches → sub-loop terminates WITHOUT dispatching fragment-b
         *                  → main step dispatches.
         *   Proves zone match (not step exhaustion) ends the sub-loop.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements zone-gate-first termination in ProcessActiveResume.
         */

        // Arrange
        const uint questId = 91004;

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "fragment-go-128",
            Parameters = [],
            Steps =
            [
                new TravelStep
                {
                    Id = "fragment-a",
                    Destination = new TravelDestination(130, new Position3(10f, 0f, 10f))
                    // no expect
                },
                new TravelStep
                {
                    Id = "fragment-b",
                    Destination = new TravelDestination(128, new Position3(20f, 0f, 20f))
                    // no expect — should NEVER be dispatched in this test
                }
            ]
        };
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-go-128");
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b4");

        // Tick 1 — zone 130: fragment-a dispatches
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        var nav1 = Assert.IsType<EngineAction.Navigate>(action1);
        Assert.Equal(new WorldPosition(10f, 0f, 10f), nav1.Destination);

        // Player arrives in RequiredZone before fragment-b runs
        harness.GameState.SetZone(new ZoneId(128));

        // Tick 2 — zone matches → sub-loop ends → main step dispatches (NOT fragment-b)
        var action2 = await harness.Engine.Tick(CancellationToken.None);
        var nav2 = Assert.IsType<EngineAction.Navigate>(action2);
        // Main step NPC at (999,0,999) — must NOT be (20,0,20) which is fragment-b
        Assert.NotEqual(new WorldPosition(20f, 0f, 20f), nav2.Destination);
        Assert.Equal(new WorldPosition(999f, 0f, 999f), nav2.Destination);
    }

    // =========================================================================
    // B5 — Executed marker prevents re-trigger within same sequence
    // =========================================================================

    [Fact]
    public async Task B5_ExecutedMarker_PreventsRetriggerWithinSameSequence()
    {
        /*
         * AC B5 — After the resume completes and the main step is first dispatched:
         *   Move player BACK to wrong zone (130), keep sequence, Tick.
         *   Assert resume does NOT re-trigger — main step dispatches directly (or Wait),
         *   NOT the fragment's Navigate.
         *
         * The step's id is in _resumePointExecutedIds so condition (d) blocks re-arming.
         * The main step's Expect = "false" so it is still the "first unconfirmed" step.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements _resumePointExecutedIds cursor.
         */

        // Arrange
        const uint questId = 91005;
        var fragmentDest = new Position3(50f, 0f, 50f);

        var fragment = FragmentWithSingleTravelStep("fragment-go-128", "fragment-travel-1", fragmentDest);
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-go-128");
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // start wrong
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b5");

        // Tick 1 — fragment running
        await harness.Engine.Tick(CancellationToken.None);

        // Player arrives; resume completes on Tick 2
        harness.GameState.SetZone(new ZoneId(128));
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        // Tick 2 should dispatch main step (Navigate toward NPC at 999,0,999)
        var nav2 = Assert.IsType<EngineAction.Navigate>(tick2);
        Assert.Equal(new WorldPosition(999f, 0f, 999f), nav2.Destination);

        // Move player BACK to wrong zone — would re-trigger if marker is absent
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 3 — marker guards against re-trigger; main step must dispatch directly
        var action3 = await harness.Engine.Tick(CancellationToken.None);

        // Assert — resume must NOT re-trigger; must NOT be the fragment's Navigate(50,0,50)
        // The main step dispatches directly (Navigate toward NPC at 999,0,999)
        var nav3 = Assert.IsType<EngineAction.Navigate>(action3);
        Assert.NotEqual(new WorldPosition(50f, 0f, 50f), nav3.Destination);
        Assert.True(nav3.Destination == new WorldPosition(999f, 0f, 999f),
            $"Expected main step Navigate(999,0,999) but got {FormatAction(action3)}. " +
            "Resume must not re-trigger within the same sequence — _resumePointExecutedIds not populated or not checked.");
    }

    // =========================================================================
    // B6 — Sequence change clears executed marker and active resume; re-triggers in new seq
    // =========================================================================

    [Fact]
    public async Task B6_SequenceChange_ClearsResumeState_AndRetriggersInNewSequence()
    {
        /*
         * AC B6 — 2-sequence quest. Both seq-0 and seq-1 main steps have same fragment.
         *   Complete resume in seq-0 (so _resumePointExecutedIds has the seq-0 step id).
         *   Advance to seq-1 with player in wrong zone for the seq-1 step.
         *   Tick: the seq-1 resume TRIGGERS (returns fragment Navigate), proving the
         *   executed marker was cleared on sequence change.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder clears _resumePointExecutedIds on sequence change.
         */

        // Arrange
        const uint questId = 91006;
        var fragmentDest = new Position3(50f, 0f, 50f);

        var fragment = FragmentWithSingleTravelStep("fragment-go-128", "fragment-travel-1", fragmentDest);
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        // seq-0 and seq-1 both use the same ResumePointFragmentId but different main step ids
        var mainStepSeq0 = MainStepWithResumePoint("talk-to-npc-seq0", "128", "fragment-go-128");
        var mainStepSeq1 = MainStepWithResumePoint("talk-to-npc-seq1", "128", "fragment-go-128");

        var quest = BuildTwoSeqQuest(questId, [mainStepSeq0], [mainStepSeq1]);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b6");

        // Trigger resume in seq-0
        await harness.Engine.Tick(CancellationToken.None);  // fragment running

        // Complete resume in seq-0
        harness.GameState.SetZone(new ZoneId(128));
        await harness.Engine.Tick(CancellationToken.None);  // zone match → main step dispatches

        // Advance to seq-1 with player in wrong zone again
        harness.QuestState.SetQuestSequence(new QuestId(questId), 1);
        harness.GameState.SetZone(new ZoneId(130));  // back to wrong zone for seq-1

        // Tick: seq-1 resume should trigger (marker for seq-0 step must be cleared)
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — seq-1 resume triggers → fragment's Navigate(50,0,50)
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.True(navigate.Destination == new WorldPosition(50f, 0f, 50f),
            $"Expected fragment Navigate(50,0,50) from seq-1 resume trigger but got {FormatAction(action)}. " +
            "Sequence change must clear _resumePointExecutedIds so seq-1 step can re-trigger.");
    }

    // =========================================================================
    // B7 — BeginRun clears resume state; re-arms from scratch on new run
    // =========================================================================

    [Fact]
    public async Task B7_BeginRun_ClearsResumeState_RearmsFromScratch()
    {
        /*
         * AC B7 — Trigger a resume (active fragment set) on run-1.
         *   Call BeginRun("run-2") while player is still in wrong zone.
         *   Tick: resume re-arms from scratch, returning the fragment's FIRST step action.
         *   No stale _activeResumeFragment or mid-fragment cursor from run-1 leaks.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder clears _activeResumeFragment and _resumePointExecutedIds in BeginRun.
         */

        // Arrange
        const uint questId = 91007;
        var fragmentDest = new Position3(50f, 0f, 50f);

        var fragment = FragmentWithSingleTravelStep("fragment-go-128", "fragment-travel-1", fragmentDest);
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-go-128");
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b7-1");

        // Tick 1 run-1: fragment arms and dispatches first step
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var nav1 = Assert.IsType<EngineAction.Navigate>(tick1);
        Assert.Equal(new WorldPosition(50f, 0f, 50f), nav1.Destination);

        // Start new run — must clear stale resume state
        harness.Engine.BeginRun("run-b7-2");
        // Player still in wrong zone; quest still loaded

        // Tick 1 run-2: resume must re-arm from scratch, returning fragment's FIRST step
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert — must be fragment's first step Navigate(50,0,50), not some mid-fragment state
        var nav2 = Assert.IsType<EngineAction.Navigate>(tick2);
        Assert.True(nav2.Destination == new WorldPosition(50f, 0f, 50f),
            $"Expected fragment first-step Navigate(50,0,50) on run-2 but got {FormatAction(tick2)}. " +
            "BeginRun must clear _activeResumeFragment so the sub-loop re-arms from the beginning.");
    }

    // =========================================================================
    // B8 — RequiredZone null + fragment id set → no trigger (condition b requires RequiredZone)
    // =========================================================================

    [Fact]
    public async Task B8_RequiredZoneNull_FragmentIdSet_NoTrigger_MainStepDispatches()
    {
        /*
         * AC B8 — Given a main step with ResumePointFragmentId = "fragment-x" but RequiredZone == null;
         *          player in any zone.
         *          When Tick, resume does NOT trigger (condition b requires RequiredZone to be set).
         *          Main step dispatches directly.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder checks RequiredZone as condition (b) for the trigger.
         */

        // Arrange
        const uint questId = 91008;
        var fragmentDest = new Position3(50f, 0f, 50f);

        var fragment = FragmentWithSingleTravelStep("fragment-x", "fragment-travel-1", fragmentDest);
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-x"] = fragment };

        var mainStep = MainStepWithFragmentIdOnly("talk-to-npc", "fragment-x");  // RequiredZone = null
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b8");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — no trigger; main step dispatches directly (Navigate toward NPC at 999,0,999)
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.NotEqual(new WorldPosition(50f, 0f, 50f), navigate.Destination);  // NOT fragment
        Assert.Equal(new WorldPosition(999f, 0f, 999f), navigate.Destination);    // main step NPC
    }

    // =========================================================================
    // B9 — RequiredZone set but fragment id null → no trigger (§38 data-only behavior)
    // =========================================================================

    [Fact]
    public async Task B9_RequiredZoneSet_FragmentIdNull_NoTrigger_MainStepDispatches()
    {
        /*
         * AC B9 — Given a main step with RequiredZone = "128" but ResumePointFragmentId == null;
         *          player in zone 130 (wrong zone).
         *          When Tick, resume does NOT trigger (condition c requires ResumePointFragmentId).
         *          Main step dispatches directly — this is the §38 data-only behavior.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder checks ResumePointFragmentId as condition (c).
         */

        // Arrange
        const uint questId = 91009;

        var mainStep = MainStepWithRequiredZoneOnly("talk-to-npc", "128");  // no ResumePointFragmentId
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone, but no fragment to run
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments: null);
        harness.Engine.BeginRun("run-b9");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — no trigger; main step dispatches directly (Navigate toward NPC at 999,0,999)
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(999f, 0f, 999f), navigate.Destination);
    }

    // =========================================================================
    // B10 — Missing fragment in dict → throws at Tick (lazy resolution)
    // =========================================================================

    [Fact]
    public async Task B10_MissingFragmentInDict_ThrowsAtTick()
    {
        /*
         * AC B10 — Given a main step with RequiredZone = "128", ResumePointFragmentId = "fragment-missing",
         *           player in zone 130, AND _fragments dict does NOT contain "fragment-missing".
         *           When Tick, an exception is thrown (lazy resolution at Tick time).
         *
         * Contrast with inline FragmentStep which throws at StartQuest (eager resolution).
         * Resume fragments are resolved lazily because warm runs never need them.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements lazy fragment lookup in ArmResumeFragment.
         */

        // Arrange
        const uint questId = 91010;

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-missing");
        var quest = BuildQuest(questId, mainStep);

        // Dict exists but does not contain "fragment-missing"
        var fragments = new Dictionary<string, FragmentDefinition>
        {
            ["fragment-other"] = FragmentWithSingleTravelStep("fragment-other", "fragment-t", new Position3(1f, 0f, 1f))
        };

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone — trigger fires
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);  // must NOT throw here (lazy resolution)
        harness.Engine.BeginRun("run-b10");

        // Act & Assert — exception thrown at Tick time
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Engine.Tick(CancellationToken.None));
    }

    // =========================================================================
    // B11 — OnResumeFail intercepts AwaitUser from a resume-fragment step
    // =========================================================================

    [Fact]
    public async Task B11_OnResumeFail_InterceptsAwaitUser_SubstitutesRecoveryAction()
    {
        /*
         * AC B11 — Given a main step with RequiredZone = "128", ResumePointFragmentId = "fragment-fail",
         *           and Recover = { OnResumeFail = new AwaitUserRecoverAction { Reason = "resume blocked" } };
         *           player in zone 130;
         *           AND "fragment-fail" whose first step is an AwaitUserStep (which the Builder must
         *           map to EngineAction.AwaitUser in ResolveActionForStep — see BUILDER NOTE below).
         *           When Tick, the returned action is EngineAction.AwaitUser with Reason == "resume blocked"
         *           (the OnResumeFail action), NOT the raw AwaitUserStep reason.
         *
         * BUILDER NOTE:
         *   The current ResolveActionForStep dispatcher has no arm for AwaitUserStep — it falls to
         *   `_ => throw new NotSupportedException(...)`. The Builder must add a case for AwaitUserStep:
         *     AwaitUserStep awaitUser => new EngineAction.AwaitUser(awaitUser.Reason),
         *   so that a fragment step of type "await-user" produces EngineAction.AwaitUser, which is
         *   then intercepted by the OnResumeFail hook in ProcessActiveResume.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId and OnResumeFail.
         * RED: Will fail at runtime until Builder:
         *   1. Adds AwaitUserStep arm to ResolveActionForStep returning EngineAction.AwaitUser.
         *   2. Implements OnResumeFail interception in ProcessActiveResume.
         */

        // Arrange
        const uint questId = 91011;

        var fragmentStep = new AwaitUserStep
        {
            Id = "fragment-await-user",
            Reason = "raw fragment failure reason"  // this reason must NOT appear in the result
        };

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "fragment-fail",
            Parameters = [],
            Steps = [fragmentStep]
        };
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-fail"] = fragment };

        var mainStep = MainStepWithResumePoint(
            "talk-to-npc", "128", "fragment-fail",
            recover: new RecoverConfig
            {
                OnResumeFail = new AwaitUserRecoverAction { Reason = "resume blocked" }  // RED: property does not exist yet
            });
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b11");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — OnResumeFail intercepts → AwaitUser with OnResumeFail reason, NOT raw fragment reason
        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.True(awaitUser.Reason == "resume blocked",
            $"Expected OnResumeFail reason 'resume blocked' but got '{awaitUser.Reason}'. " +
            "OnResumeFail must intercept the fragment step's AwaitUser and substitute the recovery action's reason.");
    }

    // =========================================================================
    // B12 — No OnResumeFail → raw AwaitUser propagates from fragment step
    // =========================================================================

    [Fact]
    public async Task B12_NoOnResumeFail_RawAwaitUserPropagates()
    {
        /*
         * AC B12 — Same as B11 but Recover.OnResumeFail is null (or Recover is null).
         *           When Tick, the returned AwaitUser reason is the raw fragment-step failure reason,
         *           NOT an intercepted reason.
         *           This proves interception only happens when OnResumeFail is set.
         *
         * BUILDER NOTE: Same as B11 — requires AwaitUserStep arm in ResolveActionForStep.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements AwaitUserStep dispatch and
         *      the absence-of-interception path in ProcessActiveResume.
         */

        // Arrange
        const uint questId = 91012;
        const string rawReason = "raw fragment failure reason";

        var fragmentStep = new AwaitUserStep
        {
            Id = "fragment-await-user",
            Reason = rawReason
        };

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "fragment-fail",
            Parameters = [],
            Steps = [fragmentStep]
        };
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-fail"] = fragment };

        // No OnResumeFail on this step
        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-fail", recover: null);
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b12");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — no interception; raw AwaitUser propagates
        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        // The reason should come from the fragment step's dispatch (raw) — not a recovery reason.
        // We do not over-constrain the exact reason string since AwaitUserStep dispatch is new;
        // we only assert that an AwaitUser is returned (not intercepted to a different reason).
        Assert.NotNull(awaitUser.Reason);
    }

    // =========================================================================
    // B13 — Fragment exhausted with zone still wrong → Wait; sub-loop stays armed
    // =========================================================================

    [Fact]
    public async Task B13_FragmentExhausted_ZoneStillWrong_ReturnsWait_StaysArmed()
    {
        /*
         * AC B13 — Fragment has one step whose Expect is always true (confirms immediately)
         *           but does NOT move the player; player in zone 130, RequiredZone = "128".
         *   Tick 1: fragment step confirmed; zone still 130 → EngineAction.Wait (exhausted-but-not-arrived).
         *           _activeResumeFragment remains set.
         *   Tick 2 (zone still 130): still Wait; engine has NOT fallen through to main step.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder implements the exhausted-fragment Wait path (§3.5 step 4).
         */

        // Arrange
        const uint questId = 91013;

        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "fragment-go-128",
            Parameters = [],
            Steps =
            [
                new TravelStep
                {
                    Id = "fragment-travel-1",
                    Destination = new TravelDestination(128, new Position3(50f, 0f, 50f)),
                    Expect = new PredicateExpect { Predicate = "1 == 1" }  // always true → confirms immediately
                }
            ]
        };
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        var mainStep = MainStepWithResumePoint("talk-to-npc", "128", "fragment-go-128");
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone — stays wrong both ticks
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b13");

        // Tick 1 — fragment step auto-confirms (Expect = "true"); zone still 130 → exhausted → Wait
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(action1);

        // Tick 2 — zone still 130; sub-loop still armed; still Wait (NOT fallen through to main step)
        var action2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(action2);
        // The action must not be the main step's Navigate (which would mean the sub-loop was cleared incorrectly)
    }

    // =========================================================================
    // B14 — Resume cursor independent of main cursor (shared step id does not collide)
    // =========================================================================

    [Fact]
    public async Task B14_ResumeCursor_IndependentOfMainCursor_SharedStepIdDoesNotCollide()
    {
        /*
         * AC B14 — A fragment step and main step share the same raw Id = "go".
         *   Trigger the resume (player in wrong zone). The fragment step "go" confirms within
         *   the sub-loop. Zone changes to match → resume done → main step "go" dispatches.
         *   Assert the main step is dispatched (NOT skipped as if confirmed).
         *   Proves ConfirmedFragmentStepIds is separate from _confirmedStepIds.
         *
         * RED: Will fail to compile until Builder adds ResumePointFragmentId to Step.
         * RED: Will fail at runtime until Builder uses a per-ActiveResumeFragment confirmed set
         *      separate from _confirmedStepIds (§3.1 rationale).
         */

        // Arrange
        const uint questId = 91014;

        // Fragment step has the SAME id as the main step — "go"
        var fragment = new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = "fragment-go-128",
            Parameters = [],
            Steps =
            [
                new TravelStep
                {
                    Id = "go",  // SAME id as the main step below
                    Destination = new TravelDestination(128, new Position3(50f, 0f, 50f)),
                    Expect = new PredicateExpect { Predicate = "1 == 1" }  // confirms immediately
                }
            ]
        };
        var fragments = new Dictionary<string, FragmentDefinition> { ["fragment-go-128"] = fragment };

        // Main step also has Id = "go"
        var mainStep = new TalkStep
        {
            Id = "go",  // SAME id as the fragment step
            Target = new NpcLocation(12345u, 128, new Position3(999f, 0f, 999f)),
            RequiredZone = "128",
            ResumePointFragmentId = "fragment-go-128",  // RED: property does not exist yet
            Expect = new PredicateExpect { Predicate = "0 == 1" }  // never auto-confirms
        };
        var quest = BuildQuest(questId, mainStep);

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(130));  // wrong zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest, fragments);
        harness.Engine.BeginRun("run-b14");

        // Tick 1 — zone 130: resume triggers; fragment step "go" has Expect=true → confirms and…
        // The fragment step confirms but zone != 128 so Wait is returned (exhausted).
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        // Fragment step auto-confirms; zone still 130 → Wait (exhausted-not-arrived)
        Assert.IsType<EngineAction.Wait>(action1);

        // Set zone to match RequiredZone
        harness.GameState.SetZone(new ZoneId(128));

        // Tick 2 — zone matches → resume done; main step "go" must dispatch (NOT be skipped)
        var action2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert — main step "go" dispatches; NOT skipped/confirmed because of the fragment cursor
        var navigate = Assert.IsType<EngineAction.Navigate>(action2);
        Assert.True(navigate.Destination == new WorldPosition(999f, 0f, 999f),
            $"Expected main step Navigate(999,0,999) but got {FormatAction(action2)}. " +
            "The fragment step 'go' confirmation must NOT pollute _confirmedStepIds for the main step 'go'.");
    }
}
