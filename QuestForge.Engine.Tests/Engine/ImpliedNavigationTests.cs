using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;
using SchemaAetheryteId = QuestForge.Schema.AetheryteId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for implied navigation: before QuestEngine emits EngineAction.Interact it checks
/// the player's 2D (XZ-plane) distance to the target. If distance > (step.StopDistance ?? 3.0f)
/// it emits EngineAction.Navigate to the target position instead.
///
/// RED PHASE: All tests will fail until the Builder adds the distance check to
/// QuestEngine.ResolveActionForStep. The feature does not exist yet — that is correct.
///
/// Spec: Implied Navigation feature (QuestEngine.ResolveActionForStep distance guard).
/// Behaviors B1-B8.
///
/// Common coordinates:
///   Target position:    (35.0f, 4.0f, -151.0f), NpcId 1003987, Zone 182, QuestId 12345, seq 0
///   In-range player:    (36.0f, 4.0f, -150.0f)  XZ distance ~1.41
///   Out-of-range player:(60.0f, 4.0f, -151.0f)  XZ distance = 25.0
/// </summary>
public sealed class ImpliedNavigationTests
{
    // Common test fixtures
    private const uint TestNpcId = 1003987u;
    private const int TestZone = 182;
    private const uint TestQuestId = 12345u;
    private const int TestSequence = 0;

    private static readonly Position3 TargetPosition = new(35.0f, 4.0f, -151.0f);
    private static readonly NpcLocation TestNpcLocation =
        new(NpcId: TestNpcId, Zone: TestZone, Position: TargetPosition);

    // XZ distance from (36,_,-150) to (35,_,-151) = sqrt(1+1) ~1.41
    private static readonly WorldPosition InRangePlayer = new(36.0f, 4.0f, -150.0f);
    // XZ distance from (60,_,-151) to (35,_,-151) = 25.0
    private static readonly WorldPosition OutOfRangePlayer = new(60.0f, 4.0f, -151.0f);

    // =========================================================================
    // B1 — TalkStep, player in range (~1.41) → emits Interact(NpcId 1003987)
    // =========================================================================

