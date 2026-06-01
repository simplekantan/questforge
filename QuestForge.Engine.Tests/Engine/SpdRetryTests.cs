using QuestForge.Adapters.State;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Schema;
using Xunit;
using QuestId = QuestForge.Adapters.Types.QuestId;
using NpcId = QuestForge.Adapters.Types.NpcId;
using ZoneId = QuestForge.Adapters.Types.ZoneId;
using WorldPosition = QuestForge.Adapters.Types.WorldPosition;

namespace QuestForge.Engine.Tests.Engine;

/// <summary>
/// RED tests for SPD retry entry fields on DutyStep + EnterSinglePlayerDuty enrichment.
/// Spec: docs/SPD_RETRY_PLAN.md -- Decisions R1-R10, scenarios R-S1 through R-S6.
///
/// All tests reference API surface that does NOT yet exist:
///   - DutyStep.EntryTargetId (uint?)                                        (TODO)
///   - DutyStep.EntryPosition (Position3?)                                   (TODO)
///   - EngineAction.EnterSinglePlayerDuty(uint? ContentFinderConditionId,
///       uint? EntryTargetId, Position3? EntryPosition, Step? Origin)        (TODO)
///
/// Current shape: EnterSinglePlayerDuty(Step? Origin = null)
/// These tests will FAIL TO COMPILE until the builder implements the new shapes.
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~SpdRetryTests"
/// </summary>
public sealed class SpdRetryTests
{
    // =========================================================================
    // R-S1 -- Happy path: SPD with EntryTargetId + EntryPosition, action carries fields
    // =========================================================================

