using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Combat;

/// <summary>
/// NS-existing-fixtures-green (inline variant) — asserts that non-combat step ticks
/// never invoke GetHostileActors in the engine.
///
/// The full parametric replay (simple-linear-acceptance, with-attunement) is already
/// covered by EngineFixtureTests.EngineProducesExpectedTransitions — those tests must
/// continue to PASS and serve as the NS regression guard.
///
/// This file adds inline unit assertions that pin the step-gating contract directly,
/// without requiring the questforge-data repo. They must PASS immediately.
///
/// Spec: docs/COMBAT_STEP_PART_A_PLAN.md §G7
/// </summary>
public sealed class CombatNonStarvationTests
{
    private static string FormatReads(FakeGameStateProvider gs)
        => string.Join(", ", gs.RecordedReads.Snapshot().Select(r => r.Method));

    // =========================================================================
    // NS-no-common-path-read (inline, always runs)
    // — AcceptStep tick → no GetHostileActors
    // =========================================================================

    [Fact]
    public async Task NS_NoCommonPathRead_AcceptStepTick_NoGetHostileActorsInReads()
    {
        /*
         * This test MUST PASS immediately (no combat code touched).
         *
         * An AcceptStep tick goes through the common per-tick path only.
         * GetHostileActors must NOT appear in RecordedReads after the tick.
         * Pins that the combat read is step-gated and never leaked into the common path.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(12345u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId.Value,
            Name = "NS Test: AcceptStep",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0,0,0)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new AcceptStep
                        {
                            Id = "accept-ns-test",
                            Target = new NpcLocation(1000u, 0, new Position3(0,0,0)),
                            SkipIf = new PredicateExpect { Predicate = "isQuestAccepted(12345)" },
                            Expect = new PredicateExpect { Predicate = "isQuestAccepted(12345)" }
                        }
                    ]
                }
            ]
        };
        harness.Engine.StartQuest(quest);

        harness.GameState.Reset();
        await harness.Engine.Tick(CancellationToken.None);

        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.False(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must NEVER appear on a non-combat step tick. Reads: {FormatReads(harness.GameState)}");
    }

    // =========================================================================
    // NS-no-common-path-read (inline, always runs)
    // — TravelStep tick → no GetHostileActors
    // =========================================================================

    [Fact]
    public async Task NS_NoCommonPathRead_TravelStepTick_NoGetHostileActorsInReads()
    {
        /*
         * This test MUST PASS immediately.
         *
         * A TravelStep tick emits Navigate. GetHostileActors must never appear.
         */
        var harness = new EngineTestHarness();
        var questId = new QuestId(22222u);
        harness.QuestState.SetQuestSequence(questId, 0);
        harness.GameState.SetPosition(new WorldPosition(0f, 0f, 0f));

        var quest = new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = questId.Value,
            Name = "NS Test: TravelStep",
            Expansion = "arr",
            Category = "side",
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "complete", KnownIssues = [] },
            LastVerifiedPatch = "7.4",
            Requirements = new Requirements { MinLevel = 1, Prereqs = [] },
            AcceptFrom = new NpcLocation(0u, 0, new Position3(0,0,0)),
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 0,
                    Steps =
                    [
                        new TravelStep
                        {
                            Id = "travel-ns-test",
                            Destination = new TravelDestination(130, new Position3(100,0,100))
                        }
                    ]
                }
            ]
        };
        harness.Engine.StartQuest(quest);

        harness.GameState.Reset();
        await harness.Engine.Tick(CancellationToken.None);

        var reads = harness.GameState.RecordedReads.Snapshot();
        Assert.False(reads.Any(r => r.Method == nameof(QuestForge.Adapters.State.IGameStateProvider.GetHostileActors)),
            $"GetHostileActors must NEVER appear on a TravelStep tick. Reads: {FormatReads(harness.GameState)}");
    }
}