    [Fact]
    public async Task B1_TalkStep_PlayerInRange_EmitsInteract()
    {
        /*
         * RED: Will fail until Builder adds distance check to ResolveActionForStep.
         * Currently the engine always emits Interact for TalkStep regardless of distance.
         * After the feature is implemented this test must PASS (it verifies the in-range case).
         * NOTE: this test documents the contract that in-range still yields Interact — it will
         * remain failing until the distance check is added, because the test exercises the
         * NEW code path (the distance check itself), even though the outcome is Interact.
         *
         * CONTRACT: Given a TalkStep with Target at (35, 4, -151),
         *           AND player position is (36, 4, -150) (XZ distance ~1.41, within default 3.0f),
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact(NpcId(1003987)) is returned.
         *
         * BUILDER GUIDANCE: In ResolveActionForStep, before emitting Interact for TalkStep,
         *   call GetPlayerPosition asynchronously (requires ResolveActionForStep to become async
         *   or a helper to be invoked from ResolveAction). Compute XZ distance:
         *   sqrt((px - tx)^2 + (pz - tz)^2). If distance <= stopDistance → Interact.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(InRangePlayer);

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new TalkStep
            {
                Id = "talk-in-range",
                Target = TestNpcLocation
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert: player is within default 3.0f stop distance → Interact
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(TestNpcId), interact.Target);
    }

    // =========================================================================
    // B2 — TalkStep, player out of range (distance 25) → emits Navigate
    // =========================================================================

    [Fact]
    public async Task B2_TalkStep_PlayerOutOfRange_EmitsNavigate()
    {
        /*
         * RED: Will fail until Builder adds distance check to ResolveActionForStep.
         * Currently TalkStep always falls through to Interact — Navigate is never emitted.
         *
         * CONTRACT: Given a TalkStep with Target at (35, 4, -151),
         *           AND player position is (60, 4, -151) (XZ distance = 25.0, beyond default 3.0f),
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned with:
         *             Destination = WorldPosition(35.0f, 4.0f, -151.0f)
         *             Options.StoppingDistance = 3.0f (default stop distance)
         *
         * BUILDER GUIDANCE: XZ distance check: sqrt((60-35)^2 + (-151-(-151))^2) = 25.0.
         *   25.0 > 3.0 → emit Navigate(target position, new NavigationOptions(StoppingDistance: 3.0f)).
         *   The Y coordinate of the target position is preserved as-is in the Navigate destination.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(OutOfRangePlayer);

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new TalkStep
            {
                Id = "talk-out-of-range",
                Target = TestNpcLocation
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert: player is 25 units away (beyond default 3.0f) → Navigate
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.0f, 4.0f, -151.0f), navigate.Destination);
        Assert.Equal(3.0f, navigate.Options.StoppingDistance);
    }

    // =========================================================================
    // B3 — AcceptStep, player out of range (distance 15) → emits Navigate
    // =========================================================================

    [Fact]
    public async Task B3_AcceptStep_PlayerOutOfRange_EmitsNavigate()
    {
        /*
         * RED: Will fail until Builder adds distance check to ResolveActionForStep.
         * Currently AcceptStep always emits Interact regardless of distance.
         *
         * CONTRACT: Given an AcceptStep with Target at (35, 4, -151),
         *           AND player position is (50, 4, -151) (XZ distance = 15.0, beyond default 3.0f),
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned with:
         *             Destination = WorldPosition(35.0f, 4.0f, -151.0f)
         *             Options.StoppingDistance = 3.0f
         *
         * BUILDER GUIDANCE: XZ distance: sqrt((50-35)^2 + 0^2) = 15.0.
         *   15.0 > 3.0 → emit Navigate. Same distance-guard logic as TalkStep.
         */

        // Arrange — player at (50, 4, -151): XZ distance = 15.0
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(new WorldPosition(50.0f, 4.0f, -151.0f));

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new AcceptStep
            {
                Id = "accept-out-of-range",
                Target = TestNpcLocation,
                SkipIf = new PredicateExpect { Predicate = "isQuestAccepted(12345)" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.0f, 4.0f, -151.0f), navigate.Destination);
        Assert.Equal(3.0f, navigate.Options.StoppingDistance);
    }

    // =========================================================================
    // B4 — TurnInStep, player out of range (distance 51) → emits Navigate
    // =========================================================================