    [Fact]
    public async Task SpdWithEntryFields_ActionCarriesAllFields_RS1()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70020), 0);
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "spd-with-entry",
            Kind = "spd",
            ContentFinderConditionId = 830,
            EntryTargetId = 1045123u,                              // RED: property does not exist
            EntryPosition = new Position3(100f, 20f, -50f),        // RED: property does not exist
            Expect = new PredicateExpect { Predicate = "questSequence(70020) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70020, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Equal(830u, espd.ContentFinderConditionId);         // RED: property does not exist
        Assert.Equal(1045123u, espd.EntryTargetId);                // RED: property does not exist
        Assert.NotNull(espd.EntryPosition);                        // RED: property does not exist
        Assert.Equal(100f, espd.EntryPosition!.X, precision: 3);
        Assert.Equal(20f, espd.EntryPosition!.Y, precision: 3);
        Assert.Equal(-50f, espd.EntryPosition!.Z, precision: 3);
        Assert.Equal("spd-with-entry", espd.Origin?.Id);
    }

    // =========================================================================
    // R-S2 -- SPD without EntryTargetId -- action carries nulls
    // =========================================================================

    [Fact]
    public async Task SpdWithoutEntryTargetId_ActionCarriesNulls_RS2()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70021), 0);
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "spd-no-entry",
            Kind = "spd",
            ContentFinderConditionId = 830,
            EntryTargetId = null,                                  // RED: property does not exist
            EntryPosition = null,                                  // RED: property does not exist
            Expect = new PredicateExpect { Predicate = "questSequence(70021) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70021, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Equal(830u, espd.ContentFinderConditionId);         // RED: property does not exist
        Assert.Null(espd.EntryTargetId);                           // RED: property does not exist
        Assert.Null(espd.EntryPosition);                           // RED: property does not exist
        Assert.Equal("spd-no-entry", espd.Origin?.Id);
    }

    // =========================================================================
    // R-S3 -- SPD without ContentFinderConditionId -- action carries null CFC
    // =========================================================================

    [Fact]
    public async Task SpdWithoutCfcId_ActionCarriesNullCfc_RS3()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70022), 0);
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "spd-no-cfc",
            Kind = "spd",
            // ContentFinderConditionId intentionally omitted (null -- legacy quest file)
            EntryTargetId = 5000u,                                 // RED: property does not exist
            Expect = new PredicateExpect { Predicate = "questSequence(70022) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70022, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Must NOT be AwaitUser -- CFC is optional for SPDs
        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Null(espd.ContentFinderConditionId);                // RED: property does not exist
        Assert.Equal(5000u, espd.EntryTargetId);                   // RED: property does not exist
    }

    // =========================================================================
    // R-S4 -- Existing happy-path shape updated: null entry fields when unset
    // =========================================================================

    /// <summary>
    /// Mirrors the original S1 test but verifies the action's new fields are null
    /// when the DutyStep does not set them.
    /// </summary>
    [Fact]
    public async Task SpdLegacyStep_ActionFieldsAreNull_RS4()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70023), 0);
        harness.QuestBattleRunner.BossModAvailable = true;

        // Legacy DutyStep: no EntryTargetId, no EntryPosition, no ContentFinderConditionId
        var step = new DutyStep
        {
            Id = "spd-legacy",
            Kind = "spd",
            Expect = new PredicateExpect { Predicate = "questSequence(70023) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70023, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        var espd = Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action);
        Assert.Null(espd.ContentFinderConditionId);                // RED: property does not exist
        Assert.Null(espd.EntryTargetId);                           // RED: property does not exist
        Assert.Null(espd.EntryPosition);                           // RED: property does not exist
        Assert.Equal("spd-legacy", espd.Origin?.Id);
    }

    // =========================================================================
    // R-S5 -- BossMod unavailable still returns AwaitUser (entry fields irrelevant)
    // =========================================================================

    [Fact]
    public async Task SpdBossModUnavailable_AwaitUserIgnoresEntryFields_RS5()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70024), 0);
        harness.QuestBattleRunner.BossModAvailable = false;

        var step = new DutyStep
        {
            Id = "spd-bossmod-unavail",
            Kind = "spd",
            ContentFinderConditionId = 830,
            EntryTargetId = 1045123u,                              // RED: property does not exist
            EntryPosition = new Position3(100f, 20f, -50f),        // RED: property does not exist
            Expect = new PredicateExpect { Predicate = "questSequence(70024) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70024, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Pre-arm short-circuits before constructing EnterSinglePlayerDuty
        var awaitUser = Assert.IsType<EngineAction.AwaitUser>(action);
        Assert.Contains("BossMod", awaitUser.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // R-S6 -- Retry: re-dispatches with same entry fields across multiple ticks
    // =========================================================================

    [Fact]
    public async Task SpdRetry_ReDispatchesWithSameEntryFields_RS6()
    {
        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(70025), 0);
        harness.QuestBattleRunner.BossModAvailable = true;

        var step = new DutyStep
        {
            Id = "spd-retry-entry",
            Kind = "spd",
            ContentFinderConditionId = 830,
            EntryTargetId = 5000u,                                 // RED: property does not exist
            EntryPosition = new Position3(10f, 0f, 20f),           // RED: property does not exist
            Expect = new PredicateExpect { Predicate = "questSequence(70025) >= 3" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(70025, 0, step));

        // Tick 1-3: Expect stays unsatisfied -- engine re-dispatches each time
        var retryActions = new List<EngineAction.EnterSinglePlayerDuty>();
        for (var i = 0; i < 3; i++)
        {
            var action = await harness.Engine.Tick(CancellationToken.None);
            retryActions.Add(Assert.IsType<EngineAction.EnterSinglePlayerDuty>(action));
        }

        // All 3 retries carry the same entry fields
        foreach (var espd in retryActions)
        {
            Assert.Equal(830u, espd.ContentFinderConditionId);     // RED: property does not exist
            Assert.Equal(5000u, espd.EntryTargetId);               // RED: property does not exist
            Assert.NotNull(espd.EntryPosition);                    // RED: property does not exist
            Assert.Equal(10f, espd.EntryPosition!.X, precision: 3);
            Assert.Equal(0f, espd.EntryPosition!.Y, precision: 3);
            Assert.Equal(20f, espd.EntryPosition!.Z, precision: 3);
        }

        // Simulate duty completion: quest sequence advances
        harness.QuestState.SetQuestSequence(new QuestId(70025), 3);

        // Fourth tick: step confirmed -- Wait emitted (no more steps)
        var finalAction = await harness.Engine.Tick(CancellationToken.None);
        Assert.IsType<EngineAction.Wait>(finalAction);
    }

    // =========================================================================
    // Factory helpers
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "SPD Retry Test Quest",
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
