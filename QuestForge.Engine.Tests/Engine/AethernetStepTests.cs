using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of AethernetStep.
/// Spec: docs/AETHERNET_STEP_PLAN.md -- Decisions AN1-AN9, scenarios A1-A10.
///
/// RED PHASE: All tests fail to compile because AethernetStep does not exist in
/// QuestForge.Schema yet. The builder must add AethernetStep to Step.cs,
/// register it in QuestForgeJsonContext, and add the dispatch arm in QuestEngine.
/// </summary>
public sealed class AethernetStepTests
{
    // =========================================================================
    // A1 -- Happy path: player far from source shard, emits Navigate to fromPosition
    // =========================================================================

    [Fact]
    public async Task AethernetStep_PlayerFarFromShard_EmitsNavigate()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player at (0, 0, 0) in zone 128,
         *           AND AethernetStep { From=8, To=48, FromPosition=(-218.9, 16.0, 51.4) },
         *           When Engine.Tick() is called,
         *           Then returns EngineAction.Navigate with Destination approximately (-218.9, 16.0, 51.4).
         *
         * BUILDER GUIDANCE:
         *   1. Add AethernetStep class to Step.cs with From (uint), To (uint), FromPosition (Position3).
         *   2. Add dispatch arm in ResolveActionForStep using ResolveInteractOrNavigate.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80001), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: 80001,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-far",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var nav = Assert.IsType<EngineAction.Navigate>(action);
        Assert.Equal(-218.9f, nav.Destination.X, precision: 1);
        Assert.Equal(16.0f, nav.Destination.Y, precision: 1);
        Assert.Equal(51.4f, nav.Destination.Z, precision: 1);
    }

    // =========================================================================
    // A2 -- Happy path: player near source shard, emits UseAethernet
    // =========================================================================

    [Fact]
    public async Task AethernetStep_PlayerNearShard_EmitsUseAethernet()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player at (-218.0, 16.0, 51.0) in zone 129 (near source shard),
         *           AND AethernetStep { From=8, To=48, FromPosition=(-218.9, 16.0, 51.4) },
         *           When Engine.Tick() is called,
         *           Then returns EngineAction.UseAethernet with Destination.Value == 48
         *                and SourcePosition approximately (-218.9, 16.0, 51.4).
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80002), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(-218.0f, 16.0f, 51.0f));

        var quest = BuildSingleStepQuest(
            questId: 80002,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-near",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var useAethernet = Assert.IsType<EngineAction.UseAethernet>(action);
        Assert.Equal(48u, useAethernet.Destination.Value);
        Assert.NotNull(useAethernet.SourcePosition);
        var src = useAethernet.SourcePosition!.Value;
        Assert.Equal(-218.9f, src.X, precision: 1);
        Assert.Equal(16.0f, src.Y, precision: 1);
        Assert.Equal(51.4f, src.Z, precision: 1);
    }

    // =========================================================================
    // A3 -- Expect already satisfied: step skipped, no action emitted
    // =========================================================================

    [Fact]
    public async Task AethernetStep_ExpectAlreadySatisfied_StepSkipped_EmitsWait()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player in zone 128 (the Expect zone),
         *           AND AethernetStep { Expect = "playerZone() == 128" },
         *           When Engine.Tick() is called,
         *           Then returns EngineAction.Wait (Expect is already true).
         *           AND harness.Teleporter.RecordedAethernetTeleports.Count == 0.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80003), 0);
        harness.GameState.SetZone(new ZoneId(128));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: 80003,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-already-there",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.Teleporter.RecordedAethernetTeleports.Count);
    }

    // =========================================================================
    // A4 -- Two-tick completion: Navigate then UseAethernet then Expect satisfied
    // =========================================================================

    [Fact]
    public async Task AethernetStep_TwoTickCompletion_NavigateThenUseAethernetThenDone()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player at (0,0,0) in zone 129,
         *           AND AethernetStep { From=8, To=48, FromPosition=(-218.9, 16.0, 51.4),
         *               Expect="playerZone() == 128" },
         *           When Tick 1 -> Navigate, move player to shard position,
         *                Tick 2 -> UseAethernet, manually call TeleportToAethernet + flip zone to 128,
         *                Tick 3,
         *           Then Tick 3 returns Wait (Expect satisfied).
         *                RecordedAethernetTeleports.Count == 1.
         *                Recorded call's Destination.Value == 48.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80004), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: 80004,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-two-tick",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: player far -> Navigate
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Move player to shard position
        harness.GameState.SetPosition(new WorldPosition(-218.9f, 16.0f, 51.4f));

        // Tick 2: player near shard -> UseAethernet
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        var useAethernet = Assert.IsType<EngineAction.UseAethernet>(tick2);
        Assert.Equal(48u, useAethernet.Destination.Value);

        // Manually dispatch the aethernet teleport and flip zone
        await harness.Teleporter.TeleportToAethernet(new AethernetId(48), CancellationToken.None);
        harness.GameState.SetZone(new ZoneId(128));

        // Tick 3: Expect satisfied -> Wait
        var tick3 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick3);

        Assert.Equal(1, harness.Teleporter.RecordedAethernetTeleports.Count);
        Assert.Equal(48u, harness.Teleporter.RecordedAethernetTeleports[0].Destination.Value);
    }

    // =========================================================================
    // A5 -- Custom StopDistance: player within custom range emits UseAethernet
    // =========================================================================

    [Fact]
    public async Task AethernetStep_CustomStopDistance_PlayerWithinRange_EmitsUseAethernet()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player at (-210.0, 16.0, 51.0) (~9 units from shard),
         *           AND AethernetStep { StopDistance=10.0f },
         *           When Engine.Tick() is called,
         *           Then returns EngineAction.UseAethernet (within custom 10-unit range).
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80005), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(-210.0f, 16.0f, 51.0f));

        var quest = BuildSingleStepQuest(
            questId: 80005,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-custom-stop",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                StopDistance = 10.0f,
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.UseAethernet>(action);
    }

    // =========================================================================
    // A6 -- Default StopDistance: player at ~4 units emits Navigate (just outside 3.0)
    // =========================================================================

    [Fact]
    public async Task AethernetStep_DefaultStopDistance_PlayerJustOutside_EmitsNavigate()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player at (-215.0, 16.0, 51.4) (~3.9 units from shard),
         *           AND AethernetStep with no custom StopDistance (default 3.0),
         *           When Engine.Tick() is called,
         *           Then returns EngineAction.Navigate (beyond default 3.0).
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80006), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(-215.0f, 16.0f, 51.4f));

        var quest = BuildSingleStepQuest(
            questId: 80006,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-default-stop",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Navigate>(action);
    }

    // =========================================================================
    // A7 -- Adapter-reported Arrived but wrong zone: engine re-emits UseAethernet
    // =========================================================================

    [Fact]
    public async Task AethernetStep_ArrivedButWrongZone_ReEmitsUseAethernet()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player near shard in zone 129,
         *           AND AethernetStep { Expect="playerZone() == 128" },
         *           AND after TeleportToAethernet zone flips to 999 (wrong),
         *           When Tick 1 -> UseAethernet, manually dispatch + flip zone to 999,
         *                Tick 2,
         *           Then Tick 2 returns UseAethernet again (Expect still false).
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80007), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(-218.0f, 16.0f, 51.0f));

        var quest = BuildSingleStepQuest(
            questId: 80007,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-wrong-zone",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: UseAethernet
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAethernet>(tick1);

        // Manually dispatch: zone flips to wrong zone 999
        await harness.Teleporter.TeleportToAethernet(new AethernetId(48), CancellationToken.None);
        harness.GameState.SetZone(new ZoneId(999));

        // Tick 2: Expect still false -> re-emits UseAethernet
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAethernet>(tick2);

        // One manual dispatch recorded; tick2 UseAethernet above proves the engine re-emitted
        Assert.Equal(1, harness.Teleporter.RecordedAethernetTeleports.Count);
    }

    // =========================================================================
    // A8 -- Missing Expect (null): engine emits UseAethernet every tick (spin-loop)
    // =========================================================================

    [Fact]
    public async Task AethernetStep_MissingExpect_SpinLoopsUseAethernet()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given player near shard in zone 129,
         *           AND AethernetStep with no Expect,
         *           When Tick twice with no state changes,
         *           Then both ticks return UseAethernet (no way to know step is complete).
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80008), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(-218.0f, 16.0f, 51.0f));

        var quest = BuildSingleStepQuest(
            questId: 80008,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-no-expect",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f)
            });

        harness.Engine.StartQuest(quest);

        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAethernet>(tick1);

        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.UseAethernet>(tick2);
    }

    // =========================================================================
    // A10 -- Cancellation propagates
    // =========================================================================

    [Fact]
    public async Task AethernetStep_CancelledToken_ThrowsOperationCanceledException()
    {
        /*
         * RED: Will fail to compile -- AethernetStep does not exist yet.
         *
         * CONTRACT: Given AethernetStep with valid fields, player far from shard,
         *           AND a pre-cancelled CancellationToken,
         *           When Engine.Tick(ct) is called,
         *           Then OperationCanceledException propagates.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(80010), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: 80010,
            sequence: 0,
            step: new AethernetStep
            {
                Id = "aethernet-cancellation",
                From = 8,
                To = 48,
                FromPosition = new Position3(-218.9f, 16.0f, 51.4f),
                Expect = new PredicateExpect { Predicate = "playerZone() == 128" }
            });

        harness.Engine.StartQuest(quest);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Aethernet Step Test Quest",
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
