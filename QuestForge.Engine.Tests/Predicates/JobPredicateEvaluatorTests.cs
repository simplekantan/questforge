using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Predicates;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// Engine-evaluation tests for playerJobId(), isDiscipleOfWar(), isDiscipleOfMagic() (job-predicate Slice 2).
/// RED: every test currently throws UnknownStateFunctionException from the default arm of EvaluateFunction.
/// For the adapter-failure tests the wrong exception type is thrown — also RED.
/// </summary>
public sealed class JobPredicateEvaluatorTests
{
    private static PredicateEvaluator Evaluator(JobId job, int level = 1)
    {
        var gameState = new FakeGameStateProvider();
        gameState.SetJob(job, level);
        return new PredicateEvaluator(gameState, new FakeQuestState());
    }

    private static PredicateEvaluator FailingEvaluator()
    {
        var gameState = new FakeGameStateProvider();
        gameState.SetCurrentJobFail("simulated-job-failure");
        return new PredicateEvaluator(gameState, new FakeQuestState());
    }

    // =========================================================================
    // 1. playerJobId() returns the id boxed as long, composes with comparisons
    // =========================================================================

    [Fact]
    public async Task PlayerJobId_EqualsCurrentJob_ReturnsTrue()
    {
        // CONTRACT: SetJob(WAR=21) → playerJobId() == 21 is true
        var evaluator = Evaluator(new JobId(21u));
        var ast = PredicateParser.Parse("playerJobId() == 21").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task PlayerJobId_DifferentId_ReturnsFalse()
    {
        // CONTRACT: SetJob(WAR=21) → playerJobId() == 19 is false
        var evaluator = Evaluator(new JobId(21u));
        var ast = PredicateParser.Parse("playerJobId() == 19").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task PlayerJobId_NotEqual_ReturnsFalse()
    {
        // CONTRACT: SetJob(WAR=21) → playerJobId() != 21 is false
        var evaluator = Evaluator(new JobId(21u));
        var ast = PredicateParser.Parse("playerJobId() != 21").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task PlayerJobId_GreaterOrEqual_ReturnsTrue()
    {
        // CONTRACT: SetJob(WAR=21) → playerJobId() >= 21 is true (proves Int boxing)
        var evaluator = Evaluator(new JobId(21u));
        var ast = PredicateParser.Parse("playerJobId() >= 21").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task PlayerJobId_LessOrEqual_ReturnsTrue()
    {
        // CONTRACT: SetJob(WAR=21) → playerJobId() <= 21 is true (proves Int boxing)
        var evaluator = Evaluator(new JobId(21u));
        var ast = PredicateParser.Parse("playerJobId() <= 21").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    // =========================================================================
    // 2. isDiscipleOfWar() — true for Tank / Melee / PhysicalRanged ids
    // =========================================================================

    [Theory]
    [InlineData(1u,  "GLA (Tank)")]
    [InlineData(21u, "WAR (Tank)")]
    [InlineData(32u, "DRK (Tank)")]
    [InlineData(20u, "MNK (Melee)")]
    [InlineData(22u, "DRG (Melee)")]
    [InlineData(34u, "SAM (Melee)")]
    [InlineData(23u, "BRD (PhysicalRanged)")]
    [InlineData(31u, "MCH (PhysicalRanged)")]
    [InlineData(38u, "DNC (PhysicalRanged)")]
    public async Task IsDiscipleOfWar_DoWJobs_ReturnsTrue(uint jobId, string label)
    {
        // CONTRACT: DoW roles (Tank/Melee/PhysicalRanged) → isDiscipleOfWar() true
        var evaluator = Evaluator(new JobId(jobId));
        var ast = PredicateParser.Parse("isDiscipleOfWar()").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None), label);
    }

    [Theory]
    [InlineData(1u,  "GLA (Tank)")]
    [InlineData(21u, "WAR (Tank)")]
    [InlineData(23u, "BRD (PhysicalRanged)")]
    [InlineData(20u, "MNK (Melee)")]
    public async Task IsDiscipleOfMagic_DoWJobs_ReturnsFalse(uint jobId, string label)
    {
        // CONTRACT: DoW roles → isDiscipleOfMagic() false (mutually exclusive)
        var evaluator = Evaluator(new JobId(jobId));
        var ast = PredicateParser.Parse("isDiscipleOfMagic()").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None), label);
    }

    // =========================================================================
    // 3. isDiscipleOfMagic() — true for Caster / Healer ids
    // =========================================================================

    [Theory]
    [InlineData(25u, "BLM (Caster)")]
    [InlineData(27u, "SMN (Caster)")]
    [InlineData(35u, "RDM (Caster)")]
    [InlineData(24u, "WHM (Healer)")]
    [InlineData(28u, "SCH (Healer)")]
    [InlineData(33u, "AST (Healer)")]
    public async Task IsDiscipleOfMagic_DoMJobs_ReturnsTrue(uint jobId, string label)
    {
        // CONTRACT: DoM roles (Caster/Healer) → isDiscipleOfMagic() true
        var evaluator = Evaluator(new JobId(jobId));
        var ast = PredicateParser.Parse("isDiscipleOfMagic()").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None), label);
    }

    [Theory]
    [InlineData(25u, "BLM (Caster)")]
    [InlineData(24u, "WHM (Healer)")]
    [InlineData(35u, "RDM (Caster)")]
    public async Task IsDiscipleOfWar_DoMJobs_ReturnsFalse(uint jobId, string label)
    {
        // CONTRACT: DoM roles → isDiscipleOfWar() false (mutually exclusive)
        var evaluator = Evaluator(new JobId(jobId));
        var ast = PredicateParser.Parse("isDiscipleOfWar()").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None), label);
    }

    // =========================================================================
    // 4. Unknown role (crafters, gatherers, jobId 0) → both false
    // =========================================================================

    [Theory]
    [InlineData(8u,  "CRP (crafter)")]
    [InlineData(16u, "MIN (gatherer)")]
    [InlineData(0u,  "no job (id 0)")]
    public async Task IsDiscipleOfWar_UnknownRole_ReturnsFalse(uint jobId, string label)
    {
        // CONTRACT: Unknown role (crafters/gatherers/id 0) → isDiscipleOfWar() false
        var evaluator = Evaluator(new JobId(jobId));
        var ast = PredicateParser.Parse("isDiscipleOfWar()").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None), label);
    }

    [Theory]
    [InlineData(8u,  "CRP (crafter)")]
    [InlineData(16u, "MIN (gatherer)")]
    [InlineData(0u,  "no job (id 0)")]
    public async Task IsDiscipleOfMagic_UnknownRole_ReturnsFalse(uint jobId, string label)
    {
        // CONTRACT: Unknown role (crafters/gatherers/id 0) → isDiscipleOfMagic() false
        var evaluator = Evaluator(new JobId(jobId));
        var ast = PredicateParser.Parse("isDiscipleOfMagic()").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None), label);
    }

    // =========================================================================
    // 5. Mutual exclusivity — and / or composition
    // =========================================================================

    [Fact]
    public async Task MutualExclusivity_DoWAndDoM_BothFalse()
    {
        // CONTRACT: DoW job → isDiscipleOfWar() and isDiscipleOfMagic() is false
        var evaluator = Evaluator(new JobId(21u)); // WAR
        var ast = PredicateParser.Parse("isDiscipleOfWar() and isDiscipleOfMagic()").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task MutualExclusivity_DoMAndDoW_BothFalse()
    {
        // CONTRACT: DoM job → isDiscipleOfWar() and isDiscipleOfMagic() is false
        var evaluator = Evaluator(new JobId(24u)); // WHM
        var ast = PredicateParser.Parse("isDiscipleOfWar() and isDiscipleOfMagic()").Ast!;
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task MutualExclusivity_DoWOrDoM_TrueForDoW()
    {
        // CONTRACT: DoW job → isDiscipleOfWar() or isDiscipleOfMagic() is true
        var evaluator = Evaluator(new JobId(21u)); // WAR
        var ast = PredicateParser.Parse("isDiscipleOfWar() or isDiscipleOfMagic()").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    // =========================================================================
    // 6. not() composition
    // =========================================================================

    [Fact]
    public async Task NotIsDiscipleOfMagic_DoWJob_ReturnsTrue()
    {
        // CONTRACT: DoW job → not(isDiscipleOfMagic()) is true
        var evaluator = Evaluator(new JobId(21u)); // WAR
        var ast = PredicateParser.Parse("not(isDiscipleOfMagic())").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task NotIsDiscipleOfWar_DoMJob_ReturnsTrue()
    {
        // CONTRACT: DoM job → not(isDiscipleOfWar()) is true
        var evaluator = Evaluator(new JobId(24u)); // WHM
        var ast = PredicateParser.Parse("not(isDiscipleOfWar())").Ast!;
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    // =========================================================================
    // 7. Adapter failure propagates — must throw InvalidOperationException
    // =========================================================================

    [Theory]
    [InlineData("playerJobId() == 1")]
    [InlineData("isDiscipleOfWar()")]
    [InlineData("isDiscipleOfMagic()")]
    public async Task AdapterFailure_Propagates_ThrowsInvalidOperationException(string predicate)
    {
        // CONTRACT: SetCurrentJobFail → ValueOrThrow throws InvalidOperationException; never silently returns
        var evaluator = FailingEvaluator();
        var ast = PredicateParser.Parse(predicate).Ast!;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => evaluator.Evaluate(ast, CancellationToken.None));
    }
}
