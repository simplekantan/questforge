using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using NpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ItemId = QuestForge.Adapters.Types.ItemId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of HandOverItemStep.
///
/// RED PHASE: All tests will fail to compile until Builder implements:
///   - HandOverItemStep schema type (QuestForge.Schema)
///   - EngineAction.HandOver record (QuestForge.Engine)
///   - HandOverItemStep arm in QuestEngine.ResolveActionForStep
///
/// Spec: hand-over-item step feature, Behaviors B1-B2e.
/// Design: HandOverItemStep uses implied navigation (same as AcceptStep/TalkStep).
///   If player XZ distance > (step.StopDistance ?? 3.0f) → Navigate, else → HandOver.
/// </summary>
public sealed class HandOverItemStepTests
{
    private const uint TestNpcId         = 1003987u;
    private const int  TestZone          = 182;
    private const uint TestQuestId       = 12345u;
    private const uint TestItemId        = 2002001u;
    private const int  TestSeq           = 0;
    // A separate quest ID used only in predicates — ensures marking it complete
    // does not cause the engine to return Done for the running quest (12345).
    private const uint ConditionQuestId  = 99999u;

    private static readonly Position3    TargetPos     = new(35.56f, 4f, -151.18f);
    private static readonly NpcLocation  TargetLoc     = new(NpcId: TestNpcId, Zone: TestZone, Position: TargetPos);

    // =========================================================================
    // B1 — player in range (XZ distance 0) → emits EngineAction.HandOver
    // =========================================================================

