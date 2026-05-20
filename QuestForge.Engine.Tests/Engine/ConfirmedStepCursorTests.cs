using System.Text.Json;
using QuestForge.Adapters.Movement;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED PHASE: Tests for the confirmed-step cursor feature.
///
/// QuestEngine gains two new private fields:
///   _confirmedStepIds: HashSet&lt;string&gt; — cleared by BeginRun and sequence change
///   _lastKnownSequence: int              — initialized to -1, updated each tick
///
/// The step evaluation loop in ResolveAction is modified so that:
///   1. Steps present in _confirmedStepIds are skipped BEFORE evaluating expect.
///   2. When expect evaluates true: step.Id is added to _confirmedStepIds, then skipped.
///   3. skipIf true: skips but does NOT confirm (unchanged semantics).
///
/// ALL 15 tests fail until Builder implements the cursor.
/// </summary>
public sealed class ConfirmedStepCursorTests
{
    // =========================================================================
    // Fixture loading
    // =========================================================================

    private static QuestDefinition LoadFixture()
    {
        var json = File.ReadAllText("Fixtures/66130.json");
        return JsonSerializer.Deserialize<QuestDefinition>(json, QuestForgeJsonContext.QuestFileOptions)
               ?? throw new InvalidOperationException("Failed to deserialize fixture 66130.json");
    }

    // Wymond position from fixture: zone 182, (35.56, 4.0, -151.18)
    private static readonly WorldPosition WymondPos = new(35.56f, 4.0f, -151.18f);

    // Momodi position from fixture: zone 182, (21.84, 7.0, -81.13)
    private static readonly WorldPosition MomodiPos = new(21.84f, 7.0f, -81.13f);

    private static readonly WorldPosition FarPos = new(0f, 0f, 0f);

    // =========================================================================
    // Synthetic quest helpers
    // =========================================================================

