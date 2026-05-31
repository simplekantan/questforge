using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Engine.Tests.Helpers;
using QuestForge.Predicates;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// RED PHASE: Tests for the jobGearsetExists(N) predicate function.
/// Spec: docs/REGISTER_GEARSET_STEP_PLAN.md -- Decision RG-6, scenarios RGP1-RGP4.
///
/// ALL tests in this file will fail until Builder adds:
///   - "jobGearsetExists" arm in PredicateEvaluator.EvaluateFunction       (TODO)
///   - IGameStateProvider.GearsetExistsForJob(uint, CancellationToken)     (TODO)
///   - FakeGameStateProvider.SetGearsetExistsForJob(uint, bool)            (TODO)
///   - "jobGearsetExists" entry in FunctionRegistry.All (tools repo)       (TODO)
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~JobGearsetExistsPredicateTests"
/// </summary>
public sealed class JobGearsetExistsPredicateTests
{
    private static PredicateEvaluator Evaluator(FakeGameStateProvider? gameState = null)
    {
        gameState ??= new FakeGameStateProvider();
        return new PredicateEvaluator(gameState, new FakeQuestState());
    }

    // =========================================================================
    // RGP1 -- jobGearsetExists(32) returns true when gearset exists
    // =========================================================================

    [Fact]
    public async Task JobGearsetExists_GearsetExists_ReturnsTrue_RGP1()
    {
        // CONTRACT: Given SetGearsetExistsForJob(32, true),
        //           When evaluating "jobGearsetExists(32)",
        //           Then result is true.
        var gs = new FakeGameStateProvider();
        gs.SetGearsetExistsForJob(32, true);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse("jobGearsetExists(32)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "jobGearsetExists(32) should be true when gearset exists for job 32");
    }

    // =========================================================================
    // RGP2 -- jobGearsetExists(32) returns false when no gearset exists
    // =========================================================================

    [Fact]
    public async Task JobGearsetExists_NoGearset_ReturnsFalse_RGP2()
    {
        // CONTRACT: Given SetGearsetExistsForJob(32, false) (or default),
        //           When evaluating "jobGearsetExists(32)",
        //           Then result is false.
        var gs = new FakeGameStateProvider();
        gs.SetGearsetExistsForJob(32, false);
        var evaluator = Evaluator(gs);
        var ast = PredicateParser.Parse("jobGearsetExists(32)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "jobGearsetExists(32) should be false when no gearset exists for job 32");
    }

    // =========================================================================
    // RGP3 -- jobGearsetExists used in skipIf with RegisterGearsetStep -- step skipped when true
    // =========================================================================

    [Fact]
    public async Task JobGearsetExists_InSkipIf_StepSkipped_RGP3()
    {
        // CONTRACT: Given RegisterGearsetStep { SkipIf = "jobGearsetExists(32)" },
        //           and SetGearsetExistsForJob(32, true),
        //           When engine ticks,
        //           Then step is skipped and adapter never called.

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87030), 0);
        harness.GameState.SetGearsetExistsForJob(32, true);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-skipif-pred",
            SkipIf = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87030, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Step skipped, engine moves past it
        Assert.IsType<EngineAction.Wait>(action);
        Assert.Equal(0, harness.GearsetManager.RecordedCalls.Count);
    }

    // =========================================================================
    // RGP4 -- jobGearsetExists used in expect -- step confirmed when true
    // =========================================================================

    [Fact]
    public async Task JobGearsetExists_InExpect_StepConfirmed_RGP4()
    {
        // CONTRACT: Given RegisterGearsetStep { Expect = "jobGearsetExists(32)" },
        //           and SetGearsetExistsForJob(32, true),
        //           When engine ticks,
        //           Then step is confirmed (Expect satisfied). Returns Wait.

        var harness = new EngineTestHarness();
        harness.QuestState.SetQuestSequence(new QuestId(87031), 0);
        harness.GameState.SetGearsetExistsForJob(32, true);

        var step = new RegisterGearsetStep
        {
            Id = "register-gearset-expect-pred",
            Expect = new PredicateExpect { Predicate = "jobGearsetExists(32)" }
        };

        harness.Engine.StartQuest(BuildSingleStepQuest(87031, 0, step));

        var action = await harness.Engine.Tick(CancellationToken.None);

        // Expect satisfied, step confirmed done; Wait
        Assert.IsType<EngineAction.Wait>(action);
    }

    // =========================================================================
    // Factory helper
    // =========================================================================

    private static QuestDefinition BuildSingleStepQuest(uint questId, int sequence, Step step) =>
        new()
        {
            SchemaVersion = "1.0.0",
            Id = questId,
            Name = "JobGearsetExists Predicate Test Quest",
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
