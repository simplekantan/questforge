using QuestForge.Adapters.Fakes.State;
using QuestForge.Engine.Predicates;
using QuestForge.Predicates;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// Tests for the inventoryHasCoffers() predicate function.
/// Spec: docs/OPEN_COFFERS_STEP_PLAN.md -- Decision OC-10, scenarios OC_P1-OC_P3.
///
/// RED: Will fail until Builder implements the "inventoryHasCoffers" arm in
/// PredicateEvaluator.EvaluateFunction AND IGameStateProvider.HasCoffers(ct)
/// AND FakeGameStateProvider.SetHasCoffers(bool).
///
/// Run with: dotnet test QuestForge.Engine.Tests --filter "FullyQualifiedName~InventoryHasCoffersPredicateTests"
/// </summary>
public sealed class InventoryHasCoffersPredicateTests
{
    // =========================================================================
    // OC_P1 -- inventoryHasCoffers() returns true when coffers exist
    // =========================================================================

    [Fact]
    public async Task InventoryHasCoffers_CoffersExist_ReturnsTrue_OC_P1()
    {
        /*
         * CONTRACT: Given gameState.SetHasCoffers(true),
         *           When evaluating "inventoryHasCoffers()",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: Add "inventoryHasCoffers" arm to PredicateEvaluator.EvaluateFunction:
         *   "inventoryHasCoffers" => (await _gameState.HasCoffers(ct)).ValueOrThrow,
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetHasCoffers(true);
        var evaluator = new PredicateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("inventoryHasCoffers()").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "inventoryHasCoffers() should be true when coffers exist in inventory");
    }

    // =========================================================================
    // OC_P2 -- inventoryHasCoffers() returns false when no coffers
    // =========================================================================

    [Fact]
    public async Task InventoryHasCoffers_NoCoffers_ReturnsFalse_OC_P2()
    {
        /*
         * CONTRACT: Given gameState.SetHasCoffers(false),
         *           When evaluating "inventoryHasCoffers()",
         *           Then result is false.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetHasCoffers(false);
        var evaluator = new PredicateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("inventoryHasCoffers()").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "inventoryHasCoffers() should be false when no coffers in inventory");
    }

    // =========================================================================
    // OC_P3 -- not inventoryHasCoffers() evaluates correctly
    // =========================================================================

    [Fact]
    public async Task NotInventoryHasCoffers_CoffersExist_ReturnsFalse_OC_P3()
    {
        /*
         * CONTRACT: Given gameState.SetHasCoffers(true),
         *           When evaluating "not inventoryHasCoffers()",
         *           Then result is false (negation of true).
         *
         * This is the standard Expect pattern for OpenCoffersStep:
         *   "expect": "not inventoryHasCoffers()"
         * The step advances when all coffers have been opened.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetHasCoffers(true);
        var evaluator = new PredicateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("not inventoryHasCoffers()").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "not inventoryHasCoffers() should be false when coffers still exist");
    }
}
