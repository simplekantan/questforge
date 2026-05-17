using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of AcceptStep.
///
/// RED PHASE: All tests will throw NotImplementedException until Builder implements
/// the AcceptStep arm in QuestEngine.ResolveActionForStep.
///
/// Spec: docs/ACCEPT_TURNIN_PLAN.md §5, Behaviors B1-B4.
/// Design decision (Option A): AcceptStep ALWAYS emits Interact(NpcId(step.Target.NpcId)).
/// Navigation to the NPC is the responsibility of a preceding authored TravelStep.
/// </summary>
public sealed class AcceptStepTests
{
    // =========================================================================
    // B1 — AcceptStep not yet accepted: emits Interact with Target.NpcId
    // =========================================================================

    [Fact]
    public async Task AcceptStep_NotYetAccepted_EmitsInteract()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the AcceptStep arm.
         *
         * CONTRACT: Given a quest with one AcceptStep whose Target.NpcId == 1003987,
         *           AND isQuestAccepted(12345) is false (AddAcceptedQuest has NOT been called),
         *           AND the current quest sequence matches the step's containing sequence block,
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact(new NpcId(1003987)) is returned.
         *
         * BUILDER GUIDANCE: Add the following arm to QuestEngine.ResolveActionForStep:
         *   AcceptStep accept => new EngineAction.Interact(new NpcId(accept.Target.NpcId)),
         *   Insert between the TalkStep arms and the AttunementStep arm so all
         *   Interact-emitting arms are grouped together.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        // IsQuestAccepted checks the _accepted HashSet — do NOT call AddAcceptedQuest here.

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AcceptStep
            {
                Id = "accept-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
                SkipIf = new PredicateExpect { Predicate = "isQuestAccepted(12345)" },
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(12345)" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1003987), interact.Target);
    }

    // =========================================================================
    // B2 — AcceptStep.SkipIf true: step is skipped, engine returns Wait
    // =========================================================================

    [Fact]
    public async Task AcceptStep_SkipIf_AlreadyAccepted_StepIsSkipped()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the AcceptStep arm.
         * Note: if SkipIf evaluates true BEFORE dispatch, the NotImplementedException is
         * never reached; this test may pass once the predicate wiring works correctly.
         *
         * CONTRACT: Given a quest with one AcceptStep where SkipIf = isQuestAccepted(12345),
         *           AND harness.QuestState.AddAcceptedQuest(new QuestId(12345)) has been called
         *           (IsQuestAccepted checks _accepted HashSet, NOT _statuses — use AddAcceptedQuest,
         *            NOT SetQuestStatus(Accepted)),
         *           AND the current quest sequence matches the step's sequence block,
         *           When Engine.Tick is called,
         *           Then the per-step loop fires SkipIf = true, skips the step via continue,
         *                exhausts all steps (only one exists), and returns EngineAction.Wait.
         *
         * BUILDER GUIDANCE: The per-step loop in QuestEngine.ResolveAction (lines 175-183)
         *   evaluates SkipIf before calling ResolveActionForStep. When SkipIf is true, `continue`
         *   fires and dispatch is never reached. The AcceptStep arm is only exercised when both
         *   Expect and SkipIf evaluate false.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        harness.QuestState.AddAcceptedQuest(new QuestId(12345));  // makes isQuestAccepted(12345) true

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AcceptStep
            {
                Id = "accept-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
                SkipIf = new PredicateExpect { Predicate = "isQuestAccepted(12345)" },
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(12345)" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — SkipIf fired; step skipped; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B3 — AcceptStep.Expect met after an Interact: step completes
    // =========================================================================

    [Fact]
    public async Task AcceptStep_ExpectMet_AfterInteract_StepCompletes()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the AcceptStep arm.
         *
         * CONTRACT: Given a quest with one AcceptStep where Expect = isQuestAccepted(12345),
         *           AND the initial state has isQuestAccepted(12345) == false,
         *           When Engine.Tick is called once → returns Interact(NpcId(1003987)),
         *           AND the test then simulates the in-game acceptance by calling
         *               harness.QuestState.AddAcceptedQuest(new QuestId(12345))
         *               (IsQuestAccepted checks _accepted HashSet, not _statuses),
         *           When Engine.Tick is called a second time,
         *           Then the per-step loop short-circuits via Expect evaluating true;
         *                no further steps exist; EngineAction.Wait is returned.
         *
         * BUILDER GUIDANCE: The per-step loop checks step.Expect before dispatch.
         *   After AddAcceptedQuest is called, isQuestAccepted(12345) evaluates true,
         *   so the loop calls `continue` and the step is considered complete.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        // Quest NOT accepted initially — isQuestAccepted(12345) == false

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AcceptStep
            {
                Id = "accept-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(12345)" }
                // SkipIf is null — Expect only
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: isQuestAccepted == false → Expect false → dispatch runs → Interact
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var interact = Assert.IsType<EngineAction.Interact>(tick1);
        Assert.Equal(new NpcId(1003987), interact.Target);

        // Simulate the in-game quest acceptance completing
        harness.QuestState.AddAcceptedQuest(new QuestId(12345));

        // Tick 2: isQuestAccepted == true → Expect fires → step skipped → Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert — postcondition met; step done; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(tick2);
    }

    // =========================================================================
    // B4 — AcceptStep.Expect unmet: engine retries Interact on next tick
    // =========================================================================

    [Fact]
    public async Task AcceptStep_ExpectUnmet_RetriesInteract()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the AcceptStep arm.
         *
         * CONTRACT: Given B1's setup with Expect = isQuestAccepted(12345),
         *           AND the test does NOT call AddAcceptedQuest between ticks
         *               (in-game quest acceptance has not occurred),
         *           When Engine.Tick is called twice,
         *           Then both ticks return EngineAction.Interact(new NpcId(1003987)).
         *           This is the standard stateless retry contract — the engine re-reads
         *           adapter state on every tick.
         *
         * BUILDER GUIDANCE: The engine is stateless per tick. No counter is incremented
         *   (recovery ladder is out of scope for this phase per spec §7). As long as
         *   isQuestAccepted remains false, the step is never skipped and dispatch fires
         *   Interact on every tick. This is correct and expected retry behavior.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        // Quest NOT accepted — will not change between ticks

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AcceptStep
            {
                Id = "accept-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1003987, Zone: 182, Position: new Position3(35.56f, 4f, -151.18f)),
                Expect = new PredicateExpect { Predicate = "isQuestAccepted(12345)" }
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: not accepted → Interact
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var interact1 = Assert.IsType<EngineAction.Interact>(tick1);
        Assert.Equal(new NpcId(1003987), interact1.Target);

        // Do NOT call AddAcceptedQuest — simulate a failed or pending in-game acceptance

        // Tick 2: still not accepted → Interact again (stateless retry)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var interact2 = Assert.IsType<EngineAction.Interact>(tick2);
        Assert.Equal(new NpcId(1003987), interact2.Target);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "AcceptStep Test Quest",
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
}
