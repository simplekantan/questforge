using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of CutsceneStep.
///
/// RED PHASE: All B1-B6 tests will throw NotImplementedException until Builder adds the
/// CutsceneStep arm to QuestEngine.ResolveActionForStep.
///
/// Spec: docs/CUTSCENE_STEP_PLAN.md §3.3-3.4, Behaviors B1-B6.
/// Decision table:
///   CutscenePlaying=true              → Wait("cutscene playing")
///   CutscenePlaying=false, Expect=null → Wait("cutscene ended; awaiting sequence advance")
///   CutscenePlaying=false, Expect=false → Wait("cutscene ended; awaiting sequence advance")
///   CutscenePlaying=false, Expect=true  → per-step loop short-circuits; dispatch never runs
/// </summary>
public sealed class CutsceneStepTests
{
    // Helper to build a UiState with a specific CutscenePlaying value and all other flags false.
    private static UiState WithCutscene(bool playing) =>
        new UiState(
            DialogueOpen: false,
            CutscenePlaying: playing,
            LoadingZone: false,
            YesNoPromptOpen: false,
            SelectStringOpen: false,
            RewardSelectionOpen: false,
            ShopOpen: false,
            DeathPromptOpen: false,
            CurrentDialogue: null);

    // Minimal quest factory — one CutsceneStep, one sequence.
    private static QuestDefinition BuildCutsceneQuest(uint questId, int sequence, CutsceneStep step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Cutscene Test Quest",
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

    // =========================================================================
    // B1 — Wait while a skippable cutscene plays
    // =========================================================================

    [Fact]
    public async Task CutsceneStep_CutscenePlaying_True_EmitsWait()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the CutsceneStep arm.
         *
         * CONTRACT: Given a quest with one CutsceneStep (Id="watch-intro",
         *             Expect = questSequence(12345) >= 1)
         *           AND UiState.CutscenePlaying == true
         *           AND questSequence(12345) == 0  (Expect not yet satisfied)
         *           When Engine.Tick,
         *           Then EngineAction.Wait is returned.
         *
         * BUILDER GUIDANCE:
         *   CutsceneStep arm: ui.CutscenePlaying == true  → new EngineAction.Wait("cutscene playing")
         *   The "skippable" framing is the production scenario. The engine does not distinguish
         *   between skippable and non-skippable cutscenes — both set CutscenePlaying=true.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.GameState.SetUiState(WithCutscene(playing: true));
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildCutsceneQuest(
            questId: 12345,
            sequence: 0,
            step: new CutsceneStep
            {
                Id = "watch-intro",
                Expect = new PredicateExpect { Predicate = "questSequence(12345) >= 1" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal("cutscene playing", wait.Reason);
    }

    // =========================================================================
    // B2 — Wait while a non-skippable cutscene plays
    // =========================================================================

    [Fact]
    public async Task CutsceneStep_NonSkippableCutscenePlaying_EmitsWait()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the CutsceneStep arm.
         *
         * CONTRACT: Identical to B1 but documents that the engine treats non-skippable
         *           cutscenes exactly the same: CutscenePlaying=true → Wait("cutscene playing").
         *           The plugin layer (EngineHost.TryCutsceneSkipConfirm) differentiates;
         *           the engine does not need to.
         *
         * BUILDER GUIDANCE: No separate handling is needed. The single CutscenePlaying bool
         *   covers both skippable (OccupiedInCutSceneEvent) and non-skippable (WatchingCutscene78).
         *   Both arrive here as CutscenePlaying=true.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.GameState.SetUiState(WithCutscene(playing: true));
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildCutsceneQuest(
            questId: 12345,
            sequence: 0,
            step: new CutsceneStep
            {
                Id = "watch-intro",
                Skip = "never",  // non-skippable authoring hint; engine ignores this field
                Expect = new PredicateExpect { Predicate = "questSequence(12345) >= 1" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — same outcome as B1; discriminator is the CutscenePlaying bool, not Skip field
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal("cutscene playing", wait.Reason);
    }

    // =========================================================================
    // B3 — Step completes when cutscene ends AND Expect is satisfied
    // =========================================================================

    [Fact]
    public async Task CutsceneStep_CutsceneEnded_ExpectSatisfied_StepCompletes()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the CutsceneStep arm.
         *
         * CONTRACT: Given a quest with one CutsceneStep (Expect = questSequence(12345) >= 1)
         *           AND UiState.CutscenePlaying == false
         *           AND questSequence(12345) == 1  (game advanced sequence after cutscene)
         *           When Engine.Tick,
         *           Then the per-step loop short-circuits via step.Expect evaluating true
         *                (the dispatch arm itself is never reached); the loop exhausts all
         *                steps and returns Wait (no further steps).
         *
         * BUILDER GUIDANCE: This tests that the standard per-step loop already handles
         *   step completion before dispatch. When step.Expect evaluates true, the loop calls
         *   `continue`, skipping the step. Since this is the only step, Wait is returned.
         *   Mirrors AttunementStepTests B3 pattern exactly.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.GameState.SetUiState(WithCutscene(playing: false));
        harness.QuestState.SetQuestSequence(new QuestId(12345), 1);  // Expect IS met

        var quest = BuildCutsceneQuest(
            questId: 12345,
            sequence: 1,  // current sequence matches the block
            step: new CutsceneStep
            {
                Id = "watch-cutscene",
                Expect = new PredicateExpect { Predicate = "questSequence(12345) >= 1" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — Expect satisfied; step skipped by per-step loop; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B4 — Cutscene ended but Expect not yet satisfied — keep waiting
    // =========================================================================

    [Fact]
    public async Task CutsceneStep_CutsceneEnded_ExpectNotSatisfied_EmitsWait()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the CutsceneStep arm.
         *
         * CONTRACT: Given a quest with one CutsceneStep (Expect = questSequence(12345) >= 1)
         *           AND UiState.CutscenePlaying == false
         *           AND questSequence(12345) == 0  (game has not yet advanced sequence)
         *           When Engine.Tick,
         *           Then EngineAction.Wait is returned (the "brief gap" case between the
         *                cutscene addon closing and the quest journal updating).
         *
         * BUILDER GUIDANCE:
         *   CutsceneStep arm: ui.CutscenePlaying == false
         *     → new EngineAction.Wait("cutscene ended; awaiting sequence advance")
         *   The Expect check in the per-step loop fires first; since Expect is false,
         *   the step is NOT skipped and dispatch runs.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.GameState.SetUiState(WithCutscene(playing: false));
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);  // Expect NOT met

        var quest = BuildCutsceneQuest(
            questId: 12345,
            sequence: 0,
            step: new CutsceneStep
            {
                Id = "watch-cutscene",
                Expect = new PredicateExpect { Predicate = "questSequence(12345) >= 1" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal("cutscene ended; awaiting sequence advance", wait.Reason);
    }

    // =========================================================================
    // B5 — No Expect: emits Wait without throwing when cutscene ends
    // =========================================================================

    [Fact]
    public async Task CutsceneStep_NoExpect_CutsceneEnded_EmitsWait_NoException()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the CutsceneStep arm.
         *
         * CONTRACT: Given a quest with one CutsceneStep where Expect == null and SkipIf == null,
         *           AND UiState.CutscenePlaying == false,
         *           When Engine.Tick,
         *           Then EngineAction.Wait is returned and no exception is thrown.
         *
         * BUILDER GUIDANCE:
         *   The per-step loop does not check Expect when it is null (step is not skipped).
         *   The dispatch arm hits the "cutscene ended; awaiting sequence advance" branch.
         *   This is acceptable behavior: cutscene steps without Expect stay "current" until
         *   a later tick or the sequence advances. The test documents that no crash occurs.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.GameState.SetUiState(WithCutscene(playing: false));
        harness.QuestState.SetQuestSequence(new QuestId(12345), 0);

        var quest = BuildCutsceneQuest(
            questId: 12345,
            sequence: 0,
            step: new CutsceneStep
            {
                Id = "watch-cutscene-no-expect"
                // Expect is null — no postcondition
                // SkipIf is null — no pre-skip
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — dispatch arm ran; Wait returned; no exception
        var wait = Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal("cutscene ended; awaiting sequence advance", wait.Reason);
    }

    // =========================================================================
    // B6 — SkipIf skips before cutscene starts (already-watched case)
    // =========================================================================

    [Fact]
    public async Task CutsceneStep_SkipIf_AlreadyWatched_StepIsSkipped()
    {
        /*
         * RED: Will fail with NotImplementedException until Builder adds the CutsceneStep arm
         *      (needed so the test compiles and exercises the correct code path). However,
         *      the SkipIf check fires BEFORE dispatch — so if SkipIf is true, the
         *      NotImplementedException is never reached. The test will pass once SkipIf
         *      predicates work correctly with questSequence(). It documents that the
         *      per-step loop handles the already-watched case without touching the dispatch arm.
         *
         * CONTRACT: Given a quest with one CutsceneStep where
         *             SkipIf = questSequence(12345) >= 1  (watched in a prior session)
         *           AND questSequence(12345) == 1
         *           AND UiState.CutscenePlaying == false
         *           When Engine.Tick,
         *           Then the per-step loop skips the step via SkipIf → Wait (no more steps).
         *
         * BUILDER GUIDANCE: The SkipIf check (per-step loop line 170) evaluates before dispatch.
         *   When SkipIf is true, `continue` fires and the step is skipped. The dispatch arm
         *   for CutsceneStep is never called in this scenario.
         *   Mirrors AttunementStepTests B1 pattern exactly.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.GameState.SetUiState(WithCutscene(playing: false));
        harness.QuestState.SetQuestSequence(new QuestId(12345), 1);  // SkipIf IS true

        var quest = BuildCutsceneQuest(
            questId: 12345,
            sequence: 1,
            step: new CutsceneStep
            {
                Id = "watch-intro",
                SkipIf = new PredicateExpect { Predicate = "questSequence(12345) >= 1" }
                // Expect is null — SkipIf is the entire gate
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — SkipIf fired; step was skipped; no more steps; engine returns Wait
        Assert.IsType<EngineAction.Wait>(action);
    }
}
