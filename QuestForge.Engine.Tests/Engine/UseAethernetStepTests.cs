using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using AdaptersNpcId = QuestForge.Adapters.Types.NpcId;
using QuestId = QuestForge.Adapters.Types.QuestId;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of TravelStep with RouteHint.Aethernet.
///
/// RED PHASE: All tests referencing EngineAction.UseAethernet will fail to compile
/// because that action type does not exist yet. Tests B3, B4, B5 may compile but will
/// fail at runtime because the engine does not yet handle the Aethernet dispatch path.
///
/// Spec: issue #24 TravelStep.RouteHint.Aethernet support.
/// </summary>
public sealed class UseAethernetStepTests
{
    // =========================================================================
    // B1 — TravelStep with Aethernet=[33] emits UseAethernet(AethernetId(33))
    // =========================================================================

    [Fact]
    public async Task TravelStep_AethernetHint_EmitsUseAethernet_WithFirstShard()
    {
        /*
         * RED: Will fail to compile until Builder adds:
         *   1. EngineAction.UseAethernet action type
         *   2. AethernetRouteHint record in QuestForge.Schema
         *   3. RouteHint.Aethernet changed from uint[]? to AethernetRouteHint?
         *   4. Engine dispatch arm updated: { Length: > 0 } shards → { To: > 0 } hop
         *
         * CONTRACT: Given a TravelStep with RouteHint.Aethernet == { To=33u },
         *           AND player is in zone 130 (different from destination zone 131),
         *           When Engine.Tick is called,
         *           Then EngineAction.UseAethernet(new AethernetId(33)) is returned.
         *
         * BUILDER GUIDANCE:
         *   1. Add `public sealed record UseAethernet(AethernetId Destination, ...) : EngineAction;`
         *   2. Change engine dispatch pattern from `is { Length: > 0 } shards` to
         *      `is { To: > 0 } hop`, and use `new AethernetId(hop.To)` instead of `shards[0]`.
         *
         * TODO FOR BUILDER: This test previously used `new uint[] { 33 }` which was the
         * old array form. It now uses `new AethernetRouteHint(null, 33u)` per Issue #25.
         * The old array form is a breaking change — see SER_2_8 for the rejection test.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99001), 0);
        harness.GameState.SetZone(new ZoneId(130));

        var quest = BuildSingleStepQuest(
            questId: 99001,
            sequence: 0,
            step: new TravelStep
            {
                Id = "to-thal",
                Destination = new TravelDestination(Zone: 131),
                // TODO BUILDER: old form was `new uint[] { 33 }` — now AethernetRouteHint
                // RED: AethernetRouteHint does not exist yet — compile error expected
                RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: null, To: 33u))
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert
        var useAethernet = Assert.IsType<EngineAction.UseAethernet>(action); // RED: type does not exist yet
        Assert.Equal(new AethernetId(33), useAethernet.Destination);
    }

    // =========================================================================
    // B2 — AethernetRouteHint with From=125, To=33 → emits UseAethernet(33)
    //       (single hop; old multi-shard array form is removed per Issue #25)
    // =========================================================================

    [Fact]
    public async Task TravelStep_AethernetRouteHintFromAndTo_EmitsUseAethernetWithTo()
    {
        /*
         * RED: Will fail to compile until Builder adds EngineAction.UseAethernet
         * AND adds AethernetRouteHint and changes dispatch pattern to { To: > 0 } hop.
         *
         * CONTRACT: Given a TravelStep with RouteHint.Aethernet == { From=125, To=33 },
         *           When Engine.Tick is called,
         *           Then EngineAction.UseAethernet with Destination == AethernetId(33) is returned.
         *           The engine reads hop.To as the destination shard ID.
         *
         * BUILDER GUIDANCE: Change the dispatch pattern from:
         *   travel.RouteHint?.Aethernet is { Length: > 0 } shards → new AethernetId(shards[0])
         * to:
         *   travel.RouteHint?.Aethernet is { To: > 0 } hop → new AethernetId(hop.To)
         * The From field is informational (used by trace/authoring); engine only uses To.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99002), 0);
        harness.GameState.SetZone(new ZoneId(128));

        // NOTE: With the new AethernetRouteHint type, multi-hop encoding via an array
        // is no longer supported. AethernetRouteHint carries a single From→To hop.
        // The "first shard" semantics now map to .To directly.
        // TODO BUILDER: Old form was `new uint[] { 33, 125 }`. Now uses single hop.
        // RED: AethernetRouteHint does not exist yet — compile error expected
        var quest = BuildSingleStepQuest(
            questId: 99002,
            sequence: 0,
            step: new TravelStep
            {
                Id = "multi-shard",
                Destination = new TravelDestination(Zone: 131),
                RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: 125u, To: 33u))
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — destination shard (hop.To) is emitted
        var useAethernet = Assert.IsType<EngineAction.UseAethernet>(action); // RED: type does not exist yet
        Assert.Equal(new AethernetId(33), useAethernet.Destination);
    }

    // =========================================================================
    // B3 — TravelStep with no RouteHint and a Position → still emits Navigate
    //       (regression guard: Aethernet dispatch must not break the Position path)
    // =========================================================================

    [Fact]
    public async Task TravelStep_NoRouteHint_WithPosition_EmitsNavigate()
    {
        /*
         * RED: Will fail at runtime until Builder restructures the TravelStep dispatch arms
         * in the correct order. Currently the existing arm fires for any TravelStep with
         * a Position, so this test may pass before the Builder touches the code. It is
         * authored here as a regression guard — it must continue to pass after B1's arm
         * is inserted above it.
         *
         * CONTRACT: Given a TravelStep with Destination.Position set and no RouteHint,
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned.
         *
         * BUILDER GUIDANCE: The Aethernet arm must be ordered BEFORE the Position arm in
         *   the switch expression. The Position arm remains unchanged.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99003), 0);

        var quest = BuildSingleStepQuest(
            questId: 99003,
            sequence: 0,
            step: new TravelStep
            {
                Id = "navigate-to-pos",
                Destination = new TravelDestination(Zone: 131, Position: new Position3(10f, 0f, 20f)),
                RouteHint = null
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — Aethernet path not taken; falls through to Navigate
        Assert.IsType<EngineAction.Navigate>(action);
    }

    // =========================================================================
    // B4 — TravelStep with empty Aethernet array → emits Navigate
    //       (empty Aethernet == no aethernet route specified)
    // =========================================================================

    [Fact]
    public async Task TravelStep_NullAethernet_WithPosition_EmitsNavigate()
    {
        /*
         * CONTRACT: Given a TravelStep with RouteHint.Aethernet == null
         *               AND Destination.Position is set,
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate is returned (null = no aethernet route).
         *
         * NOTE: With the AethernetRouteHint type change, the old empty-array sentinel
         * (Array.Empty<uint>()) no longer applies. The equivalent is Aethernet = null.
         * The dispatch pattern changes from `{ Length: > 0 }` to `{ To: > 0 }`;
         * a null Aethernet does not match and falls through to the Position arm.
         *
         * TODO BUILDER: Old test used `new RouteHint(Aethernet: Array.Empty<uint>())`.
         * That form is now a compile error. This test now verifies the null case.
         * The AethernetRouteHint(From, To) form with To=0 is the equivalent "bad" case
         * and is covered by engine dispatch tests for To==0 guard.
         *
         * BUILDER GUIDANCE: Change the dispatch pattern from:
         *   travel.RouteHint?.Aethernet is { Length: > 0 } shards
         * to:
         *   travel.RouteHint?.Aethernet is { To: > 0 } hop
         * Then: null Aethernet does not match → falls through to Position arm.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99004), 0);

        var quest = BuildSingleStepQuest(
            questId: 99004,
            sequence: 0,
            step: new TravelStep
            {
                Id = "null-aethernet",
                Destination = new TravelDestination(Zone: 131, Position: new Position3(5f, 0f, 5f)),
                RouteHint = new RouteHint(Aethernet: null)
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — null Aethernet does not trigger Aethernet path
        Assert.IsType<EngineAction.Navigate>(action);
    }

    // =========================================================================
    // B5 — TravelStep with Aethernet hint but Expect already satisfied → Wait
    //       (step is skipped by the per-step Expect guard before dispatch)
    // =========================================================================

    [Fact]
    public async Task TravelStep_AethernetHint_ExpectAlreadySatisfied_EmitsWait()
    {
        /*
         * RED: Will compile (no UseAethernet reference here), but will fail at runtime
         * because the Aethernet arm throws NotSupportedException today instead of
         * executing. Once the Expect guard fires before dispatch, this test passes.
         *
         * CONTRACT: Given a TravelStep with RouteHint.Aethernet=[33] and
         *               Expect="playerZone() == 131",
         *           AND player zone is already 131 (condition already satisfied),
         *           When Engine.Tick is called,
         *           Then the per-step Expect guard short-circuits (continue),
         *                the step is skipped, no further steps exist → EngineAction.Wait.
         *
         * BUILDER GUIDANCE: The Expect guard in QuestEngine.ResolveAction fires before
         *   ResolveActionForStep. When the player is already in zone 131, playerZone() == 131
         *   evaluates true, the step is skipped via `continue`, and Wait is returned.
         *   No UseAethernet is ever emitted.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99005), 0);
        harness.GameState.SetZone(new ZoneId(131)); // already at destination

        var quest = BuildSingleStepQuest(
            questId: 99005,
            sequence: 0,
            step: new TravelStep
            {
                Id = "to-thal-expect",
                Destination = new TravelDestination(Zone: 131),
                // TODO BUILDER: old form was `new uint[] { 33 }` — now AethernetRouteHint
                // RED: AethernetRouteHint does not exist yet — compile error expected
                RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: null, To: 33u)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 131" }
            });

        harness.Engine.StartQuest(quest);

        // Act
        var action = await harness.Engine.Tick(CancellationToken.None);

        // Assert — Expect fired; dispatch never reached; no further steps → Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // B6a — TravelStep with Aethernet + Position, player AT source → UseAethernet
    //        (two-phase: navigate to source shard first, then teleport)
    // =========================================================================

    [Fact]
    public async Task TravelStep_AethernetAndPosition_PlayerAtSource_EmitsUseAethernet()
    {
        /*
         * CONTRACT: Given a TravelStep with RouteHint.Aethernet=[33] and
         *               Destination.Position=(10,0,20) (source shard coords),
         *           AND player is AT the source position (distance 0, within StopDistance),
         *           When Engine.Tick is called,
         *           Then EngineAction.UseAethernet(AethernetId(33)) is returned
         *           with SourcePosition matching the Destination.Position.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99006), 0);
        harness.GameState.SetPosition(new WorldPosition(10f, 0f, 20f)); // at source shard

        var quest = BuildSingleStepQuest(
            questId: 99006,
            sequence: 0,
            step: new TravelStep
            {
                Id = "aethernet-at-source",
                Destination = new TravelDestination(Zone: 131, Position: new Position3(10f, 0f, 20f)),
                // TODO BUILDER: old form was `new uint[] { 33 }` — now AethernetRouteHint
                // RED: AethernetRouteHint does not exist yet — compile error expected
                RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: null, To: 33u))
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useAethernet = Assert.IsType<EngineAction.UseAethernet>(action);
        Assert.Equal(new AethernetId(33), useAethernet.Destination);
        Assert.NotNull(useAethernet.SourcePosition); // carries source coords for trace
    }

    // =========================================================================
    // B6b — TravelStep with Aethernet + Position, player FAR from source → Navigate
    //        (navigate to source shard before emitting UseAethernet)
    // =========================================================================

    [Fact]
    public async Task TravelStep_AethernetAndPosition_PlayerFarFromSource_EmitsNavigate()
    {
        /*
         * CONTRACT: Given the same TravelStep (Aethernet={ To=33 }, Position=(10,0,20)),
         *           AND player is FAR from source (0,0,0) — distance > StopDistance,
         *           When Engine.Tick is called,
         *           Then EngineAction.Navigate toward (10,0,20) is returned.
         *           UseAethernet is not emitted until the player arrives at the source.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99007), 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f)); // far from source

        var quest = BuildSingleStepQuest(
            questId: 99007,
            sequence: 0,
            step: new TravelStep
            {
                Id = "aethernet-navigate-first",
                Destination = new TravelDestination(Zone: 131, Position: new Position3(10f, 0f, 20f)),
                // TODO BUILDER: old form was `new uint[] { 33 }` — now AethernetRouteHint
                // RED: AethernetRouteHint does not exist yet — compile error expected
                RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: null, To: 33u))
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Player is far — navigate to source shard first
        var navigate = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(10f, navigate.Destination.X, precision: 2);
        Assert.Equal(20f, navigate.Destination.Z, precision: 2);
    }

    // =========================================================================
    // B7 — Regression: Aetheryte-only step (no Position, no Aethernet)
    //       still throws NotSupportedException (existing behavior preserved)
    // =========================================================================

    [Fact]
    public async Task TravelStep_AetheryteOnly_NoAethernetNoPosition_ThrowsNotSupported()
    {
        /*
         * RED: Will fail at runtime until Builder implements the Aethernet arm,
         * because today the existing NotSupportedException arm fires for both
         * Aetheryte-only steps and for Aethernet steps (both have null Position).
         * After the Builder inserts the Aethernet arm, Aethernet steps are dispatched
         * correctly, but Aetheryte-only steps (RouteHint.Aetheryte set, no Aethernet)
         * must still reach the NotSupportedException arm.
         *
         * CONTRACT: Given a TravelStep with RouteHint.Aetheryte="Limsa Lominsa" but
         *               RouteHint.Aethernet is null and Destination.Position is null,
         *           When Engine.Tick is called,
         *           Then NotSupportedException propagates (Phase 4 guard preserved).
         *
         * BUILDER GUIDANCE: After inserting the Aethernet arm, ensure the final TravelStep
         *   arm (null Position) still reads:
         *     TravelStep travel when travel.Destination.Position is null =>
         *         throw new NotSupportedException("Phase 4 does not support aetheryte-only travel steps"),
         *   Aetheryte-only steps (Aethernet null or empty) fall through to this arm.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99007), 0);

        var quest = BuildSingleStepQuest(
            questId: 99007,
            sequence: 0,
            step: new TravelStep
            {
                Id = "aetheryte-only",
                Destination = new TravelDestination(Zone: 131),
                RouteHint = new RouteHint(Aetheryte: "Limsa Lominsa Lower Decks", Aethernet: null)
            });

        harness.Engine.StartQuest(quest);

        // Act + Assert — must still throw NotSupportedException
        await Assert.ThrowsAsync<NotSupportedException>(
            () => harness.Engine.Tick(CancellationToken.None));
    }

    // =========================================================================
    // I1 — Integration: full tick sequence simulating Aethernet arrival
    // =========================================================================

    [Fact]
    public async Task TravelStep_AethernetHint_TickSequence_ArrivalSimulated()
    {
        /*
         * RED: Will fail to compile until Builder adds EngineAction.UseAethernet
         * AND adds the UseAethernet arm to EngineTestHarness.RunToCompletion.
         *
         * CONTRACT:
         *   Tick 1: player in zone 130; step Expect = "playerZone() == 131" not satisfied;
         *           engine emits UseAethernet(AethernetId(33));
         *           harness calls Teleporter.TeleportToAethernet and simulates arrival by
         *           calling harness.GameState.SetZone(new ZoneId(131));
         *   Tick 2: playerZone() == 131 → Expect satisfied → step skipped → Wait;
         *   Assert: harness.Teleporter.RecordedAethernetTeleports.Count == 1
         *           AND recorded call's Destination.Value == 33.
         *
         * BUILDER GUIDANCE:
         *   Add the following arm to EngineTestHarness.RunToCompletion's switch:
         *
         *     case EngineAction.UseAethernet ua:
         *         actions.Add(action);
         *         EmitActionSubmitted("UseAethernet",
         *             JsonSerializer.SerializeToElement(ua.Destination, _jsonOpts));
         *         var aethernetResult = await Teleporter.TeleportToAethernet(ua.Destination, ct);
         *         EmitActionCompleted("UseAethernet",
         *             aethernetResult.IsSuccess ? aethernetResult.ValueOrThrow.ToString() : "Failed");
         *         break;
         *
         *   FakeTeleporter.TeleportToAethernet already exists and records the call.
         *   The test manually calls SetZone after Tick 1 to simulate in-game arrival.
         */

        // Arrange
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(99008), 0);
        harness.GameState.SetZone(new ZoneId(130)); // player NOT at destination yet

