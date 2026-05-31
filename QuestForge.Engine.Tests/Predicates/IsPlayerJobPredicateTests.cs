using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Predicates;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// RED PHASE: Tests for the isPlayerJob(N) predicate function.
/// Spec: docs/CHANGE_JOB_STEP_PLAN.md -- Decision CJ-10, scenarios CJP1-CJP4.
///
/// ALL tests in this file will fail until Builder adds:
///   - "isPlayerJob" arm in PredicateEvaluator.EvaluateFunction         (TODO)
///   - "isPlayerJob" entry in FunctionRegistry.All (tools repo)         (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~IsPlayerJobPredicateTests"
/// </summary>
public sealed class IsPlayerJobPredicateTests
{
    private static PredicateEvaluator Evaluator(JobId job, int level = 1)
    {
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(job, level);
        return new PredicateEvaluator(gameState, new FakeQuestState());
    }

    // =========================================================================
    // CJP1 -- isPlayerJob(32) returns true when player is on job 32
    // =========================================================================

    [Fact]
    public async Task IsPlayerJob_MatchesCurrentJob_ReturnsTrue_CJP1()
    {
        // CONTRACT: Given SetJob(DRK=32),
        //           When evaluating "isPlayerJob(32)",
        //           Then result is true.
        var evaluator = Evaluator(new JobId(32));
        var ast = PredicateParser.Parse("isPlayerJob(32)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "isPlayerJob(32) should be true when player is on job 32");
    }

    // =========================================================================
    // CJP2 -- isPlayerJob(32) returns false when player is on job 19
    // =========================================================================

    [Fact]
    public async Task IsPlayerJob_DoesNotMatchCurrentJob_ReturnsFalse_CJP2()
    {
        // CONTRACT: Given SetJob(PLD=19),
        //           When evaluating "isPlayerJob(32)",
        //           Then result is false.
        var evaluator = Evaluator(new JobId(19));
        var ast = PredicateParser.Parse("isPlayerJob(32)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "isPlayerJob(32) should be false when player is on job 19");
    }

    // =========================================================================
    // CJP3 -- isPlayerJob used in expect with ChangeJobStep -- step confirmed when true
    // =========================================================================

    [Fact]
    public async Task IsPlayerJob_InExpectWithChangeJobStep_StepConfirmed_CJP3()
    {
        // CONTRACT: Given ChangeJobStep { JobId = 32, Expect = "isPlayerJob(32)" },
        //           and player already on job 32,
        //           When engine ticks,
        //           Then step confirmed (Wait returned).

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86030), 0);
        harness.GameState.SetJob(new JobId(32), 90); // already DRK

        var step = new ChangeJobStep
        {
            Id = "change-job-with-expect",
            JobId = 32,
            Expect = new PredicateExpect { Predicate = "isPlayerJob(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86030, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect satisfied, step confirmed done; Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // CJP4 -- isPlayerJob used in skipIf -- step skipped when true
    // =========================================================================

    [Fact]
    public async Task IsPlayerJob_InSkipIf_StepSkipped_CJP4()
    {
        // CONTRACT: Given ChangeJobStep { JobId = 32, SkipIf = "isPlayerJob(32)" },
        //           and player already on job 32,
        //           When engine ticks,
        //           Then step is skipped (adapter never called).

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(86031), 0);
        harness.GameState.SetJob(new JobId(32), 90); // already DRK

        var step = new ChangeJobStep
        {
            Id = "change-job-skip-if",
            JobId = 32,
            SkipIf = new PredicateExpect { Predicate = "isPlayerJob(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(86031, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Step skipped, engine moves past it; Wait (no more steps)
        Assert.IsType<EngineAction.Wait>(action);
        // Adapter never called
        Assert.Equal(0, harness.JobChanger.RecordedCalls.Count);
    }

    // =========================================================================
    // Factory helper
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "IsPlayerJob Predicate Test Quest",
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
