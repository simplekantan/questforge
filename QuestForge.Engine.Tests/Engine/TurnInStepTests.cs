using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of TurnInStep.
///
/// RED PHASE: All tests will throw NotImplementedException until Builder implements
/// the TurnInStep arm in QuestEngine.ResolveActionForStep.
///
/// Spec: docs/ACCEPT_TURNIN_PLAN.md §5, Behaviors B5-B8.
/// Design decision (Option A): TurnInStep ALWAYS emits Interact(NpcId(step.Target.NpcId)).
/// Navigation to the NPC is the responsibility of a preceding authored TravelStep.
/// DialogueChoices field exists on TurnInStep but is ignored by the engine in this phase (spec §7).
/// </summary>
public sealed class TurnInStepTests
{
    // =========================================================================
    // B5 — TurnInStep not yet complete: emits Interact with Target.NpcId
    // =========================================================================

    [Fact]
    public async Task TurnInStep_NotYetComplete_EmitsInteract()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the TurnInStep arm.
         *
         * CONTRACT: Given a quest with one TurnInStep whose Target.NpcId == 1000327,
         *           AND isQuestComplete(12345) is false (SetQuestStatus has NOT been called
         *               with QuestStatus.Complete for this quest),
         *           AND the current quest sequence matches the step's containing sequence block,
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact(new NpcId(1000327)) is returned.
         *
         * BUILDER GUIDANCE: Add the following arm to QuestEngine.ResolveActionForStep:
         *   TurnInStep turnIn => new EngineAction.Interact(new NpcId(turnIn.Target.NpcId)),
         *   Insert between the AcceptStep arm and the AttunementStep arm so all
         *   Interact-emitting arms are grouped together.
         *   The DialogueChoices field on TurnInStep is NOT consulted here — it is metadata
         *   for a future phase (spec §7, deferred item 2).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        // isQuestComplete checks _statuses[quest] == Complete — no SetQuestStatus call means false
        // Player at NPC position: XZ distance = 0 → within default 3.0f → Interact
        harness.GameState.SetPosition(new WorldPosition(21.84f, 7f, -81.13f));

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new TurnInStep
            {
                Id = "turn-in-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1000327, Zone: 130, Position: new Position3(21.84f, 7f, -81.13f)),
                SkipIf = new PredicateExpect { Predicate = "isQuestComplete(12345)" },
                Expect = new PredicateExpect { Predicate = "isQuestComplete(12345)" },
                DialogueChoices = []
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1000327), interact.Target);
    }

    // =========================================================================
    // B6 — TurnInStep.SkipIf true: step is skipped, engine returns Wait
    // =========================================================================

    [Fact]
    public async Task TurnInStep_SkipIf_AlreadyComplete_StepIsSkipped()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the TurnInStep arm.
         * Note: if SkipIf evaluates true BEFORE dispatch, the NotImplementedException is
         * never reached; this test may pass once the predicate wiring works correctly.
         *
         * CONTRACT: Given a quest (engine quest ID = 12345) with one TurnInStep where
         *               SkipIf = isQuestComplete(99999)  — a DIFFERENT quest from the engine quest,
         *           AND harness.QuestState.SetQuestStatus(new QuestId(99999), QuestStatus.Complete)
         *               has been called for quest 99999,
         *           (IsQuestComplete checks _statuses[quest] == Complete, NOT _accepted.
         *            We use quest 99999 in the predicate so the engine's own IsQuestComplete(12345)
         *            check at the top of ResolveAction does NOT prematurely return Done — that check
         *            uses the engine's own questId, not the predicate argument.),
         *           AND the current quest sequence matches the step's sequence block,
         *           When Engine.Tick is called,
         *           Then the per-step loop fires SkipIf = true (isQuestComplete(99999) == true),
         *                skips the step via continue, exhausts all steps, and returns EngineAction.Wait.
         *
         * BUILDER GUIDANCE: The per-step loop in QuestEngine.ResolveAction (lines 175-183)
         *   evaluates SkipIf before calling ResolveActionForStep. When SkipIf is true, `continue`
         *   fires and dispatch is never reached. The TurnInStep arm is only exercised when both
         *   Expect and SkipIf evaluate false.
         *   Using a different quest ID in the predicate vs the engine quest prevents the top-level
         *   IsQuestComplete guard (line 151-153) from short-circuiting to Done before the step
         *   loop runs.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        // Mark a DIFFERENT quest (99999) as complete — isQuestComplete(99999) is what SkipIf checks.
        // Quest 12345 (engine's own quest) must NOT be marked complete, or the engine exits with Done.
        harness.QuestState.SetQuestStatus(new QuestId(99999), QuestStatus.Complete);

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new TurnInStep
            {
                Id = "turn-in-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1000327, Zone: 130, Position: new Position3(21.84f, 7f, -81.13f)),
                SkipIf = new PredicateExpect { Predicate = "isQuestComplete(99999)" },
                DialogueChoices = []
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — SkipIf fired; step skipped; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B7 — TurnInStep.Expect met after an Interact: step completes
    // =========================================================================

    [Fact]
    public async Task TurnInStep_ExpectMet_AfterInteract_StepCompletes()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the TurnInStep arm.
         *
         * CONTRACT: Given a quest (engine quest ID = 12345) with one TurnInStep where
         *               Expect = isQuestComplete(99999)  — a DIFFERENT quest from the engine quest,
         *           AND the initial state has isQuestComplete(99999) == false,
         *           When Engine.Tick is called once → returns Interact(NpcId(1000327)),
         *           AND the test then simulates the in-game side effect by calling
         *               harness.QuestState.SetQuestStatus(new QuestId(99999), QuestStatus.Complete)
         *               (IsQuestComplete checks _statuses[quest] == Complete),
         *           When Engine.Tick is called a second time,
         *           Then the per-step loop short-circuits via Expect evaluating true;
         *                no further steps exist; EngineAction.Wait is returned.
         *
         * Implementation note: We use quest 99999 in the Expect predicate rather than 12345
         * (the engine's own quest) to avoid the engine's top-level IsQuestComplete(12345) guard
         * at ResolveAction line 151-153. If that guard fires, the engine returns Done before
         * the step loop runs — masking whether the step-level Expect short-circuit is working.
         * Using a distinct predicate quest ID isolates the per-step-loop behavior under test.
         *
         * BUILDER GUIDANCE: The per-step loop checks step.Expect before dispatch.
         *   After SetQuestStatus(99999, Complete) is called, isQuestComplete(99999) evaluates true,
         *   so the loop calls `continue` and the step is considered complete.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        // Neither quest 12345 nor 99999 is complete initially
        // Player at NPC position: XZ distance = 0 → within default 3.0f → Interact
        harness.GameState.SetPosition(new WorldPosition(21.84f, 7f, -81.13f));

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new TurnInStep
            {
                Id = "turn-in-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1000327, Zone: 130, Position: new Position3(21.84f, 7f, -81.13f)),
                Expect = new PredicateExpect { Predicate = "isQuestComplete(99999)" }
                // SkipIf is null — Expect only; using 99999 avoids the engine's top-level guard
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: isQuestComplete(99999) == false → Expect false → dispatch runs → Interact
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var interact = Assert.IsType<EngineAction.Interact>(tick1);
        Assert.Equal(new NpcId(1000327), interact.Target);

        // Simulate the side-effect that makes the Expect predicate true
        harness.QuestState.SetQuestStatus(new QuestId(99999), QuestStatus.Complete);

        // Tick 2: isQuestComplete(99999) == true → Expect fires → step skipped → Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert — postcondition met; step done; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(tick2);
    }

    // =========================================================================
    // B8 — TurnInStep.Expect unmet: engine retries Interact on next tick
    // =========================================================================

    [Fact]
    public async Task TurnInStep_ExpectUnmet_RetriesInteract()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the TurnInStep arm.
         *
         * CONTRACT: Given B5's setup with Expect = isQuestComplete(12345),
         *           AND the test does NOT call SetQuestStatus(Complete) between ticks
         *               (in-game turn-in has not completed),
         *           When Engine.Tick is called twice,
         *           Then both ticks return EngineAction.Interact(new NpcId(1000327)).
         *           This is the standard stateless retry contract — the engine re-reads
         *           adapter state on every tick.
         *
         * BUILDER GUIDANCE: The engine is stateless per tick. No counter is incremented
         *   (recovery ladder is out of scope for this phase per spec §7). As long as
         *   isQuestComplete remains false, the step is never skipped and dispatch fires
         *   Interact on every tick. This is correct and expected retry behavior.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        // Quest NOT complete — will not change between ticks
        // Player at NPC position: XZ distance = 0 → within default 3.0f → Interact each tick
        harness.GameState.SetPosition(new WorldPosition(21.84f, 7f, -81.13f));

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new TurnInStep
            {
                Id = "turn-in-coming-to-uldah",
                Target = new NpcLocation(NpcId: 1000327, Zone: 130, Position: new Position3(21.84f, 7f, -81.13f)),
                Expect = new PredicateExpect { Predicate = "isQuestComplete(12345)" }
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: not complete → Interact
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var interact1 = Assert.IsType<EngineAction.Interact>(tick1);
        Assert.Equal(new NpcId(1000327), interact1.Target);

        // Do NOT call SetQuestStatus(Complete) — simulate a failed or pending in-game turn-in

        // Tick 2: still not complete → Interact again (stateless retry)
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var interact2 = Assert.IsType<EngineAction.Interact>(tick2);
        Assert.Equal(new NpcId(1000327), interact2.Target);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "TurnInStep Test Quest",
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