        var quest = BuildSingleStepQuest(
            questId: 99008,
            sequence: 0,
            step: new TravelStep
            {
                Id = "integration-aethernet",
                Destination = new TravelDestination(Zone: 131),
                // TODO BUILDER: old form was `new uint[] { 33 }` — now AethernetRouteHint
                // RED: AethernetRouteHint does not exist yet — compile error expected
                RouteHint = new RouteHint(Aethernet: new AethernetRouteHint(From: null, To: 33u)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 131" }
            });

        harness.Engine.StartQuest(quest);

        // Act — Tick 1: Expect not satisfied → UseAethernet emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var useAethernet = Assert.IsType<EngineAction.UseAethernet>(tick1); // RED: type does not exist yet
        Assert.Equal(new AethernetId(33), useAethernet.Destination);

        // Simulate arrival (harness.RunToCompletion would handle this; here we do it manually)
        await harness.Teleporter.TeleportToAethernet(useAethernet.Destination, CancellationToken.None);
        harness.GameState.SetZone(new ZoneId(131));

        // Tick 2: playerZone() == 131 → Expect satisfied → step skipped → Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        // Assert adapter call recorded
        Assert.Equal(1, harness.Teleporter.RecordedAethernetTeleports.Count);
        Assert.Equal(33u, harness.Teleporter.RecordedAethernetTeleports[0].Destination.Value);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "UseAethernet Test Quest",
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
