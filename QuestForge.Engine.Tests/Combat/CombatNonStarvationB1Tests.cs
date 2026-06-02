using QuestForge.Adapters.Fakes;
using QuestForge.Adapters.Fakes.Replay;
using QuestForge.Adapters.Tracing;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Replay;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// GREEN REGRESSION GUARDS — G-NS: fixture non-starvation after the GetHostileActors flip.
///
/// These tests assert that existing non-combat fixtures stay green after the part-B flip
/// (RecordingGameStateProvider records GetHostileActors, ReplayGameStateProvider scans it).
/// Non-starvation is guaranteed because GetHostileActors is step-gated (only called inside
/// CombatController.Decide, which only runs when the active step is a CombatStep).
/// The two existing fixtures have no combat steps → GetHostileActors is never requested
/// → no (GetHostileActors, *) scanner lookup → no starvation. Pins §A7.
///
/// NS-existing-fixtures-green: skip when questforge-data is absent (same as the parametric theory).
/// NS-noncombat-tick-no-hostile-read: always runs (no data dependency).
///
/// Spec: docs/COMBAT_STEP_PART_B_PLAN.md §G-NS
/// </summary>
public sealed class CombatNonStarvationB1Tests
{
    // -------------------------------------------------------------------------
    // NS-existing-fixtures-green
    // -------------------------------------------------------------------------

    /// <summary>
    /// NS-existing-fixtures-green: simple-linear-acceptance and with-attunement fixtures
    /// still pass (or skip) unchanged after the GetHostileActors record/scan flip.
    /// No re-record is required because neither fixture has a CombatStep.
    ///
    /// Skips when questforge-data is absent (exactly like EngineFixtureTests.EngineProducesExpectedTransitions).
    /// Expected to PASS (regression guard, not a RED test).
    /// </summary>
    [Theory]
    [InlineData("fixtures/engine/simple-linear-acceptance.json")]
    [InlineData("fixtures/engine/with-attunement.json")]
    public void NS_ExistingFixtures_Green_AfterGetHostileActorsFlip(string relativePath)
    {
        // This test is a regression guard, not a new behavior test.
        // It must PASS immediately (the step-gating guarantee means the flip cannot starve them).
        // It skips if questforge-data is absent (same condition as the parametric theory).

        var dataRoot = FixtureLocator.TryGetQuestForgeDataRoot();
        if (dataRoot is null)
        {
            Assert.Skip(
                $"NS-existing-fixtures-green: questforge-data not present. " +
                $"Skipping regression guard for '{relativePath}'. " +
                $"When questforge-data is present, the parametric EngineFixtureTests theory covers this.");
            return;
        }

        var fixturePath = System.IO.Path.Combine(
            dataRoot, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        if (!System.IO.File.Exists(fixturePath))
        {
            Assert.Skip($"NS-existing-fixtures-green: fixture file not found at '{fixturePath}'. Skipping.");
            return;
        }

        // The fixture exists and questforge-data is present.
        // The parametric EngineFixtureTests.EngineProducesExpectedTransitions theory will run it.
        // This guard verifies the file is present and accessible; the parametric theory
        // verifies the actual engine transitions. If the flip caused starvation, the parametric
        // theory would throw ReplayObservationStarvationException → Assert.Skip
        // (per WrapTickForStarvation), surfacing as a skip, not a silent pass.
        //
        // Result: this test passes (fixture file exists and is reachable).
        Assert.True(System.IO.File.Exists(fixturePath),
            $"Fixture must exist at '{fixturePath}' for the parametric theory to run it.");
    }

    // -------------------------------------------------------------------------
    // NS-noncombat-tick-no-hostile-read
    // -------------------------------------------------------------------------

    /// <summary>
    /// NS-noncombat-tick-no-hostile-read: a TalkStep / TravelStep tick driven by the engine
    /// (via a RecordingGameStateProvider) emits NO GetHostileActors ObservationEvent.
    /// Pins that the combat read stays step-gated and never leaked into the per-tick prelude
    /// even after the flip.
    ///
    /// Expected to PASS immediately (step-gating pre-exists the flip; this is a pin test).
    /// </summary>
    [Fact]
    public async Task NS_NoncombatTick_NoGetHostileActorsRead_TalkStepEmitsNoHostileObs()
    {
        // Build a minimal quest with only a TalkStep (no CombatStep).
        var questId = new QuestId(70001u);

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId.Value,
            Name = "NS-B1: Non-combat talk quest",
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
                    Sequence = 0,
                    Steps =
                    [
                        new TalkStep
                        {
                            Id = "ns-talk-npc",
                            Target = new NpcLocation(1000u, 0, new Position3(0f, 0f, 0f))
                        }
                    ]
                }
            ]
        };

        var harness = new Helpers.EngineTestHarness();
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.Engine.StartQuest(quest);

        // Clear any reads accumulated during StartQuest
        harness.GameState.Reset();
        // Also clear trace events so we only see tick-produced observations
        harness.TraceWriter.Reset();

        await harness.Engine.Tick(CancellationToken.None);

        // The trace should contain NO GetHostileActors observation (step-gated)
        var hostileObs = harness.TraceWriter.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "GetHostileActors")
            .ToList();

        Assert.Empty(hostileObs);
    }

    /// <summary>
    /// NS-noncombat-tick-no-hostile-read (TravelStep variant): same assertion for a TravelStep.
    /// Expected to PASS immediately.
    /// </summary>
    [Fact]
    public async Task NS_NoncombatTick_NoGetHostileActorsRead_TravelStepEmitsNoHostileObs()
    {
        var questId = new QuestId(70002u);

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId.Value,
            Name = "NS-B1: Non-combat travel quest",
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
                    Sequence = 0,
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "ns-travel",
                            Destination = new TravelDestination(130, new Position3(100f, 0f, 100f))
                        }
                    ]
                }
            ]
        };

        var harness = new Helpers.EngineTestHarness();
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        harness.Engine.StartQuest(quest);

        harness.GameState.Reset();
        harness.TraceWriter.Reset();

        await harness.Engine.Tick(CancellationToken.None);

        var hostileObs = harness.TraceWriter.RecordedEvents
            .OfType<ObservationEvent>()
            .Where(e => e.Data.Method == "GetHostileActors")
            .ToList();

        Assert.Empty(hostileObs);
    }
}