    [Fact]
    public async Task HandOverItemStep_InRange_EmitsHandOver()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItemStep (Schema) and
         * EngineAction.HandOver (Engine). Once those compile, will fail at runtime until
         * Builder implements the HandOverItemStep dispatch arm.
         *
         * CONTRACT: Given a quest with seq=0 containing HandOverItemStep
         *             { Target=NpcLocation(1003987, 182, (35.56,4,-151.18)), Item=2002001 },
         *           AND player is at (35.56, 4, -151.18) (XZ distance = 0),
         *           When Engine.Tick is called,
         *           Then EngineAction.HandOver with Target==NpcId(1003987) and Item==ItemId(2002001)
         *                is returned.
         *
         * BUILDER GUIDANCE: Add the HandOverItemStep arm to QuestEngine.ResolveActionForStep.
         *   Compute XZ distance identical to the TalkStep/AcceptStep implied-navigation pattern:
         *     dist = sqrt((px - tx)^2 + (pz - tz)^2)
         *   If dist <= (step.StopDistance ?? 3.0f) → return new EngineAction.HandOver(NpcId, ItemId).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(35.56f, 4f, -151.18f)); // XZ distance = 0

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSeq,
            step: new HandOverItemStep
            {
                Id     = "hand-over-letter",
                Target = TargetLoc,
                Item   = TestItemId
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var handOver = Assert.IsType<EngineAction.HandOver>(action);
        Assert.Equal(new NpcId(TestNpcId), handOver.Target);
        Assert.Equal(new ItemId(TestItemId), handOver.Item);
    }

    // =========================================================================
    // B2 — player out of range → emits EngineAction.Navigate
    // =========================================================================

    [Fact]
    public async Task HandOverItemStep_OutOfRange_EmitsNavigate()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItemStep and EngineAction.HandOver.
         *
         * CONTRACT: Given same HandOverItemStep,
         *           AND player is at (0, 0, 0) (XZ distance >> 3.0f default stop distance),
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned whose Destination matches target position.
         *
         * BUILDER GUIDANCE: Same implied-navigation pattern as TalkStep. When dist > stopDistance,
         *   return new EngineAction.Navigate(targetWorldPosition, new NavigationOptions(StoppingDistance: stopDist)).
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f)); // far away

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSeq,
            step: new HandOverItemStep
            {
                Id     = "hand-over-letter",
                Target = TargetLoc,
                Item   = TestItemId
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — out of range → Navigate
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(TargetPos.X, navigate.Destination.X, precision: 2);
        Assert.Equal(TargetPos.Z, navigate.Destination.Z, precision: 2);
    }

    // =========================================================================
    // B2b — custom StopDistance honored: player just inside custom range → HandOver
    // =========================================================================

    [Fact]
    public async Task HandOverItemStep_CustomStopDistance_InRange_EmitsHandOver()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItemStep and EngineAction.HandOver.
         *
         * CONTRACT: Given HandOverItemStep with StopDistance=5.0f,
         *           AND player at (40.06, 4, -151.18) — XZ distance ≈4.5 from target (35.56,4,-151.18),
         *           When Engine.Tick is called,
         *           Then EngineAction.HandOver is returned (player is within the 5.0f custom radius).
         *
         * BUILDER GUIDANCE: step.StopDistance ?? 3.0f — use the custom value when present.
         *   XZ distance = sqrt((40.06-35.56)^2 + (-151.18-(-151.18))^2) = sqrt(4.5^2) = 4.5 < 5.0 → in range.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        // XZ distance ≈ 4.5 from target — within 5.0f custom radius
        harness.GameState.SetPosition(new WorldPosition(40.06f, 4f, -151.18f));

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSeq,
            step: new HandOverItemStep
            {
                Id           = "hand-over-letter",
                Target       = TargetLoc,
                Item         = TestItemId,
                StopDistance = 5.0f
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — within custom stop distance → HandOver, not Navigate
        Assert.IsType<EngineAction.HandOver>(action);
    }

    // =========================================================================
    // B2c — SkipIf predicate true → step is skipped, engine returns Wait
    // =========================================================================

    [Fact]
    public async Task HandOverItemStep_SkipIf_True_StepIsSkipped()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItemStep.
         *
         * CONTRACT: Given HandOverItemStep with SkipIf="isQuestComplete(12345)",
         *           AND harness.QuestState.SetQuestStatus(QuestId(12345), QuestStatus.Complete),
         *           When Engine.Tick is called,
         *           Then SkipIf evaluates true, step is skipped, engine returns EngineAction.Wait.
         *
         * BUILDER GUIDANCE: The per-step loop in QuestEngine.ResolveAction evaluates SkipIf
         *   before calling ResolveActionForStep. When SkipIf is true the loop calls `continue`.
         *   With only one step, the loop exhausts and returns Wait.
         */

        // Arrange — use ConditionQuestId (99999) in predicate, not TestQuestId (12345),
        // so the engine's IsQuestComplete(12345) check does not return Done prematurely.
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.QuestState.SetQuestStatus(new QuestId(ConditionQuestId), QuestStatus.Complete);

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSeq,
            step: new HandOverItemStep
            {
                Id     = "hand-over-letter",
                Target = TargetLoc,
                Item   = TestItemId,
                SkipIf = new PredicateExpect { Predicate = $"isQuestComplete({ConditionQuestId})" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — SkipIf fired; step skipped; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B2d — Expect predicate met → step completes, engine returns Wait
    // =========================================================================

    [Fact]
    public async Task HandOverItemStep_Expect_Met_StepAdvances()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItemStep.
         *
         * CONTRACT: Given HandOverItemStep with Expect="isQuestComplete(12345)",
         *           AND quest marked complete (Expect evaluates true),
         *           When Engine.Tick is called,
         *           Then Expect fires, step is considered complete, engine returns EngineAction.Wait.
         *
         * BUILDER GUIDANCE: The per-step loop checks step.Expect before dispatch.
         *   When Expect is true, `continue` fires and dispatch is skipped.
         *   With only one step, Wait is returned.
         */

        // Arrange — use ConditionQuestId (99999) in predicate, not TestQuestId (12345),
        // so the engine's IsQuestComplete(12345) check does not return Done prematurely.
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        harness.QuestState.SetQuestStatus(new QuestId(ConditionQuestId), QuestStatus.Complete);

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSeq,
            step: new HandOverItemStep
            {
                Id     = "hand-over-letter",
                Target = TargetLoc,
                Item   = TestItemId,
                Expect = new PredicateExpect { Predicate = $"isQuestComplete({ConditionQuestId})" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — Expect met; step done; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B2e — position failure fails open → HandOver (not Navigate)
    // =========================================================================

    [Fact]
    public async Task HandOverItemStep_PositionFailure_FailsOpen_EmitsHandOver()
    {
        /*
         * RED: Will fail to compile until Builder adds HandOverItemStep and EngineAction.HandOver.
         *
         * CONTRACT: Given HandOverItemStep in range,
         *           AND harness.GameState.SetPositionFailure("test", "test") is called
         *               (GetPlayerPosition returns a failed Result),
         *           When Engine.Tick is called,
         *           Then EngineAction.HandOver is returned (fail-open: skip distance check, proceed).
         *
         * BUILDER GUIDANCE: The implied-navigation distance check reads player position via
         *   GetPlayerPosition. If that call returns a failed Result, the engine must fail-open:
         *   skip the Navigate and emit HandOver directly. This matches the established pattern
         *   for TalkStep/AcceptStep/AttunementStep implied navigation.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(TestQuestId), TestSeq);
        // Do NOT set player position — set a position failure instead
        harness.GameState.SetPositionFailure("test", "test");

        var quest = BuildSingleStepQuest(
            questId: TestQuestId,
            sequence: TestSeq,
            step: new HandOverItemStep
            {
                Id     = "hand-over-letter",
                Target = TargetLoc,
                Item   = TestItemId
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — position failure → fail-open → HandOver, not Navigate
        Assert.IsType<EngineAction.HandOver>(action);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id            = questId,
            Name          = "HandOverItem Test Quest",
            Expansion     = "arr",
            Category      = "side",
            Enabled       = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements  = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom    = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences     =
            [
                new QuestSequence
                {
                    Sequence = sequence,
                    Steps    = [step]
                }
            ]
        };
}
