using System.Text.Json;
using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using AdaptersAetheryteId = QuestForge.Adapters.Types.AetheryteId;
using QuestId = QuestForge.Adapters.Types.QuestId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// Tests for QuestEngine dispatch of TeleportStep.
/// Spec: docs/TELEPORT_STEP_PLAN.md — Decisions T1-T11, scenarios T1-T12.
/// </summary>
public sealed class TeleportStepTests
{
    // =========================================================================
    // T1 — Happy path: not yet in target zone → emits Teleport
    // =========================================================================

    [Fact]
    public async Task TeleportStep_NotInTargetZone_EmitsTeleport()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71001), 0);
        harness.GameState.SetZone(new ZoneId(129));

        var quest = BuildSingleStepQuest(
            questId: 71001,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-to-test-aetheryte",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var teleport = Assert.IsType<EngineAction.Teleport>(action);
        Assert.Equal(new AdaptersAetheryteId(1000), teleport.Destination);
        Assert.NotNull(teleport.Origin); // step is passed through as origin
    }

    // =========================================================================
    // T2 — Happy path completion: I-integration tick sequence
    // =========================================================================

    [Fact]
    public async Task TeleportStep_IntegrationTickSequence_ArrivesAndCompletes()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71002), 0);
        harness.GameState.SetZone(new ZoneId(129));

        harness.Teleporter.RegisterAetheryte(
            new AdaptersAetheryteId(1000),
            new ZoneId(130),
            new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: 71002,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-integration",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        var teleport = Assert.IsType<EngineAction.Teleport>(tick1);
        Assert.Equal(new AdaptersAetheryteId(1000), teleport.Destination);

        // Mirrors what RunToCompletion's Teleport arm does; fake flips zone to 130 on Arrived
        await harness.Teleporter.TeleportToAetheryte(teleport.Destination, CancellationToken.None);

        // Tick 2 — player now in zone 130; synthesised Expect satisfied → step done → Wait
        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(tick2);

        // Exactly one teleport call recorded
        Assert.Equal(1, harness.Teleporter.RecordedTeleports.Count);
        Assert.Equal(1000u, harness.Teleporter.RecordedTeleports[0].Destination.Value);
    }

    // =========================================================================
    // T3 — InCombat refusal: emits AwaitUser, no teleport call
    // =========================================================================

    [Fact]
    public async Task TeleportStep_InCombat_EmitsAwaitUser_NoTeleportCall()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71003), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetInCombat(true);

        var quest = BuildSingleStepQuest(
            questId: 71003,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-combat-guard",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("cannot teleport while in combat", awaitUser.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Teleporter.RecordedTeleports.Count);
    }

    // =========================================================================
    // T4 — Mounted before teleport: dismount fires when preceded by Navigate
    // =========================================================================

    [Fact]
    public async Task TeleportStep_AfterNavigate_MountedPlayer_DismountFires()
    {
        /*
         * Two-step quest: TravelStep (Expect = "playerZone() == 130") → TeleportStep.
         * Player starts in zone 128 so TravelStep.Expect is initially false.
         * After tick 1 returns Navigate, we flip the zone to 130 to satisfy TravelStep.
         * Tick 2 transitions from Navigate → Teleport; the lazy-dismount hook fires.
         */

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71004), 0);
        harness.GameState.SetZone(new ZoneId(128)); // not the TravelStep target zone
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildTwoStepQuest(
            questId: 71004,
            sequence: 0,
            step0: new TravelStep
            {
                Id = "navigate-before-teleport",
                Destination = new TravelDestination(Zone: 130, Position: new Position3(200f, 0f, 0f)),
                Expect = new PredicateExpect { Predicate = "playerZone() == 130" }
            },
            step1: new TeleportStep
            {
                Id = "teleport-after-navigate",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        // Tick 1: TravelStep.Expect false → Navigate emitted
        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Navigate>(tick1);

        // Simulate arrival
        harness.GameState.SetZone(new ZoneId(130));

        // Tick 2: TravelStep satisfied → advance to TeleportStep; lazy-dismount fires
        var tick2 = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Teleport>(tick2);
        Assert.True(harness.Mount.DismountCallCount >= 1,
            $"Expected DismountCallCount >= 1 but was {harness.Mount.DismountCallCount}.");
    }

    // =========================================================================
    // T4b — TeleportStep alone, mounted, no prior Navigate: dismount does NOT fire
    // =========================================================================

    [Fact]
    public async Task TeleportStep_Standalone_MountedPlayer_NoPriorNavigate_NoDismount()
    {
        // Lazy-dismount hook is bound to prior Navigate transitions; no prior Navigate → no dismount.
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71004_2), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetMountState(MountState.Mounted);

        var quest = BuildSingleStepQuest(
            questId: 71004_2,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-standalone-mounted",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Teleport>(action);
        Assert.Equal(0, harness.Mount.DismountCallCount);
    }

    // =========================================================================
    // T5 — ITeleporter returns failure → engine re-emits Teleport (stateless retry)
    // =========================================================================

    [Fact]
    public async Task TeleportStep_TeleporterFailure_ReEmitsTeleport()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71005), 0);
        harness.GameState.SetZone(new ZoneId(129));

        // No RegisterAetheryte — zone would not flip even without failure (defense-in-depth)
        var quest = BuildSingleStepQuest(
            questId: 71005,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-failure-retry",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Teleport>(tick1);

        harness.Teleporter.ScriptNextTeleportFailure("adapter-error", "lifestream rejected");
        await harness.Teleporter.TeleportToAetheryte(new AdaptersAetheryteId(1000), CancellationToken.None);
        // Returns Result.Failure; zone stays at 129

        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Teleport>(tick2);

        // Only the manual call is recorded; engine ticks emit the action but do not call the adapter
        Assert.Equal(1, harness.Teleporter.RecordedTeleports.Count);
    }

    // =========================================================================
    // T6 — Adapter-reported Arrived but wrong zone → engine re-emits Teleport
    // =========================================================================

    [Fact]
    public async Task TeleportStep_ArrivedButWrongZone_ReEmitsTeleport()
    {
        // RegisterAetheryte with zone 999 (wrong; map says 130). Synthesised Expect stays false.
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71006), 0);
        harness.GameState.SetZone(new ZoneId(129));

        harness.Teleporter.RegisterAetheryte(
            new AdaptersAetheryteId(1000),
            new ZoneId(999),
            new WorldPosition(0f, 0f, 0f));

        var quest = BuildSingleStepQuest(
            questId: 71006,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-wrong-zone",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        var tick1 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Teleport>(tick1);

        await harness.Teleporter.TeleportToAetheryte(new AdaptersAetheryteId(1000), CancellationToken.None);
        // Fake flips zone to 999; synthesised Expect "playerZone() == 130" stays false

        var tick2 = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Teleport>(tick2);

        Assert.Equal(1, harness.Teleporter.RecordedTeleports.Count);
    }

    // =========================================================================
    // T7 — Unknown aetheryteId → AwaitUser before calling ITeleporter
    // =========================================================================

    [Fact]
    public async Task TeleportStep_UnknownAetheryteId_EmitsAwaitUser_NoTeleportCall()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71007), 0);
        harness.GameState.SetZone(new ZoneId(129));

        var quest = BuildSingleStepQuest(
            questId: 71007,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-unknown-aetheryte",
                AetheryteId = new AetheryteId(424242) // guaranteed not in AetheryteZoneMap
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("424242", awaitUser.Reason);
        Assert.Contains("not in zone map", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Teleporter.RecordedTeleports.Count);
    }

    // =========================================================================
    // T8 — Already in target zone → step skipped via synthesised Expect, no teleport call
    // =========================================================================

    [Fact]
    public async Task TeleportStep_AlreadyInTargetZone_StepSkipped_EmitsWait()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71008), 0);
        harness.GameState.SetZone(new ZoneId(130));

        var quest = BuildSingleStepQuest(
            questId: 71008,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-already-there",
                AetheryteId = new AetheryteId(1000) // maps to zone 130
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.Teleporter.RecordedTeleports.Count);
    }

    // =========================================================================
    // T9 — Authored Expect overrides synthesis
    // =========================================================================

    [Fact]
    public async Task TeleportStep_AuthoredExpect_OverridesSynthesis_StepSkipped()
    {
        // Zone 129 ≠ 130, so the synthesised predicate would be false — but Expect is authored, suppressing synthesis.
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71009), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetAetheryteAttuned(new AdaptersAetheryteId(8), true);

        var quest = BuildSingleStepQuest(
            questId: 71009,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-authored-expect",
                AetheryteId = new AetheryteId(1000),
                Expect = new PredicateExpect { Predicate = "isAttuned(8)" }
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Authored Expect is true → step skipped → no more steps → Wait
        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.Teleporter.RecordedTeleports.Count);
    }

    // =========================================================================
    // T10 — Cancellation propagates from dispatch arm
    // =========================================================================

    [Fact]
    public async Task TeleportStep_CancelledToken_ThrowsOperationCanceledException()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71010), 0);
        harness.GameState.SetZone(new ZoneId(129));

        var quest = BuildSingleStepQuest(
            questId: 71010,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-cancellation",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Engine.Tick(cts.Token));
    }

    // =========================================================================
    // T11 — InCombat adapter read failure → fail-open, Teleport still emitted
    // =========================================================================

    [Fact]
    public async Task TeleportStep_InCombatReadFailure_FailOpen_EmitsTeleport()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(71011), 0);
        harness.GameState.SetZone(new ZoneId(129));
        harness.GameState.SetInCombatFailure("adapter-error");

        var quest = BuildSingleStepQuest(
            questId: 71011,
            sequence: 0,
            step: new TeleportStep
            {
                Id = "teleport-incombat-failopen",
                AetheryteId = new AetheryteId(1000)
            });

        harness.Engine.StartQuest(quest);

        var action = await harness.Engine.Tick(CancellationToken.None);

        var teleport = Assert.IsType<EngineAction.Teleport>(action);
        Assert.Equal(new AdaptersAetheryteId(1000), teleport.Destination);
        Assert.Equal(0, harness.Teleporter.RecordedTeleports.Count);
    }

    // =========================================================================
    // T12 — Round-trip JSON serialization of TeleportStep
    // =========================================================================

    [Fact]
    public void TeleportStep_RoundTripJson_SerializesAndDeserializesCorrectly()
    {
        Step original = new TeleportStep
        {
            Id = "teleport-to-limsa",
            AetheryteId = new AetheryteId(8)
        };

        var json = JsonSerializer.Serialize(original, QuestForgeJsonContext.QuestFileOptions);

        Assert.Contains("\"type\": \"teleport\"", json);
        Assert.Contains("\"aetheryteId\": 8", json);

        var deserialized = JsonSerializer.Deserialize<Step>(json, QuestForgeJsonContext.QuestFileOptions);

        var teleportStep = Assert.IsType<TeleportStep>(deserialized);
        Assert.Equal(8u, teleportStep.AetheryteId.Value);
        Assert.Equal("teleport-to-limsa", teleportStep.Id);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Teleport Step Test Quest",
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

    private static QuestDefinition BuildTwoStepQuest(
        uint questId,
        int sequence,
        Step step0,
        Step step1) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "Teleport Step Two-Step Test Quest",
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
                    Steps = [step0, step1]
                }
            ]
        };
}