    [Fact]
    public async Task B4_TurnInStep_PlayerOutOfRange_EmitsNavigate()
    {
        /*
         * RED: Will fail until Builder adds distance check to ResolveActionForStep.
         * Currently TurnInStep always emits Interact regardless of distance.
         *
         * CONTRACT: Given a TurnInStep with Target at (35, 4, -151),
         *           AND player position is (35, 4, -100) (XZ distance = 51.0, beyond default 3.0f),
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned with:
         *             Destination = WorldPosition(35.0f, 4.0f, -151.0f)
         *             Options.StoppingDistance = 3.0f
         *
         * BUILDER GUIDANCE: XZ distance: sqrt(0^2 + (-100-(-151))^2) = 51.0.
         *   51.0 > 3.0 → Navigate. Same guard applies to all NpcLocation-based step types.
         */

        // Arrange — player at (35, 4, -100): XZ distance along Z = 51.0
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(new WorldPosition(35.0f, 4.0f, -100.0f));

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new TurnInStep
            {
                Id = "turnin-out-of-range",
                Target = TestNpcLocation
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.0f, 4.0f, -151.0f), navigate.Destination);
        Assert.Equal(3.0f, navigate.Options.StoppingDistance);
    }

    // =========================================================================
    // B5 — InteractObjectStep, player far away → emits Navigate
    //       (InteractObject uses InteractableTarget; Navigate half only tested here —
    //        no EngineAction.InteractObject exists yet so the in-range Interact is deferred)
    // =========================================================================

    [Fact]
    public async Task B5_InteractObjectStep_PlayerOutOfRange_EmitsNavigate()
    {
        /*
         * RED: Will fail in two ways until Builder implements:
         *   1. Currently InteractObjectStep falls through to the catch-all
         *      NotSupportedException arm — so any test using it fails immediately.
         *   2. Once an InteractObjectStep arm exists, it must also apply the distance guard.
         *
         * CONTRACT: Given an InteractObjectStep with Target.InteractableId 99001
         *                                          at position (35, 4, -151),
         *           AND player position is (0, 0, 0) (XZ distance >> stop distance),
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned with:
         *             Destination = WorldPosition(35.0f, 4.0f, -151.0f)
         *             Options.StoppingDistance = 3.0f
         *
         * BUILDER GUIDANCE: Add an InteractObjectStep arm to the switch. Apply the same
         *   XZ-plane distance guard used by TalkStep/AcceptStep/TurnInStep. The target
         *   position comes from step.Target.Position. No EngineAction.InteractObject
         *   exists yet; when in-range the Interact action for objects is deferred to a
         *   later phase — for now the arm only needs to emit Navigate when out of range.
         *   When in-range, throwing NotSupportedException is acceptable for this phase.
         */

        // Arrange — player at origin, target at (35, 4, -151), XZ distance >> 3.0f
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new InteractObjectStep
            {
                Id = "interact-object-out-of-range",
                Target = new InteractableTarget(
                    InteractableId: 99001u,
                    Zone: TestZone,
                    Position: TargetPosition)
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert: far from target → Navigate first
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.0f, 4.0f, -151.0f), navigate.Destination);
        Assert.Equal(3.0f, navigate.Options.StoppingDistance);
    }

    // =========================================================================
    // B6 — TalkStep with custom StopDistance = 10f:
    //       player at distance 8 → Interact; player at distance 15 → Navigate(StoppingDistance 10f)
    // =========================================================================

    [Theory]
    [InlineData(43.0f, 4.0f, -151.0f, true)]   // distance 8  (35+8=43), within StopDistance 10 → Interact
    [InlineData(50.0f, 4.0f, -151.0f, false)]  // distance 15 (35+15=50), beyond StopDistance 10 → Navigate
    public async Task B6_TalkStep_CustomStopDistance_CorrectActionEmitted(
        float playerX, float playerY, float playerZ, bool expectInteract)
    {
        /*
         * RED: Will fail until Builder adds distance check to ResolveActionForStep
         *      AND respects step.StopDistance when set.
         *
         * CONTRACT: Given a TalkStep with Target at (35, 4, -151) and StopDistance = 10f,
         *           WHEN player is at distance 8 (within 10f) → EngineAction.Interact
         *           WHEN player is at distance 15 (beyond 10f) → EngineAction.Navigate
         *                with Options.StoppingDistance = 10f (not the default 3.0f)
         *
         * BUILDER GUIDANCE: Use step.StopDistance ?? 3.0f for both the comparison threshold
         *   and the NavigationOptions.StoppingDistance value. When StopDistance is 10f,
         *   pass 10f as StoppingDistance to NavigationOptions.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(new WorldPosition(playerX, playerY, playerZ));

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new TalkStep
            {
                Id = "talk-custom-stopdistance",
                Target = TestNpcLocation,
                StopDistance = 10f
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        if (expectInteract)
        {
            var interact = Assert.IsType<EngineAction.Interact>(action);
            Assert.Equal(new NpcId(TestNpcId), interact.Target);
        }
        else
        {
            var navigate = Assert.IsType<EngineAction.Navigate>(action);
            Assert.Equal(new WorldPosition(35.0f, 4.0f, -151.0f), navigate.Destination);
            Assert.Equal(10f, navigate.Options.StoppingDistance);
        }
    }

    // =========================================================================
    // B7 — TalkStep, GetPlayerPosition returns Failure → fail-open: emits Interact
    // =========================================================================

    [Fact]
    public async Task B7_TalkStep_PositionFailure_FallsBackToInteract()
    {
        /*
         * RED: Will fail until Builder adds distance check to ResolveActionForStep.
         * Once added, the failure path must default to Interact (fail-open) rather
         * than propagating the error or emitting Navigate.
         *
         * CONTRACT: Given a TalkStep with Target at (35, 4, -151),
         *           AND GetPlayerPosition returns Result<WorldPosition>.Failure,
         *           When Engine.Tick is called,
         *           Then EngineAction.Interact(NpcId(1003987)) is returned
         *                (fail-open: cannot determine distance → act as if in range).
         *
         * BUILDER GUIDANCE: Wrap the position read in a null-safe check.
         *   If GetPlayerPosition returns a Failure result, skip the distance guard
         *   and proceed directly to Interact. Do NOT propagate the failure as AwaitUser
         *   or throw — the correct behavior is silent fail-open.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPositionFailure("simulated position read failure", "unit test");

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new TalkStep
            {
                Id = "talk-position-failure",
                Target = TestNpcLocation
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert: position unavailable → fail-open → Interact
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(TestNpcId), interact.Target);
    }

    // =========================================================================
    // B8 — TalkStep with Target = null AND Targets = null → NotSupportedException
    //       (regression guard: the existing null-check must survive the refactor)
    // =========================================================================

    [Fact]
    public async Task B8_TalkStep_NullTargetAndTargets_ThrowsNotSupportedException()
    {
        /*
         * RED: This test exercises the existing NotSupportedException throw. It should
         * PASS today (the throw already exists), but is included here as a regression
         * guard to ensure the Builder does not accidentally remove or bypass this check
         * when wiring up the distance guard.
         *
         * CONTRACT: Given a TalkStep where both Target and Targets are null,
         *           When Engine.Tick is called,
         *           Then NotSupportedException is thrown
         *                (the existing guard "Phase 4 does not support multi-target talk steps"
         *                 or equivalent must remain).
         *
         * BUILDER GUIDANCE: Do NOT remove the null-target guard. The distance check must
         *   be inserted AFTER the target is resolved, not before. A null Target with null
         *   Targets remains an unsupported configuration and must continue to throw.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(InRangePlayer);

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new TalkStep
            {
                Id = "talk-null-targets",
                Target = null,   // null
                Targets = null   // also null
            });

        harness.Engine.StartQuest(quest);

        // Act + Assert: null target configuration throws
        await Assert.ThrowsAsync<NotSupportedException>(
            () => harness.Engine.Tick(CancellationToken.None));
    }

    // =========================================================================
    // B9 — AttunementStep with Location, player out of range → emits Navigate
    // B10 — AttunementStep without Location → emits Interact directly (no position to navigate to)
    // =========================================================================

    [Fact]
    public async Task B9_AttunementStep_WithLocation_PlayerOutOfRange_EmitsNavigate()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(OutOfRangePlayer);

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new AttunementStep
            {
                Id = "attune-out-of-range",
                Target = new SchemaAetheryteId(9),
                Location = TestNpcLocation
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.0f, 4.0f, -151.0f), navigate.Destination);
        Assert.Equal(7.0f, navigate.Options.StoppingDistance); // aetheryte default: larger than NPC default
    }

    [Fact]
    public async Task B10_AttunementStep_WithoutLocation_EmitsInteractDirectly()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSequence);
        harness.GameState.SetPosition(OutOfRangePlayer);

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSequence,
            step: new AttunementStep
            {
                Id = "attune-no-location",
                Target = new SchemaAetheryteId(9),
                Location = null
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        // No Location → no position to navigate to → Interact directly regardless of distance
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(9u), interact.Target);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "ImpliedNavigation Test Quest",
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