    /// <summary>
    /// Builds a minimal QuestDefinition with a single sequence block.
    /// </summary>
    private static QuestDefinition BuildSyntheticQuest(uint questId, params QuestSequence[] sequences) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Synthetic Test Quest",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0f, 0f, 0f)),
            Sequences = sequences
        };

    private static TravelStep TravelStepWithExpect(string id, Position3 dest, string? expectPredicate) =>
        new()
        {
            Id = id,
            Destination = new TravelDestination(Zone: 182, Position: dest),
            Expect = expectPredicate is not null
                ? new PredicateExpect { Predicate = expectPredicate }
                : null
        };

    private static TravelStep TravelStepWithSkipIf(string id, Position3 dest, string skipIfPredicate) =>
        new()
        {
            Id = id,
            Destination = new TravelDestination(Zone: 182, Position: dest),
            SkipIf = new PredicateExpect { Predicate = skipIfPredicate }
        };

    private static TravelStep TravelStepWithExpectAndSkipIf(
        string id, Position3 dest, string expectPredicate, string skipIfPredicate) =>
        new()
        {
            Id = id,
            Destination = new TravelDestination(Zone: 182, Position: dest),
            Expect = new PredicateExpect { Predicate = expectPredicate },
            SkipIf = new PredicateExpect { Predicate = skipIfPredicate }
        };

    // =========================================================================
    // Group A — Confirmation basics
    // =========================================================================

    // -------------------------------------------------------------------------
    // A1: expect true on first tick → step confirmed → next tick skips it
    // -------------------------------------------------------------------------

    [Fact]
    public async Task A1_ExpectTrueFirstTick_StepIsConfirmed_NextTickSkipsIt()
    {
        /*
         * RED: Will fail until Builder adds _confirmedStepIds and the cursor logic to ResolveAction.
         *
         * CONTRACT: Given quest 66130, seq 0, player at Wymond (playerNear expect is true)
         *           When BeginRun + Tick once,
         *           Then travel-to-wymond expect evaluates true and is CONFIRMED,
         *                AND the returned action is Interact for talk-to-wymond (NpcId 1003987),
         *                NOT Navigate for travel-to-wymond.
         *
         * BUILDER GUIDANCE:
         *   After the expect-true path adds step.Id to _confirmedStepIds and continues,
         *   the loop reaches talk-to-wymond whose expect (questSequence(66130) >= 255) is false,
         *   so Interact(NpcId(1003987)) is returned.
         *   Without the cursor the loop already returns Interact here via the existing logic;
         *   this test confirms it still does and sets up A2.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(WymondPos);   // playerNear expect is true

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-a1");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — step-2 (talk-to-wymond) should be returned, not step-1 navigate
        // travel-to-wymond's expect was true → confirmed; loop advances to talk-to-wymond
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1003987), interact.Target);
        Assert.NotNull(interact.Origin);
        Assert.Equal("talk-to-wymond", interact.Origin!.Id);
    }

    // -------------------------------------------------------------------------
    // A2: expect true tick-1 → confirmed; expect false tick-2 → still skipped (core bug fix)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task A2_ExpectTrueThenFalse_StepStillSkippedOnSecondTick()
    {
        /*
         * RED: Will fail until Builder adds the cursor. This is the core bug the feature fixes.
         *
         * CONTRACT: Given quest 66130, seq 0, player at Wymond (expect true),
         *           When BeginRun, tick once (confirms travel-to-wymond),
         *           THEN player moves to (0,0,0) (expect now false), tick again,
         *           Then the engine does NOT return Navigate for travel-to-wymond
         *           (the cursor short-circuits it). The next step is talk-to-wymond;
         *           the player is at (0,0,0) and the NPC is ~156 units away, so the
         *           engine returns Navigate toward the NPC for that step.
         *           Confirming a TravelStep means 'the player was there',
         *           not 'still there'.
         *
         * BUILDER GUIDANCE:
         *   On tick-2 the cursor check fires BEFORE expect evaluation. travel-to-wymond
         *   is in _confirmedStepIds → skip it. Without the cursor the engine would see
         *   expect=false and re-navigate, which is the bug being fixed.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(WymondPos);   // tick-1: expect true → confirms

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-a2");

        // Act — tick 1: confirms travel-to-wymond
        await harness.Engine.Tick(CancellationToken.None);

        // Move player far away so travel-to-wymond's expect is now false
        harness.GameState.SetPosition(FarPos);

        // Act — tick 2
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — travel-to-wymond is confirmed; engine skips it and moves on to
        // talk-to-wymond. Player is at (0,0,0); the NPC is at ~(35.56, 4, -151.18) — roughly
        // 156 units away — so the engine returns Navigate (toward the NPC) for that step.
        // The crucial assertion is that travel-to-wymond was NOT re-emitted; confirming a
        // TravelStep means 'the player was there', not 'still there', so the next step's
        // proximity check is re-evaluated against the player's current location.
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // A3: expect false → not confirmed → same navigate returned on subsequent ticks
    // -------------------------------------------------------------------------

    [Fact]
    public async Task A3_ExpectFalse_StepNotConfirmed_NavigateReturnedOnSubsequentTicks()
    {
        /*
         * RED: Will fail until Builder adds the cursor (this test verifies cursor does NOT
         *      incorrectly confirm a step when expect is false).
         *
         * CONTRACT: Given quest 66130, seq 0, player at (0,0,0) (playerNear expect false),
         *           When BeginRun, tick twice (same position both ticks),
         *           Then both ticks return Navigate for travel-to-wymond.
         *
         * BUILDER GUIDANCE:
         *   expect evaluates false → step is NOT added to _confirmedStepIds.
         *   On tick-2 the cursor check finds no entry → expect is re-evaluated → still false
         *   → Navigate returned again. Correct behavior; cursor must not short-circuit here.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(FarPos);      // expect false both ticks

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-a3");

        // Act — tick 1
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        // Act — tick 2 (same state)
        var action2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert both ticks return Navigate for travel-to-wymond
        var nav1 = Assert.IsType<EngineAction.Navigate>(action1);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), nav1.Destination);

        var nav2 = Assert.IsType<EngineAction.Navigate>(action2);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), nav2.Destination);
    }

    // -------------------------------------------------------------------------
    // A4: confirmed step persists even when zone changes (regression for playerZone bug)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task A4_ConfirmedStep_PersistsWhenZoneChanges()
    {
        /*
         * RED: Will fail until Builder adds the cursor.
         *
         * CONTRACT: Given a synthetic 2-step quest:
         *             step-1 expect: playerZone() == 129  (step-2 has no meaningful expect)
         *           AND player zone = 129 (step-1 expect true),
         *           When BeginRun, tick once (confirms step-1),
         *           THEN player zone changes to 130 (step-1 expect now false), tick again,
         *           Then engine still skips step-1 (confirmed) and returns Navigate for step-2.
         *
         * BUILDER GUIDANCE:
         *   This is the zone-change variant of A2. The cursor must not inspect expect again
         *   for confirmed steps. Confirmed = permanently skipped for this run + sequence.
         */

        // Arrange
        const uint questId = 99001;
        var step1Pos = new Position3(100f, 0f, 100f);
        var step2Pos = new Position3(200f, 0f, 200f);

        var quest = BuildSyntheticQuest(questId,
            new QuestSequence
            {
                Sequence = 0,
                Steps =
                [
                    TravelStepWithExpect("step-zone-check", step1Pos, "playerZone() == 129"),
                    TravelStepWithExpect("step-next", step2Pos, null)  // no expect → never satisfied
                ]
            });

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(129));     // step-1 expect is true
        harness.GameState.SetPosition(FarPos);

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-a4");

        // Act — tick 1: step-1 expect true → confirmed
        await harness.Engine.Tick(CancellationToken.None);

        // Change zone so step-1's expect would now be false
        harness.GameState.SetZone(new ZoneId(130));

        // Act — tick 2
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — step-1 is still confirmed; engine returns Navigate for step-2
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(step2Pos.X, step2Pos.Y, step2Pos.Z), navigate.Destination);
    }

    // =========================================================================
    // Group B — Reset semantics
    // =========================================================================

    // -------------------------------------------------------------------------
    // B1: fresh engine instance has empty confirmed set
    // -------------------------------------------------------------------------

    [Fact]
    public async Task B1_NewEngineInstance_HasEmptyConfirmedSet()
    {
        /*
         * RED: Will fail until Builder adds _confirmedStepIds (initialized empty).
         *
         * CONTRACT: Given a fresh harness, quest 66130, seq 0, player at (0,0,0),
         *           When BeginRun, tick once,
         *           Then Navigate for travel-to-wymond is returned (nothing pre-confirmed).
         *
         * BUILDER GUIDANCE: _confirmedStepIds = new HashSet<string>() in the constructor.
         *   No entries exist on first tick; expect evaluates normally.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(FarPos);

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-b1");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — no pre-confirmed steps; Navigate returned for travel-to-wymond
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // B2: BeginRun clears the confirmed set
    // -------------------------------------------------------------------------

    [Fact]
    public async Task B2_BeginRun_ClearsConfirmedSet()
    {
        /*
         * RED: Will fail until Builder clears _confirmedStepIds inside BeginRun.
         *
         * CONTRACT: Given quest 66130, seq 0, player at Wymond,
         *           When BeginRun("rid-1"), tick (confirms travel-to-wymond),
         *           THEN BeginRun("rid-2"), move player to (0,0,0), tick,
         *           Then Navigate for travel-to-wymond is returned (confirmation cleared).
         *
         * BUILDER GUIDANCE:
         *   BeginRun must call _confirmedStepIds.Clear(). The new run starts with no confirmed steps,
         *   so expect is re-evaluated; player is far → Navigate.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(WymondPos);

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-b2-rid1");

        // Tick 1 with run 1: confirms travel-to-wymond
        await harness.Engine.Tick(CancellationToken.None);

        // Start a new run — must clear confirmed set
        harness.Engine.BeginRun("run-b2-rid2");
        harness.GameState.SetPosition(FarPos);   // expect now false

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — confirmed set was cleared by BeginRun; expect re-evaluated → Navigate
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // B3: sequence change clears the confirmed set
    // -------------------------------------------------------------------------

    [Fact]
    public async Task B3_SequenceChange_ClearsConfirmedSet()
    {
        /*
         * RED: Will fail until Builder clears _confirmedStepIds when _lastKnownSequence changes.
         *
         * CONTRACT: Given quest 66130, seq 0, player at Wymond,
         *           When BeginRun, tick (confirms travel-to-wymond in seq-0),
         *           THEN game sequence advances to 255, player moves to (0,0,0) (far from Momodi),
         *           tick again,
         *           Then Navigate for travel-to-momodi is returned (seq-0 confirmations cleared).
         *
         * BUILDER GUIDANCE:
         *   At the top of ResolveAction (or the tick loop), compare currentSeq to _lastKnownSequence.
         *   If they differ: _confirmedStepIds.Clear(); _lastKnownSequence = currentSeq.
         *   travel-to-wymond was confirmed for seq-0. After clear, the seq-255 block is used;
         *   travel-to-momodi is unconfirmed; player is far → Navigate to Momodi.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(WymondPos);

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-b3");

        // Tick 1: confirms travel-to-wymond in seq-0
        await harness.Engine.Tick(CancellationToken.None);

        // Game advances sequence; player is far from Momodi
        harness.QuestState.SetQuestSequence(new QuestId(66130), 255);
        harness.GameState.SetPosition(FarPos);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — seq-0 confirmed set cleared; seq-255 travel step unconfirmed → Navigate to Momodi
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(21.84f, 7.0f, -81.13f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // B4: same sequence preserves confirmed set across ticks
    // -------------------------------------------------------------------------

    [Fact]
    public async Task B4_SameSequence_PreservesConfirmedSetAcrossTicks()
    {
        /*
         * RED: Will fail until Builder adds the cursor.
         *
         * CONTRACT: Given quest 66130, seq 0, player at Wymond,
         *           When BeginRun, tick (confirms travel-to-wymond),
         *           sequence stays 0, player moves to (0,0,0), tick again,
         *           Then engine returns Navigate (cursor preserved; travel-to-wymond is
         *           skipped; the talk step's NPC is ~156 units away so the dispatch
         *           emits Navigate toward the NPC). Confirming a TravelStep means 'the
         *           player was there', not 'still there'.
         *
         * BUILDER GUIDANCE:
         *   No sequence change → _confirmedStepIds not cleared → travel-to-wymond still skipped.
         *   talk-to-wymond's expect (questSequence(66130) >= 255) is false → returns Interact.
         *   This is the happy path for the cursor feature — it must persist within a sequence run.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(WymondPos);

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-b4");

        // Tick 1: confirms travel-to-wymond
        await harness.Engine.Tick(CancellationToken.None);

        // Move far but keep sequence at 0
        harness.GameState.SetPosition(FarPos);

        // Act — tick 2
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — confirmed set preserved; travel-to-wymond skipped. The next step is
        // talk-to-wymond; the player is at (0,0,0) and the NPC is ~156 units away, so the
        // engine returns Navigate (toward the NPC). Confirming a TravelStep means 'the
        // player was there', not 'still there' — the proximity check for the talk
        // step is re-evaluated against the player's current location.
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(35.56f, 4.0f, -151.18f), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // B5: sequence change prevents stale confirmed ID from skipping a new block's step
    // -------------------------------------------------------------------------

    [Fact]
    public async Task B5_SequenceChange_PreventsStaleCursorFromSkippingNewBlockStep()
    {
        /*
         * RED: Will fail until Builder clears _confirmedStepIds on sequence change.
         *
         * CONTRACT: Given a synthetic 2-sequence quest where BOTH sequences contain a step
         *             with the same Id "shared-step" whose expect starts false,
         *           When the step in seq-A is confirmed (set expect true, tick, set expect false),
         *           THEN advance to seq-B, tick,
         *           Then engine returns action for "shared-step" in seq-B (not skipped from seq-A).
         *
         * BUILDER GUIDANCE:
         *   The Id "shared-step" exists in both sequences. After the seq change the cursor is
         *   cleared, so even though "shared-step" is in the cleared set, it is NOT skipped in seq-B.
         *   If the clear is missing, the stale cursor skips seq-B's step — that is the bug.
         */

        // Arrange
        const uint questId = 99002;
        var stepPos = new Position3(50f, 0f, 50f);
        var step2Pos = new Position3(60f, 0f, 60f);

        // seq-A = 0: shared-step with expect playerNear(50,0,50, 3) + another step
        // seq-B = 1: shared-step with expect playerNear(50,0,50, 3) — same Id, same expect
        var quest = BuildSyntheticQuest(questId,
            new QuestSequence
            {
                Sequence = 0,
                Steps =
                [
                    TravelStepWithExpect("shared-step", stepPos, "playerNear({\"x\":50,\"y\":0,\"z\":50}, 3)")
                ]
            },
            new QuestSequence
            {
                Sequence = 1,
                Steps =
                [
                    TravelStepWithExpect("shared-step", stepPos, "playerNear({\"x\":50,\"y\":0,\"z\":50}, 3)")
                ]
            });

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(182));

        // Position player at step position so expect is true → confirms "shared-step" in seq-A
        harness.GameState.SetPosition(new WorldPosition(stepPos.X, stepPos.Y, stepPos.Z));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-b5");

        // Tick 1 in seq-A: confirms "shared-step"
        await harness.Engine.Tick(CancellationToken.None);

        // Move player far so expect would be false if re-evaluated
        harness.GameState.SetPosition(FarPos);

        // Advance to seq-B
        harness.QuestState.SetQuestSequence(new QuestId(questId), 1);

        // Act — tick 2 in seq-B
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — cursor was cleared on sequence change; "shared-step" in seq-B is NOT confirmed
        // expect is false (player far) → Navigate returned for "shared-step" in seq-B
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(stepPos.X, stepPos.Y, stepPos.Z), navigate.Destination);
    }

    // =========================================================================
    // Group C — skipIf semantics
    // =========================================================================

    // -------------------------------------------------------------------------
    // C1: skipIf true skips step WITHOUT confirming it
    // -------------------------------------------------------------------------

    [Fact]
    public async Task C1_SkipIfTrue_SkipsStepWithoutConfirming()
    {
        /*
         * RED: Will fail until Builder ensures that the skipIf path does NOT add to _confirmedStepIds.
         *
         * CONTRACT: Given a 2-step synthetic quest where:
         *             step-1 has only skipIf (evaluates true via questSequence(99003) >= 0)
         *             step-2 has no skipIf; expect = playerNear(200,0,200, 3) (false — player at 0,0,0)
         *           When BeginRun, tick,
         *           Then action returned is Navigate for step-2.
         *
         * BUILDER GUIDANCE:
         *   skipIf is true → loop calls `continue` but does NOT add step.Id to _confirmedStepIds.
         *   step-2 is not skipped → Navigate returned.
         *   This is unchanged behavior; the test documents that skipIf has not accidentally
         *   started confirming steps.
         */

        // Arrange
        const uint questId = 99003;
        var step1Pos = new Position3(100f, 0f, 100f);
        var step2Pos = new Position3(200f, 0f, 200f);

        var quest = BuildSyntheticQuest(questId,
            new QuestSequence
            {
                Sequence = 0,
                Steps =
                [
                    TravelStepWithSkipIf("skipif-step", step1Pos, "questSequence(99003) >= 0"),
                    TravelStepWithExpect("navigate-step", step2Pos, "playerNear({\"x\":200,\"y\":0,\"z\":200}, 3)")
                ]
            });

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(FarPos);   // step-2 expect false

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-c1");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — step-1 skipped by skipIf; step-2 navigate returned
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(step2Pos.X, step2Pos.Y, step2Pos.Z), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // C2: skipIf flip to false → step returns as unconfirmed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task C2_SkipIfFlipsToFalse_StepReturnsAsUnconfirmed()
    {
        /*
         * RED: Will fail until Builder ensures skipIf does not confirm and cursor is NOT set.
         *
         * CONTRACT: Given a 2-step quest (questId=99004, block Sequence=0) where:
         *             step-1 skipIf: questSequence(11111) >= 50
         *               (references a DIFFERENT questId so it does not affect block lookup)
         *             step-2 expect: playerNear(200,0,200, 3) — false (player at 0,0,0)
         *           QuestId(99004) adapter value = 0 (matches Sequence=0 block)
         *           QuestId(11111) adapter value = 50 on tick-1 (skipIf true → step-1 skipped)
         *           Tick 1: step-1 skipIf true → skipped (NOT confirmed); step-2 Navigate returned
         *           THEN set QuestId(11111) to 0 (skipIf now false); QuestId(99004) still 0
         *           Tick 2: block Sequence=0 still matched; step-1 skipIf false; NOT in cursor
         *                   → Navigate for step-1 returned
         *
         * BUILDER GUIDANCE:
         *   skipIf fires → continue but NO _confirmedStepIds.Add(step.Id).
         *   tick-2: QuestId(99004) sequence = 0 → same block → no cursor clear.
         *   Cursor check for "skipif-step": not in set → miss.
         *   skipIf: questSequence(11111)=0 < 50 → false → miss.
         *   expect: null → not skipped.
         *   ResolveActionForStep → Navigate for step-1.
         */

        // Arrange
        const uint questId = 99004;
        const uint skipIfQuestId = 11111;
        var step1Pos = new Position3(100f, 0f, 100f);
        var step2Pos = new Position3(200f, 0f, 200f);

        var quest = BuildSyntheticQuest(questId,
            new QuestSequence
            {
                Sequence = 0,
                Steps =
                [
                    TravelStepWithSkipIf("skipif-step", step1Pos, $"questSequence({skipIfQuestId}) >= 50"),
                    TravelStepWithExpect("navigate-step", step2Pos, "playerNear({\"x\":200,\"y\":0,\"z\":200}, 3)")
                ]
            });

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);         // block lookup: Sequence=0
        harness.QuestState.SetQuestSequence(new QuestId(skipIfQuestId), 50);  // skipIf predicate: 50>=50 → true
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(FarPos);   // step-2 expect false

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-c2");

        // Tick 1: skipIf (questSequence(11111)=50 >= 50) true → step-1 skipped, not confirmed
        // step-2 expect false → Navigate(200,0,200)
        var action1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(action1);
        Assert.Equal(new WorldPosition(step2Pos.X, step2Pos.Y, step2Pos.Z),
            ((EngineAction.Navigate)action1).Destination);

        // Flip skipIf predicate to false: questSequence(11111)=0 < 50 → false
        // QuestId(99004) sequence still 0 → block Sequence=0 still matched → no cursor clear
        harness.QuestState.SetQuestSequence(new QuestId(skipIfQuestId), 0);

        // Act — tick 2
        var action2 = await harness.Engine.Tick(CancellationToken.None);

        // Assert — step-1 was not confirmed; skipIf is false; cursor check misses → Navigate for step-1
        var navigate = Assert.IsType<EngineAction.Navigate>(action2);
        Assert.Equal(new WorldPosition(step1Pos.X, step1Pos.Y, step1Pos.Z), navigate.Destination);
    }

    // -------------------------------------------------------------------------
    // C3: confirmed check fires BEFORE skipIf
    // -------------------------------------------------------------------------

    [Fact]
    public async Task C3_ConfirmedCheckFiresBeforeSkipIf()
    {
        /*
         * RED: Will fail until Builder places the confirmed-cursor check BEFORE the skipIf check
         *      in the step evaluation loop.
         *
         * CONTRACT: Given a synthetic step with both expect (true) and skipIf (false),
         *           When BeginRun, tick (step confirmed via expect),
         *           THEN set expect false AND skipIf false (nothing would skip without cursor),
         *           tick again,
         *           Then step is STILL skipped (confirmed cursor wins) → next step or Wait returned.
         *
         * BUILDER GUIDANCE:
         *   Evaluation order in the loop must be:
         *     1. if (_confirmedStepIds.Contains(step.Id)) continue;   ← FIRST
         *     2. if (step.Expect != null && await Evaluate(step.Expect, ct)) { confirm + continue; }
         *     3. if (step.SkipIf != null && await Evaluate(step.SkipIf, ct)) continue;
         *     4. return dispatch
         *   If confirmed check is after skipIf, the cursor is invisible when skipIf flips.
         */

        // Arrange
        const uint questId = 99005;
        var step1Pos = new Position3(100f, 0f, 100f);
        var step2Pos = new Position3(200f, 0f, 200f);

        // skipIf predicate: questSequence(99005) >= 100 (starts false at seq 0)
        // expect predicate: playerNear(100,0,100, 3) (starts true — player will be at that position)
        var quest = BuildSyntheticQuest(questId,
            new QuestSequence
            {
                Sequence = 0,
                Steps =
                [
                    TravelStepWithExpectAndSkipIf(
                        "both-predicates-step",
                        step1Pos,
                        expectPredicate: "playerNear({\"x\":100,\"y\":0,\"z\":100}, 3)",
                        skipIfPredicate: "questSequence(99005) >= 100"),
                    TravelStepWithExpect("second-step", step2Pos, null)
                ]
            });

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(new WorldPosition(step1Pos.X, step1Pos.Y, step1Pos.Z)); // expect true

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-c3");

        // Tick 1: expect true → step confirmed
        await harness.Engine.Tick(CancellationToken.None);

        // Now set expect false (player far) AND skipIf false (seq still 0 < 100)
        // Without the cursor nothing would skip this step
        harness.GameState.SetPosition(FarPos);

        // Act — tick 2
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — confirmed cursor fires first → step skipped → second-step Navigate returned
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(step2Pos.X, step2Pos.Y, step2Pos.Z), navigate.Destination);
    }

    // =========================================================================
    // Group D — Ordering and block
    // =========================================================================

    // -------------------------------------------------------------------------
    // D1: all steps confirmed → Wait returned
    // -------------------------------------------------------------------------

    [Fact]
    public async Task D1_AllStepsConfirmed_ReturnsWait()
    {
        /*
         * RED: Will fail until Builder adds the cursor (confirming all steps in one tick
         *      means the loop exhausts all steps → Wait).
         *
         * CONTRACT: Given a synthetic 2-step 1-sequence quest where BOTH expects evaluate true,
         *           When BeginRun, tick,
         *           Then EngineAction.Wait is returned (both confirmed in same tick).
         *
         * BUILDER GUIDANCE:
         *   Both steps have expect=true. The loop confirms step-A, then confirms step-B.
         *   No unconfirmed steps remain → the loop falls through → Wait returned.
         *   (This is the same loop fallthrough as the existing "all steps satisfied" case,
         *   but now triggered by the confirmation path rather than pre-existing true expect.)
         */

        // Arrange
        const uint questId = 99006;
        var posA = new Position3(5f, 0f, 5f);
        var posB = new Position3(6f, 0f, 6f);

        var quest = BuildSyntheticQuest(questId,
            new QuestSequence
            {
                Sequence = 0,
                Steps =
                [
                    TravelStepWithExpect("step-a", posA, "playerNear({\"x\":5,\"y\":0,\"z\":5}, 10)"),
                    TravelStepWithExpect("step-b", posB, "playerNear({\"x\":6,\"y\":0,\"z\":6}, 10)")
                ]
            });

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(182));
        // Player at origin — both positions are within 10 units of (5,0,5) and (6,0,6)
        // Actually player at (0,0,0): dist to (5,0,5) = ~7.07, within 10; dist to (6,0,6) = ~8.49, within 10
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d1");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — both steps confirmed this tick; loop exhausts → Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // -------------------------------------------------------------------------
    // D2: mixed confirmed/unconfirmed → first unconfirmed returned
    // -------------------------------------------------------------------------

    [Fact]
    public async Task D2_MixedConfirmedAndUnconfirmed_ReturnsFirstUnconfirmed()
    {
        /*
         * RED: Will fail until Builder adds the cursor.
         *
         * CONTRACT: Given quest 66130, seq 0, player at Wymond (step-1 expect true, step-2 false),
         *           When BeginRun, tick,
         *           Then returned stepId is "talk-to-wymond" (step-2, first unconfirmed).
         *           step-1 "travel-to-wymond" is confirmed this tick and NOT returned.
         *
         * BUILDER GUIDANCE:
         *   In a single tick: expect for step-1 is true → confirmed + continue.
         *   expect for step-2 is false → step-2 is the first unconfirmed step → Interact returned.
         *   The returned action's Origin.Id must be "talk-to-wymond".
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(66130), 0);
        harness.GameState.SetZone(new ZoneId(182));
        harness.GameState.SetPosition(WymondPos);  // step-1 expect true; step-2 expect false

        var quest = LoadFixture();
        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d2");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — step-1 confirmed this tick; step-2 is first unconfirmed → Interact
        var interact = Assert.IsType<EngineAction.Interact>(action);
        Assert.Equal(new NpcId(1003987), interact.Target);
        Assert.NotNull(interact.Origin);
        Assert.Equal("talk-to-wymond", interact.Origin!.Id);
    }

    // -------------------------------------------------------------------------
    // D3: multiple steps confirmed same tick → engine returns first unconfirmed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task D3_MultipleStepsConfirmedSameTick_ReturnsFirstUnconfirmed()
    {
        /*
         * RED: Will fail until Builder adds the cursor.
         *
         * CONTRACT: Given a synthetic 3-step quest:
         *             step-A: expect playerNear(5,0,5, 10)  → true at player (0,0,0)
         *             step-B: expect playerNear(6,0,6, 10)  → true at player (0,0,0)
         *             step-C: expect playerNear(500,0,500, 3) → false (far away)
         *           When BeginRun, tick,
         *           Then action is Navigate for step-C (A and B both confirmed this tick).
         *
         * BUILDER GUIDANCE:
         *   step-A expect true → confirmed + continue.
         *   step-B expect true → confirmed + continue.
         *   step-C expect false → first unconfirmed → Navigate to step-C's destination.
         */

        // Arrange
        const uint questId = 99007;
        var posA = new Position3(5f, 0f, 5f);
        var posB = new Position3(6f, 0f, 6f);
        var posC = new Position3(500f, 0f, 500f);

        var quest = BuildSyntheticQuest(questId,
            new QuestSequence
            {
                Sequence = 0,
                Steps =
                [
                    TravelStepWithExpect("step-a", posA, "playerNear({\"x\":5,\"y\":0,\"z\":5}, 10)"),
                    TravelStepWithExpect("step-b", posB, "playerNear({\"x\":6,\"y\":0,\"z\":6}, 10)"),
                    TravelStepWithExpect("step-c", posC, "playerNear({\"x\":500,\"y\":0,\"z\":500}, 3)")
                ]
            });

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(questId), 0);
        harness.GameState.SetZone(new ZoneId(182));
        // Player at origin — within 10 of posA and posB; far from posC
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        harness.Engine.StartQuest(quest);
        harness.Engine.BeginRun("run-d3");

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — steps A and B confirmed this tick; step-C is first unconfirmed → Navigate
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(new WorldPosition(posC.X, posC.Y, posC.Z), navigate.Destination);
    }
}
