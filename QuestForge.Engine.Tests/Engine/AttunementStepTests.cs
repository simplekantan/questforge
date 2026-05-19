using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;
using QuestForge.Schema;
using Xunit;
using AdaptersAetheryteId = QuestForge.Adapters.Types.AetheryteId;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of AttunementStep.
///
/// RED PHASE: All tests will throw NotImplementedException until Builder implements
/// the AttunementStep arm in QuestEngine.ResolveActionForStep.
///
/// Spec: PHASE_11B_PLAN.md §3.8, Behaviors B1-B6.
/// Uses Option A semantics: AttunementStep ALWAYS returns Interact; Location is metadata only.
/// </summary>
public sealed class AttunementStepTests
{
    // =========================================================================
    // B1 — skipIf already-attuned: step is skipped, engine advances past it
    // =========================================================================

    [Fact]
    public async Task AttunementStep_SkipIf_AlreadyAttuned_StepIsSkipped()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep dispatch.
         *
         * CONTRACT: Given a quest with one AttunementStep where:
         *             SkipIf = PredicateExpect("isAttuned(1000)")
         *             Expect = null (no postcondition)
         *           AND FakeGameStateProvider.SetAetheryteAttuned(1000, true)
         *           AND quest sequence matches the step's sequence block,
         *           When Tick,
         *           Then the engine does NOT return Interact for this step.
         *           Instead it returns Wait (all steps in current sequence are satisfied by skipIf).
         *
         * BUILDER GUIDANCE:
         *   The engine's per-step loop (QuestEngine.ResolveAction lines 166-174) evaluates SkipIf
         *   for each step. If SkipIf evaluates true, the step is skipped via `continue`.
         *   Since this is the only step and SkipIf is true, the loop exhausts all steps
         *   and returns Wait.
         *   The isAttuned predicate must be implemented (§3.3) for this to work.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.GameState.SetAetheryteAttuned(new AdaptersAetheryteId(1000), true);
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AttunementStep
            {
                Id = "attune-limsa",
                Target = new AetheryteId(1000),
                SkipIf = new PredicateExpect { Predicate = "isAttuned(1000)" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — skipIf fired; step was skipped; no more steps; engine returns Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B2 — not attuned: emits Interact with AetheryteId.Value as NpcId
    // =========================================================================

    [Fact]
    public async Task AttunementStep_NotAttuned_EmitsInteract()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep dispatch.
         *
         * CONTRACT: Given a quest with one AttunementStep (Target.Value == 1000),
         *           AND isAttuned(1000) == false (not attuned),
         *           When Tick,
         *           Then EngineAction.Interact(new NpcId(1000)) is returned.
         *
         * BUILDER GUIDANCE: The dispatch arm:
         *   AttunementStep attune => new EngineAction.Interact(new NpcId(attune.Target.Value))
         *   NpcId is in Adapters.Types; AetheryteId.Value (uint) is reused as the NPC identifier
         *   for the Interact action. Lumina lookup is out of scope for Phase 11B.
         */

        // Arrange
        var harness = new EngineTestHarness();
        // Not attuned (default state)
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AttunementStep
            {
                Id = "attune-limsa",
                Target = new QuestForge.Schema.AetheryteId(1000),
                SkipIf = new PredicateExpect { Predicate = "isAttuned(1000)" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1000), interact.Target);
    }

    // =========================================================================
    // B3 — postcondition met after Interact: step completes, engine advances
    // =========================================================================

    [Fact]
    public async Task AttunementStep_PostconditionMet_StepCompletes()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep dispatch.
         *
         * CONTRACT: Given a quest with one AttunementStep with Expect = "isAttuned(1000)",
         *           AND the step's SkipIf is null,
         *           AND after the first Tick (which returns Interact) the test calls
         *               SetAetheryteAttuned(1000, true) to simulate the in-game effect,
         *           When the second Tick runs,
         *           Then the step's Expect evaluates true and the step is considered complete.
         *           The engine advances past it (returns Wait, since no further steps exist).
         *
         * BUILDER GUIDANCE: The per-step loop checks step.Expect (postcondition) via
         *   _expectEvaluator.Evaluate(step.Expect, ct). If true, the step is skipped via continue.
         *   After SetAetheryteAttuned(true), isAttuned(1000) evaluates true, so the step is done.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AttunementStep
            {
                Id = "attune-limsa",
                Target = new QuestForge.Schema.AetheryteId(1000),
                Expect = new PredicateExpect { Predicate = "isAttuned(1000)" }
                // SkipIf is null — postcondition only
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: isAttuned(1000) == false → step not done → Interact emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Interact>(tick1);

        // Simulate in-game attunement completing
        harness.GameState.SetAetheryteAttuned(new AdaptersAetheryteId(1000), true);

        // Tick 2: isAttuned(1000) == true → step.Expect satisfied → step skipped → Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert — postcondition met; step is done; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(tick2);
    }

    // =========================================================================
    // B4 — postcondition unmet: engine retries Interact on next tick
    // =========================================================================

    [Fact]
    public async Task AttunementStep_PostconditionUnmet_RetriesInteract()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep dispatch.
         *
         * CONTRACT: Given B2's setup (Interact emitted on tick 1),
         *           AND SetAetheryteAttuned is NOT called (in-game state unchanged),
         *           When Tick is called again,
         *           Then EngineAction.Interact(new NpcId(1000)) is returned again.
         *           (Standard stateless retry — the engine re-evaluates state each tick.)
         *
         * BUILDER GUIDANCE: The engine is stateless per tick. After each Interact emission,
         *   if the postcondition is still false, the engine re-runs the step on the next tick.
         *   No counter is incremented in Phase 11B (recovery ladder is out of scope).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AttunementStep
            {
                Id = "attune-limsa",
                Target = new QuestForge.Schema.AetheryteId(1000),
                Expect = new PredicateExpect { Predicate = "isAttuned(1000)" }
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: not attuned → Interact
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Interact>(tick1);

        // Do NOT set attuned — simulate failed in-game interaction

        // Tick 2: still not attuned → Interact again
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var interact = Assert.IsType<EngineAction.Interact>(tick2);
        Assert.Equal(new NpcId(1000), interact.Target);
    }

    // =========================================================================
    // B5 — Location is metadata only (Option A): Interact uses Target.Value, not Location.NpcId
    // =========================================================================

    [Fact]
    public async Task AttunementStep_LocationPresent_InteractUsesTargetValue()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep dispatch.
         *
         * CONTRACT: Given an AttunementStep with Target.Value == 1000
         *           AND Location.NpcId == 999999 (a different value),
         *           When Tick,
         *           Then Interact carries NpcId(1000), NOT NpcId(999999).
         *           (Location is metadata only — runtime uses Target.Value.)
         *
         * BUILDER GUIDANCE: Option A decision (see spec §3.8):
         *   AttunementStep ALWAYS returns Interact(new NpcId(attune.Target.Value)).
         *   Location is never consulted at runtime. It is for authoring/inference only.
         */

        // Arrange — place player at the aetheryte position so proximity check passes
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);
        harness.GameState.SetPosition(new WorldPosition(-15.5f, 4f, -7.2f)); // XZ distance = 0 → in range

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AttunementStep
            {
                Id = "attune-with-location",
                Target = new QuestForge.Schema.AetheryteId(1000),
                Location = new NpcLocation(
                    NpcId: 999999,   // deliberately different from Target.Value
                    Zone: 130,
                    Position: new Position3(-15.5f, 4f, -7.2f))
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — Interact uses Target.Value (1000), not Location.NpcId (999999)
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1000), interact.Target);
        Assert.NotEqual(new NpcId(999999), interact.Target);
    }

    // =========================================================================
    // B6 — SkipIf doubles as postcondition when Expect is null
    // =========================================================================

    [Fact]
    public async Task AttunementStep_SkipIf_DoublesAsPostcondition()
    {
        /*
         * RED: Will fail until Builder implements AttunementStep dispatch.
         *
         * CONTRACT: Given an AttunementStep with SkipIf = "isAttuned(1000)" and Expect = null,
         *           AND SetAetheryteAttuned(1000, false) (initial state),
         *           WHEN Tick → Interact(1000) is returned,
         *           THEN SetAetheryteAttuned(1000, true) is called to simulate game effect,
         *           WHEN Tick again → SkipIf now evaluates true → step is skipped → Wait.
         *
         * BUILDER GUIDANCE: The per-step loop (lines 166-174 of QuestEngine.cs) checks
         *   step.Expect (postcondition) first, then step.SkipIf. If SkipIf is true, the step
         *   is skipped. After the attunement completes in-game, SkipIf fires on re-evaluation
         *   and the step is considered done. This is the intended "skipIf as postcondition" pattern.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildSingleStepQuest(
            questId: 12345,
            sequence: 0,
            step: new AttunementStep
            {
                Id = "attune-skipif-only",
                Target = new QuestForge.Schema.AetheryteId(1000),
                SkipIf = new PredicateExpect { Predicate = "isAttuned(1000)" }
                // Expect is null intentionally — SkipIf doubles as postcondition
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: not attuned → SkipIf false → Interact emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Interact>(tick1);

        // Simulate in-game attunement completing
        harness.GameState.SetAetheryteAttuned(new AdaptersAetheryteId(1000), true);

        // Tick 2: SkipIf (isAttuned(1000)) now true → step skipped → Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert
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
            Name = "Attunement Test Quest",
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
